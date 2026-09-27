"""Reconstruction bench harness for kitchen-0095 (docs/research/p1-recon-bench.md §2).

Scores any candidate reconstruction -- a textured glb plus cameras with video timestamps -- the
same way, against one fixed *reference*:

- `reference`: a dedicated pycolmap SfM over every 3rd frame of the 10 fps grid (~3.3 fps, ~290
  frames), which by construction contains every evaluation frame (every 15th, offset 2, so none is
  an SfM frame of a `--fps 1/2/3` pipeline run). It is made metric and +Y-up with the pipeline's
  own calibration (up from the cameras, caption-altitude scale), the evaluation frames are
  extracted at full resolution through the same `fps=10` filter and undistorted to the reference
  pinhole, and a reference dense cloud (an OpenMVS `scene_dense.ply` from any strong run) is
  brought into the reference frame by a Sim(3) fit on timestamp-matched camera centres.
- `score`: Sim(3)-aligns the candidate to the reference on its timestamp-matched camera centres
  (Umeyama with one outlier-trimming pass), renders it at the evaluation poses (`preview.render`)
  and reports speed, view/registration coverage, PSNR/SSIM (all and held-out), Chamfer and
  F-score at 2/5 cm, a sharpness ratio, and the Quest budget. Writes `<out>/<name>.json` and
  appends a row to `<out>/results.md`.

Known bias: geometry is scored against an OpenMVS cloud, so methods that resemble OpenMVS (same
depth-map fusion, same failure modes on dark/untextured surfaces) are favoured; the photometric
metrics at held-out frames are the method-neutral check.
"""

import argparse
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.calibrate import interpolate_positions_at_times, similarity_matrix, umeyama
from pipeline.package import Camera

VIDEO_FPS = 30000 / 1001
QUEST_TRIS, QUEST_TEX, QUEST_MB = 200_000, 4096, 20.0  # QUEST_MB: our own mesh-download cap


# --- pure math (unit-tested) -----------------------------------------------------------------


def id_times(ids: list[str], times: dict | None, id_fps: float | None) -> np.ndarray:
    """Video timestamp per camera id: an explicit `{id: t_s}` map (preview/other methods) or the
    pipeline's `%04d` numbering at a fixed fps (`run._frame_timestamp`)."""
    if times is not None:
        return np.array([float(times[i]) for i in ids])
    return np.array([(int(i) - 1) / id_fps for i in ids])


def robust_sim3(
    src: np.ndarray, dst: np.ndarray, trim: float = 3.0, floor: float = 0.02
) -> tuple[float, np.ndarray, np.ndarray, float, np.ndarray]:
    """Umeyama Sim(3) `dst ~= s R src + t`, refit once without points whose residual exceeds
    `max(trim * median, floor)` (a few mis-registered cameras shouldn't drag the whole fit).
    Returns `(s, R, t, rms_residual_of_inliers, inlier_mask)`."""
    s, R, t, _ = umeyama(src, dst)
    res = np.linalg.norm(dst - (s * (R @ src.T).T + t), axis=1)
    keep = res <= max(trim * float(np.median(res)), floor)
    if keep.sum() >= 3 and not keep.all():
        s, R, t, _ = umeyama(src[keep], dst[keep])
    res = np.linalg.norm(dst - (s * (R @ src.T).T + t), axis=1)
    return s, R, t, float(np.sqrt(np.mean(res[keep] ** 2))), keep


def nn_dist(points: np.ndarray, queries: np.ndarray) -> np.ndarray:
    """Exact distance from each query to its nearest point (scipy kd-tree, all cores)."""
    from scipy.spatial import cKDTree

    return cKDTree(points).query(queries, k=1, workers=-1)[0]


def geometry_metrics(cand: np.ndarray, ref: np.ndarray, taus=(0.02, 0.05)) -> dict:
    """Accuracy (cand->ref), completeness (ref->cand), Chamfer (their mean) and F-score per tau.
    Candidate points outside the reference's bounding box (+5 cm) are dropped first: the reference
    can't judge geometry it never saw, so they'd count as errors otherwise."""
    lo, hi = ref.min(0) - 0.05, ref.max(0) + 0.05
    cand = cand[np.all((cand >= lo) & (cand <= hi), axis=1)]
    if len(cand) == 0:
        return {"n_cand": 0}
    acc, comp = nn_dist(ref, cand), nn_dist(cand, ref)
    out = {
        "n_cand": len(cand),
        "n_ref": len(ref),
        "accuracy_m": float(acc.mean()),
        "completeness_m": float(comp.mean()),
        "chamfer_m": float((acc.mean() + comp.mean()) / 2),
    }
    for tau in taus:
        p, r = float((acc < tau).mean()), float((comp < tau).mean())
        cm = round(tau * 100)
        out[f"precision_{cm}cm"], out[f"recall_{cm}cm"] = p, r
        out[f"f_{cm}cm"] = 2 * p * r / (p + r) if p + r > 0 else 0.0
    return out


def laplacian(gray: np.ndarray) -> np.ndarray:
    """4-neighbour Laplacian of the interior pixels."""
    return (
        -4 * gray[1:-1, 1:-1] + gray[:-2, 1:-1] + gray[2:, 1:-1] + gray[1:-1, :-2] + gray[1:-1, 2:]
    )


def laplacian_var(gray: np.ndarray, mask: np.ndarray) -> float:
    """Variance of the Laplacian over `mask` (interior pixels only)."""
    m = mask[1:-1, 1:-1]
    return float(laplacian(gray)[m].var()) if m.sum() > 100 else float("nan")


def detail_scores(render: np.ndarray, photo: np.ndarray, covered: np.ndarray) -> tuple:
    """(sharpness ratio, Laplacian correlation) of render vs photo on covered pixels, both at half
    resolution (2x2 mean): the splat renderer's nearest-texel sampling adds pixel-level aliasing
    that would otherwise read as "sharper than the photo". The ratio says how much high-frequency
    energy the render has; the correlation says how much of it is the photo's real detail (noise,
    speckle and double surfaces score ~0)."""
    h, w = (covered.shape[0] // 2) * 2, (covered.shape[1] // 2) * 2

    def half(x):
        x = x[:h, :w].astype(np.float64)
        return (x[0::2, 0::2] + x[1::2, 0::2] + x[0::2, 1::2] + x[1::2, 1::2]) / 4

    gray = lambda x: half(x @ np.array([0.299, 0.587, 0.114]))
    mask = erode(half(covered) == 1.0, 1)
    if mask[1:-1, 1:-1].sum() <= 100:
        return None, None
    lr, lp = laplacian(gray(render)), laplacian(gray(photo))
    m = mask[1:-1, 1:-1]
    ratio = float(lr[m].var() / lp[m].var()) if lp[m].var() > 0 else None
    corr = float(np.corrcoef(lr[m], lp[m])[0, 1]) if lr[m].std() > 0 and lp[m].std() > 0 else None
    return ratio, corr


def erode(mask: np.ndarray, r: int = 2) -> np.ndarray:
    """Binary erosion by a (2r+1)^2 square -- keeps silhouette edges out of the sharpness ratio."""
    out = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out &= np.roll(np.roll(mask, dy, 0), dx, 1)
    return out


def voxel_downsample(points: np.ndarray, voxel: float) -> np.ndarray:
    """One point (the first) per `voxel`-sized cell."""
    keys = np.floor(points / voxel).astype(np.int64)
    _, first = np.unique(keys, axis=0, return_index=True)
    return points[np.sort(first)]


# --- reference build ------------------------------------------------------------------------


def _undistort_simple_radial(
    img: np.ndarray, f: float, cx: float, cy: float, k: float
) -> np.ndarray:
    """Resample a SIMPLE_RADIAL image onto the pinhole with the same f/cx/cy (COLMAP's model:
    x_d = x_u * (1 + k r_u^2) in normalised coordinates)."""
    import cv2

    h, w = img.shape[:2]
    u, v = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    x, y = (u - cx) / f, (v - cy) / f
    d = 1 + k * (x * x + y * y)
    return cv2.remap(
        img,
        (x * d * f + cx).astype(np.float32),
        (y * d * f + cy).astype(np.float32),
        cv2.INTER_LINEAR,
    )


def _recon_track(sparse: Path, id_fps: float):
    """(ids, times, centres, cam_to_world R) of a pycolmap reconstruction named `%04d.jpg`."""
    import pycolmap

    recon = pycolmap.Reconstruction(sparse)
    ims = sorted(recon.images.values(), key=lambda i: i.name)
    R = np.array([i.cam_from_world().rotation.matrix() for i in ims])
    t = np.array([np.asarray(i.cam_from_world().translation) for i in ims])
    ids = [Path(i.name).stem for i in ims]
    return recon, ims, ids, id_times(ids, None, id_fps), np.einsum("nji,nj->ni", R, -t), R


def build_reference(a: argparse.Namespace) -> None:
    from pipeline import sfm, telemetry
    from pipeline.calibrate import align_to_gltf, scale_from_caption_altitude, up_from_cameras
    from pipeline.run import RawPose, poses_to_cameras

    out = Path(a.out)
    img_dir = out / "sfm_images"
    img_dir.mkdir(parents=True, exist_ok=True)
    frames = sorted(Path(a.frames_dir).glob("*.jpg"))
    for p in frames:  # every `every`-th 10 fps frame, phase chosen so the eval frames are included
        if (int(p.stem) - 1) % a.every == a.eval_offset % a.every and not (
            img_dir / p.name
        ).exists():
            (img_dir / p.name).symlink_to(p.resolve())
    if not (out / "sfm/sparse/0/cameras.bin").exists():
        res = sfm.run_sfm(img_dir, out / "sfm", num_threads=a.threads)
        print(
            f"reference SfM: {res.num_registered}/{res.num_images} registered, "
            f"{res.mapper_used}, reproj {res.mean_reprojection_error:.2f}px, {res.timings_s}"
        )
    recon, ims, ids, times, C, R_wc = _recon_track(out / "sfm/sparse/0", a.id_fps)

    # metric + Y-up, exactly as the pipeline calibrates a GPS-less clip
    up = up_from_cameras(np.transpose(R_wc, (0, 2, 1)))
    recs = telemetry.parse_srt(Path(a.srt).read_text())
    h = np.array(
        [
            r.rel_alt_m if r.rel_alt_m is not None else np.nan
            for r in telemetry.resample(recs, list(times))
        ]
    )
    alt = scale_from_caption_altitude(C @ up, h)
    M = similarity_matrix(alt.scale, align_to_gltf(up), np.zeros(3))
    cam0 = ims[0].camera
    f, cx, cy, k = (float(x) for x in cam0.params)  # SIMPLE_RADIAL
    raw = [
        RawPose(
            im.name,
            im.cam_from_world().rotation.matrix(),
            np.asarray(im.cam_from_world().translation),
            f,
            f,
            cx,
            cy,
            cam0.width,
            cam0.height,
        )
        for im in ims
    ]
    cams = {c.id: c for c in poses_to_cameras(raw, M)}

    # evaluation frames: same fps=10 filter as the pipeline's frames, full resolution, undistorted
    raw_dir, eval_dir = out / "eval_raw", out / "eval"
    if not any(raw_dir.glob("*.jpg")):
        raw_dir.mkdir(parents=True, exist_ok=True)
        sel = f"fps={a.id_fps:g},select='eq(mod(n\\,{a.eval_every})\\,{a.eval_offset})'"
        subprocess.run(
            [
                "ffmpeg",
                "-y",
                "-i",
                a.video,
                "-vf",
                sel,
                "-fps_mode",
                "passthrough",
                "-q:v",
                "2",
                str(raw_dir / "%04d.jpg"),
            ],
            check=True,
            capture_output=True,
        )
    eval_dir.mkdir(exist_ok=True)
    evals = []
    for j, p in enumerate(sorted(raw_dir.glob("*.jpg"))):
        eid = f"{a.eval_offset + 1 + j * a.eval_every:04d}"
        if eid not in cams:
            print(f"eval frame {eid} not registered in the reference, dropped")
            continue
        dst = eval_dir / f"{eid}.jpg"
        if not dst.exists():
            img = np.asarray(Image.open(p).convert("RGB"))
            sx = img.shape[1] / cam0.width
            Image.fromarray(
                _undistort_simple_radial(img, f * sx, cx * sx - 0.5, cy * sx - 0.5, k)
            ).save(dst, quality=95)
        c = cams[eid]
        evals.append(
            {
                "id": eid,
                "t": float(times[ids.index(eid)]),
                "R": c.R.tolist(),
                "t_wc": c.t.tolist(),
                "fx": c.fx,
                "fy": c.fy,
                "cx": c.cx,
                "cy": c.cy,
                "w": c.w,
                "h": c.h,
            }
        )

    track = np.array([cams[i].position for i in ids])
    ref = {
        "n_sfm_frames": len(list(img_dir.glob("*.jpg"))),
        "n_registered": len(ids),
        "reproj_px": recon.compute_mean_reprojection_error(),
        "altitude_scale": alt.scale,
        "altitude_residual_m": alt.residual_m,
        "altitude_notes": alt.notes,
        "track": {"ids": ids, "times": times.tolist(), "positions": track.tolist()},
        "eval": evals,
    }

    if a.cloud:  # bring a dense cloud (in its own run's SfM frame) into the metric reference frame
        import pymeshlab

        _, _, _, ctimes, cC, _ = _recon_track(Path(a.cloud_sparse), a.cloud_id_fps)
        m = (ctimes >= times.min()) & (ctimes <= times.max())
        s, R, t, resid, keep = robust_sim3(
            cC[m], interpolate_positions_at_times(times, track, ctimes[m])
        )
        ms = pymeshlab.MeshSet()  # OpenMVS clouds carry per-vertex view lists trimesh can't parse
        ms.load_new_mesh(str(a.cloud))
        pts = ms.current_mesh().vertex_matrix().astype(np.float64)
        pts = voxel_downsample(s * (R @ pts.T).T + t, a.voxel)
        np.save(out / "cloud.npy", pts.astype(np.float32))
        ref["cloud"] = {
            "source": str(a.cloud),
            "points": len(pts),
            "voxel_m": a.voxel,
            "align_residual_m": resid,
            "align_inliers": int(keep.sum()),
            "align_n": int(m.sum()),
        }
    (out / "reference.json").write_text(json.dumps(ref, indent=1))
    print(
        json.dumps({k: v for k, v in ref.items() if k not in ("track", "eval")}, indent=1),
        f"\n{len(evals)} eval frames",
    )


# --- scoring ---------------------------------------------------------------------------------


def _speed(a: argparse.Namespace) -> dict:
    speed: dict = {}
    if a.run_json and Path(a.run_json).exists():
        run = json.loads(Path(a.run_json).read_text())
        speed.update(
            wall_s=run.get("wall_s"), args=run.get("args"), cached_from=run.get("cached_from")
        )
    if a.wall_s is not None:
        speed["wall_s"] = a.wall_s
    speed["cached_s"] = a.cached_s
    if speed.get("wall_s") is not None:
        speed["est_total_s"] = speed["wall_s"] + a.cached_s
    if a.report and Path(a.report).exists():
        rep = json.loads(Path(a.report).read_text())
        speed["timings_s"] = {k: round(v, 1) for k, v in rep.get("timings_s", {}).items() if v}
        speed["sfm"] = rep.get("sfm")
    return speed


def score(a: argparse.Namespace) -> dict:
    import trimesh

    from pipeline.preview import render
    from pipeline.qa import _load_cameras, _resolve_package_files, _write_side_by_side, psnr, ssim

    ref_dir, scene = Path(a.ref), Path(a.scene)
    ref = json.loads((ref_dir / "reference.json").read_text())
    tr_t = np.array(ref["track"]["times"])
    tr_p = np.array(ref["track"]["positions"])

    cams = _load_cameras(scene)
    ids = sorted(cams)
    times = id_times(ids, json.loads(Path(a.times).read_text()) if a.times else None, a.id_fps)
    C = np.array([cams[i].position for i in ids])
    m = (times >= tr_t.min()) & (times <= tr_t.max())
    s, R, t, resid, keep = robust_sim3(C[m], interpolate_positions_at_times(tr_t, tr_p, times[m]))
    M = similarity_matrix(s, R, t)

    mesh_file, _ = _resolve_package_files(scene)
    glb = scene / mesh_file
    mesh = trimesh.load(glb, force="scene", process=False).to_geometry()
    mesh.apply_transform(M)

    out = Path(a.out)
    shots = out / a.name
    per = []
    for k, e in enumerate(ref["eval"]):
        cam = Camera(
            id=e["id"],
            file="",
            thumb="",
            R=np.array(e["R"]),
            t=np.array(e["t_wc"]),
            fx=e["fx"],
            fy=e["fy"],
            cx=e["cx"],
            cy=e["cy"],
            w=e["w"],
            h=e["h"],
        )
        with Image.open(ref_dir / "eval" / f"{e['id']}.jpg") as im:
            photo = np.asarray(im.convert("RGB").resize((a.width, a.height), Image.LANCZOS))
        img, cov = render(mesh, cam, width=a.width, height=a.height)
        gap = float(np.min(np.abs(times - e["t"])))
        sharp, lap_corr = detail_scores(img, photo, cov)
        per.append(
            {
                "id": e["id"],
                "t": e["t"],
                "coverage": float(cov.mean()),
                "seen": gap < 0.5 / VIDEO_FPS,
                "registered": gap <= a.max_gap,
                "psnr": psnr(img, photo, mask=cov) if cov.sum() > 100 else None,
                "ssim": ssim(img, photo, mask=cov) if cov.sum() > 100 else None,
                "sharpness_ratio": sharp,
                "lap_corr": lap_corr,
            }
        )
        if k % 10 == 0:
            _write_side_by_side(photo, img, shots / f"{e['id']}.png")

    def agg(rows):
        f = lambda key: [r[key] for r in rows if r[key] is not None and np.isfinite(r[key])]
        mean = lambda xs: float(np.mean(xs)) if xs else None
        median = lambda xs: float(np.median(xs)) if xs else None
        return {
            "n": len(rows),
            "coverage": mean([r["coverage"] for r in rows]),
            "psnr": mean(f("psnr")),
            "ssim": mean(f("ssim")),
            "sharpness_ratio": median(f("sharpness_ratio")),
            "lap_corr": mean(f("lap_corr")),
        }

    area = float(mesh.area)
    tex = mesh.visual.material.baseColorTexture.size if hasattr(mesh.visual, "material") else (0, 0)
    mb = glb.stat().st_size / 1e6
    geom = {}
    cloud = ref_dir / "cloud.npy"
    if cloud.exists():
        n = int(min(a.max_samples, max(10_000, area * a.samples_per_m2)))
        pts, _ = trimesh.sample.sample_surface(mesh, n, seed=0)
        geom = geometry_metrics(np.asarray(pts), np.load(cloud).astype(np.float64))
    result = {
        "name": a.name,
        "scene": str(scene),
        "speed": _speed(a),
        "alignment": {
            "scale_ref_per_cand": s,
            "cand_size_error_pct": (1 / s - 1) * 100,
            "residual_m": resid,
            "inliers": int(keep.sum()),
            "n": int(m.sum()),
        },
        "registration_coverage": float(np.mean([r["registered"] for r in per])),
        "all": agg(per),
        "held_out": agg([r for r in per if not r["seen"]]),
        "seen": agg([r for r in per if r["seen"]]),
        "geometry": geom,
        "detail": {
            "triangles": len(mesh.faces),
            "area_m2": area,
            "tris_per_m2": len(mesh.faces) / area if area else None,
            "texture_px": list(tex),
            "texels_per_m2": tex[0] * tex[1] / area if area else None,
        },
        "quest": {
            "triangles": len(mesh.faces),
            "texture_px": int(max(tex)),
            "glb_mb": mb,
            "ok": len(mesh.faces) <= QUEST_TRIS and max(tex) <= QUEST_TEX and mb <= QUEST_MB,
        },
        "per_frame": per,
        "note": a.note,
    }
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{a.name}.json").write_text(json.dumps(result, indent=1))
    _append_row(out / "results.md", result)
    return result


_COLS = (
    "run | wall s | est total s | view cov | reg cov | PSNR | SSIM | held-out PSNR | Chamfer cm "
    "| F@2cm | F@5cm | sharp | lap corr | tris | tex | MB | quest | align cm | size err %"
)


def _append_row(path: Path, r: dict) -> None:
    def f(x, fmt):
        return format(x, fmt) if isinstance(x, (int, float)) and x is not None else "-"

    g, sp, al = r["geometry"], r["speed"], r["alignment"]
    row = [
        r["name"],
        f(sp.get("wall_s"), "d"),
        f(sp.get("est_total_s"), "d"),
        f(r["all"]["coverage"], ".3f"),
        f(r["registration_coverage"], ".2f"),
        f(r["all"]["psnr"], ".2f"),
        f(r["all"]["ssim"], ".3f"),
        f(r["held_out"]["psnr"], ".2f"),
        f(g.get("chamfer_m", None) and g["chamfer_m"] * 100, ".2f"),
        f(g.get("f_2cm"), ".3f"),
        f(g.get("f_5cm"), ".3f"),
        f(r["all"]["sharpness_ratio"], ".2f"),
        f(r["all"].get("lap_corr"), ".2f"),
        f(r["quest"]["triangles"], "d"),
        f(r["quest"]["texture_px"], "d"),
        f(r["quest"]["glb_mb"], ".1f"),
        "ok" if r["quest"]["ok"] else "over",
        f(al["residual_m"] * 100, ".1f"),
        f(al["cand_size_error_pct"], "+.1f"),
    ]
    if not path.exists():
        path.write_text(f"| {_COLS} |\n|{'---|' * len(row)}\n")
    with path.open("a") as fh:
        fh.write("| " + " | ".join(row) + " |\n")


def rebuild_table(a: argparse.Namespace) -> None:
    out = Path(a.out)
    (out / "results.md").unlink(missing_ok=True)
    runs = [json.loads(p.read_text()) for p in sorted(out.glob("*.json"))]
    for r in sorted(runs, key=lambda r: r["name"]):
        _append_row(out / "results.md", r)
    print((out / "results.md").read_text())


def main(argv: list[str] | None = None) -> None:
    p = argparse.ArgumentParser(prog="python -m pipeline.experiments.bench")
    sub = p.add_subparsers(dest="cmd", required=True)

    r = sub.add_parser("reference", help="build the reference cameras, eval frames and cloud")
    r.add_argument("--video", required=True)
    r.add_argument(
        "--frames-dir", required=True, help="the pipeline's 10 fps frames (work/<site>/frames)"
    )
    r.add_argument("--srt", required=True, help="caption track, for the metric scale")
    r.add_argument("--out", required=True)
    r.add_argument("--id-fps", type=float, default=10.0)
    r.add_argument("--every", type=int, default=3, help="reference SfM uses every Nth 10 fps frame")
    r.add_argument("--eval-every", type=int, default=15)
    r.add_argument("--eval-offset", type=int, default=2)
    r.add_argument("--cloud", default=None, help="reference dense cloud (ply) in its own SfM frame")
    r.add_argument("--cloud-sparse", default=None, help="that run's SfM model (for alignment)")
    r.add_argument("--cloud-id-fps", type=float, default=10.0)
    r.add_argument("--voxel", type=float, default=0.005)
    r.add_argument("--threads", type=int, default=16)
    r.set_defaults(func=build_reference)

    s = sub.add_parser(
        "score", help="score one candidate (dir with scene.json or mesh.glb+cameras.json)"
    )
    s.add_argument("scene")
    s.add_argument("--ref", required=True)
    s.add_argument("--name", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--times", default=None, help="JSON {camera id: video time s}")
    s.add_argument(
        "--id-fps", type=float, default=10.0, help="else ids are %%04d frames at this fps"
    )
    s.add_argument("--run-json", default=None, help="bench run record (wall_s, args)")
    s.add_argument("--report", default=None, help="pipeline work/<site>/report.json (step timings)")
    s.add_argument("--wall-s", type=int, default=None)
    s.add_argument("--cached-s", type=int, default=0, help="time of stages reused from a cache")
    s.add_argument("--max-gap", type=float, default=1.0)
    s.add_argument("--width", type=int, default=960)
    s.add_argument("--height", type=int, default=540)
    s.add_argument("--samples-per-m2", type=float, default=20_000)
    s.add_argument("--max-samples", type=int, default=1_000_000)
    s.add_argument("--note", default="")
    s.set_defaults(
        func=lambda a: print(
            json.dumps({k: v for k, v in score(a).items() if k != "per_frame"}, indent=1)
        )
    )

    t = sub.add_parser("table", help="rebuild <out>/results.md from every <out>/*.json")
    t.add_argument("out")
    t.set_defaults(func=rebuild_table)

    args = p.parse_args(argv)
    args.func(args)
