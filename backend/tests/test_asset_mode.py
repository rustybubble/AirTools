"""asset-mode: the headset's 3D-model preference -- "hf" (Hunyuan3D image-to-3D), "llm_scad" (Grok
writes OpenSCAD) or "auto" -- per request; both variants kept per part; HF meshes made once (raw
cache by photo hash) and warmed ahead of need within the ZeroGPU quota. HF / LLM / OpenSCAD calls
are all stubbed."""

import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
import respx
import trimesh
from fastapi.testclient import TestClient
from httpx import Response

from server import agent, assets, hf_warm, jobs, meshgen, warm
from server.app import app
from server.config import get_settings
from server.models import Asset, Dims, Part

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "assets"
SOURCE_PHOTO = FIXTURES_DIR / "source_photo_window_ac.jpg"
AC_DIMS = Dims(w=614.68, d=515.62, h=368.3)


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    hf_warm.reset()
    assets._scad_jobs.clear()
    assets._scad_failed.clear()
    assets.HF_KEYS.reset()
    yield tmp_path
    hf_warm.reset()
    get_settings.cache_clear()


@pytest.fixture(autouse=True)
def _public_example_com(monkeypatch):
    import socket

    def fake(host, *args, **kwargs):
        return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 0))]

    monkeypatch.setattr(socket, "getaddrinfo", fake)


def _part(pid="ac-1", **kw) -> Part:
    return Part(
        id=pid,
        name="Window air conditioner",
        dims_mm=kw.pop("dims_mm", AC_DIMS),
        image_url=kw.pop("image_url", "https://example.com/photo.jpg"),
        **kw,
    )


def _fake_llm(monkeypatch, template="box_appliance", shape="template") -> list:
    calls = []

    async def chat(role, messages, **kw):
        calls.append(role)
        answer = {
            "template": template,
            "colors": {"body": "#F0F0F0"},
            "photo": {"face": "front", "rotate_cw": 0, "single": True},
            "shape_class": shape,
        }
        return SimpleNamespace(
            model="qwen/qwen3.8-27b",
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(answer)))],
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _hunyuan_like_ac() -> trimesh.Trimesh:
    body = trimesh.creation.box(bounds=[[-1.0, -0.62, -0.45], [1.0, 0.62, 0.40]])
    grille = trimesh.creation.box(bounds=[[-0.9, -0.5, 0.40], [0.9, 0.3, 0.45]])
    return trimesh.util.concatenate([body, grille])


def _fake_hunyuan(monkeypatch, tmp_path, *, mesh=None, outcome=None) -> list:
    """ai_mesh stub: a Hunyuan-like AC (or `mesh`), or None with AI_MESH_LAST's outcome."""
    calls = []

    async def fake_ai_mesh(image_path):
        calls.append(image_path)
        if outcome is not None:
            assets._ai_mesh_outcome(outcome, "tencent/Hunyuan3D-2.1", "stub")
            return None
        dest = tmp_path / f"hy-{len(calls)}.glb"
        (mesh or _hunyuan_like_ac()).export(dest)
        assets._ai_mesh_outcome("ok", "tencent/Hunyuan3D-2.1")
        return dest

    monkeypatch.setattr("server.assets.ai_mesh", fake_ai_mesh)
    return calls


def _serve_photo(router):
    router.get("https://example.com/photo.jpg").mock(
        return_value=Response(
            200, content=SOURCE_PHOTO.read_bytes(), headers={"content-type": "image/jpeg"}
        )
    )


def _no_openscad(monkeypatch):
    monkeypatch.setattr(meshgen, "openscad", lambda: None)


# --- modes ----------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("given", "want"),
    [
        ("hf", "hf"),
        ("HF", "hf"),
        ("hunyuan", "hf"),
        ("llm_scad", "llm_scad"),
        ("LLM+CAD", "llm_scad"),
        ("llm-scad", "llm_scad"),
        ("scad", "llm_scad"),
        ("auto", "auto"),
        ("", "auto"),
        (None, "auto"),
        ("banana", "auto"),
    ],
)
def test_normalize_mode(given, want):
    assert assets.normalize_mode(given) == want


# --- hf ---------------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_hf_mode_makes_a_hunyuan_mesh_for_a_template_part(monkeypatch, tmp_path):
    """Auto would build this box appliance from the template; "hf" asks Hunyuan for every part."""
    _fake_llm(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path)
    part = _part()
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(part, mode="hf")
    assert len(hy) == 1
    a = got.asset
    assert (a.tier, a.mode, a.status, a.note) == ("ai_mesh", "hf", "ready", None)
    assert a.made_by == "hf:tencent/Hunyuan3D-2.1"
    pdir = assets.part_dir(part.id)
    assert (pdir / "model.hf.glb").exists() and (pdir / "asset.hf.json").exists()
    assert (pdir / "model.glb").read_bytes() == (pdir / "model.hf.glb").read_bytes()
    assert json.loads((pdir / "part.json").read_text())["asset"]["tier"] == "ai_mesh"
    scene = trimesh.load(pdir / "model.hf.glb", force="scene")
    assert scene.extents == pytest.approx([0.61468, 0.3683, 0.51562], abs=1e-4)
    assert "photo" in scene.geometry  # photo-textured, like auto's ai_mesh
    raw = list((tmp_path / "cache" / "hunyuan").glob("*.glb"))
    assert len(raw) == 1 and raw[0].stem == assets.photo_hash(pdir / "image.jpg")


@pytest.mark.asyncio
async def test_hf_mesh_is_never_generated_twice(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path)
    with respx.mock() as router:
        _serve_photo(router)
        await assets.resolve_asset(_part("a"), mode="hf")
        await assets.resolve_asset(_part("a"), mode="hf")  # the variant
        await assets.resolve_asset(_part("a"), mode="hf", rebuild=True)  # the raw cache
        other = await assets.resolve_asset(_part("b"), mode="hf")  # same photo: same raw mesh
        assert await assets.warm_hf(_part("a")) == "ready"
    assert len(hy) == 1
    assert other.asset.tier == "ai_mesh"
    assert assets.hf_final("a") and assets.hf_final("b")


@pytest.mark.asyncio
async def test_hf_quota_falls_back_to_the_template_with_a_note(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _fake_hunyuan(monkeypatch, tmp_path, outcome="quota")
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(_part(), mode="hf")
        assert await assets.warm_hf(_part()) == "quota"
    a = got.asset
    assert (a.tier, a.mode) == ("llm", "hf")
    assert a.note == "Hunyuan's ZeroGPU quota is spent for now: the LLM template instead"
    assert a.made_by == "groq:qwen/qwen3.8-27b"
    assert not assets.hf_final("ac-1")  # the warm queue tries it again later


@pytest.mark.asyncio
async def test_hf_quota_then_warm_replaces_the_stand_in_without_selecting_it(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _fake_hunyuan(monkeypatch, tmp_path, outcome="quota")
    with respx.mock() as router:
        _serve_photo(router)
        await assets.resolve_asset(_part(), mode="hf")
        await assets.resolve_asset(_part(), mode="auto")  # the headset switched back to auto
        hy = _fake_hunyuan(monkeypatch, tmp_path)  # the quota reset
        assert await assets.warm_hf(_part()) == "ready"
    assert len(hy) == 1
    assert assets.variant_asset("ac-1", "hf").tier == "ai_mesh"
    shown = json.loads((assets.part_dir("ac-1") / "part.json").read_text())["asset"]
    assert (shown["tier"], shown["mode"]) == ("llm", "auto")  # the warm didn't change the model


@pytest.mark.asyncio
async def test_hf_mode_keeps_a_squashed_depth_guess(monkeypatch, tmp_path):
    """Live: a thin window frame (1206 x 902 x 83 mm) from a front photo came back 74% off (a depth
    guess). Auto drops that (> 60%); "hf" keeps it, squashed to size, and says how far off it was."""
    _fake_llm(monkeypatch, template="window")
    slab = trimesh.creation.box(extents=(1.2, 0.9, 0.3))  # too deep for a window: ~140% off
    _fake_hunyuan(monkeypatch, tmp_path, mesh=slab)
    part = _part("win", dims_mm=Dims(w=1206.5, d=82.55, h=901.7))
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(part, mode="hf")
    assert got.asset.tier == "ai_mesh"
    assert 60 < got.asset.scale_residual_pct < assets.MAX_HF_MODE_RESIDUAL_PCT


@pytest.mark.asyncio
async def test_hf_shape_mismatch_is_settled_and_noted(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path, mesh=trimesh.creation.box(extents=(1, 1, 1)))
    part = _part("thin", dims_mm=Dims(w=13, d=305, h=241))
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(part, mode="hf")
        assert await assets.warm_hf(part) == "mismatch"
    assert got.asset.tier == "llm"
    assert "didn't match the listed size" in got.asset.note
    assert assets.hf_final("thin") and len(hy) == 1


@pytest.mark.asyncio
async def test_hf_without_a_photo_says_so(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path)
    got = await assets.resolve_asset(_part(image_url=None), mode="hf")
    assert not hy
    assert got.asset.tier == "llm"
    assert got.asset.note.startswith("No product photo for image-to-3D")


# --- llm_scad ---------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_llm_scad_without_openscad_is_the_labelled_template(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _no_openscad(monkeypatch)
    _fake_hunyuan(monkeypatch, tmp_path, outcome="failed")

    async def no_scad(*a, **kw):
        raise AssertionError("no openscad: grok_scad must not run")

    monkeypatch.setattr(meshgen, "grok_scad", no_scad)
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(_part(), mode="llm_scad")
    a = got.asset
    assert (a.tier, a.mode, a.template) == ("llm", "llm_scad", "box_appliance")
    assert a.note == "OpenSCAD isn't installed on the server: the LLM template instead"
    assert a.made_by == "groq:qwen/qwen3.8-27b"
    assert (assets.part_dir("ac-1") / "model.scad.glb").exists()


@pytest.mark.asyncio
async def test_llm_scad_uses_the_cached_openscad_model(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _no_openscad(monkeypatch)
    part = _part("hanger", dims_mm=Dims(w=127, d=38, h=45))
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True)
    box = trimesh.creation.box(bounds=[[-0.0635, 0, 0], [0.0635, 0.045, 0.038]])
    box.export(pdir / "scad.glb")
    with respx.mock() as router:
        _serve_photo(router)
        got = await assets.resolve_asset(part, mode="llm_scad")
    assert (got.asset.tier, got.asset.note) == ("scad", None)
    assert got.asset.made_by == "xai:grok-4.7 + openscad"


@pytest.mark.asyncio
async def test_llm_scad_with_openscad_writes_it_in_the_background(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    release = asyncio.Event()
    calls = []

    async def fake_grok_scad(part, photo, out):
        calls.append(part.id)
        await release.wait()
        trimesh.creation.box(bounds=[[-0.3, 0, 0], [0.3, 0.37, 0.52]]).export(out)
        return True

    monkeypatch.setattr(meshgen, "grok_scad", fake_grok_scad)
    with respx.mock() as router:
        _serve_photo(router)
        first = await assets.resolve_asset(_part(), mode="llm_scad")
        assert first.asset.tier == "llm"
        assert first.asset.note.startswith("Grok is still writing the OpenSCAD model")
        assert assets.scad_pending("ac-1")
        release.set()
        await assets._scad_jobs["ac-1"]
    assert calls == ["ac-1"]
    assert assets.variant_asset("ac-1", "llm_scad").tier == "scad"


# --- both variants, select, legacy ------------------------------------------------------------


@pytest.mark.asyncio
async def test_both_variants_coexist_and_model_glb_follows_the_last_mode(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _no_openscad(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path)
    pdir = assets.part_dir("ac-1")
    with respx.mock() as router:
        _serve_photo(router)
        await assets.resolve_asset(_part(), mode="hf")
        await assets.resolve_asset(_part(), mode="llm_scad")
        assert (pdir / "model.glb").read_bytes() == (pdir / "model.scad.glb").read_bytes()
        again = await assets.resolve_shared(_part(), mode="hf")  # back: no new call
    assert len(hy) == 1
    assert again.asset.tier == "ai_mesh"
    assert (pdir / "model.glb").read_bytes() == (pdir / "model.hf.glb").read_bytes()
    assert assets.ready("ac-1", "llm_scad").asset.tier == "llm"
    assert assets.ready("ac-1", "hf").asset.tier == "ai_mesh"
    assert assets.ready("ac-1", "auto") is None  # not asked for yet


def test_a_legacy_hunyuan_model_is_adopted_not_regenerated(tmp_path):
    part = _part("old")
    pdir = assets.part_dir(part.id)
    pdir.mkdir(parents=True)
    trimesh.creation.box(extents=(0.6, 0.37, 0.52)).export(pdir / "model.glb")
    part.asset = Asset(tier="ai_mesh", status="ready", made_by="groq:qwen/qwen3.8-27b")
    (pdir / "part.json").write_text(part.model_dump_json())
    hf = assets.ready("old", "hf")
    assert hf is not None and (hf.asset.tier, hf.asset.mode) == ("ai_mesh", "hf")
    assert assets.ready("old", "auto").asset.mode == "auto"
    assert assets.hf_final("old")
    assert jobs.load_part("old").asset.mode == "auto"


# --- the warm queue ---------------------------------------------------------------------------


def _save(part: Part) -> Part:
    jobs.save_part(part)
    return part


@pytest.mark.asyncio
async def test_warm_queue_is_sequential_and_backs_off_on_the_quota(monkeypatch):
    monkeypatch.setenv("HF_WARM", "true")
    get_settings.cache_clear()
    monkeypatch.setattr(hf_warm, "PAUSE_S", 0.0)
    monkeypatch.setattr(hf_warm, "BACKOFF_START_S", 0.05)
    for pid in ("p1", "p2", "p3"):
        _save(_part(pid))
    outcomes = {"p1": ["quota", "ready"], "p2": ["mismatch"], "p3": ["ready"]}
    running, seen = [], []

    async def fake_warm(part):
        running.append(part.id)
        assert len(running) == 1, "one generation at a time"
        await asyncio.sleep(0.01)
        running.remove(part.id)
        seen.append(part.id)
        return outcomes[part.id].pop(0)

    monkeypatch.setattr(assets, "warm_hf", fake_warm)
    monkeypatch.setattr(assets, "hf_final", lambda pid: False)
    assert hf_warm.enqueue_many([_part("p1"), _part("p2")]) == 2
    assert hf_warm.enqueue(_part("p3"), front=True)  # an on-demand hf request jumps the line
    assert not hf_warm.enqueue(_part("p9", image_url=None))  # no photo: nothing to make it from
    await hf_warm.drain(5)
    assert seen == ["p3", "p1", "p1", "p2"]
    st = hf_warm.status()
    assert (st["made"], st["mismatch"], st["quota_hits"], st["queued"]) == (2, 1, 1, [])
    assert st["backoff_s"] == 0.0  # reset by the success


def test_warm_queue_is_off_offline_and_when_disabled(monkeypatch):
    assert not hf_warm.enqueue(_part())  # conftest: HF_WARM=false
    monkeypatch.setenv("HF_WARM", "true")
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    assert not hf_warm.enqueue(_part())


@pytest.mark.asyncio
async def test_search_resolves_in_the_requested_mode_then_warms_hf(monkeypatch):
    seen = []

    async def fake_shared(part, *, allow_ai=True, mode=None):
        seen.append((part.id, mode))
        return part

    queued = []
    monkeypatch.setattr(assets, "resolve_shared", fake_shared)
    monkeypatch.setattr(hf_warm, "enqueue_many", lambda parts: queued.extend(p.id for p in parts))
    await jobs._resolve_assets([_part("a"), _part("b")], "llm_scad")
    await jobs._resolve_assets([_part("c")])
    assert seen == [("a", "llm_scad"), ("b", "llm_scad"), ("c", None)]
    assert queued == ["a", "b", "c"]


@pytest.mark.asyncio
async def test_warm_cli_hf_backs_off_then_goes_on(monkeypatch, capsys):
    for pid in ("w1", "w2"):
        _save(_part(pid))
    outcomes = ["quota", "ready", "no_photo"]

    async def fake_warm(part):
        return outcomes.pop(0)

    slept = []

    async def fake_sleep(s):
        slept.append(s)

    monkeypatch.setattr(assets, "warm_hf", fake_warm)
    counts = await warm.warm_hf(sleep=fake_sleep)
    assert counts == {"ready": 1, "no_photo": 1}
    assert slept == [warm.HF_BACKOFF_START_S]
    assert "ZeroGPU quota spent" in capsys.readouterr().out


# --- the session context ----------------------------------------------------------------------


@pytest.mark.asyncio
async def test_the_context_asset_mode_rides_on_the_sessions_searches(monkeypatch):
    started = []

    def fake_start(req):
        started.append(req)
        return SimpleNamespace(id="job1")

    monkeypatch.setattr(jobs, "start_search", fake_start)
    session = agent._get_session("s-mode")
    agent._apply_context(session, {"asset_mode": "LLM+CAD"})
    assert session.asset_mode == "llm_scad"
    await agent._run_tool(session, "find_part", {"query": "window air conditioner"}, {})
    assert started[-1].asset_mode == "llm_scad"
    agent._apply_context(session, {})  # absent: the last one stays
    assert session.asset_mode == "llm_scad"


# --- endpoints --------------------------------------------------------------------------------


def test_endpoints_serve_each_mode_and_report_the_tier(monkeypatch, tmp_path):
    _fake_llm(monkeypatch)
    _no_openscad(monkeypatch)
    hy = _fake_hunyuan(monkeypatch, tmp_path)
    _save(_part())
    with respx.mock() as router:
        _serve_photo(router)
        with TestClient(app) as client:
            r = client.post("/parts/ac-1/model?mode=hf&wait_s=30")
            assert r.status_code == 200, r.text
            body = r.json()
            assert (body["status"], body["mode"], body["asset"]["tier"]) == (
                "ready",
                "hf",
                "ai_mesh",
            )
            assert body["model_url"] == "/parts/ac-1/model.glb?mode=hf"

            r = client.get("/parts/ac-1/model.glb?mode=hf")
            assert r.status_code == 200
            assert r.headers["x-asset-tier"] == "ai_mesh"
            assert r.headers["x-asset-made-by"] == "hf:tencent/Hunyuan3D-2.1"
            hf_bytes = r.content

            r = client.get("/parts/ac-1/model.glb?mode=llm_scad")
            assert r.status_code == 200 and r.headers["x-asset-tier"] == "llm"
            assert r.content != hf_bytes

            j = client.get("/parts/ac-1/part.json?mode=hf").json()
            assert (j["asset"]["tier"], j["asset"]["status"]) == ("ai_mesh", "ready")
            assert client.get("/parts/ac-1/model.glb").content == hf_bytes  # the last mode

            j = client.get("/parts/ac-1/part.json").json()  # no mode: the file as before
            assert j["asset"]["mode"] == "hf"

            st = client.get("/assets/hf-warm").json()
            assert {"queued", "made", "quota_messages", "last_ai_mesh"} <= set(st)
    assert len(hy) == 1


def test_part_json_with_a_mode_is_pending_until_made(monkeypatch):
    _save(_part())
    gate = asyncio.Event()

    async def slow_resolve(part, *, allow_ai=True, mode="auto", **kw):
        await gate.wait()
        return part

    monkeypatch.setattr(assets, "resolve_asset", slow_resolve)
    with TestClient(app) as client:
        j = client.get("/parts/ac-1/part.json?mode=llm_scad").json()
        assert (j["asset"]["status"], j["asset"]["mode"]) == ("pending", "llm_scad")
        r = client.post("/parts/ac-1/model", json={"mode": "llm_scad"})
        assert r.json()["status"] == "pending"
        assert client.get("/parts/nope/part.json?mode=hf").status_code == 404


def test_the_space_answer_is_the_local_file_gradio_already_downloaded(tmp_path):
    """Live 2026-09-26: gradio_client 2.x downloads the GLB itself; its path is local (the old
    `/file=` fetch of that path was a 403, so every Hunyuan call "failed")."""
    local = tmp_path / "white_mesh.glb"
    local.write_bytes(b"glTF-local")
    client = SimpleNamespace(src="https://example.invalid", src_prefixed="https://example.invalid/")
    assert (
        assets._resolve_remote_file(client, {"value": str(local), "__type__": "update"})
        == b"glTF-local"
    )
    assert assets._resolve_remote_file(client, {"value": {"path": str(local)}}) == b"glTF-local"
    assert assets._resolve_remote_file(client, str(local)) == b"glTF-local"


@pytest.mark.asyncio
async def test_a_photo_the_space_just_failed_on_is_not_resent(monkeypatch, tmp_path):
    hy = _fake_hunyuan(monkeypatch, tmp_path, outcome="failed")
    photo = tmp_path / "p.jpg"
    photo.write_bytes(SOURCE_PHOTO.read_bytes())
    assert (await assets.hunyuan_raw(photo))[0] is None
    got, meta = await assets.hunyuan_raw(photo)
    assert got is None and meta["outcome"] == "failed" and "ago" in meta["detail"]
    assert len(hy) == 1
