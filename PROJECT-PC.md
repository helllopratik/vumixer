# PROJECT-PC — VU Mixer for Windows

> **Read this first.** This file is the complete project brief. Any developer or
> AI agent picking up this repo after reading only this file should be able to
> build, run, extend and test the Windows port of **VU Mixer**.
>
> Status: **v1 (native desktop port)** · last updated 2026-09-22

---

## 1. What this project is

VU Mixer is a VoiceMeeter-style *virtual audio mixer*:

- one **microphone** input,
- the **desktop** output (everything your PC plays),
- mixed into **two user-chosen output buses (A / B)**,

with per-bus mic sends, gain/mute on every lane, live VU meters and one-click
**noise reduction**.

There are two implementations in this repo:

| Implementation | Tech | Where | Status |
| --- | --- | --- | --- |
| **Linux / PipeWire** | Python (stdlib) + PipeWire tools | repo root (`server.py`, `static/`) | **stable, in use** (reference behavior) |
| **Windows / WASAPI** | C# (.NET 8) + NAudio + Avalonia UI | `windows/` | this project's target |

The Linux version is the **reference** for behavior, UX and the HTTP API. The
Windows port is a **native desktop app** — no browser, no Python, no runtime
installs. One double-clickable, self-contained `VUMixer.exe`.

## 2. Hard requirements (non-negotiable)

1. **Runs on double-click.** No installer, no admin rights, no "install .NET",
   no "install Python", no VB-Cable required for the core features.
2. **Single self-contained `.exe`** (Avalonia UI is compiled in; .NET runtime
   bundled via `--self-contained`).
3. **Portable** — the app must not write outside its own folder/`%LOCALAPPDATA%`;
   settings live in a `config.json` next to the exe.
4. **Zero cloud / zero telemetry** — fully offline, local-only.
5. **Latency budget:** added latency ≤ 40 ms end-to-end (WASAPI shared mode;
   buffer 20 ms or less per stage).
6. **CPU:** idle < 1%, mixing < 5% on a normal desktop CPU.
7. Single instance (second launch focuses the running window).
8. English UI (v1). Dark theme (matches the web app).
9. If a device (mic / bus output) vanishes or appears → **auto-recover** with
   minimal glitches + a user-visible event log line (same behavior as Linux).
10. Same **feature parity** as the Linux web UI (see §4).

## 3. Why Avalonia + NAudio (decisions locked in)

- **C# (.NET 8 LTS)** — user-approved ("C#, C will also work"); modern, safe,
  single-file publish.
- **Avalonia UI (v11, Fluent theme)** — chosen over WPF **because the build
  host is Linux**: WPF applications cannot be reliably cross-compiled from
  Linux, whereas Avalonia publishes a `win-x64` self-contained single-file exe
  from Linux with `EnableWindowsTargeting`-free plain `dotnet publish`.
  Avalonia feels native, has themable dark styling, and needs no runtime.
- **NAudio (MIT)** — the standard C# audio library: WASAPI capture,
  loopback, render, resampling and mixing. Ships inside the exe (no external
  install).
- **No Python** (user requirement) → the old `vimeter.c`/PipeWire helpers do
  not exist on Windows; meters are computed in-engine from the float buffers.

## 4. Feature set (parity with the Linux app)

| # | Feature | Notes |
| --- | --- | --- |
| F1 | Pick **microphone** | WASAPI input endpoints, friendly names |
| F2 | Mic **gain** (0–150%) + **MUTE** | slider + live % readout |
| F3 | Mic **sends to A / B** independently | two toggle buttons; desktop always goes to both |
| F4 | **Desktop** capture | WASAPI **loopback** of the default render device ("what you hear") |
| F5 | **SYSTEM** gain + MUTE | desktop lane |
| F6 | Pick output device for **A** and **B** | WASAPI render endpoints |
| F7 | **A / B gain + MUTE** | per-bus master |
| F8 | VU **meters** (MIC / SYSTEM / A / B) | peak + RMS bars, clip indicator, ~60 fps updates |
| F9 | **Noise reduction** toggle (per-mic) | v1 = noise-gate (see §7) |
| F10 | **Hotplug auto-repair** | WASAPI device-change notifications + 5 s health sweep + manual **Refresh** button |
| F11 | **Event log panel** | bottom panel, timestamps; every click/action/device event logged |
| F12 | **Build code** shown in title bar | changes with every build → proves which version runs |
| F13 | Settings persist | `config.json` next to exe (device picks, gains, sends, NS on/off) |
| F14 | Preserve system defaults | never change the OS default devices; restore gracefully on exit |

## 5. Audio architecture (data flow)

```
                 ┌───────────── WASAPI (shared mode, float32) ─────────────┐
                 │                                                         │
  MIC endpoint ─► mic capture ─► resample@48k ─► NoiseGate ─► gain ─► ─┐    │
                 │                                          mute ─► ─┐ │    │
                 │   (meter)                                (meter)  │ │    │
                 │                                                    ├──► Mix A ─► resample→dev fmt ─► 16-bit ─► WASAPI render ► device A
  DEFAULT render ─► loopback capture ─► resample@48k ─► gain ─► ──────┤   (always)    │
                 │   (desktop)                     mute ─► ────────┐  │             │
                 │   (meter)                          (meter)      │  ├──► Mix B ─► resample→dev fmt ─► 16-bit ─► WASAPI render ► device B
                 │                                        always ──┴──┘  (always)
                 └─────────────────────────────────────────────────────────┘
         mic → A : only when mic-send A ON
         mic → B : only when mic-send B ON
         desktop → A and B : always
```

**Threading model**

- One capture thread per input (mic, desktop); each pushes 10 ms blocks into
  the corresponding stage.
- Render threads **pull** from the mixes (WASAPI shared-mode callbacks).
- All float buffer math is lock-free-ish (single producer → per-input queue /
  atomic snapshot; the mixer is pull-driven so it just reads the latest block).
- Meters: each stage writes `peak/RMS` float pairs into a shared snapshot;
  a UI `DispatcherTimer` (≈ 60 Hz) reads them to draw the bars (never touch
  UI from audio threads).

**Sample format strategy (robustness)**

1. All inputs resampled to **48 kHz stereo float32** (`WdlResamplingSampleProvider`).
2. Each bus renderer queries the target device's **mix format** and resamples
   48 kHz → device format, converts to 16-bit PCM, feeds `WasapiOut`
   (`AudioClientShareMode.Shared`). This avoids "format not supported" crashes
   across arbitrary hardware (44.1 kHz TV HDMI, 48 kHz USB, etc.).

**Loopback of a chosen default device**: the desktop lane loops back the OS
**default render device** (like "Stereo Mix"). That device is itself user-
switchable in Windows, so the app reads *"what the user hears"* — matching the
Linux behavior of `VM-System`.

## 6. UI layout (about 1 window, dark theme)

```
┌─────────────────────────────────────────────────────────────┐
│ VU Mixer  ·  build 0.1.0-win-<hash>    [● ready] [Refresh]  │
├─────────────────────────────────────────────────────────────┤
│ MIC   [input device v]   ◼◼◼◼◼ VL ◼◼ met ◼◼  Gain ▓▓▓ 100%  │
│        [A] [B] [NS]  sends   MUTE                          │
├─────────────────────────────────────────────────────────────┤
│ SYSTEM (desktop)  met ▓▓  Gain ▓▓ 100%   MUTE              │
├─────────────────────────────────────────────────────────────┤
│ BUS A [output device v]  met ▓▓  Gain ▓▓ 100%   MUTE       │
│ BUS B [output device v]  met ▓▓  Gain ▓▓ 100%   MUTE       │
├─────────────────────────────────────────────────────────────┤
│ Events: [timestamped log of actions/device changes]  Copy   │
└─────────────────────────────────────────────────────────────┘
```

- One `MainWindow`, tabs not needed; strip-per-lane like the web app.
- VU meters: vertical-bar progress controls with peak-hold ticks + red CLIP.
- Device combo boxes refresh on device change events (keep selection).
- Buttons reflect engine state; disabled tooltips when no device.

## 7. Noise reduction — v1 scope (be honest)

- v1 ships a **noise gate** (soft-knee RMS gate with attack/hold/release,
  threshold ≈ −50 dBFS, adjustable in a small popover) computed on 10 ms mic
  blocks. It kills background hum/hiss between speech.
- Full RNNoise/WebRTC AEC on Windows would need a native DLL dependency — out
  of scope for the zero-install v1. Documented as roadmap.
- The UI toggle is labeled **NS** for parity; gate parameters live in
  `config.json` (advanced users can edit).

## 8. Device handling & hotplug (parity F10)

- Device inventory via NAudio `MMDeviceEnumerator` +
  `RegisterEndpointNotificationCallback`:
  - `on-default-device-change` → hot-swap desktop loopback.
  - `on-device-added/removed` → refresh lists; if the active mic/A/B output
    vanished → try previous pick on re-appearance; log each event.
- Plus a **5-second health sweep**: if a capture/render thread died or a used
  endpoint is gone, attempt a clean reopen (bounded retries, log lines).
- Manual **Refresh** button forces re-scan + reopen.
- Engine keeps last-good state; audio keeps playing on the unaffected lanes
  while a lane is being repaired (do not tear down the whole graph).

## 9. Build & packaging (exact commands)

Requirements to build: .NET 8 SDK (any OS — **Linux build host is fully
supported** because we chose Avalonia), internet for NuGet restore.

```sh
cd windows
./build.sh                 # publishes win-x64 self-contained single-file
# output: windows/publish/VUMixer.exe
```

Equivalent:
```sh
dotnet publish src/VUMixer -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true
```

- Single-file bundle is ~150 MB (bundled .NET runtime) — accepted trade-off
  for "double-click and it works". If size matters later a framework-dependent
  build (~25 MB) can be offered alongside (needs .NET Desktop Runtime 8).
- Windows 10 1809+ / Windows 11. ARM64 (win-arm64) is a later milestone; the
  RID is a one-line change.
- Build host = Linux: only cross-compile check is that the produced file is a
  valid PE32+, `sigcheck`/`file` can confirm; **all functional testing must
  happen on the real Windows machine** (see §10).

## 10. Testing checklist (run on the Windows target machine)

Manual test plan — run in order, record result + log lines for each:

1. **Launch**: copy `VUMixer.exe` to a folder (e.g. `C:\VUMixer\`),
   double-click → window appears, status = ready, build code visible.
   No UAC prompt, no "missing runtime" dialog.
2. **Devices list**: all input/output endpoints appear with friendly names.
3. **Mic lane**: play/speak → MIC meter reacts; gain slider changes level;
   MUTE kills it; muted state reflects on all dependants.
4. **System lane**: play YouTube/music → SYSTEM meter reacts; desktop audio
   lands on both A and B (hear it on both selected outputs).
5. **Sends**: turn **A** mic-send OFF → mic disappears from A but stays on B
   (and vice versa); desktop remains on both.
6. **Bus routing**: set A = headset, B = HDMI/USB capture → audio plays on
   both; VU meters on A and B move.
7. **NS**: with a quiet background noise source, toggle NS → noise floor drops
   noticeably during silence/gaps; speech stays natural enough.
8. **Hotplug**: unplug the mic → app logs event, MUTE/lane state survives;
   plug back in → mic returns automatically ≤ 5 s. Same for an A/B output
   device. Refresh button also re-scans.
9. **Persistence**: change device/gain/send settings, close the app
   (x / tray), reopen → settings restored from `config.json`.
10. **Single instance**: launch a second copy → first window comes to front.
11. **Exit**: close app → audio returns to normal instantly, no zombie
    processes (`Task Manager`), no system default changes.
12. **Latency feel**: talk + hear through layout — no distracting echo/delay.

Expected engine invariants (spot-check with log tooling):
- No exception spam in the event log; device events logged once, not looped.
- Capture/render threads stay alive during 30-minute soak with music on.

## 11. Known limitations (v1, deliberate)

- Desktop lane loopbacks the **default** render device only (not per-app
  routing). Per-app capture needs a virtual cable (e.g. free VB-Cable) —
  **designed for later insertion**, out of scope v1.
- NS is a gate, not full spectral noise suppression/AEC.
- No mic echo cancellation on the A-lane (we intentionally keep mic off A via
  sends; that's the anti-feedback workflow).
- 44.1 kHz → resampled to 48 kHz pipeline (transparent, but noted).
- No tray icon/no minimize-to-tray in v1 (window close quits engine).

## 12. Repository map (Windows side)

```
windows/
├── PROJECT-PC.md                  ← you are here
├── build.sh                       # publish win-x64 single-file
├── VUMixer.sln
└── src/VUMixer/
    ├── VUMixer.csproj             # net8.0, Avalonia, NAudio
    ├── Program.cs                 # single-instance mutex, app boot
    ├── App.axaml(.cs)
    ├── MainWindow.axaml(.cs)      # all UI wiring (code-behind, no heavy MVVM)
    ├── Audio/
    │   ├── AudioGraph.cs          # engine: capture→mix→render, hotplug
    │   ├── LoopbackCapture.cs     # WASAPI loopback of chosen render device
    │   ├── MixSampleProvider.cs   # pull-driven stereo float32 mixer + meters
    │   ├── NoiseGate.cs           # RMS soft-knee gate
    │   └── DeviceWatcher.cs       # endpoint notifications + health sweep
    ├── Config.cs                  # config.json next to exe
    └── UiLog.cs                   # timestamped event log (UI-bound)
```

## 13. Conventions for contributors

- C# language version = latest 8.x defaults. Nullable enabled. Async where I/O.
- **Never touch UI from audio threads** — all engine→UI updates go through the
  dispatcher or an atomic snapshot class.
- All audio sinks/sources must implement `IDisposable` and be torn down in a
  fixed order (stop render → stop capture → dispose queue).
- Keep the `/api/*` HTTP contract (Linux side) untouched; a future "web remote
  for Windows" can reuse it.
- Every user action appends a `UiLog` line — do not add silent behaviors.
- Bump the **build code** (a `BUILD` const; e.g. date+hash) on every release —
  it is shown in the title bar (feature parity with the web app's code hash).

## 14. Roadmap (next steps, in priority order)

1. Ship v1 exe (this milestone) → user test pass on real hardware (§10).
2. Tray icon + minimize-to-tray.
3. Optional VB-Cable detection → route arbitrary apps into the mix.
4. Better NS (port RNNoise/WebRTC AEC via a small native DLL, with graceful
   fallback to the gate).
5. `win-arm64` build; framework-dependent "tiny" build alongside the big one.
6. Optional embedded web remote (same `/api` contract) for controlling from
   another device.