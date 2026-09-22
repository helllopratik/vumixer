#!/usr/bin/env python3
"""
VU Mixer - a VoiceMeeter-style virtual audio mixer for PipeWire (Linux).

A dependency-free local web app (Python standard library only) that:

  * lets you choose a microphone input and set its gain,
  * shows live VU meters for the mic, the desktop ("system") audio and
    the two output buses,
  * routes the microphone and the desktop audio to two independent output
    buses (A and B) at the same time; each bus can be sent to any real
    output device (headphones, HDMI, ...),
  * tells you which input to select in other apps so they capture the
    mixed mic bus instead of fighting over the raw hardware microphone.

Audio is routed natively by PipeWire.  This program only creates virtual
nodes and the links between them; no audio ever passes through Python.

Graph
-----
    real mic  --->  vm_miclink  (mic bus, gain)  ---+
                                                     +--> vm_a --> device A
    desktop   --->  vm_system   (default sink)   ---+     vm_b --> device B
                       ^                                  ^
                       |                                  |
                 apps play here                 apps read vm_miclink.monitor

Run:   ./start.sh          (or: python3 server.py)
Stop:  Ctrl-C, or ./stop.sh
Clean: python3 server.py --cleanup
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import webbrowser
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
STATIC_DIR = os.path.join(HERE, "static")
CONFIG_PATH = os.path.join(HERE, "config.json")
PIDFILE = os.path.join(HERE, "vumixer.pid")
VIMETER = os.path.join(HERE, "vimeter")

# Virtual node key -> PipeWire / PulseAudio node name
VM = {
    "system": "vm_system",
    "miclink": "vm_miclink",
    "a": "vm_a",
    "b": "vm_b",
}
VM_DESC = {
    "system": "VM-System",
    "miclink": "VM-Mic",
    "a": "VM-Bus-A",
    "b": "VM-Bus-B",
}
ALL_VM_NAMES = set(VM.values())
# Virtual nodes owned by the mic "noise suppression" feature (WebRTC AEC):
#  - vm_ns      : the filtered mic source (what gets linked into the mic bus)
#  - vm_ns_echo : the module's echo-reference sink (never user-selectable)
FX_NAMES = {"vm_ns", "vm_ns_echo"}
NS_AEC_PATHS = (
    "/usr/lib/x86_64-linux-gnu/spa-0.2/aec/libspa-aec-webrtc.so",
    "/usr/lib64/spa-0.2/aec/libspa-aec-webrtc.so",
)
MIC_MONITOR = "vm_miclink.monitor"
VIMETER_NODE_NAME = "vimeter-stream"

MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    ".ico": "image/x-icon",
    ".json": "application/json",
    ".woff2": "font/woff2",
}

# Short hash of the app files (server + web UI).  Every change to any of
# these files changes this code, so the page can prove whether it is running
# the latest code (it is surfaced in the UI and compared on every poll).
BUILD_FILES = ("server.py", "static/app.js", "static/index.html",
               "static/style.css")
APP_VERSION = "v4"


def build_id():
    h = hashlib.sha1()
    for rel in BUILD_FILES:
        p = os.path.join(HERE, rel)
        try:
            with open(p, "rb") as f:
                h.update(rel.encode())
                h.update(f.read())
        except OSError:
            h.update(rel.encode())
            h.update(b"?")
    return h.hexdigest()[:10]


class CmdError(RuntimeError):
    """A shell helper (pactl / pw-link / pw-dump) failed."""


# --------------------------------------------------------------------------
# small command helpers
# --------------------------------------------------------------------------

def run(cmd, timeout=15):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except FileNotFoundError as e:
        raise CmdError("command not found: %s (%s)" % (cmd[0], e))
    except subprocess.TimeoutExpired:
        raise CmdError("command timed out: %s" % " ".join(cmd))


def run_ok(cmd, timeout=15):
    p = run(cmd, timeout=timeout)
    if p.returncode != 0:
        msg = (p.stderr or p.stdout or "").strip() or "exit %d" % p.returncode
        raise CmdError("%s failed: %s" % (" ".join(cmd), msg))
    return p.stdout


def pactl_json(what):
    p = run(["pactl", "-f", "json", "list", what])
    if p.returncode != 0:
        raise CmdError("pactl list %s failed: %s" % (what, (p.stderr or "").strip()))
    try:
        data = json.loads(p.stdout or "[]")
    except json.JSONDecodeError:
        return []
    return data if isinstance(data, list) else []


_pw_dump_cache = {"t": 0.0, "data": None}
_pw_dump_lock = threading.Lock()


def pw_dump(max_age=0.5):
    now = time.monotonic()
    with _pw_dump_lock:
        if _pw_dump_cache["data"] is None or now - _pw_dump_cache["t"] > max_age:
            out = run_ok(["pw-dump"])
            _pw_dump_cache["data"] = json.loads(out or "[]")
            _pw_dump_cache["t"] = now
        return _pw_dump_cache["data"]


def invalidate_dump():
    with _pw_dump_lock:
        _pw_dump_cache["data"] = None


def pw_node_ids():
    """Map node.name -> PipeWire global node id (needed by vimeter)."""
    ids = {}
    for obj in pw_dump(max_age=4.0):
        if obj.get("type") == "PipeWire:Interface:Node":
            props = obj.get("info", {}).get("props", {}) or {}
            name = props.get("node.name")
            if name:
                ids[name] = obj.get("id")
    return ids


def find_null_sink_modules():
    """sink_name -> pulse module id, for every module-null-sink we own."""
    out = run(["pactl", "list", "modules"]).stdout or ""
    result = {}
    for block in re.split(r"\n(?=Module #)", out):
        m = re.match(r"Module #(\d+)", block)
        if not m:
            continue
        nm = re.search(r"\n\s*Name:\s*(\S+)", block)
        ar = re.search(r"\n\s*Argument:\s*(.*)", block)
        if nm and nm.group(1) == "module-null-sink" and ar:
            for mm in re.finditer(r"sink_name=(\S+)", ar.group(1)):
                result[mm.group(1)] = int(m.group(1))
    return result


def unload_echo_cancel_module():
    """Unload the WebRTC echo-cancel module powering the mic noise
    suppression feature (removes the vm_ns source + vm_ns_echo sink).

    Returns True if a module was actually unloaded.
    """
    out = run(["pactl", "list", "modules"]).stdout or ""
    found = False
    for block in re.split(r"\n(?=Module #)", out):
        m = re.match(r"Module #(\d+)", block)
        if not m:
            continue
        nm = re.search(r"\n\s*Name:\s*(\S+)", block)
        if nm and nm.group(1) == "module-echo-cancel":
            run(["pactl", "unload-module", str(int(m.group(1)))])
            found = True
    return found


def load_null_sink(name, desc):
    p = run(["pactl", "load-module", "module-null-sink",
             "sink_name=%s" % name,
             "sink_properties=device.description=%s" % desc])
    if p.returncode != 0:
        raise CmdError("could not create virtual sink %s: %s"
                       % (name, (p.stderr or "").strip()))
    try:
        return int(p.stdout.strip())
    except ValueError:
        return None


def get_default_sink():
    return (run(["pactl", "get-default-sink"]).stdout or "").strip()


def get_default_source():
    return (run(["pactl", "get-default-source"]).stdout or "").strip()


def set_default_sink(name):
    run(["pactl", "set-default-sink", name])


def set_default_source(name):
    run(["pactl", "set-default-source", name])


def pct_from_volume(vol):
    """PulseAudio volume dict -> average percentage (0..100+)."""
    if not isinstance(vol, dict):
        return 0.0
    vals = [v.get("value", 0) for v in vol.values() if isinstance(v, dict)]
    if not vals:
        return 0.0
    return round(100.0 * (sum(vals) / len(vals)) / 65536.0, 1)


def is_meter_stream(props):
    """True for the capture streams created by our own vimeter helper."""
    if not isinstance(props, dict):
        return False
    if (props.get("node.name") or "").startswith("vimeter"):
        return True
    if "vimeter" in (props.get("application.process.binary") or ""):
        return True
    return (props.get("application.name") or "") == "vimeter"


def list_real_sinks():
    out = []
    for s in pactl_json("sinks"):
        name = s.get("name", "")
        if not name or name in ALL_VM_NAMES or name in FX_NAMES:
            continue
        out.append({
            "name": name,
            "description": s.get("description") or name,
            "index": s.get("index"),
            "volume": pct_from_volume(s.get("volume")),
            "mute": bool(s.get("mute")),
            "monitor": s.get("monitor_source"),
        })
    out.sort(key=lambda x: x["description"].lower())
    return out


def list_real_mics():
    out = []
    for s in pactl_json("sources"):
        name = s.get("name", "")
        if not name or name in ALL_VM_NAMES or name in FX_NAMES \
                or name.endswith(".monitor"):
            continue
        out.append({
            "name": name,
            "description": s.get("description") or name,
            "index": s.get("index"),
            "volume": pct_from_volume(s.get("volume")),
            "mute": bool(s.get("mute")),
        })
    out.sort(key=lambda x: x["description"].lower())
    return out


def find_mic_monitor_source():
    for s in pactl_json("sources"):
        if s.get("name") == MIC_MONITOR:
            return s
    return None


# --------------------------------------------------------------------------
# graph: create the virtual sinks and (re)wire the links
# --------------------------------------------------------------------------

class Graph:
    def __init__(self):
        self.lock = threading.RLock()
        self.node_ids = {}
        self.mic_device = None
        self.bus_devices = {"A": None, "B": None}
        self._links = None
        self._links_t = 0.0

    # -- discovery ---------------------------------------------------------

    def refresh_ids(self):
        ids = pw_node_ids()
        self.node_ids = {key: ids.get(name) for key, name in VM.items()}

    def ensure_nodes(self):
        existing = find_null_sink_modules()
        for key, name in VM.items():
            if name not in existing:
                load_null_sink(name, VM_DESC[key])
        invalidate_dump()
        self.refresh_ids()

    # -- ports / links -----------------------------------------------------

    @staticmethod
    def _ports(direction):
        out = run_ok(["pw-link", direction])
        return [ln.strip() for ln in out.splitlines() if ln.strip()]

    def out_ports(self, node):
        return [p for p in self._ports("-o") if p.startswith(node + ":")]

    def in_ports(self, node):
        return [p for p in self._ports("-i") if p.startswith(node + ":")]

    def links(self, fresh=False):
        with self.lock:
            now = time.monotonic()
            if fresh or self._links is None or now - self._links_t > 0.4:
                self._links = self._read_links()
                self._links_t = now
            return self._links

    @staticmethod
    def _read_links():
        out = run_ok(["pw-link", "-l"])
        links = set()
        current = None
        for line in out.splitlines():
            if not line.strip():
                continue
            if line[0] not in " \t|":
                current = line.strip()
                continue
            s = line.strip()
            if s.startswith("|->"):
                links.add((current, s[3:].strip()))
            elif s.startswith("|<-"):
                links.add((s[3:].strip(), current))
        return links

    def connect(self, out_port, in_port):
        with self.lock:
            if (out_port, in_port) in self.links():
                return
            run(["pw-link", out_port, in_port])
            self._links = None

    def disconnect(self, out_port, in_port):
        with self.lock:
            run(["pw-link", "-d", out_port, in_port])
            self._links = None

    def remove_links_out_of(self, node):
        for o, i in list(self.links(fresh=True)):
            if o.startswith(node + ":"):
                self.disconnect(o, i)

    def remove_links_into(self, node):
        for o, i in list(self.links(fresh=True)):
            if i.startswith(node + ":"):
                self.disconnect(o, i)

    # -- wiring ------------------------------------------------------------

    def link_mic(self, mic_name):
        """Feed the chosen hardware mic into the mic bus (mono -> both)."""
        with self.lock:
            self.remove_links_into(VM["miclink"])
            if not mic_name:
                return
            caps = [p for p in self.out_ports(mic_name) if ":capture" in p]
            bus = VM["miclink"]
            fl, fr = "%s:playback_FL" % bus, "%s:playback_FR" % bus
            if any(p.endswith(":capture_FL") for p in caps) and \
               any(p.endswith(":capture_FR") for p in caps):
                self.connect("%s:capture_FL" % mic_name, fl)
                self.connect("%s:capture_FR" % mic_name, fr)
            elif any(p.endswith(":capture_MONO") for p in caps):
                mono = "%s:capture_MONO" % mic_name
                self.connect(mono, fl)
                self.connect(mono, fr)
            elif caps:
                first = caps[0]
                self.connect(first, fl)
                self.connect(first, fr)

    def link_buses(self, mic_to_a=True, mic_to_b=True):
        """Desktop audio (vm_system) is copied to both buses; the mic
        (vm_miclink) only to the buses whose send toggle is enabled."""
        with self.lock:
            sends = {"a": mic_to_a, "b": mic_to_b}
            for key in ("a", "b"):
                bus = VM[key]
                self.remove_links_into(bus)
                self.connect("%s:monitor_FL" % VM["system"],
                             "%s:playback_FL" % bus)
                self.connect("%s:monitor_FR" % VM["system"],
                             "%s:playback_FR" % bus)
                if sends[key]:
                    self.connect("%s:monitor_FL" % VM["miclink"],
                                 "%s:playback_FL" % bus)
                    self.connect("%s:monitor_FR" % VM["miclink"],
                                 "%s:playback_FR" % bus)

    def link_device(self, bus_key, device_name):
        """Bus monitor -> the real output device's playback ports.

        Old links to other devices are torn down, but our meter capture
        links (vimeter-stream) are left in place.
        """
        with self.lock:
            bus = VM[bus_key]
            for o, i in list(self.links(fresh=True)):
                if o.startswith(bus + ":monitor_") and not i.startswith("vimeter"):
                    self.disconnect(o, i)
            if not device_name:
                return
            plays = [p for p in self.in_ports(device_name) if ":playback" in p]
            out_l, out_r = "%s:monitor_FL" % bus, "%s:monitor_FR" % bus
            if any(p.endswith(":playback_FL") for p in plays) and \
               any(p.endswith(":playback_FR") for p in plays):
                self.connect(out_l, "%s:playback_FL" % device_name)
                self.connect(out_r, "%s:playback_FR" % device_name)
            elif any(p.endswith(":playback_MONO") for p in plays):
                mono = "%s:playback_MONO" % device_name
                self.connect(out_l, mono)
                self.connect(out_r, mono)

    def apply_all(self, mic_to_a=True, mic_to_b=True):
        self.link_mic(self.mic_device)
        self.link_buses(mic_to_a, mic_to_b)
        self.link_device("a", self.bus_devices["A"])
        self.link_device("b", self.bus_devices["B"])

    # -- meter targets -----------------------------------------------------

    def meter_targets(self):
        return {
            "MIC": self.node_ids.get("miclink"),
            "SYS": self.node_ids.get("system"),
            "A": self.node_ids.get("a"),
            "B": self.node_ids.get("b"),
        }


# --------------------------------------------------------------------------
# meters: one vimeter process per node, parsed into shared state
# --------------------------------------------------------------------------

class MeterManager:
    def __init__(self):
        self.lock = threading.Lock()
        self.values = {}
        self.procs = {}
        self.threads = {}
        self.targets = {}

    def start(self, label, node_id):
        if not node_id:
            return
        p = self.procs.get(label)
        if self.targets.get(label) == node_id and p is not None and p.poll() is None:
            return
        self.stop(label)
        if not os.path.exists(VIMETER):
            return
        try:
            proc = subprocess.Popen(
                [VIMETER, str(node_id), label],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, bufsize=1)
        except OSError:
            return
        with self.lock:
            self.procs[label] = proc
            self.targets[label] = node_id
            self.values[label] = {"rmsL": 0.0, "rmsR": 0.0,
                                  "peakL": 0.0, "peakR": 0.0,
                                  "clip": False, "ts": time.time()}
        t = threading.Thread(target=self._reader, args=(label, proc), daemon=True)
        self.threads[label] = t
        t.start()

    def _reader(self, label, proc):
        try:
            for line in proc.stdout:
                line = line.strip()
                if not line.startswith("M "):
                    continue
                parts = line.split()
                if len(parts) < 7:
                    continue
                try:
                    rms_l, rms_r, pk_l, pk_r = (float(parts[2]), float(parts[3]),
                                                float(parts[4]), float(parts[5]))
                except ValueError:
                    continue
                with self.lock:
                    self.values[label] = {
                        "rmsL": rms_l, "rmsR": rms_r,
                        "peakL": pk_l, "peakR": pk_r,
                        "clip": parts[6] == "1",
                        "ts": time.time(),
                    }
        except (ValueError, OSError):
            pass

    def ensure(self, targets):
        for label, node_id in targets.items():
            proc = self.procs.get(label)
            if proc is None or proc.poll() is not None or \
               self.targets.get(label) != node_id:
                self.start(label, node_id)

    def snapshot(self):
        with self.lock:
            out = {}
            for label, val in self.values.items():
                proc = self.procs.get(label)
                alive = proc is not None and proc.poll() is None
                out[label] = dict(val, alive=alive)
            return out

    def stop(self, label):
        proc = self.procs.pop(label, None)
        self.targets.pop(label, None)
        self.threads.pop(label, None)
        if proc is not None:
            try:
                proc.terminate()
                proc.wait(timeout=1)
            except (OSError, subprocess.TimeoutExpired):
                try:
                    proc.kill()
                except OSError:
                    pass

    def stop_all(self):
        for label in list(self.procs):
            self.stop(label)


# --------------------------------------------------------------------------
# config
# --------------------------------------------------------------------------

def load_config():
    try:
        with open(CONFIG_PATH) as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, json.JSONDecodeError):
        return {}


def save_config(cfg):
    try:
        tmp = CONFIG_PATH + ".tmp"
        with open(tmp, "w") as f:
            json.dump(cfg, f, indent=2)
        os.replace(tmp, CONFIG_PATH)
    except OSError:
        pass


def pid_alive(pid):
    if not pid or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except OSError:
        return False
    return True


def read_pidfile():
    try:
        with open(PIDFILE) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return None


def write_pidfile(pid):
    try:
        with open(PIDFILE, "w") as f:
            f.write(str(pid))
    except OSError:
        pass


def remove_pidfile():
    try:
        if os.path.exists(PIDFILE):
            os.unlink(PIDFILE)
    except OSError:
        pass


# --------------------------------------------------------------------------
# the application
# --------------------------------------------------------------------------

class App:
    def __init__(self, config):
        self.config = config
        self.graph = Graph()
        self.meters = MeterManager()
        self.original_default_sink = None
        self.original_default_source = None
        self.default_input_enabled = bool(config.get("default_input", True))
        # microphone routing: per-bus sends + noise suppression
        self.mic_ns_enabled = bool(config.get("mic_ns", False))
        self.mic_to_a = bool(config.get("mic_to_a", True))
        self.mic_to_b = bool(config.get("mic_to_b", True))
        config.setdefault("mic_ns", self.mic_ns_enabled)
        config.setdefault("mic_to_a", self.mic_to_a)
        config.setdefault("mic_to_b", self.mic_to_b)
        self.ns_available = any(os.path.exists(p) for p in NS_AEC_PATHS)
        self._last_ns_load_attempt = 0.0
        self._state_cache = None
        self._state_t = 0.0
        self._state_lock = threading.Lock()
        self._tick_lock = threading.Lock()
        self._events = []
        self._event_seq = 0
        self._last_sinks = None
        self._last_sources = None
        self._last_hdmi_attempt = 0.0
        self._supervisor = None
        self._stop = threading.Event()

    # -- startup / shutdown ------------------------------------------------

    def startup(self):
        # Capture the user's real defaults *before* we take over.  If a previous
        # run crashed, the current default may still point at one of our virtual
        # nodes; fall back to the last known real device from config.json.
        cur_sink = get_default_sink()
        cur_source = get_default_source()

        self.graph.ensure_nodes()
        self.ensure_hdmi_usb_sink()

        real_sinks = list_real_sinks()
        real_names = {s["name"] for s in real_sinks}
        real_mics = list_real_mics()
        mic_names = {m["name"] for m in real_mics}

        if cur_sink in real_names:
            self.original_default_sink = cur_sink
        else:
            saved = self.config.get("real_sink")
            self.original_default_sink = (saved if saved in real_names
                                          else (real_sinks[0]["name"] if real_sinks else None))

        if cur_source in mic_names:
            self.original_default_source = cur_source
        else:
            saved = self.config.get("real_source")
            self.original_default_source = (saved if saved in mic_names
                                            else (real_mics[0]["name"] if real_mics else None))

        if self.original_default_sink:
            self.config["real_sink"] = self.original_default_sink
        if self.original_default_source:
            self.config["real_source"] = self.original_default_source

        mic = self.config.get("mic")
        if mic not in mic_names:
            mic = self.original_default_source
        if mic not in mic_names:
            mic = real_mics[0]["name"] if real_mics else None
        self.graph.mic_device = mic

        bus_a = self.config.get("busA")
        if bus_a not in real_names:
            bus_a = self.original_default_sink
        if bus_a not in real_names:
            bus_a = real_sinks[0]["name"] if real_sinks else None
        self.graph.bus_devices["A"] = bus_a

        bus_b = self.config.get("busB")
        if bus_b not in real_names or bus_b == bus_a:
            bus_b = next((s["name"] for s in real_sinks if s["name"] != bus_a), None)
        self.graph.bus_devices["B"] = bus_b

        self.graph.apply_all(self.mic_to_a, self.mic_to_b)
        save_config(self.config)

        # desktop audio lands in VM-System; new apps read the mixed mic bus
        set_default_sink(VM["system"])
        if self.default_input_enabled and find_mic_monitor_source():
            set_default_source(MIC_MONITOR)

        self.meters.ensure(self.graph.meter_targets())

        self._supervisor = threading.Thread(target=self._supervise, daemon=True)
        self._supervisor.start()

    def _supervise(self):
        """Watch for hotplug events and repair the routing automatically."""
        while not self._stop.wait(2.0):
            try:
                self._tick()
            except Exception:
                pass

    def log_event(self, msg):
        with self._state_lock:
            self._event_seq += 1
            self._events.append({"id": self._event_seq,
                                 "t": time.time(), "msg": msg})
            del self._events[:-8]

    # -- microphone processing / routing ------------------------------------

    def _ns_source_ready(self):
        try:
            return any(s.get("name") == "vm_ns" for s in pactl_json("sources"))
        except CmdError:
            return False

    def _ns_module_id(self):
        out = run(["pactl", "list", "modules"]).stdout or ""
        for block in re.split(r"\n(?=Module #)", out):
            m = re.match(r"Module #(\d+)", block)
            if not m:
                continue
            nm = re.search(r"\n\s*Name:\s*(\S+)", block)
            if nm and nm.group(1) == "module-echo-cancel":
                return int(m.group(1))
        return None

    def _load_ns_module(self):
        """Start the WebRTC noise-suppression chain: raw mic -> vm_ns."""
        self._last_ns_load_attempt = time.monotonic()
        mic = self.graph.mic_device
        if not mic:
            raise CmdError("no microphone selected")
        p = run(["pactl", "load-module", "module-echo-cancel",
                 "source_master=%s" % mic,
                 "source_name=vm_ns",
                 "sink_name=vm_ns_echo",
                 "sink_master=%s" % VM["system"],
                 "aec_method=webrtc"])
        if p.returncode != 0:
            raise CmdError("could not start noise suppression: %s"
                           % ((p.stderr or "").strip() or "module load failed"))
        invalidate_dump()

    def _reload_ns_module(self):
        mid = self._ns_module_id()
        if mid is not None:
            run(["pactl", "unload-module", str(mid)])
        self._last_ns_load_attempt = 0.0
        self._load_ns_module()

    def _apply_mic_input(self, want_ns=None, mic=None):
        """Exactly one input feeds the mic bus: the raw mic, or the
        noise-suppressed vm_ns source when noise suppression is on."""
        if want_ns is None:
            want_ns = self.mic_ns_enabled and self._ns_source_ready()
        if mic is None:
            mic = self.graph.mic_device
        with self.graph.lock:
            self.graph.remove_links_into(VM["miclink"])
            if not want_ns:
                self.graph.link_mic(mic)
                return
            outs = [p for p in self.graph.out_ports("vm_ns")
                    if ":capture" in p]
            bus = VM["miclink"]
            fl, fr = "%s:playback_FL" % bus, "%s:playback_FR" % bus
            if any(p.endswith(":capture_FL") for p in outs) and \
               any(p.endswith(":capture_FR") for p in outs):
                self.graph.connect("vm_ns:capture_FL", fl)
                self.graph.connect("vm_ns:capture_FR", fr)
            elif outs:
                self.graph.connect(outs[0], fl)
                self.graph.connect(outs[0], fr)

    def _mic_input_matches(self, want_ns, mic):
        links = self.graph.links()
        ns_in = any(o.startswith("vm_ns:capture") and
                    i.startswith("vm_miclink:playback") for o, i in links)
        raw_in = bool(mic) and any(o.startswith(mic + ":capture") and
                                   i.startswith("vm_miclink:playback")
                                   for o, i in links)
        if want_ns:
            return ns_in and not raw_in
        return raw_in and not ns_in

    def _ensure_mic_input(self, ns_present):
        """Re-link the mic input (raw or noise-suppressed) if it drifted."""
        mic = self.graph.mic_device
        want_ns = self.mic_ns_enabled and ns_present
        if not want_ns:
            if not (mic and mic in (self._last_sources or set())):
                return False
        try:
            if self._mic_input_matches(want_ns, mic):
                return False
            self._apply_mic_input(want_ns, mic)
            if want_ns:
                self.log_event("mic input is now noise-suppressed")
            else:
                self.log_event("re-connected microphone (%s)" % mic)
            return True
        except CmdError:
            return False

    def _bus_linked(self, bus, dev):
        for o, i in self.graph.links():
            if o.startswith(bus + ":monitor_") and i.startswith(dev + ":playback"):
                return True
        return False

    def _defaults_drifted(self):
        if get_default_sink() != VM["system"]:
            return True
        if (self.default_input_enabled and find_mic_monitor_source()
                and get_default_source() != MIC_MONITOR):
            return True
        return False

    def reassert_defaults(self):
        set_default_sink(VM["system"])
        if self.default_input_enabled and find_mic_monitor_source():
            set_default_source(MIC_MONITOR)

    def ensure_hdmi_usb_sink(self):
        """Expose the GPU's second HDMI output (the "HDMI TO USB" path on this
        machine) as a standalone sink so it can be picked as a bus device.

        The Nvidia HDA card maps PCM device <idx>,7 to output hdmi-output-1
        (pin 0x5 = the HDMI-to-USB adapter).  Best effort and idempotent: if
        the sink or the PCM is missing, nothing happens.
        """
        try:
            names = {s["name"] for s in list_real_sinks()}
            if "hdmi_to_usb" in names:
                return
            idx = None
            with open("/proc/asound/cards") as fh:
                for line in fh:
                    if "NVidia" in line or "pci-0000_01_00" in line:
                        parts = line.split()
                        if parts:
                            try:
                                idx = int(parts[0])
                            except ValueError:
                                pass
                        if idx is not None:
                            break
            if idx is None:
                return
            if not os.path.exists("/proc/asound/card%d/pcm7p" % idx):
                return
            run(["pactl", "load-module", "module-alsa-sink",
                 "device=hw:%d,7" % idx,
                 "sink_name=hdmi_to_usb",
                 "sink_properties=device.description=HDMI-To-USB"])
        except Exception:
            pass

    def _tick(self):
        """One reconciliation pass: detect changes, re-connect, repair."""
        with self._tick_lock:
            sinks = list_real_sinks()
            mic_sources = list_real_mics()
            sink_names = {s["name"] for s in sinks}
            mic_names = {s["name"] for s in mic_sources}

            # is the noise-suppression module alive?
            ns_present = False
            if self.mic_ns_enabled:
                try:
                    ns_present = any(s.get("name") == "vm_ns"
                                     for s in pactl_json("sources"))
                except CmdError:
                    ns_present = False

            # keep noise suppression running if it was enabled
            if self.mic_ns_enabled and not ns_present and \
               time.monotonic() - self._last_ns_load_attempt > 10.0:
                try:
                    self._load_ns_module()
                    ns_present = True
                    self.log_event("restarted mic noise suppression")
                except CmdError:
                    pass

            # log hotplug transitions
            if self._last_sinks is not None:
                for s in sinks:
                    if s["name"] not in self._last_sinks:
                        self.log_event("output device detected: %s"
                                       % s["description"])
                for name in self._last_sinks - sink_names:
                    self.log_event("output device removed: %s" % name)
                for s in mic_sources:
                    if s["name"] not in self._last_sources:
                        self.log_event("input device detected: %s"
                                       % s["description"])
                for name in self._last_sources - mic_names:
                    self.log_event("input device removed: %s" % name)
            self._last_sinks = set(sink_names)
            self._last_sources = set(mic_names)

            # keep the second HDMI output ("HDMI To USB") available
            if "hdmi_to_usb" not in sink_names and \
               time.monotonic() - self._last_hdmi_attempt > 10.0:
                self._last_hdmi_attempt = time.monotonic()
                self.ensure_hdmi_usb_sink()

            self.graph.refresh_ids()

            # our virtual nodes vanished (crashed run / manual unload)?
            modules = find_null_sink_modules()
            if any(name not in modules for name in VM.values()):
                self.log_event("recreating the virtual audio graph")
                self.graph.ensure_nodes()
                self.graph.apply_all(self.mic_to_a, self.mic_to_b)
                try:
                    self._apply_mic_input()
                except CmdError:
                    pass
                self.reassert_defaults()
                self.meters.ensure(self.graph.meter_targets())
                self.invalidate_state()
                return

            healed = False

            # mic input: raw mic, or the noise-suppressed vm_ns source
            if self._ensure_mic_input(ns_present):
                healed = True

            # output device reconnected?
            for key, label in (("a", "A"), ("b", "B")):
                dev = self.graph.bus_devices.get(label)
                if dev and dev in sink_names and not self._bus_linked(VM[key], dev):
                    try:
                        self.graph.link_device(key, dev)
                        self.log_event("re-connected output bus %s (%s)"
                                       % (label, dev))
                        healed = True
                    except CmdError:
                        pass

            # per-bus microphone send toggles (mic -> A / mic -> B)
            for key, label in (("a", "A"), ("b", "B")):
                enabled = self.mic_to_a if label == "A" else self.mic_to_b
                has = any(
                    o.startswith(VM["miclink"] + ":monitor_") and
                    i.startswith(VM[key] + ":playback")
                    for o, i in self.graph.links())
                try:
                    if enabled and not has:
                        self._apply_mic_send(label, True)
                        healed = True
                    elif not enabled and has:
                        self._apply_mic_send(label, False)
                        healed = True
                except CmdError:
                    pass

            # desktop audio must keep flowing through the buses
            if self._defaults_drifted():
                try:
                    self.reassert_defaults()
                    healed = True
                except CmdError:
                    pass

            self.meters.ensure(self.graph.meter_targets())
            if healed:
                self.invalidate_state()

    def shutdown(self):
        self._stop.set()
        self.meters.stop_all()
        try:
            modules = find_null_sink_modules()
            for name in VM.values():
                mid = modules.get(name)
                if mid is not None:
                    run(["pactl", "unload-module", str(mid)])
            unload_echo_cancel_module()
            invalidate_dump()
        except Exception:
            pass
        try:
            sinks = {s["name"] for s in list_real_sinks()}
            if self.original_default_sink in sinks:
                set_default_sink(self.original_default_sink)
        except Exception:
            pass
        try:
            mics = {m["name"] for m in list_real_mics()}
            if self.original_default_source in mics:
                set_default_source(self.original_default_source)
        except Exception:
            pass

    # -- state -------------------------------------------------------------

    def invalidate_state(self):
        with self._state_lock:
            self._state_cache = None

    def state(self, fresh=False):
        with self._state_lock:
            now = time.monotonic()
            if fresh or self._state_cache is None or now - self._state_t > 0.8:
                self._state_cache = self._build_state()
                self._state_t = now
            cached = self._state_cache
        result = dict(cached)
        result["meters"] = self.meters.snapshot()
        result["default_sink"] = get_default_sink()
        return result

    def _build_state(self):
        sinks = pactl_json("sinks")
        sink_by_name = {s.get("name"): s for s in sinks}
        mics = list_real_mics()

        def strip(node_name):
            s = sink_by_name.get(node_name, {})
            return {
                "node_name": node_name,
                "volume": pct_from_volume(s.get("volume")),
                "mute": bool(s.get("mute")),
                "exists": bool(s),
            }

        return {
            "devices": [s for s in list_real_sinks()],
            "mics": mics,
            "mic": dict(strip(VM["miclink"]),
                        device=self.graph.mic_device),
            "system": strip(VM["system"]),
            "buses": {
                "A": dict(strip(VM["a"]), device=self.graph.bus_devices.get("A")),
                "B": dict(strip(VM["b"]), device=self.graph.bus_devices.get("B")),
            },
            "mic_monitor": MIC_MONITOR,
            "mic_monitor_label": "Monitor of VM-Mic",
            "default_input_enabled": self.default_input_enabled,
            "default_source": get_default_source(),
            "auto_repair": True,
            "mic_ns": self.mic_ns_enabled,
            "ns_available": self.ns_available,
            "mic_to_a": self.mic_to_a,
            "mic_to_b": self.mic_to_b,
            "build": build_id(),
            "app_version": APP_VERSION,
            "events": list(self._events),
        }

    # -- actions -----------------------------------------------------------

    def set_gain(self, node_name, body):
        if body.get("volume") is not None:
            pct = max(0.0, min(200.0, float(body["volume"])))
            run_ok(["pactl", "set-sink-volume", node_name, "%g%%" % pct])
        if body.get("mute") is not None:
            run_ok(["pactl", "set-sink-mute", node_name,
                    "1" if body["mute"] else "0"])
        self.invalidate_state()

    def set_mic(self, device):
        if device not in {m["name"] for m in list_real_mics()}:
            raise CmdError("unknown input device: %r" % device)
        self.graph.mic_device = device
        if self.mic_ns_enabled:
            try:
                self._reload_ns_module()
            except CmdError:
                self.mic_ns_enabled = False
                self.config["mic_ns"] = False
                save_config(self.config)
        self._apply_mic_input()
        self.config["mic"] = device
        save_config(self.config)
        self.invalidate_state()

    def set_mic_ns(self, enabled):
        """Toggle WebRTC noise suppression on the mic input."""
        enabled = bool(enabled)
        if enabled and not self._ns_source_ready():
            try:
                self._load_ns_module()
            except CmdError:
                self.mic_ns_enabled = False
                self.config["mic_ns"] = False
                save_config(self.config)
                self.invalidate_state()
                raise
        elif not enabled:
            mid = self._ns_module_id()
            if mid is not None:
                run(["pactl", "unload-module", str(mid)])
        self.mic_ns_enabled = enabled
        self.config["mic_ns"] = enabled
        save_config(self.config)
        # always re-apply so the mic input matches the requested mode
        self._apply_mic_input()
        self.invalidate_state()

    def set_mic_send(self, bus, enabled):
        """Turn the microphone feed to a bus on or off (VoiceMeeter B1/B2)."""
        key = (bus or "").upper()
        if key not in ("A", "B"):
            raise CmdError("bus must be A or B")
        enabled = bool(enabled)
        attr = "mic_to_a" if key == "A" else "mic_to_b"
        setattr(self, attr, enabled)
        self.config["mic_to_%s" % key.lower()] = enabled
        save_config(self.config)
        # always re-apply: an enable must create the links immediately, even
        # if the flag already matched (e.g. links were dropped meanwhile)
        self._apply_mic_send(key, enabled)
        self.invalidate_state()

    def _apply_mic_send(self, key, enabled):
        bus = VM[key.lower()]
        with self.graph.lock:
            if enabled:
                self.graph.connect("%s:monitor_FL" % VM["miclink"],
                                   "%s:playback_FL" % bus)
                self.graph.connect("%s:monitor_FR" % VM["miclink"],
                                   "%s:playback_FR" % bus)
            else:
                for o, i in list(self.graph.links(fresh=True)):
                    if o.startswith(VM["miclink"] + ":monitor_") and \
                       i.startswith(bus + ":playback"):
                        self.graph.disconnect(o, i)

    def set_bus_device(self, bus, device):
        bus = (bus or "").upper()
        if bus not in ("A", "B"):
            raise CmdError("bus must be A or B")
        if device and device not in {s["name"] for s in list_real_sinks()}:
            raise CmdError("unknown output device: %r" % device)
        self.graph.bus_devices[bus] = device
        self.graph.link_device(bus.lower(), device)
        self.config["bus" + bus] = device
        save_config(self.config)
        self.invalidate_state()

    def set_default_input(self, enabled):
        self.default_input_enabled = bool(enabled)
        self.config["default_input"] = self.default_input_enabled
        save_config(self.config)
        if self.default_input_enabled and find_mic_monitor_source():
            set_default_source(MIC_MONITOR)
        elif self.original_default_source:
            set_default_source(self.original_default_source)
        self.invalidate_state()

    def move_streams(self, move_inputs=False):
        moved = 0
        for si in pactl_json("sink-inputs"):
            idx = si.get("index")
            if idx is None:
                continue
            run(["pactl", "move-sink-input", str(idx), VM["system"]])
            moved += 1
        inputs = 0
        if move_inputs and find_mic_monitor_source():
            for so in pactl_json("source-outputs"):
                idx = so.get("index")
                if idx is None:
                    continue
                if is_meter_stream(so.get("properties") or {}):
                    continue
                run(["pactl", "move-source-output", str(idx), MIC_MONITOR])
                inputs += 1
        self.invalidate_state()
        return {"moved_outputs": moved, "moved_inputs": inputs}

    def handle_action(self, path, body):
        if path == "/api/mic/device":
            self.set_mic(body.get("device"))
        elif path == "/api/mic/gain":
            self.set_gain(VM["miclink"], body)
        elif path == "/api/system/gain":
            self.set_gain(VM["system"], body)
        elif path == "/api/bus/device":
            self.set_bus_device(body.get("bus"), body.get("device"))
        elif path == "/api/bus/gain":
            bus = (body.get("bus") or "").upper()
            if bus not in ("A", "B"):
                raise CmdError("bus must be A or B")
            self.set_gain(VM[bus.lower()], body)
        elif path == "/api/default-input":
            self.set_default_input(body.get("enabled"))
        elif path == "/api/mic/ns":
            self.set_mic_ns(body.get("enabled"))
        elif path == "/api/mic/send":
            self.set_mic_send(body.get("bus"), body.get("enabled"))
        elif path == "/api/move-streams":
            return self.move_streams(bool(body.get("inputs")))
        elif path == "/api/refresh":
            self._tick()
        else:
            raise CmdError("unknown endpoint: %s" % path)
        return {"state": self.state(fresh=True)}

    # -- diagnostics -------------------------------------------------------

    def diagnostics(self):
        sources = pactl_json("sources")
        by_index = {s.get("index"): s for s in sources}
        hardware, virtual, other_monitor = [], [], []
        for so in pactl_json("source-outputs"):
            props = so.get("properties") or {}
            # ignore our own meter capture streams
            if is_meter_stream(props):
                continue
            src = by_index.get(so.get("source"), {})
            sname = src.get("name", "?")
            app = (props.get("application.name")
                   or props.get("application.process.binary")
                   or "Unknown app")
            entry = {"app": app, "source": sname, "index": so.get("index")}
            if sname == MIC_MONITOR:
                virtual.append(entry)
            elif sname.endswith(".monitor"):
                other_monitor.append(entry)
            else:
                hardware.append(entry)

        default_source = get_default_source()
        default_is_virtual = default_source == MIC_MONITOR
        default_is_hardware = (
            default_source not in ("", MIC_MONITOR)
            and not default_source.endswith(".monitor"))

        return {
            "vm_mic_monitor": MIC_MONITOR,
            "vm_mic_label": "Monitor of VM-Mic",
            "hardware_users": hardware,
            "virtual_users": virtual,
            "other_monitor_users": other_monitor,
            "default_source": default_source,
            "default_is_virtual": default_is_virtual,
            "default_is_hardware": default_is_hardware,
            "default_input_enabled": self.default_input_enabled,
            "mic_device": self.graph.mic_device,
        }


# --------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    server_version = "VUMixer/1.0"
    protocol_version = "HTTP/1.1"

    @property
    def app(self):
        return self.server.app

    def log_message(self, fmt, *args):
        if getattr(self.server, "verbose", False):
            sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    def _log(self, msg):
        sys.stderr.write("%s\n" % msg)
        sys.stderr.flush()

    # -- helpers -----------------------------------------------------------

    def _send_json(self, obj, status=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _fail(self, msg, status=400):
        self._send_json({"ok": False, "error": str(msg)}, status)

    def _read_body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            data = json.loads(raw.decode("utf-8") or "{}")
        except (json.JSONDecodeError, UnicodeDecodeError):
            return {}
        return data if isinstance(data, dict) else {}

    # -- routes ------------------------------------------------------------

    def do_GET(self):
        path = urlparse(self.path).path
        if path != "/api/meters":
            self._log("GET %s" % path)
        if path == "/api/state":
            try:
                self._send_json({"ok": True, **self.app.state()})
            except Exception as e:  # noqa: BLE001
                self._fail(e, 500)
            return
        if path == "/api/meters":
            self._send_json({"ok": True, "meters": self.app.meters.snapshot()})
            return
        if path == "/api/build":
            self._send_json({"ok": True, "build": build_id(),
                             "version": APP_VERSION})
            return
        if path == "/api/diagnose":
            try:
                self._send_json({"ok": True,
                                 "diagnostics": self.app.diagnostics()})
            except Exception as e:  # noqa: BLE001
                self._fail(e, 500)
            return
        self._serve_static(path)

    def do_POST(self):
        path = urlparse(self.path).path
        body = self._read_body()
        self._log("POST %s %s" % (path, json.dumps(body)))
        try:
            result = self.app.handle_action(path, body)
        except CmdError as e:
            self._fail(e, 400)
        except Exception as e:  # noqa: BLE001
            self._fail(e, 500)
        else:
            self._send_json({"ok": True, **(result or {})})

    def _serve_static(self, path):
        if path in ("", "/"):
            path = "/index.html"
        rel = path.lstrip("/")
        full = os.path.normpath(os.path.join(STATIC_DIR, rel))
        if not (full == STATIC_DIR or full.startswith(STATIC_DIR + os.sep)):
            self.send_error(404)
            return
        if not os.path.isfile(full):
            self.send_error(404)
            return
        ctype = MIME.get(os.path.splitext(full)[1].lower(),
                         "application/octet-stream")
        try:
            with open(full, "rb") as f:
                data = f.read()
        except OSError:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def check_dependencies():
    missing = [b for b in ("pactl", "pw-link", "pw-dump") if not shutil.which(b)]
    if missing:
        raise CmdError(
            "missing required tools: %s\n"
            "Install them with: sudo apt install pipewire-bin pulseaudio-utils"
            % ", ".join(missing))
    if not os.path.exists(VIMETER):
        print("warning: meter helper %s not found - VU meters will be empty.\n"
              "         build it with ./build_meter.sh" % VIMETER,
              file=sys.stderr)


def cleanup_only():
    modules = find_null_sink_modules()
    removed = 0
    for name in VM.values():
        mid = modules.get(name)
        if mid is not None:
            run(["pactl", "unload-module", str(mid)])
            removed += 1
    if unload_echo_cancel_module():
        removed += 1
    invalidate_dump()
    # drop any leftover links that pointed at the (now gone) virtual nodes
    for o, i in Graph._read_links():
        if o.startswith("vm_") or i.startswith("vm_"):
            run(["pw-link", "-d", o, i])
    print("removed %d virtual sink(s)." % removed)
    return 0


def main(argv=None):
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except (AttributeError, ValueError):
        pass
    ap = argparse.ArgumentParser(description="VoiceMeeter-style mixer for PipeWire")
    ap.add_argument("--host", default="127.0.0.1",
                    help="address to bind (default: 127.0.0.1)")
    ap.add_argument("--port", type=int, default=8777,
                    help="port to listen on (default: 8777)")
    ap.add_argument("--no-browser", action="store_true",
                    help="do not open a browser window")
    ap.add_argument("--cleanup", action="store_true",
                    help="remove the virtual sinks and exit")
    ap.add_argument("--verbose", action="store_true",
                    help="log every HTTP request")
    args = ap.parse_args(argv)

    try:
        check_dependencies()
    except CmdError as e:
        print("error: %s" % e, file=sys.stderr)
        return 1

    if args.cleanup:
        try:
            return cleanup_only()
        except CmdError as e:
            print("error: %s" % e, file=sys.stderr)
            return 1

    running = read_pidfile()
    if pid_alive(running):
        print("error: VU Mixer is already running (pid %d).\n"
              "       stop it with ./stop.sh" % running, file=sys.stderr)
        return 1
    remove_pidfile()

    app = App(load_config())
    try:
        app.startup()
    except CmdError as e:
        print("error: could not set up the audio graph: %s" % e, file=sys.stderr)
        return 1

    try:
        httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    except OSError as e:
        print("error: cannot listen on %s:%d: %s" % (args.host, args.port, e),
              file=sys.stderr)
        app.shutdown()
        return 1
    httpd.app = app
    httpd.verbose = args.verbose
    httpd.daemon_threads = True

    url = "http://%s:%d/" % (args.host, args.port)
    write_pidfile(os.getpid())
    print("VU Mixer is running at %s" % url)
    print("  mic bus:   %s" % MIC_MONITOR)
    print("  press Ctrl-C to stop and remove the virtual audio graph")

    def on_signal(signum, frame):
        threading.Thread(target=httpd.shutdown, daemon=True).start()

    signal.signal(signal.SIGINT, on_signal)
    signal.signal(signal.SIGTERM, on_signal)

    if not args.no_browser:
        threading.Thread(target=_open_browser, args=(url,), daemon=True).start()

    try:
        httpd.serve_forever()
    finally:
        httpd.server_close()
        remove_pidfile()
        app.shutdown()
        print("\nstopped; virtual audio graph removed.")
    return 0


def _open_browser(url):
    try:
        webbrowser.open(url)
    except Exception:  # noqa: BLE001
        pass


if __name__ == "__main__":
    sys.exit(main())
