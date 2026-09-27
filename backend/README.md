# airtools-drone-backend

P4 parts server backend for AirTool. Takes a voice or text query from a Unity/Quest headset,
finds real parts on the web at true size with verified dimensions, sellers, prices, delivery
estimates, and a 3D GLB asset, runs a fit check against the headset's tape measurement, and
supports a Cybersource sandbox checkout. The Unity/Quest app is the client; see
[`docs/api.md`](docs/api.md) for the endpoint contract.

## Quickstart

```bash
uv sync
cp .env.example .env   # fill in keys — see "API keys" below
uv run uvicorn server.app:app --host 0.0.0.0 --port 8000
```

Open the debug console at **http://localhost:8000/debug** to type commands and watch the
server's responses without a headset.

## API keys

| Variable | Status | Purpose |
|---|---|---|
| `GROQ_API_KEY` | **Required** | LLM inference, STT (Whisper), TTS (Orpheus) |
| `SERP_API_KEY` | Strongly recommended | Google Shopping discovery, Home Depot dims, seller stores |
| `EXA_API_KEY` | Optional | Web search fallback (first in chain) |
| `YOUDCOM_API_KEY` | Optional | Web search fallback (second in chain) |
| `TAVILY_API_KEY` | Optional | Web search fallback (third in chain) |
| `OLLAMA_WEB_API_KEY` | Optional | Web search fallback (fourth in chain) |
| `DEEPSEEK_API_KEY` | Optional | `hard` LLM role default (`deepseek-v4-pro`) |
| `GROK_API_KEY` | Optional | xAI provider; set `LLM_AGENT=xai:<model>` etc. to swap; the offline `warm --grok-moulded` job; every Grok feature (voice, Imagine, survey, manuals, safety, installers, report, planner, coach) |
| `CYBERSOURCE_MERCHANT_ID` / `KEY_ID` / `SECRET_KEY` | Optional | Sandbox checkout; offline receipt used if absent |
| `HF_TOKEN` | Optional | Raises anonymous GPU quota on the Hunyuan3D-2.1 HF Space (moulded-part fallback) |

**Fallback keys:** `SERP_API_KEY_2`, `SERP_API_KEY_3`, ..., `HF_TOKEN_2`, ... and `GROQ_API_KEY_2`, ... are
tried in number order when the previous key is rate limited (SerpApi `429`, HF "ZeroGPU quota",
Groq `429`); Groq keys rotate per model, since each key has its own daily cap per model
(`qwen/qwen3.8-27b`: 200K tokens/day on the free tier) and are retried after 15 min. A
limited SerpApi or HF key is retried after an hour, HF falls back to anonymous last, and only when every key
is limited does the call fail. Keys are logged by position (`SERP_API_KEY #2`), never by value.
To give another service the same rotation, wrap its call in a `server.keys.KeyPool`.

Without `SERP_API_KEY` the pipeline can still run via the web search fallback chain
(`EXA → You.com → Tavily → Ollama`), but Home Depot dims and verified seller stores
are unavailable.

## Demo prep (offline mode)

Pre-warm the disk cache so the scripted demo works on a dead uplink:

```bash
uv run python -m server.warm          # uses built-in demo queries
uv run python -m server.warm --queries-file demo.json   # custom queries
uv run python -m server.warm --retry-proxies          # re-mesh parts stuck on a proxy box
uv run python -m server.warm --grok-moulded           # Grok OpenSCAD models for moulded parts
uv run python -m server.warm --catalog                # every site's catalog: place, searches, models
```

`--catalog` (`[--sites a,b] [--no-models]`) fills `GET /catalog` for every served site: one Grok
call per site names the place (~$0.002, cached per revision), every category with fewer than 3
known parts is searched (lite: no store lookups), the first 3 parts per category get models and
every listed part its photo. It prints each site's categories and what it spent (SerpApi calls,
LLM calls and dollars). Re-running it costs nothing.

`--grok-moulded` spends xAI credit (`grok-4.7`, 1–3 calls and ~5 min per part) on the parts the
asset call tagged moulded or organic (clips, vinyl hangers), and stores `data/parts/<id>/scad.glb`
so every later resolve reuses it (tier `scad`). It needs OpenSCAD with the manifold backend: on
`PATH`, or the nightly AppImage at `work/assets-llm/tools/openscad.AppImage`; without one it
logs and skips. It never runs on the request path.

Then run the server in cache-only mode:

```bash
OFFLINE=true uv run uvicorn server.app:app --host 0.0.0.0 --port 8000
```

`warm` runs `find_parts` + `resolve_asset` + TTS pre-generation sequentially and prints a
results table. Re-running after a successful warm is a no-op (every step checks its cache
first).

Offline, literal commands still work: "find a gutter hanger", "select the first", "cheapest
first", "every 60 cm", "equip tape". A live measurement that differs from the warmed one reuses
the cached answer for the same query with fit recomputed. Anything else gets a spoken
"offline" reply. `/scene/ask` and `/voice/command` STT return `503` offline.

## Switching LLM providers

Each role is independently overridable via environment variable:

| Variable | Default | Role |
|---|---|---|
| `LLM_AGENT` | `groq:openai/gpt-oss-120b` | Agent tool loop |
| `LLM_EXTRACT` | `groq:openai/gpt-oss-120b` | Part extraction from search text |
| `LLM_SEARCH` | `groq:openai/gpt-oss-120b` | Web search / browser_search |
| `LLM_CHEAP` | `groq:openai/gpt-oss-20b` | Dims extraction from page text |
| `LLM_HARD` | `deepseek:deepseek-v4-pro` | Fallback hard reasoning |
| `LLM_VISION` | `groq:qwen/qwen3.8-27b` | Headset frame description |
| `LLM_ASSET` | `groq:qwen/qwen3.8-27b` | Part 3D asset: template, params, colours, photo face (one vision call per part; `xai:grok-4.20-0309-non-reasoning` works too) |
| `LLM_ASSET_SCAD` | `xai:grok-4.7` | Offline OpenSCAD models for moulded parts (`warm --grok-moulded` only) |
| `LLM_ASSET_RETRY` | `xai:grok-4.20-0309-non-reasoning` | A poor `asset` answer (none, or a known shape such as a window answered with another template) is asked once more here; `""` = off. `asset.made_by` in part.json says who made each model |
| `LLM_EXTRACT_RETRY` | `xai:grok-4.20-0309-non-reasoning` | The search's page-text dims extraction (`cheap`) found no complete size: one more try here; `""` = off |
| `LLM_SURVEY` | `xai:grok-4.20-0309-non-reasoning` | Drone condition survey (`POST /scene/survey`): one vision call over up to 6 capture frames |
| `LLM_SURVEY_CAREFUL` | `xai:grok-4.7` | The same survey with `model: "careful"` (low reasoning effort) |
| `LLM_REPORT` | `xai:grok-4.3` | Site-walk report summary (`GET /report/{session_id}`); falls back to the `agent` role if xAI fails |
| `LLM_PLAN` | `xai:grok-4.20-0309-non-reasoning` | "Where does it go?" planner pick (`POST /scene/plan`): one short strict-schema call, no geometry |
| `LLM_COACH` | `xai:grok-4.20-0309-non-reasoning` | Capture coach line (`GET /scenes/{site}/coverage`): two spoken sentences from the computed coverage |
| `LLM_LABELS` | `xai:grok-4.20-0309-non-reasoning` | Live labels (`POST /scene/labels`, `warm --labels <site>`): one streamed vision call per frame |
| `LLM_PACKET` | `xai:grok-4.20-0309-non-reasoning` | Job packet summaries (`POST /packet/{session_id}`); xai only (it goes through `responses()`) |
| `LLM_BOOTH` | `xai:grok-4.20-0309-non-reasoning` | Booth wall caption (`POST /booth/{session_id}/share`): one number-checked line per shared design |
| `LLM_CATALOG` | `xai:grok-4.20-0309-non-reasoning` | Catalog (`GET /catalog`): what kind of place a scan is, from 2 thumbnails + its parts and labels, plus up to 2 site-specific categories; once per site revision. `CATALOG_SEARCH_N` (4): candidates per background category search |
| `LLM_QUOTE` | `xai:grok-4.20-0309-non-reasoning` | Paper-quote reader (`POST /quote/check`): one strict-schema vision call per quote image |
| `LLM_COACH_VISION` | `xai:grok-4.20-0309-non-reasoning` | Install coach step checks (`POST /coach/{id}/check`): yes / no / cant_see answers on one headset frame, ~$0.001 |

Other Grok feature settings: `XAI_VOICE` (`rex`, realtime voice and report narration),
`XAI_REALTIME_MODEL` (`grok-voice-latest`), `REALTIME_MAX_S` (`600`, one voice session's cap) and
`DEFAULT_LOCATION` (`Atlanta, GA`, installer search when no place is named). The booth wall's
optional X post needs `X_CLIENT_ID`, `X_CLIENT_SECRET` and `X_REFRESH_TOKEN`. Every Grok feature
needs `GROK_API_KEY`; `docs/integration-grok-features.md` lists which feature uses what.

Format: `provider:model`. Providers: `groq`, `deepseek`, `xai`. Exact model names and quirk
handling live in `server/llm.py` (`ROLE_DEFAULTS`, `PROVIDERS`). `reasoning_effort` is sent only
to models that take it (both grok-4.20 models answer 400 on it; grok-4.3/4.5/4.6/4.7 take it), so
any role can run on any Grok model.

`LLM_FALLBACK` (default `xai:grok-4.20-0309-non-reasoning`, needs `GROK_API_KEY`; `""` turns it
off): when a Groq call can't be answered -- every key rate limited, Groq unreachable, a 5xx -- the
same call goes there instead (vision, strict json_schema and function tools included; Groq's
built-in `browser_search` never falls back). Speech-to-text does the same: Groq Whisper, one retry,
then xAI `grok-transcribe` over a short realtime session (xAI has no REST transcription endpoint).

Agent model (measured 2026-09-26, 12 prompts x 2 on the kitchen, the LLM path only):
`xai:grok-4.20-0309-non-reasoning` picked the right tool 24/24 with a 0.82 s median first call
(p90 1.1 s, max 1.2 s); `xai:grok-4.7` 22/24, 1.5 s median, p90 5.7 s, max 11 s (twice it answered
in words instead of calling the tool); `xai:grok-4.5` 22/24, 1.2 s, one 57 s outlier;
`groq:openai/gpt-oss-120b` 15/24 (the free tier ran out mid-run). For the headset:
`LLM_AGENT=xai:grok-4.20-0309-non-reasoning`.

To move everything to Grok:

```bash
LLM_AGENT=xai:grok-4.7 LLM_EXTRACT=xai:grok-4.7 LLM_SEARCH=xai:grok-4.7 \
LLM_CHEAP=xai:grok-4.7 uv run uvicorn server.app:app ...
```

## Tests

```bash
uv run pytest              # offline unit tests, no network calls
uv run pytest -m live      # network tests — costs real API calls
```

## Module map

| File | Job |
|---|---|
| `server/app.py` | FastAPI routes |
| `server/agent.py` | Tool-calling command agent; returns client-side `actions` for Unity |
| `server/search.py` | `find_parts` pipeline: discover → extract → enrich → sellers → fit |
| `server/catalog.py` | `GET /catalog` (the place, its categories, background fill) and `GET /catalog/search`; tables in `catalog_tables.py`, the in-memory index in `catalog_index.py` |
| `server/sellers.py` | SerpApi Google Shopping, Home Depot product dims + ETA, seller ranking |
| `server/web.py` | Exa / You.com / Tavily / Ollama search + page fetch and condensing |
| `server/assets.py` | 3D asset tiers: CAD URL → LLM template + photo → cached Grok OpenSCAD / Hunyuan3D-2.1 (moulded parts) → photo decal box |
| `server/meshgen.py` | The `llm` tier: one vision call → template → photo projection; decal box; offline Grok OpenSCAD loop |
| `server/part_templates.py` | Parametric part templates (box appliance, tank, fridge, sink, bent strip, ...) at exact size |
| `server/scad_lib.scad` | Frame-safe OpenSCAD helper library the Grok job writes against |
| `server/vision.py` | Groq vision: headset frame description + part identification |
| `server/voice.py` | Groq Whisper STT, Orpheus TTS, edge-tts fallback, xAI token relay |
| `server/checkout.py` | Cybersource sandbox or offline receipt |
| `server/jobs.py` | In-memory job table + asyncio tasks, results persisted via cache |
| `server/llm.py` | OpenAI-compatible client, provider/model per role, quirk encoding |
| `server/cache.py` | Disk JSON cache; every paid network call goes through it |
| `server/warm.py` | Demo warm-up CLI: pre-fills cache + pre-generates TTS |
| `server/models.py` | Shared Pydantic types: `Part`, `Seller`, `Dims`, `Measurement`, `Job` |
| `server/config.py` | Settings from `.env`; LLM role overrides |

## Operational notes

- **Groq free tier** (as of 2026-09-24): ~8K tokens/min, ~1K requests/day on `gpt-oss-120b`.
  `browser_search` costs ~50K prompt tokens per call and is used only as a last-resort discovery
  fallback. Normal searches make exactly one `extract` call with a ~4K-token prompt.
- **SerpApi free tier**: 250 searches/month. Sellers are expanded lazily (only on
  `show_sellers`) to avoid burning quota during initial search.
- **Groq Orpheus TTS**: requires accepting terms in the Groq console
  (console.groq.com → Models). If the model is unavailable, `edge-tts` (`en-GB-RyanNeural` by
  default) is used as a silent fallback.
- **3D assets** (bench: `docs/research/p4-llm-assets-bench.md`): by default one `LLM_ASSET` call
  per part (~4.3K tokens in, 270–430 out, 2 s when Groq is idle, longer when its ~1K output
  tokens/min free-tier limit queues it) picks a parametric template, which is built at exact size
  and gets the product photo projected onto the face it shows: 30–100 KB, about 1 s of CPU, no
  Space quota. Groq's 200K tokens/day on `qwen/qwen3.8-27b` covers ~40 assets per key per day.
  Answers are cached, so a warmed part costs no call. Parts the call tags moulded/organic use a
  cached Grok OpenSCAD model (`--grok-moulded`) or else the Hunyuan3D-2.1 HF Space
  (`tencent/Hunyuan3D-2.1`, ~15–20 s, kept upright, photo-textured); meshes with >60%
  uniform-scale residual are rejected. The anonymous ZeroGPU quota is per-IP and runs out after
  a few meshes a day; set `HF_TOKEN` (free HF account, ~12 meshes/day; PRO 40 min/day). With no
  LLM answer and no mesh the part gets the photo decal box (exact size, photo on one face), or a
  plain coloured box without a photo. `asset.tier` says which tier made each GLB. Re-run the
  tiers for decal/plain boxes with `--retry-proxies`.
- **Data directory**: all cached results and part assets land under `DATA_DIR` (`./data` by
  default). Back it up before a demo.
- **Scene packages**: `pipeline/` writes finished scene packages (mesh, collision, cameras,
  thumbs — plan §1/§1a) under `SCENE_DIR` (`./scene` by default); the server serves them
  read-only at `GET /scenes` (list) and `GET /scenes/<site>/<file>` (files), see
  [`docs/api.md`](docs/api.md). `scene.json` is the only file the app polls — it's served with
  `Cache-Control: no-cache` + `ETag` so a conditional `GET` gets a `304`; revision-named files
  (`*.r<rev>.*`) are immutable once published and cached long-term.
