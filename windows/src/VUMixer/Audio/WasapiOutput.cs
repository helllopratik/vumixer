using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>
/// Event-driven WASAPI render (shared mode) via NAudio's public
/// <see cref="AudioRenderClient"/> wrapper: pulls stereo float32 from an
/// <see cref="ISampleProvider"/> at the device mix rate and writes the device
/// format. v1 supports stereo output devices only.
/// </summary>
public sealed class WasapiOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly int _latencyMs;
    private AudioClient? _ac;
    private AudioRenderClient? _rc;
    private AutoResetEvent? _evt;
    private Thread? _thread;
    private volatile bool _running;
    private ISampleProvider? _source;
    private float[]? _work;
    private WaveFormat _fmt = null!;

    public string DeviceId => _device.ID;
    public string DeviceName => _device.FriendlyName;
    public bool Alive => _running;
    public bool UnsupportedChannels { get; private set; }

    public event Action<string>? Failed;

    public WasapiOutput(MMDevice device, int latencyMs = 100)
    {
        _device = device;
        _latencyMs = latencyMs;
    }

    /// <summary>Prepare the device. Returns false (with a log) when unsupported.</summary>
    public bool Init(ISampleProvider source)
    {
        _source = source;
        _ac = _device.AudioClient;
        _fmt = _ac.MixFormat;
        if (_fmt.Channels != 2)
        {
            UnsupportedChannels = true;
            UiLog.Write("bus output " + _device.FriendlyName + ": " + _fmt.Channels +
                        " channels not supported in v1 (stereo only) — lane disabled");
            return false;
        }
        return true;
    }

    public void Start()
    {
        if (_running || _ac == null || _source == null) return;
        _ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                       _latencyMs * 10000, 0, _fmt, Guid.Empty);
        _rc = _ac.AudioRenderClient;
        int bufferFrames = _ac.BufferSize;
        _work = new float[bufferFrames * 2];
        _evt = new AutoResetEvent(false);
        _ac.SetEventHandle(_evt.SafeWaitHandle.DangerousGetHandle());

        // prime with one silent buffer so the engine starts cleanly
        IntPtr silent = _rc.GetBuffer(bufferFrames);
        Fmt.WriteStereoFloats(silent, new float[bufferFrames * 2], bufferFrames, _fmt);
        _rc.ReleaseBuffer(bufferFrames, AudioClientBufferFlags.None);

        _ac.Start();
        _running = true;
        _thread = new Thread(PlayLoop) { IsBackground = true, Name = "VU-out" };
        _thread.Start();
    }

    private void PlayLoop()
    {
        try
        {
            int bufferFrames = _work!.Length / 2;
            while (_running)
            {
                if (!_evt!.WaitOne(2000))
                {
                    if (_running) UiLog.Write("render timeout (" + _device.FriendlyName + ")");
                    break;
                }
                int pad = _ac!.CurrentPadding;
                int frames = bufferFrames - pad;
                if (frames <= 0) continue;
                int want = frames * 2;
                int got = _source!.Read(_work, 0, want);
                for (int i = got; i < want; i++) _work[i] = 0f;

                IntPtr p = _rc!.GetBuffer(frames);
                Fmt.WriteStereoFloats(p, _work, frames, _fmt);
                _rc.ReleaseBuffer(frames, AudioClientBufferFlags.None);
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                UiLog.Write("render stopped (" + _device.FriendlyName + "): " + ex.Message);
                try { Failed?.Invoke(ex.Message); } catch { }
            }
        }
        finally
        {
            _running = false;
        }
    }

    public void Stop()
    {
        _running = false;
        try { _ac?.Stop(); } catch { }
        _thread?.Join(1500);
    }

    public void Dispose()
    {
        Stop();
        _ac?.Dispose();
        _ac = null;
        _rc = null;
    }
}