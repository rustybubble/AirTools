"""Thin OpenAI-compatible LLM client: Groq (primary) / DeepSeek (fallback) / xAI (future swap).

One client, provider/model picked per role from `ROLE_DEFAULTS` or a `LLM_<ROLE>=provider:model`
env override -- see docs/research/llm-providers.md §6 for why. Quirks encoded here:

- never send `tools` and `response_format` in the same call (Groq/xAI 400: "json mode cannot be
  combined with tool/function calling") -- `chat()` guards it.
- Groq/xAI strict json_schema needs `additionalProperties: false` and *every* property in
  `required` (optional fields stay nullable via `anyOf` null, not by being absent from
  `required`) -- `_strict_schema()`.
- DeepSeek has no json_schema/strict mode: `json_object` + schema pasted into the prompt,
  validate-and-retry instead of enforced structure.
- Same for Groq's only vision model (`qwen/qwen3.8-27b`): `json_mode` but no
  `structured_outputs` (confirmed live via `GET /openai/v1/models`, 2026-09-24) -- `extract()`
  routes it through the DeepSeek-style prompt-schema path via `NO_STRICT_SCHEMA_MODELS`.
"""

import json
import logging
import time
from typing import Any

import httpx
import openai
import pydantic
from openai import AsyncOpenAI
from openai.types.chat import ChatCompletion

from server import cache, web
from server.config import get_settings
from server.keys import KeyPool, KeysExhausted

logger = logging.getLogger(__name__)

PROVIDERS = {
    "groq": {"base_url": "https://api.groq.com/openai/v1", "api_key_field": "GROQ_API_KEY"},
    "deepseek": {"base_url": "https://api.deepseek.com", "api_key_field": "DEEPSEEK_API_KEY"},
    "xai": {"base_url": "https://api.x.ai/v1", "api_key_field": "GROK_API_KEY"},
}

ROLE_DEFAULTS = {
    "agent": "groq:openai/gpt-oss-120b",
    "extract": "groq:openai/gpt-oss-120b",
    "search": "groq:openai/gpt-oss-120b",
    "cheap": "groq:openai/gpt-oss-20b",
    "hard": "deepseek:deepseek-v4-pro",
    # Groq's only vision-capable model as of 2026-09-24 (confirmed live: `GET
    # /openai/v1/models` -- input_modalities includes "image" only for this id among the
    # current listing). Max 3 images/request, no structured_outputs (see extract() below).
    "vision": "groq:qwen/qwen3.8-27b",
    # Part asset generation (server/meshgen.py): one vision call per part. `asset_scad` is the
    # offline Grok OpenSCAD job for moulded parts (`server.warm --grok-moulded`), never live.
    "asset": "groq:qwen/qwen3.8-27b",
    "asset_scad": "xai:grok-4.7",
    # Site-walk report summary (Grok track; live-checked 2026-09-26 on our key). One call per
    # report content hash; server/report.py falls back to "agent" if xAI fails.
    "report": "xai:grok-4.3",
    # "Where does it go?" planner picker (server/plan.py): one short strict-schema call, no
    # geometry. A Grok-feature default; groq:openai/gpt-oss-120b also works (f7-planner.md).
    "plan": "xai:grok-4.20-0309-non-reasoning",
    # Drone condition survey (server/survey.py): one vision call over <=6 capture frames. 4.20
    # non-reasoning did it for $0.007 in 9 s on 3 frames; grok-4.7 at low effort gives tighter
    # boxes for ~3x the cost and ~35 s (docs/research/grok-ideas/g4-round3.md calls #1/#2).
    "survey": "xai:grok-4.20-0309-non-reasoning",
    "survey_careful": "xai:grok-4.7",
    # Capture coach (server/coverage.py): two spoken sentences from computed coverage, ~$0.002.
    "coach": "xai:grok-4.20-0309-non-reasoning",
    # Install coach (server/coach.py): yes/no/cant_see answers on one headset frame, ~$0.001 and
    # ~2 s (G6 call #14). `coach` is F12's capture coach, hence the longer name.
    "coach_vision": "xai:grok-4.20-0309-non-reasoning",
    # Job packet blurbs (server/packet.py): one fast call per packet content hash, via
    # `responses()`, so it must stay an xai model; any failure falls back to a template.
    "packet": "xai:grok-4.20-0309-non-reasoning",
    # Live labels (server/labels.py): one streamed vision call per frame, first token in
    # 0.6-0.8 s, $0.001 (docs/research/grok-ideas/g6-round5.md #6-7). grok-4.7 took 58.7 s.
    "labels": "xai:grok-4.20-0309-non-reasoning",
    # Booth wall caption (server/booth.py): one line per shared design, number-checked.
    "booth": "xai:grok-4.20-0309-non-reasoning",
    # Paper-quote reader (server/quote.py): one strict-schema vision call per quote image, cached
    # by its hash. G6 saw 4.20 non-reasoning read printed text for ~$0.001 a frame.
    "quote": "xai:grok-4.20-0309-non-reasoning",
    # catalog: what kind of place a scan is, from 2 thumbnails + the scene's parts and labels,
    # plus up to 2 site-specific categories (server/catalog.py). Once per site revision, cached.
    "catalog": "xai:grok-4.20-0309-non-reasoning",
}

# Models with `json_mode` but no `structured_outputs`/`json_schema` support -- need the same
# schema-in-prompt + validate/retry treatment `extract()` already gives DeepSeek.
NO_STRICT_SCHEMA_MODELS = {"qwen/qwen3.8-27b"}

# Reasoning parameters are per model, not per provider (live-checked 2026-09-26 on our xAI key):
# grok-4.3 / 4.5 / 4.6 / 4.7 take `reasoning_effort`; both grok-4.20 models (reasoning and
# non-reasoning) answer HTTP 400 "does not support parameter reasoningEffort". `chat()` drops the
# parameter for these, and learns any other model that 400s on it (retried once without it).
NO_REASONING_EFFORT_PREFIXES = ("grok-4.20", "grok-3", "grok-4-0709", "grok-4-fast", "grok-code")
_REASONING_PARAMS = ("reasoning_effort", "reasoning")
_no_reasoning_models: set[tuple[str, str]] = set()


def takes_reasoning_effort(provider: str, model: str) -> bool:
    """Whether `provider:model` accepts `reasoning_effort` (see NO_REASONING_EFFORT_PREFIXES)."""
    if (provider, model) in _no_reasoning_models:
        return False
    if provider == "xai":
        return "non-reasoning" not in model and not model.startswith(NO_REASONING_EFFORT_PREFIXES)
    return True


def fallback_for(provider: str, tools: list[dict[str, Any]] | None) -> tuple[str, str] | None:
    """Where a Groq call goes when Groq can't answer it (LLM_FALLBACK, default xAI grok-4.20
    non-reasoning: vision, strict json_schema and tools all work there). None for other
    providers, without GROK_API_KEY, or for Groq-only built-in tools (browser_search)."""
    settings = get_settings()
    spec = (settings.LLM_FALLBACK or "").strip()
    if provider != "groq" or not spec:
        return None
    if any(t.get("type") != "function" for t in tools or []):
        return None
    fb_provider, _, fb_model = spec.partition(":")
    if fb_provider not in PROVIDERS or not fb_model or fb_provider == provider:
        return None
    if not getattr(settings, PROVIDERS[fb_provider]["api_key_field"], None):
        return None
    return fb_provider, fb_model


def _unavailable(exc: Exception) -> bool:
    """Groq couldn't answer at all (not a bad request): every key limited, down, or 5xx."""
    return isinstance(
        exc,
        KeysExhausted
        | openai.RateLimitError
        | openai.APIConnectionError
        | openai.InternalServerError,
    )


def _rejects_reasoning(exc: Exception) -> bool:
    """A 400 about the reasoning parameter ("does not support parameter reasoningEffort")."""
    text = str(exc).lower()
    return isinstance(exc, openai.BadRequestError) and "reasoning" in text and "support" in text


# Output cap per role (a caller's own max_tokens wins). Groq's free tier gives the vision model
# only 1000 output tokens/min; uncapped, the second "what is this?" within a minute got a 429
# (seen live 2026-09-25: "limit 1000, requested 1707"). Its JSON answers are ~10-60 tokens, so
# 400 leaves room for a box and a part_query while two calls still fit in a minute.
ROLE_MAX_TOKENS = {"vision": 400}


class Source(pydantic.BaseModel):
    title: str
    url: str
    content: str


class SearchResult(pydantic.BaseModel):
    text: str
    sources: list[Source]


def resolve_role(role: str) -> tuple[str, str]:
    """(provider, model) for `role`: `LLM_<ROLE>=provider:model` override, else ROLE_DEFAULTS."""
    override = getattr(get_settings(), f"LLM_{role.upper()}", None)
    spec = override or ROLE_DEFAULTS.get(role)
    if not spec:
        raise ValueError(f"unknown llm role: {role!r}")
    provider, _, model = spec.partition(":")
    if provider not in PROVIDERS or not model:
        raise ValueError(f"bad llm role spec {spec!r} for role {role!r}, want 'provider:model'")
    return provider, model


# Groq's limits are per key (each key is its own organisation) and per model -- the vision model's
# 200K tokens/day runs out first -- so each model rotates through GROQ_API_KEY, GROQ_API_KEY_2, ...
# on its own: a key capped for qwen still serves gpt-oss.
_GROQ_POOLS: dict[str, KeyPool] = {}


def groq_pool(model: str) -> KeyPool:
    if model not in _GROQ_POOLS:
        _GROQ_POOLS[model] = KeyPool(
            "GROQ_API_KEY",
            is_limited=lambda exc: isinstance(exc, openai.RateLimitError),
            cooldown_s=900,  # daily caps roll off gradually; a 15 min re-check costs one 429
        )
    return _GROQ_POOLS[model]


def _client(provider: str, api_key: str | None = None, max_retries: int = 4) -> AsyncOpenAI:
    cfg = PROVIDERS[provider]
    api_key = api_key or getattr(get_settings(), cfg["api_key_field"])
    # ponytail: explicit httpx.AsyncClient() -- openai's default transport is a vendored
    # httpx2 client respx (built for httpx) never sees, so without this tests would silently
    # hit the real network. Fresh client per call, not pooled: fine at this call volume, add
    # caching/reuse if profiling ever says otherwise.
    # max_retries: Groq free tier is 8K tokens/min, 429s are routine; the SDK honours retry-after.
    return AsyncOpenAI(
        api_key=api_key,
        base_url=cfg["base_url"],
        http_client=httpx.AsyncClient(timeout=120),
        max_retries=max_retries,
    )


USD_TICKS = 1e10  # xAI `usage.cost_in_usd_ticks`: 1e10 ticks = $1


def cost_usd(usage: Any) -> float | None:
    """A chat completion's `usage` in dollars: xAI reports `cost_in_usd_ticks`, others don't."""
    ticks = getattr(usage, "cost_in_usd_ticks", None)
    return ticks / USD_TICKS if ticks is not None else None


async def chat(
    role: str,
    messages: list[dict[str, Any]],
    tools: list[dict[str, Any]] | None = None,
    **kw: Any,
) -> ChatCompletion:
    """Resolve `role` to a provider/model, call it, log, return the SDK response as-is.

    `max_retries` (default 4) is the SDK's retry count; pass 0 for slow billed calls (Grok
    reasoning), where a blind retry after a timeout pays twice. `timeout=` passes through to the
    SDK as a per-request override of the client's 120 s."""
    if tools is not None and "response_format" in kw:
        raise ValueError("never send tools + response_format together (Groq/xAI 400s)")
    if get_settings().OFFLINE:
        # Every chat() call is a live network hit -- no per-call cache to hit-or-miss (only
        # search.py's whole-result cache covers this, and it short-circuits before ever calling
        # chat()). This is the fallback boundary for other callers, e.g. vision.py.
        raise cache.OfflineMiss(f"offline: chat() unavailable (role={role!r})")
    provider, model = resolve_role(role)
    retries = kw.pop("max_retries", 4)
    if role in ROLE_MAX_TOKENS:
        kw.setdefault("max_tokens", ROLE_MAX_TOKENS[role])
    if not takes_reasoning_effort(provider, model):
        for key in _REASONING_PARAMS:
            kw.pop(key, None)
    kwargs: dict[str, Any] = dict(model=model, messages=messages, **kw)
    if tools is not None:
        kwargs["tools"] = tools

    async def call() -> ChatCompletion:
        pool = groq_pool(model) if provider == "groq" else None
        if pool and pool.keys():
            return await pool.call(
                lambda key: _client(provider, key, retries).chat.completions.create(**kwargs)
            )
        return await _client(provider, None, retries).chat.completions.create(**kwargs)

    start = time.monotonic()
    try:
        resp = await call()
    except openai.BadRequestError as exc:
        if not (_rejects_reasoning(exc) and any(k in kwargs for k in _REASONING_PARAMS)):
            raise
        logger.warning("llm %s:%s rejects reasoning parameters; retrying without", provider, model)
        _no_reasoning_models.add((provider, model))
        for key in _REASONING_PARAMS:
            kwargs.pop(key, None)
        resp = await call()
    except Exception as exc:
        fallback = fallback_for(provider, tools) if _unavailable(exc) else None
        if fallback is None:
            raise
        logger.warning(
            "llm role=%s %s:%s unavailable (%s); falling back to %s:%s",
            role,
            provider,
            model,
            type(exc).__name__,
            *fallback,
        )
        provider, model = fallback
        kwargs["model"] = model
        if not takes_reasoning_effort(provider, model):
            for key in _REASONING_PARAMS:
                kwargs.pop(key, None)
        resp = await _client(provider, None, retries).chat.completions.create(**kwargs)
    latency_ms = (time.monotonic() - start) * 1000
    usage = resp.usage
    cost = cost_usd(usage)
    logger.info(
        "llm role=%s provider=%s model=%s latency_ms=%.0f prompt_tokens=%s completion_tokens=%s"
        " cost_usd=%s",
        role,
        provider,
        model,
        latency_ms,
        getattr(usage, "prompt_tokens", None),
        getattr(usage, "completion_tokens", None),
        f"{cost:.5f}" if cost is not None else None,
    )
    return resp


XAI_RESPONSES_URL = "https://api.x.ai/v1/responses"


class ResponsesResult(pydantic.BaseModel):
    text: str  # every output_text block of the final message(s), joined
    citations: list[str]  # every source URL the server-side tools touched (xAI `citations`)
    cost_usd: float | None
    tool_usage: dict[str, int]  # usage.server_side_tool_usage_details, e.g. web_search_calls


def parse_responses(data: dict[str, Any]) -> ResponsesResult:
    """Pull text/citations/cost out of a raw `/v1/responses` body."""
    texts, urls = [], list(data.get("citations") or [])
    for item in data.get("output") or []:
        action = item.get("action") or {}  # web_search_call: search hits / opened page
        urls += [s["url"] for s in action.get("sources") or [] if s.get("url")]
        if action.get("url"):
            urls.append(action["url"])
        if item.get("type") != "message":
            continue
        for block in item.get("content") or []:
            if block.get("type") == "output_text":
                texts.append(block.get("text") or "")
                urls += [a["url"] for a in block.get("annotations") or [] if a.get("url")]
    usage = data.get("usage") or {}
    ticks = usage.get("cost_in_usd_ticks")
    return ResponsesResult(
        text="".join(texts),
        citations=list(dict.fromkeys(urls)),
        cost_usd=ticks / USD_TICKS if ticks is not None else None,
        tool_usage={
            k: v
            for k, v in (usage.get("server_side_tool_usage_details") or {}).items()
            if isinstance(v, int) and v
        },
    )


async def responses(
    model: str,
    input: str | list[dict[str, Any]],
    tools: list[dict[str, Any]] | None = None,
    timeout_s: float = 120,
    **kw: Any,
) -> ResponsesResult:
    """xAI Responses API (`POST /v1/responses`) -- the only path to xAI's server-side tools
    (`web_search`, `x_search`, ...), which chat completions doesn't have. Plain httpx, no SDK
    retries: one call = one bill. `kw` goes into the body as-is (`instructions`, `text`,
    `include`, `max_turns`, ...)."""
    if get_settings().OFFLINE:
        raise cache.OfflineMiss("offline: responses() unavailable")
    api_key = get_settings().GROK_API_KEY
    if not api_key:
        raise RuntimeError("GROK_API_KEY missing")
    body: dict[str, Any] = {"model": model, "input": input, **kw}
    if tools is not None:
        body["tools"] = tools

    start = time.monotonic()
    async with httpx.AsyncClient(timeout=timeout_s) as client:
        resp = await client.post(
            XAI_RESPONSES_URL, json=body, headers={"Authorization": f"Bearer {api_key}"}
        )
    latency_ms = (time.monotonic() - start) * 1000
    if resp.is_error:
        raise RuntimeError(f"xai responses {resp.status_code}: {resp.text[:300]}")
    result = parse_responses(resp.json())
    logger.info(
        "llm responses provider=xai model=%s latency_ms=%.0f cost_usd=%s tools=%s citations=%d",
        model,
        latency_ms,
        f"{result.cost_usd:.4f}" if result.cost_usd is not None else None,
        result.tool_usage,
        len(result.citations),
    )
    return result


def _strict_schema(schema: dict[str, Any]) -> dict[str, Any]:
    """Enforce Groq/xAI strict json_schema rules everywhere: no extra props, all props required."""
    schema = json.loads(json.dumps(schema))  # cheap deep copy

    def walk(node: Any) -> None:
        if isinstance(node, dict):
            if "properties" in node:
                node["additionalProperties"] = False
                node["required"] = list(node["properties"].keys())
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(schema)
    return schema


async def extract(
    role: str,
    messages: list[dict[str, Any]],
    model_cls: type[pydantic.BaseModel],
) -> pydantic.BaseModel:
    """Call `role`, parse+validate the JSON reply as `model_cls`. Retries once on bad JSON."""
    provider, model = resolve_role(role)
    schema = model_cls.model_json_schema()

    if provider == "deepseek" or model in NO_STRICT_SCHEMA_MODELS:
        # No json_schema/strict here -- paste the schema into the prompt and validate on receipt.
        call_messages = [
            {
                "role": "system",
                "content": f"Respond with a single JSON object matching this schema:\n"
                f"{json.dumps(schema)}",
            },
            *messages,
        ]
        response_format: dict[str, Any] = {"type": "json_object"}
    else:
        call_messages = messages
        response_format = {
            "type": "json_schema",
            "json_schema": {
                "name": model_cls.__name__,
                "strict": True,
                "schema": _strict_schema(schema),
            },
        }

    try:
        resp = await chat(role, call_messages, response_format=response_format)
    except openai.BadRequestError as exc:
        # Groq strict mode sometimes rejects its own generation ("json_validate_failed", seen
        # live 2026-09-24 on the first search of the day); a straight retry usually passes.
        if "json_validate_failed" not in str(exc):
            raise
        logger.warning("llm extract: %s json_validate_failed, retrying once", role)
        resp = await chat(role, call_messages, response_format=response_format)
    content = resp.choices[0].message.content or ""
    try:
        return model_cls.model_validate(json.loads(content))
    except (json.JSONDecodeError, pydantic.ValidationError) as exc:
        retry_messages = [
            *call_messages,
            {"role": "assistant", "content": content},
            {
                "role": "user",
                "content": f"That reply was invalid ({exc}). Reply with only valid JSON "
                f"matching the schema.",
            },
        ]
        resp = await chat(role, retry_messages, response_format=response_format)
        content = resp.choices[0].message.content or ""
        return model_cls.model_validate(json.loads(content))


# Providers with a built-in search tool whose response shape has actually been exercised live
# (Groq's `executed_tools` -> `search_results`, confirmed 2026-09-24). xAI's `web_search` tool
# is documented (tools=[{"type": "web_search"}], docs/research/llm-providers.md §5) but its
# response shape for pulling out sources was never live-verified (no xAI credits) -- guessing
# Groq's shape onto it would silently return wrong/empty sources, so it (and anything else) goes
# through the `web.search()` fallback below instead of raising and breaking `LLM_SEARCH=...`.
_VERIFIED_SEARCH_TOOL_PROVIDERS = {"groq"}


async def web_search(query: str, instructions: str | None = None) -> SearchResult:
    """Groq `browser_search` built-in tool -> SearchResult(text, sources). Any other provider
    falls back to `server.web.search()`'s condensed multi-provider chain (Exa/You/Tavily/Ollama),
    so `LLM_SEARCH=<provider>:...` stays a config-only swap instead of an unimplemented dead end.
    """
    provider, _ = resolve_role("search")
    if provider not in _VERIFIED_SEARCH_TOOL_PROVIDERS:
        logger.info(
            "llm.web_search: provider=%s has no verified search tool, using web.search()", provider
        )
        hits = await web.search(query)
        return SearchResult(
            text="", sources=[Source(title=h.title, url=h.url, content=h.text) for h in hits]
        )

    logger.info("llm.web_search: provider=%s -> browser_search tool", provider)
    tool = {"type": "browser_search"}
    messages: list[dict[str, Any]] = []
    if instructions:
        messages.append({"role": "system", "content": instructions})
    messages.append({"role": "user", "content": query})

    resp = await chat("search", messages, tools=[tool], tool_choice="required")
    message = resp.choices[0].message
    sources = [
        Source(title=r.get("title") or "", url=r.get("url") or "", content=r.get("content") or "")
        for executed in (getattr(message, "executed_tools", None) or [])
        for r in ((executed.get("search_results") or {}).get("results") or [])
    ]
    return SearchResult(text=message.content or "", sources=sources)
