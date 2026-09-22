using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>
/// Polling WASAPI input (shared mode, device mix format) via NAudio's public
/// <see cref="AudioCaptureClient"/> wrapper. Captures either a microphone
/// endpoint or the loopback of a render endpoint ("what you hear"). Delivers
/// stereo float32 blocks; mono is duplicated, &gt;2ch downmixed to stereo.
/// </summary>
public sealed class WasapiInput : IDisposable
{
    private readonly MMDevice _device;
    private readonly bool _loopback;
    private WaveFormat _fmt = null!;
    private AudioClient? _ac;
    private AudioCaptureClient? _cap;
    private Thread? _thread;
    private volatile bool _running;

    public int SampleRate { get; private set; }
    public string DeviceId => _device.ID;
    public string DeviceName => _device.FriendlyName;
    public bool Alive => _running;

    /// <summary>Raised on the capture thread with stereo float32 blocks.</summary>
    public event Action<float[], int>? Samples;

    public WasapiInput(MMDevice device, bool loopback)
    {
        _device = device;
        _loopback = loopback;
    }

    public void Start()
    {
        if (_running) return;
        _ac = _device.AudioClient;
        _fmt = _ac.MixFormat;
        SampleRate = _fmt.SampleRate;
        var flags = _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
        _ac.Initialize(AudioClientShareMode.Shared, flags, 200 * 10000, 0, _fmt, Guid.Empty);
        _cap = _ac.AudioCaptureClient;
        _ac.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "VU-in" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            while (_running)
            {
                while (_running && _cap!.GetNextPacketSize() > 0)
                {
                    IntPtr ptr = _cap.GetBuffer(out int frames, out AudioClientBufferFlags flags, out long devPos, out long qpc);
                    try
                    {
                        if (frames > 0)
                        {
                            var stereo = Fmt.ReadStereoFloats(ptr, frames, _fmt);
                            if (flags.HasFlag(AudioClientBufferFlags.Silent))
                                Array.Clear(stereo, 0, stereo.Length);
                            Samples?.Invoke(stereo, frames);
                        }
                    }
                    finally
                    {
                        _cap.ReleaseBuffer(frames);
                    }
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            if (_running) UiLog.Write("capture stopped (" + _device.FriendlyName + "): " + ex.Message);
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
        _thread?.Join(1000);
    }

    public void Dispose()
    {
        Stop();
        _ac?.Dispose();
        _ac = null;
        _cap = null;
    }
}