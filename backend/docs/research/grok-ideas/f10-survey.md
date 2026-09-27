# F10: drone condition survey

Feature agent F10, 2026-09-26. Built pick 1 of [G4](g4-round3.md) ("The three to build first"):
Grok vision over a scene's capture frames, with findings pinned on the mesh. Branch
`feat/grok-condition-survey`.

**Licence.** The Zabel and Hospital footage is B1's, and B1's licence notes for it aren't on
`main`. Treat the image below and any survey output on these scenes as **internal review
only** until the licence is checked.

**Bottom line.**
- It works end to end on B1's Hospital scene. Six 1280 px frames → one
  `grok-4.20-0309-non-reasoning` call → **$0.0097, 7.7 s** → 4 pins on the collision mesh.
  One pinch ("find a fix for pin f1") then found a real SBS roof cap sheet at $139.
- Zabel (the red-tile school) came back all "looked fine", 11 findings and no pins, for
  **$0.0099 in 8.2 s**. That matches G4 call #1.
- `grok-4.7` at low effort on 3 frames cost **$0.0101 in 7.3 s**, much faster than G4's 34 s
  through the Responses API. It gave tighter, more specific findings ("many facade windows
  broken"), but one box covered most of the frame.
- G4's open question is answered: with the explicit 0–1000 example in the prompt, 4.20
  non-reasoning answered on **0–1000 in all 3 runs** (G4's 0–100 was the short wording). The code
  still accepts 0–100.
- The 3D pins are consistent across views. Pins cast from one frame land on the same roof and
  facade when projected back into a different frame (image below, middle panel).
- Total live spend: **$0.0388** in 4 calls, against a $0.30 cap.

---

## 1. What was built

| Piece | Where |
|---|---|
| Frame pick, Grok call, box scales, rules, projection, merge | `server/survey.py` |
| `LLM_SURVEY` (`xai:grok-4.20-0309-non-reasoning`) and `LLM_SURVEY_CAREFUL` (`xai:grok-4.7`) roles | `server/llm.py`, `server/config.py` |
| `POST /scene/survey`, `GET /scene/survey/{id}` | `server/app.py` |
| A small background task table (`start_task`, `get_task`, `wait_task`) | `server/jobs.py` |
| Agent tool `survey_condition`; "find a fix for pin f1" → `find_part` + a notebook entry; actions `survey_started`, `show_survey` | `server/agent.py` |
| `/debug` section: the frames with boxes, the pin list, gaps and "looked fine" | `server/static/debug.html` |
| Contract | `docs/api.md` (endpoints, actions, context `site`/`survey_id`, limits) |
| 10 offline tests; fixture: the kitchen collision mesh trimmed to 4.3k triangles plus 2 cameras (112 KB) | `tests/test_survey.py`, `tests/fixtures/survey/kitchen/` |

**Flow.**
1. `pick_frames` groups the cameras by band (roof: pitched more than 30° down; facade: less than
   15°) and by view-side octant. It takes up to 6 frames round robin, roof groups first, evenly
   spaced within each group. On Hospital (all 65 cameras at 37–39°) it took 2 frames from each of
   3 sides. On Zabel it took 3 roof and 3 facade frames.
2. Each frame is read from `survey/<id>.jpg` if the package has one, else from the 640 px
   `thumbs/<id>.jpg`, and sent at a 1280 px long edge with `detail: high`.
3. One `llm.chat` call with a strict `json_schema`. `frame` is an enum of the ids sent, so the
   model can't invent a frame. The call uses `max_retries=0`, because a timed-out Grok call may
   still bill. The cost comes from `usage.cost_in_usd_ticks` (1e10 = $1) and is logged.
4. The raw reply is cached (`cache.cached("survey", {site, rev, frames, model, focus})`). The
   rules, projection and merge run locally on every request, so a rule change never costs a call.
   OFFLINE serves cached surveys and returns 503 on a miss.
5. Rules, in code:
   - drop confidence < 0.5;
   - list `severity: none` as "looked fine" and never pin it;
   - hail/impact → `suspected: true`, `inspect_closer`, "(suspected, verify on the roof)";
   - keep `part_query` only for `repair`/`replace`;
   - accept boxes on 0–1000, 0–100 or 0–1, chosen per reply;
   - clamp boxes and swap reversed corners.
6. `to_pin`: rays `Rᵀ K⁻¹ [u·w, v·h, 1]` from `position` through the box centre and the 4 inner
   25/75 % points against `collision.r<rev>.glb` (trimesh, cached per path). The pin is the
   per-axis median hit, or `null` if all miss.
7. Merge: worst first. A pin of the same element (`flat_roof_membrane` counts as
   `roof_covering`) closer than the radius folds into the kept pin's `also_in`. The radius is
   1.5 m on metric scenes; on unscaled scenes (`scale_method: none`, like all of B1's) it is
   **15 % of the collision bbox diagonal**.

---

## 2. Live results (4 calls, $0.0388)

Scenes: B1's `hospital` and `zabel` packages (rev 2), copied read-only from hfbox
(`/workspace/airtools/scene/b1/*`) to a scratch `SCENE_DIR`. The picked frames came from
`work/b1/<site>/frames` (1920×1080), downscaled into a scratch `survey/` folder. Nothing on hfbox
was changed. Every call went through the real server (`POST /scene/survey`, or
`/agent/command` for the last one).

| # | Scene, model, frames | Result | Cost | Time |
|---|---|---|---|---|
| 1 | Hospital, 4.20 NR, 6 picked (`all`) | 10 findings, all on the 0–1000 scale. Roof `severe` in all 5 roof views ("extensive deterioration and plant growth"), facade in 4 (3 `severe`: "crumbling concrete and exposed rebar"), vegetation `moderate`. → **4 pins**: the 5 roof findings merge into f1; the facade gives 2 pins, one per wall (0.16 units apart); f4 vegetation. Coverage gaps: "side and rear elevations not fully covered" | **$0.00967** | 7.7 s |
| 2 | Zabel, 4.20 NR, 6 picked | 11 findings, all `none`: tile roof, skylights, chimney, flashing, dormers, brick facade, windows, gutters, trees. → **0 pins**; "Nothing wrong that I can see in the footage." It again named a `flat_roof_membrane` (G4 saw the same; it is metal edging), rated fine and harmless. Its gaps include "close-up views of potential hail damage", which is right | **$0.00988** | 8.2 s |
| 3 | Hospital, grok-4.7 low, 0001/0221/0291 | 4 findings: `flat_roof_membrane` severe 0.93 ("membrane largely missing or failed; exposed substrate, ponding stains"), `window` severe ("many facade windows broken"), `facade` moderate, `vegetation` moderate. Better wording and elements than 4.20. But the membrane and vegetation boxes cover most of frame 0221 (`[290,160,770,700]`), so they are no tighter than 4.20's here | **$0.01014** | 7.3 s |
| 4 | Hospital, 4.20 NR, `focus: roof`, via "survey the roof" on `/agent/command` | Actions `survey_started` then `show_survey` in one reply; 4 pins. The roof splits into 2 roof pins (0.2 units apart, beyond the 0.12 radius) plus `flat_roof_membrane` and vegetation. "find a fix for pin f1" → `find_part("flat roof membrane replacement materials")` → Liberty SBS self-adhering cap sheet, $139, plus an `add_note {pin_id: f1, frame_id: 0001}` | **$0.00915** | 6.0 s |

Replays of all 4 from the cache cost $0. With `OFFLINE=true`, `POST /scene/survey` on a cached
survey returns `done` at once; an uncached one (Zabel `careful`) returns 503; the agent's "survey
the roof" still answers from the cache.

![Hospital: Grok's boxes, and the 3D pins cast onto collision.r2.glb and projected back into each frame. Left: frame 0001 (4.20 NR boxes). Middle: frame 0291, where the pins made from the other frames still land on the roof and the facade. Right: grok-4.7's boxes and pins on 0221. Internal review only: B1 footage licence unverified.](f10/survey-pins.jpg)

**Projection checks.**
- `position == -Rᵀt` holds to 1e-16 on Hospital, Zabel and the kitchen.
- Centre rays hit `collision.r2.glb` on both building scenes.
- In the image, pins projected back through a *different* camera sit on the element they name
  (middle panel). That is the check that matters for Unity.
- On the kitchen fixture, a box's pin re-projects to within 2 % of the box centre (test).
- Facade pins sit a little up the wall from the box centre: the 5-ray median mixes roof-edge and
  wall hits on a coarse 50k-triangle collision mesh.

**Merge radius.** At G4's suggested 5 % of the diagonal (0.04 units), Hospital run 1 gave 8 pins,
5 of them the same failed roof. At 15 % (0.12) it gives 4, and the two facade walls stay
separate. That is tuned on one building; once the P2 tape gives B1's scenes a metric scale, the
1.5 m path applies.

---

## 3. Demo

1. Put a full scene package under `SCENE_DIR` (`cameras.r<rev>.json`, `collision.r<rev>.glb`,
   `thumbs/`). For sharper findings, add `survey/<id>.jpg` frames at ≥ 1280 px for the ids
   `survey.pick_frames` chooses.
2. `uv run uvicorn server.app:app`, open `/debug`, type the site (e.g. `hospital`) in the
   **Condition survey** box and press **Survey**. About 8 s later it shows the frames with boxes,
   the pin list and "$0.0097". Press it again: "$0 (cached)".
3. Or, with the same site in the box, type "survey the roof" in **Command**: the reply is the
   spoken line, with `survey_started` and `show_survey` actions. Then "find a fix for pin f1"
   starts the part search.
4. To show it offline, run the survey once online, then restart with `OFFLINE=true`.
5. Say the numbers: "a cent to survey this roof, 8 seconds".

---

## 4. Open issues

- **Frame source.** The pipeline doesn't export `survey/` yet (G4's one-line change in
  `pipeline/package.py`). Without it the survey sends the 640 px thumbs, which were not tested
  live. Left to the pipeline owner.
- **Intrinsics vs frames.** B1's camera records carry the undistorted size (1884×1059 on
  Hospital; the frames are 1920×1080), so a 0–1 box maps with an offset of up to about 2 % at the
  edges. That is fine for region pins.
- **Region boxes.** Both models box regions, and grok-4.7's are not always tighter (call 3).
  The pins say "this roof", not "this tile".
- **Merge radius on unscaled scenes** is a share of the bbox diagonal, tuned on one building.
- **No `warm.py --survey <site>`.** Running the survey once online warms it (step 4 above).
- **"Find a fix for that one"** (with no pin id) isn't understood. The pinch sends the pin id,
  which is.
- **Survey tasks** live in memory, like search jobs. After a restart,
  `GET /scene/survey/{id}` is 404, but a new POST is served from the cache for $0.
- No `-m live` test: it would need B1's scene package, which is only on hfbox.
