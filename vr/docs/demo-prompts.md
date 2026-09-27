# Demo prompts: the whole app, by voice

*Sat 2026-09-26, e2e lane (`feat/e2e-demo`).*

These are 15 prompts that show measure → fit → buy in a drone scan, with the exact phrasing that works. Each prompt
lists the actions it exercises and one of three statuses.

- **Today** works with the demo server as it runs now (`:8004`, `app-handoff-vrnext`) and the app on
  `backend-integration`. Where a step needs **this branch** (`feat/e2e-demo`), it says so.
- **e2e patch** needs `docs/handoff/p4-e2e/0001–0003` applied to the server, then a restart.
- **Needs backend** needs server work that no patch here does.

**LLM-routed** means the server's fast path doesn't match the phrase, so the agent LLM (Groq `gpt-oss-120b`) picks the
tool. During the probes the LLM was rate limited (`KeysExhausted` → HTTP 500) and misrouted phrases: "measure it" became
a survey of the appliances in view, and "put it in there" became a new search. Rehearse these steps, or keep to the
fast-path phrasings.

How each routing claim was checked:
- **Server routing:** `agent._fast_route` on the patched tree, for each phrase, with `site` = kitchen and
  synthetic-facade-parts.
- **App handlers:** the app has a handler for every action named here. Grepped `[AgentAction]` plus the
  `AgentActions` switch.
- **The replace flow:** run for real against `:8005` (the patched server, real searches) with the kitchen's parts
  (dw1, bc1, fr1, rg1, served since 18:03). The in-headset part (tapes, models, fit colours) is the E2E harness's job at
  the Editor gate (below).

## The headline: replace the dishwasher

| # | Say (exactly) | What happens (actions) | Status |
|---|---|---|---|
| 1 | "**Take out the dishwasher.**" | `remove_component dw1`: the dishwasher's mesh and collider hide, the gap shows with "17¼ × 23¼ × 17½″ · estimated" | e2e patch (fast path). Today with this branch: the server only chats ("Pull the dishwasher out, disconnect…", no action), so the headset does it itself and says "Took out the dishwasher · on the headset" |
| 2 | "**Measure the gap.**" (or "measure it", "measure the dimensions of it") | `measure_cavity`: the real tape across the gap, height → depth → width, three notebook rows "Dishwasher gap height/depth/width" (one Undo removes all three) | e2e patch. Today with this branch: `:8004` answers with `survey {appliance, visible}`; the headset drops that and tapes the gap |
| 3 | "**The opening is 34 and a half inches tall.**" | `scale_gap h 0.8763`: the scene's scale is set from that tape (×1.48). Heads-up: "Scale set from the gap's height · 34½″ · ×1.48" | e2e patch. Today with this branch (local), unless the LLM answers with an action of its own. **Needed on the kitchen**: its scan reads ~1.47× small, so the gap is 437 × 591 × 445 mm and no real dishwasher fits it. See "The kitchen's scale" below |
| 4 | "**Find a dishwasher that fits.**" | `search_started`: the search goes out with the gap's W × H × D (`cavity`), every candidate is fitted on 3 axes, and the ones that fit come first. Find parts shows 3 cards | e2e patch (on `:8005`: "Three dishwashers. The first is an exact fit.", the Whirlpool WDP540HAMW). Today: LLM-routed `find_part`, fitted against the width tape only; the headset still shows the true 3-axis fit when the part is placed |
| 5 | "**Put it in there.**" | `place_part` (the first that fits) at the cavity insert, facing out, with its fit: green "✓ Fits the gap · 0″ spare" / amber "Tight in the gap" (≤ 5 mm over) / red "Too big for the gap · 1″ over" | e2e patch. Today with this branch (local; `:8004` sent a new search, which the headset drops) |
| 6 | "**Next one.**" ×2, "**Previous one.**", "**Show me option 1.**" | `cycle_model {index}`: the placement editor swaps the model where it stands (keeps a nudge), with the fit re-checked. Toast "Frigidaire FDPC4221AS · 2 of 3 · ✗ Too tall…" | e2e patch. Today with this branch (local, same `NextPlacedModel` / `ShowPlacedModel`) |
| 7 | "**Put it back.**" | `restore_component`: the model leaves the gap and the dishwasher returns | e2e patch. Today with this branch (local) |

One breath, e2e patch: "**Take out the dishwasher and measure the gap**", then "**The opening is 34 and a half inches
tall and find a dishwasher that fits**". The server returns both actions of each; this branch does the same on the
headset when the server doesn't.

The presenter page's new beat **Replace the dishwasher** (key `8`) runs steps 1–7 through the same command path, with
the kitchen's scale step. `AgentHarness.E2E(…)` runs it in the Editor.

## The headline, in one sentence (autonomy: `docs/handoff/p4-e2e/0004–0012`)

With 0004–0012 on the server, one sentence does all of the steps above. The server runs the job; the headset shows
"Replace the dishwasher · n of 7" on the heads-up line and says the result when it's done.

| # | Say (exactly) | What happens | Status |
|---|---|---|---|
| A1 | "**Replace the dishwasher with a new one that fits.**" | `job_started` “Replace the dishwasher” → `remove_component dw1` → `measure_cavity` → (`scale_gap` only when uncalibrated) → `search_started` → `place_part` (fits or tight only) → `job_done`, spoken: “Put a Frigidaire FDPC4221AS in the dishwasher gap, 74 mm to spare. Say next one to see the other two.” | Fast path, no LLM. 1.7 s on `:8005` (cached search) |
| A2 | "**Swap out the dishwasher for one that fits.**", "**The dishwasher is broken, find me a new one and put it in.**", "**Take out the dishwasher and put in a new one.**" | The same job | Fast path |
| A3 | "**Replace the range with a 30-inch induction one under $1,200.**" / "**Remove the range. Find a 30-inch induction range under $1,200 that fits.**" | `show_limits $1,200` + the job on rg1: search “30 inch induction range”, fitted to the range's gap. If nothing under $1,200 fits, it picks the best fit and says it's over | Fast path. About 20 s (a live search) |
| A4 | "**My old stove died, can you sort me out with an induction one?**", "**The fridge is too small for us, get us a bigger one.**" | Grok picks `replace_component` (0.7–1 s), then the same job. For the fridge, "bigger" found 36–60 in fridges, none fit, so it searched "30 inch fridge" and put in an LG LTCS20020S (37 mm to spare, its model generated in 2 s) | LLM-routed (`LLM_AGENT=xai:grok-4.20-0309-non-reasoning`). About 50 s |
| A5 | "**Next one.**", "**Show me option 3.**", "**Undo.**", "**Put it back.**" | `cycle_model` / `undo_edit` / `restore_component` | Fast path |
| A6 | "**Show me the gym model.**" | `show_model {site: zabel-gymnasium}` (it used to search for “gym models that fit the gap”) | Fast path |

## Fourteen more

| # | Say (exactly) | Capabilities / actions | Status |
|---|---|---|---|
| 8 | "**Remove the range, find a 30-inch induction range under $1,200 that fits, put it in.**" Then "**Put it in there.**" when the cards are up | Purchase limits (`show_limits` ≤ $1,200: checkout refuses more), `remove_component rg1`, the gap-fitted search; "put it in" answers "Say put it in when the options are up" | e2e patch only. **Today's server reads "$1,200" as a $1 limit** (fixed in 0002). rg1 is open-topped (no height tape). At ×1.48 its gap is ~766 × 1176 × 653 mm: a 30″ range is 762 wide (fits by ~4 mm), but most are deeper than 653 mm and will show red. Untested live |
| 9 | "**Take out the fridge and find a counter-depth fridge that fits.**" Then "**Put it in there.**", then "**Show it in stainless.**" | remove + gap-fitted search in one reply; `place_part`; F11 `set_finish` (a server-rendered finish, `model_url`) | e2e patch + Today (the finish). Say the finish **on its own**: in the same breath F11's matcher takes the whole utterance. fr1's gap at ×1.48 is ~730 × 1746 × 805 mm, so a 24″ counter-depth fits and a 30″ doesn't. Untested live |
| 10 | "**What am I looking at?**" | F16 `label_view` (fast path; the app sends the view's photo): tappable labels, a tap searches that part | Today (kitchen) |
| 11 | "**Survey the facade.**" Then pinch the red pin, or "**Find a fix for pin f1.**" | F10 `survey_condition` → `show_survey` pins (severity colours; the reply names the worst); `fix_pin` → `search_started` + `add_note` | Today (hospital-bg / Zabel; xAI). The seed "…and find a fix for the worst problem" runs the survey only: the fast path takes the first phrase |
| 12 | "**Measure every cabinet door.**" → "**Which one is the widest?**" → "**Find cabinet hinges under $40.**" → "**Buy it.**" | The agent tapes 18 doors with the real tape (`survey` → `/agent/observe` → `show_tape_survey`), `survey_query`, limits + search (LLM-routed), `start_checkout` (opens the hold-to-pay panel only: the judge holds Pay) | Today (kitchen). The seed "…and order matching hinges under $40" sets the limit and runs the survey; "order matching hinges" is dropped |
| 13 | "**Is this gutter sloped enough to drain?**" → "**Find a gutter hanger.**" → "**Every 60 cm.**" | `check_slope` (a real 2-point tape along the gutter edge, verdict + note); search; `place_array` (⌊L/s⌋+1 hangers, one undo) | Today on the built-in facade. On a scanned site "every 60 cm" goes to the LLM planner (F7). The seed "check the gutter slope and find hangers every 60 cm" is LLM-routed (the word "find" keeps it off the slope path) |
| 14 | "**LED strip under these cabinets.**" → "**Place them.**" | F7 `plan_placement` → `show_plan` (dashed runs, ghosts, lengths) → `place_plan` → `place_array` | Today, LLM-routed (kitchen, aim at the cabinets) |
| 15 | "**Reimagine this with navy cabinets.**" → "**Darker.**" → "**Undo.**" → "**Walk me into it.**" | F2 `show_reimagined` on the capture camera's quad; F18 refine / undo (fast path while it's on screen); F8 walk-in clip (`flythrough_started` → `show_video`) | Today (kitchen; reimagine and the walk-in are LLM-routed, xAI imagine) |
| 16 | "**Do the whole job.**" | F15 `run_job`: survey → part → safety + rules + postcard → packet → checkout panel, on the job rail (`job_started` / `job_step` / `job_done`) | Today (fast path; needs a loaded site or a named goal) |
| 17 | "**Is it recalled?**" | F6 `check_safety` → `show_safety` (RECALLED pill on the spec card and the seller panel) | Today (fast path; on the selected part) |
| 18 | "**Send it to my contractor.**" | F14 `send_packet` → `show_packet` (a QR code of the job packet PDF) | Today (fast path; needs notebook entries) |
| 19 | "**Teach me to install this.**" → "**Check it.**" → "**Next.**" | F17 install coach: `coach_started`, `coach_step`, `coach_check` from the view's photo, the drill crosshair and the stop card | Today (fast path; needs a selected part with a manual) |
| 20 | "**Show me the gym model.**" | Model view with the Zabel gym on the table (`show_model`, merged app side) | Needs backend: no server emits `show_model` (routes to the LLM, which has no such tool). By hand: ring ▸ Model view ▸ the Zabel card |

## The kitchen's scale

The kitchen's parts file reads the dishwasher gap as 437 × 591 × 445 mm. The backend README says the scan is about
1.47× small indoors (its scale comes from the drone's altitude caption). At 1.0, every dishwasher is red ("too tall by
298 mm"), so steps 3–6 need the scale first.

What we chose, as honest and repeatable: **set the scale from the opening's standard height**, 34½″ under a 36″
counter, with the gap's own height tape. That gives ×1.48, which agrees with the team's 1.47. The gap becomes
648 × 876 × 659 mm (a standard 24″ opening is 24–24¼″ wide × 34½″ × 24″), and three real dishwashers come back: one
fits and two are too tall by 13 mm.

The heads-up line always says the assumption: "Scale set from the gap's height · 34½″ · ×1.48".

A real tape of the counter height (Scene window ▸ Set scale, 36″) works too, and is better when the judge can see the
counter.

## Where the phrase set lives

- **App:** `Runtime/Agent/LocalIntents.cs`. Whole-utterance phrases only, fillers allowed ("okay", "please", "hey
  grok"), 2–4 joined by and / then / commas. Tests: `Tests/EditMode/E2EFlowTests.cs`, 101 phrase cases + 9 arbitration.
- **Server:** `server/replace.py`, the same phrases. Tests: `tests/test_replace.py`, 51.
- The app acts only when the reply has no action for the phrase, or only contradicting ones. Those are dropped:
  - a survey, for "measure the gap" / "find one that fits" / the scale;
  - a new search or "select candidate", for "put it in there" / "next one" / "option N".
- A running coach keeps "next", and Model view (tabletop) keeps "next model".
