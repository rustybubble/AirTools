# Backend hand-off 4: measure and replace by voice (e2e)

*Sat 2026-09-26, app e2e lane → P4. Three patches on top of `app-handoff-vrnext` (`integration/vr-next` plus the
p4-grok series 0001–0011, the demo server's branch). Made and tested in a scratch worktree
(`backend-e2e`, branch `e2e-measure-replace`). Nothing changed in your repo, on `:8000` or on the demo server `:8004`,
and nothing was pushed.*

Your note said "measure and replace" by voice was coming on `feat/voice-measure-replace` (not pushed), with a draft
contract: `remove_component {component_id}`, `place_part {part_id, model_url, pose, fits, clearance_mm}` and the job rail.
The app already handles both actions. These patches are the smallest server change that makes the agent emit them. It
also adds three things the demo needed: a gap-fitted search, model switching and scale from the gap. If your branch
lands first, keep yours and take what's useful from here. The phrase set and the action args are the contract the app
tests against.

## How to apply

```sh
cd airtools-drone-backend
git switch app-handoff-vrnext          # or any branch with p4-grok 0001–0011 (0001 touches their code)
git am /path/to/AirTools/docs/handoff/p4-e2e/*.patch
uv run pytest -q tests/test_replace.py tests/test_agent.py tests/test_search.py tests/test_mandate.py
```

- The three patches apply with `git am` on `2dfd50f`. The result is byte-identical to the tested tree (`6f94ea91`).
- They do not apply on bare `integration/vr-next`. 0001 edits lines that p4-grok 0009–0011 added (`_OWN_LINE_TOOLS`,
  the `tape_survey` session field, `set_limits`).
- No new dependencies and no new env vars. Restart uvicorn afterwards.
- Tests: `tests/test_replace.py` has 51 tests, all passing. The full suite gives 1296 passed, 2 skipped and 3 failed.
  The 3 failures are `tests/pipeline/test_structure_*` (`os.sched_getaffinity` doesn't exist on macOS), and they fail
  the same way on the untouched branch. No test calls an LLM or the network.

## Patches

| Patch | What | Tests |
|---|---|---|
| `0001-feat-measure-and-replace-by-voice-…` | New `server/replace.py`, which reads the site's `parts.r<rev>.json` (via scene.json's `parts` entry). It holds the phrase matcher and the 3-axis gap fit. In `agent.py`, a fast-path route sits after the coach and reimagine words. Six tools: `remove_component`, `restore_component`, `measure_cavity`, `find_in_gap` (a `find_part` with `SearchRequest.cavity`), `place_in_gap` and `cycle_in_gap`. Up to four phrases joined by "and" / "then" / commas run in one reply. `SearchRequest.cavity` (`models.Cavity`): `search.find_parts` refits every candidate against the gap's three axes and sorts the ones that fit first. This runs for cached answers too. `Session` gains `removed`, `cavity` and `gap_index`, and `_apply_context` reads `context.removed` / `cavity`. `docs/api.md` documents the actions, `SearchRequest.cavity` and the context fields | `tests/test_replace.py` (new) |
| `0002-fix-1-200-in-a-spoken-limit-…` | **A real demo bug.** "under $1,200" set a **$1** limit and left ",200" in the command, because the price regex stopped at the comma. Whisper writes spoken thousands with commas. Now fixed: "$1,200" and "2,500 dollars" are read correctly. Also, "…, put it in" right after a search in the same breath now says "Say put it in when the options are up.", since the candidates aren't there yet. `docs/integration-grok-features.md` gets a row in its fast-path table | +5 |
| `0003-feat-set-the-scale-from-the-gap-…` | "the opening is 34 and a half inches tall" / "the gap is 24 inches wide" → `scale_gap {component_id, axis, real_m}`. The rest of the same reply uses the corrected size. Once a model stands in the gap, "next / previous one" and "option N" become the app placement editor's `cycle_model {index, part_id}`: it keeps the adjusted pose and adds one undo step. The first "put it in there" stays `place_part`. A fit at most 5 mm over on one axis is `fits: "tight"` (amber in the app) and is spoken that way | +8 |

## The phrases (fast path, no LLM)

| Say | Action(s) | Needs |
|---|---|---|
| "remove / take out / pull out the dishwasher", "take the dishwasher out" | `remove_component {component_id}`; the reply says the file's gap ("about 44 by 59 by 44 centimetres, estimated") | a removable component of that id / label / class in the site's parts file |
| "measure it", "measure the gap / hole / opening", "measure the dimensions of it", "how big is the gap?" | `measure_cavity {component_id}` | a part out |
| "the opening is 34 and a half inches tall", "the gap is 24 inches wide", "it's 610 mm wide", "the opening's height is 87.6 cm" | `scale_gap {component_id, axis, real_m}` | a part out |
| "find a dishwasher that fits", "look for new dishwashers", "find one that fits" | `search_started` (a `find_part` with `cavity`: W × H × D from the headset's `cavity` context, including its tape readings in `measured`, else the file × `scale`) | a part out |
| "put it in there", "place it", "install it", "put the best one in" | `place_part {part_id, model_url, name, component_id, fits, clearance_mm, cycle}` for the first candidate that fits (else the one that overruns least) | a part out and candidates (`candidate_ids`, else the last search's) |
| "next one", "show me the next model", "previous one", "show me option 2", "the third one" | `cycle_model {index, part_id, component_id, cycle}` once a model stands in the gap (`placed` / `selected_part_id` among the candidates); else `place_part` for that candidate | the same |
| "put it back", "put the dishwasher back", "restore the original" | `restore_component {component_id}` | a part out |

Compound, one breath: "remove the range, find a 30-inch induction range under $1,200 that fits, put it in" gives
`show_limits` + `remove_component` + `search_started`, and the reply ends "Say put it in when the options are up."

Precedence:
- A running install coach keeps "next" / "back".
- A reimagine on screen keeps "remove …" / "swap …".
- With no `site`, no parts file, or no part out, everything goes to the LLM as before.
- A finish in the same breath ("…, show it in stainless") matches F11's `set_finish` first, so say it on its own after
  the part is in.

## Measured on the patched server (`:8005`, real searches, the real kitchen parts, 2026-09-26 18:15)

```text
> take out the dishwasher and measure the gap
  Took out the dishwasher. The gap is about 44 by 59 by 44 centimetres, estimated. Taping the gap.
  [remove_component dw1, measure_cavity dw1]
> the opening is 34 and a half inches tall
  Setting the scale from the gap's height: 34 and a half inches.            [scale_gap h 0.8763]   (app: ×1.4826)
> find a dishwasher that fits
  Searching for dishwashers that fit the gap: 65 by 88 by 66 centimetres.   [search_started]
  job: "Three dishwashers. The first is an exact fit."  Whirlpool WDP540HAMW 606×876×622 fits · 2 Frigidaires too tall by 13 mm
> put it in there        Putting the Whirlpool WDP540HAMW in. It fits exactly.            [place_part … fits, clearance w 42 h 0 d 37]
> next one               Option 2 of 3, the Frigidaire FDPC4221AS. It's too tall by 13 millimetres.   [cycle_model 1]
> next one               Option 3 of 3, the Frigidaire FDPH4316AS. …                                  [cycle_model 2]
> put it back            The dishwasher's back.                                                         [restore_component dw1]
```

On `synthetic-facade-parts` (cab1, 600 × 820 × 580 mm exact), "find a countertop dishwasher that fits" returned the
Mueller DW-600 (fits, 51 mm spare), the BLACK+DECKER BCD6W (fits, 29 mm) and the Hamilton Beach HBDW3208 (too deep by
25 mm). "find a dishwasher that fits" found three 24″ built-ins, all too tall for an 82 cm gap.

## The kitchen's scale

At scale 1.0 the kitchen's dishwasher gap is 437 × 591 × 445 mm. Your README says the scan reads about 1.47× small
indoors (the altitude caption), and at that scale no real dishwasher fits.

The app and this patch set the scale from the opening's standard height: "the opening is 34 and a half inches tall"
(34½″ under a 36″ counter). That gives ×1.48 and a gap of 648 × 876 × 659 mm, which matches your 1.47. The headset
says the assumption on its heads-up line: "Scale set from the gap's height · 34½″ · ×1.48". A tape of the real
counter height works too: tape it, then use Set scale in the Scene window.

## Asks

1. `feat/voice-measure-replace`: if it lands, keep these action names and args, or tell us the differences. The app
   accepts `pose` / `fits` / `clearance_mm` in several shapes (`PlacePartArgs`).
2. Fix the "$1,200" limit bug (0002) whatever happens to the rest.
3. The kitchen scale: ideally `scene.json` would carry the indoor correction, or a `scale_candidates` entry the app
   can offer.

---

# Hand-off 4b: one sentence → the whole replace, autonomously (0004–0012)

*Sat 2026-09-26, app autonomy lane → P4. Nine more patches on top of 0001–0003 (branch `e2e-measure-replace` in the
scratch worktree `backend-e2e`, tip `2b8d6e0`). Tested on `:8005` with the real kitchen parts; nothing pushed.*

"Replace the dishwasher with a new one that fits" used to reach the LLM agent, which answered "Surveying 4 appliances".
Now one sentence runs the whole chain on the server, and the headset executes each step's actions as they land.

## How to apply

```sh
git am /path/to/AirTools/docs/handoff/p4-e2e/00{04..12}-*.patch     # on top of 0003 (a7e7a2b)
uv run pytest -q tests/test_replace_job.py tests/test_replace.py tests/test_llm.py tests/test_voice.py tests/test_agent.py
```

The result is byte-identical to the tested tree (`9b88aaa1`). No new dependencies. Env (all optional):
`LLM_AGENT=xai:grok-4.20-0309-non-reasoning` (recommended, below); `LLM_FALLBACK` (default
`xai:grok-4.20-0309-non-reasoning`, `""` = off). Full suite: 1368 passed, 2 skipped, 3 failed (the same macOS-only
`tests/pipeline/test_structure_*` as before). `tests/test_replace_job.py`: 50 tests, no network, no LLM.

## Patches

| Patch | What |
|---|---|
| `0004-fix-llm-reasoning-parameters-per-model…` | `reasoning_effort` only for models that take it: both grok-4.20 models answer 400 on it, grok-4.3/4.5/4.6/4.7 take it. A model that 400s on it is retried once without and remembered. `LLM_AGENT=xai:grok-4.20-0309-non-reasoning` works now |
| `0005-feat-llm-a-Groq-call-that-can-t-be-answered…` | When a Groq call can't be answered (every key limited, unreachable, 5xx), the same call goes to `LLM_FALLBACK`. This covers agent, extract, cheap, vision and asset (the voice test's "meshgen: extract call … Connection error"). Groq's `browser_search` never falls back |
| `0006-fix-voice-speech-to-text-retries-once…` | Whisper retries once, then falls back to xAI `grok-transcribe` over one short realtime session (~3 s; xAI has no REST STT). `/voice/command` answers 503, never 500 |
| `0007-feat-one-sentence-the-whole-replace…` | `server/replace_job.py`: remove → measure → scale → search → pick → model → place → `job_done`, on runjob's table and poll (`kind: "replace"`). Also: the `replace_component` tool for the LLM plus fast-path phrasings; the voice test's fixes (busy reply instead of a 500; "34 1⁄2" and number words; two sentences; `show_model`; the scale hint; no red fit placed); `undo_edit` |
| `0008…` | The next two candidates' proxy boxes get upgraded too. A calibrated scale (any context `scale` ≠ 1, or `scale_source`) skips the prior: the kitchen starts at its site default ×1.63 |
| `0009…`, `0011…` | Wording: "Found three 30 inch induction ranges … two fit"; "an LG"; an over-limit pick says so in the spoken summary |
| `0010…` | When nothing that came back fits, one more search for the gap's size ("30 inch fridge"). The plain-kind fallback starts only when the named search is slow (45 s) or empty. `GET /parts/{id}/model.glb` builds a searched part's model on first request (never a 404). "next one" with nothing in the gap places a model instead of swapping |
| `0012-docs…` | `docs/api.md` (the replace job, `undo_edit`, `show_model`, normalisation, the busy reply) and the README (LLM_FALLBACK, agent model numbers) |

## The chain (one run, `GET /job/run/{run_id}?after=n`)

| Step | Action(s) | Notes |
|---|---|---|
| `remove` | `remove_component {component_id}` | Named, a synonym ("stove" is the range), or `context.pointer`'s part ("replace this") |
| `measure` | `measure_cavity {component_id}` | The headset tapes the gap |
| `scale` | `scale_gap {component_id, axis, real_m}` or `skipped` | Only when uncalibrated (scale 1, no `scale_source`) and the gap is implausible for its kind. Uses the standard opening that keeps the other axes plausible: dishwasher 34½″ high, range 30″, fridge 30/33/36″, base cabinet 34½″. Says "The scan reads small; scaled it from a standard 34½″ dishwasher opening (×1.48)." |
| `search` | `search_started {job_id}` | Fitted to the gap (W × H × D), 150 s cap, `job_step {status: "running"}` progress lines. Plain-kind fallback; a size-anchored retry |
| `pick` | — | Fits > tight (≤ 5 mm over), within the purchase limit when one fits under it. None fits: `stopped`, saying how far off the closest is |
| `model` | — | The pick's GLB is built or awaited (a proxy box gets one more try). The next two are built before `job_done` |
| `place` | `place_part {part_id, model_url, name, component_id, fits, clearance_mm, cycle}` | |
| — | `job_done {kind: "replace", spoken, summary, part_id, component_id, fits, models_ready, total_usd, …}` | The app says `spoken` and shows `summary` on the chip |

Routing:
- Fast path (no LLM), for a removable part of the open site:
  - "replace / swap out / change out / upgrade the X (with / for …)";
  - "the X is broken, … new / replace …";
  - "take out the X and put in a new one";
  - "remove the X. find a Y that fits";
  - "get / put a new X in here";
  - "get a counter-depth fridge in here".
- Anything else goes to the LLM's `replace_component {component, query, max_price_usd}`. It's offered when the scene has
  parts, and the system prompt names them.
- A "find a new X that fits" with X's gap already open stays the step flow's search.
- Follow-ups: "next one", "option 2", "undo" (`undo_edit`), "put it back".

## Measured on `:8005` (the kitchen at ×1.63, `LLM_AGENT=xai:grok-4.20-0309-non-reasoning`, live searches and models)

```text
> “Replace the dishwasher with a new one that fits.”  → job_started “Replace the dishwasher”   (fast path, 0.0 s)
  remove DONE   Took out the dishwasher.                          ▸ remove_component dw1 (gap 713 × 963 × 725 mm)
  measure DONE  Taped the gap: 71 × 96 × 72 cm on the scan.       ▸ measure_cavity dw1
  scale SKIPPED The scale's already set (×1.63); kept it.
  search DONE   Found three dishwashers for the 71 × 96 × 72 cm gap: three fit.   ▸ search_started
  pick DONE     Picked the Frigidaire FDPC4221AS: 74 mm to spare, $329.
  model DONE    3D model ready: built to size from the product photo.
  place DONE    Put the Frigidaire FDPC4221AS in. It fits with 74 millimetres to spare.  ▸ place_part (GLB 40 KB, tier llm)
  job_done “Put a Frigidaire FDPC4221AS in the dishwasher gap, 74 mm to spare. Say next one to see the other two.”  (1.7 s)
> “next one” → cycle_model 1 (Frigidaire FDPH4316AS) · “undo” → undo_edit · “put it back” → restore_component dw1

> “the fridge is too small for us, get us a bigger one”  (Grok → replace_component fridge, query “larger fridge”, 0.7 s)
  search  … 40 s of progress lines, none of the larger fridges fit (36–60 in) → “searching for 30 inch fridges”
  search DONE (48.7 s) Found three 30 inch fridges for the 80 × 192 × 88 cm gap: two fit, one too big.
  pick    LG LTCS20020S: 37 mm to spare, $899 · model DONE (2.1 s, generated, llm tier) · place_part
  job_done “Put an LG LTCS20020S in the fridge gap, 37 mm to spare. Say next one to see the other two.”  (54 s)
```

At scale 1 (uncalibrated) the same dishwasher sentence adds `scale_gap h 0.8763`, "The scan reads small; scaled it from a
standard 34½″ dishwasher opening (×1.48)", and puts in the Whirlpool WDP540HAMW (an exact fit).

## Which Grok for the agent

These are 12 prompts × 2 on the kitchen, on the LLM path only (fast path off, tools recorded, not run):

| Model | Right tool | First call median | p90 | max |
|---|---|---|---|---|
| `xai:grok-4.20-0309-non-reasoning` | 24/24 | 0.82 s | 1.1 s | 1.2 s |
| `xai:grok-4.7` | 22/24 | 1.5 s | 5.7 s | 11 s |
| `xai:grok-4.5` | 22/24 | 1.2 s | 2.2 s | 57 s |
| `groq:openai/gpt-oss-120b` | 15/24 | — | — | — |

- grok-4.7's two misses were answers in words with no tool call.
- gpt-oss-120b ran out of free-tier quota mid-run.

Recommendation: `LLM_AGENT=xai:grok-4.20-0309-non-reasoning`. `xai:grok-4.7` also works, but it's slower and has a
long tail.
