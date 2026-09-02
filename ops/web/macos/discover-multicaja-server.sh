#!/usr/bin/env bash
# Descubre la API multicaja en la LAN (puerto 7279). Compatible con macOS 10.15+.
set -euo pipefail
TIMEOUT_MS="${1:-6000}"
PORT=7279

probe_host() {
  local host="$1"
  curl -fsS --max-time 2 "http://${host}:${PORT}/health/live" >/dev/null 2>&1
}

# IP local para acotar subred
local_ip="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
if [[ -z "$local_ip" ]]; then
  local_ip="$(ifconfig | awk '/inet / && $2 != "127.0.0.1" {print $2; exit}')"
fi

if [[ -n "$local_ip" ]]; then
  prefix="${local_ip%.*}"
  for last in $(seq 1 254); do
  host="${prefix}.${last}"
  [[ "$host" == "$local_ip" ]] && continue
  probe_host "$host" && echo "http://${host}:${PORT}/" && exit 0
  done
fi

probe_host "127.0.0.1" && echo "http://127.0.0.1:${PORT}/" && exit 0
exit 1
