"""Metric scale from a printed ArUco reference seen in the drone footage (plan §2.1, §2.3) --
`calibrate.py`'s `_METHOD_RANK` puts `"scalebar_aruco"` first: it's direct metric ground truth,
unlike GPS (~1-3 m accurate) or a metric-depth model (only "approximately metric").

Two printable references, same detect/triangulate/scale pipeline underneath:
  - `BoardSpec`: a grid of markers on one rigid sheet (more redundancy: many known inter-corner
    distances from one placement).
  - `ScaleBarSpec`: two markers a known centre-to-centre distance apart (simpler to carry/print --
    "a plain printed 1 m scale bar", plan §2.1).

Dictionary: `cv2.aruco.DICT_4X4_50`, not 5X5/6X6. A marker's *decodability* needs every inner grid
cell to span a few pixels, and 4X4 has the fewest cells to resolve at a given printed size and
distance: 4+2*border = 6x6 = 36 cells, vs 5X5's 7x7=49 or 6X6's 8x8=64 -- fewer, bigger cells at
the same footage. 50 IDs is far more than the <=12 markers any board here needs, so the smaller
dictionary's weaker inter-marker Hamming distance (more false-positive risk in a cluttered scene)
doesn't cost anything: the board is the only ArUco-like thing in frame.

Default board size -- pixels-per-marker math (why 0.30 m, not smaller): `pipeline.frames`/`cli.py`
extract at 1920 px wide (`--long-edge` default) regardless of the DJI Mini 4K's native 4K sensor,
so that's the resolution `detect()` actually sees. The Mini 4K's ~82 deg FOV gives a ground-plane
width at distance `d` (shooting roughly along the ground, plan §2.1's 30-45 deg gimbal tilt) of
`~2*d*tan(41deg) = 1.74*d` metres, so:
    m/px = 1.74*d / 1920
    marker_px = marker_side_m / (m/px) = marker_side_m * 1920 / (1.74*d)
    d=10m: 0.0090 m/px -> a 0.30 m marker is ~33 px (comfortable)
    d=20m: 0.0181 m/px -> ~17 px (borderline -- matches the brief's own worked example)
    d=30m: 0.0272 m/px -> ~11 px (too small)
A 4x4 marker needs its 6x6 cell grid to resolve at all -- in practice that needs a marker side of
>=~20 px (roughly 3 px/cell after H.264/JPEG blur) to decode, and >=~40 px for the sub-pixel corner
accuracy `triangulate_corners` wants for a clean scale fit. `MIN_MARKER_PX`/`TARGET_MARKER_PX`
below encode that floor/target. Net: default marker side 0.30 m, and **the board pass should stay
inside ~15-20 m** -- comfortably inside this project's actual capture plan (§2.1: 15 m wide orbit,
6 m close pass) even though the brief's nominal range is 10-30 m. A single marker big enough to
read at a true 30 m (>=~0.7 m side) stops being a "carry it in a backpack" board.

Default board: `BoardSpec()` -- DICT_4X4_50, 3 cols x 2 rows (6 markers, more redundancy than a
2x3), 0.30 m markers, 0.07 m gaps -> 1.04 x 0.67 m sheet (the "1 m board" the brief asks for).
"""

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from pipeline.calibrate import ScaleEstimate, reprojection_error_px, triangulate_dlt
from pipeline.package import Camera

MIN_MARKER_PX = 20  # below this, DICT_4X4's 6x6 cell grid stops decoding reliably
TARGET_MARKER_PX = 40  # below this, corners are detected but sub-pixel refinement gets noisy

_MM_PER_M = 1000.0
_IN_PER_M = 1.0 / 0.0254


@dataclass
class BoardSpec:
    """A rigid grid of ArUco markers on one printed sheet."""

    dictionary_id: int = cv2.aruco.DICT_4X4_50
    cols: int = 3
    rows: int = 2
    marker_length_m: float = 0.30
    gap_m: float = 0.07
    name: str = "aruco-board"

    def dictionary(self) -> cv2.aruco.Dictionary:
        return cv2.aruco.getPredefinedDictionary(self.dictionary_id)

    def cv2_board(self) -> cv2.aruco.GridBoard:
        return cv2.aruco.GridBoard(
            (self.cols, self.rows), self.marker_length_m, self.gap_m, self.dictionary()
        )

    def layout_points(self) -> dict[int, np.ndarray]:
        """`{marker_id: (4,2) XY metres in the board plane}`, corner order (top-left, top-right,
        bottom-right, bottom-left) matching `cv2.aruco`'s detected-corner order -- reuses
        `GridBoard.getObjPoints()` (already exactly this layout) rather than re-deriving it."""
        board = self.cv2_board()
        ids = board.getIds().ravel()
        return {int(i): np.asarray(p)[:, :2] for i, p in zip(ids, board.getObjPoints())}

    @property
    def width_m(self) -> float:
        return self.cols * self.marker_length_m + (self.cols - 1) * self.gap_m

    @property
    def height_m(self) -> float:
        return self.rows * self.marker_length_m + (self.rows - 1) * self.gap_m


@dataclass
class ScaleBarSpec:
    """Two ArUco markers a known centre-to-centre distance apart -- "a plain printed 1 m scale
    bar" (plan §2.1): easier to carry/print than a full board, at the cost of fewer known
    distances (one board placement gives 1, this gives 1 too, but the board's redundant corners
    make its scale fit far more robust to a single bad detection)."""

    dictionary_id: int = cv2.aruco.DICT_4X4_50
    marker_length_m: float = 0.20
    center_distance_m: float = 1.0
    marker_ids: tuple[int, int] = (0, 1)
    name: str = "aruco-scalebar"

    def dictionary(self) -> cv2.aruco.Dictionary:
        return cv2.aruco.getPredefinedDictionary(self.dictionary_id)

    def layout_points(self) -> dict[int, np.ndarray]:
        half = self.marker_length_m / 2.0
        centers = {self.marker_ids[0]: 0.0, self.marker_ids[1]: self.center_distance_m}
        return {
            mid: np.array(
                [[cx - half, half], [cx + half, half], [cx + half, -half], [cx - half, -half]]
            )
            for mid, cx in centers.items()
        }


Spec = BoardSpec | ScaleBarSpec


# --- printable artifacts -------------------------------------------------------------------


def make_board_image(spec: Spec, dpi: int = 300) -> Image.Image:
    """Print-ready RGB image at true physical scale (`dpi` controls only pixel density, not
    physical size): the board/scale-bar itself, a ruler line along the bottom with 0.1 m ticks,
    and exact-size text -- so the capture team can measure the print with a real tape and catch a
    printer's silent "fit to page" rescale before flying (the single most common way a scale
    reference becomes worthless)."""
    m_to_px = dpi * _IN_PER_M
    margin_m = 0.05
    ruler_h_m = 0.09
    text_h_m = 0.14

    if isinstance(spec, BoardSpec):
        content_w_m, content_h_m = spec.width_m, spec.height_m
        ruler_len_m = content_w_m
        marker_line = f"{spec.cols}x{spec.rows} markers, {spec.marker_length_m * _MM_PER_M:.0f} mm side, {spec.gap_m * _MM_PER_M:.0f} mm gap"
    else:
        content_w_m = spec.center_distance_m + spec.marker_length_m
        content_h_m = spec.marker_length_m
        ruler_len_m = spec.center_distance_m
        marker_line = f"2 markers, {spec.marker_length_m * _MM_PER_M:.0f} mm side, {ruler_len_m:.3f} m centre-to-centre"
    warn_line = (
        f"ruler above must measure exactly {ruler_len_m:.3f} m printed -- do not scale to fit page"
    )

    font = ImageFont.load_default(size=round(0.04 * m_to_px))
    text_w_px = max(
        ImageDraw.Draw(Image.new("RGB", (1, 1))).textlength(line, font=font)
        for line in (spec.name, marker_line, warn_line)
    )

    canvas_w_m = max(content_w_m, text_w_px / m_to_px) + 2 * margin_m
    canvas_h_m = content_h_m + 2 * margin_m + ruler_h_m + text_h_m
    canvas = Image.new("RGB", (round(canvas_w_m * m_to_px), round(canvas_h_m * m_to_px)), "white")
    draw = ImageDraw.Draw(canvas)

    content_x0 = round(margin_m * m_to_px)
    content_y0 = round(margin_m * m_to_px)
    if isinstance(spec, BoardSpec):
        board_img = spec.cv2_board().generateImage(
            (round(content_w_m * m_to_px), round(content_h_m * m_to_px)), marginSize=0, borderBits=1
        )
        canvas.paste(Image.fromarray(board_img).convert("RGB"), (content_x0, content_y0))
    else:
        side_px = round(spec.marker_length_m * m_to_px)
        for i, marker_id in enumerate(spec.marker_ids):
            marker_img = cv2.aruco.generateImageMarker(
                spec.dictionary(), marker_id, side_px, borderBits=1
            )
            x = content_x0 + round(i * spec.center_distance_m * m_to_px)
            canvas.paste(Image.fromarray(marker_img).convert("RGB"), (x, content_y0))

    ruler_y = content_y0 + round(content_h_m * m_to_px) + round(0.02 * m_to_px)
    _draw_ruler(draw, content_x0, ruler_y, ruler_len_m, m_to_px)

    text_y = ruler_y + round(ruler_h_m * m_to_px)
    draw.text((content_x0, text_y), spec.name, fill="black", font=font)
    draw.text((content_x0, text_y + round(0.045 * m_to_px)), marker_line, fill="black", font=font)
    draw.text((content_x0, text_y + round(0.09 * m_to_px)), warn_line, fill="black", font=font)
    return canvas


def _draw_ruler(
    draw: ImageDraw.ImageDraw, x0: int, y0: int, length_m: float, m_to_px: float
) -> None:
    x1 = x0 + round(length_m * m_to_px)
    draw.line([(x0, y0), (x1, y0)], fill="black", width=max(1, round(0.002 * m_to_px)))
    n_ticks = round(length_m / 0.1) + 1
    for i in range(n_ticks):
        x = x0 + round(i * 0.1 * m_to_px)
        draw.line([(x, y0), (x, y0 + round(0.03 * m_to_px))], fill="black", width=1)


# --- tiling for a printer that only takes Letter/A4 -----------------------------------------

_PAGE_SIZES_IN = {"letter": (8.5, 11.0), "a4": (8.27, 11.69)}
_TILE_MARGIN_IN = 0.3  # unprintable border most home/office printers refuse to print into
_TILE_OVERLAP_IN = 0.4  # tape/trim allowance; registration crosshairs land in this strip


def tile_for_printing(image: Image.Image, dpi: int, page: str = "letter") -> list[Image.Image]:
    """Split a `make_board_image` page into `page`-sized tiles (`"letter"`/`"a4"`) with a
    registration crosshair stamped at the same absolute position on every tile that covers it (so
    overlapping edges line up when taped) and a corner label ("tile 1/6 (row 1, col 2)")."""
    page_w_in, page_h_in = _PAGE_SIZES_IN[page]
    tile_w = round((page_w_in - 2 * _TILE_MARGIN_IN) * dpi)
    tile_h = round((page_h_in - 2 * _TILE_MARGIN_IN) * dpi)
    step_x = tile_w - round(_TILE_OVERLAP_IN * dpi)
    step_y = tile_h - round(_TILE_OVERLAP_IN * dpi)

    stamped = image.convert("RGB").copy()
    _stamp_registration_marks(stamped, step_x, step_y)

    img_w, img_h = stamped.size
    cols = max(1, -(-max(img_w - tile_w, 0) // step_x) + 1)
    rows = max(1, -(-max(img_h - tile_h, 0) // step_y) + 1)

    font = ImageFont.load_default(size=round(0.18 * dpi))
    tiles = []
    for r in range(rows):
        for c in range(cols):
            x0, y0 = c * step_x, r * step_y
            crop = stamped.crop((x0, y0, x0 + tile_w, y0 + tile_h))
            if crop.size != (tile_w, tile_h):  # ran past the image edge -- pad with white
                padded = Image.new("RGB", (tile_w, tile_h), "white")
                padded.paste(crop, (0, 0))
                crop = padded
            ImageDraw.Draw(crop).text(
                (round(0.05 * dpi), round(0.05 * dpi)),
                f"tile {r * cols + c + 1}/{rows * cols} (row {r + 1}, col {c + 1}) -- "
                f"align + marks to adjacent sheet, tape, trim outer margin",
                fill="red",
                font=font,
            )
            tiles.append(crop)
    return tiles


def _stamp_registration_marks(image: Image.Image, step_x: int, step_y: int) -> None:
    draw = ImageDraw.Draw(image)
    size = round(image.width * 0.006) + 4
    for x in range(0, image.width + 1, step_x):
        for y in range(0, image.height + 1, step_y):
            draw.line([(x - size, y), (x + size, y)], fill="red", width=2)
            draw.line([(x, y - size), (x, y + size)], fill="red", width=2)


# --- detection -------------------------------------------------------------------------------


def detect(frame_paths: list[Path], spec: Spec) -> dict[str, dict[int, np.ndarray]]:
    """`{image_name: {marker_id: (4,2) pixel corners}}` for every marker found, image-file basename
    keyed (matches `Camera.file`'s basename). Corner order is `cv2.aruco`'s own (top-left,
    top-right, bottom-right, bottom-left), sub-pixel refined (`CORNER_REFINE_SUBPIX`, built into
    `ArucoDetector` -- no separate `cv2.cornerSubPix` pass needed)."""
    params = cv2.aruco.DetectorParameters()
    params.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    detector = cv2.aruco.ArucoDetector(spec.dictionary(), params)

    detections: dict[str, dict[int, np.ndarray]] = {}
    for frame_path in frame_paths:
        frame_path = Path(frame_path)
        img = cv2.imread(str(frame_path), cv2.IMREAD_GRAYSCALE)
        if img is None:
            raise ValueError(f"detect: could not read image {frame_path}")
        corners, ids, _rejected = detector.detectMarkers(img)
        if ids is None:
            continue
        detections[frame_path.name] = {
            int(marker_id): c.reshape(4, 2) for c, marker_id in zip(corners, ids.ravel())
        }
    return detections


# --- triangulation -----------------------------------------------------------------------------

_MAX_REPROJECTION_ERROR_PX = 5.0  # board corners are flat, well-textured, sub-pixel refined; a
# real corner should reproject to a couple of px at most, several px means a bad/mismatched
# detection in one view


def _projection_matrix(cam: Camera) -> np.ndarray:
    K = np.array([[cam.fx, 0.0, cam.cx], [0.0, cam.fy, cam.cy], [0.0, 0.0, 1.0]])
    Rt = np.hstack([np.asarray(cam.R), np.asarray(cam.t).reshape(3, 1)])
    return K @ Rt


def triangulate_corners(
    detections: dict[str, dict[int, np.ndarray]],
    cameras: dict[str, Camera],
    max_reprojection_error_px: float = _MAX_REPROJECTION_ERROR_PX,
) -> dict[tuple[int, int], np.ndarray]:
    """`{(marker_id, corner_idx): xyz}` in the reconstruction's (unscaled) world frame -- DLT
    triangulation of every corner seen in `cameras` from >=2 views, dropping any whose
    triangulated point reprojects with mean error above `max_reprojection_error_px`."""
    tracks: dict[tuple[int, int], list[tuple[np.ndarray, np.ndarray]]] = {}
    for image_name, markers in detections.items():
        cam = cameras.get(image_name)
        if cam is None:
            continue
        P = _projection_matrix(cam)
        for marker_id, corners in markers.items():
            for corner_idx, uv in enumerate(corners):
                tracks.setdefault((marker_id, corner_idx), []).append((P, uv))

    points3d = {}
    for key, views in tracks.items():
        if len(views) < 2:
            continue
        X = triangulate_dlt(views)
        if reprojection_error_px(X, views) <= max_reprojection_error_px:
            points3d[key] = X
    return points3d


# --- scale -----------------------------------------------------------------------------------

_MIN_PAIRS = 4  # below this, a median ratio is too easily one bad pair away from garbage


def scale_from_board(
    points3d: dict[tuple[int, int], np.ndarray],
    spec: Spec,
    num_views: int | None = None,
) -> ScaleEstimate:
    """Robust scale from every reconstructed-vs-known inter-corner distance (every pair of known
    board-layout corners that both triangulated -- within a marker that's the 4 sides + 2
    diagonals, across markers it's every known layout distance, exactly the brief's "compare all
    reconstructed inter-corner distances ... with the true ones"). `scale = true_dist /
    recon_dist`'s robust (median) value; `residual_m` = RMS of `(scale * recon_dist - true_dist)`
    over the same pairs. Also fits the board plane (SVD normal of the reconstructed corner cloud)
    -- `ScaleEstimate.board_normal`.

    Raises `ValueError` (not a silent `None`) when too few corners/views triangulated to say
    anything about scale -- `estimate_board_scale` is the `| None`-returning, "maybe there's no
    board in this scene" wrapper around this.
    """
    layout = spec.layout_points()
    known = {
        (marker_id, ci): np.array([xy[0], xy[1], 0.0])
        for marker_id, corners in layout.items()
        for ci, xy in enumerate(corners)
    }
    keys = [k for k in known if k in points3d]
    if len(keys) < 3:
        raise ValueError(
            f"scale_from_board: only {len(keys)} known corners triangulated (need >=3) -- "
            "too few views saw the board, or none matched the given spec's marker ids"
        )

    true_d, recon_d = [], []
    for i in range(len(keys)):
        for j in range(i + 1, len(keys)):
            td = float(np.linalg.norm(known[keys[i]] - known[keys[j]]))
            rd = float(np.linalg.norm(points3d[keys[i]] - points3d[keys[j]]))
            if td > 1e-6 and rd > 1e-9:
                true_d.append(td)
                recon_d.append(rd)
    if len(true_d) < _MIN_PAIRS:
        raise ValueError(
            f"scale_from_board: only {len(true_d)} usable corner-pair distances (need "
            f">={_MIN_PAIRS})"
        )
    true_d, recon_d = np.array(true_d), np.array(recon_d)

    scale = float(np.median(true_d / recon_d))
    residual_m = float(np.sqrt(np.mean((scale * recon_d - true_d) ** 2)))

    pts = np.array([points3d[k] for k in keys])
    _, _, vt = np.linalg.svd(pts - pts.mean(axis=0))
    board_normal = vt[-1] / np.linalg.norm(vt[-1])

    views_note = f"{num_views} views, " if num_views is not None else ""
    return ScaleEstimate(
        method="scalebar_aruco",
        scale=scale,
        residual_m=residual_m,
        notes=f"{views_note}{len(keys)} corners, {len(true_d)} pairwise distances, median ratio",
        board_normal=board_normal,
    )


def estimate_board_scale(
    frames_dir: Path, cameras: list[Camera], spec: Spec | None = None
) -> ScaleEstimate | None:
    """One-shot entry point for `run.py`: detect `spec` (default `BoardSpec()`) in every frame
    under `frames_dir` that has a pose in `cameras`, triangulate, and return the scale -- or
    `None` if the board wasn't seen in enough views (no board placed, or it fell out of frame),
    rather than raising, since a missing board is an expected, non-fatal outcome for the caller
    (fall through to the next-best `calibrate.choose_scale` method)."""
    spec = spec or BoardSpec()
    by_name = {Path(c.file).name: c for c in cameras}
    frame_paths = [p for p in Path(frames_dir).iterdir() if p.name in by_name]
    if len(frame_paths) < 2:
        return None

    detections = detect(frame_paths, spec)
    if len(detections) < 2:
        return None

    points3d = triangulate_corners(detections, by_name)
    try:
        return scale_from_board(points3d, spec, num_views=len(detections))
    except ValueError:
        return None
