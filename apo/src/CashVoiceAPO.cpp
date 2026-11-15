// CashVoice — system-wide microphone effect (Audio Processing Object).
//
// Loaded by the Windows audio engine (audiodg.exe) as a *stream effect* on capture
// endpoints. Applies the voice effect and mixes in soundboard audio that the
// CashVoice app writes into a shared-memory ring buffer ("Global\CashVoiceShared").

#include <windows.h>
#include <unknwn.h>
#include <mmreg.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioenginebaseapo.h>
#include <sddl.h>
#include <strsafe.h>
#include <new>
#include <cstdarg>
#include <cstdio>
#include <vector>
#include "dsp.h"

#ifndef APOERR_FORMAT_NOT_SUPPORTED
#define APOERR_FORMAT_NOT_SUPPORTED ((HRESULT)0x88890008L)
#endif

// ---- identifiers -----------------------------------------------------------------
static const wchar_t* kClsidStr = L"{8F3A7C21-5B6D-4E90-A1C4-7D2E9B0F6A38}";
static const CLSID CLSID_CashVoiceAPO =
    { 0x8F3A7C21, 0x5B6D, 0x4E90, { 0xA1, 0xC4, 0x7D, 0x2E, 0x9B, 0x0F, 0x6A, 0x38 } };
static const GUID kSubtypeFloat =
    { 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

// Marker interface every system-effect APO exposes (IAudioSystemEffects).
MIDL_INTERFACE("5FA00F27-ADD6-499A-8A9D-6B98521FA75B")
IVfSystemEffects : public IUnknown {};

static HMODULE g_hMod = nullptr;
static volatile LONG g_objects = 0;

// ---- shared memory (layout MUST match app/Core/SharedMem.cs) ---------------------
static const uint32_t kMagic = 0x47524656;   // 'VFRG'
static const uint32_t kRingFrames = 1u << 16;

struct SharedHeader {
    uint32_t magic, version;
    int32_t  voiceEnabled, micMute;
    float    pitch, ringHz, ringMix, echoMs, echoFb, echoMix, hpHz, lpHz, drive, micGain, soundGain;
    int32_t  soundEnabled;
    uint32_t apoSampleRate, apoInstances, leadFrames;
    uint32_t reserved0, reserved1;
    int64_t  writePos;
};
static_assert(sizeof(SharedHeader) == 96, "SharedHeader layout must be 96 bytes");
static const SIZE_T kMapSize = sizeof(SharedHeader) + kRingFrames * sizeof(float);

static SharedHeader* g_shared = nullptr;
static HANDLE        g_map    = nullptr;
static SRWLOCK       g_lock   = SRWLOCK_INIT;
static ULONGLONG     g_lastTry = 0;

// The app and the APO share one small file in C:\ProgramData\CashVoice (set up by the installer
// with permissions for the audio engine). A named "Global\" section doesn't work: audiodg.exe runs
// with a stripped-down token and isn't allowed to create one. FILE_ATTRIBUTE_TEMPORARY keeps the
// pages in RAM rather than writing them to disk.
static const wchar_t* kFile = L"C:\\ProgramData\\CashVoice\\shared.bin";

// One line per event in C:\ProgramData\CashVoice\apo.log (never called on the real-time thread).
static void Log(const wchar_t* fmt, ...) {
    wchar_t msg[400];
    va_list ap; va_start(ap, fmt);
    StringCchVPrintfW(msg, 400, fmt, ap);
    va_end(ap);
    SYSTEMTIME t; GetLocalTime(&t);
    char line[600];
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE, "%04u-%02u-%02u %02u:%02u:%02u pid=%lu %ls\r\n",
                        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, GetCurrentProcessId(), msg);
    OutputDebugStringA(line);
    HANDLE f = CreateFileW(L"C:\\ProgramData\\CashVoice\\apo.log", FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        // Diagnostics: say why, in a place the audio engine (LOCAL SERVICE) can always write to.
        DWORD err = GetLastError();
        wchar_t tmp[MAX_PATH];
        DWORD n2 = GetTempPathW(MAX_PATH, tmp);
        if (n2 == 0 || n2 > MAX_PATH - 32) return;
        StringCchCatW(tmp, MAX_PATH, L"CashVoiceAPO.log");
        HANDLE g = CreateFileW(tmp, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (g == INVALID_HANDLE_VALUE) return;
        char extra[64];
        int m = _snprintf_s(extra, sizeof(extra), _TRUNCATE, "(apo.log open failed, error %lu) ", err);
        DWORD w2 = 0;
        if (m > 0) WriteFile(g, extra, (DWORD)m, &w2, nullptr);
        if (n > 0) WriteFile(g, line, (DWORD)n, &w2, nullptr);
        CloseHandle(g);
        return;
    }
    LARGE_INTEGER size{};
    if (GetFileSizeEx(f, &size) && size.QuadPart > 256 * 1024) {   // keep it small
        CloseHandle(f);
        f = CreateFileW(L"C:\\ProgramData\\CashVoice\\apo.log", GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS,
                        FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) return;
    }
    DWORD w = 0;
    if (n > 0) WriteFile(f, line, (DWORD)n, &w, nullptr);
    CloseHandle(f);
}

static SharedHeader* MapShared() {
    HANDLE file = CreateFileW(kFile, GENERIC_READ | GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                              FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        Log(L"can't open %s (error %lu)", kFile, GetLastError());
        return nullptr;
    }
    HANDLE map = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, (DWORD)kMapSize, nullptr);
    DWORD err = GetLastError();
    CloseHandle(file);                       // the mapping keeps the file open
    if (!map) { Log(L"CreateFileMapping failed (error %lu)", err); return nullptr; }
    void* view = MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, kMapSize);
    if (!view) { Log(L"MapViewOfFile failed (error %lu)", GetLastError()); CloseHandle(map); return nullptr; }
    g_map = map;
    SharedHeader* h = (SharedHeader*)view;
    if (h->magic != kMagic) {                       // fresh mapping: write defaults
        h->version = 1;
        h->micGain = 1.f; h->soundGain = 1.f; h->soundEnabled = 1;
        h->ringHz = 50.f; h->echoMs = 250.f; h->echoFb = 0.4f;
        h->leadFrames = 2880;
        MemoryBarrier();
        h->magic = kMagic;
    }
    h->apoInstances = 0;     // first mapping in this audiodg: any older count belonged to a dead process
    Log(L"shared memory ready");
    return h;
}

// Non-real-time threads only. Retries (at most every 2 s) if the folder wasn't there yet.
static SharedHeader* GetShared() {
    if (g_shared) return g_shared;
    AcquireSRWLockExclusive(&g_lock);
    ULONGLONG now = GetTickCount64();
    if (!g_shared && (g_lastTry == 0 || now - g_lastTry > 2000)) {
        g_lastTry = now;
        SharedHeader* h = MapShared();
        if (h) { MemoryBarrier(); g_shared = h; }
    }
    ReleaseSRWLockExclusive(&g_lock);
    return g_shared;
}

// Remember which unknown interfaces we've already logged (the audio engine asks for dozens, repeatedly).
static GUID    g_missed[64];
static LONG    g_nMissed = 0;
static SRWLOCK g_missLock = SRWLOCK_INIT;
static bool FirstMiss(REFIID riid) {
    AcquireSRWLockExclusive(&g_missLock);
    bool first = true;
    for (LONG k = 0; k < g_nMissed; ++k) if (IsEqualGUID(g_missed[k], riid)) { first = false; break; }
    if (first && g_nMissed < 64) g_missed[g_nMissed++] = riid;
    ReleaseSRWLockExclusive(&g_missLock);
    return first;
}

static void FormatStr(IAudioMediaType* t, wchar_t* out, size_t n) {
    const WAVEFORMATEX* wf = t ? t->GetAudioFormat() : nullptr;
    if (!wf) { StringCchCopyW(out, n, L"(none)"); return; }
    StringCchPrintfW(out, n, L"tag=0x%X bits=%u ch=%u rate=%lu", wf->wFormatTag, wf->wBitsPerSample,
                     wf->nChannels, wf->nSamplesPerSec);
}

// ---- format helper ----------------------------------------------------------------
static bool IsFloat32(IAudioMediaType* t, UINT32* ch, UINT32* sr) {
    if (!t) return false;
    const WAVEFORMATEX* wf = t->GetAudioFormat();
    if (!wf || wf->wBitsPerSample != 32) return false;
    bool fl = (wf->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
    if (!fl && wf->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wf->cbSize >= 22)
        fl = IsEqualGUID(((const WAVEFORMATEXTENSIBLE*)wf)->SubFormat, kSubtypeFloat) != FALSE;
    if (!fl) return false;
    if (ch) *ch = wf->nChannels;
    if (sr) *sr = wf->nSamplesPerSec;
    return true;
}

// ---- the APO ------------------------------------------------------------------------
class CashVoiceAPO final : public IAudioProcessingObject,
                            public IAudioProcessingObjectRT,
                            public IAudioProcessingObjectConfiguration,
                            public IVfSystemEffects {
    // The audio engine creates APOs aggregated: it passes its own "outer" object to CreateInstance
    // and only talks to us through it. So, as COM aggregation requires, we have a private
    // ("non-delegating") IUnknown that owns our lifetime, and every interface's IUnknown methods
    // forward to the outer object (or to that private IUnknown when nobody aggregates us).
    struct Inner final : IUnknown {
        CashVoiceAPO* self = nullptr;
        STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override { return self->InnerQueryInterface(riid, ppv); }
        STDMETHOD_(ULONG, AddRef)() override { return (ULONG)InterlockedIncrement(&self->m_ref); }
        STDMETHOD_(ULONG, Release)() override {
            ULONG r = (ULONG)InterlockedDecrement(&self->m_ref);
            if (r == 0) delete self;
            return r;
        }
    };

    LONG   m_ref = 1;
    Inner  m_inner;
    IUnknown* m_outer = nullptr;   // never AddRef'd: the outer object owns us, not the other way round
    UINT32 m_channels = 0, m_sr = 0;
    bool   m_locked = false;
    int64_t m_readPos = 0;
    vf::Engine m_engine;
    std::vector<float> m_sound;

public:
    explicit CashVoiceAPO(IUnknown* outer) {
        m_inner.self = this;
        m_outer = outer ? outer : static_cast<IUnknown*>(&m_inner);
        InterlockedIncrement(&g_objects);
    }
    ~CashVoiceAPO() { InterlockedDecrement(&g_objects); Log(L"APO released"); }

    IUnknown* InnerUnknown() { return &m_inner; }

    // IUnknown (delegating)
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override { return m_outer->QueryInterface(riid, ppv); }
    STDMETHOD_(ULONG, AddRef)() override { return m_outer->AddRef(); }
    STDMETHOD_(ULONG, Release)() override { return m_outer->Release(); }

    HRESULT InnerQueryInterface(REFIID riid, void** ppv) {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (IsEqualGUID(riid, __uuidof(IUnknown))) {
            *ppv = static_cast<IUnknown*>(&m_inner);
            m_inner.AddRef();
            return S_OK;
        }
        if (IsEqualGUID(riid, __uuidof(IAudioProcessingObject)))
            *ppv = static_cast<IAudioProcessingObject*>(this);
        else if (IsEqualGUID(riid, __uuidof(IAudioProcessingObjectRT)))
            *ppv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (IsEqualGUID(riid, __uuidof(IAudioProcessingObjectConfiguration)))
            *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (IsEqualGUID(riid, __uuidof(IVfSystemEffects)))
            *ppv = static_cast<IVfSystemEffects*>(this);
        else {
            wchar_t i[64] = L"?";
            StringFromGUID2(riid, i, 64);
            if (FirstMiss(riid)) Log(L"QueryInterface: no %s", i);
            return E_NOINTERFACE;
        }
        static_cast<IUnknown*>(*ppv)->AddRef();   // delegating AddRef, as COM aggregation requires
        return S_OK;
    }

    // IAudioProcessingObject
    STDMETHOD(Reset)() override { Log(L"Reset"); return S_OK; }
    STDMETHOD(GetLatency)(HNSTIME* pTime) override {
        Log(L"GetLatency");
        if (!pTime) return E_POINTER;
        *pTime = 0;
        return S_OK;
    }
    STDMETHOD(GetRegistrationProperties)(APO_REG_PROPERTIES** ppRegProps) override {
        Log(L"GetRegistrationProperties");
        if (!ppRegProps) return E_POINTER;
        const UINT32 nIf = 3;
        SIZE_T sz = sizeof(APO_REG_PROPERTIES) + (nIf - 1) * sizeof(IID);
        APO_REG_PROPERTIES* p = (APO_REG_PROPERTIES*)CoTaskMemAlloc(sz);
        if (!p) return E_OUTOFMEMORY;
        ZeroMemory(p, sz);
        p->clsid = CLSID_CashVoiceAPO;
        p->Flags = (APO_FLAG)(APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH |
                              APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH);
        StringCchCopyW(p->szFriendlyName, 256, L"CashVoice Voice Changer");
        StringCchCopyW(p->szCopyrightInfo, 256, L"CashVoice");
        p->u32MajorVersion = 1; p->u32MinorVersion = 0;
        p->u32MinInputConnections = 1;  p->u32MaxInputConnections = 1;
        p->u32MinOutputConnections = 1; p->u32MaxOutputConnections = 1;
        p->u32MaxInstances = 0xFFFFFFFF;
        p->u32NumAPOInterfaces = nIf;
        p->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);
        p->iidAPOInterfaceList[1] = __uuidof(IAudioProcessingObjectRT);
        p->iidAPOInterfaceList[2] = __uuidof(IAudioProcessingObjectConfiguration);
        *ppRegProps = p;
        return S_OK;
    }
    // Works for both slot styles: the legacy LFX/GFX slots pass APOInitSystemEffects, the modern
    // SFX/MFX ones APOInitSystemEffects2 (bigger). We need nothing from either.
    STDMETHOD(Initialize)(UINT32 cbData, BYTE*) override {
        Log(L"Initialize (%u bytes of init data: %s slot)", cbData,
            cbData >= sizeof(APOInitSystemEffects2) ? L"modern" : L"legacy");
        GetShared();                 // create the shared mapping early (non-realtime thread)
        return S_OK;
    }
    STDMETHOD(IsInputFormatSupported)(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                      IAudioMediaType** ppSupported) override {
        wchar_t a[96], b[96];
        FormatStr(pOpposite, a, 96); FormatStr(pRequested, b, 96);
        Log(L"IsInputFormatSupported opposite[%s] requested[%s]", a, b);
        if (!pRequested || !ppSupported) return E_POINTER;
        *ppSupported = nullptr;
        UINT32 ch = 0;
        if (!IsFloat32(pRequested, &ch, nullptr)) {
            const WAVEFORMATEX* wf = pRequested->GetAudioFormat();
            Log(L"IsInputFormatSupported: rejected tag=%u bits=%u ch=%u", wf ? wf->wFormatTag : 0,
                wf ? wf->wBitsPerSample : 0, wf ? wf->nChannels : 0);
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        return Negotiate(pOpposite, pRequested, ppSupported, L"input");
    }
    STDMETHOD(IsOutputFormatSupported)(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                       IAudioMediaType** ppSupported) override {
        wchar_t a[96], b[96];
        FormatStr(pOpposite, a, 96); FormatStr(pRequested, b, 96);
        Log(L"IsOutputFormatSupported opposite[%s] requested[%s]", a, b);
        if (!pRequested || !ppSupported) return E_POINTER;
        *ppSupported = nullptr;
        if (!IsFloat32(pRequested, nullptr, nullptr)) {
            Log(L"IsOutputFormatSupported: rejected");
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        return Negotiate(pOpposite, pRequested, ppSupported, L"output");
    }

    // We process in place, so input and output must be the same format. When the engine asks for
    // a different channel count or rate on the other side (e.g. mono mic in, stereo out), say
    // "not that, but this" (S_FALSE + the opposite format) like Microsoft's base APO does; the
    // engine then adds its own converter after us. Saying S_OK there made it drop the APO.
    HRESULT Negotiate(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                      IAudioMediaType** ppSupported, const wchar_t* side) {
        UINT32 rc = 0, rr = 0, oc = 0, orate = 0;
        IsFloat32(pRequested, &rc, &rr);
        if (pOpposite && IsFloat32(pOpposite, &oc, &orate) && (oc != rc || orate != rr)) {
            *ppSupported = pOpposite;
            pOpposite->AddRef();
            m_channels = oc;
            Log(L"%s format: suggesting %u ch %u Hz instead", side, oc, orate);
            return S_FALSE;
        }
        *ppSupported = pRequested;
        pRequested->AddRef();
        m_channels = rc;
        return S_OK;
    }
    STDMETHOD(GetInputChannelCount)(UINT32* pCount) override {
        Log(L"GetInputChannelCount -> %u", m_channels);
        if (!pCount) return E_POINTER;
        *pCount = m_channels;
        return S_OK;
    }

    // IAudioProcessingObjectConfiguration
    STDMETHOD(LockForProcess)(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** ppIn,
                              UINT32 nOut, APO_CONNECTION_DESCRIPTOR** ppOut) override {
        Log(L"LockForProcess called: %u in, %u out", nIn, nOut);
        if (nIn != 1 || nOut != 1 || !ppIn || !ppOut) return E_INVALIDARG;
        UINT32 ch = 0, sr = 0;
        if (!IsFloat32(ppIn[0]->pFormat, &ch, &sr) || ch == 0) {
            Log(L"LockForProcess: unsupported format");
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        try {
            UINT32 maxFrames = ppIn[0]->u32MaxFrameCount;
            if (maxFrames < 1024) maxFrames = 1024;
            if (maxFrames > 65536) maxFrames = 65536;
            m_sound.assign(maxFrames, 0.f);
            m_engine.init((int)sr, (int)ch);
        } catch (...) { return E_OUTOFMEMORY; }
        m_channels = ch; m_sr = sr;
        SharedHeader* s = GetShared();
        if (s) {
            InterlockedExchange((volatile LONG*)&s->apoSampleRate, (LONG)sr);
            InterlockedIncrement((volatile LONG*)&s->apoInstances);
            int64_t wp = InterlockedCompareExchange64((volatile LONG64*)&s->writePos, 0, 0);
            m_readPos = wp - (int64_t)(s->leadFrames ? s->leadFrames : 2880);
        }
        m_locked = true;
        Log(L"LockForProcess: %u ch, %u Hz, max %u frames, shared=%s",
            ch, sr, ppIn[0]->u32MaxFrameCount, s ? L"yes" : L"no");
        return S_OK;
    }
    STDMETHOD(UnlockForProcess)() override {
        Log(L"UnlockForProcess");
        if (m_locked) {
            SharedHeader* s = g_shared;
            if (s && s->apoInstances > 0) InterlockedDecrement((volatile LONG*)&s->apoInstances);
            m_locked = false;
        }
        return S_OK;
    }

    // IAudioProcessingObjectRT  — runs on the real-time audio thread: no allocation, no locks.
    STDMETHOD_(void, APOProcess)(UINT32 nIn, APO_CONNECTION_PROPERTY** ppIn,
                                 UINT32 nOut, APO_CONNECTION_PROPERTY** ppOut) override {
        if (!m_locked || nIn < 1 || nOut < 1) return;
        float* in  = (float*)ppIn[0]->pBuffer;
        float* out = (float*)ppOut[0]->pBuffer;
        const UINT32 frames = ppIn[0]->u32ValidFrameCount;
        const size_t samples = (size_t)frames * m_channels;

        if (ppIn[0]->u32BufferFlags == BUFFER_SILENT) memset(out, 0, samples * sizeof(float));
        else if (in != out)                           memcpy(out, in, samples * sizeof(float));

        vf::Params p;
        SharedHeader* s = g_shared;
        const float* ring = nullptr;
        int64_t wp = 0, lead = 2880;
        if (s) {
            p.voiceOn = s->voiceEnabled != 0;  p.micMute = s->micMute != 0;  p.soundOn = s->soundEnabled != 0;
            p.pitch = s->pitch; p.ringHz = s->ringHz; p.ringMix = s->ringMix;
            p.echoMs = s->echoMs; p.echoFb = s->echoFb; p.echoMix = s->echoMix;
            p.hpHz = s->hpHz; p.lpHz = s->lpHz; p.drive = s->drive;
            p.micGain = s->micGain; p.soundGain = s->soundGain;
            s->reserved0++;                                  // heartbeat: the app shows "live" while this moves
            ring = (const float*)(s + 1);
            wp   = InterlockedCompareExchange64((volatile LONG64*)&s->writePos, 0, 0);
            if (s->leadFrames) lead = s->leadFrames;
            // keep our read cursor locked to wall-clock time (writePos - lead)
            int64_t desired = wp - lead, tol = lead / 2 > 240 ? lead / 2 : 240;
            if (m_readPos > desired + tol || m_readPos < desired - tol) m_readPos = desired;
        }

        UINT32 done = 0;
        const UINT32 cap = (UINT32)m_sound.size();
        while (done < frames) {
            UINT32 seg = frames - done; if (seg > cap) seg = cap;
            const float* snd = nullptr;
            if (ring && p.soundOn) {
                for (UINT32 i = 0; i < seg; ++i) {
                    int64_t pos = m_readPos + done + i;
                    m_sound[i] = (pos < wp && pos >= wp - (int64_t)kRingFrames)
                                     ? ring[(size_t)(pos & (int64_t)(kRingFrames - 1))] : 0.f;
                }
                snd = m_sound.data();
            }
            m_engine.process(out + (size_t)done * m_channels, seg, p, snd);
            done += seg;
        }
        m_readPos += frames;

        ppOut[0]->u32ValidFrameCount = frames;
        ppOut[0]->u32BufferFlags = BUFFER_VALID;
    }
    STDMETHOD_(UINT32, CalcInputFrames)(UINT32 outFrames) override  { return outFrames; }
    STDMETHOD_(UINT32, CalcOutputFrames)(UINT32 inFrames) override  { return inFrames; }
};

// ---- class factory ------------------------------------------------------------------
class Factory final : public IClassFactory {
    LONG m_ref = 1;
public:
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (IsEqualGUID(riid, __uuidof(IUnknown)) || IsEqualGUID(riid, __uuidof(IClassFactory))) {
            *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
        }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHOD_(ULONG, AddRef)() override { return (ULONG)InterlockedIncrement(&m_ref); }
    STDMETHOD_(ULONG, Release)() override {
        ULONG r = (ULONG)InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }
    STDMETHOD(CreateInstance)(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        wchar_t i[64] = L"?";
        StringFromGUID2(riid, i, 64);
        // The audio engine aggregates APOs (outer != null); COM then only allows asking for IUnknown.
        if (outer && !IsEqualGUID(riid, __uuidof(IUnknown))) {
            Log(L"CreateInstance aggregated iid=%s -> CLASS_E_NOAGGREGATION", i);
            return CLASS_E_NOAGGREGATION;
        }
        CashVoiceAPO* apo = new (std::nothrow) CashVoiceAPO(outer);
        if (!apo) return E_OUTOFMEMORY;
        IUnknown* inner = apo->InnerUnknown();
        HRESULT hr = inner->QueryInterface(riid, ppv);
        inner->Release();
        Log(L"CreateInstance %s iid=%s -> 0x%08lX", outer ? L"aggregated" : L"plain", i, (unsigned long)hr);
        return hr;
    }
    STDMETHOD(LockServer)(BOOL lock) override {
        if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects);
        return S_OK;
    }
};

// ---- COM exports + registration -------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { g_hMod = h; DisableThreadLibraryCalls(h); }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    wchar_t c[64] = L"?", i[64] = L"?", exe[MAX_PATH] = L"?";
    StringFromGUID2(rclsid, c, 64);
    StringFromGUID2(riid, i, 64);
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    Log(L"DllGetClassObject clsid=%s iid=%s in %s", c, i, exe);
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualGUID(rclsid, CLSID_CashVoiceAPO)) return CLASS_E_CLASSNOTAVAILABLE;
    Factory* f = new (std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

static LSTATUS SetStr(const wchar_t* key, const wchar_t* name, const wchar_t* val) {
    return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key, name, REG_SZ, val,
                           (DWORD)((wcslen(val) + 1) * sizeof(wchar_t)));
}
static LSTATUS SetDw(const wchar_t* key, const wchar_t* name, DWORD v) {
    return RegSetKeyValueW(HKEY_LOCAL_MACHINE, key, name, REG_DWORD, &v, sizeof(v));
}

STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_hMod, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    wchar_t k1[300], k2[300], k3[300];
    StringCchPrintfW(k1, 300, L"SOFTWARE\\Classes\\CLSID\\%s", kClsidStr);
    StringCchPrintfW(k2, 300, L"SOFTWARE\\Classes\\CLSID\\%s\\InprocServer32", kClsidStr);
    StringCchPrintfW(k3, 300, L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", kClsidStr);

    LSTATUS rc = SetStr(k1, nullptr, L"CashVoice APO");
    if (rc == ERROR_SUCCESS) rc = SetStr(k2, nullptr, path);
    if (rc == ERROR_SUCCESS) rc = SetStr(k2, L"ThreadingModel", L"Both");
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);

    SetStr(k3, L"FriendlyName", L"CashVoice Voice Changer");
    SetStr(k3, L"Copyright", L"CashVoice");
    SetDw(k3, L"MajorVersion", 1);  SetDw(k3, L"MinorVersion", 0);
    SetDw(k3, L"Flags", (DWORD)(APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH |
                                APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH));
    SetDw(k3, L"MinInputConnections", 1);  SetDw(k3, L"MaxInputConnections", 1);
    SetDw(k3, L"MinOutputConnections", 1); SetDw(k3, L"MaxOutputConnections", 1);
    SetDw(k3, L"MaxInstances", 0xFFFFFFFF);
    SetDw(k3, L"NumAPOInterfaces", 3);
    const IID* ids[3] = { &__uuidof(IAudioProcessingObject), &__uuidof(IAudioProcessingObjectRT),
                          &__uuidof(IAudioProcessingObjectConfiguration) };
    for (int i = 0; i < 3; ++i) {
        wchar_t name[32], guid[64];
        StringCchPrintfW(name, 32, L"APOInterface%d", i);
        StringFromGUID2(*ids[i], guid, 64);
        SetStr(k3, name, guid);
    }
    return S_OK;
}

STDAPI DllUnregisterServer() {
    wchar_t k1[300], k3[300];
    StringCchPrintfW(k1, 300, L"SOFTWARE\\Classes\\CLSID\\%s", kClsidStr);
    StringCchPrintfW(k3, 300, L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", kClsidStr);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, k1);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, k3);
    return S_OK;
}
