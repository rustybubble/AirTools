#!/usr/bin/env bash
# Install the ROCm 7.2.1 HIP toolchain under /workspace/rocm on hfbox WITHOUT root package
# installs: download the pinned .debs (rocm-7.2.1-hfbox.urls) and dpkg-deb -x them.
# The big runtime-only libs (hipblaslt, rocblas, rocsolver, rocsparse, rocfft) are left out;
# their -dev headers are kept, and torch's bundled copies supply the .so at run time.
# Usage (on hfbox):  bash fetch-rocm-hfbox.sh rocm-7.2.1-hfbox.urls [/workspace/rocm]
set -euo pipefail
URLS=${1:?url list}; DEST=${2:-/workspace/rocm}
DEBS=/workspace/.cache/rocm-7.2.1-debs; STAGE=$DEST.stage
mkdir -p "$DEBS" "$STAGE"
while read -r u; do
  f=$DEBS/$(basename "$u")
  [ -s "$f" ] || nice -n 19 curl -fsSL --retry 3 -o "$f" "$u"
  nice -n 19 dpkg-deb -x "$f" "$STAGE"
done < "$URLS"
[ -e "$DEST" ] && mv "$DEST" "$DEST.old.$(date +%s)"
mv "$STAGE/opt/rocm-7.2.1" "$DEST"
# Ubuntu system libs the image lacks (libnuma1), vendored next to the ROCm libs.
cp -a "$STAGE"/usr/lib/x86_64-linux-gnu/lib*.so* "$DEST/lib/"
mv "$STAGE" "$DEST/.debs-extract-leftovers"
echo "installed ROCm $(cat "$DEST/.info/version" 2>/dev/null) -> $DEST"
