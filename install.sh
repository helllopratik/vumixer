#!/usr/bin/env bash
#
# VU Mixer — one-click installer.
#
# Installs everything needed to run VU Mixer:
#   * the PipeWire tools the app drives (pactl, pw-link, pw-dump)
#   * the WebRTC noise-suppression plugin used by the NS button
#   * a C compiler + PipeWire dev headers (to build the tiny VU meter helper)
# ...then builds the meter helper and runs a 10-second self-test.
#
# No sudo is needed to *run* VU Mixer; the installer only asks for a password
# when it actually has to install distro packages.
#
# Usage:
#   git clone https://github.com/helllopratik/vumixer.git
#   cd vumixer
#   ./install.sh
#
# Or as a one-liner:
#   git clone https://github.com/helllopratik/vumixer.git && cd vumixer && ./install.sh
#
set -euo pipefail
cd "$(dirname "$0")"

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
ok()  { printf '\033[1;32m    ok:\033[0m %s\n' "$*"; }
warn(){ printf '\033[1;33m  warn:\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m  error:\033[0m %s\n' "$*" >&2; exit 1; }

SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo"

# --------------------------------------------------------------------------
# 1. basic tools
# --------------------------------------------------------------------------
say "Basic tools"
for t in python3 git curl; do
  if command -v "$t" >/dev/null 2>&1; then ok "$t — $(command -v "$t")"
  else die "$t is required but not installed."
  fi
done

# --------------------------------------------------------------------------
# 2. is the PipeWire stack alive?
# --------------------------------------------------------------------------
say "Audio stack"
pgrep -x pipewire       >/dev/null 2>&1 && ok "pipewire is running"       || warn "pipewire is NOT running"
pgrep -x wireplumber    >/dev/null 2>&1 && ok "wireplumber is running"    || warn "wireplumber is NOT running"
pgrep -x pipewire-pulse >/dev/null 2>&1 && ok "pipewire-pulse is running" || warn "pipewire-pulse is NOT running"

# --------------------------------------------------------------------------
# 3. distro-specific packages
# --------------------------------------------------------------------------
PM=""
if   command -v apt-get >/dev/null 2>&1; then PM=apt
elif command -v dnf     >/dev/null 2>&1; then PM=dnf
elif command -v pacman  >/dev/null 2>&1; then PM=pacman
elif command -v zypper  >/dev/null 2>&1; then PM=zypper
else die "no supported package manager found (apt / dnf / pacman / zypper).\n       Install the packages listed in the README manually."
fi
ok "package manager: $PM"

# tool -> owning package, per distro
tool_pkg() {
  case "$PM" in
    apt)    case "$1" in
              pactl)     echo "pulseaudio-utils" ;;
              pw-link|pw-dump) echo "pipewire-bin" ;;
              gcc)       echo "gcc" ;;
              pkg-config) echo "pkg-config" ;;
            esac ;;
    dnf)    case "$1" in
              pactl)     echo "pulseaudio-utils" ;;
              pw-link|pw-dump) echo "pipewire-utils" ;;
              gcc)       echo "gcc" ;;
              pkg-config) echo "pkgconf-pkg-config" ;;
            esac ;;
    pacman) case "$1" in
              pactl)     echo "libpulse" ;;
              pw-link|pw-dump) echo "pipewire" ;;
              gcc)       echo "gcc" ;;
              pkg-config) echo "pkg-config" ;;
            esac ;;
    zypper) case "$1" in
              pactl)     echo "pulseaudio-utils" ;;
              pw-link|pw-dump) echo "pipewire-utils" ;;
              gcc)       echo "gcc" ;;
              pkg-config) echo "pkgconf" ;;
            esac ;;
  esac
}

pkg_installed() {
  case "$PM" in
    apt)      dpkg -s "$1" >/dev/null 2>&1 ;;
    dnf|zypper) rpm -q "$1" >/dev/null 2>&1 ;;
    pacman)   pacman -Q "$1" >/dev/null 2>&1 ;;
  esac
}

NEED=()
for t in pactl pw-link pw-dump gcc pkg-config; do
  if ! command -v "$t" >/dev/null 2>&1; then
    p="$(tool_pkg "$t")"
    [ -n "$p" ] && NEED+=("$p") || warn "no package mapping for '$t' — install it manually"
  fi
done

# WebRTC noise-suppression plugin.  The app works without it (the NS button
# is simply hidden), but it is the one genuinely useful piece of DSP.
case "$PM" in
  apt)    pkg_installed libspa-0.2-modules || NEED+=(libspa-0.2-modules) ;;
  dnf)    pkg_installed pipewire-libs      || NEED+=(pipewire-libs) ;;
  pacman) : ;;   # spa plugins ship inside the pipewire package on Arch
  zypper) pkg_installed libspa-0_2-0       || NEED+=(libspa-0_2-0) ;;
esac

# dev headers so the meter helper builds cleanly via pkg-config
case "$PM" in
  apt)    for p in libpipewire-0.3-dev libspa-0.2-dev; do
            pkg_installed "$p" || NEED+=("$p")
          done ;;
  dnf)    pkg_installed pipewire-devel || NEED+=(pipewire-devel) ;;
  pacman) : ;;   # pipewire headers are part of the pipewire package
  zypper) pkg_installed pipewire-devel || NEED+=(pipewire-devel) ;;
esac

if [ "${#NEED[@]}" -gt 0 ]; then
  say "Installing: ${NEED[*]}"
  case "$PM" in
    apt)    $SUDO apt-get update && $SUDO apt-get install -y "${NEED[@]}" ;;
    dnf)    $SUDO dnf install -y "${NEED[@]}" ;;
    pacman) $SUDO pacman -S --needed --noconfirm "${NEED[@]}" ;;
    zypper) $SUDO zypper --non-interactive install "${NEED[@]}" ;;
  esac
  hash -r
else
  ok "all required packages are already installed"
fi

# --------------------------------------------------------------------------
# 4. verify the pieces actually exist now
# --------------------------------------------------------------------------
say "Verifying tools"
for t in pactl pw-link pw-dump gcc; do
  command -v "$t" >/dev/null 2>&1 && ok "$t — $(command -v "$t")" \
    || die "$t is still missing after the install step"
done

NS=0
for f in /usr/lib/x86_64-linux-gnu/spa-0.2/aec/libspa-aec-webrtc.so \
         /usr/lib64/spa-0.2/aec/libspa-aec-webrtc.so \
         /usr/lib/spa-0.2/aec/libspa-aec-webrtc.so; do
  if [ -f "$f" ]; then ok "noise-suppression plugin found ($f)"; NS=1; break; fi
done
[ "$NS" = 1 ] || warn "WebRTC AEC plugin not found — the NS button will be unavailable,\n          the mixer itself still works fine."

# --------------------------------------------------------------------------
# 5. build the VU level-meter helper
# --------------------------------------------------------------------------
say "Building the VU meter helper"
if [ -x ./vimeter ]; then
  ok "vimeter already built"
else
  bash ./build_meter.sh && ok "vimeter built"
fi
[ -x ./vimeter ] || warn "vimeter could not be built — the VU meters will stay empty"

# --------------------------------------------------------------------------
# 6. same dependency check the server runs at startup
# --------------------------------------------------------------------------
python3 - <<'PY'
import os, sys
sys.path.insert(0, os.getcwd())
import server            # noqa: F401  (module-level definitions only)
server.check_dependencies()
print("deps OK")
PY

# --------------------------------------------------------------------------
# 7. live self-test: boot the server and see if /api/state answers
# --------------------------------------------------------------------------
if curl -fsS -m 2 http://127.0.0.1:8777/api/build >/dev/null 2>&1; then
  ok "VU Mixer is already running on this machine — nothing else to do"
else
  say "Quick self-test: starting VU Mixer for a few seconds"
  ./start.sh --no-browser >/tmp/vumixer-install.log 2>&1 &
  SPPID=$!
  answered=0
  for _ in $(seq 1 20); do
    if curl -fsS -m 1 http://127.0.0.1:8777/api/state >/dev/null 2>&1; then
      answered=1
      break
    fi
    sleep 0.5
  done
  if [ "$answered" = 1 ]; then
    ok "self-test passed (/api/state answered)"
    ./stop.sh >/dev/null 2>&1 || true
    ok "test server stopped cleanly — run ./start.sh whenever you want the mixer"
  else
    warn "the self-test server did not answer in time; startup log:"
    sed -n '1,40p' /tmp/vumixer-install.log 2>/dev/null || true
  fi
fi

say "Install complete"
printf '\n  Start it with:   ./start.sh\n  Then open:       http://127.0.0.1:8777\n\n'