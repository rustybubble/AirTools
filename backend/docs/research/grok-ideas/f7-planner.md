# F7: "Where does it go?" planner

Build of G3 pick 3 (`g3-round2.md` §5), 2026-09-26, branch `feat/grok-placement-planner`. The user
says "LED strip under these cabinets" or "hooks under this cabinet every 10 cm". Ghost segments
and points appear on the scan, with a length and a count that flow into the BOM.

**Bottom line.**
- Local Python does all the geometry over `structure.r<rev>.json`. One fast LLM call only picks
  the planner and its arguments from a text summary of the scene, never coordinates. This is the
  split G3's live checks 2 and 4 asked for: Grok alone took 194 s, or was fast but picked the
  wrong cabinets.
- On the kitchen, `grok-4.20-0309-non-reasoning` got **6/6** phrasings right at **0.67–0.96 s** and
  **$0.0023–0.0025** per call ($0.0005 when the prompt is cached). The geometry adds under 10 ms.
- Groq `openai/gpt-oss-120b` also got **6/6** right, twice. Unthrottled it takes 0.95–1.65 s and
  costs nothing, but back-to-back calls hit the free tier's 8K tokens/min: the prompt is about
  2K tokens, so five calls in a row waited 8–17 s on 429 retries. It would do the job just as
  well. The default stays xAI because this is the Grok-feature branch; `LLM_PLAN=groq:openai/gpt-oss-120b`
  swaps it.
- `grok-4.3` also got 6/6, but took 2.2–4.4 s at $0.0016–0.0035. It is not worth it here.
- Live spend for this build: **$0.051** over 23 xAI calls (cap $0.20). Each call's cost is now
  logged by `llm.chat()` (`cost_usd=`, from `usage.cost_in_usd_ticks`, 1e10 ticks = $1).

## What was built

| Piece | Where | Notes |
|---|---|---|
| Planners | `server/plan.py` | Pure numpy over the structure layer: `under_cabinet_runs`, `along_edge` (spacing via `hanger_plan`, or a fixed `count`), `centered_on`, and `measure` (the cheap extra) |
| `hanger_plan` | `server/plan.py` | A verbatim copy of the one in `server/realtime.py` on `feat/grok-realtime-quartermaster`, so voice and plan give the same count (3.66 m at 600 mm = 7). Fold one into the other once both are on `main` |
| Planner pick | `plan.plan()` | `llm.extract("plan", ..., Choice)`: strict json_schema `{planner, target_id, spacing_mm, count, stock_length_m, hint}` |
| LLM role | `server/llm.py`, `server/config.py` | `plan` → `xai:grok-4.20-0309-non-reasoning`, overridable with `LLM_PLAN` |
| Endpoint | `POST /scene/plan` | 409 no structure layer, 422 planner `none` (the hint is the detail), 422 unknown target, 502 schema-invalid reply, 503 offline |
| Agent | `server/agent.py` | Tool `plan_placement(request)`, offered only when `context.site` is set; it emits `show_plan {plan_id, segments, points, label}` and speaks the locally built line. "place them" is a fast path: `place_array {spacing_mm, plan_id}` for point plans, or the `what_else` BOM with the count for runs |
| Debug | `/debug` | A plan section: site, utterance, a Plan button, and a top-down SVG (x right, z down) of plane outlines, objects and the plan |
| Docs | `docs/api.md` | The endpoint, the `show_plan` action, and the `site`/`pointer` context fields |
| Tests | `tests/test_plan.py` (12, 1 live) | A trimmed kitchen fixture (`tests/fixtures/structure/kitchen/`, 31 KB). Only `llm.chat` is mocked, so the strict schema and validation in `extract()` still run |

### Geometry notes

- **Wall cabinets.** A `cabinet_door` whose bottom is more than 0.3 m above the counter counts as a
  wall cabinet. The counter is the largest horizontal plane with cabinets wholly below and wholly
  above it (`p5`, y −0.563 on the kitchen). If there is no such plane, the rule falls back to the top
  40 % of the objects' height span.
- **Nested rectangles.** A door that lies mostly inside a bigger rectangle on the same plane is
  dropped. On the kitchen that keeps the outer `o10` and drops `o9`/`o11`, as the call-2 oracle
  did. It matters for the length: `o10` reaches 2.2 cm further than `o11`.
- **Runs.** Bottom edges are grouped per wall line (parallel within 25°, within 25 cm across), then
  merged as intervals along the wall with a 2 cm join. Kitchen: one run of **2.433 m** from
  `[-1.413, 0.03, 1.213]` to `[-1.303, 0.01, -1.219]`, the same endpoints as the G3 example.
  Grouping per wall is meant to cover L-shaped and galley kitchens, but only the kitchen tested
  it.
- **`centered_on`.** The target's normal is taken from the object's own corners, not its plane,
  because `p13` is labelled horizontal while holding vertical doors. It is turned to face
  `scene.json`'s `recommended_spawn`.
- **`measure` on a plane.** The PCA long axis of the outline in plan view: the counter `p5` gives
  1.75 m.

## Live results (scene/kitchen, `structure.r1.json`)

The pointer is given in the structure frame, and the planner input is ~2.0K prompt tokens and ~33
completion tokens. "Right" means the planner, the target and the arguments are all what a person
would pick. Runs are the final prompt (after the fixes below).

| # | Phrasing (context) | Expected | grok-4.20 non-reasoning | ms | $ | gpt-oss-120b (Groq) | ms | grok-4.3 | ms | $ |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | "LED strip under these cabinets, it comes in 1 metre strips" | `under_cabinet_runs`, stock 1 m → 2.43 m, 3 strips | right | 958 | 0.00249 | right | 947 | right | 2870 | 0.00310 |
| 2 | "hooks under this cabinet every 10 cm" (pointer on o5) | `along_edge` o5, 100 mm → 3 hooks on 0.26 m | right | 803 | 0.00250 | right | 1647 | right | 3165 | 0.00156 |
| 3 | "put 4 hooks evenly along the bottom of this one" (pointer on o10) | `along_edge` o10, count 4 | right | 761 | 0.00231 | right | 1026 | right | 3006 | 0.00336 |
| 4 | "put a handle in the middle of this door" (pointer on o27, 128×30×20 mm) | `centered_on` o27 | right | 665 | 0.00250 | right | 7999* | right | 2201 | 0.00300 |
| 5 | "how long is the counter?" | `measure` p5 → 1.75 m | right | 815 | 0.00227 | right | 17497* | right | 4409 | 0.00348 |
| 6 | "hangers along the gutter every 60 cm" (no gutter in a kitchen) | `none` → 422 "Point at the gutter." | right | 929 | 0.00054 (cached) | right | n/r* | right | n/r | 0.00332 |

\* Groq free-tier 429 waits. A second full Groq run was also 6/6, with retries on calls 2–6
(8.3–17.3 s); its first call took 1066 ms. "n/r" means the latency was not recorded: a 422 raises
before the plan reports it, and the logging fix came later.

Through the whole stack (`POST /agent/command`, `context.site = "kitchen"`), sentence 1 took 563 ms
for the Groq agent's `plan_placement` call plus 874 ms for the Grok pick. It returned
`show_plan` and the spoken "One run under the wall cabinets, 2.43 metres. Three 1-metre strips,
cut the last."

### The first prompt got 3/6 right on grok-4.20

| # | Wrong answer | Fix |
|---|---|---|
| 2 | `along_edge e269`: a 6 cm LIMAP line that happened to be nearest the pointer | The pointer line now says "Pointer is on object o5 (cabinet_door)", and the prompt says to target the object id, never one of its edges, when the user names a cabinet, door, drawer or appliance |
| 3 | `along_edge o9e1`: an edge of `o9`, not the outer `o10` | Same fix |
| 5 | `measure p5`, which the code then rejected with "that's a surface" | `measure`/`along_edge` now accept a plane (its long axis) |
| 6 | `along_edge e482`: 4 "gutter hangers" on a 1.56 m ceiling edge | The summary lists the object labels and says "nothing else is known in this scan". The prompt says to answer `none` for anything not listed (a gutter, a window, a fence) |

This is the lesson of G3 call 4 again, in a milder form. The fast model is reliable at intent
(which planner, spacing, count, strip length) but guesses at geometry ids unless the summary
makes the right id the obvious one. Keep the summary opinionated: the pointer's object first, the
wall cabinets tagged, and the known labels listed.

## Demo steps

1. `SCENE_DIR=<repo>/scene uv run uvicorn server.app:app --host 0.0.0.0 --port 8000` (needs
   `GROK_API_KEY` in `.env`).
2. Open `http://<laptop>:8000/debug`. The site defaults to `kitchen`. Press **Plan** with "LED strip
   under these cabinets, it comes in 1 metre strips". The top-down SVG draws the 2.43 m red run
   along the wall cabinets, and the reply reads "Three 1-metre strips, cut the last."
3. Or through the agent: in the Command box (site set), "put LED strip under these cabinets, it
   comes in 1 metre strips" returns `show_plan`. "place them" then goes to the BOM path; select a
   part first, or it answers "nothing placed yet".
4. On the Quest, send `context.site` and `context.pointer` (the structure frame, X flip undone)
   with each command, and render `show_plan` as described in `docs/api.md` §5.

## Open issues

- **Pointer frame.** Unity has to un-flip X before sending `pointer`. This is untested on the
  headset.
- **Only the kitchen tested the geometry.** It has one wall of uppers. The per-wall grouping for L
  and galley kitchens and the 40 % fallback have no real-scene test.
- **`o18`** (a 7×16 cm `cabinet_door` 10 cm proud of the wall) is kept as a wall cabinet. It
  doesn't change the run, but it is S4 noise.
- **"Place them" on a run** needs a selected part to count, or it returns "nothing placed yet".
  There is no cut list for strips yet.
- **No offline path.** OFFLINE returns 503; the pick call isn't cached. It could cache on
  (site, utterance, pointer rounded) if the booth needs it.
- **`hanger_plan` exists twice** until `feat/grok-realtime-quartermaster` merges. Then
  `realtime.py` should import it from `plan.py`.
- **Groq as the pick model** is as accurate, but the free tier throttles back-to-back plans. On a
  paid Groq key it would be the faster, cheaper choice.
