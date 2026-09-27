#!/bin/bash
# score.sh NAME SCENE_DIR CACHED_S [extra harness args] -- scores into /workspace/bench/results
NAME=$1; SCENE=$2; CACHED=$3; shift 3
source /workspace/env.sh; cd /workspace/airtools
RJ=/workspace/bench/runs/$NAME.json; REP=work/bench/$NAME/report.json
taskset -c ${CORES:-0-11} uv run --extra pipeline python -m pipeline.experiments.bench score $SCENE --ref /workspace/bench/ref \
  --name $NAME --out /workspace/bench/results --run-json $RJ --report $REP --cached-s $CACHED "$@"
