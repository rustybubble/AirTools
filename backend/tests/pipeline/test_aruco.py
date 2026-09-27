import numpy as np
import pytest

pytest.importorskip("cv2")  # pipeline.aruco needs the `pipeline` extra (opencv-python-headless)

import cv2

from pipeline.aruco import (
    BoardSpec,
    ScaleBarSpec,
    detect,
    estimate_board_scale,
    make_board_image,
    scale_from_board,
    tile_for_printing,
    triangulate_corners,
)
from pipeline.package import Camera

# --- synthetic camera rig ------------------------------------------------------------------

_W, _H = 1280, 960
_K = np.array([[1500.0, 0.0, _W / 2], [0.0, 1500.0, _H / 2], [0.0, 0.0, 1.0]])


def _camera_pose(pos: np.ndarray, target: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """World-to-camera (R, t) for a camera at `pos` looking at `target`, roll 0 (world Z as the
    stable "up" reference for the right vector -- board lies flat in the world Z=0 plane)."""
    forward = target - pos
    forward = forward / np.linalg.norm(forward)
    right = np.cross(forward, np.array([0.0, 0.0, 1.0]))
    right = right / np.linalg.norm(right)
    down = np.cross(forward, right)
    R_cw = np.stack([right, down, forward], axis=1)  # camera-to-world, columns = camera axes
    R_wc = R_cw.T
    t_wc = -R_wc @ pos
    return R_wc, t_wc


def _orbit_cameras(target: np.ndarray, n: int, radius: float, height: float):
    for a in np.linspace(0, 2 * np.pi, n, endpoint=False):
        pos = target + np.array([radius * np.cos(a), radius * np.sin(a), height])
        yield _camera_pose(pos, target)


def _signed_area(quad: np.ndarray) -> float:
    x, y = quad[:, 0], quad[:, 1]
    return 0.5 * float(np.sum(x * np.roll(y, -1) - np.roll(x, -1) * y))


def _render_views(spec, frames_dir, n_views=8, radius=3.2, height=2.0, true_scale=1.0, blur=0.6):
    """Render `n_views` synthetic photos of `spec`'s markers, laid flat in the world Z=0 plane at
    their true metric layout (scaled by `true_scale`, simulating a physical board printed/placed
    at other than its nominal size -- 1.0 unless a test says otherwise), as seen by an orbiting
    camera rig. Returns `{image_name: Camera}` with the *rendering* (true, metric) pose -- callers
    that want an "unscaled SfM reconstruction" scale `.t` themselves afterwards (pixels, and thus
    detections, are unaffected by a pure world scale -- the classic SfM scale ambiguity).

    A camera only ever sees a *non-mirrored* rendering of a planar marker if the marker's own
    corner winding (as embedded into the world plane) and `_camera_pose`'s right/down convention
    agree -- `BoardSpec` (inherited from `cv2.aruco.GridBoard.getObjPoints`) and `ScaleBarSpec`
    (this file's own layout) don't happen to wind the same way, so the world Y axis is flipped
    here, per spec, to always match: get this wrong and every marker still looks like a plausible
    square in the render, but its bit pattern is a mirror image and never decodes (confirmed by
    hand: `cv2.aruco.detectMarkers` found the right outer squares but rejected all of them)."""
    layout = spec.layout_points()
    ids = list(layout.keys())
    y_sign = -1.0 if _signed_area(layout[ids[0]]) > 0 else 1.0
    layout = {mid: quad * np.array([1.0, y_sign]) for mid, quad in layout.items()}
    textures = {
        mid: cv2.aruco.generateImageMarker(spec.dictionary(), mid, 300, borderBits=1) for mid in ids
    }
    tex_corners = np.array([[0, 0], [300, 0], [300, 300], [0, 300]], dtype=np.float32)

    all_xy = np.concatenate(list(layout.values()), axis=0) * true_scale
    center = np.array([*all_xy.mean(axis=0), 0.0])

    rng = np.random.default_rng(0)
    cameras = {}
    for i, (R_wc, t_wc) in enumerate(_orbit_cameras(center, n_views, radius, height)):
        name = f"{i:02d}.png"
        dst = rng.integers(60, 196, size=(_H, _W), dtype=np.uint8)  # noisy background
        for mid in ids:
            # project each of the marker's 4 known 3D corners straight to pixels (not via a
            # composed metres-to-pixels homography matrix -- K's ~1e3 entries next to a metres-
            # scale ~1e-3 intermediate homography is so ill-conditioned that warpPerspective's
            # internal float32 path silently corrupts the warp; a fresh getPerspectiveTransform
            # fit purely in pixel space per marker has none of that dynamic-range problem).
            world_xy = layout[mid] * true_scale
            dst_quad = np.array(
                [
                    ((_K @ (R_wc[:, :2] @ xy + t_wc))[:2] / (R_wc[:, :2] @ xy + t_wc)[2])
                    for xy in world_xy
                ],
                dtype=np.float32,
            )
            H = cv2.getPerspectiveTransform(tex_corners, dst_quad)
            warped = cv2.warpPerspective(textures[mid], H, (_W, _H), borderValue=255)
            mask = cv2.warpPerspective(
                np.full((300, 300), 255, np.uint8), H, (_W, _H), borderValue=0
            )
            dst = np.where(mask > 127, warped, dst)
        dst = cv2.GaussianBlur(dst, (3, 3), blur)
        cv2.imwrite(str(frames_dir / name), dst)
        cameras[name] = Camera(
            id=name,
            file=name,
            thumb="",
            R=R_wc,
            t=t_wc,
            fx=1500.0,
            fy=1500.0,
            cx=_W / 2,
            cy=_H / 2,
            w=_W,
            h=_H,
        )
    return cameras


# --- board / scale-bar recovery, end to end -------------------------------------------------


@pytest.mark.parametrize("recon_scale", [0.137])
def test_board_scale_recovered_from_unscaled_reconstruction(tmp_path, recon_scale):
    spec = BoardSpec()
    cameras = _render_views(spec, tmp_path, n_views=8)
    # simulate an unscaled SfM reconstruction: rotations are invariant to a pure world scale,
    # translations (and thus positions) scale by `recon_scale` -- pixels/detections don't change.
    unscaled = {
        name: Camera(
            id=c.id,
            file=c.file,
            thumb="",
            R=c.R,
            t=c.t * recon_scale,
            fx=c.fx,
            fy=c.fy,
            cx=c.cx,
            cy=c.cy,
            w=c.w,
            h=c.h,
        )
        for name, c in cameras.items()
    }

    result = estimate_board_scale(tmp_path, list(unscaled.values()), spec)

    assert result is not None
    assert result.method == "scalebar_aruco"
    assert result.scale == pytest.approx(1.0 / recon_scale, rel=0.005)
    assert result.residual_m < 0.005
    assert result.board_normal is not None
    # board is flat in world Z=0 -- normal should be ~world Z regardless of sign
    assert abs(abs(result.board_normal[2]) - 1.0) < 0.02


def test_scalebar_variant_recovers_scale(tmp_path):
    spec = ScaleBarSpec()
    cameras = _render_views(spec, tmp_path, n_views=6, radius=2.0, height=1.3)
    recon_scale = 2.7
    unscaled = {
        name: Camera(
            id=c.id,
            file=c.file,
            thumb="",
            R=c.R,
            t=c.t * recon_scale,
            fx=c.fx,
            fy=c.fy,
            cx=c.cx,
            cy=c.cy,
            w=c.w,
            h=c.h,
        )
        for name, c in cameras.items()
    }

    result = estimate_board_scale(tmp_path, list(unscaled.values()), spec)

    assert result is not None
    assert result.scale == pytest.approx(1.0 / recon_scale, rel=0.005)
    assert result.residual_m < 0.005


def test_too_few_views_returns_none_from_estimate_board_scale(tmp_path):
    spec = BoardSpec()
    cameras = _render_views(spec, tmp_path, n_views=1)  # a single photo can't triangulate anything

    result = estimate_board_scale(tmp_path, list(cameras.values()), spec)

    assert result is None


def test_too_few_corners_raises_clearly_from_scale_from_board():
    with pytest.raises(ValueError, match="too few"):
        scale_from_board({}, BoardSpec())


def test_detect_finds_all_markers_in_every_view(tmp_path):
    spec = BoardSpec()
    cameras = _render_views(spec, tmp_path, n_views=4)

    detections = detect([tmp_path / name for name in cameras], spec)

    assert set(detections.keys()) == set(cameras.keys())
    for markers in detections.values():
        assert set(markers.keys()) == set(spec.layout_points().keys())
        for corners in markers.values():
            assert corners.shape == (4, 2)


# --- triangulate_corners: exact math, no image rendering ------------------------------------


def test_triangulate_corners_recovers_exact_points_from_clean_projections():
    true_points = {
        (0, 0): np.array([0.1, 0.2, 1.0]),
        (0, 1): np.array([-0.1, 0.15, 1.2]),
        (0, 2): np.array([0.0, -0.1, 0.9]),
    }
    target = np.array([0.0, 0.0, 1.0])
    cams = list(_orbit_cameras(target, n=5, radius=2.0, height=0.0))

    cameras = {}
    detections = {}
    for i, (R, t) in enumerate(cams):
        name = f"{i}.png"
        cameras[name] = Camera(
            id=name,
            file=name,
            thumb="",
            R=R,
            t=t,
            fx=1500.0,
            fy=1500.0,
            cx=640.0,
            cy=480.0,
            w=1280,
            h=960,
        )
        # project each of the 3 "corners" (reusing corner_idx 0/1/2 as 3 independent test points)
        pts = np.zeros((3, 2))
        for (mid, cidx), X in true_points.items():
            x_cam = R @ X + t
            pts[cidx] = (_K @ x_cam)[:2] / x_cam[2]
        detections[name] = {0: pts}

    points3d = triangulate_corners(detections, cameras)

    assert set(points3d.keys()) == {(0, 0), (0, 1), (0, 2)}
    for key, X in true_points.items():
        assert points3d[key] == pytest.approx(X, abs=1e-6)


# --- printable artifacts --------------------------------------------------------------------


def test_make_board_image_is_true_metric_size_at_given_dpi():
    spec = BoardSpec()
    dpi = 150
    image = make_board_image(spec, dpi=dpi)
    m_to_px = dpi / 0.0254
    # canvas must be at least the board's true footprint (plus margins/ruler/text) at this dpi
    assert image.width >= round(spec.width_m * m_to_px)
    assert image.height >= round(spec.height_m * m_to_px)


def test_tile_for_printing_covers_the_whole_master_image():
    spec = BoardSpec()
    image = make_board_image(spec, dpi=100)
    tiles = tile_for_printing(image, dpi=100, page="letter")
    assert len(tiles) >= 1
    assert all(t.size == tiles[0].size for t in tiles)  # every page the same physical size


def test_triangulate_corners_drops_a_single_view_corner():
    target = np.array([0.0, 0.0, 1.0])
    R0, t0 = next(iter(_orbit_cameras(target, n=1, radius=2.0, height=0.0)))
    cam0 = Camera(
        id="0",
        file="0.png",
        thumb="",
        R=R0,
        t=t0,
        fx=1500.0,
        fy=1500.0,
        cx=640.0,
        cy=480.0,
        w=1280,
        h=960,
    )
    detections = {"0.png": {0: np.zeros((1, 2))}}

    points3d = triangulate_corners(detections, {"0.png": cam0})

    assert points3d == {}
