using System;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VUMixer.Audio;

/// <summary>
/// The audio engine: mic + desktop-loopback captures, NS gate, gain/mute/sends,
/// two pull-driven mix buses, and WASAPI render to the A/B output devices.
/// Self-heals via device notifications and a periodic health sweep.
/// </summary>
public sealed class AudioGraph : IDisposable
{
    private const int MixRate = 48000;

    private readonly AppConfig _cfg;
    private readonly object _gate = new();
    private int _reopenDepth;

    private Timer? _health;
    private WasapiInput? _micIn, _sysIn;
    private StoreAndForwardSampleProvider? _micBridge, _sysBridge;
    private WdlResamplingSampleProvider? _micRes, _sysRes;
    private NoiseGateProvider? _ns;
    private MeterSampleProvider? _meterMic, _meterSys;
    private MixBusSampleProvider _mixA = null!, _mixB = null!;
    private WasapiOutput? _outA, _outB;

    public DeviceWatcher Watcher { get; } = new();

    public StageMeter MeterMic => _meterMic?.Meter ?? _staticMeter;
    public StageMeter MeterSys => _meterSys?.Meter ?? _staticMeter;
    public StageMeter MeterA => _mixA?.Meter ?? _staticMeter;
    public StageMeter MeterB => _mixB?.Meter ?? _staticMeter;
    private readonly StageMeter _staticMeter = new();

    public AudioGraph(AppConfig cfg) => _cfg = cfg;

    public void Start()
    {
        Watcher.Start();
        Watcher.DevicesChanged += OnDevicesChanged;
        Watcher.DefaultOutputChanged += OnDefaultOutputChanged;
        _mixA = new MixBusSampleProvider();
        _mixB = new MixBusSampleProvider();
        try
        {
            SafeReopen(ReopenAll);
        }
        catch (Exception ex)
        {
            UiLog.Write("startup: " + ex.Message);
        }
        _health = new Timer(_ => HealthSweep(), null, 5000, 5000);
    }

    // ------------------------------------------------------------- rebuild
    private void ReopenAll()
    {
        OpenMic();
        OpenSys();
        OpenOutputs();
        RebuildMixers();
        WarnFeedback();
    }

    /// <summary>
    /// If the desktop loopback target is also a bus output, our own rendered mix
    /// lands inside the loopback (feedback / doubling risk). Log a clear warning
    /// so the user can pick a different default output or bus device.
    /// </summary>
    private void WarnFeedback()
    {
        if (_sysIn == null) return;
        string loop = _sysIn.DeviceId;
        bool aHits = _outA != null && _outA.DeviceId == loop;
        bool bHits = _outB != null && _outB.DeviceId == loop;
        if (aHits || bHits)
        {
            var which = (aHits ? "A" : "") + (bHits ? "B" : "");
            UiLog.Write("warning: desktop loopback captures bus " + which +
                        "'s own output (" + _sysIn.DeviceName + ") — feedback risk. " +
                        "Pick a different default output or bus device.");
        }
    }

    private void OpenMic()
    {
        TeardownMic();
        var all = Watcher.CaptureDevices();
        var mic = all.FirstOrDefault(d => d.ID == _cfg.MicDevice) ?? all.FirstOrDefault();
        if (mic == null)
        {
            UiLog.Write("mic: no input device available");
            return;
        }
        try
        {
            _micIn = new WasapiInput(mic, false);
            _micBridge = new StoreAndForwardSampleProvider(_micIn.SampleRate);
            _micIn.Samples += _micBridge.Push;
            _micRes = new WdlResamplingSampleProvider(_micBridge, MixRate);
            _ns = new NoiseGateProvider(_micRes)
            {
                Enabled = _cfg.NsEnabled,
                ThresholdDb = (float)_cfg.NsThresholdDb,
            };
            _meterMic = new MeterSampleProvider(_ns);
            _micIn.Start();
            UiLog.Write("mic: " + mic.FriendlyName + " (" + _micIn.SampleRate + " Hz)");
        }
        catch (Exception ex)
        {
            UiLog.Write("mic: failed to open " + mic.FriendlyName + ": " + ex.Message);
            TeardownMic();
        }
    }

    private void OpenSys()
    {
        TeardownSys();
        var def = Watcher.DefaultRender();
        if (def == null)
        {
            UiLog.Write("system: no default render device");
            return;
        }
        try
        {
            _sysIn = new WasapiInput(def, loopback: true);
            _sysBridge = new StoreAndForwardSampleProvider(_sysIn.SampleRate);
            _sysIn.Samples += _sysBridge.Push;
            _sysRes = new WdlResamplingSampleProvider(_sysBridge, MixRate);
            _meterSys = new MeterSampleProvider(_sysRes);
            _sysIn.Start();
            UiLog.Write("system: loopback of " + def.FriendlyName);
        }
        catch (Exception ex)
        {
            UiLog.Write("system: loopback failed: " + ex.Message);
            TeardownSys();
        }
    }

    private void OpenOutputs()
    {
        TeardownOutputs();
        _outA = OpenOutput("A", _cfg.DeviceA, _mixA);
        _outB = OpenOutput("B", _cfg.DeviceB, _mixB);
    }

    private WasapiOutput? OpenOutput(string label, string? savedId, ISampleProvider mix)
    {
        var devs = Watcher.RenderDevices();
        var dev = devs.FirstOrDefault(d => d.ID == savedId) ?? devs.FirstOrDefault();
        if (dev == null)
        {
            UiLog.Write("bus " + label + ": no render device available");
            return null;
        }
        try
        {
            var rate = dev.AudioClient.MixFormat.SampleRate;
            var resampler = new WdlResamplingSampleProvider(mix, rate);
            var outDev = new WasapiOutput(dev);
            if (!outDev.Init(resampler)) return null;
            outDev.Start();
            UiLog.Write("bus " + label + ": " + dev.FriendlyName + " (" + rate + " Hz)");
            return outDev;
        }
        catch (Exception ex)
        {
            UiLog.Write("bus " + label + ": failed: " + ex.Message);
            return null;
        }
    }

    private void RebuildMixers()
    {
        _mixA.Sources.Clear();
        _mixB.Sources.Clear();

        if (_sysRes != null)
        {
            _mixA.Sources.Add(new MixBusSampleProvider.Source
            {
                Provider = _sysRes,
                Name = "system",
                Gain = (float)_cfg.SysGain,
                Muted = _cfg.SysMute,
                Enabled = true,
            });
            _mixB.Sources.Add(new MixBusSampleProvider.Source
            {
                Provider = _sysRes,
                Name = "system",
                Gain = (float)_cfg.SysGain,
                Muted = _cfg.SysMute,
                Enabled = true,
            });
        }

        if (_meterMic != null)
        {
            _mixA.Sources.Add(new MixBusSampleProvider.Source
            {
                Provider = _meterMic,
                Name = "mic",
                Gain = (float)_cfg.MicGain,
                Muted = _cfg.MicMute,
                Enabled = _cfg.MicToA,
            });
            _mixB.Sources.Add(new MixBusSampleProvider.Source
            {
                Provider = _meterMic,
                Name = "mic",
                Gain = (float)_cfg.MicGain,
                Muted = _cfg.MicMute,
                Enabled = _cfg.MicToB,
            });
        }

        ApplyBusMaster(_mixA, _cfg.GainA, _cfg.MuteA);
        ApplyBusMaster(_mixB, _cfg.GainB, _cfg.MuteB);
    }

    private static void ApplyBusMaster(MixBusSampleProvider mix, double gain, bool mute)
        => mix.MasterGain = mute ? 0f : (float)gain;

    // ------------------------------------------------------- user controls
    public void SetMicDevice(string id)
    {
        if (_cfg.MicDevice == id && _micIn != null) return;
        _cfg.MicDevice = id;
        _cfg.Save();
        SafeReopen(() => { OpenMic(); RebuildMixers(); });
        UiLog.Write("mic: switched to " + (Watcher.CaptureDevices().FirstOrDefault(d => d.ID == id)?.FriendlyName ?? id));
    }

    public void SetBusDevice(string bus, string id)
    {
        if (bus == "A")
        {
            if (_cfg.DeviceA == id && _outA != null) return;
            _cfg.DeviceA = id;
        }
        else
        {
            if (_cfg.DeviceB == id && _outB != null) return;
            _cfg.DeviceB = id;
        }
        _cfg.Save();
        SafeReopen(() => { OpenOutputs(); });
        UiLog.Write("bus " + bus + ": switched to " + (Watcher.RenderDevices().FirstOrDefault(d => d.ID == id)?.FriendlyName ?? id));
    }

    public void SetMicGain(double gain)
    {
        _cfg.MicGain = gain;
        foreach (var m in new[] { _mixA, _mixB })
            foreach (var s in m.Sources.Where(s => s.Name == "mic")) s.Gain = (float)gain;
        _cfg.Save();
    }

    public void SetSysGain(double gain)
    {
        _cfg.SysGain = gain;
        foreach (var m in new[] { _mixA, _mixB })
            foreach (var s in m.Sources.Where(s => s.Name == "system")) s.Gain = (float)gain;
        _cfg.Save();
    }

    public void SetBusGain(string bus, double gain)
    {
        if (bus == "A") { _cfg.GainA = gain; _mixA.MasterGain = _cfg.MuteA ? 0f : (float)gain; }
        else { _cfg.GainB = gain; _mixB.MasterGain = _cfg.MuteB ? 0f : (float)gain; }
        _cfg.Save();
    }

    public void SetMicMute(bool muted)
    {
        _cfg.MicMute = muted;
        foreach (var m in new[] { _mixA, _mixB })
            foreach (var s in m.Sources.Where(s => s.Name == "mic")) s.Muted = muted;
        _cfg.Save();
    }

    public void SetSysMute(bool muted)
    {
        _cfg.SysMute = muted;
        foreach (var m in new[] { _mixA, _mixB })
            foreach (var s in m.Sources.Where(s => s.Name == "system")) s.Muted = muted;
        _cfg.Save();
    }

    public void SetBusMute(string bus, bool muted)
    {
        if (bus == "A") { _cfg.MuteA = muted; _mixA.MasterGain = muted ? 0f : (float)_cfg.GainA; }
        else { _cfg.MuteB = muted; _mixB.MasterGain = muted ? 0f : (float)_cfg.GainB; }
        _cfg.Save();
    }

    public void SetMicSend(string bus, bool enabled)
    {
        if (bus == "A")
        {
            _cfg.MicToA = enabled;
            foreach (var s in _mixA.Sources.Where(s => s.Name == "mic")) s.Enabled = enabled;
        }
        else
        {
            _cfg.MicToB = enabled;
            foreach (var s in _mixB.Sources.Where(s => s.Name == "mic")) s.Enabled = enabled;
        }
        _cfg.Save();
        UiLog.Write(bus + " send mic " + (enabled ? "ON" : "OFF"));
    }

    public void SetNs(bool enabled)
    {
        _cfg.NsEnabled = enabled;
        if (_ns != null) _ns.Enabled = enabled;
        _cfg.Save();
        UiLog.Write("noise reduction " + (enabled ? "ON" : "OFF"));
    }

    public void SetNsThreshold(double db)
    {
        _cfg.NsThresholdDb = db;
        if (_ns != null) _ns.ThresholdDb = (float)db;
        _cfg.Save();
    }

    // ------------------------------------------------------------- hotplug
    private void OnDevicesChanged()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            UiLog.Write("device list changed — rescanning");
            SafeReopen(ReopenAll);
        });
    }

    private void OnDefaultOutputChanged(string? id)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            UiLog.Write("default output changed — reopening desktop loopback");
            SafeReopen(() => { OpenSys(); RebuildMixers(); });
        });
    }

    public void ForceRescan()
    {
        UiLog.Write("manual refresh");
        SafeReopen(ReopenAll);
    }

    private void SafeReopen(Action action)
    {
        if (Interlocked.Increment(ref _reopenDepth) > 1)
        {
            Interlocked.Decrement(ref _reopenDepth);
            return;
        }
        try
        {
            lock (_gate) action();
        }
        catch (Exception ex)
        {
            UiLog.Write("repair: " + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _reopenDepth);
        }
    }

    private void HealthSweep()
    {
        if (Watcher == null) return;
        bool anyBad = false;
        var checks = new (WasapiInput? input, string label)[]
        {
            (_micIn, "mic"),
            (_sysIn, "system loopback"),
        };
        foreach (var (input, label) in checks)
        {
            if (input != null && input.DeviceId != null && !input.Alive)
            {
                UiLog.Write("health: " + label + " lane died — reopening");
                anyBad = true;
            }
        }
        foreach (var (outDev, label) in new[] { (_outA, "bus A"), (_outB, "bus B") })
        {
            if (outDev != null && outDev.DeviceId != null && !outDev.Alive)
            {
                UiLog.Write("health: " + label + " lane died — reopening");
                anyBad = true;
            }
        }
        if (anyBad) SafeReopen(ReopenAll);
    }

    // ------------------------------------------------------------ teardown
    private void TeardownMic()
    {
        _micIn?.Dispose(); _micIn = null;
        _micBridge = null; _micRes = null; _ns = null; _meterMic = null;
    }

    private void TeardownSys()
    {
        _sysIn?.Dispose(); _sysIn = null;
        _sysBridge = null; _sysRes = null; _meterSys = null;
    }

    private void TeardownOutputs()
    {
        _outA?.Dispose(); _outA = null;
        _outB?.Dispose(); _outB = null;
    }

    public void Dispose()
    {
        _health?.Dispose();
        _health = null;
        Watcher.DevicesChanged -= OnDevicesChanged;
        Watcher.DefaultOutputChanged -= OnDefaultOutputChanged;
        Watcher.Dispose();
        TeardownMic();
        TeardownSys();
        TeardownOutputs();
        UiLog.Write("engine stopped · graph cleaned up");
    }
}