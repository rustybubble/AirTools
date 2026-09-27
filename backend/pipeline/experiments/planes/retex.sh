#!/usr/bin/env bash
# S3: re-texture a plane-regularized mesh and score it (s3-learned-planes.md §5).
#   [WORK=work/bench/<run>] retex.sh NAME STRUCTURE [regularize.py args...]   (STRUCTURE=none: control)
# $WORK/mvs/union_mesh.ply (a run's untextured mesh, COLMAP frame) -> regularize.py --mesh-in ->
# OpenMVS TextureMesh with that run's own arguments (its --decimate is read from its log) ->
# pipeline mesh.process_mesh with the camera-centre Sim(3) to the scene frame -> scene dir ->
# E1 bench score (PSNR/SSIM/F@2cm) and S1 structure score of the mesh (planarity, edge band, snap).
set -euo pipefail
NAME=$1; ST=$2; shift 2
source /workspace/env.sh
cd /workspace/airtools
W=${WORK:-work/bench/i-hybrid-moge2}; S=scene/bench/i-hybrid-moge2; D=work/dji0095/sfm/dense
O=/workspace/s3/retex/$NAME; SC=/workspace/s3/scene_$NAME
mkdir -p $O $SC
T=(taskset -c 12-15)
if [ "$ST" = none ]; then cp $W/mvs/union_mesh.ply $O/mesh_in.ply; else
  "${T[@]}" /workspace/envs/pxw/bin/python pipeline/experiments/planes/regularize.py --scene $S \
    --structure $ST --dense $D --mesh-in $W/mvs/union_mesh.ply --out $O/mesh_in.ply "$@" \
    | tee $O/regularize.json
fi
DEC=$(grep -ho -- "--decimate [0-9.]*" $W/mvs/TextureMesh-*.log | tail -1 | cut -d' ' -f2)
# scene.mvs holds image paths relative to its dir (../sfm/dense/images): mirror that layout with a
# symlink so TextureMesh's working folder (and its log) is ours, not the run's
mkdir -p $O/mvs; cp $W/mvs/scene.mvs $O/mvs/; ln -sfn $PWD/$W/sfm $O/sfm
t0=$(date +%s)
"${T[@]}" $AIRTOOLS_OPENMVS_DIR/bin/TextureMesh -i $O/mvs/scene.mvs -m $O/mesh_in.ply \
  -o $O/textured.glb --decimate $DEC --resolution-level 0 --max-texture-size 4096 \
  --cost-smoothness-ratio 0.1 --global-seam-leveling 0 --local-seam-leveling 0 --export-type glb \
  --max-threads 4 -w $O/mvs > $O/texturemesh.log 2>&1
echo "TextureMesh $(( $(date +%s) - t0 )) s (decimate $DEC)"
cp $S/scene.json $S/cameras.r1.json $SC/; ln -sfn $(readlink -f $S/thumbs) $SC/thumbs
"${T[@]}" .venv/bin/python - $O $SC $D $S <<'EOF'
import sys
from pathlib import Path
import numpy as np
from pipeline import mesh
from pipeline.experiments.planes.pxw_lift import scene_sim3
O, SC, D, S = map(Path, sys.argv[1:])
s, R, t = scene_sim3(D, S / "cameras.r1.json")  # x_colmap = s R x_scene + t
M = np.eye(4); M[:3, :3] = R.T / s; M[:3, 3] = -R.T @ t / s
st, _ = mesh.process_mesh(O / "textured.glb", SC / "mesh.r1.glb", SC / "collision.r1.glb", align_matrix=M)
EOF
"${T[@]}" .venv/bin/python -m pipeline.experiments.bench score $SC --ref /workspace/bench/ref \
  --name s3-$NAME --out /workspace/s3/bench_out > $O/bench.log 2>&1
"${T[@]}" .venv/bin/python -m pipeline.experiments.structure score $SC \
  --gt pipeline/experiments/structure/kitchen0095/gt.json --out /workspace/s3/retex/results_r6.jsonl \
  --name s3-$NAME --note "retex $W $ST $*" 2>&1 | tail -1
python3 -c "import json; r=json.load(open('/workspace/s3/bench_out/s3-$NAME.json')); a=r['all']; g=r['geometry']; print('$NAME', 'psnr', round(a['psnr'],3), 'ssim', round(a['ssim'],4), 'lap', round(a['lap_corr'],3), 'cov', round(a['coverage'],4), 'F2', round(g['f_2cm'],4))"
rm -f $O/*.png $O/mesh_in.ply $O/textured.glb  # the packaged scene dir is what we keep (disk)
rm -f /workspace/s3/bench_out/s3-$NAME/*.png
