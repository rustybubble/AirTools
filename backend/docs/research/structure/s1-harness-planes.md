# S1: structure harness, mesh-free kitchen GT, baseline planes and mesh regularization

Code:
- `pipeline/experiments/structure/`: `gt.py`, `score.py` + `__main__.py`, `planes.py`, `regularize.py`
- GT: `kitchen0095/gt.json`
- Tests: `tests/pipeline/test_structure_score.py`

Schema: `schema.md`. Results:
- local: `work/s1/results_r6.jsonl`, `work/s1/results_r6_sweep.jsonl`
- hfbox: `/workspace/bench/structure/results_r6.jsonl` (older runs are in `results.jsonl`)

## 1. Ground truth (independent of any mesh)

Scene: kitchen DJI_0095, GT frame = `scene/bench/i-hybrid-moge2` (`cameras.r1.json`). Its altitude
scale is ~1.47× too small indoors (the dishwasher reads 414 mm), so compare rows, or use ratios.

The GT is built in four steps:
1. **Pick.** Each point is picked coarsely by eye in 1–3 anchor frames (`picks.json`, OpenCV px on the
   hfbox `frames/%04d.jpg`; local ffmpeg decodes differ byte-wise).
2. **Refine.** Each pick is refined to the LSD line intersection near it: two line families, TLS
   fits, sub-pixel. The unit test recovers a synthetic corner to < 0.3 px.
3. **Track.** KLT tracks each point through the 10 fps frames (forward-backward check < 0.7 px) and
   re-refines it at the SfM frames. Each point is then densified by projecting it into every SfM
   frame and refining within 3 px.
4. **Triangulate.** Pixels are mapped SIMPLE_RADIAL → pinhole, then RANSAC runs over view pairs
   (1.5 px) and Gauss-Newton refines the point.

Quality is judged by σ at 0.5 px from the Jacobian and by split-half agreement:
- grade A: σ < 1 mm and ≥ 10 views;
- grade B: σ < 5 mm;
- anything worse is dropped.

Result: 22 points, 4–124 views (median 58), reprojection RMS 0.40–0.84 px, σ median 0.35 mm,
split-half median 0.28 mm.

**Cross-check downgrades** (lead's request, after S2/S4 disagreed):

| point | evidence | grade |
|---|---|---|
| ua_bl | Frame-451 track sits 4.4 px from the anchor refinement, which looks like two junctions (the door corner vs the carcass behind it). It is 17 mm deeper than ua_seam/ua_br (S4: 16 mm behind the door plane). Nearest LIMAP edge 20 mm (S2); raw mesh 10.6 mm. Highest RMS (0.81 px) | **C** |
| dw_tr | Textureless panel top under the shadowed overhang gap. 38 of 63 views rejected; split-half 1.39 mm. Implies a 2.9° dishwasher yaw that dw_tl, the drawer face (depths −1134 / −1131 mm), the mesh (14.9 mm off) and S4 don't show | **C** |
| dw_tl | Split-half 0.07 mm, mesh 2.7 mm, depth consistent with the drawer and counter | stays A |

- Grade C points are excluded from snap and measurement stats (`--grades AB`, now 16 A + 4 B).
  Pairs and GT edges that use them are dropped.
- The ratio reference moved from the dishwasher width to "upper cab B width".
- Plane regions still use every point. The ua_front region is therefore slightly tilted by ua_bl;
  this is a known small effect on planarity and angle for that one region.

## 2. Scorer

`python -m pipeline.experiments.structure score <scene> --gt gt.json [--structure s.json] [--frames dir] [--mesh alt.glb] --out r.jsonl --name N`

Takes about 2 s without `--frames`.

- **Alignment:** the scene's camera centres are Sim(3)-aligned onto the GT track by video time. A
  `"frame": "cameras"` structure is aligned by its own cameras.
- **Snap:**
  - Both policies are reported. The headline is **R6 §7.3**: corner ≤ 2.5 cm, else edge ≤ 2 cm
    (a corner beats an in-range edge only if d_c ≤ d_e + 1 cm), else plane ≤ 3 cm inside its
    polygon, else collision-mesh `ClosestPoint`. The old policy is corner > edge > plane, all
    within 3 cm.
  - Each policy is scored with perfect aim and with an aim miss (uniform ±1 cm ball, 16 samples per
    point).
  - Also reported: grade-A-only stats and GT-edge line distance.
- **Measurement:**
  - |snapped pair length − GT| over the GT pairs, with perfect aim and with aim miss;
  - ratio error against the reference pair;
  - repeat-group spreads.
- **Planarity:** mesh vertex RMS inside the GT plane regions (15 mm inset, 2 cm band).
- **Angles:** angle error on GT angle pairs (⊥ / ∥).
- **Sharpness:** width of the normal-turn band across each GT edge. Faces are binned by distance,
  each side has its own flat level at 3–6 cm, the band ends at 75 % of the turn, and bins are 4 mm.
- **LSD reprojection:** structure edges are reprojected into SfM frames with id ≡ 1 mod 25 (exact
  poses), giving the median px to LSD segments and strong-LSD recall. Interpolated poses were
  16 px off, which is useless.

## 3. Baseline plane layer (`planes.py`)

- **Input:** mesh surface samples (800k plus face normals). MVS points were not used because they
  miss cabinet undersides: the front face bleeds past the edge.
- **Axes:** gravity axis plus a dominant yaw, with Manhattan snapping.
- **Detection:** per signed axis, offset-histogram peaks (1 mm bins), then connected components on
  a 1 cm grid, then convex-hull polygons.
- **Creases:** plane∩plane creases, where support is present on both sides (convex or concave),
  clipped to support and extended by 2 cm.
- **Corners:** crease ∩ third-axis plane. Edge ends snap to corners.
- **Runtime:** 1.3 s.

## 4. Results (20-point GT, R6 policy; median / p90 mm)

| row | perfect aim | ±1 cm aim | grade A aim | old policy aim | measurement (aim) | planarity | angle | edge band | PSNR / SSIM |
|---|---|---|---|---|---|---|---|---|---|
| 0 i-hybrid-moge2 (local) | 2.4 / 4.7 | 6.9 / 10.0 | 7.0 / 10.0 | same | 4.1 | 6.1 | 1.08° | 16 | – |
| 0 s1-smooth2 (hfbox control) | 2.5 / 4.7 | 7.0 / 10.0 | 7.1 / 10.0 | same | 4.1 | 6.1 | 1.08° | 16 | 21.85 / 0.811 |
| 0b s1-smooth0 | 3.1 / 4.5 | 6.9 / 9.7 | 6.9 / 9.7 | same | 4.3 | 6.4 | 1.84° | 20 | 21.56 / 0.770 |
| 1 planes eps 5 mm | 3.8 / 7.9 | 7.2 / 10.6 | 7.2 / 10.9 | 7.5 / 22.2 | 4.1 | – | – | – | – |
| 1b planes eps 12 mm | 6.8 / 17.8 | 8.4 / 18.2 | 8.9 / 17.8 | 8.9 / 25.3 | 4.4 | – | – | – | – |
| 1 planes eps 8 mm, min 300 (best) | 4.2 / 6.7 | 6.6 / 9.7 | 6.6 / 9.9 | 6.6 / 10.2 | 3.9 | – | – | – | – |
| 0c RefineMesh res1 auto-decimate (143k) | 1.7 / 4.3 | 6.6 / 9.4 | 6.7 / 9.5 | same | 3.5 | 5.0 | 1.66° | 10 | 21.70 / 0.702 |
| 0c RefineMesh res1 `--decimate 1` | 1.4 / 5.3 | 7.0 / 9.6 | 6.8 / 9.5 | same | 3.7 | 6.1 | 1.89° | 10 | 21.42 / 0.770 |
| **0c RefineMesh res0 `--decimate 1`** (466 s) | **1.0 / 3.9** | 6.6 / 9.4 | 6.6 / 9.5 | same | 4.2 | 6.2 | 1.30° | **4** | **21.77 / 0.797** |
| 7 reg e5 tol 3 cm | 2.8 / 5.1 | 7.1 / 10.0 | 7.1 / 9.9 | same | 4.2 | 5.0 | 0.44° | 10 | 20.55 / 0.767 |
| 7 reg e5 tol 1 cm | 2.7 / 5.1 | 7.0 / 10.0 | 7.1 / 10.1 | same | 4.2 | 5.8 | 0.53° | 10 | 21.29 / 0.781 |
| 7 reg e12 tol 3 cm | 2.9 / 5.7 | 7.0 / 10.0 | 7.1 / 9.9 | same | 4.3 | 5.1 | 1.21° | 20 | 20.38 / 0.752 |
| 7 reg e12 tol 1 cm | 2.8 / 5.7 | 7.1 / 10.0 | 7.1 / 10.0 | same | 4.2 | 5.6 | 0.91° | 24 | 21.12 / 0.775 |
| 0d `--max-views 4` (278 s) | 1.6 / 4.2 | 6.6 / 9.5 | 6.6 / 9.6 | same | 4.0 | 6.0 | 0.65° | 10 | 21.69 / 0.791 |
| 0d `--scales 3 --max-views 6` (398 s) | 1.2 / 3.9 | 6.5 / 9.3 | 6.5 / 9.4 | same | 3.7 | 6.1 | 0.69° | 12 | 21.60 / 0.791 |
| 0d `--max-views 6` (404 s) | 1.3 / 4.2 | 6.6 / 9.4 | 6.6 / 9.4 | same | 4.2 | 6.0 | 1.02° | 10 | 21.79 / 0.797 |
| 0d `--fast` (457 s) | 1.2 / 3.9 | 6.5 / 9.6 | 6.6 / 9.7 | same | 3.5 | 6.1 | 1.79° | 4 | 21.60 / 0.779 |
| 0d `--planar-vertex-ratio 0.02` (465 s) | 1.3 / 3.6 | 6.6 / 9.5 | 6.6 / 9.7 | same | 4.6 | 6.1 | 1.25° | 4 | 21.76 / 0.797 |
| 0d `--scales 3` (494 s) | 1.6 / 4.0 | 6.2 / 9.5 | 6.2 / 9.4 | same | 3.5 | 6.2 | 0.81° | 4 | 21.68 / 0.792 |
| 0d `--scales 1` (905 s) | 1.0 / 4.9 | 6.6 / 9.5 | 6.5 / 9.4 | same | 4.0 | 6.0 | 1.25° | 16 | 21.59 / 0.792 |
| oracle (GT as structure) | 0 | 0 | 0 | 0 | 0 | – | – | – | LSD 1.3 px |

- Rows 7 are the regularized meshes, scored as mesh only. Planarity is in mm and the edge band in mm.
- PSNR/SSIM come from E1's bench (`/workspace/bench/score.sh`, ref `/workspace/bench/ref`).
- The old 22-point / old-policy numbers are in `results.jsonl`. For example, row 0 was 2.5 / 8.9
  with perfect aim and 7.3 / 11.8 with aim miss; `--grades ABC` reproduces them exactly.

## 5. Findings

Verified (runs above):
- **The raw mesh is a strong baseline:** 6.9 / 10.0 mm with a ±1 cm aim miss. The aim miss itself
  dominates: a mesh snap keeps the tangential part of the miss, so only corner and edge snaps can
  beat it.
- **The S1 plane layer does not beat the mesh.** Its corners are too few (recall 0.14 at eps 5 mm)
  and wrong creases capture snaps. R6's 2 cm edge radius cuts the old-policy p90 damage from
  22 to 10.6 mm.
- **RefineMesh (row 0c) at full resolution without pre-decimation sharpens edges:**
  - edge band 16 → 4 mm; perfect-aim snap 2.5 → 1.0 mm; GT-edge 3.2 → 1.4 mm;
  - cost: −0.08 dB PSNR and −0.014 SSIM, plus 466 s of RefineMesh on 8 cores;
  - it's the best mesh-level change measured. The develop build's default auto-decimation
    (to 80k faces) is what drops SSIM to 0.70, so pass `--decimate 1`.
- **RefineMesh speed (row 0d):** no variant keeps band ≤ 5 mm and SSIM ≥ 0.795 below 466 s.
  - Fewer views (`--max-views 4/6`) save up to 40 % of the time but widen the band to 10–12 mm.
  - `--scales 3` doesn't save time; `--scales 1` doubles it, because every iteration then runs at
    full resolution.
  - `--fast` loses SSIM.
  - `--planar-vertex-ratio 0.02` changes nothing (same vertex count), so it doubles as a replicate
    of 0c: 465 s, band 4 mm, SSIM 0.797.
  - PSNR noise between equivalent runs is about ±0.15 dB.
- **`--smooth 0` gives nothing.** The edge band doesn't narrow (20 vs 16 mm), and it costs 0.3 dB
  and 0.04 SSIM. Keep the default.
- **Regularizing the mesh onto the S1 planes improves geometry but fails the visual check:**
  planarity 6.1 → 5.0 mm, angle 1.08° → 0.44°, edge band 16 → 10 mm, but PSNR drops by 0.56–1.3 dB
  and snapping doesn't change.

Inferred (not separately tested):
- The plane layer's losses come from:
  - mesh undersides sitting about 8 mm off (seen when fitting);
  - fronts merging across small depth steps;
  - Manhattan forcing on fronts that aren't quite parallel (ua 2.2°, dw 2.9°; the dw figure may
    itself be the dw_tr GT error).
- The PSNR drop is most likely texture misregistration where vertices moved (median 3 mm) while the
  texture stayed put. Re-texturing after regularization might recover it; this wasn't tried.
- Mesh regularization is worth retrying only with a cleaner structure layer (S2f fusion or S4
  rectangles). Even then, snapping gains must come from the structure layer, not the mesh.

- **Mesh regularization now belongs to S3** (lead's call). S3 regularizes union_mesh.ply in COLMAP
  space and then re-textures. The rows 7 above are S1's snap-only first pass and are not being
  pursued further.

Gravity convention: every scorer "up" is +Y of the GT frame (`cameras.r1.json` of
i-hybrid-moge2, i.e. the glTF +Y). Other scenes and cameras-frame layers are Sim(3)-aligned onto it
through their camera centres. This is the same convention as S3. The angle metrics compare plane
pairs (⊥ / ∥), so they don't depend on gravity except through how the GT regions are built.
