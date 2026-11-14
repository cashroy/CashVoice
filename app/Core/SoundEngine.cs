using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CashVoice.Core;

/// <summary>
/// Mixes soundboard clips and streams them into the APO's shared ring buffer in (roughly) real time,
/// and optionally plays them on your speakers too.
/// </summary>
public sealed class SoundEngine : IDisposable
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    sealed class Voice { public float[] Data = Array.Empty<float>(); public int Pos; public float Gain = 1f; }

    readonly SharedMem _shm;
    readonly object _lock = new();
    readonly List<Voice> _voices = new();
    readonly List<WaveOutEvent> _local = new();
    readonly Thread _thread;
    volatile bool _run = true;

    static readonly ConcurrentDictionary<string, float[]> Cache = new();

    public SoundEngine(SharedMem shm)
    {
        _shm = shm;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "CashVoice-Mixer" };
        _thread.Start();
    }

    public int SampleRate { get { uint r = _shm.ApoSampleRate; return r == 0 ? 48000 : (int)r; } }

    public void Play(float[] data, float gain)
    {
        lock (_lock) _voices.Add(new Voice { Data = data, Gain = gain });
    }

    public void StopAll()
    {
        lock (_lock) _voices.Clear();
        WaveOutEvent[] copy;
        lock (_local) copy = _local.ToArray();
        foreach (var w in copy) { try { w.Stop(); } catch { } }
    }

    public void PlayLocal(string path, float volume)
    {
        try
        {
            var reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 2f) };
            var wo = new WaveOutEvent();
            wo.Init(reader);
            wo.PlaybackStopped += (_, _) =>
            {
                lock (_local) _local.Remove(wo);
                wo.Dispose(); reader.Dispose();
            };
            lock (_local) _local.Add(wo);
            wo.Play();
        }
        catch { }
    }

    /// <summary>Decode any supported audio file to mono float at the given sample rate (cached for short clips).</summary>
    public static float[] GetOrDecode(string path, int sampleRate, int maxSeconds = 180)
    {
        string key = $"{path}|{sampleRate}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (Cache.TryGetValue(key, out var hit)) return hit;

        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 1);
        if (sp.WaveFormat.SampleRate != sampleRate)
            sp = new WdlResamplingSampleProvider(sp, sampleRate);

        var list = new List<float>(sampleRate * 8);
        var buf = new float[8192];
        int max = sampleRate * maxSeconds, n;
        while (list.Count < max && (n = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) list.Add(buf[i]);
        var arr = list.ToArray();
        if (arr.Length <= sampleRate * 30) Cache[key] = arr;
        return arr;
    }

    void Mix(float[] chunk, int n)
    {
        Array.Clear(chunk, 0, n);
        lock (_lock)
        {
            for (int vi = _voices.Count - 1; vi >= 0; vi--)
            {
                var v = _voices[vi];
                int m = Math.Min(n, v.Data.Length - v.Pos);
                for (int i = 0; i < m; i++) chunk[i] += v.Data[v.Pos + i] * v.Gain;
                v.Pos += m;
                if (v.Pos >= v.Data.Length) _voices.RemoveAt(vi);
            }
        }
    }

    void Loop()
    {
        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        var chunk = new float[1024];
        long written = 0, baseWritten = 0, lead = 0;
        double t0 = 0;
        uint sr = 0;

        while (_run)
        {
            if (!_shm.Connected) { sr = 0; Thread.Sleep(50); continue; }

            uint apoSr = _shm.ApoSampleRate; if (apoSr == 0) apoSr = 48000;
            if (sr != apoSr)
            {
                sr = apoSr;
                lead = (long)(sr * 0.06);
                _shm.LeadFrames = (uint)lead;
                baseWritten = _shm.WritePos;
                written = baseWritten;
                t0 = sw.Elapsed.TotalSeconds;
            }

            long target = baseWritten + lead + (long)((sw.Elapsed.TotalSeconds - t0) * sr);
            if (target - written > sr)               // stalled (sleep/hibernate): skip ahead cleanly
            {
                _shm.ClearRing();
                written = target - lead;
                _shm.WritePos = written;
            }
            while (written < target)
            {
                int n = (int)Math.Min(chunk.Length, target - written);
                Mix(chunk, n);
                _shm.WriteRing(written, chunk, n);
                written += n;
                _shm.WritePos = written;
            }
            Thread.Sleep(4);
        }
        timeEndPeriod(1);
    }

    public void Dispose()
    {
        _run = false;
        try { _thread.Join(500); } catch { }
        StopAll();
    }
}
