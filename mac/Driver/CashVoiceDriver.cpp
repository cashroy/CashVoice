// CashVoice — Core Audio "Audio Server Plug-in" (HAL driver) for macOS.
//
// Two devices, both 48 kHz stereo float:
//   * "CashVoice Mic"  — input only. This is what Discord, OBS, games etc. record from.
//   * "CashVoice Feed" — output only and hidden from the Sound settings. The CashVoice app plays
//                        your processed voice + soundboard into it.
// Whatever goes into the Feed comes out of the Mic, through a small FIFO. Because the Mic is its
// own device, the app can tell when somebody is actually listening (the Mic is running) and only
// opens your real microphone then, so the orange mic indicator isn't on all the time.
//
// Installed to /Library/Audio/Plug-Ins/HAL/CashVoiceDriver.driver and loaded by coreaudiod.
// Object IDs are fixed: 1 = plug-in, 2 = Mic device, 3 = its input stream,
//                       4 = Feed device, 5 = its output stream.

#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreFoundation/CoreFoundation.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <string.h>

// ---- constants ---------------------------------------------------------------------------
enum : AudioObjectID {
    kObjPlugIn = kAudioObjectPlugInObject,   // 1
    kObjMic = 2, kObjMicStream = 3,
    kObjFeed = 4, kObjFeedStream = 5,
};

static const Float64 kSampleRate = 48000.0;
static const UInt32  kChannels = 2;
static const UInt32  kBytesPerFrame = kChannels * sizeof(Float32);
static const UInt32  kPeriodFrames = 16384;            // zero-timestamp period
static const UInt32  kFifoFrames = 8192;               // power of two
static const UInt32  kMaxLagFrames = 2400;             // > 50 ms queued: drop the excess (keep latency low)

#define kManufacturer "cashroy"

struct DeviceInfo {
    AudioObjectID id, stream;
    bool input;
    CFStringRef name, uid, model;
    // clock
    UInt64 anchorHostTime, numberTimeStamps;
    UInt32 ioRunning;
};

static DeviceInfo gDev[2] = {
    { kObjMic,  kObjMicStream,  true,  CFSTR("CashVoice Mic"),  CFSTR("CashVoiceMic_UID"),  CFSTR("CashVoiceMic_Model"),  0, 0, 0 },
    { kObjFeed, kObjFeedStream, false, CFSTR("CashVoice Feed"), CFSTR("CashVoiceFeed_UID"), CFSTR("CashVoiceFeed_Model"), 0, 0, 0 },
};

static DeviceInfo* DevByObject(AudioObjectID o) {
    for (auto& d : gDev) if (d.id == o) return &d;
    return nullptr;
}
static DeviceInfo* DevByStream(AudioObjectID o) {
    for (auto& d : gDev) if (d.stream == o) return &d;
    return nullptr;
}

// ---- state -------------------------------------------------------------------------------
static pthread_mutex_t gStateMutex = PTHREAD_MUTEX_INITIALIZER;
static AudioServerPlugInHostRef gHost = nullptr;
static UInt32  gRefCount = 0;
static Float64 gHostTicksPerFrame = 0.0;

// Feed -> Mic FIFO. Single writer (Feed's IO thread), single reader (Mic's IO thread).
static Float32 gFifo[kFifoFrames * kChannels];
static volatile UInt64 gWritten = 0, gRead = 0;     // frame counters

static AudioStreamBasicDescription Format() {
    AudioStreamBasicDescription f = {};
    f.mSampleRate = kSampleRate;
    f.mFormatID = kAudioFormatLinearPCM;
    f.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagsNativeEndian | kAudioFormatFlagIsPacked;
    f.mBytesPerPacket = kBytesPerFrame;
    f.mFramesPerPacket = 1;
    f.mBytesPerFrame = kBytesPerFrame;
    f.mChannelsPerFrame = kChannels;
    f.mBitsPerChannel = 32;
    return f;
}

#define NEED(sz) do { if (inDataSize < (sz)) return kAudioHardwareBadPropertySizeError; } while (0)
#define CFSTR_OUT(s) do { NEED(sizeof(CFStringRef)); *(CFStringRef*)outData = (s); *outDataSize = sizeof(CFStringRef); } while (0)
#define U32_OUT(v) do { NEED(sizeof(UInt32)); *(UInt32*)outData = (v); *outDataSize = sizeof(UInt32); } while (0)

// ---- interface -----------------------------------------------------------------------------
static HRESULT QueryInterface(void*, REFIID, LPVOID*);
static ULONG   AddRef(void*);
static ULONG   Release(void*);
static OSStatus Initialize(AudioServerPlugInDriverRef, AudioServerPlugInHostRef);
static OSStatus CreateDevice(AudioServerPlugInDriverRef, CFDictionaryRef, const AudioServerPlugInClientInfo*, AudioObjectID*);
static OSStatus DestroyDevice(AudioServerPlugInDriverRef, AudioObjectID);
static OSStatus AddDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*);
static OSStatus RemoveDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*);
static OSStatus PerformDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*);
static OSStatus AbortDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*);
static Boolean  HasProperty(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*);
static OSStatus IsPropertySettable(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, Boolean*);
static OSStatus GetPropertyDataSize(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32*);
static OSStatus GetPropertyData(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32, UInt32*, void*);
static OSStatus SetPropertyData(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32, const void*);
static OSStatus StartIO(AudioServerPlugInDriverRef, AudioObjectID, UInt32);
static OSStatus StopIO(AudioServerPlugInDriverRef, AudioObjectID, UInt32);
static OSStatus GetZeroTimeStamp(AudioServerPlugInDriverRef, AudioObjectID, UInt32, Float64*, UInt64*, UInt64*);
static OSStatus WillDoIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, Boolean*, Boolean*);
static OSStatus BeginIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*);
static OSStatus DoIOOperation(AudioServerPlugInDriverRef, AudioObjectID, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*, void*, void*);
static OSStatus EndIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*);

static AudioServerPlugInDriverInterface gInterface = {
    nullptr,
    QueryInterface, AddRef, Release,
    Initialize, CreateDevice, DestroyDevice, AddDeviceClient, RemoveDeviceClient,
    PerformDeviceConfigurationChange, AbortDeviceConfigurationChange,
    HasProperty, IsPropertySettable, GetPropertyDataSize, GetPropertyData, SetPropertyData,
    StartIO, StopIO, GetZeroTimeStamp, WillDoIOOperation, BeginIOOperation, DoIOOperation, EndIOOperation,
};
static AudioServerPlugInDriverInterface* gInterfacePtr = &gInterface;
static AudioServerPlugInDriverRef gDriverRef = &gInterfacePtr;

// Factory named in Info.plist.
extern "C" void* CashVoice_Create(CFAllocatorRef, CFUUIDRef requestedTypeUUID) {
    return CFEqual(requestedTypeUUID, kAudioServerPlugInTypeUUID) ? gDriverRef : nullptr;
}

static HRESULT QueryInterface(void* inDriver, REFIID inUUID, LPVOID* outInterface) {
    if (inDriver != gDriverRef || !outInterface) return kAudioHardwareIllegalOperationError;
    CFUUIDRef req = CFUUIDCreateFromUUIDBytes(nullptr, inUUID);
    HRESULT r = E_NOINTERFACE;
    if (CFEqual(req, IUnknownUUID) || CFEqual(req, kAudioServerPlugInDriverInterfaceUUID)) {
        pthread_mutex_lock(&gStateMutex); ++gRefCount; pthread_mutex_unlock(&gStateMutex);
        *outInterface = gDriverRef;
        r = S_OK;
    }
    CFRelease(req);
    return r;
}
static ULONG AddRef(void* inDriver) {
    if (inDriver != gDriverRef) return 0;
    pthread_mutex_lock(&gStateMutex); ULONG r = ++gRefCount; pthread_mutex_unlock(&gStateMutex);
    return r;
}
static ULONG Release(void* inDriver) {
    if (inDriver != gDriverRef) return 0;
    pthread_mutex_lock(&gStateMutex); ULONG r = gRefCount > 0 ? --gRefCount : 0; pthread_mutex_unlock(&gStateMutex);
    return r;
}

static OSStatus Initialize(AudioServerPlugInDriverRef inDriver, AudioServerPlugInHostRef inHost) {
    if (inDriver != gDriverRef) return kAudioHardwareBadObjectError;
    gHost = inHost;
    mach_timebase_info_data_t tb; mach_timebase_info(&tb);
    Float64 hostClockFrequency = ((Float64)tb.denom / (Float64)tb.numer) * 1000000000.0;
    gHostTicksPerFrame = hostClockFrequency / kSampleRate;
    memset(gFifo, 0, sizeof(gFifo));
    return 0;
}
static OSStatus CreateDevice(AudioServerPlugInDriverRef, CFDictionaryRef, const AudioServerPlugInClientInfo*, AudioObjectID*) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus DestroyDevice(AudioServerPlugInDriverRef, AudioObjectID) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus AddDeviceClient(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, const AudioServerPlugInClientInfo*) {
    return (inDriver == gDriverRef && DevByObject(inDevice)) ? 0 : kAudioHardwareBadObjectError;
}
static OSStatus RemoveDeviceClient(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, const AudioServerPlugInClientInfo*) {
    return (inDriver == gDriverRef && DevByObject(inDevice)) ? 0 : kAudioHardwareBadObjectError;
}
static OSStatus PerformDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*) { return 0; }
static OSStatus AbortDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*) { return 0; }

// ---- which properties exist ----------------------------------------------------------------
static Boolean HasProperty(AudioServerPlugInDriverRef inDriver, AudioObjectID inObject, pid_t, const AudioObjectPropertyAddress* a) {
    if (inDriver != gDriverRef || !a) return false;
    const AudioObjectPropertySelector s = a->mSelector;
    if (inObject == kObjPlugIn) {
        switch (s) {
            case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
            case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
            case kAudioPlugInPropertyDeviceList: case kAudioPlugInPropertyTranslateUIDToDevice:
            case kAudioPlugInPropertyResourceBundle:
                return true;
        }
        return false;
    }
    if (DevByObject(inObject)) {
        switch (s) {
            case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
            case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
            case kAudioDevicePropertyDeviceUID: case kAudioDevicePropertyModelUID: case kAudioDevicePropertyTransportType:
            case kAudioDevicePropertyRelatedDevices: case kAudioDevicePropertyClockDomain: case kAudioDevicePropertyDeviceIsAlive:
            case kAudioDevicePropertyDeviceIsRunning: case kAudioObjectPropertyControlList:
            case kAudioDevicePropertyNominalSampleRate: case kAudioDevicePropertyAvailableNominalSampleRates:
            case kAudioDevicePropertyIsHidden: case kAudioDevicePropertyZeroTimeStampPeriod: case kAudioDevicePropertyStreams:
            case kAudioDevicePropertyDeviceCanBeDefaultDevice: case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
            case kAudioDevicePropertyLatency: case kAudioDevicePropertySafetyOffset:
            case kAudioDevicePropertyPreferredChannelsForStereo:
                return true;
        }
        return false;
    }
    if (DevByStream(inObject)) {
        switch (s) {
            case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
            case kAudioObjectPropertyOwnedObjects: case kAudioStreamPropertyIsActive: case kAudioStreamPropertyDirection:
            case kAudioStreamPropertyTerminalType: case kAudioStreamPropertyStartingChannel: case kAudioStreamPropertyLatency:
            case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
            case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
                return true;
        }
        return false;
    }
    return false;
}

static OSStatus IsPropertySettable(AudioServerPlugInDriverRef inDriver, AudioObjectID inObject, pid_t pid,
                                   const AudioObjectPropertyAddress* a, Boolean* outIsSettable) {
    if (inDriver != gDriverRef || !a || !outIsSettable) return kAudioHardwareIllegalOperationError;
    if (!HasProperty(inDriver, inObject, pid, a)) return kAudioHardwareUnknownPropertyError;
    // Fixed format; "setting" the same values is accepted so picky clients don't give up.
    *outIsSettable = (DevByObject(inObject) && a->mSelector == kAudioDevicePropertyNominalSampleRate) ||
                     (DevByStream(inObject) && (a->mSelector == kAudioStreamPropertyVirtualFormat ||
                                                a->mSelector == kAudioStreamPropertyPhysicalFormat));
    return 0;
}

static OSStatus GetPropertyDataSize(AudioServerPlugInDriverRef inDriver, AudioObjectID inObject, pid_t pid,
                                    const AudioObjectPropertyAddress* a, UInt32 qs, const void* q, UInt32* outDataSize) {
    if (!outDataSize) return kAudioHardwareIllegalOperationError;
    UInt8 scratch[512];
    UInt32 used = 0;
    OSStatus err = GetPropertyData(inDriver, inObject, pid, a, qs, q, sizeof(scratch), &used, scratch);
    if (err == 0) *outDataSize = used;     // CFStrings handed out are CFSTR constants: nothing to release
    return err;
}

// ---- property values -----------------------------------------------------------------------
static OSStatus GetPropertyData(AudioServerPlugInDriverRef inDriver, AudioObjectID inObject, pid_t,
                                const AudioObjectPropertyAddress* a, UInt32 qs, const void* q,
                                UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    if (inDriver != gDriverRef || !a || !outDataSize || !outData) return kAudioHardwareIllegalOperationError;
    *outDataSize = 0;

    if (inObject == kObjPlugIn) {
        switch (a->mSelector) {
            case kAudioObjectPropertyBaseClass: U32_OUT(kAudioObjectClassID); return 0;
            case kAudioObjectPropertyClass: U32_OUT(kAudioPlugInClassID); return 0;
            case kAudioObjectPropertyOwner: U32_OUT(kAudioObjectUnknown); return 0;
            case kAudioObjectPropertyManufacturer: CFSTR_OUT(CFSTR(kManufacturer)); return 0;
            case kAudioObjectPropertyOwnedObjects:
            case kAudioPlugInPropertyDeviceList: {
                AudioObjectID ids[2] = { kObjMic, kObjFeed };
                UInt32 n = inDataSize / sizeof(AudioObjectID); if (n > 2) n = 2;
                memcpy(outData, ids, n * sizeof(AudioObjectID)); *outDataSize = n * sizeof(AudioObjectID);
                return 0;
            }
            case kAudioPlugInPropertyTranslateUIDToDevice: {
                NEED(sizeof(AudioObjectID));
                AudioObjectID id = kAudioObjectUnknown;
                if (qs == sizeof(CFStringRef) && q && *(const CFStringRef*)q)
                    for (auto& d : gDev)
                        if (CFStringCompare(*(const CFStringRef*)q, d.uid, 0) == kCFCompareEqualTo) id = d.id;
                *(AudioObjectID*)outData = id; *outDataSize = sizeof(AudioObjectID);
                return 0;
            }
            case kAudioPlugInPropertyResourceBundle: CFSTR_OUT(CFSTR("")); return 0;
        }
        return kAudioHardwareUnknownPropertyError;
    }

    if (DeviceInfo* d = DevByObject(inObject)) {
        const AudioObjectPropertyScope myScope = d->input ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput;
        switch (a->mSelector) {
            case kAudioObjectPropertyBaseClass: U32_OUT(kAudioObjectClassID); return 0;
            case kAudioObjectPropertyClass: U32_OUT(kAudioDeviceClassID); return 0;
            case kAudioObjectPropertyOwner: U32_OUT(kObjPlugIn); return 0;
            case kAudioObjectPropertyName: CFSTR_OUT(d->name); return 0;
            case kAudioObjectPropertyManufacturer: CFSTR_OUT(CFSTR(kManufacturer)); return 0;
            case kAudioDevicePropertyDeviceUID: CFSTR_OUT(d->uid); return 0;
            case kAudioDevicePropertyModelUID: CFSTR_OUT(d->model); return 0;
            case kAudioDevicePropertyTransportType: U32_OUT(kAudioDeviceTransportTypeVirtual); return 0;
            case kAudioDevicePropertyRelatedDevices: U32_OUT(d->id); return 0;
            case kAudioDevicePropertyClockDomain: U32_OUT(0); return 0;
            case kAudioDevicePropertyDeviceIsAlive: U32_OUT(1); return 0;
            case kAudioDevicePropertyDeviceIsRunning: {
                pthread_mutex_lock(&gStateMutex); UInt32 r = d->ioRunning > 0; pthread_mutex_unlock(&gStateMutex);
                U32_OUT(r); return 0;
            }
            // The Mic may be the default input; the Feed is never a default anything.
            case kAudioDevicePropertyDeviceCanBeDefaultDevice: U32_OUT(d->input ? 1 : 0); return 0;
            case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: U32_OUT(0); return 0;
            case kAudioDevicePropertyLatency: U32_OUT(0); return 0;
            case kAudioDevicePropertySafetyOffset: U32_OUT(0); return 0;
            case kAudioDevicePropertyIsHidden: U32_OUT(d->input ? 0 : 1); return 0;
            case kAudioDevicePropertyZeroTimeStampPeriod: U32_OUT(kPeriodFrames); return 0;
            case kAudioObjectPropertyControlList: *outDataSize = 0; return 0;
            case kAudioDevicePropertyNominalSampleRate: {
                NEED(sizeof(Float64)); *(Float64*)outData = kSampleRate; *outDataSize = sizeof(Float64); return 0;
            }
            case kAudioDevicePropertyAvailableNominalSampleRates: {
                if (inDataSize >= sizeof(AudioValueRange)) {
                    AudioValueRange* r = (AudioValueRange*)outData;
                    r->mMinimum = kSampleRate; r->mMaximum = kSampleRate;
                    *outDataSize = sizeof(AudioValueRange);
                }
                return 0;
            }
            case kAudioDevicePropertyPreferredChannelsForStereo: {
                NEED(2 * sizeof(UInt32));
                ((UInt32*)outData)[0] = 1; ((UInt32*)outData)[1] = 2; *outDataSize = 2 * sizeof(UInt32);
                return 0;
            }
            case kAudioObjectPropertyOwnedObjects:
            case kAudioDevicePropertyStreams: {
                bool has = a->mScope == kAudioObjectPropertyScopeGlobal || a->mScope == myScope;
                if (has && inDataSize >= sizeof(AudioObjectID)) {
                    *(AudioObjectID*)outData = d->stream; *outDataSize = sizeof(AudioObjectID);
                }
                return 0;
            }
        }
        return kAudioHardwareUnknownPropertyError;
    }

    if (DeviceInfo* d = DevByStream(inObject)) {
        switch (a->mSelector) {
            case kAudioObjectPropertyBaseClass: U32_OUT(kAudioObjectClassID); return 0;
            case kAudioObjectPropertyClass: U32_OUT(kAudioStreamClassID); return 0;
            case kAudioObjectPropertyOwner: U32_OUT(d->id); return 0;
            case kAudioObjectPropertyOwnedObjects: *outDataSize = 0; return 0;
            case kAudioStreamPropertyIsActive: U32_OUT(1); return 0;
            case kAudioStreamPropertyDirection: U32_OUT(d->input ? 1 : 0); return 0;
            case kAudioStreamPropertyTerminalType:
                U32_OUT(d->input ? kAudioStreamTerminalTypeMicrophone : kAudioStreamTerminalTypeSpeaker); return 0;
            case kAudioStreamPropertyStartingChannel: U32_OUT(1); return 0;
            case kAudioStreamPropertyLatency: U32_OUT(0); return 0;
            case kAudioStreamPropertyVirtualFormat:
            case kAudioStreamPropertyPhysicalFormat: {
                NEED(sizeof(AudioStreamBasicDescription));
                *(AudioStreamBasicDescription*)outData = Format(); *outDataSize = sizeof(AudioStreamBasicDescription);
                return 0;
            }
            case kAudioStreamPropertyAvailableVirtualFormats:
            case kAudioStreamPropertyAvailablePhysicalFormats: {
                if (inDataSize >= sizeof(AudioStreamRangedDescription)) {
                    AudioStreamRangedDescription* r = (AudioStreamRangedDescription*)outData;
                    r->mFormat = Format();
                    r->mSampleRateRange.mMinimum = kSampleRate; r->mSampleRateRange.mMaximum = kSampleRate;
                    *outDataSize = sizeof(AudioStreamRangedDescription);
                }
                return 0;
            }
        }
        return kAudioHardwareUnknownPropertyError;
    }
    return kAudioHardwareBadObjectError;
}

static OSStatus SetPropertyData(AudioServerPlugInDriverRef inDriver, AudioObjectID inObject, pid_t,
                                const AudioObjectPropertyAddress* a, UInt32, const void*, UInt32 inDataSize, const void* inData) {
    if (inDriver != gDriverRef || !a || !inData) return kAudioHardwareIllegalOperationError;
    if (DevByObject(inObject) && a->mSelector == kAudioDevicePropertyNominalSampleRate) {
        if (inDataSize != sizeof(Float64)) return kAudioHardwareBadPropertySizeError;
        return *(const Float64*)inData == kSampleRate ? 0 : kAudioHardwareIllegalOperationError;
    }
    if (DevByStream(inObject) &&
        (a->mSelector == kAudioStreamPropertyVirtualFormat || a->mSelector == kAudioStreamPropertyPhysicalFormat)) {
        if (inDataSize != sizeof(AudioStreamBasicDescription)) return kAudioHardwareBadPropertySizeError;
        const AudioStreamBasicDescription* f = (const AudioStreamBasicDescription*)inData;
        return (f->mSampleRate == kSampleRate && f->mChannelsPerFrame == kChannels && f->mFormatID == kAudioFormatLinearPCM)
                   ? 0 : kAudioDeviceUnsupportedFormatError;
    }
    return kAudioHardwareUnknownPropertyError;
}

// ---- IO ------------------------------------------------------------------------------------
static OSStatus StartIO(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32) {
    DeviceInfo* d = DevByObject(inDevice);
    if (inDriver != gDriverRef || !d) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    if (d->ioRunning++ == 0) {
        d->numberTimeStamps = 0;
        d->anchorHostTime = mach_absolute_time();
    }
    pthread_mutex_unlock(&gStateMutex);
    return 0;
}
static OSStatus StopIO(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32) {
    DeviceInfo* d = DevByObject(inDevice);
    if (inDriver != gDriverRef || !d) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    if (d->ioRunning > 0) --d->ioRunning;
    pthread_mutex_unlock(&gStateMutex);
    return 0;
}

// Each device's clock: one zero timestamp per period, paced by the host clock.
static OSStatus GetZeroTimeStamp(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32,
                                 Float64* outSampleTime, UInt64* outHostTime, UInt64* outSeed) {
    DeviceInfo* d = DevByObject(inDevice);
    if (inDriver != gDriverRef || !d) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    UInt64 now = mach_absolute_time();
    Float64 ticksPerPeriod = gHostTicksPerFrame * (Float64)kPeriodFrames;
    UInt64 next = d->anchorHostTime + (UInt64)((Float64)(d->numberTimeStamps + 1) * ticksPerPeriod);
    if (next <= now) ++d->numberTimeStamps;
    *outSampleTime = (Float64)(d->numberTimeStamps * kPeriodFrames);
    *outHostTime = d->anchorHostTime + (UInt64)((Float64)d->numberTimeStamps * ticksPerPeriod);
    *outSeed = 1;
    pthread_mutex_unlock(&gStateMutex);
    return 0;
}

static OSStatus WillDoIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32, UInt32 op,
                                  Boolean* outWillDo, Boolean* outWillDoInPlace) {
    DeviceInfo* d = DevByObject(inDevice);
    if (inDriver != gDriverRef || !d) return kAudioHardwareBadObjectError;
    bool will = d->input ? op == kAudioServerPlugInIOOperationReadInput : op == kAudioServerPlugInIOOperationWriteMix;
    if (outWillDo) *outWillDo = will;
    if (outWillDoInPlace) *outWillDoInPlace = true;
    return 0;
}
static OSStatus BeginIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*) {
    return (inDriver == gDriverRef && DevByObject(inDevice)) ? 0 : kAudioHardwareBadObjectError;
}
static OSStatus EndIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*) {
    return (inDriver == gDriverRef && DevByObject(inDevice)) ? 0 : kAudioHardwareBadObjectError;
}

static OSStatus DoIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDevice, AudioObjectID, UInt32,
                              UInt32 op, UInt32 frames, const AudioServerPlugInIOCycleInfo*, void* ioMainBuffer, void*) {
    DeviceInfo* d = DevByObject(inDevice);
    if (inDriver != gDriverRef || !d) return kAudioHardwareBadObjectError;
    if (!ioMainBuffer) return 0;
    Float32* buf = (Float32*)ioMainBuffer;

    if (!d->input && op == kAudioServerPlugInIOOperationWriteMix) {
        // Feed: append to the FIFO. If the Mic isn't being read, the reader catches up by skipping.
        UInt64 w = gWritten;
        for (UInt32 i = 0; i < frames; ++i) {
            UInt32 slot = (UInt32)((w + i) & (kFifoFrames - 1)) * kChannels;
            for (UInt32 c = 0; c < kChannels; ++c) gFifo[slot + c] = buf[i * kChannels + c];
        }
        __atomic_store_n(&gWritten, w + frames, __ATOMIC_RELEASE);
    } else if (d->input && op == kAudioServerPlugInIOOperationReadInput) {
        UInt64 w = __atomic_load_n(&gWritten, __ATOMIC_ACQUIRE);
        UInt64 r = gRead;
        if (w - r > kMaxLagFrames || r > w) r = w > frames ? w - frames : 0;   // too far behind: jump
        UInt64 avail = w - r;
        UInt32 n = avail < frames ? (UInt32)avail : frames;
        // Not enough yet (start-up / jitter): pad the front with silence, play what's there.
        UInt32 pad = frames - n;
        memset(buf, 0, (size_t)pad * kBytesPerFrame);
        for (UInt32 i = 0; i < n; ++i) {
            UInt32 slot = (UInt32)((r + i) & (kFifoFrames - 1)) * kChannels;
            for (UInt32 c = 0; c < kChannels; ++c) buf[(pad + i) * kChannels + c] = gFifo[slot + c];
        }
        gRead = r + n;
    }
    return 0;
}
