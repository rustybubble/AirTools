import numpy as np
import pytest
import trimesh
from PIL import Image

pytest.importorskip(
    "pymeshlab"
)  # pipeline.mesh needs the `pipeline` extra (uv sync --extra pipeline)

from pipeline.mesh import (
    _drop_small_components,
    _merge_atlases,
    _tight_crop_scene_textures,
    build_collision_mesh,
    camera_target,
    crop_to_subject,
    crop_to_subject_auto,
    decimate_textured,
    scale_from_known_height,
    view_centrality_scores,
)


def _textured_box(extents=(4.0, 3.0, 2.0), subdivide=2) -> trimesh.Trimesh:
    box = trimesh.creation.box(extents=extents)
    for _ in range(subdivide):
        box = box.subdivide()
    image = Image.new("RGB", (8, 8), (180, 90, 40))
    uv = np.random.default_rng(0).random((len(box.vertices), 2))
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, baseColorFactor=[1, 1, 1, 1]
    )
    box.visual = trimesh.visual.TextureVisuals(uv=uv, image=image, material=material)
    return box


# --- synthetic tower/ground/floater scene, for crop_to_subject_auto -------------------------
# A tower (subject) sitting on a much wider ground plane, plus a small floater (e.g. a sky blob)
# disconnected from both -- the exact "tower + wide ground + floater" corpus the crop is meant to
# handle, with cameras that orbit or fly by while keeping the tower centred (the pilot's real
# behaviour either way).

_WORLD_UP = np.array([0.0, 1.0, 0.0])
_TOWER_LOOK_AT = np.array([0.0, 10.0, 0.0])


def _tower_ground_floater() -> tuple[trimesh.Trimesh, trimesh.Trimesh]:
    """Returns `(combined, tower)` -- `tower` alone, for asserting how much of it survives.
    Subdivided enough that the floater (left coarse, 12 faces) is well under 1% of total faces
    (`_drop_small_components`'s default threshold), and ground has real geometry to test "far
    ground dropped" against."""
    tower = trimesh.creation.box(extents=(4.0, 20.0, 4.0))
    for _ in range(3):
        tower = tower.subdivide()
    tower.apply_translation([0.0, 10.0, 0.0])  # base at y=0, top at y=20

    ground = trimesh.creation.box(extents=(200.0, 1.0, 200.0))
    for _ in range(3):
        ground = ground.subdivide()
    ground.apply_translation([0.0, -0.5, 0.0])  # top surface at y=0

    floater = trimesh.creation.box(extents=(2.0, 2.0, 2.0))
    floater.apply_translation([0.0, 40.0, 0.0])  # isolated, well above the tower -- a sky blob

    combined = trimesh.util.concatenate([tower, ground, floater])
    return combined, tower


def _camera_intrinsics(n: int, fx=1400.0, fy=1400.0, cx=960.0, cy=540.0, w=1920.0, h=1080.0):
    return np.tile(np.array([fx, fy, cx, cy, w, h]), (n, 1))


def _world_to_camera_R(forward: np.ndarray, world_up: np.ndarray = _WORLD_UP) -> np.ndarray:
    """World-to-camera rotation (rows = camera axes in world coords, `package.Camera.R`
    convention) for a camera looking along `forward`, right-handed OpenCV axes (x=right,
    y=down, z=forward)."""
    right = np.cross(forward, world_up)
    right = right / np.linalg.norm(right)
    down = np.cross(forward, right)
    return np.stack([right, down, forward], axis=0)


def _orbit_cameras(n: int = 12, radius: float = 30.0, height: float = 12.0):
    """Cameras circling the tower, always looking at it -- the easy case even for the legacy
    `camera_target`/`crop_to_subject` orbit crop."""
    angles = np.linspace(0, 2 * np.pi, n, endpoint=False)
    positions = np.stack(
        [radius * np.cos(angles), np.full(n, height), radius * np.sin(angles)], axis=1
    )
    forwards = _TOWER_LOOK_AT - positions
    forwards /= np.linalg.norm(forwards, axis=1, keepdims=True)
    rotations = np.array([_world_to_camera_R(f) for f in forwards])
    return positions, rotations


def _flyby_cameras_fixed_heading(n: int = 41, z: float = 30.0):
    """Cameras sliding along a straight line (varying x, fixed y/z) with a near-constant heading
    (a slight downward tilt, not re-aimed per frame) -- a genuine facade/flyby pass, and the exact
    pattern `camera_target` is documented to reject (near-parallel optical axes, high cond(A)):
    only the frames near x=0 actually have the tower centred, mirroring
    `test_camera_target_rejects_near_parallel_rays`."""
    xs = np.linspace(-60.0, 60.0, n)
    positions = np.stack([xs, np.full(n, 12.0), np.full(n, z)], axis=1)
    forward = np.array([0.0, -0.1, -1.0])
    forward = forward / np.linalg.norm(forward)
    rotations = np.tile(_world_to_camera_R(forward), (n, 1, 1))
    return positions, rotations


# --- camera_target ------------------------------------------------------------------


def test_camera_target_recovers_exact_intersection_point():
    target = np.array([1.0, 2.0, -3.0])
    rng = np.random.default_rng(1)
    positions = target + rng.normal(scale=5.0, size=(10, 3))
    forwards = target - positions  # every camera looks exactly at target

    recovered = camera_target(positions, forwards)

    assert recovered == pytest.approx(target, abs=1e-8)


def test_camera_target_least_squares_with_noisy_forwards():
    target = np.array([0.0, 0.0, 0.0])
    rng = np.random.default_rng(2)
    positions = rng.normal(scale=10.0, size=(30, 3))
    forwards = (target - positions) + rng.normal(scale=0.01, size=(30, 3))

    recovered = camera_target(positions, forwards)

    assert recovered == pytest.approx(target, abs=0.5)


def test_camera_target_rejects_near_parallel_rays():
    # straight flyby: camera slides along Y, always pointing +Z -- not a converging orbit. Real
    # footage that produced this exact pattern crashed crop_to_subject via a wildly-wrong
    # intersection point (cond(A) ~ 45000) before this guard was added.
    positions = np.array([[0.0, y, 0.0] for y in np.linspace(-6.0, 6.0, 30)])
    forwards = np.tile([0.01, -0.003, 1.0], (30, 1))

    with pytest.raises(ValueError):
        camera_target(positions, forwards)


# --- crop_to_subject ------------------------------------------------------------------


def test_crop_to_subject_removes_far_geometry():
    subject = trimesh.creation.box(extents=(2.0, 2.0, 2.0))  # centred at origin
    background = trimesh.creation.box(extents=(1.0, 1.0, 1.0))
    background.apply_translation([100.0, 0.0, 0.0])  # far away
    combined = subject + background

    cam_positions = np.array([[5.0, 0.0, 0.0], [0.0, 5.0, 0.0], [-5.0, 0.0, 0.0]])
    cropped = crop_to_subject(combined, center=np.zeros(3), cam_positions=cam_positions, k=1.5)

    assert cropped.vertices[:, 0].max() < 50.0  # background box excluded
    assert len(cropped.faces) < len(combined.faces)


def test_crop_to_subject_noop_when_radius_keeps_nothing():
    mesh = trimesh.creation.box(extents=(1.0, 1.0, 1.0))
    mesh.apply_translation([1000.0, 0.0, 0.0])  # entirely outside any sane crop radius
    cam_positions = np.array([[0.0, 0.0, 0.0]])

    cropped = crop_to_subject(mesh, center=np.zeros(3), cam_positions=cam_positions, k=0.001)

    assert len(cropped.faces) == len(mesh.faces)  # unchanged, not emptied


# --- _drop_small_components -------------------------------------------------------------


def test_drop_small_components_removes_floater_keeps_large_regions():
    combined, _tower = _tower_ground_floater()

    cleaned = _drop_small_components(combined)

    # floater (12 faces / 1548 total, isolated) gone; tower + ground (768 faces each) both kept --
    # size-based floater removal alone doesn't know which large region is "the subject" yet.
    assert len(cleaned.faces) == len(combined.faces) - 12
    assert cleaned.vertices[:, 1].max() < 35.0  # floater (y=40) not in the result


# --- view_centrality_scores / crop_to_subject_auto ---------------------------------------


def test_view_centrality_scores_favors_centrally_viewed_vertices():
    # one vertex dead ahead of every camera, one far off (high above the cameras' horizontal
    # orbit plane -- well outside any of their central boxes, not just far away)
    vertices = np.array([[0.0, 0.0, 0.0], [0.0, 500.0, 500.0]])
    n = 8
    angles = np.linspace(0, 2 * np.pi, n, endpoint=False)
    positions = np.stack([20.0 * np.cos(angles), np.zeros(n), 20.0 * np.sin(angles)], axis=1)
    forwards = -positions / np.linalg.norm(positions, axis=1, keepdims=True)  # all look at origin
    rotations = np.array([_world_to_camera_R(f) for f in forwards])
    intrinsics = _camera_intrinsics(len(positions))

    scores = view_centrality_scores(vertices, positions, rotations, intrinsics)

    assert scores[0] > 0.0
    assert scores[1] == 0.0
    assert scores[0] > scores[1]


def test_crop_to_subject_auto_keeps_tower_orbit_drops_ground_and_floater():
    combined, tower = _tower_ground_floater()
    positions, rotations = _orbit_cameras()
    intrinsics = _camera_intrinsics(len(positions))

    cropped = crop_to_subject_auto(combined, positions, rotations, intrinsics, _WORLD_UP)

    assert len(cropped.faces) > 0
    assert len(cropped.faces) <= len(tower.faces) * 1.5  # tower plus at most a little base ground
    assert len(cropped.faces) < len(combined.faces)
    assert cropped.vertices[:, 1].max() > 15.0  # tower's upper half survived
    assert np.abs(cropped.vertices[:, 0]).max() < 50.0  # far ground (out to +-100) excluded
    assert np.abs(cropped.vertices[:, 2]).max() < 50.0
    assert cropped.vertices[:, 1].max() < 35.0  # floater (y=40) excluded


def test_crop_to_subject_auto_keeps_tower_on_a_flyby_that_breaks_camera_target():
    combined, _tower = _tower_ground_floater()
    positions, rotations = _flyby_cameras_fixed_heading()

    # the legacy orbit method genuinely can't handle this camera path (documented failure mode,
    # see test_camera_target_rejects_near_parallel_rays) -- confirms this is a real flyby case,
    # not an easy one in disguise.
    forwards = rotations[:, 2, :]
    with pytest.raises(ValueError):
        camera_target(positions, forwards)

    intrinsics = _camera_intrinsics(len(positions))
    cropped = crop_to_subject_auto(combined, positions, rotations, intrinsics, _WORLD_UP)

    assert len(cropped.faces) > 0
    assert len(cropped.faces) < len(combined.faces)
    assert cropped.vertices[:, 1].max() > 15.0  # tower survived
    assert np.abs(cropped.vertices[:, 0]).max() < 50.0  # far ground excluded
    assert cropped.vertices[:, 1].max() < 35.0  # floater excluded


def test_crop_to_subject_auto_noop_when_nothing_centrally_viewed():
    mesh = trimesh.creation.box(extents=(2.0, 2.0, 2.0))
    # camera far off to the side, never centred on the mesh
    positions = np.array([[500.0, 0.0, 0.0]])
    rotations = np.array([_world_to_camera_R(np.array([0.0, 0.0, 1.0]))])
    intrinsics = _camera_intrinsics(1)

    cropped = crop_to_subject_auto(mesh, positions, rotations, intrinsics, _WORLD_UP)

    assert len(cropped.faces) == len(mesh.faces)  # unchanged, not emptied


# --- scale_from_known_height -----------------------------------------------------------


def test_scale_from_known_height_matches_target_height():
    mesh = trimesh.creation.box(extents=(2.0, 10.0, 2.0))  # 10 units tall in Y

    estimate = scale_from_known_height(mesh, known_height_m=5.0)

    assert estimate.method == "known_dimension"
    assert estimate.scale == pytest.approx(0.5, rel=1e-6)
    assert estimate.residual_m is not None


def test_scale_from_known_height_residual_reflects_outliers():
    # many vertices (subdivided) so the 1st/99th percentile has enough population to actually
    # ignore a couple of injected outliers, rather than being defined by them
    mesh = trimesh.creation.box(extents=(2.0, 10.0, 2.0)).subdivide().subdivide().subdivide()
    outliers = np.array([[0.0, 50.0, 0.0], [0.0, -50.0, 0.0]])
    mesh.vertices = np.vstack([mesh.vertices, outliers])

    estimate = scale_from_known_height(mesh, known_height_m=5.0)

    assert estimate.scale == pytest.approx(0.5, rel=0.2)  # still close to the clean-box answer
    assert estimate.residual_m > 0.0  # full extent vs. percentile extent now visibly differ


def test_scale_from_known_height_rejects_degenerate_mesh():
    mesh = trimesh.creation.box(extents=(2.0, 10.0, 2.0))
    mesh.vertices[:, 1] = 0.0  # flatten to zero height
    with pytest.raises(ValueError):
        scale_from_known_height(mesh, known_height_m=5.0)


# --- _tight_crop_scene_textures ----------------------------------------------------------


def test_tight_crop_scene_textures_shrinks_sparse_atlas_and_keeps_colours():
    # the real patches only occupy the top-left quarter of the canvas (OpenMVS packs charts from
    # the top, UVs are OpenGL-style so that quarter is u<0.5, v>0.5); the rest is fill colour.
    image = np.full((100, 100, 3), (255, 128, 0), dtype=np.uint8)
    image[:25, :25] = (200, 0, 0)
    image[25:50, :50] = (0, 200, 0)
    image[:25, 25:50] = (0, 0, 200)
    uv = np.array([[0.05, 0.95], [0.45, 0.95], [0.45, 0.55], [0.05, 0.55], [0.3, 0.9]])
    patch = trimesh.Trimesh(
        vertices=np.random.default_rng(0).random((5, 3)),
        faces=[[0, 1, 2], [0, 2, 3], [0, 4, 1]],
        visual=trimesh.visual.TextureVisuals(
            uv=uv,
            material=trimesh.visual.material.PBRMaterial(baseColorTexture=Image.fromarray(image)),
        ),
        process=False,
    )
    before = patch.visual.to_color().vertex_colors[:, :3].copy()
    scene = trimesh.Scene({"patch": patch})

    _tight_crop_scene_textures(scene)

    cropped = scene.geometry["patch"]
    assert cropped.visual.material.baseColorTexture.size[0] <= 50
    assert cropped.visual.material.baseColorTexture.size[1] <= 50
    assert cropped.visual.to_color().vertex_colors[:, :3].tolist() == before.tolist()
    assert (255, 128, 0) not in {tuple(c) for c in before.tolist()}  # never samples the fill


def test_tight_crop_scene_textures_is_noop_when_uv_already_fills_canvas():
    mesh = _textured_box(subdivide=1)
    mesh.visual.uv[:] = np.clip(mesh.visual.uv, 0.0, 1.0)
    mesh.visual.uv[0] = [0.0, 0.0]
    mesh.visual.uv[1] = [1.0, 1.0]
    original_size = mesh.visual.material.baseColorTexture.size
    scene = trimesh.Scene({"box": mesh})

    _tight_crop_scene_textures(scene)

    assert scene.geometry["box"].visual.material.baseColorTexture.size == original_size


def test_merge_atlases_keeps_every_face_on_its_own_texels():
    # the kitchen-clip shape trimesh 5.1's packer got wrong: a full atlas plus a shorter one
    rng = np.random.default_rng(1)
    scene = trimesh.Scene()
    for name, (w, h) in {"a": (64, 64), "b": (60, 34)}.items():
        img = Image.fromarray(rng.integers(0, 255, (h, w, 3), dtype=np.uint8))
        px = rng.integers(0, (w, h), (30, 2)) + 0.5  # texel centres: exact samples
        uv = np.c_[px[:, 0] / w, 1.0 - px[:, 1] / h]
        scene.add_geometry(
            trimesh.Trimesh(
                rng.random((30, 3)),
                rng.integers(0, 30, (20, 3)),
                process=False,
                visual=trimesh.visual.TextureVisuals(
                    uv, material=trimesh.visual.material.PBRMaterial(baseColorTexture=img)
                ),
            ),
            geom_name=name,
        )
    before = np.vstack(
        [g.visual.to_color().vertex_colors[:, :3] for g in scene.geometry.values()]
    ).astype(int)

    merged = _merge_atlases(scene)

    assert merged.visual.uv.min() >= 0.0 and merged.visual.uv.max() <= 1.0
    after = merged.visual.to_color().vertex_colors[:, :3].astype(int)
    assert np.abs(after - before).max() <= 2  # random texels: a misplaced uv is off by ~100


# --- decimate_textured ------------------------------------------------------------------


def test_decimate_textured_reduces_faces_and_keeps_uv_and_texture():
    mesh = _textured_box(subdivide=3)
    target = len(mesh.faces) // 4

    decimated = decimate_textured(mesh, target_triangles=target)

    assert (
        len(decimated.faces) <= target * 1.5
    )  # quadric collapse lands close to, not exact, target
    assert len(decimated.faces) < len(mesh.faces)
    assert decimated.visual.uv is not None
    assert len(decimated.visual.uv) == len(decimated.vertices)
    assert decimated.visual.material.baseColorTexture is not None


def test_decimate_textured_is_noop_under_budget():
    mesh = _textured_box(subdivide=1)
    decimated = decimate_textured(mesh, target_triangles=len(mesh.faces) * 10)
    assert len(decimated.faces) == len(mesh.faces)


# --- build_collision_mesh ----------------------------------------------------------------


def test_build_collision_mesh_is_untextured_and_reduced():
    mesh = _textured_box(subdivide=3)
    target = len(mesh.faces) // 4

    collision = build_collision_mesh(mesh, target_triangles=target)

    assert len(collision.faces) <= target * 1.5
    assert (
        not hasattr(collision.visual, "material")
        or getattr(collision.visual, "material", None) is None
        or getattr(collision.visual.material, "baseColorTexture", None) is None
    )


def test_load_openmvs_glb_drops_the_develop_build_node_rotation(tmp_path):
    from pipeline.mesh import load_openmvs_glb

    box = trimesh.creation.box(extents=(1.0, 2.0, 3.0))
    box.apply_translation((5.0, 0.0, 0.0))
    scene = trimesh.Scene()
    y_up = np.array([[1, 0, 0, 0], [0, 0, 1, 0], [0, -1, 0, 0], [0, 0, 0, 1]], float)
    scene.add_geometry(box, node_name="mesh", transform=y_up)
    path = tmp_path / "textured.glb"
    scene.export(path)

    loaded = load_openmvs_glb(path).to_geometry()

    assert np.allclose(loaded.bounds, box.bounds)
