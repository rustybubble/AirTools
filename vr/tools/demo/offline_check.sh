#!/usr/bin/env bash
# Would the demo survive OFFLINE=true right now? Checks it without touching the running :8000 server:
# snapshots the backend's data/ dir, starts a second OFFLINE=true instance on 127.0.0.1:8002 against the
# snapshot (so its writes, e.g. part.json refits, orders, never reach the real data dir), runs
# tools/demo/parts_check.py (the six chips with and without a tape, first-candidate files + sellers, typed
# agent, voice 503, one offline checkout), then stops it and deletes the snapshot. No network, no LLM calls.
#
#   tools/demo/offline_check.sh                 # extra args go to parts_check.py, e.g. --queries "cabinet hinge"
# Env: BACKEND_DIR (default ~/airtools-drone-backend), CHECK_PORT (8002)
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BACKEND_DIR="${BACKEND_DIR:-$HOME/airtools-drone-backend}"
PORT="${CHECK_PORT:-8002}"

if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "port $PORT is busy; set CHECK_PORT to a free one"; exit 2
fi
tmp="$(mktemp -d -t airtools-offline)"
cp -a "$BACKEND_DIR/data" "$tmp/data" || { echo "can't copy $BACKEND_DIR/data"; exit 2; }
log="$tmp/server.log"
uvrun=(uv run); [ -d "$BACKEND_DIR/.venv" ] && uvrun=(uv run --no-sync)
( cd "$BACKEND_DIR" || exit 1
  exec env OFFLINE=true DATA_DIR="$tmp/data" "${uvrun[@]}" uvicorn server.app:app --host 127.0.0.1 --port "$PORT" ) >"$log" 2>&1 </dev/null &
pid=$!
cleanup() { kill "$pid" 2>/dev/null; sleep 1; rm -rf "$tmp"; }
trap cleanup EXIT
for _ in $(seq 1 40); do
  curl -s -m 1 "http://127.0.0.1:$PORT/health" 2>/dev/null | grep -q '"ok":true' && break
  sleep 0.5
done
if ! curl -s -m 2 "http://127.0.0.1:$PORT/health" | grep -q '"ok":true'; then
  echo "offline instance didn't start:"; cat "$log"; exit 2
fi
echo "OFFLINE=true instance on :$PORT (pid $pid) over a snapshot of $BACKEND_DIR/data"
python3 "$ROOT/tools/demo/parts_check.py" --base "http://127.0.0.1:$PORT" --tapes none,demo --sellers --extras "$@"
