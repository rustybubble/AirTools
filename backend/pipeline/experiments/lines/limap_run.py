"""LIMAP 3D line map on our fixed COLMAP poses, then snap-ready edges and corners (S2).

Needs a Python 3.12 venv with `pylimap` (+ LIMAP's requirements.txt and CPU/ROCm torch), see
docs/research/structure/s2-lines.md. Steps:
  1. copy the undistorted model with the bench's held-out frames (id % 25 == 1) deregistered;
  2. `limap.cli.automatic_point_line_triangulation` on it (poses stay fixed);
  3. export raw 3D lines, then Manhattan-snap, merge collinear, find corners ->
     structure.json (all) and structure_axis.json (structural subset), S1's schema.

    python -m pipeline.experiments.lines.limap_run --sfm <work>/sfm --out <dir> \
        --limap-src <limap git checkout> --m-per-unit 0.2545 [-- extra limap cfg overrides]
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import pycolmap

from pipeline.experiments.lines import structure as st


def heldout_model(model_dir: Path, out_dir: Path, mod: int) -> list[str]:
    """Deregister the bench's held-out frames: 10 fps ids with id % `mod` == 1 (S1's scorer uses
    ids = 1 mod 25 for LSD reprojection)."""
    recon = pycolmap.Reconstruction(str(model_dir))
    ims = sorted(recon.images.values(), key=lambda im: im.name)
    held = [im for im in ims if mod > 0 and int(Path(im.name).stem) % mod == 1]
    for im in held:
        recon.deregister_frame(im.frame_id)
    out_dir.mkdir(parents=True, exist_ok=True)
    recon.write(str(out_dir))
    return [im.name for im in held]


def export_raw(final_model: Path) -> list[st.Seg]:
    import limap.scene  # only in the LIMAP venv

    rec = limap.scene.HolisticReconstruction()
    rec.read(str(final_model))
    segs = []
    for line in rec.structure_recon.lines3D.values():
        views = {int(e.image_id) for e in line.track.elements}
        segs.append(st.Seg(np.array(line.start), np.array(line.end), views))
    return segs


_RANK = ["low", "medium", "high"]


def _edge_conf(s: st.Seg) -> str:
    """high: on a Manhattan axis with >= 6 views; medium: on-axis; low: off-axis."""
    return "low" if s.axis < 0 else ("high" if len(s.views) >= 6 else "medium")


def postprocess(
    segs: list[st.Seg],
    m_per_unit: float,
    min_views: int = 4,
    axis_only: bool = False,
    merge_m: float = 0.006,
    corner_m: float = 0.01,
    ext_m: float = 0.06,
) -> dict:
    """`axis_only`: keep only Manhattan-axis edges with >= 6 views and >= 10 cm (the "structural"
    subset: cabinet, counter and appliance outlines; drops most clutter). `merge_m`: collinear
    merge distance; `corner_m`: max line-line gap for a junction; `ext_m`: how far past a segment
    end a junction may lie (all metres, via `m_per_unit`)."""
    u = 1.0 / m_per_unit  # map units per metre
    raw_n = len(segs)
    segs = [
        st.Seg(s.p0.copy(), s.p1.copy(), set(s.views))
        for s in segs
        if len(s.views) >= min_views and s.length > 0.03 * u
    ]
    axes = st.manhattan_axes(segs)
    st.snap_to_axes(segs, axes, tol_deg=5.0)
    if axis_only:
        segs = [s for s in segs if s.axis >= 0]
    merged = st.merge_collinear(segs, dist_tol=merge_m * u, gap_tol=0.05 * u)
    merged = [s for s in merged if s.length > 0.05 * u]
    if axis_only:
        merged = [s for s in merged if len(s.views) >= 6 and s.length > 0.10 * u]
    corners = st.junctions(merged, dist_tol=corner_m * u, ext_tol=ext_m * u)
    st.extend_to_corners(merged, corners, ext_tol=ext_m * u)
    return {
        "schema": "airtools.structure/1",
        "frame": "cameras",
        "method": "limap",
        "params": {
            "m_per_unit": m_per_unit,
            "min_views": min_views,
            "axis_only": axis_only,
            "merge_m": merge_m,
            "corner_m": corner_m,
            "ext_m": ext_m,
        },
        "axes": axes.T.tolist(),
        "counts": {
            "raw": raw_n,
            "kept": len(segs),
            "edges": len(merged),
            "on_axis": sum(s.axis >= 0 for s in merged),
            "corners": len(corners),
            "corners3": sum(len(c["edges"]) >= 3 for c in corners),
        },
        "planes": [],
        "edges": [
            {
                "id": f"e{i}",
                "a": s.p0.tolist(),
                "b": s.p1.tolist(),
                "planes": [],
                "kind": "line",
                "src": ["limap"],
                "views": len(s.views),
                "confidence": _edge_conf(s),
                "axis": s.axis,
                "view_ids": sorted(s.views),
            }
            for i, s in enumerate(merged)
        ],
        "corners": [
            {
                "id": f"c{i}",
                "p": c["p"].tolist(),
                "edges": [f"e{k}" for k in c["edges"]],
                "planes": [],
                "kind": "2edge",
                "residual_m": c["residual"] * m_per_unit,
                "confidence": min((_edge_conf(merged[k]) for k in c["edges"]), key=_RANK.index),
            }
            for i, c in enumerate(corners)
        ],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sfm", type=Path, required=True, help="work/<site>/sfm (has dense/)")
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--limap-src", type=Path, required=True, help="for cfgs/")
    ap.add_argument("--config", default="cfgs/structure_triangulation/default_cpu.yaml")
    ap.add_argument("--m-per-unit", type=float, required=True)
    ap.add_argument("--holdout-mod", type=int, default=25)
    ap.add_argument(
        "--skip-limap", action="store_true", help="reuse out/raw_lines.json or out/limap"
    )
    a, extra = ap.parse_known_args()
    a.out.mkdir(parents=True, exist_ok=True)
    timings = {}

    model = a.out / "train_model"
    held = heldout_model(a.sfm / "dense" / "sparse", model, a.holdout_mod)
    t0 = time.monotonic()
    if not a.skip_limap:
        cmd = [
            sys.executable,
            "-m",
            "limap.cli.automatic_point_line_triangulation",
            "-c",
            a.config,
            "-m",
            str(model.resolve()),
            "-i",
            str((a.sfm / "dense" / "images").resolve()),
            "-o",
            str((a.out / "limap").resolve()),
            *extra,
        ]
        subprocess.run(cmd, check=True, cwd=a.limap_src)
    timings["limap_s"] = time.monotonic() - t0

    raw = a.out / "raw_lines.json"
    if a.skip_limap and raw.exists():  # re-post-process without the LIMAP venv
        segs = [
            st.Seg(np.array(r["p0"]), np.array(r["p1"]), set(r["views"]))
            for r in json.loads(raw.read_text())
        ]
    else:
        segs = export_raw(a.out / "limap" / "final_model")
        raw.write_text(
            json.dumps(
                [{"p0": s.p0.tolist(), "p1": s.p1.tolist(), "views": sorted(s.views)} for s in segs]
            )
        )
    for name, axis_only in (("structure.json", False), ("structure_axis.json", True)):
        t1 = time.monotonic()
        out = postprocess(segs, a.m_per_unit, axis_only=axis_only)
        timings["postprocess_s"] = time.monotonic() - t1
        out["cameras"] = str((a.sfm / "dense" / "sparse").resolve())
        out["heldout"] = held
        out["cost"] = {
            "runtime_s": sum(timings.values()),
            "stages_s": dict(timings),
            "host": socket.gethostname(),
            "cores": len(os.sched_getaffinity(0)),
        }
        (a.out / name).write_text(json.dumps(out, indent=1))
        print(name, json.dumps({"counts": out["counts"], "cost": out["cost"]}, indent=1))


if __name__ == "__main__":
    main()
