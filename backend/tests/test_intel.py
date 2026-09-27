"""Installer intel: xAI Responses request shape, parsing, the honesty filter, caching/offline,
the HTTP endpoint and the agent tool. `/v1/responses` is respx-mocked with a fixture trimmed from
a live call (2026-09-26, mini-split installers in Atlanta), message text hand-edited to include
unevidenced and duplicate businesses."""

import json
from pathlib import Path

import httpx
import pytest
import respx
from fastapi.testclient import TestClient

from server import agent, cache, intel, llm
from server.app import app

FIXTURE = Path(__file__).parent / "fixtures" / "llm" / "xai_responses_installers.json"


@pytest.fixture(autouse=True)
def _tmp_data(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    from server.config import get_settings

    get_settings.cache_clear()
    agent._sessions.clear()


def _mock(router: respx.MockRouter) -> respx.Route:
    return router.post(llm.XAI_RESPONSES_URL).mock(
        return_value=httpx.Response(200, json=json.loads(FIXTURE.read_text()))
    )


def test_parse_responses_text_citations_cost():
    res = llm.parse_responses(json.loads(FIXTURE.read_text()))
    assert json.loads(res.text)["installers"][0]["name"] == "Estes Services"
    # annotations + web_search_call sources + opened pages, deduped
    assert set(res.citations) == {
        "https://www.estesair.com/ductless-mini-splits",
        "https://bardi.com/air-conditioning/ductless-mini-splits",
        "https://profindr.com/top/ductless-mini-split-atlanta-ga",
    }
    assert res.cost_usd == pytest.approx(0.12810175)
    assert res.tool_usage == {"web_search_calls": 9, "x_search_calls": 2, "x_posts_fetched": 2}


@pytest.mark.asyncio
async def test_find_installers_request_shape_and_honesty_filter():
    with respx.mock(assert_all_called=True) as router:
        route = _mock(router)
        found = await intel.find_installers("mini-split installer", "Atlanta, GA")

    req = route.calls.last.request
    assert req.headers["authorization"] == "Bearer test-dummy-key"
    body = json.loads(req.content)
    assert body["model"] == intel.MODEL
    web, x = body["tools"]
    assert web == {
        "type": "web_search",
        "user_location": {
            "type": "approximate",
            "country": "US",
            "city": "Atlanta",
            "region": "GA",
        },
    }
    assert x["type"] == "x_search" and x["from_date"] < "2099"
    assert body["text"]["format"]["type"] == "json_schema"
    assert body["include"] == ["no_inline_citations"]
    assert "mini-split installer" in body["input"] and "Atlanta, GA" in body["input"]

    names = [i.name for i in found.installers]
    # Ghost HVAC (no URL) and Fake Air (URL the search never returned) are dropped; the second
    # "estes services" merges into the first.
    assert names == ["Estes Services", "Bardi"]
    assert found.dropped == 2
    assert found.installers[0].evidence_urls == [
        "https://www.estesair.com/ductless-mini-splits",
        "https://profindr.com/top/ductless-mini-split-atlanta-ga",
    ]
    # the model's summary named a dropped business -> deterministic line instead
    assert "Ghost" not in found.summary
    assert "Estes Services" in found.summary
    assert found.cost_usd == pytest.approx(0.12810175)


@pytest.mark.asyncio
async def test_find_installers_cached_then_offline(monkeypatch):
    with respx.mock(assert_all_called=True) as router:
        route = _mock(router)
        first = await intel.find_installers("mini-split installer", "Atlanta, GA")
        again = await intel.find_installers("Mini-split installer", "atlanta ga")
        assert route.call_count == 1  # normalized key -> cache hit
    assert again == first

    monkeypatch.setenv("OFFLINE", "true")
    from server.config import get_settings

    get_settings.cache_clear()
    assert (await intel.find_installers("mini-split installer", "Atlanta, GA")) == first
    with pytest.raises(cache.OfflineMiss):
        await intel.find_installers("roofer", "Atlanta, GA")


def test_user_location_only_parses_city_region():
    assert intel._user_location("near Georgia Tech") == {"type": "approximate", "country": "US"}


def test_first_sentence():
    assert intel._first_sentence("R.S. Andrews and Bardi do it. Walmart too.") == (
        "R.S. Andrews and Bardi do it."
    )


def test_endpoint_defaults_location_and_maps_errors():
    client = TestClient(app)
    with respx.mock(assert_all_called=True) as router:
        route = _mock(router)
        resp = client.post("/intel/installers", json={"query": "mini-split installer"})
    assert resp.status_code == 200
    body = resp.json()
    assert body["location"] == "Atlanta, GA"
    assert [i["name"] for i in body["installers"]] == ["Estes Services", "Bardi"]
    assert "Atlanta" in json.loads(route.calls.last.request.content)["input"]

    assert client.post("/intel/installers", json={"query": " "}).status_code == 400
    with respx.mock() as router:
        router.post(llm.XAI_RESPONSES_URL).mock(return_value=httpx.Response(500, text="boom"))
        assert client.post("/intel/installers", json={"query": "roofer"}).status_code == 502


@pytest.mark.asyncio
async def test_agent_find_installer_tool_returns_show_installers_action():
    with respx.mock(assert_all_called=True) as router:
        route = _mock(router)
        session = agent._get_session("s1")
        result, action, job_id = await agent._run_tool(
            session, "find_installer", {"trade": "mini-split installer", "location": None}, {}
        )
    assert job_id is None
    assert "Atlanta, GA" in json.loads(route.calls.last.request.content)["input"]
    assert action["name"] == "show_installers"
    assert [i["name"] for i in action["args"]["installers"]] == ["Estes Services", "Bardi"]
    assert action["args"]["installers"][0]["evidence_urls"]
    assert result["spoken"] == action["args"]["summary"]


@pytest.mark.asyncio
async def test_agent_find_installer_offline_miss_is_an_error_not_a_crash(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    from server.config import get_settings

    get_settings.cache_clear()
    result, action, _ = await agent._run_tool(
        agent._get_session("s1"), "find_installer", {"trade": "roofer", "location": "Macon, GA"}, {}
    )
    assert action is None and "offline" in result["error"]
