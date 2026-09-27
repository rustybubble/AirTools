#!/usr/bin/env bash
# S3: PlanarSplatting (ant-research/PlanarSplatting @ ec626fd) on hfbox's RX 9070 XT (gfx1201).
# Builds its two CUDA extensions with torch's hipify against /opt/venv's torch 2.14 ROCm 7.2 and
# the rootless ROCm SDK (docs/research/bench/e2-rocm-builds.md), on hfbox itself (~1 min).
# The venv inherits /opt/venv's torch through a .pth (no CUDA torch is ever pulled).
set -euo pipefail
source /workspace/env.sh
source /workspace/rocm-env.sh
R=${R:-/workspace/src/psplat}
E=${E:-/workspace/envs/psplat}
[ -d "$R" ] || git clone --recurse-submodules --depth 1 https://github.com/ant-research/PlanarSplatting "$R"
[ -x "$E/bin/python" ] || {
  uv venv --python /opt/venv/bin/python3.12 "$E"
  echo /opt/venv/lib/python3.12/site-packages > "$E/lib/python3.12/site-packages/_opt_venv.pth"
}
# open3d 0.20's legacy ScalableTSDFVolume returns an empty mesh (E3); PlanarSplatting's init uses it
uv pip install --python "$E/bin/python" ninja setuptools wheel numpy pyhocon loguru open3d==0.19.0 \
  plyfile numpy-quaternion trimesh opencv-python-headless matplotlib tqdm pycolmap

S=$R/submodules
# HIP fixes, all in the CUDA sources (torch re-hipifies them on every build):
# 1. no device_launch_parameters.h / cooperative_groups/reduce.h in HIP (both unused)
sed -i '/device_launch_parameters.h/d; /cooperative_groups\/reduce.h/d' \
  $S/quaternion-utils/quat_convert.h $S/diff-rect-rasterization/cuda_rasterizer/*.h \
  $S/diff-rect-rasterization/cuda_rasterizer/*.cu
# 2. "<< <" / ">> >" kernel launches (MSVC style) do not parse in clang
sed -i 's/<< </<<</g; s/>> >/>>>/g' $S/quaternion-utils/*.cu $S/diff-rect-rasterization/cuda_rasterizer/*.cu
# 3. every kernel is launched 1-D and non-cooperatively: plain index instead of cg::this_grid()
sed -i 's/cg::this_grid().thread_rank()/(blockIdx.x * blockDim.x + threadIdx.x)/' \
  $S/diff-rect-rasterization/cuda_rasterizer/*.cu
# 4. __trap is CUDA-only
sed -i 's/__trap();/__builtin_trap();/' $S/diff-rect-rasterization/cuda_rasterizer/auxiliary.h
# 5. the bundled glm's TMax functor is host-only under HIP clang: component-wise fmaxf instead
sed -i 's/return glm::max(result, 0.0f);/return glm::vec3(fmaxf(result.x, 0.0f), fmaxf(result.y, 0.0f), fmaxf(result.z, 0.0f));/' \
  $S/diff-rect-rasterization/cuda_rasterizer/forward.cu
export MAX_JOBS=4
for m in quaternion-utils diff-rect-rasterization; do
  (cd $S/$m && nice -n 10 uv pip install --python "$E/bin/python" --no-build-isolation --no-deps .)
done

# pytorch3d (only knn_points K=1 in merge_util), pyrender and rerun (imported, unused with
# pre_align off) are stubbed instead of installed
SP=$E/lib/python3.12/site-packages
mkdir -p $SP/pytorch3d && touch $SP/pytorch3d/__init__.py
cat > $SP/pytorch3d/ops.py <<'EOF'
"""S3 stub: pytorch3d.ops.knn_points (K=1 only, the one PlanarSplatting use), chunked torch.cdist."""
from collections import namedtuple
import torch
_KNN = namedtuple("KNN", "dists idx knn")
def knn_points(p1, p2, K=1, **kw):
    assert K == 1 and p1.shape[0] == 1
    d, i = [], []
    for c in torch.split(p1[0], 8192):
        dd = torch.cdist(c, p2[0]).min(1)
        d.append(dd.values ** 2); i.append(dd.indices)
    return _KNN(torch.cat(d)[None, :, None], torch.cat(i)[None, :, None], None)
EOF
echo '"""S3 stub: imported at module level; only the depth pre-align (off) uses it."""' > $SP/pyrender.py
echo '"""S3 stub: imported, unused, by the PlanarSplatting trainer."""' > $SP/rerun.py
"$E/bin/python" -c "import diff_rect_rasterization, quaternion_utils._C; print('psplat HIP build OK')"
