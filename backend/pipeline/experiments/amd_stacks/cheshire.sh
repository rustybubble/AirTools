#!/bin/bash
# Cheshire (HIP AliceVision 3.4) run as Meshroom's legacy photogrammetry graph, CLI only (no Meshroom UI).
# Usage: cheshire.sh [IMAGES_DIR] [WORK_DIR]; SFM_IN=<our poses as .sfm, from colmap_to_av.py> skips AliceVision SfM. Bundle: /workspace/tools/cheshire/bundle (+ libnuma.so.1 copied into lib/).
# viewIdMethod filename: without EXIF, viewIds are path hashes and Sequential matching pairs
# hash-adjacent (random) frames -- AliceVision SfM then registered 11/175. Frame numbers fix the order.
# No vocabulary tree ships with the bundle, so pairs are Sequential (video) instead of Meshroom's
# exhaustive-below-200-images default. FOV 66.7 deg = our COLMAP focal (1457.6 px at 1920 wide).
set -euo pipefail
B=/workspace/tools/cheshire/bundle
export LD_LIBRARY_PATH=$B/lib ALICEVISION_ROOT=$B ALICEVISION_SENSOR_DB=$B/share/aliceVision/cameraSensors.db
IMG=${1:-/workspace/airtools/work/dji0095/frames_nominal}
W=${2:-/workspace/airtools/work/cheshire-kitchen}
FOV=${FOV:-66.7}; NB=${NB:-30}
mkdir -p "$W" && cd "$W"
# Shared box: pinned to 4 cores (CORES, NCPU) so E1's timed CPU benchmarks are not disturbed; GPU is not limited.
CORES=${CORES:-12-15}; NCPU=${NCPU:-4}; export OMP_NUM_THREADS=$NCPU
av() { taskset -c "$CORES" nice -n 19 "$B/bin/aliceVision_$1" "${@:2}" --maxCoresAvailable "$NCPU"; }
stage() {  # stage <name> <cmd...>
  local name=$1; shift
  [ -e ".done_$name" ] && return  # resumable: a finished stage is skipped
  local t0; t0=$(date +%s)
  echo "== $name start $(date -Is) load $(cut -d' ' -f1 /proc/loadavg)" | tee -a times.txt
  "$@" > "log_$name.txt" 2>&1 || { echo "== $name FAILED"; tail -30 "log_$name.txt"; exit 1; }
  echo "== $name done $(( $(date +%s) - t0 )) s load $(cut -d' ' -f1 /proc/loadavg)" | tee -a times.txt
  touch ".done_$name"
}
mkdir -p sfm_extra feat match dense depth filt mesh tex colmap
SFM=${SFM_IN:-sfm.abc}
if [ -z "${SFM_IN:-}" ]; then
  stage cameraInit av cameraInit --imageFolder "$IMG" -o cameraInit.sfm --defaultFieldOfView "$FOV" --allowSingleView 1 --viewIdMethod filename --viewIdRegex ".*?(\d+)"
  stage featureExtraction av featureExtraction -i cameraInit.sfm -o feat
  stage imageMatching av imageMatching -i cameraInit.sfm -f feat -o pairs.txt --method Sequential --nbNeighbors "$NB"
  stage featureMatching av featureMatching -i cameraInit.sfm -f feat -l pairs.txt -o match
  stage sfm av incrementalSfM -i cameraInit.sfm -f feat -m match -o sfm.abc --outputViewsAndPoses cameras.sfm --extraInfoFolder sfm_extra
fi
stage prepareDenseScene av prepareDenseScene -i "$SFM" -o dense
stage depthMap av depthMapEstimation -i "$SFM" --imagesFolder dense -o depth
stage depthMapFilter av depthMapFiltering -i "$SFM" --depthMapsFolder depth -o filt
stage meshing av meshing -i "$SFM" --depthMapsFolder filt --output densePointCloud.abc --outputMesh mesh/mesh.obj
stage meshFiltering av meshFiltering -i mesh/mesh.obj -o mesh/meshFiltered.obj
stage texturing av texturing -i densePointCloud.abc --inputMesh mesh/meshFiltered.obj --imagesFolder dense -o tex --textureSide 4096 --useUDIM 0 --colorMappingFileType png --subdivisionTargetRatio 0
[ -n "${SFM_IN:-}" ] || stage exportColmap av exportColmap -i cameras.sfm -o colmap
