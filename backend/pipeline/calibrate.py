"""Scale, up, and north calibration (plan §2.3, "the part judges should hear about").

A reconstruction from video alone (COLMAP/GLOMAP/VGGT/...) is unitless and arbitrarily oriented.
Everything here turns that into metres, +Y-up, glTF-convention world coordinates, and -- just as
important for a demo -- a residual number honest about how good the fit actually is.
"""

import json
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

import numpy as np

if TYPE_CHECKING:
    from pipeline.package import Camera  # typing only -- package.py imports this module already,
    # a runtime import here would be circular


def umeyama(
    src: np.ndarray, dst: np.ndarray, with_scale: bool = True
) -> tuple[float, np.ndarray, np.ndarray, float]:
    """Umeyama (1991) least-squares similarity fit: `dst ~= s * R @ src + t` over N point pairs.

    SVD-based, and always returns a proper rotation (det +1) even when the best unconstrained fit
    would be a reflection (det -1) -- a mirrored camera path is physically meaningless, so the
    classic Umeyama correction (flip the last singular vector when det(U)*det(V) < 0) is applied
    unconditionally. Returns `(s, R, t, rms_residual)`; residual is in `dst`'s units.
    """
    src = np.asarray(src, dtype=np.float64)
    dst = np.asarray(dst, dtype=np.float64)
    n, dim = src.shape
    mu_src, mu_dst = src.mean(axis=0), dst.mean(axis=0)
    src_c, dst_c = src - mu_src, dst - mu_dst

    cov = (dst_c.T @ src_c) / n
    u, sing, vt = np.linalg.svd(cov)
    sign = np.eye(dim)
    if np.linalg.det(u) * np.linalg.det(vt) < 0:
        sign[-1, -1] = -1
    R = u @ sign @ vt

    if with_scale:
        var_src = (src_c**2).sum() / n
        s = float(np.trace(np.diag(sing) @ sign) / var_src)
    else:
        s = 1.0
    t = mu_dst - s * R @ mu_src

    fitted = s * (R @ src.T).T + t
    rms_residual = float(np.sqrt(np.mean(np.sum((dst - fitted) ** 2, axis=1))))
    return s, R, t, rms_residual


def scale_from_known_distance(p_a: np.ndarray, p_b: np.ndarray, true_len_m: float) -> float:
    """Ratio of a real, hand-measured length to the same two points' distance in the (unitless)
    reconstruction -- multiply reconstruction distances by this to get metres."""
    recon_len = float(
        np.linalg.norm(np.asarray(p_b, dtype=np.float64) - np.asarray(p_a, dtype=np.float64))
    )
    if recon_len <= 0:
        raise ValueError("scale_from_known_distance: p_a and p_b coincide in the reconstruction")
    return true_len_m / recon_len


def triangulate_dlt(views: list[tuple[np.ndarray, np.ndarray]]) -> np.ndarray:
    """Linear multi-view DLT triangulation: `views` is `[(P, (u, v)), ...]`, `P` each view's 3x4
    camera projection matrix (`K @ [R|t]`). Stacks the 2 homogeneous constraint rows per view
    (standard two-view DLT, https://en.wikipedia.org/wiki/Direct_linear_transformation) and takes
    the least-squares null vector -- the natural N-view generalisation, not a pairwise
    triangulation averaged afterwards. Shared by `pipeline.aruco` (board corners) and
    `known_distance_scale` below (a hand-picked pair of points, >=2 frames)."""
    rows = []
    for P, (u, v) in views:
        rows.append(u * P[2] - P[0])
        rows.append(v * P[2] - P[1])
    _, _, vt = np.linalg.svd(np.asarray(rows))
    X = vt[-1]
    return X[:3] / X[3]


def reprojection_error_px(X: np.ndarray, views: list[tuple[np.ndarray, np.ndarray]]) -> float:
    """Mean pixel reprojection error of a triangulated point `X` back into each view's `(P, uv)` --
    the same sanity check `pipeline.aruco.triangulate_corners` uses, shared here for
    `known_distance_scale`."""
    errs = []
    for P, uv in views:
        x = P @ np.append(X, 1.0)
        errs.append(float(np.linalg.norm(x[:2] / x[2] - uv)))
    return float(np.mean(errs)) if errs else float("nan")


def _camera_projection_matrix(cam: "Camera") -> np.ndarray:
    K = np.array([[cam.fx, 0.0, cam.cx], [0.0, cam.fy, cam.cy], [0.0, 0.0, 1.0]])
    Rt = np.hstack([np.asarray(cam.R), np.asarray(cam.t).reshape(3, 1)])
    return K @ Rt


def known_distance_scale(observations_path: Path, cameras: dict[str, "Camera"]) -> "ScaleEstimate":
    """Metric scale from a hand-measured distance between two points, each clicked in >=2 frames
    (a two-point stand-in for `pipeline.aruco`'s board, for whoever didn't bring a printed board).

    `observations_path` JSON:
    ```json
    {
      "length_m": 1.52,
      "observations": [
        {"frame": "0012.jpg", "a": [x, y], "b": [x, y]},
        {"frame": "0045.jpg", "a": [x, y], "b": [x, y]}
      ]
    }
    ```
    `frame` must match a registered frame's basename (`cameras`, keyed the same way `run.py` keys
    its raw per-frame poses -- pixel coords in *that* frame's own resolution, i.e. the frames SfM
    actually ran on, not a full-resolution re-extraction). `a`/`b` are the same two physical points
    in every observation, in pixel coordinates. Points `a` and `b` are each triangulated (DLT,
    `triangulate_dlt`) from every observation whose frame is in `cameras`, then
    `scale_from_known_distance` turns their reconstructed (unitless) distance into a scale.

    Raises `ValueError` if fewer than 2 observations reference a registered frame -- triangulation
    needs >=2 views.
    """
    data = json.loads(Path(observations_path).read_text())
    length_m = float(data["length_m"])
    obs = [o for o in data["observations"] if o["frame"] in cameras]
    if len(obs) < 2:
        raise ValueError(
            f"known_distance_scale: only {len(obs)} observation(s) reference a registered frame "
            "(need >=2 to triangulate)"
        )

    views_a, views_b = [], []
    for o in obs:
        P = _camera_projection_matrix(cameras[o["frame"]])
        views_a.append((P, np.asarray(o["a"], dtype=np.float64)))
        views_b.append((P, np.asarray(o["b"], dtype=np.float64)))
    p_a, p_b = triangulate_dlt(views_a), triangulate_dlt(views_b)

    scale = scale_from_known_distance(p_a, p_b, length_m)
    err_a, err_b = reprojection_error_px(p_a, views_a), reprojection_error_px(p_b, views_b)
    return ScaleEstimate(
        method="known_dimension",
        scale=scale,
        residual_m=None,  # a single measured length has no independent redundancy to check it
        # against -- reprojection error is the closest available honesty check, reported in notes
        notes=(
            f"known-distance JSON: {len(obs)} observations, length_m={length_m}, mean "
            f"reprojection error a={err_a:.2f}px b={err_b:.2f}px"
        ),
    )


# Umeyama's similarity fit has 7 DOF (3 rotation + 3 translation + 1 scale) -- with fewer points
# than this the fit is degenerate/ill-conditioned even though it technically runs. Matches
# `pipeline.run._align_full_to_preview`'s own ">=3 for a stable fit" threshold for a rigid fit.
_MIN_UMEYAMA_POINTS = 3


def scale_from_gps(
    cam_centers: np.ndarray, enu: np.ndarray
) -> tuple[float, np.ndarray, np.ndarray, float]:
    """Umeyama fit of the reconstruction's camera path onto its GPS-derived local ENU track
    (`telemetry.to_enu`, resampled to the same frames as `cam_centers`) -- scale, up and north
    all come out of one fit. GPS is only accurate to ~1-3 m, so the residual (4th element) must
    always be reported alongside the scale (plan §2.3).

    Rows with a non-finite `enu` or `cam_centers` value (no GPS fix that frame, `telemetry.
    to_enu`'s NaN passthrough) are dropped before fitting -- tolerates missing GPS rather than
    poisoning the whole fit with NaN. Raises `ValueError` if fewer than `_MIN_UMEYAMA_POINTS`
    rows survive; callers should check there are enough finite fixes *before* calling this (e.g.
    fall back to `up_from_cameras`) so this is a defensive backstop, not the normal path."""
    cam_centers = np.asarray(cam_centers, dtype=np.float64)
    enu = np.asarray(enu, dtype=np.float64)
    finite = np.all(np.isfinite(cam_centers), axis=1) & np.all(np.isfinite(enu), axis=1)
    if finite.sum() < _MIN_UMEYAMA_POINTS:
        raise ValueError(
            f"scale_from_gps: only {int(finite.sum())} frame(s) have a finite GPS-derived ENU "
            f"position (need >={_MIN_UMEYAMA_POINTS} for a stable similarity fit)"
        )
    return umeyama(cam_centers[finite], enu[finite], with_scale=True)


# 0.1m is the single-line OSD SRT format's own quantisation step for H (assignment: "1 Hz samples
# and 0.1 m quantisation" -- don't overclaim accuracy from it). Rounding-to-0.1m noise has std
# ~= 0.1/sqrt(12) ~= 0.029m; require the observed range of H to be several multiples of that
# (0.3m, ~10x) before trusting a scale fit against it -- a near-hover clip's H barely moves, and a
# fit off pure quantisation noise would be meaningless.
_MIN_H_RANGE_M = 0.3
# Minimum R^2 of the centred fit (H explained by camera height). DJI_0095 gives 0.96 on both the
# 165-frame SfM path and the 48-frame VGGT preview; a fit below this means the up axis or the
# poses don't track H (bad up estimate, drifting poses) and the scale would be a guess.
_MIN_ALTITUDE_R2 = 0.8


def scale_from_caption_altitude(
    up_cam: np.ndarray,
    h_caption_m: np.ndarray,
    min_range_m: float = _MIN_H_RANGE_M,
    min_r2: float = _MIN_ALTITUDE_R2,
) -> "ScaleEstimate | None":
    """Caption-based scale candidate (assignment task 2, "altitude") for footage with no GPS fix:
    fits `s` minimising `sum((s*du - dh)**2)` where `du`/`dh` are `up_cam`/`h_caption_m` centred on
    their own means (removes each series' arbitrary/unrelated additive offset -- the
    reconstruction's up-axis origin is arbitrary, and the caption's `H` origin is "home point", not
    necessarily 0 at frame 0) -- the classic least-squares scale-only fit once both series are
    centred. `up_cam` is each frame's camera centre projected onto the reconstruction's up axis
    (reconstruction units); `h_caption_m` is the caption's `H` (relative altitude, metres)
    resampled at the same frame timestamps. Both may contain NaN (no `H` reading, e.g. GPS-derived
    row without one -- shouldn't happen for `H` but kept NaN-safe for symmetry with `to_enu`).

    Returns `None` (never raises) if fewer than 3 finite pairs remain, if `H`'s range is below
    `min_range_m`, or if the fit explains less than `min_r2` of H's variance -- all "not enough
    signal to trust a fit" cases, not errors."""
    up_cam = np.asarray(up_cam, dtype=np.float64)
    h = np.asarray(h_caption_m, dtype=np.float64)
    finite = np.isfinite(up_cam) & np.isfinite(h)
    if finite.sum() < _MIN_UMEYAMA_POINTS:
        return None
    up_cam, h = up_cam[finite], h[finite]
    h_range = float(h.max() - h.min())
    if h_range < min_range_m:
        return None  # near-constant H (e.g. a hover) -- can't distinguish signal from quantisation
    du = up_cam - up_cam.mean()
    dh = h - h.mean()
    denom = float(np.sum(du * du))
    if denom <= 0:
        return None
    s = float(np.sum(du * dh) / denom)
    residual_m = float(np.sqrt(np.mean((s * du - dh) ** 2)))
    r2 = 1.0 - float(np.sum((s * du - dh) ** 2)) / float(np.sum(dh * dh))
    if r2 < min_r2:
        return None
    return ScaleEstimate(
        method="altitude",
        scale=s,
        residual_m=residual_m,
        notes=(
            f"camera-up-axis fit to caption H: range={h_range:.2f}m, R2={r2:.3f} over "
            f"{int(finite.sum())} frames (H is 0.1m quantised, 1Hz -- don't overclaim accuracy)"
        ),
    )


def scale_from_caption_odometry(
    cam_times_s: np.ndarray,
    cam_positions: np.ndarray,
    up: np.ndarray,
    caption_times_s: np.ndarray,
    caption_h_speed_mps: np.ndarray,
    reference_scale: float | None = None,
) -> "ScaleEstimate | None":
    """Caption-based scale candidate (assignment task 2, "odometry"), a noisy cross-check rather
    than a primary estimate: integrates the caption's `H.S` (horizontal speed) over time to get a
    metric horizontal path length, and compares it with the reconstruction's own horizontal camera
    path length (camera centres projected off the `up` axis, summed consecutive-frame distances)
    over the same time span -- `scale = caption_length_m / recon_length_units`. Indoors, `H.S`
    comes from vision positioning (noisy, drifts) rather than GPS, hence "cross-check", not ground
    truth; `reference_scale` (typically the "altitude" estimate's scale), if given, is folded into
    `notes` as an agreement check, not into the returned scale itself.

    `cam_times_s`/`cam_positions` are the reconstruction's own (unresampled) frame timestamps and
    camera centres; `caption_times_s`/`caption_h_speed_mps` are the *raw* caption samples (its own
    ~1Hz grid, not resampled to frame times -- trapezoidal integration wants the real sample
    spacing). Returns `None` (never raises) if there's too little data to integrate or the
    reconstruction path is degenerate (a static/near-static camera can't calibrate an odometry
    scale)."""
    cam_times_s = np.asarray(cam_times_s, dtype=np.float64)
    cam_positions = np.asarray(cam_positions, dtype=np.float64)
    if len(cam_times_s) < 2:
        return None
    up = np.asarray(up, dtype=np.float64)
    up = up / np.linalg.norm(up)

    caption_times_s = np.asarray(caption_times_s, dtype=np.float64)
    caption_h_speed_mps = np.asarray(caption_h_speed_mps, dtype=np.float64)
    finite = np.isfinite(caption_times_s) & np.isfinite(caption_h_speed_mps)
    if finite.sum() < 2:
        return None
    ct, cs = caption_times_s[finite], caption_h_speed_mps[finite]
    order = np.argsort(ct)
    ct, cs = ct[order], cs[order]

    # restrict the caption integration to the reconstruction's own time span, so both path
    # lengths cover the same stretch of flight
    t_lo, t_hi = float(cam_times_s.min()), float(cam_times_s.max())
    span_mask = (ct >= t_lo) & (ct <= t_hi)
    if span_mask.sum() < 2:
        return None
    ct, cs = ct[span_mask], cs[span_mask]
    caption_length_m = float(np.trapezoid(cs, ct))

    cam_order = np.argsort(cam_times_s)
    positions_sorted = cam_positions[cam_order]
    horiz = positions_sorted - np.outer(positions_sorted @ up, up)  # drop the up component
    recon_length = float(np.sum(np.linalg.norm(np.diff(horiz, axis=0), axis=1)))
    if recon_length <= 0 or caption_length_m <= 0:
        return None

    scale = caption_length_m / recon_length
    notes = (
        f"caption horizontal path {caption_length_m:.2f}m (integral of H.S, {int(span_mask.sum())} "
        "samples, ~1Hz, noisy indoors -- vision positioning, not GPS) vs reconstruction horizontal "
        f"path {recon_length:.2f} (unitless) over the same time span"
    )
    if reference_scale:
        recon_length_m = recon_length * reference_scale
        diff_pct = abs(recon_length_m - caption_length_m) / caption_length_m * 100.0
        notes += (
            f"; at the altitude estimate's scale ({reference_scale:.4g}) that reconstruction path "
            f"is {recon_length_m:.2f}m, {diff_pct:.0f}% from the caption's {caption_length_m:.2f}m"
        )
    return ScaleEstimate(method="odometry", scale=scale, residual_m=None, notes=notes)


def up_from_cameras(cam_to_world_rotations: np.ndarray) -> np.ndarray:
    """Fallback "up" when there's no GPS fit: drone footage keeps camera roll ~0, so the camera's
    local x-axis (right, OpenCV convention x=right/y=down/z=forward) stays horizontal all flight
    regardless of pitch or yaw. World "up" is then the one direction orthogonal to every camera's
    right vector -- the smallest-singular-vector of the stacked right vectors (an SVD null-space
    fit, robust to per-frame noise unlike averaging cross products). Sign is disambiguated with
    the camera's down (y) axis: the mean down vector should point mostly opposite "up".

    `cam_to_world_rotations`: (N,3,3), each column i is camera axis i expressed in world coords.
    """
    R = np.asarray(cam_to_world_rotations, dtype=np.float64)
    right_axes = R[:, :, 0]  # camera x (right) in world coords, one row per camera
    down_axes = R[:, :, 1]  # camera y (down) in world coords

    _, _, vt = np.linalg.svd(right_axes)
    up = vt[-1]
    if np.mean(down_axes @ up) > 0:  # down should mostly oppose up; flip if it doesn't
        up = -up
    return up / np.linalg.norm(up)


def similarity_matrix(s: float, R: np.ndarray, t: np.ndarray) -> np.ndarray:
    """4x4 homogeneous matrix for `x -> s * R @ x + t`."""
    M = np.eye(4)
    M[:3, :3] = s * np.asarray(R, dtype=np.float64)
    M[:3, 3] = np.asarray(t, dtype=np.float64)
    return M


def apply_similarity(points: np.ndarray, M: np.ndarray) -> np.ndarray:
    """Apply a 4x4 similarity matrix (`similarity_matrix`) to (N,3) points."""
    points = np.asarray(points, dtype=np.float64)
    return (M[:3, :3] @ points.T).T + M[:3, 3]


def transform_camera(
    R_wc: np.ndarray, t_wc: np.ndarray, M: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    """Update a world-to-camera pose (`x_cam = R_wc @ x_world + t_wc`) for the same physical
    camera after the world has been re-expressed via `M = similarity_matrix(s, R_sim, t_sim)`.

    Scale drops out of the projection (only x/z, y/z ratios matter for a pinhole camera), so the
    same point's camera-frame coordinates simply scale by `s` too. Substituting `x_world_old =
    (1/s) R_sim^T (x_world_new - t_sim)` into the old pose equation and collecting terms gives
    `R_wc_new = R_wc_old @ R_sim^T` (a rotation composition -- exactly orthonormal, no drift) and
    `t_wc_new = s * t_wc_old - R_wc_new @ t_sim`. `s` is recovered from `M` as `det(M[:3,:3])**(1/3)`
    (valid since `det(s*R) = s**3` for a proper 3x3 rotation `R`).
    """
    s = float(np.linalg.det(M[:3, :3])) ** (1 / 3)
    R_sim = M[:3, :3] / s
    t_sim = M[:3, 3]
    R_wc_new = np.asarray(R_wc, dtype=np.float64) @ R_sim.T
    t_wc_new = s * np.asarray(t_wc, dtype=np.float64) - R_wc_new @ t_sim
    return R_wc_new, t_wc_new


def align_to_gltf(up_vec: np.ndarray, north_vec: np.ndarray | None = None) -> np.ndarray:
    """Rotation mapping `up_vec -> +Y` and, if given, `north_vec -> -Z` (glTF/Unity convention:
    right-handed, +Y up, -Z forward/"into the screen" -- so facing north means facing -Z).

    Without a north reference, falls back to a fixed world axis (global X, or global Z if that's
    nearly parallel to `up_vec`) projected onto the horizontal plane, so the heading is arbitrary
    but always the same for the same `up_vec` (deterministic, not "whatever the solver felt").
    """
    y = np.asarray(up_vec, dtype=np.float64)
    y = y / np.linalg.norm(y)

    ref = (
        np.asarray(north_vec, dtype=np.float64)
        if north_vec is not None
        else np.array([1.0, 0.0, 0.0])
    )
    horiz = ref - np.dot(ref, y) * y
    if np.linalg.norm(horiz) < 1e-6:  # ref (north or the default) is ~parallel to up
        ref = np.array([0.0, 0.0, 1.0])
        horiz = ref - np.dot(ref, y) * y
    z = -horiz / np.linalg.norm(horiz)  # north (horiz) maps to -Z
    x = np.cross(y, z)
    return np.stack([x, y, z], axis=0)


@dataclass
class ScaleEstimate:
    # "scalebar_aruco" | "known_dimension" | "gps" | "altitude" | "odometry" | "metric_depth" |
    # "none" -- see _METHOD_RANK below for the evidence ordering.
    method: str
    scale: float
    residual_m: float | None
    notes: str = ""
    # World-frame (unscaled reconstruction) unit normal of the ArUco board/scale-bar plane, only
    # set by "scalebar_aruco" (pipeline.aruco.scale_from_board) -- if the board was laid flat on
    # the ground during capture, this is a second, independent "up" estimate for free.
    board_normal: np.ndarray | None = None


# Best evidence first. ArUco/scale-bar and a hand-measured known dimension are direct metric
# ground truth; GPS is ~1-3 m accurate (plan §2.3); "altitude"/"odometry" (assignment task 2) are
# caption-only fallbacks for footage with no GPS fix -- 1Hz, 0.1m-quantised, and (for odometry)
# vision-positioning-derived indoors, so ranked below GPS but still real telemetry, above a bare
# metric-depth guess; odometry ranked below altitude since H.S is noisier than H indoors (assignment:
# "treat it as a cross-check candidate"); metric-depth models are approximately metric at best;
# "none" means no calibration ran at all (scale stays 1.0, everything downstream is a guess).
_METHOD_RANK = [
    "scalebar_aruco",
    "known_dimension",
    "gps",
    "altitude",
    "odometry",
    "metric_depth",
    "none",
]


def choose_scale(estimates: list[ScaleEstimate]) -> ScaleEstimate:
    """Pick the best-evidence estimate by `_METHOD_RANK`. `scene.json`'s `scale_method` and
    `scale_residual_m` come straight from the result."""
    if not estimates:
        raise ValueError("choose_scale: no scale estimates given")
    return min(
        estimates,
        key=lambda e: (
            _METHOD_RANK.index(e.method) if e.method in _METHOD_RANK else len(_METHOD_RANK)
        ),
    )


# --- preview <-> full rigid alignment (plan §1a) ----------------------------------------------


def interpolate_positions_at_times(
    times_known: np.ndarray, positions_known: np.ndarray, times_query: np.ndarray
) -> np.ndarray:
    """Piecewise-linear interpolation of `(N,3)` positions at new times, one axis at a time
    (`numpy.interp`, same building block `telemetry.resample` uses) -- `rigid_align_cameras`'s
    "interpolate if timestamps differ" (plan §1a: the full stage's cameras and the preview's don't
    generally land on the exact same video timestamps). Times outside `times_known`'s range clamp
    to the nearest endpoint (`numpy.interp`'s default)."""
    times_known = np.asarray(times_known, dtype=np.float64)
    order = np.argsort(times_known)
    times_known = times_known[order]
    positions_known = np.asarray(positions_known, dtype=np.float64)[order]
    times_query = np.asarray(times_query, dtype=np.float64)
    return np.stack(
        [np.interp(times_query, times_known, positions_known[:, i]) for i in range(3)], axis=1
    )


def rigid_align_points(src: np.ndarray, dst: np.ndarray) -> tuple[np.ndarray, np.ndarray, float]:
    """Rotation + translation only (no scale) least-squares fit: `dst ~= R @ src + t`. Thin
    wrapper over `umeyama(..., with_scale=False)` returning just `(R, t, rms_residual)` -- plan
    §1a's "rigid-align (rotation + translation, Umeyama without scale)" of the full revision's
    camera centres onto the preview's."""
    _, R, t, residual = umeyama(src, dst, with_scale=False)
    return R, t, residual


def scale_agreement(preview_scale: float, full_scale: float, threshold_pct: float = 2.0) -> dict:
    """Plan §1a: "if the two scale estimates differ by more than 2%[...]". The full revision's own
    scale is always what actually gets applied (Stage B never adopts the preview's scale -- rigid
    alignment has no scale term by construction, see `rigid_align_points`) -- this is purely the
    honesty check on how much the two independent calibrations agreed, for `scale_candidates`/logs."""
    if preview_scale == 0:
        diff_pct = float("inf")
    else:
        diff_pct = abs(full_scale - preview_scale) / abs(preview_scale) * 100.0
    return {
        "preview_scale": preview_scale,
        "full_scale": full_scale,
        "diff_pct": diff_pct,
        "agrees": diff_pct <= threshold_pct,
    }
