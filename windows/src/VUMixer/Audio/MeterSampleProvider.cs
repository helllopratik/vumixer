using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>Transparent passthrough that meters whatever flows through it.</summary>
public sealed class MeterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _inner;

    public WaveFormat WaveFormat => _inner.WaveFormat;
    public readonly StageMeter Meter = new();

    public MeterSampleProvider(ISampleProvider inner) => _inner = inner;

    public int Read(float[] buffer, int offset, int count)
    {
        int got = _inner.Read(buffer, offset, count);
        Meter.Update(buffer, offset, got, WaveFormat.Channels);
        return got;
    }
}