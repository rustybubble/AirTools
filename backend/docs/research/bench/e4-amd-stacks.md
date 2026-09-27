# E4: third-party AMD reconstruction stacks on kitchen-0095

This log covers the 2026-09-25 bench session. It tests the stacks that `r3-community.md` §3–4 says
run on our RX 9070 XT against our pipeline, using the same 175 frames (`work/dji0095/frames_nominal`,
2 fps, 1920×1080) or our COLMAP model. Every run was scored by E1's harness
(`python -m pipeline.experiments.bench score ... --id-fps 10`) against `/workspace/bench/ref`.
Result JSONs and side-by-sides (photo | render | diff) are in `hfbox:/workspace/tools/e4/results/`.

Scripts are in `pipeline/experiments/amd_stacks/`:

| file | what |
|---|---|
| `spirula.sh` | Spirula end to end: `sfm auto` → `geometry` (MoGe-2 normals) → `train meshing` → `mesh --color texture --format glb`, with every stage timed |
| `cheshire.sh` | Cheshire / AliceVision, run as Meshroom's legacy photogrammetry graph from the CLI (no Meshroom). `SFM_IN=` skips AliceVision SfM and uses our poses |
| `colmap_to_av.py` | our undistorted COLMAP model → AliceVision SfMData JSON (poses, pinhole intrinsics, 11k landmarks) |
| `to_bench.py` | COLMAP model or AliceVision `.json` plus any mesh → `mesh.glb` + `cameras.json` for the harness. It merges multi-atlas meshes into one texture and takes `--transform` for axis flips |

## 1. Results

Box: hfbox, RX 9070 XT (gfx1201), Mesa 25.2.8 RADV for Vulkan, and Cheshire's own bundled HIP
7.2 runtime. The box was shared the whole time: E1 was running timed CPU jobs and E3 GPU jobs,
with load 7–39 on 16 cores. At 07:40 the lead asked me to pin my jobs to **cores 12–15** (4
threads, `nice 19`), and everything I started after that ran pinned. The GPU was shared with E3
throughout.

| stack / run | install | time on kitchen | outputs | view cov | PSNR | SSIM | Chamfer cm | F@2cm | F@5cm | tris | Quest |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **ours** b-r2-full (COLMAP 4.2 + OpenMVS res 0), E1 | – | 1972 s (32.9 min), idle box | textured glb | 0.701 | 20.67 | 0.740 | 0.73 | 0.926 | 0.994 | 200k | ok |
| **ours** d-res1 (OpenMVS res 1), E1 | – | 720 s (12 min) | textured glb | 0.773 | 20.70 | 0.726 | 1.28 | 0.823 | 0.953 | 200k | ok |
| **Cheshire on our poses**, quadric 200k (`e4-cheshire-ourposes-q200k`) | prebuilt bundle, 2 min | dense 690 s (11.5 min) + our SfM ≈ 130 s → **≈ 14 min** | textured obj (3 × 4096 atlases), depth/sim maps | **0.896** | **21.07** | **0.766** | 3.81 | 0.566 | 0.730 | 184k | tris ok, 3 atlases |
| Cheshire on our poses, full mesh (`e4-cheshire-ourposes`) | ″ | dense 686 s | ″ | 0.896 | 21.12 | 0.749 | 3.81 | 0.564 | 0.731 | 1.18 M | over |
| **Cheshire end to end** (AliceVision SfM + dense) (`e4-cheshire-e2e`) | ″ | **1224 s (20.4 min)** on 4 CPU cores; ≈ 16 min with 16 cores (featureMatching 151 s vs 402 s) | textured obj, AliceVision SfM (163/175) | 0.921 | 20.33 | 0.703 | 3.51 | 0.576 | 0.747 | 1.25 M | over |
| Spirula, checkpoint at 18k of 30k steps (`e4-spirula-18k`) | single binary, 1 min | sfm 57 + normals 45 + train 1740 (killed at 18.2k steps) + mesh 53 s | splat ply, textured glb, COLMAP model, normals | **0.950** | 19.57 | 0.722 | 5.75 | 0.354 | 0.618 | 293k | over (tris) |
| **Spirula 30k, full run** (`e4-spirula-30k`) | ″ | sfm 57 + normals 45 + train 2125 + mesh 80 → **2307 s (38.5 min)** | ″ | 0.987 | 16.06 | 0.552 | 6.70 | 0.266 | 0.504 | 1.12 M | over |
| Spirula 30k, `--texture-size 4096 --floater-min-faces 2000` (`e4-spirula-30k-t4k`) | ″ | mesh 122 s | ″ | 0.985 | 16.08 | 0.575 | 6.79 | 0.263 | 0.501 | 990k | over |
| Brush 0.3.0 on our COLMAP (splat, **not a mesh**) | single binary, 1 min | train 30k steps **1037 s (17.3 min)**, ~30 steps/s | splat ply (98k splats, 23 MB), eval renders | – | own eval: 35.41* | 0.965* | – | – | – | – | – |

\* Brush's own metric on 21 held-out *training-set* frames (every 8th of our 165). Those frames
sit 0.5 s from their neighbours, so the number is not comparable with the harness numbers.

Every evaluation frame is held out for every run, so held-out PSNR equals PSNR. Chamfer and F
are measured against an OpenMVS reference cloud, which favours OpenMVS-like meshes (see
`e1-experiments.md` §1). For Cheshire, **recall@2cm is 0.92** (completeness 0.96 cm, as complete as
the reference) but precision is only 0.41. Much of the "inaccuracy" is surface that OpenMVS never
reconstructed (walls, cabinet fronts), which the reference cannot score. Held-out PSNR/SSIM and
view coverage are the method-neutral numbers.

Screenshots (photo | render | diff, eval frames 0003/0153/0303/0453/0603/0753):
`hfbox:/workspace/tools/e4/results/e4-<run>/<id>.png`; ours: `hfbox:/workspace/bench/results/b-r2-full/`.

## 2. Cheshire (HIP AliceVision), v0.3.3

**Install: done in ~2 min, no ROCm SDK needed.** `cheshire-alicevision-hip-linux-x64-rocm7.2.tar.gz`
(123 MB, SHA256 OK) bundles `libamdhip64.so.7`, `libhsa-runtime64` and `libamd_comgr`. The only
missing library was `libnuma.so.1`, which I got with `apt-get download libnuma1` + `dpkg -x` and
copied into `bundle/lib/`. That needs no root package install. `/dev/kfd` is present in the
container, and the GPU was detected on the first run ("Supported CUDA-Enabled GPU detected",
GPU SIFT `[gpu]`). E2's `/workspace/rocm` SDK was not needed.

It is not a Meshroom install: it is the AliceVision 3.4-dev CLI nodes. I ran Meshroom's legacy
photogrammetry graph by hand (`cheshire.sh`).

**Timings, dense stages on our poses** (`work/cheshire-kitchen-ourposes/times.txt`, 165 views):

| stage | s | notes |
|---|---|---|
| prepareDenseScene | 21 | 16 cores, load 30–39 |
| **DepthMap** (GPU, downscale 2) | **473** (2.9 s/view) | 16 cores; the GPU was shared with Spirula training and E3. This matches the author's ~3 s/view |
| DepthMapFilter (GPU) | 6 | |
| Meshing (GPU votes + GPU max-flow) | 148 | restarted, pinned to 4 cores |
| MeshFiltering | 9 | 4 cores |
| quadric decimate to 200k (fast_simplification) | 4 | 4 cores |
| Texturing (4096, no subdivision) | 29 | 4 cores |
| **total dense** | **690** | vs OpenMVS res 0 ≈ 1840 s, res 1 ≈ 590 s (E1) |

**End to end** (`work/cheshire-kitchen-e2e`, AliceVision SfM too, all pinned to 4 cores except the
GPU): cameraInit 1, featureExtraction (GPU SIFT) 10, featureMatching 402 (CPU geometric
verification; 151 s on 16 cores in the first attempt), SfM 85 (163/175 registered), then dense
23 + 519 + 12 + 125 + 11 + 36 → **1224 s**.

**Gotchas found (all in `cheshire.sh` or the converters now):**
1. **AliceVision SfM registered only 11/175 frames at first.** Our frames have no EXIF, so
   `cameraInit` gives views path-hash IDs, and `imageMatching --method Sequential` pairs
   *hash-adjacent*, which means random frames. `--viewIdMethod filename` fixes the order, and it
   then registered 163/175. Locking a pinhole focal did not help before that fix. No vocabulary
   tree ships with the bundle, so Sequential (30 neighbours) stood in for Meshroom's exhaustive
   default below 200 images.
2. **AliceVision writes OBJ meshes with Y and Z negated** relative to its SfM frame. `to_bench.py
   --transform av_flip.json` (diag(1, −1, −1)) aligns them. Without it the harness saw coverage 0.
3. Texturing writes **no texture images** unless `--colorMappingFileType png`, and it re-subdivides
   the mesh (200k → 960k tris) unless `--subdivisionTargetRatio 0`.
4. The mesh spans three 4096 atlases (UDIM off). trimesh's `to_geometry()` merge scrambles
   multi-atlas UVs (render PSNR 11.7, pure noise), so `to_bench.single_atlas` tiles them into one
   8192 atlas. For the Quest budget, a real run would need `--textureSide 8192` downscaled, or a
   repack.
5. `meshing -o` is ambiguous (`--output`/`--outputMesh`), so use the long names.
6. **Cheshire's GPU meshing pass resets the GPU for other contexts.** Twice, a concurrent Spirula
   (Vulkan) training died with `VK_ERROR_DEVICE_LOST` ("radv: context is lost. This context is
   innocent"). The first was at 07:42:54, when Cheshire's meshing vote kernel started at 07:42:56.
   The second was at 08:29:05, during the vote pass 08:28:55–08:29:15, which ray-marches 26 M
   rays over 17 M cells for about 20 s. E3's jobs in those windows exited 0. **Do not run
   Cheshire meshing next to another GPU job.** Cheshire has `CHESHIRE_GPU_VOTE=0` for a CPU pass.
   [verified by timestamps, cause inferred: a long compute dispatch trips the amdgpu ring timeout]

**Pose injection** (`colmap_to_av.py`). AliceVision JSON stores `rotation` as world→camera R in
column-major order, `center` as the camera centre, focal in mm over `sensorWidth`, and
`principalPoint` as the offset from the image centre. I checked this two ways. Projecting
AliceVision's own landmarks gave < 1.5 px error. Round-tripping our 165 cameras through
`aliceVision_exportColmap` gave max |ΔR| 1e-16 and |Δt| 2e-15. One bug was caught on the way:
`repr(np.float64)` writes `np.float64(27.3)`, which AliceVision silently parsed as focal 1 mm
(53 px).

**Quality.** On our poses, Cheshire has the best photometric score of any mesh so far: 21.07 / 0.766
at 184k tris, against 20.67 / 0.740 for ours. Its view coverage is 0.90 against 0.70, because it
fills walls and cabinet fronts. Frame 0303 (`e4-cheshire-ourposes/0303.png`) has a complete
backsplash wall, counter and appliances. Upper cabinet doors are blotchy, and the counter edge is
speckled. Geometry is worse against the OpenMVS reference (Chamfer 3.8 cm vs 0.73; F@2cm 0.57 vs
0.93), but with recall@2cm at 0.92 the gap is mostly extra surface. A neutral geometry check would
need a non-OpenMVS reference.

**Licence.** MPL-2.0 (AliceVision) plus Cheshire's own patches (MPL-2.0). It is file-level copyleft,
so it is fine to ship as an unmodified external binary called by our pipeline. Modified files would
have to be published. The bundle also carries the MIT libmv code and ROCm runtime libraries
(MIT/NCSA). The project has one author and 4 stars, so pin a version.

## 3. Spirula Studio, v2026.9.24

**Install: done in 1 min.** `spirula-2026.9.24-ubuntu-vulkan-x86_64.zip` holds a single 167 MB
binary with no missing libraries. Vulkan was already present: the loader and RADV ICD ship in the
image, and only `vulkaninfo` was missing, which I got with `apt-get download vulkan-tools` +
`dpkg -x` into `/workspace/tools/vktools`. GPU0 is `AMD Radeon RX 9070 XT (RADV GFX1201)`,
16 GB. RADV prints "not a conformant Vulkan implementation" but works.

**Headless: yes.** It has a full CLI (`sfm`, `geometry`, `train`, `mesh`, `sam`, `encode`), and
`train` also serves a web viewer on :7007 (harmless). `spirula.sh` runs it end to end.

| stage | s | notes |
|---|---|---|
| `sfm auto --data-type video` (GPU SIFT, incremental) | **57** | 164/175 registered (ours: 165 in 113 s); 16 cores |
| `geometry` (MoGe-2 ViT-S normals, ONNX on Vulkan, 1064 px) | 45 | 164 images, about 0.2 s each; model auto-downloaded, no account |
| `train meshing` (30k steps, 1 M splats cap) | **2125** (run 3, 35:25) | runs 1 and 2 died with GPU device loss at 18.2k and 19.9k steps (§2 gotcha 6). Run 3 ran pinned to 4 cores, with the GPU shared only with E3. Run 1 had 16 cores until 07:40 and was at about the same pace (18.2k steps in 29:00 against 35:25 for 30k), so training is GPU-bound and 4 cores do not slow it |
| `mesh --color texture --format glb` | 53 | 293k faces, one 4096 texture; 4 cores |

Add `--disable-viewer 1 --keep-viewer-alive 0` for headless runs. Without them `train` waits for
Ctrl-C after it finishes; `spirula.sh` now passes both.

**Quality at 30k steps (full run) is worse than at 18k.** It scored PSNR 16.06, SSIM 0.552 and
Chamfer 6.7 cm. The mesh has 1.12 M faces in 4217 components, with a 2048 texture that the
texel budget auto-picked. Frame 0303 (`e4-spirula-30k/0303.png`) shows the backsplash and cabinets
as a patchwork of wrong-coloured blocks, over a lumpy surface. Re-meshing with a 4096 texture and
floater culling (2 components left) did not help (16.08 / 0.575), so the problem is the geometry
from the 30k splats, not the bake. I did not find out why late training degraded the mesh (the
end-of-run densify/opacity schedule with 1 M splats is one candidate) or whether a second run
would vary; the 90-minute time-box ran out.

**Quality at 18k steps.** It has the highest view coverage (0.95) because it fills everything,
but PSNR is 19.57 and SSIM 0.722, and the geometry is worst (Chamfer 5.8 cm, recall@5cm 0.81).
Frame 0303 (`e4-spirula-18k/0303.png`) shows black holes and torn triangles on the backsplash,
the tile wall and inside the sink, smeared cabinets, and a wavy counter edge. The splat itself
trained to about 35–38 dB PSNR on the training views, so the loss is in the Gaussian → mesh
extraction on blank walls. Its model came out within 5.9% of metric scale. That is a coincidence of `sfm auto`'s unit-scale
normalisation, not a measurement, because no telemetry was given. `--metric-positions` could take
our caption-altitude positions; that is untested.

**Licence.** GPL-3.0. It is fine as a separately executed tool (subprocess, no linking), and the
output meshes are ours. Bundling or modifying it into our distribution would make that
distribution GPL.

## 4. Brush 0.3.0

**Install: done in 1 min.** `brush-app-x86_64-unknown-linux-gnu.tar.xz` holds a single 171 MB
binary, and wgpu picked the RX 9070 XT on RADV. It reads our undistorted COLMAP model directly
(`ds/images` → `sfm/dense/images`, `ds/sparse/0` → `sfm/dense/sparse`), and COLMAP 4.2's extra
`frames.bin`/`rigs.bin` do not bother it.

Run: `brush_app ds --total-steps 30000 --eval-split-every 8 --eval-every 10000 --eval-save-to-disk
--export-every 5000 --export-path out`, pinned to 4 cores. The runner is
`hfbox:/workspace/airtools/work/brush-kitchen/run.sh`. Headless, it prints nothing unless it has a
tty; `script -qfc` fixes that, and the first 12-minute attempt was restarted for this reason.

| | value |
|---|---|
| wall, 30k steps | **1037 s** (it includes wgpu autotune on the first steps; the GPU was shared with E3) |
| splats | 97,645. Growth stops early by default, 10× fewer than Spirula's 1 M cap |
| held-out PSNR / SSIM (Brush eval, 21 views) | 10k: 32.75 / 0.962; 20k: 34.51 / 0.964; 30k: **35.41 / 0.965** |
| outputs | `out/export_{05000..30000}.ply` (3DGS ply), `out/eval_*/` renders |

There is no mesh and no harness score. A splat → mesh step (for example Spirula's `mesh` on
`export_30000.ply`, which accepts a `splat.ply`) would be the next thing to try; that is untested.
Brush is Apache-2.0, so there are no adoption concerns.

## 5. Verified vs inferred

- **Verified** (run on hfbox, scored by the harness): every row in §1. Brush's PSNR is its own eval, not the harness.
  Also verified: Cheshire ~3 s/view DepthMap on gfx1201, Spirula SfM 57 s, the harness converter
  (our own mesh through `to_bench.py` reproduces b-r2-full: 20.69 / 0.740 / cov 0.701 / Chamfer
  0.73), and the AliceVision pose round trip.
- **Inferred:**
  - The Cheshire-on-our-poses total of ≈ 14 min: its dense time plus E1's 130 s SfM cache, not
    one timed run.
  - The ≈ 16 min Cheshire end to end on 16 cores: from the featureMatching difference.
  - The cause of the GPU resets.
  - All timings are on a shared box and would be faster idle, especially DepthMap, which shared
    the GPU with Spirula training.
