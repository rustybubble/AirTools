#!/bin/bash
# cheshire_job.sh NAME : timed Cheshire (HIP AliceVision) dense stages on our COLMAP poses (E4's
# e4-cheshire-ourposes-q200k chain), then bench score. Env: CORES (default 0-15), NCPU (16).
# Chain: colmap_to_av -> cheshire.sh (SFM_IN, its full-mesh texturing skipped) -> quadric
# decimate to 200k (fast_simplification) -> AliceVision texturing 4096, no subdivision.
NAME=$1; CORES=${CORES:-0-15}; NCPU=${NCPU:-16}
source /workspace/env.sh; cd /workspace/airtools || exit 1
A=pipeline/experiments/amd_stacks; B=/workspace/tools/cheshire/bundle
W=work/bench/$NAME; S=scene/bench/$NAME
rm -rf "work/bench/${NAME:?}" "scene/bench/${NAME:?}"; mkdir -p $W/mesh
W=$(realpath $W)
taskset -cp $$ > /workspace/bench/logs/$NAME.affinity 2>&1
t0=$(date +%s)
.venv/bin/python $A/colmap_to_av.py $PWD/work/dji0095/sfm/dense/sparse $PWD/work/dji0095/sfm/dense/images $W/ours.sfm
touch $W/.done_texturing
CORES=$CORES NCPU=$NCPU SFM_IN=$W/ours.sfm bash $A/cheshire.sh $PWD/work/dji0095/frames_nominal $W || exit 1
t1=$(date +%s)
taskset -c $CORES .venv/bin/python -c "
import trimesh, fast_simplification
m = trimesh.load('$W/mesh/meshFiltered.obj', process=False, force='mesh')
v, f = fast_simplification.simplify(m.vertices, m.faces, target_reduction=max(0.0, 1 - 200000 / len(m.faces)))
trimesh.Trimesh(v, f, process=False).export('$W/mesh/meshQuadric200k.obj')
print('faces', len(m.faces), '->', len(f))"
t2=$(date +%s)
(cd $W && LD_LIBRARY_PATH=$B/lib ALICEVISION_ROOT=$B ALICEVISION_SENSOR_DB=$B/share/aliceVision/cameraSensors.db \
  taskset -c $CORES $B/bin/aliceVision_texturing -i densePointCloud.abc --inputMesh mesh/meshQuadric200k.obj \
  --imagesFolder dense -o texq --textureSide 4096 --useUDIM 0 --colorMappingFileType png \
  --subdivisionTargetRatio 0 --maxCoresAvailable $NCPU > log_texq.txt 2>&1) || exit 1
t3=$(date +%s)
echo "== decimate $((t2-t1)) s; texturing q200k $((t3-t2)) s" >> $W/times.txt
python3 -c "
import json; json.dump({'name': '$NAME', 'cached_from': 'dji0095', 'cached_stages': 'sfm', 'wall_s': $((t3-t0)),
 'exit': 0, 'args': ['cheshire on our poses, q200k'], 'cores': '$CORES', 'stages_txt': open('$W/times.txt').read()},
 open('/workspace/bench/runs/$NAME.json', 'w'), indent=1)"
.venv/bin/python -m pipeline.experiments.amd_stacks.to_bench work/dji0095/sfm/dense/sparse $W/texq/texturedMesh.obj $S \
  --transform /workspace/tools/e4/av_flip.json
taskset -c $CORES uv run --extra pipeline python -m pipeline.experiments.bench score $S --ref /workspace/bench/ref \
  --name $NAME --out /workspace/bench/results --run-json /workspace/bench/runs/$NAME.json --cached-s 130 --id-fps 10 \
  --note "idle box: Cheshire dense on our poses, quadric 200k, AliceVision texturing 4096"
