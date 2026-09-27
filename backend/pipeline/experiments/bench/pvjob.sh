#!/bin/bash
# pvjob.sh NAME FRAMES LOWVRAM(0|1) NOTE : fresh timed preview-only run on DJI_0095, then score it
NAME=$1; FRAMES=$2; LOW=$3; NOTE=$4
source /workspace/env.sh; cd /workspace/airtools || exit 1
W=work/bench/$NAME; S=scene/bench/$NAME
rm -rf "work/bench/${NAME:?}" "scene/bench/${NAME:?}"; mkdir -p $W /workspace/bench/runs
if [ "$LOW" = 1 ]; then
  export AIRTOOLS_VGGT_LOWVRAM=1 AIRTOOLS_VGGT_REPO=/workspace/src/vggt-low-vram TORCHDYNAMO_DISABLE=1
fi
t0=$(date +%s)
taskset -c ${CORES:-0-11} uv run --extra pipeline python -m pipeline.cli run data/real/DJI_0095.MP4 --site $NAME --out $S --threads ${THREADS:-12} \
  --work $W --stages preview --preview-frames $FRAMES > /workspace/bench/logs/$NAME.run.log 2>&1
rc=$?; wall=$(( $(date +%s)-t0 ))
echo "{\"name\": \"$NAME\", \"wall_s\": $wall, \"exit\": $rc, \"args\": [\"--stages\", \"preview\", \"--preview-frames\", \"$FRAMES\", \"lowvram=$LOW\"]}" > /workspace/bench/runs/$NAME.json
taskset -c ${CORES:-0-11} uv run --extra pipeline python -m pipeline.experiments.bench score $S --ref /workspace/bench/ref \
  --name $NAME --out /workspace/bench/results --run-json /workspace/bench/runs/$NAME.json \
  --report $W/preview/report.json --times $W/preview/frame_times.json --note "$NOTE"
