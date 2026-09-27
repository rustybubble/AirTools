#!/bin/zsh
# Keep the USB tunnel up for the headset: every time the Quest (re)connects over USB, re-apply the adb reverse
# forwards (a reconnect clears them, and the app then can't reach the laptop) and restart the logcat stream.
#
#   nohup tools/demo/tunnel_watch.sh > SpikeData/tunnel_watch.log 2>&1 &
#
# Env: PORTS (default "8004 8765 8766"; not 8000: the app probes it first and would pick the team's unpatched server), LOGDIR (default SpikeData), SERIAL (adb serial if several).
PORTS=(${=PORTS:-8004 8765 8766})
LOGDIR=${LOGDIR:-SpikeData}
ADB=(adb ${SERIAL:+-s $SERIAL})
last=""
logcat_pid=""
while true; do
  state=$($ADB get-state 2>/dev/null)
  if [[ "$state" == "device" ]]; then
    # Transport id changes on every USB reconnect; the reverse list is empty after one.
    tid=$($ADB devices -l 2>/dev/null | awk '/transport_id/ {for (i=1;i<=NF;i++) if ($i ~ /^transport_id:/) print $i}' | head -1)
    missing=0
    list=$($ADB reverse --list 2>/dev/null)
    for p in $PORTS; do [[ "$list" == *"tcp:$p tcp:$p"* ]] || missing=1; done
    if [[ "$tid" != "$last" || $missing == 1 ]]; then
      for p in $PORTS; do $ADB reverse tcp:$p tcp:$p >/dev/null 2>&1; done
      echo "$(date +%H:%M:%S) headset $tid: reverse ${PORTS[*]} applied"
      last=$tid
      if [[ -n "$logcat_pid" ]] && kill -0 $logcat_pid 2>/dev/null; then kill $logcat_pid; fi
      f="$LOGDIR/quest-$(date +%Y%m%d-%H%M%S).log"
      $ADB logcat -v time Unity:I VrApi:W '*:S' > "$f" 2>&1 &
      logcat_pid=$!
      echo "$(date +%H:%M:%S) logcat → $f"
    fi
  elif [[ -n "$last" ]]; then
    echo "$(date +%H:%M:%S) headset gone ($state)"
    last=""
  fi
  sleep 2
done
