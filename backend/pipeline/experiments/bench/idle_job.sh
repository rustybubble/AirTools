#!/bin/bash
# idle_job.sh NAME BASE KEEP CACHED_S NOTE -- ARGS... : one idle-box re-time job. Logs the box state,
# waits while /workspace has <6 GB free, runs sweep.sh on all 16 cores, adds novel-view coverage,
# then deletes the run's own unshared depth maps, depth caches and dense clouds (timing is captured).
NAME=$1
bash /workspace/bench/idle_check.sh "$NAME"
while [ "$(df --output=avail -BG /workspace | tail -1 | tr -dc 0-9)" -lt 6 ]; do
  echo "$(date -Is) <6 GB free on /workspace, holding $NAME" >> /workspace/bench/logs/idle.txt; sleep 60
done
CORES=0-15 THREADS=16 bash /workspace/bench/sweep.sh "$@"
bash /workspace/bench/novel.sh "$NAME" /workspace/airtools/scene/bench/"$NAME"
W=/workspace/airtools/work/bench/"$NAME"
find "$W" \( -name "*.dmap" -o -name "*.npy" -o -name "*.npz" -o -name "scene_dense*.ply" \) -links 1 -delete
find "$W" -maxdepth 3 -type d -name "depth_cache" -exec rm -r {} +
