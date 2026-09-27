import base64
import io
from pathlib import Path

import httpx
import numpy as np
import pytest
import respx
import trimesh
from fastapi.testclient import TestClient
from PIL import Image, ImageDraw

from server import agent, assets, finish, jobs, meshgen
from server import part_templates as T
from server.app import app
from server.config import get_settings
from server.models import Asset, Dims, Finish, Part

DIMS = Dims(w=111, h=111, d=92)
PLAN = meshgen.Plan(
    template="fixture",
    params={},
    colors=dict(T.TEMPLATES["fixture"][3]),
    finishes={"body": "chrome"},
    face="front",
    rotate_cw=0,
    single=True,
    shape_class="template",
)


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()


def _disc(color, dx: float = 0.0) -> Image.Image:
    """A 'shower head': a disc on a white catalogue background, shifted by `dx` of the width."""
    img = Image.new("RGB", (256, 256), (255, 255, 255))
    x = 256 * dx
    ImageDraw.Draw(img).ellipse((40 + x, 40, 216 + x, 216), fill=color)
    return img


def _b64(img: Image.Image) -> str:
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=95)
    return base64.b64encode(buf.getvalue()).decode()


def _part(tier: str = "llm", **kw) -> Part:
    part = Part(id="shower", name="Shower head", dims_mm=DIMS, finish="Chrome", **kw)
    part.asset = Asset(tier=tier, status="ready")
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True, exist_ok=True)
    _disc((200, 200, 205)).save(pdir / "image.jpg", quality=95)
    assets.proxy_mesh(DIMS).export(pdir / "model.glb")
    jobs.save_part(part)
    return part


@pytest.fixture
def plan(monkeypatch):
    async def ask(part, photo):
        return PLAN

    monkeypatch.setattr(meshgen, "ask", ask)


def _mock_edit(router, img: Image.Image):
    body = {"data": [{"b64_json": _b64(img)}], "usage": {"cost_in_usd_ticks": 700_000_000}}
    return router.post(finish.EDIT_URL).mock(return_value=httpx.Response(200, json=body))


def _check_contract(glb: Path) -> trimesh.Scene:
    """api.md §2: bbox == dims in metres, origin at the mount-face centre, part toward +Z."""
    scene = trimesh.load(glb, force="scene")
    lo, hi = scene.bounds
    assert hi - lo == pytest.approx(np.array([DIMS.w, DIMS.h, DIMS.d]) / 1000, abs=1e-5)
    assert [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]] == pytest.approx([0, 0, 0], abs=1e-6)
    return scene


def test_registered_and_helpers(tmp_path):
    photo, same, moved = (tmp_path / f"{n}.jpg" for n in ("photo", "same", "moved"))
    _disc((200, 200, 205)).save(photo)
    _disc((20, 20, 20)).save(same)
    _disc((20, 20, 20), dx=0.1).save(moved)
    ok, shift, iou = finish.registered(photo, same)
    assert ok and shift < 0.01 and iou > 0.95
    assert not finish.registered(photo, moved)[0]
    assert (finish.pbr_finish("Matte Black"), finish.pbr_finish("brushed nickel")) == (
        "painted",
        "brushed",
    )
    assert finish.pbr_finish("Polished Chrome") == "chrome"
    colors = {"jacket": "#949494", "top": "#888888", "pipe": "#C0C0C0", "base": "#555555"}
    assert finish.product_roles(colors, {"jacket": 9, "top": 1, "pipe": 1, "base": 2}) == [
        "jacket",
        "top",
    ]


@pytest.mark.asyncio
async def test_llm_variant_is_retextured_at_the_same_size(plan):
    part = _part()
    with respx.mock(assert_all_called=True) as router:
        route = _mock_edit(router, _disc((20, 20, 20)))
        res = await finish.apply(part, "Matte Black")
    assert route.call_count == 1
    sent = route.calls[0].request
    assert b"matte black" in sent.content.lower() and b"data:image/jpeg;base64," in sent.content
    assert res["source"] == "imagine" and res["reason"] is None
    assert res["model_url"] == "/parts/shower/model-matte-black.glb"
    assert res["image_url"] == "/parts/shower/finish-matte-black.jpg"
    assert res["cost_usd"] == pytest.approx(0.07)
    pdir = assets.part_dir("shower")
    assert Image.open(pdir / "finish-matte-black.jpg").size == (256, 256)
    scene = _check_contract(pdir / "model-matte-black.glb")
    assert "photo" in scene.geometry  # the variant photo is on
    body = scene.geometry["body"].visual.material  # template body in the variant's colour
    assert max(body.baseColorFactor[:3]) < 60


@pytest.mark.asyncio
async def test_not_listed_finish_is_labelled_and_spoken(plan):
    part = _part()
    with respx.mock() as router:
        _mock_edit(router, _disc((20, 20, 20)))
        res = await finish.apply(part, "matte black")
    assert res["label"] == finish.NOT_LISTED == "Not a listed finish"
    assert "not a listed finish" in res["spoken"]

    part = _part(finishes=[Finish(name="Matte Black", hex="#1A1A1A")])
    res = await finish.apply(part, "matte black")  # same render, cached: only the label differs
    assert res["label"] is None and res["spoken"] == "Here it is in matte black."


@pytest.mark.asyncio
async def test_outline_moved_is_rejected(plan):
    part = _part()
    with respx.mock() as router:
        _mock_edit(router, _disc((20, 20, 20), dx=0.1))
        res = await finish.apply(part, "matte black")
    assert res["source"] == "tint" and res["model_url"] is None
    assert "outline moved" in res["reason"]
    assert res["spoken"].startswith("I've tinted it matte black; I couldn't render")
    assert not (assets.part_dir("shower") / "model-matte-black.glb").exists()


@pytest.mark.asyncio
async def test_cad_part_is_tint_only():
    part = _part(tier="cad")
    with respx.mock(assert_all_called=False) as router:
        route = _mock_edit(router, _disc((20, 20, 20)))
        res = await finish.apply(part, "chrome")
    assert route.call_count == 0
    assert (res["source"], res["model_url"], res["label"]) == ("tint", None, None)
    assert res["spoken"] == "Tinted it chrome."


@pytest.mark.asyncio
async def test_cache_hit_rebuilds_offline_without_a_call(plan, monkeypatch):
    part = _part()
    with respx.mock() as router:
        route = _mock_edit(router, _disc((20, 20, 20)))
        await finish.apply(part, "matte black")
        glb = assets.part_dir("shower") / "model-matte-black.glb"
        glb.unlink()  # e.g. a fresh data dir with only the warmed cache
        monkeypatch.setenv("OFFLINE", "true")
        get_settings.cache_clear()
        res = await finish.apply(part, "matte black")
    assert route.call_count == 1
    assert res["source"] == "imagine" and glb.exists()


@pytest.mark.asyncio
async def test_offline_miss_tints(plan, monkeypatch):
    part = _part()
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    res = await finish.apply(part, "matte black")
    assert res["source"] == "tint" and res["reason"] == "offline, finish not cached"


@pytest.mark.asyncio
@pytest.mark.parametrize("tier", ["proxy", "ai_mesh"])
async def test_proxy_and_ai_mesh_are_reprojected(tier, monkeypatch):
    async def no_plan(part, photo):
        return None

    monkeypatch.setattr(meshgen, "ask", no_plan)
    part = _part(tier=tier)
    with respx.mock() as router:
        _mock_edit(router, _disc((20, 20, 20)))
        res = await finish.apply(part, "matte black")
    assert res["source"] == "imagine"
    scene = _check_contract(assets.part_dir("shower") / "model-matte-black.glb")
    assert "photo" in scene.geometry


def test_finish_routes(plan):
    _part()
    client = TestClient(app)
    assert client.post("/parts/nope/finish", json={"finish": "black"}).status_code == 404
    assert client.post("/parts/shower/finish", json={"finish": "  "}).status_code == 422
    with respx.mock() as router:
        _mock_edit(router, _disc((20, 20, 20)))
        body = client.post("/parts/shower/finish", json={"finish": "matte black"}).json()
    assert body["source"] == "imagine"
    glb = client.get(body["model_url"])
    assert glb.status_code == 200 and glb.content[:4] == b"glTF"
    assert client.get(body["image_url"]).headers["content-type"] == "image/jpeg"
    assert client.get("/parts/shower/model-nope.glb").status_code == 404


@pytest.mark.asyncio
async def test_agent_set_finish_is_server_side(plan, monkeypatch):
    _part()

    async def no_llm(*a, **kw):
        raise AssertionError("fast path must not call the LLM")

    monkeypatch.setattr(agent, "chat", no_llm)
    with respx.mock() as router:
        _mock_edit(router, _disc((20, 20, 20)))
        result = await agent.handle_command(
            "s1", "show it in matte black", {"selected_part_id": "shower"}
        )
    args = result["actions"][0]["args"]
    assert result["actions"][0]["name"] == "set_finish"
    assert args == {
        "name": "matte black",
        "model_url": "/parts/shower/model-matte-black.glb",
        "label": "Not a listed finish",
    }
    assert "not a listed finish" in result["reply"]


@pytest.mark.asyncio
async def test_agent_set_finish_tints_without_a_render(monkeypatch):
    """No selected part, or a tint-only result: the old `{name}` action, nothing else."""
    result = await agent.handle_command("s2", "show it in matte black", {})
    assert result["actions"] == [{"name": "set_finish", "args": {"name": "matte black"}}]

    _part(tier="cad")
    result = await agent.handle_command("s2", "show it in chrome", {"selected_part_id": "shower"})
    assert result["actions"] == [{"name": "set_finish", "args": {"name": "chrome"}}]
    assert result["reply"] == "Tinted it chrome."
