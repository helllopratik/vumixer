using System;

namespace VUMixer.Audio;

/// <summary>Peak/RMS snapshot for one stereo lane (written by audio threads, read by the UI).</summary>
public sealed class StageMeter
{
    private readonly object _gate = new();
    private float _peakL, _peakR, _rmsL, _rmsR;
    private bool _clip;
    private long _clipUntil; // Environment.TickCount64 deadline

    public void Update(float[] buffer, int offset, int count, int channels)
    {
        int frames = count / Math.Max(1, channels);
        if (frames <= 0) return;
        double sumL = 0, sumR = 0;
        float pl = 0, pr = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = buffer[offset + i * channels];
            float r = channels > 1 ? buffer[offset + i * channels + 1] : l;
            float al = Math.Abs(l), ar = Math.Abs(r);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
            sumL += (double)l * l;
            sumR += (double)r * r;
        }
        lock (_gate)
        {
            // slow-decaying peak hold
            _peakL = Math.Max(pl, _peakL * 0.995f);
            _peakR = Math.Max(pr, _peakR * 0.995f);
            _rmsL = (float)Math.Sqrt(sumL / frames);
            _rmsR = (float)Math.Sqrt(sumR / frames);
            if (pl > 0.988f || pr > 0.988f) _clipUntil = Environment.TickCount64 + 700;
            _clip = Environment.TickCount64 < _clipUntil;
        }
    }

    public void Snapshot(out float peakL, out float peakR, out float rmsL, out float rmsR, out bool clip)
    {
        lock (_gate)
        {
            peakL = _peakL; peakR = _peakR; rmsL = _rmsL; rmsR = _rmsR; clip = _clip;
        }
    }
}