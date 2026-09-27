#!/bin/bash
# mkwork.sh <base_work> <new_work> <keep: sfm|dense|mesh>  -- hardlink-copy cached stages
set -e
base=$1; new=$2; keep=$3
rm -rf $new; mkdir -p $new
for d in frames frames_nominal frames_full sfm; do [ -e $base/$d ] && cp -al $base/$d $new/$d; done
cp $base/telemetry.srt $base/frame_id.txt $new/ 2>/dev/null || true
if [ "$keep" != sfm ]; then
  mkdir -p $new/mvs
  ln $base/mvs/scene.mvs $new/mvs/; ln $base/mvs/scene_dense.mvs $new/mvs/; ln $base/mvs/scene_dense.ply $new/mvs/
  for f in $base/mvs/depth*.dmap; do ln $f $new/mvs/; done
  [ "$keep" = mesh ] && ln $base/mvs/scene_dense_mesh.ply $new/mvs/
fi
