using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace CashVoice.Core;

// Layout MUST match SharedHeader in apo/src/CashVoiceAPO.cpp (96 bytes, natural alignment).
[StructLayout(LayoutKind.Sequential)]
public struct SharedHeader
{
    public uint Magic, Version;
    public int VoiceEnabled, MicMute;
    public float Pitch, RingHz, RingMix, EchoMs, EchoFb, EchoMix, HpHz, LpHz, Drive, MicGain, SoundGain;
    public int SoundEnabled;
    public uint ApoSampleRate, ApoInstances, LeadFrames;
    public uint Reserved0, Reserved1;
    public long WritePos;
}

/// <summary>Connects to the shared-memory block that the APO creates inside the Windows audio engine.</summary>
public sealed unsafe class SharedMem : IDisposable
{
    public const int RingFrames = 1 << 16;
    const string MapName = @"Global\CashVoiceShared";
    const uint Magic = 0x47524656;
    static readonly int HeaderSize = sizeof(SharedHeader);

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _base;
    volatile bool _connected;

    public bool Connected => _connected;
    SharedHeader* H => (SharedHeader*)_base;
    float* Ring => (float*)(_base + HeaderSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public bool TryConnect()
    {
        if (_connected) return true;
        // Cheap existence check first: avoids throwing an exception every poll while the APO isn't loaded.
        IntPtr probe = OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, MapName);
        if (probe == IntPtr.Zero) return false;
        CloseHandle(probe);
        try
        {
            long size = HeaderSize + (long)RingFrames * sizeof(float);
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
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

    public void Dispose() => Cleanup();
}
