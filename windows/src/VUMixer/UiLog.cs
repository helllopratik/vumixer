using System;

namespace VUMixer;

/// <summary>Thread-safe timestamped event log; the UI subscribes to <see cref="Line"/>.</summary>
public static class UiLog
{
    public static event Action<string>? Line;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        string line;
        lock (Gate)
        {
            line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;
        }
        Line?.Invoke(line);
    }
}