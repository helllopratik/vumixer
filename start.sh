#!/usr/bin/env bash
#
# Start VU Mixer.  Any extra arguments are passed to server.py
# (e.g. ./start.sh --port 9000 --no-browser).
#
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"

if [ ! -x "$DIR/vimeter" ]; then
    echo "==> meter helper missing, building it"
    "$DIR/build_meter.sh"
fi

exec python3 "$DIR/server.py" "$@"
