import json
from pathlib import Path

import httpx
import pydantic
import pytest
import respx

from server import web
from server.config import get_settings
from server.llm import Source, chat, extract, resolve_role, web_search

FIXTURES = Path(__file__).parent / "fixtures" / "llm"


def _load(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text())


GROQ_URL = "https://api.groq.com/openai/v1/chat/completions"
DEEPSEEK_URL = "https://api.deepseek.com/chat/completions"


class PartCandidate(pydantic.BaseModel):
    name: str
    needs_measurement_tool: bool
    width_mm: int


# --- role resolution -------------------------------------------------------


def test_resolve_role_defaults():
    assert resolve_role("agent") == ("groq", "openai/gpt-oss-120b")
    assert resolve_role("extract") == ("groq", "openai/gpt-oss-120b")
    assert resolve_role("search") == ("groq", "openai/gpt-oss-120b")
    assert resolve_role("cheap") == ("groq", "openai/gpt-oss-20b")
    assert resolve_role("hard") == ("deepseek", "deepseek-v4-pro")


def test_resolve_role_env_override(monkeypatch):
    monkeypatch.setenv("LLM_AGENT", "xai:grok-4.7")
    get_settings.cache_clear()
    assert resolve_role("agent") == ("xai", "grok-4.7")


def test_resolve_role_unknown():
    with pytest.raises(ValueError):
        resolve_role("nope")


# --- tools + response_format guard -----------------------------------------


@pytest.mark.asyncio
async def test_chat_rejects_tools_with_response_format():
    with pytest.raises(ValueError, match="tools . response_format"):
        await chat(
            "agent",
            [{"role": "user", "content": "hi"}],
            tools=[{"type": "browser_search"}],
            response_format={"type": "json_object"},
        )


# --- per-role output cap (vision: Groq free tier is 1000 output tokens/min) -------------------


@pytest.mark.asyncio
async def test_chat_caps_vision_output_tokens_only():
    fixture = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=fixture))
        msgs = [{"role": "user", "content": "hi"}]
        await chat("vision", msgs)
        vision_body = json.loads(route.calls.last.request.content)
        await chat("vision", msgs, max_tokens=50)
        override_body = json.loads(route.calls.last.request.content)
        await chat("agent", msgs)
        agent_body = json.loads(route.calls.last.request.content)

    assert vision_body["max_tokens"] == 400
    assert override_body["max_tokens"] == 50  # a caller's own cap wins
    assert "max_tokens" not in agent_body


# --- extract: groq strict json_schema happy path ----------------------------


@pytest.mark.asyncio
async def test_extract_happy_path_groq():
    fixture = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=fixture))
        result = await extract(
            "extract", [{"role": "user", "content": "describe the part"}], PartCandidate
        )

    assert result == PartCandidate(
        name="5 inch K-style hidden hanger bracket", needs_measurement_tool=False, width_mm=127
    )
    body = json.loads(route.calls.last.request.content)
    assert "tools" not in body
    assert body["response_format"]["type"] == "json_schema"
    assert body["response_format"]["json_schema"]["strict"] is True
    schema = body["response_format"]["json_schema"]["schema"]
    assert schema["additionalProperties"] is False
    assert set(schema["required"]) == set(schema["properties"].keys())


@pytest.mark.asyncio
async def test_extract_retry_on_invalid_json():
    bad = {
        "id": "x",
        "object": "chat.completion",
        "created": 1,
        "model": "openai/gpt-oss-120b",
        "choices": [
            {
                "index": 0,
                "finish_reason": "stop",
                "message": {"role": "assistant", "content": "not json"},
            }
        ],
    }
    good = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(
            side_effect=[httpx.Response(200, json=bad), httpx.Response(200, json=good)]
        )
        result = await extract(
            "extract", [{"role": "user", "content": "describe the part"}], PartCandidate
        )

    assert route.call_count == 2
    assert result.width_mm == 127
    second_body = json.loads(route.calls[1].request.content)
    assert second_body["messages"][-2]["content"] == "not json"


@pytest.mark.asyncio
async def test_extract_retries_groq_json_validate_failed():
    err = {
        "error": {
            "message": "Failed to validate JSON.",
            "type": "invalid_request_error",
            "code": "json_validate_failed",
            "failed_generation": "",
        }
    }
    good = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(
            side_effect=[httpx.Response(400, json=err), httpx.Response(200, json=good)]
        )
        result = await extract("extract", [{"role": "user", "content": "x"}], PartCandidate)
    assert route.call_count == 2
    assert result.width_mm == 127


# --- extract: deepseek json_object path -------------------------------------


@pytest.mark.asyncio
async def test_extract_deepseek_uses_json_object():
    resp = {
        "id": "x",
        "object": "chat.completion",
        "created": 1,
        "model": "deepseek-v4-pro",
        "choices": [
            {
                "index": 0,
                "finish_reason": "stop",
                "message": {
                    "role": "assistant",
                    "content": json.dumps(
                        {"name": "bracket", "needs_measurement_tool": True, "width_mm": 10}
                    ),
                },
            }
        ],
    }
    with respx.mock(assert_all_called=True) as router:
        route = router.post(DEEPSEEK_URL).mock(return_value=httpx.Response(200, json=resp))
        result = await extract("hard", [{"role": "user", "content": "describe"}], PartCandidate)

    assert result.name == "bracket"
    body = json.loads(route.calls.last.request.content)
    assert body["response_format"] == {"type": "json_object"}
    assert "tools" not in body
    assert body["messages"][0]["role"] == "system"
    assert "schema" in body["messages"][0]["content"].lower()


# --- chat: Groq quirks the openai SDK must tolerate --------------------------


@pytest.mark.asyncio
async def test_chat_parses_groq_service_tier_the_sdk_doesnt_know():
    """Groq's `service_tier: "on_demand"` isn't in the openai SDK's own Literal for that field.
    The real call path (`AsyncOpenAI` without `_strict_response_validation`) must still parse
    it -- if this ever starts raising, that's the minimal spot in llm.py to relax."""
    fixture = _load("groq_tool_calling_alone.json")
    assert fixture["service_tier"] == "on_demand"
    with respx.mock(assert_all_called=True) as router:
        router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=fixture))
        resp = await chat(
            "agent",
            [{"role": "user", "content": "what's the last gutter measurement?"}],
            tools=[{"type": "function", "function": {"name": "get_gutter_measurement"}}],
        )

    assert resp.service_tier == "on_demand"
    assert resp.choices[0].message.tool_calls[0].function.name == "get_gutter_measurement"


# --- web_search --------------------------------------------------------------


@pytest.mark.asyncio
async def test_web_search_parses_sources():
    fixture = _load("groq_browser_search.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(GROQ_URL).mock(return_value=httpx.Response(200, json=fixture))
        result = await web_search("5 inch K-style hidden hanger gutter bracket price")

    assert result.text
    assert result.sources
    assert any("homedepot.com" in s.url for s in result.sources)
    assert all(hasattr(s, "title") and hasattr(s, "content") for s in result.sources)

    body = json.loads(route.calls.last.request.content)
    assert body["tools"] == [{"type": "browser_search"}]
    assert body["tool_choice"] == "required"
    assert "response_format" not in body


@pytest.mark.asyncio
async def test_web_search_falls_back_to_web_search_for_unverified_provider(monkeypatch):
    """xAI's web_search response shape was never live-verified -- config-only provider swap
    (LLM_SEARCH=xai:...) must still work by falling back to server.web.search(), not raise."""
    monkeypatch.setenv("LLM_SEARCH", "xai:grok-4.7")
    get_settings.cache_clear()

    async def fake_web_search(query, n=6):
        assert query == "gutter hanger dims"
        return [
            web.Hit(
                title="Amerimax hanger", url="https://example.com/p", text="specs", source="exa"
            )
        ]

    monkeypatch.setattr("server.llm.web.search", fake_web_search)
    result = await web_search("gutter hanger dims")

    assert result.sources == [
        Source(title="Amerimax hanger", url="https://example.com/p", content="specs")
    ]


# --- live smoke test, never run by default (see pyproject `-m 'not live'') --


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_groq_extract_tiny():
    result = await extract(
        "extract",
        [{"role": "user", "content": "A 5 inch K-style hidden hanger bracket, 127mm wide."}],
        PartCandidate,
    )
    assert isinstance(result, PartCandidate)


# --- asset role: the xai path carries images --------------------------------------------------


def test_asset_role_defaults_to_groq_qwen():
    assert resolve_role("asset") == ("groq", "qwen/qwen3.8-27b")
    assert resolve_role("asset_scad") == ("xai", "grok-4.7")


@pytest.mark.asyncio
async def test_asset_role_on_xai_sends_the_image(monkeypatch):
    monkeypatch.setenv("LLM_ASSET", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    url = "data:image/jpeg;base64,/9j/4AAQ"
    messages = [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": "which template?"},
                {"type": "image_url", "image_url": {"url": url}},
            ],
        }
    ]
    body = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post("https://api.x.ai/v1/chat/completions").mock(
            return_value=httpx.Response(200, json=body)
        )
        await chat("asset", messages, response_format={"type": "json_object"}, max_retries=0)
    sent = json.loads(route.calls.last.request.content)
    assert sent["model"] == "grok-4.20-0309-non-reasoning"
    assert sent["messages"][0]["content"][1] == {"type": "image_url", "image_url": {"url": url}}
    assert "max_retries" not in sent


# --- reasoning parameters are per model (xAI's grok-4.20 models 400 on reasoning_effort) --------

XAI_URL = "https://api.x.ai/v1/chat/completions"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "model,kept",
    [
        ("grok-4.20-0309-non-reasoning", False),
        ("grok-4.20-0309-reasoning", False),
        ("grok-4.7", True),
        ("grok-4.5", True),
    ],
)
async def test_reasoning_effort_only_for_models_that_take_it(monkeypatch, model, kept):
    monkeypatch.setenv("LLM_AGENT", f"xai:{model}")
    get_settings.cache_clear()
    body = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        route = router.post(XAI_URL).mock(return_value=httpx.Response(200, json=body))
        await chat("agent", [{"role": "user", "content": "hi"}], reasoning_effort="low")
    sent = json.loads(route.calls.last.request.content)
    assert ("reasoning_effort" in sent) is kept
    assert sent["model"] == model


@pytest.mark.asyncio
async def test_a_model_that_rejects_reasoning_effort_is_retried_without_and_remembered(
    monkeypatch,
):
    from server import llm

    monkeypatch.setattr(llm, "_no_reasoning_models", set())
    monkeypatch.setenv("LLM_AGENT", "xai:grok-9-new")
    get_settings.cache_clear()
    body = _load("groq_json_schema_alone.json")
    refusal = {"code": "Client specified an invalid argument",
               "error": "Model grok-9-new does not support parameter reasoningEffort."}  # fmt: skip
    with respx.mock(assert_all_called=True) as router:
        route = router.post(XAI_URL).mock(
            side_effect=[httpx.Response(400, json=refusal), httpx.Response(200, json=body)]
        )
        await chat("agent", [{"role": "user", "content": "hi"}], reasoning_effort="low")
        first, second = (json.loads(c.request.content) for c in route.calls)
    assert first["reasoning_effort"] == "low"
    assert "reasoning_effort" not in second
    assert not llm.takes_reasoning_effort("xai", "grok-9-new")  # the next call skips it


@pytest.mark.asyncio
async def test_other_bad_requests_still_raise(monkeypatch):
    import openai

    monkeypatch.setenv("LLM_AGENT", "xai:grok-4.7")
    get_settings.cache_clear()
    with respx.mock(assert_all_called=True) as router:
        router.post(XAI_URL).mock(return_value=httpx.Response(400, json={"error": "bad tools"}))
        with pytest.raises(openai.BadRequestError):
            await chat("agent", [{"role": "user", "content": "hi"}], reasoning_effort="low")


# --- Groq can't answer -> the same call on xAI (LLM_FALLBACK) ----------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize("status", [429, 503])
async def test_groq_unavailable_falls_back_to_xai(monkeypatch, status):
    monkeypatch.setenv("LLM_FALLBACK", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    body = _load("groq_json_schema_alone.json")
    with respx.mock(assert_all_called=True) as router:
        router.post(GROQ_URL).mock(return_value=httpx.Response(status, json={"error": "busy"}))
        xai = router.post(XAI_URL).mock(return_value=httpx.Response(200, json=body))
        await chat(
            "vision", [{"role": "user", "content": "hi"}], reasoning_effort="low", max_retries=0
        )
    sent = json.loads(xai.calls.last.request.content)
    assert sent["model"] == "grok-4.20-0309-non-reasoning"
    assert "reasoning_effort" not in sent  # grok-4.20 400s on it
    assert sent["max_tokens"] == 400  # the role's own settings ride along


@pytest.mark.asyncio
async def test_no_fallback_for_bad_requests_groq_only_tools_or_when_off(monkeypatch):
    import openai

    from server import llm

    monkeypatch.setenv("LLM_FALLBACK", "xai:grok-4.20-0309-non-reasoning")
    get_settings.cache_clear()
    assert llm.fallback_for("groq", [{"type": "browser_search"}]) is None
    assert llm.fallback_for("xai", None) is None
    assert llm.fallback_for("groq", None) == ("xai", "grok-4.20-0309-non-reasoning")
    with respx.mock(assert_all_called=True) as router:
        router.post(GROQ_URL).mock(return_value=httpx.Response(400, json={"error": "bad"}))
        with pytest.raises(openai.BadRequestError):
            await chat("agent", [{"role": "user", "content": "hi"}])
    monkeypatch.setenv("LLM_FALLBACK", "")
    get_settings.cache_clear()
    assert llm.fallback_for("groq", None) is None
