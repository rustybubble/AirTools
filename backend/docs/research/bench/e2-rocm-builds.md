# E2: a ROCm/HIP toolchain and GPU builds for hfbox (gfx1201)

Build log for the 2026-09-25 bench session. hfbox had no HIP compiler: no `/opt/rocm`, no
`hipcc`, only the runtime libraries bundled inside torch. So gsplat, the 2DGS rasterisers, COLMAP
`HIP_ENABLED` and OpenMVS `develop` could not be built. Everything below was compiled on the laptop
(Fedora, podman, no GPU) in an Ubuntu 24.04 / Python 3.12 container. Only the finished artefacts,
and the ROCm debs themselves, went to hfbox. **No heavy compile ran on hfbox** (E1's timed benchmarks
were running); hfbox saw only downloads, `dpkg-deb -x` and GPU smoke tests under `nice -n 19`.

Scripts: `pipeline/tools/rocm/`. Laptop build root: `$B=/home/sdash/rocmbuild/workspace` (sources in
`$B/src`, wheels in `$B/out`, relocatable trees in `$B/tools`).

## 0. Summary

| # | Deliverable | Status | Path on hfbox | Verified by | Time |
|---|---|---|---|---|---|
| 1 | ROCm 7.2.1 HIP toolchain | **works** | `/workspace/rocm`, `source /workspace/rocm-env.sh` | `hipcc` builds and runs a saxpy kernel on the RX 9070 XT | ~15 min |
| 2 | gsplat (3DGS + **2DGS**) for gfx1201 | **works** | venv `/workspace/envs/gsplat` | the port's 23 fwd/bwd checks + our own 3DGS/2DGS fit test on the GPU | ~25 min |
| 3 | COLMAP 4.2.0 `HIP_ENABLED` + ONNX | **works, correct, but slow**: see the verdict below | `/workspace/tools/colmap-hip/bin/colmap` | GPU `patch_match_stereo` on 12 kitchen views; depth within median 1.3–3.2 % of SfM points; ALIKED + LightGlue run | ~90 min (the time-box) |
| 4a | OpenMVS `develop` (CPU, multi-view SGM) | **works** (MVS apps only) | `/workspace/tools/openmvs-develop/bin/` | `--help` lists `--fusion-mode -2`; SGM and PatchMatch densify a 12-view slice | ~35 min |
| 4b | original 2DGS: `diff-surfel-rasterization` + `simple-knn` | **works** (extensions verified; the trainer runs) | venv `/workspace/envs/2dgs`, repo `/workspace/src/2dgs-rocm` | kNN, fit and depth checks; 1000 training iterations | ~15 min |
| 4c | OpenMVS with GPU | not attempted: OpenMVS has a CUDA backend only, with no HIP port (`r2-oss-industry.md`) | — | — | — |

**The finding that matters for densify speed: COLMAP's HIP PatchMatch is not a speed win on
this box.**
- Measured: 78 s per view for the photometric pass alone at 1600 px with 11 source views, and a
  geometric pass would roughly double that.
- For comparison, OpenMVS CPU densify runs the whole kitchen at about 9 s per view (27 min for
  175 views).
- The upstream PR's own number (RX 7900 XTX: 86 min for 218 maps at 1280×720, ~24 s per map) says
  this is how the HIP port performs, not a gfx1201 fault.
- One caveat: our GPU was shared with another agent's Vulkan job (`spirula`, 97–99 % GPU busy on
  its own). So treat 78 s as an upper bound. Even uncontended, it stays well above OpenMVS CPU.
- The OpenMVS `develop` SGM (§4.1) is the better lead for densify speed.

## 1. The ROCm HIP toolchain: `/workspace/rocm`

**Which ROCm.** `torch.version.hip` on hfbox is `7.2.53211` and `torch.version.rocm` is `7.2.1`
(torch `2.14.0+rocm7.2`, git `08187d9e`). The apt repo's `hip-runtime-amd` is `7.2.26015` in ROCm
7.2.0 and `7.2.53211` from 7.2.1 on. So the toolchain is **ROCm 7.2.1**, the release torch was built
against.

**Candidates checked.**
- TheRock / `rocm-sdk` pip packages (`repo.amd.com/rocm/whl/gfx120X-all/`): `rocm-sdk-devel` exists
  only as 7.9 to 7.13. **There is no 7.2**, so it cannot match torch's runtime. Rejected.
- Copying `/opt/rocm` out of a container: works, but the laptop to hfbox link measured ~1.1 MB/s
  (50 MB in 45 s), so a 3 GB tree would take about 45 min.
- **Chosen: download the pinned ROCm 7.2.1 `.deb`s on hfbox itself and unpack them with
  `dpkg-deb -x`.** hfbox has fast internet, `dpkg-deb` needs no root and installs nothing
  system-wide, and the result is the same files an apt install writes to `/opt/rocm-7.2.1`, moved to
  `/workspace/rocm`. ROCm 7.x resolves its paths relative to its own binaries, so the move is safe.

**Package set.** `hipcc hip-dev rocm-llvm rocm-device-libs rocm-cmake rocminfo rocm-core hiprand-dev
rocrand-dev rocprim-dev hipcub-dev rocthrust-dev hipblas-dev hipblas-common-dev hipsparse-dev
hipsolver-dev hipblaslt-dev rocblas-dev rocsparse-dev rocsolver-dev hipfft-dev rocfft-dev`, plus the
dependency closure from `apt-get install --print-uris` in an `ubuntu:24.04` container with the
repo added. The pinned list is `pipeline/tools/rocm/rocm-7.2.1-hfbox.urls` (35 debs, 632 MB).
- Left out: the runtime-only `hipblaslt` (1.6 GB), `rocsolver`, `rocsparse`, `rocblas` and `rocfft`.
  torch ships its own copies of those, and nothing we build links them. Their `-dev` headers are kept,
  because torch's C++ extension headers include them.
- Added: Ubuntu's `libnuma1`. hfbox's image lacks it, and `libamdhip64` needs it.

**Failure 1: the Ubuntu-universe hipcc.** Without apt pinning, `apt-get --print-uris` resolved
`hipcc`, `rocminfo`, `rocm-cmake` and `libhsa-runtime64` to **Ubuntu's own ROCm 5.7 packages**.
Their version strings (`5.7.1`) sort above AMD's (`1.1.1.70201`). The first unpack had no
`bin/hipcc`. The fix is AMD's documented pin, `Pin: release o=repo.radeon.com` with
`Pin-Priority: 600`, applied when generating the list. `Containerfile` uses the same pin.

**Install (hfbox, as root, no apt):**

```bash
scp pipeline/tools/rocm/{fetch-rocm-hfbox.sh,rocm-7.2.1-hfbox.urls} hfbox:/workspace/
scp pipeline/tools/rocm/env.sh hfbox:/workspace/rocm-env.sh
ssh hfbox 'cd /workspace && nice -n 19 bash fetch-rocm-hfbox.sh rocm-7.2.1-hfbox.urls'
# -> "installed ROCm 7.2.1 -> /workspace/rocm"   (2.9 GB; debs cached in /workspace/.cache/rocm-7.2.1-debs)
```

**Use:** `source /workspace/rocm-env.sh`. It sets `ROCM_PATH`, `ROCM_HOME` and `HIP_PATH` to
`/workspace/rocm`, puts `bin` and `llvm/bin` on `PATH`, adds `lib` to `LD_LIBRARY_PATH`, and sets
`PYTORCH_ROCM_ARCH=gfx1201`. torch's `cpp_extension` then finds the compiler through `ROCM_HOME`,
so JIT extensions build on hfbox too (keep those small, and `nice` them).

**Verification (hfbox):**

```
$ source /workspace/rocm-env.sh; hipcc --version
HIP version: 7.2.53211-e1a6bc5663
AMD clang version 22.0.0git (https://github.com/RadeonOpenCompute/llvm-project roc-7.2.1 26084 f58b06dce1f9...)
$ hipcc --offload-arch=gfx1201 pipeline/tools/rocm/hip_smoke.hip -o hip_smoke && ./hip_smoke   # 1.5 s compile
device: AMD Radeon RX 9070 XT (gfx1201), warpSize 32
saxpy n=1048576 mismatches=0 -> PASS
$ rocminfo | grep -E "Marketing|Name: +gfx"
  Name:                    gfx1201
  Marketing Name:          AMD Radeon RX 9070 XT
```

`ldd` over every binary and `.so` in `/workspace/rocm` finds no missing library, except
`librocblas`, `librocsolver`, `librocsparse` and `librocfft` for the hip* wrapper libraries that
were deliberately left out. If something ever needs them, append
`/opt/venv/lib/python3.12/site-packages/torch/lib` to `LD_LIBRARY_PATH`, or add the four debs to the
URL list.

**The laptop build image** (`pipeline/tools/rocm/Containerfile`, tag `airtools-rocm721`) holds the
same ROCm 7.2.1 set via apt. It is copied to `/workspace/rocm` inside the image, and `/opt/rocm*` is
deleted, so every build resolves ROCm at the path it will have on hfbox. It also holds the COLMAP
and OpenMVS build dependencies (§3, §4).

```bash
cd pipeline/tools/rocm && podman build -t airtools-rocm721 .   # ~3.7 GB of ROCm debs; ~25 min
```

Fedora gotchas:
- Bind mounts need `--security-opt label=disable`, because SELinux otherwise gives "Permission
  denied" on `/build/*.sh`.
- podman's default OCI image format ignores `SHELL`. The dependency `RUN` is written for `/bin/sh`
  (dash). Switching to `--format docker` invalidated the whole layer cache.

## 2. gsplat for gfx1201: `/workspace/envs/gsplat`

**Source:** [Painter3000/amd-gsplat-rocm72-gfx1201](https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201)
`e2f3319` (2026-08-02). It is AMD-Ecosystem's gsplat 1.5.3 HIP port plus Wave32/gfx1201 fixes,
validated upstream on an R9700 with torch 2.13. The package name is `amd_gsplat`, and the import is
`gsplat`.

**Build (laptop, 10 min on 4 jobs):**

```bash
git clone --recurse-submodules https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201 $B/src/gsplat-rocm
git -C $B/src/gsplat-rocm checkout e2f3319
curl -Lo $B/../wheels/torch-2.14.0+rocm7.2-cp312-cp312-manylinux_2_28_x86_64.whl \
  https://download.pytorch.org/whl/rocm7.2/torch-2.14.0%2Brocm7.2-cp312-cp312-manylinux_2_28_x86_64.whl
podman run --rm --security-opt label=disable -v $B:/build -v $B/../wheels:/wheels:ro \
  airtools-rocm721 bash /build/build-gsplat.sh        # = pipeline/tools/rocm/build-gsplat.sh
# -> $B/out/amd_gsplat-1.5.3+e2f3319-cp312-cp312-linux_x86_64.whl  (9.3 MB; 40 gfx1201 code objects)
```

- The torch wheel is byte-for-byte the build on hfbox (same git `08187d9e`), so the ABI matches.
- **Port bug, patched in the script:** `setup.py` takes the GPU arch from `rocminfo`. With no GPU it
  silently falls back to **gfx942** (MI300). The script patches it to honour `PYTORCH_ROCM_ARCH`,
  and the wheel carries only `amdgcn-amd-amdhsa--gfx1201` code objects (checked with `strings`).
- GLM goes to `$HOME/.local`, as the port's README says.
- The port was written for torch 2.13. It compiled against 2.14 unchanged.

**Install on hfbox** (a venv that inherits `/opt/venv`'s ROCm torch through a `.pth` file, the same
trick as `setup_gpu_envs.sh`):

```bash
scp $B/out/amd_gsplat-1.5.3+e2f3319-cp312-cp312-linux_x86_64.whl hfbox:/workspace/.cache/
ssh hfbox '. /workspace/env.sh; E=/workspace/envs/gsplat
  uv venv --python /opt/venv/bin/python3.12 $E
  echo /opt/venv/lib/python3.12/site-packages > $E/lib/python3.12/site-packages/_opt_venv.pth
  uv pip install --python $E/bin/python --no-deps /workspace/.cache/amd_gsplat-1.5.3+e2f3319-cp312-cp312-linux_x86_64.whl
  uv pip install --python $E/bin/python jaxtyping rich'
```

**Use:** `/workspace/envs/gsplat/bin/python`, then `from gsplat import rasterization,
rasterization_2dgs`. The runtime needs no ROCm SDK, because the extension links torch's
`libamdhip64`. To run `examples/simple_trainer*.py`, also install the example requirements with
`--no-deps` (`tyro`, `viser`, `nerfview`, `torchmetrics`, `imageio`, `pycolmap`, and so on), without
letting pip replace torch (`p1-gpu-rocm.md`). The examples and `tests/rocm` are unpacked at
`/workspace/src/gsplat-rocm-tests`.

**Verification (hfbox GPU):**

```
$ python -c "import gsplat; from gsplat import csrc"  ->  gsplat 1.5.3, csrc.so in the venv, GSPLAT_IMPORT: PASS
$ python tests/rocm/gsplat_remaining_paths_validation.py          (8.6 s)
GSPLAT_REMAINING_PATHS_VALIDATION: PASS
15/15 Forward/Backward-Laeufe ohne NaN/Inf/fehlende Gradienten.   (3DGS + 2DGS, packed/unpacked, RGB/D/ED, 2DGS median/normals/distort)
$ python tests/rocm/gsplat_multicamera_packed_validation.py
GSPLAT_MULTICAMERA_PACKED_VALIDATION: PASS  8/8
$ python pipeline/tools/rocm/gsplat_smoke.py        # ours: fit 400 splats to a 128x128 target, Adam, 300 steps
3DGS fit: PSNR 18.3 -> 51.0 dB in 8.3s PASS
2DGS fit: PSNR 20.0 -> 40.7 dB in 3.1s PASS
GSPLAT_SMOKE: PASS
```

- The port's own checks prove **internal consistency only**: packed against unpacked, and paths
  against each other. It says so itself: "not CUDA/ROCm parity".
- `gsplat_smoke.py` is the independent check. If forward or backward were wrong, the loss would not
  fall 20 to 30 dB.
- A parity check against gsplat's pure-torch `_rasterization` needs `nerfacc`, which is not
  installed. It was skipped.
- One run hit that `nerfacc` `ImportError` and then printed `terminate called without an active
  exception` at interpreter exit. Clean runs exit 0 (two repeats).
- **Not validated, per the port:** SH rendering (`sh_degree>0`), `rasterize_mode="antialiased"`, and
  the 2DGS distortion loss's numerics against CUDA. Start the kitchen with `--sh_degree 0`.

## 3. COLMAP 4.2.0 with `HIP_ENABLED`: `/workspace/tools/colmap-hip`

**Source:** tag `4.2.0` (`be5e291`), which includes PR #4420 (HIP `patch_match_stereo`).

**Configuration** (`pipeline/tools/rocm/build-colmap.sh`):

```
-DCUDA_ENABLED=OFF -DHIP_ENABLED=ON -DCMAKE_HIP_ARCHITECTURES=gfx1201
-DCMAKE_HIP_COMPILER=/workspace/rocm/llvm/bin/clang++ -DGUI_ENABLED=OFF -DOPENGL_ENABLED=OFF
-DONNX_ENABLED=ON (FETCH_ONNX: onnxruntime 1.27.1, CPU) -DIPO_ENABLED=OFF -DCMAKE_INSTALL_PREFIX=/workspace/tools/colmap-hip
```

- Dependencies: Ubuntu 24.04 packages (Boost 1.83, Ceres 2.2, OpenImageIO 2.4, CGAL 5.6, SuiteSparse,
  glog, and so on). PoseLib and faiss come in through COLMAP's own FetchContent.
- **ONNX is enabled.** It is essentially free, because COLMAP fetches the prebuilt onnxruntime. That
  gives the ALIKED and LightGlue paths the pycolmap 4.2 wheel aborts on ("requires ONNX support").

```bash
podman run --rm --security-opt label=disable -e JOBS=3 -v $B:/build -v $B/tools:/workspace/tools \
  airtools-rocm721 bash /build/build-colmap.sh       # 404 ninja steps; ~25 min at -j3
```

Failures on the way, all fixed in the script:
1. `find_package(OpenGL)` and `Glew` are unconditional, even with `OPENGL_ENABLED=OFF`. The script
   installs `libgl-dev libglew-dev` at build time.
2. `CGALConfig.cmake` was not found, because `libcgal-dev` was missing from the image. The script
   installs it at build time.
3. **OOM kills.** `PoissonRecon.cpp` alone takes about 4 GB in `cc1plus`. On the 15 GB laptop, where
   zram swap was full, it was killed at `-j8`, `-j5` and `-j2`. The script now builds
   `colmap_poisson_recon` alone at `-j1`, then the rest at `-j3`.

**Packaging.**
- `bundle-libs.sh` copies every non-glibc, non-ROCm `.so` the binary needs into `lib/` (299 libs,
  because OpenImageIO drags in GDAL, OpenCV, ffmpeg and more). It then sets the binary's rpath to
  `$ORIGIN/../lib:/workspace/rocm/lib`.
- The package drops the static libs and headers: `colmap-hip.tar.zst` is 123 MB, 410 MB unpacked.
- hfbox has no `zstd`. It was unpacked from Ubuntu's `zstd` deb with `dpkg-deb -x` into
  `/workspace/bin/zstd`.

```bash
scp $B/../colmap-hip.tar.zst hfbox:/workspace/.cache/                       # ~2 min at ~1 MB/s
ssh hfbox 'cd /workspace/tools && zstd -dc /workspace/.cache/colmap-hip.tar.zst | tar x'
```

**Use:** `/workspace/tools/colmap-hip/bin/colmap ...`. It needs no environment: the rpath finds
`/workspace/rocm/lib`, which provides `libamdhip64`, `libhiprand` and `librocrand`. The version
line reads `COLMAP 4.2.0 (Commit be5e291 on 2026-08-31 with HIP)`. Two points about ALIKED and
LightGlue:
- COLMAP downloads the ONNX models to `$HOME/.cache/colmap`, and `/root` does not persist. Run
  `mkdir -p /workspace/.cache/colmap /root/.cache && ln -sfn /workspace/.cache/colmap
  /root/.cache/colmap` once per container start. The models are cached there already.
- These run on the **CPU**, because the fetched onnxruntime has no ROCm or MIGraphX execution
  provider.

**Verification on hfbox** (`colmap_pms_smoke.sh`, `pms_check.py`; inputs read-only from
`/workspace/bench/ref`; outputs in `/workspace/exp/colmap-hip-smoke`):

1. The slice is 12 consecutive registered frames (`0414`–`0447`, 0.3 s apart) from
   `ref/sfm/sparse/0`. `image_undistorter --image_list_path` undistorts them to 1600 px. The script
   writes `patch-match.cfg` itself, with each view's 11 siblings as sources.
   - Using `image_deleter` to cut the model to 12 images, then undistorting, left only 2 images
     registered in 4.2.0. That is why the script takes the route above.
2. **Photometric pass (GPU): 12/12 depth and normal maps, 939.8 s (78 s per view,
   ~16 s per PatchMatch iteration, 5 iterations).** The RX 9070 XT showed 100 % busy. The
   `spirula` Vulkan job was running at the same time and keeps the GPU at 97–99 % busy on its own,
   so this is an upper bound.
   - The geometric pass was stopped after starting, to give the GPU back. It would cost about the
     same again.
3. **Correctness: an unfiltered photometric pass (`--PatchMatchStereo.filter false`), 3 views at
   800 px, 125 s.** Its depth maps agree with the SfM points each view observes:

   ```
   0417.jpg: valid=99.5%  sfm pts with depth=297  median rel err=0.027  within 5%=76.8%
   0414.jpg: valid=99.9%  sfm pts with depth=305  median rel err=0.032  within 5%=71.5%
   0420.jpg: valid=99.7%  sfm pts with depth=725  median rel err=0.013  within 5%=91.6%
   ```

   So the gfx1201 kernels are **correct**. PR #4420 had tested only gfx1100 and gfx90a.
4. With the default filter, only 2.7–7 % of pixels survive on this slice. `stereo_fusion
   --input_type photometric` then fuses 1 point. This is the data, not the build: 12 frames over
   3.3 s give triangulation angles mostly below the 3° filter. A real test needs the full,
   wider-baseline frame set plus the geometric pass.
5. **ONNX features:** 4 images, `feature_extractor --FeatureExtraction.type ALIKED_N16ROT` (2 CPU
   threads) took 11 s and gave 629–1070 keypoints per image. `exhaustive_matcher
   --FeatureMatching.type ALIKED_LIGHTGLUE` took 5 s, and all 6 pairs verified with 398–637
   inliers.

**Verdict for densify speed.** HIP PatchMatch would have to run under ~10 s per view to beat
OpenMVS CPU. It runs at 78 s per view (contended), or ~20 s per view if we extrapolate the PR's
7900 XTX figure to our settings. **Not worth pursuing for speed.** It stays useful as a
classic-MVS depth source on the GPU when the CPU is busy. Also left open: the fusion-to-OpenMVS seam
from `p1-colmap-realityscan.md` §1.3.

## 4. Optional items

### 4.1 OpenMVS `develop` (CPU, multi-view SGM): `/workspace/tools/openmvs-develop`

**Source:** `develop` at `e23d25f` (2026-09-16, "sgm: multi-view SGM densification ... (#1306)").
The binaries still print `OpenMVS x64 v2.4.0 (e23d25f*)`: `develop` has not bumped the version,
and the `*` marks the one-line patch below. Nothing under `/workspace/tools/openmvs` (v2.4.0) was
touched.

**Build:** not vcpkg (upstream's route, which builds OpenCV, Ceres and CGAL from source, 30+ min).
It uses Ubuntu 24.04 packages plus the small `develop`-only dependencies, built in the image at the
same versions as `develop`'s vcpkg ports, into `/opt/omvs-deps`:
- CGAL v6.0.1, TinyEXIF 1.1.0, TinyNPY v1.1, tinyply 3.0, tinygltf v2.9.6;
- BS::thread_pool v5.0.0, halfmesh v0.4.0, and PoseLib `ccdd2f6`.

```bash
podman run --rm --security-opt label=disable -e JOBS=4 -v $B:/build -v $B/tools:/workspace/tools \
  airtools-rocm721 bash /build/build-openmvs.sh     # 46 steps, 3.5 min
# -DOpenMVS_USE_CUDA=OFF -DOpenMVS_USE_PYTHON=OFF -DOpenMVS_BUILD_VIEWER=OFF (IPO on, the default)
```

Failures, all handled in the script or the Containerfile:
- Missing `boost_exception` CMake config: the script installs `libboost-exception-dev`.
- tinyply 2.3.4 is too old for halfmesh (`const tinyply::Buffer`): bumped to 3.0, the vcpkg port's
  version.
- PoseLib `ccdd2f6` is on no branch of the fork any more, so `git checkout` fails: fetched as a
  codeload tarball instead.
- `cv::IMWRITE_JPEGXL_QUALITY` does not exist in OpenCV 4.6: guarded with
  `#if CV_VERSION_MINOR >= 11`. It only affects `.jxl` output.
- **`libs/SFM` needs OpenCV ≥ 4.7** (`cv::SIFT::setContrastThreshold`). So the script builds only
  the MVS apps: `DensifyPointCloud ReconstructMesh RefineMesh TextureMesh TransformScene
  InterfaceCOLMAP InterfaceMVSNet InterfaceMetashape InterfacePolycam`. **Not built:**
  `CreateStructure` (with `--roma2`), `ExtractKeyframes` and `Tests`. Those need the vcpkg OpenCV.

**Package:** 9 binaries plus 141 bundled libs (171 MB), shipped as `openmvs-develop.tar.zst`
(60 MB). The rpath is `$ORIGIN/../lib`, so it needs no environment.

**Use:**
- Point the pipeline at it with `AIRTOOLS_OPENMVS_DIR=/workspace/tools/openmvs-develop`, then run
  `DensifyPointCloud ... --fusion-mode -2` for SGM.
- The v2.4.0 flags still apply. `develop`'s fusion threshold default is already 1.0 (#1298).

**Verification on hfbox** (`openmvs_smoke.sh`: the same 12-view slice, `--resolution-level 1`,
`--max-threads 4`, `nice -n 19`, on a box loaded by E1):

```
$ DensifyPointCloud --help | grep -A1 fusion-mode
  --fusion-mode arg (=0)   depth-maps fusion mode (-2 - SGM depth-maps & fusion, -1 - export SGM pair disparity-maps only, 0 - ...
fusion-mode -2 (SGM):        10.2 s,  12 depth-maps,  1,047 points
fusion-mode  0 (PatchMatch): 28.9 s,  12 depth-maps, 23,815 points
```

- SGM is 2.8× faster here, in line with the design doc's 2.7–3.3×.
- But it kept 1/23 of the points on this low-baseline slice. The design doc also notes SGM "has no
  geometric-consistency filter" and loses recall. **The quality verdict needs E1's full-kitchen
  bench.** Fairness caveat: this build links Ubuntu's OpenCV 4.6 and the v2.4.0 release links
  vcpkg's newer OpenCV. So compare SGM against PatchMatch **from this same binary**, not against the
  v2.4.0 timings.

### 4.2 The original 2DGS extensions: `/workspace/envs/2dgs`

**Source:** Painter3000/amd-2dgs-rocm72-gfx1201 release **v1.0.0**, bundle
`amd-2dgs-rocm72-gfx1201-public-release.tar.gz`. `SHA256SUMS` checked OK. Its
`payload/source/2dgs-qualified-source-gfx1201.tar.gz` holds the ported 2DGS tree, with
diff-surfel-rasterization `6bb0a9f` and simple-knn `a88e3e2`. The port's installer, which enforces
torch 2.13, was not used. The two extensions are plain torch `CUDAExtension`s that torch hipifies,
built with `PYTORCH_ROCM_ARCH=gfx1201` against torch 2.14:

```bash
podman run --rm --security-opt label=disable -e MAX_JOBS=3 -v $B:/build -v $B/../wheels:/wheels:ro \
  airtools-rocm721 bash /build/build-2dgs.sh          # ~6 min; -> simple_knn-0.0.0-*.whl, diff_surfel_rasterization-0.0.1-*.whl
ssh hfbox '. /workspace/env.sh; E=/workspace/envs/2dgs; uv venv --python /opt/venv/bin/python3.12 $E
  echo /opt/venv/lib/python3.12/site-packages > $E/lib/python3.12/site-packages/_opt_venv.pth
  uv pip install --python $E/bin/python --no-deps /workspace/.cache/{simple_knn,diff_surfel_rasterization}-*.whl
  uv pip install --python $E/bin/python open3d==0.19.0 plyfile mediapy opencv-python-headless trimesh tqdm scikit-image matplotlib'
# 2DGS source tree (train.py / render.py) unpacked at /workspace/src/2dgs-rocm
```

**Verification** (`surfel_smoke.py`, GPU):

```
simple_knn distCUDA2 on unit grid: median=1.0000 PASS
diff_surfel_rasterization fit: PSNR 20.0 -> 41.2 dB PASS
surfel depth at z=2: expected 2.0000, median 2.0000 PASS
SURFEL_SMOKE: PASS
```

- `train.py` on the 12-view slice (`--resolution 2 --sh_degree 0`) ran 1000 iterations in 45 s at
  ~33 it/s, with the GPU shared, and the loss fell to 0.025.
- `render.py`'s bounded TSDF gave an **empty mesh**. The rendered depths are 7–9 m, where the SfM
  depths are 0.7–1.4 m. The camera-radius heuristic sets `depth_trunc=1.75`, so everything is
  truncated away.
- The rasteriser's depth is correct (the z=2 check above). So this is 1000 iterations on 12
  low-baseline frames with no distortion or normal loss yet (those start at 3k and 7k iterations),
  not the port. A real test is the full kitchen at 15–30k iterations.
- Use `/workspace/envs/2dgs/bin/python` from `/workspace/src/2dgs-rocm`.

For mesh extraction, prefer gsplat's `rasterization_2dgs` (§2): it is Apache-2.0, where 2DGS is
Inria non-commercial.

### 4.3 OpenMVS with GPU

Not possible on AMD. OpenMVS has only a CUDA backend (`OpenMVS_USE_CUDA`), and there is no HIP port
(`r2-oss-industry.md` §2). A HIP port of its PatchMatch would be a project in its own right, and the
COLMAP result in §3 suggests even a straight port would not beat the CPU by much on this GPU.

## 5. Reproduce everything from scratch

```bash
B=/home/sdash/rocmbuild/workspace; R=pipeline/tools/rocm
# 0. hfbox toolchain (5 min, no laptop needed)
scp $R/{fetch-rocm-hfbox.sh,rocm-7.2.1-hfbox.urls} hfbox:/workspace/; scp $R/env.sh hfbox:/workspace/rocm-env.sh
ssh hfbox 'cd /workspace && nice -n 19 bash fetch-rocm-hfbox.sh rocm-7.2.1-hfbox.urls'
# 1. laptop build image (~25 min; 26 GB image)
(cd $R && podman build -t airtools-rocm721 .)
# 2. sources
mkdir -p $B/src $B/tools $B/../wheels && cp $R/*.sh $B/
git clone --recurse-submodules https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201 $B/src/gsplat-rocm && git -C $B/src/gsplat-rocm checkout e2f3319
git clone --depth 1 --branch 4.2.0 https://github.com/colmap/colmap $B/src/colmap
git clone https://github.com/cdcseacave/openMVS $B/src/openmvs && git -C $B/src/openmvs checkout e23d25f
# 2DGS: download the v1.0.0 release bundle, verify SHA256SUMS, untar payload/source/*.tar.gz into $B/src/2dgs-release/src
# torch wheel for the builds: download.pytorch.org/whl/rocm7.2/torch-2.14.0+rocm7.2-cp312-...whl -> $B/../wheels
# 3. builds (run one at a time on a 15 GB machine)
P="podman run --rm --security-opt label=disable -v $B:/build"
$P -v $B/../wheels:/wheels:ro airtools-rocm721 bash /build/build-gsplat.sh
$P -e JOBS=3 -v $B/tools:/workspace/tools airtools-rocm721 bash /build/build-colmap.sh
$P -e JOBS=4 -v $B/tools:/workspace/tools airtools-rocm721 bash /build/build-openmvs.sh
$P -v $B/../wheels:/wheels:ro airtools-rocm721 bash /build/build-2dgs.sh
# 4. ship: wheels in $B/out; trees in $B/tools (tar | zstd, excluding *.a and include/) -> hfbox /workspace/tools
```

Smoke tests on hfbox: `hip_smoke.hip`, `gsplat_smoke.py`, `surfel_smoke.py`, and
`colmap_pms_smoke.sh IMAGES SPARSE OUT [N] [SIZE]` followed by `pms_check.py DENSE TXT_MODEL`.
Also `openmvs_smoke.sh`, which uses the COLMAP slice and `subset_txt.py`. Keep GPU tests short:
another agent's Vulkan job shares the GPU.

## 6. Time log (2026-09-25, EDT)

| Item | Wall | Notes |
|---|---|---|
| Reading, probing hfbox, choosing the approach | 02:56–03:05 | the torch ROCm version pinned to 7.2.1; TheRock has no 7.2 |
| 1. Toolchain on hfbox | 03:05–03:12 (~10 min) | one bad unpack (the Ubuntu hipcc), then libnuma |
| Laptop image, ROCm layers | 02:59–03:14 | 3.7 GB of debs at ~12 MB/s |
| 2. gsplat | 03:14–03:34 (~20 min) | 10 min compile; validation + smoke 2 min |
| 3. COLMAP | 03:35–05:14 (~95 min) | 3 OOM kills; build finally 25 min; smoke 16 min of GPU; correctness check + ONNX 5 min |
| 4a. OpenMVS develop | 03:35–05:07 (~35 min of attention, in parallel) | 5 dependency and compatibility fixes; the build itself 3.5 min |
| 4b. 2DGS extensions | 05:01–05:12 (~15 min) | |
| Notes | 05:12–05:25 | |

Load placed on hfbox: downloads (632 MB of debs, ~650 MB of pip wheels for 2DGS), `dpkg-deb`,
`tar`, and GPU smoke tests. Every CPU step ran under `nice -n 19`, with OpenMVS on 4 threads for
40 s. The one long job was the COLMAP photometric pass, 16 min at 1 CPU thread plus the GPU. It was
not niced, which I will flag to E1 as a possible source of noise between 08:46 and 09:02 box
time (04:46–05:02 EDT).
