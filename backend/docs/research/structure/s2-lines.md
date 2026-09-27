# S2: 3D line and edge map from the images (LIMAP)

Row 2 of `../p2-structure-bench.md`. The goal is straight 3D edge segments and their junctions,
recovered from multi-view line detections on our fixed COLMAP poses, so that tools snap to real
edges and corners independently of the smooth mesh.

## Setup

- **LIMAP 2.0.0** is on PyPI as `pylimap`, with manylinux wheels for cp310–312. It is built against
  COLMAP/pycolmap 4.2.0, reads our model directly, and needs no C++ build.
  - **Laptop:** a uv venv with py3.12, then `pylimap`, CPU torch and LIMAP's `requirements.txt`.
    GlueStick pins `opencv-python==4.7`, which breaks under numpy 2. Re-install
    `opencv-python>=4.10` afterwards.
  - **hfbox:** `/workspace/envs/limap` (0.9 GB).
    - The `--system-site-packages` flag does **not** expose `/opt/venv`, because that is a venv
      itself. A plain pip install then pulled 3.9 GB of CUDA torch into the venv and filled the
      disk twice; I deleted it again.
    - The working recipe is a `zz_optvenv.pth` file containing `/opt/venv/lib/python3.12/site-packages`,
      which gives the ROCm torch. After that, install the git requirements with `--no-deps`
      (without pxwplanar), plus `opencv-python-headless`, and set `TMPDIR` on /workspace.
  - Clone the LIMAP git checkout (`/workspace/tools/s2-limap/limap-src`) for `cfgs/`.
- **Runner:** `pipeline/experiments/lines/limap_run.py`.
  1. It copies `sfm/dense/sparse` and deregisters the bench's held-out frames (ids ≡ 1 mod 25,
     33 of 165).
  2. It runs `limap.cli.automatic_point_line_triangulation`. Groups are off; poses stay fixed.
  3. It exports the raw 3D lines (`raw_lines.json`) and post-processes them into
     `structure.json` (all edges) and `structure_axis.json` (the structural subset). Both use
     S1's `airtools.structure/1` schema with `frame: "cameras"`.
  - `--skip-limap` re-runs only the post-processing from `raw_lines.json`, and needs no LIMAP venv.
- **Post-processing** (`structure.py`, tested on a synthetic box in
  `tests/pipeline/test_lines_structure.py`):
  1. Keep lines with 4 or more views and at least 3 cm long.
  2. Find the Manhattan axes by a greedy length-weighted mode plus a Procrustes refit.
  3. Snap lines within 5° of an axis onto that axis exactly, pivoting about the midpoint.
  4. Merge collinear lines: within 3°, both endpoints within 6 mm of the line, gap under 5 cm.
     The fit is length-weighted PCA.
  5. Place junctions at the line-line closest point, not at endpoints, since occlusion cuts
     endpoints short. Two lines qualify when they pass within 1 cm of each other, and the
     junction lies on both segments or no more than 6 cm past an end. Pairs merge greedily into
     multi-edge corners only while the least-squares residual stays at 5 mm or less, and never
     with parallel twins.
  6. Extend edge ends onto their corners.
  - **Structural subset:** on-axis edges only, with at least 6 views and at least 10 cm long.
  - Metric tolerances assume 0.26 m per COLMAP unit, between the MoGe-2 and object-scale cues.
    Nothing is sensitive to that choice at the ±20 % level.
- **Evaluation:**
  - S1's scorer, with GT from 22 image-triangulated corners.
  - `pipeline/experiments/lines/evaluate.py`: it projects the edges into the 33 held-out frames and
    matches them to LSD lines within 3° and more than 50 % overlap. It reports the median distance
    of matches within 10 px, and the recall of strong image lines (60 px or longer) within 5 px.
    There is no occlusion test.

## Results (kitchen-0095, hfbox SfM cache, 165 frames)

The scorer mm values are at the altitude scale, which is about 1.47× too small. Compare rows.

| Variant | Time | Edges / corners | Snap, perfect aim (median / p90 mm) | Snap, ±1 cm aim | Measurement, aim miss | GT-edge | Scorer LSD px / recall | Own held-out px (median / p90), matched, recall |
|---|---|---|---|---|---|---|---|---|
| Row 0: raw mesh ClosestPoint | – | – | 2.5 / 8.9 | 7.3 / 11.8 | 4.0 mm | – | – | – |
| LSD, all edges | 135 s + 4 s | 481 / 611 | 2.6 / 18.5 | 2.9 / 19.3 | 3.1 mm | 2.4 mm | 1.9 / 0.69 | 0.90 / 3.6, 73 %, 0.79 |
| **LSD, axis subset** | 135 s + 1 s | 159 / 137 | 2.6 / 18.6 | **2.9** / 19.4 | **2.1 mm** (0.71 %) | 2.4 mm | 1.2 / 0.56 | 0.89 / 2.9, 84 %, 0.67 |
| DeepLSD + GlueStick, axis subset | 304 s + 1 s | 174 / 145 | 3.4 / 19.0 | 4.0 / 19.7 | 1.8 mm | 2.9 mm | 1.6 / 0.59 | – |

About the timings:
- The times are LIMAP on hfbox, `taskset -c 8-11`, with the GPU used for SIFT/ALIKED matching and
  DeepLSD.
- Laptop, 16 CPU cores, LSD: 320 s.
- LIMAP spends most of its time re-extracting and matching points (hloc SIFT) for its point
  triangulation. Line detection and triangulation are the smaller part.

The first map (laptop, the 175-frame local SfM, every 10th frame held out, 15 mm merge, 2 cm
corner clustering) had these figures:
- held-out median 1.01 px;
- 384 edges and 257 corners;
- scored on the hfbox model: 4.4 / 15.4 mm with perfect aim, 4.5 / 15.5 mm with a ±1 cm aim miss,
  and 2.2 mm measurement error.

Tolerance sweep on the LSD map (±1 cm aim, median / p90):

| Setting | Result |
|---|---|
| Merge 15 mm | 4.1 / 19.5 |
| Merge 6 mm, corners at 2 cm | 4.4 / 19.5 |
| Merge 6 mm, corners at 1 cm | 2.9 / 19.4 |
| Merge 6 mm, corners at 5 mm | 3.2 / 19.5, recall 0.88 |

With n = 22, differences under about 1 mm are noise.

## Findings

1. **Structural line maps work out of the box on our poses.** Cabinet door edges and seams, the
   counter front, the fridge, the dishwasher and the upper-cabinet undersides all come out as clean
   on-axis lines (figures below), about 0.9 px from held-out LSD lines. That is below the GT
   corners' own 0.4–0.9 px reprojection RMS. DeepLSD and GlueStick bring no gain here, at twice
   the time.
2. **Corners help most when the aim misses.** With a ±1 cm miss the median drops from 7.3 mm
   (mesh) to 2.9 mm, and measurement error from 4.0 to 2.1 mm, because a snapped corner is the
   corner itself. With perfect aim the median equals the mesh (2.6 vs 2.5 mm).
3. **The p90 is worse than the mesh (about 19 vs 12 mm), for two reasons:**
   - **Coverage misses.** For ua_bl and ctr_fr the nearest line is 17–20 mm away: that GT edge
     was not triangulated, or LSD picked the other side of the counter nose.
   - **A wrong corner beats a correct edge.** mw_bl has an edge 1 mm away, but the microwave's
     left vertical edge is missing. A cabinet-side corner 2 cm away then wins under the
     corner > edge priority.

   Candidate fixes, in order:
   - keep only corners supported by S1/S3 planes, or by 3 edges;
   - prefer an edge over a corner when the edge is much closer;
   - use line-map corners only where the mesh agrees within about 1 cm.
4. **Clutter** (bottles, the sink sign, stove knobs) gives off-axis lines and dense spurious
   corners. The axis subset removes most of it, costs 0.13 strong-line recall, and improves
   measurement.
5. **Caveat:** S1's GT corners are also LSD-line intersections, so LSD-based lines share any bias
   the detector has. Tape measurements would settle this.

## Fusion with S3's planes (row 2f)

`pipeline/experiments/lines/fuse.py` combines the LSD line map (all edges) with S3's PxwPlanar
layer. The planes file is `hfbox:/workspace/s3/pxw/structure.json` as of 15:32 (Manhattan 3°,
53 planes), in the same COLMAP frame. It runs in about 2 s on the laptop. A toy test covers it in
`tests/pipeline/test_lines_structure.py`.

1. **Tag lines.** A line is attached to a plane when both ends are within 1 cm of it, it runs
   parallel to it (within 8°), and its ends and midpoint fall inside the polygon grown by 5 cm.
   Two planes more than 30° apart make it a crease. On this map 142 of 481 lines are attached and
   339 are free.
2. **Snap.** Attached lines are projected into their plane, or onto the plane∩plane line for a
   crease.
3. **Corners.**
   - line × line (the residual-gated junctions above);
   - line × plane where an attached line ends within 6 cm of another plane, inside its polygon;
   - free line × line corners are dropped when any of their edges is off-axis.
4. **Creases.** S3's plane∩plane edges are added when no line runs along them (30 added). Each
   end moves to the nearest third-plane or attached-line crossing within 6 cm, instead of
   stopping where the plane support ends.

| Variant | Snap, perfect aim | Snap, ±1 cm aim | Measurement, aim miss | GT-edge | Corners |
|---|---|---|---|---|---|
| Lines only (row 2, all) | 2.6 / 18.5 | 2.9 / 19.3 | 3.1 mm | 2.4 mm | 611 |
| Lines + planes, concatenated only | 2.6 / 18.5 | 2.9 / 19.3 | 3.1 mm | 2.4 mm | 611 |
| **Light:** + line × plane corners | 2.6 / **10.5** | 2.9 / 16.4 | 2.6 mm | 2.4 mm | 643 |
| **Full:** + snapping, creases, off-axis free-corner drop | 2.9 / **10.5** | 3.2 / **14.3** | **1.2 mm** (0.51 %) | 2.4 mm | 329 |
| Full, but every free corner dropped | 2.6 / 16.1 | 5.7 / 16.3 | 3.4 mm | 1.8 mm | 171 |
| S3 planes alone (row 3 v0) | 9.2 / 26.0 | 10.0 / 26.1 | 3.3 mm | 3.5 mm | 2–9 |

The table is from the scorer; mm are at the altitude scale. The rows up to "every free corner
dropped" were measured on S3's earlier planes file, except Light and Full, which use the 15:32
file (hfbox rows `s2-fuse-*`). On the earlier file, Full scored 2.9 / 10.8, 3.8 / 16.3 and 1.5 mm.

What the fusion shows:
- **Planes as snap targets add nothing** over the lines. The concatenated row equals lines only,
  because corners and edges win first.
- **The gain comes from planes closing corners.** A line that meets a plane gives a corner exactly
  where LIMAP had no second line, as for mw_bl and ctr_fr. That halves the perfect-aim p90.
- **Only a third of the lines are attached**, because S3's planes cover the big faces but not the
  door seams or the upper-cabinet undersides. Dropping *all* free corners therefore loses corner
  recall (0.69) and doubles the aim-miss median. Filter only the off-axis ones.
- **Aim-miss p90 is 14 mm against 12 mm for the mesh.** The remaining misses are near-duplicate
  corners 1–2 cm apart, where the corner > edge priority picks the wrong one. S1's planned Unity
  tolerances (corner 2.5 cm, edge 2 cm, hysteresis) should reduce these. Re-score when they land.
- **Row naming:** the rows named `pxw0` actually used S3's 15:32 output. By md5, that file is
  the one later published as **v6** at `/workspace/s3/pxw/structure.json`, not
  `/workspace/s3/sweep/v0.json`. The "earlier planes file" rows used S3's first output.

### Fusion v2 under the R6 snap policy (20-point GT)

S1's scorer (`40a2e9c`) now uses the R6 Unity policy as its headline:
- snap to a corner within 2.5 cm, else to an edge within 2 cm;
- a corner beats an in-range edge only when it is at most 1 cm further away;
- otherwise snap to a plane within 3 cm, else to the mesh.

Two GT points are downgraded to grade C and excluded. The fusion also gained one step:
- **Support-sharing corner merge.** Corners closer than 1 cm that share a line or plane are
  merged. The result sits at the least-squares point over the union of their constraints: line
  perpendicular distances plus plane normal distances, with a weak prior to their mean.
- A merge is skipped if its residual exceeds 5 mm, and corners with disjoint support are never
  merged. This keeps a counter-lip corner and a door-face corner 13 mm apart separate.

In the table, the first three R6 columns are median / p90 mm. "Measurement" is measurement error
with aim miss; "Measurement error" is with perfect aim.

| Row (hfbox `results.jsonl` name) | R6 aim miss | R6 perfect | R6 grade A, aim | Measurement | Measurement error | GT-edge | Corners |
|---|---|---|---|---|---|---|---|
| Row 0, mesh (S1) | 6.9 / 10.0 | 2.4 / 4.7 | 7.0 / – | 4.1 mm | 0.7 mm | 3.4 mm | – |
| `s2-limap-lsd-axis-r6` | 2.8 / 11.7 | 2.5 / 7.9 | 2.8 / 9.5 | 2.1 mm | 1.4 mm | 1.7 mm | 137 |
| `s2-limap-lsd-all-r6` | 2.8 / 10.0 | 2.5 / 7.9 | 2.8 / 8.8 | 2.9 mm | 2.8 mm | 1.8 mm | 611 |
| **`s2-fuse-lsd+pxw6`** (v2 default) | **2.7 / 8.4** | **2.5 / 4.5** | 2.8 / 8.0 | **1.3 mm** | **0.8 mm** (0.33 %) | 1.8 mm | 515 |
| `s2-fuse-snap-lsd+pxw6` (first recipe) | 2.9 / 10.0 | 2.9 / 6.7 | 2.9 / 8.1 | 1.5 mm | 0.9 mm | 2.1 mm | 263 |
| v2 without the corner merge (laptop) | 2.8 / 8.8 | 2.5 / 4.5 | 2.7 / 7.9 | 2.3 mm | 2.3 mm | 1.8 mm | 685 |
| v2 without creases (laptop) | 2.8 / 8.4 | 2.6 / 4.5 | 2.8 / 8.2 | 1.3 mm | 0.8 mm | 1.8 mm | 484 |
| v2 with off-axis free corners dropped (laptop) | 2.6 / 9.5 | 2.5 / 5.0 | 2.7 / 7.7 | 1.3 mm | 0.8 mm | 1.7 mm | 288 |

- **v2 recipe.** Keep the lines where LIMAP put them, and keep all their corners. Add line × plane
  corners and the missing creases, then merge the duplicates.
  - Snapping lines into S3's planes moves them 1–3 mm the wrong way. LIMAP lines sit at about
    0.9 px, while the plane RMS is 2–5 mm, so that step is off by default (`snap_lines=True` to
    restore it).
- **Result against the mesh.**
  - The aim-miss median drops from 6.9 to 2.7 mm, and the p90 is now also better (8.4 vs 10.0).
  - Perfect aim ties the mesh (2.5 / 4.5 vs 2.4 / 4.7).
  - Measurement error with aim miss drops from 4.1 to 1.3 mm.
- **Cost.** The fuse takes 30 s on hfbox cores 8-11 (10 s without the merge). The merge is an
  O(n²)-per-pass loop, which is fine at a few hundred corners.

### S4 rectangles merged in (`lines/rects.py`)

S4's `structure.s4.json` (e2bd1e7) is in the scene frame, with 40 objects and 160 rect corners
and sides. It is merged into the scene-frame fused layer:
- A rect side that runs along a LIMAP line (within 3°, both ends within 1 cm of it) takes that
  line's position: 94 of the 160 sides. The four corners are then re-intersected from their two
  sides.
- LIMAP corners win within 1 cm.
- S4's `s4-relief` sub-planes, objects[] and groups[] are kept; its copies of S3's planes are
  dropped.

In the table, the R6 columns are median / p90 mm and "Measurement" is with aim miss. Door spread
is the width spread of the upper cabinet B doors; the GT spread is 3.9 mm.

| Variant (20-pt GT) | R6 aim miss | R6 perfect | Measurement | GT-edge | Door spread |
|---|---|---|---|---|---|
| Fused alone (`s2-fuse-lsd+pxw6-scene`) | 2.7 / 8.8 | 2.5 / 4.5 | 1.3 mm | 1.8 mm | 7.9 mm |
| **Fused + rects, LIMAP corners win** (`s2-fuse-lsd+pxw6+s4rects`) | 2.7 / 8.8 | 2.5 / 4.5 | 1.3 mm | **1.6 mm** | 7.9 mm |
| Fused + rects, rect corners win | 3.4 / 11.1 | 3.1 / 8.4 | 1.8 mm | 1.8 mm | 3.8 mm |
| Same, rect sides not snapped to lines | 4.0 / 11.3 | 3.0 / 8.4 | 2.5 mm | 1.8 mm | 0.1 mm |
| S4 rects alone | 5.5 / 11.7 | 3.4 / 8.5 | 4.5 mm | 2.2 mm | 0.1 mm |

What this shows:
- **At the GT points, LIMAP corners are more accurate than rect corners.** Letting the rects win
  costs 0.7 mm of median and 2.5 mm of p90.
- **Snapping the sides to lines still halves the rect error** (4.0 → 3.4 median).
- **With LIMAP corners winning, the rects cost nothing on snap and add object topology:** door
  and drawer outlines, `object` refs and repeat groups for P3's fit checks and arrays. Edge snap
  also improves slightly (1.8 → 1.6 mm).
- **The rect corners also fix repeatability.** The door-width spread matches GT (3.8 vs 3.9 mm)
  when they are used, against 7.9 mm from LIMAP corners. Unsnapped rects give 0.1 mm, which is
  suspiciously uniform.

### Scene-frame output (`lines/frame.py`)

`to_scene(doc, M)` applies the pipeline's raw-to-scene similarity. That is
`cameras_matrix = scale @ align_matrix` in `run.py`, the same matrix applied to the mesh and the
cameras. It moves edges, corners, plane normals, offsets and polygons, and the axes, and writes
`"frame": "scene"`.
- For a published run, `M` is recovered from `cameras.r<rev>.json` against the COLMAP model by
  Umeyama. The residual is 2.5e-16 m, so it is exact.
- The scene-frame file scores identically to the cameras-frame file: the largest per-point snap
  difference is 1e-12 mm (`s2-fuse-lsd+pxw6-scene`).
- The pipeline does exactly this: see "Pipeline integration" below.

### Generalization to a fresh pipeline run (i-e2e-fixed)

The same recipe was run on the default end-to-end run's own SfM (`work/bench/i-e2e-defaults`,
175 frames, a different COLMAP gauge from the cache), and scored against the same GT; the
scorer's camera-track alignment residual is 2.1 mm. That run kept no OpenMVS depth maps, so S3's
lift ran **mono-only**, and its planes are weaker: 71 of 443 lines attached, against 142 of 481
before.

In the table, the R6 columns are median / p90 mm and "Measurement" is with aim miss.

| i-e2e-fixed, 20-pt GT | R6 aim miss | R6 perfect | Measurement | GT-edge |
|---|---|---|---|---|
| Mesh (row 0 on this run, `s2-e2e-mesh-baseline`) | 6.5 / 9.6 | 3.1 / 4.5 | 3.3 mm | 1.7 mm |
| LIMAP lines only (`s2-e2e-limap-lsd-all`) | 3.4 / 10.0 | 2.2 / 6.1 | 2.7 mm | 1.3 mm |
| PxwPlanar mono-only planes (`s2-e2e-pxw-mono`) | 8.5 / 13.6 | 4.2 / 13.0 | 6.5 mm | 2.4 mm |
| **Fused** (`s2-e2e-fuse-lsd+pxwmono`) | **3.4 / 11.3** | **2.3 / 7.4** | **1.5 mm** | 1.9 mm |

- **The line map generalizes.** On a fresh SfM the aim-miss median still halves against the mesh,
  and measurement error is 2.2× lower.
- **The perfect-aim p90 does not generalize yet.** It is 7.4 against the mesh's 4.5, because the
  mono-only planes close fewer corners.
- In the pipeline, the fusion should run while OpenMVS's depth maps still exist, or the pipeline
  should keep them for S3's lift.

### Cost

hfbox, `taskset -c 8-11`:

| Stage | kitchen cache (165 frames) | fresh run (175 frames) |
|---|---|---|
| LIMAP, LSD (point re-matching on the GPU) | 135 s | 182 s |
| PxwPlanar inference (GPU) | – | 55 s |
| PxwPlanar lift | – | 59 s |
| Fuse | **6 s**, down from 32 s | 14 s |
| Scene transform + rects | 1.6 s | – |

- **Fuse speed-up:** the corner merge now takes candidate pairs from a KD-tree and runs one greedy
  pass per round. The old loop restarted after every merge. The result changes slightly: 511 vs
  515 corners, and aim p90 8.8 vs 8.4, which is noise.
- **Estimated wall time at 16 cores** (not measured, because the cores are shared): about
  80–100 s for LIMAP (line detection and triangulation scale with cores; the point re-matching is
  GPU-bound) and about 30 s for the lift.
  - Total: roughly 2.5–3 min on top of the pipeline's ~10 min.
  - The laptop, with 16 cores and no GPU, took 320 s for LIMAP.

## Pipeline integration (`pipeline/structure.py`)

The full stage now publishes `structure.r<rev>.json` next to `mesh.r<rev>.glb`, in the scene frame,
and adds `"structure": {"file", "schema", "edges", "corners", "planes", "objects"}` to
`scene.json` (null when the layer did not run).
- **When it starts:** `mvs.run_mvs(on_densified=...)` calls back once DensifyPointCloud has
  written its depth maps. From there a background thread runs:
  - LIMAP LSD (no holdout) on the undistorted model;
  - PxwPlanar inference on the GPU, then S3's v6 MVS lift on the depth maps. The PxwPlanar cache
    (~0.4 GB) is deleted after the lift.
  - the fusion.
  All of this overlaps the hybrid fill, ReconstructMesh and TextureMesh.
- **After the mesh:** once `cameras.r<rev>.json` is written, the stage runs
  `to_scene(cameras_matrix)`, then S4's rectangles on the written mesh
  (`--hold-every 0`), then `merge_rects`, then writes the file before `scene.json`.
- **Rect corners:** they are now kept only where both sides run on LIMAP lines. On this run an
  unsupported S4 corner, 5.6 mm off at the microwave, outranked a 1.3 mm edge under R6 and moved
  the aim median from 3.6 to 4.5 mm. Rect sides, objects and groups are all kept. On the cache run
  the change makes no difference.
- **Switches:**
  - `--structure on|off`, default on.
  - `AIRTOOLS_LIMAP_ENV` is the LIMAP venv. It must also contain LIMAP's `cfgs/`, which is copied
    there on hfbox.
  - `AIRTOOLS_PXW_ENV` is the PxwPlanar venv.
  - Both are in `/workspace/env.sh`. If either is missing, the layer runs without it (lines only,
    or planes only). With neither, or with no metric scale, it is skipped with a warning.
- **It never blocks or fails the mesh publish:**
  - Each step runs in its own session with a wall-clock timeout: 1800 s, or 300 s for S4. On
    timeout the whole process group is killed, pool workers included.
  - The publish waits at most 600 s for the background part, then kills it and publishes without
    it.
  - Any failure falls back to what exists.
  - `report.json` has a `structure` entry listing what ran, what failed, the per-step times and
    the counts. `validate_package` checks that the file is a scene-frame
    `airtools.structure/1` doc sitting inside the mesh bbox.
- **Hang found and fixed:** S4's per-plane fork pool deadlocked on hfbox, with the children
  blocked on OpenCV's thread-pool futex. S4 now spawns its workers (fcd0c8b). The pipeline also
  sets `OPENCV_FOR_THREADS_NUM=1` and relies on the process-group timeout above. There are tests
  for the group kill and for a background part that hangs.

### Fresh end-to-end run (`s2-e2e-structure`)

The run: `--stages preview,full --crop none`, hfbox cores 8–11 plus the GPU, 175 frames, wall time
1286 s.

The structure step:
- It ran all five steps, with the planes lifted on MVS depth: 481 edges, 513 corners, 85 planes
  and 18 objects.
- It took 268 s from start to finish (LIMAP 187 s; PxwPlanar 63 s inference plus 111 s lift; fuse
  3 s), overlapping the meshing.
- `validate_package` reported no problems, and `scene.json` revision 2 carries the entry.

**What it adds to the wall clock:** about 2 minutes, roughly 10 %, on 4 cores:
- 11 s after the mesh (the background part had already finished; S4 took 11 s).
- Contention on the 4 shared cores slowed the mesh steps. A re-run of the same steps with
  `--structure off` on the same cores took 37 s for ReconstructMesh (56 s with structure) and
  84 s for TextureMesh (176 s with structure). That makes about 110 s, an upper bound, since the
  re-run had a warm cache.

The i-e2e-fixed timings cannot serve as the comparison: that report comes from a resumed run on
12 cores. With more cores, the contention cost should shrink. Running the structure steps under
`nice` would move that cost onto the structure step itself, which has slack.

**Scores:** R6 snap on the 20-point GT (median / p90 mm) and E1's bench:

| s2-e2e-structure | R6 aim miss | R6 perfect | Measurement (aim miss) | Meas. error |
|---|---|---|---|---|
| Mesh (`s2-pipeline-e2e-mesh`) | 6.8 / 9.8 | 2.3 / 5.1 | 3.7 mm | 1.3 mm |
| Published layer (`s2-pipeline-e2e-struct`) | 4.5 / 11.4 | 3.5 / 7.9 | 1.3 mm | 0.8 mm |
| **Pipeline default now**, rect corners only on LIMAP lines (`s2-pipeline-e2e-struct-v2`) | **3.6 / 11.4** | **3.2 / 7.9** | **1.3 mm** | **0.8 mm** |
| Parts: LIMAP lines only | 3.6 / 11.9 | 3.5 / 6.6 | 0.8 mm | – |
| Parts: PxwPlanar planes only | 8.8 / 14.7 | 4.4 / 11.8 | 5.6 mm | – |
| Parts: fused, before rects | 3.6 / 11.4 | 3.2 / 7.9 | 1.3 mm | – |

- E1 bench: held-out PSNR 21.58 against 21.51 for i-e2e-fixed; SSIM 0.803 against 0.807;
  coverage 0.995; F@2 cm 0.597.
  The structure layer does not touch the mesh, so these differences are run-to-run noise.
- The layer still halves the aim-miss median against the mesh, and the measurement error is 2.8×
  lower.
- With perfect aim, this run's mesh is better (2.3 / 5.1). The lines' perfect-aim numbers vary
  with the SfM run: 2.2 / 6.1 on i-e2e-fixed, 3.5 / 6.6 here.

## Figures

- `s2/lsd_axis_overlay_0401.jpg`: axis subset projected into held-out frame 0401.
  - Line colours: x red, y green, z blue.
  - Corners are magenta; LSD detections are thin grey.
- `s2/lsd_all_overlay_0401.jpg`: all edges. Off-axis lines are yellow.
- `s2/lsd_axis_views.png` and `s2/lsd_all_views.png`: orthographic views in the Manhattan frame.
  The left panel is the counter-wall elevation.

## Reproduce

```
# hfbox
cd /workspace/airtools
taskset -c 8-11 /workspace/envs/limap/bin/python -m pipeline.experiments.lines.limap_run \
  --sfm work/dji0095/sfm --out /workspace/tools/s2-limap/runs/lsd \
  --limap-src /workspace/tools/s2-limap/limap-src --m-per-unit 0.26 \
  --config cfgs/structure_triangulation/default_cpu.yaml   # default.yaml = DeepLSD+GlueStick
.venv/bin/python -m pipeline.experiments.structure score scene/bench/i-hybrid-moge2 \
  --gt pipeline/experiments/structure/kitchen0095/gt.json \
  --structure /workspace/tools/s2-limap/runs/lsd/structure_axis.json \
  --frames work/bench/i-hybrid-moge2/frames --out /workspace/bench/structure/results.jsonl --name ...
.venv/bin/python -m pipeline.experiments.lines.evaluate --lines <structure.json> \
  --model work/dji0095/sfm/dense/sparse --images work/dji0095/sfm/dense/images --out <dir>
```
