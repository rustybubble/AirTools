# S4: in-plane rectangles (doors, drawers, appliance fronts) for snapping

Bench row 8 in `../p2-structure-bench.md`. Code: `pipeline/experiments/objects/` (`ortho.py`,
`rects.py`, `run.py`), test `tests/pipeline/test_objects_rects.py`. Figures: `s4/`.
Output: `s4/structure.s4.json` (scene frame, schema `airtools.structure/1`).

Crease edges (plane∩plane, S1/S3) miss the edges that lie inside a plane: door gaps, drawer
outlines and appliance fronts. S4 fits exact axis-aligned rectangles in each vertical plane's own
(u, v) frame, the way RoomPlan places doors and windows. The rectangle corners and sides then
become `rect` corners and `boundary` edges lying exactly on the plane, and equal doors form
`repeat` groups.

Tags: **[V]** measured here, **[I]** inferred.

## 1. Method

1. **Planes.**
   - Source: S3's PxwPlanar layer (`hfbox:/workspace/s3/pxw/structure.json`, 53 planes, COLMAP
     frame).
   - It is mapped into the scene frame by a Sim(3) (Umeyama) fit on the 165 shared camera
     centres. The residual is 4e-16 m: the scene is an exact similarity of the COLMAP model. [V]
   - A vertical plane (|n·y| < 0.15) spanning ≥ 15 cm both ways gets processed: 20 of the 53.
   - The frame per plane is `u = up × n` and `v = n × u`, so v is up. That makes cabinet and
     appliance outlines axis-aligned.
   - **Relief sub-planes.** A dishwasher or oven front stands 1–3 cm proud of the cabinet run.
     In the parent plane's orthophoto such fronts blur by parallax. The fix:
     - render the mesh as a height map over the parent plane;
     - take connected regions that sit 0.8–6 cm off it;
     - peel up to two RANSAC planes (5 mm tolerance, within 15° of the parent) off each region;
     - keep a sub-plane only if its box is ≥ 15 cm and ≥ 60 % filled by inliers. The fill gate
       drops bottle clutter.
2. **Orthophoto** at 2 mm/px (scene units):
   - For every plane pixel, lift it to 3D on the plane, project it through each COLMAP pose,
     apply the SIMPLE_RADIAL distortion, and `remap` the raw 1920×1080 frame. Undistorting
     the frames first is not needed.
   - Up to 80 frames, ranked by footprint (distance / cos of the view angle), with the 7 finest
     per pixel.
   - Every 4th SfM frame is **held out** for the reprojection check.
   - A per-frame gain match is applied.
   - **A per-pixel colour median leaves exposure seams**, where the set of contributing frames
     changes. On the upper cabinets one seam ran across all the doors at 1.5 cm below their
     true top, and the rectangle fit latched onto it. [V]
   - The fix is a **seam-free grey image**: the per-pixel median of each frame's own gradients
     (a frame's coverage border is not a gradient inside that frame), integrated with a Neumann
     Poisson solve (DCT). The fix was checked visually and with the rectangle fit: the true
     top edges are found (`s4/ortho_sources_p1.jpg`). [V]
   - Only k = 7 slots per pixel are kept (filled in rank order), not every warped frame, and
     the medians run over the slots. Frames are ranked and coverage-checked on a 1/8 sub-grid;
     full-resolution maps are built only for the kept frames. The per-frame gain is still
     matched on every pixel a frame sees, not only its slots (see §2, speed changes).
   - Cost: 13–18 s for the largest (1–2 m²) planes on one core. [V]
3. **Rectangles** (`rects.py`):
   - LSD on the CLAHE'd orthophoto. Horizontal and vertical families within 3°.
   - 1D clustering of segment positions within 2.5 px gives lines.
   - Each line's coverage is re-measured from the across-line grey step (≥ 6 grey levels, ±1
     px), because LSD fragments a low-contrast door gap.
   - Every (left, right, top, bottom) quadruple 4 cm – 1.2 m in size is scored by the covered
     fraction of each side: min ≥ 0.55, mean ≥ 0.7.
   - **Atomic** rectangles are those no other line crosses end to end. That makes a door win
     over its cabinet pair.
   - **Outer** rectangles are crossed ones with score ≥ 0.95 that add at least two new corners,
     such as a dishwasher's outline around its door and control strip.
   - Greedy NMS: IoU ≤ 0.2, ties broken by side contrast.
4. **Gates.**
   - ≥ 75 % of the rectangle's mesh pixels lie within 2 cm of the plane.
   - No S3 crease runs through the interior for more than a third of the short side. This
     removes a wall patch framed by the counter on the fridge-side plane.
   - Minimum side 4 cm.
   - The same rectangle seen on two near-coincident planes is kept once.
5. **Lift and group.**
   - Corners go to `origin + u·U + v·V`, exactly on the plane.
   - `group_repeats`: atomic rectangles with width and height within 6 % of each other.
   - Labels are a size heuristic (drawer / cabinet_door / appliance / panel), not a detector.
     Grounding-DINO / SAM-2 were not tried (see §4).

## 2. Results (kitchen-0095, i-hybrid-moge2 frame)

**Orthophoto source** (sum over the 20 face planes, final run) [V]:

| Source | Axis-aligned LSD length (px ≥ 20) | Rectangles |
|---|---|---|
| Mesh texture (`mesh.r1.glb`, 4096² atlas) | 3,682 | 1 |
| Frames, gradient-median + Poisson | 39,357 | 45 |

- The texture atlas is made of thousands of tiny charts. Bilinear sampling bleeds across them
  into speckle (see `s4/ortho_sources_p1.jpg`).
- The Laplacian-variance "sharpness" wrongly favours the mesh texture (18k vs 57) because of
  that speckle, so LSD length is the fairer measure.

**Yield** (38 objects: 28 atomic, 10 outer; final run `work/s4/final4`) [V]:
- All 10 upper doors (4 cabinets, including the one over the fridge).
- The 2 drawer fronts and 4 lower doors.
- The dishwasher (door, control strip, outline) on its own relief plane.
- Microwave door, window and keypad panels.
- Wall outlets, a switch and a sign on the backsplash.
- Left over after the gates: a few rectangles on clutter (the wipes box by the sink) and nested
  microwave frame pieces.

**Snap, S1 scorer, R6 policy** (corner ≤ 2.5 cm, edge ≤ 2 cm, corner beats edge by ≤ 1 cm; the
GT excludes the downgraded ua_bl and dw_tr) [V]:

| | Perfect aim (median / p90 mm) | ±1 cm aim | Grade A, aim | Measurement, aim |
|---|---|---|---|---|
| Row 0, mesh `ClosestPoint` | 2.4 / 4.7 | 6.9 / 10.0 | 7.0 / 10.0 | 4.1 mm |
| **S4 rectangles only** | 3.5 / 8.6 | **5.5** / 12.7 | 4.6 / 15.0 | 4.5 mm (2.4 perfect aim) |

- **On the points S4 targets, the error is 1.3–5.5 mm** (snap to the nearest rectangle corner):
  - upper door corners and seams: ua_seam 3.2, ua_br 3.8, ub_bl 4.4, ub_seam 2.5, ub_br 4.4,
    uc_bl 2.8, uc_seam 1.3, ud_seam 5.5, ud_bl 7.7;
  - drawer top corners 6.9 / 8.1 as corners, 3.2 / 4.1 to the nearest edge.
- **Door widths from snapped corners** [V]:
  - upper A right +0.6, B left −1.1, B right −4.5, C left +1.6, D left +1.0 mm;
  - median |err| 1.1 mm, about 0.4 %;
  - left drawer −4.5 mm, upper cab B overall −5.5 mm.
- **The p90 and the overall measurement error come from points S4 has no rectangle for.**
  Counter and lip corners are crease points (S1/S2). The oven window has no plane in S3's layer.
  mw_bl lies on a side-facing plane. The snap there falls back to the mesh or to a rectangle
  corner 1–2.5 cm away. Microwave width is −16 mm for the same reason. The p90 moved 11.7 →
  12.7 mm against the earlier run (`final3`) only through mw_br (11.7 → 16.6 mm), where a
  keypad-strip rectangle flipped on sub-grey-level fusion differences. [V]
- **Rectangles are a layer to fuse, not a standalone layer** [I]. The S2 fused layer supplies the
  creases; S4 supplies the in-plane corners.

**Repeatability** (scene units, which are ~0.72× true scale, `p1-recon-bench`) [V]:

| Group | Members | Width | Spread (max − min) | Std | Height spread |
|---|---|---|---|---|---|
| g0: upper doors of cabinets B and D | 6 (4 on p1, 2 on p13) | 262.4 mm | 12.9 mm | 4.0 mm | 6.2 mm |
| g1: upper doors of cabinets A and C | 4 | 208.4 mm | 8.1 mm | 2.9 mm | 9.4 mm |
| g2: microwave strips above and below the keypad | 2 | 78.9 mm | 0.0 mm | 0.0 mm | 0.3 mm |
| g3: lower left-cabinet doors | 2 | 205.2 mm | 1.7 mm | 0.9 mm | 7.1 mm |
| g4: lower sink-cabinet doors | 2 | 261.4 mm | 0.8 mm | 0.4 mm | 0.0 mm |
| g5: wall outlets | 2 | 57.6 mm | 0.9 mm | 0.4 mm | 0.0 mm |

- In the scorer's GT pair group "cab B doors", S4 snaps to 0.4 mm spread against 3.9 mm in the
  GT.
- The GT spread includes its own seam bias: S1 notes T-junction seams carry a few mm of
  view-dependent bias. Real repeated doors are likely within 1–2 mm [I].
- Snapping a group to equal sizes was tested (below) and does not pay on this scene.

**Group regularization** (`--regularize`, `rects.regularize`; re-detected on the final
orthophotos with `--reuse`) [V]:

| Mode | What it does | R6 aim miss (median / p90) | Perfect aim | Door widths median \|err\| | Upper B right |
|---|---|---|---|---|---|
| off (default) | | **5.5** / 12.7 | **3.5** / 8.6 | 1.1 mm | −4.5 mm |
| rows, 5 mm | members of a group on one plane with bottoms within 5 mm share bottom and top | 5.5 / 12.7 | 3.5 / 8.6 | 1.1 mm | −4.5 mm |
| chain, 5 mm | rows, then each touching door pair keeps its outer edges and gets one shared seam at equal widths | 5.9 / 12.7 | 3.7 / 8.6 | 0.9 mm | −4.7 mm |
| rows / chain, 15 mm | as above with a wider row tolerance | 6.6–6.9 / 12.7 | 5.0–5.4 / 9.2 | 0.9–1.1 mm | −4.5 / −4.7 mm |
| median | every member gets the group's median width and height about its own centre | 6.7 / 12.7 | 4.9 / 9.2 | 0.9 mm | −4.5 mm |

- **It hides real differences more than it removes noise.**
  - The rows a wide tolerance joins are not one line. In g0 the cabinet over the fridge
    (o0, o1) and cabinet B (o4, o5) have bottoms 11 mm apart and tops 9 mm apart; in g1
    the two cabinets' bottoms are 12 mm apart. A 15 mm row tolerance flattens these real
    steps and moves corners off by ~5 mm. At 5 mm each row is a single door pair, which
    already shares its bottom and top, so nothing changes.
  - Equal widths move the one well-placed seam of a door pair: chain gains 0.2 mm on door
    widths but costs 0.4 mm of snap median; median costs 1.2 mm.
- **Upper B right at −4.5 mm is not width noise, and regularization cannot fix it.** S4
  already measures cabinet B's two doors as equal (rectangles 260.6 and 261.1 mm; snapped
  spread 0.4 mm). The GT makes them 261.7 and 265.6 mm, 3.9 mm apart. So the −4.5 mm is the GT's left/right
  difference: equalizing moves it to −4.7 mm. [V] Whether the doors truly differ or the GT
  seam point carries the few mm of view-dependent T-junction bias S1 notes is open [I]. The
  overall cabinet B width is also short, by 5.5 mm.
- So groups stay as metadata (`groups[]` with widths, spreads, pitch). A snapper can use them
  to suggest "same as its neighbour" but should not move the corners.

**Held-out reprojection** [V]:
- 41 SfM frames never used for fusion, undistorted to the scene pinhole.
- Rectangle sides are matched to LSD within 3° and 10 px.
- Median 1.36 px, p90 4.63 px; 79 % of visible sides matched.
- For comparison, S2's LIMAP lines: 0.89 px, 84 %.
- The scorer's own LSD check (12 fixed frames): 2.1 px median. It includes hidden sides and
  frames used for fusion.
- Figures: `s4/reproj_0211.jpg`, `s4/reproj_0411.jpg`.

**Cost** [V] (`taskset -c 0-3`, `--workers 4`, laptop, other jobs running, load ~6 on 16
cores):
- **40 s to the finished layer** (20 face planes and 8 relief planes), 375 % CPU, 1.26 GB max
  RSS. Plus 20 s for the held-out reprojection check, which is evaluation only and runs on
  the same pool; 60 s wall in total.
- **Production setting `--hold-every 0`** (fuse all frames, no check; what the pipeline
  should pass): 44 s wall, 370 % CPU, 1.22 GB RSS, 37 objects. With the extra frames it
  scores R6 aim 4.9 / 11.4, perfect 3.7 / 9.0, measurement 2.4 mm with aim miss
  (`work/s4/prod1`). One run only, so treat the gain as within run-to-run noise.
- The first version took 368 s wall at ~1.2 cores and 3.4 GB RSS.
- The slowest planes are p5 18 s, p3 18 s, p1 16 s and p2 14 s (one core each), so the
  wall time is bounded by the largest plane plus the relief pass. More cores help only
  until then.
- Output: 38 objects, 152 corners and 152 edges, 0.13 MB of JSON.

**Speed changes** [V]:
- **Parallel planes.** A `spawn` process pool, one plane per task, biggest first, with
  `cv2.setNumThreads(1)` in the workers. Each worker loads the frames, mesh and planes once.
  A `fork` pool deadlocked: the children inherit cv2's live thread pool after the parent has
  used it.
- **Per-plane time 42–50 s to 14–18 s** for the four largest planes (p1: 55 frames,
  316 × 990 px; fusion alone 8–9 s in isolation):
  - k slots instead of the full (frames × h × w × 3) stack, and a sort-based slot median
    instead of `np.nanmedian`;
  - the coverage pre-check on a 1/8 sub-grid, so full distortion maps are only made for the
    frames that are warped.
- **Parity check.** The first slot version matched each frame's gain on its slots only. That
  changed the grey image on p1 (mean 77 against 100, correlation 0.972 with the old image) and
  lost the upper B left door and 5 objects in total. Matching the gain on every pixel the frame sees again
  restores it: correlation 0.99998 with the old orthophoto, mean |difference| 0.06 grey
  levels on p1.
- **Held-out check** parallelized per frame on the same pool: 61 s to 20 s.

## 3. Findings

- **Fuse raw frames, not the mesh texture, for in-plane edges.** It is the difference between
  42 rectangles and 1. [V]
- **Median colour fusion is not enough.** Exposure seams create straight, axis-aligned false
  edges exactly where a rectangle fitter looks. Gradient-domain fusion removes them for the cost
  of one DCT. [V]
- **Plane placement from S3 is good enough for door fronts.** GT door points sit within 0–5 mm
  of S3's cabinet-front plane, except the downgraded ua_bl. [V]
- **Textureless or yawed fronts are the weak spot.** The dishwasher and microwave fronts are
  white, so MVS is weak there and the mesh relies on MoGe fill. The relief plane can sit ~1–2 cm
  off, and the GT disagrees with the mesh about the dishwasher's 2.5° yaw. [V, cause I]
  - A photo-consistency sweep of the plane offset (pick the offset where the warped frames agree
    best) is the natural fix. Not done.
- **Junk control matters more than recall for snapping.** A wrong corner 1–2 cm from a GT point
  beats the correct mesh or edge snap.
  - The mesh-support, crease, min-size and relief-fill gates cut the objects from 61 to 40.
  - Perfect-aim priority p90 went from 17.6 to 8.5 mm (R6 policy).
  - Remaining junk: clutter-backed rectangles and nested microwave frames. A 2D detector label
    ("cabinet door", "drawer", "dishwasher") would remove most of it [I].

## 4. Not done / next

- **Planes:** not rerun on S1's planes; S3's are the better layer (lead's call).
- **Semantics:** no 2D detector for labels.
  - Grounding-DINO is Apache-2.0 and SAM-2 Apache-2.0; both run on CPU.
  - They would replace the size heuristic and filter clutter.
- **Offset refinement:** no plane-offset photo-consistency sweep; it is the fix for textureless
  fronts.
- **Oven:** the oven front needs a plane. S1's layer may have one.
- **GT figures:** the green crosses in `s4/ortho_*.jpg` are GT points within 3 cm of the plane.
  Scene mm are at the uncertain altitude scale.
