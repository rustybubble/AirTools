# F3: "Who installs this near me?" (installer intel)

Feature agent F3, 2026-09-26. Branch `feat/grok-installer-intel`. This builds G2 idea #2 (`g2-voice-agents.md` §4, §5 pick 2).

**Headline.** One xAI Responses call (web search plus X search) returns real, local installers with links to their evidence, in 13–20 s for about $0.10–0.14. Four live runs listed 20 businesses. All 14 evidence pages we fetched loaded (HTTP 200), and 11 of them showed the business or the claimed text (the other 3 were unreadable or only quote-checked). The web does almost all the work: X search ran in 3 of 4 calls but never supplied an installer. Quotes are the weak spot: 4 of the 9 we checked were found word for word on the page.

## What was built

| Piece | Where | Notes |
|---|---|---|
| `llm.responses(model, input, tools, **kw)` | `server/llm.py` | Plain httpx `POST https://api.x.ai/v1/responses` with `GROK_API_KEY`. No SDK retries, so one call is one bill. Returns `ResponsesResult{text, citations, cost_usd, tool_usage}` and logs latency, cost, tool counts and citation count. Honours `OFFLINE`. `llm.chat` is untouched |
| `parse_responses()` | `server/llm.py` | Joins the `output_text` blocks. Collects citation URLs from three places: the message's `url_citation` annotations, each `web_search_call` item's `action.sources[]`, and opened pages (`action.url`). The top-level `citations` field was **empty** on `/v1/responses` in all 4 calls, so the annotations are the real source. Cost is `usage.cost_in_usd_ticks / 1e10` |
| `intel.find_installers(part_or_trade, location, *, max_results=5)` | `server/intel.py` | Model `grok-4.20-0309-non-reasoning` ($1.25 / $2.50 per 1M tokens). Tools: `web_search` with `user_location {type: approximate, country: US, city, region}`, where city and region are parsed from `"City, ST"`, plus `x_search` with `from_date` set one year back. Also sends `text.format` strict `json_schema`, `include: ["no_inline_citations"]` (otherwise `[[1]](url)` links land inside the JSON strings) and `max_turns: 6`. The reply is validated with pydantic, then filtered by the honesty rule. Results are cached via `cache.cached("intel", {part_or_trade, location})` |
| `POST /intel/installers {query, location?}` | `server/app.py` | **Synchronous**. A job queue isn't worth it at 13–20 s with an instant cache. Errors: `400` empty query, `503` offline miss, `502` xAI failure |
| Agent tool `find_installer(trade, location\|null)` | `server/agent.py` | The location comes from the tool call, else `context.location`, else `Settings.DEFAULT_LOCATION` (`"Atlanta, GA"`, new). It returns the action `show_installers {installers, summary}`. The spoken reply is the intel summary, with no second agent turn. The system prompt gained 4 words ("or local installers") |
| Debug page section | `server/static/debug.html` | Query and location inputs; the list shows evidence links `[1] [2]`, the quote, and the kept/dropped/latency/cost line. The agent's `show_installers` action renders in the same list |
| Docs | `docs/api.md` | Covers the endpoint, the action, the context `location` field, the honesty rule and a known-limits row |
| Tests | `tests/test_intel.py`, `tests/test_agent.py` | The respx fixture `tests/fixtures/llm/xai_responses_installers.json` is trimmed from the live mini-split response: real `web_search_call`, `custom_tool_call` (x_keyword_search), `reasoning`, `message` and `usage` shapes, with hand-edited message text. Tests cover request shape (model, both tools, `user_location`, schema, include), parsing, dropping (no URL; a URL the search never returned), dedupe/merge, the summary fallback, cache hit on a normalised key, OFFLINE hit and miss, endpoint default location and error mapping, and the agent tool/action plus the one-turn reply |

**Honesty rule, as built.** Having a URL is not enough. An evidence URL survives only if it appears in that call's own search citations (compared without scheme or trailing slash). An installer with no surviving URL is dropped. Duplicate names merge their evidence. If anything was dropped, the model's summary is replaced by a deterministic line built from the kept names, because the model's summary may name a dropped business. The summary is also cut to one sentence, since the model sometimes writes three.

## Live results (4 real calls, $0.472 total)

| # | Query → location | Latency | Cost | Tools used | Listed → kept |
|---|---|---|---|---|---|
| 1 | K-style gutter installation → Atlanta, GA | 13.5 s | $0.136 | web ×11, X ×0 | 5 → 5 |
| 2 | mini-split installer → Atlanta, GA | 13.0 s | $0.128 | web ×9, X ×2 (2 posts) | 5 → 5 |
| 3 | window AC installation → near Georgia Tech, Atlanta | 12.8 s | $0.105 | web ×10, X ×2 (0 posts) | 5 → 5 |
| 4 | agent: "who installs gutter guards around here?" → default Atlanta, GA (end to end through `/agent/command` and `llm.responses`) | 19.5 s xAI + 0.7 s Groq = 20.1 s | $0.103 | web ×11, X ×1 | 5 → 5 |

Every call used about 83K input tokens, 25–49K of them cached, so the search context dominates cost. Calls 2–4 used the final prompt. Call 1 used the first draft, which had no "run an X search" line and no "sentiment from third-party reviews only" line. That run made **zero** X calls and marked businesses "positive" from their own marketing copy, which led to both prompt fixes.

**Who came back.** All are real Atlanta-area businesses:
- gutters: K & K Gutters, Urban Seamless Gutters ATL, Solify, Five Star Gutters GA, KR Gutters
- mini-splits: PV Heating Cooling & Plumbing, Estes, R.S. Andrews, Bardi, Bird Family
- window AC: Walmart store 3741's install service, Taskrabbit taskers, FixTman, Indoor Experts, Estes
- gutter guards: Urban Seamless, Mastershield ATL, Gutter Perfection, Swainco, TRG

The window-AC query sensibly surfaced handyman marketplaces (Taskrabbit, Walmart), because HVAC firms don't do window units. **No installer was dropped live**: the model stayed inside its citations every time. The filter is a safety net, and the tests prove it works.

**Spot-check (fetched the evidence pages).** All 14 fetched pages returned 200. Eleven showed the business name or its claimed service text: K & K, Urban Seamless, PV, profindr (lists Estes and PV), Estes, Indoor Experts, Five Star, KR, Mastershield, TRG, and bestpickreports (Gutter Perfection and Swainco). Taskrabbit and Walmart render client-side or bot-wall, so we could not read them. The Urban Seamless gutter-guards page was checked only for its quote, which wasn't found. Checking the quotes word for word:
- **found:** K & K, PV ("absolutely incredible"), and the Swainco and Gutter Perfection reviews on bestpickreports
- **not found:** Urban Seamless ×2, Indoor Experts, Taskrabbit and Walmart. Taskrabbit and Walmart are JS or bot-walled. The others are either paraphrased or come from a review widget that isn't in the static HTML

So treat `quote` as a model-extracted snippet, not a verified citation. `docs/api.md` says so.

## Limitations

- **X adds little for local trades.** This confirms G2: 5 X searches across 3 calls fetched 2 posts and produced 0 installers. It costs little ($5 per 1K posts fetched), so it stays for the rare hit.
- **Latency is 13–20 s.** Too long for a live voice beat, so warm the demo queries first (below). The agent tool blocks the command for that time. If that becomes a problem, switch it to a background job that emits the action on completion.
- **Quote fidelity** is roughly half verbatim (see above). Sentiment comes from the model and isn't cross-checked.
- **`user_location` only for "City, ST".** Free text like "near Georgia Tech" biases only the country. Call 3 still stayed in Atlanta because of the prompt.
- **The one-sentence trim** splits "Dr. Roof." style abbreviations (a `ponytail:` comment in `intel._first_sentence`).
- **The cache has no TTL**, so installers stay cached until `data/cache/intel/` is cleared. The cached call-1 gutter result in this worktree came from the first-draft prompt: re-fetch it (~$0.14) before a demo if the sentiment matters.
- **OFFLINE**: `/intel/installers` serves only cached lookups. The agent's offline path has no installer intent: its regex fast path only covers `find_part`.

## Demo steps

1. `uv run uvicorn server.app:app --host 0.0.0.0 --port 8000`, with `GROK_API_KEY` in `.env`.
2. Warm the lines you'll say, about $0.12 each. The cache key is the exact query plus the location, normalised:
   `curl -X POST :8000/intel/installers -H 'Content-Type: application/json' -d '{"query":"K-style gutter installation"}'`
3. Open `http://<laptop>:8000/debug`, go to "Who installs this near me?", and press **Find installers**. A cached result shows in about 50 ms, with evidence links `[1] [2]` to click.
4. Voice/agent: "who installs gutter guards around here?" The Quartermaster speaks the summary, and the headset gets `show_installers`. For the demo, warm the exact trade string the agent produces (it's in the cache key; the live run used "gutter guard installation"), or rely on one live 20 s call.
