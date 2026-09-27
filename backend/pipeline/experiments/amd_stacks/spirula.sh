#!/bin/bash
# Spirula Studio end to end on the kitchen frames (hfbox): SfM -> MoGe normals -> meshing splats -> textured glb.
# Usage: spirula.sh [IMAGES_DIR] [WORK_DIR]   (extra train flags via TRAIN_ARGS)
# Every stage is timed into $W/times.txt; the box is shared, so everything runs under nice -n 19.
set -euo pipefail
S=/workspace/tools/spirula/spirula
IMG=${1:-/workspace/airtools/work/dji0095/frames_nominal}
W=${2:-/workspace/airtools/work/spirula-kitchen}
DEV=${DEV:-0}
# Shared box: pinned to 4 cores (CORES, NCPU) so E1's timed CPU benchmarks are not disturbed; GPU is not limited.
CORES=${CORES:-12-15}; NCPU=${NCPU:-4}; export OMP_NUM_THREADS=$NCPU
mkdir -p "$W/ds"
[ -e "$W/ds/images" ] || cp -r "$IMG" "$W/ds/images"
cd "$W"
stage() {  # stage <name> <cmd...>
  local name=$1; shift
  local t0; t0=$(date +%s)
  echo "== $name start $(date -Is) load $(cut -d' ' -f1 /proc/loadavg)" | tee -a times.txt
  taskset -c "$CORES" nice -n 19 "$@" 2>&1 | tee "log_$name.txt"
  echo "== $name done $(( $(date +%s) - t0 )) s load $(cut -d' ' -f1 /proc/loadavg)" | tee -a times.txt
}
[ -e ds/sparse/0/images.bin ] || stage sfm $S sfm auto ds/images -o ds --data-type video --device "$DEV" --threads "$NCPU" --decode-threads "$NCPU"
[ -d ds/normals ] || stage geometry $S geometry ds --device "$DEV"
[ -d run ] || stage train $S train meshing --data ds --data-format colmap --colmap-recon-dir sparse/0 \
  --output-dir-prefix "$W" --output-dir-name run --device "$DEV" --disable-viewer 1 --keep-viewer-alive 0 ${TRAIN_ARGS:-}
stage mesh $S mesh run --color texture --format glb --output "$W/mesh" --device "$DEV" --num-threads "$NCPU"
