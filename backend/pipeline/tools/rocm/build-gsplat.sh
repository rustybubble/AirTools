#!/usr/bin/env bash
# Build the gfx1201 gsplat wheel (Painter3000/amd-gsplat-rocm72-gfx1201 @ e2f3319) inside the
# airtools-rocm721 image, against torch 2.14.0+rocm7.2 (the exact wheel /opt/venv has on hfbox).
# Host side (laptop, no GPU needed):
#   podman run --rm -v $B:/build -v $B/wheels:/wheels:ro airtools-rocm721 bash /build/build-gsplat.sh
# where $B/src/gsplat-rocm is a recursive clone at e2f3319. Output: $B/out/gsplat-*.whl
set -euo pipefail
SRC=/build/src/gsplat-rocm; OUT=/build/out; mkdir -p "$OUT"
[ -x /venv/bin/python ] || python3 -m venv /venv
/venv/bin/pip install -q /wheels/torch-2.14.0+rocm7.2-cp312-cp312-manylinux_2_28_x86_64.whl --no-deps
/venv/bin/pip install -q ninja numpy rich jaxtyping setuptools wheel typing_extensions filelock \
    sympy networkx jinja2 fsspec
cmake -S $SRC/gsplat/cuda/csrc/third_party/glm -B /tmp/glm -DGLM_BUILD_TESTS=OFF \
    -DBUILD_SHARED_LIBS=OFF -DCMAKE_INSTALL_PREFIX=$HOME/.local >/dev/null
cmake --build /tmp/glm -j4 >/dev/null && cmake --install /tmp/glm >/dev/null
cd $SRC
# setup.py picks the arch from `rocminfo` (no GPU here -> falls back to gfx942); honour the env.
grep -q PYTORCH_ROCM_ARCH setup.py || \
  sed -i 's/gpu_arch = get_rocm_arch()/gpu_arch = os.environ.get("PYTORCH_ROCM_ARCH") or get_rocm_arch()/' setup.py
export ROCM_HOME=$ROCM_PATH PYTORCH_ROCM_ARCH=gfx1201 MAX_JOBS=${MAX_JOBS:-4}
time /venv/bin/pip wheel --no-build-isolation --no-deps -w "$OUT" -v . 2>&1 | tail -40
ls -la "$OUT"
