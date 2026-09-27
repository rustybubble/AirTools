"""job-cancel: `POST /job/run/{run_id}/cancel` (runjob.cancel). The headset stops following a run
when the user switches to another scanned model mid-run; the server stops the run's chain at the
await it is in (a search or model wait), marks it `cancelled`, closes its actions with
`job_done {status: "cancelled"}`, and frees the session for a new job at once. Both kinds: "do the
whole job" (runjob) and the autonomous replace (replace_job). Everything is faked; no network."""

import asyncio
import uuid
from collections import OrderedDict

import pytest
from httpx import ASGITransport, AsyncClient

from server import agent, assets, jobs, runjob
from server.config import get_settings
from server.models import Job
from tests import test_replace_job as rj

# test_replace_job's fixtures: the kitchen-test scene with fresh run / job tables (autouse), and the
# faked search + model builds.
_scene = rj._scene
shop = rj.shop
_no_llm_chat, _run_id, _say = rj._no_llm_chat, rj._run_id, rj._say


def _never_search(started: list):
    """jobs.start_search -> a search that never finishes (the step waits on it until its cap)."""

    def never(req):
        job = Job(id=uuid.uuid4().hex[:12], status="running", stage="charting the candidates")
        jobs._jobs[job.id] = job
        jobs._done_events[job.id] = asyncio.Event()
        started.append(job)
        return job

    return never


async def _until(check, tries=300):
    for _ in range(tries):
        if check():
            return True
        await asyncio.sleep(0.01)
    return False


def _client():
    from server.app import app

    return AsyncClient(transport=ASGITransport(app=app), base_url="http://t")


# --- the replace run --------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_cancel_stops_a_replace_waiting_on_its_search_and_frees_the_session(
    monkeypatch, shop
):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    started: list = []
    monkeypatch.setattr(jobs, "start_search", _never_search(started))
    r = await _say("replace the dishwasher", session="hs1")
    run = runjob.get(_run_id(r))
    assert await _until(lambda: started and run.steps[3]["status"] == "running"), runjob.body(run)
    chain = run.tasks[0]
    assert not chain.done()

    async with _client() as client:
        res = await client.post(
            f"/job/run/{run.id}/cancel",
            json={"session_id": "hs1", "reason": "switched to zabel-gymnasium"},
        )
        assert res.status_code == 200
        assert res.json() == {
            "run_id": run.id,
            "status": "cancelled",
            "cancelled": True,
            "next": len(run.actions),
        }
        # the session is free now: a new job starts at once (no 409 / "a job is already running")
        again = await _say("replace the fridge", session="hs1")
        assert [a["name"] for a in again["actions"]][-1] == "job_started", again
        second = runjob.get(_run_id(again))
        assert second.id != run.id and second.status == "running"

        await asyncio.sleep(0.05)
        assert chain.cancelled(), "the chain's task is cancelled at its search wait"
        polled = (await client.get(f"/job/run/{run.id}")).json()
    assert polled["status"] == "cancelled"
    done = polled["actions"][-1]
    assert done == {
        "name": "job_done",
        "args": {
            "run_id": run.id,
            "status": "cancelled",
            "kind": "replace",
            "reason": "switched to zabel-gymnasium",
        },
    }
    by = {s["name"]: s["status"] for s in polled["steps"]}
    assert by["remove"] == "done" and by["measure"] == "done"
    assert {by[n] for n in ("search", "pick", "model", "place")} == {"cancelled"}
    names = [a["name"] for a in polled["actions"]]
    assert "place_part" not in names and names.count("job_done") == 1, names
    runjob.cancel(second)


@pytest.mark.asyncio
async def test_cancel_stops_the_next_models_builds(monkeypatch, shop):
    """After the pick goes in, the replace run builds the next two candidates' models (st.others):
    a cancel stops those builds too."""
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    gate = asyncio.Event()
    real = assets.resolve_asset
    building: list = []

    async def slow_after_first(part, **kw):
        if building:  # the pick's own model is fast; the others hang until cancelled
            building.append(part.id)
            await gate.wait()
        building.append(part.id)
        return await real(part, **kw)

    monkeypatch.setattr(assets, "resolve_asset", slow_after_first)
    r = await _say("replace the dishwasher", session="hs2")
    run = runjob.get(_run_id(r))
    assert await _until(lambda: len(run.tasks) > 1), runjob.body(run)
    others = run.tasks[1:]
    assert runjob.cancel(run, "switched to hospital-bg")
    await asyncio.sleep(0.05)
    assert all(t.cancelled() for t in others), "the next models' builds were cancelled"
    assert run.status == "cancelled"
    assert [a["name"] for a in run.actions].count("job_done") == 1


@pytest.mark.asyncio
async def test_cancel_is_idempotent_and_leaves_a_finished_run_alone(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    r = await _say("replace the dishwasher", session="hs3")
    run = runjob.get(_run_id(r))
    assert await _until(lambda: run.status != "running")
    assert run.status == "done"
    n = len(run.actions)
    async with _client() as client:
        res = (await client.post(f"/job/run/{run.id}/cancel")).json()
        assert res == {"run_id": run.id, "status": "done", "cancelled": False, "next": n}
        assert len(run.actions) == n, "a finished run gets no second job_done"
        assert (await client.post("/job/run/nope/cancel")).status_code == 404


@pytest.mark.asyncio
async def test_only_the_runs_session_may_cancel_it(monkeypatch, shop):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    monkeypatch.setattr(jobs, "start_search", _never_search([]))
    r = await _say("replace the dishwasher", session="hs4")
    run = runjob.get(_run_id(r))
    async with _client() as client:
        other = await client.post(f"/job/run/{run.id}/cancel", json={"session_id": "someone-else"})
        assert other.status_code == 403
        assert run.status == "running"
        mine = await client.post(f"/job/run/{run.id}/cancel", json={"session_id": "hs4"})
        assert mine.json()["cancelled"] is True


# --- "do the whole job" --------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_cancel_stops_a_whole_job_run(monkeypatch, tmp_path):
    monkeypatch.setenv("DATA_DIR", str(tmp_path / "data"))
    get_settings.cache_clear()
    monkeypatch.setattr(jobs, "_jobs", OrderedDict())
    monkeypatch.setattr(runjob, "_runs", OrderedDict())
    started: list = []
    monkeypatch.setattr(jobs, "start_search", _never_search(started))
    session = agent.Session(id="hs5")
    run = runjob.start(session, None, "ductless mini-split", {})
    assert await _until(lambda: started)
    assert run.steps[0]["name"] == "part" and run.steps[0]["status"] == "pending"
    assert runjob.cancel(run, "switched")
    await asyncio.sleep(0.05)
    assert run.tasks[0].cancelled()
    assert run.status == "cancelled"
    assert {s["status"] for s in run.steps} == {"cancelled"}
    assert [a["name"] for a in run.actions] == ["job_started", "job_done"]
    assert run.actions[-1]["args"] == {
        "run_id": run.id,
        "status": "cancelled",
        "kind": "job",
        "reason": "switched",
    }
    # the session may start a new one at once
    nxt = runjob.start(session, None, "a new faucet", {})
    assert nxt.status == "running"
    runjob.cancel(nxt)
    await asyncio.sleep(0.01)
    get_settings.cache_clear()
