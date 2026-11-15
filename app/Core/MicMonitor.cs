using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CashVoice.Core;

/// <summary>
/// "Hear yourself": records the default mic and plays it straight to the default output with
/// small WASAPI buffers (about 20-30 ms in total, plus ~37 ms while pitch/formant shifting is on).
/// The APO recognises this stream (see SharedMem.ClaimMonitor), leaves the soundboard out of it,
/// and applies the voice effects only when <see cref="Wet"/> is on.
/// Nothing runs while it's switched off.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    readonly SharedMem _shm;
    WasapiCapture? _cap;
    WasapiOut? _out;
    BufferedWaveProvider? _buf;
    VolumeSampleProvider? _vol;
    float _volume = 1f;
    bool _wet = true;

    public MicMonitor(SharedMem shm) { _shm = shm; }

    public bool Running => _out != null;
    public string? Error { get; private set; }

    public float Volume
    {
        get => _volume;
        set { _volume = value; if (_vol != null) _vol.Volume = value; }
    }

    public bool Wet
    {
        get => _wet;
        set { _wet = value; _shm.MonitorWet = value; }
    }

    public void Start()
    {
        if (Running) return;
        Error = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            var mic = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var spk = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _shm.ClaimMonitor(_wet);              // the next APO instance to start is ours
            _cap = new WasapiCapture(mic, true, 10);
            _buf = new BufferedWaveProvider(_cap.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(250),
                ReadFully = true,
            };
            int maxBytes = _cap.WaveFormat.AverageBytesPerSecond * 30 / 1000;   // keep ≤ 30 ms queued
            _cap.DataAvailable += (_, e) =>
            {
                var b = _buf;
                if (b == null) return;
                if (b.BufferedBytes > maxBytes) b.ClearBuffer();                // drift: drop, don't lag
                b.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            _vol = new VolumeSampleProvider(_buf.ToSampleProvider()) { Volume = _volume };
            _out = new WasapiOut(spk, AudioClientShareMode.Shared, true, 10);
            _out.Init(_vol.ToWaveProvider());
            _cap.StartRecording();
            _out.Play();
            // If no APO instance took the claim (CashVoice isn't on this mic), don't leave it for
            // the next app that opens the mic to pick up by mistake.
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => _shm.CancelMonitorClaim());
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Stop();
        }
    }

    public void Stop()
    {
        try { _cap?.StopRecording(); } catch { }
        try { _out?.Stop(); } catch { }
        _cap?.Dispose(); _cap = null;
        _out?.Dispose(); _out = null;
        _buf = null; _vol = null;
        _shm.CancelMonitorClaim();
    }

    public void Dispose() => Stop();
}
