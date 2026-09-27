#!/usr/bin/env bash
# gfx1201 wheels for the original 2DGS repo's native extensions (diff-surfel-rasterization,
# simple-knn), from Painter3000/amd-2dgs-rocm72-gfx1201 v1.0.0's qualified source archive
# (payload/source/2dgs-qualified-source-gfx1201.tar.gz, extracted to $B/src/2dgs-release/src).
#   podman run --rm --security-opt label=disable -v $B:/build -v $B/wheels:/wheels:ro \
#       airtools-rocm721 bash /build/build-2dgs.sh
set -euo pipefail
SRC=/build/src/2dgs-release/src/submodules; OUT=/build/out; mkdir -p "$OUT"
[ -x /venv/bin/python ] || python3 -m venv /venv
/venv/bin/pip install -q /wheels/torch-2.14.0+rocm7.2-cp312-cp312-manylinux_2_28_x86_64.whl --no-deps
/venv/bin/pip install -q ninja numpy setuptools wheel typing_extensions filelock sympy networkx jinja2 fsspec
export ROCM_HOME=$ROCM_PATH PYTORCH_ROCM_ARCH=gfx1201 MAX_JOBS=${MAX_JOBS:-4}
for m in simple-knn diff-surfel-rasterization; do
  (cd $SRC/$m && /venv/bin/pip wheel --no-build-isolation --no-deps -w "$OUT" . 2>&1 | tail -3)
done
ls -la "$OUT"
