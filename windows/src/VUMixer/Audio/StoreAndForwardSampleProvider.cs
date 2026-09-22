using System;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>
/// Push-to-pull bridge: the capture thread pushes the newest stereo block via
/// <see cref="Push"/>, render threads pull a continuous stream (repeats the
/// last block; silence until the first block arrives).
/// </summary>
public sealed class StoreAndForwardSampleProvider : ISampleProvider
{
    private readonly object _gate = new();
    private float[] _latest = Array.Empty<float>();
    private int _pos;

    public WaveFormat WaveFormat { get; }

    public StoreAndForwardSampleProvider(int sampleRate, int channels = 2)
        => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public void Push(float[] stereo, int frames)
    {
        int len = frames * 2;
        if (len <= 0) return;
        var copy = new float[len];
        Array.Copy(stereo, copy, len);
        lock (_gate)
        {
            _latest = copy;
            _pos = 0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_latest.Length == 0)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            int written = 0;
            while (written < count)
            {
                int take = Math.Min(_latest.Length - _pos, count - written);
                Array.Copy(_latest, _pos, buffer, offset + written, take);
                written += take;
                _pos += take;
                if (_pos >= _latest.Length) _pos = 0; // loop latest block
            }
            return count;
        }
    }
}