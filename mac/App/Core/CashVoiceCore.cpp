// CashVoice for macOS — audio core.
//
//   real mic --(AUHAL input)--> resample to 48 kHz --> FIFO --+
//                                                             |
//   CashVoice Feed <--(AUHAL output, 48 kHz stereo)-- voice engine (dsp.h) + soundboard
//                                                             |
//   speakers/headphones <--(AUHAL output)-- hear-yourself + soundboard preview
//
// The driver loops CashVoice Feed back out of CashVoice Mic, which is what apps record from.
// Capture only runs while some app is recording from CashVoice Mic (or hear-yourself is on).

#include "CashVoiceCore.h"
#include "../../../apo/src/dsp.h"

#include <AudioToolbox/AudioToolbox.h>
#include <CoreAudio/CoreAudio.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

constexpr double kRate = 48000.0;
constexpr int    kMaxFrames = 8192;
const char* const kMicUID = "CashVoiceMic_UID";
const char* const kFeedUID = "CashVoiceFeed_UID";

double Now() {
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

// ---- single-producer / single-consumer mono FIFO ----------------------------------------------
struct Fifo {
    std::vector<float> buf;
    uint64_t mask = 0;
    std::atomic<uint64_t> w{0}, r{0};
    void init(size_t pow2) { buf.assign(pow2, 0.f); mask = pow2 - 1; w = 0; r = 0; }
    void push(const float* x, size_t n) {
        uint64_t ww = w.load(std::memory_order_relaxed);
        for (size_t i = 0; i < n; ++i) buf[(ww + i) & mask] = x[i];
        w.store(ww + n, std::memory_order_release);
    }
    // Reader side. Keeps latency bounded: if more than maxLag is queued, skip to `target`;
    // if there isn't enough, pad the front with silence.
    void pop(float* out, size_t n, uint64_t maxLag, uint64_t target) {
        uint64_t ww = w.load(std::memory_order_acquire), rr = r.load(std::memory_order_relaxed);
        if (rr > ww || ww - rr > maxLag) rr = ww > target ? ww - target : 0;
        size_t k = (size_t)std::min<uint64_t>(ww - rr, n), pad = n - k;
        std::memset(out, 0, pad * sizeof(float));
        for (size_t i = 0; i < k; ++i) out[pad + i] = buf[(rr + i) & mask];
        r.store(rr + k, std::memory_order_release);
    }
};

// ---- streaming resampler (4-point Hermite) ---------------------------------------------------
struct Resampler {
    double step = 1.0, pos = 0.0;          // input samples per output sample; position in `work`
    std::vector<float> work;               // 3 samples of history + the new block
    std::vector<float> out;
    void init(double inRate) {
        step = inRate / kRate; pos = 1.0;
        work.reserve(kMaxFrames * 2 + 8); work.assign(3, 0.f);
        out.reserve(kMaxFrames * 8);              // no allocation on the audio thread
    }
    const std::vector<float>& process(const float* in, size_t n) {
        out.clear();
        work.insert(work.end(), in, in + n);
        const size_t len = work.size();
        while (pos + 2.0 < (double)len) {
            size_t i = (size_t)pos; float fr = (float)(pos - (double)i);
            float xm1 = work[i - 1], x0 = work[i], x1 = work[i + 1], x2 = work[i + 2];
            float c1 = 0.5f * (x1 - xm1), c2 = xm1 - 2.5f * x0 + 2.f * x1 - 0.5f * x2,
                  c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
            out.push_back(((c3 * fr + c2) * fr + c1) * fr + x0);
            pos += step;
        }
        size_t keepFrom = (size_t)pos - 1;                  // keep x[-1] for the next block
        if (keepFrom > len) keepFrom = len;
        work.erase(work.begin(), work.begin() + (long)keepFrom);
        pos -= (double)keepFrom;
        return out;
    }
};

// ---- soundboard clips ------------------------------------------------------------------------
struct Clip {
    std::vector<float> data;
    float micGain = 0, localGain = 0;
    size_t micPos = 0, localPos = 0;
    std::atomic<bool> micDone{false}, localDone{false}, stop{false};
};
constexpr int kMaxClips = 24;
std::atomic<Clip*> gClips[kMaxClips];
struct Dead { Clip* c; double t; };
std::vector<Dead> gGraveyard;               // UI thread only: freed a while after the audio threads let go
std::mutex gClipMutex;

// Mix one destination of all clips into out (adds). Audio threads only.
void MixClips(float* out, size_t n, bool mic, float master) {
    for (auto& slot : gClips) {
        Clip* c = slot.load(std::memory_order_acquire);
        if (!c) continue;
        std::atomic<bool>& done = mic ? c->micDone : c->localDone;
        if (done.load(std::memory_order_acquire)) continue;
        float g = (mic ? c->micGain : c->localGain) * master;
        size_t& pos = mic ? c->micPos : c->localPos;
        if (c->stop.load(std::memory_order_relaxed) || g <= 0.f) { done.store(true, std::memory_order_release); continue; }
        size_t k = std::min(n, c->data.size() - pos);
        for (size_t i = 0; i < k; ++i) out[i] += c->data[pos + i] * g;
        pos += k;
        if (pos >= c->data.size()) done.store(true, std::memory_order_release);
    }
}
bool AnyLocalClips() {
    for (auto& slot : gClips) { Clip* c = slot.load(); if (c && !c->localDone.load()) return true; }
    return false;
}

// ---- shared parameters -----------------------------------------------------------------------
CVParams gParamsBuf[2];
std::atomic<unsigned> gParamsSeq{0};
std::atomic<float> gLocalVolume{0.8f}, gMonitorVolume{1.f};
std::atomic<bool> gMonitorOn{false}, gMonitorWet{true};
std::atomic<float> gPeak{0.f};

vf::Params Snapshot() {
    CVParams c = gParamsBuf[gParamsSeq.load(std::memory_order_acquire) & 1];
    vf::Params p;
    p.voiceOn = c.voiceOn != 0; p.micMute = c.micMute != 0; p.soundOn = c.soundOn != 0;
    p.pitch = c.pitch; p.formant = c.formant; p.ringHz = c.ringHz; p.ringMix = c.ringMix;
    p.echoMs = c.echoMs; p.echoFb = c.echoFb; p.echoMix = c.echoMix;
    p.hpHz = c.hpHz; p.lpHz = c.lpHz; p.drive = c.drive;
    p.micGain = c.micGain; p.soundGain = c.soundGain;
    return p;
}

// ---- CoreAudio helpers -----------------------------------------------------------------------
std::string CFToString(CFStringRef s) {
    if (!s) return "";
    char buf[512];
    if (CFStringGetCString(s, buf, sizeof(buf), kCFStringEncodingUTF8)) return buf;
    return "";
}
std::string DeviceString(AudioDeviceID d, AudioObjectPropertySelector sel) {
    AudioObjectPropertyAddress a = { sel, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    CFStringRef s = nullptr; UInt32 sz = sizeof(s);
    if (AudioObjectGetPropertyData(d, &a, 0, nullptr, &sz, &s) != noErr || !s) return "";
    std::string r = CFToString(s);
    CFRelease(s);
    return r;
}
std::vector<AudioDeviceID> AllDevices() {
    AudioObjectPropertyAddress a = { kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    UInt32 sz = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &a, 0, nullptr, &sz) != noErr) return {};
    std::vector<AudioDeviceID> ids(sz / sizeof(AudioDeviceID));
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, nullptr, &sz, ids.data()) != noErr) return {};
    ids.resize(sz / sizeof(AudioDeviceID));
    return ids;
}
UInt32 InputChannels(AudioDeviceID d) {
    AudioObjectPropertyAddress a = { kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeInput, kAudioObjectPropertyElementMain };
    UInt32 sz = 0;
    if (AudioObjectGetPropertyDataSize(d, &a, 0, nullptr, &sz) != noErr || sz == 0) return 0;
    std::vector<uint8_t> raw(sz);
    auto* list = (AudioBufferList*)raw.data();
    if (AudioObjectGetPropertyData(d, &a, 0, nullptr, &sz, list) != noErr) return 0;
    UInt32 ch = 0;
    for (UInt32 i = 0; i < list->mNumberBuffers; ++i) ch += list->mBuffers[i].mNumberChannels;
    return ch;
}
// Finds a device by UID, hidden ones included (the Feed is hidden from the device list).
AudioDeviceID DeviceForUID(const char* uid) {
    CFStringRef s = CFStringCreateWithCString(nullptr, uid, kCFStringEncodingUTF8);
    AudioDeviceID d = kAudioObjectUnknown;
    AudioValueTranslation t = { &s, sizeof(s), &d, sizeof(d) };
    AudioObjectPropertyAddress a = { kAudioHardwarePropertyDeviceForUID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    UInt32 sz = sizeof(t);
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, nullptr, &sz, &t);
    CFRelease(s);
    return d;
}
bool IsOurs(const std::string& uid) { return uid == kMicUID || uid == kFeedUID; }
UInt32 GetU32(AudioObjectID o, AudioObjectPropertySelector sel, AudioObjectPropertyScope scope = kAudioObjectPropertyScopeGlobal) {
    AudioObjectPropertyAddress a = { sel, scope, kAudioObjectPropertyElementMain };
    UInt32 v = 0, sz = sizeof(v);
    if (AudioObjectGetPropertyData(o, &a, 0, nullptr, &sz, &v) != noErr) return 0;
    return v;
}
AudioDeviceID DefaultInput() {
    AudioObjectPropertyAddress a = { kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    AudioDeviceID d = kAudioObjectUnknown; UInt32 sz = sizeof(d);
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, nullptr, &sz, &d);
    return d;
}
AudioDeviceID DefaultOutput() {
    AudioObjectPropertyAddress a = { kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain };
    AudioDeviceID d = kAudioObjectUnknown; UInt32 sz = sizeof(d);
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, nullptr, &sz, &d);
    return d;
}

// The real mic: the chosen one if present, else the default input, else (if the default input is
// CashVoice Mic itself) the built-in mic or the first other input.
AudioDeviceID PickInput(const std::string& uid) {
    if (!uid.empty()) {
        AudioDeviceID d = DeviceForUID(uid.c_str());
        if (d != kAudioObjectUnknown && InputChannels(d) > 0) return d;
    }
    AudioDeviceID def = DefaultInput();
    if (def != kAudioObjectUnknown && !IsOurs(DeviceString(def, kAudioDevicePropertyDeviceUID))) return def;
    AudioDeviceID first = kAudioObjectUnknown;
    for (AudioDeviceID d : AllDevices()) {
        if (InputChannels(d) == 0 || IsOurs(DeviceString(d, kAudioDevicePropertyDeviceUID))) continue;
        if (GetU32(d, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeBuiltIn) return d;
        if (first == kAudioObjectUnknown) first = d;
    }
    return first;
}

AudioUnit NewHAL() {
    AudioComponentDescription desc = { kAudioUnitType_Output, kAudioUnitSubType_HALOutput, kAudioUnitManufacturer_Apple, 0, 0 };
    AudioComponent comp = AudioComponentFindNext(nullptr, &desc);
    AudioUnit au = nullptr;
    if (!comp || AudioComponentInstanceNew(comp, &au) != noErr) return nullptr;
    return au;
}
AudioStreamBasicDescription FloatFormat(double rate, UInt32 ch) {
    AudioStreamBasicDescription f = {};
    f.mSampleRate = rate; f.mFormatID = kAudioFormatLinearPCM;
    f.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagsNativeEndian | kAudioFormatFlagIsPacked;
    f.mChannelsPerFrame = ch; f.mBitsPerChannel = 32;
    f.mBytesPerFrame = f.mBytesPerPacket = 4 * ch; f.mFramesPerPacket = 1;
    return f;
}
void Dispose(AudioUnit& au) {
    if (!au) return;
    AudioOutputUnitStop(au);
    AudioUnitUninitialize(au);
    AudioComponentInstanceDispose(au);
    au = nullptr;
}

// ---- state -----------------------------------------------------------------------------------
std::mutex gMutex;                 // UI-thread operations (start/stop/config)
std::string gWantedInput;
AudioUnit gInUnit = nullptr, gFeedUnit = nullptr, gLocalUnit = nullptr;
AudioDeviceID gInDevice = kAudioObjectUnknown;
UInt32 gInChannels = 1;
double gInRate = kRate;
std::vector<float> gInInterleaved, gInMono;
Resampler gResampler;
Fifo gMicFifo, gMonFifo;
vf::Engine gEngine;
std::vector<float> gFeedTmp, gFeedDry, gFeedSnd, gLocalTmp;
double gLastNeeded = 0, gLastLocalUse = 0;
std::string gError, gInName;

// real mic -> mono 48 kHz -> FIFO
OSStatus InputProc(void*, AudioUnitRenderActionFlags* flags, const AudioTimeStamp* ts, UInt32 bus, UInt32 frames, AudioBufferList*) {
    if (frames > (UInt32)kMaxFrames) frames = kMaxFrames;
    AudioBufferList abl;
    abl.mNumberBuffers = 1;
    abl.mBuffers[0].mNumberChannels = gInChannels;
    abl.mBuffers[0].mDataByteSize = frames * gInChannels * sizeof(float);
    abl.mBuffers[0].mData = gInInterleaved.data();
    if (AudioUnitRender(gInUnit, flags, ts, bus, frames, &abl) != noErr) return noErr;
    const float* in = gInInterleaved.data();
    float peak = 0.f;
    for (UInt32 i = 0; i < frames; ++i) {
        float v = gInChannels >= 2 ? 0.5f * (in[i * gInChannels] + in[i * gInChannels + 1]) : in[i * gInChannels];
        gInMono[i] = v;
        peak = std::max(peak, std::fabs(v));
    }
    gPeak.store(std::max(peak, gPeak.load(std::memory_order_relaxed) * 0.9f), std::memory_order_relaxed);
    if (gInRate == kRate) gMicFifo.push(gInMono.data(), frames);
    else { const auto& o = gResampler.process(gInMono.data(), frames); gMicFifo.push(o.data(), o.size()); }
    return noErr;
}

// FIFO -> voice engine + soundboard -> CashVoice Feed (stereo)
OSStatus FeedProc(void*, AudioUnitRenderActionFlags*, const AudioTimeStamp*, UInt32, UInt32 frames, AudioBufferList* io) {
    float* out = (float*)io->mBuffers[0].mData;
    size_t n = std::min<size_t>(frames, kMaxFrames);
    float* x = gFeedTmp.data();
    gMicFifo.pop(x, n, 2400, 480);             // keep 10-50 ms queued
    vf::Params p = Snapshot();
    const bool monOn = gMonitorOn.load(std::memory_order_relaxed);
    const bool wet = gMonitorWet.load(std::memory_order_relaxed);
    if (monOn && !wet) gMonFifo.push(x, n);
    gEngine.process(x, (uint32_t)n, p, nullptr);
    if (monOn && wet) gMonFifo.push(x, n);
    float* s = gFeedSnd.data();
    std::memset(s, 0, n * sizeof(float));
    MixClips(s, n, true, p.soundOn ? p.soundGain : 0.f);
    for (size_t i = 0; i < n; ++i) {
        float v = vf::SoftLimit(x[i] + s[i]);
        out[2 * i] = v; out[2 * i + 1] = v;
    }
    for (size_t i = n; i < frames; ++i) out[2 * i] = out[2 * i + 1] = 0.f;
    return noErr;
}

// hear-yourself + soundboard preview -> speakers/headphones (stereo, 48 kHz; AUHAL converts)
OSStatus LocalProc(void*, AudioUnitRenderActionFlags*, const AudioTimeStamp*, UInt32, UInt32 frames, AudioBufferList* io) {
    float* out = (float*)io->mBuffers[0].mData;
    size_t n = std::min<size_t>(frames, kMaxFrames);
    float* m = gLocalTmp.data();
    if (gMonitorOn.load(std::memory_order_relaxed)) {
        gMonFifo.pop(m, n, 2400, 480);
        float v = gMonitorVolume.load(std::memory_order_relaxed);
        for (size_t i = 0; i < n; ++i) m[i] *= v;
    } else {
        std::memset(m, 0, n * sizeof(float));
    }
    MixClips(m, n, false, gLocalVolume.load(std::memory_order_relaxed));
    for (size_t i = 0; i < n; ++i) { float v = vf::SoftLimit(m[i]); out[2 * i] = v; out[2 * i + 1] = v; }
    for (size_t i = n; i < frames; ++i) out[2 * i] = out[2 * i + 1] = 0.f;
    return noErr;
}

void SetBufferSize(AudioUnit au, UInt32 frames) {
    AudioUnitSetProperty(au, kAudioDevicePropertyBufferFrameSize, kAudioUnitScope_Global, 0, &frames, sizeof(frames));
}

bool StartOutput(AudioUnit& au, AudioDeviceID dev, AURenderCallback cb, UInt32 bufferFrames) {
    au = NewHAL();
    if (!au) return false;
    AudioUnitSetProperty(au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &dev, sizeof(dev));
    auto f = FloatFormat(kRate, 2);
    AudioUnitSetProperty(au, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &f, sizeof(f));
    UInt32 maxFrames = kMaxFrames;
    AudioUnitSetProperty(au, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maxFrames, sizeof(maxFrames));
    AURenderCallbackStruct rc = { cb, nullptr };
    AudioUnitSetProperty(au, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0, &rc, sizeof(rc));
    SetBufferSize(au, bufferFrames);
    if (AudioUnitInitialize(au) != noErr || AudioOutputUnitStart(au) != noErr) { Dispose(au); return false; }
    return true;
}

bool StartCapture() {
    gError.clear();
    AudioDeviceID feed = DeviceForUID(kFeedUID);
    if (feed == kAudioObjectUnknown) { gError = "The CashVoice Mic driver isn't installed (or Core Audio hasn't loaded it yet)."; return false; }
    AudioDeviceID in = PickInput(gWantedInput);
    if (in == kAudioObjectUnknown) { gError = "No microphone found."; return false; }

    gInUnit = NewHAL();
    if (!gInUnit) { gError = "Couldn't open the audio system."; return false; }
    UInt32 one = 1, zero = 0;
    AudioUnitSetProperty(gInUnit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &one, sizeof(one));
    AudioUnitSetProperty(gInUnit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, &zero, sizeof(zero));
    if (AudioUnitSetProperty(gInUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &in, sizeof(in)) != noErr) {
        gError = "Couldn't open the microphone."; Dispose(gInUnit); return false;
    }
    AudioStreamBasicDescription devFmt = {}; UInt32 sz = sizeof(devFmt);
    AudioUnitGetProperty(gInUnit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &devFmt, &sz);
    gInChannels = std::max<UInt32>(1, std::min<UInt32>(devFmt.mChannelsPerFrame, 64));
    gInRate = devFmt.mSampleRate > 0 ? devFmt.mSampleRate : kRate;
    auto client = FloatFormat(gInRate, gInChannels);
    AudioUnitSetProperty(gInUnit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, &client, sizeof(client));
    UInt32 maxFrames = kMaxFrames;
    AudioUnitSetProperty(gInUnit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maxFrames, sizeof(maxFrames));
    AURenderCallbackStruct cb = { InputProc, nullptr };
    AudioUnitSetProperty(gInUnit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0, &cb, sizeof(cb));
    SetBufferSize(gInUnit, 256);
    gInInterleaved.assign((size_t)kMaxFrames * gInChannels, 0.f);
    gInMono.assign(kMaxFrames, 0.f);
    gResampler.init(gInRate);
    gMicFifo.init(1 << 15);                       // (gMonFifo lives as long as the app: the speaker output reads it)
    gEngine.init((int)kRate, 1);
    gFeedTmp.assign(kMaxFrames, 0.f); gFeedDry.assign(kMaxFrames, 0.f); gFeedSnd.assign(kMaxFrames, 0.f);

    if (AudioUnitInitialize(gInUnit) != noErr || AudioOutputUnitStart(gInUnit) != noErr) {
        gError = "Couldn't start the microphone."; Dispose(gInUnit); return false;
    }
    if (!StartOutput(gFeedUnit, feed, FeedProc, 256)) {
        gError = "Couldn't open CashVoice Mic."; Dispose(gInUnit); return false;
    }
    gInDevice = in;
    gInName = DeviceString(in, kAudioObjectPropertyName);
    return true;
}

void StopCapture() {
    Dispose(gFeedUnit);
    Dispose(gInUnit);
    gInDevice = kAudioObjectUnknown;
    gPeak = 0.f;
}

bool MicInUse() {
    AudioDeviceID mic = DeviceForUID(kMicUID);
    return mic != kAudioObjectUnknown && GetU32(mic, kAudioDevicePropertyDeviceIsRunningSomewhere) != 0;
}

void CollectGarbage(bool all) {
    std::lock_guard<std::mutex> lk(gClipMutex);
    for (auto& slot : gClips) {
        Clip* c = slot.load();
        if (c && c->micDone.load() && c->localDone.load()) {
            slot.store(nullptr);
            gGraveyard.push_back({ c, Now() });
        }
    }
    double t = Now();
    for (size_t i = 0; i < gGraveyard.size();) {
        if (all || t - gGraveyard[i].t > 2.0) { delete gGraveyard[i].c; gGraveyard.erase(gGraveyard.begin() + (long)i); }
        else ++i;
    }
}

} // namespace

// ---- C API -----------------------------------------------------------------------------------
extern "C" {

void cv_init(void) {
    CVParams p = {};
    p.micGain = 1.f; p.soundGain = 1.f; p.soundOn = 1; p.ringHz = 50.f; p.echoMs = 250.f; p.echoFb = 0.4f;
    gParamsBuf[0] = gParamsBuf[1] = p;
    gLocalTmp.assign(kMaxFrames, 0.f);
    gMonFifo.init(1 << 15);
}

void cv_set_params(const CVParams* p) {
    unsigned s = gParamsSeq.load();
    gParamsBuf[(s + 1) & 1] = *p;
    gParamsSeq.store(s + 1, std::memory_order_release);
}

void cv_set_input(const char* uid) {
    std::lock_guard<std::mutex> lk(gMutex);
    std::string u = uid ? uid : "";
    if (u == gWantedInput) return;
    gWantedInput = u;
    if (gInUnit) { StopCapture(); StartCapture(); }
}

void cv_tick(void) {
    std::lock_guard<std::mutex> lk(gMutex);
    double t = Now();
    bool need = MicInUse() || gMonitorOn.load();
    if (need) gLastNeeded = t;

    // The mic we're using disappeared (unplugged): reopen on whatever is available now.
    if (gInUnit && gInDevice != kAudioObjectUnknown && GetU32(gInDevice, kAudioDevicePropertyDeviceIsAlive) == 0) StopCapture();

    if (need && !gInUnit) StartCapture();
    else if (!need && gInUnit && t - gLastNeeded > 2.0) StopCapture();

    bool local = gMonitorOn.load() || AnyLocalClips();
    if (local) gLastLocalUse = t;
    if (local && !gLocalUnit) StartOutput(gLocalUnit, DefaultOutput(), LocalProc, 256);
    else if (!local && gLocalUnit && t - gLastLocalUse > 3.0) Dispose(gLocalUnit);

    CollectGarbage(false);
}

void cv_get_status(CVStatus* s) {
    std::lock_guard<std::mutex> lk(gMutex);
    std::memset(s, 0, sizeof(*s));
    s->driverInstalled = DeviceForUID(kMicUID) != kAudioObjectUnknown && DeviceForUID(kFeedUID) != kAudioObjectUnknown;
    s->micInUse = MicInUse();
    s->running = gInUnit != nullptr;
    s->inputRate = (float)gInRate;
    s->inputLevel = gPeak.load();
    std::snprintf(s->inputName, sizeof(s->inputName), "%s", gInName.c_str());
    std::snprintf(s->error, sizeof(s->error), "%s", gError.c_str());
}

void cv_play(const float* mono48k, int frames, float micGain, float localGain) {
    if (!mono48k || frames <= 0) return;
    auto* c = new Clip();
    c->data.assign(mono48k, mono48k + frames);
    c->micGain = micGain; c->localGain = localGain;
    if (micGain <= 0.f) c->micDone = true;
    if (localGain <= 0.f) c->localDone = true;
    CollectGarbage(false);
    bool placed = false;
    {
        std::lock_guard<std::mutex> lk(gClipMutex);
        for (auto& slot : gClips) {
            Clip* expected = nullptr;
            if (slot.compare_exchange_strong(expected, c)) { placed = true; break; }
        }
    }
    if (!placed) { delete c; return; }            // 24 sounds already playing
    if (localGain > 0.f) {                         // make sure the speaker output is running
        std::lock_guard<std::mutex> lk(gMutex);    // (same lock order as cv_tick: gMutex, then gClipMutex)
        gLastLocalUse = Now();
        if (!gLocalUnit) StartOutput(gLocalUnit, DefaultOutput(), LocalProc, 256);
    }
}

void cv_stop_sounds(void) {
    for (auto& slot : gClips) { Clip* c = slot.load(); if (c) c->stop = true; }
}

void cv_set_local_volume(float v) { gLocalVolume = v; }

void cv_set_monitor(int on, int wet, float volume) {
    gMonitorWet = wet != 0;
    gMonitorVolume = volume;
    gMonitorOn = on != 0;
}

int cv_list_inputs(char* buf, int len) {
    std::string all;
    for (AudioDeviceID d : AllDevices()) {
        if (InputChannels(d) == 0) continue;
        std::string uid = DeviceString(d, kAudioDevicePropertyDeviceUID);
        if (uid.empty() || IsOurs(uid)) continue;
        all += uid + "\t" + DeviceString(d, kAudioObjectPropertyName) + "\n";
    }
    if (!buf || len <= 0) return (int)all.size() + 1;
    std::snprintf(buf, (size_t)len, "%s", all.c_str());
    return (int)std::min<size_t>(all.size(), (size_t)len - 1);
}

void cv_shutdown(void) {
    std::lock_guard<std::mutex> lk(gMutex);
    StopCapture();
    Dispose(gLocalUnit);
    cv_stop_sounds();
}

} // extern "C"
