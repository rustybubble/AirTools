"""Regularize a scene mesh onto its structure layer (bench §1, "optional regularized mesh").

    python -m pipeline.experiments.structure.regularize <scene_in> <structure.json> <scene_out>

The QuestRoomScan / R6 rule, extended to creases and corners:
- a vertex within `--plane-tol` of a plane, with vertex-normal dot > `--normal-dot` and inside the
  plane's polygon (or within `--margin` of it), is projected onto the plane;
- a vertex within `--edge-r` of a crease segment moves onto the line;
- a vertex within `--corner-r` of a corner moves onto it.
Only the POSITION accessors of `mesh` and `collision` are rewritten in place (same GLB layout, so
UVs, indices and the texture stay byte-identical). The rest of the package is hard-linked.
"""

import json
import os
import shutil
import struct
from pathlib import Path

import numpy as np

from pipeline.experiments.structure.score import (
    Structure,
    closest_on_segments,
    dist_to_polygon_boundary,
    point_in_polygon,
)


def _read_glb(path: Path) -> tuple[dict, bytearray]:
    b = Path(path).read_bytes()
    jl = struct.unpack_from("<I", b, 12)[0]
    js = json.loads(b[20 : 20 + jl])
    bl = struct.unpack_from("<I", b, 20 + jl)[0]
    return js, bytearray(b[28 + jl : 28 + jl + bl])


def _write_glb(path: Path, js: dict, bin_: bytes) -> None:
    jb = json.dumps(js, separators=(",", ":")).encode()
    jb = jb.ljust((len(jb) + 3) // 4 * 4, b" ")
    out = b"glTF" + struct.pack("<II", 2, 12 + 8 + len(jb) + 8 + len(bin_))
    out += (
        struct.pack("<I", len(jb)) + b"JSON" + jb + struct.pack("<I", len(bin_)) + b"BIN\0" + bin_
    )
    Path(path).write_bytes(out)


def _accessor(js: dict, bin_: bytearray, i: int, dtype, width: int) -> tuple[np.ndarray, int]:
    acc = js["accessors"][i]
    bv = js["bufferViews"][acc["bufferView"]]
    assert bv.get("byteStride") in (None, width * np.dtype(dtype).itemsize), "strided buffer"
    off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    arr = np.frombuffer(bytes(bin_[off : off + acc["count"] * width * 4]), dtype)
    return arr.reshape(acc["count"], width).copy(), off


def vertex_normals(V: np.ndarray, F: np.ndarray) -> np.ndarray:
    fn = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])  # area-weighted
    vn = np.zeros_like(V)
    for k in range(3):
        np.add.at(vn, F[:, k], fn)
    return vn / np.maximum(np.linalg.norm(vn, axis=1, keepdims=True), 1e-18)


def snap_vertices(
    V: np.ndarray,
    VN: np.ndarray,
    st: Structure,
    plane_tol: float,
    normal_dot: float,
    margin: float,
    edge_r: float,
    corner_r: float,
) -> tuple[np.ndarray, dict]:
    out = V.copy()
    kind = np.zeros(len(V), np.int8)  # 0 none, 1 plane, 2 edge, 3 corner
    best = np.full(len(V), np.inf)
    v_axes = st.v
    for j in range(len(st.n)):
        sd = (V - st.origin[j]) @ st.n[j]
        cand = np.flatnonzero((np.abs(sd) < plane_tol) & (np.abs(VN @ st.n[j]) > normal_dot))
        if not len(cand):
            continue
        if st.poly[j] is not None:
            rel = V[cand] - st.origin[j]
            uv = np.stack([rel @ st.u[j], rel @ v_axes[j]], 1)
            ok = point_in_polygon(uv, st.poly[j]) | (
                dist_to_polygon_boundary(uv, st.poly[j]) < margin
            )
            cand = cand[ok]
        closer = np.abs(sd[cand]) < best[cand]
        cand = cand[closer]
        best[cand] = np.abs(sd[cand])
        out[cand] = V[cand] - sd[cand, None] * st.n[j]
        kind[cand] = 1
    if len(st.ea):
        for s in range(0, len(V), 20000):  # chunked (Q x E)
            q = V[s : s + 20000]
            d, p = closest_on_segments(q, st.ea, st.eb)
            j = d.argmin(1)
            hit = d[np.arange(len(q)), j] < edge_r
            idx = np.flatnonzero(hit) + s
            out[idx] = p[np.flatnonzero(hit), j[hit]]
            kind[idx] = 2
    if len(st.c):
        from scipy.spatial import cKDTree

        d, j = cKDTree(st.c).query(V, distance_upper_bound=corner_r)
        hit = np.isfinite(d)
        out[hit] = st.c[j[hit]]
        kind[hit] = 3
    disp = np.linalg.norm(out - V, axis=1)
    stats = {
        "moved_frac": float((kind > 0).mean()),
        "plane_frac": float((kind == 1).mean()),
        "edge_frac": float((kind == 2).mean()),
        "corner_frac": float((kind == 3).mean()),
        "disp_mm_p50_moved": float(np.median(disp[kind > 0]) * 1000) if (kind > 0).any() else 0.0,
        "disp_mm_max": float(disp.max() * 1000),
    }
    return out, stats


def regularize_glb(src: Path, dst: Path, st: Structure, a) -> dict:
    js, bin_ = _read_glb(src)
    prim = js["meshes"][0]["primitives"][0]
    assert len(js["meshes"]) == 1 and len(js["meshes"][0]["primitives"]) == 1
    assert not any(
        k in n for n in js["nodes"] for k in ("matrix", "translation", "rotation", "scale")
    )
    V, off = _accessor(js, bin_, prim["attributes"]["POSITION"], np.float32, 3)
    F, _ = _accessor(js, bin_, prim["indices"], np.uint32, 1)
    V64 = V.astype(np.float64)
    Vn, stats = snap_vertices(
        V64,
        vertex_normals(V64, F.reshape(-1, 3)),
        st,
        a.plane_tol,
        a.normal_dot,
        a.margin,
        a.edge_r,
        a.corner_r,
    )
    Vf = Vn.astype(np.float32)
    bin_[off : off + Vf.nbytes] = Vf.tobytes()
    acc = js["accessors"][prim["attributes"]["POSITION"]]
    acc["min"], acc["max"] = Vf.min(0).tolist(), Vf.max(0).tolist()
    _write_glb(dst, js, bytes(bin_))
    return stats


def main(argv=None) -> None:
    import argparse

    p = argparse.ArgumentParser(prog="python -m pipeline.experiments.structure.regularize")
    p.add_argument("scene_in")
    p.add_argument("structure")
    p.add_argument("scene_out")
    p.add_argument("--plane-tol", type=float, default=0.03)
    p.add_argument("--normal-dot", type=float, default=0.9)
    p.add_argument("--margin", type=float, default=0.02, help="polygon margin for plane snaps")
    p.add_argument("--edge-r", type=float, default=0.01)
    p.add_argument("--corner-r", type=float, default=0.01)
    a = p.parse_args(argv)
    src, dst = Path(a.scene_in), Path(a.scene_out)
    doc = json.loads(Path(a.structure).read_text())
    st = Structure(doc)
    if dst.exists():
        shutil.rmtree(dst)
    dst.mkdir(parents=True)
    scene = json.loads((src / "scene.json").read_text())
    targets = {scene["mesh"]["file"], scene["collision"]["file"]}
    for f in src.iterdir():
        if f.name in targets:
            continue
        if f.is_dir():
            shutil.copytree(f, dst / f.name, copy_function=os.link)
        else:
            os.link(f, dst / f.name)
    report = {"params": {k: v for k, v in vars(a).items()}, "structure_method": doc.get("method")}
    for t in sorted(targets):
        report[t] = regularize_glb(src / t, dst / t, st, a)
        print(t, report[t])
    (dst / "regularize.json").write_text(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
