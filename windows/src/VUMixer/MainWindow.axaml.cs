using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VUMixer.Audio;

namespace VUMixer;

/// <summary>A render/input endpoint shown in a device combo box.</summary>
public sealed class DeviceItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private static readonly Color Accent = Color.Parse("#2E7CF6");
    private static readonly Color MuteRed = Color.Parse("#D64545");
    private static readonly Color ActiveOk = Color.Parse("#2DBF7A");
    private static readonly Color PanelBg = Color.Parse("#151A25");
    private static readonly Color BtnOff = Color.Parse("#232A38");

    private readonly AppConfig _cfg;
    private readonly AudioGraph _graph;
    private readonly DispatcherTimer _timer;
    private readonly List<string> _logLines = new();
    private bool _loading;

    // controls (created in BuildUi)
    private ComboBox _micBox = null!, _boxA = null!, _boxB = null!;
    private Slider _sMic = null!, _sSys = null!, _sA = null!, _sB = null!;
    private TextBlock _tMic = null!, _tSys = null!, _tA = null!, _tB = null!;
    private Button _bMicMute = null!, _bSysMute = null!, _bAMute = null!, _bBMute = null!;
    private Button _bSendA = null!, _bSendB = null!, _bNs = null!;
    private ProgressBar _vuMicL = null!, _vuMicR = null!;
    private ProgressBar _vuSysL = null!, _vuSysR = null!;
    private ProgressBar _vuAL = null!, _vuAR = null!;
    private ProgressBar _vuBL = null!, _vuBR = null!;
    private TextBlock _clipMic = null!, _clipSys = null!, _clipA = null!, _clipB = null!;
    private TextBlock _status = null!;
    private TextBox _logBox = null!;

    public MainWindow()
    {
        _cfg = AppConfig.Load();
        _graph = new AudioGraph(_cfg);
        InitializeComponent();
        Title = "VU Mixer  ·  build " + AppConfig.BuildCode;
        BuildUi();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => UpdateMeters();
        _timer.Start();
        UiLog.Line += OnLogLine;
        Closing += (_, _) => OnExit();
        _graph.Start();
        RefreshDeviceLists();
        ApplyUiState();
        _status.Text = "ready";
        UiLog.Write("VU Mixer " + AppConfig.BuildCode + " started");
    }

    // ---------------------------------------------------------------- layout
    private void BuildUi()
    {
        var root = this.FindControl<DockPanel>("Root")!;
        root.Children.Add(BuildHeader());
        root.Children.Add(BuildLogPanel());

        var lanes = new StackPanel { Orientation = Orientation.Vertical, Spacing = 12 };
        lanes.Children.Add(BuildMicLane());
        lanes.Children.Add(BuildSysLane());
        lanes.Children.Add(BuildBusLane("BUS A", "A"));
        lanes.Children.Add(BuildBusLane("BUS B", "B"));

        var scroll = new ScrollViewer { Content = lanes };
        root.Children.Add(scroll);
    }

    private Control BuildHeader()
    {
        var dock = new DockPanel();
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        var title = new TextBlock { Text = "VU MIXER", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = "build " + AppConfig.BuildCode,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A94A6")),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(ActiveOk), VerticalAlignment = VerticalAlignment.Center });
        _status = new TextBlock { Text = "starting…", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B9C2D2")), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_status);

        DockPanel.SetDock(stack, Dock.Left);
        dock.Children.Add(stack);

        var refresh = MakeButton("⟳ Refresh", () =>
        {
            UiLog.Write("manual refresh");
            _graph.ForceRescan();
            RefreshDeviceLists();
        });
        DockPanel.SetDock(refresh, Dock.Right);
        dock.Children.Add(refresh);

        return new Border
        {
            Padding = new Thickness(2, 0, 2, 10),
            Child = dock,
        };
    }

    private Control BuildLogPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(panel, Dock.Bottom);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        btnRow.Children.Add(new TextBlock { Text = "EVENTS", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8A94A6")), VerticalAlignment = VerticalAlignment.Center });
        btnRow.Children.Add(MakeButton("Copy", () =>
        {
            try
            {
                var text = string.Join("\n", _logLines);
                if (text.Length > 0) _ = this.Clipboard?.SetTextAsync(text);
            }
            catch { /* clipboard unavailable */ }
        }));
        DockPanel.SetDock(btnRow, Dock.Top);

        _logBox = new TextBox
        {
            IsReadOnly = true,
            Height = 130,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Background = new SolidColorBrush(Color.Parse("#0B0E14")),
            Foreground = new SolidColorBrush(Color.Parse("#9FD0A8")),
        };
        panel.Children.Add(_logBox);
        return panel;
    }

    private Control BuildMicLane()
    {
        _vuMicL = NewVu(); _vuMicR = NewVu(); _clipMic = NewClip();
        _sMic = NewGainSlider(); _tMic = NewGainText();
        _sMic.Value = ToSlider(_cfg.MicGain);
        _sMic.ValueChanged += (_, e) => OnMicGain(e.NewValue);

        _bMicMute = MakeMuteButton(() => ToggleMicMute());
        _bSendA = MakeToggleButton("A", OnSendA);
        _bSendB = MakeToggleButton("B", OnSendB);
        _bNs = MakeToggleButton("NS", OnNs);

        _micBox = NewDeviceBox();
        _micBox.SelectionChanged += (_, e) =>
        {
            if (!_loading && _micBox.SelectedItem is DeviceItem di) MicPicked(di.Id);
        };

        return LaneCard("MIC", _micBox, _vuMicL, _vuMicR, _clipMic, _sMic, _tMic, _bMicMute,
            new[] { _bSendA, _bSendB, _bNs });
    }

    private Control BuildSysLane()
    {
        _vuSysL = NewVu(); _vuSysR = NewVu(); _clipSys = NewClip();
        _sSys = NewGainSlider(); _tSys = NewGainText();
        _sSys.Value = ToSlider(_cfg.SysGain);
        _sSys.ValueChanged += (_, e) => OnSysGain(e.NewValue);
        _bSysMute = MakeMuteButton(() => ToggleSysMute());
        return LaneCard("SYSTEM · desktop (what you hear)", null, _vuSysL, _vuSysR, _clipSys, _sSys, _tSys, _bSysMute, null);
    }

    private Control BuildBusLane(string label, string bus)
    {
        var vuL = NewVu(); var vuR = NewVu(); var clip = NewClip();
        var slider = NewGainSlider(); var text = NewGainText();
        var mute = MakeMuteButton(() => ToggleBusMute(bus));
        var box = NewDeviceBox();

        if (bus == "A")
        {
            _boxA = box; _sA = slider; _tA = text; _bAMute = mute;
            _vuAL = vuL; _vuAR = vuR; _clipA = clip;
            slider.Value = ToSlider(_cfg.GainA);
            slider.ValueChanged += (_, e) => OnBusGain("A", e.NewValue);
        }
        else
        {
            _boxB = box; _sB = slider; _tB = text; _bBMute = mute;
            _vuBL = vuL; _vuBR = vuR; _clipB = clip;
            slider.Value = ToSlider(_cfg.GainB);
            slider.ValueChanged += (_, e) => OnBusGain("B", e.NewValue);
        }

        box.SelectionChanged += (_, e) =>
        {
            if (!_loading && box.SelectedItem is DeviceItem di) OnBusPicked(bus, di.Id);
        };

        return LaneCard(label, box, vuL, vuR, clip, slider, text, mute, null);
    }

    // -------------------------------------------------------- lane compositor
    private static Control LaneCard(string label, ComboBox? device, ProgressBar vuL, ProgressBar vuR, TextBlock clip,
        Slider gain, TextBlock gainText, Button mute, IReadOnlyList<Button>? extra)
    {
        var inner = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new TextBlock { Text = label, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        if (device != null)
        {
            device.Width = 320;
            titleRow.Children.Add(device);
        }
        titleRow.Children.Add(new TextBlock { Text = "L", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8A94A6")), VerticalAlignment = VerticalAlignment.Bottom });
        titleRow.Children.Add(vuL);
        titleRow.Children.Add(new TextBlock { Text = "R", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8A94A6")), VerticalAlignment = VerticalAlignment.Bottom });
        titleRow.Children.Add(vuR);
        titleRow.Children.Add(clip);
        inner.Children.Add(titleRow);

        var ctlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        ctlRow.Children.Add(new TextBlock { Text = "Gain", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B9C2D2")), VerticalAlignment = VerticalAlignment.Center });
        gain.Width = 180;
        ctlRow.Children.Add(gain);
        ctlRow.Children.Add(gainText);
        ctlRow.Children.Add(new TextBlock { Text = "|", FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#3A4356")), VerticalAlignment = VerticalAlignment.Center });
        ctlRow.Children.Add(mute);
        if (extra != null)
            foreach (var b in extra) ctlRow.Children.Add(b);
        inner.Children.Add(ctlRow);

        return new Border
        {
            Background = new SolidColorBrush(PanelBg),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = inner,
        };
    }

    // ---------------------------------------------------------------- builders
    private static Slider NewGainSlider() => new()
    {
        Minimum = 0,
        Maximum = 150,
        TickFrequency = 5,
        IsSnapToTickEnabled = true,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock NewGainText() => new()
    {
        Text = "100%",
        Width = 52,
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.Parse("#E7ECF5")),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static ProgressBar NewVu() => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 0,
        Width = 150,
        Height = 13,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock NewClip() => new()
    {
        Text = "CLIP",
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static ComboBox NewDeviceBox() => new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button MakeMuteButton(Action onClick)
    {
        var b = new Button { Content = "MUTE", Padding = new Thickness(10, 4), MinWidth = 76 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button MakeToggleButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4), MinWidth = 44 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void SetToggle(Button b, bool active)
    {
        b.Background = active ? new SolidColorBrush(Accent) : new SolidColorBrush(BtnOff);
        b.Foreground = Brushes.White;
    }

    private static void SetMute(Button b, bool active)
    {
        b.Background = active ? new SolidColorBrush(MuteRed) : new SolidColorBrush(BtnOff);
        b.Foreground = Brushes.White;
    }

    // ------------------------------------------------------------------- state
    private void ApplyUiState()
    {
        SetMute(_bMicMute, _cfg.MicMute);
        SetMute(_bSysMute, _cfg.SysMute);
        SetMute(_bAMute, _cfg.MuteA);
        SetMute(_bBMute, _cfg.MuteB);
        SetToggle(_bSendA, _cfg.MicToA);
        SetToggle(_bSendB, _cfg.MicToB);
        SetToggle(_bNs, _cfg.NsEnabled);
        _tMic.Text = Pct(_cfg.MicGain);
        _tSys.Text = Pct(_cfg.SysGain);
        _tA.Text = Pct(_cfg.GainA);
        _tB.Text = Pct(_cfg.GainB);
    }

    private void RefreshDeviceLists()
    {
        _loading = true;
        try
        {
            FillBox(_micBox, _graph.Watcher.CaptureDevices().Select(d => new DeviceItem { Id = d.ID, Name = d.FriendlyName }).ToList(), _cfg.MicDevice);
            var renders = _graph.Watcher.RenderDevices().Select(d => new DeviceItem { Id = d.ID, Name = d.FriendlyName }).ToList();
            FillBox(_boxA, renders, _cfg.DeviceA);
            FillBox(_boxB, renders, _cfg.DeviceB);
        }
        catch (Exception ex)
        {
            UiLog.Write("device list: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private static void FillBox(ComboBox box, List<DeviceItem> items, string? selectedId)
    {
        box.ItemsSource = items;
        var item = items.FirstOrDefault(i => i.Id == selectedId) ?? items.FirstOrDefault();
        box.SelectedItem = item;
    }

    // ------------------------------------------------------------- handlers
    private void MicPicked(string id) => _graph.SetMicDevice(id);
    private void OnBusPicked(string bus, string id) => _graph.SetBusDevice(bus, id);

    private void OnMicGain(double v)
    {
        if (_loading) return;
        double g = v / 100.0;
        _tMic.Text = Pct(g);
        _graph.SetMicGain(g);
    }
    private void OnSysGain(double v)
    {
        if (_loading) return;
        double g = v / 100.0;
        _tSys.Text = Pct(g);
        _graph.SetSysGain(g);
    }
    private void OnBusGain(string bus, double v)
    {
        if (_loading) return;
        double g = v / 100.0;
        if (bus == "A") { _tA.Text = Pct(g); _graph.SetBusGain("A", g); }
        else { _tB.Text = Pct(g); _graph.SetBusGain("B", g); }
    }

    private void ToggleMicMute()
    {
        bool on = !_cfg.MicMute;
        _cfg.MicMute = on;
        _graph.SetMicMute(on);
        SetMute(_bMicMute, on);
        UiLog.Write(on ? "mic MUTE on" : "mic MUTE off");
    }
    private void ToggleSysMute()
    {
        bool on = !_cfg.SysMute;
        _cfg.SysMute = on;
        _graph.SetSysMute(on);
        SetMute(_bSysMute, on);
        UiLog.Write(on ? "system MUTE on" : "system MUTE off");
    }
    private void ToggleBusMute(string bus)
    {
        bool on = bus == "A" ? !_cfg.MuteA : !_cfg.MuteB;
        _graph.SetBusMute(bus, on);
        if (bus == "A") { SetMute(_bAMute, on); UiLog.Write(on ? "bus A MUTE on" : "bus A MUTE off"); }
        else { SetMute(_bBMute, on); UiLog.Write(on ? "bus B MUTE on" : "bus B MUTE off"); }
    }

    private void OnSendA()
    {
        bool on = !_cfg.MicToA;
        _graph.SetMicSend("A", on);
        SetToggle(_bSendA, on);
    }
    private void OnSendB()
    {
        bool on = !_cfg.MicToB;
        _graph.SetMicSend("B", on);
        SetToggle(_bSendB, on);
    }
    private void OnNs()
    {
        bool on = !_cfg.NsEnabled;
        _graph.SetNs(on);
        SetToggle(_bNs, on);
    }

    // ------------------------------------------------------------------ meters
    private void UpdateMeters()
    {
        Pump(_vuMicL, _vuMicR, _clipMic, _graph.MeterMic);
        Pump(_vuSysL, _vuSysR, _clipSys, _graph.MeterSys);
        Pump(_vuAL, _vuAR, _clipA, _graph.MeterA);
        Pump(_vuBL, _vuBR, _clipB, _graph.MeterB);
    }

    private static void Pump(ProgressBar l, ProgressBar r, TextBlock clip, StageMeter m)
    {
        m.Snapshot(out float _, out float _, out float rmsL, out float rmsR, out bool clipFlag);
        l.Value = DbToPct(rmsL);
        r.Value = DbToPct(rmsR);
        clip.Foreground = clipFlag ? new SolidColorBrush(MuteRed) : Brushes.Transparent;
    }

    private static double DbToPct(float level)
    {
        if (level <= 0.000001f) return 0;
        double db = 20 * Math.Log10(level);
        return Math.Clamp((db + 60) / 60.0 * 100.0, 0, 100);
    }

    private static string Pct(double g) => (int)Math.Round(g * 100) + "%";

    private static double ToSlider(double gain) => Math.Clamp(gain * 100, 0, 150);

    // -------------------------------------------------------------------- log
    private void OnLogLine(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logLines.Add(line);
            if (_logLines.Count > 300) _logLines.RemoveRange(0, _logLines.Count - 300);
            _logBox.Text = string.Join("\n", _logLines);
            _logBox.CaretIndex = Math.Max(0, _logBox.Text.Length - 1);
        });
    }

    private void OnExit()
    {
        try
        {
            _timer.Stop();
            UiLog.Line -= OnLogLine;
            _cfg.Save();
            _graph.Dispose();
        }
        catch { /* closing anyway */ }
    }
}