using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
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
    // Speakers/headphones: one low-latency output with a live master volume. It only runs while
    // something is playing.
    const int LocalRate = 48000;
    readonly object _localLock = new();
    MixingSampleProvider? _localMix;
    VolumeSampleProvider? _localVol;
    WasapiOut? _localOut;
    float _localVolume = 0.8f;
    readonly Thread _thread;
    volatile bool _run = true;
    readonly AutoResetEvent _wake = new(false);

    static readonly ConcurrentDictionary<string, float[]> Cache = new();
    const long CacheBudgetBytes = 32L << 20;    // keep decoded clips in RAM up to 32 MB total
    static long _cacheBytes;

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
        _wake.Set();
    }

    public void StopAll()
    {
        lock (_lock) _voices.Clear();
        lock (_localLock) _localMix?.RemoveAllMixerInputs();
        StopLocalSoon();
    }

    /// <summary>Master volume for sounds on your speakers/headphones; applies to sounds already playing.</summary>
    public float LocalVolume
    {
        get => _localVolume;
        set { _localVolume = value; lock (_localLock) if (_localVol != null) _localVol.Volume = value; }
    }

    /// <summary>Play a clip on the default output (path is decoded and loudness-matched like the mic copy).</summary>
    public void PlayLocal(string path, float gain)
    {
        try
        {
            var data = GetOrDecode(path, LocalRate);
            lock (_localLock)
            {
                if (_localOut == null)
                {
                    _localMix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(LocalRate, 2)) { ReadFully = true };
                    _localMix.MixerInputEnded += (_, _) => StopLocalSoon();
                    _localVol = new VolumeSampleProvider(_localMix) { Volume = _localVolume };
                    _localOut = new WasapiOut(AudioClientShareMode.Shared, true, 40);
                    _localOut.Init(_localVol.ToWaveProvider());
                    _localOut.Play();
                }
                var clip = new VolumeSampleProvider(new ArraySampleProvider(data, LocalRate)) { Volume = gain };
                _localMix!.AddMixerInput(new MonoToStereoSampleProvider(clip));
                Interlocked.Exchange(ref _localIdleSince, -1);
            }
        }
        catch { }
    }

    long _localIdleSince = -1;

    // Close the output a couple of seconds after the last sound ends (an open output keeps the
    // audio device and a thread awake).
    // Also raised on the audio thread while the mixer holds its own lock: don't take _localLock here.
    void StopLocalSoon()
    {
        Interlocked.Exchange(ref _localIdleSince, Environment.TickCount64);
        System.Threading.Tasks.Task.Delay(2500).ContinueWith(_ =>
        {
            lock (_localLock)
            {
                long idle = Interlocked.Read(ref _localIdleSince);
                if (_localOut == null || idle < 0 || _localMix!.MixerInputs.Any()) return;
                if (Environment.TickCount64 - idle < 2000) return;
                try { _localOut.Stop(); } catch { }
                _localOut.Dispose(); _localOut = null; _localMix = null; _localVol = null;
            }
        });
    }

    sealed class ArraySampleProvider : ISampleProvider
    {
        readonly float[] _d; int _pos;
        public ArraySampleProvider(float[] d, int rate) { _d = d; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _d.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_d, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    // Clips from YouTube and files differ a lot in loudness. Bring each to about the level of a
    // normal speaking voice (-20 dBFS RMS over its louder parts), without letting peaks clip.
    static void Normalize(float[] a)
    {
        if (a.Length == 0) return;
        const int Block = 2400;                   // 50 ms at 48 kHz
        var rms = new List<double>();
        double peak = 0;
        for (int i = 0; i < a.Length; i += Block)
        {
            int n = Math.Min(Block, a.Length - i);
            double e = 0;
            for (int j = 0; j < n; j++) { double v = a[i + j]; e += v * v; peak = Math.Max(peak, Math.Abs(v)); }
            rms.Add(Math.Sqrt(e / n));
        }
        if (peak < 1e-4) return;
        rms.Sort();
        double loud = rms[(int)(rms.Count * 0.8)];  // 80th percentile: ignores silences and fades
        if (loud < 1e-5) return;
        double gain = Math.Min(0.1 / loud, 0.95 / peak);
        gain = Math.Clamp(gain, 0.05, 20);
        for (int i = 0; i < a.Length; i++) a[i] = (float)(a[i] * gain);
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
        Normalize(arr);
        long bytes = (long)arr.Length * sizeof(float);
        if (arr.Length <= sampleRate * 30 && Interlocked.Read(ref _cacheBytes) + bytes <= CacheBudgetBytes
            && Cache.TryAdd(key, arr))
            Interlocked.Add(ref _cacheBytes, bytes);
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

    // Streams mixed sound into the APO's ring. While nothing is playing it sleeps completely
    // (no 1 ms timer, no wakeups), so an idle CashVoice costs ~0% CPU and doesn't hurt battery life.
    void Loop()
    {
        var sw = Stopwatch.StartNew();
        var chunk = new float[1024];
        long written = 0, baseWritten = 0, lead = 0;
        double t0 = 0, lastActive = 0;
        uint sr = 0;
        bool fastTimer = false, resume = false;

        void SlowTimer() { if (fastTimer) { timeEndPeriod(1); fastTimer = false; } }

        while (_run)
        {
            if (!_shm.Connected) { sr = 0; SlowTimer(); _wake.WaitOne(1000); continue; }

            bool active; lock (_lock) active = _voices.Count > 0;
            double now = sw.Elapsed.TotalSeconds;
            if (active) lastActive = now;
            else if (now - lastActive > 1.0)
            {
                // ≥1 s of silence already written, so the APO only ever re-reads zeros while we sleep.
                SlowTimer();
                _wake.WaitOne();
                resume = true;
                continue;
            }
            if (!fastTimer) { timeBeginPeriod(1); fastTimer = true; }

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
            if (resume || target - written > sr)     // woke from idle, or stalled (sleep/hibernate): skip ahead cleanly
            {
                resume = false;
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
        SlowTimer();
    }

    public void Dispose()
    {
        _run = false;
        _wake.Set();
        try { _thread.Join(500); } catch { }
        StopAll();
        lock (_localLock) { _localOut?.Dispose(); _localOut = null; }
    }
}
