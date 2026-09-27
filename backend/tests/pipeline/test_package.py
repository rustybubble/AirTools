import json

import numpy as np
import pytest
import trimesh
from PIL import Image

from pipeline.calibrate import ScaleEstimate
from pipeline.package import (
    Camera,
    load_or_create_frame_id,
    make_thumbs,
    publish_revision,
    recommended_spawn,
    revision_filenames,
    validate_package,
    write_cameras_json,
)


def _make_camera(id="0001", thumb="thumbs/0001.jpg") -> Camera:
    return Camera(
        id=id,
        file=f"frames/{id}.jpg",
        thumb=thumb,
        R=np.eye(3),
        t=np.array([0.0, 0.0, -5.0]),
        fx=1400.0,
        fy=1400.0,
        cx=960.0,
        cy=540.0,
        w=1920,
        h=1080,
    )


def _textured_box(extents=(4.0, 3.0, 2.0)) -> trimesh.Trimesh:
    box = trimesh.creation.box(extents=extents)
    image = Image.new("RGB", (4, 4), (180, 90, 40))
    uv = np.zeros((len(box.vertices), 2))
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, baseColorFactor=[1, 1, 1, 1]
    )
    box.visual = trimesh.visual.TextureVisuals(uv=uv, image=image, material=material)
    return box


def _flat_box(extents=(4.0, 3.0, 2.0)) -> trimesh.Trimesh:
    box = trimesh.creation.box(extents=extents)
    box.visual = trimesh.visual.TextureVisuals(
        material=trimesh.visual.material.PBRMaterial(baseColorFactor=[0.7, 0.7, 0.7, 1.0])
    )
    return box


# --- write_cameras_json ------------------------------------------------------------


def test_write_cameras_json_round_trips_and_derives_position(tmp_path):
    cam = _make_camera()
    out = tmp_path / "cameras.json"

    write_cameras_json([cam], out)
    data = json.loads(out.read_text())

    assert len(data) == 1
    entry = data[0]
    assert entry["id"] == "0001"
    assert entry["R"] == np.eye(3).tolist()
    assert entry["t"] == [0.0, 0.0, -5.0]
    # position = -R^T @ t = -(-5 along z) = [0,0,5]
    assert entry["position"] == pytest.approx([0.0, 0.0, 5.0])
    assert entry["fx"] == 1400.0
    assert entry["w"] == 1920


# --- make_thumbs ---------------------------------------------------------------------


def test_make_thumbs_downscales_to_long_edge(tmp_path):
    frame = tmp_path / "0001.jpg"
    Image.new("RGB", (1920, 1080), (10, 20, 30)).save(frame)

    out_dir = tmp_path / "thumbs"
    thumbs = make_thumbs([frame], out_dir, long_edge=640)

    assert len(thumbs) == 1
    assert thumbs[0].exists()
    with Image.open(thumbs[0]) as img:
        assert max(img.size) <= 640
        assert img.size[0] / img.size[1] == pytest.approx(1920 / 1080, rel=0.02)


# --- recommended_spawn ------------------------------------------------------------------


def test_recommended_spawn_stands_on_ground_facing_center():
    mesh_bounds = np.array([[-2.0, 0.0, -1.0], [2.0, 3.0, 1.0]])
    cameras = [_make_camera()]  # position [0,0,5] -- "in front" along +Z

    spawn = recommended_spawn(mesh_bounds, cameras)

    assert spawn["pos"][1] == pytest.approx(0.0 + 1.6)  # lowest mesh y + eye height
    assert spawn["look"] == pytest.approx([0.0, 1.5, 0.0])  # bbox center
    assert spawn["pos"][2] > 1.0  # pushed out towards the cameras (+Z side)


def test_recommended_spawn_deterministic_default_without_cameras():
    mesh_bounds = np.array([[-1.0, 0.0, -1.0], [1.0, 2.0, 1.0]])
    spawn = recommended_spawn(mesh_bounds, [])
    assert spawn["pos"][1] == pytest.approx(1.6)
    assert spawn["pos"][2] > 0  # default direction is +Z


# --- load_or_create_frame_id -----------------------------------------------------------


def test_load_or_create_frame_id_persists_across_calls(tmp_path):
    first = load_or_create_frame_id(tmp_path, "Klaus East Window")
    second = load_or_create_frame_id(tmp_path, "Klaus East Window")
    assert first == second
    assert first.startswith("klaus-east-window-")


def test_load_or_create_frame_id_slugifies_the_site_name(tmp_path):
    frame_id = load_or_create_frame_id(tmp_path, "  Weird!! Site_Name  ")
    assert frame_id.startswith("weird-site-name-")


# --- revision_filenames -----------------------------------------------------------------


def test_revision_filenames_names_by_revision():
    assert revision_filenames(2) == {
        "mesh": "mesh.r2.glb",
        "collision": "collision.r2.glb",
        "cameras": "cameras.r2.json",
    }


# --- publish_revision --------------------------------------------------------------------


def _write_revision_files(tmp_path, revision, *, textured_mesh=True, mesh_extents=(4.0, 3.0, 2.0)):
    filenames = revision_filenames(revision)
    mesh = _textured_box(mesh_extents) if textured_mesh else _flat_box(mesh_extents)
    mesh.export(tmp_path / filenames["mesh"])
    _flat_box((0.5, 0.5, 0.5)).export(tmp_path / filenames["collision"])
    cameras = [{"id": "0001", "thumb": "thumbs/0001.jpg", "position": [0, 0, 0]}]
    (tmp_path / filenames["cameras"]).write_text(json.dumps(cameras))


def _write_minimal_package(
    tmp_path,
    *,
    revision=1,
    quality="full",
    textured_mesh=True,
    mesh_extents=(4.0, 3.0, 2.0),
    with_thumb=True,
):
    _write_revision_files(
        tmp_path, revision, textured_mesh=textured_mesh, mesh_extents=mesh_extents
    )
    thumbs_dir = tmp_path / "thumbs"
    thumbs_dir.mkdir(exist_ok=True)
    if with_thumb:
        Image.new("RGB", (10, 10)).save(thumbs_dir / "0001.jpg")
    scale = ScaleEstimate(method="gps", scale=1.0, residual_m=0.1)
    publish_revision(
        tmp_path,
        revision=revision,
        quality=quality,
        name="site",
        scale=scale,
        gravity_residual_deg=0.1,
        frame_id="site-aaaa",
        aligned_to_preview=(revision >= 2),
        alignment_residual_m=0.02 if revision >= 2 else None,
    )
    return tmp_path


def test_publish_revision_writes_scene_json_pointing_at_revision_files(tmp_path):
    _write_minimal_package(tmp_path, revision=2, quality="full")
    scene = json.loads((tmp_path / "scene.json").read_text())

    assert scene["revision"] == 2
    assert scene["quality"] == "full"
    assert scene["mesh"]["file"] == "mesh.r2.glb"
    assert scene["collision"]["file"] == "collision.r2.glb"
    assert scene["cameras"] == "cameras.r2.json"
    assert scene["frame"] == {
        "id": "site-aaaa",
        "aligned_to_preview": True,
        "alignment_residual_m": 0.02,
    }
    assert scene["pipeline_version"]


def test_publish_revision_prunes_revision_minus_2_files(tmp_path):
    _write_revision_files(tmp_path, 1)  # a stale revision 1, as if preview published earlier
    _write_minimal_package(tmp_path, revision=3, quality="full")  # publishing revision 3

    r1 = revision_filenames(1)
    for name in r1.values():
        assert not (tmp_path / name).exists()  # revision 1 = 3 - 2, pruned
    r3 = revision_filenames(3)
    for name in r3.values():
        assert (tmp_path / name).exists()  # the just-published revision stays


def test_publish_revision_does_not_touch_thumbs(tmp_path):
    _write_minimal_package(tmp_path, revision=1)
    assert (tmp_path / "thumbs" / "0001.jpg").exists()
    _write_revision_files(tmp_path, 2)
    publish_revision(
        tmp_path,
        revision=2,
        quality="full",
        name="site",
        scale=ScaleEstimate(method="gps", scale=1.0, residual_m=0.1),
        gravity_residual_deg=0.1,
        frame_id="site-aaaa",
        aligned_to_preview=True,
        alignment_residual_m=0.01,
    )
    assert (tmp_path / "thumbs" / "0001.jpg").exists()  # shared across revisions, never pruned


def test_publish_revision_lists_scale_candidates(tmp_path):
    _write_revision_files(tmp_path, 1)
    winner = ScaleEstimate(method="scalebar_aruco", scale=1.001, residual_m=0.004)
    candidates = [ScaleEstimate(method="gps", scale=1.05, residual_m=1.2), winner]
    thumbs_dir = tmp_path / "thumbs"
    thumbs_dir.mkdir()
    Image.new("RGB", (10, 10)).save(thumbs_dir / "0001.jpg")

    publish_revision(
        tmp_path,
        revision=1,
        quality="preview",
        name="site",
        scale=winner,
        gravity_residual_deg=0.1,
        frame_id="site-aaaa",
        aligned_to_preview=False,
        alignment_residual_m=None,
        scale_candidates=candidates,
    )
    scene = json.loads((tmp_path / "scene.json").read_text())

    assert scene["scale_method"] == "scalebar_aruco"
    assert scene["scale_candidates"] == [
        {"method": "gps", "scale": 1.05, "residual_m": 1.2},
        {"method": "scalebar_aruco", "scale": 1.001, "residual_m": 0.004},
    ]


# --- validate_package ---------------------------------------------------------------------


def test_validate_package_passes_on_a_well_formed_package(tmp_path):
    _write_minimal_package(tmp_path)
    assert validate_package(tmp_path) == []


def test_validate_package_flags_missing_files(tmp_path):
    _write_minimal_package(tmp_path)
    (tmp_path / "mesh.r1.glb").unlink()
    problems = validate_package(tmp_path)
    assert any("mesh.r1.glb" in p for p in problems)


def test_validate_package_flags_untextured_mesh(tmp_path):
    _write_minimal_package(tmp_path, textured_mesh=False)
    problems = validate_package(tmp_path)
    assert any("no texture" in p for p in problems)


def test_validate_package_does_not_flag_untextured_collision(tmp_path):
    """collision.r<rev>.glb is deliberately untextured (lighter, for tool snapping) -- must never
    be flagged for missing texture."""
    _write_minimal_package(tmp_path)
    problems = validate_package(tmp_path)
    assert not any("collision" in p for p in problems)


def test_validate_package_flags_implausible_scale(tmp_path):
    _write_minimal_package(tmp_path, mesh_extents=(0.001, 0.001, 0.001))  # a millimetre-scale bug
    problems = validate_package(tmp_path)
    assert any("bbox diagonal" in p for p in problems)


def test_validate_package_flags_missing_referenced_thumb(tmp_path):
    _write_minimal_package(tmp_path, with_thumb=False)
    problems = validate_package(tmp_path)
    assert any("missing thumb" in p for p in problems)


def test_validate_package_flags_scale_method_none(tmp_path):
    _write_minimal_package(tmp_path)
    scene = json.loads((tmp_path / "scene.json").read_text())
    scene["scale_method"] = "none"
    (tmp_path / "scene.json").write_text(json.dumps(scene))
    problems = validate_package(tmp_path)
    assert any("scale_method is 'none'" in p for p in problems)


def test_validate_package_flags_old_flat_package(tmp_path):
    """Pre-0.3.0 packages had a flat scene.json with no 'revision'/'cameras' field -- must be a
    clear error, not a crash or a silent pass."""
    _textured_box().export(tmp_path / "mesh.glb")
    (tmp_path / "scene.json").write_text(json.dumps({"scale_method": "gps"}))
    problems = validate_package(tmp_path)
    assert len(problems) == 1
    assert "old flat package" in problems[0]


def test_validate_package_missing_scene_json(tmp_path):
    assert validate_package(tmp_path) == ["missing scene.json"]
