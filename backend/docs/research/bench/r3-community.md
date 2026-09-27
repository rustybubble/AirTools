# R3: What practitioners, hackathon teams and social media actually do (2024 to Sep 2026)

Agent R3, 2026-09-25. Scope: how people get fast, complete, detailed reconstructions from phone or
drone video in practice, as opposed to what papers claim. The target is the kitchen-0095 bench
(`../p1-recon-bench.md`): 87 s of DJI Mini 4K, 4K30, low light (ISO up to 2000, 1/30 s), with no GPS.
The current full pipeline takes 34 min (27 min of it in densify) and covers 70%. The preview takes 67 s.
Compute is one RX 9070 XT (ROCm, no CUDA).

## 0. How to read this

**Evidence tags**

- **[numbers]**: the source posts timings or metrics.
- **[anecdote]**: a practitioner's claim with no numbers.
- **[vendor]**: the tool's own author or company.
- **[our calc]**: our own arithmetic.
- **[unverified]**: could not be checked, or comes from a single low-reputation source.

Reddit scores and comment counts come from the Arctic Shift archive at the time of capture, so they
are lower bounds.

**Access limits this session**

- **X/Twitter** needs a login (x.com redirects to onboarding, and xcancel returns HTTP 451). We
  created no account, so X is covered only second-hand, through radiancefields.com news and posts
  that people cross-posted to Reddit or HN.
- **YouTube** captions came back empty, so we used titles, dates and descriptions only.
- **Devpost** was read through its own JSON search in a headless browser.
- **Reddit** was read through the Arctic Shift archive API, because reddit.com blocks bots.
- **The session's WebSearch budget ran out** early (a shared limit), so discovery went through the
  Reddit archive, HN Algolia, the GitHub API, Devpost and YouTube search instead.
- **The r/drones, r/dji and r/computervision coverage is thin.** Those queries kept timing out on
  the archive, and the hits we did get were about outdoor mapping and orthomosaics, not
  video-to-mesh.

## 1. TL;DR

1. **Frame selection is the cheapest large win, and everyone serious does it.** No practitioner
   pipeline we found uses blind fixed-fps extraction any more. They either pick the sharpest frame
   per time window, or pick keyframes adaptively from optical flow and camera motion. Our footage is
   1/30 s in low light, so motion blur is the dominant quality killer. [our calc]: at 4K, about 18 px
   of blur for 0.5 m/s at 2 m, and about 25 px for a 20°/s yaw. Sharpest-in-window selection
   directly attacks both detail and registration rate.
2. **Pin the intrinsics.** One hackathon team (Orbis Engine, Hack the North 2026, repo read) went
   from **3 of 550 to 550 of 550 frames registered by fixing the focal length** [numbers]. An
   r/photogrammetry user measured a **12 dB PSNR collapse** when GLOMAP silently drifted the focal
   length to fx/fy = 2.36 on well-calibrated input [numbers]. COLMAP 4.2's global mapper is
   GLOMAP-derived, so our 70% coverage may partly be a self-calibration problem, not only a
   capture problem.
3. **Practitioners with AMD cards are moving to Vulkan or HIP tools**, which removes our "no CUDA"
   wall:
   - **Spirula Studio** (Vulkan, GPL-3.0, Linux binary). It covers video → GPU SfM → depth and
     normal priors → splat → textured mesh, and reads telemetry for metric scale. It has the
     strongest community signal of anything found: a 163-point launch post and many "switched to
     Spirula" comments. One user reported going from 17 h to under 2 h [anecdote].
   - **Cheshire**, a HIP port of AliceVision/Meshroom. It is validated on an **RX 9070 with ROCm
     7.2**: DepthMap takes about 3.0 s per view, and GPU SIFT took 2021 s → 26 s [vendor, numbers,
     4 stars].
   - Either could replace the 27-minute CPU OpenMVS densify.
4. **Blank walls are a capture and prior problem, not a trainer problem.** Real-estate splat
   operators report no gain from 4K over 1080p, from 300 → 1200 frames, or from 30k → 120k steps
   [numbers]. What helps is shooting perpendicular to and down the length of walls, and adding
   depth and normal priors. A 160-point Reddit post shows this with Spirula's depth and normal maps.
5. **No reconstruction recovers what was never filmed.** Orbis measured 0% recorded coverage beyond
   about 41° off a forward walking path [numbers]. The fixes they tried:
   - TSDF hole-filling of masked depth failed.
   - Generative fill works visually. Marble plus a gsplat fine-tune took held-out PSNR from
     9.96 to 33.09 dB. But Marble's scale was about 25% off, so it is not metric.

   For coverage, the capture path matters more than the algorithm.
6. **Hackathon teams that shipped video-to-3D in 24 to 36 h used the same stack as ours**: a
   feed-forward model (VGGT, Pi3X or MASt3R) or COLMAP/GLOMAP for poses, then gsplat, Brush or
   OpenSplat, viewed in Spark/three.js or WebXR. None shipped a textured metric mesh. The only
   end-to-end timings are:
   - Vora Earth: under 60 s on a Modal L4 [writeup].
   - EasySplat: 21 min → 3 m 14 s for a 250-frame drone video on Apple Silicon [writeup].
   - Beacon: VGGT-Ω post-processing cut from 14.1 s to 4.4 s by caching depth and checking
     cross-view agreement at half resolution [writeup].
7. **The "video versus photos" split is real.** Photogrammetry veterans say video is "atrocious" and
   that photos carry "a factor of ten in detail" [anecdote]. Splat people say "shitty but very
   densely captured" video works fine [anecdote]. For a mesh deliverable, the next capture should use
   interval photos or a fast shutter. The current clip can only be rescued by frame selection.

## 2. Findings by theme

### 2.1 Frame extraction and selection

| Finding | Evidence | Source (date) |
|---|---|---|
| Sharpest frame per block, scored with Tenengrad (Sobel energy). `--every SECONDS` or `--count N`, CPU, `pip install sharp-frame-extractor` or run with `uvx`. MIT. | [vendor] | github.com/cansik/sharp-frame-extractor (updated 2026-01) |
| Reflct "Sharp Frames": best-N sharpest frames spread across the clip (`--selection-method best-n --num-frames 300 --min-buffer 3`), with a TUI. It is the de facto community default ("I usually use sharp-frames.reflct.app… reduces image count and gets mostly sharp frames"). License NOASSERTION. | [anecdote], 212★ | github.com/Reflct/sharp-frames-python; https://reddit.com/r/GaussianSplatting/comments/1vqpd97/ (2026-08-17) |
| Motion-aware keyframes: dense DIS optical flow on a point grid, with a new keyframe when motion, tracked-point loss or a maximum interval crosses a threshold. It writes a CSV of the reasons. Python CLI (MIT) and a C++ GUI. No benchmark against fixed fps yet; blur rejection is "high on the list". | [vendor] | github.com/morishuz/frame-extractor, github.com/morishuz/adaptive-frame-extractor; https://reddit.com/r/photogrammetry/comments/1vqrhv4/ (2026-08-17, 67 pts), https://reddit.com/r/photogrammetry/comments/1w77cpe/ (2026-09-04) |
| Meshroom's `KeyframeSelection` node is used as a video front end by some. | [anecdote] | same thread 1vqpd97 |
| "6M splats and 1652 frames for one room might be too much… limit frames to 500–800 and use Sharp Frames." | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1tm9h69/ (2026-05-24) |
| "Pictures are too close to each other. No need to use every frame." This was the diagnosis for an indoor-room mesh that came out as "nothingness". | [anecdote] | https://reddit.com/r/photogrammetry/comments/1kgdjq7/ (2025-05-06) |
| Replica (Ambiens, macOS): from a 45 s 8K handheld video (1,345 frames), 160 frames were used for photogrammetry. The solved poses and sparse cloud then anchored tracking of all 1,345 frames. This is keyframes for geometry plus densification of cameras afterwards. | [vendor] | https://reddit.com/r/photogrammetry/comments/1wjtvyf/ (2026-09-18) |
| Ledge (MarinHacks 2026): video scored for blur, exposure and overlap, cut to 60–150 sharp, non-redundant keyframes, then VGGT → COLMAP export → gsplat. | [writeup + repo docs] | https://devpost.com/software/ledge-q0hupn (2026-08-02); github.com/BackendAdam/Ledge `docs/research/cloud/reconstruction.md` |
| Florent Poux's 4-engine benchmark (VGGT, Pi3, DA3, SfM+MVS on a phone video of a white room) uses Laplacian-sharpness keyframe selection before every engine, then shared TSDF fusion. | [video description only; transcript unavailable] | https://youtu.be/__6T6kB5i3Y (2026-08-11) |
| SRT telemetry for frame selection: "drive frame selection from the telemetry instead of fixed-interval extraction… drop redundant hover frames and keep a healthy baseline." A 2024 Mini 4 Pro user filtered frames by the SRT distance between them. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1uugct0/ (2026-07-12); https://reddit.com/r/photogrammetry/comments/1c3unrf/ (2024-04-14) |
| Spirula v2026.9.20 added adaptive frame extraction, and its GPU video decode is "about 15× faster" than shelling out to ffmpeg. | [vendor] | github.com/harry7557558/spirula-studio releases (2026-09-20) |

**Implication for kitchen-0095.** Our 1 Hz caption track has H.S and V.S (horizontal and vertical
speed). Combining sharpest-in-window with "prefer windows where speed is low" costs a few lines on
top of `sharp-frame-extractor` or plain OpenCV. The practitioner budget for a single room is 300–800
frames. We currently use 2 fps × 87 s ≈ 174, which is at the low end. So the tuning question is
*which* frames, not more frames.

### 2.2 Pose solving and registration (the coverage lever)

| Finding | Evidence | Source (date) |
|---|---|---|
| **Fixing the focal length took SfM from 3 of 550 to 550 of 550 registered frames** (11 s corridor walk, phone video). | [numbers, repo log] | github.com/dtpu/OrbisEngine `docs/research-log.md` (Hack the North 2026, repo HEAD 2026-09-23) |
| **GLOMAP drifted intrinsics** to fx/fy 1849.9/784.8 (should be 508/508) with spurious distortion. PSNR was 16.42 against 28.16 for incremental mapping on the same hloc database, and both runs registered 434 of 434. `--skip_view_graph_calibration 1 --BundleAdjustment.optimize_intrinsics 0` fixed the ratio but locked in the unrefined initial focal length. | [numbers] | https://reddit.com/r/photogrammetry/comments/1t3p2a4/ (2026-05-04) |
| "VGGT and Depth Anything 3 make mid-quality point clouds, but for camera position and calibration they are still bad compared with regular software." VGGT-Omega at 512 px → COLMAP gave a visibly worse splat than RealityCapture poses. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1tm8lx1/ (2026-05-24) |
| "Colmap is probably more robust if you can afford the processing time." VGGT → COLMAP export → splat is a valid path. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1r72pau/ (2026-02-17) |
| Known-pose triangulation: COLMAP `point_triangulator` with fixed poses, plus `model_aligner` for Sim(3) to reference centres, both documented. Ledge's plan was "COLMAP/glomap SfM, then Sim(3)-anchor"; VGGT alone was rejected as up-to-scale. | [repo docs, quoted COLMAP docs] | Ledge repo `docs/research/cloud/reconstruction.md` |
| Chunked VGGT: past about 50 frames on 24 GB it runs out of memory. Chunks stitched with a GTSAM factor graph (DINOv2 loop closure) gave 70% less pose error than naive stitching on TUM/Replica. | [numbers, author] | https://reddit.com/r/GaussianSplatting/comments/1t1bjqo/ (2026-05-02); github.com/jashshah999/vggt-factor-refinement (no license) |
| Sail-Recon: about 400 images in 2–3 min (A6000), .ply and KITTI output, no COLMAP export. FastVGGT is also mentioned. | [numbers, anecdote] | https://reddit.com/r/GaussianSplatting/comments/1n93lhe/ (2025-09-05) |
| Real-estate splat operator's pipeline: 4K room video → ffmpeg sharp PNGs → COLMAP GPU SIFT, exhaustive matching, global mapper → COLMAP PatchMatch plus fusion → seed the trainer from the dense cloud. | [numbers] | https://reddit.com/r/GaussianSplatting/comments/1vk588o/ (2026-08-09) |
| A user whose run took about 17 h (8 h COLMAP plus 8 h LichtFeld) got "a cleaner splat in less than 2 hours" from Spirula's built-in SfM and trainer. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1w0by3s/ (2026-08-28, 163 pts) |
| HN (VGGT launch, 190 pts): one user "uploaded a low quality video of an indoor space and got decent results". Another saw the HF demo miss about half the features. Position accuracy "falls off… dead-reckoning drift" over distance. | [anecdote] | https://news.ycombinator.com/item?id=43470651 (2025-03-25) |
| Orbis: VGGT took 164 s and 15 GB for 20 frames on an M3 Pro (MPS), against about 12 s and 1.7 GB for Depth Anything 3. They then chose Pi3X as their camera and depth solver. | [numbers, Mac-specific] | OrbisEngine `docs/research-log.md` |

**Implication.** Before any new method, check the Mini 4K intrinsics:

- a single shared camera;
- a focal length prior from the known 24 mm-equivalent lens (about 2170 px at 3840 wide);
- possibly no distortion refinement in the global stage.

Compare the registered-frame counts against the current COLMAP 4.2 global run.

### 2.3 Dense reconstruction and meshing on AMD (the speed lever)

| Tool | What people report | Evidence | Source (date) |
|---|---|---|---|
| **Cheshire** (HIP AliceVision) | On an RX 9070 (RDNA4), ROCm 7.2, Linux bundle 124 MB, "bit-identical"-validated against CUDA. DepthMap: 6 views 17.4 s, 41 views 124.4 s (**about 3.0 s/view**). DepthMapFilter 123.5 → 26.5 s. Meshing 510 → 101 s. Texturing 165.8 → 79.3 s (107 photos). GPU SIFT 2021 → 26 s (41 views). Whole 107-photo job 39 → about 30 min on a 12-thread desktop. Installs by swapping node binaries into Meshroom 2023.3. MPL-2.0. | [vendor, numbers; 4★, single author, posted 2026-09-14] | github.com/dspl1236/cheshire; https://news.ycombinator.com/item?id=49700640 |
| **Spirula Studio** | Vulkan on NVIDIA, AMD, Intel and Apple. Built-in GPU-SIFT incremental SfM (LoMa features optional), SAM masking, frame extraction, training of 10M SH3 Gaussians in 8 GB, and **meshing to a textured mesh** (Delaunay over 7 points per Gaussian, `src/mesh/`). Telemetry → metric scale (`--metric-gps`, `--metric-positions`) since v2026.9.13. MCP server since v2026.9.24. Linux x86_64 zip. GPL-3.0. Praise includes "finally treated like a first class citizen as a Linux + AMD user" (22 pts). | [vendor + many anecdotes] | github.com/harry7557558/spirula-studio (994★, v2026.9.24); https://reddit.com/r/GaussianSplatting/comments/1w0by3s/, https://reddit.com/r/GaussianSplatting/comments/1w5e0vo/ (2026-09-02, 160 pts), https://reddit.com/r/GaussianSplatting/comments/1wi8erv/ (2026-09-16) |
| **Brush** | WebGPU/Burn trainer, runs on AMD, Linux, Android and in the browser. Needs COLMAP input. Its author: "COLMAP can still be quite expensive… order half hour, as opposed to seconds." VIMA (Hacktech 2026) and AURIC (Hack the North 2026) used it. | [vendor, anecdote] | https://news.ycombinator.com/item?id=41938831 (2024-10-24); github.com/ArthurBrussee/brush (Apache-2.0) |
| **gsplat on RDNA4** | Community ports for gfx1201 fix a hardcoded wave64 warp that "silently corrupts the backward pass" on wave32. One reports 2.45× on the training step with tile-size 16. AMD's official gsplat-amd targets Instinct MI300X. | [vendor] | github.com/charyang-ai/gsplat-rocm-rdna4 (no license), github.com/Painter3000/amd-gsplat-rocm72-gfx1201 (Apache-2.0), github.com/AMD-Ecosystem/gsplat-amd |
| **AirVis Studio** | "5 min 360 video took about 2 hours on a modern PC", Vulkan/Metal SfM and trainer, auto LOD and collision. One RX 7900 XT owner: "first time… it just worked". Windows and Mac, no Linux yet. | [vendor + anecdote] | https://reddit.com/r/GaussianSplatting/comments/1vvf7ou/ (2026-08-22, 518 pts) |
| **VkSplat** | Vulkan 3DGS trainer, "more academic than practical". | [vendor] | https://reddit.com/r/GaussianSplatting/comments/1t04rz2/ (2026-04-30) |
| LichtFeld Studio | The popular free trainer (paid tier around €30), but **CUDA 12.8+ only; AMD not supported**. | [vendor README] | github.com/MrNeRF/LichtFeld-Studio |
| RealityScan | CUDA-only for texturing: "Your CUDA driver version… not supported" on AMD and old Quadro cards. | [anecdote] | https://reddit.com/r/photogrammetry/comments/1u4psab/ (2026-06-13) |

**Timing reference points posted by practitioners**

- EasySplat, 250-frame drone video, Apple Silicon: COLMAP plus Brush took about 21 min, cut to
  3 m 14 s by a Metal-optimised pipeline. The "3D mapping stage" became 1.68× faster.
  Most paper ideas (SparseAdam, Skip-GS, MCMC densification, other feature extractors) "did nothing,
  reduced quality or made it slower" [writeup, numbers]. Source: https://devpost.com/software/easysplat
  (OpenAI Build Week, 2026-07-21).
- Real-estate splats: no measurable gain from 1080p → 4K, 300 → 1200 frames, or 30k → 120k steps
  [numbers, 1vk588o]. Resolution and step count are not where the quality is.
- Gustavo Santiago's study: COLMAP 100 images 40 min, Meshroom 100 images 10 min, VGGT 10 images
  about 2 min. It uses the synthetic lego scene, so it is weak evidence [numbers].
  Source: github.com/GustavoSantiago113/ReconstructionStudies (2026-05).

**Implication.** Cheshire's DepthMap at about 3 s/view means about 174 views ≈ 9 min for dense depth,
plus about 2 min of filter and meshing, against 27 min of CPU OpenMVS densify today [our calc,
unverified on our data]. Spirula is the one-binary alternative, and it also gives a splat for free.
Both are hours, not days, to trial.

### 2.4 Textureless walls, reflective appliances and low light (the indoor problems)

| Finding | Evidence | Source (date) |
|---|---|---|
| Walls got noisy and unstable with RealityScan poses plus Brush. The same poses with **Spirula depth and normal maps as priors** gave visibly flatter walls. Top comment: "the amazing things depth and normals can do for indoor scenes". | [anecdote with images, 160 pts] | https://reddit.com/r/GaussianSplatting/comments/1w5e0vo/ (2026-09-02) |
| "There is no detail in neutral walls, it is just a problem rooted in reality." The fixes offered were: "shoot down the length of the walls more", "capture perpendicular to the walls", iPhone LiDAR, or fake planes in post. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1vk588o/ (2026-08-09) |
| Indoor workflow (58 pts): pass 1 walks along the walls pointing at the opposite side; pass 2 walks a "#" grid through the middle; pass 3 tilts to the ceiling and floor and repeats. "If exposure is too low, the camera increases shutter time → motion blur, noise, unstable matching… brighter lighting helps a LOT." Open doors first so the path is continuous. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1tqzpm1/ (2026-05-29) |
| Three heights (chest, overhead, low) plus ground and ceiling passes plus point-of-interest passes. Olli Huttunen's interior demo uses the same "three different heights" pattern. | [tutorial] | https://www.captures.studio/interactive-capture-tutorial (via https://reddit.com/r/GaussianSplatting/comments/1tvv16b/, 2026-06-03, 176 pts); https://youtu.be/2ZX_5bOdKjo (2024-05-08) |
| Blank walls: feed-forward models are "genuinely better than COLMAP here" (survey quote). Mirrors: both COLMAP and feed-forward models triangulate the virtual room behind the glass. | [secondary, quoted] | Ledge repo `docs/research/cloud/reconstruction.md` |
| TSDF fusion of masked depth "failed" to fill low-texture holes (roof, stairwell). | [numbers-light, repo log] | OrbisEngine `docs/research-log.md` |
| LingBot-Depth (depth completion, Apache-2.0) filled glass-cabinet, chrome and mirror voids plausibly in RGB-D captures, with softened edges. | [anecdote, RGB-D not RGB] | https://reddit.com/r/photogrammetry/comments/1qzbk39/ (2026-02-08) |
| "Reflective surfaces are killer to photogrammetry": glass, wet road, car paint. On HN, a car scanner said you "would spray paint it matte". | [anecdote] | https://reddit.com/r/photogrammetry/comments/1q8o5vx/ (2026-01-09); https://news.ycombinator.com/item?id=42815995 (2025-01-24) |
| Low-light, one-sided light, 4K60 action cam: "surprisingly clean… indoor captures can be difficult if too much light bleeds in from the window." | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1tm9h69/ |
| Blurry 360 splats: "raise the shutter speed to 1/500 or more… image sharpness is really important", or use AprilTags. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1woc3rd/ (2026-09-23) |
| MASt3R "is genuinely sensitive to scene texture"; a team had to stage their rubble scene for visual variety. | [writeup] | https://devpost.com/software/riis (LA Hacks 2026) |
| "Clips with too little parallax, or too dark, fail reconstruction." | [repo known-limits] | OrbisEngine `docs/known-limits.md` |

### 2.5 Coverage: what the camera path can and cannot give you

- Orbis measured that a camera walking forward records only about 41° off its own path. Recorded
  coverage at 60°, 90° and 180° was 0%, and three depth and SfM sources agreed. "No reconstruction
  method recovers pixels that were never filmed." [numbers] (OrbisEngine `docs/research-log.md`)
- Their coverage fixes, in order:
  - Floor extension failed: "a flat rectangle ending".
  - FLUX panorama outpainting passed 4 of 16 held-out seeds.
  - Marble world plus a gsplat fine-tune on the posed frames was "the biggest single jump",
    9.96 → 33.09 dB held-out PSNR. It "buys fidelity on the camera path plus a short step either
    side, not free viewpoint", and "always fit the scale against real depth" (Marble's scale was
    about 25% off). [numbers]
  - For AirTools this is a fill-the-gaps option for the preview only. It can never be metric truth.
- Real-world practice for a room is multi-pass: walls, a grid through the centre, ceiling and floor,
  and several heights (§2.4). A single 87 s drone pass that never looks back at its own start
  cannot reach 100% view coverage, whatever the algorithm. **Our 70% is partly a capture ceiling.**
  An honest bench should report coverage of the frames the camera *saw*, separately from the frames
  held out for evaluation.

### 2.6 Drone capture (for the aerial building orbit target)

| Recommendation | Evidence | Source (date) |
|---|---|---|
| **Three concentric loops.** Low: building fills 50–75% of frame, camera tilted 10–20° down. Mid: 30–60%, 20–30° down. High: 10–40%, 30–45° down. Then vertical cascades. Never let the building fill 100% of the frame. | [vendor, "from tens of thousands of drone captures"] | https://get.teleport.varjo.com/product-pages/drone-capture-guide, via https://reddit.com/r/GaussianSplatting/comments/1vbpx1i/ (2026-07-31, 93 pts) |
| Photos, not video; interval every 2–3 s; **shutter 1/500 s or faster**; manual ISO, aperture and shutter fixed before takeoff; about 70% overlap ("non-negotiable"); 3–6 m/s; about 24 mm; 300–500 photos per home (200 is the floor). | [vendor] | same guide |
| Pro mode, shutter 1/1000, lower the max rotation and movement velocities, well-lit scene (DJI Mini 3, HLOC + fVDB pipeline). | [anecdote + repo] | https://reddit.com/r/GaussianSplatting/comments/1uugct0/ (2026-07-12); github.com/CVxTz/gaussian_splatting_recipe (GPL-3.0) |
| Gimbal about 75° down for medium POI orbits, 60–75° low. Avoid sky in frame. Opposing high passes at 75–90°. | [forum anecdote] | https://mavicpilots.com/threads/autonomous-3d-mapping-photogrammetry-tutorial-for-the-dji-mini-4-pro.144004/page-2 |
| Spiral up or down around the building. Work outside-in, always looking inward, with multiple elevations. | [anecdote] | https://reddit.com/r/GaussianSplatting/comments/1w919j6/ (2026-09-06) |
| Write SRT GPS into EXIF as a position prior for scale and stability. "Trust the GPS position more [than altitude]." Spirula found that fitting GPS altitude turned 5 m of altitude error into 5° of tilt, so `--metric-gps horizontal` is its default. | [anecdote + vendor design note] | 1uugct0; spirula-studio `docs/notes/sfm-design.md` D75 |
| "Don't use video, the quality is atrocious compared to photos; just set interval shots." Against that, a Mini 4 Pro user found extracted frames "nearly the same quality" as photos. | [anecdote, conflicting] | https://reddit.com/r/photogrammetry/comments/1c3unrf/ (2024-04-14) |
| Video suits 3DGS more than photogrammetry: "compression issues and rolling shutter are ill suited… shitty but very densely captured input works well [for splats]." | [anecdote] | https://reddit.com/r/photogrammetry/comments/1vqrhv4/ |

### 2.7 Splat → mesh in practice (the deliverable is a mesh)

- The practitioner default is still two tracks: photogrammetry for the mesh, splats for the looks.
  - One maker built a climbing-gym mesh with COLMAP + OpenMVS on an iPhone (HN, 29 pts):
    https://news.ycombinator.com/item?id=49250141 (2026-08-10).
  - A DJI Avata 360 flight was run through both WebODM (mesh) and COLMAP + LichtFeld (splat):
    https://youtu.be/jTRew7HD1bQ (2026-09-12).
  - A "video > splat > 3D mesh?" asker's own workflow was ffmpeg → COLMAP/GLOMAP → Brush/gsplat →
    OpenMVS for the mesh. There was no better open answer (2026-05-01, 1t0fkq1).
- Splat-native meshing now exists in tools people use: Spirula (Delaunay over the Gaussians),
  KIRI Engine 4.2 (phone, "splat to mesh just got better", https://youtu.be/l5vWKKCWSfA), and an
  XR AI Spotlight tool review (https://youtu.be/SASCBKbyFgk).
- SuGaR-style mesh refinement remains a known time trap (SkySplat's post-mortem, already in
  `../p1-prior-hackathons.md`).
- save-splat (Future Legends 2026) chose *not* to mesh, "because it smooths over the uncertainty".
  It measured directly on splats with opacity-weighted RANSAC:
  https://devpost.com/software/save-splat (2026-09-19). Not our need, but it confirms that meshing
  loses information people care about.

### 2.8 Hackathon projects (Devpost), video or drone → 3D, 2025–2026

The six projects already audited in `../p1-prior-hackathons.md` and `../p1-treehacks-vort3xed.md`
(the latter is Strata, TreeHacks 2026) are not repeated here.

| Project (event, date) | Input → method | What worked, with numbers | Repo |
|---|---|---|---|
| **Orbis Engine** (Hack the North 2026, 27 likes, most-liked hit) | Video → Pi3X poses and depth → LaMa people-removal → Marble world → gsplat fine-tune on real frames → Spark/WebXR on Quest | Fixed focal 3 → 550 of 550 frames registered; Marble + fine-tune 9.96 → 33.09 dB; about 25 min and $0.15 GPU per 10 s clip; "clips that work: one continuous shot, 5–15 s" | github.com/dtpu/OrbisEngine (no license) |
| **Ledge** (MarinHacks, 2026-08-02) | iPhone room video → blur, exposure and overlap scoring → keyframes → VGGT → COLMAP export → gsplat; SAM 3 → OBB colliders | Frame scoring is part of the pipeline, not an afterthought | github.com/BackendAdam/Ledge |
| **Vora Earth** (NextStep Hacks 2026-09-19) | Smartphone video → GLOMAP + Depth Anything V2 + gsplat on Modal L4 | "Complete pipeline under 60 s" upload → twin; switched to Apache/MIT gsplat for licensing | github.com/glacerous/vora-earth |
| **Beacon / Swarm Sight** (Hack the North 2026) | Many phones (ARKit poses) → VGGT-Ω on a Baseten H100 | Post-processing 14.1 s → 4.4 s by caching depth and checking cross-view agreement at half resolution; about 130 ms round trip | github.com/owenguoo/htn26 |
| **RIIS** (LA Hacks 2026) | Rover frames → MASt3R poses → InstantSplat → Unity on Quest 2 | MASt3R texture-sensitive; had to stage the scene | github.com/srinisriram/RIIS |
| **AURIC** (Hack the North 2026, solo) | Room video → FFmpeg → COLMAP → Brush → three.js | Classic stack done by one person in one weekend | none |
| **Memento** (SOON Hackathon 2026-05) | Video → ffmpeg → COLMAP (optionally MASt3R-SfM) → OpenSplat | — | github.com/itsmarsss/memento |
| **StructureFirst** (QuantumHacks et al., 2026-08) | Photos → SIFT + indoor LoFTR + VGGT with correspondence checks to decide which views connect → SHARP / LucidFrame splats | "A reconstruction can look convincing even when the source images don't support it", so they keep unverified rooms separate | github.com/meowshmalloww/StructureFirst |
| **EasySplat** (OpenAI Build Week 2026-07) | Photos or video → COLMAP-class SfM + Brush, Metal-optimised | 250-frame drone video 21 min → 3 m 14 s | github.com/dud8/EasySplat |
| **Spare** (VTHacks 14) | Head-cam → Depth Anything V2 + OpenCV tracking | "Building 3D from a single moving camera, with blank walls and no depth sensor" was the listed hard problem; OpenCV silently fell to 5 fps | github.com/SohanD88/VTHacks14 |

**Pattern across all of them.**

- None produced a *metric, textured mesh* within the hackathon.
- Teams that reached a usable 3D result all did two things: they curated frames (few, sharp,
  non-redundant), and they separated pose solving (COLMAP/GLOMAP or a feed-forward model) from
  appearance (splat training).
- The fastest end-to-end numbers (under 60 s, about 3 min) came from a warm cloud GPU or aggressive
  frame reduction, not from a new algorithm.

### 2.9 Quest and VR delivery (brief)

- Spark (three.js splat renderer, HN 377 pts, 2025-06-11, https://news.ycombinator.com/item?id=44249565)
  is the WebXR default in the 2026 hackathons (Orbis, Vora, StructureFirst).
- Quest-native capture apps exist: OpenQuestCapture (https://github.com/samuelm2/OpenQuestCapture,
  HN 2025-12-05) and a hobby Quest 3 splat app with a LichtFeld companion
  (https://reddit.com/r/GaussianSplatting/comments/1wmwugh/, 2026-09-22).
- Orbis found a Quest-specific pitfall: main-thread `toBlob` took about 4 s per image in native Quest
  XR, and moving it to a worker took it to 25–40 ms [numbers, `docs/known-limits.md`]. It is only
  relevant if we render spectator images on-device.

## 3. Top tricks we could apply within hours, ranked by expected gain

Each trick says what it changes, the expected gain, the evidence behind it and the effort.

1. **Sharpest frame per window, instead of fixed 2 fps.**
   - Change: for example `uvx sharp-frame-extractor DJI_0095.MP4 --every 0.4`, or about 15 lines
     of OpenCV Laplacian or Tenengrad per 0.3–0.5 s window. Optionally skip windows where caption
     H.S or V.S is high.
   - Gain: **detail + registration**. It directly counters the 1/30 s motion blur, which is the
     dominant failure in our footage.
   - Evidence: universal practitioner default (§2.1).
   - Effort: under 1 h. It helps both the full path and the VGGT preview.
2. **Pin the intrinsics in SfM.**
   - Change: one shared camera; focal prior about 2170 px at 3840 wide (Mini 4K, 83° HFOV), scaled to
     the extraction size; hold the focal and distortion fixed in the global stage, or seed from a
     one-off incremental or calibration run.
   - Gain: **coverage (registration) + geometry**.
   - Evidence: 3 → 550 of 550 (Orbis) and the 12 dB GLOMAP drift (§2.2).
   - Effort: under 1 h, since these are mapper flags.
3. **Replace CPU OpenMVS densify with a GPU dense stage on AMD.** Two routes, which can be tried in
   parallel:
   - (a) The Cheshire HIP AliceVision bundle, run on our COLMAP poses (DepthMap → Filter → Meshing
     → Texturing). About 3 s/view on an RX 9070 [vendor].
   - (b) Spirula Studio end to end.
   - Gain: **speed**, with an estimated 27 min → about 10 min for dense [our calc, unverified].
   - Effort: 2–4 h each. Cheshire has 4 stars and one author, so trust it only after a bit-level
     check on our clip.
4. **Depth and normal priors for walls.**
   - Change: Spirula's depth and normal maps, or our own VGGT/Pi3X depth, used as priors in splat
     training before meshing, or as a TSDF blend in the preview.
   - Gain: **detail and completeness on blank walls and cabinets**, where OpenMVS leaves holes.
   - Evidence: a 160-point before/after post (§2.4). Orbis's contrary result: TSDF on *masked*
     depth did not fill holes.
   - Effort: about half a day.
5. **Use VGGT or Pi3X poses as an initialiser, not the answer.**
   - Change: feed the preview's poses to COLMAP `point_triangulator` plus bundle adjustment
     (known-poses path) to register frames that global SfM dropped, then run the dense stage.
   - Gain: **coverage**. It also makes the preview and full scale consistent (the preview is
     currently 45% off in scale).
   - Evidence: practitioners say feed-forward poses are "worse than regular software" on their own
     (§2.2), so they need refining.
   - Effort: about half a day.
6. **Keep the frame budget at 300–800 sharp frames for a room.**
   - Gain: speed and quality. More frames did not help the real-estate operator; too many (1,652)
     hurt; too few or too close together failed (§2.1).
   - Effort: none, just a parameter.
7. **Next capture (the free win for coverage and detail).**
   - Indoors: lights on, 1/250 s or faster, ISO and white balance locked, a wall-perpendicular pass
     plus a centre grid plus ceiling and floor passes at three heights.
   - Aerial: Teleport's three-loop orbit at 1/500 s or faster, about 70% overlap, interval photos
     every 2–3 s (§2.4, §2.6).
   - Gain: the largest of all for coverage, but it needs a re-fly.
8. **Preview-only generative fill for never-seen regions**, labelled as not measured.
   - Evidence: Marble + fine-tune took PSNR from 9.96 to 33.09 on the camera path (§2.5).
   - Gain: perceived completeness in the Quest only. It is not metric and costs money, so it is
     optional and flagged.

## 4. Tools and scripts worth trying on the AMD box (hfbox, RX 9070 XT, ROCm 7.2)

| Tool | Why | Runs on AMD Linux? | License | Link |
|---|---|---|---|---|
| **Spirula Studio** v2026.9.24 | Video → SfM → splat → **textured mesh**, depth and normal priors, telemetry metric scale, CLI plus MCP, one binary | Yes, Vulkan; Ubuntu x86_64 zip | GPL-3.0 (fine as an external tool) | https://github.com/harry7557558/spirula-studio |
| **Cheshire** v0.3.3 | GPU DepthMap, Filter, Meshing, Texturing and SIFT for AliceVision; tested on RX 9070 + ROCm 7.2 | Yes, HIP (validated gfx1201) | MPL-2.0 | https://github.com/dspl1236/cheshire |
| sharp-frame-extractor | Sharpest frame per interval; pip or uvx; CPU | Yes, CPU | MIT | https://github.com/cansik/sharp-frame-extractor |
| morishuz frame-extractor / adaptive-frame-extractor | Optical-flow keyframes plus CSV diagnostics | Yes, CPU | MIT | https://github.com/morishuz/frame-extractor |
| Reflct sharp-frames | Best-N sharp frames, the community default | Yes, CPU | unclear (NOASSERTION); ideas only | https://github.com/Reflct/sharp-frames-python |
| Brush | Splat trainer, WebGPU/Vulkan; for a splat track or a mesh prior | Yes, Vulkan | Apache-2.0 | https://github.com/ArthurBrussee/brush |
| gsplat RDNA4 ports | gsplat training with wave32 fixes | Yes, ROCm gfx1201 | Apache-2.0 (Painter3000) / none (charyang-ai) | https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201, https://github.com/charyang-ai/gsplat-rocm-rdna4 |
| LingBot-Depth | Depth completion for glass, chrome and mirror holes (RGB-D oriented; would need adapting to mono depth) | PyTorch, probably ROCm; untested | Apache-2.0 | https://github.com/robbyant/lingbot-depth |
| Sail-Recon / FastVGGT | Faster feed-forward recon for about 400 frames | PyTorch, probably ROCm; untested | MIT / unclear | https://github.com/HKUST-SAIL/sail-recon, https://github.com/mystorm16/FastVGGT |
| vggt-factor-refinement | Chunked VGGT plus GTSAM loop closure, if the preview goes beyond about 64 frames | PyTorch; untested | none (ideas only) | https://github.com/jashshah999/vggt-factor-refinement |
| LoGeR | Long-video feed-forward recon (DeepMind/Berkeley); code and weights released | Untested | none stated | https://github.com/Junyi42/LoGeR, https://news.ycombinator.com/item?id=47319620 (2026-03-10, 150 pts) |

These were ruled out for the AMD box:

- LichtFeld Studio is CUDA-only.
- RealityScan needs CUDA for textured output.
- AirVis Studio has no Linux build.
- KIRI's Scan Prep Tool is Windows-first (AGPL-3.0).

## 5. Sources not reachable or thin (for honesty)

- **X/Twitter**: not accessed (login wall). Brush's own launch was an X thread; its HN
  cross-post is cited instead.
- **YouTube transcripts**: none. These videos have promising titles but we could not verify their
  numbers:
  - Florent Poux's 4-model benchmark (https://youtu.be/__6T6kB5i3Y).
  - XR AI Spotlight's 10-tool 360-drone comparison (https://youtu.be/zoFDv2IaLLE, with a
    side-by-side viewer at https://voluma.ai/embed/voluma/gabriele/windmills).
  - Ops Above's mesh vs splat drone test (https://youtu.be/jTRew7HD1bQ).
  - MipMap's indoor capture tutorial (https://youtu.be/3ENIHCdCq4Q).
  - Olli Huttunen (for example https://youtu.be/2ZX_5bOdKjo, https://youtu.be/N15E_0kZ1UM).
- **Paywalled**: the XR AI Spotlight benchmark article (2026-03-24) and the heyulei indoor capture
  guide on Medium (HTTP 403).
- **Reddit coverage of r/drones, r/dji, r/computervision and r/virtualreality** is title-search
  only, because of archive timeouts. The hits were mostly outdoor mapping and orthomosaics.
  Examples: https://reddit.com/r/dji/comments/1r2i7z7/ (Mini 4 Pro mapping, 2026-02-12) and
  https://reddit.com/r/drones/comments/1rnehxm/ ("usable 3D models without babysitting
  photogrammetry software?", 2026-03-07).
