# R2: open-source stacks and commercial products for video → textured metric mesh (Sep 2026)

Research notes for the 2026-09-25 bench session. Scope: open-source pipelines and commercial
capture products. For each one: how it works, how fast it is, and what we can reuse. Academic
methods are in `r1-academic.md`, the densify sweep is in `e1-experiments.md`, and the merged view is
`p1-recon-bench.md`. This file does not repeat what `p1-toolchain.md`, `p1-fast-recon.md`,
`p1-colmap-realityscan.md` and `p1-cloud-world-models.md` already established. Those docs cover the
pricing and API survey of the services, the RealityScan CLI, the COLMAP 4.2 HIP changelog and the
AWS/Azure quota. This file covers what is new or goes deeper.

**Evidence labels:**
- **[hfbox]**: run or read live on hfbox this session.
- **[src]**: read from a primary source, such as repo code, release notes, a PR, an official manual
  or an official help page.
- **[press]**: from trade press.
- **UNVERIFIED**: could not be confirmed this session. Such items come from memory or from a single
  weak source.

**Research-budget note.** The WebSearch quota was used up before this pass started, so every
source below was reached by a direct fetch (GitHub API from hfbox, raw repo files, vendor pages, or
Bing RSS from hfbox). Coverage of the closed products is therefore uneven, and the gaps are marked.

---

## 0. TL;DR

1. **Densify spends most of its time on a resolution the output never uses [hfbox].** The kitchen
   `DensifyPointCloud` log breaks down as follows:
   - photometric PatchMatch: 18 m 54 s;
   - two geometric-consistency passes: 3 m 35 s each;
   - fusion: 1 m 06 s.

   It ran on 165 images at 1.98 MP (`--resolution-level 0`) and produced **2.41 M points**. We then
   decimate the mesh to **200 k triangles** (about 100 k vertices), so the depth maps are about
   24× denser than the output needs. The texture detail comes from `TextureMesh`, which bakes from
   the full-resolution photos whatever the densify level.
   - Trying `--resolution-level 1 --geometric-iters 1` at `--fps 1` should cut 27 min to roughly
     3–4 min. This is an estimate from linear pixel scaling; E1 is measuring it.
   - This is the biggest cheap win on the AMD box. §6.1 has the commands.
2. **OpenMVS `develop` is moving fast, and v2.4.0 is not the best OpenMVS [src].** Since the
   v2.4.0 release (2026-01-20), `develop` has added:
   - a CPU **multi-view SGM** densifier (`--fusion-mode -2`), 2.7–3.3× faster than CPU PatchMatch
     on the EPFL scenes at an F-score 0.01–0.03 lower;
   - a graph-cut solver that is 1.8× faster;
   - a fusion threshold of 1.0;
   - `remove-unseen-faces` in `TextureMesh`;
   - a CUDA `RefineMesh`;
   - its own SfM with RoMa v2 matching.

   The same design doc publishes the **CPU vs CUDA PatchMatch** numbers we were missing: 85 s → 6 s
   and 150 s → 9 s (**14–17×**), and Tanks and Temples at 251–410 images in **2–4 min on CUDA**.
3. **An OpenMVS CUDA build on a rented NVIDIA GPU is the cleanest way to get the full-quality mesh
   in minutes.**
   - OpenMVS has no HIP port [src].
   - hfbox has **no ROCm SDK** (`/opt/rocm` is absent; only torch's bundled `libamdhip64` is there).
     So every "HIP source build" plan (COLMAP `HIP_ENABLED`, OpenSplat HIP, AliceVision SYCL via
     AdaptiveCpp) first needs a ROCm dev toolchain installed into `/workspace` [hfbox].
4. **pycolmap 4.2's ALIKED/LightGlue/LoMa enums exist, but the pip wheel has no ONNX Runtime
   [hfbox].**
   - Setting `FeatureExtractorType.ALIKED_N16ROT` **aborts the whole Python process** with
     `ALIKED feature extraction requires ONNX support` (a C++ `std::terminate`, not a catchable
     exception).
   - Learned features for low light therefore mean hloc with ALIKED+LightGlue in the ROCm torch env,
     or a source build of COLMAP with ONNX.
5. **Splat and NeRF stacks do not help the mesh on AMD [src].**
   - Nerfstudio has been stale since v1.1.5 (Nov 2024) and needs CUDA.
   - The good splat-to-mesh methods (2DGS, PGSR, GOF, RaDe-GS, MILo) all ship their own CUDA
     rasterizers.
   - AMD's official `gsplat` port documents MI300X only.
   - Brush (wgpu/Vulkan) is the only trainer that claims AMD support, and it exports splats only.
   - Splats remain a cloud/CUDA quality-tier experiment, not a path on hfbox.
6. **Meta Hyperscape confirms our architecture choice by doing the opposite of it
   [src/press].**
   - Capture: a headset walk (layout pass, detail pass, ceiling) that records images plus headset
     poses.
   - Processing: cloud training "up to 8 hours".
   - Representation: Gaussian splats.
   - Viewing: **cloud-rendered and streamed**, because the Quest cannot render them locally.
   - Sharing was switched off in May 2026, and there is no export.

   A local, offline-viewable Quest asset must be a mesh plus an atlas, which is what we ship.
7. **Quest side: the 200 k-triangle budget is conservative and the texture format is the real
   cost.**
   - Meta's own Quest 3/3S guidance is **1.3–1.8 M triangles per frame** [src].
   - The JPEG atlas in our GLB is decoded to uncompressed RGBA32 at runtime: 64 MiB, or about
     85 MiB with mips.
   - KTX2 (ETC1S or UASTC, transcoded to ASTC or ETC2 on the Quest) cuts that 4–8×.
   - With KTX2 we can afford about 400–500 k triangles and two 4 K atlases (§6.3).

---

## 1. Comparison table

Legend for "hfbox": **Y** = runs today; **B** = needs a source build or a toolchain we lack;
**N** = NVIDIA or Apple only.

| Project / product | Kind | Input | Output | Speed (best evidence) | Metric scale | hfbox | Licence / price | API or CLI | Use for us |
|---|---|---|---|---|---|---|---|---|---|
| **OpenMVS v2.4.0** (ours) | MVS + mesh + texture | posed images | textured mesh, GLB | kitchen densify 27 m on CPU [hfbox]; CUDA PatchMatch 14–17× faster [src] | no (inherits the SfM scale) | Y (CPU zip) | AGPL-3.0 | CLI | **Keep. Tune flags now; CUDA build in the cloud** |
| **OpenMVS `develop`** | same | same | same | SGM CPU 2.7–3.3× faster than CPU PatchMatch [src] | no | B (vcpkg build, CPU only) | AGPL-3.0 | CLI | Trial the SGM mode and the new fusion |
| **COLMAP 4.2 / pycolmap** (ours) | SfM (+ CUDA or HIP MVS) | images | sparse model; mesh via `mesh_texturer` | SfM 113 s on the kitchen [hfbox] | GPS priors | Y (SfM); B (HIP MVS, no ROCm SDK) | BSD-3 | Py + CLI | Keep. ALIKED needs ONNX (not in the wheel) |
| **GLOMAP** | global SfM | — | — | merged into COLMAP 4.0; repo archived 2026-03-09 [src] | — | Y (via pycolmap) | BSD-3 | — | Already used |
| **InstantSfM** (both repos) | GPU global SfM | images | COLMAP text | GPU claims | — | N (**cuDSS**) | research | Py | Skip on AMD |
| **VGGSfM v2** | learned SfM | ≤400 images (1000+ in video mode) | COLMAP bin | tuned for a 32 GB GPU | no | B (pytorch3d, etc.) | custom (NC-ish) | Py | Low priority |
| **hloc** + ALIKED / SuperPoint + LightGlue | learned features → COLMAP | images | COLMAP model | GPU features | no | Y (pure PyTorch, ROCm torch; not run here) | Apache-2.0 (SuperPoint/SuperGlue weights NC) | Py | **Trial for low-light registration** |
| **ODM 3.6.2** | full stack | images **or video** | textured OBJ, **GLB** (`--gltf`), 3D Tiles | CPU; GPU = CUDA popsift only [src] | GPS/GCP | Y (Docker, CPU) | AGPL-3.0 | CLI / NodeODM API | Reference for flags and frame extraction; not faster |
| **Meshroom 2025.1 / AliceVision 3.3.x** | full stack | images / video | textured OBJ | CUDA depth maps; **SYCL backend merged 2026-05-27** [src] | no | B (AdaptiveCpp + ROCm SDK) | MPL-2.0 | CLI / graph | Borrow `KeyframeSelection` |
| **mvs-texturing (texrecon)** | texturing only | mesh + posed images | OBJ + atlas | CPU | — | Y | BSD-3 | CLI | BSD fallback for `TextureMesh` |
| **Nerfstudio 1.1.5** | NeRF/3DGS framework | images / video | splat PLY; mesh via `tsdf` / `poisson` | — | no | N (README requires CUDA) | Apache-2.0 | CLI | Skip |
| **gsplat 1.5.3 / AMD `gsplat-amd`** | 3DGS rasteriser | COLMAP | splats (plus a 2DGS trainer upstream) | MI300X 158–889 s on Mip-NeRF360 [src] | no | B (MI300X-only docs; gfx1201 untested) | Apache-2.0 | Py | Only if we go the splat → mesh route |
| **Brush 0.3.0** | 3DGS trainer (Burn/wgpu) | COLMAP / nerfstudio | splat PLY | "faster than gsplat" (claim) | no | likely Y (Vulkan, RADV ICD present) | Apache-2.0 | CLI + GUI | Optional splat viewer / demo |
| **OpenSplat 1.2.2** (now `WebODM/OpenSplat`) | 3DGS trainer (C++/libtorch) | COLMAP / ODM / nerfstudio | PLY, SPLAT, SPZ | CPU fallback ~100× slower | no | B (HIP build; docs show gfx906) | AGPL-3.0 | CLI | Skip for the mesh |
| **2DGS / PGSR / GOF / RaDe-GS / MILo** | splat → mesh | COLMAP | mesh (TSDF / tetra / in-loop) | tens of minutes on CUDA | no | N (custom CUDA rasterisers) | research | Py | Cloud quality-tier experiment |
| **instant-ngp / SDFStudio / Kaolin / Postshot / LichtFeld** | NeRF / SDF / tools | — | untextured MC mesh / splats | — | — | N | various | — | Skip |
| **RealityScan 2.2** (Epic) | photogrammetry | images, LiDAR | textured mesh, GLB, LODs | fastest desktop (reported); AMD GPU on **Windows** | GCPs | N (Linux AMD "later") | free under $1 M revenue | CLI, REST/gRPC | Cloud Windows GPU quality tier (see `p1-colmap-realityscan.md`) |
| **Agisoft Metashape 2.3** | photogrammetry | images, **video import** | textured / tiled mesh | GPU depth maps; **AMD via OpenCL** [src] | GCP / GPS | likely Y (Linux + OpenCL), paid | $3.5 k Pro | Python API | Strong paid fallback that runs on AMD |
| **Bentley iTwin Capture** | photogrammetry | photos, point clouds, video | 3MX/3SM/OBJ/tiles | cloud or on-prem (RTX 3070+) | GCP | N | enterprise | REST | Skip |
| **Pix4D (matic / catch / cloud)** | photogrammetry | images, AR-tracked video (catch) | mesh / ortho | hours (cloud) | RTK / GCP | N | subscription | cloud API | Skip |
| **DroneDeploy** | cloud photogrammetry | images | OBJ / GLB | minutes to hours | GPS | N | from $349/mo | GraphQL | Skip |
| **Skydio 3D Scan** | autonomous capture | Skydio X10 only | datasets for 3rd-party photogrammetry | — | RTK | N | sales | — | Borrow the capture-planning idea only |
| **Polycam** | app + cloud | photos, video, LiDAR, drone | mesh (GLB/OBJ/…), splats, mesh-from-splat | minutes (cloud) | LiDAR or measure | N | $0–30/mo; Enterprise API | Enterprise API | Online-trial candidate (other agent) |
| **KIRI Engine** | app + cloud | photos, **video ≤3 min, 1080p** | mesh, splats, 3DGS → mesh | UNVERIFIED | measure-and-rescale | N | $6.99/mo; $1/credit API | REST | Online-trial candidate |
| **Scaniverse → Niantic Spatial "Capture"** | on-device app | phone, 360, drone | splats (SPZ) **and meshes, on-device** | seconds–minutes on phone (UNVERIFIED figure) | LiDAR / AR | N | free app; enterprise | no public API | SPZ if we ever ship splats |
| **Luma AI** | (was) NeRF / 3DGS capture | — | — | — | — | — | — | — | **Pivoted to generative video**; capture gone from the site |
| **Apple Object Capture / RoomPlan** | on-device / Mac photogrammetry; parametric room | HEIC + LiDAR depth | USDZ mesh (5 detail levels) / parametric USD | minutes on a Mac | **from LiDAR depth in HEIC** | N (Apple) | free | Swift API | Scale trick; not our platform |
| **Matterport** | 360 / LiDAR / iPhone capture + Cortex AI | camera | tour mesh, OBJ (Matterpak) | "thousands of twins daily" | yes (sensor + AI) | N | subscription | SDK | Skip |
| **Teleport (Varjo)** | cloud 3DGS | iPhone capture (5–10 min), drone in v2 | splats (1–100 M), PLY | cloud GPU; rendered on device | UNVERIFIED | N | $30/mo | capture API | Skip (no mesh) |
| **Gracia** | 4D splat volumetric video | multi-camera rig | 4DGS, "native on Quest 3/3S" | — | — | N | — | — | Proof that splats run on the Quest only with heavy per-frame engineering |
| **Arrival.Space** | web 3D hosting | splats / meshes | WebXR space | — | — | — | — | — | UNVERIFIED beyond the tagline; skip |
| **Meta Hyperscape Capture** | Quest capture + cloud splats | Quest 3/3S cameras + headset poses | splats, **cloud-streamed**, no export | 15–30 min capture; **2–8 h** cloud | yes (headset tracking) | N | free | none | Architecture lesson (§4.13) |

---

## 2. Open-source pipelines

### 2.1 OpenMVS: live flags, phase timings, and what `develop` adds

**Phase breakdown of our kitchen run [hfbox]** (`/workspace/airtools/work/dji0095/mvs/logs/densify.log`):

| Phase | Time | Note |
|---|---|---|
| Load, ROI, neighbour selection | <1 s | 165 images, 326 MP total (1.98 MP each), 11 k sparse points, 14.7 views per point |
| **Photometric PatchMatch** (`--iters 3`, `--sub-resolution-levels 2`) | **18 m 54 s** | 70 % of densify |
| Geometric-consistency pass 1 | 3 m 35 s | `--geometric-iters 2` → two passes |
| Geometric-consistency pass 2 | 3 m 33 s | |
| Dense fusion | 1 m 06 s | 104.8 M depths → 2.45 M points |
| **Total** | **27 m 10 s** | peak RSS only **4.9 GB**, so RAM is not the constraint |

**Live `DensifyPointCloud --help` on hfbox (v2.4.0, build 2026-01-19) [hfbox]**, the knobs that
matter:

| Flag | Default | Effect for us |
|---|---|---|
| `--resolution-level` | 1 (pipeline passes **0**) | Halves each side per level. Level 1 = 4× fewer pixels. The main lever |
| `--max-resolution` / `--min-resolution` | 2560 / 640 | 1920 px frames pass through unchanged |
| `--sub-resolution-levels` | 2 | Coarse-to-fine PatchMatch init (the depth is estimated at ½ and ¼ scale and upsampled as the seed). Helps low-texture areas. Dropping it to 1 saves the coarse passes but hurts textureless walls |
| `--number-views` | 5 (CPU build; the CUDA build defaults to 8) | Neighbour views per depth map. 3–4 is faster and noisier |
| `--number-views-fuse` | 2 | Minimum agreeing views to keep a point |
| `--iters` | 3 (CPU; 4 on CUDA) | PatchMatch iterations |
| `--geometric-iters` | 2 | **Each pass costs 3.5 min at level 0.** 1 halves that; 0 skips it, at a precision cost |
| `--fusion-mode` | 0 | 0 = depth maps + fusion; 1 = export depth maps only; −1 = export disparity; −2 = SGM (reworked on `develop`) |
| `--fusion-filter` | 2 | 0 merge, 1 fuse, 2 dense-fuse |
| `--postprocess-dmaps` | 0 | Bit flags: 1 remove speckles, 2 fill gaps, 4 adjust confidence (8 = recalibrated confidence on `develop`) |
| `-m/--mask-path`, `--ignore-mask-label` | — | Per-image `*.mask.png`. The hook for sky or background masks on aerial clips |
| `--estimate-roi` / `--crop-to-roi` / `--roi-border` | 1.1 / 1 / 0 | ROI from the sparse cloud. Our kitchen ROI kept 96 % of the points |
| `--tower-mode` | 4 | Adds a cylinder of points at the ROI centre when the up axis is known (`--up-axis`). Relevant to building orbits, where facade tops go missing |
| `--sub-scene-area` | 0 | Splits big scenes. Relevant for large aerial jobs |
| `--remove-dmaps` | 0 | Delete the `.dmap` files after fusion (they are several GB) |

**Config-file-only defaults (`OPTDENSE` in `libs/MVS/DepthMap.cpp`) [src]:**
- `nMaxViews=12` is the per-depth-map neighbour cap, and `nMaxViewsFuse=32` is the fusion neighbour
  cap.
- `fOptimAngle=12°`, `fMinAngle=3°` and `fMaxAngle=65°` set the view-selection geometry.
- `fNCCThresholdKeep=0.9`.
- These are reachable through `-c DensifyPointCloud.cfg`.

**ReconstructMesh (v2.4.0 live help) [hfbox]:**
- **`--free-space-support 1`** is off by default. It "exploits the free-space support in order to
  reconstruct weakly-represented surfaces", which is exactly the textureless-cabinet and wall case
  that limits our 70 % coverage.
- `--target-face-num N` gives an exact face count, a cleaner option than the fractional
  `--decimate` that `mvs.py::_auto_decimate_ratio` guesses today.
- `--close-holes 30`, `--smooth 2`, `--remove-spurious 20` and `--min-point-distance 1.5` (a lower
  value keeps more points).

**TextureMesh (v2.4.0 live help) [hfbox]**:
- **`--virtual-face-images 3`** is off by default. It groups coplanar triangles into virtual faces
  for view selection: fewer seams and larger patches on flat counters and cabinets.
- `--patch-packing-heuristic` (0 = best fit … 100 = fastest; default 3).
- `--sharpness-weight 0.5`, `--outlier-threshold 0.06`, and `--empty-color` (the colour of unseen
  faces).
- `--ignore-mask-label -1` auto-masks the lens-distortion border.
- `--max-texture-size` splits the atlas into several pages when needed.
- OpenMVS issues #534 and #1094: a non-zero `--resolution-level` can make `TextureMesh` *slower*
  (https://github.com/cdcseacave/openMVS/issues/1094).

**What `develop` has over v2.4.0 [src, GitHub API from hfbox; commit list dated 2026-07-16 to
2026-09-16]:**
- **#1306, multi-view SGM (`--fusion-mode -2`)**, merged 2026-09-16. It is CPU-only for now (a
  CUDA port is "next steps"). Numbers from the design doc
  (https://github.com/cdcseacave/openMVS/blob/develop/docs/design/SemiGlobalMatching.md), all at
  `--resolution-level 1` on a 24-thread workstation:

  | Scene | Multi-view SGM (CPU) | PatchMatch CPU | PatchMatch CUDA |
  |---|---|---|---|
  | Herz-Jesu-P8 | F 0.373, **31 s** | F 0.402, 85 s | F 0.403, **6 s** |
  | fountain-P11 | F 0.260, **46 s** | F 0.268, 150 s | F 0.252, **9 s** |
  | T&T Truck, 251 images | F 0.652, 11 min | — | F 0.722, **2–4 min** |
  | T&T Barn, 410 images | F 0.517, 31 min | — | F 0.648, **2–4 min** |

  - **These are the first published CPU vs CUDA figures for OpenMVS PatchMatch: 14–17× faster on
    CUDA.**
  - SGM loses recall on bigger scenes because it has no geometric-consistency filter.
  - It is worth a trial on hfbox only if the tuned PatchMatch (§6.1) is still too slow.
- **#1298**: the fusion reprojection threshold moves from 1.2 to 1.0, a free +0.004–0.007 F1 on
  T&T. `--fusion-recycle-dropped` (opt-in) raises recall but is not recommended when a mesh
  follows.
- **#1292**: depth-map confidence recalibration (ROC-AUC 0.84 → 0.93). It is on by default on the
  GPU and costs a pass on the CPU (`--postprocess-dmaps 8`).
- **#1296**: the TetraFlow graph cut in `ReconstructMesh`, 1.8× faster with 2.4× less memory.
- Also:
  - `TextureMesh` remove-unseen-faces;
  - a `RefineMesh` CUDA rewrite (#1299/#1300);
  - `sfm`: finetune from known poses, plus a RoMa v2 (DINOv3 + ONNX) `CreateStructure --roma2`
    (release `roma2-model`, 2026-09-09) — OpenMVS is growing its own SfM (UNVERIFIED quality);
  - `InterfacePolycam`, already present in v2.4.0 [hfbox], which imports Polycam raw captures
    (ARKit poses plus LiDAR depth). This is a handheld-capture path that skips SfM.
- **No HIP/ROCm port** exists (CMake has only `OpenMVS_USE_CUDA`, and there are no HIP issues or
  PRs) [src]. Building `develop` CPU-only needs vcpkg, CMake ≥ 3.21 (hfbox has 3.28), and about
  20–40 min of compile (UNVERIFIED estimate).

### 2.2 COLMAP 4.2 / pycolmap (what is new beyond `p1-colmap-realityscan.md`)

- **The ALIKED/LightGlue/LoMa enums in the pip wheel are a trap [hfbox].**
  - `FeatureExtractorType` = {SIFT, ALIKED_N16ROT, ALIKED_N32, LOMA_B, LOMA_B128}.
  - `FeatureMatcherType` adds SIFT_LIGHTGLUE, ALIKED_LIGHTGLUE and the LOMA_* variants.
  - Running ALIKED aborts the interpreter with `ALIKED feature extraction requires ONNX support`.
    **Never set these in `pipeline/sfm.py` without a subprocess guard.**
  - A source build with ONNX Runtime would fix it. On ROCm, ONNX Runtime's ROCm EP is removed as of
    ORT 1.23; the replacement is the **MIGraphX EP** (ORT 1.23.2 supports ROCm 7.2) [src:
    https://onnxruntime.ai/docs/execution-providers/MIGraphX-ExecutionProvider.html].
- `colmap mesh_texturer` and `colmap mesh_simplifier` (QEM) exist since 4.0 [src: `src/colmap/exe/mvs.cc`].
  - They write `mesh.ply` plus `texture.png` and have no GLB export.
  - They are only interesting if we move to COLMAP's own CUDA or HIP MVS end to end.
- HIP `patch_match_stereo` needs `hipcc`, `hiprand` and `rocrand`, **none of which are on hfbox**
  (`/opt/rocm` is missing) [hfbox]. Options:
  - install the ROCm 7.2 dev packages into the container (the apt root is not persistent);
  - use a `rocm/dev-ubuntu-24.04` base image;
  - use AMD's "TheRock" pip SDK wheels (UNVERIFIED that they include hipcc usable for CMake).

### 2.3 Fast SfM alternatives

- **GLOMAP** was archived on 2026-03-09 and lives in COLMAP as the global mapper [src:
  https://github.com/colmap/glomap]. Nothing new.
- **InstantSfM** has two repos, `cre185/InstantSfM` and `icratmp483/InstantSfM`. Both need NVIDIA
  **cuDSS** (NVIDIA's sparse direct solver), which has no HIP equivalent. They are not pure PyTorch
  and not usable on AMD [src].
- **VGGSfM v2** (facebookresearch/vggsfm) handles 400 frames, or 1000+ with a sliding window in
  `video_demo.py`. Its hyper-parameters are tuned for 32 GB. It depends on pytorch3d, and ROCm
  builds of pytorch3d are painful (UNVERIFIED on gfx1201). It exports COLMAP. Low priority.
- **VGGT-X** (`Linketic/VGGT-X`) is pure PyTorch, handles hundreds of images via `chunk_size`, and
  exports COLMAP binaries. It is the most AMD-plausible learned SfM besides VGGT itself (not run
  here).
- **VGGT-Long** is chunked, with loop closure, for kilometre-scale sequences. It is pure Python and
  has no COLMAP export.
- **SLAM-as-SfM**:
  - DROID-SLAM, DPVO and MASt3R-SLAM all compile CUDA extensions.
  - PyTorch's `cpp_extension` auto-hipifies `CUDAExtension` builds under a ROCm torch
    (`IS_HIP_EXTENSION`) [src: `torch/utils/cpp_extension.py`], so they are *mechanically*
    buildable. No one documents gfx1201.
  - DPVO has `--save_colmap`.
- **Light3R-SfM** has no public code as of today [src].
- **hloc** (cvg/Hierarchical-Localization) is Apache-2.0 and pure PyTorch, with confs for
  `aliked-n16`, `superpoint_*` and `disk` plus `superpoint+lightglue` / `aliked+lightglue`
  matching.
  - It writes a COLMAP database and reconstructs with pycolmap, so it drops into our `sfm.py`
    output contract.
  - SuperPoint/SuperGlue weights are Magic Leap non-commercial; **ALIKED+LightGlue has no such
    restriction** (UNVERIFIED wording).
  - This is the realistic route to learned features on hfbox (§6.1, item 6).

### 2.4 OpenDroneMap 3.6.2 (2026-08-12)

It is a complete reference implementation of "drone video to textured GLB" and useful for its
defaults [src: https://docs.opendronemap.org/arguments/]:
- Video input is native: `--video-limit 500` (maximum frames) and `--video-resolution 4000`.
- `--pc-quality {ultra..lowest}` (default medium, about 4× time per step), `--feature-quality`
  (default high) and `--min-num-features 10000`.
- Mesh: `--mesh-size 200000` (the same number as our budget) and `--mesh-octree-depth 11` (screened
  Poisson).
- Texturing uses mvs-texturing. `--texturing-single-material` gives one atlas;
  `--texturing-keep-unseen-faces` also exists.
- `--gltf` writes a single binary GLB, and `--3d-tiles` writes tiles.
- GPU = CUDA popsift SIFT only (`opendm/gpu.py` calls `libcuda` directly). On AMD it silently falls
  back to CPU [src: https://raw.githubusercontent.com/OpenDroneMap/ODM/master/opendm/gpu.py].
- ODM runs OpenMVS `DensifyPointCloud` internally with `--filter-point-cloud -20`.

**Verdict:** it is not faster than our chain (same OpenMVS core plus Poisson). Run it once in
Docker as a quality baseline, because its Poisson mesh (`--mesh-octree-depth 11`) is more
*complete* (watertight) than a Delaunay graph cut. That is a coverage datapoint.

### 2.5 Meshroom / AliceVision

- The latest packaged releases are Meshroom 2025.1.0 / AliceVision 3.3.0 (2025-08-19). Tags run to
  v3.3.7 (2026-08-21) without packaged releases [src].
- **Depth maps now have a SYCL backend** (PR #2077, merged 2026-05-27, milestone 3.4.0). It uses
  AdaptiveCpp, which can target ROCm. AMD hardware testing is not documented. It needs the ROCm SDK
  that hfbox lacks. Verdict: B, low priority.
- **The `KeyframeSelection` algorithm is worth copying**
  (https://github.com/alicevision/AliceVision/blob/develop/src/aliceVision/keyframe/KeyframeSelector.hpp):
  - Sharpness = the standard deviation of the local mean Laplacian in a sliding window
    (`sharpnessWindowSize`), optionally computed on a frame rescaled to `rescaledWidthSharpness`.
  - Motion = the median per-cell optical flow (`flowCellSize`).
  - Frame step: `minFrameStep` 12 to `maxFrameStep` 36.
  - The "smart" mode picks the sharpest frame nearest the middle of each motion-defined
    sub-sequence.

### 2.6 mvs-texturing (texrecon)

BSD-3 [src: https://raw.githubusercontent.com/nmoehrle/mvs-texturing/master/apps/texrecon/arguments.cpp].
- Flags: `--data_term {gmi,area}` (default GMI), `--smoothness_term`,
  `--outlier_removal {none,gauss_clamping,gauss_damping}` (removes moving objects, useful for people
  in frame), `--skip_global_seam_leveling`, `--keep_unseen_faces` and `--write_timings`.
- There is no published speed comparison with `TextureMesh`. We keep `TextureMesh` (GLB export,
  `--decimate`, virtual faces), and texrecon remains the BSD fallback.

### 2.7 Splat and NeRF stacks: AMD status

- **Nerfstudio**:
  - The last release is v1.1.5 (2024-11-11), and the README requires NVIDIA CUDA.
  - `ns-export marching-cubes` asserts an SDF field, so it cannot mesh splatfacto.
  - `ns-export tsdf|poisson` produce meshes.
  - Verdict: skip.
- **gsplat**:
  - Upstream is at 1.5.3 (2025-07-04), and AMD requests (#771, #434) are open.
  - AMD's port is `ROCm/gsplat`, now `AMD-Ecosystem/gsplat-amd`, installed with
    `pip install amd_gsplat --extra-index-url=https://pypi.amd.com/rocm-7.0.0/simple/`. Its
    documentation covers the **MI300X** only, with ROCm 6.4.3/7.0 and torch 2.6/2.8. The last
    commit was 2026-03-18.
  - gfx1201 wheels are UNVERIFIED (the AMD index may ship gfx942-only kernels).
  - Upstream gsplat has its own 2DGS trainer (`examples/simple_trainer_2dgs.py`); whether the AMD
    port includes the 2DGS kernels is UNVERIFIED.
  - Sources: https://github.com/AMD-Ecosystem/gsplat-amd,
    https://rocm.blogs.amd.com/software-tools-optimization/gsplat/README.html
- **Brush**:
  - v0.3.0 (2025-09-14) uses Burn with wgpu, so it runs on Vulkan, Metal or WebGPU.
  - The README claims "AMD/Nvidia/Intel". It takes COLMAP or nerfstudio input and exports PLY (also
    compressed) with **no mesh**.
  - hfbox has the Mesa `radeon_icd.json` Vulkan ICD [hfbox], so it probably runs headless.
  - Useful only for a splat preview.
- **OpenSplat**:
  - Now `WebODM/OpenSplat`, v1.2.2 (2026-09-16). It has a real HIP build path
    (`-DGPU_RUNTIME=HIP -DHIP_ROOT_DIR=/opt/rocm`, `PYTORCH_ROCM_ARCH=gfx906` in the docs) and
    exports PLY, SPLAT and SPZ.
  - It needs the ROCm SDK plus libtorch-ROCm, and has no mesh export.
- **Mesh from splats**:
  - 2DGS (`diff-surfel-rasterization`), PGSR (`diff-plane-rasterization`), GOF and RaDe-GS
    (`diff-gaussian-rasterization` variants) and MILo (built on RaDe-GS/GOF) **all use custom CUDA
    rasterisers, not gsplat**, so none run on AMD without a port.
  - Gaussian surfels uses Poisson at depth 10. PGSR reports about 0.5 h per DTU scan.
- **Taichi 3DGS** is CUDA-only in practice despite Taichi's Vulkan backend. **LichtFeld Studio**
  explicitly excludes AMD. **Postshot** needs Windows and NVIDIA (CC ≥ 7.5).
- **instant-ngp** is CUDA-only, and its marching-cubes mesh has vertex colours rather than a UV
  atlas (the latter UNVERIFIED). SDFStudio is frozen and on CUDA 11.3. **Kaolin** is CUDA-only.

**Net:** there is no AMD-native path from a splat to a textured mesh that beats OpenMVS. Splats stay
a cloud experiment (PGSR, MILo or 2DGS on an NVIDIA box, then `TextureMesh` on the extracted mesh).

---

## 3. Tricks the fast and complete products use (and what maps to us)

1. **Motion-based frame selection, not fixed fps.**
   - Metashape's *Import Video* frame step is a **% image-width shift**: Small ≈ 3 %, Medium ≈ 7 %,
     Large ≈ 14 %. It also reads DJI SRT, MISB KLV and GoPro GPMF telemetry directly [src:
     https://www.agisoft.com/pdf/metashape-pro_2_3_en.pdf].
   - AliceVision picks the sharpest frame per motion window (§2.5).
   - Pix4Dcatch records frames tagged with AR poses during a live walk
     [src: https://www.pix4d.com/product/pix4dcatch].
   - For our 1/30 s low-light footage, the sharpest frame per 0.5–1 s window matters more than the
     fps. Blur is a first-order cause of our 165/175 registration and the 2-component split.
2. **Poses come from the device and SfM only refines them.**
   - Hyperscape records headset poses with the images [src: Meta help].
   - Pix4Dcatch records AR poses, and Apple Object Capture uses the gravity and scale in LiDAR HEIC
     depth [src: WWDC21 10076].
   - Polycam raw data carries ARKit poses plus depth, and OpenMVS ships `InterfacePolycam`.
   - Our DJI clip gives only 1 Hz H / H.S. The equivalent for us is to use video order (sequential
     matching, which we already do) and treat the caption altitude as a scale prior (already done).
3. **GPU MVS everywhere.**
   - RealityScan uses CUDA, and AMD on Windows since 2.2.
   - Metashape uses CUDA **or OpenCL (AMD Radeon R9+/Pro WX)**, for depth maps, meshing and Vulkan
     texture blending on Linux [src: Metashape 2.2/2.3 manual].
   - Bentley needs an RTX 3070 or better.
   - Nobody ships CPU PatchMatch as the fast path. The Metashape OpenCL path is the only
     commercial MVS proven on AMD under Linux.
4. **Depth-map-direct meshing and tiling for big scenes.**
   - Metashape can build a mesh or a *tiled model* straight from depth maps ("recommended for large
     projects").
   - Bentley and Cesium output LOD tiles (3MX/3SM/3D Tiles).
   - Our kitchen does not need it; aerial building orbits may (§6.3).
5. **Texture blending that keeps detail.**
   - Metashape's *Mosaic* mode blends the low frequencies across views (so no seams) but takes the
     high frequencies from the single best, most frontal, sharpest view.
   - OpenMVS's global and local seam leveling plus `--sharpness-weight` is the same idea.
   - `--virtual-face-images 3` adds larger patches on planar surfaces.
6. **Decimate before texturing, or bake the detail into normals.**
   - OpenMVS `TextureMesh --decimate` (what we do) and RealityScan `simplify` then `reproject
     texture` give an atlas baked for the low-poly mesh.
   - Apple Object Capture *Full* bakes normal, AO and displacement maps onto the reduced mesh
     [src: WWDC21 10076].
   - For the Quest, a normal map costs a second texture. Skip it until geometry detail, not texture
     detail, is the complaint.
7. **Masking and background.**
   - RealityScan 2.0 has AI background masking [src: realityscan.com news].
   - OpenMVS takes `--mask-path` in both Densify and Texture.
   - For aerial orbits, a sky mask (any segmentation net → `*.mask.png`) stops sky "blobs" and
     wasted depth maps. It is not needed indoors.
8. **Cloud rendering when the asset is too heavy.** Hyperscape streams splats from the cloud
   because the Quest cannot render them locally. We avoid this by shipping a mesh.
9. **Compact transport formats.**
   - Niantic's SPZ is about 10× smaller than PLY for splats [src: https://github.com/nianticlabs/spz].
   - The mesh-side equivalent is meshopt plus KTX2 (§5).

---

## 4. Companies and products

Services already surveyed for price and API in `p1-cloud-world-models.md` §1 are only extended here
with how they work.

### 4.1 Polycam
- **Input and output:** photos (20–200), video (MP4), LiDAR "Space mode", and drone imagery, with a
  "Drone / Aerial" product line on the homepage. Exports mesh (GLB/OBJ/FBX/USDZ/…) and Gaussian
  splats, and offers "**downloadable mesh for any Gaussian splat**" [src:
  https://poly.cam/tools/gaussian-splatting, https://poly.cam/].
- **Processing:** cloud; LiDAR mode is on-device (ARKit scene reconstruction).
- **Internals:** UNVERIFIED. Whether photo mode licenses RealityCapture or Object Capture, or is
  in-house, could not be confirmed.
- **Scale:** metric via LiDAR on Pro iPhones.
- **API:** Enterprise only.
- **Relevance:** its raw-data export is importable by OpenMVS `InterfacePolycam` [hfbox, binary
  present]. That is a no-SfM handheld path if we ever capture handheld with an iPhone Pro.

### 4.2 Luma AI
- The current site lists only generative products (Ray 3.2 video, Uni-1, Luma Skills), and
  `/interactive-scenes` resolves to the generic app [src: https://lumalabs.ai/].
- History (from memory, UNVERIFIED): NeRF capture in 2022–23, then 3DGS "Interactive Scenes" in
  2023, with mesh export through a separate extraction step.
- **Relevance:** none now. The capture product is gone.

### 4.3 KIRI Engine
- **Featureless Object Scan** is neural surface reconstruction on video, for reflective or
  textureless objects [src: https://www.kiriengine.app/blog/explained/featureless-object-scan].
- The 3DGS pipeline runs from video to a dense point cloud to 3DGS, with an optional mesh via
  "Include Mesh" [src: https://www.kiriengine.app/blog/explained/threed-gaussian-splatting].
- It open-sourced a 3DGS Blender add-on
  (https://github.com/Kiri-Innovation/3dgs-render-blender-addon).
- The API video endpoint is ≤1080p and ≤3 min (see `p1-cloud-world-models.md` §2.1).
- **Relevance:** the online-trial agent's candidate. The internal method (SfM → MVS, or
  NSR/2DGS-style) is UNVERIFIED beyond the blog posts.

### 4.4 Scaniverse / Niantic Spatial "Capture"
- scaniverse.com redirects to `nianticspatial.com/products/capture`. Niantic sold its games to
  Scopely in March 2025 and spun off Niantic Spatial [press:
  https://www.roadtovr.com/pokemon-go-maker-niantic-sells-off-gaming-division/].
- The product page says: phone, 360-camera and drone input; "Generate Gaussian Splats **and
  meshes** processed efficiently **on-device**"; SPZ export; and "Bring Your Own Data" [src:
  https://www.nianticspatial.com/products/capture].
- SPZ (https://github.com/nianticlabs/spz) is about 10× smaller than PLY, with quantised spherical
  harmonics and C++ and TS/WASM libraries.
- On-device training time (about a minute on a recent iPhone) is from memory and UNVERIFIED.
- **Relevance:** if we ever ship splats, SPZ is the transport format. Their on-device splat and
  mesh pair shows that a mesh is still produced alongside the splat for tooling.

### 4.5 RealityScan (Epic)
- Already deep in `p1-colmap-realityscan.md`. New:
  - capturingreality.com now redirects to realityscan.com.
  - 2.0 added AI masking, better low-feature alignment, aerial LiDAR fusion, and **quality
    heat-maps that flag under-covered areas before meshing** [src: realityscan.com 2.0 news].
- The out-of-core design and a meshing path that skips full per-view depth maps are known from
  Falkingham's 2019 test and community lore. Official internals are UNVERIFIED.
- **Relevance:** the coverage heat-map idea maps to our `qa.py` view-coverage metric. We could
  flag gaps to the pilot before leaving the site.

### 4.6 Apple Object Capture, RoomPlan and ARKit
Sources: WWDC21 10076, WWDC22 10127, WWDC20 10611.
- **Object Capture detail levels:** Preview → Reduced → Medium → Full → Raw. Full bakes diffuse,
  normal, AO, roughness and displacement. Raw is unbaked at maximum poly count.
- **Scale and gravity come from LiDAR depth embedded in the HEIC files**, not from SfM.
- The iOS 18 "area mode" is UNVERIFIED (the docs are a JavaScript single-page app).
- **RoomPlan** is **parametric, not a mesh**: walls, doors and windows as dimensioned surfaces, and
  furniture as category cuboids, exported to USD/USDZ.
- ARKit scene reconstruction fuses 60 Hz LiDAR depth into a mesh with semantic classes.
- **Relevance:**
  - The scale lesson: a metric depth sensor beats every visual trick. Our caption altitude is the
    poor man's version.
  - RoomPlan's parametric walls are a good idea for a *collision* proxy in the kitchen
    (`collision.glb`).

### 4.7 Matterport
- Cortex AI combines computer vision, deep nets and "real-time spatial alignment" in the capture
  app for dimensionally accurate twins, "thousands daily without human intervention" [src:
  https://matterport.com/cortex-ai].
- Matterport is now a CoStar subsidiary [src: Wikipedia listing].
- Mesh / Matterpak OBJ details and any splat work are UNVERIFIED.
- **Relevance:** low. It is a capture-hardware ecosystem.

### 4.8 Pix4D and DroneDeploy
- **Pix4Dcatch:**
  - The phone records video frames plus pose and orientation during a walk.
  - It shows a live AR mesh and point-cloud overlay while capturing (a coverage-feedback trick).
  - It exports to PIX4Dcloud, mapper or matic [src: https://www.pix4d.com/product/pix4dcatch].
- **DroneDeploy** is an enterprise site-capture platform (Aerial, Ground 360, Progress AI), starting
  at $349/mo (third-party). Its splat work is UNVERIFIED.
- **Relevance:** the live coverage overlay again. Neither is a video-to-GLB-in-minutes product.

### 4.9 Skydio 3D Scan
- "Autonomous, asset-aware flight" holds proximity, coverage and overlap around structures, even
  without GNSS. Its datasets export to DroneDeploy, Pix4D, Esri, Bentley and others; it does not
  reconstruct itself [src: https://www.skydio.com/3d-scan].
- **Relevance:** the lesson for building orbits is the *capture plan*. A consistent standoff and
  overlap (about 70–80 %) is what makes everything downstream fast and complete. Write this into
  the pilot brief for the aerial target.

### 4.10 Bentley iTwin Capture Modeler
- Photos, LiDAR and video. Cloud or on-prem engines; minimum RTX 3070 and 16 GB, recommended
  RTX 4080 and 64 GB [src: https://www.bentley.com/software/itwin-capture-modeler/].
- Splat and tile-format specifics are UNVERIFIED this pass (3MX/3SM per the earlier doc).
- **Relevance:** none for us.

### 4.11 Agisoft Metashape 2.3 (Feb 2026)
Highest-confidence section; verified against the official 2.2 and 2.3 manuals.
- **Import Video** has % shift frame steps and reads DJI SRT.
- Depth maps are pairwise and then merged. Build Model or Tiled Model works from depth maps, tie
  points or the cloud.
- Texture *Mosaic* blending (§3.5).
- **GPU: NVIDIA via CUDA, AMD via OpenCL 1.2 (Radeon R9 and later), Vulkan texture blending on
  Linux.**
- **No Gaussian-splat feature** in the 2.3 manual (the only "Gaussian" is a blur filter).
- **Relevance:**
  - The only commercial photogrammetry that plausibly runs **GPU-accelerated on hfbox (Linux +
    AMD)**. It would need OpenCL on the container (the ROCm OpenCL runtime is not installed;
    UNVERIFIED).
  - $3.5 k Pro, with a 30-day trial.
  - As a speed and quality reference only.

### 4.12 Teleport by Varjo
- iPhone capture of 5–10 min. Cloud 3DGS with "NVIDIA GPU-trained generative models", then
  **rendered on device**. $30/mo [press:
  https://www.roadtovr.com/varjo-teleport-3d-model-scanning-app-release/].
- Reconstructions run from 1 M to 100 M splats on elastic GPU clusters [src:
  https://get.teleport.varjo.com/].
- Drone input in v2 per the earlier doc. PLY export only.
- Quest viewing is UNVERIFIED.

### 4.13 Meta Horizon Hyperscape Capture
The most relevant product. Sources:
- Meta help, https://www.meta.com/help/quest/1088536553019177/ [src]
- TechCrunch, 2025-09-17 [press]
- RoadToVR, 2024-09-26 and 2026-03-18 [press]
- MLQ summary [press]

**Capture on a Quest 3/3S, in one session:**
1. *Space layout*: walk the room edge corner to corner, stopping and looking toward the centre.
   Press coverage says this builds a scene mesh in 10–30 s.
2. *Detail capture*: get close and circle objects. About 5 min.
3. *Final touches*: look at the ceiling and add more passes.
- The room must be at least about 3 × 3 m, well lit, with nothing moving (TVs and fans off).
- The app records sequential images plus the headset's relative position and orientation. Users
  report about 15–25 min of capture for a room.

**Upload and processing:**
- Uploads to Meta's servers (the headset stays on; capture can pause and resume).
- Processing is "up to 8 hours depending on the size", and 2–4 h is typical per press and users.
- A notification arrives on the headset and in the phone app.

**Representation and rendering:**
- Gaussian splats. Meta's own 2024 wording: "By utilizing Gaussian Splatting … we can take
  advantage of **cloud rendering and streaming** to make these spaces viewable on a standalone
  Quest 3." TechCrunch (2025): "Gaussian splatting, cloud rendering, and streaming".
- The name of the streaming stack (for example "Avalanche") is UNVERIFIED.

**Output and status:**
- View-only in the Hyperscape Capture app. **No export** of the raw capture or the trained PLY
  (per the earlier doc, citing Meta help).
- **Sharing and co-presence ended on 2026-05-12.** Horizon Worlds VR is being wound down
  (RoadToVR, 2026-03-18).
- Quest 3/3S only; 18+.

**What it teaches us:**
- **Pose from the device and a guided capture are how they get completeness.** The layout pass
  gives global structure; the detail pass gives texture. For our drone this means asking the pilot
  for an outer loop at standoff plus inner detail passes, and keeping sequential matching with loop
  closure.
- **Hours of cloud training plus streaming is the price of splat realism on the Quest.** Our
  mesh-plus-atlas design is the right call for an offline, local, fast (minutes) Quest asset.
  Nothing in Hyperscape is reusable as code.
- **Quality ceiling reference:** Hyperscape is what a "wow" indoor room looks like. Our PSNR 20.8
  mesh will look flatter. Texture and lighting fidelity (seam leveling, exposure) is where we can
  close part of the gap, not the triangle count.

### 4.14 Gracia and Arrival.Space
- **Gracia:** "Volumetric video production powered by 4D Gaussian Splatting (4DGS). From
  multi-camera capture to delivery in any engine, browser, or headset", "delivered natively on
  Meta Quest 3/3S, Apple Vision…" [src: gracia.ai meta description and a Bing snippet].
  - It targets studio multi-camera rigs, not scene reconstruction.
  - Its Quest renderer internals (splat count, streaming) are UNVERIFIED.
  - The takeaway: on-device splats on the Quest are possible for small, bounded content with
    heavy custom engineering, which is not our scope.
- **Arrival.Space:** "Your 3D space on the web" / "claim. edit. share." It is a WebXR hosting
  platform. Its formats and limits are UNVERIFIED (the site is JavaScript-only). Skip.

---

## 5. Quest-side rendering of big textured meshes

- **Budget [src: https://developers.meta.com/horizon/documentation/unity/unity-perf/]:**
  - Quest 3/3S: **1.3–1.8 M triangles per frame**.
  - Draw calls: 200–300 for a heavy simulation, 400–600 medium, 700–1000 light.
  - Our 200 k triangles in one material is about 11–15 % of the triangle budget. The headroom is
    real.
- **Texture memory is the actual risk (arithmetic, not sourced):**

  | Format | Size of one 4096² atlas (with mips) |
  |---|---|
  | RGBA8 (what a JPEG becomes after decode) | 64 MiB (≈ 85 MiB) |
  | ASTC 4×4 | 16 MiB (≈ 21 MiB) |
  | ASTC 6×6 | 7.1 MiB (≈ 9.5 MiB) |
  | ETC2 RGB | 8 MiB (≈ 10.7 MiB) |

  - glTFast loads a JPEG or PNG into an uncompressed `Texture2D`, which also costs CPU decode time
    at load (UNVERIFIED as to exact glTFast behaviour, but standard for runtime-loaded JPEGs).
  - Adreno 740 supports ASTC LDR (standard for this GPU class; not doc-cited here).
- **KTX2 in glTF (`KHR_texture_basisu`)** [src:
  https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_texture_basisu/README.md]:
  - Use ETC1S for colour (smallest) or UASTC for quality. The spec says to transcode to ASTC or BC7
    when available.
  - glTFast needs the **KTX for Unity** package (`com.unity.cloud.ktx`) to decode it, which is
    already noted in `p1-toolchain.md` §4.
  - For our photo atlas, **UASTC + RDO** keeps the detail and **ETC1S** is about 2× smaller but
    visibly blockier on photos.
- **Tools** [src: https://github.com/zeux/meshoptimizer/blob/master/gltf/README.md,
  https://gltf-transform.dev/cli.html]:
  ```sh
  # meshopt geometry + KTX2 textures in one pass (gltfpack build must include BasisU for -tc)
  gltfpack -i mesh.glb -o mesh_q.glb -cc -tc -tq 8     # -tq = texture quality 1..10
  # or gltf-transform (needs KTX-Software `toktx` on PATH for uastc/etc1s)
  npx @gltf-transform/cli uastc mesh.glb mesh_uastc.glb --level 2 --rdo --zstd 18
  npx @gltf-transform/cli etc1s mesh.glb mesh_etc1s.glb --quality 255
  npx @gltf-transform/cli meshopt mesh_uastc.glb mesh_final.glb
  ```
  - The exact flag spellings (`--rdo`, `--zstd`, `--quality`) are UNVERIFIED this session; check
    them with `gltf-transform help uastc`.
  - `EXT_meshopt_compression` needs Unity's meshopt decompress package.
- **Index format:** 200 k triangles are about 100 k vertices, which is more than 65,535, so the mesh
  needs 32-bit indices (`IndexFormat.UInt32`, supported on Adreno) or splitting into sub-meshes
  [src: https://docs.unity3d.com/ScriptReference/Rendering.IndexFormat.html]. glTFast chooses this
  from the accessor type (UNVERIFIED).
- **LOD and tiling:**
  - `MSFT_lod` exists, but **glTFast is not a listed implementer** [src:
    https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Vendor/MSFT_lod].
  - For LODs, ship 2–3 GLBs (for example 500 k / 150 k / 40 k via `gltfpack -si`) and switch them
    with a Unity `LODGroup` in the app.
  - 3D Tiles (Cesium for Unity) only matters for city-scale aerial scenes.
- **What others ship to the Quest:**
  - Hyperscape: a cloud-streamed splat.
  - Teleport: cloud-trained splats rendered on device (Quest support UNVERIFIED).
  - Gracia: 4DGS natively on the Quest.
  - Into the Scaniverse: on-device splats (UNVERIFIED).
  - Nobody ships a big photogrammetry mesh to the Quest as a product. Our mesh plus KTX2 approach
    is the standard game-engine path and needs no novel runtime.

---

## 6. Recommendations for our pipeline

Ranked by expected gain per hour of work.

### 6.1 Try now on AMD (hfbox)

1. **Densify at the resolution the output can use.** Keep `TextureMesh --resolution-level 0` so
   the texture stays full-res.
   ```sh
   # A: level 1, one geometric pass (expect ~6-8 min at 165 frames; ~3-4 min at --fps 1)
   DensifyPointCloud scene.mvs -o scene_dense.mvs --resolution-level 1 --number-views 5 \
       --geometric-iters 1 --max-threads 16
   # B: A + fewer views
   DensifyPointCloud scene.mvs -o scene_dense.mvs --resolution-level 1 --number-views 4 \
       --geometric-iters 1 --max-threads 16
   # C: level 2 as the floor (0.12 MP depth maps; check that the F@5cm/coverage metrics hold)
   DensifyPointCloud scene.mvs -o scene_dense.mvs --resolution-level 2 --geometric-iters 1
   ```
   Pipeline: `run.py` already exposes `resolution_level` and `number_views`; `--geometric-iters`
   needs one arg in `mvs.py`. Score each variant with the E1 harness. Expect PSNR/SSIM to barely
   move, because texture detail is independent of densify level. F@2cm may drop.
2. **Coverage flags (70 % → ?):**
   ```sh
   ReconstructMesh scene_dense.mvs -o mesh.ply --free-space-support 1 --close-holes 60
   TextureMesh scene_dense.mvs -m mesh.ply -o tex.glb --virtual-face-images 3 \
       --decimate <auto> --max-texture-size 4096 --export-type glb
   ```
   Also test `DensifyPointCloud --postprocess-dmaps 2` (fill gaps).
3. **Sharpness-aware frame selection** (AliceVision / Metashape style) replacing fixed `--fps 2`.
   Extract at 10 fps and 960 px, score the Laplacian variance, keep the sharpest frame in each
   window, and reject frames below 0.3× the median sharpness.
   ```python
   import cv2, glob, numpy as np
   fs = sorted(glob.glob("f10/*.jpg"))            # ffmpeg -i in.mp4 -vf fps=10,scale=960:-1 f10/%05d.jpg
   s = np.array([cv2.Laplacian(cv2.imread(f, 0), cv2.CV_64F).var() for f in fs])
   win = 5                                        # 0.5 s windows -> 2 fps; 10 -> 1 fps
   keep = [i + int(np.argmax(s[i:i + win])) for i in range(0, len(fs), win)]
   keep = [k for k in keep if s[k] > 0.3 * np.median(s)]
   ```
   Then re-extract the kept timestamps at full resolution (the pipeline already seeks frames by
   timestamp). It is also worth a motion gate: skip windows where the median optical flow between
   kept frames is below about 2 % of the image width (Metashape's "Small" is 3 %).
4. **Exact face count at mesh time** instead of `_auto_decimate_ratio`:
   `ReconstructMesh --target-face-num 400000` (then crop, then `TextureMesh --decimate` to 200 k),
   or keep today's flow. Low priority.
5. **KTX2 for the Quest (§5).** One post-process step in `package.py` after the GLB export. It is
   the single biggest runtime-memory win, and it frees budget to go to about 400 k triangles or two
   4 K atlases for the kitchen.
6. **Learned features for low light via hloc**, not pycolmap ALIKED (which aborts). Install into
   the vggt env, which already has ROCm torch:
   ```sh
   git clone --recursive https://github.com/cvg/Hierarchical-Localization /workspace/hloc
   /workspace/envs/vggt/bin/pip install -e /workspace/hloc --no-deps && \
     /workspace/envs/vggt/bin/pip install kornia h5py lightglue@git+https://github.com/cvg/LightGlue
   # then: extract_features conf 'aliked-n16', pairs_from_sequential (overlap ~10) + retrieval,
   # match conf 'aliked+lightglue', reconstruction -> COLMAP model -> existing undistort/OpenMVS path
   ```
   - The hloc conf names and module entry points need checking against the current repo
     (UNVERIFIED).
   - It passes if all 175 frames register in one component.
7. **Optional: build OpenMVS `develop` (CPU) and trial `--fusion-mode -2` (SGM) plus the 1.0
   fusion threshold.**
   - Only worth it if item 1 still leaves densify above about 5 min.
   - It is a vcpkg build of about 30 min+ and needs `-DOpenMVS_USE_CUDA=OFF`.
   - Also gets the TetraFlow `ReconstructMesh` (1.8× faster).
8. **Optional: an ODM Docker baseline** for a Poisson-mesh coverage datapoint:
   ```sh
   docker run -ti --rm -v /workspace/odm:/datasets opendronemap/odm --project-path /datasets kitchen \
       --video-limit 200 --pc-quality medium --mesh-size 200000 --gltf --texturing-single-material
   ```
   Docker inside the hfbox container is UNVERIFIED and likely unavailable. If so, skip.
9. **Blocked until a ROCm SDK exists on hfbox:** COLMAP `HIP_ENABLED` patch-match, OpenSplat HIP,
   AliceVision SYCL. First step for whoever tries them: install ROCm 7.2 `hip-dev hiprand-dev
   rocrand-dev` into a `/workspace` prefix, or use a `rocm/dev-ubuntu-24.04` image.
10. **Not worth time on AMD:** Nerfstudio, gsplat-amd (MI300X-only), 2DGS/PGSR/GOF/MILo, InstantSfM,
    DROID-SLAM/MASt3R-SLAM/DPVO, instant-ngp, SDFStudio, Kaolin, Postshot, LichtFeld. Brush only
    as a demo.

### 6.2 Needs CUDA or the cloud (AWS/Azure NVIDIA)

1. **OpenMVS CUDA build: the full-quality mesh in minutes.**
   - Published: 14–17× faster densify on CUDA, and 2–4 min for 251–410 images.
   - Our 27 min would plausibly become about 2–3 min at level 0, and under 1 min at level 1.
     `RefineMesh` and `TextureMesh` also have `--cuda-device`.
   ```sh
   # on g6e.xlarge (L40S) / g5.xlarge (A10G), Ubuntu 24.04 + CUDA 12.x
   git clone --recurse-submodules -b develop https://github.com/cdcseacave/openMVS && cd openMVS
   cmake -S . -B make -DCMAKE_BUILD_TYPE=Release -DOpenMVS_USE_CUDA=ON \
         -DCMAKE_CUDA_ARCHITECTURES=89 -DCMAKE_TOOLCHAIN_FILE=$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake
   cmake --build make -j
   DensifyPointCloud scene.mvs --resolution-level 0 --number-views 8 --cuda-device -1
   ```
   `CMAKE_CUDA_ARCHITECTURES=89` is for the L40S; use 86 for the A10G. vcpkg manifest mode with
   the `cuda` feature is per CMakeLists [src]; the exact configure line is UNVERIFIED.
2. **`pycolmap-cuda12`** for GPU SIFT and matching (a pip wheel; SfM 113 s → about 20–40 s,
   estimate), and GPU bundle adjustment through Caspar (4.1).
3. **Splat → mesh quality tier:** PGSR or MILo on COLMAP poses (about 30 min on an L40S per scene),
   then OpenMVS `TextureMesh` on the extracted mesh. It is the only route likely to beat PatchMatch
   on textureless kitchen surfaces. It belongs to the R1 shortlist, not this doc.
4. **RealityScan 2.2 on a Windows GPU VM** with `-importColmap` (see `p1-colmap-realityscan.md`),
   as an independent quality and speed reference.
5. **Metashape** (trial licence) on the same VM, or on hfbox via OpenCL if the ROCm OpenCL runtime
   can be installed, as the second commercial reference.

### 6.3 For the aerial building target, from the products

- Plan the capture like Skydio or Hyperscape:
  - an outer orbit at a constant standoff with 70–80 % overlap;
  - a second orbit at a different altitude and gimbal pitch (−30° and −60°);
  - close detail passes.
- Use DJI SRT GPS (outdoors) as pose priors (`p1-colmap-realityscan.md` §1.2.3).
- Use `DensifyPointCloud --mask-path` with sky masks; `--tower-mode` and `--up-axis` for tall
  facades; `--sub-scene-area` if the site is large.
- For city-block scale, split into 3D Tiles or a few LOD GLBs rather than one 200 k mesh.

---

## 7. Verified vs unverified (summary)

**Verified hands-on on hfbox:**
- The OpenMVS v2.4.0 `--help` for Densify, ReconstructMesh and TextureMesh (flags and defaults
  above).
- The kitchen densify phase timings and peak memory.
- The `InterfacePolycam` binary is present.
- No `/opt/rocm` and no `hipcc`; torch sees the RX 9070 XT; a Mesa `radeon_icd.json` Vulkan ICD is
  present.
- pycolmap 4.2 exposes the ALIKED/LightGlue/LoMa enums, and ALIKED aborts for lack of ONNX
  (tested on 6 kitchen frames, then cleaned up).
- Release dates via the GitHub API: OpenMVS v2.4.0 on 2026-01-20; ODM 3.6.2 on 2026-08-12; Brush
  0.3.0 on 2025-09-14; gsplat 1.5.3 on 2025-07-04; Nerfstudio 1.1.5 on 2024-11-11; Meshroom 2025.1.0
  on 2025-08-19; OpenSplat moved to WebODM; ROCm/gsplat moved to AMD-Ecosystem/gsplat-amd.

**Primary source, not run:**
- The OpenMVS `develop` PRs #1292, #1296, #1298, #1305 and #1306, and the SGM design doc (all
  timing tables).
- The ODM arguments and `gpu.py`; AliceVision PR #2077 and `KeyframeSelector.hpp`; texrecon
  `arguments.cpp`; the COLMAP `mvs.cc` commands.
- The InstantSfM cuDSS dependency; the VGGSfM, VGGT-X and VGGT-Long READMEs.
- The ORT MIGraphX EP; the AMD gsplat README.
- The Metashape 2.2 and 2.3 manuals; Apple WWDC transcripts; Meta's Hyperscape help page; Meta's
  Quest performance page; the `KHR_texture_basisu` and `MSFT_lod` specs; the gltfpack README.

**UNVERIFIED:**
- All speed estimates for untried flag combinations.
- Whether gfx1201 works for any HIP source build.
- Brush actually running headless on hfbox.
- hloc conf names; gltf-transform flag spellings; glTFast's index and JPEG handling.
- The internals of Polycam, Luma, Matterport, DroneDeploy, Bentley splats and RealityCapture
  meshing.
- Hyperscape's streaming-stack name.
- Gracia's, Arrival.Space's and Teleport's Quest renderers; Scaniverse on-device timing.
- Apple area mode.
