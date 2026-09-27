"""Merge S4's in-plane rectangles (doors, drawers, appliance fronts) into the S2 fused layer.

Both docs must be in the scene frame (`frame.py` moves the fused layer there).
- A rect side that runs along a LIMAP line (within `ang_deg` and both ends within `snap_m` of it)
  takes that line's position, and the rect corners are re-intersected from their two sides.
  The rect keeps its topology but gains the line's accuracy.
- Rect corners are emitted only where both sides run on LIMAP lines (`rect_corners="lines"`):
  an unsupported rect corner (5.6 mm off at the microwave on the pipeline e2e run) outranks a
  1.3 mm edge under the snap policy. `rect_corners="all"` keeps every one.
- Corner dedupe within `dedupe_m`: by default the LIMAP-derived corner wins (it scored more
  accurate on the GT); `prefer="rect"` drops the fused corner instead.
- S4's copies of S3 face planes are dropped (the fused layer has them); its `s4-relief`
  sub-planes (dishwasher front, recessed panels), objects[] and groups[] are kept for P3.

    python -m pipeline.experiments.lines.rects --fused f_scene.json --rects s4.json --out out.json
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from pipeline.experiments.lines import structure as st


def merge_rects(
    fused: dict,
    rects: dict,
    snap_m: float = 0.01,
    ang_deg: float = 3.0,
    dedupe_m: float = 0.01,
    prefer: str = "fused",
    rect_corners: str = "lines",
    tol_scale: float = 1.0,
) -> dict:
    """`tol_scale` multiplies `snap_m` and `dedupe_m` (pipeline.structure.tol_scale)."""
    assert fused["frame"] == "scene" and rects.get("frame", "scene") == "scene"
    snap_m, dedupe_m = snap_m * tol_scale, dedupe_m * tol_scale
    lines = [
        st.Seg(np.array(e["a"], float), np.array(e["b"], float))
        for e in fused["edges"]
        if e.get("kind", "line") == "line"
    ]
    cos_tol = np.cos(np.radians(ang_deg))

    def snap_side(a: np.ndarray, b: np.ndarray):
        """(point, dir) of the best LIMAP line under side a-b, else the side's own line."""
        side = st.Seg(a, b)
        best, best_d = None, snap_m
        for s in lines:
            if abs(s.d @ side.d) < cos_tol:
                continue
            perp = [np.linalg.norm(np.cross(x - s.p0, s.d)) for x in (a, b)]
            t = sorted(((a - s.p0) @ s.d, (b - s.p0) @ s.d))
            if max(perp) <= best_d and t[1] > 0 and t[0] < s.length:  # overlaps the line
                best, best_d = s, max(perp)
        return (best.p0, best.d, True) if best is not None else (a, side.d, False)

    edges, corners, objects, n_snapped = [], [], [], 0
    for o in rects["objects"]:
        c3 = [np.array(c, float) for c in o["corners3d"]]
        sides = [snap_side(c3[i], c3[(i + 1) % 4]) for i in range(4)]
        n_snapped += sum(s[2] for s in sides)
        new = []
        for i in range(4):  # corner i sits between side i-1 and side i
            (p, d, _), (q, e, _) = sides[i - 1], sides[i]
            mid, _, _, _ = st._closest(st.Seg(p, p + d), st.Seg(q, q + e))
            new.append(mid)
        oid = o["id"]
        for i in range(4):
            edges.append(
                {
                    "id": f"{oid}e{i}",
                    "a": new[i].tolist(),
                    "b": new[(i + 1) % 4].tolist(),
                    "kind": "boundary",
                    "planes": [o["plane"]],
                    "object": oid,
                    "src": ["s4_rect"] + (["limap"] if sides[i][2] else []),
                    "confidence": o.get("confidence", "medium"),
                }
            )
            if rect_corners == "lines" and not (sides[i - 1][2] and sides[i][2]):
                continue
            corners.append(
                {
                    "id": f"{oid}c{i}",
                    "p": new[i].tolist(),
                    "kind": "rect",
                    "planes": [o["plane"]],
                    "object": oid,
                    "edges": [f"{oid}e{(i - 1) % 4}", f"{oid}e{i}"],
                    "confidence": o.get("confidence", "medium"),
                }
            )
        objects.append(dict(o, corners3d=[c.tolist() for c in new]))

    rect_p = np.array([c["p"] for c in corners]).reshape(-1, 3)
    fused_p = np.array([c["p"] for c in fused["corners"]]).reshape(-1, 3)

    def far(p, others):
        return not len(others) or np.linalg.norm(others - p, axis=1).min() > dedupe_m

    if prefer == "rect":
        kept = [c for c in fused["corners"] if far(c["p"], rect_p)]
    else:  # LIMAP corners are more accurate; the rect keeps its sides and object refs
        kept = list(fused["corners"])
        corners = [c for c in corners if far(np.array(c["p"]), fused_p)]
    out = dict(fused)
    out.update(
        method=fused.get("method", "fused") + "+s4-rects",
        planes=fused.get("planes", [])
        + [p for p in rects.get("planes", []) if p.get("source") == "s4-relief"],
        edges=fused["edges"] + edges,
        corners=kept + corners,
        objects=objects,
        groups=rects.get("groups", []),
    )
    out["counts"] = dict(
        fused.get("counts", {}),
        rects=len(objects),
        rect_sides_on_limap=n_snapped,
        fused_corners_dropped=len(fused["corners"]) - len(kept),
        rect_corners=len(corners),
    )
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fused", type=Path, required=True, help="scene-frame fused structure")
    ap.add_argument("--rects", type=Path, required=True, help="S4 structure (scene frame)")
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args()
    out = merge_rects(json.loads(a.fused.read_text()), json.loads(a.rects.read_text()))
    a.out.write_text(json.dumps(out, indent=1))
    print(json.dumps(out["counts"]))


if __name__ == "__main__":
    main()
