# Prior hackathon video/photo → 3D pipelines: what they actually did, and what we can reuse

Scope: six prior hackathon projects claimed as prior art in the plan's §7 (audited there 2026-09-24,
shallow: Devpost + a glance at three repos). This pass re-audits all six from the Devpost write-ups
*and* clones every linked repo (six repos total — three the plan already named, plus three found via
each project's own Devpost page: `dylshukla/hackprinceton2024` for Firefighter SLAM,
`eneief/NTR-AR` for NTR-AR, `philip-chen6/vima` for VIMA) and reads the actual code, not just
READMEs. Every claim below is tagged **[code]** (verified by reading the cloned repo),
**[writeup]** (Devpost's own claim, not independently confirmed), or **[inferred]** (my reasoning
from the two, not directly stated by either).

**TL;DR: none of the six is a reusable video → metric 3D pipeline for us — confirms plan §7's
top-line verdict — but the *repos* disagree with their own *Devpost write-ups* far more than the
plan's shallow audit caught, in specific, confirmed ways (below). The most useful takeaways:**

1. **Two teams independently hit the exact wall our own `p1-fast-recon.md` documented**:
   SkySplat's own postmortem says SuGaR mesh refinement burned hours for marginal gain on a 40-second
   clip **[writeup]**; VIMA built a working COLMAP-pose + monocular-depth dense-cloud tool but never
   trusted it enough to ship it as the live demo, shipping a 1,770-point sparse cloud instead
   **[code]**. Both are independent corroboration — from teams with no connection to this project —
   that per-scene-optimized splat training and monocular-depth-as-geometry are real time/quality
   traps at hackathon timescales, the same conclusion our own research already reached.
2. **The upload-bandwidth lever we already planned for (§5.3 of `p1-fast-recon.md`: compress
   before uploading, don't ship raw 4K video) is exactly what Worldly's `create_world.py` does**
   (ffmpeg-compress to ≤10 MB / ≤1800 frames before any cloud call) **[code]** — independent
   confirmation it's a real, common hackathon constraint, not an over-cautious assumption.
3. **CORS on remote splat/asset URLs is a real, documented pitfall**, hit twice inside one repo
   (Worldly's teleporter README calls it out directly, and their portal web app routes around it
   with a public CORS proxy) **[code]** — worth a one-line check for our own `/scenes` static file
   server if any browser-based tool ever fetches it cross-origin.
4. **Devpost write-ups oversell their repos in specific, confirmed ways** for three of the six
   (NTR-AR's "RoomPlan" claim, Worldly's "Node.js and Flask" backend claim, VIMA's shipped-cloud
   framing) — see §"Corrections to plan §7" and each project's "repo vs. claim" note. Treat any future
   "prior art" claim from a Devpost page as unverified until the repo is actually read.

Two repos had reusable *code* worth a closer look for us (both already flagged by plan §7, now
confirmed line-by-line): Worldly's WebXR splat viewer (`teleporter/`) and Iron World's VGGT chunking
glue (`src/recon/vggt_pipeline.py`). Neither repo has a LICENSE file, so both are **ideas/patterns
only, no code copying** (default "all rights reserved"). NTR-AR is the only repo with a real license
(MIT) but has nothing reconstruction-related worth reusing.

---

## Comparison table

| Project | Input | Method | Compute & time/scene | Output | Display | Metric? | Reuse verdict |
|---|---|---|---|---|---|---|---|
| **SkySplat** (Hoponga/splatting) | Parrot drone video via Olympe SDK, web-app-triggered capture **[writeup]** | COLMAP poses → custom PyTorch 3D Gaussian Splatting, one model/scene; SuGaR tried for mesh, dropped **[writeup]**; **repo has zero reconstruction code** **[code]** | Google Cloud VM, CUDA T4, free credits **[writeup]**; no time figure for splat training itself; SuGaR: "multiple hours" for a 40 s clip **[writeup]** | `.ply` Gaussian splat per scene **[writeup]** | Custom web app, three.js/WebGL via gsplat.js — **not VR, not a headset** **[writeup]** | No | **SKIP** — nothing to copy (no code exists); lesson-only: SuGaR is a time trap, corroborates our own findings |
| **Worldly** | Drone video, captured on iOS via ReplayKit screen-record of the DJI Fly controller app (DJI SDK iOS-unavailable workaround) **[code]** | World Labs Marble API (`worlds:generate`, video mode), fully hosted/generative — no local reconstruction at all **[code]** | 100% World Labs' cloud; **[code]**: `poll_until_done` comment says "~5 minutes"; ffmpeg pre-compresses to ≤10 MB/≤1800 frames before upload | Hosted Marble "world" (splat-based world model) at a `marble.worldlabs.ai/world/<id>` URL; no local files **[code]** | Two paths: (1) a real from-scratch WebXR viewer (`teleporter/`, three.js + `@sparkjsdev/spark` + Meta IWSDK) loading `.spz/.ply/.splat/.ksplat` **[code]**; (2) a Unity "portal" that just opens the Marble URL in Oculus Browser, no in-headset render **[code]** | Marble API reportedly returns `metric_scale_factor` (per `p1-fast-recon.md`'s own research, not re-verified this pass), but `create_world.py` never reads/uses it **[code]** | **BORROW the teleporter pattern (idea only, no LICENSE)**: Spark `SplatMesh`+`VRButton` setup, its splat auto-recenter/fit-scale logic, and the ffmpeg-pre-compress-before-upload pattern |
| **Firefighter SLAM** | Devpost claims monocular helmet-cam footage → SLAM point cloud **[writeup]**; **repo has no footage, no SLAM/COLMAP code anywhere, either branch** **[code]** | `main` branch: 2 MATLAB scripts — 2D temperature-peak plotting from a missing `firedata.xlsx`, and a synthetic random-point "heat" mesh via `griddata` (fabricated `2000*cos(r)/r+400` field, not real sensor data) **[code]**. `master` branch: Unity VR shell with one static bundled `compressedroom.obj`, no capture pipeline behind it **[code]** | N/A — nothing runs a reconstruction | MATLAB plots + a static pre-made Unity room asset **[code]** | Unity VR (bundled Oculus SDK, joystick locomotion, waypoint-follow camera) **[code]** | No | **SKIP**, confirmed more decisively than plan §7 implied — zero working reconstruction of any kind, real or fake-but-functional |
| **NTR-AR** | Devpost claims "scan rooms via AR and RoomPlan" **[writeup]**; **repo uses plain ARKit `ARWorldTrackingConfiguration` + horizontal-plane detection — `RoomPlan` is never imported or referenced anywhere (0 grep hits)** **[code]** | No 3D reconstruction in the app at all: tap-to-place 3 fixed local USDZ furniture models on a detected plane **[code]**. The one relevant network call, `processRoomScan()`, is **defined but never called** anywhere in the app **[code]**; the paired Lambda just does an S3 `GetObject` and logs the string — no scan processing **[code]** | N/A — no pipeline executes | N/A | ARKit/RealityKit on iPhone, no headset | No | **SKIP** — nothing 3D-reconstruction-related to borrow; only genuinely reusable piece is a clean, unrelated MIT-licensed Terraform S3+Lambda+API-Gateway skeleton, not needed by our architecture |
| **Iron World** | Construction POV video, mp4/dir/image, real footage **[code]** | VGGT-1B feed-forward, `src/recon/vggt_pipeline.py`: stride 2, ≤180 frames, 60-frame windows, resize to 518×518 (nearest multiple of 14) → `point_map`/`extrinsics`/`intrinsics`/`depth` `.npy` + `manifest.json`, **no meshing in this script** **[code]** | HiPerGator (Univ. of Florida HPC), Slurm-scheduled **B200-class GPU nodes**, hardcoded paths under `/blue/cis4914/jietao/ironsite` **[code]**; no per-scene time figure found | Raw point maps + poses per chunk (`.npy`/`manifest.json`), no mesh from this script **[code]** | `src/dashboard/`: a real Flask+three.js viewer that loads `point_map.npy`/`depth.npy`/`intrinsics.npy`/`extrinsics.npy` and renders with `THREE.Scene`/`WebGLRenderer` **[code]**. Separately, `IronWorld-Unity/` is a **live Quest 3 Scene-API + Gemini wayfinding app that does not consume the VGGT output at all** **[code]** | No scale calibration in `vggt_pipeline.py` **[code]** | **BORROW the VGGT chunking glue (idea only, no LICENSE)** — stride/window/max-frames/resize-to-14-multiple pattern, `open3d`+`trimesh` present in `requirements.txt`. New finding: `scripts/recon_compare/` benchmarks 19 reconstruction methods (VGGT, Pi3, DUSt3R, MASt3R, CUT3R, COLMAP, GLOMAP, etc.) — not read this pass, worth a skim later |
| **VIMA** | Real "hardhat/bodycam-style masonry footage" **[writeup]** | COLMAP poses feed **two separate downstream products [code]**: (1) `tools/dense_cloud_gen.py` — DepthAnythingV2-Small monocular depth (CPU) unprojected via COLMAP poses into a dense colored `.ply`; (2) real 3D Gaussian Splats **trained with Brush** (`masonry-splat-10k.ply` = 22,553 splats, `masonry-splat-30k.ply` = 62,783 splats, SH-degree-3 PLY headers confirmed). **Neither is what's shown live** — the shipped `/demo` page loads `sparse.ply`, COLMAP's raw 1,770-point sparse cloud **[code]** | DepthAnythingV2 hardcoded to CPU in the script; no time figures found; Brush training location/duration not found this pass | Trained but unused: 2 real Gaussian Splat `.ply` files. Shipped: 1,770-point sparse COLMAP cloud **[code]** | React-Three-Fiber + three.js `PLYLoader`+`OrbitControls`, explicitly chosen over `@react-three/drei` to avoid an unneeded 200 KB dep (stated in their own code comments) **[code]**; a `@mkkellogg/gaussian-splats-3d` splat viewer component exists but is dead code, disabled on purpose **[code]** | No | Nothing to copy (unlicensed, reconstruction is secondary to their product), but their COLMAP+monocular-depth dense-cloud pattern and their "don't add a dependency you don't need" viewer choice are small reference data points |

---

## Per-project detail

### 1. SkySplat (Hoponga/splatting, TreeHacks 2024 Sustainability Grand Prize)

- **Capture [writeup]**: Parrot drone (not DJI), controlled/simulated via Parrot's Olympe SDK on
  Ubuntu; a web app starts/stops a recording session, which is sent to a Google Cloud backend.
- **Method [writeup]**: video split into frames → COLMAP for pose recovery → a custom PyTorch 3D
  Gaussian Splatting implementation (from the original 3DGS paper), one model trained per scene, on
  Google Cloud VMs with CUDA T4 GPUs (free-credit constrained). SuGaR was tried for mesh refinement
  and dropped: "long training times and diminishing marginal returns... multiple hours to produce a
  more refined mesh... given a 40-second video."
- **Repo reality [code]**: 5 commits total (`git log --all` after un-shallowing: `fb191a5`,
  `8c00b4d`, `793682d`, `800c4b3`, `b159f73` — matches plan §7's "5 commits" exactly). Contents:
  `frontend/treehacks/treehacks.py` — a Reflex file-upload skeleton, "Start capture"/"End capture"
  buttons only `console.log`, not wired to anything; `gsplat/` — an unmodified Vite+TypeScript
  starter template (`counter.ts` etc.), no splat rendering code; `gsplat/app.py` — a 24-line Flask
  app with exactly one route, `/api/hello`, returning `{"message": "hola"}`; unused imports for
  `scp`/`paramiko`; `utils/upload.py` — a real, functional but standalone Google Cloud Storage
  upload helper, never called by anything else in the repo. **No COLMAP invocation, no Gaussian
  Splatting training code, no SuGaR usage anywhere in the repo.**
- **Committed secret, confirmed [code]**: `utils/exemplary-fiber-414612-c94072190382.json` is a real
  Google Cloud service-account key (`"type": "service_account"`, project `exemplary-fiber-414612`,
  service account `storageacct@exemplary-fiber-414612.iam.gserviceaccount.com`, non-empty
  `private_key` field present). Added in commit `793682d` ("add initial bucket code") and still
  present in the working tree at `HEAD`. I did not print or copy the key material itself — only
  confirmed its existence, type, and location, as instructed.
- **License**: no `LICENSE` file anywhere → default all-rights-reserved. Moot here since there's no
  reconstruction code to copy regardless.
- **Reuse verdict**: **SKIP**, matches plan §7. The one real value is the honest postmortem
  (SuGaR = a time sink for marginal gain), independent corroboration of `p1-fast-recon.md`'s §3
  finding that per-scene splat-training/mesh-refinement methods don't fit a hackathon's time budget.

### 2. Worldly (TreeHacks 2026)

- **Capture [code]**: `footage_extractor/` is a real SwiftUI iOS app using `ReplayKit`'s broadcast
  extension (`BroadcastPickerView.swift`, Darwin-notification IPC to detect when a screen recording
  finishes) to screen-record the DJI Fly controller app — the workaround Devpost describes ("DJI SDK
  only supported Android... forced us to pivot toward manual drone control with ReplayKit screen
  recording"). It trims the first 5 seconds, exports via `AVAssetExportSession`, and uploads the
  `.mov` via a raw multipart POST to `https://vihaan.ayush.digital/generate-worldvr`.
- **Method [code]**: `world-models/create_world.py` calls the real World Labs Marble API
  (`https://api.worldlabs.ai/marble/v1`). For video input it ffmpeg-compresses first
  (`libx264, crf 30, -frames:v 1800, -fs 10M` — i.e., hard caps at ≤1800 frames and ≤10 MB before
  any upload), uploads to a signed URL, calls `worlds:generate` with a large generative-wishlist text
  prompt (geometry, PBR materials, lighting reconstruction, camera path — describes *desired*
  output, not a guarantee of what the model actually recovers), then polls
  `operations/{id}` every 15 s. The code's own print statement says "world generation can take
  ~5 minutes" — more precise than, and not contradicting, plan §7's "polls 5–15 min" (I found no
  15-minute figure anywhere in the repo; the one hard number in code is ~5 min).
- **Output/display [code]**: the API returns a hosted `marble.worldlabs.ai/world/<id>` URL (and a
  `/worldvr/<id>` variant) — no local mesh/splat file is produced by this pipeline at all except the
  one bundled demo asset (`teleporter/public/splats/demo.spz`) used for local viewer testing.
  Two display paths exist in the repo: (1) `teleporter/src/index.ts` — a genuine, working WebXR
  viewer: three.js + `@sparkjsdev/spark` (`SplatMesh`, `VRButton`, `SparkControls`) +
  `@iwsdk/core` (Meta Immersive Web SDK) + `vite-plugin-mkcert` (local HTTPS, required for WebXR) +
  `vite-plugin-iwer`. It accepts `.spz/.ply/.splat/.ksplat` via a file picker
  (`SplatFileType` enum covers all four — confirms plan §7's claim exactly), auto-recenters/scales
  the splat to fit a 900 m target max dimension (clamped 0.2–300×), and supports thumbstick
  locomotion in XR (confirmed in its own README). An older, abandoned Next.js viewer
  (`old-teleporter/`, using `@mkkellogg/gaussian-splats-3d`) was superseded by this one.
  (2) `portal/unity/WorldLauncher.cs` — **not an in-headset splat viewer**: it just populates a
  button list and calls `Application.OpenURL(marbleUrl)`, opening the world in Oculus Browser. Only
  path (1) actually renders a splat inside a headset.
- **Went wrong [writeup, confirmed in code]**: DJI SDK iOS unavailability (confirmed by the
  ReplayKit workaround existing at all); "creating a custom WebXR viewer from scratch became
  necessary due to limited PLY format support" (confirmed by the multi-format
  `inferFileType`/`SplatFileType` logic); CORS on remote splat URLs — confirmed **twice**:
  `teleporter/README.md` explicitly warns "If remote files fail to load, confirm CORS headers allow
  cross-origin fetch," and `portal/web-app/index.html` routes Marble thumbnail fetches through a
  public `corsproxy.io` proxy to work around exactly this.
- **Repo vs. Devpost gap [code]**: Devpost's "How we built it" says "Backend: Node.js and Flask
  servers orchestrating the processing pipeline." The repo contains only a Flask server
  (`world_server.py`, a 33-line wrapper around `create_world`/`poll_until_done`). No Node.js backend
  service code exists anywhere in the repo — `portal/web-app/` is static HTML/JS, not a Node server,
  and no `server.js`/Express/Node entrypoint was found. This is a confirmed, material gap between the
  write-up and the shipped code.
- **License**: no `LICENSE` file anywhere in the repo → all rights reserved → ideas/patterns only.
- **Reuse verdict**: the viewer *pattern* (Spark `SplatMesh`+`VRButton` setup, its recenter/fit-scale
  math, the multi-format file-type inference) is a good, small reference if P2/P4 ever want a WebXR
  fallback demo — re-implement, don't copy (no license). The ffmpeg-pre-compress-before-upload
  pattern independently confirms our own plan's §1a/§5.3 approach (upload frames/small files, not
  raw 1–2 GB video) is a real, commonly-hit constraint. The CORS lesson is a one-line thing to check
  if any browser-based tool ever hits our `/scenes` static route cross-origin.

### 3. Firefighter SLAM / "Immersive Firefighter Performance Review" (HackPrinceton 2024)

- **Devpost claim [writeup]**: monocular helmet-camera footage → SLAM-derived point cloud → 3D mesh,
  with spatially-simulated room temperature overlaid, viewed in VR; MATLAB, Unity3D, and Colmap
  listed as tools used.
- **Repo reality [code]**: two branches, both checked. `main` (the "MATLAB part" per the README)
  contains exactly two files: `livingroom.m`, which reads a temperature-vs-time Excel file
  (`firedata.xlsx`, not present in the repo) and finds local maxima on a 2D curve — no 3D, no point
  cloud; and `threedroomtemp.m`, which generates **100 uniformly random synthetic (x, y) points**,
  computes `z = 2000*cos(r)/r + 400` (a fabricated radial function, not derived from any sensor
  reading), and interpolates a mesh via `griddata` — this is a synthetic demo of MATLAB's 3D plotting,
  not a SLAM-derived reconstruction of anything. `master` (the "Unity VR part") is a Unity project
  with the standard bundled Oculus SDK (AudioManager, sample scripts) plus two small custom scripts
  (`Joystick Locomotion.cs`, `MoveOnWayPoints.cs` — a simple point-A-to-point-B camera mover) and one
  static asset, `compressedroom.obj` — a pre-made room mesh with **no code anywhere in either branch
  that produces this OBJ from any capture pipeline**. Grepping both branches for "colmap"
  (case-insensitive) returns zero hits — COLMAP is a Devpost/README tag, not code.
- **License**: no repo-level `LICENSE` on either branch (only third-party license files bundled
  inside the vendored `Assets/Oculus/` SDK folder, which is not their own code).
- **Reuse verdict**: **SKIP**, confirmed even more decisively than plan §7's summary suggests — this
  isn't "MATLAB point-cloud demos," it's a 2D curve-plotting script, a synthetic-random-point demo,
  and a disconnected static Unity asset with a waypoint-follow camera. There is no working
  reconstruction of any kind, real or fabricated-but-functional, anywhere in this repo.

### 4. NTR-AR (VandyHacks XI)

- **Devpost claim [writeup]**: "scan rooms via AR and RoomPlan, upload scans to cloud storage, and
  retrieve them later."
- **Repo reality [code]**: `ContentView.swift`'s `RealityKitView` uses plain
  `ARWorldTrackingConfiguration` with `config.planeDetection = [.horizontal]`, `ARCoachingOverlayView`,
  and `FocusEntity` — **Apple's `RoomPlan` framework is never imported or referenced anywhere in the
  repo** (grepped case-insensitively for "roomplan" across every file: zero hits). The actual
  behavior: detect a horizontal plane, let the user pick one of three hardcoded furniture names
  ("Bed"/"Dresser"/"Desk"), and tap to place one of three bundled local USDZ models
  (`Asylum_Bed.usdz`, `Dresser.usdz`, `Computer_Desk.usdz`) at a hardcoded per-model scale factor.
  There is no room-scanning, mesh-export, or scan-upload code in the app at all.
  `Networking.swift` defines `NetworkingManager.processRoomScan(bucketName:objectKey:)`, which POSTs
  `{bucketName, objectKey}` to an AWS API Gateway endpoint — but **this function is never called
  anywhere in the app** (grepped for the call site: only the definition exists). The paired Lambda
  (`Terraform-Backend/layer-nodejs/index.js`) does an S3 `GetObject` for the given key and returns/logs
  the string body — it performs no scan processing, meshing, or analysis of any kind. The
  Terraform (`main.tf`) does provision a real private S3 bucket with versioning, an IAM role, the
  Lambda function, and (per file listing) an API Gateway route — clean, working infrastructure code,
  just with no client ever calling into it to actually upload a scan.
- **License [code]**: a real `LICENSE` file, MIT, Copyright (c) 2024 Nafees — the only one of the six
  repos with an unambiguous, permissive license.
- **Reuse verdict**: **SKIP** for reconstruction (there's nothing to borrow — no scanning code
  exists at all, despite the Devpost claim). The Terraform S3+Lambda+API-Gateway skeleton is
  legitimately MIT-licensed and clean, but our plan doesn't use AWS anywhere (laptop-hosted FastAPI
  instead per §1b), so it's a "note, not a recommendation."

### 5. Iron World (Hacktech 2026, `github.com/jiekaitao/ironworld`)

- **Capture [writeup/code]**: real construction-site POV/helmet-cam-style footage ("shaky, blurry,"
  per Devpost). `src/recon/vggt_pipeline.py`'s `_iter_frames` accepts an mp4, an image directory, or
  a single image.
- **Method [code, confirms plan §7 precisely]**: `vggt_pipeline.py` loads **VGGT-1B**
  (`load_vggt()` pulls from `models/vggt_1b_commercial/` — its own docstring/comment flags a real
  nuance: "we stored under this name even though the commercial variant was access-restricted; this
  is the research variant," i.e. they are actually running the CC-BY-NC-4.0 research checkpoint, not
  the gated commercial one, despite the directory name). `main()`'s own argparse defaults are
  `--stride 2`, `--window 60`, `--max-frames 180`, `--max-size 518` — matching plan §7's "stride 2,
  60-frame chunks, ≤180 frames, resize ≤518 px" exactly. Frames are resized to a multiple of 14
  (VGGT's patch size) and run through the model in windows of ≤60, producing per-chunk
  `point_map`/`extrinsics`/`intrinsics`/`depth`, saved as `.npy` object arrays plus a `manifest.json`
  recording source/frame-count/stride/window/max-size — matches plan §7 exactly. This script does
  **not** mesh the output (meshing/fusion, if any, lives elsewhere in `src/recon/`, e.g.
  `build_3d_memory.py`/`classify_scene_pc.py`, not read further this pass — time-boxed).
  A separate detection/analysis stack (OWLv2 + SigLIP, multi-frame confidence voting, region
  scoring by coverage/framing/angle/occlusion/sharpness) sits on top but is not reconstruction.
- **Where it ran [code]**: `CLAUDE.md` line 31: "Slurm jobs for the heavy GPU work live in
  `scripts/slurm/*.sbatch` (**B200 nodes**)." Hardcoded paths throughout point at
  `/blue/cis4914/jietao/ironsite` — this is University of Florida's **HiPerGator** HPC cluster
  (`ssh -L 8080:<node>:8080 jietao@hpg.rc.ufl.edu` per the README), Slurm-scheduled onto B200-class
  GPU allocations. Plan §7's "ran on B200 nodes with hardcoded cluster paths" is **confirmed
  accurate**; the added precision is that it's specifically UF's HiPerGator Slurm cluster.
  No per-scene time figure was found anywhere in the reconstruction scripts this pass.
- **Display [code]**: `src/dashboard/server.py` genuinely loads `point_map.npy`, `depth.npy`,
  `intrinsics.npy`, `extrinsics.npy` from a recon output directory, and `src/dashboard/static/index.html`
  renders them with real three.js (`THREE.Scene`, `PerspectiveCamera`, `WebGLRenderer`,
  `GridHelper`) — confirms plan §7's "three.js point-cloud player" claim precisely.
  Separately, `IronWorld-Unity/` (13 custom scripts) is a **live, on-device Quest 3 app**: it uses
  Meta's Scene API for room mesh (`RoomDiorama.cs`, `SceneMeshBootstrap.cs`), Gemini for live
  passthrough-camera object detection (`GeminiObjectDetector.cs`), and voice-driven wayfinding with
  a glowing path (`NavigationSystem.cs`, `VoiceQuery.cs`) — **this Unity app does not consume the
  VGGT `.npy`/`manifest.json` output at all**; no loader for those files exists under
  `IronWorld-Unity/Assets`. It's an independent, unrelated live-Scene-API demo, not the reconstruction
  pipeline's viewer.
- **Metric**: no scale calibration logic found in `vggt_pipeline.py`.
- **Went wrong [writeup]**: heavy pivoting — SAM 3.1 underperformed on construction footage,
  unsupervised clustering abandoned for data inconsistency, "finding the right primitives to anchor
  ourselves in space is definitely the toughest part." Consistent with the repo's sprawl: a newly
  found **19-method reconstruction bake-off**, `scripts/recon_compare/` (`run_vggt.py`, `run_pi3.py`,
  `run_dust3r.py`, `run_mast3r.py`, `run_cut3r.py`, `run_monst3r.py`, `run_spann3r.py`,
  `run_must3r.py`, `run_streamvggt.py`, `run_noposplat.py`, `run_anysplat.py`, `run_mapanything.py`,
  `run_moge2.py`, `run_da3.py`, `run_colmap.py`, `run_glomap.py`, `run_easi3r.py`,
  `run_splatt3r.py`, `run_pow3r.py`) — file listing confirmed **[code]**, contents not read this pass
  (time-boxed; this is close to a superset of the exact model landscape `p1-fast-recon.md`
  researched independently, and is a real leftover worth a skim later, not built here).
- **License**: no `LICENSE` file anywhere → ideas only.
- **Reuse verdict**: **BORROW the VGGT chunking glue**, as plan §7 already says, now confirmed
  line-by-line — its stride/window/max-frames/resize pattern is structurally close to our own
  `pipeline/vggt.py`'s ">64 frames evenly subsampled" handling, a useful independent sanity check
  that our own chunking choices are reasonable (not a new technique to adopt). `open3d`+`trimesh`
  confirmed present in `requirements.txt`, matching plan §7. New: `scripts/recon_compare/` is worth
  a follow-up skim for any single alternative method's runner-script pattern.

### 6. VIMA (Hacktech 2026, `github.com/philip-chen6/vima`)

- **Capture [writeup]**: real "hardhat/bodycam-style masonry footage."
- **Method [code, richer than plan §7's summary]**: COLMAP poses (`frontend/public/reconstruction/colmap/sparse/{cameras,images,points3D}.bin`)
  feed two separate, real downstream products:
  1. `tools/dense_cloud_gen.py` — reads COLMAP's `images.bin` directly (hand-rolled binary parser),
     runs each frame through **DepthAnythingV2-Small** (`transformers` pipeline,
     `depth-anything/Depth-Anything-V2-Small-hf`, hardcoded `device="cpu"`), unprojects depth pixels
     into world space via the COLMAP pose, accumulates points across all frames, drops points beyond
     the 95th-percentile distance from the median (crude outlier rejection), and writes an ASCII
     `.ply`. This is functionally the same "monocular depth as scale/geometry" approach our own
     `calibrate.py`/`p1-fast-recon.md`/`p1-gpu-rocm.md` tested (MoGe-2, Depth-Anything-V2) and ranked
     as unreliable — independent, real-world confirmation the same idea gets tried elsewhere, not a
     new technique for us to adopt.
  2. Real **3D Gaussian Splats trained with Brush** (`ArthurBrussee/brush` — the same tool named in
     our own plan's §7 tool table): `frontend/public/reconstruction/masonry-splat-10k.ply` and
     `masonry-splat-30k.ply`. Verified from the binary PLY headers: `comment Exported from Brush`,
     `comment SH degree: 3`, `element vertex 22553` (the "10k" file) and `element vertex 62783` (the
     "30k" file), with `f_dc_0/1/2` + `f_rest_0..44` spherical-harmonics properties — these are
     genuine trained Gaussian Splat assets, not raw point clouds. (The "10k"/"30k" names appear to be
     rough labels, not literal splat counts — actual counts are 22,553 and 62,783.)
  - **None of this is what ships live.** `frontend/app/demo/demo-client.tsx` (line 480) loads
    `src="/reconstruction/sparse.ply"` on the actual `/demo` page. That file's own binary PLY header
    reads `element vertex 1770` with only x/y/z + RGB properties (no SH data) — COLMAP's raw sparse
    SfM point cloud. This matches plan §7's "1,770-point COLMAP cloud" claim **exactly** for what's
    shown, but plan §7 is incomplete: the repo also contains a working dense-cloud tool and two real
    trained splat exports, deliberately not wired into the shipped demo. A
    `@mkkellogg/gaussian-splats-3d` viewer dependency is present in `frontend/package.json`, but its
    component (`splat-viewer.tsx`) is dead code — its own top-of-file comment: "intentionally unused.
    the demo no longer mounts a faux reconstruction viewer from this module; /demo now uses the
    colmap point-cloud viewer directly."
- **Display [code]**: `point-cloud-viewer.tsx` is a real, custom React-Three-Fiber component using
  three.js's stock `PLYLoader` + `OrbitControls` — their own code comment explains why: "Why not
  `@react-three/drei`: drei is 200KB+ for two helpers we don't need. Three core ships PLYLoader +
  OrbitControls." It also draws a small wireframe camera-frustum per pose (from `cameras.json`) for
  provenance/evidence, matching their stated "evidence first, answer second" product philosophy.
- **Went wrong [writeup]**: "raw VLM answers were often plausible but ungrounded"; pressure to unify
  many working components (CII productivity, spatial zones, temporal memory, masks, depth, agents, a
  Solana payment layer under `mobile/`) into one coherent story. Reconstruction here is explicitly a
  small "evidence" layer, not the product's core — consistent with why the more polished splat
  assets exist but were deliberately left out of the shipped demo in favor of a simpler, more
  obviously-trustworthy sparse cloud.
- **License**: no `LICENSE` file anywhere → ideas only.
- **Reuse verdict**: nothing to copy (unlicensed; and their own reconstruction is secondary/evidence-
  only for them, same conclusion as the plan). Two small reference points, not techniques to adopt:
  their COLMAP-pose + off-the-shelf-monocular-depth dense-cloud pattern (confirms this class of
  method is genuinely usable for a *rough, non-metric* preview, just not one we should trust for
  scale — matching our own already-made decision), and their explicit "don't add drei for two
  helpers" dependency discipline.

---

## Corrections to plan §7

Plan §7's table is directionally right for all six ("none gives a video → metric splat + mesh
pipeline") but has these specific gaps, now checked against the actual code:

1. **NTR-AR — real correction, not just refinement.** Plan §7 repeats Devpost's framing
   ("iOS RoomPlan scans → S3 via Terraform") without flagging that the repo **never uses RoomPlan at
   all** (0 references anywhere) and the one network call that would upload a scan
   (`processRoomScan`) is **defined but never invoked** in the app. The actual app is static
   ARKit-plane-detection furniture placement with 3 fixed USDZ models. Plan §7 should note this gap
   explicitly rather than repeating the Devpost claim as if it described the shipped code. Also
   worth adding: NTR-AR is MIT-licensed, the only one of the six with a clear permissive license
   (moot for reuse here since there's nothing reconstruction-related to take).

2. **Worldly — one refinement, one new gap.** (a) "polls 5–15 min" should read "code's own comment
   says ~5 min" — I found no 15-minute figure anywhere in the repo; not a contradiction, just more
   precise than the range plan §7 gives. (b) New: Devpost claims a "Node.js and Flask" backend; the
   repo only contains a Flask server (`world_server.py`) — no Node.js backend service code exists
   anywhere in the repo. Worth flagging as a Devpost-vs-repo gap plan §7 didn't catch.

3. **Iron World — accurate, add precision.** "ran on B200 nodes with hardcoded cluster paths" is
   confirmed correct; worth adding that this is specifically University of Florida's HiPerGator
   Slurm cluster (`/blue/cis4914/jietao/ironsite`, `hpg.rc.ufl.edu`), in case anyone later needs to
   reason about reproducing or accessing it. Also new, not in plan §7 at all: `scripts/recon_compare/`
   is a 19-method reconstruction bake-off (VGGT, Pi3, DUSt3R, MASt3R, CUT3R, MonST3R, Spann3R,
   MUSt3R, StreamVGGT, NoPoSplat, AnySplat, MapAnything, MoGe2, Depth-Anything-3, COLMAP, GLOMAP,
   Easi3R, Splatt3R, Pow3R) — undiscovered by the shallow audit, worth a skim later (not done this
   pass, time-boxed).

4. **VIMA — accurate but incomplete.** "the only 3D is one pre-baked 1,770-point COLMAP cloud" is
   correct for what's *shown live*, but the repo also contains a working dense-cloud tool
   (COLMAP + DepthAnythingV2) and two real trained Gaussian Splat exports (Brush, 22,553 and 62,783
   splats) that were built and then deliberately left out of the shipped demo. Worth adding this
   nuance — it changes the "reuse ideas" available (their dense-cloud script is a legitimate
   non-metric-preview reference; the plan's summary implies there's nothing at all beyond the sparse
   cloud).

5. **SkySplat and Firefighter SLAM — no correction needed**, both independently confirmed accurate
   (5 commits and a real committed GCP key for SkySplat; zero SLAM/reconstruction code on either
   branch for Firefighter SLAM) — if anything, Firefighter SLAM's repo is even more disconnected
   than plan §7's summary suggests (the "point-cloud demo" is a synthetic-random-point plotting
   script, not derived from any capture).

---

## Sources

All fetched/cloned 2026-09-24.

**Devpost write-ups**
- SkySplat: https://devpost.com/software/skysplat
- Worldly: https://devpost.com/software/worldly-0tm3bl
- Immersive Firefighter Performance Review (VR + SLAM): https://devpost.com/software/firefighter-slam
- NTR-AR: https://devpost.com/software/ntr-ar
- Iron World: https://devpost.com/software/iron-world
- VIMA: https://devpost.com/software/vima

**Repos cloned shallowly and read directly (not just READMEs)**
- SkySplat: https://github.com/Hoponga/splatting (then `git fetch --unshallow` for full commit history)
- Worldly: https://github.com/vihaanm23/worldly
- Firefighter SLAM: https://github.com/dylshukla/hackprinceton2024 (both `main` and `master` branches — repo URL only found via the Devpost page, not previously in the plan)
- NTR-AR: https://github.com/eneief/NTR-AR (repo URL only found via the Devpost page, not previously in the plan)
- Iron World: https://github.com/jiekaitao/ironworld
- VIMA: https://github.com/philip-chen6/vima (repo URL only found via the Devpost page, not previously in the plan)

**Local repo (read for context, not re-cited inline)**
- `docs/AirTool implementation plan.md` §1, §1a, §2, §7, §7b
- `docs/research/p1-fast-recon.md`
