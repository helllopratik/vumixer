using System;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>Simple RMS noise gate — v1 stand-in for the WebRTC NS used on Linux.</summary>
public sealed class NoiseGateProvider : ISampleProvider
{
    private const int BlockFrames = 960; // 20 ms @ 48 kHz

    private readonly ISampleProvider _inner;

    public WaveFormat WaveFormat => _inner.WaveFormat;
    public volatile bool Enabled = true;
    public volatile float ThresholdDb = -50f; // dBFS

    private float _env; // smoothed gate 0..1
    private double _acc; // squared-sum accumulator
    private int _left;   // samples until next block evaluation

    public NoiseGateProvider(ISampleProvider inner) => _inner = inner;

    public int Read(float[] buffer, int offset, int count)
    {
        int got = _inner.Read(buffer, offset, count);
        if (!Enabled || got <= 0) return got;

        float thr = (float)Math.Pow(10.0, ThresholdDb / 20.0); // linear amplitude
        if (_left <= 0) _left = BlockFrames * 2;

        int frames = got / 2;
        for (int i = 0; i < frames; i++)
        {
            float l = buffer[offset + i * 2];
            float r = buffer[offset + i * 2 + 1];
            _acc += (double)l * l + (double)r * r;
            _left -= 2;
            if (_left <= 0)
            {
                _left = BlockFrames * 2;
                float rms = (float)Math.Sqrt(_acc / BlockFrames); // per-channel-lane
                _acc = 0;
                float target = rms < thr ? 0f : 1f;
                // fast attack, slow release → no pumping on speech gaps
                _env = target > _env ? target : Math.Max(0f, _env - 0.003f);
            }
        }

        float g = _env;
        if (g != 1f)
            for (int i = 0; i < got; i++) buffer[offset + i] *= g;

        return got;
    }
}