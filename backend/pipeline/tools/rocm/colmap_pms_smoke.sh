#!/usr/bin/env bash
# GPU patch_match_stereo smoke test for the HIP COLMAP build, on an N-image slice of a COLMAP
# model (read-only on the inputs).  Usage on hfbox:
#   bash colmap_pms_smoke.sh IMAGES_DIR SPARSE_DIR OUT_DIR [N=12] [MAX_SIZE=1600]
set -euo pipefail
IMG=$1; SPARSE=$2; OUT=$3; N=${4:-12}; MAXS=${5:-1600}
C=${COLMAP:-/workspace/tools/colmap-hip/bin/colmap}
mkdir -p "$OUT/txt"
$C model_converter --input_path "$SPARSE" --output_path "$OUT/txt" --output_type TXT >/dev/null
# images.txt: every other line after the header is "ID QW QX QY QZ TX TY TZ CAM NAME"
awk 'NR>4 && NR%2==1 {print $10}' "$OUT/txt/images.txt" | sort > "$OUT/all.txt"
mid=$(( $(wc -l < "$OUT/all.txt") / 2 ))
sed -n "$((mid+1)),$((mid+N))p" "$OUT/all.txt" > "$OUT/keep.txt"
# (image_deleter + undistorter left only 2 of the 12 images registered in 4.2.0, so instead:
# undistort just the slice from the full model and write patch-match.cfg with explicit sources.)
nice -n 19 $C image_undistorter --image_path "$IMG" --input_path "$SPARSE" \
    --output_path "$OUT/dense" --image_list_path "$OUT/keep.txt" --max_image_size "$MAXS" >/dev/null
while read -r ref; do echo "$ref"; grep -vxF "$ref" "$OUT/keep.txt" | paste -sd, | sed 's/,/, /g'; done \
    < "$OUT/keep.txt" > "$OUT/dense/stereo/patch-match.cfg"
for geom in false true; do
  t0=$(date +%s.%N)
  $C patch_match_stereo --workspace_path "$OUT/dense" --PatchMatchStereo.gpu_index 0 \
      --PatchMatchStereo.max_image_size "$MAXS" --PatchMatchStereo.geom_consistency $geom \
      > "$OUT/pms-$geom.log" 2>&1 || { tail -30 "$OUT/pms-$geom.log"; exit 1; }
  echo "patch_match_stereo geom_consistency=$geom: $(awk "BEGIN{print $(date +%s.%N) - $t0}") s wall"
done
ls "$OUT/dense/stereo/depth_maps" | head -4; echo "depth maps: $(ls "$OUT/dense/stereo/depth_maps" | wc -l)"
nice -n 19 $C stereo_fusion --workspace_path "$OUT/dense" --output_path "$OUT/fused.ply" > "$OUT/fusion.log" 2>&1
grep -i 'number of fused points' "$OUT/fusion.log" || tail -3 "$OUT/fusion.log"
