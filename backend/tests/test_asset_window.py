"""assetgen: the window asset on the server -- the window template, the Grok retry of a poor
template answer and who made it (meshgen), and that provenance in part.json (assets). No network,
no keys: every LLM call is stubbed."""

import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import trimesh
from PIL import Image

from server import agent, assets, meshgen
from server import part_templates as T
from server.config import get_settings
from server.models import Dims, Part

WINDOW = Dims(w=1486, h=1181, d=80)
NAME = "58.5 in. x 46.5 in. Double Hung White Vinyl Replacement Window"
WINDOW_ANSWER = {
    "template": "window",
    "params": {"style": "double_hung", "grid_x": 2, "grid_y": 1},
    "colors": {"frame": "#F2F2F2", "glass": "#9FB4C4"},
    "finishes": {"frame": "plastic"},
    "photo": {"face": "front", "rotate_cw": 0, "single": True},
    "shape_class": "template",
}
PANEL_ANSWER = {**WINDOW_ANSWER, "template": "flat_panel", "params": {"frame_w": 0.1}}


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


@pytest.fixture
def photo(tmp_path) -> Path:
    """A white window frame with grey glass on a white catalogue background."""
    path = tmp_path / "window.jpg"
    img = Image.new("RGB", (500, 400), (255, 255, 255))
    img.paste((230, 230, 228), (50, 40, 450, 360))
    img.paste((120, 140, 160), (80, 70, 420, 330))
    img.save(path)
    return path


def _part(**kw) -> Part:
    return Part(**{"id": "win", "name": NAME, "dims_mm": WINDOW, **kw})


def fake_llm(monkeypatch, replies: dict, answered: dict | None = None) -> list:
    """Stub `llm.chat`: role -> reply text (or an exception); `answered`: role -> the model name
    the response says answered it (a Groq call that fell back to xAI says grok-*)."""
    calls = []

    async def chat(role, messages, **kw):
        calls.append(role)
        reply = replies[role]
        if isinstance(reply, Exception):
            raise reply
        msg = SimpleNamespace(content=reply)
        return SimpleNamespace(
            choices=[SimpleNamespace(message=msg)], model=(answered or {}).get(role)
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _bounds_mm(glb: Path) -> np.ndarray:
    lo, hi = trimesh.load(glb, force="scene").bounds
    return (hi - lo) * 1000


# --- the window template -------------------------------------------------------------------------


@pytest.mark.parametrize("style", ["double_hung", "single_hung", "slider", "casement", "fixed"])
def test_every_window_style_fills_its_box_exactly(style):
    scene, info = T.build("window", WINDOW.model_dump(), {"style": style, "panes": 2, "grid_x": 1})
    assert info["fit_scale"] == pytest.approx([1, 1, 1], abs=0.01)
    lo, hi = scene.bounds
    assert (hi - lo) * 1000 == pytest.approx([WINDOW.w, WINDOW.h, WINDOW.d], abs=0.01)
    assert [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]] == pytest.approx([0, 0, 0], abs=1e-6)
    assert {"frame", "glass"} <= set(scene.geometry)
    # glass reads as glass without being asked (DEFAULT_FINISH): glossy, not metal
    glass = scene.geometry["glass"].visual.material
    assert (glass.metallicFactor, glass.roughnessFactor) == T.FINISHES["glass"]


def test_a_thin_frame_has_no_lock_and_stays_in_its_box():
    scene, _ = T.build("window", {"w": 600, "h": 900, "d": 20}, {"style": "slider"})
    lo, hi = scene.bounds
    assert (hi - lo)[2] * 1000 == pytest.approx(20, abs=0.01)
    assert "hardware" not in scene.geometry


def test_the_catalogue_offers_the_window():
    text = T.catalogue()
    assert "- window: window unit or replacement window frame" in text
    assert "style=double_hung (double_hung|single_hung|slider|casement|fixed)" in text
    assert "(a window: use window)" in text  # flat_panel points windows at it
    template, params, colors = T.validate("window", {"style": "bay", "grid_x": 99}, None)
    assert template == "window" and params["style"] == "double_hung" and params["grid_x"] == 6
    assert colors["glass"] == "#A9C0CE"


# --- meshgen: the template a known shape needs, the Grok retry, who made it ------------------------


@pytest.mark.parametrize(
    ("name", "want"),
    [
        (NAME, "window"),
        ("American Craftsman 70 Series Vinyl Replacement Window with Screen", "window"),
        ("36 in. x 48 in. Vinyl Slider Window", "window"),
        ("5,000 BTU 115-Volt Window Air Conditioner", "box_appliance"),
        ("Window Screen Frame Kit", None),
        ("Everbilt Window Lock", None),
        ("Casement Window Crank", None),
        ("5/16 in. x 84 in. Aluminum Screen Frame", None),
        ("Rheem 40 Gal. Water Heater", "cylinder_tank"),
        ("5 in. Aluminum Hidden Gutter Hanger", None),
    ],
)
def test_expected_template(name, want):
    assert meshgen.expected_template(_part(name=name)) == want


def test_made_by_names_the_model_that_answered():
    assert (
        meshgen.made_by("groq", "qwen/qwen3.8-27b", "qwen/qwen3.8-27b") == "groq:qwen/qwen3.8-27b"
    )
    # llm.chat re-sent the Groq call to xAI (LLM_FALLBACK): the response says grok
    by = meshgen.made_by("groq", "qwen/qwen3.8-27b", "grok-4.20-0309-non-reasoning")
    assert by == "xai:grok-4.20-0309-non-reasoning"
    assert meshgen.made_by("groq", "qwen/qwen3.8-27b", None) == "groq:qwen/qwen3.8-27b"


@pytest.mark.asyncio
async def test_a_window_answered_as_a_panel_is_asked_again_on_grok(monkeypatch, photo):
    monkeypatch.setenv("LLM_ASSET_RETRY", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    calls = fake_llm(
        monkeypatch,
        {"asset": json.dumps(PANEL_ANSWER), "asset_retry": json.dumps(WINDOW_ANSWER)},
        {"asset": "qwen/qwen3.8-27b", "asset_retry": "grok-4.20-0309-non-reasoning"},
    )
    plan = await meshgen.ask(_part(), photo)
    assert calls == ["asset", "asset_retry"]
    assert plan.template == "window" and plan.retried
    assert plan.made_by == "xai:grok-4.20-0309-non-reasoning"
    # both answers and who gave them are cached: the same plan again, no call (finish/resize rely
    # on this to rebuild without an LLM call)
    again = await meshgen.ask(_part(), photo)
    assert again == plan and calls == ["asset", "asset_retry"]


@pytest.mark.asyncio
async def test_a_good_answer_is_not_retried(monkeypatch, photo):
    monkeypatch.setenv("LLM_ASSET_RETRY", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    calls = fake_llm(
        monkeypatch, {"asset": json.dumps(WINDOW_ANSWER)}, {"asset": "qwen/qwen3.8-27b"}
    )
    plan = await meshgen.ask(_part(), photo)
    assert calls == ["asset"] and plan.template == "window" and not plan.retried
    assert plan.made_by == "groq:qwen/qwen3.8-27b"


@pytest.mark.asyncio
async def test_a_retry_that_does_no_better_keeps_the_first_answer(monkeypatch, photo):
    monkeypatch.setenv("LLM_ASSET_RETRY", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    calls = fake_llm(monkeypatch, {"asset": json.dumps(PANEL_ANSWER), "asset_retry": "nope"})
    plan = await meshgen.ask(_part(), photo)
    assert calls == ["asset", "asset_retry"]
    assert plan.template == "flat_panel" and not plan.retried


@pytest.mark.asyncio
async def test_no_answer_at_all_is_retried_on_grok(monkeypatch, photo):
    monkeypatch.setenv("LLM_ASSET_RETRY", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    calls = fake_llm(
        monkeypatch,
        {
            "asset": RuntimeError("groq down"),
            "extract": "not json",
            "asset_retry": json.dumps(WINDOW_ANSWER),
        },
    )
    plan = await meshgen.ask(_part(), photo)
    assert calls == ["asset", "extract", "asset_retry"] and plan.template == "window"


@pytest.mark.asyncio
async def test_the_retry_is_off_without_its_setting(monkeypatch, photo):
    calls = fake_llm(monkeypatch, {"asset": json.dumps(PANEL_ANSWER)})  # LLM_ASSET_RETRY="" here
    plan = await meshgen.ask(_part(), photo)
    assert calls == ["asset"] and plan.template == "flat_panel"


@pytest.mark.asyncio
async def test_resolve_asset_records_who_made_it(monkeypatch):
    fake_llm(
        monkeypatch, {"extract": json.dumps(WINDOW_ANSWER)}, {"extract": "openai/gpt-oss-120b"}
    )
    part = await assets.resolve_asset(_part())  # no image_url: the text path
    stored = json.loads((assets.part_dir("win") / "part.json").read_text())["asset"]
    assert part.asset.tier == "llm" and stored["tier"] == "llm"
    assert stored["template"] == "window" and stored["retried"] is False
    assert stored["made_by"] == "groq:openai/gpt-oss-120b"
    assert stored["seconds"] is not None and stored["seconds"] >= 0
    assert _bounds_mm(assets.part_dir("win") / "model.glb") == pytest.approx(
        [WINDOW.w, WINDOW.h, WINDOW.d], abs=0.01
    )
