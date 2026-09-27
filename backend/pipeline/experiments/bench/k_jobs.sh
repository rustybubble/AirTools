#!/bin/bash
# E7 k-* jobs, run in sequence on the idle box (16 cores).
export CORES=0-15 THREADS=16 OMVS=/workspace/tools/openmvs
B=/workspace/bench; W=/workspace/airtools/work/bench; S=/workspace/airtools/scene/bench
st() { echo "$(date -Is) $*" >> $B/logs/k_status.txt; }
# 1. hybrid fill 1 cm on the cached SfM (timed; keeps dense for the paired control)
st start k-fill1cm; bash $B/idle_check.sh k-fill1cm
bash $B/pipejob.sh k-fill1cm dji0095 sfm --crop none --densify hybrid --densify-args "--fill-dist-m 0.01"
bash $B/score.sh k-fill1cm $S/k-fill1cm 153 --note "E7 hybrid MoGe-2 fill-dist 1 cm, cached SfM"
bash $B/novel.sh k-fill1cm $S/k-fill1cm; st done k-fill1cm
# 1b. paired control: same DensifyPointCloud output, fill 2 cm (quality only)
st start k-fill2cm-ctl
bash $B/pipejob.sh k-fill2cm-ctl bench/k-fill1cm dense --crop none --densify hybrid
bash $B/score.sh k-fill2cm-ctl $S/k-fill2cm-ctl 153 --note "E7 control: fill 2 cm on k-fill1cm dense cloud"
bash $B/novel.sh k-fill2cm-ctl $S/k-fill2cm-ctl; st done k-fill2cm-ctl
for n in k-fill1cm k-fill2cm-ctl; do
  find $W/$n \( -name "*.dmap" -o -name "*.npy" -o -name "*.npz" -o -name "scene_dense*.ply" \) -delete
  find $W/$n -maxdepth 3 -type d -name depth_cache -exec rm -r {} +
done
# 2. sharpest-of-2 at fps4, fresh, same stages as i-e2e-defaults
st start k-sw2-e2e; bash $B/idle_check.sh k-sw2-e2e
while [ "$(df --output=avail -BG /workspace | tail -1 | tr -dc 0-9)" -lt 6 ]; do st hold disk; sleep 60; done
bash $B/pipejob.sh k-sw2-e2e none sfm --crop none --stages preview,full --fps 4 --sharpen-window 2
bash $B/score.sh k-sw2-e2e $S/k-sw2-e2e 0 --id-fps 20 --note "E7 fps4 sharpest-of-2, hybrid default, fresh e2e"
bash $B/novel.sh k-sw2-e2e $S/k-sw2-e2e
find $W/k-sw2-e2e \( -name "*.dmap" -o -name "*.npy" -o -name "*.npz" -o -name "scene_dense*.ply" \) -delete
find $W/k-sw2-e2e -maxdepth 3 -type d -name depth_cache -exec rm -r {} +
st done k-sw2-e2e
