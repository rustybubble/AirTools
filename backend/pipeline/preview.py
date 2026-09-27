"""Headless textured-mesh preview renderer (Part 1 objective QA): render `mesh.glb` from a
`cameras.json` camera (same intrinsics/pose) to an RGB image. No GPU/display needed -- pure numpy.

Rasterization is Monte-Carlo surface splatting + z-buffer, not scanline/per-pixel barycentric:
for each triangle, sample points at random barycentric coordinates *on the 3D triangle* (count
scaled by the triangle's screen-space pixel area), project every sampled 3D point through the
pinhole camera model individually, then resolve all samples across all triangles in one shot via
a depth sort + last-write-wins fancy-index scatter (numpy's documented behaviour for `a[idx] = v`
with duplicate indices: later entries win, so sorting farthest-to-nearest and writing in that
order does the z-test for free, no explicit compare-and-swap loop needed). Because each sample is
an exact point on the mesh surface, projecting it already gives the correct pixel/depth/UV --
unlike screen-space affine interpolation, this needs no separate perspective-correction step.
Fully vectorised: no per-triangle Python loop, so it comfortably handles ~200k triangles at
960x540 well under 30s (see tests/pipeline/test_preview.py's timing assertion).
"""

from pathlib import Path

import numpy as np
import trimesh

from pipeline.package import Camera

_SAMPLE_DENSITY = 3.0  # Monte-Carlo samples per covered screen pixel of a triangle's area
_MIN_SAMPLES_PER_TRI = 3
# A real reconstructed mesh mixes many small triangles over the subject with a handful of huge
# ones (background/ground-plane geometry the crop missed) -- confirmed on real package data: a few
# outlier triangles' *raw* area so dominated the naive sample total that the global rescale below
# starved every small triangle, rendering the actual subject as sparse confetti instead of a solid
# surface. Capping the area used *for sampling purposes* (not the triangle itself) means one huge
# background triangle can look a bit sparse without stealing density from the small triangles that
# matter -- 4000px is already a big single-triangle footprint at 960x540 (many times any subject
# triangle in a ~200k-tri mesh).
_MAX_TRIANGLE_AREA_FOR_SAMPLING_PX = 4000.0
# Overall sample budget (bounds memory/time for pathological meshes with *many* huge triangles) --
# for the ~200k-triangle/2-3px-avg meshes this pipeline actually produces, the naive total stays
# well under this and no rescale happens at all.
_MAX_TOTAL_SAMPLES = 8_000_000


def _project(points_cam: np.ndarray, fx: float, fy: float, cx: float, cy: float):
    """Pinhole-project camera-space points `(N, 3)` -> pixel xy `(N, 2)`, depth `(N,)`."""
    z = points_cam[:, 2]
    x = fx * points_cam[:, 0] / z + cx
    y = fy * points_cam[:, 1] / z + cy
    return np.stack([x, y], axis=1), z


def _texture_image(mesh: trimesh.Trimesh) -> np.ndarray:
    material = getattr(mesh.visual, "material", None)
    image = getattr(material, "baseColorTexture", None) if material is not None else None
    if image is None:
        raise ValueError("preview.render: mesh has no baseColorTexture to sample")
    return np.asarray(image.convert("RGB"))


def _sample_texture(image: np.ndarray, uv: np.ndarray) -> np.ndarray:
    """Nearest-neighbour texture lookup. trimesh keeps UVs in OpenGL convention after loading a
    glb (it flips V on import): v=1 is the image's top row, so row = (1 - v) * h. Pinned by
    test_sample_texture_matches_trimesh_to_color."""
    h, w = image.shape[:2]
    u = np.mod(uv[:, 0], 1.0)
    v = np.mod(uv[:, 1], 1.0)
    col = np.clip((u * w).astype(np.int64), 0, w - 1)
    row = np.clip(((1.0 - v) * h).astype(np.int64), 0, h - 1)
    return image[row, col]


def _fill_small_holes(out: np.ndarray, covered: np.ndarray, min_neighbors: int = 5) -> None:
    """Patch stray uncovered pixels (Monte-Carlo sampling variance, not real gaps) from covered
    8-neighbours -- keeps the surface looking solid instead of dotted. In-place. Fixed 8-shift
    cost regardless of image size, not a per-pixel Python loop."""
    h, w, _ = out.shape
    padded = np.pad(out.astype(np.int32), ((1, 1), (1, 1), (0, 0)))
    padded_cov = np.pad(covered, ((1, 1), (1, 1)))
    acc = np.zeros((h, w, 3), dtype=np.int32)
    count = np.zeros((h, w), dtype=np.int32)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dy == 0 and dx == 0:
                continue
            shifted = padded[1 + dy : 1 + dy + h, 1 + dx : 1 + dx + w]
            shifted_cov = padded_cov[1 + dy : 1 + dy + h, 1 + dx : 1 + dx + w]
            acc += shifted * shifted_cov[..., None]
            count += shifted_cov
    fillable = (~covered) & (count >= min_neighbors)
    if fillable.any():
        out[fillable] = (acc[fillable] // count[fillable, None]).astype(np.uint8)
        covered[fillable] = True


def _fill_see_through(
    out: np.ndarray, depth: np.ndarray, rel: float = 0.05, min_front: int = 6
) -> None:
    """Replace pixels where only a *hidden* surface got a sample (the random sampling missed the
    front surface there, so a surface behind it shows through as a speckle) with the mean of their
    clearly-nearer 8-neighbours. Needs `min_front` of 8 neighbours nearer by > `rel`, so a real
    silhouette edge (at most ~5 nearer neighbours) is left alone. In-place; `depth` is inf where
    uncovered."""
    h, w, _ = out.shape
    pad_d = np.pad(depth, 1, constant_values=np.inf)
    pad_c = np.pad(out.astype(np.int32), ((1, 1), (1, 1), (0, 0)))
    acc = np.zeros((h, w, 3), dtype=np.int32)
    count = np.zeros((h, w), dtype=np.int32)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dy == 0 and dx == 0:
                continue
            nd = pad_d[1 + dy : 1 + dy + h, 1 + dx : 1 + dx + w]
            front = nd < depth * (1.0 - rel)
            acc += pad_c[1 + dy : 1 + dy + h, 1 + dx : 1 + dx + w] * front[..., None]
            count += front
    fix = np.isfinite(depth) & (count >= min_front)
    if fix.any():
        out[fix] = (acc[fix] // count[fix, None]).astype(np.uint8)


def render(
    mesh: Path | trimesh.Trimesh,
    camera: Camera,
    width: int = 960,
    height: int = 540,
) -> tuple[np.ndarray, np.ndarray]:
    """Render `mesh`'s baseColor texture from `camera`'s pose (OpenCV world-to-camera `R`/`t`,
    `package.Camera`'s convention), intrinsics scaled from `camera.w/h` to `width x height`.
    `mesh` may be a path to a textured glb or an already-loaded `trimesh.Trimesh` (pass a loaded
    mesh when rendering many cameras against the same package, e.g. from `qa.photo_consistency`,
    to avoid re-loading the glb/texture per camera).

    Returns `(image, covered)`: `image` is `(height, width, 3)` uint8 RGB (uncovered background
    pixels are black), `covered` is a `(height, width)` bool mask of pixels the mesh reached.
    """
    if isinstance(mesh, (str, Path)):
        mesh = trimesh.load(mesh, force="scene", process=False).to_geometry()
    image = _texture_image(mesh)

    sx, sy = width / camera.w, height / camera.h
    fx, fy = camera.fx * sx, camera.fy * sy
    cx, cy = camera.cx * sx, camera.cy * sy

    R, t = np.asarray(camera.R, dtype=np.float64), np.asarray(camera.t, dtype=np.float64)
    verts_cam = (R @ mesh.vertices.T).T + t  # world -> camera, OpenCV convention
    faces = mesh.faces
    uv = mesh.visual.uv

    v0, v1, v2 = verts_cam[faces[:, 0]], verts_cam[faces[:, 1]], verts_cam[faces[:, 2]]
    uv0, uv1, uv2 = uv[faces[:, 0]], uv[faces[:, 1]], uv[faces[:, 2]]

    # a triangle with any vertex behind the camera projects undefined -- drop it (no near-plane
    # clipping; fine for QA renders where the whole subject is meant to be in front of the camera)
    in_front = (v0[:, 2] > 1e-6) & (v1[:, 2] > 1e-6) & (v2[:, 2] > 1e-6)
    if not in_front.any():
        return np.zeros((height, width, 3), dtype=np.uint8), np.zeros((height, width), dtype=bool)
    v0, v1, v2 = v0[in_front], v1[in_front], v2[in_front]
    uv0, uv1, uv2 = uv0[in_front], uv1[in_front], uv2[in_front]

    p0, _ = _project(v0, fx, fy, cx, cy)
    p1, _ = _project(v1, fx, fy, cx, cy)
    p2, _ = _project(v2, fx, fy, cx, cy)
    # frustum cull: a triangle wholly off one side of the screen gets no samples. Without this a
    # complete room (walls beside/behind the view) spends the _MAX_TOTAL_SAMPLES budget off-screen
    # and the visible surface renders as dots (kitchen depthfusion mesh: 22% of pixels unsampled).
    xs = np.stack([p0[:, 0], p1[:, 0], p2[:, 0]], 1)
    ys = np.stack([p0[:, 1], p1[:, 1], p2[:, 1]], 1)
    vis = (xs.max(1) >= 0) & (xs.min(1) < width) & (ys.max(1) >= 0) & (ys.min(1) < height)
    v0, v1, v2, uv0, uv1, uv2 = v0[vis], v1[vis], v2[vis], uv0[vis], uv1[vis], uv2[vis]
    p0, p1, p2 = p0[vis], p1[vis], p2[vis]
    if not vis.any():
        return np.zeros((height, width, 3), dtype=np.uint8), np.zeros((height, width), dtype=bool)
    area_px = 0.5 * np.abs(
        (p1[:, 0] - p0[:, 0]) * (p2[:, 1] - p0[:, 1])
        - (p2[:, 0] - p0[:, 0]) * (p1[:, 1] - p0[:, 1])
    )
    # ponytail: random (not stratified) barycentric sampling, density heuristic not physically
    # derived -- can leave a rare sub-pixel hole on a near-degenerate triangle; _fill_small_holes
    # papers over it. Upgrade to stratified sampling (or real scanline rasterization) if renders
    # ever show visible dotting after that pass.
    area_for_sampling = np.minimum(area_px, _MAX_TRIANGLE_AREA_FOR_SAMPLING_PX)
    samples_per_tri = np.maximum(
        np.ceil(area_for_sampling * _SAMPLE_DENSITY).astype(np.int64), _MIN_SAMPLES_PER_TRI
    )
    total_naive = int(samples_per_tri.sum())
    if total_naive > _MAX_TOTAL_SAMPLES:
        scale = _MAX_TOTAL_SAMPLES / total_naive
        samples_per_tri = np.maximum((samples_per_tri * scale).astype(np.int64), 1)

    tri_idx = np.repeat(np.arange(len(v0)), samples_per_tri)
    total = tri_idx.shape[0]
    rng = np.random.default_rng(0)  # deterministic renders -- QA scores must be repeatable
    r1, r2 = rng.random(total), rng.random(total)
    flip = r1 + r2 > 1.0
    r1[flip], r2[flip] = 1.0 - r1[flip], 1.0 - r2[flip]
    w0, w1, w2 = (1.0 - r1 - r2)[:, None], r1[:, None], r2[:, None]  # barycentric, on the 3D tri

    pts = w0 * v0[tri_idx] + w1 * v1[tri_idx] + w2 * v2[tri_idx]
    sample_uv = w0 * uv0[tri_idx] + w1 * uv1[tri_idx] + w2 * uv2[tri_idx]
    px, depth = _project(pts, fx, fy, cx, cy)

    xi = np.floor(px[:, 0]).astype(np.int64)
    yi = np.floor(px[:, 1]).astype(np.int64)
    onscreen = (xi >= 0) & (xi < width) & (yi >= 0) & (yi < height) & (depth > 1e-6)
    xi, yi, depth, sample_uv = xi[onscreen], yi[onscreen], depth[onscreen], sample_uv[onscreen]

    if xi.size == 0:
        return np.zeros((height, width, 3), dtype=np.uint8), np.zeros((height, width), dtype=bool)

    colors = _sample_texture(image, sample_uv)

    order = np.argsort(-depth)  # farthest first -> last (nearest) write wins per pixel
    pixel_idx = yi[order] * width + xi[order]

    out_flat = np.zeros((height * width, 3), dtype=np.uint8)
    covered_flat = np.zeros(height * width, dtype=bool)
    out_flat[pixel_idx] = colors[order]
    covered_flat[pixel_idx] = True
    depth_flat = np.full(height * width, np.inf)
    depth_flat[pixel_idx] = depth[order]

    out = out_flat.reshape(height, width, 3)
    covered = covered_flat.reshape(height, width)
    _fill_see_through(out, depth_flat.reshape(height, width))
    _fill_small_holes(out, covered)
    return out, covered
