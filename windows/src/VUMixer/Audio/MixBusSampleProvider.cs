using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>
/// Pull-driven stereo float32 mixer at 48 kHz. Desktop is always present; the
/// mic joins only when its send (A or B) is enabled. Per-source gain/mute,
/// per-bus master gain/mute, and a built-in lane meter.
/// </summary>
public sealed class MixBusSampleProvider : ISampleProvider
{
    public sealed class Source
    {
        public required ISampleProvider Provider { get; init; }
        public required string Name { get; init; }
        public volatile float Gain = 1f;
        public volatile bool Muted;
        public volatile bool Enabled = true;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public readonly List<Source> Sources = new();
    public readonly StageMeter Meter = new();
    public volatile float MasterGain = 1f;

    private float[]? _tmp;

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (_tmp == null || _tmp.Length < count) _tmp = new float[count];

        foreach (var s in Sources)
        {
            if (!s.Enabled) continue;
            int got = s.Provider.Read(_tmp, 0, count);
            float g = s.Muted ? 0f : s.Gain;
            if (g != 1f)
            {
                for (int i = 0; i < got; i++) buffer[offset + i] += _tmp[i] * g;
            }
            else
            {
                for (int i = 0; i < got; i++) buffer[offset + i] += _tmp[i];
            }
        }

        float m = MasterGain;
        if (m != 1f)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] *= m;
        }

        Meter.Update(buffer, offset, count, 2);
        return count;
    }
}