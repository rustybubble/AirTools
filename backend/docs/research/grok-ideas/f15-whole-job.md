# F15: "do the whole job"

Feature agent F15, 2026-09-26, branch `feat/grok-do-the-whole-job`. Built from G5's pick 3
(`g5-round4.md` §5): one sentence runs the Grok features as a chain, and each step lands in the
headset as it finishes.

**Branched off `integration/grok-all`, not `main`.** The chain calls F5 (postcard), F6 (safety),
F10 (survey), F13 (rules) and F14 (packet), and those only exist together on the integration
branch. Merge this after `integration/grok-all`, or review it as a diff against that branch.

## What was built

| Piece | Where | Notes |
|---|---|---|
| Chain | `server/runjob.py` | A fixed chain, not a Grok planner. xAI's multi-agent client tools are a gated beta (G5 call #1), and six LLM tool picks in a row would be slow and fragile. |
| Endpoints | `server/app.py` | `POST /job/run {session_id, site?, goal?, context}` returns `{run_id, steps}` at once. `GET /job/run/{run_id}?after=n` returns the actions from index n plus `next`. `409` if a run is already going for the session; `400` with neither a site nor a goal. |
| Agent tool | `server/agent.py` | `run_job(goal?)`. Fast path: "do the whole job", "run it end to end", and "handle it" / "take care of it" when that's the whole command ("how do I handle it?" stays a question). It replies with `job_started` and "On it: survey, part, safety, rules, preview, packet. I'll stop at the pay panel." The tool description carries the trigger phrases, for the realtime relay (which skips the fast path). |
| Unity actions | `docs/api.md` | `job_started {run_id, steps}`, `job_step {i, name, status, spoken, seconds, cost_usd}` followed by the step's own actions (`show_survey`, `select_candidate`, `show_safety`, `show_rules`, `show_postcard`, `show_packet`, `start_checkout`), then `job_done {total_usd, after_rebates_usd, packet_url, cost_usd}`. |
| Debug console | `server/static/debug.html` | "Do the whole job" section: site, need, a step timeline (status, time, cost, spoken line) polled with `?after=n`. The steps' actions draw in their own sections, and a `job_started` from a typed command starts the same poll. |
| Tests | `tests/test_runjob.py` | 8 tests, every step's module mocked. See below. |

### The chain

1. **survey**: F10's survey of `site`, only when there's a site and no stated need. The worst pin
   with a `part_query` becomes the need; its frame and box also place the postcard.
2. **part**: a parts search (`jobs.start_search`) for the need. The top candidate becomes the
   agent session's selection. It is also written to the notebook as a `placement`
   (`source: "run_job"`), so the packet and the report list it.
3. **safety**, **rules**, **postcard**, in parallel: F6 `safety.check`; F13's rules check (skipped
   when the part has no rules job kind, so it spends nothing); F5's postcard (the headset's frame,
   `context.frame_id`, or the survey pin's frame and box).
4. **packet**: F14's packet, with a public link unless OFFLINE. The survey and rules checks ran
   as jobs, so the packet's extras hook finds them.
5. **checkout**: a `start_checkout` action, i.e. the hold-to-pay panel. **It never pays:** nothing
   in `runjob.py` touches `server/checkout.py`, and a test fails if it's called. A `recalled`
   verdict ends the run here: "This model is recalled; I stopped before checkout."

A step that fails, times out (45 s; 90 s for `part`), hits an OFFLINE cache miss or has nothing
to work on is `skipped` with a spoken note ("No part, so no recall check."), and the chain goes
on. Each step's `cost_usd` is what it spent in this run: 0 for a cached recall check, rules check
or postcard.

## Live run (1 run, kitchen scene, cap $0.50)

One end-to-end run, in-process, on the kitchen scene (the user's own scan). It used a fresh
`DATA_DIR`, so every call was cold. Goal: "ductless mini-split heat pump 12000 BTU"; context
`frame_id: 0481` and a public campus address. With a stated need there's no survey. The Hospital
scene was not used: its footage licence is unverified (F10), and the packet uploads the postcard
publicly.

| t (s) | Step | Status | Took | Grok cost | Spoken |
|---|---|---|---|---|---|
| 0.0 | `job_started` | | | | "On it: part, safety, rules, preview, packet." |
| 12.5 | part | done | 12.3 s | $0 (Exa + Groq extract) | "Picked the Ready to Install 12,000 BTU 23 SEER2 Wi-Fi Ductless Mini Split..." (TCL TH12SVH23XW, Home Depot, $448) |
| 24.0 | safety | done | 11.4 s | **$0.0998** | "Caution: several owners report Excessive noise." |
| 25.5 | rules | done | 13.2 s | **$0.1897** (permit $0.1038 + money $0.0859) | "You'll need a mechanical (HVAC) permit and an electrical permit from the City of Atlanta Office of Buildings... Georgia Power Ductless Mini-Split Heat Pump Rebate: up to 500 dollars... Not legal advice; confirm with the permitting office." |
| 27.0 | postcard | done | 14.5 s | **$0.0800** | "Here's how it'll look installed." (the unit on the kitchen wall above the cabinets) |
| 32.4 | packet | done | 5.6 s | **$0.0012** | "The job packet's up: scan the code. The link is public for 7 days." |
| 32.4 | checkout | done | 0.0 s | $0 | "Pay panel's open. Hold to pay when you're ready." |

**Total: $0.3707 of Grok, 32.4 s wall time** (the three middle steps overlapped: 39 s of step
time in 15 s). The packet had 4 pages with the `postcard`, `rules` and `safety` sections, read
through the real getters. The public link returned 200. Then it was revoked and the xAI file
deleted, and the same GET returned **404**. The key was never printed.

**Replay.** The same command with `OFFLINE=true` on that data dir finished in 1.5 s at $0: every
step came from cache, and the packet was LAN-only. Driven from `/debug`, the browser's own frame
pick had no cached Imagine edit, so the postcard step said "needs the uplink" and the chain went
on.

**Bugs the live run found (fixed here).**
- *After rebates: $-52.00.* An "up to $500" rebate off a $448 kit showed as $-52 on the packet
  cover and in `job_done`, and the Grok summary said "you have a credit of $52.00" (52 was in the
  facts, so the number check passed). Both totals now floor at $0. Test:
  `test_rebate_bigger_than_the_price_floors_at_zero`.
- *Cached steps were billed again.* An OFFLINE replay reported $0.29 because F6 and F13 return
  the cost from when they fetched. `run_job` now counts 0 when the answer was already cached
  (`rules.is_cached`, and a `safety_grok` cache check).
- *An OFFLINE miss read as "failed".* It's now "The postcard step needs the uplink; skipped it."

## Tests (8, all offline)

1. **Happy path**: `job_started`; survey (`survey_started`, `show_survey`); part
   (`search_started`, `select_candidate`); safety, rules and postcard in any order; packet
   (`show_packet`); checkout (`start_checkout`); `job_done`. Also checks: the pin's query drives
   the search and its box the postcard, the notebook gets the placement with the site, the
   session's selection is set, `job_done` totals and cost add up, and there's no pay/authorise
   action (`checkout.authorize` is mocked to raise).
2. **Recalled** part: no `start_checkout`, checkout step `stopped` with the recall line, run
   status `stopped`, and the packet still built.
3. **Failed and slow steps**: a postcard that raises and a rules check that hangs past the cap
   are both skipped with a note, and packet and checkout still run.
4. **Rebate bigger than the price**: `after_rebates_usd` is 0.
5. **No part**: a stated need skips the survey; an empty search skips the part, and every step
   that needs a part says why.
6. **Paging and HTTP**: `after=3` returns exactly `actions[3:]`, `after=next` returns nothing,
   a second POST gets `409`, an unknown run `404`, a bad site `400`, and no site or goal `400`.
7. **OFFLINE with everything cached**: a real `safety.check` reads its cache (cost counted as 0),
   and the real `packet.make` builds a LAN-only PDF with no network. An uncached survey is
   skipped ("needs the uplink"), and so is an uncached postcard.
8. **Agent**: the fast paths, the spoken line, "already running", and "tell me what to fix" with
   no scene.

The full suite: 860 passed, 2 skipped; `ruff check` is clean.

## Demo steps

```bash
uv run uvicorn server.app:app --host 0.0.0.0 --port 8000
curl -X POST localhost:8000/job/run -H 'content-type: application/json' -d '{"session_id":"demo",
  "site":"kitchen","goal":"ductless mini-split heat pump 12000 BTU",
  "context":{"frame_id":"0481","address":"225 North Ave NW, Atlanta, GA 30332"}}'
curl 'localhost:8000/job/run/<run_id>?after=0'      # then ?after=<next> until status != running
curl -X DELETE localhost:8000/packet/<packet_id>    # take the public packet down afterwards
```

In the headset: load the Hospital scan and say "do the whole job". Survey pins drop, the repair
part is selected, then the recall pill, the permit and rebate card and the postcard fill in, the
packet QR appears, and the pay panel opens. Warm it once before the demo (about $0.36 cold; $0
and about a second warm). `/debug` shows the same timeline under "Do the whole job".

## Open issues

- **Voice.** Over `WS /voice/realtime` the `run_job` tool works, but the relay doesn't push the
  run's later actions, so the headset polls `GET /job/run/{id}` (G5 suggested pushing them).
- **No closing Grok line.** G5 suggested a number-checked closing sentence. The template
  `job_step` lines and `job_done` cover it; add one if the demo wants a summary voice.
- **Top candidate, not the "best" one.** The chain takes the search's first candidate. G5
  suggested preferring verified, in-stock and cheapest; add that in `_part` if picks look off.
- **Rebate vs certification.** F13 said the TCL unit "isn't ENERGY STAR certified" and in the
  same breath listed the Georgia Power mini-split rebate as active at up to $500. That rebate
  likely needs a certified system. F13 should tie the two together (an F13 fix, not here).
- **Runs live in memory**, like jobs; a restart forgets them. The chain also leaves a
  `placement` notebook entry for a part that was chosen, not physically placed.
- **Cost of a cached F6/F13 answer** is counted as 0 by checking the cache first.
  `cache.get` ignores the TTL, so a refetch of an expired entry would be logged as $0.
