#!/bin/bash
# Re-score every E1 run so far with the current renderer (34cc339 changed preview.render mid-sweep).
S=/workspace/bench/score.sh
A=/workspace/airtools
bash $S b-r1-preview /workspace/bench/cands/r1 0 --wall-s 67 --times $A/work/dji0095/preview/frame_times.json --note "existing r1 preview, unscaled (scale_method none)"
bash $S b-r2-full /workspace/bench/cands/r2 0 --wall-s 1972 --note "existing r2 full, crop auto; wall = 2039 s run minus 67 s preview"
bash $S b0-nocrop $A/scene/bench/b0-nocrop 1800 --note "r2 full with --crop none; densify/SfM/mesh reused, cached_s estimated"
for n in d-res1 d-res2 d-res1-geo0 d-res1-sub1 d-res1-nv3 d-res2-min320; do
  bash $S $n $A/scene/bench/$n 130 --note "$(python3 -c "import json;print(json.load(open('/workspace/bench/results/$n.json'))['note'])")"
done
bash $S m-res1-fss $A/scene/bench/m-res1-fss 620 --note "res 1 dense reused + ReconstructMesh --free-space-support 1"
bash $S t-res1-vfi3 $A/scene/bench/t-res1-vfi3 640 --note "res 1 mesh reused + TextureMesh --virtual-face-images 3"
for n in p-48 p-96-lowvram; do
  bash $S $n $A/scene/bench/$n 0 --run-json /workspace/bench/runs/$n.json --report $A/work/bench/$n/preview/report.json --times $A/work/bench/$n/preview/frame_times.json --note "$(python3 -c "import json;print(json.load(open('/workspace/bench/results/$n.json'))['note'])")"
done
