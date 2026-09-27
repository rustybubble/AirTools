"""Objective QA (Part 1): render `mesh.glb` from real camera poses (`preview.render`) and compare
against the real photos -- PSNR/SSIM/coverage stand in for "does this look right" when nobody's
looking at a headset. `pipeline qa <scene_dir> --frames <dir>` writes `qa.json` + side-by-side
PNGs (`photo | render | abs diff`). `run.py` wires this in automatically at the end of a run.

Hold-out: `default_ids` can include frame ids that were never registered in `cameras.json` --
`run()` extracts `holdout_every`x denser than it feeds to SfM by default (see `run.py`), so most
runs have a genuine held-out set on disk; scoring those measures generalisation, not just how well
the mesh reproduces its own texture source. Their pose is approximated by interpolating between
their two nearest *registered* neighbours (`interpolate_camera`), good enough for a QA render,
never used for the real package. `photo_consistency`'s result reports `train`/`held_out` as
separate aggregates (plus the combined top-level `mean_*` over both) for exactly this reason --
a mesh that looks great only on its own source photos and falls apart on held-out ones is a real,
different failure mode from one that's just low quality everywhere.
"""

import json
import logging
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
import trimesh
import trimesh.transformations as tf
from PIL import Image

from pipeline.package import Camera
from pipeline.preview import render

logger = logging.getLogger(__name__)

_DEFAULT_HOLDOUT_EVERY = 5
_DEFAULT_WIDTH, _DEFAULT_HEIGHT = 960, 540


# --- metrics (pure numpy -- no skimage/scipy dependency) -------------------------------------


def psnr(a: np.ndarray, b: np.ndarray, mask: np.ndarray | None = None) -> float:
    """Peak signal-to-noise ratio in dB over `mask` (default: everywhere). `inf` for identical
    images, `nan` if `mask` covers nothing."""
    a, b = a.astype(np.float64), b.astype(np.float64)
    diff = (a - b) if mask is None else (a[mask] - b[mask])
    if diff.size == 0:
        return float("nan")
    mse = float(np.mean(diff**2))
    return float("inf") if mse == 0.0 else 10.0 * np.log10(255.0**2 / mse)


def _to_gray(img: np.ndarray) -> np.ndarray:
    return img[..., 0] * 0.299 + img[..., 1] * 0.587 + img[..., 2] * 0.114


def _box_filter(img: np.ndarray, k: int) -> np.ndarray:
    """Mean over a `k x k` window, same shape as `img` (edge-replicated border) -- a numpy-only
    substitute for `scipy.ndimage.uniform_filter`, via an integral image (cumulative sum)."""
    pad = k // 2
    padded = np.pad(img, pad, mode="edge")
    cs = np.pad(np.cumsum(np.cumsum(padded, axis=0), axis=1), ((1, 0), (1, 0)))
    h, w = img.shape
    total = cs[k : k + h, k : k + w] - cs[:h, k : k + w] - cs[k : k + h, :w] + cs[:h, :w]
    return total / (k * k)


def ssim(a: np.ndarray, b: np.ndarray, mask: np.ndarray | None = None, window: int = 7) -> float:
    """Simplified single-scale SSIM (Wang et al. 2004) on grayscale luminance, windowed mean/
    variance/covariance via `_box_filter`. Mean of the per-pixel SSIM map over `mask` (default:
    everywhere). `1.0` for identical images, `nan` if `mask` covers nothing."""
    if mask is not None and not mask.any():
        return float("nan")
    ga, gb = _to_gray(a.astype(np.float64)), _to_gray(b.astype(np.float64))
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    mu_a, mu_b = _box_filter(ga, window), _box_filter(gb, window)
    mu_a2, mu_b2, mu_ab = mu_a * mu_a, mu_b * mu_b, mu_a * mu_b
    var_a = _box_filter(ga * ga, window) - mu_a2
    var_b = _box_filter(gb * gb, window) - mu_b2
    cov_ab = _box_filter(ga * gb, window) - mu_ab
    ssim_map = ((2 * mu_ab + c1) * (2 * cov_ab + c2)) / (
        (mu_a2 + mu_b2 + c1) * (var_a + var_b + c2)
    )
    return float(ssim_map.mean()) if mask is None else float(ssim_map[mask].mean())


# --- cameras.json + held-out pose interpolation -----------------------------------------------


def _resolve_package_files(package_dir: Path) -> tuple[str, str]:
    """(mesh_filename, cameras_filename) actually published for this package: reads scene.json's
    `mesh.file`/`cameras` fields (plan §1a's revision-named layout, e.g. "mesh.r2.glb"), falling
    back to the old flat "mesh.glb"/"cameras.json" names if there's no scene.json (or it predates
    the 'cameras' field) -- keeps a bare mesh.glb+cameras.json dir (existing tests, ad-hoc use)
    working unchanged."""
    scene_path = Path(package_dir) / "scene.json"
    if scene_path.exists():
        scene = json.loads(scene_path.read_text())
        if "cameras" in scene and "mesh" in scene:
            return scene["mesh"]["file"], scene["cameras"]
    return "mesh.glb", "cameras.json"


def _load_cameras(package_dir: Path) -> dict[str, Camera]:
    _, cameras_file = _resolve_package_files(package_dir)
    rows = json.loads((Path(package_dir) / cameras_file).read_text())
    return {
        row["id"]: Camera(
            id=row["id"],
            file=row["file"],
            thumb=row["thumb"],
            R=np.array(row["R"], dtype=np.float64),
            t=np.array(row["t"], dtype=np.float64),
            fx=row["fx"],
            fy=row["fy"],
            cx=row["cx"],
            cy=row["cy"],
            w=row["w"],
            h=row["h"],
        )
        for row in rows
    }


def interpolate_camera(cams: dict[str, Camera], target_id: str) -> Camera:
    """Approximate pose for `target_id`, a frame not in `cams` (never registered by SfM): SLERP
    the rotation and lerp the world-space camera position between its two temporally-nearest
    *registered* neighbours, by numeric frame index (`frames.extract_frames` names frames
    sequentially at a fixed fps, so index order is time order). Falls back to the single nearest
    neighbour's pose (with a warning) at either end of the sequence, where there's no bracketing
    pair. Intrinsics are copied from the nearer neighbour (SfM runs `single_camera=True`, so
    they're the same for every frame anyway)."""
    target_idx = int(target_id)
    by_idx = sorted((int(cid), cid) for cid in cams)
    if not by_idx:
        raise ValueError("interpolate_camera: no registered cameras to interpolate from")

    lower = max(((idx, cid) for idx, cid in by_idx if idx < target_idx), default=None)
    upper = min(((idx, cid) for idx, cid in by_idx if idx > target_idx), default=None)

    if lower is None or upper is None:
        nearest = lower or upper
        logger.warning(
            "interpolate_camera: %s is outside the registered range, reusing nearest pose (%s)",
            target_id,
            nearest[1],
        )
        return cams[nearest[1]]

    lo_cam, hi_cam = cams[lower[1]], cams[upper[1]]
    fraction = (target_idx - lower[0]) / (upper[0] - lower[0])

    q0 = tf.quaternion_from_matrix(lo_cam.R)
    q1 = tf.quaternion_from_matrix(hi_cam.R)
    q = tf.quaternion_slerp(q0, q1, fraction)
    R = tf.quaternion_matrix(q)[:3, :3]

    position = (1.0 - fraction) * lo_cam.position + fraction * hi_cam.position
    t = -R @ position

    return Camera(
        id=target_id,
        file=f"frames/{target_id}.jpg",
        thumb=f"thumbs/{target_id}.jpg",
        R=R,
        t=t,
        fx=lo_cam.fx,
        fy=lo_cam.fy,
        cx=lo_cam.cx,
        cy=lo_cam.cy,
        w=lo_cam.w,
        h=lo_cam.h,
    )


def default_ids(
    package_dir: Path,
    frames_dir: Path,
    holdout_every: int = _DEFAULT_HOLDOUT_EVERY,
    max_ids: int | None = None,
) -> list[str]:
    """Every registered camera id, plus (if `holdout_every > 0`) every `holdout_every`-th frame
    in `frames_dir` that ISN'T registered -- frames present on disk but never fed to SfM/OpenMVS,
    so scoring them exercises generalisation (assignment's hold-out requirement). `max_ids`, if
    given, evenly subsamples the combined id list (keeps an auto-wired QA pass from multiplying
    a `run()`'s wall time by its frame count)."""
    cams = _load_cameras(package_dir)
    ids = list(cams.keys())
    if holdout_every > 0:
        frame_ids = sorted(p.stem for p in Path(frames_dir).glob("*.jpg"))
        held_out = [
            fid
            for i, fid in enumerate(frame_ids)
            if (i + 1) % holdout_every == 0 and fid not in cams
        ]
        ids += held_out
    ids = sorted(set(ids))
    if max_ids is not None and len(ids) > max_ids:
        stride = max(1, len(ids) // max_ids)
        ids = ids[::stride][:max_ids]
    return ids


# --- photo_consistency ---------------------------------------------------------------------


@dataclass
class FrameQA:
    id: str
    held_out: bool
    coverage: float
    psnr: float
    ssim: float


def _find_photo(frames_dir: Path, frame_id: str) -> Path | None:
    for ext in (".jpg", ".jpeg", ".png"):
        candidate = frames_dir / f"{frame_id}{ext}"
        if candidate.exists():
            return candidate
    return None


def _write_side_by_side(photo: np.ndarray, rendered: np.ndarray, out_path: Path) -> None:
    diff = np.abs(photo.astype(np.int16) - rendered.astype(np.int16)).astype(np.uint8)
    combined = np.concatenate([photo, rendered, diff], axis=1)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(combined).save(out_path)


def photo_consistency(
    package_dir: Path,
    frames_dir: Path,
    ids: list[str],
    *,
    out_dir: Path | None = None,
    width: int = _DEFAULT_WIDTH,
    height: int = _DEFAULT_HEIGHT,
) -> dict:
    """Render `package_dir/mesh.glb` from each of `ids`'s cameras and compare to the real photo
    in `frames_dir` (resized to `width x height`) over the pixels the mesh covers: PSNR, a
    windowed SSIM, and coverage fraction. Ids not in `package_dir/cameras.json` are treated as
    held out (`interpolate_camera`). Writes a `<id>.png` side-by-side (photo | render | abs diff)
    per id under `out_dir` if given. Returns
    `{"per_frame": [...], "mean_psnr", "mean_ssim", "mean_coverage"}`.
    """
    package_dir, frames_dir = Path(package_dir), Path(frames_dir)
    mesh_file, _ = _resolve_package_files(package_dir)
    mesh = trimesh.load(package_dir / mesh_file, force="scene", process=False).to_geometry()
    cams = _load_cameras(package_dir)

    per_frame: list[FrameQA] = []
    for frame_id in ids:
        photo_path = _find_photo(frames_dir, frame_id)
        if photo_path is None:
            logger.warning("qa: no photo for id %s in %s, skipping", frame_id, frames_dir)
            continue

        held_out = frame_id not in cams
        camera = interpolate_camera(cams, frame_id) if held_out else cams[frame_id]

        with Image.open(photo_path) as img:
            photo = np.asarray(img.convert("RGB").resize((width, height), Image.LANCZOS))
        rendered, covered = render(mesh, camera, width=width, height=height)

        per_frame.append(
            FrameQA(
                id=frame_id,
                held_out=held_out,
                coverage=float(covered.mean()),
                psnr=psnr(rendered, photo, mask=covered),
                ssim=ssim(rendered, photo, mask=covered),
            )
        )
        if out_dir is not None:
            _write_side_by_side(photo, rendered, Path(out_dir) / f"{frame_id}.png")

    return {
        "per_frame": [asdict(f) for f in per_frame],
        **_aggregate(per_frame),  # mean_psnr/mean_ssim/mean_coverage over ALL scored frames
        # split out: generalisation (held_out) vs. how well the mesh reproduces its own texture
        # source (train) -- an uncropped/no-held-out run has real "train" numbers and a "held_out"
        # block of all-None/n=0, which is the honest answer when no held-out frame was ever scored.
        "train": _aggregate([f for f in per_frame if not f.held_out]),
        "held_out": _aggregate([f for f in per_frame if f.held_out]),
    }


def _aggregate(rows: list[FrameQA]) -> dict:
    finite_psnr = [r.psnr for r in rows if np.isfinite(r.psnr)]
    finite_ssim = [r.ssim for r in rows if not np.isnan(r.ssim)]
    return {
        "n": len(rows),
        "mean_psnr": float(np.mean(finite_psnr)) if finite_psnr else None,
        "mean_ssim": float(np.mean(finite_ssim)) if finite_ssim else None,
        "mean_coverage": float(np.mean([r.coverage for r in rows])) if rows else None,
    }
