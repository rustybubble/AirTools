import ipaddress
import json
import socket
from pathlib import Path

import httpx
import numpy as np
import pytest
import respx
import trimesh
from httpx import Response

from server import assets, meshgen
from server.assets import (
    MAX_AI_RESIDUAL_PCT,
    ai_mesh,
    normalize_mesh,
    part_dir,
    proxy_mesh,
    resolve_asset,
)
from server.config import get_settings
from server.models import Dims, Part

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "assets"
NORMALIZED_FIXTURE = FIXTURES_DIR / "hunyuan3d_ac_unit_normalized.glb"
SOURCE_PHOTO = FIXTURES_DIR / "source_photo_window_ac.jpg"


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


@pytest.fixture(autouse=True)
def _resolve_example_com_as_public(monkeypatch):
    """fetch_image/_try_cad now route through `web.safe_get`, which DNS-checks the host via
    `is_public_url` -- stub the resolver so these example.com-based tests don't need (or risk)
    a real DNS lookup. A literal IP host still resolves to itself (matching real getaddrinfo),
    so a test that deliberately targets a private IP -- see test_fetch_image_rejects_private_ip
    -- isn't masked by this stub."""

    def fake(host, *args, **kwargs):
        try:
            target = str(ipaddress.ip_address(host))
        except ValueError:
            target = "93.184.216.34"
        family = socket.AF_INET6 if ":" in target else socket.AF_INET
        return [(family, socket.SOCK_STREAM, 6, "", (target, 0))]

    monkeypatch.setattr(socket, "getaddrinfo", fake)


def _make_part(**overrides) -> Part:
    defaults = {
        "id": "test-part",
        "name": "Test part",
        "dims_mm": Dims(w=127, d=38, h=45),
        "image_url": "https://example.com/photo.jpg",
    }
    defaults.update(overrides)
    return Part(**defaults)


# --- part_dir ---------------------------------------------------------------


def test_part_dir(tmp_path):
    assert part_dir("abc") == tmp_path / "parts" / "abc"


# --- normalize_mesh ----------------------------------------------------------


def test_normalize_mesh_rotated_offset_fixture():
    original = trimesh.load(NORMALIZED_FIXTURE, force="scene").to_geometry()
    extents_mm = original.extents * 1000.0  # (x, y, z) = (w, h, d) already, per fixture provenance
    dims = Dims(w=extents_mm[0], d=extents_mm[2], h=extents_mm[1])

    scrambled = original.copy()
    scrambled.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [1, 0, 0]))
    scrambled.apply_scale(2.5)
    scrambled.apply_translation([1.0, -2.0, 3.5])

    mesh, residual_pct = normalize_mesh(scrambled, dims, mount_face="-z", exact=True)

    got = mesh.extents
    want = np.array([dims.w, dims.h, dims.d]) / 1000.0
    assert got == pytest.approx(want, abs=1e-3)

    bmin, bmax = mesh.bounds
    assert bmin[2] == pytest.approx(0.0, abs=1e-6)
    assert (bmin[0] + bmax[0]) / 2 == pytest.approx(0.0, abs=1e-6)
    assert (bmin[1] + bmax[1]) / 2 == pytest.approx(0.0, abs=1e-6)

    # a pure rotation+uniform-scale+translation of the mesh has no real shape distortion, so a
    # uniform scale should have recovered the target almost exactly too
    assert residual_pct < 1.0


def test_normalize_mesh_decimates_large_meshes():
    original = trimesh.load(NORMALIZED_FIXTURE, force="scene").to_geometry()
    # subdivide well past MAX_FACES (8000 -> well over 20000)
    dense = original.subdivide()
    assert len(dense.faces) > 20_000
    dims = Dims(w=400, d=300, h=500)

    mesh, _ = normalize_mesh(dense, dims)

    assert len(mesh.faces) <= 20_000


# --- proxy_mesh ---------------------------------------------------------------


def test_proxy_mesh_color_round_trips(tmp_path):
    dims = Dims(w=127, d=38, h=45)
    box = proxy_mesh(dims, color_hex="#3355AA")

    out = tmp_path / "proxy.glb"
    box.export(out)

    reloaded = trimesh.load(out, force="scene").to_geometry()
    factor = reloaded.visual.material.baseColorFactor
    # glTF baseColorFactor is linear: sRGB #3355AA -> linear (0.033, 0.091, 0.402) * 255
    assert list(factor[:3]) == pytest.approx([8.5, 23.2, 102.5], abs=1)

    assert reloaded.extents == pytest.approx([0.127, 0.045, 0.038], abs=1e-6)
    assert reloaded.bounds[0][2] == pytest.approx(0.0, abs=1e-6)


def test_proxy_mesh_default_color_is_light_grey():
    box = proxy_mesh(Dims(w=100, d=100, h=100))
    factor = box.visual.material.baseColorFactor
    # light grey: all channels equal-ish and bright
    r, g, b = factor[0], factor[1], factor[2]
    assert r == pytest.approx(g, abs=1e-6)
    assert g == pytest.approx(b, abs=1e-6)
    assert r > 0.5


def test_hex_to_rgba_converts_srgb_to_linear():
    # #CCCCCC (sRGB 0.8) is linear 0.604; writing 0.8 raw renders as ~#E7E7E7
    assert assets._hex_to_rgba("#CCCCCC") == pytest.approx([0.604, 0.604, 0.604, 1.0], abs=1e-3)
    assert assets._hex_to_rgba("#000000")[:3] == [0.0, 0.0, 0.0]
    assert assets._hex_to_rgba("#FFFFFF") == pytest.approx([1.0, 1.0, 1.0, 1.0])
    assert assets._hex_to_rgba("#080808")[0] == pytest.approx(0x08 / 255 / 12.92)  # linear toe


# --- fetch_image: SSRF guard --------------------------------------------------


@pytest.mark.asyncio
async def test_fetch_image_rejects_private_ip(tmp_path, monkeypatch):
    """`part.image_url` comes from LLM/SerpApi output -- fetch_image must reject a private/
    internal target instead of handing `follow_redirects=True` a free pass at it."""

    def fake_getaddrinfo(host, *args, **kwargs):
        return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", ("169.254.169.254", 0))]

    monkeypatch.setattr(socket, "getaddrinfo", fake_getaddrinfo)

    from server.assets import fetch_image

    with respx.mock(assert_all_called=False) as router:
        ok = await fetch_image("http://internal.example/latest/meta-data/", tmp_path / "img.jpg")

    assert ok is False
    assert router.calls.call_count == 0
    assert not (tmp_path / "img.jpg").exists()


# --- resolve_asset: falls to proxy -------------------------------------------


@pytest.mark.asyncio
async def test_resolve_asset_falls_to_proxy_on_image_404(tmp_path):
    part = _make_part()
    with respx.mock(assert_all_called=True) as router:
        route = router.get("https://example.com/photo.jpg").mock(return_value=Response(404))
        result = await resolve_asset(part)

        assert result.asset.tier == "proxy"
        assert result.asset.status == "ready"
        assert route.call_count == 1

        # idempotent: second call must not re-fetch the image
        result2 = await resolve_asset(part)
        assert result2.asset.tier == "proxy"
        assert route.call_count == 1

    model_path = part_dir(part.id) / "model.glb"
    json_path = part_dir(part.id) / "part.json"
    assert model_path.exists()
    assert json_path.exists()
    assert not (part_dir(part.id) / "image.jpg").exists()


@pytest.mark.asyncio
async def test_ai_mesh_stops_after_zerogpu_quota_error(monkeypatch, tmp_path):
    calls = []

    def fake_generation(space_id, image_path, token):
        calls.append(space_id)
        raise RuntimeError("You have exceeded your ZeroGPU quota (90s requested vs. 59s left).")

    monkeypatch.setattr(assets, "_hunyuan_shape_generation", fake_generation)
    assert await assets.ai_mesh(tmp_path / "image.jpg") is None
    assert await assets.ai_mesh(tmp_path / "image.jpg") is None
    assert calls == ["tencent/Hunyuan3D-2.1"]


@pytest.mark.asyncio
async def test_resolve_asset_falls_to_proxy_when_ai_mesh_returns_none(monkeypatch):
    part = _make_part(id="test-part-2")
    jpg_bytes = SOURCE_PHOTO.read_bytes()

    async def _fake_ai_mesh(image_path):
        return None

    monkeypatch.setattr("server.assets.ai_mesh", _fake_ai_mesh)

    with respx.mock(assert_all_called=True) as router:
        router.get("https://example.com/photo.jpg").mock(
            return_value=Response(200, content=jpg_bytes, headers={"content-type": "image/jpeg"})
        )
        result = await resolve_asset(part)

    assert result.asset.tier == "proxy"
    assert result.asset.status == "ready"
    assert (part_dir(part.id) / "image.jpg").exists()


# --- resolve_asset: ai_mesh tier ---------------------------------------------


@pytest.mark.asyncio
async def test_resolve_asset_uses_ai_mesh_when_available(monkeypatch, tmp_path):
    dims = Dims(w=406, d=559, h=305)
    part = _make_part(id="test-part-3", dims_mm=dims)
    jpg_bytes = SOURCE_PHOTO.read_bytes()

    async def _fake_ai_mesh(image_path):
        assert image_path.exists()
        dest = tmp_path / "fake_ai_mesh_output.glb"
        dest.write_bytes(NORMALIZED_FIXTURE.read_bytes())
        return dest

    monkeypatch.setattr("server.assets.ai_mesh", _fake_ai_mesh)

    with respx.mock(assert_all_called=True) as router:
        router.get("https://example.com/photo.jpg").mock(
            return_value=Response(200, content=jpg_bytes, headers={"content-type": "image/jpeg"})
        )
        result = await resolve_asset(part)

    assert result.asset.tier == "ai_mesh"
    assert result.asset.status == "ready"

    json_path = part_dir(part.id) / "part.json"
    reloaded_part = Part.model_validate_json(json_path.read_text())
    assert reloaded_part.asset.tier == "ai_mesh"

    model_path = part_dir(part.id) / "model.glb"
    mesh = trimesh.load(model_path, force="scene").to_geometry()
    want = np.array([dims.w, dims.h, dims.d]) / 1000.0
    assert mesh.extents == pytest.approx(want, abs=1e-3)


@pytest.mark.asyncio
async def test_resolve_asset_rejects_ai_mesh_with_wrong_proportions(monkeypatch, tmp_path):
    """Live case: a 10-pack box photo for a thin hanger gave a 9000% residual. Stretching that
    to size is garbage, so fall back to the honest exact-size proxy."""
    part = _make_part(id="test-part-4", dims_mm=Dims(w=13, d=305, h=241))
    jpg_bytes = SOURCE_PHOTO.read_bytes()

    async def _fake_ai_mesh(image_path):
        dest = tmp_path / "cube.glb"
        trimesh.creation.box(extents=(1, 1, 1)).export(dest)
        return dest

    monkeypatch.setattr("server.assets.ai_mesh", _fake_ai_mesh)
    with respx.mock() as router:
        router.get("https://example.com/photo.jpg").mock(
            return_value=Response(200, content=jpg_bytes, headers={"content-type": "image/jpeg"})
        )
        result = await resolve_asset(part)

    assert result.asset.tier == "proxy"
    assert result.asset.scale_residual_pct == 0.0


# --- live, skipped by default (pyproject: addopts = "-m 'not live'") --------


@pytest.mark.asyncio
async def test_resolve_asset_offline_never_calls_ai_mesh(monkeypatch, tmp_path):
    part = _make_part(id="test-part-offline")
    jpg_bytes = SOURCE_PHOTO.read_bytes()

    async def _fail_ai_mesh(image_path):
        raise AssertionError("OFFLINE must never call ai_mesh (it's the HF Space)")

    monkeypatch.setattr("server.assets.ai_mesh", _fail_ai_mesh)
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()

    with respx.mock(assert_all_called=True) as router:
        router.get("https://example.com/photo.jpg").mock(
            return_value=Response(200, content=jpg_bytes, headers={"content-type": "image/jpeg"})
        )
        result = await resolve_asset(part)

    assert result.asset.tier == "proxy"
    assert result.asset.status == "ready"


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_ai_mesh_real_hunyuan_call():
    result = await ai_mesh(SOURCE_PHOTO)
    assert result is not None
    assert result.exists()
    mesh = trimesh.load(result, force="scene")
    assert mesh is not None
    result.unlink()


# --- normalize_mesh: upright orientation (the wall-AC grille-up bug) --------------------------


def _hunyuan_like_ac() -> trimesh.Trimesh:
    """A Hunyuan3D-2.1 wall AC in its canonical pose: +Y up, grille relief on +Z (the front),
    and too shallow, as Hunyuan guesses depth from one photo (raw extents ~2.0 x 1.23 x 0.90)."""
    body = trimesh.creation.box(bounds=[[-1.0, -0.62, -0.45], [1.0, 0.62, 0.40]])
    grille = trimesh.creation.box(bounds=[[-0.9, -0.5, 0.40], [0.9, 0.3, 0.45]])
    return trimesh.util.concatenate([body, grille])  # grille = the last 8 vertices


AC_DIMS = Dims(w=614.68, d=515.62, h=368.3)


def _grille_dir(mesh: trimesh.Trimesh) -> np.ndarray:
    """Where the grille sits, -1..1 per axis of the bbox."""
    lo, hi = mesh.bounds
    return (mesh.vertices[-8:].mean(axis=0) - (lo + hi) / 2) / ((hi - lo) / 2)


def test_normalize_mesh_free_search_lays_the_ac_grille_up():
    """The bug, still the behaviour for CAD files (unknown frame): aspect ratio alone swaps
    Hunyuan's too-shallow depth with the height."""
    mesh, _ = normalize_mesh(_hunyuan_like_ac(), AC_DIMS)
    assert abs(_grille_dir(mesh)[1]) > 0.8  # grille on top or bottom


def test_normalize_mesh_upright_keeps_the_ac_grille_on_the_front():
    mesh, residual = normalize_mesh(_hunyuan_like_ac(), AC_DIMS, upright=True)
    assert _grille_dir(mesh)[2] > 0.8  # front, +Z
    assert mesh.extents == pytest.approx(np.array([AC_DIMS.w, AC_DIMS.h, AC_DIMS.d]) / 1000)
    assert mesh.bounds[0][2] == pytest.approx(0.0, abs=1e-9)
    assert residual < MAX_AI_RESIDUAL_PCT


def test_normalize_mesh_upright_turns_about_y_only():
    """A hanger generated long along X while its depth (Z) is the long dim: a quarter turn about
    Y, never a tip onto its side."""
    strip = trimesh.creation.box(bounds=[[-1.0, -0.35, -0.13], [1.0, 0.35, 0.13]])
    tip = trimesh.creation.box(bounds=[[0.9, 0.3, -0.13], [1.0, 0.35, 0.13]])  # marks top end
    mesh, _ = normalize_mesh(
        trimesh.util.concatenate([strip, tip]), Dims(w=25.4, d=114.3, h=69.85), upright=True
    )
    assert np.argmax(mesh.extents) == 2
    assert _grille_dir(mesh)[1] > 0.8  # the top-end marker stayed on top


# --- resolve_asset: the llm tier and the chain behind it ----------------------------------------


def _fake_llm(monkeypatch, answer: dict | Exception) -> list:
    from types import SimpleNamespace

    calls = []

    async def chat(role, messages, **kw):
        calls.append(role)
        if isinstance(answer, Exception):
            raise answer
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(answer)))]
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _answer(template="box_appliance", face="front", shape="template", single=True) -> dict:
    return {
        "template": template,
        "colors": {"body": "#F0F0F0"},
        "photo": {"face": face, "rotate_cw": 0, "single": single},
        "shape_class": shape,
    }


def _serve_photo(router):
    router.get("https://example.com/photo.jpg").mock(
        return_value=Response(
            200, content=SOURCE_PHOTO.read_bytes(), headers={"content-type": "image/jpeg"}
        )
    )


def _load(part_id: str) -> trimesh.Scene:
    return trimesh.load(part_dir(part_id) / "model.glb", force="scene")


def _no_ai_mesh(monkeypatch):
    async def fail(image_path):
        raise AssertionError("ai_mesh must not be called here")

    monkeypatch.setattr("server.assets.ai_mesh", fail)


@pytest.mark.asyncio
async def test_resolve_asset_llm_tier_builds_the_template_with_the_photo(monkeypatch):
    calls = _fake_llm(monkeypatch, _answer())
    _no_ai_mesh(monkeypatch)
    part = _make_part(id="llm-ac", dims_mm=AC_DIMS)
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)

    assert calls == ["asset"]
    assert (result.asset.tier, result.asset.status, result.asset.scale_residual_pct) == (
        "llm",
        "ready",
        0.0,
    )
    scene = _load(part.id)
    assert scene.extents == pytest.approx([0.61468, 0.3683, 0.51562], abs=1e-5)
    assert scene.bounds[0][2] == pytest.approx(0.0, abs=1e-6)
    assert "photo" in scene.geometry and "sleeve" in scene.geometry
    assert "grille_bg" not in scene.geometry  # photo mode: the photo draws the grille


@pytest.mark.asyncio
async def test_resolve_asset_kit_photo_gets_the_plain_template(monkeypatch):
    _fake_llm(monkeypatch, _answer(single=False))
    part = _make_part(id="llm-kit", dims_mm=AC_DIMS)
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)
    assert result.asset.tier == "llm"
    assert "photo" not in _load(part.id).geometry and "grille_bg" in _load(part.id).geometry


@pytest.mark.asyncio
async def test_resolve_asset_moulded_part_uses_the_cached_grok_scad(monkeypatch):
    _fake_llm(monkeypatch, _answer("bent_strip", face="right", shape="moulded"))
    _no_ai_mesh(monkeypatch)
    part = _make_part(id="hanger")
    scad = trimesh.creation.box(bounds=[[-0.0635, -0.0225, 0], [0.0635, 0.0225, 0.038]])
    part_dir(part.id).mkdir(parents=True)
    scad.export(part_dir(part.id) / "scad.glb")
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)
    assert result.asset.tier == "scad"
    assert "photo" in _load(part.id).geometry


@pytest.mark.asyncio
async def test_resolve_asset_moulded_part_without_scad_goes_to_hunyuan(monkeypatch, tmp_path):
    _fake_llm(monkeypatch, _answer("bent_strip", face="front", shape="moulded"))
    seen = []

    async def fake_ai_mesh(image_path):
        seen.append(image_path)
        dest = tmp_path / "hy.glb"
        _hunyuan_like_ac().export(dest)
        return dest

    monkeypatch.setattr("server.assets.ai_mesh", fake_ai_mesh)
    part = _make_part(id="moulded-ac", dims_mm=AC_DIMS)
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)
    assert result.asset.tier == "ai_mesh" and len(seen) == 1
    scene = _load(part.id)
    assert scene.extents == pytest.approx([0.61468, 0.3683, 0.51562], abs=1e-5)
    assert "photo" in scene.geometry


@pytest.mark.asyncio
async def test_resolve_asset_moulded_part_keeps_the_template_when_hunyuan_fails(monkeypatch):
    _fake_llm(monkeypatch, _answer("bent_strip", shape="organic"))

    async def no_mesh(image_path):
        return None

    monkeypatch.setattr("server.assets.ai_mesh", no_mesh)
    part = _make_part(id="organic")
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)
    assert result.asset.tier == "llm"


@pytest.mark.asyncio
async def test_resolve_asset_llm_failure_ends_on_the_photo_decal_box(monkeypatch):
    calls = _fake_llm(monkeypatch, RuntimeError("groq down"))

    async def no_mesh(image_path):
        return None

    monkeypatch.setattr("server.assets.ai_mesh", no_mesh)
    part = _make_part(id="decal")
    with respx.mock() as router:
        _serve_photo(router)
        result = await resolve_asset(part)
    assert calls == ["asset", "extract"]  # vision, then the text fallback
    assert result.asset.tier == "proxy"
    scene = _load(part.id)
    assert "photo" in scene.geometry  # the E3 decal, not the grey box
    assert scene.extents == pytest.approx([0.127, 0.045, 0.038], abs=1e-6)


@pytest.mark.asyncio
async def test_resolve_asset_warmed_plan_costs_no_call_offline(monkeypatch):
    calls = _fake_llm(monkeypatch, _answer())
    part = _make_part(id="warm-ac", dims_mm=AC_DIMS)
    with respx.mock() as router:
        _serve_photo(router)
        await resolve_asset(part)
    (part_dir(part.id) / "model.glb").unlink()  # force a full re-resolve
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    with respx.mock() as router:
        router.get("https://example.com/photo.jpg").mock(side_effect=httpx.ConnectError("down"))
        result = await resolve_asset(part)
    assert calls == ["asset"]  # the second resolve was a cache hit
    assert result.asset.tier == "llm" and "photo" in _load(part.id).geometry
