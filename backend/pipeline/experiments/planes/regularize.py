"""S3: regularize the *untextured* dense mesh onto a structure layer before texturing, so the
texture is baked on the snapped surface (docs/research/structure/s3-learned-planes.md §5).

    python regularize.py --structure s.json --scene scene/bench/i-hybrid-moge2 \
        --dense work/dji0095/sfm/dense --mesh-in work/.../mvs/union_mesh.ply --out reg.ply

The mesh is OpenMVS's, in the COLMAP frame. The snap rule is S1's
(`pipeline.experiments.structure.regularize.snap_vertices`): the R6 vertex rule (|normal . n| >
`--normal-dot`, within `--plane-tol` and inside the polygon + `--margin`), then vertices within
`--edge-r` of a crease go onto it and within `--corner-r` of a corner onto it. Tolerances are
scene metres and are converted with the camera-centre Sim(3) (`pxw_lift.scene_sim3`); a structure
in the scene frame is moved into the COLMAP frame with the same Sim(3).
"""

import argparse
import json
import sys
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[3]
if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)
sys.path.insert(0, str(_ROOT))

import numpy as np


def main() -> None:
    import trimesh

    from pipeline.experiments.planes.pxw_lift import scene_sim3
    from pipeline.experiments.structure.regularize import snap_vertices, vertex_normals
    from pipeline.experiments.structure.score import Structure

    p = argparse.ArgumentParser()
    p.add_argument("--structure", required=True)
    p.add_argument("--scene", required=True, help="scene dir (for cameras.r*.json)")
    p.add_argument("--dense", required=True, help="COLMAP dir of the mesh's frame")
    p.add_argument("--mesh-in", required=True, help="untextured ply, COLMAP frame")
    p.add_argument("--out", required=True, help="output ply")
    p.add_argument("--plane-tol", type=float, default=0.03, help="scene m")
    p.add_argument("--normal-dot", type=float, default=0.9)
    p.add_argument("--margin", type=float, default=0.02, help="scene m")
    p.add_argument("--edge-r", type=float, default=0.01, help="scene m")
    p.add_argument("--corner-r", type=float, default=0.01, help="scene m")
    a = p.parse_args()
    scene = Path(a.scene)
    cams = json.loads((scene / "scene.json").read_text())["cameras"]
    s, R, t = scene_sim3(Path(a.dense), scene / cams)  # x_colmap = s R x_scene + t
    doc = json.loads(Path(a.structure).read_text())
    st = Structure(doc)
    if doc.get("frame", "scene") == "scene":
        st.transform(s, R, t)
    elif Path(doc["cameras"]).resolve() != (Path(a.dense) / "sparse").resolve():
        raise SystemExit(f"structure is in the frame of {doc['cameras']}, not {a.dense}")
    m = trimesh.load(a.mesh_in, process=False)
    V = np.asarray(m.vertices, np.float64)
    V2, stats = snap_vertices(
        V, vertex_normals(V, np.asarray(m.faces)), st, a.plane_tol * s, a.normal_dot,
        a.margin * s, a.edge_r * s, a.corner_r * s,
    )  # fmt: skip
    m.vertices = V2
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    m.export(a.out)
    stats["disp_mm_p50_moved"] /= s
    stats["disp_mm_max"] /= s
    print(json.dumps(stats | {"vertices": len(V), "out": a.out}))


if __name__ == "__main__":
    main()
