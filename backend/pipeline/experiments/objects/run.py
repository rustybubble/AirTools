"""S4: in-plane rectangles (doors, drawers, appliance fronts) for snapping, RoomPlan style.

For every large vertical plane: fuse a seam-free orthophoto from the original frames
(`ortho.fuse_frames`), fit axis-aligned rectangles to its LSD line families (`rects`), keep the
ones the mesh agrees lie on the plane, lift the corners exactly onto the plane, and group repeats
(equal doors). Writes `objects[]`, `groups[]`, rect `corners[]` and `boundary` `edges[]` in the
structure schema of docs/research/structure/r6-industry.md §7.2, plus overlay figures, and a held-out-frame
reprojection check. Score the JSON with `python -m pipeline.experiments.structure score`.

    python -m pipeline.experiments.objects.run --planes structure.json --scene <pkg> \
        --frames <run>/frames --sparse-raw <run>/sfm/sparse/0 --out work/s4

Planes: a structure.json in the scene frame, or in COLMAP units with "frame": "cameras" (S3's
PxwPlanar output), in which case `--colmap` (that model) maps it to the scene frame through the
camera centres.
"""

from __future__ import annotations

import argparse
import json
import multiprocessing
import time
from pathlib import Path

import cv2
import numpy as np

from pipeline.experiments.objects import ortho as O
from pipeline.experiments.objects import rects as Rx

UP = np.array([0.0, 1.0, 0.0])


# --- planes -------------------------------------------------------------------------------------


def colmap_to_scene(colmap_dir: Path, cams: list[dict]) -> tuple[float, np.ndarray, np.ndarray]:
    """Similarity (s, R, t) with scene = s R x + t, from the shared camera centres (Umeyama)."""
    import pycolmap

    rec = pycolmap.Reconstruction(str(colmap_dir))
    c = {Path(im.name).stem: im.projection_center() for im in rec.images.values()}
    ids = [k["id"] for k in cams if k["id"] in c]
    a = np.array([c[i] for i in ids])
    b = np.array([k["position"] for k in cams if k["id"] in c])
    ma, mb = a.mean(0), b.mean(0)
    u, s, vt = np.linalg.svd((b - mb).T @ (a - ma))
    d = np.diag([1, 1, np.sign(np.linalg.det(u @ vt))])
    r = u @ d @ vt
    sc = float((s * np.diag(d)).sum() / ((a - ma) ** 2).sum())
    return sc, r, mb - sc * r @ ma


def load_planes(path: Path, cams: list[dict], colmap: Path | None) -> tuple[list[dict], np.ndarray]:
    """Planes as {id, n, poly (N,3) scene xyz, area, rms}, and the file's crease edges as (E, 2, 3),
    both in the scene frame."""
    data = json.loads(Path(path).read_text())
    s, r, t = 1.0, np.eye(3), np.zeros(3)
    if data.get("frame") == "cameras":
        s, r, t = colmap_to_scene(colmap, cams)
    out = []
    for p in data["planes"]:
        n = r @ np.asarray(p["normal"], float)
        poly = np.asarray(p["polygon"], float)
        if poly.shape[1] == 2:  # (u, v) polygon in the plane's own frame
            o, u = np.asarray(p["origin"], float), np.asarray(p["u"], float)
            poly = o + poly[:, :1] * u + poly[:, 1:] * np.cross(p["normal"], u)
        poly = s * poly @ r.T + t
        out.append(
            {
                "id": p["id"],
                "n": n / np.linalg.norm(n),
                "poly": poly,
                "area": p.get("area_m2"),
                "rms": p.get("rms_m"),
                "label": p.get("label"),
            }
        )
    ab = [(e["a"], e["b"]) for e in data.get("edges", []) if e.get("kind", "crease") == "crease"]
    return out, s * np.asarray(ab, float).reshape(-1, 2, 3) @ r.T + t


def face_planes(planes: list[dict], cams: list[dict], max_up: float, min_extent: float):
    """Vertical planes whose polygon spans at least `min_extent` (scene m) both ways, with the
    normal flipped toward the cameras."""
    centres = np.array([c["position"] for c in cams])
    out = []
    for p in planes:
        if abs(p["n"] @ UP) > max_up:
            continue
        o, n, u, v = O.plane_frame(p["n"], p["poly"].mean(0), UP)
        if np.median((centres - o) @ n) < 0:
            o, n, u, v = O.plane_frame(-p["n"], o, UP)
        o = o - n * ((o - p["poly"]) @ n).mean()  # exactly on the plane
        uv = np.stack([(p["poly"] - o) @ u, (p["poly"] - o) @ v], -1)
        ext = uv.max(0) - uv.min(0)
        if ext.min() >= min_extent:
            out.append({**p, "o": o, "n": n, "u": u, "v": v, "uv": uv})
    return out


# --- per plane ----------------------------------------------------------------------------------


def label_of(
    w: float, h: float, exterior: bool = False, above_floor: float = 0.0, depth: float = -1.0
) -> str | None:
    """Heuristic label from the size (scene metres; the kitchen scene is ~0.72x true scale).
    `exterior` (a building facade): only window and door sizes count, a door being door-sized with
    its bottom within 0.5 m of the plane's lowest point (`above_floor`), and set back into the
    facade (`depth`, the mesh's median height over the plane inside the rect, < 0): on Zabel the
    window panes sit 10-18 cm back, the brick patches LSD also frames 5-17 cm proud. Anything else
    gets None and is dropped."""
    if exterior:
        if min(w, h) < 0.4 or max(w, h) > 4.0 or not 0.25 <= w / h <= 4.0 or depth >= 0:
            return None
        return "door" if 0.7 <= w <= 2.5 and 1.9 <= h <= 3.2 and above_floor < 0.5 else "window"
    if min(w, h) < 0.06:
        return "panel"
    if h < 0.5 * w:
        return "drawer"
    if w > 0.35 and h > 0.35:
        return "appliance"
    return "cabinet_door"


def axis_len(gray: np.ndarray) -> float:
    """Total px length of axis-aligned LSD segments >= 20 px: how much rectangle evidence an
    orthophoto holds (Laplacian variance rewards the mesh texture's seam speckle instead)."""
    return float(
        sum(
            np.hypot(*(s[:, 2:] - s[:, :2]).T).sum() for s in Rx.families(Rx.lsd_segments(gray, 20))
        )
    )


def crosses_crease(grid: O.Grid, uv: list[float], creases: np.ndarray, a) -> bool:
    """True when a plane-plane crease within `crease_dist` of this plane runs through the rect's
    interior (inset by `crease_inset`) for over a third of its smaller side: the rect then spans
    two surfaces (a wall patch framed by a counter), not one panel."""
    if not len(creases):
        return False
    near = (np.abs((creases - grid.origin) @ grid.n) < a.crease_dist).all(1)
    u0, v0, u1, v1 = uv
    t = np.linspace(0, 1, 50)[:, None]
    for e in creases[near]:
        q = grid.to_uv(e[0] + t * (e[1] - e[0]))
        inside = (q[:, 0] > u0 + a.crease_inset) & (q[:, 0] < u1 - a.crease_inset)
        inside &= (q[:, 1] > v0 + a.crease_inset) & (q[:, 1] < v1 - a.crease_inset)
        if inside.mean() * np.linalg.norm(e[1] - e[0]) > min(u1 - u0, v1 - v0) / 3:
            return True
    return False


def detect(
    grid: O.Grid, gray: np.ndarray, height: np.ndarray, a, creases: np.ndarray | None = None
) -> list[dict]:
    """Rectangles on one plane: LSD families -> lines -> scored rects -> mesh support and
    crease gates."""
    h, w = gray.shape
    segs = Rx.lsd_segments(cv2.createCLAHE(2.0, (8, 8)).apply(gray))
    hs, vs = Rx.families(segs)
    vl = Rx.edge_cover(gray, Rx.cluster_lines(vs, True, h), True)
    hl = Rx.edge_cover(gray, Rx.cluster_lines(hs, False, w), False)
    px = lambda m: m / grid.res
    atomic, outer = Rx.fit_rects(
        vl, hl, px(a.min_side), px(a.max_side), px(a.min_side), px(a.max_side)
    )
    out = []
    for r in [{**r, "kind": "atomic"} for r in atomic] + [{**r, "kind": "outer"} for r in outer]:
        c0, c1, r0, r1 = (round(r[k]) for k in ("c0", "c1", "r0", "r1"))
        hh = height[r0 : r1 + 1, c0 : c1 + 1]
        sup = float((np.abs(np.nan_to_num(hh, nan=1.0)) < a.mesh_tol).mean())
        if sup < a.min_mesh_support:
            continue
        (u0, v1), (u1, v0) = grid.px_to_uv(np.array([[r["c0"], r["r0"]], [r["c1"], r["r1"]]]))
        if creases is not None and crosses_crease(grid, [u0, v0, u1, v1], creases, a):
            continue
        depth = float(np.nanmedian(hh)) if np.isfinite(hh).any() else 0.0  # + = proud
        out.append({**r, "uv": [u0, v0, u1, v1], "mesh_support": sup, "depth": depth})
    return out


def ransac_plane(
    q: np.ndarray, near_n: np.ndarray, tol: float, iters: int = 300, max_deg: float = 15.0
):
    """3-point RANSAC for a plane within `max_deg` of `near_n`, then a TLS refit on the inliers.
    Returns (centroid, unit normal facing near_n, inlier mask) or None."""
    if len(q) < 3:
        return None
    rng = np.random.default_rng(0)
    best = np.zeros(len(q), bool)
    for _ in range(iters):
        s = q[rng.choice(len(q), 3, replace=False)]
        n = np.cross(s[1] - s[0], s[2] - s[0])
        if np.linalg.norm(n) < 1e-12 or abs(n @ near_n) < np.cos(
            np.radians(max_deg)
        ) * np.linalg.norm(n):
            continue
        inl = np.abs((q - s[0]) @ (n / np.linalg.norm(n))) < tol
        if inl.sum() > best.sum():
            best = inl
    if best.sum() < 3:
        return None
    for _ in range(2):
        c = q[best].mean(0)
        n = np.linalg.eigh((q[best] - c).T @ (q[best] - c))[1][:, 0]
        best = np.abs((q - c) @ n) < tol
    n = n if n @ near_n > 0 else -n
    return c, n, best


def relief_planes(p: dict, g: O.Grid, height: np.ndarray, verts: np.ndarray, a) -> list[dict]:
    """Fronts that stand proud of (or sit back from) plane `p` by `relief_min`..`relief_max`,
    such as a dishwasher 1-3 cm in front of the cabinet run: connected regions of the mesh height
    map, each refit as its own plane (TLS on the mesh vertices there). The parent plane's
    orthophoto blurs them by parallax, so they get their own."""
    h, w = height.shape
    px = np.round(g.uv_to_px(g.to_uv(verts))).astype(int)
    d = (verts - g.origin) @ g.n
    inb = (px[:, 0] >= 0) & (px[:, 0] < w) & (px[:, 1] >= 0) & (px[:, 1] < h)
    hgt = np.nan_to_num(height, nan=0.0)
    out = []
    for sign in (1, -1):
        m = ((sign * hgt > a.relief_min) & (sign * hgt < a.relief_max)).astype(np.uint8)
        m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((9, 9), np.uint8))
        n_lab, lab, st, _ = cv2.connectedComponentsWithStats(m)
        for k in range(1, n_lab):
            _, _, bw, bh, area = st[k]
            if min(bw, bh) * g.res < a.relief_size or area < 0.3 * bw * bh:
                continue
            sel = np.zeros(len(verts), bool)
            sel[inb] = lab[px[inb, 1], px[inb, 0]] == k
            sel &= (sign * d > a.relief_min / 2) & (sign * d < a.relief_max)
            idx = np.flatnonzero(sel)
            # a component can hold several fronts (a dishwasher yawed 2.5 deg next to an oven):
            # peel planes off it with RANSAC
            for _ in range(2):
                fit = ransac_plane(verts[idx], g.n, a.relief_tol)
                if fit is None or fit[2].sum() < max(300, 0.25 * sel.sum()):
                    break
                c, n, inl = fit
                lo, hi = np.percentile(px[idx[inl]], [2, 98], axis=0)
                occ = np.zeros((h, w), np.uint8)  # a panel fills its box; clutter (bottles) doesn't
                occ[px[idx[inl], 1], px[idx[inl], 0]] = 1
                occ = cv2.dilate(occ, np.ones((7, 7), np.uint8))
                (x0, y0), (x1, y1) = np.floor(lo).astype(int), np.ceil(hi).astype(int)
                fill = occ[y0 : y1 + 1, x0 : x1 + 1].mean()
                if (hi - lo).min() * g.res >= a.relief_size and fill >= a.relief_fill:
                    o, n, u, v = O.plane_frame(n, c, UP)
                    box = g.lift(g.px_to_uv(np.array([lo, [hi[0], lo[1]], hi, [lo[0], hi[1]]])))
                    box = box - np.outer((box - o) @ n, n)
                    q = verts[idx[inl]]
                    out.append(
                        {
                            "id": f"{p['id']}r{len(out)}",
                            "o": o,
                            "n": n,
                            "u": u,
                            "v": v,
                            "uv": np.stack([(box - o) @ u, (box - o) @ v], -1),
                            "parent": p["id"],
                            "rms": float(np.sqrt(np.mean(((q - o) @ n) ** 2))),
                            "fill": float(fill),
                        }
                    )
                idx = idx[~inl]
    return out


# --- evaluation ---------------------------------------------------------------------------------


def reproj_frame(job: tuple) -> tuple:
    """One held-out frame (undistorted to the scene pinhole): LSD, projected boundary edges,
    matched distances, and an optional overlay figure."""
    from pipeline.experiments.lines.evaluate import lsd, project_segment, seg_distance

    cid, edges, fig = job
    c, raw = next(c for c in _CTX["cams"] if c["id"] == cid), _CTX["raw"]
    k = np.array([[c["fx"], 0, c["cx"]], [0, c["fy"], c["cy"]], [0, 0, 1.0]])
    pose = np.hstack([np.array(c["R"]), np.array(c["t"])[:, None]])
    yy, xx = np.mgrid[: c["h"], : c["w"]].astype(np.float64)
    xn = np.stack([(xx - c["cx"]) / c["fx"], (yy - c["cy"]) / c["fy"]], -1)
    m = O.distort_simple_radial(xn, raw["f"], raw["cx"], raw["cy"], raw["k"]).astype(np.float32)
    img = cv2.remap(_load(cid), m[..., 0], m[..., 1], cv2.INTER_LINEAR)
    det = lsd(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), 20)
    proj = []
    for e in edges:
        s = project_segment(np.array(e["a"]), np.array(e["b"]), pose, k, (c["w"], c["h"]))
        if s is not None and np.linalg.norm(s[1] - s[0]) >= 20:
            proj.append(s)
    dists = [min((seg_distance(s, q) for q in det), default=np.inf) for s in proj]
    name = None
    if fig and proj:
        v = img.copy()
        for a_, b_ in det:
            cv2.line(v, tuple(map(int, a_)), tuple(map(int, b_)), (200, 200, 200), 1)
        for a_, b_ in proj:
            cv2.line(v, tuple(map(int, a_)), tuple(map(int, b_)), (0, 0, 255), 2)
        name = f"reproj_{cid}.jpg"
        cv2.imwrite(
            str(_CTX["out"] / name),
            cv2.resize(v, None, fx=0.5, fy=0.5),
            [cv2.IMWRITE_JPEG_QUALITY, 80],
        )
    return [d for d in dists if d <= 10], len(proj), name


def reproject(edges: list[dict], held: list[str], pmap=map, n_figs: int = 4) -> dict:
    """Project boundary edges into held-out frames and match them to LSD lines within 10 px,
    like S2's evaluate.py."""
    every = max(1, len(held) // n_figs)
    jobs = [(cid, edges, i % every == 0 and i // every < n_figs) for i, cid in enumerate(held)]
    res = list(pmap(reproj_frame, jobs))
    dists = [d for r in res for d in r[0]]
    vis = sum(r[1] for r in res)
    return {
        "frames": len(held),
        "visible_edges": vis,
        "matched_frac": len(dists) / max(vis, 1),
        "median_px": float(np.median(dists)) if dists else None,
        "p90_px": float(np.percentile(dists, 90)) if dists else None,
        "figures": [r[2] for r in res if r[2]],
    }


# --- main ---------------------------------------------------------------------------------------


_CTX: dict = {}  # run() fills it before forking the plane workers


def _load(cid: str) -> np.ndarray:
    c = _CTX["cache"]
    if cid not in c:
        c[cid] = cv2.imread(str(Path(_CTX["a"].frames) / f"{cid}.jpg"))
    return c[cid]


def grid_res(uv: np.ndarray, margin: float, res: float, max_px: float) -> float:
    """The orthophoto m/px: `res`, coarsened so one plane's raster stays under `max_px` pixels.
    Kitchen planes are under 0.3 MP at 2 mm; a 60 m^2 building facade was 15 MP, and 16 workers
    fusing rasters that size ran hfbox out of memory (S4 hung for 18 min past its timeout).
    Capped at 1 MP with 8 workers, the Zabel facades fuse in 94 s using 7 GB."""
    w, h = np.ptp(uv, 0) + 2 * margin
    return max(res, float(np.sqrt(w * h / max_px)))


def plane_job(p: dict) -> dict:
    """Orthophoto (or its cache), relief sub-planes, rectangles and figures for one plane."""
    a, out, mesh = _CTX["a"], _CTX["out"], _CTX["mesh"]
    g = O.Grid(
        p["o"],
        p["u"],
        p["v"],
        p["n"],
        *(p["uv"].min(0) - a.margin),
        *(p["uv"].max(0) + a.margin),
        grid_res(p["uv"], a.margin, a.res, a.max_px),
    )
    tp = time.monotonic()
    cached = out / f"ortho_{p['id']}.npz"
    if a.reuse and cached.exists():  # iterate on detection without re-fusing
        z = np.load(cached)
        gray, height, mimg, used = z["gray"], z["height"], None, []
    else:
        mimg, height = O.render_mesh(
            g, mesh.vertices, mesh.faces, mesh.visual.uv, _CTX["tex"], band=a.relief_max
        )
        fused, gray, used = O.fuse_frames(
            g, _CTX["cams"], _load, _CTX["raw"], exclude=set(_CTX["held"]), max_frames=a.max_frames
        )
        np.savez_compressed(
            cached,
            gray=gray,
            height=height,
            o=g.origin,
            u=g.u,
            v=g.v,
            n=g.n,
            box=[g.u0, g.v0, g.u1, g.v1, g.res],
        )
        cv2.imwrite(str(out / f"ortho_{p['id']}_color.jpg"), fused)
        cv2.imwrite(str(out / f"ortho_{p['id']}_mesh.jpg"), mimg)
    relief = [] if "parent" in p else relief_planes(p, g, height, mesh.vertices, a)
    found = detect(g, gray, height, a, _CTX["creases"])
    vis = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
    for r in found:
        cv2.rectangle(
            vis,
            (round(r["c0"]), round(r["r0"])),
            (round(r["c1"]), round(r["r1"])),
            (0, 0, 255) if r["kind"] == "atomic" else (255, 0, 255),
            2,
        )
    cv2.imwrite(str(out / f"ortho_{p['id']}.jpg"), vis, [cv2.IMWRITE_JPEG_QUALITY, 85])
    mgray = None if mimg is None else cv2.cvtColor(mimg, cv2.COLOR_BGR2GRAY)
    stats = {
        "plane": p["id"],
        "shape": list(g.shape),
        "frames": len(used),
        "rects": len(found),
        "lsd_axis_px_fused": axis_len(gray),
        "rects_mesh_texture": None if mgray is None else len(detect(g, mgray, height, a)),
        "lsd_axis_px_mesh": None if mgray is None else axis_len(mgray),
        "time_s": round(time.monotonic() - tp, 1),
    }
    objs = [
        {
            "plane": p["id"],
            "grid": g,
            "uv": r["uv"],
            "above_floor": r["uv"][1] - float(p["uv"][:, 1].min()),
            "depth": r["depth"],
            "w": r["uv"][2] - r["uv"][0],
            "h": r["uv"][3] - r["uv"][1],
            "score": r["score"],
            "mesh_support": r["mesh_support"],
            "kind": r["kind"],
        }
        for r in found
    ]
    return {"stats": stats, "objs": objs, "relief": relief}


def _init_ctx(a) -> None:
    """Load everything the plane jobs share into _CTX (run in the parent and in each worker)."""
    import pycolmap
    import trimesh

    scene = Path(a.scene)
    cams = json.loads(Path(a.cameras or scene / "cameras.r1.json").read_text())
    rc = next(iter(pycolmap.Reconstruction(str(a.sparse_raw)).cameras.values()))
    mesh = next(iter(trimesh.load(scene / a.mesh).geometry.values()))
    tex = np.array(mesh.visual.material.baseColorTexture.convert("RGB"))[..., ::-1].copy()
    planes, creases = load_planes(Path(a.planes), cams, Path(a.colmap) if a.colmap else None)
    ids = sorted(c["id"] for c in cams)
    _CTX.update(
        a=a,
        cams=cams,
        raw=dict(zip(("f", "cx", "cy", "k"), rc.params), w=rc.width, h=rc.height),
        mesh=mesh,
        tex=tex,
        planes=planes,
        creases=creases,
        out=Path(a.out),
        cache={},
        held=ids[a.hold_offset :: a.hold_every] if a.hold_every else [],  # never used for fusion
    )
    if a.workers > 1:
        cv2.setNumThreads(1)


def run(a) -> dict:
    t0 = time.monotonic()
    Path(a.out).mkdir(parents=True, exist_ok=True)
    _init_ctx(a)
    cams, held, out = _CTX["cams"], _CTX["held"], _CTX["out"]
    scene = Path(a.scene)
    faces = face_planes(_CTX["planes"], cams, a.max_up, a.min_extent)
    # planes are independent; relief sub-planes need their parent's height map, so they go
    # second. Spawned workers load their own context (forking after cv2 has started its thread
    # pool deadlocks).
    if a.workers > 1:
        pool = multiprocessing.get_context("spawn").Pool(a.workers, _init_ctx, (a,))
        pmap = lambda f, xs: pool.map(f, xs, chunksize=1)
    else:
        pool, pmap = None, map
    faces.sort(key=lambda p: -np.prod(np.ptp(p["uv"], 0)))  # biggest first balances the pool
    done = list(pmap(plane_job, faces))
    relief = [r for d in done for r in d["relief"]]
    done += list(pmap(plane_job, relief))
    todo = faces + relief
    per_plane = [d["stats"] for d in done]
    objs = [o for d in done for o in d["objs"]]
    for st in per_plane:
        print(st, flush=True)
    t_planes = time.monotonic() - t0

    # the same rectangle seen on two nearly coincident planes: keep the better-supported one
    objs.sort(key=lambda o: -(o["score"] * o["mesh_support"]))
    kept = []
    for o in objs:
        c = o["grid"].lift(np.array([(o["uv"][0] + o["uv"][2]) / 2, (o["uv"][1] + o["uv"][3]) / 2]))
        if all(np.linalg.norm(c - k["c"]) > 0.03 or abs(o["w"] / k["w"] - 1) > 0.1 for k in kept):
            kept.append({**o, "c": c})
    for o in kept:
        o["label"] = label_of(o["w"], o["h"], a.exterior, o["above_floor"], o["depth"])
    kept = [o for o in kept if o["label"]]
    kept.sort(key=lambda o: (o["plane"], o["uv"][0], o["uv"][1]))

    idx = [i for i, o in enumerate(kept) if o["kind"] == "atomic"]
    reps = [[idx[m] for m in g] for g in Rx.group_repeats([kept[i] for i in idx], a.repeat_tol)]
    raw_wh = [(o["w"], o["h"]) for o in kept]
    if a.regularize:
        for mem in reps:
            Rx.regularize(kept, mem, a.row_tol, a.regularize)

    s = {"objects": [], "groups": [], "corners": [], "edges": []}
    for i, o in enumerate(kept):
        u0, v0, u1, v1 = o["uv"]
        c3 = o["grid"].lift(np.array([[u0, v0], [u1, v0], [u1, v1], [u0, v1]]))
        oid = f"o{i}"
        conf = "high" if o["score"] > 0.97 and o["mesh_support"] > 0.8 else "medium"
        s["objects"].append(
            {
                "id": oid,
                "label": o["label"],
                "plane": o["plane"],
                "rect": [round(x, 5) for x in o["uv"]],
                "rect_uv": [[round(u0, 5), round(v0, 5)], [round(u1, 5), round(v1, 5)]],
                "corners3d": c3.round(5).tolist(),
                "w_m": round(o["w"], 5),
                "h_m": round(o["h"], 5),
                "confidence": conf,
                "score": round(o["score"], 3),
                "mesh_support": round(o["mesh_support"], 3),
                "rect_kind": o["kind"],
                "group": None,
            }
        )
        for j, p3 in enumerate(c3):
            s["corners"].append(
                {
                    "id": f"{oid}c{j}",
                    "p": p3.round(5).tolist(),
                    "kind": "rect",
                    "planes": [o["plane"]],
                    "object": oid,
                    "confidence": conf,
                }
            )
        for j in range(4):
            s["edges"].append(
                {
                    "id": f"{oid}e{j}",
                    "a": c3[j].round(5).tolist(),
                    "b": c3[(j + 1) % 4].round(5).tolist(),
                    "kind": "boundary",
                    "planes": [o["plane"]],
                    "object": oid,
                    "src": ["s4_rect"],
                    "confidence": conf,
                }
            )
    for gi, mem in enumerate(reps):
        ws = np.array([raw_wh[m][0] for m in mem])  # as measured, before any regularization
        hs = np.array([raw_wh[m][1] for m in mem])
        cs = sorted(kept[m]["c"] @ kept[m]["grid"].u for m in mem)
        gid = f"g{gi}"
        for m in mem:
            s["objects"][m]["group"] = gid
        s["groups"].append(
            {
                "id": gid,
                "kind": "repeat",
                "members": [f"o{m}" for m in mem],
                "width_m": float(np.mean(ws)),
                "height_m": float(np.mean(hs)),
                "width_spread_mm": float((ws.max() - ws.min()) * 1e3),
                "width_std_mm": float(ws.std() * 1e3),
                "height_spread_mm": float((hs.max() - hs.min()) * 1e3),
                "pitch_m": float(np.median(np.diff(cs))) if len(cs) > 1 else None,
                "regularized": a.regularize,
            }
        )
    planes_out = [
        {
            "id": p["id"],
            "origin": p["o"].round(5).tolist(),
            "normal": p["n"].round(6).tolist(),
            "u": p["u"].round(6).tolist(),
            "offset": round(float(-p["o"] @ p["n"]), 5),
            "polygon": p["uv"].round(4).tolist(),
            "parent": p.get("parent"),
            "source": "s4-relief" if "parent" in p else str(a.planes),
        }
        for p in todo
    ]
    res = {
        "schema": "airtools.structure/1",
        "frame": "scene",
        "method": "s4-ortho-rects",
        "params": {
            "scene": str(scene),
            "mesh": a.mesh,
            "planes": str(a.planes),
            "res_m": a.res,
            "min_side_m": a.min_side,
            "min_mesh_support": a.min_mesh_support,
        },
        "planes": planes_out,
        **s,
    }
    t_out = time.monotonic() - t0  # production time: the layer is complete here
    res["stats"] = {"per_plane": per_plane, "held_out": held, "planes_s": t_planes}
    res["stats"]["reproj"] = reproject(s["edges"], held, pmap) if held else None  # eval only
    if pool:
        pool.close()
    res["stats"]["runtime_s"] = t_out
    res["stats"]["eval_s"] = time.monotonic() - t0 - t_out
    res["cost"] = {
        "runtime_s": round(t_out, 1),
        "eval_s": round(res["stats"]["eval_s"], 1),
        "host": "laptop",
        "workers": a.workers,
    }
    (out / "structure.s4.json").write_text(json.dumps(res, indent=1))
    return res


def main() -> None:
    ap = argparse.ArgumentParser(prog="python -m pipeline.experiments.objects.run")
    ap.add_argument("--planes", required=True, help="structure.json with planes")
    ap.add_argument("--colmap", help="COLMAP model the planes are in (frame == 'cameras')")
    ap.add_argument("--scene", required=True, help="scene package dir (mesh + cameras)")
    ap.add_argument("--cameras", help="cameras json (default <scene>/cameras.r1.json)")
    ap.add_argument("--mesh", default="mesh.r1.glb")
    ap.add_argument("--frames", required=True, help="the run's raw frames/ (<id>.jpg)")
    ap.add_argument("--sparse-raw", required=True, help="SfM model with the lens distortion")
    ap.add_argument("--out", required=True)
    ap.add_argument("--res", type=float, default=0.002, help="orthophoto m/px (scene units)")
    ap.add_argument("--max-px", type=float, default=1e6, help="orthophoto pixel cap per plane")
    ap.add_argument("--margin", type=float, default=0.05)
    ap.add_argument("--max-up", type=float, default=0.15, help="|n.y| for a vertical plane")
    ap.add_argument("--min-extent", type=float, default=0.15)
    ap.add_argument("--exterior", action="store_true", help="window/door size priors (label_of)")
    ap.add_argument("--min-side", type=float, default=0.04)
    ap.add_argument("--crease-dist", type=float, default=0.02)
    ap.add_argument("--crease-inset", type=float, default=0.01)
    ap.add_argument("--max-side", type=float, default=1.2)
    ap.add_argument("--mesh-tol", type=float, default=0.02)
    ap.add_argument("--min-mesh-support", type=float, default=0.75)
    ap.add_argument("--max-frames", type=int, default=80)
    ap.add_argument(
        "--hold-every", type=int, default=4, help="0: fuse all frames, skip the reprojection check"
    )
    ap.add_argument("--hold-offset", type=int, default=2)
    ap.add_argument("--workers", type=int, default=4, help="planes fused in parallel")
    ap.add_argument("--reuse", action="store_true", help="reuse ortho_<plane>.npz in --out")
    ap.add_argument("--relief-min", type=float, default=0.008)
    ap.add_argument("--relief-max", type=float, default=0.06)
    ap.add_argument("--relief-tol", type=float, default=0.005)
    ap.add_argument("--relief-fill", type=float, default=0.6)
    ap.add_argument("--relief-size", type=float, default=0.15)
    ap.add_argument(
        "--regularize",
        choices=["rows", "chain", "median"],
        default=None,
        help="snap repeat groups (rects.regularize)",
    )
    ap.add_argument("--row-tol", type=float, default=0.015)
    ap.add_argument("--repeat-tol", type=float, default=0.06)
    res = run(ap.parse_args())
    st = res["stats"]
    print(
        json.dumps({k: v for k, v in st.items() if k not in ("held_out",)}, indent=1, default=str)[
            :6000
        ]
    )
    for g in res["groups"]:
        print(g)


if __name__ == "__main__":
    main()
