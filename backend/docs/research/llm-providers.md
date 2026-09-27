# LLM provider research — Groq (primary) / DeepSeek (fallback) / xAI Grok (later swap)

Researched 2026-09-24 for `docs/AirTool implementation plan.md` §1b (parts server contract)
and §4b (`server/grok.py`: voice agent, search + spec extraction, seller recommendation).
The plan was written against xAI Grok; this doc adapts it to Groq-primary / DeepSeek-fallback,
keeping the client OpenAI-compatible so the later swap back to xAI is config-only.

All raw evidence is under `tests/fixtures/llm/`. No key values appear anywhere below or in
any fixture (checked with `grep -rlE "sk-|gsk_|xai-"`, no matches).

## Headline finding that changes the plan

**`groq/compound` and `groq/compound-mini` (the models §4b assumed for built-in web search) do
not exist any more.** They are absent from the live `GET /openai/v1/models` listing and
[GroqDocs — Model Deprecations](https://console.groq.com/docs/deprecations) confirms both were
decommissioned 2026-09-21 (three days before this research), "no replacement models were
recommended." The current equivalent is the `browser_search` built-in tool on the `gpt-oss`
models (confirmed live, see §2). Everything below reflects `browser_search`, not `compound`.

---

## 1. Groq text models

Live listing, `GET https://api.groq.com/openai/v1/models` (full response:
`tests/fixtures/llm/groq_models_list.json`):

| id | context window | max completion | `supported_features` | notes |
|---|---|---|---|---|
| `openai/gpt-oss-120b` | 131,072 | 65,536 | tools, json_mode, structured_outputs, reasoning | **strongest general model**, ~500 tps per docs |
| `openai/gpt-oss-20b` | 131,072 | 65,536 | tools, json_mode, structured_outputs, reasoning | smaller/faster sibling |
| `openai/gpt-oss-safeguard-20b` | 131,072 | 65,536 | tools, json_mode, structured_outputs, reasoning | moderation-tuned, not general purpose |
| `qwen/qwen3.8-27b` | 131,072 | 16,384 | tools, json_mode, reasoning (**no** structured_outputs) | vision input, but can't do json_schema |
| `whisper-large-v3`, `whisper-large-v3-turbo` | 448 | 448 | — | STT, see §3 |
| `canopylabs/orpheus-v1-english`, `orpheus-arabic-saudi` | 4,000 | 50,000 | — | TTS, see §3 |
| `allam-2-7b`, `meta-llama/llama-prompt-guard-2-{22m,86m}` | small | small | json_mode only / none | not relevant here |

`llama-3.1-8b-instant` and `llama-3.3-70b-versatile` (the models the wider Groq docs still
reference in comparison tables) are **gone** — retired 2026-08-16 per the deprecations page,
migrated to `openai/gpt-oss-20b` / `openai/gpt-oss-120b`. The live `/models` call agrees: neither
id appears in the listing.

**Answer to Q1:** `openai/gpt-oss-120b` is the strongest model with both (a) OpenAI-style
function/tool calling and (b) `response_format: {"type":"json_schema", "strict": true}` — but
**not at the same time** (see live-call evidence below, this is the load-bearing quirk).

- Context window: 131,072 tokens, 65,536 max completion tokens.
- Reasoning params ([GroqDocs — Reasoning](https://console.groq.com/docs/reasoning)):
  `reasoning_effort`: `low | medium | high` (gpt-oss family); `reasoning_format`: `parsed | raw | hidden`,
  default `raw` alone but forced to `parsed`/`hidden` when JSON mode or tools are active.
  `reasoning_effort` and `reasoning_format` cannot both be set for gpt-oss models.
- Rate limits, **free tier** ([GroqDocs — Rate Limits](https://console.groq.com/docs/rate-limits)):
  gpt-oss-120b/20b: 30 RPM / 1K RPD / 8K TPM / 200K TPD. Dev tier numbers are account-specific
  ("higher limits available for select workloads"), not published as a flat table — doc directs
  you to the account limits page.

### Live evidence: tools + json_schema together (Groq gpt-oss-120b)

Call 1 — both `tools` and `response_format: json_schema` on the same request
(`tests/fixtures/llm/groq_tools_plus_json_schema_error.json`):

```json
{
  "label": "tools_plus_json_schema_single_call",
  "status": "error",
  "error": "Error code: 400 - {'error': {'message': 'json mode cannot be combined with tool/function calling', 'type': 'invalid_request_error', 'param': 'response_format'}}"
}
```

Call 2 — `tools` alone, `tool_choice: "required"` (`tests/fixtures/llm/groq_tool_calling_alone.json`):
works, returns `finish_reason: "tool_calls"`, `message.tool_calls[0].function` = `{"name":
"get_gutter_measurement", "arguments": "{\"location\":\"rear fascia\"}"}`, plus a `reasoning` field
on the message (gpt-oss models always emit a chain-of-thought `reasoning` string alongside content).

Call 3 — `response_format: json_schema` alone (`tests/fixtures/llm/groq_json_schema_alone.json`):
works, `finish_reason: "stop"`, `message.content` = `{"name":"5 inch K-style hidden hanger
bracket","needs_measurement_tool":false,"width_mm":127}` — valid against the schema.

**Conclusion:** unlike the plan's assumption for xAI Grok 4 ("Structured outputs and server-side
tools are allowed in one call for the Grok 4 family" — plan §4b.1), **Groq gpt-oss-120b requires
two calls**: one to search/call tools (plain text or tool result), one to extract structured JSON
from that result. This is the single most important design change from the original plan.

---

## 2. Groq web search — `browser_search` (replaces `compound`/`compound-mini`)

[GroqDocs — Browser Search](https://console.groq.com/docs/browser-search):
- Supported models: `openai/gpt-oss-20b`, `openai/gpt-oss-120b`, `openai/gpt-oss-safeguard-20b`.
- Invoke with `tools: [{"type": "browser_search"}]`, `tool_choice: "required"` to force it.
  Runs server-side, no client-side execution needed.
- Powered by Exa under the hood (confirmed live — see below).
- **Incompatible with `response_format: json_schema`** (matches the `json mode cannot be
  combined with tool/function calling` error from §1 — `browser_search` is a tool, so the same
  restriction applies). Compatibility with *custom* function tools alongside `browser_search` is
  not documented either way; treat as unsupported until tested.
- Not available on HIPAA-covered or sovereign/regional endpoints.
- With reasoning models, docs recommend `reasoning_effort: "low"` to bound search session length/cost.

### Live evidence (`tests/fixtures/llm/groq_browser_search.json`)

Request: `openai/gpt-oss-120b`, `tools: [{"type": "browser_search"}]`, `tool_choice: "required"`,
prompt asking for a gutter-hanger price + source URL. Response shape:

```json
{
  "message": {
    "content": "**Product:** Amerimax Home Products 5 in. White Vinyl K-Style Hidden Gutter Hanger (M0722B)  \n**Price:** $2.86 USD …\n**Source URL:** https://www.homedepot.com/p/Amerimax-Home-Products-5-in-White-Vinyl-K-Style-Hidden-Gutter-Hanger-M0722B/313258081",
    "executed_tools": [
      {
        "name": "browser.search",
        "type": "browser_search",
        "arguments": "{\"query\": \"5 inch K-style hidden hanger gutter bracket price\", \"topn\": 10, \"source\": \"news\"}",
        "output": "L0: \nL1: URL: https://exa.ai/search?q=... \n# Search Results\n* [0†Amerimax Hidden Gutter Hanger …†www.dunnlumber.com]\n* [1†5 in. White Vinyl K-Style Hidden Gutter Hanger†www.homedepot.com]\n...",
        "search_results": {
          "results": [
            {"title": "Amerimax Hidden Gutter Hanger for K-Style Gutters, 5.17\", Aluminum", "url": "https://www.dunnlumber.com/...", "content": "", "score": 0},
            {"title": "5 in. White Vinyl K-Style Hidden Gutter Hanger", "url": "https://www.homedepot.com/p/...", "content": "", "score": 0}
          ]
        }
      }
    ]
  }
}
```

So: `message.executed_tools[]` carries `name`, `type`, `arguments` (the query the model issued),
`output` (raw citation-annotated text, `【N†...】` markers), and `search_results.results[]` with
`title`/`url`/`content`/`score` per hit (`content` was empty in this sample — the model reads the
full page separately and cites it inline in `output`/final `content`, it doesn't hydrate
`search_results[].content`). Final answer text embeds citation markers referencing the opened
page.

**No `groq/browser_search` "standalone" endpoint exists** — it's a tool flag on chat completions,
not a separate model to call directly.

---

## 3. Groq speech (STT/TTS)

[GroqDocs — Speech to Text](https://console.groq.com/docs/speech-to-text) /
[Text to Speech](https://console.groq.com/docs/text-to-speech), cross-checked against the live
model listing:

| Role | Model id(s) | Endpoint | Formats / limits |
|---|---|---|---|
| STT | `whisper-large-v3` ($0.111/hr, multilingual transcribe+translate), `whisper-large-v3-turbo` ($0.04/hr, transcribe only, faster) | `POST /openai/v1/audio/transcriptions`, `POST /openai/v1/audio/translations` | Accepts flac/mp3/mp4/mpeg/mpga/m4a/ogg/wav/webm, upload or URL. Max 25 MB (free) / 100 MB (dev tier). Preprocessed to 16kHz mono. Min duration 0.01s, billed minimum 10s. Multi-track audio: only first track transcribed. Response formats: json, verbose_json (timestamps), text. |
| TTS | `canopylabs/orpheus-v1-english` (English, vocal-direction controls), `canopylabs/orpheus-arabic-saudi` (Saudi Arabic) | `POST /openai/v1/audio/speech` | Params: model, input text, voice, response_format. Default format WAV; MP3/PCM also standard options. Voices include `troy`, `hannah`, `austin`, others (full list in dedicated Orpheus doc page, not in this excerpt — flagged below). |

Note: `playai-tts` (the model the plan-writer likely had in mind from older Groq docs) is **not**
in the current model listing or TTS docs — Orpheus (Canopy Labs) is the current TTS family.

**Concern (1 of 3 running):** the TTS voice list beyond troy/hannah/austin wasn't returned in the
fetched docs excerpt — minor, doesn't block a config choice (voices are a runtime string, easy to
swap later), noting rather than blocking on it.

---

## 4. DeepSeek

Live listing, `GET https://api.deepseek.com/models` (full response:
`tests/fixtures/llm/deepseek_models_list.json`):

| id | name | context | max output | notes |
|---|---|---|---|---|
| `deepseek-flash` | DeepSeek-V4.1-Flash | 1,048,576 | 393,216 | text+image input; `effort` param: `low\|high\|max`, default `high` |
| `deepseek-v4-pro` | DeepSeek-V4-Pro | 1,048,576 | 393,216 | text only; same `effort` levels |

Note: the old `deepseek-chat` / `deepseek-reasoner` ids from prior DeepSeek docs are gone —
`deepseek-flash` and `deepseek-v4-pro` are current.

Base URL: `https://api.deepseek.com` (OpenAI-compatible, confirmed via
[DeepSeek docs — JSON Mode](https://api-docs.deepseek.com/guides/json_mode)).

Tool calling + JSON mode
([DeepSeek docs — Create Chat Completion](https://api-docs.deepseek.com/api/create-chat-completion)):
- `tools` / `tool_choice` params supported ("Currently, only functions are supported as a tool"),
  values `none | auto | required | {function}`.
- `response_format: {"type": "json_object"}` supported — **`json_object` only, no
  `json_schema`/`strict` mode documented.** DeepSeek enforces "valid JSON" via prompting
  ("you must also instruct the model to produce JSON yourself"), not schema-conformance
  enforcement. Docs also warn "the API may occasionally return empty content" in JSON mode.
- Docs describe `tools` and `response_format` as separate, independently-documented params; both
  appear in the same endpoint's parameter list (`thinking`/`reasoning_effort`, `tools`,
  `tool_choice`, `response_format` all accepted together per the endpoint reference) — unlike
  Groq, nothing in the docs says they're mutually exclusive. **Not live-verified** (budget used
  elsewhere; DeepSeek is a rare fallback, calling it once wasn't justified for this).

**Quirk to design around:** because DeepSeek has no `json_schema` strict mode, spec extraction on
the DeepSeek fallback path needs a Pydantic-validate-and-retry loop (ask for JSON matching a
schema described in the prompt, `json.loads` + `model_validate`, re-prompt on failure) rather than
relying on schema enforcement the way Groq's `strict: true` does.

---

## 5. xAI Grok (docs only, no inference — key has no credits)

Base URL: `https://api.x.ai/v1`, OpenAI-compatible (`docs.x.ai/docs/overview`).

Models (`docs.x.ai/docs/models`): `grok-4.7`, `grok-4.6`, `grok-4.5`, `grok-4.3` (general
purpose, non-reasoning-labeled, 500k–1M context), and the `grok-4.20` line: `grok-4.20-0309-reasoning`
(reasoning-enabled), `grok-4.20-0309-non-reasoning`, `grok-4.20-multi-agent-0309` — all 1M
context. (The plan's `grok-4.20-non-reasoning` for cheap seller-recommendation and `grok-4.7` for
search+extraction map onto real current ids, just with a `-0309` date suffix on the 4.20 pair —
double check exact suffix at swap time, doc UI truncates it in places.)

**Live Search is deprecated** — removed 2026-01-12, returns `410 Gone`
([X post from xAI developer relations](https://x.com/BenjaminDEKR/status/1996738390583054723),
corroborated by multiple framework bug trackers e.g.
[langchain-ai/langchain#33961](https://github.com/langchain-ai/langchain/issues/33961)). This
predates the plan's `web_search()` tool-object call style, which is actually already the
**replacement** — the plan's `tools=[web_search()]` pattern is correct; what's deprecated is the
older `search_parameters` field some code still uses. Migrate via **Agent Tools API**:
`tools: [{"type": "web_search"}]` in the request body — same shape as Groq's `browser_search`
call, which keeps the eventual swap thin.

Structured outputs (`docs.x.ai/developers/model-capabilities/text/structured-outputs`):
`response_format: {"type": "json_schema", "json_schema": {...}, "strict": true}`. `strict` is
accepted but per third-party integration notes not structurally guaranteed — validate on receipt
regardless. Every field (including optional ones) needs a `description` under strict mode or the
request 400s. **Not compatible with `stream: true`.**

Realtime voice: `wss://api.x.ai/v1/realtime` (OpenAI Realtime-API-compatible transport). Ephemeral
token endpoint: `POST /v1/realtime/client_secrets`, body `{"expires_after": {"seconds": <=3600,
default 600}}`. **No bound session config:** xAI's docs say the endpoint "does not support
`session` or `expires_after.anchor`" (corrected 2026-09-26, G2), so a client using the token must
send its own `session.update`. Response: `{"value": "xai-client-secret....", "expires_at":
<unix_ts>}` (`expires_at` is an integer, live-verified by G2). The headset doesn't use tokens:
`WS /voice/realtime` (server/realtime.py) relays with the server key. Use `value` either as `Authorization: Bearer` or in the
`sec-websocket-protocol` header with the `xai-client-secret.` prefix. Turn detection:
server VAD (default, emits `input_audio_buffer.speech_started/stopped`) or manual
(`turn_detection: null`, client calls `input_audio_buffer.commit`) — this resolves the plan's
"unverified" note in §4b.1: turning off turn_detection **is** supported (manual mode), matches
plan's push-to-talk intent directly, no VAD-threshold fallback needed.

This is enough surface (base_url, model ids, tool-call shape, `response_format` shape, realtime
token shape) to make the future Groq→xAI swap a config change: same OpenAI SDK, same
`tools:[{"type":"web_search"}]` call shape, same `response_format` json_schema shape, only
`base_url`/`api_key`/model-id strings and the realtime WS URL change.

---

## 6. Recommendation: one OpenAI-compatible client, provider/model per role

Single `openai.OpenAI(api_key=..., base_url=...)` client, swapped per provider by env config:

| Env var | Groq (primary) | DeepSeek (fallback) | xAI (future swap) |
|---|---|---|---|
| `*_BASE_URL` | `https://api.groq.com/openai/v1` | `https://api.deepseek.com` | `https://api.x.ai/v1` |
| `*_API_KEY` | `GROQ_API_KEY` | `DEEPSEEK_API_KEY` | `GROK_API_KEY` |

| Role | Model | Why |
|---|---|---|
| (a) agent/tool-calling (voice function router, `find_part` etc.) | Groq `openai/gpt-oss-120b`, plain `tools` call (no `response_format`) | Strongest model with real tool-calling; confirmed live (Call 2) |
| (b) spec extraction → JSON schema | Groq `openai/gpt-oss-120b`, second call, `response_format: json_schema, strict: true`, **no tools attached** | Confirmed live (Call 3) it works standalone; combining with tools 400s (Call 1) — must be its own call, fed the tool/search output as context |
| (c) web search | Groq `openai/gpt-oss-120b` + `tools:[{"type":"browser_search"}]`, `tool_choice:"required"` | Confirmed live (browser_search call); replaces the plan's now-dead `compound` |
| (d) cheap seller recommendation | Groq `openai/gpt-oss-20b`, plain text or light JSON, no tools | Cheaper/faster sibling, matches plan's "one cheap call, no tools" role |
| (e) STT | Groq `whisper-large-v3-turbo` | Fast/cheap; fall back to `whisper-large-v3` if translation or higher accuracy needed |
| (f) TTS | Groq `canopylabs/orpheus-v1-english` | Only English TTS option currently listed |
| Hard-reasoning fallback (rare) | DeepSeek `deepseek-v4-pro` (`effort: "max"`) for (a)/(b) when Groq is down or a query needs deeper reasoning than gpt-oss gives | Per brief: "rare fallback for hard reasoning"; use `deepseek-flash` (cheaper) first if fallback is just capacity, `-v4-pro` for genuinely harder reasoning |

**Quirks to encode in the client wrapper:**
1. **Never send `tools` and `response_format:{"type":"json_schema"}` in the same Groq request** —
   400s (confirmed live). Pipeline must be: call 1 (search/tool) → call 2 (extract JSON from call
   1's output, no tools attached). This is a real deviation from the plan's single xAI call.
2. `browser_search` is itself a tool, so rule 1 applies to it too — never pair it with
   `response_format`.
3. DeepSeek has no `json_schema`/`strict` — the JSON-extraction call needs a
   validate-and-retry wrapper on the DeepSeek path (Pydantic `model_validate` + one re-prompt on
   parse failure), not needed on Groq/xAI where `strict` does more of that work (though xAI's own
   docs say `strict` isn't structurally guaranteed either — validate everywhere regardless, this
   was already the plan's own guidance in §4b.1: "guard every candidate").
4. gpt-oss models always emit a `reasoning` field alongside `content`/`tool_calls` — strip it
   before logging/showing to the judge-facing UI, keep it only for debug.
5. Realtime voice manual turn-detection is real (`turn_detection: null` + `input_audio_buffer.commit`)
   — resolves the plan's own flagged uncertainty; safe to hard-code push-to-talk without a VAD
   fallback branch.

---

## Files consulted (docs, no inference cost)

- https://console.groq.com/docs/deprecations
- https://console.groq.com/docs/browser-search
- https://console.groq.com/docs/tool-use/built-in-tools/web-search (stale/empty for this topic, superseded by browser-search page)
- https://console.groq.com/docs/models
- https://console.groq.com/docs/rate-limits
- https://console.groq.com/docs/text-to-speech
- https://console.groq.com/docs/speech-to-text
- https://console.groq.com/docs/reasoning
- https://api-docs.deepseek.com/quick_start/pricing
- https://api-docs.deepseek.com/guides/function_calling
- https://api-docs.deepseek.com/guides/json_mode
- https://api-docs.deepseek.com/api/create-chat-completion
- https://docs.x.ai/docs/overview
- https://docs.x.ai/docs/models
- https://docs.x.ai/developers/rest-api-reference/inference/voice
- https://docs.x.ai/developers/model-capabilities/text/structured-outputs (via search summary, not directly fetched — see concern below)
- https://x.com/BenjaminDEKR/status/1996738390583054723 (Live Search deprecation date)
- https://github.com/langchain-ai/langchain/issues/33961 (Live Search deprecation corroboration)

**Concern (2 of 3):** the xAI structured-outputs page
(`docs.x.ai/developers/model-capabilities/text/structured-outputs`) was read via a WebSearch
summary of third-party pages (theneuralbase.com, hexdocs.pm), not a direct WebFetch of the xAI
doc itself — WebFetch wasn't attempted on that exact URL. The `strict`-not-guaranteed claim and
the `stream=True` incompatibility should be re-checked against the primary doc before the actual
swap, since xAI inference can't be tested now (no credits).

**Concern (3 of 3):** Groq dev-tier (paid) rate limits weren't in the fetched rate-limits page —
only free-tier numbers and a pointer to the per-account limits page in the console. Not blocking
(free-tier numbers are the conservative case to design against), but exact dev-tier throughput
isn't documented publicly as a table.

Three concerns reached — surfacing here per threshold; none blocks the recommendation above, all
three are "verify closer to build time" items, not open problems with today's answer.
