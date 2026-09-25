# VU Mixer

**A VoiceMeeter-style mixer for Linux / PipeWire — with noise suppression and automatic USB hotplug repair.**

VU Mixer is a small self-hosted web app that turns your PC into a virtual
audio mixer the way VoiceMeeter does on Windows:

- pick any **microphone**, set its gain and watch its level
- route the **desktop** and the **mic** independently to **two output buses (A / B)**
- keep the mic **off your own speakers/headset** while it still reaches the
  other side (e.g. the second PC behind your HDMI capture card)
- apply **WebRTC noise suppression** to the mic with one click
- **auto-heal**: unplug your USB mic or HDMI audio — VU Mixer notices and
  reconnects everything when it comes back

Everything runs natively in PipeWire. **No audio ever passes through the
browser or Python** — the web page is only a remote control.

---

## Windows version

There is also a **native Windows port** — a single self-contained
`VUMixer.exe` (C# / .NET 8 + Avalonia UI + WASAPI). No Python, no runtime
installs, no admin rights: copy the exe anywhere and double-click. It mirrors
this app's features — mic gain/mute, desktop loopback, A/B buses, per-bus mic
sends, noise reduction, VU meters, hotplug repair, event log.

> Full project spec (read this first if you're picking the project up):
> [`PROJECT-PC.md`](./PROJECT-PC.md) · source lives in [`windows/`](./windows/).

```sh
cd windows && ./build.sh          # from any OS → windows/publish/VUMixer.exe
```

Status: **v0.1.0-win (test build)** — cross-compiled on Linux as a PE32+
Windows binary; real-device testing checklist in `PROJECT-PC.md` §10.

---

```
        ┌────────────────────────────────────────────┐
        │   your PC / PipeWire                        │
        │                                            │
  MIC ──► VM-Mic ───(gain / NS)──┬──► Bus A ──► output A (e.g. headset)
        │                        │                  │
  DESKTOP ──► VM-System ─────────┴──► Bus B ──► output B (e.g. HDMI/USB)
        │                                            │
        └────────────────────────────────────────────┘
                      ▲  apps use "Monitor of VM-Mic"
```

## Features

- **Mic strip** — input device picker, gain fader, MUTE, live VU meter, and
  three send buttons: `A`, `B`, `NS`.
- **Per-bus mic sends** — the mic goes only to the buses you enable, so you can
  send voice to the streaming PC over HDMI while staying silent on your own
  speakers.
- **Noise suppression (WebRTC AEC)** — a dependency-free filter (libspa's
  `aec_method=webrtc`) applied to the mic before it reaches the buses.
- **Desktop audio** — everything playing to the default output is captured into
  `VM-System` and copied to both buses; *Catch desktop audio* re-grabs apps
  that started elsewhere.
- **Hotplug self-healing** — a background supervisor watches your devices and
  repairs the routing when something is unplugged/plugged back in. A manual
  **Refresh devices** button forces an immediate check.
- **VU meters** on all four lanes (MIC / SYSTEM / A / B) using a tiny C helper
  that reads PipeWire meter info directly.
- **Mic routing diagnostic** — a dialog that shows which apps are fighting for
  the raw hardware mic and helps you point them at `Monitor of VM-Mic` instead.
- **No root, no pip, no system-wide install** — the web server is pure Python
  standard library; userspace only.

## Requirements

| Thing | Why |
| --- | --- |
| Linux with **PipeWire 0.3.50+**, `pipewire-pulse`, `wireplumber` | the audio backend the app drives |
| `pactl` (pulseaudio-utils), `pw-link`, `pw-dump` (pipewire-bin) | driving the graph |
| Webrtc AEC spa plugin (`libspa-0.2-modules` on Debian/Ubuntu) | the NS button |
| Python 3.8+ | standard library only — **no pip packages** |
| `gcc` + PipeWire dev headers | build the tiny VU meter helper (`vimeter`) |

> The app does **not** need sudo to run. Only the installer needs elevated
> rights, and only because it installs distro packages.

## Install (one click)

```sh
git clone https://github.com/helllopratik/vumixer.git && cd vumixer && ./install.sh
```

The installer detects your package manager (apt / dnf / pacman / zypper),
installs only what's missing, builds the meter helper, verifies all
dependencies, and boots the app for a few seconds as a self-test.

Covers Debian/Ubuntu, Fedora, Arch, and openSUSE. On a system where a package
name differs, the installer tells you exactly what to install manually — the
mixer still works, at worst the NS button is hidden.

**Manual fallback (any distro)**

```sh
cd vumixer
./build_meter.sh            # builds vimeter (needs gcc + libpipewire-0.3-dev)
./start.sh --no-browser     # starts the server; also opens the browser by default
```

## Run

```sh
./start.sh        # starts VU Mixer and opens http://127.0.0.1:8777
./stop.sh         # stops it and removes the virtual audio graph
```

The app keeps running while you need it. Closing it removes the virtual
devices and restores your previous defaults.

> Tip: only one instance can run at a time — if you see *"VU Mixer is already
> running (pid …)"*, just open http://127.0.0.1:8777 or run `./stop.sh` first.

## Using the web UI

1. **Pick your microphone** in the MIC strip. Its gain and VU meter are shown
   there. Use the **A**/**B** buttons to choose which output buses receive the
   mic — e.g. turn **A** off so you don't hear yourself, keep **B** on to send
   your voice to the other PC over HDMI.
2. **Noise suppression:** press **NS** to remove background noise with the
   built-in WebRTC filter.
3. **Pick a device for A and for B** — for example your headset on A and the
   HDMI/USB capture on B. Desktop audio is copied to both; the mic only to the
   buses you enabled.
4. **Desktop audio** is captured by making `VM-System` the default output. If
   something is already playing elsewhere, press **Catch desktop audio**.
5. **Other apps** (Discord, OBS, browsers…) should select `Monitor of VM-Mic`
   as their *input*. Then they get the same mixed mic signal without grabbing
   the hardware microphone. The **Mic routing** button shows which apps are
   still on the raw hardware mic.

## How it works

| Virtual node | Role |
| --- | --- |
| `VM-System` | captures the desktop default output; copied to A and B |
| `VM-Mic` | the gain-controlled mic bus; sends to A / B as enabled |
| `VM-Mic` NS (`vm_ns`) | WebRTC-filtered mic used when NS is on |
| `VM-Bus-A` / `VM-Bus-B` | the two output buses, linked to your chosen devices |
| `Monitor of VM-Mic` | the input *apps* should select to share the mic |

The web server applies and repairs link graph when devices come and go, and a
supervisor tick re-checks the graph every few seconds.

## HTTP API

| Endpoint | Method | Purpose |
| --- | --- | --- |
| `/api/state` | GET | full mixer state (devices, gains, sends, build code) |
| `/api/meters` | GET | live VU levels for all four lanes |
| `/api/build` | GET | current code hash + version (the page verifies it is not stale) |
| `/api/diagnose` | GET | mic-routing / conflicts report |
| `/api/mic/gain` | POST | mic volume & mute `{"volume":…,"mute":…}` |
| `/api/mic/device` | POST | pick microphone `{"device":…}` |
| `/api/mic/send` | POST | mic→bus send `{"bus":"A"\|"B","enabled":bool}` |
| `/api/mic/ns` | POST | noise suppression `{"enabled":bool}` |
| `/api/bus/gain` | POST | bus volume & mute `{"bus":"A"\|"B",…}` |
| `/api/bus/device` | POST | pick output for a bus `{"bus":…,"device":…}` |
| `/api/system/gain` | POST | desktop volume & mute |
| `/api/move-streams` | POST | move playing apps into `VM-System` |
| `/api/refresh` | POST | re-scan devices and repair the routing |
| `/api/default-input` | POST | switch the default-input policy |

## Project layout

```
vumixer/
├── install.sh          # one-click installer
├── build_meter.sh      # builds the VU meter helper
├── vimeter.c           # tiny PipeWire meter C helper
├── server.py           # stdlib-only web server + PipeWire driver + supervisor
├── start.sh / stop.sh  # lifecycle
├── static/
│   ├── index.html      # the web UI
│   ├── style.css
│   └── app.js          # meters, controls, live log panel, stale-code check
└── config.json         # created at runtime — machine-specific (ignored by git)
```

## Troubleshooting

- **`pactl` / `pw-link` not found** — the tools are missing; run `./install.sh`
  or install `pulseaudio-utils` and `pipewire-bin` (names may differ per distro).
- **NS button is disabled** — the WebRTC AEC plugin is not installed
  (`libspa-0.2-modules` on Debian/Ubuntu). The mixer still works.
- **My USB mic/HDMI doesn't reappear** — click **Refresh devices**; the
  supervisor repairs it automatically, but the button forces an immediate scan.
- **Another app grabs the mic first** (two programs can't share raw hardware
  input) — use **Mic routing** and switch the app to `Monitor of VM-Mic`.
- **"VU Mixer is already running"** — only one instance is allowed; run
  `./stop.sh` first, or just open http://127.0.0.1:8777.
- **Want to know exactly what the page did?** — open the **Debug log** panel at
  the bottom of the UI; every click, request and result is timestamped. The
  header also shows the current **code** hash and flags itself red if the page
  is older than the server.
- **Server log** — runtime logs stream to `/tmp/vumixer.log`.

## License

[MIT](LICENSE) © 2026 [Pratik Gondane](https://github.com/helllopratik) —
pratikgondane07@gmail.com