# F6: Recall and defect radar

Feature agent F6, 2026-09-26. Branch `feat/grok-safety-radar`. This builds G3 pick 1 (`g3-round2.md`, "The three to build first").

**Headline.** The radar flags the Midea MAW08U1QWT window AC as **recalled** (CPSC #25320, June 2025, mold) and marks a joist hanger and an LG through-the-wall AC **clear**. Three live xAI calls cost **$0.269** in total, and each took 9–18 s. CPSC alone decides the Midea verdict, and it's free. Grok independently found the same recall on cpsc.gov and added five cited owner complaints. Across the three calls the honesty filter dropped 4 items: Grok invented a Home Depot URL twice and, on the LG AC, returned a "no recall found" placeholder dressed up as a recall.

## What was built

| Piece | Where | Notes |
|---|---|---|
| `llm.responses()` / `parse_responses()` | `server/llm.py` | Copied byte for byte from F3 (`feat/grok-installer-intel`), with the same name, signature and position, so the two branches add an identical hunk |
| `safety.cpsc_recalls(manufacturer)` | `server/safety.py` | `GET saferproducts.gov/RestWebServices/Recall?format=json&Manufacturer=<m>`, via `cache.cached("cpsc", m, ttl_s=86400)` |
| `safety.match_model(record, model_no)` | same | Matches the model as a whole token anywhere in the record, upper-cased with dashes and spaces dropped. A plain substring match would let `LUS26` match `LUS26Z`, and a 5-digit model match inside a date. Models shorter than 4 characters never match |
| `safety.grok_check(part)` | same | Model `grok-4.20-0309-non-reasoning`, `web_search` + `x_search` (`from_date` 2 years back), `max_turns: 3`, strict `json_schema` (`recalls[]`, `complaints[]`) and `include: ["no_inline_citations"]`. `[[n]](url)` markup is stripped from the raw JSON before parsing, so no string field can carry it. Only URLs in the call's own citations survive, the same rule as F3; recalls marked `applies_to_model: "no"` are dropped. Cached as `safety_grok/<part_id>` for 24 h |
| `safety.merge()` | same | Applies the fixed verdict rules (see `docs/api.md`) and builds `headline` and `spoken` from templates, not model text |
| `safety.check()` / `start()` / `peek()` | same | `check()` runs both sources with `asyncio.gather`. A failed source becomes `None`, never a raise. `start()` kicks the check off in the background and dedupes in-flight checks per part, so a background start and a user's question share one paid call. `peek()` reads only the cache (no network) and is used by checkout |
| `GET /parts/{part_id}/safety` | `server/app.py` | Errors: `400` or `404`, never a `5xx` for a source failure |
| Agent tool `check_safety(part_id?)` → `show_safety {part_id, verdict, headline}` | `server/agent.py` | The fast path catches "recalled / recall(s)" and "is it/this/that safe", so it works OFFLINE too. The reply is `spoken`, verbatim, with no second LLM turn. `select_candidate` and `show_sellers` call `safety.start(part)`. On a recalled part, `start_checkout` speaks the recall once per part per session, and the panel still opens |
| Receipt `safety_verdict` | `server/checkout.py`, `server/app.py` | Comes from `peek()` (`"unknown"` if the part was never checked) and lands in the order file and the notebook entry |
| `/debug` section | `server/static/debug.html` | Part id input (selecting a card fills it) and a verdict pill with headline, spoken line, recalls, complaints, sources, latency and cost. The agent's `show_safety` action triggers it too |
| Tests | `tests/test_safety.py` (15 offline + 1 `live`) | Fixtures: a trimmed copy of the real CPSC Midea response (`tests/fixtures/cpsc_recalls_midea.json`, records #25320 and #17024) and the live Midea xAI call (`tests/fixtures/llm/xai_responses_safety.json`). Covered: a model match, a brand-only match (and an old dehumidifier recall of a different product type left out), a recall URL from a disallowed domain, CPSC returning 500, markup stripping, uncited items dropped, the domain allowlist, a cache hit, OFFLINE with and without a cache, both sources down, concurrent calls sharing one paid call, the agent tool, the checkout warning (spoken once), the background start on select, and the receipt |

## Live results (3 xAI calls, $0.269 total)

| Part | Verdict | Evidence | xAI latency / total | Cost | Tools | Citations → dropped |
|---|---|---|---|---|---|---|
| Midea MAW08U1QWT window AC | **recalled** | CPSC #25320 lists `MAW08U1QWT`. Grok cited the same cpsc.gov page (`applies_to_model: yes`), which was deduped by URL. 5 complaints: Wirecutter, Amazon, the CPSC injury text, a BBB complaint, airconditionerlab | 14.1 s / 14.3 s | $0.0910 | web ×8, X ×1 (1 post) | 26 → 0 |
| Simpson Strong-Tie LUS26 joist hanger | **clear** | CPSC: 0 records for the brand. 2 complaints with different themes: a class action over premature corrosion and Simpson's own unapproved-fastener warning | 9.1 s / 9.3 s | $0.0935 | web ×8, X ×1 | 47 → 3 (a Home Depot product URL twice, with a different item id from ours and not in the citations; a JLC article not in the citations) |
| LG LT1016CER through-the-wall AC | **clear** | CPSC: 44 records match "LG", including a Feb 2025 LG electric range recall. The same-product-type rule keeps that range recall from flagging an AC. 5 complaints, 5 different themes; 4 of them come from one Amazon page | 17.9 s / 17.9 s | $0.0845 | web ×9, X ×1 | 45 → 1 (a "No official recall found" entry, `applies_to_model: no`) |

All costs are from `usage.cost_in_usd_ticks / 1e10`. The latencies are close to G3's 11.4 s, at about 20 % lower cost ($0.09 against $0.116), probably because of the smaller schema. With `no_inline_citations`, **no** `[[n]](url)` markup appeared in any of the three calls. The regex strip stays as a safety net and is covered by a test.

**CPSC-only sweep (free).** Running the rules over all 40+ cached parts in `data/parts/` flags only the Midea AC. The two brand filters prevent the noise a raw `Manufacturer=` query would cause: the query is a substring match, so "LG" also returns recalls from other firms. The filters are a whole-word brand match in the record, and recency (2 years) plus a shared product word between the part name and the recall title.

**Cache, OFFLINE and end to end.** I ran all of this on a local server. Repeat GETs took 4–65 ms. `OFFLINE=true` served the Midea verdict from cache and gave `unknown` with a 200 for an unchecked part. "is this recalled?" went through `/agent/command` on the fast path and emitted `show_safety`. "any safety problems with this AC?" went through the Groq LLM path and called `check_safety`. "ok buy it from the first seller" spoke the recall before opening the panel, and a repeat didn't. `/checkout` then wrote `safety_verdict: "recalled"` on the receipt.

## Verdict quality

- **The recalled verdict is solid.** CPSC does it deterministically, and Grok agreed on its own. Neither source alone is needed: the tests cover a CPSC outage (Grok's cited cpsc.gov recall still gives `recalled`) and a Grok outage.
- **The clear verdicts are defensible, but a judgement call.** The Simpson corrosion class action is real and relevant to galvanized hangers used outdoors, but it's one source, so it doesn't reach `caution`. The LG AC's complaints are ordinary appliance failures (compressor, noise, tripped breaker), each seen once.
- **The model doesn't reuse theme labels well.** Midea's five complaints came back as "Mold growth" ×2, "Mold in unit", "Mold exposure symptoms" and "Poor drainage leading to mold". Grouping on exact labels undercounts. It doesn't matter for Midea, since CPSC already says recalled, but a part with no recall and scattered labels could stay `clear` when it should be `caution`.
- **X adds nothing here.** Three X searches fetched 1 post and produced 0 complaints, matching G2, G3 and F3. It stays in because it costs little.

## Open issues

- **Theme clustering is exact-label** (see above). The next step is a word-overlap or stem match between themes, or asking the model for a fixed theme vocabulary.
- **Brand heuristics.** Product type is a word overlap between the part name and the recall title (a `ponytail:` comment in the code). The "manufacturer's domain" test accepts a second-level domain label equal to the brand or its first word, so a hypothetical recall page on `delta.com` would count for Delta faucets.
- **CPSC coverage.** CPSC covers US consumer products only, and the manufacturer string must match CPSC's spelling. For example, "Amerimax Home Products" and "Amerimax" both return 0, which may be true or may be a spelling gap.
- **Cost of the background check.** Every newly selected part costs about $0.09 once. The check is not wired into `server/warm.py`, so warm the demo parts by hand (below).
- **The live cache is in the session scratchpad**, not the main repo's `data/`. The demo needs one live call per part, or a copy of `data/cache/cpsc/` and `data/cache/safety_grok/` from a warmed server.
- **`peek()` ignores the TTL**, so a receipt can carry a verdict older than 24 h. That's deliberate: checkout never waits on the network.
- **Merging with F3.** `server/llm.py` is an identical hunk. `server/app.py`'s import block and the end of `agent.TOOLS` will textually conflict with F3 (both add an entry), but resolving it only means keeping both.
- **Pre-existing agent quirk**, not changed here: if the model returns text together with tool calls, `handle_command` breaks before running the tools.

## Demo steps

1. `uv run uvicorn server.app:app --host 0.0.0.0 --port 8000`, with `GROK_API_KEY` in `.env`.
2. Warm the demo parts, about $0.09 each, then free for 24 h:
   `curl :8000/parts/midea-maw08u1qwt-8d6d08/safety`
3. Open `http://<laptop>:8000/debug`, go to "Recall and defect radar", and press **Check safety**. It shows a red RECALLED pill, "Recalled June 2025: risk of mold exposure (CPSC #25320)", the CPSC link and five cited complaints, in about 40 ms.
4. Voice or agent, with the Midea AC selected: "is this recalled?" The Quartermaster says "Heads up: this exact model was recalled in June 2025 for risk of mold exposure.", and the headset gets `show_safety`.
5. "buy it from the first seller": the Quartermaster repeats the recall and says "The pay panel's open if you still want it." Hold to pay, and the receipt shows `safety_verdict: "recalled"`.
