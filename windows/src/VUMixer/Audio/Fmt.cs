using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VUMixer.Audio;

/// <summary>Conversions between WASAPI mix-format bytes and stereo float32.</summary>
public static class Fmt
{
    // standard KSDATAFORMAT_SUBTYPE media-subtype GUIDs (NAudio 2.x keeps
    // AudioSubtypes internal, so we define the well-known values ourselves)
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsIeeeFloat(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return f is WaveFormatExtensible ex && ex.SubFormat == SubtypeIeeeFloat;
    }

    public static bool IsPcm(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.Pcm) return true;
        return f is WaveFormatExtensible ex && ex.SubFormat == SubtypePcm;
    }

    /// <summary>Read <paramref name="frames"/> frames from a WASAPI capture buffer as stereo float32.</summary>
    public static float[] ReadStereoFloats(IntPtr src, int frames, WaveFormat fmt)
    {
        int ch = fmt.Channels;
        int bits = fmt.BitsPerSample;
        int n = frames * ch;
        float[] raw = new float[n];

        if (IsIeeeFloat(fmt) && bits == 32)
        {
            Marshal.Copy(src, raw, 0, n);
        }
        else if (IsPcm(fmt) && bits == 16)
        {
            var s = new short[n];
            Marshal.Copy(src, s, 0, n);
            for (int i = 0; i < n; i++) raw[i] = s[i] / 32768f;
        }
        else if (IsPcm(fmt) && bits == 24)
        {
            var b = new byte[n * 3];
            Marshal.Copy(src, b, 0, b.Length);
            for (int i = 0; i < n; i++)
            {
                int v = b[i * 3] | (b[i * 3 + 1] << 8) | (sbyte)b[i * 3 + 2] << 16;
                raw[i] = v / 8388608f;
            }
        }
        else if (IsPcm(fmt) && bits == 32)
        {
            var ii = new int[n];
            Marshal.Copy(src, ii, 0, n);
            for (int i = 0; i < n; i++) raw[i] = ii[i] / 2147483648f;
        }
        else
        {
            return new float[frames * 2]; // unsupported format → silence
        }

        if (ch == 2) return raw;
        var st = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            if (ch == 1) { st[i * 2] = raw[i]; st[i * 2 + 1] = raw[i]; }
            else { st[i * 2] = raw[i * ch]; st[i * 2 + 1] = raw[i * ch + 1]; }
        }
        return st;
    }

    /// <summary>Write stereo float32 into a WASAPI render buffer in the device mix format.</summary>
    public static void WriteStereoFloats(IntPtr dst, float[] stereo, int frames, WaveFormat fmt)
    {
        int ch = fmt.Channels;
        int bits = fmt.BitsPerSample;
        int n = frames * ch;

        if (IsIeeeFloat(fmt) && bits == 32)
        {
            Marshal.Copy(stereo, 0, dst, frames * 2);
        }
        else if (IsPcm(fmt) && bits == 16)
        {
            var s = new short[n];
            for (int i = 0; i < frames; i++)
            {
                float l = Clamp(stereo[i * 2]);
                s[i * ch] = (short)(l * 32767f);
                if (ch > 1) s[i * ch + 1] = (short)(Clamp(stereo[i * 2 + 1]) * 32767f);
            }
            Marshal.Copy(s, 0, dst, n);
        }
        else if (IsPcm(fmt) && bits == 24)
        {
            var b = new byte[n * 3];
            for (int i = 0; i < frames; i++)
            {
                Write24(b, i * ch, Clamp(stereo[i * 2]));
                if (ch > 1) Write24(b, i * ch + 1, Clamp(stereo[i * 2 + 1]));
            }
            Marshal.Copy(b, 0, dst, b.Length);
        }
        else if (IsPcm(fmt) && bits == 32)
        {
            var ii = new int[n];
            for (int i = 0; i < frames; i++)
            {
                ii[i * ch] = (int)(Clamp(stereo[i * 2]) * 2147483647f);
                if (ch > 1) ii[i * ch + 1] = (int)(Clamp(stereo[i * 2 + 1]) * 2147483647f);
            }
            Marshal.Copy(ii, 0, dst, n);
        }
        // other bit depths: unsupported → nothing written (caller logs once)
    }

    private static float Clamp(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);

    private static void Write24(byte[] b, int idx, float v)
    {
        int val = (int)(Clamp(v) * 8388607f);
        b[idx * 3] = (byte)(val & 0xFF);
        b[idx * 3 + 1] = (byte)((val >> 8) & 0xFF);
        b[idx * 3 + 2] = (byte)((val >> 16) & 0xFF);
    }
}