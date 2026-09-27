# R6: how industry, open source and practitioners get CAD-like snapping from scans

Survey for the P2/P3 structure bench (`docs/research/p2-structure-bench.md`), 2026-09-25. It covers
products, open-source libraries, Unity/VR snapping practice and community reports. It ends with a
proposed `structure.r<rev>.json` for the scene package and a Unity snapping recipe for P3.
Academic methods are covered in `r5-academic.md` and are not repeated here.

**Tags.**
- **[V]**: verified this session, by reading the vendor page or doc, the source code, a package
  registry, or by running the code.
- **[S]**: a secondary source (review, press, search snippet) that I did not confirm.
- **[I]**: my inference.

## 0. TL;DR

1. **No product snaps to raw photogrammetry edges.**
   - Each one either builds a parametric or segmented model and snaps to that, or makes the user
     draw vectors and snaps to those:
     - model and snap: RoomPlan, Hover, EagleView, ReCap/AutoCAD, Cyclone 3DR, Canvas's drafters;
     - draw and snap: Metashape, Pix4D.
   - Matterport's own accuracy article says to "measure closer to the center of the wall away from
     corners with floors and ceilings" [V]. That is our rounded-edge problem, and their advice is to
     avoid it.
   - Polycam: "Object Mode models are made from photos and are not dimensionally accurate unless
     you use the Rescale tool" [V].
   - So a separate structure layer is the industry answer, not a patch on the mesh.
2. **Edges and corners are computed, not detected.**
   - AutoCAD's point-cloud snaps [V]:
     - PEDGE is "a point on the linear intersection of two planar segments";
     - PCOR is "the intersection of 3 planar segments";
     - both need ReCap's plane segmentation.
   - Leica Cyclone 3DR's Building Extraction intersects every new plane with the existing ones and
     writes `ExtractedEdges` [V].
   - **Copy:** planes are primary; edges are plane∩plane, clipped to where both planes have
     support; corners are plane∩plane∩plane. Image lines (LIMAP) add the edges that lie on a single
     plane: cabinet-door outlines, window frames.
3. **Planes are stored as a local frame plus a 2D polygon.**
   - RoomPlan: `Surface.transform` + `dimensions` + `polygonCorners` [V].
   - MRUK: `Transform` + `PlaneBounds` + `PlaneBoundary2D` [V].
   - This makes every face exactly planar by construction. Snapping is an analytic ray/plane hit
     plus a point-in-polygon test. MRUK does exactly that, with no collider [V, source read].
4. **Doors, windows and cabinet faces are found in the plane, not in 3D.**
   - RoomPlan projects the scan onto each wall plane and runs a 2D orthographic detector [V].
   - Streem's patent splits mesh faces along 2D semantic-segmentation boundaries to sharpen door
     edges [V, patent].
   - **Copy:**
     1. Render each large plane's orthophoto from our textured mesh.
     2. Run LSD plus a 2D detector or segmenter on it.
     3. Fit rectangles in the plane's (u,v).
5. **Regularization is standard.** Snap normals to dominant (Manhattan) axes:
   - QuestRoomScan uses 10° [V];
   - Cyclone 3DR offers "Fit vertical plane" and "normal along X axis" [V];
   - CGAL has `Shape_regularization`.
6. **Honest accuracy bars from the field:**

   | Product | Claim | Tag |
   |---|---|---|
   | Polycam LiDAR rooms | ±½ in | [V] |
   | Canvas | 1–2 % | [V] |
   | Matterport Pro2 | ±1 % | [V] |
   | Matterport Pro3 | ±20 mm (0.2 %) | [V] |
   | Hover | 2.6 % variance vs tape on 40 homes | [S] |
   | EagleView roofs | 98.77 %, 0.2 ft average linear difference | [V, press release] |
   | RoomPlan | detection only, 95 % P/R walls and windows, 90 % doors; no mm figure | [V] |

   - **Our scale error dominates all of these:** P1 §5.2 found the caption altitude about 31 %
     off indoors.
   - Canvas and Magicplan both let the user supply a "critical dimension" or a Bluetooth laser
     reading [V]. That is our `known_dimension` scale method, and the structure layer makes it
     easy: tape a door or a counter height on an exact plane.
7. **Two things experiment agents can start now.** Both were sent to the lead as interims.
   - **(a)** OpenMVS `ReconstructMesh --smooth` defaults to **10 Taubin iterations** (develop
     source [V]), and `pipeline/mvs.py` does not override it. A `--smooth 0` A/B test costs one flag.
   - **(b)** LIMAP 2.0 is `pip install pylimap`: manylinux x86_64 wheels, cp310–312 [V on PyPI].
     It targets COLMAP 4.2, which is what we run.

## 1. Products

### 1.1 Summary table

| Product | Snap / measure targets | How structure is made | Accuracy claim | Licence / availability | What we copy |
|---|---|---|---|---|---|
| **Apple RoomPlan** | Parametric walls, doors (open/closed), windows, openings, floors, 16 object classes as cuboids | LiDAR + RGB on the ANE. Walls and openings are detected as 2D lines in bird's-eye pseudo-images (a "semantic map" and a "Z-slicing map"), then lifted to 3D using wall height. Doors and windows use "an orthographic-detection approach" on wall planes. Objects are fused and "clipped or aligned to the nearby walls" [V] | 95 % P/R walls and windows, 90 % doors, objects 91/90 % AP/AR at 30 % IoU [V]. Wall-length drift "up to ±5 cm per wall" [S] | iOS-only, closed. USD/USDZ export | Data model (§1.2), orthographic door/window detection, clipping objects to walls |
| **Meta MRUK** (Quest Scene API) | Room anchors: wall/floor/ceiling planes (rect + polygon), volumes (boxes), global mesh; `Raycast`, `GetBestPoseFromRaycast`, `TryGetClosestSurfacePosition`, `IsPositionInSceneVolume` | The user scans the room on the headset (depth sensor). The OS fits planes and boxes, and the user can edit them | None published [V: none found]. Journalism puts Quest measure apps at "±1 cm" [S] | Oculus SDK License; the source ships in the UPM package (v207 read) [V] | JSON format and collisionless plane raycast (§1.3). Possibly load our structure through `LoadSceneFromJsonString` |
| **Polycam** | Ruler (distance + angle), pen, area, auto-measurement of walls (Space/Floorplan modes). "Measurement snapping options" sit in settings; details are not public [V] | LiDAR room mode (RoomPlan-like floor plans); photo "Object Mode" | "Standard accuracy of +/-½ inch on standard interior captures" (Space and Floorplan). Photo Object Mode is "not dimensionally accurate" without Rescale [V] | Closed SaaS | Auto-measure walls. Treat photo meshes as needing a scale reference |
| **Matterport** | Measurement mode snaps the scope "onto edges and corners" of the **mesh**; Alt disables snapping. **Double-clicking a wall "projects a perpendicular line across to the opposing wall"** [V] | Structured light / LiDAR / 360 + Cortex AI depth. Schematic floor plans and Property Intelligence auto-dimensions (AI + product) [S] | Pro2 ±54 mm (1 %), range ~5 m. Pro3 ±20 mm (0.2 %), ~20 m processed. BLK360 G1 ±6 mm @10 m (from their table) [V]. The measure tool runs on the **low-res** mesh; thin objects and edges may not measure well [V] | Closed SaaS | **Wall-to-wall perpendicular measure** as a one-gesture tool. Advice: measure plane-to-plane, not at corners |
| **Magicplan** | Walls, corners, doors, windows, outlets on a 2D plan. Bluetooth laser measurers override dimensions [V/S] | LiDAR auto-scan; "Wall Mode" points at elements [S] | No figure published [V]. Recommends a Bluetooth laser for "100 % accurate" | Closed | User-entered critical dimension beats any scan |
| **Canvas.io** | CAD/BIM export (SketchUp, Revit, DWG, Chief) from a LiDAR scan | ARKit + own SLAM/recon. Scan to CAD is a paid service; the page does not say whether people or software do it [V]. Reported as human drafters [S/I] | "Within 1–2 %" of tape; worse under 1 ft. Clients can submit "manual critical dimensions" [V] | Closed service | Same as Magicplan. A 1–2 % bar is what the market accepts for as-builts |
| **Hover** | Measured 3D exterior model: roof facets, walls, windows, doors, soffit, fascia; PDF reports | Smartphone photos (8–16). Patents: line segments in 3D, grouped into vertical/horizontal by **vanishing points**, RANSAC plane fits, **scale from orthographic imagery** [V, patent US9437033B2]. Runs the S23DR roof-wireframe challenge (HoHo dataset, ~25k houses) [V] | Marketing says "to-the-inch". Haag study: 2.6 % variance vs 3.4 % for aerial, 40 homes [S] | Closed. The S23DR 2026 paper is CC-BY-4.0 [V]; the HoHo dataset licence was not checked | Exteriors: VP-grouped lines → planes → wireframe. S23DR 2026 winner HSS 0.654, vertex F1 0.791 [V] |
| **EagleView** | Roof, wall and window reports | Oblique + ortho aerial imagery, "proprietary photogrammetry and AI" [S]. Assess uses drones | 98.77 % roof lines (0.2 linear ft avg diff), 98.43 % area, 98.49 % pitch, against CompassData UAV and terrestrial LiDAR, Denver 2025 [V, press release] | Closed | Report accuracy as length error against LiDAR checkpoints, as they do |
| **Pix4D (matic/survey)** | Manual vector tools: marker, polyline, polygon, arc, catenary. **Snapping to existing vertices** of imported DXF / vectors; "Drape polyline" to the cloud [V] | Photogrammetry. No automatic edge or plane extraction in the docs [V] | Survey-grade with GCPs (not checked) | Commercial | Snap targets are user or vector data, not mesh edges. Draping = snap-to-surface |
| **Agisoft Metashape Pro** | Draw point/polyline/polygon on the model; **Axis snap, Vertex snap, Edge snap (Shift)** snap to *existing shapes'* vertices and sides [V]; export DXF/SHP/GeoJSON | Photogrammetry. No plane detection in the docs [V/I] | GCP-dependent | Commercial | Same lesson. Their precise points come from markers placed in several **photos** (triangulated). Our bench's ground truth does the same |
| **RealityScan (ex-RealityCapture)** | Distance/area/volume, ortho projections, a 2D measure tool on orthos, DXF export [S] | Photogrammetry | – | Epic, free under revenue threshold | Nothing structural to copy |
| **Leica Cyclone 3DR** | Building Extraction: the user clicks each planar surface. Best-fit plane with auto tolerance, optional "Fit vertical plane" / "normal along X". **Intersections with all existing planes are computed automatically** into `ExtractedEdges`; exports DXF [V] | TLS / mobile scans | BLK360 G1 ±6 mm @10 m (point accuracy) [V] | Commercial | **Click-to-extract-plane** UX (a VR fallback when auto-detection misses). Auto plane∩plane edges |
| **FARO As-Built / ClearEdge3D EdgeWise** | Semi-automatic wall/pipe/steel fitting into Revit. EdgeWise auto-extracts walls, windows, doors, "up to 73 %" of modelling with low effort [S] | Laser scans | – | Commercial | Automatic proposals plus human confirmation |
| **Autodesk ReCap Pro + AutoCAD/Revit** | ReCap indexes scans into **planar and cylindrical segments**. AutoCAD point-cloud osnaps: PNOD (nearest point), PNEA (on plane), PPER (perpendicular to plane), PEDGE (plane∩plane), PCOR (3-plane corner), perpendicular to edge, PCL (cylinder centreline) [V]. Revit snaps to "implicit planar surfaces dynamically detected in the point cloud … only in a small vicinity of the cursor", with **planar snaps prioritised over direct point snaps; Tab cycles** [S, Autodesk help] | Laser/photo scans; segmentation at indexing | – | Commercial | **Snap priority list and glyphs** (§3). Local, on-demand plane detection near the cursor as a fallback |
| **Niantic Scaniverse** | Two-point measure. Splat-derived meshes. Metric splats via a printed **calibration board** circled at the start [S] | Splats + mesh on device | "Centimeter-level" only for the enterprise multi-sensor service [S] | Free app | Printed-board scale = our `scalebar_aruco` |
| **Luma** | No measurement or structure tools found [I] | – | – | – | – |

### 1.2 RoomPlan `CapturedRoom`: the data model to borrow [V, Apple doc JSON]

- `CapturedRoom`: `walls`, `doors`, `windows`, `openings`, `floors`, `objects`, `sections` (room
  labels + centre), `story`, `version`, `identifier`. Serializes with `encode(to:)`; exports USD.
- `CapturedRoom.Surface`:
  - identity: `identifier`, `parentIdentifier` (window → wall), `category`
    (`wall | floor | door(isOpen:) | window | opening`), `confidence` (`high | medium | low`);
  - geometry: `transform` (4×4), `dimensions` (float3 bounding box), `polygonCorners` (float3 list,
    for non-rectangular walls and floors), `curve` (curved walls), `completedEdges`
    (`top | bottom | left | right`: which borders were actually observed), `story`.
- `CapturedRoom.Object`: `identifier`, `parentIdentifier`, `category` (16: storage, refrigerator,
  stove, bed, sink, washerDryer, toilet, bathtub, oven, dishwasher, table, sofa, chair, fireplace,
  television, stairs), `confidence`, `transform`, `dimensions`, `attributes`, `story`.
- **Worth copying:**
  - `parentIdentifier`: a door sits in a wall, a cabinet door on a cabinet face.
  - `completedEdges`: tells the snapper which polygon borders are real, observed edges and which
    are just where the scan stopped. Snap only to real ones.
  - Three-level confidence.

### 1.3 MRUK: format and query API [V, `com.meta.xr.mrutilitykit` 207.0.0 source]

- **JSON** (`MRUK.SaveSceneToJsonString` / `LoadSceneFromJsonString(string, bool removeMissingRooms)`):

  ```json
  {"CoordinateSystem": "Unity",
   "Rooms": [{"UUID": "...", "RoomLabel": "...",
     "RoomLayout": {"FloorUuid": "...", "CeilingUuid": "...", "WallsUuid": ["..."]},
     "Anchors": [{"UUID": "...", "SemanticClassifications": ["WALL_FACE"],
       "Transform": {"Translation": [x,y,z], "Rotation": [ex,ey,ez], "Scale": [1,1,1]},
       "PlaneBounds": {"Min": [u0,v0], "Max": [u1,v1]},
       "PlaneBoundary2D": [[u,v], ...],
       "VolumeBounds": {"Min": [..3], "Max": [..3]}}]}]}
  ```

  - `CoordinateSystem` is marked obsolete in code ("JSON files are now always serialized in OpenXR
    coordinate system"), but the test fixtures still say `Unity`.
  - `Rotation` is Euler degrees.
- **Labels:** FLOOR, CEILING, WALL_FACE, INVISIBLE_WALL_FACE, INNER_WALL_FACE, TABLE, COUCH,
  STORAGE, BED, SCREEN, LAMP, PLANT, WALL_ART, DOOR_FRAME, WINDOW_FRAME, GLOBAL_MESH, OTHER,
  UNKNOWN.
- **Queries:**
  - `MRUKRoom`: `Raycast`, `RaycastAll`, `GetBestPoseFromRaycast`, `TryGetClosestSurfacePosition`
    (with normal), `IsPositionInSceneVolume`, `GetKeyWall`, `FindLargestSurface`, `GetRoomOutline`.
  - `MRUKAnchor`: `Raycast`, `GetClosestSurfacePosition`, `IsPositionInBoundary`,
    `GetBoundsFaceCenters`.
- **Implementation:**
  - `MRUKAnchor.Raycast` moves the ray into anchor-local space (`transform.InverseTransformPoint`).
  - It then runs `Plane.Raycast` + point-in-polygon against `PlaneBoundary2D`, and a local AABB
    test for volumes.
  - No PhysX collider is involved.
- **Limits for us:**
  - No edges and no corners, only planes and boxes.
  - Oculus SDK licence.
  - Scene objects are "never to be deleted or modified from the outside" (MRUK doc [V]).
  - Whether a JSON-loaded room can be parented under our scalable scene root and still raycast
    correctly is untested [I]. The local-space maths suggests it would.
- **Verdict:** copy the representation and the maths into our own ~200-line snapper (§7.3).
  Writing an MRUK-JSON exporter is only worth it if P2 ever wants MRUK's EffectMesh or placement
  helpers.

## 2. Open source

| Project | Outputs | Licence | Linux / AMD / CPU | Accuracy / notes | What we copy |
|---|---|---|---|---|---|
| **CGAL Shape Detection** (Efficient RANSAC, region growing) | Planes (+ cylinders, spheres …) with inlier indices, from points or meshes | GPL-3 / commercial | CPU. **Python:** `pip install cgal` (SWIG bindings 6.0.1) exposes only `efficient_RANSAC` and `region_growing` [V, installed]. Everything else needs C++ (header-only, `CGAL-devel`) | Parameters: epsilon (distance), normal threshold, cluster epsilon, min points | Plane detection on the hybrid dense cloud from Python today |
| **CGAL Shape Regularization** | Planes made parallel, orthogonal or coplanar within angle/offset tolerances | GPL | C++ only | – | Regularization step (or 30 lines of numpy: cluster normals, snap to axes, merge offsets) |
| **CGAL Polygonal Surface Reconstruction (PolyFit)** | Watertight compact polygonal mesh from planar segments (integer programme, SCIP/GLPK) | GPL (`polyfit` on PyPI is a separate MIT wrapper) | CPU, C++ | Good on buildings. Slow on cluttered indoor scenes (R5: 1000× slower than planar simplification) | Exterior building proxy (facades, roof) |
| **CGAL Kinetic Surface Reconstruction** (6.0+) | Watertight piecewise-planar mesh; shapes grow kinetically until they collide, then min-cut [V] | GPL | CPU, C++. Ships a `ksr_building.cpp` example [V] | – | Low-poly planar **collision mesh** or building shell |
| **CGAL Surface Mesh Approximation (VSA)** | K planar proxies + a remeshed approximation of a mesh | GPL | CPU, C++ | – | A piecewise-planar collision mesh straight from our 200k mesh |
| **Open3D** `segment_plane`, `detect_planar_patches` | One RANSAC plane per call / a list of `OrientedBoundingBox` patches (`R[:,2]` = normal) [V, docs] | MIT | CPU wheels, 0.20.0 (Sep 2026) [V] | Community: patches need enough points per voxel; tune `voxel_size` to plane size (Open3D discussion #6233 [V]). Iterated `segment_plane` keeps producing spurious planes from leftover noise [S] | Baseline for S1 (already on the bench) |
| **PCL** (SAC segmentation, region growing) | Planes, clusters | BSD | C++ | Mature, but no Python path we'd use | Nothing beyond Open3D/CGAL |
| **pyransac3d** 0.7.0 | Plane, cuboid, line, cylinder RANSAC in numpy | Apache-2.0 [V PyPI] | CPU, pure Python | Slow on 1M points | Cuboid fit for appliances or boxes, if needed |
| **LIMAP 2.0** | 3D line segments, vanishing points, planes (PxwPlanar), wireframe; written as a COLMAP model + `structures/` [V] | BSD-3 [V] | `pip install pylimap` 2.0.0, manylinux x86_64 cp310–312 [V]. LSD on CPU; DeepLSD, GlueStick and PxwPlanar need torch ("CUDA" in README; ROCm untested). Needs `hloc` from requirements; Open3D has no py3.13 wheels | README only shows SfM pose AUC (holistic 51.6 vs COLMAP 33.7 @0.25°) [V] | Edge layer from our existing COLMAP model; VP for Manhattan axes |
| **PxwPlanar** (PixelwisePlanarity, ECCV 2026) | Per-pixel planarity, metric depth, normals → region growing → per-segment RANSAC planes [V] | MIT (incl. its MoGe fork) [V] | Torch. Pins its own MoGe fork, so use a separate venv | Evaluated with 3D P/R at 1/5/10 mm [V] | Per-frame planes, lifted with our poses |
| **Line3D++** | 3D line segments from SfM (a COLMAP reader exists, from 2016) | **MPL-2.0** [V, README + GitHub API; one search snippet wrongly said GPL] | CPU build without CUDA/Ceres is supported [V] | Unmaintained since 2020 | Fallback only if LIMAP fails |
| **SpatialLM 1.1** | Walls, doors, windows + oriented boxes (59 categories) as a structured text layout, from a point cloud [V] | Qwen-0.5B weights Apache-2.0, but the 1.1 code builds on **Sonata (CC-BY-NC-4.0)**; Llama variant under the Llama licence [V] | CUDA 12.4, flash-attn, torchsparse [V]. On ROCm, RDNA4 flash-attn and torchsparse are **unlikely without porting** [I] | Structured3D layout F1 94.3 @0.25 IoU; ScanNet detection F1 65.6 @0.25 [V]. Needs z-up axis-aligned clouds | Only as a labeller on a cloud GPU. Not critical path |
| **SceneScript** | `make_wall` / `make_door` / `make_window` / `make_bbox` programs | CC-BY-NC; weights on request for researchers [V] | Trained on Aria semi-dense points only [V] | – | The command-language idea; not the model |
| **OneFormer3D** | 3D semantic/instance/panoptic segmentation | CC-BY-NC-4.0 [V, LICENSE] | spconv/MinkowskiEngine CUDA stack [I] | ScanNet SOTA-class (2024) | Skip |
| **Mask3D** | 3D instance masks | MIT [V] | MinkowskiEngine, CUDA [I] | – | Skip; 2D→3D lifting is easier on AMD |
| **Open3D-ML** | Semantic segmentation (RandLA-Net, KPConv, PointTransformer) with S3DIS classes: wall, floor, ceiling, door, window, … [I, from memory] | MIT [V] | PyTorch; probably ROCm-OK [I] | Domain gap from laser scans to photogrammetry [I] | Low priority |
| **Pointcept / Sonata** | PTv3 backbones, Sonata SSL | MIT / Apache-2.0 [V GitHub API] (Sonata *weights* NC per SpatialLM's note [V]) | CUDA ops (spconv) [I] | – | Skip |
| **2D segmenters lifted to 3D** (OneFormer ADE20k, SAM 2, Grounded-SAM-2) | Per-frame masks → vote onto planes via `cameras.json` | MIT / Apache-2.0 [V] | ROCm torch works for plain transformer models [I] | ADE20k's 150 classes include wall, floor, ceiling, cabinet, door, windowpane, countertop, refrigerator, dishwasher, stove, sink [I, from memory] | Plane labels (the RoomPlan/Streem approach, §0.4) |
| **OpenMVS** | Mesh + texture (our pipeline) | AGPL-3 | CPU | `ReconstructMesh --smooth` **default 10 Taubin iterations** in develop; `--decimate`, `--edge-length` remesh. `RefineMesh` does photometric refinement with a CPU path (`--gpu-device cpu`) and has `--planar-vertex-ratio` (default 0) [V, source]. Issue #637: RefineMesh leaves building boundaries soft and roads non-planar [S] | **A/B `--smooth 0` now**; try RefineMesh once |
| **COLMAP** | `delaunay_mesher` (graph-cut, sharper) vs `poisson_mesher` (rounds edges) | BSD | CPU | – | We already use OpenMVS graph-cut. Nothing new |
| **pymeshlab** 2025.7 | Filters [V, installed]: `meshing_decimation_quadric_edge_collapse` (`planarquadric`, `planarweight`, `preservenormal`, `preserveboundary`), `apply_coord_two_steps_smoothing` (feature-preserving, `normalthr`=60°), `meshing_isotropic_explicit_remeshing` (`featuredeg`=30°), `meshing_edge_flip_by_planar_optimization`, `compute_selection_crease_per_edge`, `generate_plane_fitting_to_selection` | GPL-3 | CPU; already a dependency | – | `planarquadric=True` for the collision mesh; two-step smoothing before plane fitting |
| **libigl** 2.6.3 | `sharp_edges`, `fit_plane`, `AABB`, `point_mesh_squared_distance`, `signed_distance` [V, installed] | MPL-2.0 | CPU wheels | – | `sharp_edges` for the edge-sharpness metric; AABB for bench scoring |
| **QuestRoomScan** (genesisinteractive, MIT [V]) | On-Quest TSDF + Surface Nets. **RANSAC plane detector + GPU vertex PlaneSnap** [V, ALGORITHM.md] | MIT | Quest 3, Unity 6 | Planes: 2048 sampled vertices, 80 iterations, inlier <2 cm with normal dot >0.95, **axis bias 10°**, PCA refit, ≤6 planes, 2 ms budget. Snap: normal dot >0.9 **and** distance <3 cm → project | The exact vertex-snap rule for our regularized-mesh option (§6.3) |

## 3. Unity and VR snapping practice

**How CAD viewers snap.**
- CAD tools snap exactly because they keep the topology (B-rep edges and vertices), not because
  they analyse meshes [I].
- SketchUp's inference engine gives each target a colour and a tip:
  - endpoint: green circle;
  - midpoint: cyan;
  - on edge: red square, which "doesn't snap you to a finite point — it will only lock you along
    the edge";
  - on face: blue diamond;
  - plus axis, parallel and perpendicular inferences [S, SketchUp help / tutorials].
- AutoCAD uses a pick aperture in screen pixels and priority rules [I; not re-fetched].
- **Photogrammetry and point-cloud tools add a derived topology layer and reuse the same UX:**
  - ReCap segments feed AutoCAD's PEDGE/PCOR;
  - Revit prioritises detected-plane snaps over point snaps, and Tab cycles between them [S].

**What that means for Quest.**
- The structure layer *is* our B-rep: a few hundred planes, one to two thousand edges and a few
  hundred corners.
- A brute-force loop over flat arrays each frame is ~10⁴ flops, far below 0.1 ms even in IL2CPP
  [I]. No BVH or grid is needed until ~10⁴ primitives. Use a Burst job only if profiling says so.
- The mesh fallback stays `Physics.Raycast` on the MeshCollider (PhysX midphase BVH).

**Meta Interaction SDK hooks [V].**
- `ISurface` has `Raycast(in Ray, out SurfaceHit, float maxDistance)` and
  `ClosestSurfacePoint(in Vector3, out SurfaceHit, float)`.
- It is implemented by `PlaneSurface`, `ColliderSurface`, `CylinderSurface` and
  `NavMeshSurface`, and `RayInteractable` takes a Surface.
- A small `StructureSurface : ISurface` wrapper would let ISDK ray cursors land on structure
  planes natively [I].

**Unity tooling.**
- ProBuilder and Unity's editor vertex snapping (V key) are editor-time only. Unity Reflect Review's
  measure tool is two points plus drag handles, with no documented edge snap [S].
- Pixyz exists to import real CAD (B-rep → mesh). It is irrelevant for scans [I].

**Quest measuring apps.**
- MultiMeasure highlights "the edges of every object in the room".
- Reality Ruler: lines, areas, angles, volumes.
- Users asked for "snap two lines at a predefined angle or have lines level with the ground" [S,
  HowToGeek]. That is our axis-lock and level inference.

## 4. Community and hackathons

- **Nothing we can reuse directly.**
  - Devpost projects around RoomPlan (Table Book, Roomstyler) use RoomPlan's parametric output
    as-is. None snaps to a photogrammetry mesh [S].
  - Our earlier audit of six hackathon repos (`p1-prior-hackathons.md`) found no measurement or
    snapping code.
- **QuestRoomScan (MIT, 67★, pushed 2026-09-19 [V])** is the one community project that does what
  we want on-device.
  - It detects planes from scan vertices and pulls vertices onto them every mesh cycle.
  - It also uses MRUK SCREEN anchors as "analytic TSDF plane stamps" [V].
  - Takeaway: a strong axis bias, a hard distance gate (3 cm) and a normal gate (0.9) are enough
    to get flat walls without eating furniture [I].
- **Open3D community:** plane detection fails silently when voxel size and plane size don't match.
  Always log the plane count and inlier ratios [V, discussion #6233].
- **Reddit** (r/photogrammetry, r/computervision, r/virtualreality) blocked scripted access this
  session. Searches surfaced no threads with numbers [V: attempted]. The recurring advice in vendor
  docs is: measure plane-to-plane, away from corners (Matterport), and rescale photo models to a
  known length (Polycam).
- **Patents worth knowing** [V, Google Patents]:
  - Streem US12131427: semantic boundaries projected onto the mesh, faces split, vertices moved
    onto the boundary. Door and window edges become crisp.
  - Streem US12131426: dynamic regional mesh subdivision for measurement accuracy.
  - Hover US9437033B2: VP-grouped 3D lines, RANSAC planes, scale from orthophotos.

## 5. Accuracy expectations to set in the demo

| Source | Figure | What it measures |
|---|---|---|
| Polycam LiDAR room | ±½ in (~13 mm) | single rooms |
| RoomPlan | detection 90–95 % P/R; "up to ±5 cm per wall" [S] | – |
| Canvas | 1–2 % | – |
| Matterport Pro2 / Pro3 | 1 % / 20 mm @10 m | – |
| Hover | 2.6 % (Haag, 40 homes) [S] | exteriors from phone photos |
| EagleView | 0.2 ft average roof-line difference | – |
| Quest MR measure apps | ~±1 cm [S] | – |

- **For us** [I]:
  - The structure layer can take *relative* errors (a door width, a counter depth) from today's
    1–5 cm rounding to a few mm.
  - *Absolute* error is bounded by scale. A 1 % scale error is 9 mm on a 90 cm cabinet.
  - Quote results as "X mm or Y %, whichever is larger", the way the vendors do.

## 6. Techniques to copy, ranked

1. **Planes as frame + 2D polygon; edges = plane∩plane; corners = 3-plane meets** (RoomPlan, MRUK,
   AutoCAD/ReCap, Cyclone 3DR). This is S1 on the bench. Keep a `observed` flag per polygon edge,
   like RoomPlan's `completedEdges`.
2. **Manhattan axis snap at 5–10°** (QuestRoomScan 10°, Cyclone's vertical/axis fits). Take the
   axes from LIMAP vanishing points or from the dominant normal clusters.
3. **In-plane (orthographic) detection for doors, windows and cabinet fronts** (RoomPlan). Render
   each large plane's ortho texture from `mesh.glb` at ~2 mm/px, run LSD and a 2D
   segmenter/detector, then fit axis-aligned rectangles in (u,v). Repeated cabinet doors give the
   repeatability metric for free.
4. **Image-line edges on single planes** (LIMAP), projected onto their plane. This catches what
   plane∩plane cannot: door gaps, window frames, counter overhang.
5. **Snap UX from AutoCAD/Revit/SketchUp/Matterport.**
   - A strict priority: corner > edge > plane > mesh.
   - Local-only candidates near the pointer.
   - A way to cycle or disable snaps.
   - Axis/parallel/perpendicular inference while dragging.
   - One-gesture "wall to opposite wall".
6. **Optional sharp mesh via vertex snapping** (QuestRoomScan rule, extended; §7.4) and a
   `planarquadric` collision mesh. `--smooth 0` first.
7. **Known-dimension rescale in VR** (Canvas/Magicplan critical dimensions, Polycam Rescale). Tape
   a known element such as a door or counter height, type or say the true value, and the pipeline
   or app rescales. Structure planes make that tape reading exact.

## 7. Proposal: `structure.r<rev>.json` in the scene package

### 7.1 Package and contract changes

```
scene/<site>/
  structure.r<rev>.json     # NEW: planes, edges, corners, objects; same frame as mesh.r<rev>.glb
```

`scene.json` gains:

```json
"structure": {"file": "structure.r2.json", "planes": 64, "edges": 410, "corners": 138,
              "method": "planes+limap", "snap_ready": true}
```

- It is written before `scene.json`, like the other revision files (plan §1a).
- The preview revision may omit it. The app falls back to mesh snapping when it is absent.

### 7.2 Schema sketch

**Coordinates.**
- Everything is in the **glTF frame of `mesh.r<rev>.glb`**: metres, right-handed, +Y up.
- P2 applies the same handedness flip its importer applies. UnityGLTF negates X [S]. glTFast is
  assumed to do the same [I]; **P2 must verify this with one test corner** that should sit on a
  visible mesh corner.
- Plane-local 2D coordinates are in metres along `u` and `v = normal × u`.

```jsonc
{
  "schema": "airtools.structure/1",
  "frame": {"id": "kitchen-0095-7f3a", "units": "meters", "up": [0,1,0], "mesh": "mesh.r2.glb"},
  "axes": [[1,0,0],[0,1,0],[0,0,1]],        // dominant (Manhattan) directions; used for inference and axis-lock
  "quality": {                               // filled by the bench scorer, shown in the notebook/QA
    "plane_rms_m_median": 0.004, "edge_reproj_px_median": 0.9,
    "gt_corner_snap_mm_median": null, "scale_method": "known_dimension"
  },
  "planes": [{
    "id": "p7",
    "label": "cabinet_face",                 // wall|floor|ceiling|counter|cabinet_face|appliance|door|window|facade|roof|unknown
    "label_conf": 0.82,
    "origin": [1.20, 0.45, -2.31],           // a point on the plane (polygon centroid)
    "normal": [0,0,1], "u": [1,0,0],         // v = normal x u; exactly orthonormal
    "polygon": [[-0.61,-0.44],[0.61,-0.44],[0.61,0.44],[-0.61,0.44]],  // outer ring in (u,v), CCW seen from +normal
    "holes": [],                             // inner rings (a window in a wall)
    "observed": [true,true,false,true],      // per polygon edge: real boundary (true) or scan limit (false), cf. RoomPlan completedEdges
    "area_m2": 1.07, "rms_m": 0.003, "support": 18234,
    "regularized": {"axis": 2, "delta_deg": 1.4},  // snapped to axes[2] by 1.4 deg; null if free
    "parent": "p3",                          // cabinet face -> wall it's in front of; window -> wall (RoomPlan parentIdentifier)
    "confidence": "high"                     // high|medium|low
  }],
  "edges": [{
    "id": "e41",
    "a": [0.59, 0.01, -2.31], "b": [0.59, 0.89, -2.31],
    "kind": "crease",                        // crease (plane∩plane) | boundary (one plane's observed outline) | line (image line inside a plane)
    "planes": ["p7","p9"],                   // 2 for crease, 1 for boundary/line
    "dihedral_deg": 90.4,                    // crease only
    "src": ["plane_intersection","limap"],   // what produced / confirmed it
    "reproj_px": 0.8, "views": 23,           // image support when a 3D line matched it
    "confidence": "high"
  }],
  "corners": [{
    "id": "c12", "p": [0.59, 0.89, -2.31],
    "kind": "3plane",                        // 3plane | 2edge | endpoint
    "planes": ["p7","p9","p2"], "edges": ["e41","e44"],
    "residual_m": 0.003,                     // spread of the pairwise intersection points
    "confidence": "high"
  }],
  "objects": [{                              // optional: parametric things for labels, part fitting and repeat checks
    "id": "o5", "label": "cabinet_door",     // door|window|cabinet_door|drawer|dishwasher|fridge|oven|sink|outlet
    "plane": "p7", "rect": [-0.60,-0.43,-0.01,0.43],   // [u0,v0,u1,v1] in the plane's frame
    "depth_m": null,                         // for boxes (appliances) use "box": {"center","size","rotation"} instead of rect
    "group": "g1", "confidence": "medium"
  }],
  "groups": [{"id": "g1", "kind": "repeat", "members": ["o5","o6","o7"], "pitch_m": 0.60, "width_spread_mm": 3}]
}
```

**Why this shape.**
- **Planes are frame + polygon** (MRUK's `Transform` + `PlaneBoundary2D`, RoomPlan's
  `transform` + `polygonCorners`). They are planar by construction and cheap to raycast. The same
  data can be exported to MRUK JSON (`Transform` from origin/normal/u,
  `PlaneBoundary2D` = polygon) if ever needed.
- **Edges and corners are explicit even though they derive from planes.** The app then does no
  geometry. `line` edges from LIMAP have no second plane, and corners need a residual to show
  confidence.
- **`observed` per polygon edge** stops the tape from snapping to the artificial border where the
  scan ran out.
- **`objects` reference a plane plus an in-plane rect** (the RoomPlan door/window pattern). Width
  and height are then exact, and `groups` expose the repeatability metric the bench wants.
- **Size:** ~100 planes × ~0.5 kB + 2k edges × 0.2 kB ≈ 0.5 MB of JSON, or ~100 kB gzipped [I].
  That is negligible next to the 5.9 MB package.

### 7.3 How P3 snaps to it (Unity recipe)

**Load.**
- Parse once into flat arrays of structs in **scene-root local space**.
- Planes: `origin, n, u, v, polygon[]`, 2D AABB. Edges: `a, b, dir, len`. Corners: `p`.
- Every query transforms the hand ray or pinch tip into root-local space with
  `root.InverseTransformPoint/Direction`. That keeps tabletop mode (root scale 1:50) working
  unchanged.
- World-space tolerances are divided by `root.lossyScale.x`.

**Query `Snap(Ray r, Vector3? tip)`**, priority order as in AutoCAD, Revit and SketchUp.

1. **Depth gate first.**
   - `Physics.Raycast` against the collision MeshCollider gives `meshHit`.
   - Any structure candidate must lie within `max(3 cm, 2 % of hit distance)` of `meshHit`, or of
     the ray segment in front of it.
   - This stops snaps to corners behind a wall or cabinet, the common failure in CAD viewers.
2. **Corner.** Pick the candidate with the smallest angular distance to the ray
   (`angle(r.dir, p - r.origin) < 1.5°`), or `|p - tip| < 2.5 cm` for direct touch. Among ties,
   prefer `confidence: high`.
3. **Edge.**
   - Compute the closest points between the ray and segment `ab` (standard line-line closest
     approach, clamped to the segment).
   - Accept if the angular distance is < 1.2° or the distance to the tip is < 2 cm.
   - The snapped point is on the segment, like SketchUp's "on edge" red square.
   - Also offer the **midpoint** (SketchUp cyan).
4. **Plane.**
   - Ray/plane intersection, then point-in-polygon in (u,v) (outer ring minus holes), exactly like
     `MRUKAnchor.Raycast`.
   - Accept if it is within the depth gate of `meshHit`.
   - The hit normal is the plane normal, never `RaycastHit.normal`. The level and part-seating
     tools become exact.
5. **Mesh.** `meshHit` as today.

**Stability and UX.**
- **Hysteresis:** keep the current snap until the pointer leaves 1.5× its acquire tolerance.
  Hand-ray jitter otherwise flickers between a corner and its edges.
- **Feedback:** a distinct glyph and colour per kind (corner, edge, plane), plus a haptic tick on
  acquire. Highlight the snapped edge line or the plane outline. Pinch-hold, or a toolbox toggle,
  disables snapping (Matterport's Alt, Revit's Tab-cycle).
- **Inference while dragging the tape's second point** (SketchUp): when the segment direction is
  within 3° of an `axes[i]`, an edge direction, or a plane normal, lock to it and show "on axis",
  "parallel" or "perpendicular". The Quest reviewers asked for exactly this (§3).

**Tool-specific use.**
- **Tape:** "wall-to-wall". Double-pinch on a plane casts along its normal to the first
  facing, roughly parallel plane (|n·n'| > cos 5°) and reports the plane distance. Evaluate it at
  the pinch point, not at corners (the Matterport feature).
- **Level:** show the tilt of the plane normal relative to +Y. Use `regularized.delta_deg` to show
  "measured 1.4° off true" honestly.
- **Protractor:** snap three corners, or pick two planes and report `dihedral_deg`.
- **Part placement:** seat on the plane, then slide along the plane to the nearest edge within
  5 cm (a part against the wall and the counter). `OverlapBox` fit tests still use the mesh
  collider.

**Performance [I].**
- A kitchen is ≤ 100 planes, ≤ 2k edges and ≤ 500 corners. One brute-force pass is well under
  0.1 ms on the Quest 3S CPU.
- Build the arrays once per revision swap. There is no per-frame allocation.
- Add a uniform grid only past ~10k primitives, for a whole building exterior.

**Swap-in-place.** On revision change, load `structure.r<rev>.json` together with the collision
mesh. Keep any snapped measurement's stored points as they are; they are in the same frame.

### 7.4 Producing it (for the experiment agents)

This maps onto the bench's S1, S2 and S5.

1. **Planes.**
   - Run region growing on the hybrid dense cloud: CGAL `region_growing` from Python, or Open3D
     patches.
   - Do a PCA refit.
   - Regularize with Manhattan axes from normal clustering or LIMAP VPs; snap within ≤ 8°.
   - Merge coplanar planes (angle < 2°, offset < 1.5 cm).
   - Build the polygon as a 2D alpha shape of the inliers in (u,v), simplified (Douglas–Peucker,
     1 cm) and squared to the axes.
2. **Edges.**
   - For each pair of planes with dihedral > 20°, intersect them.
   - Keep the interval where both planes have inliers within 3 cm of the line; that interval is a
     `crease`.
   - Add LIMAP 3D segments: associate each to a plane (distance < 2 cm, direction in-plane
     < 5°), project it onto the plane, and merge it with any crease it overlaps (`src` gets both).
     Unassociated strong lines become `line` edges with no plane.
3. **Corners.**
   - Intersect triples of planes whose creases meet within 2 cm. `residual_m` is the spread of the
     three pairwise edge endpoints.
   - Edge-edge closest points under 1 cm give `2edge` corners.
4. **Labels and objects.**
   - Vote 2D segmentations onto planes via `cameras.json`.
   - Orthophoto per plane, then LSD + rectangles → `objects` + `groups`.
5. **Optional sharp mesh.** For each mesh vertex:
   - near exactly one plane (normal dot > 0.9, distance < 3 cm): project it onto the plane (the
     QuestRoomScan rule);
   - within 3 cm of a crease: project onto the line;
   - within 3 cm of a corner: move onto the corner.
   - Displacements are ≤ 3 cm, so the UVs stay valid [I]. Check PSNR/SSIM against the 21.9 /
     0.806 hybrid. Build the collision mesh from the snapped mesh with `planarquadric=True`.
6. **Score** with the bench metrics (§2 of the bench) before shipping. Emit `quality` into the JSON.

## 8. What to start now

1. **`ReconstructMesh --smooth 0`** A/B test on the cached kitchen union cloud: edge-sharpness band
   and PSNR. Confirm the hfbox binary's default with `--help` first. [~10 min]
2. **LIMAP 2.0 on the kitchen COLMAP model** (S2): `pip install pylimap` into a py3.12 venv with
   `hloc`; LSD first on CPU, then DeepLSD on ROCm torch.
3. **Write the `structure.r<rev>.json` emitter** alongside S1, so P3 can build the §7.3 snapper
   against the practice package now. A hand-made JSON with 6 planes and their creases and corners
   is enough to unblock P3 today.
4. **P3/P2:**
   - Verify the glTF → Unity handedness flip with one known corner.
   - Implement the depth gate and hysteresis before anything fancy. They matter more than
     detection quality.

## Sources

Products:
[RoomPlan research](https://machinelearning.apple.com/research/roomplan) ·
[CapturedRoom docs](https://developer.apple.com/documentation/roomplan/capturedroom) ·
[MRUK scene data](https://developers.meta.com/horizon/documentation/unity/unity-mr-utility-kit-manage-scene-data/) ·
[MRUK package (npm.developer.oculus.com, com.meta.xr.mrutilitykit 207.0.0)](https://npm.developer.oculus.com/com.meta.xr.mrutilitykit) ·
[ISDK ISurface](https://developers.meta.com/horizon/reference/interaction/v74/interface_oculus_interaction_surfaces_i_surface) ·
[Polycam accuracy](https://learn.poly.cam/hc/en-us/articles/50434745985556-How-Accurate-Are-Polycam-Scans) ·
[Polycam measure](https://learn.poly.cam/hc/en-us/articles/29647317758100-How-to-Measure-Your-Captures) ·
[Matterport accuracy](https://support.matterport.com/s/article/How-accurate-are-dimensions-in-Matterport-Spaces?language=en_US) ·
[Matterport measurement mode](https://support.matterport.com/hc/en-us/articles/360039698514-How-to-Use-Measurement-Mode-on-Desktop-and-Mobile-) ·
[Magicplan accuracy](https://help.magicplan.app/how-accurate-is-magicplan) ·
[Canvas accuracy](https://support.canvas.io/article/5-what-kind-of-accuracy-can-i-expect-from-canvas) ·
[Hover reports](https://hover.to/reports/) ·
[Hover vs EagleView (Haag study)](https://hookagency.com/blog/hover-vs-eagleview/) ·
[Hover patent US9437033B2](https://patents.google.com/patent/US9437033B2/en) ·
[S23DR 2025 winner](https://arxiv.org/abs/2506.16421) ·
[S23DR 2026 winner](https://arxiv.org/html/2606.06695) ·
[EagleView 98.77 %](https://www.globenewswire.com/news-release/2025/06/04/3093587/0/en/EagleView-Roof-Measurements-Confirmed-to-Be-98-77-Accurate-Compared-to-Independent-Benchmark-Measurements.html) ·
[Pix4Dmatic vectorization](https://support.pix4d.com/hc/vectorization-pix4dmatic) ·
[PIX4Dsurvey vectorization](https://support.pix4d.com/hc/en-us/articles/360033317432) ·
[Metashape snapping](https://agisoft.freshdesk.com/support/solutions/articles/31000159337-snapping-options-for-shape-drawing) ·
[Cyclone 3DR building extraction](https://rcdocs.leica-geosystems.com/cyclone-3dr/2025.2/BuildingExtraction) ·
[EdgeWise](https://www.clearedge3d.com/products/edgewise/) ·
[AutoCAD point-cloud osnaps](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-9A6E6745-B838-4D00-95B7-952D9002EC10.htm) ·
[Revit point clouds](https://help.autodesk.com/cloudhelp/2022/ENU/Revit-Model/files/GUID-BD499295-84DD-4BDE-B60D-73008AFBC791.htm) ·
[ReCap snaps](https://help.autodesk.com/cloudhelp/2017/ENU/Reality-Capture/files/GUID-5939DFAC-0179-4C59-81C9-F614BA7EB86E.htm) ·
[Scaniverse / Niantic Spatial](https://www.nianticspatial.com/en/products/capture) ·
[RealityScan 2.0](https://www.realityscan.com/news/realityscan-20-new-release-brings-powerful-new-features-to-a-rebranded-realitycapture) ·
[Quest measuring apps (HowToGeek)](https://www.howtogeek.com/how-i-measured-everything-in-my-house-with-the-meta-quest-3/) ·
[SketchUp drawing basics](https://help.sketchup.com/en/sketchup/introducing-drawing-basics-and-concepts) ·
[Streem US12131427](https://patents.google.com/patent/US12131427) ·
[Streem US12131426](https://patents.google.com/patent/US12131426)

Open source:
[LIMAP](https://github.com/cvg/limap) · [pylimap on PyPI](https://pypi.org/project/pylimap/) ·
[PixelwisePlanarity](https://github.com/alpayozkan/PixelwisePlanarity) ·
[Line3D++](https://github.com/manhofer/Line3Dpp) ·
[SpatialLM](https://github.com/manycore-research/SpatialLM) ·
[SceneScript](https://github.com/facebookresearch/scenescript) ·
[OneFormer3D](https://github.com/oneformer3d/oneformer3d) · [Mask3D](https://github.com/JonasSchult/Mask3D) ·
[Open3D-ML](https://github.com/isl-org/Open3D-ML) · [Pointcept](https://github.com/Pointcept/Pointcept) ·
[CGAL KSR](https://doc.cgal.org/latest/Kinetic_surface_reconstruction/index.html) ·
[CGAL SWIG bindings](https://github.com/CGAL/cgal-swig-bindings) ·
[Open3D PointCloud API](https://www.open3d.org/docs/release/python_api/open3d.geometry.PointCloud.html) ·
[Open3D discussion #6233](https://github.com/isl-org/Open3D/discussions/6233) ·
[OpenMVS ReconstructMesh.cpp](https://github.com/cdcseacave/openMVS/blob/develop/apps/ReconstructMesh/ReconstructMesh.cpp) ·
[OpenMVS RefineMesh.cpp](https://github.com/cdcseacave/openMVS/blob/develop/apps/RefineMesh/RefineMesh.cpp) ·
[OpenMVS issue #637](https://github.com/cdcseacave/openMVS/issues/637) ·
[QuestRoomScan](https://github.com/genesisinteractive/QuestRoomScan) ·
[Structure-preserving planar simplification](https://arxiv.org/html/2408.06814v2) ·
[UnityGLTF handedness](https://github.com/KhronosGroup/UnityGLTF/issues/257)
