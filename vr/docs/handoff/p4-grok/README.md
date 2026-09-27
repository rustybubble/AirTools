# Backend hand-off 3: the app lane's 11 patches, rebased onto `integration/grok-all`

*Sat 2026-09-26, app lane → P4. Replaces `docs/handoff/p4/` (0001–0008) and `docs/handoff/p4-b1-b3/`
(0009–0011) for anyone running the Grok branch. Those two folders still apply to `main` @ `715d119`.*

`integration/grok-all` @ `c706f13` (the 20 Grok features, 1027 tests) had none of our 11 patches:
`/agent/observe`, `/checkout/prepare`, `/commerce/limits` and `check_slope` were all missing.
This folder is the same series, rebased onto that branch, conflicts resolved so both sides keep
working. We made and tested it in a private scratch clone. Nothing changed in your repo or on your
running `:8000` server, and nothing was pushed. No test calls an LLM or the network.

The second half of this file is the **app-side contract delta**: what changes for the Unity side
compared with the p4-b1-b3 contract.

## How to apply

```sh
cd airtools-drone-backend
git switch -c app-handoff-grok origin/integration/grok-all     # c706f13
git am /path/to/AirTools/docs/handoff/p4-grok/*.patch            # 0001–0011
uv sync --extra pipeline
uv run pytest -q
```

- All 11 patches apply cleanly with `git am` on `c706f13` (checked with `git apply` into a clean
  index, one patch at a time).
- The result is byte-identical to the tested tree (tree `2cbce9f7`, commit `a38e415` in the
  scratch clone).
- No new dependencies. No new env vars beyond `REQUIRE_HOLD_PROOF` (0010, default `false`).
- Restart uvicorn afterwards.

## Patches

"Clean" means `git am -3` applied it without help. Every rebased commit's message ends with a
"Rebased onto integration/grok-all" paragraph that says what changed.

| Patch | Rebase | What changed while rebasing | Tests |
|---|---|---|---|
| `0001-fix-open-checkout-…` | conflicted (`agent.py` imports, `_CLIENT_ONLY_TOOLS`, `start_checkout`; the `api.md` row) | **F6 had already made `start_checkout` server-side** (the recall warning, once per part per session), so our "leave `_CLIENT_ONLY_TOOLS`" hunk was dropped as already present. The rest is merged, not replaced: our seller pick (named, else cheapest/fastest, else `recommended_seller`) and our errors (`no part selected`, `no seller to check out with`), plus F6's warning, which now names the seller when the server picked it. With no recommendation computed, the first seller is picked, as in F15's pay step. The action carries only `seller_index`. See decision 3 for the reply rule | `test_agent.py`: 22 + 2 new; `test_safety.py`: +1 ("buy it" on a recalled part) |
| `0002-fix-say-fits-the-run-…` | clean | none | `test_jobs.py`: 1 |
| `0003-fix-cap-vision-output-…` | conflicted (`llm.chat()`) | `chat()` now takes a per-call `max_retries` upstream. The vision cap sits next to it | `test_llm.py` 1, `test_vision.py` 1 |
| `0004-fix-return-503-…` | conflicted (`app.py` imports) | uses `openai.RateLimitError`, because `app.py` already imports `openai`. The Grok routes that catch `openai.APIError` themselves are unchanged: F16 labels (502) and imagine/labels' own `RateLimited` (429) | `test_app.py`: 2 |
| `0005-fix-read-Home-Depot-s-…` | clean | none | `test_sellers.py`: 3 |
| `0006-feat-sizes-printed-in-the-listing-title-…` | clean | none | `test_sellers.py` 23, `test_search.py` 7 |
| `0007-feat-scene_pin-carries-…` | clean | none | existing `test_agent` case extended |
| `0008-docs-receipt-status-…` | conflicted (`api.md`) | our receipt paragraphs follow F5's `postcard_url` paragraph; both are kept | none |
| `0009-feat-the-agent-measures-with-the-headset-s-tape-…` | conflicted (add/add on `server/survey.py` and `tests/test_survey.py`; `agent.py`; `app.py`; `api.md`) | F10 already owns `server/survey.py`, `_SURVEY_RE`, `survey_condition` and `show_survey`. So we renamed: **`server/tape_survey.py`**, **`tests/test_tape_survey.py`**, `_TAPE_SURVEY_RE`, `Session.tape_survey`, and the card action **`show_tape_survey`** (decision 1). The "survey" priority is explicit (decision 2). `check_slope` now sits after the Grok stateless matchers, and `survey_query` is a stateful row. `survey` and `check_slope` joined the single early-exit tuple. The LLM `survey` tool says "sizes only", and points condition checks to `survey_condition` | `test_tape_survey.py`: 60 + 25 new (23 routing cases for both phrasings, F10's unchanged args, both surveys end to end on one site) |
| `0010-feat-measured-mandate-checkout-…` | conflicted (`checkout.py`, `app.py` `/checkout`, `api.md` flow) | `checkout.authorize` takes both F6's `safety_verdict` and our `mandate`. The receipt keeps F6's verdict and F5's `postcard_url`, and adds `mandate`. `/checkout` keeps the `bom_id` notebook entry and F9's manual prefetch | `test_mandate.py`: 36 + 1 new (the proof receipt keeps the Grok fields) |
| `0011-feat-voice-narrows-the-purchase-limits-…` | conflicted (`agent.py`, `api.md`, `test_mandate.py` imports) | The limits pre-pass runs before `_fast_route`, and the rest of the command goes back through `_handle_command`. It **skips rules and quote commands** (decision 4). `set_limits` joined the early-exit tuple. Our `Session.id` hunk was dropped: F14/F15 had already added it | `test_mandate.py`: 17 + 3 new |

**Dropped as already present:** no patch was dropped whole. Two hunks were dropped because
upstream already had them:
- 0001's `start_checkout` leaving `_CLIENT_ONLY_TOOLS` (F6);
- 0011's `Session.id` (F14/F15).

**Docs:** `docs/integration-grok-features.md` keeps its fast-path priority table true and gains
our phrases:
- row 0, the limits pre-pass;
- row 4, `stop_survey`, the tape `survey`, and the rule that decides between the two surveys;
- row 5, "buy it" / "check out";
- row 10, `check_slope`;
- row 14, "which is the widest?".

The existing row numbers and the text that refers to them are unchanged. `docs/api.md` has the
renamed action, the context rows (their `site` row extended, our `scale` row added), the
priority paragraph, and the "survey" rule.

### Test results

Same macOS machine, `uv sync --extra pipeline`, `uv run pytest -q` after every patch:

| After | passed | failed | skipped | deselected |
|---|---|---|---|---|
| `integration/grok-all` `c706f13` (baseline) | 1026 | 1 | 2 | 9 |
| 0001 | 1051 | 1 | 2 | 9 |
| 0002 | 1052 | 1 | 2 | 9 |
| 0003 | 1054 | 1 | 2 | 9 |
| 0004 | 1056 | 1 | 2 | 9 |
| 0005 | 1059 | 1 | 2 | 9 |
| 0006 | 1089 | 1 | 2 | 9 |
| 0007 | 1089 | 1 | 2 | 9 |
| 0008 | 1089 | 1 | 2 | 9 |
| 0009 | 1174 | 1 | 2 | 9 |
| 0010 | 1211 | 1 | 2 | 9 |
| 0011 | 1231 | 1 | 2 | 9 |

- The single failure is the known macOS-only one, in both the baseline and the result:
  `tests/pipeline/test_structure_layer.py::test_lines_only_when_planes_fail`
  (`os.sched_getaffinity`). Your integration doc's 1027 is this same count on Linux.
- Every upstream test still runs unchanged. The only upstream assertion we touched is the
  `scene_pin` args in `test_agent.py`, which 0007 extends with `label` (as in the first hand-off).
- `ruff check` is clean after every patch.
- `ruff format --check` flags the same 5 files as the baseline: 3 research docs, `server/search.py`
  and `tests/test_jobs.py`. They were already unformatted upstream, and we left them alone.

### Live smoke test (rebased server, `OFFLINE=true`, port 8004, `SCENE_DIR` = your `scene/`)

The server ran with `DATA_DIR` set to a copy of your `data/`. All four responses are verbatim.

1. `POST /agent/command` `{"session_id":"smoke-1","text":"measure every cabinet door","context":{"site":"kitchen"}}`
   ```json
   {"reply":"Surveying 18 doors.","actions":[{"name":"survey","args":{"label":"cabinet_door","where":"all","measure":"size","request_id":"sv-87a05901"}}],"job_id":null}
   ```
2. `POST /agent/command` `{"session_id":"smoke-1","text":"survey the roof","context":{"site":"kitchen"}}`. This took F10's
   path: `survey_condition` → `_survey`. It has no cached survey offline, and the local scenes
   have no drone scan.
   ```json
   {"reply":"Can't do that yet: the survey needs the uplink.","actions":[],"job_id":null}
   ```
3. `POST /checkout/prepare` `{"session_id":"smoke-3","part_id":"amerimax-home-products-21812-846830","seller_idx":0,"units_needed":8,"bom_id":null,"bom_lines":[],"evidence":[{"notebook_id":3,"label":"tape #3","value_m":4.2,"camera_id":316,"photo":"/scenes/synthetic-facade/thumbs/0316.jpg","array":{"spacing_mm":600,"count":8}}]}`
   ```json
   {"cart":{"id":"cart-8791bf64","intent_id":null,"lines":[{"part_id":"amerimax-home-products-21812-846830","seller_idx":0,"seller":"Home Depot","units_needed":8,"pack_qty":1,"packs":8,"unit_price_usd":2.74,"shipping_usd":0.0,"line_total_usd":21.92}],"bom_id":null,"bom_lines":[],"shipping_usd":0.0,"total_usd":21.92,"evidence":[{"notebook_id":3,"label":"tape #3","value_m":4.2,"camera_id":316,"photo":"/scenes/synthetic-facade/thumbs/0316.jpg","array":{"spacing_mm":600.0,"count":8}}],"created_at":"2026-09-26T15:16:34.825255Z"},"cart_hash":"sha256:b40cf1042e84f29cf92ffc8a8af7603feb3338fe56ff9b93b5315eaf898047e6","hold_nonce":"hn_MW6AYqu3ng3V1byjnX5e5NNCPxxixhM-AT1q5wO20c0","nonce_expires_at":"2026-09-26T15:18:34.825255+00:00","intent":null,"checks":[{"id":"price_reread","status":"ok","ok":true,"detail":"$2.74 × 8 from the saved listing"},{"id":"within_limit","status":"ok","ok":true,"detail":"no budget limit set"},{"id":"delivery","status":"ok","ok":true,"detail":"arrives Wed Sep 30; no deadline set"},{"id":"qty_evidence","status":"ok","ok":true,"detail":"tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)"},{"id":"seller_verified","status":"ok","ok":true,"detail":"Home Depot: structured seller listing"},{"id":"fit","status":"warn","ok":true,"detail":"fit not checked against a measurement"},{"id":"card","status":"ok","ok":true,"detail":"Visa test card •••• 1111, held by the server"}],"all_ok":true}
   ```
   Holding for 1043 ms with that nonce, `POST /checkout` returned `OFFLINE_RECEIPT` with
   `safety_verdict: "unknown"` and the full `mandate` block.
4. `POST /agent/observe` `{"session_id":"smoke-4","request_id":null,"kind":"slope_result","target":"gutter","run_m":4.2,"fall_mm":0.4,"low_end":[2.1,6.2,0.03],"gravity_residual_deg":0.0,"uncertainty_mm":2.0,"notebook_id":15,"tts":false}`
   ```json
   {"reply":"Flat: 0 ± 2 mm of fall over 4.20 m. It needs about 9 mm, so water will pond.","actions":[{"name":"add_note","args":{"text":"Gutter slope: won't drain (0.4 ± 2 mm of fall over 4.20 m; needs 8.7 mm)"}}],"job_id":null}
   ```

### Decisions (please confirm or override)

1. **Our card action is renamed `show_survey` → `show_tape_survey`.** The args are unchanged:
   `{request_id, label, groups, unverified, skipped, focus}`.
   - On your branch, `show_survey` is F10's `{survey_id, pins, label}`, emitted by
     `survey_condition` and by F15's run-job survey step. `docs/api.md` keeps one action table
     where one name has one shape; two shapes under one name would be the most surprising thing
     for anyone reading that table or writing a client.
   - Renaming F10's action instead would touch F10, F15, `/debug` and their tests. Renaming ours
     touches one server function and the app's dispatch.
   - The app can already sniff `pins`/`survey_id` vs `groups` (the `feat/grok-g2-overlays` app
     branch does). But the app's live `backend-integration` branch still sends every
     `show_survey` to the tape card, so F10's pins would open an empty "0 objects, 0 sizes" card.
     An explicit name removes the guesswork. The app changes are listed in the contract below.
2. **What "survey" means, by the thing surveyed** (row 4 of the priority table):
   - A bare "stop" / "cancel the survey" is `stop_survey`. This check runs first, because F10's
     matcher takes any "survey".
   - Next comes `fix_pin`.
   - Then the tape `survey`: "measure / survey / size up" + every / all (the) / each / the
     (+ one qualifier word) + a structure-object noun (cabinet door(s), door(s), drawer(s),
     window(s), panel(s), appliance(s)), with no condition word in the sentence.
   - Everything else with "survey" is F10's `survey_condition`. That covers roof, gutter(s),
     facade and building, and any sentence with a condition word: condition, damage(d/s),
     crack(s/ed), rot/rotten/rotting, rust/rusty/rusted/rusting, leak(s/y/ing).
   - So: "survey every cabinet door" / "survey the facade windows" → tape. "Survey the roof",
     "survey the gutter slope", "survey the windows for damage", "survey the condition of the
     windows" → F10.
   - `check_slope` ("is this gutter sloped enough to drain?") now runs after the Grok stateless
     matchers. "Show me the report on the gutter slope" stays `make_report`, and "any rebates for
     gutter drainage?" stays `check_rules`.
3. **`start_checkout` replies.** When the server picks the seller (a null `seller_index`: the
   "buy it" fast path, or the model passing null), the reply names that seller, e.g. "Hold-to-pay
   panel's open for Home Depot." That line is `spoken`, so F6's verbatim rule says it; the model
   can't know which seller was picked. When the model passes an explicit index, the model's own
   reply stands, as in F6.
   - On a recalled part: "Heads up: … The pay panel's open for Home Depot if you still want it."
     This is said once per part per session.
   - This changed one of our own test expectations. The null-index LLM case used to keep the
     model's text.
4. **Voice limits skip rules and quote commands.** The pre-pass would have read "any rebates up to
   $500 on a mini-split?" (F13) and "check this quote, is it under $2000?" (F20) as purchase caps.
   It now ignores money in any command that matches `_RULES_RE` or `_QUOTE_RE`. Everywhere else
   it still runs first: "do the whole job, under $500" sets the limit and then starts F15.
5. **App-lane tape lines in the early-exit rule.** `survey`, `check_slope` and `set_limits`
   joined your single early-exit tuple (`_OWN_LINE_TOOLS`), and they follow its reply order: the
   tool's line first, then the model's text. When one turn calls several of ours, their lines are
   joined in call order; a template `survey_query` answer is final. Turns without our tools behave
   exactly as before.
6. Unchanged from the earlier hand-offs:
   - `REQUIRE_HOLD_PROOF` defaults to `false`.
   - Survey and nonce state lives in memory.
   - Kitchen doors come out as 8 size groups.
   - TAP signing is still not done.

---

# APP-SIDE CONTRACT DELTA (vs `docs/handoff/p4-b1-b3/README.md`)

Everything in the p4-b1-b3 contract still holds except the items below.

## 1. `show_survey` → `show_tape_survey` (rename, same args)

`POST /agent/observe` (`survey_result`) and the "which is the widest?" follow-up now answer with:
```json
{"name": "show_tape_survey", "args": {"request_id": null, "label": "cabinet_door",
  "groups": [{"w_mm": 262, "h_mm": 278, "count": 2, "ids": ["o0", "o1"]}, {"w_mm": 208, "h_mm": 525, "count": 1, "ids": ["o6"]}],
  "unverified": ["o1"], "skipped": ["o9"], "focus": ["o6"]}}
```
On this server, `show_survey` means F10's condition survey only:
`{"name": "show_survey", "args": {"survey_id": "<12 hex>", "pins": [...], "label": "AI triage from drone frames, not an inspection"}}`,
usually right after `survey_started {survey_id}`.

**What the app must change** (line numbers are on `backend-integration`):
- `Assets/AirTools/Runtime/Agent/AgentActions.cs:31`: add `"show_tape_survey"` to `Known`.
- `Assets/AirTools/Runtime/Agent/AgentActions.cs:121`: add `case "show_tape_survey": ok = AppCommands.ShowSurvey(a.args);`.
  Keep `case "show_survey"` for servers that still run the p4-b1-b3 patches, but send it to the
  card only when `args["groups"]` is present. Otherwise it is F10's: send it to F10's pins
  handler, or refuse it quietly.
- On `feat/grok-g2-overlays`: `Assets/AirTools/Runtime/Agent/Grok/GrokOverlayActions.cs:47–53`
  (`[AgentAction("show_survey")]` with `SurveyView.IsCondition`) can stay as it is. It also needs a
  `show_tape_survey` route, either the switch case above or `[AgentAction("show_tape_survey")]` →
  `AppCommands.ShowSurvey`. Handlers run before the switch.
- `Assets/AirTools/Tests/EditMode/BackendContractTests.cs:123`: add `show_tape_survey` to the
  expected `Known`.
- `Assets/AirTools/Runtime/Dev/BackendHarnessB1B3.cs:185–187`: look for `show_tape_survey`, or
  accept both names.
- `tools/mock_parts_server.py:784–785` (`show_survey_action`): emit `"show_tape_survey"`. Also
  update the docstrings at `:36`, `:41` and `:416`, and the expectations at
  `tools/test_mock_parts_server.py:371` and `:411`.
- Comments only: `SurveyCard.cs:12,35`, `AppCommands.cs:278`, `AgentClient.cs:140`,
  `MeasureTool.cs:36,709,724`, `MainSceneBuilder.cs:585`, `docs/SPEC.md:531`.

## 2. Phrases that now route differently

| Command | Before (p4-b1-b3 server) | Now |
|---|---|---|
| "survey the roof / gutters / facade / building / kitchen" | LLM | F10 `survey_condition`: `survey_started` (+ `show_survey` pins). Offline without a cached survey: "Can't do that yet: the survey needs the uplink." |
| "survey every window for damage", "... condition" | tape `survey` | F10 `survey_condition` |
| "survey the gutter slope" | `check_slope` | F10 `survey_condition` (gutters) |
| "show me the report on the gutter slope", "any rebates for gutter drainage?" | `check_slope` | `make_report` / `check_rules` |
| "any rebates up to $500?", "check this quote, is it under $2000?" | `set_limits` + … | no limit set; F13 / F20 |
| "buy it" on a recalled part (cached verdict) | "Hold-to-pay panel's open for X." | "Heads up: … The pay panel's open for X if you still want it." (once per part per session) |
| "measure every cabinet door", "stop", "is this gutter sloped enough to drain?", "which one is the tallest?", "Find hangers, under $40, arriving by Friday." | — | unchanged |

## 3. Additive fields (no app change needed)

- The receipt carries `safety_verdict` (F6) and, when a postcard exists, `postcard_url` (F5),
  next to `mandate`.
- F15's run-job `start_checkout` carries `{seller_index, part_id}`.
- F10's `add_note` carries `{text, pin_id, frame_id}`.
- Context gains optional `survey_id`, `frame_id`, `address`, `location`, `pointer`,
  `placed_box`, `placement`, `drill_px`. None of them is needed for our features.
- A finished walk-in clip (`show_video`) or rules check (`show_rules`) can **lead** the next
  command's `actions[]`.

## 4. What in the Grok branch breaks our contract

1. **"Every 60 cm" stops being a fast path whenever `site` is sent.** §1 of the p4-b1-b3 contract
   has the app send `site` with every command. But F7's `_fast_route` drops the `place_array` fast
   path when `context.site` is set ("hangers every 60 cm on a scanned scene is a plan, let the
   LLM route it"):
   - online it goes to the LLM, which may answer `show_plan` instead of `place_array`;
   - offline it gets the offline line and no action.

   Live, on the rebased server:
   ```
   POST /agent/command {"session_id":"probe-1","text":"every 60 cm","context":{"site":"kitchen","measurement":{"label":"tape #1","value_m":4.2,"axis":"length"}}}
   → {"reply":"Offline: try a cached part or the crate menu.","actions":[],"job_id":null}
   POST /agent/command {"session_id":"probe-2","text":"every 60 cm","context":{}}
   → {"reply":"Array set every 60cm.","actions":[{"name":"place_array","args":{"spacing_mm":600.0}}],"job_id":null}
   ```
   We left F7's rule alone; this is P4's call. Two options:
   - (a) keep the fast path when `context.measurement` is set (the tape the array runs along) or
     when `OFFLINE`;
   - (b) the app leaves `site` out of the array command.

   (a) keeps F7's planner for "LED strip under these cabinets" and keeps the tape array working.
2. **`show_survey` changes meaning** (§1). A `backend-integration` app build that talks to this
   server gets F10's `{survey_id, pins, label}` under `show_survey` and renders it as an empty
   tape card. The rename fixes our side; the app still needs the routing above.
3. **About 30 new action names** (`survey_started`, `show_plan`, `show_rules`, `show_video`,
   `coach_*`, `job_*`, …) reach any app build without the Grok handlers. On `backend-integration`
   they fall through to the "Can't do that yet" toast. Because `site` is in every command, the LLM
   is now also offered `plan_placement`, `survey_condition`, `coach_capture` and
   `walk_in_preview` (the last with `frame_id`). So ordinary online commands can come back with
   these actions.
4. **Some agent calls block longer.** A command can now wait up to 20 s:
   - `set_finish` renders server-side (`FINISH_TIMEOUT_S`);
   - a condition survey waits up to `SURVEY_WAIT_S`;
   - a rules check waits up to `RULES_WAIT_S`.

   The app's 30 s text and 45 s voice timeouts cover this, so nothing breaks, but the headset
   should show a filler.

## Update, Sat 09-26 16:10: the same 11 patches on `integration/vr-next`

`git cherry-pick` of the whole series onto `origin/integration/vr-next` @ `2c4ab34` applies with no conflicts
(branch `app-handoff-vrnext` in the app lane's demo backend). `pytest`: 1252 passed, 2 skipped; the 3 failures are
macOS-only (`os.sched_getaffinity` in `pipeline/structure`: `test_lines_only_when_planes_fail`,
`test_a_late_layer_is_killed_and_the_finished_one_published`, `test_finish_filters_by_the_mesh_and_gives_s4_the_kept_planes`),
in code these patches don't touch.
