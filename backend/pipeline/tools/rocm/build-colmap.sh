#!/usr/bin/env bash
# COLMAP 4.2.0 (be5e291) with HIP_ENABLED (GPU patch_match_stereo, gfx1201) and ONNX (ALIKED /
# LightGlue learned features), headless. Runs inside the airtools-rocm721 image:
#   podman run --rm --security-opt label=disable -v $B:/build -v $B/tools:/workspace/tools \
#       airtools-rocm721 bash /build/build-colmap.sh
# Installs to /workspace/tools/colmap-hip (same path as on hfbox), then bundles the non-ROCm,
# non-glibc shared libs into lib/ with an $ORIGIN rpath so the tree is copy-and-run on hfbox.
set -euo pipefail
# FindDependencies.cmake calls find_package(OpenGL/Glew) even with OPENGL_ENABLED=OFF; CGAL (5.6,
# Ubuntu) is for delaunay_mesher. Installed here so the shared image layers stay as they are.
dpkg -s libglew-dev >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq --no-install-recommends libgl-dev libglew-dev libcgal-dev >/dev/null; }
SRC=/build/src/colmap; BLD=/build/colmap-build; PREFIX=/workspace/tools/colmap-hip
cmake -S $SRC -B $BLD -GNinja -DCMAKE_BUILD_TYPE=Release \
  -DCUDA_ENABLED=OFF -DHIP_ENABLED=ON -DCMAKE_HIP_ARCHITECTURES=gfx1201 \
  -DCMAKE_HIP_COMPILER=$ROCM_PATH/llvm/bin/clang++ -DCMAKE_PREFIX_PATH=$ROCM_PATH \
  -DGUI_ENABLED=OFF -DOPENGL_ENABLED=OFF -DONNX_ENABLED=ON -DIPO_ENABLED=OFF -DCCACHE_ENABLED=OFF \
  -DTESTS_ENABLED=OFF -DCMAKE_INSTALL_PREFIX=$PREFIX > /build/colmap-configure.log 2>&1 || { tail -40 /build/colmap-configure.log; exit 1; }
# PoissonRecon.cpp alone needs ~4 GB in cc1plus; on the 15 GB laptop it was OOM-killed at -j4 and
# -j2, so build it by itself first.
time cmake --build $BLD -j1 --target colmap_poisson_recon
time cmake --build $BLD -j${JOBS:-4}
cmake --install $BLD >/dev/null
bash /build/bundle-libs.sh $PREFIX/lib $PREFIX/bin/colmap
$PREFIX/bin/colmap -h | head -5
