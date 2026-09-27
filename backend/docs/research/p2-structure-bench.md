# P2/P3 structure bench: sharp edges, planes and corners for snapping

A living document for the research session that started 2026-09-25 (about 14:40 UTC). The
aim is to give the Quest tools (tape, protractor, level, part placement) CAD-like snap targets on
a photogrammetry mesh, which is smooth. That means flat faces, straight edges and exact corners,
either in the mesh itself or in a separate "structure layer" the tools snap to. Every method we
read about or try gets a row in §3. The per-agent notes live in `docs/research/structure/`.

## 1. Problem and target output

- **Today:** P3 tools snap with `Physics.Raycast` / `ClosestPoint` against `collision.r<rev>.glb`,
  a decimated copy of the photogrammetry mesh. Edges come out rounded over 1–5 cm, faces wobble,
  and a tape end lands wherever the ray hits. In the CAD test scenes every measurement is exact,
  because the geometry is exact.
- **Target:** the pipeline also publishes a structure layer in the same metric frame as
  `mesh.glb`. The tools then snap in priority order: corner, then edge, then plane, then mesh.
  - **Planes:** normal, offset, inlier polygon, and a label where known (wall, floor, counter,
    cabinet face, door, window).
  - **Edges:** 3D line segments where two planes meet or where a strong image line
    triangulates, with endpoints, the dihedral angle and a confidence.
  - **Corners:** points where three planes or two or more edges meet.
  - **Optionally**, a regularized mesh whose flat regions are exactly planar and whose edges are
    exactly straight (planar snapping of vertices, or a piecewise-planar proxy).
- **Footage:** the kitchen benchmark `kitchen-0095` from `docs/research/p1-recon-bench.md`
  (DJI_0095, indoor, 175 frames at 2 fps, COLMAP poses, hybrid dense cloud and mesh).

## 2. Metrics

Tape measurements of the kitchen are still pending, so ground truth comes from the images, not
from any mesh:

| Metric | Definition |
|---|---|
| **Corner / edge ground truth** | Kitchen corners and edges (cabinet doors, counter front, appliance outlines) located in 2D in several frames and triangulated with the COLMAP poses. The residual in px is reported per point. Tape measurements replace or confirm these once we have them. |
| **Snap accuracy** | For each ground-truth corner, distance from it to the snapped corner (corner snap) or to the nearest point on the snapped edge (edge snap). Report median and p90 in mm, and the snap recall (fraction of ground-truth corners with a snap target within 3 cm). |
| **Measurement error** | Tape measurements between ground-truth corner pairs (door width, dishwasher width, counter depth) done with snapping, against the triangulated ground truth. Report absolute mm and %. |
| **Planarity** | RMS distance of mesh vertices to their fitted plane, over the main planes. |
| **Angle error** | Deviation from 90° and 0° of plane pairs that should be orthogonal or parallel. |
| **Edge sharpness** | Width of the rounded band across a ground-truth edge (distance over which the mesh normal turns), in mm. |
| **Edge reprojection** | Structure-layer edges projected into held-out frames against LSD/DeepLSD image lines: median px distance and the recall of strong image lines. |
| **Repeatability** | Width spread of repeated elements (identical cabinet doors) in mm. |
| **Cost** | Runtime on hfbox, and the size added to the scene package. |
| **Visual** | PSNR / SSIM of the re-textured mesh when the mesh itself is regularized; it must not drop below the P1 hybrid (21.5 / 0.807). |

## 3. Methods table

**Scoring update (`40a2e9c`):** the headline is now the R6 Unity snap policy (a corner within 2.5 cm, an edge within 2 cm, a corner beats an edge only when d_c ≤ d_e + 1 cm, then a plane within 3 cm, then the mesh). GT points ua_bl and dw_tr are downgraded to grade C and excluded (`--grades AB`, 20 points); row 0 becomes aim miss 6.9 / 10.0 and perfect 2.4 / 4.7 mm, measurement 4.1 mm. Rows scored before this used 22 points under the older corner-first 3–5 cm rule.

**Ground truth** (S1, `a54f43f`): `pipeline/experiments/structure/kitchen0095/gt.json`
- 22 kitchen points: upper-cabinet corners and door seams, microwave, counter front, backsplash
  lip, drawer, dishwasher, oven window.
- Each point is picked by eye, refined at LSD line intersections to sub-pixel, tracked through the
  frames, and triangulated with the COLMAP poses. No mesh is used anywhere.
- Per point: reprojection RMS 0.4–0.9 px, 7–124 views, 1σ precision 0.1–1.8 mm.
- 16 measurement pairs, 3 repeat groups, 7 plane regions, 6 angle pairs, 6 GT edges.
- **mm are at the altitude scale, which is about 1.47× too small** (the dishwasher reads 414 mm).
  Compare rows, and use the scorer's scale-free ratio errors for absolute claims.
- The oracle (GT as structure) scores 0 mm snap and 1.3 px LSD, which validates the scorer.
- **Update (`40a2e9c`):** two points were downgraded to grade C and are excluded by default
  (`--grades AB`): **ua_bl**, where the track mixes the door corner with the carcass behind it
  (17 mm deeper than its neighbours), and **dw_tr**, the textureless dishwasher top (38 of 63 views
  rejected, and it implies a 2.9° yaw nothing else shows). That leaves 20 points (16 A, 4 B). The
  ratio reference is now "upper cab B width". `--grades ABC` reproduces the old numbers exactly.
- **Snap policy:** the headline is now **R6 §7.3**: a corner within 2.5 cm, else an edge within
  2 cm (a corner beats an in-range edge only if d_c ≤ d_e + 1 cm), else a plane within 3 cm, else
  the mesh. The old "corner > edge > plane, all within 3 cm" rule is still reported
  (`snap.policies.priority`). Rows scored before `40a2e9c` use 22 points and the old policy.

Scorer (hfbox, from `/workspace/airtools`, about 2 s):
`python -m pipeline.experiments.structure score <scene_dir> --gt pipeline/experiments/structure/kitchen0095/gt.json [--structure s.json] [--frames <dir>] [--mesh alt.glb] --out /workspace/bench/structure/results.jsonl --name <row>`.
The schema is in `structure/schema.md`.

| # | Method | Family | Runs on hfbox? | Speed | Snap acc. (median / p90 mm) | Planarity / angle | Status | Source |
|---|---|---|---|---|---|---|---|---|
| 0 | Raw hybrid mesh, `ClosestPoint` snapping on collision.glb (today) | baseline | yes | – | **perfect aim 2.5 / 8.9 (i-hybrid-moge2), 3.1 / 5.9 (i-e2e-fixed); with a ±1 cm aim miss 7.3 / 11.8 and 7.2 / 12.5**; measurement error 4.0 / 3.7 mm with aim miss (1.5 / 0.8 mm with perfect aim) | planarity RMS 6.1 / 5.3 mm; angle error 1.1° / 2.1°; **edge normal-turn band 16 / 20 mm** | **measured (S1)**. **With the 20-point GT and the R6 policy: perfect aim 2.4 / 4.7, with aim miss 6.9 / 10.0, grade A only 7.0 / 10.0; measurement 4.1 mm with aim miss** | P1, S1 |
| 0b | Same mesh with `ReconstructMesh --smooth 0` (the OpenMVS default is `--smooth 2`; the pipeline passes none). Control run s1-smooth2 vs s1-smooth0, same cache | mesh flag | yes | 123 s vs 119 s for the whole resumed run | **20-point GT, R6: perfect aim 3.1 / 4.5 vs 2.5 / 4.7; with aim miss 6.9 / 9.7 vs 7.0 / 10.0** | planarity 6.4 vs 6.1 mm; angle 1.84° vs 1.08°; **edge band 20 vs 16 mm**; **PSNR 21.56 vs 21.85, SSIM 0.770 vs 0.811** (E1 bench) | **measured (S1): keep `--smooth 2`.** No sharpness gain, and it costs 0.3 dB and 0.04 SSIM | R6, S1 |
| 0c | **OpenMVS `RefineMesh`** (photometric) on the union mesh before TextureMesh; best: `--resolution-level 0 --decimate 1` | mesh refinement | yes (CPU) | **RefineMesh 466 s on 8 cores**, plus 123 s texture and package (res1 without decimate: 134 s; res1 with auto-decimate: 90 s) | **R6: perfect aim 1.0 / 3.9 (row 0: 2.5 / 4.7)**; aim miss 6.6 / 9.4 (unchanged: a mesh snap keeps the tangential aim error); GT-edge 1.4 mm | **edge band 4 mm (row 0: 16)**; planarity 6.2 mm (unchanged); PSNR/SSIM 21.77 / 0.797 (−0.08 dB, −0.014). Res1 auto-decimate loses SSIM (0.702) | **S1: the best mesh so far**, but too slow for a default; speed variants running. It is S3's regularization input | R6, S1 |
| 0d | RefineMesh speed sweep around 0c (res0, `--decimate 1`, 8 cores, `--structure off`; `s1_refine.sh`) | mesh refinement | yes (CPU) | RefineMesh time: `--max-views 4` 278 s; `--scales 3 --max-views 6` **398 s**; `--max-views 6` 404 s; `--fast` 457 s; `--planar-vertex-ratio 0.02` 465 s; `--scales 3` 494 s; `--scales 1` **905 s** (all iterations at full res) | perfect aim 1.0–1.9 / 3.6–4.9 for all; aim miss 6.2–7.0 / 9.3–9.6; GT-edge 1.3–2.6 mm | band / SSIM: v4 10 mm / 0.791; s3v6 12 / 0.791; v6 10 / 0.797; fast 4 / 0.779; pvr 4 / 0.797 (a replicate of 0c: the ratio has no effect at 0.02); s3 4 / 0.792; s1 16 / 0.792. PSNR 21.59–21.96 (noise ±0.15 dB) | **measured (S1): no variant keeps band ≤ 5 mm and SSIM ≥ 0.795 below 0c's 466 s.** Fewer views cost sharpness and more scales don't save time. 12-thread (SMT) timing not run (cores busy); a 16-thread gain of 1.2–1.4× is a guess | S1 |
| 1 | S1 baseline planes: mesh surface samples (800k; MVS misses undersides), gravity plus dominant-yaw Manhattan axes, per-axis offset histograms with connected components, convex-hull polygons, plane∩plane creases (convex and concave) clipped to support, then crease∩plane corners (`structure/planes.py`) | planes, CPU | yes (numpy) | **1.3 s** | **R6, best setting eps 8 mm, min_pts 300 (68 planes, 33 edges, 4 corners): perfect aim 4.2 / 6.7, with aim miss 6.6 / 9.7 (row 0: 6.9 / 10.0).** eps 5 mm (125 / 33 / 3): 3.8 / 7.9 and 7.2 / 10.6; eps 12 mm (87 / 46 / 17): 8.4 / 18.2 with aim miss.** Old policy 7.5 / 22.2 and 8.9 / 25.3. Measurement 4.1 / 4.4 mm with aim miss | GT-edge 5.9 / 6.5 mm; corner recall@3cm 0.14 / 0.71; LSD 6.0 / 8.3 px | **measured (S1): no better than the raw mesh.** Wrong nearby creases (dishwasher top, counter underside) capture snaps; mesh undersides sit about 8 mm off; Manhattan forcing adds error on fronts that aren't parallel. R6's 2 cm edge radius removes most of the p90 damage. Notes: `structure/s1-harness-planes.md` | R5, R6, S1 |
| 2 | LIMAP 2.0 3D line map from our COLMAP 4.2 poses (LSD plus geometric matching; frames with id ≡ 1 mod 25 held out), then Manhattan snap, a 6 mm collinear merge and line-line junctions | 3D lines | **yes** (BSD-3; CPU) | **hfbox 135 s on 4 cores** (laptop 320 s on 16), plus 1–4 s post; DeepLSD+GlueStick on GPU 304 s and no better | **axis-only (159 edges, 137 corners): perfect aim 2.6 / 18.6; with aim miss 2.9 / 19.4** (row 0: 7.3 / 11.8); **measurement 2.1 mm, 0.71 %** (row 0: 4.0); GT-edge 2.4 mm | held-out LSD 0.89 px median, 84 % of edges matched | **S2 scored.** Median much better than the mesh, but p90 is worse: 3–4 GT points have no line (microwave left edge, counter nose), and a wrong corner 2 cm away beats a correct edge under the 5 cm corner-first rule. Next: fuse with S3 planes | R5, R6, S2 |
| 2f | **S2 fusion: LIMAP lines (row 2, all edges) + S3 PxwPlanar v6 planes** (`lines/fuse.py`). It adds line × plane corners where a plane-attached line (within 1 cm and parallel to the plane, inside its polygon +5 cm) ends near another plane, and plane∩plane creases where no line runs (30), ending at the nearest third-plane or line crossing. Corners within 1 cm that share a line or plane are merged at the least-squares point of their combined lines and planes. Lines are not moved: snapping them into the planes scored worse | lines + planes | yes | row 2 + row 3 + **~30 s fuse on 4 cores** | **20-pt GT, R6: aim miss 2.7 / 8.4, perfect 2.5 / 4.5, grade-A aim 2.8 / 8.0; measurement 1.3 mm with aim miss** (row 0: 6.9 / 10.0, 2.4 / 4.7, 4.1 mm); measurement error 0.8 mm (0.33 %), GT-edge 1.8 mm, corner recall 1.00. Old priority rule: 2.8 / 10.8. Ablations (R6 aim / perfect / meas-aim): no merge 2.8 / 8.8, 2.5 / 4.5, 2.3 mm; snapped lines and off-axis corner drop (first recipe) 2.9 / 10.0, 2.9 / 6.7, 1.5 mm; no creases 2.8 / 8.4, 2.6 / 4.5, 1.3 mm. Lines only, R6: all 2.8 / 10.0, 2.5 / 7.9, 2.9 mm; axis 2.8 / 11.7, 2.5 / 7.9, 2.1 mm | 511 edges, 515 corners, 53 planes | **S2 scored, best measurement row.** Line × plane corners close corners LIMAP has no second line for, and the support-sharing merge removes the near-duplicate corners that caused the measurement error. Notes: `structure/s2-lines.md` | S2, S3 |
| 2g | **Row 2f + S4 rectangles** (`lines/rects.py`), written in the scene frame (`lines/frame.py`, the pipeline's `cameras_matrix`; it scores identically to the cameras frame, largest difference 1e-12 mm). Rect sides that run along a LIMAP line (3°, 1 cm) take its position (94 of 160) and corners are re-intersected. LIMAP corners win within 1 cm. S4 relief planes, objects[] and groups[] are kept | lines + planes + objects | yes | row 2f + 1.6 s | **20-pt R6: aim miss 2.7 / 8.8, perfect 2.5 / 4.5, measurement 1.3 mm with aim miss; GT-edge 1.6 mm**. With rect corners winning: 3.4 / 11.1, 3.1 / 8.4, 1.8 mm | 40 objects, 5 repeat groups; the door-width spread with rect corners is 3.8 mm (GT 3.9) | **S2 scored.** Rects cost nothing on snap and add P3's object topology. LIMAP corners stay the accurate snap points | S2, S3, S4 |
| 2e | **Generalization: rows 2 / 2f re-run on the fresh default run's own SfM** (i-e2e-defaults, 175 frames; scored on i-e2e-fixed). That run kept no OpenMVS depth maps, so PxwPlanar ran mono-only | lines + planes | yes | LIMAP 182 s + PxwPlanar 55 s GPU + lift 59 s + fuse 14 s on cores 8-11 | **20-pt R6, fused: aim miss 3.4 / 11.3, perfect 2.3 / 7.4, measurement 1.5 mm** (mesh on this run: 6.5 / 9.6, 3.1 / 4.5, 3.3 mm). Lines only: 3.4 / 10.0, 2.2 / 6.1, 2.7 mm | 71 of 443 lines plane-attached (mono planes) | **S2 scored.** The median and measurement gains hold on a fresh SfM. The perfect-aim p90 needs MVS-lifted planes, so the pipeline should keep the depth maps for the lift | S2, S3 |
| 2p | **In the pipeline: `pipeline/structure.py`, full stage, `--structure on` (default)** — LIMAP LSD (no holdout) + PxwPlanar v6 lifted on the run's own OpenMVS depth maps + fusion, started when DensifyPointCloud finishes and overlapped with meshing; `to_scene(cameras_matrix)` + S4 rects (only LIMAP-supported rect corners kept) after the mesh; publishes `structure.r<rev>.json` + a `scene.json` `structure` entry. Fresh e2e run `s2-e2e-structure` (preview,full, crop none) | lines + planes + rects | yes | **e2e 1286 s on cores 8-11; the step takes 268 s in the background (LIMAP 187, PxwPlanar 63 + 111, fuse 3) and adds ~2 min, about 10 %**: 11 s after the mesh (S4) + ~110 s slower ReconstructMesh/TextureMesh from sharing 4 cores (37 → 56 s, 84 → 176 s against a `--structure off` re-run) | **20-pt R6: aim miss 3.6 / 11.4, perfect 3.2 / 7.9, measurement 1.3 mm (error 0.8 mm, 0.45 %)** (mesh on this run: 6.8 / 9.8, 2.3 / 5.1, 3.7 mm). As first published, with all rect corners: 4.5 / 11.4, 3.5 / 7.9. PSNR 21.58 (i-e2e-fixed 21.51), `validate_package` clean | 481 edges, 481 corners, 85 planes, 16 objects | **S2, in the pipeline.** Per-step process-group timeouts; the publish waits ≤ 600 s; any failure falls back or skips. Notes: `structure/s2-lines.md` § Pipeline integration | S2, S3, S4 |
| 3 | PxwPlanar (ECCV 2026): MoGe-2 with a per-pixel planarity head, then region growing; per-frame planes positioned by least squares on OpenMVS depth pixels inside each mask (mono depth only where MVS is empty), then fused across frames | learned planes | **yes, ROCm** (MIT; own venv, 0.1 GB) | **inference 45 s (165 frames at 960 px, 0.26 s/frame) plus lift and fuse 69 s on 4 cores** | **v0: 9.2 / 26.0; with aim miss 10.0 / 26.1 (worse than row 0)**; measurement 3.3 mm (row 0: 4.0); GT-edge 3.5 mm; corner recall@3cm 0.06 | 3947 per-frame planes fuse to 53 planes, 53 edges, 2 corners; RMS 2–5 mm on MVS-fitted planes (6–11 mm mono-fitted); most dihedrals 88–91°; clean masks on doors, counter, walls, fridge, dishwasher; few 3-plane corners; spurious short edges on small parts | **S3 v0 scored.** Cause: crease lines are right (GT within 0.5–5 mm of both planes) but edge segments stop 2–3 cm short, so the snap drags along the line; the counter lip fused with the door fronts. Sweep of extents, fuse offset and gravity-fixed Manhattan snap running | R5, R6, S3 |
| 3b | PxwPlanar v6: edges at full support plus 2 cm, **gravity-fixed Manhattan snap 3°** (up from the pipeline's own camera fit) | learned planes | yes | same | **R6 policy: aim miss 7.9 / 13.0, perfect 3.8 / 11.9** (row 0 with the AB grades: 6.9 / 10.0 and 2.4 / 4.7) | **measurement 2.0 mm (1.11 %); structure angle error 0.00°**; upper-cabinet bottom corners have no underside plane, so boundary lines must close them; the dishwasher front and counter lip are fused into the lower door plane (5–13 mm) | **S3 default** (`/workspace/s3/pxw/structure.json`) | S3 |
| 4 | PlanarSplatting (CVPR 2025): optimises 3D rectangles against MoGe-2 depth and normal priors (from the PxwPlanar cache) on our COLMAP poses | learned planes | **yes: S3 ported diff-rect-rasterization to gfx1201 with hipify and 5 source fixes** (`planes/psplat_hip.sh`); pytorch3d stubbed; open3d pinned to 0.19 | **94 s training (5k iterations) plus 8 s priors** | **R6 policy: aim miss 10.2 / 23.0, perfect 7.4 / 19.1**; measurement 3.8 mm; angle error 3.19° | 53 planes, 52 edges, 14 corners; **rejected: worse than PxwPlanar on every snap metric**, so no licence swap and no LiP-Map | **research only:** the rasterizer carries the Inria 3DGS non-commercial licence (PlanarSplatting itself is Apache-2.0; swapping in the gsplat rasterizer would fix it) | R5, S3 |
| 5 | CGAL Kinetic_surface_reconstruction_3: a watertight piecewise-planar proxy mesh | planar mesh | yes (GPL, CPU) | | | | candidate for mesh regularization | R6 |
| 6 | LiP-Map (TPAMI 2026): lines and planes jointly; F 0.465 vs LIMAP 0.181 on ScanNetV2 | lines + planes | needs the same HIP rasterizer port as row 4 | | | | only if the S3 port works; no licence file, so research use only | R5 |
| 7 | Sharp mesh: snap the 200k mesh's vertices to the structure planes, edges and corners, planar decimation, re-texture; plus a 5–10k planar collision mesh (row 5 / COMPOD) | mesh regularization | yes (CPU) | not timed (numpy, one pass; `structure/regularize.py`, rewrites POSITION in place) | **S1 row-1 planes, R6, with aim miss: 7.0–7.1 / 10.0 (row 0: 7.0 / 10.0); perfect aim 2.7–2.9 / 5.1–5.7** | eps 5 mm, tol 3 cm: **planarity 5.0 mm (row 0: 6.1), angle 0.44° (1.08°), edge band 10 mm (16)**, but **PSNR 20.55 (−1.3 dB), SSIM 0.767**; tol 1 cm: 5.8 mm, 0.53°, 10 mm, PSNR 21.29 (−0.56) | **measured (S1) with S1 planes: fails the visual constraint** (−0.6 to −1.3 dB), and snapping doesn't move. About 50 % of vertices move, median 3 mm. Retry with S2f's structure only if it is cleaner | R5, S1 |
| 7b | S3 mesh regularization before texturing: vertices snapped to PxwPlanar v6 planes (tolerance 6 mm–3 cm), optional crease and corner rules, then TextureMesh; on the unrefined and RefineMesh r0d1 meshes | mesh regularization | yes | one pass plus re-texture | best refined variant: perfect aim 1.5 / 4.2 (control 1.1 / 3.9) | **PSNR −0.3 to −1.1 dB, SSIM −0.02 to −0.07; planarity 6.2 → 5.4 mm at best**; crease and corner rules widen the band (10 mm vs 4 mm) | **S3: rejected.** Ship the RefineMesh mesh unmodified; exact snapping comes from the structure layer | S3 |
| 8 | In-plane rectangles (RoomPlan-style) on S3 PxwPlanar planes plus mesh-relief sub-planes (dishwasher): raw frames warped onto each plane and fused as a gradient median with a Poisson solve (a seam-free orthophoto), LSD horizontal and vertical line families, scored line quadruples (atomic plus outer rectangles), mesh-support, crease, size and relief-fill gates, corners lifted exactly onto the plane, repeat groups | objects | yes (CPU; laptop) | **40 s on 4 laptop cores** (spawn pool per plane, 375 % CPU, 1.3 GB RSS; 20 face and 8 relief planes; was 368 s on ~1.2 cores) plus 20 s for the optional held-out check (44 s with `--hold-every 0`, the production setting), 0.13 MB of JSON | **R6 policy, rectangles only: aim miss 5.5 / 12.7, perfect aim 3.5 / 8.6, grade A 4.6 / 15.0** (row 0: 6.9 / 10.0); measurement 4.5 mm with aim miss (2.4 mm perfect aim); **upper door corners and seams 1.3–5.5 mm; door widths median |err| 1.1 mm (0.4 %)**; group regularization (equal widths, shared rows) does not help: door widths 1.1 to 0.9 mm but aim miss 5.5 to 5.9–6.9 mm, upper B right stays −4.5 mm, so it is off by default | held-out reprojection 1.36 px median, p90 4.6 px, 79 % of sides matched; 38 objects: all 10 upper doors, 2 drawers, 4 lower doors, dishwasher, microwave panels, outlets; **repeat groups: door width spread 0.8 / 1.7 / 8.1 / 12.9 mm (std 0.4–4 mm); cab B doors 0.4 mm vs GT 3.9 mm** | **S4 scored.** Needs fusion: its p90 comes from points with no rectangle (counter, lip, oven window, mw_bl). The mesh-texture orthophoto finds 1 rectangle against 45 from the frames. Output `structure/s4/structure.s4.json` (scene frame) is handed to S2 for the fused layer. Notes: `structure/s4-objects.md` | R5, R6, S4 |
| – | Deprioritised by R5: AirPlanes, DSINE, PGSR (non-commercial); PlanarGS, MonoSDF, Manhattan-SDF (30 min+ per scene); NEF, EMAP, EdgeGaussians (object-scale curves); Scan2CAD, ROCA, CAD-Recode (~20 cm tolerances); mesh denoisers (never exactly planar) | – | – | – | – | – | not run | R5 |

## 4. Method notes

Per-agent notes: `structure/r5-academic.md` (papers) and `structure/r6-industry.md` (products,
open source, community).

Cross-cutting findings so far:
- **Division of labour (R5):**
  - Learned priors decide which points belong to a plane. Monocular normals are 8–10° off at the
    median, too loose to place a plane but enough to group a textureless face.
  - A least-squares fit on MVS points places the plane.
  - Plane intersections give exact straight edges. Triangulated image lines give the edges where
    planes don't meet (door gaps, counter lips, window frames).
- **No product snaps to raw photogrammetry edges (R6).**
  - Every product either builds a parametric or segmented layer (RoomPlan, Hover, EagleView,
    ReCap/AutoCAD, Cyclone 3DR, Canvas) or has users draw vectors (Metashape, Pix4D).
  - Matterport tells users to measure "away from corners".
  - Polycam says photo-mode models are "not dimensionally accurate" without a rescale.
  - Vendor accuracy claims:
    - Polycam ±½ in, Canvas 1–2 %
    - Matterport Pro2 1 %, Pro3 20 mm at 10 m
    - Hover 2.6 % (secondary source), EagleView 0.2 ft on roof lines
  - Our scale error (the caption altitude is about 31 % off indoors) is bigger than all of them,
    so the app should let the user set scale from one known dimension in VR, as Canvas, Magicplan
    and Polycam do.
- **Snapping in Unity (R6 §7.3):**
  - Strict priority: corner, then edge, then plane, then mesh.
  - Tolerances: a corner within 1.5° of the ray or 2.5 cm of the tip, an edge within 1.2° or 2 cm,
    then a plane via analytic ray/plane plus point-in-polygon, as MRUK does.
  - A depth gate against the mesh raycast rejects corners hidden behind a wall.
  - Hysteresis at 1.5× the acquire tolerance.
  - Brute force over flat arrays is estimated at well under 0.1 ms on Quest.
  - P2 must check the glTF-to-Unity X flip with one known corner.
- **Industry practice (R6):** AutoCAD/ReCap point-cloud snaps and Leica Cyclone 3DR compute
  edges as plane∩plane and corners as the meeting point of 3 planes, rather than detecting them in
  the mesh.
  - Meta MRUK scene anchors are a local frame plus a 2D polygon with a semantic class (WALL_FACE,
    DOOR_FRAME, WINDOW_FRAME, ...).
  - MRUK raycasts analytically, with no collider.
  - Our structure JSON follows MRUK's plane representation and adds edges and corners, which MRUK
    lacks.
- **Error budget (R5, estimated):**
  - Image lines localise to about 1 mm at 1.5 m, and line depth to about 3 mm.
  - The MoGe-fill bias could be 2–10 mm.
  - Absolute measurements are dominated by metric scale (about ±6 % between cues) until we have
    a tape measurement, so scale-free metrics are reported too.

## 5. Findings and recommendation

### 5.1 Headline numbers

Scored with S1's scorer: R6 Unity snap policy, 20 image-triangulated kitchen points (grades A and
B), median / p90 mm. "Aim miss" means the pointer lands ±1 cm off the target. The mm are at the
caption-altitude scale, which is about 1.47× too small, so compare rows rather than reading them
as absolute errors.

| Configuration | Snap, aim miss | Snap, perfect aim | Tape measurement (aim miss) | Edge band | PSNR / SSIM |
|---|---|---|---|---|---|
| Raw hybrid mesh (today, row 0) | 6.9 / 10.0 | 2.4 / 4.7 | 4.1 mm | 16 mm | 21.85 / 0.811 |
| RefineMesh res0 mesh (row 0c) | 6.6 / 9.4 | **1.0 / 3.9** | 3.5 mm | **4 mm** | 21.77 / 0.797 |
| PxwPlanar planes only (row 3b) | 7.9 / 13.0 | 3.8 / 11.9 | 2.0 mm | – | – |
| S4 rectangles only (row 8) | 5.5 / 12.7 | 3.5 / 8.6 | 4.5 mm; door widths 1.1 mm | – | – |
| LIMAP lines only (row 2) | 2.8 / 10.0 | 2.5 / 7.9 | 2.9 mm | – | – |
| **Fused layer: lines + planes + rectangles, on the SfM cache (row 2g)** | **2.7 / 8.8** | 2.5 / 4.5 | **1.3 mm** (0.8 mm with perfect aim) | – | – |
| **Fused layer in the pipeline, fresh end-to-end run (row 2p)** | **3.6 / 11.4** | 3.2 / 7.9 | **1.3 mm** (0.8 mm, 0.45 %) | – | 21.58 / 0.803 |
| Mesh of that same fresh run | 6.8 / 9.8 | 2.3 / 5.1 | 3.7 mm | – | – |

### 5.2 What we learned

1. **Exact snapping needs a separate structure layer, not a better mesh.**
   - Every mesh-only change leaves the aim-miss median at about 6.5–7 mm, because a mesh snap
     keeps the tangential part of the miss.
   - Corners and edges in a structure layer pull the tip onto the feature: 2.7–3.6 mm, and tape
     measurements within 1.3 mm instead of 3.7–4.1 mm.
   - This matches industry practice (R6): RoomPlan, ReCap/AutoCAD, Cyclone 3DR and MRUK all snap
     to fitted planes, edges and corners, never to raw photogrammetry edges.
2. **Lines from the images are the most accurate edge source.**
   - LIMAP (LSD with our fixed COLMAP poses) reprojects at about 0.9 px and beats both plane
     intersections and rectangle corners at the GT points.
   - DeepLSD and GlueStick are no better and take twice as long.
3. **Planes matter as corner generators, not as snap targets.**
   - A line crossing a plane closes corners where the second line is missing.
   - Merging corners that share support took measurement error from 2.3 to 1.3 mm.
   - Snapping lines into planes made things worse, because the lines (~0.9 px) are more accurate
     than the planes (2–5 mm RMS).
   - A gravity-fixed Manhattan snap was essential: a free-axis version made angles worse.
4. **Rectangles give P3 its semantics.** They supply doors, drawers and cabinet fronts, plus
   repeat groups, and door widths come out within about 1 mm. Line corners should still win where
   both exist.
5. **Mesh-level sharpening:**
   - RefineMesh at full resolution without pre-decimation is the only change that sharpens the
     visual mesh (band 16 → 4 mm) at the same visual quality. It costs 466 s on 8 cores.
   - `--smooth 0` does nothing useful.
   - Snapping vertices to planes before texturing costs 0.3–1.1 dB, and its crease rules make the
     band wider.
6. **Rejected:**
   - PlanarSplatting: worse on every metric, and its rasterizer carries a non-commercial
     licence.
   - S1's plane baseline: marginal.
   - Group regularization of door sizes: it flattens real steps between cabinets.
   - Mesh vertex regularization.
7. **Build the planes while the OpenMVS depth maps still exist.** Planes lifted from mono depth
   only (the depth maps already pruned) made the fused layer's p90 worse on a fresh run. The
   pipeline step does this.
8. **The scale is still the largest error for absolute measurements** (about 31 % indoors). The
   structure layer makes a single tape measurement in VR enough to rescale the whole scene
   exactly.

### 5.3 Recommendation

- **Ship the structure layer (done, `71cf192`).**
  - The full stage writes `structure.r<rev>.json` (schema `airtools.structure/1`, glTF metric
    frame, +Y up) with planes, edges (`line`, `crease`, `boundary`), corners, objects and groups.
  - It is listed in `scene.json` and checked by `validate_package`.
  - `--structure on` is the default. It is skipped with a warning when the LIMAP or PxwPlanar env
    is missing.
  - Every sub-step runs in its own process group with a wall-clock timeout, so it can never block
    the mesh publish.
  - Cost: about 268 s in the background, overlapping densify and meshing, plus about 11 s after
    the mesh. The net wall-time cost is about 2 min (about 10 %) on 4 shared cores.
- **P3 snapping** follows the R6 §7.3 recipe:
  - priority corner (2.5 cm), then edge (2 cm; a corner wins only if within the edge distance
    plus 1 cm), then plane (3 cm, analytic ray/plane with point-in-polygon), then mesh;
  - a depth gate against the mesh raycast;
  - hysteresis at 1.5× the acquire radius;
  - brute force over flat arrays is enough;
  - check the glTF-to-Unity X flip with one known corner.
- **Offer RefineMesh res0 as an opt-in** for the full stage, for a visibly sharper mesh. Don't
  make it the default until it is faster (466 s on 8 cores).
- **Add a VR "set scale from a known dimension" action** that rescales the scene from one tape
  reading on structure corners.
- **Viewer:** `work/viewer/index.html` (served locally) shows the mesh with lines, creases,
  rectangles, corners and planes, plus a tape tool that uses the R6 snap policy.

### 5.4 Next steps

- Run the pipeline on building-exterior footage: windows, facades, and a longer baseline.
- Plane position for textureless fronts (the dishwasher and microwave may sit 1–2 cm off): sweep
  the plane offset for photo-consistency between frames.
- Add a 2D detector for object labels (currently a size heuristic), and an oven-front plane.
- Tape-measure the kitchen to settle both the scale and the doubtful GT pair (upper cab B doors).
- Speed up RefineMesh on 16 cores, or run it on the GPU, before considering it as a default.
