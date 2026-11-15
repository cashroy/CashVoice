using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace CashVoice.Core;

// Layout MUST match SharedHeader in apo/src/CashVoiceAPO.cpp (96 bytes, natural alignment).
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SharedHeader
{
    public uint Magic, Version;
    public int VoiceEnabled, MicMute;
    public float Pitch, RingHz, RingMix, EchoMs, EchoFb, EchoMix, HpHz, LpHz, Drive, MicGain, SoundGain;
    public int SoundEnabled;
    public uint ApoSampleRate, ApoInstances, LeadFrames;
    public uint Reserved0;      // heartbeat
    public float Formant;       // semitones
    public long WritePos;
}

// After the ring (0.2.5+). Layout MUST match MonitorExt in CashVoiceAPO.cpp (64 bytes).
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MonitorExt
{
    public int Claim;          // 1 = app is about to open its monitor stream, 2 = an APO instance took it
    public uint ClaimTick;     // Environment.TickCount when claimed
    public int Wet;            // monitor hears the voice effects
    public fixed uint Reserved[13];
}

/// <summary>
/// Connects to the shared block the APO keeps in C:\ProgramData\CashVoice\shared.bin. (A named
/// "Global\" section can't be used: the audio engine isn't allowed to create one.)
/// </summary>
public sealed unsafe class SharedMem : IDisposable
{
    public const int RingFrames = 1 << 16;
    public static readonly string Folder =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CashVoice");
    public static string SharedFile => System.IO.Path.Combine(Folder, "shared.bin");
    public static string ApoLog => System.IO.Path.Combine(Folder, "apo.log");
    const uint Magic = 0x47524656;
    static readonly int HeaderSize = sizeof(SharedHeader);

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _base;
    volatile bool _connected;

    bool _hasExt;

    public bool Connected => _connected;
    /// <summary>The running APO knows about the monitor controls (0.2.5+).</summary>
    public bool HasMonitor => _connected && _hasExt;
    SharedHeader* H => (SharedHeader*)_base;
    float* Ring => (float*)(_base + HeaderSize);
    MonitorExt* X => (MonitorExt*)(_base + HeaderSize + (long)RingFrames * sizeof(float));

    public bool TryConnect()
    {
        if (_connected) return true;
        long baseSize = HeaderSize + (long)RingFrames * sizeof(float);
        long fullSize = baseSize + sizeof(MonitorExt);
        // Cheap check first so polling doesn't throw while the APO hasn't created the file yet.
        var fi = new System.IO.FileInfo(SharedFile);
        if (!fi.Exists || fi.Length < baseSize) return false;
        _hasExt = fi.Length >= fullSize;          // an older APO still running makes the smaller file
        long size = _hasExt ? fullSize : baseSize;
        try
        {
            var fs = new System.IO.FileStream(SharedFile, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite,
                                              System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, size, MemoryMappedFileAccess.ReadWrite,
                                                   System.IO.HandleInheritability.None, leaveOpen: false);
            _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
            if (H->Magic != Magic) { Cleanup(); return false; }
            _connected = true;
            return true;
        }
        catch { Cleanup(); return false; }
    }

    void Cleanup()
    {
        _connected = false;
        try { if (_view != null && _base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
        _base = null;
        _view?.Dispose(); _view = null;
        _mmf?.Dispose(); _mmf = null;
    }

    public uint ApoSampleRate => _connected ? Volatile.Read(ref H->ApoSampleRate) : 0u;
    public uint ApoInstances => _connected ? Volatile.Read(ref H->ApoInstances) : 0u;
    /// <summary>Incremented by the APO on every audio buffer; if it moves, the effect is live.</summary>
    public uint Heartbeat => _connected ? Volatile.Read(ref H->Reserved0) : 0u;

    public uint LeadFrames
    {
        get => _connected ? H->LeadFrames : 0u;
        set { if (_connected) H->LeadFrames = value; }
    }

    public long WritePos
    {
        get => _connected ? Volatile.Read(ref H->WritePos) : 0L;
        set { if (_connected) Volatile.Write(ref H->WritePos, value); }
    }

    public void WriteRing(long pos, float[] src, int count)
    {
        if (!_connected) return;
        float* r = Ring;
        for (int i = 0; i < count; i++) r[(pos + i) & (RingFrames - 1)] = src[i];
    }

    public void ClearRing()
    {
        if (!_connected) return;
        new Span<float>(Ring, RingFrames).Clear();
    }

    public void Push(AppConfig c, VoicePreset v)
    {
        if (!_connected) return;
        var h = H;
        h->VoiceEnabled = c.VoiceEnabled ? 1 : 0;
        h->MicMute = c.MicMuted ? 1 : 0;
        h->Pitch = (float)v.Pitch;
        h->Formant = (float)v.Formant;
        h->RingHz = (float)v.RingHz;
        h->RingMix = (float)v.RingMix;
        h->EchoMs = (float)v.EchoMs;
        h->EchoFb = (float)v.EchoFb;
        h->EchoMix = (float)v.EchoMix;
        h->HpHz = (float)v.HpHz;
        h->LpHz = (float)v.LpHz;
        h->Drive = (float)v.Drive;
        h->MicGain = (float)c.MicGain;
        h->SoundGain = (float)c.SoundGain;
        h->SoundEnabled = c.SoundsIntoMic ? 1 : 0;
    }

    /// <summary>Remap if a newer APO grew the file since we connected (gains the monitor controls).</summary>
    public void RefreshLayout()
    {
        if (!_connected || _hasExt) return;
        try
        {
            long full = HeaderSize + (long)RingFrames * sizeof(float) + sizeof(MonitorExt);
            if (new System.IO.FileInfo(SharedFile).Length >= full) { Cleanup(); TryConnect(); }
        }
        catch { }
    }

    // ---- "hear yourself" monitor ---------------------------------------------------------
    /// <summary>Call right before opening the monitor's capture stream.</summary>
    public void ClaimMonitor(bool wet)
    {
        if (!HasMonitor) return;
        X->Wet = wet ? 1 : 0;
        X->ClaimTick = (uint)Environment.TickCount;
        Interlocked.Exchange(ref X->Claim, 1);
    }
    public bool MonitorWet { set { if (HasMonitor) X->Wet = value ? 1 : 0; } }
    /// <summary>True once an APO instance took the claim (the monitor stream is being processed).</summary>
    public bool MonitorClaimed => HasMonitor && Volatile.Read(ref X->Claim) == 2;
    /// <summary>Drop a claim nobody took (stream failed to start).</summary>
    public void CancelMonitorClaim() { if (HasMonitor) Interlocked.CompareExchange(ref X->Claim, 0, 1); }

    public void Dispose() => Cleanup();
}
