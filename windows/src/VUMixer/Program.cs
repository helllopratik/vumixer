using System;
using System.Threading;
using Avalonia;

namespace VUMixer;

internal static class Program
{
    // per-user single instance (Local\ prefix = no network share conflicts)
    private const string MutexName = @"Local\VUMixer-SingleInstance-v0.1.0";
    private static Mutex? _mutex;

    [STAThread]
    public static int Main(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            UiLog.Write("Another VU Mixer instance is already running.");
            return 0;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { _mutex.ReleaseMutex(); } catch { /* abandoned/closed */ }
            _mutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}