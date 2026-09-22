#!/usr/bin/env bash
#
# Stop a running VU Mixer and make sure the virtual audio graph is gone.
#
set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
PIDFILE="$DIR/vumixer.pid"

if [ -f "$PIDFILE" ]; then
    PID="$(cat "$PIDFILE" 2>/dev/null || true)"
    if [ -n "${PID:-}" ] && kill -0 "$PID" 2>/dev/null; then
        echo "==> stopping VU Mixer (pid $PID)"
        kill "$PID" 2>/dev/null
        for _ in $(seq 1 20); do
            kill -0 "$PID" 2>/dev/null || break
            sleep 0.1
        done
        kill -0 "$PID" 2>/dev/null && kill -9 "$PID" 2>/dev/null
    fi
    rm -f "$PIDFILE"
else
    echo "==> no pid file; nothing seems to be running"
fi

# belt and braces: drop any leftover virtual nodes/links
python3 "$DIR/server.py" --cleanup
