#!/usr/bin/env bash
# OpenMVS `develop` (CPU only; has the multi-view SGM densifier, `--fusion-mode -2`) built from
# Ubuntu 24.04 packages + the small source deps in /opt/omvs-deps (see Containerfile).
#   podman run --rm --security-opt label=disable -v $B:/build -v $B/tools:/workspace/tools \
#       airtools-rocm721 bash /build/build-openmvs.sh
# Copies the apps to /workspace/tools/openmvs-develop/bin (never touches /workspace/tools/openmvs = v2.4.0)
# and bundles the shared libs into lib/ with an $ORIGIN rpath.
set -euo pipefail
# Missing from the image's apt layer; installed here so the shared layers stay as they are.
dpkg -s libboost-exception-dev >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq --no-install-recommends libboost-exception-dev >/dev/null; }
SRC=/build/src/openmvs; BLD=/build/openmvs-build; PREFIX=/workspace/tools/openmvs-develop
# Ubuntu's OpenCV is 4.6; cv::IMWRITE_JPEGXL_QUALITY only exists from 4.11 (.jxl output only).
grep -q 'CV_VERSION_MINOR >= 11' $SRC/libs/Common/Types.inl || perl -0pi -e \
  's/(\s+compression_params.push_back\(cv::IMWRITE_JPEGXL_QUALITY\);\s+compression_params.push_back\(95\);)/\n#if CV_VERSION_MAJOR > 4 || CV_VERSION_MINOR >= 11$1\n#endif/' \
  $SRC/libs/Common/Types.inl
cmake -S $SRC -B $BLD -GNinja -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=/opt/omvs-deps \
  -DOpenMVS_USE_CUDA=OFF -DOpenMVS_USE_PYTHON=OFF -DOpenMVS_BUILD_VIEWER=OFF \
  -DOpenMVS_USE_BREAKPAD=OFF -DOpenMVS_USE_SIFTGPU=OFF -DCMAKE_INSTALL_PREFIX=$PREFIX \
  > /build/openmvs-configure.log 2>&1 || { tail -40 /build/openmvs-configure.log; exit 1; }
# Only the MVS apps: libs/SFM (CreateStructure, ExtractKeyframes, Tests) needs OpenCV >= 4.7
# (cv::SIFT::setContrastThreshold) and Ubuntu 24.04 has 4.6.
APPS="DensifyPointCloud ReconstructMesh RefineMesh TextureMesh TransformScene InterfaceCOLMAP InterfaceMVSNet InterfaceMetashape InterfacePolycam"
time cmake --build $BLD -j${JOBS:-6} --target $APPS
mkdir -p $PREFIX/bin && for a in $APPS; do cp "$(find $BLD/bin -name $a -type f | head -1)" $PREFIX/bin/; done
bash /build/bundle-libs.sh $PREFIX/lib $PREFIX/bin/*
(cd $SRC && git -c safe.directory='*' log -1 --format='%H %ci %s') > $PREFIX/VERSION
$PREFIX/bin/DensifyPointCloud --help 2>&1 | head -5
