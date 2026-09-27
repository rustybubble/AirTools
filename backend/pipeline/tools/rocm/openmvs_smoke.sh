#!/usr/bin/env bash
# OpenMVS develop smoke on the 12-image slice colmap_pms_smoke.sh undistorted: subset_txt.py -> InterfaceCOLMAP
# -> DensifyPointCloud with --fusion-mode -2 (SGM) and 0 (PatchMatch). Expects both .py/.sh in /workspace/tools.
set -e
O=/workspace/exp/colmap-hip-smoke; S=/workspace/exp/openmvs-develop-smoke; M=/workspace/tools/openmvs-develop/bin
[ -d $O/dense-txt ] || { mkdir -p $O/dense-txt && /workspace/tools/colmap-hip/bin/colmap model_converter --input_path $O/dense/sparse --output_path $O/dense-txt --output_type TXT; }
mkdir -p $S/colmap/sparse; ln -sfn $O/dense/images $S/colmap/images
/opt/venv/bin/python /workspace/tools/subset_txt.py $O/dense-txt $S/colmap/sparse $O/keep.txt
cd $S
nice -n 19 $M/InterfaceCOLMAP -i colmap -o scene.mvs -v 1 > ic.log 2>&1 || { tail ic.log; exit 1; }
for mode in -2 0; do
  t0=$(date +%s)
  nice -n 19 $M/DensifyPointCloud scene.mvs -o dense_fm$mode.mvs --fusion-mode $mode --resolution-level 1 \
    --max-threads 4 -w $S -v 2 > densify_fm$mode.log 2>&1 || { tail -20 densify_fm$mode.log; exit 1; }
  echo "fusion-mode $mode: $(( $(date +%s)-t0 )) s, $(grep -iE 'points? fused|Depth-maps fused' densify_fm$mode.log | tail -1)"
done
ls -la $S/*.ply
