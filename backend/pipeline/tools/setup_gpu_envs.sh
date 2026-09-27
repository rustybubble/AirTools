#!/usr/bin/env bash
# Reusable setup for the GPU/ROCm fallback envs (VGGT pose estimator + metric-depth models) on the
# hfbox container. Idempotent-ish: safe to re-run, existing venvs/clones are reused.
#
# Findings this script encodes (see docs/research/p1-gpu-rocm.md for the full writeup):
#   - /opt/venv is ITSELF a venv (base = /usr/bin), so `uv venv --system-site-packages
#     --python /opt/venv/bin/python3.12` does NOT inherit /opt/venv's ROCm torch -- Python venv
#     inheritance walks to the true base prefix (/usr), skipping the intermediate venv layer. Fix:
#     create a plain venv, then drop a .pth file pointing at /opt/venv's site-packages.
#   - Any package whose deps aren't pinned loosely enough (lightglue, accelerate, transformers's
#     [torch] extras) will happily pull a plain CUDA torch/torchvision + a pile of nvidia-*
#     wheels into the venv's OWN site-packages, which then SHADOWS the inherited ROCm torch (local
#     site-packages wins over the .pth-appended path). Always `pip list | grep torch` after
#     installing anything, and uninstall-and-retry with the offending packages if it shows a
#     `+cu`/plain build instead of `+rocm`.
#   - opencv-python (GUI build) and opencv-python-headless collide (shared cv2 namespace) --
#     installing the GUI variant transitively (e.g. via lightglue) breaks headless imports with
#     "libxcb.so.1: cannot open shared object file" in this container (no X11 libs). Force
#     opencv-python-headless with --reinstall if this happens.
#
# Usage: bash setup_gpu_envs.sh [vggt|depth|all]   (default: all)

set -euo pipefail
. /workspace/env.sh

WHAT="${1:-all}"

inherit_rocm_torch() {
    # $1 = venv dir. Appends /opt/venv's site-packages so the venv sees ROCm torch/torchvision
    # without `uv venv --system-site-packages` (which doesn't reach through an intermediate venv).
    local venv_dir="$1"
    local sp_dir
    sp_dir="$(find "$venv_dir/lib" -maxdepth 1 -name 'python3.*' | head -1)/site-packages"
    echo "/opt/venv/lib/python3.12/site-packages" > "$sp_dir/_opt_venv.pth"
}

check_rocm_torch() {
    # $1 = venv python. Fails loudly if torch isn't the ROCm build (catches the shadowing bug above).
    local py="$1"
    "$py" -c "
import torch, sys
v = torch.__version__
ok = 'rocm' in v and torch.cuda.is_available()
print(f'{sys.argv[0] if False else \"\"}torch={v} cuda_available={torch.cuda.is_available()}')
sys.exit(0 if ok else 1)
" || { echo "FATAL: $py's torch is not the ROCm build -- a package reinstalled it. Uninstall the offending package + torch/torchvision/triton/nvidia-*, then retry." >&2; exit 1; }
}

setup_vggt() {
    echo "=== VGGT (fallback pose estimator) ==="
    [ -d /workspace/envs/vggt ] || uv venv --python /opt/venv/bin/python3.12 /workspace/envs/vggt
    inherit_rocm_torch /workspace/envs/vggt
    check_rocm_torch /workspace/envs/vggt/bin/python

    [ -d /workspace/vggt ] || git clone --depth 1 https://github.com/facebookresearch/vggt /workspace/vggt

    # Base package deps, minus torch/torchvision (inherited). numpy/pillow also inherited from
    # /opt/venv (2.5.x/12.x) -- deliberately NOT reinstalled here to avoid the shadowing trap.
    uv pip install --python /workspace/envs/vggt/bin/python \
        einops safetensors "huggingface_hub[hf_xet]" opencv-python-headless scipy trimesh matplotlib
    # pycolmap: PIN to 3.10.0, not the newer 4.2.0 used by the CPU pipeline -- vggt's own
    # np_to_pycolmap.py constructs `pycolmap.Image(id=...)`, a kwarg pycolmap 4.2.0 renamed to
    # `image_id` (confirmed: AttributeError 'pycolmap._core.Image' object has no attribute 'id').
    uv pip install --python /workspace/envs/vggt/bin/python "pycolmap==3.10.0"
    uv pip install --python /workspace/envs/vggt/bin/python --no-deps -e /workspace/vggt

    # demo_colmap.py's dependency chain unconditionally imports lightglue + hydra-core at module
    # load, even without --use_ba (predict_tracks -> vggsfm_utils -> lightglue; vggsfm_tracker ->
    # hydra.utils) -- both are required just to run the script at all, not just for BA.
    uv pip install --python /workspace/envs/vggt/bin/python "git+https://github.com/jytime/LightGlue.git"
    # lightglue's own deps (kornia -> ...) pull a plain-CUDA torch + opencv-python (GUI). Undo it:
    uv pip uninstall --python /workspace/envs/vggt/bin/python torch torchvision triton \
        cuda-pathfinder cuda-toolkit nvidia-cublas nvidia-cuda-cupti nvidia-cuda-nvrtc \
        nvidia-cuda-runtime nvidia-cudnn-cu13 nvidia-cufft nvidia-cufile nvidia-curand \
        nvidia-cusolver nvidia-cusparse nvidia-cusparselt-cu13 nvidia-nccl-cu13 nvidia-nvjitlink \
        nvidia-nvshmem-cu13 nvidia-nvtx cuda-bindings opencv-python 2>/dev/null || true
    uv pip install --python /workspace/envs/vggt/bin/python --reinstall opencv-python-headless
    uv pip install --python /workspace/envs/vggt/bin/python pyceres hydra-core omegaconf
    check_rocm_torch /workspace/envs/vggt/bin/python

    echo "VGGT env ready: /workspace/envs/vggt/bin/python"
    echo "Run with TORCH_HOME=/workspace/.cache/torch set (demo_colmap.py uses torch.hub for its"
    echo "own weight download, default TORCH_HOME=~/.cache/torch is OUTSIDE /workspace and won't"
    echo "persist)."
}

setup_depth() {
    echo "=== Metric depth (MoGe-2 + Depth-Anything-V2-Metric) ==="
    [ -d /workspace/envs/depth ] || uv venv --python /opt/venv/bin/python3.12 /workspace/envs/depth
    inherit_rocm_torch /workspace/envs/depth
    check_rocm_torch /workspace/envs/depth/bin/python

    [ -d /workspace/MoGe ] || git clone --depth 1 https://github.com/microsoft/MoGe /workspace/MoGe
    uv pip install --python /workspace/envs/depth/bin/python --no-deps -e /workspace/MoGe
    # utils3d (PyPI) pulls open3d, which has no cp312 wheel -- use the project's own fork instead,
    # which v2.py already falls back to (`try: import utils3d_moge as utils3d`).
    uv pip install --python /workspace/envs/depth/bin/python \
        click numpy opencv-python-headless scipy pillow matplotlib "trimesh>=4.11" \
        huggingface_hub requests tqdm "utils3d_moge @ git+https://github.com/EasternJournalist/utils3d-moge.git"
    check_rocm_torch /workspace/envs/depth/bin/python

    # transformers (for Depth-Anything-V2-Metric via AutoModelForDepthEstimation) + accelerate
    # ALSO reinstall a plain-CUDA torch transitively -- same fix as lightglue above.
    uv pip install --python /workspace/envs/depth/bin/python transformers accelerate
    uv pip uninstall --python /workspace/envs/depth/bin/python torch torchvision triton \
        cuda-pathfinder cuda-toolkit nvidia-cublas nvidia-cuda-cupti nvidia-cuda-nvrtc \
        nvidia-cuda-runtime nvidia-cudnn-cu13 nvidia-cufft nvidia-cufile nvidia-curand \
        nvidia-cusolver nvidia-cusparse nvidia-cusparselt-cu13 nvidia-nccl-cu13 nvidia-nvjitlink \
        nvidia-nvshmem-cu13 nvidia-nvtx 2>/dev/null || true
    check_rocm_torch /workspace/envs/depth/bin/python

    echo "Depth env ready: /workspace/envs/depth/bin/python"
    echo "MoGe-2: MoGeModel.from_pretrained('Ruicheng/moge-2-vitl-normal')"
    echo "Depth-Anything-V2-Metric: AutoModelForDepthEstimation.from_pretrained('depth-anything/Depth-Anything-V2-Metric-Outdoor-Large-hf')"
}

case "$WHAT" in
    vggt) setup_vggt ;;
    depth) setup_depth ;;
    all) setup_vggt; setup_depth ;;
    *) echo "usage: $0 [vggt|depth|all]" >&2; exit 1 ;;
esac
