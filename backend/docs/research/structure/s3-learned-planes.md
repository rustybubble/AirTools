# S3: learned plane detection with depth and normal priors

Research session 2026-09-25, agent S3. Goal: exact snap planes (and their creases and corners) for
the VR tools, from photogrammetry of the kitchen benchmark `kitchen-0095`, using learned plane
detectors with monocular depth and normal priors. Rows 3, 3b and 4 of `p2-structure-bench.md` §3.
Code: `pipeline/experiments/planes/`. Figures: `structure/s3/`.

All mm below are at the scene's altitude scale (about 1.47× too small, see the bench §3). Snap
numbers use S1's scorer with the R6 policy and the 20-point AB-grade GT unless marked otherwise.

## 1. Summary

| Row | Method | Runtime on hfbox | R6 aim miss / perfect aim (mm) | Measurement | Angle error | Verdict |
|---|---|---|---|---|---|---|
| 0 | raw hybrid mesh (reference) | – | 6.9 / 10.0, 2.4 / 4.7 | 4.1 mm (aim) | 1.08° (mesh) | – |
| 3 v0 | PxwPlanar per frame, MVS-positioned, fused | 45 s GPU + 69 s CPU (4 cores) | 8.4 / 14.6, 4.1 / 14.7 | 3.4 mm | 1.40° | first result |
| **3b v6** | + full-range crease extents +2 cm, gravity-fixed Manhattan 3° | 45 s GPU + 61 s CPU | 7.9 / 13.0, 3.8 / 11.9 | **2.0 mm (1.11 %)** | **0.00°** | **S3 default** |
| 4 | PlanarSplatting, MoGe-2 priors, 5000 it | 94 s GPU + 8 s priors | 10.2 / 23.0, 7.4 / 19.1 | 3.8 mm | 3.19° | rejected |

- The plane layer on its own does not beat the mesh for point snapping. It does give the best
  planes, and those are what S2's fusion (row 2f, the best row) and S4's rectangles build on.
- **Planes are accurate where they exist.** Front planes sit 1.5–7 mm from GT at 0.1–0.5°. The
  creases pass within 0.5–5 mm of the GT corners.
- **Failures are coverage failures, not accuracy failures:**
  - the upper-cabinet undersides are never seen;
  - thin strips (counter lip, dishwasher front) merge into the neighbouring door plane;
  - crease segments stop short of the corners.

## 2. PxwPlanar (row 3)

**Setup.** `alpayozkan/PixelwisePlanarity` @ `6bdd3a5`, MIT. The weights are
`alpayozkan/pxwplanar-moge2-planarity` on HF; the checkpoint card says MIT.
- Its own venv `/workspace/envs/pxw`: a uv venv on `/opt/venv`'s Python 3.12 that inherits the ROCm
  torch 2.14 through a `.pth`. Nothing pulled a CUDA torch.
- Installed with `--no-deps`: pxwplanar, its MoGe fork (`Ahmetcanyvz/MoGe@planarity`, a submodule)
  and utils3d; light deps on top. About 0.1 GB of new disk (uv hard links), plus 1.3 GB of weights
  in `/workspace/.hf`.

**Per frame** (`pxw_infer.py`):
- Each undistorted COLMAP image (165) is resized to 960 px, aspect kept. The demo's fixed
  768×512 resize distorts our 16:9 frames, so we skip it.
- Then `model.infer(num_tokens=1600, fov_x=<COLMAP FoV>)`, followed by PxwPlanar's region growing
  with the paper's canonical parameters: planarity > 0.3, 5°, 2.5 % depth, 8 neighbours.
- **Measured: 0.26 s/frame, 45 s for the kitchen, plus 3 s model load** when cached (40 s cold).
- MoGe normals come out in the OpenCV camera frame, facing the camera. Checked against
  depth-derived normals: median dot −0.99 with the (+z) geometric ones.

**Lift** (`pxw_lift.py`), for every segment of at least 400 px:
- MoGe depth is aligned to the frame's SfM points with E3's scale-plane fit
  (`depthfusion_worker.fit_alignment`). Frames with median relative error above 5 % are skipped
  (5 of 165).
- Following R5 (mono normals are 8–10° off), the plane is **positioned by RANSAC plus TLS on the
  OpenMVS depth-map pixels** inside the eroded mask, where MVS agrees with the aligned mono depth
  within 5 %. Mono depth is used only when fewer than 150 such pixels exist: 2419 of 3947
  per-frame planes are MVS-fitted.
- The mask's pixels are then ray-cast onto that plane (kept where within 10 % of mono depth).
  That gives the support, and the boundary is the learned mask's boundary.

**Fuse** (`geom.fuse_planes`):
- Union-find over per-frame planes that share a 5 cm support voxel, have normals within 8°, and
  whose supports lie within 2 cm of each other's plane (median).
- Each cluster is refit on its members' MVS points (mono only if there are fewer than 300) and kept
  if seen in at least 2 frames.
- Support is rasterised at 2 cm and must be hit by at least 2 frames. Holes are closed; each
  connected component becomes one output plane with an `approxPolyDP` polygon (1 cm), and
  components under 0.02 m² are dropped.

**Creases and corners** (`pxw_lift.finish`):
- Each pair of planes with area ≥ 0.05 m² each and normals ≥ 15° apart is intersected. The segment
  is where both supports come within 3 cm of the line.
- Triples with at least 2 of their 3 creases give a corner when it lies within 10 cm of 2 crease
  segments. Crease ends within 10 cm are snapped onto the corner.
- `dihedral_deg` = 180° − the angle between the camera-facing normals.

**Output:** `airtools.structure/1`, `frame: cameras` (COLMAP units; `params.units_per_m` = 3.88 from
MoGe). **Stable path: hfbox `/workspace/s3/pxw/structure.json`** (currently v6), which S2 and S4
read. `structure.frames.json` maps (frame, segment) to a fused cluster, for the figures.

### 2.1 Sweep (scored with S1's harness)

| Variant | Change | R6 aim / perfect | Priority-3cm perfect | meas \|err\| | Corner recall | Angle |
|---|---|---|---|---|---|---|
| v0 | defaults: crease extent at [2, 98] % of support | 8.4/14.6, 4.1/14.7 | 7.6/24.6 | 3.4 mm | 0.07 | 1.40° |
| v1 | extent [0, 100] % + 1 cm (old 22-pt GT) | – | 8.0/15.5 | 3.6 mm | 0.06 | 1.40° |
| v2 | [0, 100] % + 2 cm | 8.2/13.3, 4.8/12.9 | 4.8/12.9 | 3.4 mm | 0.07 | 1.40° |
| v3 | v1 + fuse offset 1 cm (old GT) | – | 7.6/15.6 | 4.1 mm | 0.06 | 0.68° |
| v4 | v1 + gravity Manhattan 3° | 8.2/15.3, 6.6/14.4 | 6.6/15.4 | 2.2 mm | 0.14 | 0.00° |
| **v6** | v2 + gravity Manhattan 3° | **7.9/13.0, 3.8/11.9** | 3.8/12.3 | **2.0 mm** | 0.14 | **0.00°** |
| v7 | v6 + fuse offset 1.2 cm | 8.1/12.7, 4.7/12.1 | 4.7/14.4 | 2.1 mm | 0.14 | 0.00° |
| (m4) | free Manhattan 4° (axes from our own normals; old GT) | – | 5.4/19.5 | 4.1 mm | 0.00 | 1.82° |

- **Gravity-fixed Manhattan** (`--manhattan 3 --up-from cameras.r1.json`):
  - The up axis is the scene's +Y, i.e. the pipeline's own camera up-axis fit, carried into COLMAP
    by the camera-centre Sim(3). The horizontal axis comes from a weighted Procrustes refit of the
    snapped normals.
  - It makes fronts and counter exactly orthogonal and parallel, and cuts measurement error from
    3.4 to 2.0 mm.
  - A free Manhattan frame (axes from our own normals) made things worse, 1.82°: the counter's
    ~1.8° tilt leaked into the axes.
- **Diagnosis** (per-point, GT mapped into COLMAP by the camera Sim(3)):
  - ua_br / ub_bl / ub_br / uc_bl: the GT point lies 0.2–5 mm from both planes of the nearest
    crease, but the crease segment ended 15–30 mm short, so the edge snap slid along it. Hence the
    +2 cm extension.
  - **No down-facing plane exists near any upper-cabinet bottom corner.** The drone never sees the
    undersides, so these corners are "front + side + boundary", and only an image line (S2) can
    close them.
  - ctr_fl, dw_tl, drw_*, lip_l: the counter lip and dishwasher front are fused into the lower
    door plane `p2`, 5–13 mm off. A 1–1.2 cm fuse offset splits some of this, but snapping does
    not improve.

## 3. PlanarSplatting (row 4)

**Port to gfx1201: works**, well inside the 90 min timebox
(`psplat_hip.sh`).
- `ant-research/PlanarSplatting` @ `ec626fd` has two CUDA extensions: `diff-rect-rasterization`
  (a 3DGS-rasterizer derivative) and `quaternion-utils`. Both were built by torch's hipify on hfbox
  against `/opt/venv` torch 2.14 plus `/workspace/rocm`, `MAX_JOBS=4`, about 1 min.
- Five source fixes:
  1. drop `device_launch_parameters.h` and `cooperative_groups/reduce.h`;
  2. rewrite the MSVC-style `<< <`/`>> >` launches;
  3. replace `cg::this_grid().thread_rank()` with the 1-D block/thread index (every kernel is
     launched 1-D);
  4. `__trap` → `__builtin_trap`;
  5. replace the bundled glm's host-only `glm::max(vec3, float)` functor with `fmaxf`.
- Stubbed instead of installed:
  - `pytorch3d`: only `knn_points(K=1)` in the merger is used; replaced by chunked `torch.cdist`;
  - `pyrender` and `rerun`: imported, but unused with `pre_align` off.
- **open3d must be 0.19.** 0.20's legacy `ScalableTSDFVolume` returns an empty mesh (as E3 found).
  PlanarSplatting's mono-mesh initialisation then silently fell back to sphere initialisation, and
  the merger crashed on zero planes.

**Priors:** our MoGe-2 depth and normals from the PxwPlanar cache instead of Metric3D-v2.
- Depth is aligned per frame to SfM (E3 scale plane).
- Poses and depth are divided by units-per-metre, so its metric thresholds (voxel 0.1, merge
  5–10 cm) hold.
- Frames are 960×540. `pre_align` is off (our depth is already aligned, and its re-alignment
  renders with pyrender).
- Images are symlinked under the output dir, because it writes `mono_mesh.ply` next to the
  images' parent.

**Run** (`psplat_run.py`), measured: 5000 iterations. The whole
PlanarSplatting call, including the TSDF init and the merge, took **94 s**, plus 8 s for the
priors.
- The merged planar mesh (vertex colour = instance) is converted to planes: a TLS fit per
  instance, supports sampled from its triangles, and the same `finish()` as PxwPlanar.
- Output: 53 planes, 52–54 edges, 14–17 corners.

**Result: rejected.**
- R6 aim 10.2/23.0, perfect 7.4/19.1, angle error 3.19°, GT-edge 6.6 mm. That is worse than
  PxwPlanar on every snap metric.
- Its planes cover the whole room without gaps (figure), but the positions follow MoGe depth
  rather than MVS.
- **Licence:** PlanarSplatting's own code is Apache-2.0, but `diff-rect-rasterization` and
  `quaternion-utils` carry Inria's Gaussian-Splatting licence (`LICENSE_GS.md`: research and
  evaluation only). Verified in the sources.
- The lead decided not to pursue a gsplat-rasterizer swap or LiP-Map (no licence file), since
  PlanarSplatting doesn't win.

## 4. Figures

- `s3/s3_pxw_v6_frames.jpg`:
  - left: PxwPlanar segments per frame, coloured by the scene plane they fused into (grey = not
    kept; black = region-growing borders);
  - right: the v6 3D layer re-projected with a z-buffer (planes filled, creases red, corners
    yellow).
  - Door faces fuse across frames. Creases land on the counter front and the fridge and wall
    edges. The upper-cabinet undersides have no plane.
- `s3/s3_psplat_frames.jpg`: the PlanarSplatting layer, same frames and view. It has full
  coverage, but spurious creases on the counter and sink.

## 5. Mesh regularization (candidate 3)

The goal: planar faces and sharp creases in the published mesh, without dropping below the P1
visual bar (PSNR/SSIM 21.5/0.807).

**Snap-then-keep-texture (rejected).** Snap the vertices of the packaged 200k glb onto the v6
planes (plane distance < δ, normal within 35°, inside the polygon + 2 cm), keeping the old
texture:

| δ | PSNR / SSIM (E1 bench) |
|---|---|
| unmodified (trimesh re-export only) | 21.88 / 0.806 |
| 5 mm | 21.79 / 0.799 |
| 10 mm | 21.40 / 0.786 |

The export itself costs 0 dB. The loss comes from the texture being baked on the old surface.

**Regularize-then-texture** (`regularize.py` plus `retex.sh`):
- The *untextured* `union_mesh.ply` (COLMAP frame) is snapped with S1's rule
  (`structure/regularize.snap_vertices`): R6's vertex rule (|n_v·n| > 0.9, within `plane_tol`,
  inside the polygon + 2 cm), then vertices within `edge_r` of a crease go onto it, and within
  `corner_r` of a corner onto it.
- Then OpenMVS `TextureMesh` runs with the run's own arguments (decimate read from its log,
  4 threads, 83–124 s), then `mesh.process_mesh` with the camera Sim(3), then the E1 bench and
  S1's scorer on the packaged scene.

Measured. Unrefined = the i-hybrid-moge2 `union_mesh.ply`. Refined = S1's RefineMesh
`--resolution-level 0 --decimate 1` (`work/bench/s1-refine-r0d1`). Snap numbers use R6.
Planarity is the median RMS over the 6 GT regions. The band is the median edge-band width.

| Run | Mesh | Rule | Moved | PSNR / SSIM | F@2cm | Planarity | Band | R6 perfect | meas \|err\| | Angle (mesh) |
|---|---|---|---|---|---|---|---|---|---|---|
| ctrl | unrefined | none | – | 21.85 / 0.810 | 0.609 | 6.1 mm | 16 mm | 2.8 / 4.7 | 1.4 mm | 1.07° |
| v6 | unrefined | tol 3 cm, ndot 0.9, crease/corner 1 cm | 52 % | 20.71 / 0.735 | 0.645 | 6.2 mm | 10 mm | 2.5 / 4.9 | 0.9 mm | 2.07° |
| s2f | unrefined | same, S2's fused layer | – | 20.04 / 0.723 | 0.654 | 6.3 mm | 8 mm | 1.7 / 4.9 | – | 1.05° |
| u-p10 | unrefined | tol 1 cm, planes only | 41 % | 21.27 / 0.775 | 0.625 | 5.6 mm | 10 mm | 2.9 / 5.7 | 2.7 mm | 1.11° |
| r-ctrl | refined | none | – | 21.77 / 0.797 | 0.680 | 6.2 mm | **4 mm** | **1.1 / 3.9** | 0.8 mm | 1.23° |
| r-p10 | refined | tol 1 cm, planes only | 42 % | 20.96 / 0.754 | 0.691 | 6.0 mm | 4 mm | 1.6 / 3.7 | 1.7 mm | 0.28° |
| r-p6n97 | refined | tol 6 mm, ndot 0.97, planes only | 24 % | 21.51 / 0.774 | 0.687 | **5.4 mm** | 10 mm | 1.5 / 4.2 | 0.2 mm | 0.92° |

**Verdict: no variant reaches the target** (PSNR/SSIM ≥ 21.7/0.80, planarity well under 6 mm,
band ≤ 4 mm).
- Snapping the untextured mesh before texturing still costs 0.3–1.1 dB PSNR and 0.02–0.07 SSIM.
- It buys at most 0.8 mm of planarity (6.2 → 5.4 mm) and a better mesh angle (down to 0.28°).
- The crease and corner rules make the band *wider* on the counter front (16 → 36–56 mm on one
  side) and on the microwave. Vertices from the neighbouring surface are pulled onto a crease that
  sits a few mm off the true edge.
- The only 4 mm band comes from RefineMesh itself (r-ctrl).
- **Recommendation:** publish RefineMesh r0d1 unregularized, and put exactness in the structure
  layer (S2 fused), not in the mesh.
- Why planarity barely drops is **not diagnosed**. Likely causes:
  - GT-region vertices outside the tolerance, the normal gate or the polygons stay rough;
  - the v6 planes sit 1.5–7 mm off GT, so a snapped patch next to an unsnapped one forms a step;
  - TextureMesh's decimation (0.19–0.25) and `process_mesh` re-simplify after the snap.

## 6. Reproduce

```bash
# hfbox, from /workspace/airtools; cores 12-15
bash pipeline/experiments/planes/psplat_hip.sh     # only for PlanarSplatting
P=/workspace/envs/pxw/bin/python; D=work/dji0095/sfm/dense
taskset -c 12-15 $P pipeline/experiments/planes/pxw_infer.py --dense $D --out /workspace/s3/pxw_cache
taskset -c 12-15 $P pipeline/experiments/planes/pxw_lift.py --dense $D --mvs work/dji0095/mvs \
  --cache /workspace/s3/pxw_cache --out /workspace/s3/pxw/structure.json \
  --edge-pct 0 --edge-extend 0.02 --manhattan 3 --up-from scene/bench/i-hybrid-moge2/cameras.r1.json
taskset -c 12-15 /workspace/envs/psplat/bin/python pipeline/experiments/planes/psplat_run.py \
  --dense $D --cache /workspace/s3/pxw_cache --out /workspace/s3/psplat
$P pipeline/experiments/planes/viz.py --dense $D --structure /workspace/s3/pxw/structure.json \
  --cache /workspace/s3/pxw_cache --frames 0166.jpg,0331.jpg,0496.jpg,0661.jpg --out fig.jpg
WORK=work/bench/s1-refine-r0d1 bash pipeline/experiments/planes/retex.sh NAME \
  /workspace/s3/pxw/structure.json --plane-tol 0.01 --edge-r 0 --corner-r 0
```

Tests: `tests/pipeline/test_planes_geom.py` covers RANSAC, fusion (a parallel plane 5 cm away must
not merge), the support polygon, the crease segment, the three-plane corner and Manhattan snapping.

## 7. Verified vs inferred

- **Verified by running:**
  - every runtime and score above;
  - the ROCm build and PlanarSplatting training on gfx1201;
  - the MoGe normal convention;
  - the open3d 0.20 TSDF failure (empty `mono_mesh.ply`, 4.6 KB) and the 0.19 fix;
  - the retexture control (21.85/0.810 against 21.88/0.806 published);
  - the licence texts of PlanarSplatting, PxwPlanar and the rasterizer.
- **Inferred, not measured:**
  - that the upper-cabinet undersides are invisible from the trajectory. No down-facing plane
    exists there, and the figure shows none, but no visibility test was run;
  - that the retexture PSNR loss comes from geometry/texture misalignment in held-out views rather
    than from TextureMesh view selection;
  - PlanarSplatting's worse accuracy being due to MoGe (not MVS) positioning.
