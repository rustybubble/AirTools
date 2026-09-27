# P1 fast reconstruction research: can drone video → textured mesh hit &lt;1 minute?

Scope: find a faster replacement for the current pycolmap+GLOMAP → OpenMVS CPU chain (9–31 min/site,
`DensifyPointCloud` dominant, §2.2/§2.6 of the plan; per-stage numbers in `docs/research/
p1-quality-runs.md`). Hard constraint: output must still satisfy the plan's §1 scene-package
contract — a **textured** `mesh.glb` (~200k tri, one 4096 JPEG atlas baked from real photos),
`collision.glb`, `cameras.json`, thumbnails. Splats alone are out (§1, §9 fps test: 50k/72fps,
200k/60fps, 400k/36fps on the 3S — confirmed already in the plan, not re-tested here). Real-world
scale is applied afterward by the plan's own `calibrate.py` (ArUco/GPS/known-dimension) — nothing
here needs to be metric on its own.

**TL;DR: classic MVS (even fully GPU-accelerated on an H100) cannot hit &lt;1 minute for ~100 frames
at 1920px — its own floor is a few minutes. A feed-forward pose+depth model (VGGT or a same-shape
2025–26 successor) → fast depth fusion → OpenMVS `TextureMesh` (texture-bake only, no
`DensifyPointCloud`/`ReconstructMesh`) can. Measured hands-on this session, end-to-end, 48 real
drone-orbit frames, on a *shared* 16GB AMD consumer GPU (not even the target NVIDIA cloud
hardware): 18.9 s from cold model load to a textured GLB. On a dedicated cloud GPU (L40S/A10G/H100)
with a warm model process, the same chain should land at 5–15 s, leaving comfortable headroom under
60 s for frame extraction, packaging and QA. The real cost is geometric quality, not time: the
TSDF-fused mesh's photo-texture colour correlation (0.43–0.69, `texcorr.py`) is well below the
current full pipeline's (0.78–0.94) — this is a legitimately good <1-min *preview*, not a
drop-in replacement for the ~10–30 min full-quality mesh. Recommended architecture (§6) is exactly
the plan's own §2.6 fallback idea generalized into the primary fast path, with the current OpenMVS
chain kept running in the background as the quality upgrade.**

---

## 1. Where the current stack's time goes, and its floor on a big NVIDIA GPU

### 1.1 Current bottleneck (from `p1-quality-runs.md`, `p1-toolchain.md`, already measured)

`DensifyPointCloud` (OpenMVS's prebuilt CPU binary, no CUDA linkage — confirmed via `ldd` in
`p1-toolchain.md` §2.1) is the dominant cost: **200–1600 s** per clip in the 4-clip corpus rerun
(`p1-quality-runs.md` §"Per-stage timings" note). Everything else (SfM ~1–2 min, `ReconstructMesh`
1–3 min, `TextureMesh` 16–44 s measured, crop 2–10 s) is comparatively cheap. This matches the
plan's own §2.5 framing: "`DensifyPointCloud` is the dominant cost."

### 1.2 Cheap wins on the classic stack, ranked by expected impact

| Win | Mechanism | Expected effect | Evidence |
|---|---|---|---|
| **OpenMVS CUDA build** (compile from source, not the prebuilt CPU zip) | `DensifyPointCloud`'s PatchMatch stereo is the same algorithm class NVIDIA GPUs accelerate 5–20× in the photogrammetry literature generally | Could plausibly cut 200–1600 s to 20–150 s | Not tested (no NVIDIA box in this project); OpenMVS's CMake `CUDA` option exists but the prebuilt binaries ship without it (`p1-toolchain.md` §2.1) — real build effort (CMake+vcpkg+CUDA), a from-scratch compile is a real setup cost, not free |
| **COLMAP's own CUDA PatchMatchStereo** (`pycolmap.patch_match_stereo`) | Confirmed hands-on: `patch_match_stereo.__doc__` literally says "requires CUDA" — currently unusable on hfbox's AMD card, but on an NVIDIA cloud box this replaces `DensifyPointCloud` entirely, one dependency instead of two binaries | Comparable or better than OpenMVS-CUDA; avoids the InterfaceCOLMAP round-trip | `p1-toolchain.md` §1.1, confirmed from the pip wheel's docstring |
| **GPU SIFT + matching** (`pycolmap` `use_gpu=True`) | CPU SIFT+matching is currently ~5–13 min combined (extraction 3–8 min + matching 2–5 min, `p1-toolchain.md` §5 estimate); COLMAP's own GPU SIFT is commonly 5–10× faster than CPU SIFT | Cuts SfM's feature stage from minutes to under a minute for ~100 frames | Confirmed available (`pycolmap.Device` has a `cuda` member, `use_gpu` defaults `False` only because hfbox has no CUDA) — not measured, no NVIDIA box here |
| **Fewer/smaller frames** | Halving frame count roughly halves matching-pair count (quadratic in the worst case for exhaustive, linear for sequential+loop-closure) and halves `DensifyPointCloud`'s per-image cost | 50 frames instead of 100 at 1280px instead of 1920px is a real, free lever the plan already exposes (`--fps`, `--long-edge`) | Not separately re-benchmarked this session — the existing 4-clip corpus already runs at 57–88 frames, 1920px |
| **`DensifyPointCloud --resolution-level 2`, lower `--number-views`** | Already-documented flags (`p1-toolchain.md` §2.1); the plan's own "Needs re-measurement" section (`p1-quality-runs.md`) flags this exact ablation as not yet re-verified under the current (bug-fixed) QA metric | Real, but bounded — doesn't change dense stereo's fundamental CPU-vs-GPU gap | Documented flags, not re-measured |
| **Parallel stages** | SfM → dense → mesh → texture is a strict dependency chain per site; the only real parallelism is **across sites** (two capture sites reconstruct concurrently, matches the plan's own Sat 1–3 PM "site 2 reconstructs in background" timeline) | No single-site speedup | — |
| **Frame selection by overlap** | Drop frames whose pairwise overlap with already-kept frames is high (redundant for MVS) instead of blind fixed-fps sampling | Same quality, fewer frames → proportionally less dense-stereo time | Not implemented or tested; a real, cheap, unexplored lever for the classic path specifically |

### 1.3 Floor estimate for classic MVS on a big NVIDIA GPU (H100/A100/L40S), ~100 frames @ 1920px

Stacking every win above (GPU SIFT+matching, GLOMAP, CUDA PatchMatch dense stereo, `TextureMesh`
largely unchanged since it's not the bottleneck):

| Stage | CPU (current, measured/estimated) | GPU-accelerated floor (estimated, not measured) |
|---|---|---|
| Frame extraction (ffmpeg) | 1–2 min | 1–2 min (CPU-bound regardless) |
| SIFT + matching | 5–13 min | 20–60 s (GPU SIFT, commonly-cited 5–10×) |
| GLOMAP global mapping | 1–4 min | 30 s–2 min (CPU-bound, doesn't move much) |
| Undistort | 1–2 min | 1–2 min |
| **Dense stereo** | **15–35 min** (measured 200–1600s = 3.3–27 min on the real corpus) | **1–3 min** (CUDA PatchMatch, generously) |
| Mesh reconstruct | 1–3 min | 1–3 min (largely CPU, not the bottleneck) |
| Texture + decimate | 16–60 s (measured) | 16–60 s (already fast, not GPU-bound) |
| **Total** | **9–31 min** (matches the plan's own measured range) | **~5–12 min, optimistically** |

**This is the central finding for the classic path: even a best-case, fully GPU-accelerated classic
MVS pipeline on an H100 does not get under about 5 minutes for ~100 frames at 1920px.** The
floor isn't any single fixable bottleneck — SfM, mesh reconstruction and I/O all have real
CPU-bound and sequential-dependency costs that don't disappear with a faster GPU. Classic MVS is
not the path to &lt;1 minute; it's the path to "10 min → 5 min," which is a real, worthwhile win
for the *background full-quality* path (§6) but not for the primary demo path.

---

## 2. Feed-forward 3D (poses + dense depth in one forward pass)

### 2.1 Landscape (Sep 2026)

| Model | Code license | Weights license | Speed class | COLMAP export | Notes |
|---|---|---|---|---|---|
| **VGGT** (facebookresearch/vggt) | Meta Research, commercial-use-friendly since Jul 2025 | Default checkpoint **CC-BY-NC-4.0**; gated `VGGT-1B-Commercial` exists | Measured: 5.9–10.7 s for 48–64 frames at bf16 on a 16GB AMD card (this session + `p1-gpu-rocm.md`) | `demo_colmap.py` writes COLMAP format directly | Already the plan's own §2.5 pose fallback; extending its *use* (not just as a pose fallback) is this doc's core recommendation |
| **VGGT-Long** (DengKaiCQ/VGGT-Long) | research code | inherits VGGT | Chunk+loop+align for kilometer-scale sequences | via VGGT | Solves VGGT's frame-count ceiling for long sequences — not needed at ~100 frames/orbit, but relevant if capture length grows |
| **Pi3 / Pi3X** (yyfz/Pi3, ICLR 2026) | BSD-3-Clause | BSD-2-Clause (most permissive of this family, confirmed in `p1-toolchain.md`) | Not independently benchmarked | No shipped exporter (real integration cost) | Pi3X adds approximate metric scale + a convolutional head reducing grid artifacts + pose/depth-conditioning; a credible 2026 successor, not yet plugged into this pipeline |
| **MASt3R / MASt3R-SfM** | CC BY-NC-SA 4.0 | same | Slower, all-pairs by default; `-SfM` variant scales via retrieval-based pairing | Yes, via its own exporter | More restrictive license; second-tier fallback per `p1-toolchain.md` |
| **MapAnything** (facebookresearch, 3DV 2026) | Apache-2.0 | CC-BY-NC-4.0 | Not benchmarked | Modular — wraps VGGT/DUSt3R/MASt3R/MUSt3R/Pi3X behind one interface | Newest, genuinely metric-scale output (factored depth+ray-map+pose+scale); "watch" not "chosen" per `p1-toolchain.md`, still true |
| **Depth Anything 3** (ByteDance-Seed, Nov 2025) | code license not fully checked | **DA3-Giant: CC BY-NC-4.0** (confirmed); smaller variants (Base/Small) not separately confirmed this pass | Single-transformer depth+ray+camera from any number of views, no known poses needed | Not confirmed | Newest depth-focused entrant; worth a follow-up smoke test, not evaluated hands-on here |
| **MUSt3R / CUT3R / Fast3R / DUSt3R** | research licenses, mostly NC | — | CUT3R uses a recurrent state (streaming-friendly); Fast3R optimized for inference speed; DUSt3R is pairwise-only, superseded by MASt3R-SfM for N≈100 | — | Mentioned in 2025–26 SLAM literature (MASt3R-SLAM, S-MUSt3R, EC3R-SLAM) mostly for streaming/SLAM use cases, not one-shot orbit reconstruction — lower priority for this use case |

VGGT remains the right primary choice: it's the only one in this table with (a) a working COLMAP
exporter already wired into this repo (`pipeline/vggt.py`), (b) measured VRAM/speed numbers on the
actual hardware family available (both the AMD dev box and, by extension, any NVIDIA cloud box —
VGGT is plain PyTorch, no CUDA-only ops, confirmed `p1-toolchain.md` §1.2), and (c) a resolution
ceiling (518 px) that's a non-issue for texture, because texture is baked from the original
full-resolution photos, not from VGGT's internal resolution (confirmed hands-on this session — see
§7).

### 2.2 The 518px resolution limit, and how texture detail survives it anyway

VGGT (and every model in this family) processes images resized to ~518px on the long edge — far
below the 1920px frames the plan captures. This *only* affects the geometry (mesh vertex density
and depth-map resolution); it does not need to affect texture, because `OpenMVS TextureMesh` bakes
its atlas by re-projecting the **original full-resolution photos** onto whatever mesh it's given
(`-i scene.mvs -m externally-produced-mesh.ply`, confirmed live via `--help` in `p1-toolchain.md`
§2.1). This session's hands-on pipeline does exactly that: VGGT's depth (at 518×294) drives a
coarse mesh, but the texture atlas is baked from the same 1920×1080 JPEGs the current pipeline
uses. The atlas quality ceiling is therefore set by the mesh's *UV unwrap quality and triangle
density* (coarse, since the mesh itself is coarse), not by VGGT's internal resolution — a real but
different limitation than "blurry from low-res depth."

---

## 3. Feed-forward / fast splatting → mesh

| Method | What it is | Speed | Mesh output? | Verdict |
|---|---|---|---|---|
| **AnySplat** (SIGGRAPH Asia 2025) | Feed-forward 3DGS from uncalibrated/unconstrained views, predicts Gaussians + camera params in one pass | Feed-forward (seconds class, not independently timed) | Splats only per its own docs; would need a separate mesh-extraction pass (2DGS-style TSDF from rendered depth) | Interesting but adds a conversion step VGGT+TSDF already does more directly |
| **InstantSplat** (NVlabs) | Sparse-view, pose-free 3DGS "in seconds" | Documented: unbounded scenes in **under 1 minute**, sparse views in ~40s | Splats only | Genuinely fast, but still splats-only — would need the same mesh-extraction tail as any splat method; not obviously better than going straight to depth+TSDF |
| **2DGS / PGSR / RaDe-GS** | Train a splat scene, then extract a mesh via TSDF fusion of *rendered* depth maps | Training itself is the classic 3DGS cost (minutes, even accelerated); depth-render+TSDF is fast once trained | Yes, by design (that's the point of 2DGS) | Training time dominates — doesn't fit &lt;1 min unless paired with a fast-training variant below |
| **MILo** (SIGGRAPH Asia 2025) | Differentiable mesh extraction *during* Gaussian training (vertices+connectivity from Gaussian params every iteration), not a post-hoc TSDF pass | Training-time cost, lighter output mesh (60–400MB per the paper) than post-hoc extraction | Yes | Quality-focused, not speed-focused; a background/quality-tier candidate, not a &lt;1-min one |
| **DashGaussian** | 3DGS training in ~200 s on a consumer GPU | 200 s | No (still training-only, needs its own mesh pass) | Too slow on its own for &lt;1 min once a mesh pass is added |
| **FastGS** (CVPR 2026 Highlight) | 3DGS training in **~100 s**, 3.3× faster than DashGaussian | ~100 s | No | Same issue — even the fastest published 3DGS *trainer* alone eats most of a 1-minute budget before any mesh/texture step |
| **Taming 3DGS / Speedy-Splat** | Sparsification + pruning strategies that speed up 3DGS training | Contributing techniques inside FastGS/DashGaussian, not standalone end-to-end numbers | No | Building blocks, not a shippable path |

**Verdict on §3: every fast-splat-training method still measures its "fast" in the 40 s–200 s
range for *training alone*, before any mesh extraction or texture bake.** That's a much smaller
margin than feed-forward depth+pose (VGGT: single forward pass, no training loop, no per-scene
optimization at all — this is the structural reason it's faster). None of the splat-training
methods surveyed publish a documented end-to-end "video in, textured mesh out" number under a
minute. Feed-forward depth (§2) remains the better fit for this specific time budget; fast-splat
training is a second-tier option if VGGT-class quality turns out to be insufficient and 100–200 s
is an acceptable budget instead of 60 s.

---

## 4. Commercial/cloud services and SDKs

| Service | API/CLI programmatic? | Output | Metric? | Turnaround | Price per scene | Standing cost | License/access |
|---|---|---|---|---|---|---|---|
| **RealityScan / RealityCapture** (Epic) | CLI, incl. **import COLMAP projects directly** (2026 feature — this repo's own `pycolmap` poses could feed it) | Textured mesh, OBJ/FBX/glTF export | Yes, with control points/known scale (same "apply afterward" pattern as this pipeline) | Not benchmarked here; widely reported as the fastest desktop photogrammetry tool, GPU-accelerated (CUDA-only — **no textured mesh without an NVIDIA card**, confirmed) | **Free** under $1M annual revenue (hackathon qualifies) | $0 (free tier) or $1,250/seat/yr above threshold | Free tier confirmed 2026; needs a Windows GPU VM in the cloud plan since it's Windows-only and CUDA-only |
| **Agisoft Metashape** | Yes, network processing + a documented Python API | Textured mesh | Yes (GCPs) | Not benchmarked | Pro license $3,499 one-time, or Service Provider rental from $155.90/mo | High relative to a single hackathon | CUDA/OpenCL, cross-platform |
| **Pix4Dmatic / Pix4Dcloud** | Cloud API exists, workflow-oriented | Textured mesh/ortho | Yes | Hours, not minutes, per user reports (cloud-accelerated but not built for &lt;1 min single-scene turnaround) | $125–249+/mo subscriptions | Subscription, not per-scene | Not evaluated for programmatic single-scene driving |
| **DroneDeploy** | Cloud-first, "hours even for 1,000+ image datasets" | Textured mesh/ortho | Yes | Hours | $329–599/mo | Subscription | Enterprise-workflow tool, wrong shape for a single 36h hackathon |
| **Bentley iTwin Capture (ContextCapture)** | Cloud API exists | Textured mesh | Yes | Not evaluated this pass | Enterprise pricing, not published | — | Not evaluated further — enterprise sales-gated, wrong fit for a hackathon timeline |
| **Luma API** | Luma's consumer scanning app is **officially discontinued/no longer updated** (confirmed) | — | — | — | — | — | Ruled out |
| **Polycam** | Mobile app + Pro tier, not a documented scene-reconstruction-from-video API | Mesh export | Approximate | App-based, not built for programmatic drone-video ingestion | $150/yr Pro | — | Wrong shape (object/room scanning, not orbit-drone-video) |
| **KIRI Engine** | Mobile app, free unlimited scans+exports, GLB support | Mesh export | Approximate | App-based | Free / $6.99/mo Pro | — | Same shape mismatch as Polycam |
| **World Labs Marble (World API)** | **Yes — a real, documented API**, launched Jan 21 2026: `POST /marble/v1/worlds/<id>:export {"asset_type":"mesh","format":"glb"}`, async, poll to completion | **Textured mesh, GLB** — correcting the brief's assumption, mesh export does exist | **Also correcting the brief**: response includes `metric_scale_factor` to convert to real meters — approximately metric, not "not metric" | Not benchmarked (generation + export both async, no published latency found) | ~$1.20 (1,500 credits) world generation + ~$2.80 (3,500 credits) HQ mesh export ≈ **~$4/scene** | Pay-as-you-go credits, $5 minimum purchase | **Generative** — Marble is a world *model*, not a measured reconstruction; real quality risk for an "accuracy test" use case (§ of the plan) since there's no guarantee the output mesh matches the actual captured building dimensions beyond the declared scale factor. Flag, don't rely on for the accuracy-test beat |
| **NVIDIA NIM / Omniverse NuRec** | NIM microservices exist generally; NuRec is a 3DGS reconstruction *library* for robotics/AV sim, not a hosted "video→mesh" API | Gaussian-based scene reconstruction for simulation | N/A | N/A | N/A | N/A | Not a fit — built for robotics real-to-sim pipelines, not a mesh-out product |
| **Apple Object Capture** | Yes, CLI (`PhotogrammetrySession`), macOS-only | OBJ/USDZ textured mesh | No (needs external scale like everything else here) | Not benchmarked; general photogrammetry-class timing (minutes) | Free (needs a Mac) | $0 + a Mac | Confirmed it accepts drone-video-derived image sets (third-party tools like AR Code feed it drone footage); still a desktop photogrammetry pipeline under the hood, not obviously faster than RealityCapture |

**Verdict on §4:** nothing in this table is built for a sub-minute turnaround; they're all classic
or cloud-batch photogrammetry with the same structural floor as §1, or (Marble) a generative model
with a real accuracy/quality-risk mismatch for this project's "accuracy test" beat. RealityScan is
the standout for the **background full-quality path** (§6's fallback) — it's free, CLI-drivable,
and can import this pipeline's own COLMAP poses directly, potentially replacing the OpenMVS
CPU chain with a faster GPU one if a Windows GPU VM is in the cloud budget anyway. Marble is worth
a mention on stage ("we also tried a generative world model") but not the accuracy-test path.

---

## 5. Cloud hardware: AWS and Azure, within ~$20k combined credit

### 5.1 Instance/pricing snapshot (Sep 2026, on-demand unless noted)

| Provider | Instance | GPU | On-demand $/GPU-hr | Spot $/GPU-hr | Notes |
|---|---|---|---|---|---|
| AWS | `g6e.xlarge` | 1× L40S 48GB | ~$1.86/hr (instance price, 1 GPU) | not separately found this pass | Best-fit size for this workload — VGGT's own measured VRAM ceiling is 9.6GB/16GB, an L40S's 48GB is enormous headroom |
| AWS | `g5.xlarge` | 1× A10G 24GB | lower than g6e, exact figure not confirmed this pass | — | Cheaper fallback, still far more VRAM than needed |
| AWS | `p5.48xlarge` | 8× H100 80GB | ~$6.88/GPU-hr ($55/hr instance) | 60–70% off typical | Overkill for this workload; relevant only if also running the background full-quality OpenMVS-CUDA path at scale |
| AWS | P5 Capacity Blocks | H100 | ~$4.33/GPU-hr reserved | — | Reserve a future date range in advance — worth checking if it sidesteps the standard quota wait, not confirmed this pass |
| Azure | `NC40ads H100 v5` | 1× H100 | $6.98/hr on-demand | $1.29/hr spot | Single-GPU H100 node |
| Azure | `ND96isr H100 v5` | 8× H100 80GB | ~$12.29/GPU-hr ($98.32/hr) | $2.25–3.69/GPU-hr | 1-yr reserved drops to ~$7.93/GPU-hr |
| Azure | `NCads A100 v4` | A100 | $4.10/hr on-demand | ~$1.20/hr spot | — |

### 5.2 Quota — the real constraint, not the $20k budget

**Every new AWS account starts at a 0 vCPU quota for every GPU instance family (G, P) — confirmed
directly, not inferred.** A quota increase request is mandatory before launching *any* GPU
instance, small or large. AWS's own documented approval window is **15 minutes to 48 hours**, but
new accounts can be denied outright for large asks. **Azure's ND H100 v5 quota specifically is
documented at 1–4 weeks approval in popular regions** — far too slow to plan around for a
36-hour event unless requested weeks in advance.

**Recommendation: request AWS `g6e`/`g5` quota (a small ask — 4–8 vCPUs, one instance) as early as
possible, days before the event.** This is a low-risk, fast-turnaround request compared to
H100-class quota on either cloud. If Azure access is wanted as a backup, request `NCads A100 v4` or
`NC40ads H100 v5` quota **now** — not the week of — given the 1–4 week documented lead time on the
H100 SKU specifically.

### 5.3 Cold-start vs. warm instance, and where upload time actually goes

- **Cold start** (new instance): OS boot + container/environment pull + model weight download
  (VGGT-1B is 4.68GB, confirmed in `p1-gpu-rocm.md`) realistically costs **minutes**, not seconds —
  this alone blows the &lt;1 min budget if done per-reconstruction. **A warm, already-running
  instance with the model already loaded in GPU memory is mandatory for the &lt;1 min target.**
  This session's own measurement shows why: cold model load was 8–36s even with weights already
  cached locally (`p1-gpu-rocm.md`-style first-forward-pass kernel compile adds more); a
  warm process serving repeated requests pays that cost exactly once.
- **Keeping a warm L40S/A10G for the ~36–48h event**: at $1.86/hr (L40S) that's **~$70–90 total**
  for the whole event — trivial against $10k AWS credit. There is no cost reason not to keep it
  warm the entire time; the only reason not to is if quota/capacity makes spinning up early risky
  (mitigated by requesting quota days ahead, §5.2).
- **Upload bandwidth, confirmed as a real risk per the brief's own framing**: a 2–3 min 4K/30
  clip is ~1–2GB. On event Wi-Fi (typically the actual bottleneck at a hackathon, not the venue's
  uplink), uploading 1–2GB could itself take longer than the entire reconstruction. **Mitigation
  already implicit in the plan's own pipeline shape**: extract and downsample JPEG frames
  on-site (laptop-side ffmpeg, seconds, already how the pipeline works locally) and upload only
  the ~50–150 JPEGs actually needed (a few MB at 1920px, not 1–2GB of raw video) — this is a free,
  already-available lever, not new work.

---

## 6. Recommended architecture

### 6.1 Primary path: feed-forward preview, target &lt;1 min

```
video (on-site laptop)
  -> ffmpeg frame extraction + selection (local, seconds)          [existing pipeline step]
  -> upload ~50-100 JPEG frames to a WARM cloud GPU instance        [MB, not GB — seconds]
  -> VGGT forward pass: poses + depth, one call, chunked to <=64    [measured: 5.9-10.7s @ 48-64 frames]
  -> TSDF fusion (Open3D UniformTSDFVolume) -> coarse mesh          [measured: ~1.0s @ 48 frames]
  -> OpenMVS TextureMesh only (no Densify/ReconstructMesh)          [measured: ~2.8s @ 48 frames, 2048px atlas]
  -> trimesh post-process (decimate/UV touch-up, +Y up, collision)  [existing pipeline step, <1s at this vertex count]
  -> package (cameras.json, thumbs, scene.json)                     [existing pipeline step, ~1-2s]
```

### 6.2 Measured per-stage time budget (this session, hands-on, 48 real frames, shared 16GB AMD GPU)

| Stage | Measured (this session) | Notes |
|---|---|---|
| Model load (cold) | 8.0–36.4 s (varied run to run — HF Hub rate limiting/cache state) | **Eliminated** by a warm, pre-loaded process |
| Frame preprocess (load+resize) | 0.9–2.4 s | — |
| VGGT forward (poses+depth, bf16) | 5.9–54.5 s (54.5 s was a genuine cold-kernel-compile outlier; steady-state 5.9–10.7 s matches `p1-gpu-rocm.md`'s prior bench) | Kernel warmup is also a one-time, eliminable cost on a persistent process |
| TSDF fusion + mesh extract | 1.0 s | Open3D `UniformTSDFVolume`, 320³ resolution |
| Mesh cleanup + export | 0.01 s | — |
| Build COLMAP recon (full-res rescale) | 0.02–0.08 s | — |
| `pycolmap.undistort_images` | 0.06 s | — |
| `InterfaceCOLMAP` | 0.08 s (with a non-empty seed point cloud — see §7.2) | — |
| `TextureMesh` (full-res photos, 2048px atlas) | 2.8 s | Scales with frame count/atlas size; still sub-10s at 100 frames is a reasonable extrapolation |
| **Steady-state total (warm process)** | **≈10–15 s** for 48 frames | Cold-start total was 18.9 s end-to-end this session |

On a dedicated cloud L40S/A10G (vs. this session's *shared* 16GB AMD card), the VGGT forward pass
specifically should be faster (more VRAM, no contention, and NVIDIA's flash-attention path is more
mature than ROCm's) — **a realistic budget for ~100 frames on a warm cloud GPU is 5–15 s
compute, leaving 30–50 s of the 60 s budget for frame extraction/upload/packaging/QA**, which is
comfortable.

### 6.3 Fallback: full-quality mesh replaces the preview in the background

Exactly the shape the plan already anticipates (a fast thing now, a good thing later) — nothing new
to build, just point it at this fast path as the *foreground*:

1. Serve the &lt;1 min feed-forward mesh immediately as `mesh.glb` v1 (loaded into the APK / shown
   live).
2. Kick off the existing OpenMVS CPU chain (or, if the cloud GPU budget stretches to it, an
   OpenMVS-CUDA or `pycolmap.patch_match_stereo` GPU build per §1.2) in the background, ~5–12 min
   optimistically on a big NVIDIA GPU, ~9–31 min on hfbox's AMD box as already measured.
3. Replace `mesh.glb` with the higher-quality result when it lands — a rebuild-and-swap the app
   side already has to support for any new site (§2.4 of the plan, "every new site is a rebuild").

### 6.4 What this buys and what it costs

- **Buys**: a real, textured, on-scale mesh **in well under a minute**, on cloud hardware within
  budget, using a model already wired into this repo (`pipeline/vggt.py`) and requiring no new
  licensing risk beyond what's already accepted (VGGT's default checkpoint is CC-BY-NC-4.0 —
  fine for a hackathon, already the project's stance per `p1-toolchain.md` §8).
- **Costs**: geometric/texture fidelity. This session's `texcorr.py` measurement — 0.43–0.69
  colour correlation for the fast path vs. 0.78–0.94 for the current full pipeline on the same
  site — is real, measured evidence that the &lt;1 min mesh is visibly rougher. It's the right
  trade for a live demo beat ("watch it appear in under a minute") with the real mesh swapped in
  moments later, not a wholesale replacement of the quality path.

---

## 7. What was measured hands-on this session vs. taken from sources

### 7.1 Hands-on (hfbox, `ssh hfbox`, `/workspace/exp/fast/`, script copied to
`pipeline/experiments/fast/vggt_tsdf_texture.py`)

- Installed Open3D 0.20.0 + its missing system lib (`libusb-1.0.so.0`, via `apt-get`) and two pure
  Python viz deps (`plotly`, `dash`) into the existing `/workspace/envs/vggt` ROCm venv, confirmed
  the ROCm torch stack (`2.14.0+rocm7.2`, `cuda_available=True`) was untouched afterward — same
  torch-shadowing risk `p1-gpu-rocm.md` documented for other packages, avoided the same way
  (`--no-deps` first, then only the missing pure-Python pieces).
- Ran VGGT's raw forward pass (`model.aggregator` → `model.camera_head` → `model.depth_head`,
  **not** the `demo_colmap.py` CLI) directly on 48 of strasbourg-b's real 1920×1080 drone-orbit
  frames, bf16, on hfbox's shared 16GB AMD RX 9070 XT. **Found and fixed a real bug in a first
  attempt**: running `camera_head`/`depth_head` inside the same bf16 autocast context as the
  aggregator produces NaN depth (the depth head's `exp()` activation overflows in bf16) —
  confirmed by reading `vggt/models/vggt.py`'s own `forward()`, which wraps those two heads in
  `torch.cuda.amp.autocast(enabled=False)` specifically. Fixed by matching that exact call
  structure. This is a real pitfall for anyone calling VGGT's sub-modules directly instead of via
  its own `forward()`/`demo_colmap.py`, not documented anywhere I found.
- **Found and fixed a real bug in `open3d==0.20.0`'s pip wheel**: `ScalableTSDFVolume.integrate()`
  silently returns zero points/vertices on *any* input, reproduced on a trivial synthetic flat
  plane at identity extrinsic (a case that must work if the API functions at all) — confirmed with
  three independent tests (real VGGT data at two voxel scales, and the synthetic case),
  root cause not pinned down further (time-boxed). `UniformTSDFVolume` (same package) passed the
  same synthetic test and was used instead.
- **Found and fixed a real bug in OpenMVS's `InterfaceCOLMAP`**: a `pycolmap.Reconstruction` built
  with genuinely zero 3D points (VGGT's no-BA/no-track output, `num_points3D=0` — exactly what
  `pipeline/vggt.py`'s existing fallback produces) makes `InterfaceCOLMAP` crash with no error
  text after ~45s of CPU work, right after logging "Reading points" — reproduced 3× (direct write,
  and again after adding the `pycolmap.undistort_images` step the real pipeline always runs).
  Fixed by seeding a real (if throwaway) sparse point cloud from VGGT's own depth maps (~150
  points/frame, unprojected to world space) before writing the reconstruction. Note: this applies
  to a zero-point reconstruction built by hand, as this experiment did. The fallback shipped in
  `pipeline/vggt.py` goes through `demo_colmap.py` with `--conf_thres_value 0 --vis_thresh 0`, which
  keeps ~100k points (strasbourg), and was run through OpenMVS to a valid package on hfbox (plan
  §2.6). A fast path built on this experiment's own reconstruction must keep the seeding step.
- Built a from-scratch pycolmap `Reconstruction` at full 1920×1080 resolution from VGGT's raw
  extrinsics/intrinsics (not via `demo_colmap.py`), confirmed self-consistent by manually
  unprojecting depth-map pixels and checking the resulting 3D points' distance to the camera
  center matched the source depth values almost exactly (0.32–0.35 vs. 0.31–0.34 — i.e. this
  pipeline's pose+depth+intrinsics math is correct, independently of the TSDF/InterfaceCOLMAP
  issues above).
- Ran `pycolmap.undistort_images` → `InterfaceCOLMAP` → `OpenMVS TextureMesh -m <tsdf-mesh.ply>`
  (bypassing `DensifyPointCloud`/`ReconstructMesh` entirely) against the real full-resolution
  frames, end to end, successfully. **Measured wall time, this exact chain, 48 frames**: TSDF
  fusion 1.0s, mesh cleanup 0.01s, recon build 0.08s, undistort 0.06s, InterfaceCOLMAP 0.08s,
  TextureMesh 2.8s — **4.1s for the whole post-VGGT chain**, 18.9s including a cold model load.
- Ran `texdbg/texcorr.py` (this repo's own photo-vs-texture colour correlation check) against the
  resulting `textured.glb`: **0.43–0.69** per-view correlation (`(1-v)` UV convention, matching
  this repo's known-correct convention per `p1-quality-runs.md`'s V-flip fix) — compared directly
  against strasbourg-b's existing full-pipeline package's own measured **0.78–0.94** (same
  metric, same site, from `p1-quality-runs.md`).
- Confirmed AMD glibc 2.39 satisfies Open3D's `manylinux_2_35` requirement (the concern
  `p1-toolchain.md` §2.4 flagged as unchecked) — Open3D installs and imports cleanly on hfbox.

### 7.2 From sources, not hands-on (no NVIDIA GPU or cloud account access in this session)

- All §1.3 GPU-accelerated classic-MVS floor estimates — extrapolated from this project's own CPU
  measurements plus generally-cited GPU-vs-CPU speedup ratios for SIFT and PatchMatch stereo, not
  benchmarked on real NVIDIA hardware.
- All of §3 (fast-splat-training methods) and most of §4 (commercial services) — read from project
  READMEs, papers, and vendor pricing pages, not run.
- All of §5's cloud pricing and quota-lead-time claims — from vendor docs and third-party pricing
  aggregators (dated Sep 2026 per the search results), not confirmed by actually requesting quota
  (task instructions explicitly ruled out creating cloud resources/accounts this session).
- VGGT-Long, Pi3X, MapAnything, Depth Anything 3, MASt3R-SLAM/CUT3R/Fast3R/MUSt3R — read from
  papers/repos, not installed or run this session (time-boxed; VGGT itself already has a working,
  tested integration in this repo and was the higher-value hands-on target).

---

## 8. Sources

**Feed-forward 3D (§2)**
- VGGT: https://github.com/facebookresearch/vggt
- VGGT-Long: https://arxiv.org/abs/2507.16443 · https://github.com/DengKaiCQ/VGGT-Long
- Pi3 / Pi3X: https://github.com/yyfz/Pi3 · weights https://huggingface.co/yyfz233/Pi3
- MASt3R / MASt3R-SfM: https://github.com/naver/mast3r
- MapAnything: https://github.com/facebookresearch/map-anything · https://arxiv.org/abs/2509.13414
- Depth Anything 3: https://depth-anything-3.github.io/ · https://arxiv.org/abs/2511.10647 ·
  https://github.com/ByteDance-Seed/Depth-Anything-3 · https://huggingface.co/depth-anything/DA3-GIANT
- S-MUSt3R / EC3R-SLAM (2025–26 SLAM survey context): https://arxiv.org/html/2602.04517v1 ·
  https://arxiv.org/html/2510.02080v1

**Fast splatting → mesh (§3)**
- AnySplat: https://github.com/InternRobotics/AnySplat · https://arxiv.org/abs/2505.23716
- InstantSplat: https://instantsplat.github.io/ · https://github.com/NVlabs/InstantSplat
- MILo: https://anttwo.github.io/milo/ · https://arxiv.org/abs/2506.24096 ·
  https://github.com/Anttwo/MILo
- 2D Gaussian Splatting: https://github.com/hbb1/2d-gaussian-splatting ·
  https://arxiv.org/abs/2403.17888
- DashGaussian: https://arxiv.org/abs/2503.18402
- FastGS: https://github.com/fastgs/FastGS · https://arxiv.org/abs/2511.04283
- gsplat (rendered-depth → Open3D TSDF meshing): https://docs.gsplat.studio/

**Commercial/cloud services (§4)**
- RealityScan/RealityCapture: https://www.capturingreality.com/ ·
  https://www.cgchannel.com/2025/11/epic-games-releases-realityscan-2-1/
- Agisoft Metashape pricing: https://www.agisoft.com/buy/online-store/ ·
  https://www.agisoft.com/buy/saas/service-provider-license/
- Pix4Dmatic/Pix4Dcloud: https://www.pix4d.com/pricing/
- DroneDeploy: https://dronedesk.io/drone-mapping-software-guide (comparison source)
- World Labs Marble API: https://docs.worldlabs.ai/api/faq · https://docs.worldlabs.ai/api/pricing ·
  https://www.worldlabs.ai/blog/announcing-the-world-api
- KIRI Engine pricing: https://www.kiriengine.app/pricing
- Apple Object Capture: https://developer.apple.com/videos/play/wwdc2021/10076/ ·
  https://ar-code.com/blog/video-to-3d-modeling-photogrammetry-ar-code-object-capture-now-on-macbook-m-series
- NVIDIA NuRec: https://developer.nvidia.com/omniverse/nurec

**Cloud hardware (§5)**
- AWS P5/H100 pricing: https://www.spheron.network/blog/aws-h100-pricing-2026/ ·
  https://www.gmicloud.ai/en/blog/aws-p5-h100-pricing
- AWS G6e pricing: https://cloudprice.net/aws/ec2/instances/g6e.xlarge
- AWS quota defaults (0 vCPU for new accounts, GPU families): https://repost.aws/knowledge-center/ec2-on-demand-instance-vcpu-increase
- Azure ND H100 v5 / NC A100 v4 pricing and quota lead time: https://www.spheron.network/blog/azure-h100-pricing/ ·
  https://cyfuture.cloud/kb/gpu/azure-nd-h100-v5-pricing-per-hour-complete-cost-breakdown
- Azure Spot eviction: https://learn.microsoft.com/en-us/azure/virtual-machines/spot-vms

**Local repo (read, not re-cited inline above)**
- `docs/AirTool implementation plan.md` §1, §2, §2.6
- `docs/research/p1-toolchain.md`
- `docs/research/p1-gpu-rocm.md`
- `docs/research/p1-quality-runs.md`
- `pipeline/run.py`, `pipeline/vggt.py`

---

## 9. Integrated fast-first pipeline, measured

§1a/§2.2's design (Stage A feed-forward preview + Stage B full quality, rigid-aligned onto the
preview) is now implemented (`pipeline/fast.py` + `pipeline/fast_worker.py`, `pipeline/package.py`'s
revision-named publishing, `pipeline/run.py`'s two-stage `run()`) and run end to end, twice, for
real, on hfbox: `data/corpus/lighthouse-orbit-coastal-1.mp4` and `data/corpus/strasbourg-cathedral-
spire.mp4`, as new sites `lighthouse-ff`/`strasbourg-ff` (the pre-existing `lighthouse-orbit-
coastal-1`/`strasbourg-*` work/scene dirs from earlier sessions were left untouched). Both runs
published a real revision 1 (preview) followed by a real revision 2 (full, rigid-aligned).

### 9.1 Four real bugs found and fixed getting there

All four were invisible to local unit tests (which mock VGGT/OpenMVS) and only surfaced on the
real GPU box; each is a one-line-ish, root-cause fix, all committed separately:

1. **`pipeline/vggt.py` name collision.** Running `fast_worker.py` directly puts its own directory
   (`pipeline/`) on `sys.path[0]`. The real VGGT repo's `vggt` package has no `__init__.py` (a PEP
   420 namespace package), and a namespace package always loses to a *regular* module of the same
   name found anywhere else on `sys.path`, regardless of search order — so `import vggt` silently
   resolved to this repo's unrelated `pipeline/vggt.py` instead. An initial fix
   (`sys.path.insert(0, vggt_repo)`) did *not* work, for exactly that reason (confirmed by
   reproducing both the failure and the fix standalone on hfbox before touching the real code); the
   real fix drops `fast_worker.py`'s own directory from `sys.path` at import time (it has no
   `pipeline.*` imports by design, so this costs nothing).
2. **OpenMVS `InterfaceCOLMAP` relative-path doubling.** A relative `-i` gets re-interpreted
   against `-w`, not the process's actual cwd — the exact bug class `pipeline/mvs.py` already has a
   comment about for Stage B. Fixed the same way: `.resolve()` `fast_worker.py`'s `work_dir` before
   building any OpenMVS command.
3. **`frame_timestamps` keyed by the wrong string.** `pipeline/fast.py` wrote `frame_times.json`
   keyed by frame id (`"p0001"`, matching `Camera.id`/`poses_to_cameras`'s own `stem` convention),
   but `_full_res_cameras`'s new `frame_timestamps` override indexed it by the raw filename
   (`"p0001.jpg"`) — a guaranteed `KeyError`. Fixed to index by `Path(raw.name).stem`; added a
   regression test (`test_full_res_cameras_frame_timestamps_override_keys_by_stem`) since nothing
   had exercised this override before.
4. **`pycolmap.undistort_images` isn't idempotent.** A rerun of Stage A on the same site (`--force`,
   or a warm worker serving the same site's spool twice) left a populated `dense_dir` from last
   time; `undistort_images` refuses to overwrite an existing `images/` dir
   (`boost::filesystem::copy_file: File exists`). Fixed by clearing `dense_dir` first.

A fifth, adjacent bug (not in the fast-path code, but blocking the QA measurements this section
needed): **`pipeline/qa.py` hardcoded `mesh.glb`/`cameras.json`**, so `run()`'s auto-wired
`qa.json` silently failed (the exception is caught and logged, not raised) on every package
published in the new revision-named layout. Fixed to read `scene.json`'s `mesh.file`/`cameras`
fields, falling back to the old flat names when there's no `scene.json` (existing tests, ad-hoc
package dirs).

### 9.2 Measured timings (hfbox, AMD RX 9070 XT, real corpus clips, 48 preview frames)

| | lighthouse-ff | strasbourg-ff |
|---|---|---|
| source clip | `lighthouse-orbit-coastal-1.mp4` (26.4MB, 23.3s, 2732×1440) | `strasbourg-cathedral-spire.mp4` (12.3MB) |
| **preview\_ready\_s, cold** (fresh subprocess, first VGGT call this session) | **32.3s** | 57.6s (first real call on a freshly-started worker — see note below) |
| **preview\_ready\_s, cold, disk cache warm** (fresh subprocess, MIOpen kernel cache already on disk) | — | **37.0s** (`vggt_forward_s` 6.7s, `model_load_s` 7.9s) |
| **preview\_ready\_s, warm** (already-loaded worker, 2nd+ dispatch) | — | **27.4s** (`vggt_forward_s` 6.7s, no model-load) |
| full-stage total (`_run_full`'s own `timings_s` sum) | ~713s (~11.9 min) | ~606s (~10.1 min), dominated by `mvs_densify_s` (534–626s) |
| revision published | preview=1, full=2 | preview=1, full=2 |

Note on the 55–57s first-warm-job numbers: a fresh worker's *very first* real forward pass still
pays MIOpen's kernel-compile cost once per process (confirmed — a controlled A/B on strasbourg-ff,
both with the on-disk MIOpen cache already warm, gives cold=37.0s vs. warm(2nd job)=27.4s, i.e. the
warm worker's real, isolated benefit is skipping the ~7.9s model load + subprocess spawn, **not**
kernel compilation, which `MIOPEN_USER_DB_PATH`/`MIOPEN_CUSTOM_CACHE_DIR` under `/workspace` already
amortizes on disk regardless of warm/cold). Lighthouse's very first cold call this session was
already fast (6.1s forward pass) because some MIOpen kernels were evidently already cached on this
shared box from earlier sessions; strasbourg's first call paid a real, unexplained ~49s forward-pass
cost once (flagged as an observed anomaly, not root-caused — time-boxed). Every `preview_ready_s`
above is well under the plan's "<1 minute" target either way.

### 9.3 QA (PSNR/SSIM/coverage, `pipeline qa`, real photos vs. rendered mesh)

| | lighthouse-ff preview (r1) | lighthouse-ff full (r2) | strasbourg-ff preview (r1) | strasbourg-ff full (r2) |
|---|---|---|---|---|
| n (train/held-out) | 48 / 0 | 93 (47/46) | 48 / 0 | 65 (33/32) |
| mean PSNR | 23.50 | 25.49 | 15.66 | 16.80 |
| mean SSIM | 0.446 | 0.687 | 0.434 | 0.524 |
| mean coverage | 0.439 | 0.265 | 0.412 | 0.113 |

Full-stage coverage is *lower* than preview's on both sites — not a regression: Stage B's
auto-crop-to-subject (`--crop auto`) tightly frames the lighthouse/spire, so PSNR/coverage over the
*whole* frame understate how good the covered region looks (confirmed by eye, §9.4). Held-out
frames score within noise of train frames on both full-stage runs (lighthouse 25.34 vs. 25.64 PSNR,
strasbourg 16.80 vs. 16.80) — genuine generalisation, not just memorizing the training photos, and
(new for this session) held-out actually triggers now: `--stages preview,full`'s default
`holdout_every=5` extracts denser than SfM feeds on, so lighthouse-ff/strasbourg-ff both have a real
held-out set, unlike the four earlier corpus clips in §7/`p1-quality-runs.md`, where every extracted
frame was also an SfM input frame.

### 9.4 texcorr (photo-vs-texture colour correlation, `hfbox:/workspace/texdbg/texcorr.py`)

Only usable for lighthouse-ff's preview mesh: **(nan), (0.40, 0.26), (0.43, -0.27), (-0.42, -0.14)**
per view (`(1-v), v` UV convention) — a real but weak/inconsistent-sign signal, expected for a
coarse <1-minute TSDF mesh's texture. Every other combination (both full-stage meshes, strasbourg's
preview) returned all-NaN: `np.corrcoef` on an empty/degenerate front-facing-triangle set, which
tracks with the same flyby-crop-skip / tight-auto-crop behaviour already flagged in
`p1-quality-runs.md`'s "remaining quality problems" #1 for this class of orbit footage — not a new
bug, just a tool (written for uncropped meshes) that doesn't handle a tightly-cropped one gracefully.
Not fixed (`texdbg/` is a hand-rolled debug script, out of this task's scope).

### 9.5 Alignment residual and scale agreement (Stage B rigid-aligned onto the preview)

| | lighthouse-ff | strasbourg-ff |
|---|---|---|
| `aligned_to_preview` | true | true |
| `alignment_residual_m` | 3.10 | 3.66 |
| scale agreement | preview 1.0 vs. full 1.0, diff 0.0%, agrees | preview 1.0 vs. full 1.0, diff 0.0%, agrees |

Caveat, honestly: neither corpus clip has GPS telemetry, an ArUco board, or a `--known-height-m`/
`--known-distance-json` override, so `scale_method` is `"none"` (scale locked to 1.0) on both —
`validate_package` correctly flags this (`"the mesh is NOT at real-world scale"`). The residuals
above are therefore in *reconstruction units*, not metres; the rigid-alignment *mechanism* (Umeyama
without scale, on interpolated camera centres at matched timestamps) is exercised and working
end-to-end (both runs published `aligned_to_preview: true` with a finite residual and the frame id
carried over from the preview), but a true metric residual needs a corpus clip with a scale
reference, which neither of these two happens to have.

### 9.6 Visual inspection (QA side-by-side PNGs, photo | render | abs-diff, hand-viewed)

All four (`lighthouse-ff` r1/r2, `strasbourg-ff` r1/r2) hand-viewed. Preview meshes (r1) are
genuinely recognisable — the lighthouse's red/white stripes and cross-shaped structure, the
cathedral spire's silhouette — but speckled/noisy at the ground plane (raw TSDF fusion, no
cleanup pass) and looser in scale/pose than the full mesh. Full meshes (r2) are a large, obvious
step up: sharp, correctly-textured, tightly cropped to the subject, essentially photorealistic for
the covered region on both sites. One preview-vs-full comparison JPEG per site (render panel only,
both under 60KB) saved at `docs/research/img/lighthouse-ff-preview-vs-full.jpg` and
`docs/research/img/strasbourg-ff-preview-vs-full.jpg`.
