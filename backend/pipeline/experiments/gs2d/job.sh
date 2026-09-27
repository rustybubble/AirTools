#!/bin/bash
# E6 jobs on hfbox (docs/research/bench/e6-splat-mesh.md). Code copy: /workspace/e6/code.
#   job.sh prep                        cameras.npz + eval_cams.npz (+ MoGe-2 priors with PRIORS=1)
#   job.sh train NAME [train.py args]  2DGS -> /workspace/e6/train/NAME/step<k>/{depth,eval}
#   job.sh mesh NAME STEP [worker args] E3's depthfusion tail (masks, TSDF 1 cm, TextureMesh) on the
#                                       splat depth via the depth cache, then E1 score + novel cov
# CPU work is pinned to cores 12-15 at nice 19 (E1 owns 0-11; override with PIN= and THREADS=);
# gpu.log samples the GPU.
source /workspace/env.sh
A=/workspace/airtools; E=/workspace/e6; C=$E/code; D=$A/work/dji0095/sfm/dense
PIN=${PIN:-taskset -c 12-15 nice -n 19}; T=${THREADS:-4}; export OMP_NUM_THREADS=$T PYTHONPATH=$C
mkdir -p $E/logs $E/runs $E/train
gpulog() { while :; do echo "$(date +%s) $(cat /sys/class/drm/card1/device/gpu_busy_percent) $(cat /sys/class/drm/card1/device/mem_info_vram_used)"; sleep 5; done >> $E/gpu.log; }
cmd=$1; shift
case $cmd in
prep)
  cd $C
  $PIN $A/.venv/bin/python -c "from pipeline.depthfusion import export_cameras as e; print(e('$D', '$E/cameras.npz'))"
  $PIN $A/.venv/bin/python -m pipeline.experiments.gs2d.evalsplat cams --cameras $E/cameras.npz --out $E/eval_cams.npz
  [ "$PRIORS" = 1 ] && $PIN $AIRTOOLS_DEPTH_ENV/bin/python $C/pipeline/experiments/gs2d/priors.py \
    --cameras $E/cameras.npz --images $D/images --out $E/priors > $E/logs/priors.log 2>&1
  ;;
train)
  NAME=$1; shift
  gpulog & G=$!
  t0=$(date +%s)
  $PIN /workspace/envs/gsplat/bin/python $C/pipeline/experiments/gs2d/train.py --cameras $E/cameras.npz \
    --images $D/images --out $E/train/$NAME --eval-cams $E/eval_cams.npz "$@" > $E/logs/$NAME.train.log 2>&1
  rc=$?; kill $G
  echo "{\"name\": \"$NAME\", \"t0\": $t0, \"wall_s\": $(( $(date +%s)-t0 )), \"exit\": $rc, \"args\": \"$*\"}" > $E/runs/$NAME.train.json
  for s in $E/train/$NAME/step*/eval; do
    (cd $C && $A/.venv/bin/python -m pipeline.experiments.gs2d.evalsplat score --renders $s > $s/../splat_score.json)
  done
  ;;
mesh)
  NAME=$1; STEP=$2; shift 2
  RUN=$NAME-$STEP; W=$A/work/dji0095-2dgs/$RUN; S=$A/scene/dji0095-2dgs/$RUN
  bash $C/pipeline/experiments/bench/mkwork.sh $A/work/dji0095 $W sfm
  # the worker skips inference when every frame is cached: the splat depth stands in for MoGe-2's
  mkdir -p $W/mvs/depthfusion/depth_cache
  ln -s $E/train/$NAME/step$STEP/depth $W/mvs/depthfusion/depth_cache/moge2_960x540
  rm -rf $S; cd $C
  t0=$(date +%s)
  $PIN $A/.venv/bin/python -m pipeline.cli run $A/data/real/DJI_0095.MP4 --site $RUN --out $S --work $W \
    --stages full --crop none --densify depthfusion --threads $T --densify-args "--align scale $*" \
    > $E/logs/$RUN.run.log 2>&1
  rc=$?; wall=$(( $(date +%s)-t0 ))
  # cached = E1's 130 s (frames + SfM) + training up to this step + the depth/eval export
  CACHED=$(python3 -c "import json;r=json.load(open('$E/train/$NAME/step$STEP/stats.json'));print(130+round(r['train_s']+r['export_s']))")
  echo "{\"name\": \"$RUN\", \"cached_from\": \"dji0095 + 2dgs $NAME step $STEP\", \"cached_s\": $CACHED, \"wall_s\": $wall, \"exit\": $rc, \"args\": \"$*\"}" > $E/runs/$RUN.json
  [ $rc = 0 ] || exit $rc
  $PIN $A/.venv/bin/python -m pipeline.experiments.bench score $S --ref /workspace/bench/ref --name e6-$RUN \
    --out $E/results --run-json $E/runs/$RUN.json --report $W/report.json --cached-s $CACHED --id-fps 10 \
    --note "E6 2DGS $NAME step $STEP -> depthfusion tail (--align scale $*)" > $E/logs/$RUN.score.log 2>&1
  $PIN $A/.venv/bin/python -m pipeline.experiments.bench.novel_coverage --ref /workspace/bench/ref $S >> $E/novel_coverage.jsonl
  ;;
esac
