"""Runs INSIDE `AIRTOOLS_DEPTH_ENV` (ROCm torch + a monocular depth model + open3d): the
`--densify depthfusion` replacement for OpenMVS `DensifyPointCloud` + `ReconstructMesh`
(docs/research/bench/e3-depth-fusion.md, r1-academic.md §9.2 R1).

Per undistorted COLMAP frame: monocular depth (MoGe-2 metric by default) -> robust fit to the
COLMAP sparse points seen in that frame (scale, scale+shift, or a scale plane + shift) -> reject
frames the fit can't explain -> mask depth edges, grazing angles and far depth -> Open3D
`VoxelBlockGrid` TSDF (sparse, CPU; the legacy `ScalableTSDFVolume` is broken in the 0.20 pip wheel
and `UniformTSDFVolume` is dense, too big at 1 cm) -> marching cubes -> drop small components ->
`mesh.ply`, in COLMAP units, ready for OpenMVS `TextureMesh -m`.

Standalone like `fast_worker.py`: no `pipeline.*` imports; the orchestrator (`pipeline.depthfusion`)
hands it a `cameras.npz` (K, cam-from-world, sparse observations with camera-frame depth).
Depth predictions are cached per model under `--cache`, so alignment/voxel ablations re-run in
seconds.
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path

if sys.path and sys.path[0] == str(Path(__file__).resolve().parent):
    sys.path.pop(0)  # pipeline/vggt.py etc. must not shadow third-party packages (see fast_worker)

os.environ.setdefault("TORCH_HOME", "/workspace/.cache/torch")
os.environ.setdefault("MIOPEN_USER_DB_PATH", "/workspace/.cache/miopen")
os.environ.setdefault("MIOPEN_CUSTOM_CACHE_DIR", "/workspace/.cache/miopen")

import numpy as np

MODELS = ("moge2", "dav2-metric", "dav2-rel", "promptda", "worldmirror", "vggt-omega")
# multi-view models: depth for a chunk of consecutive frames at once (consistent within the chunk);
# the per-frame fit below still maps every frame onto the COLMAP scale
MULTIVIEW = ("worldmirror", "vggt-omega")
ALIGNS = ("scale", "affine", "plane", "global")  # global: one scale for every frame


# --- depth models (each yields (depth (H,W) float32, valid mask (H,W) bool) at image res) --------


def _load_rgb(path: Path) -> np.ndarray:
    from PIL import Image

    with Image.open(path) as im:
        return np.asarray(im.convert("RGB"))


def sparse_prompt(uv: np.ndarray, z: np.ndarray, W: int, H: int, pw: int = 256) -> np.ndarray:
    """A dense low-res depth "prompt" (PromptDA expects a LiDAR-like map) from one frame's sparse
    SfM depths: linear interpolation inside their convex hull, nearest outside."""
    from scipy.interpolate import griddata

    ph = max(2, round(pw * H / W))
    if len(z) < 4:  # too few points to triangulate; the frame will fail alignment anyway
        return np.full((ph, pw), float(np.median(z)) if len(z) else 1.0, np.float32)
    gx, gy = np.meshgrid((np.arange(pw) + 0.5) * W / pw, (np.arange(ph) + 0.5) * H / ph)
    lin = griddata(uv, z, (gx, gy), method="linear")
    near = griddata(uv, z, (gx, gy), method="nearest")
    return np.where(np.isfinite(lin), lin, near).astype(np.float32)


def _stub_gsplat() -> None:
    """WorldMirror's module imports gsplat (CUDA-only, no ROCm wheel) for its 3DGS head, which we
    switch off; a stub lets the import succeed."""
    import types

    if "gsplat" in sys.modules:
        return
    root, rend, strat = (
        types.ModuleType(n) for n in ("gsplat", "gsplat.rendering", "gsplat.strategy")
    )
    rend.rasterization, strat.DefaultStrategy = None, object
    root.rendering, root.strategy = rend, strat
    sys.modules.update({"gsplat": root, "gsplat.rendering": rend, "gsplat.strategy": strat})


def multiview_depth_frames(
    model: str, paths: list[Path], K: np.ndarray, T_cw: np.ndarray, chunk: int
):
    """Yield `(depth, mask)` per image from a multi-view model run on chunks of `chunk` frames.
    `worldmirror` (HunyuanWorld-Mirror) gets our COLMAP poses + intrinsics as priors; `vggt-omega`
    takes images only (gated weights: `AIRTOOLS_VGGT_OMEGA_CKPT`). Depth is up to one scale per
    chunk and upsampled (bilinear) from the model's ~518 px to image resolution."""
    import torch
    import torch.nn.functional as F
    from PIL import Image

    dev = "cuda"
    if model == "worldmirror":
        sys.path.insert(
            0, os.environ.get("AIRTOOLS_WORLDMIRROR_REPO", "/workspace/src/HunyuanWorld-Mirror")
        )
        _stub_gsplat()
        from src.models.models.worldmirror import WorldMirror

        net = WorldMirror.from_pretrained("tencent/HunyuanWorld-Mirror").to(dev).eval()
        net.enable_gs = net.enable_pts = net.enable_norm = False
    else:
        sys.path.insert(0, os.environ.get("AIRTOOLS_VGGT_OMEGA_REPO", "/workspace/src/vggt-omega"))
        from vggt_omega.models import VGGTOmega
        from vggt_omega.utils.load_fn import load_and_preprocess_images

        ckpt = os.environ.get(
            "AIRTOOLS_VGGT_OMEGA_CKPT", "/workspace/models/vggt-omega/vggt_omega_1b_512.pt"
        )
        net = VGGTOmega().to(dev).eval()
        net.load_state_dict(torch.load(ckpt, map_location="cpu"))

    for c0 in range(0, len(paths), chunk):
        cp = paths[c0 : c0 + chunk]
        with Image.open(cp[0]) as im:
            W0, H0 = im.size
        with torch.no_grad():
            if model == "worldmirror":
                w, h = 518, round(H0 * 518 / W0 / 14) * 14
                imgs = torch.stack(
                    [
                        torch.from_numpy(
                            np.asarray(Image.open(p).convert("RGB").resize((w, h), Image.BICUBIC))
                        )
                        .permute(2, 0, 1)
                        .float()
                        / 255
                        for p in cp
                    ]
                )[None].to(dev)
                Ks = K[c0 : c0 + chunk].copy()
                Ks[:, 0] *= w / W0
                Ks[:, 1] *= h / H0
                views = {
                    "img": imgs,
                    "camera_poses": torch.from_numpy(np.linalg.inv(T_cw[c0 : c0 + chunk]))
                    .float()[None]
                    .to(dev),
                    "camera_intrs": torch.from_numpy(Ks).float()[None].to(dev),
                }
                with torch.autocast("cuda", dtype=torch.bfloat16):
                    pred = net(views=views, cond_flags=[1, 0, 1])
            else:
                imgs = load_and_preprocess_images([str(p) for p in cp], mode="max_size").to(dev)
                pred = net(imgs)
        d = pred["depth"][0].float()
        conf = pred["depth_conf"][0].float()
        d = d[..., 0] if d.ndim == 4 else d
        d = F.interpolate(d[:, None], size=(H0, W0), mode="bilinear", align_corners=False)[:, 0]
        conf = F.interpolate(conf[:, None], size=(H0, W0), mode="bilinear", align_corners=False)[
            :, 0
        ]
        for di, ci in zip(d.cpu().numpy(), conf.cpu().numpy()):
            m = np.isfinite(di) & (di > 0) & (ci >= np.quantile(ci, 0.1))
            yield np.where(m, di, 0).astype(np.float32), m
        del pred
        torch.cuda.empty_cache()


def depth_frames(model: str, paths: list[Path], K: np.ndarray, prompts=None):
    """Yield `(depth, mask)` per image. `moge2`/`dav2-metric` give metric depth (m); `dav2-rel`
    gives relative *disparity* (bigger = nearer), which `align` fits in inverse depth;
    `promptda` refines the per-frame `prompts` (low-res metric depth from the SfM points)."""
    import torch

    dev = "cuda"
    if model == "moge2":
        from moge.model import import_model_class_by_version

        net = import_model_class_by_version("v2").from_pretrained("Ruicheng/moge-2-vitl-normal")
        net = net.to(dev).eval()
        for p, k in zip(paths, K):
            rgb = _load_rgb(p)
            fov_x = float(np.degrees(2 * np.arctan(rgb.shape[1] / (2 * k[0, 0]))))
            x = torch.from_numpy(rgb).to(dev).permute(2, 0, 1).float() / 255
            with torch.no_grad():
                out = net.infer(x, fov_x=fov_x)
            d = out["depth"].float().cpu().numpy()
            m = out["mask"].cpu().numpy().astype(bool) & np.isfinite(d)
            yield np.where(m, d, 0).astype(np.float32), m
        return

    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    repo = {
        "dav2-metric": "depth-anything/Depth-Anything-V2-Metric-Indoor-Large-hf",
        "dav2-rel": "depth-anything/Depth-Anything-V2-Large-hf",
        "promptda": "depth-anything/prompt-depth-anything-vitl-hf",
    }[model]
    proc = AutoImageProcessor.from_pretrained(repo)
    net = AutoModelForDepthEstimation.from_pretrained(repo).to(dev).eval()
    for j, p in enumerate(paths):
        rgb = _load_rgb(p)
        inputs = {k: v.to(dev) for k, v in proc(images=rgb, return_tensors="pt").items()}
        if prompts is not None:
            inputs["prompt_depth"] = torch.from_numpy(prompts[j])[None, None].to(dev)
        with torch.no_grad():
            pred = net(**inputs).predicted_depth  # (1, h, w)
        d = torch.nn.functional.interpolate(
            pred[:, None], size=rgb.shape[:2], mode="bilinear", align_corners=False
        )[0, 0]
        d = d.float().cpu().numpy()
        yield d.astype(np.float32), np.isfinite(d) & (d > 0)


# --- alignment (pure numpy, unit-tested in tests/pipeline/test_depthfusion.py) -------------------


def _huber_irls(A: np.ndarray, b: np.ndarray, delta: float, iters: int = 10) -> np.ndarray:
    """Least squares `A x ~= b` with Huber weights (`delta` in `b`'s units)."""
    w = np.ones(len(b))
    x = np.zeros(A.shape[1])
    for _ in range(iters):
        sw = np.sqrt(w)
        x = np.linalg.lstsq(A * sw[:, None], b * sw, rcond=None)[0]
        r = np.abs(A @ x - b)
        w = np.where(r <= delta, 1.0, delta / np.maximum(r, 1e-12))
    return x


def fit_alignment(
    d_pred: np.ndarray, z_sfm: np.ndarray, uv_norm: np.ndarray, method: str, disparity: bool
) -> tuple[np.ndarray, np.ndarray]:
    """Fit predicted depth to SfM depth at the sparse points. Returns `(params, z_fit)`:
    - `scale`: z = s*d (median log-ratio, then Huber in relative error)
    - `affine`: z = s*d + t (Huber IRLS, delta = 2% of median depth)
    - `plane`: z = (a + b*u + c*v)*d + t -- a low-order scale field for a tilted depth bias
    `uv_norm` is pixel coords normalised to [-1, 1]. With `disparity`, the model output is inverse
    depth and the fit is done in 1/z (`affine` then means z = 1/(s*d + t))."""
    target = 1.0 / z_sfm if disparity else z_sfm
    med = float(np.median(target))
    delta = 0.02 * med
    if method == "scale":
        s = float(np.exp(np.median(np.log(target) - np.log(d_pred))))
        # Huber refine on the relative residual (same scale, less sensitive to the median's noise)
        s = float(_huber_irls((d_pred / target)[:, None], np.ones_like(target), 0.02)[0]) or s
        params = np.array([s])
        fit = s * d_pred
    elif method == "affine":
        A = np.stack([d_pred, np.ones_like(d_pred)], 1)
        params = _huber_irls(A, target, delta)
        fit = A @ params
    elif method == "plane":
        A = np.stack(
            [d_pred, d_pred * uv_norm[:, 0], d_pred * uv_norm[:, 1], np.ones_like(d_pred)], 1
        )
        params = _huber_irls(A, target, delta)
        fit = A @ params
    else:
        raise ValueError(f"unknown align method {method!r}")
    if disparity:
        fit = 1.0 / np.maximum(fit, 1e-9)
    return params, fit


def apply_alignment(
    d: np.ndarray, params: np.ndarray, method: str, disparity: bool, W: int, H: int
) -> np.ndarray:
    if method == "scale":
        out = params[0] * d
    elif method == "affine":
        out = params[0] * d + params[1]
    else:
        v, u = np.mgrid[0:H, 0:W].astype(np.float32)
        un, vn = 2 * u / (W - 1) - 1, 2 * v / (H - 1) - 1
        out = (params[0] + params[1] * un + params[2] * vn) * d + params[3]
    if disparity:
        out = np.where(out > 1e-9, 1.0 / np.maximum(out, 1e-9), 0)
    return out.astype(np.float32)


# --- hybrid: OpenMVS depth maps where PatchMatch is reliable, corrected mono depth elsewhere -----


def read_dmap(path: Path) -> tuple[str, np.ndarray, np.ndarray | None]:
    """OpenMVS `depthNNNN.dmap` (v2.x raw format) -> `(image file name, depth (H,W), conf|None)`.
    Depth is camera-frame z in scene (COLMAP) units at the depth-map resolution; 0 = no estimate."""
    import struct

    b = Path(path).read_bytes()
    magic, typ, _, _, _, dw, dh, _, _ = struct.unpack_from("<HBBIIIIff", b, 0)
    if magic != 0x5244:  # "DR"
        raise ValueError(f"{path}: not an OpenMVS depth map")
    o = 28
    (n,) = struct.unpack_from("<H", b, o)
    name = Path(b[o + 2 : o + 2 + n].decode()).name
    o += 2 + n
    (nid,) = struct.unpack_from("<I", b, o)
    o += 4 + 4 * nid + 8 * (9 + 9 + 3)  # neighbour ids, K, R, C
    area = dw * dh
    depth = np.frombuffer(b, "<f4", area, o).reshape(dh, dw)
    o += 4 * area
    if typ & 2:  # normals
        o += 12 * area
    conf = np.frombuffer(b, "<f4", area, o).reshape(dh, dw) if typ & 4 else None
    return name, depth, conf


def hybrid_depth(
    z_mono: np.ndarray,
    z_mvs: np.ndarray,
    conf: np.ndarray | None,
    *,
    conf_min: float,
    tol: float,
    correct: str,
    sigma_px: float,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """One frame's hybrid depth. MVS pixels are kept where confident and within `tol` (relative)
    of the mono depth after it is corrected to the MVS depth; every other pixel takes the corrected
    mono depth. Correction (`correct`): `none`; `plane` -- the scale plane + shift of
    `fit_alignment`, fitted to the MVS pixels; `field` -- `plane`, then a smooth per-pixel scale
    (Gaussian-weighted mean log-ratio over MVS inliers, `sigma_px`) where MVS supports it.
    Returns `(z, mvs_inlier_mask, z_mono_corrected)`."""
    import cv2

    H, W = z_mono.shape
    valid = (z_mvs > 0) & (z_mono > 0)
    if conf is not None:
        valid &= conf >= conf_min
    zc = z_mono.copy()
    if correct != "none" and valid.sum() >= 200:
        v, u = np.nonzero(valid)
        step = max(1, len(v) // 20000)
        v, u = v[::step], u[::step]
        uvn = np.stack([2 * u / (W - 1) - 1, 2 * v / (H - 1) - 1], 1)
        params, _ = fit_alignment(z_mono[v, u], z_mvs[v, u], uvn, "plane", False)
        zc = apply_alignment(z_mono, params, "plane", False, W, H)
        zc[z_mono <= 0] = 0
    inl = valid & (np.abs(z_mvs - zc) <= tol * np.maximum(zc, 1e-9))
    if correct == "field" and inl.sum() >= 200:
        r = np.where(inl, np.log(np.maximum(z_mvs, 1e-9) / np.maximum(zc, 1e-9)), 0).astype(
            np.float32
        )
        w = inl.astype(np.float32)
        rs = cv2.GaussianBlur(r, (0, 0), sigma_px)
        ws = cv2.GaussianBlur(w, (0, 0), sigma_px)
        # blend to no correction where MVS support fades (ws is the local inlier density)
        a = np.clip(ws / 0.05, 0, 1)
        zc = np.where(z_mono > 0, zc * np.exp(a * rs / np.maximum(ws, 1e-6)), 0).astype(np.float32)
        inl = valid & (np.abs(z_mvs - zc) <= tol * np.maximum(zc, 1e-9))
    z = np.where(inl, z_mvs, zc).astype(np.float32)
    return z, inl, zc


_CLOUD_PROPS = (
    "property float32 x property float32 y property float32 z property uint8 red "
    "property uint8 green property uint8 blue property float32 nx property float32 ny "
    "property float32 nz property list uint8 uint32 view_indices "
    "property list uint8 float32 view_weights"
)


def read_mvs_cloud(path: Path) -> tuple[list[str], bytes, np.ndarray]:
    """OpenMVS `scene_dense.ply` (binary; xyz, rgb, normal, view list, weight list) ->
    `(header lines, body bytes, xyz (N,3))`. Rows are variable-length, so offsets are walked."""
    b = Path(path).read_bytes()
    end = b.index(b"end_header\n") + len(b"end_header\n")
    header = b[:end].decode().splitlines()
    props = " ".join(ln for ln in header if ln.startswith("property"))
    if props != _CLOUD_PROPS or "format binary_little_endian 1.0" not in header:
        raise ValueError(f"{path}: unexpected OpenMVS cloud layout")
    n = int(next(ln for ln in header if ln.startswith("element vertex")).split()[-1])
    body = b[end:]
    off = np.empty(n, np.int64)
    o = 0
    for k in range(n):  # ponytail: python walk, ~1 s per million points
        off[k] = o
        o += 28 + 4 * body[o + 27]
        o += 1 + 4 * body[o]
    raw = np.frombuffer(body, np.uint8)
    xyz = raw[off[:, None] + np.arange(12)].copy().view("<f4").reshape(n, 3)
    return header, body, xyz.astype(np.float64)


def fill_views(
    X: np.ndarray, frames: dict[int, tuple[np.ndarray, np.ndarray, np.ndarray]], tol: float = 0.02
) -> list[list[int]]:
    """Per point, the view IDs whose depth map sees it: projects inside the image with a depth
    within `tol` (relative) of the map there. `frames`: view ID -> (depth, K, cam-from-world)."""
    views: list[list[int]] = [[] for _ in range(len(X))]
    for vid, (z, K, T) in frames.items():
        Xc = X @ T[:3, :3].T + T[:3, 3]
        zc = Xc[:, 2]
        with np.errstate(divide="ignore", invalid="ignore"):
            u = np.round(K[0, 0] * Xc[:, 0] / zc + K[0, 2] - 0.5).astype(np.int64)
            v = np.round(K[1, 1] * Xc[:, 1] / zc + K[1, 2] - 0.5).astype(np.int64)
        H, W = z.shape
        ok = (zc > 0) & (u >= 0) & (u < W) & (v >= 0) & (v < H)
        k = np.nonzero(ok)[0]
        zm = z[v[k], u[k]]
        for j in k[(zm > 0) & (np.abs(zm - zc[k]) <= tol * zc[k])]:
            if len(views[j]) < 255:
                views[j].append(vid)
    return views


def write_union_cloud(
    path: Path,
    header: list[str],
    body: bytes,
    n_mvs: int,
    X: np.ndarray,
    N: np.ndarray,
    views: list[list[int]],
) -> None:
    """The OpenMVS cloud plus fill points `X` (grey, normals `N`, weight-1 `views`) appended as
    rows of the same layout, for `ReconstructMesh -p`."""
    import struct

    rows = [
        struct.pack("<3f3B3fB", *x, 128, 128, 128, *nrm, len(vs))
        + struct.pack(f"<{len(vs)}I", *vs)
        + struct.pack(f"<B{len(vs)}f", len(vs), *([1.0] * len(vs)))
        for x, nrm, vs in zip(X, N, views)
    ]
    hdr = [
        f"element vertex {n_mvs + len(rows)}" if ln.startswith("element vertex") else ln
        for ln in header
    ]
    Path(path).write_bytes(("\n".join(hdr) + "\n").encode() + body + b"".join(rows))


# --- TSDF extent ---------------------------------------------------------------------------------


def tsdf_params(
    depths: dict, units_per_m: float, voxel_m: float, depth_max_m: float, fill_dist_m: float
) -> tuple[float, float, float]:
    """(voxel, depth cut-off, hybrid fill distance) in SfM units. The metre defaults suit a room
    (1 cm voxels, 4 m cut-off); a drone shot of a building sees nearly everything past 4 m, so
    every pixel was cut and no TSDF block got touched. When the median depth is past the cut-off,
    raise it to 1.5x the 95th-percentile depth; the voxel (and the fill distance with it) grows
    to median depth / 1000 when that is coarser. A room keeps the metre defaults exactly."""
    z = np.concatenate([d[d > 0].ravel()[::97] for d in depths.values()] or [np.zeros(0)])
    voxel, depth_max = voxel_m * units_per_m, depth_max_m * units_per_m
    if z.size:
        med = float(np.median(z))
        if med > depth_max:  # ponytail: global cut-off; per-frame if a clip mixes close and far
            depth_max = 1.5 * float(np.percentile(z, 95))
        voxel = max(voxel, med / 1000)
    return voxel, depth_max, fill_dist_m * units_per_m * voxel / (voxel_m * units_per_m)


# open3d 0.20's CPU extract_triangle_mesh segfaults past 2**18 blocks of 8**3 voxels (an int32
# voxel index overflows): 262144 blocks extract, 262400 crash (a building at median depth / 1000)
MAX_TSDF_BLOCKS = 1 << 18


def refit_voxel(voxel: float, n_blocks: int, max_blocks: int = MAX_TSDF_BLOCKS) -> float | None:
    """None when the TSDF fits open3d's extract limit, else a coarser voxel to re-integrate
    with; a surface's block count scales with 1 / voxel**2, plus a 10% margin."""
    if n_blocks <= max_blocks:
        return None
    return voxel * 1.1 * float(np.sqrt(n_blocks / max_blocks))


def tsdf_passes(passes: list, mask: np.ndarray) -> list:
    """One frame's masked depth passes, dropping any with no pixel left: open3d aborts the whole
    TSDF ("No block is touched") on a single empty depth image. A close pass around a building
    (gt-lcc DJI_0097, median depth under the 4 m cut-off) has frames that look past it at trees
    and sky, where every pixel is beyond the cut-off."""
    out = [np.where(mask, z, 0).astype(np.float32) for z in passes]
    return [z for z in out if z.any()]


# --- masks ----------------------------------------------------------------------------------------


def geometry_mask(z: np.ndarray, K: np.ndarray, grazing_deg: float, edge_rel: float) -> np.ndarray:
    """Drop depth discontinuities (relative jump > `edge_rel` to a 4-neighbour, dilated 1 px --
    mono-depth smears edges into "flying" surfaces) and surfaces seen at more than `grazing_deg`
    from the view ray (normals from the depth map's own point map)."""
    import cv2

    H, W = z.shape
    valid = z > 0
    lz = np.log(np.where(valid, z, 1.0))
    jump = np.zeros_like(z, dtype=bool)
    dx = np.abs(np.diff(lz, axis=1)) > edge_rel
    dy = np.abs(np.diff(lz, axis=0)) > edge_rel
    jump[:, 1:] |= dx
    jump[:, :-1] |= dx
    jump[1:] |= dy
    jump[:-1] |= dy
    jump = cv2.dilate(jump.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0

    v, u = np.mgrid[0:H, 0:W].astype(np.float32)
    rays = np.stack([(u - K[0, 2]) / K[0, 0], (v - K[1, 2]) / K[1, 1], np.ones_like(u)], -1)
    P = rays * z[..., None]
    n = np.cross(np.gradient(P, axis=1), np.gradient(P, axis=0))
    cos = np.abs((n * rays).sum(-1)) / (
        np.linalg.norm(n, axis=-1) * np.linalg.norm(rays, axis=-1) + 1e-12
    )
    return valid & ~jump & (cos >= np.cos(np.radians(grazing_deg)))


# --- main ---------------------------------------------------------------------------------------


def _resize(a: np.ndarray, W: int, H: int, nearest: bool = False) -> np.ndarray:
    import cv2

    if a.shape[1] == W and a.shape[0] == H:
        return a
    interp = cv2.INTER_NEAREST if nearest else cv2.INTER_AREA
    return cv2.resize(a.astype(np.float32), (W, H), interpolation=interp)


def run(args) -> dict:
    t_all = time.time()
    timings: dict[str, float] = {}
    cam = np.load(args.cameras)
    names = [str(n) for n in cam["names"]]
    K, T_cw, wh = cam["K"], cam["T_cw"], cam["wh"]
    obs_f, obs_uv, obs_z = cam["obs_frame"], cam["obs_uv"], cam["obs_z"]
    n = len(names)
    W0, H0 = int(wh[0][0]), int(wh[0][1])
    s_fuse = min(1.0, args.fuse_long_edge / max(W0, H0))
    Wf, Hf = round(W0 * s_fuse), round(H0 * s_fuse)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    cache = Path(args.cache) / f"{args.model}_{Wf}x{Hf}"
    cache.mkdir(parents=True, exist_ok=True)
    disparity = args.model == "dav2-rel"

    # 1. depth inference (cached per model + fusion resolution)
    t0 = time.time()
    todo = [i for i in range(n) if not (cache / f"{names[i]}.npz").exists()]
    if todo:
        paths = [Path(args.images) / names[i] for i in todo]
        prompts = None
        if args.model == "promptda":  # metric prompt: SfM depth / units-per-metre
            upm = args.units_per_m or 1.0
            prompts = [
                sparse_prompt(obs_uv[obs_f == i], obs_z[obs_f == i] / upm, W0, H0) for i in todo
            ]
        frames = (
            multiview_depth_frames(args.model, paths, K[todo], T_cw[todo], args.chunk)
            if args.model in MULTIVIEW
            else depth_frames(args.model, paths, K[todo], prompts)
        )
        for i, (d, m) in zip(todo, frames):
            d = _resize(np.where(m, d, 0), Wf, Hf, nearest=True)
            np.savez(cache / f"{names[i]}.npz", d=d.astype(np.float32))
    timings["depth_infer_s"] = time.time() - t0
    timings["depth_infer_frames"] = len(todo)
    if args.depth_only:  # prefetch (GPU) while OpenMVS densifies (CPU); fusion runs later
        return {"timings_s": timings}

    # 2. per-frame robust alignment to the SfM points seen in the frame
    t0 = time.time()
    fits, depths = [], {}
    for i in range(n):
        d = np.load(cache / f"{names[i]}.npz")["d"]
        sel = obs_f == i
        uv, z = obs_uv[sel], obs_z[sel]
        px = np.clip((uv * s_fuse).astype(int), 0, [Wf - 1, Hf - 1])
        dp = d[px[:, 1], px[:, 0]]
        ok = (dp > 0) & (z > 0)
        rec = {"name": names[i], "n_pts": int(ok.sum()), "kept": False}
        if ok.sum() >= args.min_points:
            uvn = np.stack([2 * px[ok, 0] / (Wf - 1) - 1, 2 * px[ok, 1] / (Hf - 1) - 1], 1)
            method = "scale" if args.align == "global" else args.align
            params, fit = fit_alignment(dp[ok], z[ok], uvn, method, disparity)
            rel = np.abs(fit - z[ok]) / z[ok]
            rec.update(
                params=[float(p) for p in params],
                med_rel_err=float(np.median(rel)),
                inlier_frac=float((rel < 0.05).mean()),
                log_ratio=float(np.median(np.log(z[ok]) - np.log(dp[ok]))),  # scale-only fit
            )
            if rec["med_rel_err"] <= args.max_rel_err and rec["inlier_frac"] >= 0.5:
                depths[i] = (d, params, method)
                rec["kept"] = True
        fits.append(rec)
    timings["align_s"] = time.time() - t0
    kept = [f for f in fits if f["kept"]]
    if not kept:
        raise RuntimeError("depthfusion: no frame passed the alignment check")
    if args.align == "global":  # ablation: is the per-frame fit doing any work?
        s_global = np.array([float(np.median([f["params"][0] for f in kept]))])
        depths = {i: (d, s_global, m) for i, (d, _, m) in depths.items()}
    for i, (d, params, method) in depths.items():
        za = apply_alignment(d, params, method, disparity, Wf, Hf)
        za[d <= 0] = 0
        depths[i] = za
    # hybrid: OpenMVS depth where it is confident and agrees with the (corrected) mono depth
    mvs_only: dict[int, np.ndarray] = {}  # MVS-inlier-only depth, re-integrated for extra weight
    mvs_id: dict[int, int] = {}  # frame -> OpenMVS view index
    if args.mvs_depth:
        t0 = time.time()
        idx = {nm: i for i, nm in enumerate(names)}
        all_ids = np.sort(cam["image_id"]) if "image_id" in cam else np.arange(1, n + 1)
        n_mvs, inl_frac = 0, []
        for p in sorted(Path(args.mvs_depth).glob("depth*.dmap")):
            nm, zm, cf = read_dmap(p)
            i = idx.get(nm)
            if i is None:
                continue
            # dmaps are named by COLMAP image ID; cloud view indices are positions in the
            # InterfaceCOLMAP image list, which is in image-ID order
            mvs_id[i] = int(np.searchsorted(all_ids, int(p.stem[len("depth") :])))
            zm = _resize(zm, Wf, Hf, nearest=True)
            cf = None if cf is None else _resize(cf, Wf, Hf, nearest=True)
            zmono = depths.get(i, np.zeros((Hf, Wf), np.float32))
            z, inl, _ = hybrid_depth(
                zmono, zm, cf, conf_min=args.mvs_conf, tol=args.mvs_tol,
                correct=args.mvs_correct, sigma_px=args.mvs_sigma_px * Wf / 960,
            )  # fmt: skip
            if i not in depths:  # mono fit rejected the frame: keep its confident MVS only
                inl = (zm > 0) & (cf >= args.mvs_conf if cf is not None else True)
                z = np.where(inl, zm, 0).astype(np.float32)
            depths[i] = z if args.mvs_fill else np.where(inl, z, 0).astype(np.float32)
            if args.mvs_weight > 1:
                mvs_only[i] = np.where(inl, z, 0).astype(np.float32)
            n_mvs += 1
            inl_frac.append(float(inl.mean()))
        if not n_mvs:  # else the union would silently be the bare OpenMVS cloud
            raise RuntimeError(f"depthfusion: no depth*.dmap for these frames in {args.mvs_depth}")
        timings["mvs_hybrid_s"] = time.time() - t0
        timings["mvs_frames"] = n_mvs
        timings["mvs_inlier_frac_mean"] = float(np.mean(inl_frac)) if inl_frac else 0.0

    # a metric model's scale-only fit is SfM units per metre -> a free metric-scale cue
    scale_cue = None
    if args.model in ("moge2", "dav2-metric", "promptda"):
        scale_cue = float(np.exp(-np.median([f["log_ratio"] for f in kept])))

    # 3. masks + TSDF (VoxelBlockGrid, CPU, COLMAP units)
    import open3d as o3d
    import open3d.core as o3c

    t0 = time.time()
    units_per_m = args.units_per_m or (1.0 / scale_cue if scale_cue else None)
    if units_per_m is None:
        raise RuntimeError("depthfusion: no metric scale (pass --units-per-m)")
    voxel, depth_max, fill_dist = tsdf_params(
        depths, units_per_m, args.voxel_m, args.depth_max_m, args.fill_dist_m
    )
    trunc_mult = float(args.trunc_voxels)
    Kf = K.copy()
    Kf[:, :2] *= s_fuse
    while True:
        vbg = o3d.t.geometry.VoxelBlockGrid(
            attr_names=("tsdf", "weight"),
            attr_dtypes=(o3c.float32, o3c.float32),
            attr_channels=((1), (1)),
            voxel_size=voxel,
            block_resolution=8,
            block_count=args.block_count,
            device=o3c.Device("CPU:0"),
        )
        masked_frac = []
        for i, z in depths.items():
            m = geometry_mask(z, Kf[i], args.grazing_deg, args.edge_rel) & (z < depth_max)
            masked_frac.append(1 - m.mean())
            intr = o3c.Tensor(Kf[i].astype(np.float64))
            extr = o3c.Tensor(T_cw[i].astype(np.float64))
            # ponytail: MVS weight by re-integrating its pixels (weight-1 steps); a custom weighted
            # integrate (open3d's integrate_custom example) if a fractional weight is ever needed
            passes = [z] + [mvs_only[i]] * (args.mvs_weight - 1) if i in mvs_only else [z]
            for zz in tsdf_passes(passes, m):
                depth = o3d.t.geometry.Image(o3c.Tensor(np.ascontiguousarray(zz)))
                bc = vbg.compute_unique_block_coordinates(
                    depth, intr, extr, 1.0, depth_max, trunc_mult
                )
                vbg.integrate(bc, depth, intr, extr, 1.0, depth_max, trunc_mult)
        if vbg.hashmap().size() == 0:
            raise RuntimeError(f"depthfusion: no depth left under the {depth_max:.3g} cut-off")
        coarser = refit_voxel(voxel, vbg.hashmap().size())
        if coarser is None:
            break
        timings["tsdf_refits"] = timings.get("tsdf_refits", 0) + 1
        fill_dist *= coarser / voxel
        voxel = coarser
    timings["tsdf_integrate_s"] = time.time() - t0

    t0 = time.time()
    mesh = vbg.extract_triangle_mesh(weight_threshold=args.min_weight).to_legacy()
    tri_clusters, cluster_n, _ = mesh.cluster_connected_triangles()
    tri_clusters, cluster_n = np.asarray(tri_clusters), np.asarray(cluster_n)
    mesh.remove_triangles_by_mask(cluster_n[tri_clusters] < args.min_component_tris)
    mesh.remove_unreferenced_vertices()
    mesh.compute_vertex_normals()
    out_mesh = out_dir / "mesh.ply"
    o3d.io.write_triangle_mesh(str(out_mesh), mesh)
    timings["extract_mesh_s"] = time.time() - t0

    if args.mvs_cloud:  # union cloud for ReconstructMesh: MVS points + TSDF vertices in its holes
        from scipy.spatial import cKDTree

        t0 = time.time()
        header, body, xyz = read_mvs_cloud(Path(args.mvs_cloud))
        V = np.asarray(mesh.vertices)
        dist, _ = cKDTree(xyz).query(V, distance_upper_bound=fill_dist)
        keep = ~np.isfinite(dist)
        views = fill_views(V[keep], {mvs_id[i]: (depths[i], Kf[i], T_cw[i]) for i in mvs_id})
        seen = np.array([len(v) > 0 for v in views], bool)
        write_union_cloud(
            out_dir / "union.ply", header, body, len(xyz), V[keep][seen],
            np.asarray(mesh.vertex_normals)[keep][seen], [v for v in views if v],
        )  # fmt: skip
        timings["union_s"] = time.time() - t0
        timings["union_fill_points"] = int(seen.sum())
        timings["union_mvs_points"] = len(xyz)
    timings["worker_total_s"] = time.time() - t_all

    stats = {
        "model": args.model,
        "align": args.align,
        "voxel_m": args.voxel_m,
        "voxel_units": voxel,
        "depth_max_units": depth_max,
        "units_per_m": units_per_m,
        "scale_cue_m_per_unit": scale_cue,
        "frames": n,
        "frames_kept": len(kept),
        "fuse_wh": [Wf, Hf],
        "med_rel_err_kept": float(np.median([f["med_rel_err"] for f in kept])),
        "masked_frac_mean": float(np.mean(masked_frac)),
        "triangles": len(mesh.triangles),
        "vertices": len(mesh.vertices),
        "timings_s": timings,
        "args": vars(args),
        "per_frame": fits,
    }
    (out_dir / "stats.json").write_text(json.dumps(stats, indent=1))
    return stats


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--cameras", required=True, help="cameras.npz from pipeline.depthfusion")
    p.add_argument("--images", required=True, help="undistorted images dir (COLMAP dense/images)")
    p.add_argument("--out", required=True)
    p.add_argument("--cache", required=True, help="depth prediction cache dir")
    p.add_argument("--model", choices=MODELS, default="moge2")
    p.add_argument("--align", choices=ALIGNS, default="plane")
    p.add_argument("--units-per-m", type=float, default=None, help="default: from the depth fit")
    p.add_argument("--voxel-m", type=float, default=0.01)
    p.add_argument("--trunc-voxels", type=float, default=4.0)
    p.add_argument("--depth-max-m", type=float, default=4.0)
    p.add_argument("--fuse-long-edge", type=int, default=960)
    p.add_argument("--min-points", type=int, default=20)
    p.add_argument("--max-rel-err", type=float, default=0.08)
    p.add_argument("--grazing-deg", type=float, default=80.0)
    p.add_argument("--edge-rel", type=float, default=0.04)
    p.add_argument("--min-weight", type=float, default=2.0)
    p.add_argument("--min-component-tris", type=int, default=500)
    p.add_argument("--block-count", type=int, default=200_000)
    p.add_argument("--chunk", type=int, default=40, help="frames per multi-view model pass")
    p.add_argument("--depth-only", action="store_true", help="fill the depth cache and exit")
    h = p.add_argument_group("hybrid (--mvs-depth: OpenMVS depth maps + mono fill)")
    h.add_argument("--mvs-depth", default=None, help="dir with OpenMVS depth*.dmap")
    h.add_argument("--mvs-conf", type=float, default=0.0, help="min OpenMVS confidence")
    h.add_argument("--mvs-tol", type=float, default=0.03, help="max |mvs-mono|/mono to keep MVS")
    h.add_argument("--mvs-correct", choices=("none", "plane", "field"), default="plane")
    h.add_argument("--mvs-sigma-px", type=float, default=40.0, help="field blur at 960 px wide")
    h.add_argument("--mvs-weight", type=int, default=1, help="TSDF weight of MVS pixels")
    h.add_argument("--mvs-fill", type=int, default=1, help="0: fuse the MVS inliers only")
    h.add_argument(
        "--mvs-cloud",
        default=None,
        help="OpenMVS scene_dense.ply: also write union.ply (it + TSDF vertices farther than "
        "--fill-dist-m from it) for ReconstructMesh",
    )
    h.add_argument("--fill-dist-m", type=float, default=0.02)
    return p


if __name__ == "__main__":
    stats = run(build_parser().parse_args())
    print(json.dumps({k: v for k, v in stats.items() if k != "per_frame"}, indent=1))
