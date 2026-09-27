"""Mesh geometry helpers, split across two call sites:

Auto-crop to the subject (`crop_to_subject_auto`/`crop_to_subject_orbit`, `--crop auto|orbit|none`)
runs on OpenMVS's *untextured* dense mesh, between `ReconstructMesh` and `TextureMesh`
(`mvs.run_mvs`) -- before texturing, so the 4096px atlas and triangle budget are spent on the
subject, not the surrounding scene.

`process_mesh` post-processes OpenMVS's *textured* GLB into the package's `mesh.glb` +
`collision.glb` (assignment §5): calibration + glTF up-alignment, known-dimension scale (§6),
decimate to budget (UV-preserving), JPEG texture <=4096px, and a lighter untextured collision mesh
in the same frame.
"""

import io
import logging
import tempfile
from dataclasses import dataclass
from pathlib import Path

import fast_simplification
import numpy as np
import pymeshlab
import trimesh
from PIL import Image

from pipeline.calibrate import ScaleEstimate, apply_similarity

logger = logging.getLogger(__name__)

_DEFAULT_TARGET_TRIANGLES = 200_000
_DEFAULT_COLLISION_TRIANGLES = 50_000
_DEFAULT_TEXTURE_SIZE = 4096
_DEFAULT_CROP_RADIUS_FACTOR = 1.5  # k in "k * median camera-to-target distance", orbit crop only
_HEIGHT_PERCENTILES = (1.0, 99.0)  # robust top/bottom of the subject, ignores stray/noise verts

# camera_target's normal-equations matrix A degenerates when the cameras' optical axes are close to
# parallel (a straight flyby/push shot, not a converging orbit) -- the ray-intersection point then
# becomes numerically arbitrary and can land far outside the actual scene, silently making
# crop_to_subject discard almost the whole mesh. Confirmed on real footage (a near-straight-line
# flight with <0.6 degree of yaw change end to end): cond(A) ~ 45000 vs. ~1-2 for a real converging
# orbit -- 1000 comfortably separates the two. crop_to_subject_orbit (--crop orbit) inherits this
# failure mode by design -- it's the legacy/opt-in path; crop_to_subject_auto (--crop auto, default)
# doesn't need a target point at all, so it has no equivalent guard to trip.
_MAX_TARGET_CONDITION = 1000.0

# --- view-centrality auto-crop (--crop auto, default) defaults ------------------------------
_DEFAULT_CENTRAL_FRACTION = 0.45  # "centred" = within the middle 45% of the frame, each axis
_DEFAULT_SCORE_PERCENTILE = 50.0  # median of the *positively-scored* vertices -- robust to a long
# tail of barely-seen background getting a nonzero-but-tiny score from a single grazing frame
_DEFAULT_MIN_COMPONENT_FRACTION = 0.01  # drop connected regions under 1% of the mesh's faces
_DEFAULT_CROP_MARGIN_FACTOR = 0.15  # crop box = subject's horizontal footprint + 15% of its span


@dataclass
class MeshStats:
    triangles: int
    texture_px: int
    bbox: list  # [[minx, miny, minz], [maxx, maxy, maxz]]
    mesh_bytes: int
    collision_triangles: int
    collision_bytes: int


# --- auto-crop ----------------------------------------------------------------------------


def camera_target(cam_positions: np.ndarray, cam_forwards: np.ndarray) -> np.ndarray:
    """Least-squares intersection of the cameras' optical axes (rays `pos + t*forward`) -- the
    point the cameras are, on average, looking at. Standard closed-form multi-ray intersection:
    each ray contributes a normal-equation term built from its perpendicular-distance projector
    `(I - d d^T)`; the point minimizing the sum of squared perpendicular distances solves
    `(sum of projectors) @ p = sum of projector @ pos`."""
    cam_positions = np.asarray(cam_positions, dtype=np.float64)
    cam_forwards = np.asarray(cam_forwards, dtype=np.float64)
    A = np.zeros((3, 3))
    b = np.zeros(3)
    for pos, fwd in zip(cam_positions, cam_forwards, strict=True):
        d = fwd / np.linalg.norm(fwd)
        proj = np.eye(3) - np.outer(d, d)
        A += proj
        b += proj @ pos
    if np.linalg.cond(A) > _MAX_TARGET_CONDITION:
        raise ValueError(
            "camera_target: optical axes too close to parallel to intersect reliably "
            "(looks like a straight flyby, not a converging orbit)"
        )
    return np.linalg.solve(A, b)


def crop_to_subject(
    mesh: trimesh.Trimesh, center: np.ndarray, cam_positions: np.ndarray, k: float
) -> trimesh.Trimesh:
    """Keep only geometry within `k * median(camera-to-target distance)` of `center` -- drops
    background/ground the dense reconstruction picked up, keeps the subject. No-op if that radius
    would keep zero faces (logs a warning rather than returning an empty mesh)."""
    dists = np.linalg.norm(np.asarray(cam_positions, dtype=np.float64) - center, axis=1)
    radius = k * float(np.median(dists))
    vertex_mask = np.linalg.norm(mesh.vertices - center, axis=1) <= radius
    face_mask = vertex_mask[mesh.faces].all(axis=1)
    if not face_mask.any():
        logger.warning("crop_to_subject: radius %.3g kept 0 faces, skipping crop", radius)
        return mesh
    cropped = mesh.copy()
    cropped.update_faces(face_mask)
    cropped.remove_unreferenced_vertices()
    return cropped


def crop_to_subject_orbit(
    mesh: trimesh.Trimesh,
    cam_positions: np.ndarray,
    cam_forwards: np.ndarray,
    k: float = _DEFAULT_CROP_RADIUS_FACTOR,
) -> trimesh.Trimesh:
    """`--crop orbit`: `camera_target`'s ray-intersection point + `crop_to_subject`'s radius cut.
    Only reliable for a converging orbit -- `camera_target`'s `cond(A)` guard rejects a straight
    flyby/facade pass outright (logs a warning and returns `mesh` unchanged) rather than cropping
    from a numerically-arbitrary "target". Kept as the opt-in legacy path; `crop_to_subject_auto`
    (`--crop auto`, the default) handles both motions without this failure mode."""
    try:
        center = camera_target(cam_positions, cam_forwards)
    except ValueError as exc:
        logger.warning("crop_to_subject_orbit: skipping crop (%s)", exc)
        return mesh
    return crop_to_subject(mesh, center, cam_positions, k)


# --- view-centrality auto-crop (§ remaining problem 1: works for orbits AND flybys) -----------


def _face_components(mesh: trimesh.Trimesh) -> list[np.ndarray]:
    """Connected components of the face-adjacency graph (faces sharing an edge), as arrays of face
    indices, largest first. Hand-rolled union-find over `mesh.face_adjacency` rather than trimesh's
    own `split()`/`graph.connected_components()`, which require an optional graph backend
    (`scipy`/`networkx`) that isn't a project dependency.
    ponytail: O(edges) pure-Python loop, not vectorised -- fine at pipeline scale (called a handful
    of times per run, not per-frame), upgrade to `scipy.sparse.csgraph.connected_components` if a
    multi-million-face dense mesh ever makes this a measured bottleneck."""
    n = len(mesh.faces)
    parent = np.arange(n)

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return int(i)

    for a, b in mesh.face_adjacency:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    groups: dict[int, list[int]] = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)
    return sorted((np.array(g) for g in groups.values()), key=len, reverse=True)


def _drop_small_components(
    mesh: trimesh.Trimesh, min_face_fraction: float = _DEFAULT_MIN_COMPONENT_FRACTION
) -> trimesh.Trimesh:
    """Drop connected (shared-edge) regions under `min_face_fraction` of the mesh's total faces --
    removes floaters (disconnected specks: sky blobs, birds, reconstruction noise) that a
    bounding-volume crop alone wouldn't catch, since a floater can sit anywhere in space including
    right next to the subject. No-op if there's only one component, or if every component is
    somehow under the threshold (keeps everything rather than emptying the mesh)."""
    total = len(mesh.faces)
    if total == 0:
        return mesh
    components = _face_components(mesh)
    if len(components) <= 1:
        return mesh
    keep = [c for c in components if len(c) >= min_face_fraction * total]
    if not keep:
        logger.warning(
            "_drop_small_components: every component under %.3g of faces, keeping all",
            min_face_fraction,
        )
        return mesh
    face_mask = np.zeros(total, dtype=bool)
    face_mask[np.concatenate(keep)] = True
    cleaned = mesh.copy()
    cleaned.update_faces(face_mask)
    cleaned.remove_unreferenced_vertices()
    return cleaned


def view_centrality_scores(
    vertices: np.ndarray,
    cam_positions: np.ndarray,
    cam_rotations: np.ndarray,
    cam_intrinsics: np.ndarray,
    central_fraction: float = _DEFAULT_CENTRAL_FRACTION,
) -> np.ndarray:
    """Per-vertex score: summed, over cameras, `1/depth` for every camera that sees this vertex
    within the central `central_fraction` of its frame (both axes) -- "how strongly do the cameras
    agree this point is the subject", weighted so a close, centred view counts more than a distant
    grazing one. The core signal behind `crop_to_subject_auto`: unlike `camera_target`'s single
    ray-intersection point, this needs no assumption that the cameras' optical axes converge to one
    spot, so it's equally valid for an orbit (subject stays centred from every angle) and a
    flyby/facade pass (subject sweeps through the centre only in the frames nearest it -- those
    frames still vote, the frames where it's off to the side just don't).

    `cam_positions`: `(N,3)`. `cam_rotations`: `(N,3,3)` world-to-camera `R` (OpenCV convention,
    `x_cam = R @ (x_world - position)`, matches `package.Camera.R`). `cam_intrinsics`: `(N,6)`
    columns `fx, fy, cx, cy, w, h`. No occlusion test (frustum + central-box only) -- deliberate:
    this only has to locate the subject's bounding volume, not decide per-face visibility.
    """
    vertices = np.asarray(vertices, dtype=np.float64)
    cam_positions = np.asarray(cam_positions, dtype=np.float64)
    cam_rotations = np.asarray(cam_rotations, dtype=np.float64)
    cam_intrinsics = np.asarray(cam_intrinsics, dtype=np.float64)
    scores = np.zeros(len(vertices))
    half = central_fraction / 2.0
    cams = zip(cam_positions, cam_rotations, cam_intrinsics, strict=True)
    for pos, R, (fx, fy, cx, cy, w, h) in cams:
        cam_pts = (R @ (vertices - pos).T).T
        depth = cam_pts[:, 2]
        in_front = depth > 1e-6
        safe_depth = np.where(in_front, depth, 1.0)
        u = fx * cam_pts[:, 0] / safe_depth + cx
        v = fy * cam_pts[:, 1] / safe_depth + cy
        central = in_front & (np.abs(u - cx) <= half * w) & (np.abs(v - cy) <= half * h)
        scores += np.where(central, 1.0 / np.maximum(depth, 1e-3), 0.0)
    return scores


def _horizontal_basis(up: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Two unit vectors spanning the plane perpendicular to `up` -- an arbitrary but deterministic
    heading (same fallback trick as `calibrate.align_to_gltf`'s no-north-reference case)."""
    up = np.asarray(up, dtype=np.float64)
    up = up / np.linalg.norm(up)
    ref = np.array([1.0, 0.0, 0.0]) if abs(up[0]) < 0.9 else np.array([0.0, 0.0, 1.0])
    e1 = ref - np.dot(ref, up) * up
    e1 = e1 / np.linalg.norm(e1)
    e2 = np.cross(up, e1)
    return e1, e2


def crop_to_subject_auto(
    mesh: trimesh.Trimesh,
    cam_positions: np.ndarray,
    cam_rotations: np.ndarray,
    cam_intrinsics: np.ndarray,
    up: np.ndarray,
    *,
    central_fraction: float = _DEFAULT_CENTRAL_FRACTION,
    score_percentile: float = _DEFAULT_SCORE_PERCENTILE,
    min_component_fraction: float = _DEFAULT_MIN_COMPONENT_FRACTION,
    margin_factor: float = _DEFAULT_CROP_MARGIN_FACTOR,
) -> trimesh.Trimesh:
    """`--crop auto` (default): crop to the subject via view-centrality (`view_centrality_scores`)
    instead of a single ray-intersection point (`crop_to_subject_orbit`) -- works for both a
    converging orbit and a flyby/facade pass, since it never needs the cameras' optical axes to
    meet at one point.

    1. Drop floaters (`_drop_small_components`) before scoring, so a disconnected sky blob can't
       skew the score threshold.
    2. Score every remaining vertex; threshold at the `score_percentile`th percentile of the
       *positively*-scored vertices (median by default) -- the "core" subject vertices.
    3. Keep only the core's connected region(s) of at least `min_component_fraction` of its own
       faces (drops a few scattered high-score outlier vertices that aren't part of one coherent
       subject blob).
    4. Take that core's bounding box in the plane perpendicular to `up`, expand by `margin_factor`
       of its span, and crop the *whole* (floater-dropped) mesh to that box -- unbounded along
       `up` itself, so it's a vertical prism, not a 3D box: this is what keeps the ground right at
       the subject's base (it's within the horizontal footprint) while dropping ground far away
       (it isn't), without needing a separate height threshold that could clip the subject itself.

    No-op (logs a warning, returns `mesh` unchanged) if no vertex was ever centrally viewed, or if
    any stage would keep zero faces -- never silently returns an empty mesh.
    """
    cleaned = _drop_small_components(mesh, min_component_fraction)
    scores = view_centrality_scores(
        cleaned.vertices, cam_positions, cam_rotations, cam_intrinsics, central_fraction
    )
    positive = scores[scores > 0]
    if positive.size == 0:
        logger.warning("crop_to_subject_auto: no vertex was ever centrally viewed, skipping crop")
        return mesh
    threshold = np.percentile(positive, score_percentile)
    core_face_mask = (scores > threshold)[cleaned.faces].all(axis=1)
    if not core_face_mask.any():
        logger.warning("crop_to_subject_auto: score threshold kept 0 faces, skipping crop")
        return mesh
    core = cleaned.copy()
    core.update_faces(core_face_mask)
    core.remove_unreferenced_vertices()
    core = _drop_small_components(core, min_component_fraction)

    e1, e2 = _horizontal_basis(up)
    core_h1, core_h2 = core.vertices @ e1, core.vertices @ e2
    span1 = max(float(core_h1.max() - core_h1.min()), 1e-6)
    span2 = max(float(core_h2.max() - core_h2.min()), 1e-6)
    lo1, hi1 = core_h1.min() - margin_factor * span1, core_h1.max() + margin_factor * span1
    lo2, hi2 = core_h2.min() - margin_factor * span2, core_h2.max() + margin_factor * span2

    all_h1, all_h2 = cleaned.vertices @ e1, cleaned.vertices @ e2
    vertex_mask = (all_h1 >= lo1) & (all_h1 <= hi1) & (all_h2 >= lo2) & (all_h2 <= hi2)
    face_mask = vertex_mask[cleaned.faces].all(axis=1)
    if not face_mask.any():
        logger.warning("crop_to_subject_auto: bounding prism kept 0 faces, skipping crop")
        return mesh
    cropped = cleaned.copy()
    cropped.update_faces(face_mask)
    cropped.remove_unreferenced_vertices()
    return cropped


# --- known-dimension scale (§6b) -----------------------------------------------------------


def scale_from_known_height(
    mesh: trimesh.Trimesh,
    known_height_m: float,
    percentiles: tuple[float, float] = _HEIGHT_PERCENTILES,
) -> ScaleEstimate:
    """Measure the mesh's vertical (+Y, already up-aligned) extent via robust percentiles (default
    1st-99th, ignoring outlier vertices) and scale so it matches `known_height_m`. Approximate by
    construction (percentile clipping vs. the true silhouette extremum) -- `residual_m` is half the
    gap between the percentile-based and the full min/max height, in the resulting metres, as a
    rough honesty check on that approximation."""
    y = mesh.vertices[:, 1]
    lo, hi = np.percentile(y, percentiles)
    measured = float(hi - lo)
    if measured <= 0:
        raise ValueError("scale_from_known_height: degenerate (zero-height) mesh")
    scale = known_height_m / measured
    full_extent = float(y.max() - y.min())
    residual_m = abs(full_extent - measured) * scale / 2.0
    return ScaleEstimate(
        method="known_dimension",
        scale=scale,
        residual_m=residual_m,
        notes=(
            f"approximate: {percentiles[0]:.0f}-{percentiles[1]:.0f} percentile mesh height "
            f"matched to known_height_m={known_height_m}"
        ),
    )


# --- decimation (UV-preserving) ------------------------------------------------------------


def decimate_textured(mesh: trimesh.Trimesh, target_triangles: int) -> trimesh.Trimesh:
    """Decimate a textured mesh to `target_triangles`, preserving UVs (research doc §3:
    fast-simplification/Open3D's quadric decimation both silently drop UVs -- pymeshlab's
    texture-aware quadric edge collapse is the one that doesn't). Routed through a temp OBJ+MTL
    round-trip: MeshLab's texture-aware filter requires *wedge* (per-face-corner) UVs associated
    with a texture file, which is what a plain OBJ export/import naturally carries -- simpler and
    more robust than hand-assembling MeshLab's in-memory wedge-texcoord API. No-op under budget."""
    if len(mesh.faces) <= target_triangles:
        return mesh

    with tempfile.TemporaryDirectory(prefix="airtools_decimate_") as tmp:
        tmp_dir = Path(tmp)
        in_path = tmp_dir / "in.obj"
        out_path = tmp_dir / "out.obj"
        mesh.export(in_path)

        ms = pymeshlab.MeshSet()
        ms.load_new_mesh(str(in_path))
        ms.meshing_decimation_quadric_edge_collapse_with_texture(
            targetfacenum=target_triangles, extratcoordw=1.0
        )
        ms.save_current_mesh(str(out_path))

        decimated = trimesh.load(out_path, process=False, force="mesh")

    # OBJ/MTL round-trips the texture as a plain file (usually re-encoded to PNG) -- keep geometry
    # + UVs from the round-trip but reattach the *original* texture image so nothing but the
    # decimation itself changes the visible result.
    material = mesh.visual.material
    decimated.visual = trimesh.visual.TextureVisuals(
        uv=decimated.visual.uv, image=material.baseColorTexture, material=material
    )
    return decimated


# --- texture normalisation -------------------------------------------------------------------


def _tight_crop_scene_textures(scene: trimesh.Scene) -> trimesh.Scene:
    """Crop each sub-geometry's texture to the bounding box of UV space it actually uses, in
    place. OpenMVS's TextureMesh splits its output across *multiple* atlas images when total
    patch area exceeds one canvas (confirmed: `facade-highrise-corner`, a close-up/high-detail
    facade, needed 5 separate 4096x4096 atlases) -- each such atlas only sparsely fills its own
    canvas. `Scene.to_geometry()`'s packer (`trimesh.visual.objects.pack`) lays sub-images out
    side by side without repacking their content, so combining several already-sparse atlases
    compounds the waste -- confirmed on that clip: 78% of the naively-packed combined atlas was
    unused black padding, enough to make ~40% of the final mesh sample black instead of real
    texture (verified via `preview.render`: a frame report as 94% "covered" was 99% black
    pixels). Cropping first means the packer works with much smaller, denser images."""
    for geom in scene.geometry.values():
        visual = getattr(geom, "visual", None)
        material = getattr(visual, "material", None)
        image = getattr(material, "baseColorTexture", None) if material else None
        uv = getattr(visual, "uv", None)
        if image is None or uv is None or len(uv) == 0:
            continue
        w, h = image.size
        u0, v0 = np.clip(uv.min(axis=0), 0.0, 1.0)
        u1, v1 = np.clip(uv.max(axis=0), 0.0, 1.0)
        # trimesh UVs are OpenGL-style: v=1 is the image's top row, so rows run over 1 - v.
        left, top = int(u0 * w), int((1.0 - v1) * h)
        right = max(left + 1, int(np.ceil(u1 * w)))
        bottom = max(top + 1, int(np.ceil((1.0 - v0) * h)))
        if right - left >= w and bottom - top >= h:
            continue  # already using the whole canvas, nothing to crop
        cropped = image.crop((left, top, right, bottom))
        new_uv = uv.copy()
        new_uv[:, 0] = (uv[:, 0] * w - left) / (right - left)
        new_uv[:, 1] = 1.0 - ((1.0 - uv[:, 1]) * h - top) / (bottom - top)
        geom.visual = trimesh.visual.TextureVisuals(
            uv=new_uv,
            image=cropped,
            material=trimesh.visual.material.PBRMaterial(baseColorTexture=cropped),
        )
    return scene


def _merge_atlases(scene: trimesh.Scene) -> trimesh.Trimesh:
    """One mesh with one texture from a multi-atlas scene: each (already tight-cropped) atlas
    pasted unscaled into its own cell of a near-square grid, UVs remapped. Replaces
    `Scene.to_geometry()`, whose packer (trimesh 5.1) misplaces some atlas combinations: on the
    kitchen clip, a 4093x4091 + 4071x2196 pair left 7k faces with v > 1, which render black
    (bench E1 §7)."""
    geoms = list(scene.geometry.values())
    images = [getattr(getattr(g.visual, "material", None), "baseColorTexture", None) for g in geoms]
    if len(geoms) < 2 or any(im is None for im in images):
        return scene.to_geometry()
    cw, ch = max(im.size[0] for im in images), max(im.size[1] for im in images)
    cols = int(np.ceil(np.sqrt(len(geoms))))
    rows = int(np.ceil(len(geoms) / cols))
    atlas = Image.new("RGB", (cols * cw, rows * ch))
    parts = []
    for k, (g, im) in enumerate(zip(geoms, images)):
        r, c = divmod(k, cols)
        atlas.paste(im.convert("RGB"), (c * cw, r * ch))
        w, h = im.size
        uv = np.asarray(g.visual.uv, dtype=np.float64)
        u = (c * cw + uv[:, 0] * w) / (cols * cw)
        v = 1.0 - (r * ch + (1.0 - uv[:, 1]) * h) / (rows * ch)  # v=1 is the image's top row
        parts.append(
            trimesh.Trimesh(
                g.vertices,
                g.faces,
                process=False,
                visual=trimesh.visual.TextureVisuals(np.c_[u, v]),
            )
        )
    merged = trimesh.util.concatenate(parts)
    material = trimesh.visual.material.PBRMaterial(baseColorTexture=atlas)
    merged.visual = trimesh.visual.TextureVisuals(merged.visual.uv, material=material)
    return merged


def _ensure_jpeg_texture(mesh: trimesh.Trimesh, max_size: int) -> trimesh.Trimesh:
    """Re-encode the mesh's base color texture as JPEG (whatever OpenMVS/pymeshlab produced --
    trimesh only embeds-without-re-encoding when the PIL `Image.format` is already `'JPEG'`,
    confirmed in the research pass) and cap it to `max_size`px on the long edge."""
    material = mesh.visual.material
    image = getattr(material, "baseColorTexture", None)
    if image is None:
        return mesh
    image = image.convert("RGB")
    if max(image.size) > max_size:
        image.thumbnail((max_size, max_size), Image.LANCZOS)
    buf = io.BytesIO()
    image.save(buf, format="JPEG", quality=92)
    buf.seek(0)
    jpeg_image = Image.open(buf)
    jpeg_image.load()  # materialize pixels now -- buf is about to go out of scope

    new_material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=jpeg_image, baseColorFactor=[1.0, 1.0, 1.0, 1.0]
    )
    mesh.visual = trimesh.visual.TextureVisuals(
        uv=mesh.visual.uv, image=jpeg_image, material=new_material
    )
    return mesh


# --- collision mesh -------------------------------------------------------------------------


def build_collision_mesh(mesh: trimesh.Trimesh, target_triangles: int) -> trimesh.Trimesh:
    """Lighter, untextured collision proxy in the same frame as `mesh` (plan: "for tool snapping").
    `fast_simplification` (already a base project dep) is geometry-only/no-UV, which is exactly
    right here since collision.glb is deliberately untextured -- no UV-preservation problem to
    solve, unlike the visible mesh."""
    if len(mesh.faces) <= target_triangles:
        vertices, faces = mesh.vertices, mesh.faces
    else:
        target_reduction = 1.0 - target_triangles / len(mesh.faces)
        vertices, faces = fast_simplification.simplify(
            mesh.vertices, mesh.faces, target_reduction=target_reduction
        )
    return trimesh.Trimesh(vertices=vertices, faces=faces, process=False)


# --- orchestration ---------------------------------------------------------------------------


def load_openmvs_glb(path: Path) -> trimesh.Scene:
    """Load a TextureMesh GLB in OpenMVS's own frame. OpenMVS `develop` (e23d25f) wraps the mesh in a
    Y-up node rotation that v2.4.0 does not write; `align_matrix` is fitted in the raw frame, so
    node transforms are dropped."""
    scene = trimesh.load(path, force="scene", process=False)
    for node in scene.graph.nodes_geometry:
        scene.graph.update(node, matrix=np.eye(4))
    return scene


def process_mesh(
    textured_path: Path,
    out_mesh_path: Path,
    out_collision_path: Path,
    *,
    align_matrix: np.ndarray,
    known_height_m: float | None = None,
    target_triangles: int = _DEFAULT_TARGET_TRIANGLES,
    collision_triangles: int = _DEFAULT_COLLISION_TRIANGLES,
    max_texture_size: int = _DEFAULT_TEXTURE_SIZE,
) -> tuple[MeshStats, ScaleEstimate]:
    """Load OpenMVS's textured GLB -> apply `align_matrix` (calibration similarity + glTF
    up-alignment, from `calibrate.py`) -> optional known-height scale -> decimate to
    `target_triangles` -> JPEG texture -> write `mesh.glb` + `collision.glb`. Returns stats plus
    the known-dimension `ScaleEstimate` (method="none", scale=1.0 if `known_height_m` wasn't given
    -- caller/`run.py` combines this with any GPS-based estimate via `calibrate.choose_scale`).

    Auto-crop (`--crop`) is no longer this function's job: it runs earlier, on the untextured
    dense mesh between OpenMVS's `ReconstructMesh` and `TextureMesh` (`mvs.run_mvs`) -- so the
    4096px texture atlas and the `target_triangles` budget are spent on the cropped subject, not
    wasted on background OpenMVS's `TextureMesh` already textured. By the time a mesh reaches here
    it's already cropped (or was never going to be, per `--crop none`).
    """
    scene = load_openmvs_glb(textured_path)
    mesh = _merge_atlases(_tight_crop_scene_textures(scene))
    mesh.vertices = apply_similarity(mesh.vertices, align_matrix)

    if known_height_m is not None:
        scale_estimate = scale_from_known_height(mesh, known_height_m)
        mesh.vertices = mesh.vertices * scale_estimate.scale
    else:
        scale_estimate = ScaleEstimate(
            method="none", scale=1.0, residual_m=None, notes="no scale reference given"
        )

    mesh = decimate_textured(mesh, target_triangles)
    mesh = _ensure_jpeg_texture(mesh, max_texture_size)

    collision = build_collision_mesh(mesh, collision_triangles)

    out_mesh_path.parent.mkdir(parents=True, exist_ok=True)
    out_collision_path.parent.mkdir(parents=True, exist_ok=True)
    mesh.export(out_mesh_path)
    collision.export(out_collision_path)

    texture_px = 0
    image = getattr(mesh.visual.material, "baseColorTexture", None)
    if image is not None:
        texture_px = max(image.size)

    stats = MeshStats(
        triangles=len(mesh.faces),
        texture_px=texture_px,
        bbox=mesh.bounds.tolist(),
        mesh_bytes=out_mesh_path.stat().st_size,
        collision_triangles=len(collision.faces),
        collision_bytes=out_collision_path.stat().st_size,
    )
    return stats, scale_estimate
