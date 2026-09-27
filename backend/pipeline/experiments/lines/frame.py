"""Move a structure.json from the raw COLMAP frame ("cameras") into the scene frame ("scene": the
glTF metric frame of mesh.r<rev>.glb, +Y up), as the pipeline would publish structure.r<rev>.json.

The pipeline maps raw SfM coordinates to the scene with `cameras_matrix = scale @ align_matrix`
(pipeline/run.py), the same similarity it applies to the mesh (mesh.process_mesh) and cameras.
In the pipeline, pass that matrix to `to_scene`. For a published run, it is recovered exactly
from cameras.r<rev>.json against the COLMAP model (Umeyama on the camera centres, matched by id).

    python -m pipeline.experiments.lines.frame --structure s.json \
        --cameras scene/<site>/cameras.r1.json [--model <sfm>/dense/sparse] --out s_scene.json
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from pipeline.calibrate import apply_similarity, similarity_matrix, umeyama


def matrix_from_cameras(cameras_json: Path, model_dir: Path) -> tuple[np.ndarray, float]:
    """(4x4 similarity raw COLMAP -> scene, rms residual in metres) from matching camera centres."""
    import pycolmap

    rec = pycolmap.Reconstruction(str(model_dir))
    raw = {Path(im.name).stem: im.projection_center() for im in rec.images.values()}
    cams = json.loads(Path(cameras_json).read_text())
    ids = [c["id"] for c in cams if c["id"] in raw]
    src = np.array([raw[i] for i in ids])
    dst = np.array([next(c["position"] for c in cams if c["id"] == i) for i in ids])
    s, r, t, rms = umeyama(src, dst)
    return similarity_matrix(s, r, t), rms


def to_scene(doc: dict, m: np.ndarray) -> dict:
    """Apply the similarity `m` (x -> s R x + t) to every coordinate in a structure doc."""
    s = np.cbrt(np.linalg.det(m[:3, :3]))
    r, t = m[:3, :3] / s, m[:3, 3]
    pts = lambda x: apply_similarity(np.asarray(x, float).reshape(-1, 3), m)
    out = {k: v for k, v in doc.items() if k not in ("cameras", "id_fps")}
    out["frame"] = "scene"
    if "axes" in doc:
        out["axes"] = (np.asarray(doc["axes"]) @ r.T).tolist()
    out["edges"] = [
        dict(e, a=pts(e["a"])[0].tolist(), b=pts(e["b"])[0].tolist()) for e in doc["edges"]
    ]
    out["corners"] = [dict(c, p=pts(c["p"])[0].tolist()) for c in doc["corners"]]
    planes = []
    for p in doc.get("planes", []):
        n = r @ np.asarray(p["normal"], float)
        q = dict(p, normal=n.tolist(), offset=float(s * p["offset"] - n @ t))
        for key in ("polygon", "polygon3d"):
            poly = p.get(key)
            if poly is not None and len(poly) and len(poly[0]) == 3:
                q[key] = pts(poly).tolist()
        if "origin" in p:
            q["origin"] = pts(p["origin"])[0].tolist()
        if "u" in p:
            q["u"] = (r @ np.asarray(p["u"], float)).tolist()
        planes.append(q)
    out["planes"] = planes
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--structure", type=Path, required=True)
    ap.add_argument("--cameras", type=Path, required=True, help="scene cameras.r<rev>.json")
    ap.add_argument("--model", type=Path, default=None, help="default: the doc's 'cameras'")
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args()
    doc = json.loads(a.structure.read_text())
    m, rms = matrix_from_cameras(a.cameras, a.model or Path(doc["cameras"]))
    a.out.write_text(json.dumps(to_scene(doc, m), indent=1))
    print(json.dumps({"scale": float(np.cbrt(np.linalg.det(m[:3, :3]))), "rms_m": rms}))


if __name__ == "__main__":
    main()
