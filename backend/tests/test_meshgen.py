import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import trimesh
from PIL import Image

from server import meshgen
from server import part_templates as T
from server.config import get_settings
from server.models import Dims, Part

AC_DIMS = Dims(w=614.68, h=368.3, d=515.62)
AC_ANSWER = {
    "template": "box_appliance",
    "params": {"front_frac": 0.4, "grille1_count": 99},
    "colors": {"body": "#F0F0F0", "grille_bg": "not-a-colour"},
    "finishes": {"body": "plastic", "sleeve": "unobtainium", "nope": "chrome"},
    "photo": {"face": "front", "rotate_cw": 0, "single": True},
    "shape_class": "template",
}


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


@pytest.fixture
def photo(tmp_path) -> Path:
    """A red 200x120 'product' on a white catalogue background."""
    path = tmp_path / "photo.jpg"
    img = Image.new("RGB", (400, 300), (255, 255, 255))
    img.paste((200, 30, 30), (100, 90, 300, 210))
    img.save(path)
    return path


def _part(**kw) -> Part:
    return Part(**{"id": "ac", "name": "Wall AC", "dims_mm": AC_DIMS, **kw})


def fake_llm(monkeypatch, replies: dict) -> list:
    """Stub `llm.chat`: role -> reply text (or an exception to raise). Returns the call log."""
    calls = []

    async def chat(role, messages, **kw):
        calls.append({"role": role, "messages": messages, "kw": kw})
        reply = replies[role]
        if isinstance(reply, Exception):
            raise reply
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=reply))])

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _check_contract(scene: trimesh.Scene, dims: Dims) -> None:
    lo, hi = scene.bounds
    assert hi - lo == pytest.approx(np.array([dims.w, dims.h, dims.d]) / 1000, abs=1e-5)
    assert [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]] == pytest.approx([0, 0, 0], abs=1e-6)


# --- parse_plan: JSON validation ---------------------------------------------------------------


def test_parse_plan_clamps_to_the_catalogue():
    plan = meshgen.parse_plan(AC_ANSWER, with_photo=True)
    assert plan.template == "box_appliance"
    assert plan.params["front_frac"] == 0.4
    assert plan.params["grille1_count"] == 40  # clipped to the spec's range
    assert plan.colors["body"] == "#F0F0F0"
    assert plan.colors["grille_bg"] == T.TEMPLATES["box_appliance"][3]["grille_bg"]  # bad hex
    assert plan.finishes == {"body": "plastic"}  # unknown finish and unknown role dropped
    assert (plan.face, plan.rotate_cw, plan.single, plan.shape_class) == (
        "front",
        0,
        True,
        "template",
    )


def test_parse_plan_bad_answer_falls_back_to_defaults():
    plan = meshgen.parse_plan(
        {
            "template": "spaceship",
            "photo": {"face": "diagonal", "rotate_cw": 45, "single": False},
            "shape_class": "blob",
        },
        with_photo=True,
    )
    assert plan.template == "box"  # unknown template -> generic box
    assert (plan.face, plan.rotate_cw, plan.single, plan.shape_class) == (
        "front",
        0,
        False,
        "template",
    )


def test_parse_plan_sink_front_photo_means_top():
    plan = meshgen.parse_plan({"template": "sink", "photo": {"face": "front"}}, with_photo=True)
    assert plan.face == "top"


def test_parse_plan_text_path_keeps_template_colours():
    plan = meshgen.parse_plan({**AC_ANSWER, "shape_class": "moulded"}, with_photo=False)
    assert plan.colors == T.TEMPLATES["box_appliance"][3]  # colours need a photo
    assert plan.face is None and plan.shape_class == "moulded"


def test_parse_strips_think_and_fences():
    assert meshgen._parse('<think>hm</think>\n```json\n{"a": 1}\n```') == {"a": 1}
    with pytest.raises(ValueError):
        meshgen._parse("[1, 2]")


# --- build / project: the api.md §2 contract ----------------------------------------------------


@pytest.mark.parametrize("template", ["box_appliance", "bent_strip", "cylinder_tank", "sink"])
def test_build_is_exact_size_at_the_mount_face(template):
    plan = meshgen.parse_plan({"template": template}, with_photo=False)
    _check_contract(meshgen.build(plan, AC_DIMS), AC_DIMS)


def test_build_photo_mode_drops_front_relief():
    plan = meshgen.parse_plan(AC_ANSWER, with_photo=True)
    flat = meshgen.build(plan, AC_DIMS)
    plan.single = False  # no photo going on: keep the slats
    relief = meshgen.build(plan, AC_DIMS)
    assert "grille_bg" not in flat.geometry and "grille_bg" in relief.geometry


def test_project_puts_the_photo_on_the_front_only(photo, tmp_path):
    plan = meshgen.parse_plan(AC_ANSWER, with_photo=True)
    scene = meshgen.project(meshgen.build(plan, AC_DIMS), photo, "front")
    _check_contract(scene, AC_DIMS)
    normals = scene.geometry["photo"].face_normals
    assert (normals @ [0, 0, 1] >= meshgen.FACE_DOT).all()
    out = tmp_path / "m.glb"
    scene.export(out)
    back = trimesh.load(out, force="scene")
    assert back.geometry["photo"].visual.material.baseColorTexture is not None
    _check_contract(back, AC_DIMS)


def test_decal_box_is_exact_size_in_the_edge_colour(photo):
    scene = meshgen.decal_box(AC_DIMS, photo, "front")
    _check_contract(scene, AC_DIMS)
    body = scene.geometry["body"].visual.material.baseColorFactor
    assert body[0] > 100 and body[1] < 20  # the red product's edge colour, not grey
    assert "photo" in scene.geometry


def test_aspect_guess(photo):
    assert meshgen.aspect_guess(Dims(w=200, h=120, d=40), photo) == "front"
    assert meshgen.aspect_guess(Dims(w=40, h=120, d=200), photo) == "right"
    assert meshgen.aspect_guess(AC_DIMS, photo.with_name("missing.jpg")) == "front"


# --- ask: one call, fallbacks, cache ------------------------------------------------------------


@pytest.mark.asyncio
async def test_ask_is_one_vision_call_then_cached(monkeypatch, photo):
    calls = fake_llm(monkeypatch, {"asset": json.dumps(AC_ANSWER)})
    plan = await meshgen.ask(_part(), photo)
    assert plan.template == "box_appliance" and plan.face == "front"
    assert [c["role"] for c in calls] == ["asset"]
    content = calls[0]["messages"][0]["content"]
    assert content[1]["image_url"]["url"].startswith("data:image/jpeg;base64,")
    assert calls[0]["kw"]["response_format"] == {"type": "json_object"}

    again = await meshgen.ask(_part(), photo)  # cache hit: no second call
    assert again == plan and len(calls) == 1

    monkeypatch.setenv("OFFLINE", "true")  # a warmed part works offline, still 0 calls
    get_settings.cache_clear()
    assert await meshgen.ask(_part(), photo) == plan and len(calls) == 1


@pytest.mark.asyncio
async def test_ask_falls_back_to_the_text_model(monkeypatch, photo):
    calls = fake_llm(
        monkeypatch, {"asset": RuntimeError("vision down"), "extract": json.dumps(AC_ANSWER)}
    )
    plan = await meshgen.ask(_part(material="plastic"), photo)
    assert [c["role"] for c in calls] == ["asset", "extract"]
    text_msgs = calls[1]["messages"]
    assert all(isinstance(m["content"], str) for m in text_msgs)  # no image on the text path
    assert "plastic" in text_msgs[-1]["content"]
    assert plan.face is None and plan.colors == T.TEMPLATES["box_appliance"][3]


@pytest.mark.asyncio
async def test_ask_never_raises(monkeypatch, photo):
    fake_llm(monkeypatch, {"asset": RuntimeError("down"), "extract": "not json"})
    assert await meshgen.ask(_part(), photo) is None
    monkeypatch.setenv("OFFLINE", "true")  # cache miss offline: None, not OfflineMiss
    get_settings.cache_clear()
    assert await meshgen.ask(_part(name="other"), None) is None


# --- grok_scad: offline OpenSCAD job, mocked client ---------------------------------------------

SCAD_CODE = '```openscad\npaint("#EEEEEE") box(z1=D);\n```'


def _fake_build(results: list):
    def build(exe, code, work, dims):
        return results.pop(0)

    return build


def _slab(dims) -> list:
    w, h, d = dims
    return [(trimesh.creation.box(bounds=[[-w / 2, 0, 0], [w / 2, h, d]]), "#EEEEEE")]


@pytest.mark.asyncio
async def test_grok_scad_fixes_a_compile_error_then_writes_the_glb(monkeypatch, photo, tmp_path):
    dims = Dims(w=25.4, h=69.85, d=114.3)
    part = _part(id="hanger", dims_mm=dims)
    calls = fake_llm(monkeypatch, {"asset_scad": SCAD_CODE})
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/openscad")
    builds = [
        ([], "ERROR: syntax error in model.scad, line 1"),
        (_slab((25.4, 69.85, 114.3)), None),
    ]
    monkeypatch.setattr(meshgen, "build_scad", _fake_build(builds))
    out = tmp_path / "scad.glb"

    assert await meshgen.grok_scad(part, photo, out)
    assert [c["role"] for c in calls] == ["asset_scad", "asset_scad"]
    assert "syntax error" in calls[1]["messages"][-1]["content"]
    assert calls[0]["kw"]["max_retries"] == 0 and calls[0]["kw"]["timeout"] == 600
    _check_contract(trimesh.load(out, force="scene"), dims)

    assert await meshgen.grok_scad(part, photo, out)  # already made: no new call
    assert len(calls) == 2


@pytest.mark.asyncio
async def test_grok_scad_skips_without_openscad(monkeypatch, photo, tmp_path):
    calls = fake_llm(monkeypatch, {"asset_scad": SCAD_CODE})
    monkeypatch.setattr(meshgen, "openscad", lambda: None)
    assert not await meshgen.grok_scad(_part(), photo, tmp_path / "scad.glb")
    assert calls == []


@pytest.mark.asyncio
async def test_grok_scad_gives_up_after_two_failed_builds(monkeypatch, photo, tmp_path):
    calls = fake_llm(monkeypatch, {"asset_scad": SCAD_CODE})
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/openscad")
    monkeypatch.setattr(meshgen, "build_scad", _fake_build([([], "ERROR: a"), ([], "ERROR: b")]))
    assert not await meshgen.grok_scad(_part(), photo, tmp_path / "scad.glb")
    assert len(calls) == 2 and not (tmp_path / "scad.glb").exists()


@pytest.mark.skipif(meshgen.openscad() is None, reason="OpenSCAD not installed")
def test_build_scad_with_real_openscad(tmp_path):
    dims = (600.0, 400.0, 500.0)
    code = (
        'paint("#EEEEEE") box(z1=D-20);\npaint("#333333") panel("front", [0, H/2], [W/2, H/2], 20);'
    )
    pieces, err = meshgen.build_scad(meshgen.openscad(), code, tmp_path, dims)
    assert err is None and len(pieces) == 2
    scene, raw_err = meshgen.assemble(pieces, dims)
    assert raw_err == pytest.approx(0, abs=1e-6)
    _check_contract(scene, Dims(w=600, h=400, d=500))
