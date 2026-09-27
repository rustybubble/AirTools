"""Walk-in renovation clip: frame pick, xAI video request/poll/download, cache/offline, routes,
agent tool. `imagine.edit` is stubbed; xAI and vidgen are respx-mocked; no sleeps (POLL_S=0)."""

import base64
import json
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, cache, flythrough, imagine, jobs
from server.app import app
from server.config import get_settings

EDITED = b"\xff\xd8\xff\xe0edited-end-frame"
MP4 = b"\x00\x00\x00\x18ftypmp42" + b"\x00" * 1000
MP4_URL = "https://vidgen.x.ai/tmp/abc/video.mp4"
PENDING = {"status": "pending"}
DONE = {
    "status": "done",
    "video": {"url": MP4_URL, "duration": 3, "respect_moderation": True},
    "model": "grok-imagine-video-1.5",
    "usage": {"cost_in_usd_ticks": 2_600_000_000},
}
FACING_Z = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]  # world-to-camera R; row 2 = view axis (+z)
FACING_BACK = [[1, 0, 0], [0, -1, 0], [0, 0, -1]]
REQ = {"site": "kitchen", "frame_id": "0010", "prompt": "navy cabinets"}
KITCHEN = Path(__file__).parents[1] / "scene" / "kitchen"


def _cams() -> list[dict]:
    """0000-0019 walk along +z in 0.1 m steps, all facing +z; 0020-0039 stand far off to the side
    (nothing behind them), so they exercise the fallback."""
    cams = []
    for i in range(40):
        pos = [0.0, 0.0, 0.1 * i] if i < 20 else [5.0 + i, 0.0, 0.0]
        cams.append(
            {"id": f"{i:04d}", "thumb": f"thumbs/{i:04d}.jpg", "R": FACING_Z, "position": pos}
        )
    return cams


def _thumb(frame_id: str) -> bytes:
    return b"\xff\xd8\xff\xe0thumb-" + frame_id.encode()


def _write_site(root: Path, cams: list[dict]) -> None:
    site = root / "kitchen"
    (site / "thumbs").mkdir(parents=True, exist_ok=True)
    for cam in cams:
        (site / cam["thumb"]).write_bytes(_thumb(cam["id"]))
    (site / "cameras.r1.json").write_text(json.dumps(cams))
    (site / "scene.json").write_text(json.dumps({"cameras": "cameras.r1.json", "revision": 1}))


@pytest.fixture(autouse=True)
def dirs(tmp_path, monkeypatch):
    _write_site(tmp_path / "scene", _cams())
    (tmp_path / "data" / "imagine").mkdir(parents=True)  # the stubbed edit doesn't make it
    monkeypatch.setenv("SCENE_DIR", str(tmp_path / "scene"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setattr(flythrough, "POLL_S", 0)
    get_settings.cache_clear()
    agent._sessions.clear()
    yield tmp_path
    get_settings.cache_clear()
    agent._sessions.clear()


@pytest.fixture(autouse=True)
def edits(monkeypatch):
    calls = []

    async def fake_edit(images, prompt, **kw):
        calls.append((images, prompt))
        return EDITED, {"id": "e" * 40, "cost_usd": 0.07, "latency_s": 11.0, "cached": False}

    monkeypatch.setattr(imagine, "edit", fake_edit)
    return calls


def _mock_video(router, *polls) -> respx.Route:
    submit = router.post(flythrough.VIDEO_URL).mock(
        return_value=httpx.Response(200, json={"request_id": "req-1"})
    )
    router.get(flythrough.POLL_URL.format("req-1")).mock(
        side_effect=[httpx.Response(200, json=p) for p in polls]
    )
    router.get(MP4_URL).mock(return_value=httpx.Response(200, content=MP4))
    return submit


async def _render(**kw) -> dict:
    with respx.mock(assert_all_called=True) as router:
        _mock_video(router, PENDING, DONE)
        out = await flythrough.start("kitchen", "0010", "navy cabinets", **kw)
        job = await jobs.wait(out["job_id"], 5)
    assert job.status == "done", job.error
    return job.result


# --- pick_frames --------------------------------------------------------------------------


def test_pick_frames_behind_along_view_axis_else_fallback(dirs):
    assert flythrough.pick_frames("kitchen", "0010") == ("0004", "0010")  # 0.6 m behind
    assert flythrough.pick_frames("kitchen", "0035") == ("0005", "0035")  # 30 entries earlier
    assert flythrough.pick_frames("kitchen", "0000") == ("0030", "0000")  # nothing earlier
    cams = _cams()
    cams[4]["R"] = FACING_BACK  # right spot, wrong way: skipped
    _write_site(dirs / "scene", cams)
    assert flythrough.pick_frames("kitchen", "0010")[0] in ("0003", "0005")
    with pytest.raises(ValueError):
        flythrough.pick_frames("kitchen", "9999")
    with pytest.raises(ValueError):
        flythrough.pick_frames("../etc", "0010")


@pytest.mark.skipif(not KITCHEN.is_dir(), reason="scene/kitchen not present (gitignored)")
def test_pick_frames_real_kitchen(monkeypatch):
    import numpy as np

    monkeypatch.setenv("SCENE_DIR", str(KITCHEN.parent))
    get_settings.cache_clear()
    cams = {c["id"]: c for c in json.loads((KITCHEN / "cameras.r1.json").read_text())}
    start, end = flythrough.pick_frames("kitchen", "0301")
    d = np.array(cams[end]["position"]) - np.array(cams[start]["position"])
    assert 0.4 <= d @ np.array(cams[end]["R"][2]) <= 0.8
    # 0481 has no camera behind it (G3's clip pulled back from 0301): the fallback kicks in
    assert flythrough.pick_frames("kitchen", "0481") == ("0331", "0481")


# --- render: request shape, poll, download, cost ---------------------------------------


@pytest.mark.asyncio
async def test_submit_poll_download(edits):
    with respx.mock(assert_all_called=True) as router:
        submit = _mock_video(router, PENDING, PENDING, DONE)
        out = await flythrough.start("kitchen", "0010", "navy cabinets")
        assert out["status"] == "pending" and out["from_frame"] == "0004"
        pending = TestClient(app).get(f"/scene/flythrough/{out['job_id']}").json()
        job = await jobs.wait(out["job_id"], 5)
    assert pending["status"] == "pending" and pending["label"] == "AI preview, not to scale"
    assert job.status == "done", job.error

    body = json.loads(submit.calls.last.request.content)
    assert submit.calls.last.request.headers["Authorization"] == "Bearer test-dummy-key"
    assert body["model"] == "grok-imagine-video-1.5"
    assert (
        body["image"]["url"]
        == "data:image/jpeg;base64," + base64.b64encode(_thumb("0004")).decode()
    )
    assert (
        body["last_frame"]["url"] == "data:image/jpeg;base64," + base64.b64encode(EDITED).decode()
    )
    assert body["duration"] == 3 and body["resolution"] == "480p"
    assert body["generate_audio"] is False
    assert "keyframes" not in body and "storage_options" not in body
    assert "navy cabinets" in body["prompt"]
    # the edit runs once, on the end frame only, with F2's exact prompt (shares its cache)
    assert edits == [([_thumb("0010")], "navy cabinets" + imagine.KEEP_CLAUSE)]

    r = job.result
    assert r["cost_usd"] == 0.33 and r["share_url"] is None and r["cached"] is False
    assert flythrough.clip_path(r["id"], "mp4").read_bytes() == MP4
    client = TestClient(app)
    got = client.get(f"/scene/flythrough/{job.id}").json()
    assert got["status"] == "done" and got["video_url"] == f"/scene/flythrough/{r['id']}.mp4"
    video = client.get(got["video_url"])
    assert video.content == MP4 and video.headers["content-type"] == "video/mp4"
    assert client.get(got["poster_url"]).content == EDITED


@pytest.mark.asyncio
async def test_public_flag_returns_share_url():
    done = {
        **DONE,
        "video": {**DONE["video"], "file_output": {"public_url": "https://files-cdn.x.ai/t/f.mp4"}},
    }
    with respx.mock(assert_all_called=True) as router:
        submit = _mock_video(router, done)
        out = await flythrough.start("kitchen", "0010", "navy cabinets", public=True)
        job = await jobs.wait(out["job_id"], 5)
    storage = json.loads(submit.calls.last.request.content)["storage_options"]
    assert storage["public_url"] == {"expires_after": flythrough.SHARE_EXPIRES_S}
    assert job.result["share_url"] == "https://files-cdn.x.ai/t/f.mp4"
    assert flythrough.video_args(job.result)["share_url"] == "https://files-cdn.x.ai/t/f.mp4"


@pytest.mark.asyncio
@pytest.mark.parametrize("status", ["failed", "expired"])
async def test_failed_or_expired_job_gives_the_still(status):
    with respx.mock(assert_all_called=False) as router:
        _mock_video(router, PENDING, {"status": status})
        out = await flythrough.start("kitchen", "0010", "navy cabinets")
        job = await jobs.wait(out["job_id"], 5)
    assert job.status == "failed" and status in job.error
    got = TestClient(app).get(f"/scene/flythrough/{job.id}").json()
    assert got["status"] == "failed" and got["spoken"] == flythrough.FAILED_LINE
    assert TestClient(app).get(got["poster_url"]).content == EDITED  # the still
    assert "video_url" not in got


def test_moderation_400_returns_422():
    with respx.mock(assert_all_called=True) as router:
        router.post(flythrough.VIDEO_URL).mock(
            return_value=httpx.Response(
                400, json={"code": "invalid_argument", "error": "Rejected by content moderation."}
            )
        )
        resp = TestClient(app).post("/scene/flythrough", json=REQ)
    assert resp.status_code == 422


def test_bad_frame_400_and_bad_ids_404():
    client = TestClient(app)
    assert client.post("/scene/flythrough", json={**REQ, "frame_id": "9999"}).status_code == 400
    assert client.post("/scene/flythrough", json={**REQ, "prompt": "  "}).status_code == 400
    assert client.get("/scene/flythrough/nope").status_code == 404
    assert client.get("/scene/flythrough/..%2Fx.mp4").status_code == 404
    assert flythrough.clip_path("../../etc/passwd", "mp4") is None


# --- cache + OFFLINE ---------------------------------------------------------------------


@pytest.mark.asyncio
async def test_cache_hit_makes_no_calls(edits):
    first = await _render()
    with respx.mock(assert_all_mocked=True):  # no routes: any HTTP call would raise
        again = await flythrough.start("kitchen", "0010", "navy cabinets")
    assert again["status"] == "done" and again["job_id"] is None and again["cached"] is True
    assert again["video_url"] == first["video_url"] and again["cost_usd"] == 0.0
    assert len(edits) == 1


@pytest.mark.asyncio
async def test_offline_miss_503_hit_done(monkeypatch):
    await _render()
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    client = TestClient(app)
    with respx.mock(assert_all_mocked=True):
        hit = client.post("/scene/flythrough", json=REQ)
        miss = client.post("/scene/flythrough", json={**REQ, "prompt": "never rehearsed"})
    assert hit.status_code == 200 and hit.json()["status"] == "done"
    assert miss.status_code == 503
    with pytest.raises(cache.OfflineMiss):
        await flythrough.start("kitchen", "0010", "never rehearsed")


# --- agent ---------------------------------------------------------------------------------


def _chat_calling(name: str, args: dict, seen: list):
    call = SimpleNamespace(id="c0", function=SimpleNamespace(name=name, arguments=json.dumps(args)))
    message = SimpleNamespace(content=None, tool_calls=[call])

    async def chat(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        return SimpleNamespace(choices=[SimpleNamespace(message=message)])

    return chat


@pytest.mark.asyncio
async def test_agent_walk_in_preview_then_show_video(monkeypatch):
    seen = []
    monkeypatch.setattr(agent, "chat", _chat_calling("walk_in_preview", {"prompt": "navy"}, seen))
    await agent.handle_command("s", "make me a walk in", {})
    assert "walk_in_preview" not in seen[-1]  # no site/frame_id: not offered

    ctx = {"site": "kitchen", "frame_id": "0010"}
    with respx.mock(assert_all_called=True) as router:
        _mock_video(router, DONE)
        out = await agent.handle_command("s", "walk me into it with navy cabinets", ctx)
        assert "walk_in_preview" in seen[-1]
        assert out["reply"] == flythrough.RENDERING_LINE and out["job_id"] is None
        assert out["actions"][0]["name"] == "flythrough_started"
        job = await jobs.wait(out["actions"][0]["args"]["job_id"], 5)
    assert job.status == "done"

    # the next command (any, here a fast-path one) carries the finished clip, once
    nxt = await agent.handle_command("s", "equip the tape", {})
    assert [a["name"] for a in nxt["actions"]] == ["show_video", "equip_tool"]
    assert nxt["actions"][0]["args"] == {
        "video_url": job.result["video_url"],
        "poster_url": job.result["poster_url"],
        "label": "AI preview, not to scale",
    }
    later = await agent.handle_command("s", "equip the level", {})
    assert [a["name"] for a in later["actions"]] == ["equip_tool"]

    # rendered already: plays at once
    with respx.mock(assert_all_mocked=True):
        again = await agent.handle_command("s", "walk me into it with navy cabinets", ctx)
    assert again["reply"] == flythrough.READY_LINE
    assert again["actions"][0]["name"] == "show_video"
