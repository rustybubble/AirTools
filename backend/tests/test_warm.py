import pytest

from server import agent, jobs, warm
from server.bom import Bom
from server.config import get_settings
from server.models import Dims, Fit, Part


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def _part(part_id: str) -> Part:
    return Part(
        id=part_id,
        name="Amerimax hanger",
        dims_mm=Dims(w=127, d=38, h=45),
        fit=Fit(status="fits", spare_mm=38, axis="w"),
    )


@pytest.mark.asyncio
async def test_warm_runs_against_monkeypatched_pipeline(monkeypatch):
    found = []
    resolved = []
    spoken = []

    async def fake_find_parts(req):
        found.append(req.query)
        return [_part(req.query.replace(" ", "-"))]

    async def fake_resolve_asset(part):
        resolved.append(part.id)
        part.asset.tier = "proxy"
        part.asset.status = "ready"
        return part

    async def fake_speak(text):
        spoken.append(text)
        return b"wav-bytes", "audio/wav"

    bom_calls = []

    async def fake_what_else(parts, counts):
        bom_calls.append((tuple(p.id for p in parts), counts))
        return Bom(id="bom-warm", part_ids=[p.id for p in parts], lines=[], total_usd=0.0)

    monkeypatch.setattr(warm.search, "find_parts", fake_find_parts)
    monkeypatch.setattr(warm.assets, "resolve_asset", fake_resolve_asset)
    monkeypatch.setattr(warm.voice, "speak", fake_speak)
    monkeypatch.setattr(warm.bom, "what_else", fake_what_else)
    commands = []

    async def fake_handle_command(session_id, text, context):
        commands.append((text, context["selected_part_id"]))
        return {"reply": f"reply to {text}", "actions": [], "job_id": None}

    monkeypatch.setattr(warm.agent, "handle_command", fake_handle_command)

    await warm._run(warm.DEFAULT_QUERIES)

    # every scripted demo command ran against the first gutter candidate, reply TTS'd
    assert commands == [(text, "gutter-hanger") for text in warm.DEMO_COMMANDS]
    assert {f"reply to {text}" for text in warm.DEMO_COMMANDS} <= set(spoken)

    assert found == ["gutter hanger", "window air conditioner"]
    assert resolved == ["gutter-hanger", "window-air-conditioner"]

    # every candidate is persisted to disk (jobs-equivalent path), findable by id afterwards
    for part_id in resolved:
        saved = jobs.load_part(part_id)
        assert saved is not None
        assert saved.asset.tier == "proxy"

    # the agent's canned replies + the offline lines + both jobs' spoken summaries got TTS'd
    assert set(agent._CANNED_REPLY.values()) <= set(spoken)
    assert agent.OFFLINE_REPLY in spoken
    assert jobs.OFFLINE_NO_CANDIDATES in spoken
    assert any("hanger" in line.lower() for line in spoken)
    assert any("conditioner" in line.lower() for line in spoken)

    # what_else warmed for the first (only) gutter-hanger candidate x8
    assert bom_calls == [(("gutter-hanger",), {"gutter-hanger": 8})]


@pytest.mark.asyncio
async def test_warm_bom_skips_when_no_gutter_candidates(monkeypatch):
    async def fail_what_else(parts, counts):
        pytest.fail("must not call what_else with no gutter-hanger candidates")

    monkeypatch.setattr(warm.bom, "what_else", fail_what_else)
    await warm.warm_bom({"gutter hanger": []})
    await warm.warm_bom({})


@pytest.mark.asyncio
async def test_warm_query_returns_empty_list_when_find_parts_finds_nothing(monkeypatch):
    async def fake_find_parts(req):
        return []

    fail = pytest.fail  # never reached if resolve_asset isn't called for zero candidates
    monkeypatch.setattr(warm.search, "find_parts", fake_find_parts)
    monkeypatch.setattr(warm.assets, "resolve_asset", lambda part: fail("no candidates to resolve"))

    parts = await warm.warm_query({"query": "nothing", "measurement": None})
    assert parts == []


@pytest.mark.asyncio
async def test_retry_proxies_re_resolves_proxy_parts(monkeypatch):
    proxy = _part("hanger")
    proxy.asset.tier, proxy.asset.status = "proxy", "ready"
    jobs.save_part(proxy)
    (warm.assets.part_dir("hanger") / "model.glb").write_bytes(b"box")
    seen = []

    async def fake_find_parts(req):
        return [_part("hanger")]

    async def fake_resolve_asset(part):
        seen.append((part.asset.status, (warm.assets.part_dir(part.id) / "model.glb").exists()))
        return part

    monkeypatch.setattr(warm.search, "find_parts", fake_find_parts)
    monkeypatch.setattr(warm.assets, "resolve_asset", fake_resolve_asset)

    await warm.warm_query({"query": "gutter hanger"})
    await warm.warm_query({"query": "gutter hanger"}, retry_proxies=True)

    assert seen == [("pending", True), ("pending", False)]  # model.glb kept, then cleared


@pytest.mark.asyncio
async def test_grok_moulded_runs_only_on_moulded_parts_and_re_resolves(monkeypatch):
    from server import meshgen

    parts = [_part("clip"), _part("ac"), _part("no-photo")]
    for part in parts:
        part.asset.tier, part.asset.status = "llm", "ready"
        jobs.save_part(part)
        (warm.assets.part_dir(part.id) / "model.glb").write_bytes(b"glb")
        if part.id != "no-photo":
            (warm.assets.part_dir(part.id) / "image.jpg").write_bytes(b"jpg")
    shape = {"clip": "moulded", "ac": "template"}

    async def fake_ask(part, photo):
        return meshgen.Plan("bent_strip", {}, {}, {}, "right", 0, True, shape[part.id])

    grok, resolved = [], []

    async def fake_grok_scad(part, photo, out):
        grok.append(part.id)
        out.write_bytes(b"scad")
        return True

    async def fake_resolve_asset(part):
        resolved.append((part.id, (warm.assets.part_dir(part.id) / "model.glb").exists()))
        part.asset.tier = "scad"
        return part

    monkeypatch.setattr(meshgen, "ask", fake_ask)
    monkeypatch.setattr(meshgen, "grok_scad", fake_grok_scad)
    monkeypatch.setattr(warm.assets, "resolve_asset", fake_resolve_asset)

    out = await warm.warm_grok_moulded(parts)

    assert grok == ["clip"]
    assert resolved == [("clip", False)]  # model.glb cleared so the scad tier is picked up
    assert [p.asset.tier for p in out] == ["scad", "llm", "llm"]
