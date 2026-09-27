# COLMAP 4.2 and RealityScan 2.x: what's new, what we're not using, what to do about it

Scope: two separate products the task lumped under one ask ("research COLMAP 4.2 (realityscan)") —
**they are not the same tool.** COLMAP/pycolmap 4.x is the open-source SfM/MVS library already in
`pipeline/sfm.py` (BSD-3-Clause, self-hosted, free). Epic's RealityScan 2.x (formerly Capturing
Reality's RealityCapture) is a separate, closed-source, free-under-$1M-revenue Windows/Linux desktop
photogrammetry app that happens to be able to **import a COLMAP project as its own input**. Read
`docs/research/p1-toolchain.md` §1.1/§2.1, `p1-fast-recon.md` §1–§4, `p1-cloud-world-models.md` §1,
and `p1-quality-runs.md` first — this doc only covers what those four didn't: COLMAP 4.0→4.2's actual
version-by-version changelog, the ROCm/HIP story neither doc caught (COLMAP 4.2.0 added a `HIP_ENABLED`
build option for `patch_match_stereo`, and a separate unmerged PR extends HIP to GPU SIFT), pose
priors from DJI SRT GPS, and a much deeper pass on RealityScan (its June 2026 AMD GPU support,
licensing, CLI, export, and cloud feasibility) than `p1-fast-recon.md` §4's one-row survey mention.

**Both products already touch this pipeline more than the brief assumed:** `pipeline/sfm.py` is
pycolmap 4.2.0 today (confirmed: `pipeline/sfm.py`'s own docstring and `p1-toolchain.md`'s hands-on
version check), and RealityScan's CLI has a direct `importColmap` command — meaning our existing
pycolmap sparse reconstruction could, in principle, feed RealityScan's dense/mesh/texture stage on a
rented Windows GPU box without re-running RealityScan's own SfM at all.

---

## Summary table

| Option | Verdict | Why (one line) |
|---|---|---|
| COLMAP 4.2 multi-component global mapper (auto fallback to incremental) | **Already adopted** | `pipeline/sfm.py::_map` already checks `len(recons) > 1` and a registered-fraction threshold, exactly the pattern 4.2's changelog entry enables |
| COLMAP 4.2 `HIP_ENABLED` for `patch_match_stereo` (source build) | **Trial** | Real, dated Aug 2026, targets our exact use case (AMD dense stereo replacing OpenMVS's CPU-only `DensifyPointCloud`, the pipeline's 200–1600s bottleneck) — but source-build-only, no pip wheel, `gfx1201` not in the docs' worked examples, and a real OpenMVS-integration gap (below) |
| COLMAP GPU SIFT extraction/matching | **Skip (as-is)** | Pip wheel is CUDA-only (confirmed hands-on, `p1-toolchain.md`); the HIP path for SIFT is a separate, unmerged PR as of this session, not in any tagged release |
| COLMAP vocab-tree loop detection | **Skip** | Needs a downloaded vocab-tree file; our head/tail hand-matched loop closure (`sfm.py::_match_orbit_closure`) already solves the one thing we need it for (orbit seam) without the extra dependency |
| Pose priors from DJI SRT GPS | **Trial** | Real, well-documented pycolmap API (`Database.write_pose_prior`, `PosePrior`, `GPSTransform`) — genuinely could stabilize scale/orientation on repetitive facades, but needs its own SRT-parsing glue and per-second (not per-frame) GPS granularity is a real precision ceiling |
| COLMAP's own dense MVS (`patch_match_stereo`+`stereo_fusion`) vs OpenMVS, on CUDA cloud hardware | **Trial (only if renting NVIDIA)** | Already flagged as the GPU-floor lever in `p1-fast-recon.md` §1.2; nothing new found here changes that verdict |
| RealityScan free tier | **Trial** | Genuinely free under $1M revenue (hackathon qualifies), real `importColmap` CLI path exists |
| RealityScan as full-mesh replacement, self-hosted on `hfbox` | **Skip** | Windows/CUDA-first tool on a Linux/AMD box; June 2026 AMD GPU support (RDNA3/4, our exact `gfx1201`) is confirmed **Windows-only**, Linux AMD support "coming later" per Epic's own announcement |
| RealityScan on a rented cloud Windows NVIDIA GPU box | **Trial, not before the event** | Technically sound (CUDA works, `importColmap` skips its own SfM) but adds a second OS/cloud account/licensing surface for an unmeasured, possibly-marginal speed win over an OpenMVS-CUDA build on the same rented box |
| RealityScan CLI mesh export (glTF/GLB, texture atlas, decimation) | **Confirmed usable if trialed** | `exportModel`, `calculateTexture` (up to 16384px), `simplify <targetTriangleCount>` all real, documented CLI commands |

---

## 1. COLMAP 4.x / 4.2

### 1.1 What changed, 3.x → 4.2, version by version (changelog-cited)

Our version, confirmed hands-on in `p1-toolchain.md`: `pycolmap==4.2.0` / `COLMAP 4.2.0` (released
2026-08-31). This session did **not** have pycolmap installed locally (`pip show pycolmap` → not
found) — everything below is read from the official changelog/GitHub, not re-verified against a live
install; where `p1-toolchain.md` already verified something hands-on, that's cited instead.

| Version | Date | Headline changes (quoted from the changelog) |
|---|---|---|
| **4.0.0** | 2026-03-14 | "Integrated GLOMAP global SfM pipeline into COLMAP as a first-class alternative to the incremental/hierarchical mappers" — GLOMAP is no longer a separate install (already established, `p1-toolchain.md` §1.1). Added **ALIKED feature extraction through ONNX** and **LightGlue ONNX matching for both SIFT and ALIKED**. Added EXIF-orientation auto-rotation during extraction/matching. New structure-less registration fallback (generalized relative pose) for images without 3-view overlap. New division (`SIMPLE_DIVISION`/`DIVISION`) and distortion-free `FISHEYE` camera models. Mesh simplification (QEM decimation) and mesh texture mapping added. Replaced FreeImage with OpenImageIO (~2.5× faster image I/O). Dropped Python 3.9 support. |
| **4.1.0** | 2026-06-26 | Added **Caspar**, a GPU-accelerated bundle-adjustment backend, "1-2 orders of magnitude faster than the Ceres CUDA backend" (CUDA-only per its framing — not usable on our AMD box, unconfirmed either way this session). Added spherical/equirectangular camera models (360° panoramas) and the Enhanced Unified Camera Model (EUCM). Added advancing-front surface reconstruction meshing. Added **gravity pose priors extracted from EXIF orientation tags** (photo EXIF, not video-frame GPS — see §1.2.3). |
| **4.2.0** | 2026-08-31 | Added **multi-component support to the global mapper** — a fragmented `global_mapping` result now comes back as separate `Reconstruction`s instead of one silently-wrong model (this is exactly what `pipeline/sfm.py::_map` already checks for, §1.5). Added **LoMa learned feature extraction/matching through ONNX** (`LOMA_B`, `LOMA_B128`). Added **ROCm/HIP acceleration for `patch_match_stereo`** — new, and the single most relevant finding of this doc (§1.3). Added a browser-based local 3D viewer for sparse reconstructions. Added 6-point shared-focal / one-sided-focal relative-pose solvers (helps when intrinsics are unknown). Replaced Sampson error with a pixel-consistent tangent Sampson error for two-view geometry. Added analytical reprojection Jacobians for every camera model plus 4–7× faster image undistortion. Added `SequentialPairingOptions.loop_detection_min_index_distance` (§1.2.2). |

Sources: [colmap.github.io/changelog.html](https://colmap.github.io/changelog.html),
[github.com/colmap/colmap/releases/tag/4.0.0](https://github.com/colmap/colmap/releases/tag/4.0.0),
[github.com/colmap/colmap/blob/main/CHANGELOG.rst](https://github.com/colmap/colmap/blob/main/CHANGELOG.rst).

### 1.2 Features we're not using yet

#### 1.2.1 GPU SIFT extraction/matching — CUDA-only in the pip wheel; a ROCm path exists but isn't released

`p1-toolchain.md` §1.1 already confirmed hands-on that our `pycolmap` pip wheel defaults
`use_gpu=False` and that `Device.__members__` has no `hip`/`rocm` entry — only `cpu`/`cuda`/`auto`.
That's still true for the **pip wheel**. What's new this pass: COLMAP officially ships a
CUDA-12-only accelerated wheel as a *separate* PyPI package (`pycolmap-cuda12`, Linux-only per
[colmap.github.io/pycolmap/index.html](https://colmap.github.io/pycolmap/index.html)) — irrelevant to
our AMD box, but relevant if we ever run this stage on a rented NVIDIA cloud box (§1.4).

For AMD specifically: a GitHub PR, **["Add ROCm/HIP support for GPU SIFT feature extraction and
matching" (#4635)](https://github.com/colmap/colmap/pull/4635)** by an AMD engineer (`jeffdaily`)
compiles SiftGPU's compute kernels under HIP and was **approved** ("LGTM apart from some minor
comments") with its most recent commits from **September 2026** — after the 4.2.0 tag (Aug 31 2026).
**Unverified**: whether it has since merged to `main` or which future version will ship it; as of this
session it is not in any tagged COLMAP release. Track this PR before planning around it.

#### 1.2.2 Sequential matcher options and vocab-tree loop detection

`pipeline/sfm.py::_match_orbit_closure` already hand-rolls loop closure for a closed orbit (explicitly
pairs the first/last `_LOOP_CLOSURE_K=10` frames) specifically because, per its own comment,
`SequentialPairingOptions.loop_detection` needs a `vocab_tree_path` file with "no built-in download in
this pip wheel." **New information that complicates but doesn't overturn that finding**: a COLMAP PR
merged in version **3.11.1** (Dec 2024, well before our 4.2.0) added automatic download-and-cache of
vocab-tree files (`~/.cache/colmap/`) — but that auto-download is documented against the CLI's
`vocab_tree_matcher` command / `pycolmap.match_vocabtree()` function specifically
([github.com/colmap/colmap/pull/3036](https://github.com/colmap/colmap/pull/3036)), not necessarily
against `SequentialPairingOptions.loop_detection=True` combined with `match_sequential` (the code path
`p1-toolchain.md`'s author actually checked, hands-on, on the exact 4.2.0 wheel). These may be two
different bindings to the same underlying C++ option, or genuinely different behavior — **unverified
which**, no pycolmap install in this session to check `pycolmap.match_vocabtree.__doc__` directly.
**Practical read: our existing head/tail hack is the right call regardless** — it solves the one thing
we need (closing an orbit's seam) without depending on either a manually-supplied vocab file or an
unverified auto-download reaching an internet host from `hfbox` mid-reconstruction.

New in 4.2.0 specifically: `SequentialPairingOptions.loop_detection_min_index_distance` — excludes
frames too close in sequence index from consuming the vocab-tree retrieval budget, i.e. avoids
re-detecting "loops" against a frame's own near neighbors. Only relevant if we do adopt vocab-tree
matching later; not worth adding today given the point above.

#### 1.2.3 Pose priors from DJI SRT GPS — real API, real integration gap

A genuinely unused lever the task specifically asked about. DJI's `.SRT` sidecar (the same file format
as subtitles, repurposed) carries **GPS lat/lon/altitude roughly once per second**, not per frame —
confirmed format from multiple DJI-telemetry-tooling sources, e.g.
[terasor.com/articles/what-are-srt-and-lrf-files](https://terasor.com/articles/what-are-srt-and-lrf-files)
and [swyvl.io/blog/how-to-map-dji-drone-video-gps](https://swyvl.io/blog/how-to-map-dji-drone-video-gps/).
Newer DJI models (Mini series included) use a `[latitude: .. ] [longitude: ..] [rel_alt: .. abs_alt:
..]` bracketed format; older models use `GPS(lat,lon,alt)`. **This is coarser than per-frame** — an
orbit shot at a few m/s means each GPS fix is several frames apart, so priors would need interpolation
between SRT timestamps, and any per-frame gravity/attitude info from the gimbal (if present in the SRT
at all — unverified for the Mini 4K specifically) is separate from position.

pycolmap's pose-prior machinery is real and reasonably documented, not something we'd be building from
scratch:

```python
# Sketch only -- exact method names read from PR discussion/docs, NOT verified against a live
# pycolmap 4.2.0 install this session (none installed here). Confirm signatures with
# pycolmap.PosePrior.__doc__ / pycolmap.GPSTransform.__doc__ before wiring in.
db = pycolmap.Database(db_path)
prior = pycolmap.PosePrior(
    position=[lat, lon, alt],
    coordinate_system=pycolmap.PosePrior.CoordinateSystem.WGS84,
)
db.write_pose_prior(image_id, prior)          # per image, from the interpolated SRT fix

# Convert WGS84 -> a local cartesian frame before/via bundle adjustment:
gps = pycolmap.GPSTransform(ellipsoid=pycolmap.GPSTransformEllipsoid.WGS84)
xyz = gps.ell_to_xyz([[lat, lon, alt]])        # exact method name (ell_to_xyz vs ellipsoid_to_ecef)
                                                # differed between sources checked -- unverified
```

Two consumption paths exist per COLMAP's own docs/PRs: the standalone `colmap pose_prior_mapper` CLI
executable (`--prior_position_std_x/y/z` control prior confidence), or, for a pure-Python pipeline like
ours, `pycolmap.create_pose_prior_bundle_adjuster(options=..., prior_options=..., pose_priors=dict[int,
PosePrior], reconstruction=...)` run after triangulation
([PR #2660](https://github.com/colmap/colmap/pull/2660)). **What this would actually buy us**: GPS
priors mainly help scale/global-orientation stability and can rescue registration on repetitive
facades where pure visual matching is ambiguous — not primarily a speed lever. Given
`pipeline/calibrate.py` already applies real-world scale after the fact (per the existing docs), the
marginal value here is *robustness* on hard footage, not something replacing calibration. **Trial**:
worth a spike parsing one corpus clip's `.SRT` (if the DJI Mini 4K corpus footage has one — unverified,
not checked this session) and feeding priors into one `global_mapping` run to see if registered-image
fraction improves on the two clips (`strasbourg`, `facade`) that `p1-toolchain.md`/`p1-quality-runs.md`
already flag as harder cases.

### 1.3 COLMAP's own MVS vs OpenMVS — the ROCm/HIP finding

Confirmed unchanged from `p1-toolchain.md` §1.1: the **pip wheel's** `patch_match_stereo` still
requires CUDA, no CPU fallback. **New this pass**: COLMAP 4.2.0's changelog adds "ROCm/HIP acceleration
for `patch_match_stereo`, enabling dense reconstruction on supported AMD GPUs through the
`HIP_ENABLED` build option" — confirmed via both the official changelog and the install docs
([colmap.github.io/install.html](https://colmap.github.io/install.html)):

```
cmake .. -GNinja \
    -DCUDA_ENABLED=OFF -DHIP_ENABLED=ON \
    -DCMAKE_HIP_ARCHITECTURES=gfx90a \        # docs' own example is MI200/MI250 (gfx90a) / MI300 (gfx942)
    -DCMAKE_HIP_COMPILER=/opt/rocm/llvm/bin/clang++
```
`CUDA_ENABLED` and `HIP_ENABLED` are mutually exclusive; CMake ≥3.21 required; needs `hip`, `hiprand`,
`rocrand` present. **This is source-build-only — no pip wheel ships it.**

Three real caveats before treating this as a drop-in fix for OpenMVS's `DensifyPointCloud` bottleneck
(the documented 200–1600s dominant cost, `p1-quality-runs.md`):

1. **`gfx1201` (our RX 9070 XT, RDNA4) isn't in the docs' worked `CMAKE_HIP_ARCHITECTURES` examples**
   (`gfx90a`/`gfx942` are datacenter MI-series parts). ROCm 7.2 + PyTorch on hfbox already targets
   `gfx1201` successfully for VGGT (per other docs' hands-on runs) — but that's PyTorch's own
   prebuilt ROCm wheel, a different verification than compiling COLMAP's raw HIP C++ kernels
   ourselves against `gfx1201`. **Unverified** whether it compiles/runs correctly; this would need its
   own from-source build spike on `hfbox` to know.
2. **This is genuinely new** — landed in a release 3–4 weeks before this research pass. Real bug risk
   for a first-generation hardware-acceleration backend, independent of whether it compiles at all.
3. **The bigger risk is the pipeline seam, not the build**: OpenMVS's `ReconstructMesh` consumes
   `DensifyPointCloud`'s own dense-point format, which — unlike a bare XYZ point cloud — carries
   per-point *view visibility* metadata that `ReconstructMesh`'s Delaunay-carving algorithm needs.
   Whether COLMAP's own `stereo_fusion` output (or an OpenMVS `.mvs` built by hand from it) preserves
   that in a form `ReconstructMesh` accepts is **not confirmed this session** — no converter was found
   in either OpenMVS's or COLMAP's own docs in this pass. Swapping only the dense-stereo step is a
   deeper integration change than it looks, not a one-flag config swap.

An `AMD-Ecosystem/colmap` GitHub-org mirror of the repo exists
([github.com/AMD-Ecosystem/colmap](https://github.com/AMD-Ecosystem/colmap)) but this session couldn't
determine whether it's a genuine AMD-maintained fork with its own patches/binaries or just a plain
mirror — **not confirmed**, not relied on above.

**Verdict: Trial, not Adopt.** If a from-source HIP build of COLMAP is attempted as a time-boxed spike
on `hfbox`, treat it as replacing the *entire* dense→mesh chain (patch-match stereo + COLMAP's own
`stereo_fusion` + COLMAP's new mesh-simplification/texture-mapping from 4.0.0, §1.1) rather than trying
to splice its output back into `ReconstructMesh`/`TextureMesh` — that avoids caveat 3 entirely, at the
cost of losing OpenMVS's already-tuned `--decimate`/seam-leveling-off/crop pipeline this project has
already debugged (`p1-quality-runs.md`). Given the real, measured OpenMVS chain already works end to
end and the fast VGGT preview already covers the <1 min need (`p1-fast-recon.md`), this is a
background-optimization experiment, not a blocker for anything currently planned.

### 1.4 On a rented NVIDIA cloud box (not our AMD box)

If the `$10k AWS`/`$10k Azure` budget is spent on an NVIDIA GPU instance anyway (already the plan for
the VGGT preview path per `p1-fast-recon.md` §5–§6), the CUDA story is simpler and better-trodden than
the HIP one: `pycolmap-cuda12` gives GPU SIFT+matching directly from pip (no source build), and
`pycolmap.patch_match_stereo` (CUDA) replaces `DensifyPointCloud` with **no OpenMVS-integration gap**
of the kind in §1.3 caveat 3, because in that case the natural pairing is COLMAP SfM → COLMAP's own
dense stereo → COLMAP's own 4.0.0-added mesh simplification/texture mapping, entirely inside one
toolchain. This isn't a new finding — it's the same GPU-floor lever `p1-fast-recon.md` §1.2/§1.3
already named as "not tested, real build/setup cost" — nothing here changes that verdict, it just
confirms the CUDA build path is real and documented rather than speculative.

### 1.5 What's already handled

`pipeline/sfm.py::_map` (lines 82–100) already implements exactly the pattern COLMAP 4.2.0's
multi-component changelog entry enables: pick the largest of possibly-multiple `global_mapping`
components, and fall back to `incremental_mapping` if the registered fraction is below 60% *or* the
scene fragmented into multiple components. No action needed here — flagging only so it's not
mis-read as an "unused feature."

---

## 2. RealityScan 2.x (Epic)

### 2.1 Licensing and cost

Confirmed directly from [realityscan.com/license](https://www.realityscan.com/license): **free** for
individuals, small businesses, educators, and students **under $1M USD annual gross revenue** (a
36-hour hackathon team qualifies) — **$1,250/seat/year** above that threshold, contact-sales terms for
25+ seats. Note: as of a May 27 2026 licensing change, RealityScan and Twinmotion are no longer bundled
into one Unreal Engine seat subscription; each is priced/licensed separately now
([cgchannel.com](https://www.cgchannel.com/2025/11/epic-games-releases-realityscan-2-1/)).

### 2.2 OS support

**Not Windows-only.** Epic's own hardware/software requirements page states RealityScan "runs
primarily on Windows. A Linux version is also available, but it is recommended only for CLI-based
workflows" — i.e. a headless/automation-oriented Linux build exists, distinct from the full
Windows GUI app
([dev.epicgames.com/documentation/realityscan/hardware-and-software-requirements](https://dev.epicgames.com/documentation/realityscan/hardware-and-software-requirements?lang=en-US)).
**Unverified this session**: whether that Linux CLI build is a native Linux binary or runs under a
bundled Wine layer — search results returned conflicting/unconfirmed signals on this specific point and
the primary docs page didn't state it explicitly; don't rely on either claim without re-checking Epic's
Linux-specific install docs directly.

### 2.3 GPU requirement

**NVIDIA CUDA was required through RealityScan 2.1** — minimum CUDA Compute Capability 3.5, 6.1+
recommended, 1GB VRAM minimum (8GB realistic per third-party hardware guides); the app can do image
*registration* without a GPU but cannot build models or textures without one (Epic's own requirements
page).

**RealityScan 2.2 (released 2026-06-24) added real AMD GPU support** — this is a genuine, dated,
well-corroborated finding worth flagging clearly since it changes the "CUDA-only" assumption in
`p1-fast-recon.md` §4's earlier survey-level note. Per Epic's own announcement and independent coverage
(Peter Falkingham's hands-on writeup gives the fullest architecture list):

> "RDNA 3 (gfx1100): RX 7900 XTX/XT, PRO W7900/W7800" · "RDNA 3 (gfx1101): RX 7800 XT, RX 7700 XT, PRO
> W7700" · "RDNA 3 (gfx1102): RX 7600 XT / 7600 / 7650 GRE" · "RDNA 3.5 (gfx1151): Ryzen AI Max 'Strix
> Halo' APUs" · "RDNA 4 (gfx1200): RX 9060 XT / 9060" · **"RDNA 4 (gfx1201): RX 9070 XT / 9070 / 9070
> GRE, AI PRO R9700"**

— our exact `hfbox` GPU (RX 9070 XT, `gfx1201`) is explicitly on the supported list. "Every
reconstruction stage that was previously accelerated on GeForce and Quadro is now equally accelerated
on Radeon and Radeon PRO," per RealityScan's own release post
([realityscan.com/news/realityscan-2-2-is-here-with-full-amd-gpu-support-download-today](https://www.realityscan.com/news/realityscan-2-2-is-here-with-full-amd-gpu-support-download-today)).
**The catch, also from Epic's own materials: "Available on Windows. Linux coming later."** —
so this doesn't help `hfbox` (Ubuntu 24.04) today regardless of the GPU match; it only matters if
RealityScan is ever run on a Windows box (local or cloud), where it removes the NVIDIA requirement.
No AWS/Azure GPU instance family offers RDNA3/4-class AMD GPUs on Windows as of this pass (AWS's only
AMD-GPU instance family, G4ad, uses an older Radeon Pro V520 — RDNA1/`gfx1011` — not on the supported
list above), so this doesn't currently open a cheaper cloud path either; it's relevant mainly if a
local Windows machine with a recent Radeon card ever enters the picture.

Sources: [cgchannel.com/2026/06/epic-games-releases-realityscan-2-2-with-amd-gpu-support](https://www.cgchannel.com/2026/06/epic-games-releases-realityscan-2-2-with-amd-gpu-support/),
[peterfalkingham.com/2026/06/25/realityscan-2-2-finally-brings-amd-gpu-support-to-windows](https://peterfalkingham.com/2026/06/25/realityscan-2-2-finally-brings-amd-gpu-support-to-windows/).

### 2.4 CLI / headless automation

Real and documented. Commands are passed as arguments to `RealityScan.exe` (or chained in a batch
script with `^` line-continuation); `-headless` suppresses the UI
([rshelp.capturingreality.com/en-US/tutorials/commandline.htm](https://rshelp.capturingreality.com/en-US/tutorials/commandline.htm),
[.../headless.htm](https://rshelp.capturingreality.com/en-US/tutorials/headless.htm)). RealityScan 2.1
added REST and gRPC automation plugins with Python samples and "full Linux support" for that
automation layer specifically (distinct from GPU support, §2.3).

Concrete batch example (commands cited individually against
[rshelp.capturingreality.com/en-US/appbasics/allcommands.htm](https://rshelp.capturingreality.com/en-US/appbasics/allcommands.htm);
full working script **not independently run this session** — no RealityScan install/license available
in this sandboxed environment, per the task's no-account/no-paid-resource constraint):

```batch
RealityScan.exe -headless ^
  -addFolder  C:\work\frames ^
  -selectAllImages -align ^
  -importColmap C:\work\sfm\sparse\0\cameras.txt ^
  -selectMaximalComponent ^
  -calculateNormalModel ^
  -simplify 200000 ^
  -calculateTexture ^
  -exportModel model C:\work\out\mesh.glb ^
  -quit
```

Relevant commands confirmed to exist, individually, from the official CLI reference: `-importColmap`
("Import a COLMAP project... requires the path to the COLMAP file, any of the three text files"),
`simplify <targetTriangleCount>` (exact-count decimation — notably simpler than OpenMVS's
fractional `--decimate`, no `_auto_decimate_ratio` guesswork needed), `calculateTexture` (settings
incl. resolution up to 16384×16384 via a `params.xml` settings file), `exportModel` (OBJ/PLY/**GLB**
among supported formats). **Not confirmed**: whether `-align` alone is skippable/redundant when
`-importColmap` already supplies poses, or the exact flag ordering RealityScan expects — this sketch
combines individually-documented commands, it is not a tested script.

### 2.5 Speed and quality claims for aerial/drone datasets

**No current (2026), aerial/drone-specific, apples-to-apples benchmark against COLMAP+OpenMVS was
found this session.** The only concrete timing numbers found are from a **2019** hobbyist benchmark
(Peter Falkingham, object-scale fossil photogrammetry, not aerial): 53 photos of a dinosaur model —
RealityCapture ~6.6 min, COLMAP alone ~50 min, COLMAP+OpenMVS ~37 min, Meshroom ~45 min; 218 photos of
a second specimen, RealityCapture ~10 min
([peterfalkingham.com/2019/05/17/photogrammetry-testing-reality-capture-commercial-software](https://peterfalkingham.com/2019/05/17/photogrammetry-testing-reality-capture-commercial-software/)).
**Explicitly mark this unverified for our use case**: seven-year-old software versions on both sides,
unknown/unstated GPU, object-scale not building-exterior-aerial, and RealityCapture's own explanation
for its speed edge (reconstructing directly from the sparse cloud without full depth maps, streaming
disk↔RAM instead of holding the whole dataset in memory) is architecture-level, not something that
transfers a specific percentage to our 1920px/~150-300 frame orbit footage. A search specifically for a
2026 RealityScan-vs-COLMAP Linux/aerial comparison article turned up a promising-looking title
("RealityScan vs COLMAP vs ODM: Linux Photogrammetry (2026)") but the source domain was blocked by this
session's fetch tool as unverified-safe — **its claims are not included here**, flagged as
inaccessible rather than silently dropped.

### 2.6 Export: glTF/GLB/OBJ, decimation, texture atlas

Confirmed from RealityScan's own CLI/help docs
([rshelp.capturingreality.com/en-US/tools/export.htm](https://rshelp.capturingreality.com/en-US/tools/export.htm),
[.../texturing_part2.htm](https://rshelp.capturingreality.com/en-US/tools/texturing_part2.htm),
[.../lodexport.htm](https://rshelp.capturingreality.com/en-US/tools/lodexport.htm)):

- **Export formats**: OBJ, PLY, XYZ point cloud, Alembic, and **GLB** among the supported formats.
- **GLB size ceiling, new and worth flagging**: glTF 2.0's container uses uint32 length fields, capping
  a GLB around ~4GB — a non-issue at our ~200k-tri/few-MB budget, but Khronos shipped glTF 2.1 in June
  2026 with a 64-bit container lifting that cap; not relevant to us either way.
- **Texture atlas**: resolution and texture *count* per model both configurable, resolution up to
  **16,384×16,384** (far above our 4096px target — just cap it).
- **Decimation**: `simplify <targetTriangleCount>` — an **exact** triangle-count target, unlike
  OpenMVS's `TextureMesh --decimate <fraction>` which our own `mvs.py::_auto_decimate_ratio` has to
  approximate from the pre-texture mesh's face count. If RealityScan were ever adopted for the
  dense/mesh stage, this specific piece (exact-count decimate) is strictly simpler than what we have.

### 2.7 Georeferencing and scale

CLI commands `importGroundControlPoints`, `exportGroundControlPoints`, `importControlPointsMeasurements`
exist and are documented — standard GCP-based georeferencing, the same "apply real scale from a known
external measurement" pattern `pipeline/calibrate.py` already uses (confirmed pattern-level only; exact
GCP file format/workflow **not verified hands-on** this session). GPS-EXIF-based georeferencing is
standard in this class of tool generically but wasn't separately confirmed against RealityScan's own
docs this pass — **unverified**, not contradicted, just not directly checked.

### 2.8 Cloud Windows GPU instance feasibility

**Technically fits an NVIDIA Windows cloud box; no current AMD cloud option exists.** RealityScan needs
either an NVIDIA GPU (any OS it supports) or, as of 2.2, an AMD RDNA3/4 GPU **on Windows only** (§2.3).
Cloud instance sizing:

| Instance | GPU | On-demand $/hr (Linux pricing found; Windows surcharge not separately confirmed) |
|---|---|---|
| AWS `g5.xlarge` | 1× A10G 24GB | ~$1.01/hr |
| AWS `g6e.xlarge` | 1× L40S 48GB | ~$1.86/hr (already cited, `p1-fast-recon.md` §5.1) |
| Azure `NV6ads_A10_v5` | 1× A10 (partial) | ~$0.45/hr |
| Azure `NV12ads_A10_v5` | 1× A10 | ~$0.91/hr |

No AMD-GPU Windows instance family matching RealityScan 2.2's supported RDNA3/4 list was found on
either cloud (AWS's `G4ad` AMD instances use an older, unsupported Radeon Pro V520) — so "could it run
on AMD in the cloud" is **no, not usefully today**; the NVIDIA path is the only cloud-feasible one.
**Time-per-~150-frames**: **not found for RealityScan specifically, on any cloud GPU, in this pass** —
no vendor-published or third-party benchmark surfaced at our scale. The only anchor point available is
§2.5's 2019 desktop number (~6–10 min for 53–218 object photos on unstated, presumably weaker, hardware
than an A10G/L40S) — **explicitly not a substitute for a real benchmark**, listed only because nothing
better was found. A real answer requires either an actual timed trial (out of scope — no paid cloud
resources created this session) or finding a 2026 vendor benchmark this pass didn't surface.

---

## 3. How COLMAP and RealityScan relate

Plainly: **they are two unconnected products from two different organizations** (COLMAP/pycolmap:
academic origin, ETH Zürich lineage, BSD-3-Clause, self-hosted; RealityScan: Epic Games, closed-source,
licensed). The only real bridge between them is that RealityScan's CLI can **import** a COLMAP sparse
reconstruction (`-importColmap`) as an alternative to running RealityScan's own alignment step — a
one-directional integration point, not a shared codebase, shared license, or shared company. Nothing
found this session suggests any deeper relationship (no shared code, no COLMAP-inside-RealityScan or
vice versa). Any prior assumption that "COLMAP 4.2 (RealityScan)" names one product should be corrected
to: two separate options, comparable only in that both can end at a textured mesh from posed photos.

---

## 4. Recommendation

**COLMAP 4.2 changes:**
- **Multi-component global mapper**: already adopted (§1.5) — no action.
- **HIP `patch_match_stereo` (source build)**: **Trial**, time-boxed (a few hours), only as a
  background-optimization spike on `hfbox`, not before the event and not gating anything currently
  planned. Integration step: build COLMAP from source with `-DHIP_ENABLED=ON
  -DCMAKE_HIP_ARCHITECTURES=gfx1201`; if it compiles and runs, treat the whole dense→mesh→texture tail
  as COLMAP-native (its own `stereo_fusion` + 4.0's mesh simplification/texturing) rather than trying to
  feed OpenMVS's `ReconstructMesh` from it (§1.3 caveat 3). **Risk**: real build effort (Eigen, Ceres,
  the HIP toolchain, plus whatever COLMAP's CMake pulls in), `gfx1201` untested against this specific
  code path, and a brand-new (3–4 week old) hardware backend.
- **GPU SIFT (CUDA wheel or the unmerged HIP PR)**: **Skip** on `hfbox` (no CUDA; HIP path unmerged).
  **Trial** only if/when a rented NVIDIA cloud box is actually spun up for the fast-preview path —
  `pycolmap-cuda12` is a real, documented pip package, no source build needed there.
- **Pose priors from DJI SRT GPS**: **Trial**, low-cost first step: check whether the hackathon corpus
  clips actually have a `.SRT` sidecar (not confirmed this session), then spike parsing+feeding priors
  into one hard clip (`strasbourg` or `facade`) to see if registered-fraction improves. **Risk**:
  per-second (not per-frame) GPS granularity limits precision; real new parsing code either way.
- **Vocab-tree loop detection**: **Skip** — existing head/tail hack already covers the one real need
  (orbit seam closure) without a file dependency or an unverified auto-download reaching the internet
  from `hfbox` mid-run.

**RealityScan 2.x:**
- **As a full pipeline replacement, self-hosted on `hfbox`**: **Skip** — Linux+AMD GPU acceleration
  isn't available yet ("coming later," no date given); running it CPU-only on Linux defeats the point.
- **As a rented-cloud-Windows-NVIDIA trial for the full-quality mesh path**: **Trial, not before the
  event.** It's free under our revenue threshold, `importColmap` genuinely lets us skip re-running its
  own SfM, and its exact-count `simplify` and native GLB export are strictly simpler than our current
  `TextureMesh --decimate` + `mesh.py` exact-count touch-up combo. **Risk**: unverified turnaround for
  our actual frame count/resolution (§2.5/§2.8 — nothing found beats the 2019 desktop anchor), a second
  cloud account + a Windows image + a second licensing surface to manage for the event, and an
  unverified end-to-end CLI script (the §2.4 sketch combines individually-documented commands, never
  run together). If trialed, do it as a side experiment on one corpus clip before the event, not as a
  live dependency during it.
- **AMD GPU support (2.2)**: **Not actionable for this project right now** — Windows-only, and no
  matching cloud AMD-GPU-on-Windows instance family exists. Worth re-checking if/when Linux AMD support
  ships, since our exact card (`gfx1201`) is already on the supported list.

**Leftover for a future pass, not built here**: an actual timed trial of either COLMAP's HIP dense
stereo or RealityScan's `importColmap` path against one real corpus clip — both are "Trial" verdicts
specifically because no hands-on timing exists yet for our footage on our hardware.

---

## 5. Verified vs unverified (explicit, per source)

**Verified from official/primary sources this session** (changelog, GitHub, Epic's own docs — all
fetched directly, not secondhand): COLMAP 4.0.0/4.1.0/4.2.0 changelog entries quoted in §1.1;
`HIP_ENABLED` build flags and "patch_match_stereo only" scope from
[colmap.github.io/install.html](https://colmap.github.io/install.html); PR #4635's HIP-SIFT scope and
approved-but-unmerged status; PR #3036's vocab-tree auto-download merge into 3.11.1; RealityScan's
license terms and $1,250/seat price from [realityscan.com/license](https://www.realityscan.com/license);
RealityScan 2.2's exact AMD GPU model/architecture list and "Windows only, Linux coming later" from
Epic's own release post and Peter Falkingham's hands-on writeup; RealityScan CLI commands
(`importColmap`, `simplify`, `calculateTexture`, `exportModel`) from
[rshelp.capturingreality.com/en-US/appbasics/allcommands.htm](https://rshelp.capturingreality.com/en-US/appbasics/allcommands.htm);
DJI SRT format structure from multiple independent DJI-telemetry-tooling sources.

**Not verified this session, flagged inline where used**: exact pycolmap 4.2.0 method names for
`GPSTransform`/`PosePrior` (no pycolmap install available in this sandboxed session — `pip show
pycolmap` returned "not found"; every other doc in this repo that verified pycolmap API surface did so
against a locally-installed wheel, which this session didn't have); whether `gfx1201` actually compiles
under COLMAP's HIP build; whether RealityScan's Linux CLI build is native or Wine-based; any
aerial/drone-specific 2026 RealityScan speed benchmark (none found; one candidate source was blocked by
the fetch tool as an unverified domain and is explicitly excluded rather than guessed at); RealityScan
GCP/georeferencing workflow details beyond command names existing; Windows-OS pricing surcharge on the
AWS/Azure GPU instances in §2.8 (only Linux on-demand pricing was confirmed).

**Not attempted, per task constraints**: no RealityScan account/license activation, no cloud instance
launch, no paid API call, no COLMAP from-source HIP build — all explicitly out of scope for this
research pass.

---

## 6. Sources

**COLMAP / pycolmap**
- Changelog: https://colmap.github.io/changelog.html
- 4.0.0 release: https://github.com/colmap/colmap/releases/tag/4.0.0
- CHANGELOG.rst: https://github.com/colmap/colmap/blob/main/CHANGELOG.rst
- Install docs (HIP/CUDA build flags): https://colmap.github.io/install.html
- pycolmap docs index: https://colmap.github.io/pycolmap/index.html
- HIP GPU-SIFT PR: https://github.com/colmap/colmap/pull/4635
- Vocab-tree auto-download PR: https://github.com/colmap/colmap/pull/3036
- Pose-prior incremental mapper PR: https://github.com/colmap/colmap/pull/2660
- AMD-Ecosystem colmap mirror (unconfirmed relationship to upstream): https://github.com/AMD-Ecosystem/colmap

**RealityScan**
- Licensing: https://www.realityscan.com/license
- Hardware/software requirements: https://dev.epicgames.com/documentation/realityscan/hardware-and-software-requirements?lang=en-US
- CLI overview: https://rshelp.capturingreality.com/en-US/tutorials/commandline.htm
- Headless mode: https://rshelp.capturingreality.com/en-US/tutorials/headless.htm
- All CLI commands: https://rshelp.capturingreality.com/en-US/appbasics/allcommands.htm
- Model export: https://rshelp.capturingreality.com/en-US/tools/export.htm
- Texturing settings: https://rshelp.capturingreality.com/en-US/tools/texturing_part2.htm
- LOD export: https://rshelp.capturingreality.com/en-US/tools/lodexport.htm
- 2.2 AMD GPU support announcement: https://www.realityscan.com/news/realityscan-2-2-is-here-with-full-amd-gpu-support-download-today
- 2.2 AMD coverage (full arch/model list): https://peterfalkingham.com/2026/06/25/realityscan-2-2-finally-brings-amd-gpu-support-to-windows/
- 2.2 AMD coverage (secondary): https://www.cgchannel.com/2026/06/epic-games-releases-realityscan-2-2-with-amd-gpu-support/
- 2.1 release notes: https://www.cgchannel.com/2025/11/epic-games-releases-realityscan-2-1/
- 2019 desktop benchmark (dated, object-scale, not aerial — explicitly caveated in §2.5): https://peterfalkingham.com/2019/05/17/photogrammetry-testing-reality-capture-commercial-software/

**DJI SRT format**
- https://terasor.com/articles/what-are-srt-and-lrf-files
- https://swyvl.io/blog/how-to-map-dji-drone-video-gps/

**Cloud pricing**
- AWS G5: https://aws.amazon.com/ec2/instance-types/g5/ · https://cloudprice.net/aws/ec2/instances/g5.xlarge
- Azure NVadsA10 v5: https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/gpu-accelerated/nvadsa10v5-series

**Local repo (read, not re-cited inline above)**
- `docs/research/p1-toolchain.md` §1.1, §2.1
- `docs/research/p1-fast-recon.md` §1, §4, §5
- `docs/research/p1-cloud-world-models.md` §1
- `docs/research/p1-quality-runs.md`
- `pipeline/sfm.py`, `pipeline/mvs.py`
