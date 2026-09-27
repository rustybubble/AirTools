# `structure.json` schema, `airtools.structure/1`

The snap layer the structure bench scores and P3 snaps to. It is S1's v1 draft merged with R6's
proposal (`r6-industry.md` §7.2). **Only the fields marked "required" are needed**; everything
else is optional, so a LIMAP-only run can write just edges. The scorer
(`python -m pipeline.experiments.structure score`) reads exactly this.

## Where it lives

- In the scene package: `scene/<site>/structure.r<rev>.json`, next to `mesh.r<rev>.glb`. It is
  written before `scene.json`, like every other revision file (plan §1a). `scene.json` gains:
  ```json
  "structure": {"file": "structure.r2.json", "schema": "airtools.structure/1",
                "edges": 671, "corners": 594, "planes": 61, "objects": 40}
  ```
- On the bench: anywhere. Pass it to the scorer with `--structure`.

## Frame

- `"frame": "scene"` (the default): the **glTF frame of `mesh.r<rev>.glb`**, in metres (the scene's
  calibration), right-handed, +Y up. This is the same frame as `cameras.r<rev>.json`.
- `"frame": "cameras"` is bench-only. The file then also gives `"cameras"`, which is a pipeline
  `cameras*.json` or a COLMAP sparse dir whose image names are `%04d.jpg` on the 10 fps grid, and
  optionally `"id_fps": 10`. The scorer Sim(3)-aligns those camera centres onto the GT track by
  video time. Use this for anything in raw COLMAP units, such as LIMAP output.
- Plane-local 2D coordinates are metres along `u` and `v = normal × u`.

## Fields

```jsonc
{
  "schema": "airtools.structure/1",
  "frame": "scene",                       // or "cameras" (+ "cameras", "id_fps"), see above
  "method": "s1-planes-v1", "params": {},  // free-form provenance
  "cost": {"runtime_s": 42.0, "host": "hfbox", "cores": 8},
  "axes": [[1,0,0],[0,1,0],[0,0,1]],       // dominant (Manhattan) directions, optional
  "quality": {},                           // filled from the bench scorer, optional

  "planes": [{
    "id": "p7",
    "normal": [0,0,1],                     // REQUIRED, unit
    "offset": -1.23,                       // REQUIRED, n·x + offset = 0
    "origin": [1.20, 0.45, -2.31],         // a point on the plane (polygon centroid)
    "u": [1,0,0],                          // in-plane axis; v = normal × u
    "polygon": [[-0.61,-0.44],[0.61,-0.44],[0.61,0.44],[-0.61,0.44]],  // outer ring in (u,v), CCW seen from +normal
    "polygon3d": [[x,y,z], ...],           // alternative to origin/u/polygon (scorer accepts either)
    "holes": [],                           // inner rings in (u,v)
    "observed": [true,true,false,true],    // per polygon edge: real boundary vs scan limit (RoomPlan completedEdges)
    "area_m2": 1.07, "rms_m": 0.003, "support": 18234,
    "regularized": {"axis": 2, "delta_deg": 1.4},  // snapped to axes[2] by 1.4°; null if free
    "label": "STORAGE", "label_conf": 0.8, // MRUK class names: WALL_FACE FLOOR CEILING TABLE STORAGE DOOR_FRAME WINDOW_FRAME OTHER ...
    "parent": "p3", "confidence": "high"   // high | medium | low
  }],

  "edges": [{
    "id": "e41",
    "a": [0.59, 0.01, -2.31], "b": [0.59, 0.89, -2.31],   // REQUIRED
    "kind": "crease",                      // crease (plane∩plane) | boundary (one plane's outline) | line (image line)
    "planes": ["p7","p9"],                 // 2 for crease, 1 for boundary/line, [] if unknown
    "dihedral_deg": 90.4,
    "src": ["plane_intersection","limap"],
    "reproj_px": 0.8, "views": 23, "support": 0.9, "confidence": "high"
  }],

  "corners": [{
    "id": "c12",
    "p": [0.59, 0.89, -2.31],              // REQUIRED
    "kind": "3plane",                      // 3plane | 2edge | endpoint
    "planes": ["p7","p9","p2"], "edges": ["e41","e44"],
    "residual_m": 0.003, "confidence": "high"
  }],

  "objects": [{"id": "o5", "label": "cabinet_door", "plane": "p7", "rect": [-0.60,-0.43,-0.01,0.43],
               "group": "g1", "confidence": "medium"}],     // S4
  "groups": [{"id": "g1", "kind": "repeat", "members": ["o5","o6"], "pitch_m": 0.60, "width_spread_mm": 3}]
}
```

Rules:
- A plane without a polygon is treated as infinite by the scorer, and it will then capture every
  snap within the radius. Always give a polygon (2D or 3D) for real output.
- `normal` and `offset` stay authoritative. `origin` must satisfy them.
- Corner endpoints should come from plane or line **intersections**, not from segment endpoints,
  which occlusion cuts short (R5).

## How it is snapped (bench model = P3 recipe, R6 §7.3)

The scorer reports two policies side by side (`snap.policies` in results.jsonl):

- **`r6`, the headline** (what P3 ships, R6 §7.3): a corner within 2.5 cm of the tip, else an edge
  within 2 cm, else a plane (point-in-polygon, within `--radius`, 3 cm), else the collision mesh
  (`ClosestPoint`). When both a corner and an edge are in range, the corner wins only if
  d_corner <= d_edge + 1 cm.
- **`priority`** (the first bench model): corner > edge > plane > mesh, all within `--radius`.

P3 adds a depth gate against the mesh raycast and hysteresis. The scorer has no ray, so it models
the tip as the GT point plus an aim miss (`--aim`, default ±1 cm uniform ball). Stats are over GT
points of grades A and B (`--grades AB`); grade C points are suspect and excluded; `*_gradeA` stats
use grade A only.
