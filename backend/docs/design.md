# Parts server design (P4) — as built

Living doc. Deviations from the original plan (§4b) are listed at the bottom.

## Modules (`server/`)

| File | Job |
|---|---|
| `config.py` | Settings from `.env`; per-role LLM override `LLM_<ROLE>=provider:model` |
| `models.py` | Shared types: `Part`, `Seller`, `Dims`, `Measurement`, `Job`, `SearchRequest` |
| `llm.py` | OpenAI-compatible client for groq / deepseek / xai. `chat`, `extract` (Pydantic structured output), `web_search` (Groq `browser_search`). Quirks: no tools+json_schema in one call; DeepSeek and qwen3.8-27b use prompt-schema + validate/retry instead of strict mode |
| `web.py` | Exa / You.com / Tavily / Ollama search + page fetch, condensing, URL liveness |
| `keys.py` | `KeyPool`: rotates a service's numbered keys (`NAME`, `NAME_2`, ...) on rate limits with per-key cooldowns; used by SerpApi and HF mesh tokens |
| `cache.py` | Disk JSON cache keyed by normalized text; every paid call goes through it; `OFFLINE=true` → cache-hit-or-degrade |
| `sellers.py` | SerpApi: `google_shopping` → listings with `immersive_product_page_token`; `google_immersive_product` → stores; `home_depot` search → `home_depot_product` dims + ETA; ETA text parser; pack-size parsing; rank + recommend |
| `search.py` | `find_parts(SearchRequest)` — full pipeline (see below); `refine_sellers` for lazy expansion |
| `assets.py` | Tiers: CAD URL → Hunyuan3D-2.1 HF Space image-to-3D → proxy box. trimesh normalise to spec dims, +Y up, origin at mount face. Writes `data/parts/<id>/model.glb` |
| `vision.py` | Groq `qwen/qwen3.8-27b` vision: headset frames → spoken answer + optional bounding box + optional `part_query`. Hard cap: 3 frames/request |
| `jobs.py` | In-memory job table + asyncio tasks; results persisted via cache; `summary()` for TTS |
| `warm.py` | Demo warm-up CLI: runs `find_parts` + `resolve_asset` + TTS pre-gen; idempotent |
| `agent.py` | Tool-calling command agent (text or transcribed voice). Server tools run here; client tools returned as `actions` for Unity |
| `voice.py` | Groq Whisper STT; Orpheus TTS with edge-tts fallback; xAI `/voice/token` relay for future realtime swap; TTS disk cache |
| `checkout.py` | Cybersource sandbox auth (HTTP-signature) if keys present, else labelled offline receipt |
| `app.py` | FastAPI routes |

## Search pipeline

```
query + measurement + optional headset frames
  -> vision (optional): qwen3.8-27b describes frames -> appended to query context
  -> discovery (parallel, cached):
       SerpApi google_shopping   (up to 40 listings: price, token, image)
       SerpApi home_depot search (up to 5 products)
       web.search fallback chain (Exa -> You.com -> Tavily -> Ollama, condensed text)
       llm.web_search (browser_search, ~50K tokens) only if all three above are empty
  -> one LLM extract call (role "extract", gpt-oss-120b, json_schema):
       interleaved condensed listings (~4K tokens total prompt)
       -> Plan: N + 2 distinct candidates (best first), known specs, fit constraint axis,
          fit_range_mm for sold-by-size parts ("5 in." hanger accepts a 127 mm gutter)
  -> per candidate (bounded concurrency; the 2 spares are built only to replace drops):
       dims enrichment: HD product (verified, wins) > page text + cheap LLM >
         web-search spec text ("<brand> <model> dimensions") + cheap LLM > drop
         HD spec names: directional ("Left to Right Length") > width/depth/height; flat goods
         with a length field (solar panels) get thinnest side = depth
       duplicate part ids (two plan entries, one product) collapse to one
       HD record overrides the LLM's name/brand/model (it sometimes names the retailer)
       dropped if no seller carries it
       image URL resolved
       sellers: google_immersive_product stores + HD fulfillment offer
       pack-size parsing + unit-price ranking
       deterministic recommendation (sort: best = cheapest in-stock with ETA)
       spec URL liveness check (HEAD)
       fit vs measurement: fit range (HD window ranges, HD sink minimum cabinet size, or an
         accepted size the product states, ±5 mm headset tolerance) else the measured-axis
         dimension
  -> Part list written to cache
  -> assets resolved in background job:
       cad (source_url .glb/.gltf/.obj) -> ai_mesh (Hunyuan3D-2.1 HF Space) -> proxy box
       ai_mesh rejected if uniform-scale residual > 60% (shape contradicts published dims)
       a ZeroGPU quota error disables ai_mesh until restart (anonymous quota is per-IP/day)
       all fetched URLs pass an SSRF guard (public http(s) hosts only, redirects re-checked)
       part.json asset.status: pending -> ready
```

**Token budget**: Groq free tier is ~8K tokens/min on gpt-oss-120b (measured 2026-09-24; docs
say ~200K tokens/day). One search makes exactly one `extract` call. Dims fallback runs on the
`cheap` role (gpt-oss-20b, separate bucket) with a 3K-char page slice.

**Caching**: `find_parts` caches the whole result by `normalize(query) + measurement`. SerpApi
calls are cached individually. `resolve_asset` skips parts whose `model.glb` already exists,
and `save_part` keeps a ready asset when a re-search saves the same part again.

**Offline mode** (`OFFLINE=true`): every network call becomes cache-hit-or-degrade. The
`warm` CLI pre-fills the cache before a demo. A literal "find a <thing>" still works offline
(fast path); if the live measurement differs from the warmed one, the cached answer for the
same query is reused and its fit recomputed.

## Agent

One loop for text and voice. Tools:

| Tool | Side | Behaviour |
|---|---|---|
| `find_part(query)` | Server | Runs the pipeline; ends the turn and returns the job id |
| `show_sellers(sort)` | Server | Lazy-expands sellers (one-shot `google_immersive_product`), re-ranks, returns top 3 |
| `ask_scene(question)` | Server | Only offered when the headset sent frames; calls vision module |
| `select_candidate` | Client action | Passed back to Unity as an action |
| `set_finish` | Client action | As above |
| `place_array` | Client action | As above |
| `start_checkout` | Client action | Opens the hold-to-pay panel only — never pays |
| `equip_tool` | Client action | As above |
| `add_note` | Client action | As above |

Fast-path regex short-circuits the tool loop for common intents (e.g. "sellers, cheapest
first" → `show_sellers`). Loop capped at 3 turns.

Payment is only `POST /checkout`, called by the headset after a physical hold confirmation.
No agent tool can trigger payment.

## Voice

- **STT**: `POST /voice/command` — multipart audio → Groq Whisper (`whisper-large-v3-turbo`) → text → agent loop → TTS.
- **TTS**: `voice.speak(text)` → Groq Orpheus (`canopylabs/orpheus-v1-english`, voice `troy`). Requires terms accepted in the Groq console; falls back to `edge-tts` (`en-GB-RyanNeural`, `audio/mpeg`). After a `model_terms_required` refusal, Groq TTS is skipped until restart.
- **TTS cache**: spoken lines stored to disk; `warm.py` pre-generates canned replies and job summaries.
- **Token relay**: `POST /voice/token` → xAI `/voice/token` — wired for a future realtime websocket swap.

## Checkout

`POST /checkout` → if `CYBERSOURCE_*` keys are set, HTTP-signature auth request to the
Cybersource sandbox (`apitest.cybersource.com`). Otherwise returns a clearly labelled offline
receipt with the same JSON shape.

## Deviations from the original plan

| # | Deviation | Reason |
|---|---|---|
| 1 | `groq/compound` decommissioned 2026-09-21; web search uses `browser_search` on gpt-oss-120b | Model removed from Groq |
| 2 | Groq search and extraction are two separate calls | Groq/xAI reject `tools` + `json_schema` in the same request |
| 3 | Voice is STT → agent → TTS over HTTP; xAI realtime websocket not used | No xAI credits available |
| 4 | Home Depot/Lowe's dims via SerpApi only; generic fetchers used for other sites | HD/Lowe's pages render specs client-side |
| 5 | Home Depot search added as a parallel discovery source | Provides verified dims tied to product id, reducing LLM guessing |
| 6 | Vision model caps at 3 frames, not the plan's ~6 | `qwen/qwen3.8-27b` is the only Groq vision model; hard limit of 3 images/request (confirmed 2026-09-24) |
| 7 | 3D AI meshes from `tencent/Hunyuan3D-2.1` HF Space (anonymous), not Tripo | Tripo requires paid credits; Hunyuan3D-2.1 is free and tested end-to-end |
| 8 | edge-tts used as TTS fallback when Orpheus is unavailable | Orpheus requires explicit terms acceptance per Groq console |
| 9 | Sellers expanded lazily on `show_sellers`, not during initial search | Saves SerpApi quota (free tier: 250/month) |
| 10 | `library` 3D tier dropped | No free, no-auth, text-searchable 3D library exists (see `docs/research/assets-3d.md §2`) |
