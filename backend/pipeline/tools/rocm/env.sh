# source /workspace/rocm-env.sh  -- the relocated ROCm 7.2.1 HIP toolchain (hipcc, clang, headers).
export ROCM_PATH=/workspace/rocm ROCM_HOME=/workspace/rocm HIP_PATH=/workspace/rocm
export PATH=/workspace/rocm/bin:/workspace/rocm/llvm/bin:$PATH
export LD_LIBRARY_PATH=/workspace/rocm/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
export CMAKE_PREFIX_PATH=/workspace/rocm${CMAKE_PREFIX_PATH:+:$CMAKE_PREFIX_PATH}
export PYTORCH_ROCM_ARCH=gfx1201 HIP_ARCHITECTURES=gfx1201
