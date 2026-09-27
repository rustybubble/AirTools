#!/usr/bin/env bash
# Bring the AirTools demo up (idempotent: safe to re-run at any point). See docs/demo.md.
#
#   tools/demo/up.sh                      # headset + USB tunnel + backend + launch app + stream logcat
#   tools/demo/up.sh -n                   # dry run: run the read-only checks, print every change instead of making it
#   tools/demo/up.sh --offline            # start the backend cache-only (OFFLINE=true) if nothing is on :8000
#   tools/demo/up.sh -i build/AirTools.apk --restart --site kitchen
#   tools/demo/up.sh --wifi               # hotspot: no adb reverse; the app gets -e server http://<this Mac>:8000
#   (D7) every run also starts the presenter relay (tools/presenter, :8766) + adb reverse tcp:8766; page: http://localhost:8766/
#
# Options:
#   -i, --install APK   adb install -r APK before launching
#   --server URL        launch extra: backend base URL (app default on the Quest: http://127.0.0.1:8000 = USB tunnel)
#   --site NAME         launch extra: scene site to load (kitchen, synthetic-facade, ...)
#   --offline           start the backend with OFFLINE=true (only when nothing listens on :8000; never restarts one)
#   --restart           force-stop the app first (recovers a hung app; extras only apply on a fresh start)
#   --wifi              skip adb reverse; default --server to http://<en0 IP>:8000
#   --no-server | --no-launch | --no-logcat   skip that step
#   --no-presenter      (D7) don't start the presenter relay / its adb reverse
#   --demo on|off       (D7) launch extra: DemoMode (default: off; the user disabled it Sat 09-26 evening)
#   -n, --dry-run       print the commands that change something; run only the read-only checks
# Env: BACKEND_DIR (default ~/airtools-drone-backend), PORT (8000), MOCK_PORT (8765), SERIAL (adb serial if several),
#      PRESENTER_PORT (8766, D7)
set -uo pipefail

PKG=com.airtools.quest
ACTIVITY=com.unity3d.player.UnityPlayerGameActivity
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BACKEND_DIR="${BACKEND_DIR:-$HOME/airtools-drone-backend}"
PORT="${PORT:-8000}"
MOCK_PORT="${MOCK_PORT:-8765}"
SPIKE="$ROOT/SpikeData"
STAMP="$(date +%Y%m%d-%H%M%S)"

apk=""; server=""; site=""; offline=false; restart=false; wifi=false
do_server=true; do_launch=true; do_logcat=true; dry=false
PRESENTER_PORT="${PRESENTER_PORT:-8766}"; do_presenter=true; demo=""   # D7
while [ $# -gt 0 ]; do
  case "$1" in
    -i|--install) apk="${2:?-i needs an APK path}"; shift ;;
    --server) server="${2:?--server needs a URL}"; shift ;;
    --site) site="${2:?--site needs a name}"; shift ;;
    --offline) offline=true ;;
    --restart) restart=true ;;
    --wifi) wifi=true ;;
    --no-server) do_server=false ;;
    --no-launch) do_launch=false ;;
    --no-logcat) do_logcat=false ;;
    --no-presenter) do_presenter=false ;;                          # D7
    --demo) demo="${2:?--demo needs on or off}"; shift ;;          # D7
    -n|--dry-run) dry=true ;;
    -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
    *) echo "unknown option: $1 (see --help)"; exit 2 ;;
  esac
  shift
done

ok()   { printf '  \033[32mok\033[0m    %s\n' "$*"; }
warn() { printf '  \033[33mwarn\033[0m  %s\n' "$*"; WARNINGS=$((WARNINGS + 1)); }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILS=$((FAILS + 1)); }
step() { printf '\n\033[1m%s\033[0m\n' "$*"; }
# run CMD...: a change. Printed; executed unless --dry-run.
run() {
  if $dry; then printf '  \033[36mdry\033[0m   %s\n' "$*"; return 0; fi
  printf '  \033[36mrun\033[0m   %s\n' "$*"; "$@"
}
WARNINGS=0; FAILS=0
ADB=(adb); [ -n "${SERIAL:-}" ] && ADB=(adb -s "$SERIAL")
adbq() { "${ADB[@]}" "$@" 2>/dev/null | tr -d '\r'; }   # read-only adb query, CRs stripped
$dry && echo "DRY RUN: read-only checks run for real; changes are only printed."

# ---------------------------------------------------------------- headset
step "1. Headset (adb)"
device_ok=false
if ! command -v adb >/dev/null; then
  fail "adb not found (brew install android-platform-tools)"
else
  states="$(adb devices 2>/dev/null | tr -d '\r' | awk 'NR>1 && NF>=2 {print $1" "$2}')"
  [ -n "${SERIAL:-}" ] && states="$(printf '%s\n' "$states" | awk -v s="$SERIAL" '$1==s')"
  n_dev=$(printf '%s\n' "$states" | grep -c ' device$' || true)
  if printf '%s\n' "$states" | grep -q ' unauthorized$'; then
    fail "headset unauthorized: put it on and accept 'Allow USB debugging' (tick Always allow), then re-run"
  elif printf '%s\n' "$states" | grep -q ' offline$'; then
    fail "headset offline on adb: replug USB-C, or: adb kill-server && adb start-server"
  elif [ "$n_dev" -eq 0 ]; then
    fail "no headset on adb: plug in USB-C (data cable), enable Developer mode, accept the prompt in the headset"
  elif [ "$n_dev" -gt 1 ] && [ -z "${SERIAL:-}" ]; then
    fail "several adb devices; pick one: SERIAL=<serial> $0 ...  ($(printf '%s\n' "$states" | awk '{print $1}' | tr '\n' ' '))"
  else
    device_ok=true
    [ -z "${SERIAL:-}" ] && ADB=(adb -s "$(printf '%s\n' "$states" | awk '$2=="device"{print $1; exit}')")
    model="$(adbq shell getprop ro.product.model)"
    [ "$model" = "Quest 3S" ] && ok "$model (${ADB[2]})" || warn "device is '$model', expected Quest 3S (${ADB[2]})"
    wake="$(adbq shell dumpsys power | grep -m1 -o 'mWakefulness=[A-Za-z]*' | cut -d= -f2)"
    batt="$(adbq shell dumpsys battery | awk -F': ' '/^ *level:/ {print $2; exit}')"
    [ "$wake" = "Awake" ] && ok "awake, battery ${batt:-?}%" \
      || warn "headset ${wake:-state unknown} (battery ${batt:-?}%): put it on or press power; the app pauses while asleep"
    [ -n "$batt" ] && [ "$batt" -lt 30 ] 2>/dev/null && warn "battery ${batt}%: charge before the demo"
    if adbq shell pm list packages "$PKG" | grep -q "package:$PKG\$"; then
      ver="$(adbq shell dumpsys package "$PKG" | grep -m1 -o 'versionName=[^ ]*' | cut -d= -f2)"
      upd="$(adbq shell dumpsys package "$PKG" | grep -m1 -o 'lastUpdateTime=.*' | cut -d= -f2)"
      ok "$PKG $ver installed ($upd)"
    elif [ -z "$apk" ]; then
      fail "$PKG not installed: pass -i path/to/AirTools.apk"
    fi
  fi
fi

# ---------------------------------------------------------------- network
step "2. Network"
if $wifi; then
  ip="$(ipconfig getifaddr en0 2>/dev/null || true)"
  [ -z "$server" ] && [ -n "$ip" ] && server="http://$ip:$PORT"
  ok "Wi-Fi mode: no adb reverse; app server extra = ${server:-<none: pass --server>} (Quest and Mac on the same hotspot)"
  [ -z "$ip" ] && warn "en0 has no IP: join the iPhone hotspot on this Mac"
elif $device_ok; then
  rev="$(adbq reverse --list)"
  for p in "$PORT" "$MOCK_PORT"; do
    if printf '%s\n' "$rev" | grep -q "tcp:$p tcp:$p"; then ok "adb reverse tcp:$p already set"
    else run "${ADB[@]}" reverse "tcp:$p" "tcp:$p" && ! $dry && ok "adb reverse tcp:$p set"; fi
  done
else
  warn "no headset: skipped adb reverse"
fi
code="$(curl -s -o /dev/null -m 4 -w '%{http_code}' https://api.groq.com/openai/v1/models 2>/dev/null || true)"
if [ -n "$code" ] && [ "$code" != "000" ]; then ok "internet: api.groq.com answers (HTTP $code without a key)"
else warn "internet: api.groq.com unreachable. Live search/voice will fail: use --offline (cached parts only)"; fi

# ---------------------------------------------------------------- backend
step "3. Backend server (:$PORT)"
server_mode=""
if $do_server; then
  if [ -f "$BACKEND_DIR/.env" ]; then
    for k in GROQ_API_KEY SERP_API_KEY; do
      grep -Eq "^$k=.+" "$BACKEND_DIR/.env" && ok "$k present in .env" || warn "$k missing from $BACKEND_DIR/.env (live parts/voice need it)"
    done
  else
    warn "no $BACKEND_DIR/.env: only scenes and cached parts will work"
  fi
  health="$(curl -s -m 3 "http://127.0.0.1:$PORT/health" 2>/dev/null || true)"
  pids="$(lsof -nP -iTCP:"$PORT" -sTCP:LISTEN -t 2>/dev/null | tr '\n' ' ' || true)"
  if printf '%s' "$health" | grep -q '"ok":true'; then
    pid="${pids%% *}"
    env_off="$(ps eww -o command= -p "$pid" 2>/dev/null | tr ' ' '\n' | grep -m1 '^OFFLINE=' | cut -d= -f2)"
    dot_off="$(grep -m1 '^OFFLINE=' "$BACKEND_DIR/.env" 2>/dev/null | cut -d= -f2)"
    server_mode="${env_off:-${dot_off:-false}}"
    ok "already running (pid ${pid:-?}, OFFLINE=$server_mode): $health"
    $offline && [ "$server_mode" != "true" ] && warn "--offline ignored: a server is already up. Stop it yourself first to switch modes (docs/demo.md)"
  elif [ -n "$pids" ]; then
    fail "something listens on :$PORT (pid $pids) but /health doesn't answer. Not touching it: check that process"
  else
    log="$SPIKE/server-$STAMP.log"
    envs=(); $offline && envs=(env OFFLINE=true)
    # --no-sync: never let `uv run` try to download packages at demo time (after pulling backend changes, `uv sync` once online)
    uvrun=(uv run); [ -d "$BACKEND_DIR/.venv" ] && uvrun=(uv run --no-sync)
    server_mode="$($offline && echo true || echo false)"
    if ! $dry; then mkdir -p "$SPIKE"; fi
    if $dry; then
      printf '  \033[36mdry\033[0m   (cd %s && nohup %s %s uvicorn server.app:app --host 0.0.0.0 --port %s > %s 2>&1 &)\n' \
        "$BACKEND_DIR" "${envs[*]:-}" "${uvrun[*]}" "$PORT" "$log"
    else
      printf '  \033[36mrun\033[0m   %s %s uvicorn server.app:app --host 0.0.0.0 --port %s  (log %s)\n' "${envs[*]:-}" "${uvrun[*]}" "$PORT" "$log"
      # the whole subshell is redirected, so nothing keeps this terminal/pipe open; exec = $! is uv's pid
      ( cd "$BACKEND_DIR" || exit 1; exec nohup "${envs[@]+"${envs[@]}"}" "${uvrun[@]}" uvicorn server.app:app --host 0.0.0.0 --port "$PORT" ) >"$log" 2>&1 </dev/null &
      echo "$! $PORT $log" >"$SPIKE/.server.pid"
      for _ in $(seq 1 40); do
        curl -s -m 1 "http://127.0.0.1:$PORT/health" 2>/dev/null | grep -q '"ok":true' && break
        sleep 0.5
      done
      if curl -s -m 2 "http://127.0.0.1:$PORT/health" | grep -q '"ok":true'; then ok "started (pid $(cut -d' ' -f1 "$SPIKE/.server.pid"), OFFLINE=$server_mode), log $log. Stop: kill $(cut -d' ' -f1 "$SPIKE/.server.pid")"
      else fail "didn't answer /health within 20 s: tail $log"; fi
    fi
  fi
  if curl -s -m 3 "http://127.0.0.1:$PORT/health" | grep -q '"ok":true'; then
    sites="$(curl -s -m 3 "http://127.0.0.1:$PORT/scenes" | python3 -c 'import json,sys; print(" ".join(s["site"] for s in json.load(sys.stdin)))' 2>/dev/null)"
    [ -n "$sites" ] && ok "scenes: $sites" || warn "GET /scenes returned nothing: the app stays on the built-in facade"
    printf '%s' "$sites" | grep -qw kitchen || warn "no 'kitchen' scene: the app auto-loads kitchen only if the server has it"
  fi
else
  ok "skipped (--no-server)"
fi

# ---------------------------------------------------------------- presenter relay (D7, UX W1.8)
# The laptop page that resets / hints / skips beats on the headset (never pays). Its own port: :8000 is the backend team's.
step "3b. Presenter relay (:$PRESENTER_PORT)"
presenter_url=""
if ! $do_presenter; then
  ok "skipped (--no-presenter)"
else
  phealth="$(curl -s -m 2 "http://127.0.0.1:$PRESENTER_PORT/health" 2>/dev/null || true)"
  ppids="$(lsof -nP -iTCP:"$PRESENTER_PORT" -sTCP:LISTEN -t 2>/dev/null | tr '\n' ' ' || true)"
  if printf '%s' "$phealth" | grep -q '"airtools-presenter"'; then
    ok "already running (pid ${ppids%% *})"
    $wifi && lsof -nP -iTCP:"$PRESENTER_PORT" -sTCP:LISTEN 2>/dev/null | grep -q "127.0.0.1:$PRESENTER_PORT" \
      && warn "the relay listens on localhost only (started for USB): kill ${ppids%% *} and re-run with --wifi"
  elif [ -n "$ppids" ]; then
    fail "something else listens on :$PRESENTER_PORT (pid $ppids). Not touching it: PRESENTER_PORT=<free port> $0 …"
  else
    plog="$SPIKE/presenter-$STAMP.log"
    phost=127.0.0.1; $wifi && phost=0.0.0.0   # USB: the tunnel arrives on localhost; hotspot: the Quest connects to en0
    if $dry; then
      printf '  \033[36mdry\033[0m   nohup python3 %s --port %s --host %s > %s 2>&1 &\n' "$ROOT/tools/presenter/presenter_server.py" "$PRESENTER_PORT" "$phost" "$plog"
    else
      mkdir -p "$SPIKE"
      printf '  \033[36mrun\033[0m   python3 tools/presenter/presenter_server.py --port %s --host %s  (log %s)\n' "$PRESENTER_PORT" "$phost" "$plog"
      nohup python3 "$ROOT/tools/presenter/presenter_server.py" --port "$PRESENTER_PORT" --host "$phost" >"$plog" 2>&1 </dev/null &
      echo "$! $PRESENTER_PORT $plog" >"$SPIKE/.presenter.pid"
      for _ in $(seq 1 20); do curl -s -m 1 "http://127.0.0.1:$PRESENTER_PORT/health" 2>/dev/null | grep -q airtools-presenter && break; sleep 0.25; done
      if curl -s -m 2 "http://127.0.0.1:$PRESENTER_PORT/health" | grep -q airtools-presenter; then ok "started (pid $(cut -d' ' -f1 "$SPIKE/.presenter.pid")). Stop: kill $(cut -d' ' -f1 "$SPIKE/.presenter.pid")"
      else fail "didn't answer /health within 5 s: tail $plog"; fi
    fi
  fi
  if $wifi; then
    ok "Wi-Fi mode: the app finds the relay at its server's host, port $PRESENTER_PORT"
  elif $device_ok; then
    if adbq reverse --list | grep -q "tcp:$PRESENTER_PORT tcp:$PRESENTER_PORT"; then ok "adb reverse tcp:$PRESENTER_PORT already set"
    else run "${ADB[@]}" reverse "tcp:$PRESENTER_PORT" "tcp:$PRESENTER_PORT" && ! $dry && ok "adb reverse tcp:$PRESENTER_PORT set"; fi
  else
    warn "no headset: skipped adb reverse tcp:$PRESENTER_PORT"
  fi
  presenter_url="http://localhost:$PRESENTER_PORT/"
  ok "presenter page: $presenter_url (open it in the laptop browser; it never pays)"
fi

# ---------------------------------------------------------------- app
step "4. App"
if $device_ok; then
  if [ -n "$apk" ]; then
    if [ -f "$apk" ]; then run "${ADB[@]}" install -r "$apk" || fail "adb install failed"
    else fail "APK not found: $apk"; fi
  fi
  # ServerConfig precedence: launch extras > files/server.txt > PlayerPrefs > http://127.0.0.1:8000
  pinned="$(adbq shell cat "/sdcard/Android/data/$PKG/files/server.txt" 2>/dev/null | grep -v 'No such file' | tr '\n' ' ')"
  if [ -n "${pinned// /}" ]; then
    if [ -n "$server" ]; then ok "server.txt on the headset ($pinned) is overridden by --server $server"
    else warn "the app will use server.txt on the headset: $pinned(remove: adb shell rm /sdcard/Android/data/$PKG/files/server.txt)"; fi
  fi
  if $do_launch; then
    running="$(adbq shell pidof "$PKG")"
    if [ -n "$running" ] && ! $restart; then
      if [ -n "$server$site$demo" ]; then warn "app already running (pid $running): launch extras only apply on a fresh start. Add --restart"
      else ok "app already running (pid $running): bringing it to the front (--restart for a fresh start)"; fi
    fi
    $restart && run "${ADB[@]}" shell am force-stop "$PKG"
    extras=(); [ -n "$server" ] && extras+=(-e server "$server"); [ -n "$site" ] && extras+=(-e site "$site")
    [ -n "$demo" ] && extras+=(-e demo "$demo")   # D7
    if run "${ADB[@]}" shell am start -n "$PKG/$ACTIVITY" "${extras[@]+"${extras[@]}"}"; then
      $dry || ok "launched${server:+ server=$server}${site:+ site=$site}${demo:+ demo=$demo}"
    else fail "am start failed"; fi
  else
    ok "launch skipped (--no-launch)"
  fi
else
  warn "no headset: skipped install/launch"
fi

# ---------------------------------------------------------------- logcat
step "5. Logcat"
pidfile="$SPIKE/.logcat.pid"
if ! $do_logcat; then
  ok "skipped (--no-logcat)"
elif ! $device_ok; then
  warn "no headset: no logcat"
else
  old_pid=""; old_log=""
  [ -f "$pidfile" ] && read -r old_pid old_log <"$pidfile"
  if [ -n "$old_pid" ] && ps -p "$old_pid" -o command= 2>/dev/null | grep -q 'logcat'; then
    ok "already streaming (pid $old_pid) → $old_log"
    qlog="$old_log"
  else
    qlog="$SPIKE/quest-$STAMP.log"
    filt=(Unity:I VrApi:I AndroidRuntime:E DEBUG:F '*:S')
    if $dry; then
      printf '  \033[36mdry\033[0m   nohup %s logcat -v time %s > %s &\n' "${ADB[*]}" "${filt[*]}" "$qlog"
    else
      mkdir -p "$SPIKE"
      nohup "${ADB[@]}" logcat -v time "${filt[@]}" >"$qlog" 2>&1 </dev/null &
      echo "$! $qlog" >"$pidfile"
      ok "streaming (pid $!) → $qlog"
    fi
  fi
  echo "        fps:   python3 tools/demo/fps.py ${qlog} 'AppState Passthrough -> World'"
  echo "        watch: tail -f ${qlog} | grep --line-buffered -E 'AirTools|AndroidRuntime|FATAL'"
fi

# ---------------------------------------------------------------- summary
step "Summary"
if [ "$FAILS" -gt 0 ]; then printf '  \033[31m%d problem(s)\033[0m, %d warning(s). Fix the FAIL lines and re-run (safe to repeat).\n' "$FAILS" "$WARNINGS"; exit 1; fi
printf '  ready, %d warning(s).%s\n' "$WARNINGS" "$($dry && echo ' (dry run: nothing was changed)')"
[ "$server_mode" = "true" ] && echo "  backend is OFFLINE: preset chips + cached parts work; voice / 'What is this?' answer 503 (docs/demo.md)"
[ -n "$presenter_url" ] && echo "  presenter page: $presenter_url  (Reset · Hint · Skip to beat · Enter/Exit; never pays)"   # D7
exit 0
