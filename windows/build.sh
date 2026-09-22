#!/usr/bin/env bash
#
# Build the Windows VU Mixer (self-contained single-file exe) from any OS.
#
set -euo pipefail
cd "$(dirname "$0")"

find_dotnet() {
  if [ -n "${DOTNET:-}" ]; then echo "$DOTNET"; return; fi
  if command -v dotnet >/dev/null 2>&1; then echo "$(command -v dotnet)"; return; fi
  for c in "$HOME/.dotnet/dotnet" /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet; do
    [ -x "$c" ] && { echo "$c"; return; }
  done
  return 1
}

DOTNET="$(find_dotnet)" || { echo "error: dotnet SDK 8 not found. Install it or set DOTNET=/path/to/dotnet" >&2; exit 1; }

echo "==> dotnet: $DOTNET"
"$DOTNET" publish src/VUMixer -c Release -r win-x64 --self-contained true -o publish

echo
echo "==> done: $(pwd)/publish/VUMixer.exe"
echo "    copy that single file to any Windows 10/11 machine and double-click it."