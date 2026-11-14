// CashVoice DSP — real-time safe (no allocation in process()).
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace vf {

struct Params {
    bool  voiceOn = false;
    bool  micMute = false;
    bool  soundOn = true;
    float pitch = 0, ringHz = 50, ringMix = 0;
    float echoMs = 250, echoFb = 0.4f, echoMix = 0;
    float hpHz = 0, lpHz = 0, drive = 0;
    float micGain = 1, soundGain = 1;
};

constexpr float kTwoPi = 6.28318530718f;

struct OnePole {
    float z = 0.f;
    inline float lp(float x, float a) {
        z += a * (x - z);
        if (std::fabs(z) < 1e-20f) z = 0.f;
        return z;
    }
};

// Dual-tap crossfading delay-line pitch shifter (classic "rotating delay" design).
struct PitchShifter {
    std::vector<float> buf;
    int   mask = 0, w = 0;
    float phase = 0.f, win = 1440.f;

    void init(int sr) {
        int n = 1;
        while (n < sr / 8) n <<= 1;
        buf.assign((size_t)n, 0.f);
        mask = n - 1; w = 0; phase = 0.f;
        win = 0.03f * (float)sr;                 // 30 ms grain window
    }
    inline float tap(float d) const {
        float rp = (float)w - d;
        int   i  = (int)std::floor(rp);
        float fr = rp - (float)i;
        return buf[(size_t)(i & mask)] * (1.f - fr) + buf[(size_t)((i + 1) & mask)] * fr;
    }
    inline void feed(float x) { buf[(size_t)w] = x; w = (w + 1) & mask; }
    inline float process(float x, float ratio) {
        buf[(size_t)w] = x;
        float p2 = phase + 0.5f; if (p2 >= 1.f) p2 -= 1.f;
        float g1 = 0.5f - 0.5f * std::cos(kTwoPi * phase);
        float g2 = 0.5f - 0.5f * std::cos(kTwoPi * p2);
        float y  = tap(phase * win) * g1 + tap(p2 * win) * g2;
        phase += (1.f - ratio) / win;
        phase -= std::floor(phase);
        w = (w + 1) & mask;
        return y;
    }
};

struct Echo {
    std::vector<float> buf;
    int n = 0, w = 0;
    void init(int sr) { n = sr; buf.assign((size_t)n, 0.f); w = 0; }
    inline void feed(float x) { buf[(size_t)w] = x; if (++w >= n) w = 0; }
    inline float process(float x, int delay, float fb, float mix) {
        if (delay < 1) delay = 1;
        if (delay >= n) delay = n - 1;
        int r = w - delay; if (r < 0) r += n;
        float d = buf[(size_t)r];
        buf[(size_t)w] = x + fb * d;
        if (++w >= n) w = 0;
        return x + mix * d;
    }
};

struct Channel {
    PitchShifter ps; Echo ec;
    OnePole hp1, hp2, lp1, lp2;
};

class Engine {
public:
    void init(int sampleRate, int channels) {
        sr_ = (float)sampleRate;
        chans_.assign((size_t)channels, Channel());
        for (auto& c : chans_) { c.ps.init(sampleRate); c.ec.init(sampleRate); }
        ringPhase_ = 0.f;
    }

    // data: interleaved float32, processed in place. sound: mono, `frames` long (or null).
    void process(float* data, uint32_t frames, const Params& p, const float* sound) {
        const int ch = (int)chans_.size();
        if (ch == 0) return;
        const bool  pitchOn = p.voiceOn && std::fabs(p.pitch) > 0.01f;
        const float ratio   = std::pow(2.f, p.pitch / 12.f);
        const bool  ringOn  = p.voiceOn && p.ringMix > 0.001f;
        const bool  hpOn    = p.voiceOn && p.hpHz > 20.f;
        const bool  lpOn    = p.voiceOn && p.lpHz > 20.f && p.lpHz < 0.45f * sr_;
        const float aHp     = hpOn ? 1.f - std::exp(-kTwoPi * p.hpHz / sr_) : 0.f;
        const float aLp     = lpOn ? 1.f - std::exp(-kTwoPi * p.lpHz / sr_) : 0.f;
        const bool  driveOn = p.voiceOn && p.drive > 0.01f;
        const float g       = 1.f + p.drive * 6.f;
        const float gn      = 1.f / std::tanh(g);
        const bool  echoOn  = p.voiceOn && p.echoMix > 0.01f;
        const int   echoDel = (int)(p.echoMs * 0.001f * sr_);
        const float fb      = std::min(std::max(p.echoFb, 0.f), 0.9f);
        const float ringStep = kTwoPi * p.ringHz / sr_;

        for (uint32_t f = 0; f < frames; ++f) {
            float rm = 0.f;
            if (ringOn) {
                rm = std::sin(ringPhase_);
                ringPhase_ += ringStep;
                if (ringPhase_ > kTwoPi) ringPhase_ -= kTwoPi;
            }
            const float s = (sound && p.soundOn) ? sound[f] * p.soundGain : 0.f;
            for (int c = 0; c < ch; ++c) {
                Channel& C = chans_[(size_t)c];
                float x = data[(size_t)f * ch + c] * p.micGain;
                if (p.micMute) x = 0.f;
                if (p.voiceOn) {
                    if (pitchOn) x = C.ps.process(x, ratio); else C.ps.feed(x);
                    if (ringOn)  x = x * (1.f - p.ringMix) + x * rm * p.ringMix;
                    if (hpOn)  { float y = x - C.hp1.lp(x, aHp); x = y - C.hp2.lp(y, aHp); }
                    if (lpOn)  { x = C.lp1.lp(x, aLp); x = C.lp2.lp(x, aLp); }
                    if (driveOn) x = std::tanh(g * x) * gn;
                    if (echoOn) x = C.ec.process(x, echoDel, fb, p.echoMix); else C.ec.feed(x);
                }
                float y = x + s;
                data[(size_t)f * ch + c] = std::max(-1.f, std::min(1.f, y));
            }
        }
    }
private:
    float sr_ = 48000.f, ringPhase_ = 0.f;
    std::vector<Channel> chans_;
};

} // namespace vf
