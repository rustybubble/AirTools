"""assetgen: a part made to size (server/resize.py, POST /parts/{id}/resize) and a finish that
keeps a window's glass. No network, no keys: every LLM call is stubbed."""

import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import trimesh
from fastapi.testclient import TestClient
from PIL import Image

from server import agent, assets, jobs, meshgen, resize
from server import part_templates as T
from server.app import app
from server.config import get_settings
from server.models import Asset, Dims, Part

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


# --- made to size: POST /parts/{id}/resize -------------------------------------------------------


def _ready_part(tier: str, photo: Path | None = None) -> Part:
    part = _part()
    part.asset = Asset(tier=tier, status="ready", template="window" if tier == "llm" else None)
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True, exist_ok=True)
    if photo is not None:
        Image.open(photo).save(pdir / "image.jpg")
    if tier == "llm":
        scene, _ = T.build("window", WINDOW.model_dump())
        scene.export(pdir / "model.glb")
    else:
        assets.proxy_mesh(WINDOW).export(pdir / "model.glb")
    jobs.save_part(part)
    return part


@pytest.mark.asyncio
async def test_an_llm_part_is_rebuilt_from_its_cached_plan_at_the_new_size(monkeypatch, photo):
    calls = fake_llm(monkeypatch, {"asset": json.dumps(WINDOW_ANSWER)})
    part = _ready_part("llm", photo)
    await meshgen.ask(part, assets.part_dir("win") / "image.jpg")  # resolve_asset cached the plan
    assert calls == ["asset"]
    new = Dims(w=1487.3, h=1187.3, d=80)
    out = await resize.resize(part, new)
    assert calls == ["asset"]  # rebuilt from the cached plan: no LLM call
    assert out["source"] == "template" and out["tier"] == "llm" and not out["cached"]
    assert out["model_url"] == "/parts/win/model-size-1487x1187x80.glb"
    glb = assets.part_dir("win") / "model-size-1487x1187x80.glb"
    assert _bounds_mm(glb) == pytest.approx([new.w, new.h, new.d], abs=0.01)
    assert "photo" in trimesh.load(glb, force="scene").geometry  # the product photo went on
    again = await resize.resize(part, new)
    assert again["cached"] and again["model_url"] == out["model_url"]


@pytest.mark.asyncio
async def test_a_proxy_part_gets_a_new_box_and_a_mesh_is_stretched(photo):
    part = _ready_part("proxy", photo)
    out = await resize.resize(part, Dims(w=1400, h=1100, d=100))
    assert out["source"] == "box"
    glb = assets.part_dir("win") / "model-size-1400x1100x100.glb"
    assert _bounds_mm(glb) == pytest.approx([1400, 1100, 100], abs=0.01)

    part = _ready_part("ai_mesh")
    out = await resize.resize(part, Dims(w=1300, h=1000, d=90))
    assert out["source"] == "scaled"
    scene = trimesh.load(assets.part_dir("win") / "model-size-1300x1000x90.glb", force="scene")
    lo, hi = scene.bounds
    assert (hi - lo) * 1000 == pytest.approx([1300, 1000, 90], abs=0.01)
    assert lo[2] == pytest.approx(0, abs=1e-6)  # the mount face stays on the origin


def test_sizes_out_of_range_are_refused():
    part = _part()
    with pytest.raises(resize.ResizeError):
        resize.check(part, Dims(w=WINDOW.w * 2.2, h=WINDOW.h, d=WINDOW.d))
    with pytest.raises(resize.ResizeError):
        resize.check(part, Dims(w=WINDOW.w, h=WINDOW.h * 0.4, d=WINDOW.d))
    with pytest.raises(resize.ResizeError):
        resize.check(part, Dims(w=WINDOW.w, h=WINDOW.h, d=5))
    resize.check(part, Dims(w=WINDOW.w * 0.5, h=WINDOW.h * 2, d=WINDOW.d))
    assert (
        resize.slug(Dims(w=1486.4, h=1181, d=79.6), "Matte Black")
        == "size-1486x1181x80-matte-black"
    )


def test_the_resize_route(photo):
    client = TestClient(app)
    assert client.post("/parts/nope/resize", json={"w": 1, "h": 1, "d": 1}).status_code == 404
    part = _part()
    part.asset = Asset(tier="proxy", status="pending")
    jobs.save_part(part)
    assert client.post("/parts/win/resize", json={"w": 1400, "h": 1100, "d": 80}).status_code == 409
    _ready_part("proxy", photo)
    r = client.post("/parts/win/resize", json={"w": 5000, "h": 1100, "d": 80})
    assert r.status_code == 422 and "outside" in r.json()["detail"]
    r = client.post("/parts/win/resize", json={"w": 1400, "h": 1100, "d": 80})
    assert r.status_code == 200 and r.json()["source"] == "box"
    got = client.get(r.json()["model_url"])
    assert got.status_code == 200 and got.content[:4] == b"glTF"


@pytest.mark.asyncio
async def test_a_finish_the_seller_doesnt_render_is_left_to_the_headset_tint(monkeypatch, photo):
    fake_llm(monkeypatch, {"asset": json.dumps(WINDOW_ANSWER)})
    part = _ready_part("llm", photo)
    await meshgen.ask(part, assets.part_dir("win") / "image.jpg")

    async def tint_only(p, name):
        return {"source": "tint", "reason": "offline, finish not cached"}

    monkeypatch.setattr(resize.finish, "apply", tint_only)
    out = await resize.resize(part, Dims(w=1480, h=1180, d=80), "Bronze")
    assert out["finish"] == "Bronze" and out["finish_source"] == "tint"
    assert out["source"] == "template" and out["model_url"].endswith("-bronze.glb")


def test_a_finish_keeps_a_windows_glass():
    from server import finish

    colors = {"frame": "#F0F0F0", "sash": "#F0F0F0", "glass": "#E8F0F8", "grid": "#F0F0F0"}
    areas = {"frame": 1.0, "sash": 0.8, "glass": 3.0, "grid": 0.1}  # the panes are the largest
    assert finish.product_roles(colors, areas) == ["frame", "sash", "grid"]
