# P1 reconstruction bench: kitchen-0095

A living document for the research and experimentation session that started 2026-09-25. It aims to
find the most accurate reconstruction of real drone footage that covers the whole area and runs
fast. Every method we read about or try gets a row in §3 and a section in §4. The per-agent notes
live in `docs/research/bench/`, and this file is the merged view.

## 1. Benchmark

- **Footage:** `vid/DJI_0095.MP4`, our own DJI Mini 4K. Indoor kitchen, 87.25 s of 3840×2160
  at 29.97 fps, with a 1 Hz caption track (H, H.S, V.S, no GPS). hfbox copy:
  `/workspace/airtools/data/real/DJI_0095.MP4`.
- **Box:** hfbox, with an AMD RX 9070 XT 16 GB (ROCm 7.2), 16 cores and 30 GB RAM. There is no
  CUDA. Methods that need NVIDIA are marked as such and either ported or run elsewhere.
- **Goal:** the whole kitchen, not a cropped subject, so the building auto-crop is off for this
  bench.

## 2. Metrics

A single harness scores every method the same way (`pipeline/experiments/bench/`):

| Metric | Definition |
|---|---|
| **Speed** | Wall-clock seconds on hfbox, from video on disk to a published mesh. Upload is reported separately. |
| **Coverage (view)** | Mean fraction of pixels covered by the rendered mesh over the evaluation frames. These are evenly spaced across the whole clip, and include frames the method never saw. |
| **Coverage (registration)** | Fraction of evaluation frames that the method's cameras cover in time. |
| **Fidelity** | PSNR / SSIM (and LPIPS where available) on covered pixels at the evaluation frames. Held-out frames are reported separately. |
| **Geometry** | Chamfer distance and F-score at 2 cm and 5 cm against the reference dense cloud, after aligning each method to the reference cameras by timestamp. |
| **Detail** | Rendered sharpness relative to the real frame (Laplacian-variance ratio), plus triangle and texture density on the covered surface. |
| **Scale accuracy** | The method's metric scale against the reference. Against tape-measured kitchen dimensions once we have them. |
| **Quest budget** | Triangles, texture px and GLB MB, checked against about 200k tris plus a 4K atlas. |

## 3. Methods table

| # | Method | Type | Runs on hfbox? | Speed (s) | View coverage | PSNR / SSIM | F@5cm | Detail | Notes |
|---|---|---|---|---|---|---|---|---|---|
| 0 | Current pipeline, full (COLMAP 4.2 global + OpenMVS, 2 fps, auto-crop) | classic | yes | 2039 | 0.70 (12 frames, old QA) | 20.8 / 0.74 | | | first real run, `p1-quality-runs.md` |
| 0p | Current pipeline, preview (VGGT 48 frames + TSDF + TextureMesh) | feed-forward | yes | **74 (idle re-time)** | 0.818 | 18.29 / 0.645 | 0.810 | 37k tris | scale fixed: it was 45% off because the preview never ran the caption fit; now within 0.8% of the full stage |

Candidates from the research. Figures are the **claimed or estimated** ones from the source docs
until the harness measures them; the status column tracks progress.

| # | Method | Type | hfbox? | Claimed / est. speed | Expected gain | Status | Source |
|---|---|---|---|---|---|---|---|
| 1 | OpenMVS flag tuning: `--resolution-level`, `--number-views`, `--geometric-iters`, `--min-resolution`; `--fps 1/2/3/4` | classic | yes | **measured on 12 cores, fresh: fps2 723 s vs fps1 373 s**; v2.4.0 res2 + min-res 320 + nv3 densify 126 s | fps1 halves the time at similar coverage and PSNR, but lap-corr detail drops from 0.54 to 0.39; fps3 is within noise of fps2; res2/minres320/nv3 coverage 0.793 | **E1 done: res1 + nv3 is the new pipeline default (`32146ca`)**; fps2 kept | R2 §2.1, E1 |
| 2 | OpenMVS `ReconstructMesh --free-space-support 1`, `TextureMesh --virtual-face-images 3` | classic | yes | ~same | coverage of blank walls; fewer seams | E1 queued | R2 §2.1 |
| 3 | **`--densify depthfusion`**: MoGe-2 metric depth, plane-fit to COLMAP points, TSDF 1 cm, OpenMVS TextureMesh | hybrid | **yes (MIT)** | **measured: est. total 397 s (4 cores; densify 43.5 s on a free GPU)** vs 1886 s | **view coverage 0.985 vs 0.704; novel-view 0.90/0.78 vs 0.56/0.50; PSNR/SSIM 21.33/0.785 vs 20.91/0.751**; but Chamfer 3.57 vs 0.69 cm, F@2cm 0.62 vs 0.93 (reference is OpenMVS's own cloud, so this favours it) | **E3 done** (`5d10e69`…`b59437c`); **idle re-time: 269 s end to end, §5.1** | E3 |
| 3b | depthfusion with **WorldMirror** (COLMAP pose/K priors) | hybrid | yes (Tencent licence: excludes EU/UK/KR; separate licence above 1M MAU) | measured: 428 s (4 cores), 10.4 GB VRAM at chunk 40 | coverage 0.963; **PSNR 21.69**; Chamfer 2.96 cm; F@2cm 0.70 | E3 done; **idle re-time: 290 s, §5.1** | E3 |
| 3c | **`--densify hybrid` (union)**: OpenMVS res1/nv3 dense cloud, plus WorldMirror TSDF vertices only where the cloud is empty, then ReconstructMesh and TextureMesh | hybrid | yes (WorldMirror's Tencent licence; `--model moge2` is the MIT option) | **measured h40 end to end, 4 cores with contention: 1266 s** (densify 952, worker 29, ReconstructMesh 55, TextureMesh 189; WorldMirror hidden in parallel on the GPU); an estimated ~600 s on 16 cores (unmeasured) | **res1: coverage 0.994, novel-view 0.91/0.78, PSNR/SSIM 21.92/0.812, completeness 0.76 cm, recall@2cm 0.951, 5cm 0.994**, lap 0.56, 200k tris, 4K atlas. res0: coverage 0.984, PSNR/SSIM 22.04/0.806, completeness 0.58 cm ; **fill-model ablation on the same res1 cloud: MoGe-2 fill (h46, MIT, about 3 GB VRAM) coverage 0.997, PSNR/SSIM 21.73/0.805, completeness 0.77 cm, recall@2cm 0.950, vs WorldMirror fill (h41) 0.984, 21.75/0.806, 0.76 cm, 0.952: a tie, so MoGe-2 is the default**; **E7 fill distance 1 cm vs 2 cm (MoGe-2 fill, paired on one dense cloud): 21.88/0.809 vs 21.87/0.806, lap 0.56 both, 451 s either way: a tie, so 2 cm stays** | **E5 done** (`02a9338`, `28861be`, `56d643d`); **idle re-time: 604 s end to end, coverage 0.996, §5.1: recommended default** | E5 |
| 4 | Scale-cue ensemble (altitude on good poses + depth ratio + known dims + MapAnything) | scale | yes | 1–3 min | metric scale (45% gap) | E1 queued | R1 §6, §9 #2 |
| 5 | VGGT-1B `vggt-low-vram`, all 175 frames | feed-forward | yes | 45–120 s | preview coverage + pose quality | E1 queued | R1 §9 #3 |
| 6 | VGGT-Ω 1B-512 | feed-forward | yes (gated weights) | **measured online: 25 s / 40 frames, 82 s / 88 frames** | novel-view coverage 75–80% vs our 45–47%; cam track within 9–11 mm of COLMAP; noisy points, no mesh, no scale (×1.68), held-out PSNR ≤ ours | **needs user HF access request**; NC licence; ROCm-clean code (R4) | R1 §1, R4 |
| 7 | 2DGS via gsplat `rasterization_2dgs` (gfx1201 port), 15k iterations with MoGe-2 depth/normal priors, then rendered depth, TSDF and TextureMesh (E3 tail) | splat→mesh | **yes, E2 built it** (`/workspace/envs/gsplat`) | measured on 4 cores with contention: est. total ~976 s (SfM 130 cached, priors 72, train+export 549, tail 225) | coverage 0.938, novel-view 0.85/0.73, PSNR/SSIM 21.56/0.751, Chamfer 3.44 cm, F@2cm 0.600, lap 0.45. Without priors much worse (Chamfer 5.3–5.6, F@2 0.42–0.44). The splat itself scores 30.6 dB held-out | **E6: rejected**; slower and no better than MoGe depthfusion (row 3); not in the re-time | R1 §9 #5, E2, E6 |
| 8 | VGGT-X `--use_ga` as SfM replacement | feed-forward SfM | yes | 2–5 min | speed; fixes 2-component split | queued | R1 §9 #6 |
| 9 | COLMAP 4.2 HIP `patch_match_stereo` | classic GPU MVS | **yes, E2 built it** (`/workspace/tools/colmap-hip`) | **measured: 78 s/view photometric** (GPU shared) vs OpenMVS CPU ~9 s/view | none | **rejected: the HIP port is slow** (the PR's own 7900 XTX figure is ~24 s/view too) | E2 |
| 10 | OpenMVS `develop` CPU SGM densifier (`--fusion-mode -2`) | classic | **yes, E2 built it** (`/workspace/tools/openmvs-develop`) | **measured on kitchen: densify 144 s vs 618 s PatchMatch, same binary (4.3×)**; one fusion segfault, then passed on retry | F@2cm −0.03, F@5cm −0.014 vs PatchMatch, but the develop build covers only 0.59–0.61 of views vs 0.75 for v2.4.0 (SSIM 0.67 vs 0.77); its GLB carries a Y-up node rotation (fixed in `aa82eea`) | **E1: rejected**; v2.4.0 res2 is faster at better coverage | E2, E1 |
| 11 | ALIKED + LightGlue features | learned features | **yes** (COLMAP 4.2 build with ONNX; CPU) | **measured: 558 s on CPU ONNX vs 116 s for SIFT CLI** | 175/175 registered for both; reprojection error 1.17 px vs 0.62 px for SIFT | **E1: rejected** (slower and less accurate on this footage) | R2, E1 |
| 12 | Depth Anything 3 (+ DA3-Streaming) all-in-one | feed-forward | yes (xformers risk) | 1–3 min | pose + depth + metric | queued | R1 §9 #10 |
| 13 | PGSR / MILo / RaDe-GS / GOF | splat→mesh | CUDA only | 45–60 min on L40S | best geometry | cloud tier, not run | R1 §3, §9 #8–9 |
| 14 | OpenMVS CUDA build (cloud L40S) | classic GPU | CUDA only | PatchMatch 14–17× faster (251–410 images in 2–4 min) | speed | cloud tier, not run | R2 §2.1 |
| 15 | Agisoft Metashape 2.3 (AMD via OpenCL, Linux) | commercial | likely | GPU depth maps | quality baseline | paid ($3.5k); not run | R2 §4.11 |
| 16 | RealityScan 2.2 | commercial | Windows only | fastest desktop (reported) | quality baseline | not run | `p1-colmap-realityscan.md` |
| 17 | Online tools (HF Spaces) | various | n/a | see R4 | comparison | **done:** VGGT-Ω and WorldMirror ran; MapAnything/DA3/MASt3R/InstantSplat blocked by the ZeroGPU quota | R4 |
| 24 | HunyuanWorld-Mirror (Tencent, open weights; accepts camera priors) | feed-forward | likely (to test) | **measured online: ~105 s / 40 frames** | **most coverage: 97–100% of held-out views** (ceiling and floor too); poses within 12 mm of ours; blotchy per-frame colour 2–8 dB below r2; 207 MB GLB; no scale (×1.43) | E3: use as a depth source with our COLMAP poses, then TextureMesh | R4 |
| 18 | Sharpest frame per time window (Tenengrad/Laplacian) instead of fixed 2 fps | frame selection | yes | same cost as fps2 | sharpest-of-2 at fps4 gave the best F@2cm (0.832) of the fps sweep; **E7 repeat with the hybrid default, fresh: 644 s vs 626, coverage 0.995, PSNR/SSIM 21.70/0.802 vs 21.51/0.807, F@2cm 0.611 vs 0.596, lap 0.50 vs 0.54, 165/175 registered** | **E7: rejected**, within noise and costs 40 s more SfM; `--fps 2` stays | R3, E1, E7 |
| 19 | Fixed/prior intrinsics | SfM config | yes | ~free | none on this clip | **rejected by E1:** SfM already converges to f≈1458 px @1920 (2915 @3840, HFOV 66.7°), reprojection 0.6 px. The community's 2170 px prior mistook the 83° diagonal FOV for horizontal. The 10 unregistered frames are post-landing floor shots with no overlap, not a focal problem | R3 §3, E1 §2 |
| 20 | **Cheshire** (HIP AliceVision), on our COLMAP poses | classic GPU | **yes, prebuilt** (bundles its own HIP 7.2; MPL-2.0) | **measured: dense 690 s + our SfM 130 s ≈ 14 min**; depth maps 2.9 s/view | **coverage 0.896, PSNR/SSIM 21.07/0.766** (beats OpenMVS res0 on both); Chamfer 3.81 / F@2cm 0.57 (reference bias); 3 × 4K atlases, needs repacking | E4 done; **idle re-time: 352 s end to end, §5.1**. Warning: its GPU meshing pass reset the GPU for other jobs (use `CHESHIRE_GPU_VOTE=0`) | R3 §4, E4 |
| 21 | Spirula Studio (Vulkan; GPL-3.0) | all-in-one | yes, single binary | measured: 2307 s (training 2125 s); its SfM 164/175 in 57 s | 18k checkpoint: coverage 0.95 but PSNR 19.57; the full 30k run meshed badly (PSNR 16.06, 4217 pieces) | E4 done: **rejected for the mesh**; its fast SfM is notable | R3 §4, E4 |
| 22 | Brush 0.3.0 (wgpu 3DGS; Apache-2.0) | splat | yes, single binary | measured: 1037 s for 30k steps | 98k splats, own-eval PSNR 35.4 (not comparable); no mesh | E4 done: splat demo option only | R2, R3, E4 |
| 23 | Generative fill of unseen regions (Marble + gsplat fine-tune, Orbis) | generative | cloud | n/a | held-out PSNR 9.96 → 33.09 dB, but scale ~25% off | preview/backdrop only | R3 §2 |
| 25 | KTX2 atlas compression (gltfpack 1.2 `-tc`, BasisU) of the published mesh.glb | packaging | yes, prebuilt (MIT) | **measured: 29 s for 4 variants, including decode and PSNR** | **GPU texture 42.7 → 5.3 MiB (ETC1S → ETC2) or 10.7 MiB (ASTC 4×4), with mips; held-out render PSNR/SSIM 21.51/0.807 → 21.42/0.804 (ETC1S) or 21.50/0.806 (UASTC)**; GLB 5.83 → 6.08 MB (ETC1S), 10.69 MB (UASTC), 5.01 MB with gltfpack's quantization | **E7: measured, not adopted**: needs a KHR_texture_basisu loader on the Quest | E7 |

## 4. Method notes

The detailed notes live in the per-agent files: `bench/r1-academic.md` (papers),
`bench/r2-oss-industry.md` (open-source and products), `bench/r3-community.md` (practitioners),
`bench/r4-online-tools.md` (online trials), `bench/e1-experiments.md` (harness and sweeps),
`bench/e2-rocm-builds.md` (toolchain), `bench/e3-depth-fusion.md` (mono-depth densify),
`bench/e4-amd-stacks.md` (Cheshire, Spirula, Brush), `bench/e5-hybrid.md` (hybrid densify) and
`bench/e6-splat-mesh.md` (2DGS).

Cross-cutting findings so far:
- **Where the densify time goes** (kitchen log, R2 §2.1): photometric PatchMatch takes 18m54s,
  each of the 2 geometric passes about 3.5 min, and fusion 1m06s. That produces 2.41M points,
  which we then decimate to 200k tris. Most of the 27 min buys detail the output never keeps.
- **hfbox has no ROCm SDK** (no `/opt/rocm` or hipcc; torch bundles only the runtime). Every
  HIP build waits on E2: gsplat, 2DGS, COLMAP-HIP, OpenSplat.
- **pycolmap 4.2 wheel:** ALIKED/LightGlue/LoMa are listed but abort with "requires ONNX
  support". Use hloc in the torch env instead.
- **Quest budget is looser than we assumed** (R2 §5). Meta's guidance is 1.3–1.8M tris per
  frame. The real cost is the JPEG atlas, which decodes to about 85 MiB. KTX2 (UASTC/ETC1S) via
  gltfpack cuts it 4–8×, which would allow about 400–500k tris or two 4K atlases.
- **Industry:**
  - Meta Hyperscape captures images plus headset poses, then spends 2–8 h in the cloud making
    splats that are streamed, with no export.
  - Luma dropped 3D capture.
  - Scaniverse (now Niantic Spatial "Capture") makes on-device splats and meshes.
  - Metashape 2.3 is the one commercial GPU photogrammetry tool that runs on AMD/Linux.
- **Community consensus** (R3):
  - Frame curation and fixed intrinsics beat more compute.
  - On blank walls, capture changes plus depth/normal priors help; 4K over 1080p, more frames,
    and more training steps don't.
  - Coverage is capped by the camera path: one team measured 0% coverage beyond about 41° off
    their forward walk.
  - No hackathon project shipped a metric textured mesh. The pattern is poses from a
    feed-forward model or COLMAP, then gsplat/Brush, viewed in WebXR.
- **Drone capture guidance for building orbits** (Teleport's guide, via R3): three orbit loops
  at 10–45° down, a 1/500 s or faster shutter, ~70% overlap, interval photos rather than video,
  and SRT GPS written into EXIF as a position prior.
- **Coverage gap, seen directly** (R4 renders, `bench/r4/summary_novel_views.jpg`):
  - Our OpenMVS mesh leaves holes on the cabinet faces, the fridge side and the dishwasher.
  - Feed-forward models (WorldMirror, VGGT-Ω) cover them, but with blotchy colour and no mesh.
  - The winning combination should be feed-forward or multi-view-consistent depth for geometry,
    with OpenMVS TextureMesh baking the real photos onto it. E3 is building this.
- **Scale: the caption-altitude scale is about 31% too small** (E1 §3b; E3).
  - Standard-size objects in an eval frame (dishwasher, range, cabinet doors) give ratios of
    1.43–1.44× the altitude scale, i.e. about 0.27 m/unit.
  - MoGe-2 depth gives 0.237–0.255 m/unit. DA-V2 metric gives 0.273.
  - The earlier "within ~10%" counter check in `p1-quality-runs.md` rested on two guesses (which
    mesh peak is the counter, and a floor takeoff), so it was wrong.
  - Possible cause (inferred): indoors, the Mini 4K's H may come from the downward sensor, so it
    reads the distance to counters rather than to the takeoff point, which compresses the range.
  - **A tape measurement settles it.** Until then, keep the altitude scale as one cue with a
    disagreement warning.
- **The QA renderer understated complete meshes** until `34cc339` (frustum culling and depth
  fill). All scores before that commit need re-scoring; E1 has a rescore script (`06e8059`).
- **Corrections to earlier docs** (R1): Pi3X weights are CC-BY-NC (Pi3 is BSD-2); COLMAP 4.x
  can texture meshes; the official AMD gsplat wheel is MI300X-only.

## 5. Findings and recommendation

### 5.1 Idle-box re-time (final numbers)

E1 re-timed the finalists on 2026-09-25, 11:20–12:31 UTC. Each job ran alone on hfbox with
`taskset -c 0-15`, 16 threads and a free GPU; the per-job box state is in
`/workspace/bench/logs/idle.txt`. Runs marked `*` reused the cached SfM and are charged the SfM
stage measured on the fresh run: 153 s. That includes 87 s of full-res frame extraction done as
one ffmpeg seek per frame, which a single pass should cut to about 15 s.

| Run | SfM s | Densify s | Mesh + texture s | Total s | View cov | Novel back / right | PSNR / SSIM | F@2cm | Tris / atlas |
|---|---|---|---|---|---|---|---|---|---|
| Preview (VGGT 48 frames) | 17 (frames) | 22 | 28 | **74** | 0.818 | – | 18.29 / 0.645 | 0.357 | 37k / 4K |
| OpenMVS res0 (old default) | 153* | 1617 | 89 | 1885 | 0.709 | 0.566 / 0.508 | 20.97 / 0.751 | **0.903** | 200k / 4K |
| OpenMVS res1 nv3 (new default), fresh | 153 | 350 | 80 | 609 | 0.777 | 0.658 / 0.549 | 21.08 / 0.767 | 0.762 | 200k / 4K |
| **Hybrid, MoGe-2 fill** | 153* | 345 | 77 | **604** | **0.996** | **0.911 / 0.791** | **21.88 / 0.806** | 0.609 | 200k / 4K |
| Hybrid, WorldMirror fill | 153* | 356 | 77 | 613 | 0.987 | 0.903 / 0.773 | 21.85 / 0.803 | 0.658 | 200k / 4K |
| **Depthfusion MoGe-2** | 153* | 36 | 52 | **269** | 0.984 | 0.897 / 0.777 | 21.45 / 0.786 | 0.624 | 200k / 4K |
| Depthfusion WorldMirror | 153* | 47 | 62 | 290 | 0.963 | 0.860 / 0.727 | 21.49 / 0.782 | 0.701 | 200k / 4K |
| Cheshire on our poses | 153* | 104 | 94 | 352 | 0.897 | 0.733 / 0.664 | 21.07 / 0.766 | 0.566 | 184k / 8K (18 MB) |

Totals also include 25–28 s of QA, thumbnails and startup (1 s for Cheshire), which have no
column of their own.

How to read the table:
- **F@2cm favours OpenMVS.** The reference is the res0 OpenMVS cloud, so any surface a method
  adds outside it counts as error. The hybrid covers about 10 m² against the reference's 5 m².
  On the surface the reference does have, the hybrid matches its own OpenMVS cloud: completeness
  0.76 cm at res1 and 0.58 cm at res0, against 0.57 cm for the baseline (E5).
- **Noise:** re-running res0 on the same SfM scored F@2cm 0.903 against the reference's ~0.93,
  and fresh versus cached SfM moved it by 0.06. Treat differences under ~0.05 F@2cm or ~0.3 dB
  as noise.
- **Detail (lap-corr):** OpenMVS 0.54–0.60, hybrid 0.53–0.56, depthfusion WorldMirror 0.50,
  depthfusion MoGe-2 / Cheshire / 2DGS 0.43–0.45.

### 5.2 What we learned

1. **Coverage was a densify problem, not a capture problem.** PatchMatch leaves the blank
   cabinet faces, fridge side, dishwasher and ceiling empty. Every monocular or feed-forward
   depth source fills them. Filling only where MVS is empty (the hybrid union) keeps OpenMVS
   detail and gets coverage 0.996 at no extra wall time, because depth inference runs on the GPU
   while DensifyPointCloud runs on the CPU.
2. **Most of the old 27-minute densify bought detail the 200k-triangle output throws away.**
   Moving from res0/nv5 to res1/nv3 cut densify from 1617 s to about 350 s. PSNR and SSIM went up
   slightly, and F@2cm fell from 0.90 to 0.76.
3. **Feed-forward models are fast depth sources, not finished reconstructions.**
   - VGGT-Ω and WorldMirror online had the best raw coverage but blotchy colour, no mesh and no
     scale.
   - Used as depth under COLMAP poses with OpenMVS TextureMesh, they give the 269–290 s
     depthfusion rows.
4. **Splat-to-mesh did not pay off at our scale.**
   - 2DGS with MoGe-2 priors about ties depthfusion on geometry at 2.5–4× the time.
   - Spirula's mesh broke at 30k steps.
   - Splats themselves are far better image models (30.6 dB held-out) but can't be exported to
     the Quest as a mesh.
5. **GPU MVS on AMD is not there yet.**
   - The COLMAP HIP port runs at 78 s/view.
   - OpenMVS develop SGM is 4.3× faster, but it drops coverage to about 0.60.
   - Cheshire (HIP AliceVision) is the only fast AMD GPU stack: 352 s. It still loses to
     depthfusion MoGe-2 on every metric, and its atlas exceeds the Quest budget.
6. **Learned features (ALIKED+LightGlue) did not help** this clip: slower on CPU ONNX and
   1.17 px vs 0.62 px reprojection. SIFT via the COLMAP 4.2 global mapper registers 175/175 in
   51 s.
7. **Scale is still the weakest cue.**
   - The caption altitude is about 31% small indoors. MoGe-2 (0.2545 m/unit) and the
     standard-object check (~0.27) agree with each other.
   - The preview's earlier 45% gap was a bug (the preview never ran the caption fit). With that
     fixed, preview and full agree within 1%.
   - A tape measurement is still needed to pick the right absolute cue.

### 5.3 Recommended pipeline

| Stage | Method | Time on hfbox, 16 cores | Result |
|---|---|---|---|
| Preview | VGGT 48 frames, TSDF, TextureMesh (unchanged) | **73 s** measured | coverage 0.82, 37k tris, rough |
| Full (default since `568d6f2`) | fps 2, COLMAP 4.2 global SfM, then **`--densify hybrid`**: OpenMVS res1 nv3 plus MoGe-2 fill, ReconstructMesh, TextureMesh | **measured fresh end to end: 626 s including the preview** (frames 12, SfM 51, undistort 2, densify 366, mesh 28, texture 50, QA 18) | coverage 0.996, PSNR 21.9, OpenMVS detail where MVS has data, 200k tris, 4K atlas, 5.9 MB |
| Full, fast option | `--densify depthfusion` with MoGe-2 | ~200 s (269 s measured with the old extraction) | coverage 0.984, PSNR 21.5, softer detail (lap 0.43) |
| Full, max detail | `--densify hybrid` at res0 | ~1900 s | completeness 0.58 cm, PSNR 22.0, coverage 0.98 |

- **Licences:** MoGe-2 is MIT. WorldMirror fill scores the same but its Tencent licence excludes
  the EU, UK and South Korea, so it is optional only. Cheshire (MPL-2.0) is the fallback for a
  box with no torch GPU env.
- **Against the first real run:** 2039 s at coverage 0.70 becomes ~600 s at coverage 0.996, with
  +1 dB PSNR and +0.06 SSIM.

### 5.4 Fresh end-to-end check of the new defaults

E1 ran `--stages preview,full --crop none` from the video on the idle box after `568d6f2`:
- **Timing:** the preview was ready at 73 s and the whole run took 626 s. One-pass frame
  extraction (`64e082a`) took 12 s instead of 87 s, with byte-identical frames.
- **A texture bug surfaced:** the published mesh scored only 18.96 dB because of black texture
  blotches.
  - Cause: trimesh's `Scene.to_geometry()` atlas packer in `mesh.process_mesh` pushed 7,186
    faces' UVs past 1.0 for this pair of atlas sizes.
  - This could hit any method, depending on the atlas sizes TextureMesh emits.
  - Fixed in `f3b2b4e` with our own grid packer.
- **After the fix** the same mesh scores **21.51 / 0.807**, coverage 0.996, novel-view
  0.915/0.789, lap 0.54, 200k tris, a 4096×2047 atlas and 5.8 MB. An independent re-run on the
  same SfM scored 21.49 / 0.805.
- **Versus the first real run:** 2039 s at coverage 0.70 and 20.8 dB became 626 s at coverage
  0.996 and 21.5 dB.

### 5.6 Last experiments (E7, `bench/e7-final.md`)

- **Fill distance 1 cm vs 2 cm:** a tie. This was a paired run on one dense cloud: 21.88/0.809 vs
  21.87/0.806, with the same coverage, lap-corr and 451 s wall time. E5's +0.13 dB was with
  WorldMirror fill. The default stays at 2 cm.
- **Sharpest-of-2 at fps 4 with hybrid, fresh:** 644 s vs 626 s, 21.70/0.802 vs 21.51/0.807,
  F@2cm 0.611 vs 0.596, lap-corr 0.50 vs 0.54, 165/175 frames registered. All within noise, so
  `--fps 2` stays.
- **KTX2 via gltfpack:**
  - ETC1S cuts atlas GPU memory from 42.7 MiB to 5.3 MiB (ETC2) or 10.7 MiB (ASTC) for
    −0.09 dB held-out PSNR. UASTC costs −0.01 dB but makes the GLB 10.7 MB.
  - The GLB does not shrink, because the JPEG atlas is only 0.73 MB.
  - It is not in the pipeline. It needs a `KHR_texture_basisu` loader on the Quest.

### 5.5 Next steps (not done this session)

- **KTX2 atlas compression via gltfpack** (measured by E7, §5.6; wiring it in and checking the Quest loader remain):
  - 4–8× less texture memory on the Quest.
  - That would allow a second 4K atlas or about 400k tris.
- **Tape-measure the kitchen**, then rank the scale cues (altitude, MoGe-2, object sizes) against
  it.
- ~~Sharpest-of-2 frame selection~~: repeated by E7 (§5.6), within noise, not adopted.
- **Other footage:** the hybrid has only been tested on this kitchen. It needs an outdoor orbit
  clip before we trust it there.
- **VGGT-Ω** (gated weights, NC licence) for a better preview, if the user requests access.
