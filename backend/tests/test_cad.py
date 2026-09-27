"""cad: Grok's OpenSCAD models (LLM+CAD) as a first-class asset mode -- stale llm_scad variants
made again when their reason has passed, an honest `asset.cad` status while Grok writes, the run
records next to scad.glb (calls, seconds, cost, bbox error, failures), a model-sized loop (parallel
answers for a fast model), and `python -m server.warm --scad`. OpenSCAD and the LLM are stubbed;
`test_live_grok_scad_on_a_knob` (opt-in, `-m live`) runs the real thing."""

import asyncio
import json
import os
import time
from pathlib import Path
from types import SimpleNamespace

import pytest
import respx
import trimesh
from fastapi.testclient import TestClient
from httpx import Response
from PIL import Image

from server import assets, hf_warm, jobs, meshgen, warm
from server.app import app
from server.config import get_settings
from server.models import Dims, Part

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "assets"
SOURCE_PHOTO = FIXTURES_DIR / "source_photo_window_ac.jpg"
AC_DIMS = Dims(w=614.68, d=515.62, h=368.3)
FAST = "xai:grok-4.20-0309-non-reasoning"


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch, request):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    if not request.node.get_closest_marker("live"):  # live: the configured model
        monkeypatch.setenv("LLM_ASSET_SCAD", FAST)
    get_settings.cache_clear()
    hf_warm.reset()
    assets._scad_jobs.clear()
    assets._scad_failed.clear()
    assets._scad_started.clear()
    meshgen._scad_seen_s.clear()
    yield tmp_path
    hf_warm.reset()
    get_settings.cache_clear()


@pytest.fixture
def router():
    """The product photo at example.com for the whole test (background runs fetch it too)."""
    with respx.mock(assert_all_called=False) as r:
        _serve_photo(r)
        yield r


@pytest.fixture(autouse=True)
def _public_example_com(monkeypatch, request):
    import socket

    if request.node.get_closest_marker("live"):  # the real DNS for the real API
        return

    def fake(host, *args, **kwargs):
        return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 0))]

    monkeypatch.setattr(socket, "getaddrinfo", fake)


def _part(pid="ac-1", **kw) -> Part:
    return Part(
        id=pid,
        name=kw.pop("name", "Window air conditioner"),
        dims_mm=kw.pop("dims_mm", AC_DIMS),
        image_url=kw.pop("image_url", "https://example.com/photo.jpg"),
        **kw,
    )


def _fake_plan_llm(monkeypatch) -> list:
    """The template call (role `asset`); any other role fails loudly."""
    calls = []

    async def chat(role, messages, **kw):
        calls.append(role)
        assert role in ("asset", "extract"), role
        answer = {
            "template": "box_appliance",
            "colors": {"body": "#F0F0F0"},
            "photo": {"face": "front", "rotate_cw": 0, "single": True},
            "shape_class": "template",
        }
        return SimpleNamespace(
            model="qwen/qwen3.8-27b",
            choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(answer)))],
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _serve_photo(router):
    router.get("https://example.com/photo.jpg").mock(
        return_value=Response(
            200, content=SOURCE_PHOTO.read_bytes(), headers={"content-type": "image/jpeg"}
        )
    )


def _slab(pid: str) -> None:
    """A CAD model on disk for the part, as grok_scad writes it (contract frame, metres)."""
    box = trimesh.creation.box(extents=(0.61, 0.37, 0.52))
    box.apply_translation((0, 0, 0.26))
    out = assets.scad_path(pid)
    out.parent.mkdir(parents=True, exist_ok=True)
    trimesh.Scene(box).export(out)
    (out.parent / "scad.json").write_text(
        json.dumps({"ok": True, "made_by": f"{FAST} + openscad", "seconds": 14.2, "calls": 3})
    )


def _gated_grok(monkeypatch) -> tuple[asyncio.Event, list]:
    """grok_scad stub that waits for the event, then writes the CAD model."""
    gate, calls = asyncio.Event(), []

    async def fake(part, photo, out):
        calls.append(part.id)
        await gate.wait()
        _slab(part.id)
        return True

    monkeypatch.setattr(meshgen, "grok_scad", fake)
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    return gate, calls


# --- (a) stale llm_scad variants ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_a_template_stored_without_openscad_is_made_again_once_it_is_installed(
    monkeypatch, router
):
    _fake_plan_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: None)
    first = await assets.resolve_asset(_part(), mode="llm_scad")
    assert "OpenSCAD isn't installed" in first.asset.note
    assert assets.ready("ac-1", "llm_scad") is not None  # still no OpenSCAD: it stands

    gate, grok = _gated_grok(monkeypatch)  # OpenSCAD is installed now
    assert assets.ready("ac-1", "llm_scad") is None  # stale: made again on the next request
    assert assets.has_variant("ac-1", "llm_scad")
    again = await assets.resolve_asset(_part(), mode="llm_scad")
    assert (again.asset.tier, again.asset.note) == (
        "llm",
        "Grok is still writing the OpenSCAD model: the LLM template instead",
    )
    assert assets.ready("ac-1", "llm_scad") is not None  # writing: served as is meanwhile
    assert assets.cad_status("ac-1")["status"] == "writing"

    gate.set()
    await assets._scad_jobs["ac-1"]
    done = assets.ready("ac-1", "llm_scad")
    assert (done.asset.tier, done.asset.made_by) == ("scad", f"{FAST} + openscad")
    assert grok == ["ac-1"]


@pytest.mark.asyncio
async def test_a_run_lost_to_a_restart_is_started_again(monkeypatch, router):
    _fake_plan_llm(monkeypatch)
    gate, grok = _gated_grok(monkeypatch)
    await assets.resolve_asset(_part(), mode="llm_scad")
    assert grok == ["ac-1"]
    assets._scad_jobs.pop("ac-1").cancel()  # the server restarted mid-run
    assert assets.ready("ac-1", "llm_scad") is None
    assert assets.cad_status("ac-1")["status"] == "none"
    await assets.resolve_asset(_part(), mode="llm_scad")
    assert grok == ["ac-1", "ac-1"] and assets.scad_pending("ac-1")
    gate.set()
    await assets._scad_jobs["ac-1"]


@pytest.mark.asyncio
async def test_a_cad_model_made_by_another_process_is_picked_up(monkeypatch, router):
    _fake_plan_llm(monkeypatch)
    _gate, grok = _gated_grok(monkeypatch)
    out = assets.scad_path("ac-1")
    out.parent.mkdir(parents=True)
    lock = meshgen.scad_sidecar(out, "writing")
    lock.write_text(json.dumps({"started_at": time.time() - 5, "model": FAST, "pid": os.getpid()}))
    first = await assets.resolve_asset(_part(), mode="llm_scad")
    assert grok == []  # warm --scad is on it: no second run
    assert "still writing" in first.asset.note
    status = assets.cad_status("ac-1")
    assert status["status"] == "writing" and 10 <= status["eta_s"] <= 60
    assert assets.ready("ac-1", "llm_scad") is not None

    lock.unlink()
    _slab("ac-1")  # it landed
    assert assets.ready("ac-1", "llm_scad") is None
    got = await assets.resolve_asset(_part(), mode="llm_scad")
    assert (got.asset.tier, got.asset.note) == ("scad", None)
    assert assets.cad_status("ac-1")["status"] == "ready"


@pytest.mark.asyncio
async def test_a_failure_counts_for_its_model_and_an_hour(monkeypatch, router):
    _fake_plan_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    out = assets.scad_path("ac-1")
    out.parent.mkdir(parents=True)
    failed = meshgen.scad_sidecar(out, "failed")
    failed.write_text(json.dumps({"model": FAST, "at": time.time(), "error": "ERROR: x"}))

    async def no_grok(*a):
        raise AssertionError("a failure that still counts must not run again")

    monkeypatch.setattr(meshgen, "grok_scad", no_grok)
    got = await assets.resolve_asset(_part(), mode="llm_scad")
    assert got.asset.note.startswith("Grok's OpenSCAD model didn't build")
    cad = assets.cad_status("ac-1")
    assert cad["status"] == "failed" and "ERROR: x" in cad["reason"]
    assert assets.ready("ac-1", "llm_scad") is not None

    failed.write_text(json.dumps({"model": FAST, "at": time.time() - 7200, "error": "x"}))
    assert assets.ready("ac-1", "llm_scad") is None  # an hour later: try again
    failed.write_text(json.dumps({"model": "xai:grok-4.7", "at": time.time(), "error": "x"}))
    assert assets.ready("ac-1", "llm_scad") is None  # another model: try again


@pytest.mark.asyncio
async def test_the_cad_model_can_be_served_without_the_photo(monkeypatch, router):
    _fake_plan_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    projected = []

    def fake_project(geometry, photo, face, rotate=0, **kw):  # texture: finish=
        projected.append(face)
        return geometry

    monkeypatch.setattr(meshgen, "project", fake_project)
    _slab("ac-1")
    got = await assets.resolve_asset(_part(), mode="llm_scad")
    assert got.asset.tier == "scad" and len(projected) == 1  # the photo on its front
    monkeypatch.setenv("ASSET_SCAD_PHOTO", "false")
    get_settings.cache_clear()
    got = await assets.resolve_asset(_part(), mode="llm_scad", rebuild=True)
    assert got.asset.tier == "scad" and len(projected) == 1  # Grok's own paint only


# --- (b) honest pending states on the endpoints ------------------------------------------------


def test_b_part_json_and_post_model_say_grok_is_writing(monkeypatch):
    _fake_plan_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    part = _part()
    jobs.save_part(part)
    out = assets.scad_path("ac-1")
    meshgen.scad_sidecar(out, "writing").write_text(
        json.dumps({"started_at": time.time() - 20, "model": FAST, "pid": os.getpid()})
    )
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        _serve_photo(router)
        body = client.post("/parts/ac-1/model?mode=llm_scad", json={"wait_s": 30}).json()
        assert body["status"] == "ready"
        assert body["asset"]["tier"] == "llm"  # the template stands in, noted
        assert body["cad"]["status"] == body["asset"]["cad"]["status"] == "writing"
        assert body["cad"]["eta_s"] >= 10 and body["cad"]["started_at"] > 0
        assert body["cad"]["made_by"] == f"{FAST} + openscad"

        j = client.get("/parts/ac-1/part.json?mode=llm_scad").json()
        assert (j["asset"]["tier"], j["asset"]["cad"]["status"]) == ("llm", "writing")
        r = client.get("/parts/ac-1/model.glb?mode=llm_scad")
        assert r.headers["x-asset-cad"] == "writing"

        meshgen.scad_sidecar(out, "writing").unlink()
        _slab("ac-1")  # landed (warm --scad in another process)
        j = client.get("/parts/ac-1/part.json?mode=llm_scad").json()
        assert (j["asset"]["status"], j["asset"]["tier"]) == ("ready", "scad")
        assert j["asset"]["cad"]["status"] == "ready" and j["asset"]["cad"]["seconds"] == 14.2
        assert j["asset"]["made_by"] == f"{FAST} + openscad"

        hf = client.get("/parts/ac-1/part.json?mode=auto").json()
        assert hf["asset"].get("cad") is None  # only llm_scad answers carry it


def test_b_no_openscad_is_a_failed_status_with_its_reason(monkeypatch):
    _fake_plan_llm(monkeypatch)
    monkeypatch.setattr(meshgen, "openscad", lambda: None)
    jobs.save_part(_part())
    with respx.mock(assert_all_called=False) as router, TestClient(app) as client:
        _serve_photo(router)
        body = client.post("/parts/ac-1/model", json={"mode": "llm_scad", "wait_s": 30}).json()
    assert body["cad"] == {
        "status": "failed",
        "reason": "OpenSCAD isn't installed on the server",
        "at": None,
    }


def test_b_the_eta_counts_down_to_a_floor(monkeypatch):
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    out = assets.scad_path("p")
    out.parent.mkdir(parents=True)
    (out.parent / "image.jpg").write_bytes(b"jpg")
    lock = meshgen.scad_sidecar(out, "writing")
    lock.write_text(json.dumps({"started_at": time.time() - 5, "model": "xai:grok-4.7"}))
    low = meshgen.SCAD_TYPICAL["grok-4.7@low"][0]  # no profile in the lock: this server's effort
    assert assets.cad_status("p")["eta_s"] == pytest.approx(low - 5, abs=2)
    lock.write_text(
        json.dumps({"started_at": time.time() - 5, "model": "xai:grok-4.7", "profile": "grok-4.7"})
    )
    high = meshgen.SCAD_TYPICAL["grok-4.7"][0]
    assert assets.cad_status("p")["eta_s"] == pytest.approx(high - 5, abs=2)  # the writer's
    lock.write_text(json.dumps({"started_at": time.time() - 1000, "model": "xai:grok-4.7"}))
    assert assets.cad_status("p")["eta_s"] == assets.SCAD_ETA_FLOOR_S
    lock.write_text(json.dumps({"started_at": time.time() - 4000, "model": "xai:grok-4.7"}))
    assert assets.cad_status("p")["status"] == "none"  # a stale lock doesn't count
    lock.write_text(json.dumps({"started_at": time.time(), "model": FAST, "pid": 999999}))
    assert assets.cad_status("p")["status"] == "none"  # nor one of a dead process


# --- the loop's records and budget -------------------------------------------------------------


@pytest.fixture
def photo(tmp_path) -> Path:
    path = tmp_path / "photo.jpg"
    img = Image.new("RGB", (400, 300), (255, 255, 255))
    img.paste((200, 30, 30), (100, 90, 300, 210))
    img.save(path)
    return path


def _scad_llm(monkeypatch, replies: list) -> list:
    """`asset_scad` answers in order, each with xAI usage (cost ticks)."""
    calls = []

    async def chat(role, messages, **kw):
        calls.append({"role": role, "messages": messages, "kw": kw})
        text = replies[min(len(calls), len(replies)) - 1]
        usage = SimpleNamespace(
            prompt_tokens=1600,
            completion_tokens=600,
            cost_in_usd_ticks=35_000_000,  # $0.0035
            completion_tokens_details=SimpleNamespace(reasoning_tokens=0),
        )
        return SimpleNamespace(
            model="grok-4.20-0309-non-reasoning",
            usage=usage,
            choices=[SimpleNamespace(message=SimpleNamespace(content=text))],
        )

    monkeypatch.setattr(meshgen.llm, "chat", chat)
    return calls


def _slab_pieces(dims):
    w, h, d = dims
    return [(trimesh.creation.box(bounds=[[-w / 2, 0, 0], [w / 2, h, d]]), "#EEEEEE")]


def test_the_profile_is_the_model_and_its_effort(monkeypatch):
    assert meshgen.scad_profile("xai:grok-4.7") == "grok-4.7@low"  # the default effort
    assert meshgen.scad_profile(FAST) == "grok-4.20-0309-non-reasoning"  # 4.20 takes no effort
    assert meshgen.scad_effort(FAST) is None
    assert meshgen.scad_cost_usd("grok-4.7@low") == meshgen.SCAD_TYPICAL["grok-4.7@low"][1]
    assert meshgen.scad_eta_s("grok-9@turbo") == meshgen.SCAD_DEFAULT[0]
    monkeypatch.setenv("ASSET_SCAD_EFFORT", "")
    get_settings.cache_clear()
    assert meshgen.scad_profile("xai:grok-4.7") == "grok-4.7"  # "" = the model's own default


def test_budget_by_model(monkeypatch):
    assert meshgen.scad_budget(FAST) == (1, 2)
    assert meshgen.scad_budget("xai:grok-4.7") == (1, 1)
    monkeypatch.setenv("ASSET_SCAD_SAMPLES", "2")
    monkeypatch.setenv("ASSET_SCAD_FIXES", "0")
    get_settings.cache_clear()
    assert meshgen.scad_budget(FAST) == (2, 0)


@pytest.mark.asyncio
async def test_three_answers_at_once_and_the_first_that_builds_wins(monkeypatch, photo, tmp_path):
    monkeypatch.setenv("ASSET_SCAD_SAMPLES", "3")
    get_settings.cache_clear()
    monkeypatch.setattr(meshgen, "wants_relief", lambda part: False)  # texture: no relief pass
    calls = _scad_llm(monkeypatch, ["```openscad\nA\n```", "```openscad\nB\n```", "```\nC\n```"])
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/openscad")
    dims = (614.68, 368.3, 515.62)
    lock_seen = []

    def build(exe, code, work, d):
        lock_seen.append(meshgen.scad_sidecar(out, "writing").exists())
        if code == "B":
            return _slab_pieces(dims), None
        return [], f"ERROR: {code}"

    monkeypatch.setattr(meshgen, "build_scad", build)
    out = tmp_path / "p" / "scad.glb"
    rec = await meshgen.grok_scad_run(_part(), photo, out)

    assert rec["ok"] and out.exists() and all(lock_seen)
    assert not meshgen.scad_sidecar(out, "writing").exists()
    assert [c["kw"]["temperature"] for c in calls] == [0.2, 0.7, 0.7]
    assert (rec["calls"], rec["fixes"], rec["compiled_first"], rec["samples"]) == (3, 0, 1, 3)
    assert rec["cost_usd"] == pytest.approx(0.0105)
    assert rec["made_by"] == f"{FAST} + openscad"
    assert rec["bbox_err_pct"] == pytest.approx(0, abs=1e-6)
    assert meshgen.scad_record(out)["cost_usd"] == pytest.approx(0.0105)
    assert rec["profile"] == "grok-4.20-0309-non-reasoning"
    assert meshgen.scad_eta_s(rec["profile"]) == rec["seconds"]  # this process's runs refine it


@pytest.mark.asyncio
async def test_a_run_that_never_builds_is_recorded_as_failed(monkeypatch, photo, tmp_path):
    calls = _scad_llm(monkeypatch, ["```openscad\nbad\n```"])
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/openscad")
    monkeypatch.setattr(meshgen, "build_scad", lambda *a: ([], "ERROR: syntax error"))
    out = tmp_path / "p" / "scad.glb"
    rec = await meshgen.grok_scad_run(_part(), photo, out)
    assert not rec["ok"] and not out.exists()
    assert len(calls) == 1 + 2  # one answer, two fix rounds (a non-reasoning model)
    failed = meshgen.scad_failure(out)
    assert failed["error"].startswith("ERROR: syntax error") and failed["model"] == FAST
    assert failed["cost_usd"] == pytest.approx(3 * 0.0035)
    assert not meshgen.scad_sidecar(out, "writing").exists()


@pytest.mark.asyncio
async def test_a_second_run_of_the_same_request_costs_nothing(monkeypatch):
    _scad_llm(monkeypatch, ["x"])
    first = await meshgen.cached_chat_meta("asset_scad", [{"role": "user", "content": "q"}])
    again = await meshgen.cached_chat_meta("asset_scad", [{"role": "user", "content": "q"}])
    other = await meshgen.cached_chat_meta(
        "asset_scad", [{"role": "user", "content": "q"}], sample=1
    )
    assert (first[1]["cached"], first[1]["cost_usd"]) == (False, pytest.approx(0.0035))
    assert (again[1]["cached"], again[1]["cost_usd"], again[1]["first_cost_usd"]) == (
        True,
        0.0,
        pytest.approx(0.0035),
    )
    assert other[1]["cached"] is False  # another sample is its own call


# --- (d) warm --scad ----------------------------------------------------------------------------


def _on_disk(pid: str, name: str = "Thing", photo: bool = True) -> Part:
    part = _part(pid, name=name, image_url=None)
    part.asset.tier, part.asset.status = "llm", "ready"
    jobs.save_part(part)
    (assets.part_dir(pid) / "model.glb").write_bytes(b"glb")
    if photo:
        (assets.part_dir(pid) / "image.jpg").write_bytes(SOURCE_PHOTO.read_bytes())
    return part


@pytest.mark.asyncio
async def test_warm_scad_is_parallel_resumable_and_records_failures(monkeypatch, tmp_path, capsys):
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")
    parts = [_on_disk(f"p{i}") for i in range(5)]
    _slab("p0")  # made before: skipped
    fail_ids = {"p1"}
    running, peak, grok, rebuilt = [0], [0], [], []

    async def fake_run(part, photo, out):
        grok.append(part.id)
        running[0] += 1
        peak[0] = max(peak[0], running[0])
        await asyncio.sleep(0.02)
        running[0] -= 1
        if part.id in fail_ids:
            rec = {
                "ok": False,
                "model": FAST,
                "error": "ERROR: nope",
                "calls": 5,
                "at": time.time(),
            }
            meshgen.scad_sidecar(out, "failed").write_text(json.dumps(rec))
            return {**rec, "seconds": 0.02, "cost_usd": 0.0175}
        _slab(part.id)
        return {"ok": True, "model": FAST, "calls": 3, "seconds": 0.02, "cost_usd": 0.0105}

    async def fake_resolve(part, **kw):
        rebuilt.append((part.id, kw))
        return part.model_copy(update={"asset": part.asset.model_copy(update={"tier": "scad"})})

    monkeypatch.setattr(meshgen, "grok_scad_run", fake_run)
    monkeypatch.setattr(assets, "resolve_asset", fake_resolve)

    got = await warm.warm_scad(parts, jobs_n=2)
    assert sorted(grok) == ["p1", "p2", "p3", "p4"] and peak[0] == 2
    assert (got["made"], got["failed"], got["exists"]) == (3, 1, 1)
    assert got["usd"] == pytest.approx(3 * 0.0105 + 0.0175)
    assert {pid for pid, _ in rebuilt} == {"p2", "p3", "p4"}
    assert all(kw == {"mode": "llm_scad", "select_it": False, "rebuild": True} for _, kw in rebuilt)
    rows = json.loads((tmp_path / "scad_warm.json").read_text())
    assert rows["p1"]["outcome"] == "failed" and rows["p1"]["error"] == "ERROR: nope"
    assert "estimate" in capsys.readouterr().out

    grok.clear()
    again = await warm.warm_scad(parts, jobs_n=2)  # resumed: nothing left but the failure
    assert grok == [] and (again["exists"], again["failed_before"]) == (4, 1)
    fail_ids.clear()
    await warm.warm_scad(parts, jobs_n=2, retry_failed=True)
    assert grok == ["p1"]


@pytest.mark.asyncio
async def test_warm_scad_estimate_only_runs_nothing(monkeypatch):
    monkeypatch.setattr(meshgen, "openscad", lambda: "/usr/bin/true")

    async def no_run(*a):
        raise AssertionError("estimate only")

    monkeypatch.setattr(meshgen, "grok_scad_run", no_run)
    got = await warm.warm_scad([_on_disk("a"), _on_disk("b")], jobs_n=3, estimate_only=True)
    est = got["estimate"]
    assert est["parts"] == 2 and est["model"] == FAST
    assert est["usd"] == pytest.approx(2 * meshgen.SCAD_TYPICAL["grok-4.20-0309-non-reasoning"][1])


@pytest.mark.asyncio
async def test_scad_parts_are_ids_then_demo_parts_then_catalog_items(monkeypatch, tmp_path):
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "no-scenes"))
    get_settings.cache_clear()
    dw = warm.SCAD_DEMO_PARTS[0]
    window = warm.SCAD_DEMO_PARTS[3]
    for pid, name in [("mine", "My knob"), (dw, "Dishwasher"), (window, "Vinyl Window")]:
        _on_disk(pid, name)
    got = await warm.scad_parts(ids=["mine", "missing"])
    assert [p.id for p in got] == ["mine", dw, window]
    assert [p.id for p in await warm.scad_parts(match="window")] == [window]
    assert [p.id for p in await warm.scad_parts(ids=["mine"], demo=False)] == ["mine"]


# --- opt-in: the real Grok + OpenSCAD -----------------------------------------------------------


@pytest.mark.live
@pytest.mark.skipif(meshgen.openscad() is None, reason="OpenSCAD not installed")
@pytest.mark.asyncio
async def test_live_grok_scad_on_a_knob(tmp_path, photo):
    """The configured writer (default grok-4.7 at low effort: ~1 min, ~$0.03; LLM_ASSET_SCAD /
    ASSET_SCAD_EFFORT pick another). Run: `set -a; . .env; set +a; pytest -m live -k grok_scad`."""
    knob = Part(
        id="live-knob",
        name="Round cabinet knob, satin nickel",
        dims_mm=Dims(w=32, h=32, d=28),
        material="zinc",
    )
    out = tmp_path / "live-knob" / "scad.glb"
    rec = await meshgen.grok_scad_run(knob, photo, out)
    assert rec["ok"], rec.get("error")
    scene = trimesh.load(out, force="scene")
    assert scene.extents == pytest.approx([0.032, 0.032, 0.028], abs=1e-4)
    assert rec["cost_usd"] is None or rec["cost_usd"] < 0.5
