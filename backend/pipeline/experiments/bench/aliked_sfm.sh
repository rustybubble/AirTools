#!/bin/bash
# aliked_sfm.sh NAME [FEATURE MATCHER] : COLMAP 4.2 CLI SfM on work/dji0095/frames_nominal (the
# pipeline's 175 SfM frames), mirroring sfm.run_sfm (single SIMPLE_RADIAL camera, sequential
# overlap 10, global mapper). Default ALIKED_N16ROT + ALIKED_LIGHTGLUE (ONNX, CPU); pass
# "SIFT SIFT_BRUTEFORCE" for a same-binary SIFT control. Writes runs/NAME.json with stage times.
NAME=$1; FEAT=${2:-ALIKED_N16ROT}; MATCH=${3:-ALIKED_LIGHTGLUE}
C=/workspace/tools/colmap-hip/bin/colmap; T=${THREADS:-12}; P="taskset -c ${CORES:-0-11}"
mkdir -p /workspace/.cache/colmap /root/.cache && ln -sfn /workspace/.cache/colmap /root/.cache/colmap
O=/workspace/exp/sfm-$NAME; rm -rf "${O:?}"; mkdir -p $O/sparse
IMG=/workspace/airtools/work/dji0095/frames_nominal
t0=$(date +%s)
$P $C feature_extractor --database_path $O/db.db --image_path $IMG --ImageReader.single_camera 1 \
  --ImageReader.camera_model SIMPLE_RADIAL --FeatureExtraction.type $FEAT --FeatureExtraction.num_threads $T
t1=$(date +%s)
$P $C sequential_matcher --database_path $O/db.db --FeatureMatching.type $MATCH \
  --FeatureMatching.num_threads $T --SequentialMatching.overlap 10
t2=$(date +%s)
$P $C global_mapper --database_path $O/db.db --image_path $IMG --output_path $O/sparse
t3=$(date +%s)
M=$(ls -d $O/sparse/*/ | head -1)
$C model_analyzer --path $M 2>&1 | tee $O/analyzer.txt
python3 - "$NAME" "$O" $((t1-t0)) $((t2-t1)) $((t3-t2)) "$FEAT" "$MATCH" <<'PY'
import json, re, sys
name, out, fe, ma, mp, feat, match = sys.argv[1:]
a = open(f"{out}/analyzer.txt").read()
g = lambda k: (re.search(k + r"[^\d]*([\d.]+)", a) or [None, None])[1]
json.dump({"name": name, "features": feat, "matcher": match, "extract_s": int(fe), "match_s": int(ma),
           "map_s": int(mp), "registered": g("Registered images"), "points": g("Points"),
           "reproj_px": g("Mean reprojection error"), "track_len": g("Mean track length")},
          open(f"/workspace/bench/runs/{name}.json", "w"), indent=1)
PY
cat /workspace/bench/runs/$NAME.json
