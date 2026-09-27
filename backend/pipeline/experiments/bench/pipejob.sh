#!/bin/bash
# pipejob.sh NAME BASE KEEP ARGS... : timed `pipeline.cli run --stages full` on DJI_0095.
# BASE=none for a fresh run; else hardlink cached stages from work/BASE (KEEP=sfm|dense|mesh).
# Env: CORES (taskset list, default 0-11), THREADS (default 12), OMVS (OpenMVS dir override).
NAME=$1; BASE=$2; KEEP=$3; shift 3
source /workspace/env.sh; cd /workspace/airtools
[ -n "$OMVS" ] && export AIRTOOLS_OPENMVS_DIR=$OMVS
taskset -cp $$ > /workspace/bench/logs/$NAME.affinity 2>&1
W=work/bench/$NAME; S=scene/bench/$NAME
if [ "$BASE" = none ]; then rm -rf $W; mkdir -p $W; else bash /workspace/bench/mkwork.sh work/$BASE $W $KEEP; fi
rm -rf $S; mkdir -p /workspace/bench/runs
t0=$(date +%s)
taskset -c ${CORES:-0-11} uv run --extra pipeline python -m pipeline.cli run data/real/DJI_0095.MP4 --site $NAME --out $S --work $W --stages full --threads ${THREADS:-12} "$@" > /workspace/bench/logs/$NAME.run.log 2>&1
rc=$?; wall=$(( $(date +%s)-t0 ))
python3 - "$NAME" "$BASE" "$KEEP" "$wall" "$rc" "$@" <<'PY'
import json, sys
name, base, keep, wall, rc, *args = sys.argv[1:]
json.dump({"name": name, "cached_from": base, "cached_stages": keep, "wall_s": int(wall), "exit": int(rc), "args": args, "cores": __import__("os").environ.get("CORES", "0-11"), "openmvs": __import__("os").environ.get("AIRTOOLS_OPENMVS_DIR")},
          open(f"/workspace/bench/runs/{name}.json", "w"), indent=1)
PY
