#!/usr/bin/env bash
#
# Build the tiny PipeWire level-meter helper (vimeter) used for the VU meters.
#
# Tries, in order:
#   1. pkg-config (when the *-dev packages are installed system-wide)
#   2. headers extracted from .deb files into /tmp/pwdev  (no root required)
#
set -euo pipefail
cd "$(dirname "$0")"

SRC=vimeter.c
OUT=vimeter

echo "==> building $OUT"

if pkg-config --exists libpipewire-0.3 2>/dev/null; then
    gcc "$SRC" -o "$OUT" $(pkg-config --cflags --libs libpipewire-0.3) -lm
    echo "==> OK (pkg-config)"
    exit 0
fi

# ---- fallback: get dev headers without root -------------------------------
INC=/tmp/pwdev/usr/include
if [ ! -f "$INC/pipewire-0.3/pipewire/pipewire.h" ]; then
    echo "==> libpipewire dev headers not found; downloading them (no root needed)"
    rm -rf /tmp/pwdev
    mkdir -p /tmp/pwdev/debs /tmp/pwdev/root
    (
        cd /tmp/pwdev/debs
        apt-get download libpipewire-0.3-dev libspa-0.2-dev
    )
    for deb in /tmp/pwdev/debs/*.deb; do
        dpkg-deb -x "$deb" /tmp/pwdev/root
    done
fi

LIB=$(ldconfig -p | awk '/libpipewire-0\.3\.so/{print $NF; exit}')
if [ -z "$LIB" ]; then
    echo "error: libpipewire-0.3 runtime library not found" >&2
    exit 1
fi

gcc "$SRC" -o "$OUT" \
    -I"$INC/pipewire-0.3" \
    -I"$INC/spa-0.2" \
    "$LIB" -lm
echo "==> OK (extracted headers, linked $LIB)"
