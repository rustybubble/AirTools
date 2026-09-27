# Grok features: integration branch

`integration/grok-all` is `main` plus the twenty Grok feature branches (it continues
`integration/grok-features` and `integration/grok-features-f9-f12`). Each one is merged
with `--no-ff`, so every feature stays reviewable as one merge commit. The branch exists to run
all of them in one server for the demo. Review the features on their own branches; this branch
only adds the conflict resolutions.

Merge order: F1 realtime quartermaster, F2 reimagine, F3 installer intel, F4 site report,
F5 postcard, F6 safety radar, F7 placement planner, F8 flythrough. Then `main` again (the B1
pipeline fixes and the G5 research doc), then F9 manual Q&A, F10 condition survey, F11 finish
variants and F12 capture coach. Then `main` again (the G5 review index), F13 rules check and
F14 job packet, then `main` once more (the S4 orthophoto cap and the G6 research). Final pass:
`main` again (the review index and the P1 buildings bench), F13's eligibility fix (f1ec9dd),
F15 do the whole job, F16 live labels, F17 install coach, F18 voice-refined reimagine, F19 booth
wall and F20 paper-quote checker. F15 and F17–F20 are cut from this branch; F16 from `main`.

Checked on the merged tree: 1027 passed, 2 skipped (kitchen scene data is gitignored, OpenSCAD is
not installed); `ruff check` clean. `OFFLINE=true` with every key unset serves every route below
(65 HTTP operations in `/openapi.json`, plus `WS /voice/realtime`). In a headless browser `/debug`
loads all 20 sections (its scene pickers fill from `/scenes`) with no console errors besides the
favicon, and `/booth` renders the empty wall and the three leaderboards with no console errors.
`test_debug_html_script_parses` runs `node --check` on the console's script, so a duplicate
`const` from a merge fails a test.

## What each branch adds

| # | Branch | Endpoints | Agent tool (fast path) | Unity actions | Env vars |
|---|---|---|---|---|---|
| F1 | `feat/grok-realtime-quartermaster` | `WS /voice/realtime`, `GET /realtime` (test page) | voice relay: exposes every `agent.TOOLS` entry plus local `hanger_plan`, `btu_for_room` | the same actions as `/agent/command`, sent over the socket | `XAI_VOICE`, `XAI_REALTIME_MODEL`, `REALTIME_MAX_S` |
| F2 | `feat/grok-imagine-reimagine` | `POST /scene/reimagine`, `GET /scene/reimagine/{id}.jpg` | `reimagine_view(prompt)` (LLM only) | `show_reimagined` | none |
| F3 | `feat/grok-installer-intel` | `POST /intel/installers` | `find_installer(trade, location)` (LLM only) | `show_installers` | `DEFAULT_LOCATION` |
| F4 | `feat/grok-site-report` | `GET /report/{session_id}`, `GET /report/{session_id}.json`, `POST /report/{session_id}/narrate` | `make_report` ("send/show/make ... report") | `show_report` | `LLM_REPORT` |
| F5 | `feat/grok-imagine-postcard` | `POST /parts/{part_id}/postcard`, `GET /parts/{part_id}/postcard.jpg` | `see_it_installed(placement)` ("what it'll look like", "see it installed") | `show_postcard` | none |
| F6 | `feat/grok-safety-radar` | `GET /parts/{part_id}/safety` (verdict also lands on the checkout receipt) | `check_safety(part_id)` ("recalled", "is it safe") | `show_safety` | none |
| F7 | `feat/grok-placement-planner` | `POST /scene/plan` | `plan_placement(request)` (LLM), `place_plan` ("place them" after a plan) | `show_plan`, then `place_array` | `LLM_PLAN` |
| F8 | `feat/grok-imagine-flythrough` | `POST /scene/flythrough`, `GET /scene/flythrough/{job_id}`, `GET /scene/flythrough/{id}.mp4`, `.jpg` | `walk_in_preview(prompt)` (LLM only) | `flythrough_started`, `show_video` | none |
| F9 | `feat/grok-manual-qa` | `POST /parts/{part_id}/manual`, `POST /parts/{part_id}/manual/ask`, `GET /parts/{part_id}/manual.pdf` | `ask_manual(question, part_id)` (LLM; offline: a question about the selected part with its manual cached) | `show_manual_answer` | none |
| F10 | `feat/grok-condition-survey` | `POST /scene/survey`, `GET /scene/survey/{survey_id}` | `survey_condition(focus, careful)` ("survey the roof"), `fix_pin` ("find a fix for pin f1") | `survey_started`, `show_survey`, then `search_started` + `add_note` | `LLM_SURVEY`, `LLM_SURVEY_CAREFUL` |
| F11 | `feat/grok-finish-variants` | `POST /parts/{part_id}/finish`, `GET /parts/{part_id}/model-{slug}.glb`, `GET /parts/{part_id}/finish-{slug}.jpg` | `set_finish(name)`, now server-side ("show it in matte black") | `set_finish` with `model_url`, `label` | none |
| F12 | `feat/grok-capture-coach` | `GET /scenes/{site}/coverage` | `coach_capture` ("what did I miss?", "which sides did I get?", "scan coverage") | `show_coverage` | `LLM_COACH` |
| F13 | `feat/grok-rules-check` | `POST /rules/check`, `GET /rules/check/{check_id}` | `check_rules(job, address)` ("permit", "inspection", "rebate", "tax credit", "incentive") | `rules_started`, `show_rules` | `DEFAULT_LOCATION` (shared with F3) |
| F14 | `feat/grok-job-packet` | `POST /packet/{session_id}`, `GET /packet/{packet_id}.pdf`, `DELETE /packet/{packet_id}` | `send_packet(days)` ("send it to my contractor", "share the job"), `revoke_packet` ("take the packet down") | `show_packet`, `packet_revoked` | `LLM_PACKET` |
| F15 | `feat/grok-do-the-whole-job` | `POST /job/run`, `GET /job/run/{run_id}?after=n` | `run_job(goal)` ("do the whole job", a bare "handle it" / "take care of it") | `job_started`, `job_step`, `job_done`, plus each step's own actions | none |
| F16 | `feat/grok-live-labels` | `POST /scene/labels`, `GET /scenes/{site}/labels` | `label_view(focus)` ("what am I looking at?", "label this") | `show_labels` | `LLM_LABELS` |
| F17 | `feat/grok-install-coach` | `POST /coach/start`, `GET /coach/{id}`, `POST /coach/{id}/check`, `POST /coach/{id}/advance`; relay `{"type":"frame"}` uplink | `start_coach(job)` ("teach me to install this"), `coach_step(move)` ("next", "back", "repeat that", "I did it"), `check_step` ("check it", "done") | `coach_started`, `coach_step`, `coach_check`, `coach_stop`, `coach_done` | `LLM_COACH_VISION` |
| F18 | `feat/grok-refine-reimagine` | `POST /scene/reimagine` takes `session_id` | `refine_reimagine(prompt)` (LLM, and "darker ...", "add ...", "swap ..."), `undo_reimagine` ("undo"), `start_over_reimagine` ("start over") | `show_reimagined` gains `step`, `can_undo`, `drift` | none |
| F19 | `feat/grok-booth-wall` | `GET /booth`, `GET /booth.json`, `GET /booth/cards/{id}.jpg`, `POST /booth/{session_id}/share`, `DELETE /booth/{entry_id}`, `GET /booth/x/login`, `GET /booth/x/callback`, `POST /booth/x/confirm` | `share_design(name, phrase)` ("add it to the wall as Maya", "share my design", "post it"; runs only on the consent phrase) | `show_share_preview` | `LLM_BOOTH`, `X_CLIENT_ID`, `X_CLIENT_SECRET`, `X_REFRESH_TOKEN` |
| F20 | `feat/grok-quote-checker` | `POST /quote/check` | `check_quote` ("check this quote", "look over this estimate"; needs a frame) | `show_quote_check` | `LLM_QUOTE` |

F3, F6, F9, F13 and F14 share `llm.responses()` (xAI Responses API with server-side web/X
search). F8, F10 and F13 share `jobs.start_task()` (one background-job table); F14 reads F10's
and F13's results from it with `jobs.latest()`. F15 chains F10, search, F6, F13, F5, F14 and
F6's checkout panel; F19 reads F14's `packet.gather()`; F20 calls F13 and F6; F17 reads F9's
manuals and F13's licensed-trade rule; F18 extends F2 and hands off to F8; F16 reuses F10's
`to_pin`.

## Environment variables

New in these branches (all optional):

| Var | Default | Used by |
|---|---|---|
| `XAI_VOICE` | `rex` | F1 realtime voice, F4 report narration |
| `XAI_REALTIME_MODEL` | `grok-voice-latest` | F1 |
| `REALTIME_MAX_S` | `600` | F1 session cap (cost guard) |
| `DEFAULT_LOCATION` | `Atlanta, GA` | F3 when neither the tool call nor `context.location` names a place |
| `LLM_REPORT` | unset: `xai:grok-4.3` (falls back to the `agent` role) | F4 report summary |
| `LLM_PLAN` | unset: `xai:grok-4.20-0309-non-reasoning` | F7 planner pick |
| `LLM_SURVEY` | unset: `xai:grok-4.20-0309-non-reasoning` | F10 survey, `model: "fast"` |
| `LLM_SURVEY_CAREFUL` | unset: `xai:grok-4.7` | F10 survey, `model: "careful"` |
| `LLM_COACH` | unset: `xai:grok-4.20-0309-non-reasoning` | F12 coach line (the template is the fallback) |
| `LLM_PACKET` | unset: `xai:grok-4.20-0309-non-reasoning` | F14 packet summaries (xai only: it goes through `responses()`) |
| `LLM_LABELS` | unset: `xai:grok-4.20-0309-non-reasoning` | F16 live labels, one streamed vision call per frame |
| `LLM_COACH_VISION` | unset: `xai:grok-4.20-0309-non-reasoning` | F17 install coach step checks (yes / no / cant_see on one frame) |
| `LLM_BOOTH` | unset: `xai:grok-4.20-0309-non-reasoning` | F19 booth wall caption (number-checked; template fallback) |
| `LLM_QUOTE` | unset: `xai:grok-4.20-0309-non-reasoning` | F20 paper-quote reader (strict-schema vision) |
| `X_CLIENT_ID`, `X_CLIENT_SECRET` | unset: no X | F19 optional X post: an OAuth 2.0 confidential client |
| `X_REFRESH_TOKEN` | unset | F19: the bot account's refresh token (`GET /booth/x/login` writes a rotated one to `DATA_DIR/booth/x_token.json`) |

Already on `main` and needed here: `GROK_API_KEY` (every Grok feature), `GROQ_API_KEY` (agent,
STT), `OFFLINE` (`true` replays the disk cache only; uncached Grok calls return `503` or an
"offline" line), `DATA_DIR` (`./data`: Imagine JPEGs, clips, manuals, caches) and `SCENE_DIR`
(`./scene`: the scene packages the survey, coach, planner and Imagine features read).

The README's roles table lists every `LLM_*` role. `.env.example` still lacks these variables:
a write hook refuses edits to that file, so add them by hand (commented, so an empty value
doesn't override a default):

```
# LLM_REPORT=xai:grok-4.3
# LLM_PLAN=xai:grok-4.20-0309-non-reasoning
# LLM_SURVEY=xai:grok-4.20-0309-non-reasoning
# LLM_SURVEY_CAREFUL=xai:grok-4.7
# LLM_COACH=xai:grok-4.20-0309-non-reasoning
# LLM_PACKET=xai:grok-4.20-0309-non-reasoning
# LLM_LABELS=xai:grok-4.20-0309-non-reasoning
# LLM_COACH_VISION=xai:grok-4.20-0309-non-reasoning
# LLM_BOOTH=xai:grok-4.20-0309-non-reasoning
# LLM_QUOTE=xai:grok-4.20-0309-non-reasoning
# X_CLIENT_ID=
# X_CLIENT_SECRET=
# X_REFRESH_TOKEN=
# XAI_VOICE=rex
# XAI_REALTIME_MODEL=grok-voice-latest
# REALTIME_MAX_S=600
# DEFAULT_LOCATION=Atlanta, GA
```

## Conflict resolutions

The merge commit bodies have the full list. The ones that change behaviour:

- **One early-exit rule in `agent.handle_command`.** F2 (`ends_turn`), F3 (`has_installer`), F7
  (`speaks_itself`), F9 (`has_manual`), F10 (`has_ask_scene` widened), F11 (`finish_reply`),
  F12 (`has_coach`) and F13 (`has_rules`) each added a "this tool already has the reply, don't spend a second LLM turn"
  check. They are now one tuple: `ask_scene`, `reimagine_view`, `find_installer`,
  `see_it_installed`, `check_safety`, `plan_placement`, `walk_in_preview`, `ask_manual`,
  `survey_condition`, `set_finish`, `coach_capture`, `check_rules`, `send_packet`,
  `revoke_packet`. These tools run even when the model also
  returns text (on F11 and F12 alone, filler text skipped the tool). Reply order: the tool's
  `spoken`, then the model's text, then `answer`, then the canned line. F6's verbatim safety line
  (check_safety or start_checkout with `spoken`) still goes first. Tests:
  `test_check_safety_runs_even_when_the_model_also_returns_text`,
  `test_speaking_tools_run_despite_model_text`.
- **Tool hiding** keeps every branch's rule. `ask_scene` needs `frames`. `reimagine_view` needs
  `site` plus `frame_id` or `frames`. `see_it_installed` needs a frame (b64, or site plus
  frame_id). `plan_placement`, `survey_condition` and `coach_capture` need `site`.
  `walk_in_preview` needs `site` plus `frame_id`. `send_packet` needs a notebook for the
  session, `revoke_packet` a live public packet. `check_rules` is always offered ("do I need a
  permit for a mini-split?" names its job with no part selected). Test:
  `test_packet_tools_hidden_until_there_is_something_to_share`.
- **One background-job table.** F8 and F10 each added `jobs.start_task` with different
  signatures. F8's version stays: a `Job` row in `_jobs`, with the same eviction (a running job is
  never dropped) and `jobs.get`/`jobs.wait`. F10's parallel `_tasks` dict is gone. The survey
  routes turn the `Job` into F10's public shape (`{survey_id, status: running|done|failed,
  **result}`), and `GET /scene/survey/{id}` 404s on any job that isn't a survey. Survey ids are
  now 12 hex characters, not `s-<hex>`.
- **Riders on the next command.** A walk-in clip (F8) and a rules check (F13) that finish
  after their reply lead the next command's actions (`show_video`, `show_rules`).
- **Actions can be a list.** F10's `survey_condition` and `fix_pin` queue two actions each. The
  voice relay (`server/realtime.py`) now sends every action in `agent._as_list(action)`.
- **`set_finish` is server-side** (F11). It left `_CLIENT_ONLY_TOOLS`, which is now just
  `place_array` and `equip_tool` (`start_checkout` moved server-side in F6). The voice relay calls
  `agent._run_tool`, so a spoken "show it in navy" renders there too. Test:
  `test_voice_set_finish_runs_server_side_and_multi_action_tools_send_each`.
- **`llm.responses()`**: F9, F13 and F14 copied it verbatim from F3; there is one copy (and
  one `USD_TICKS`). F13 also copied `jobs.start_task` and `Job.result` with the same
  signatures; one copy. F13's `_as_actions()` duplicated `_as_list()`; it's gone.
- **`DEFAULT_LOCATION`**: F3 and F13 both added it with the same default; one field. The
  context `location` row in `docs/api.md` covers both, plus a new `address` row for F13.
- **F14 and F4.** F14 copied F4's `Report`/`assemble` and helpers into `server/packet.py`; the
  copy is gone and `packet.py` imports `Report`, `assemble` and `_notebook_entries` from
  `server/report.py`. `server/report_demo.py` was byte-identical and merged clean.
- **F14 extras read the real modules now.** F14 read the survey, safety and rules cache files
  with assumed shapes (its own report said so), and the rules one was wrong: F13 caches
  `rules_money` by state and job, not by part. Now:
  - safety: `safety.peek(part)` per part (F6's cache-only verdict, CPSC + Grok), shown as
    `verdict: headline` plus each recall;
  - survey: the newest finished survey of the notebook's site (`jobs.latest("survey", ...)`),
    F10's already-triaged pins;
  - rules: the newest finished rules check for one of the parts (F13's result now carries
    `part_ids`) or their job kind: a permit line plus each incentive; only F13's `counted`
    rows (active, with an amount, eligible) come off the cover total (see the final pass).
  Survey and rules results live in the in-memory jobs table, so a restart forgets them until
  they run again (both replay from cache at once). `tests/test_packet.py` seeds F10/F13-shaped
  jobs and F6's cache and checks the lines.
- **Cost logging**: one `llm.cost_usd(usage)` helper serves F7's per-call cost log in `chat()`,
  F10's survey and F12's coach. The coach's own duplicate log line is gone (`chat()` already logs
  `role=coach` with latency and cost).
- **`hanger_plan`**: F1 and F7 had identical copies. `server/realtime.py` now imports it from
  `server/plan.py`.
- **`XAI_VOICE`**: F1 and F4 both added it with the same default. There is one definition, and
  the report narration uses it too.
- **`server/imagine.py`** (shared by F2, F5 and F8): kept the one shared client, F2's
  `reimagine()` and a docstring that covers all its callers.
- **Shadowed name**: F6's `report = safety.peek(part)` in `do_checkout` shadowed F4's `report`
  module. It is renamed to `safety_report`. `/checkout` also keeps the `bom_id` notebook entry and
  F9's manual prefetch.
- **Selecting a candidate** starts both the safety check (F6) and the manual prefetch (F9).
- **Agent phrases.** The fast paths don't overlap; `tests/test_agent.py::test_grok_fast_paths_route_apart`
  and `test_grok_phrases_left_to_the_llm` pin them down.
  - "Send/show me the report" goes to `make_report`.
  - "Show me what it'll look like" and "see it installed" go to `see_it_installed` (a postcard of
    the selected part).
  - "Show it/this/that in matte black" goes to `set_finish`. The finish matcher needs
    it/this/that right after "show", so the postcard phrase never reaches it. "Show it in the
    kitchen" / "in my room" is refused: not a finish.
  - "Is it recalled / is it safe" goes to `check_safety`.
  - "Survey the roof" goes to `survey_condition`; "... pin f1" goes to `fix_pin`.
  - "What did I miss?", "which sides did I get?", "scan coverage" and a bare "(show me the)
    coverage" go to `coach_capture`. F12 matched any "coverage" or "which sides", which stole
    "what's the coverage on this sealant?" and "which sides of the cabinet get the strip?"; those
    now go to the LLM. "What else do I need?" still goes to `what_else`.
  - "Do I need a permit?", "any rebates on a mini-split?", "is there a tax credit?" go to
    `check_rules` (the job kind comes from the words, else the selected part). The rules
    matcher sits after the report and safety matchers: "show me the report on the permits"
    stays `make_report`, "is it recalled? do I need a permit?" stays `check_safety`.
  - "Send it to my contractor", "share the job", "give me the packet" go to `send_packet`;
    "take the packet down", "revoke the link" go to `revoke_packet`. These two matchers run
    first, so "send the survey and the permits to my contractor" is a packet (which already
    carries both), not a new survey or rules check, and "send the report to my contractor" is
    a packet too. "Send me the report" stays `make_report`.
  - "Place them" goes to `place_plan`, but only after a plan.
  - "What would navy cabinets look like?" has no fast path (`_INSTALLED_RE` only matches
    it/that/this), so the LLM picks `reimagine_view`. "Walk me into it with navy cabinets" also
    goes to the LLM, which picks `walk_in_preview`. Install questions ("what drill bit do I
    need?") go to the LLM's `ask_manual`; offline, F9's question matcher answers from the cached
    manual.
- **`/debug`**: each feature gets its own section. `showCommandResult` handles every new action
  (a `survey_started` polls and draws the survey; `show_coverage` draws the ring). `buildContext`
  takes `site`/`frame_id` from, in priority order: a picked reimagine frame, a picked postcard
  frame, a typed survey site, the coach's site picker, the walk-in site/frame boxes, then the plan
  site box. A frame always belongs to the `site` sent with it. Selecting a card fills the
  postcard, safety and finish part boxes. The rules section adds `context.address`; a
  `show_rules` draws its result and a `rules_started` polls; a `show_packet` fills the packet
  section's links and a `packet_revoked` greys out its revoke button.
- **`docs/api.md`**: kept every section, action row and limits row. The action rows are one table
  again (the "place them" and survey notes sit below it). There is one row each for the `site` and
  `frame_id` context fields, and one context JSON example with every field. The
  route-verification line no longer states a fixed route count.

### Final pass: F13's fix and F15–F20

- **Rebates count only eligible rows everywhere.** F13's fix (f1ec9dd) marks a rebate
  `not_eligible` when the program needs ENERGY STAR and the unit isn't listed, and `unverified`
  when the lookup failed; `rules.counted(row)` is the one rule for "comes off the price", and
  `money["rebates_usd"]` sums it. Two callers merged clean but still summed every
  `status == "active"` row, so an `unverified` amount came off the total: `packet.rebates_usd`
  (F14; the booth wall's rebate figure and leaderboard read it through `packet.gather`) and
  `runjob._rules` (F15's `job_done.after_rebates_usd`). Both use `rules.counted` now. The packet
  lines and F20's quote flags show the `eligibility_reason` of an unverified row. Tests:
  `test_rebates_count_only_eligible_rows`, `test_unverified_rebate_stays_on_the_price`,
  `test_unverified_rebate_says_so`. The `docs/api.md` `show_rules` row is the fix's.
- **One `to_pin`.** F16 copied F10's box-to-mesh raycast; `server/labels.py` imports it from
  `server/survey.py`.
- **`Session.id`.** F16 renamed it to `session_id`; the integration keeps `id` (F14, F15, F17
  and F19 use it).
- **One early-exit tuple, extended.** `run_job`, `label_view` (F16's own `has_ask_scene` exit
  folded in), `refine_reimagine`, the three coach tools, `share_design` and `check_quote` joined
  it. Tool hiding: `label_view` and `check_quote` need a frame; `coach_step`/`check_step` a
  running coach; `refine_reimagine` a reimagine stack; `share_design` the consent phrase and a
  notebook.
- **One relay `frame` uplink.** F16's spec wanted one and F17 built it
  (`{"type":"frame","jpg_b64"}` on `WS /voice/realtime`). It sets `context.frame_jpg_b64` and
  `frame_at`, which `check_step` (with a 10 s freshness rule) and `label_view` both read.
- **Left separate:** F17's drill-over-outlet stop finds outlets and switches in its own check
  call (its prompt and schema), not through F16's labels. One call per check either way.
- **One fast-path router**, `agent._fast_route(session, text, ctx)`; see the table below.
- **Consent phrase tightened (F19).** `_SHARE_RE` matched "add|put|stick ... to/on the wall"
  with anything in between, so with a reimagine on screen "add floating shelves to the wall" (an
  F18 refine) would have posted the design to the public booth wall. The thing added must now be
  the design: it / this / that / me / us / my|our|this|the design.
- **Quote vs manual quote (F20).** `_QUOTE_RE` caught "read me the quote from the manual" (F9's
  answer, F17's step quote); a sentence naming the manual now goes to the LLM.
- **`/debug`**: the labels, booth and quote sections had each been merged into the previous
  feature's `<section>`; 20 sections now. Two hunk boundaries cut a closing `});` (run_job's
  click handler) and `re.IGNORECASE)` (`_LABEL_RE`); both restored. `buildContext` sends the
  quote frame, then the coach frame while coaching.
- **Config:** `LLM_LABELS`, `LLM_COACH_VISION`, `LLM_BOOTH`, `LLM_QUOTE` and the `X_*` settings
  all kept; the README roles table now lists `LLM_PACKET`, `LLM_BOOTH`, `LLM_QUOTE` too.

### Fast-path priority

`agent._fast_route` tries these in order and takes the first match; anything left goes to the
LLM. Tests: `test_grok_fast_paths_route_apart` (stateless), `test_grok_phrases_left_to_the_llm`,
and `test_stateful_fast_paths_route_apart`, which pins 32 phrases in four states (no state, a
coach running, a reimagine on screen, both).

| # | Matcher | Phrases | When |
|---|---|---|---|
| 0 | `set_limits` (app lane), a pre-pass | "under $40", "no more than 40 dollars", "arriving by Friday" are cut out of any command and set the purchase limits (`show_limits`); the rest of the command then goes through rows 1–14 ("Find hangers, under $40" → limits, then the search). Skipped when the command is a rules or quote one (rows 7–8): "any rebates up to $500?" is about the rebate | always |
| 1 | `run_job` (F15) | "do the whole job", a bare "handle it" / "take care of it" | always |
| 2 | `revoke_packet`, `send_packet` (F14) | "take the packet down", "revoke the link"; "send it to my contractor", "share the job" | always |
| 3 | `share_design` (F19) | "add it to the wall (as Maya)", "put it on the wall", "share my design", "post it" | always |
| 4 | `stop_survey` (app lane), `fix_pin`, tape `survey` (app lane), `survey_condition` (F10) | a bare "stop" / "cancel the survey"; "find a fix for pin f1"; "measure / survey / size up every cabinet door" (door(s), drawer(s), window(s), panel(s), appliance(s)); "survey the roof". The thing surveyed decides: a structure-object noun is the tape; roof, gutter(s), facade, building or a condition word ("condition", "damage", "cracks", "rot", "rust", "leaks") is F10's | always |
| 5 | base paths | equip a tool, "pick the second one", "buy it" / "check out (with ...)" / "pay for it" → `start_checkout` (app lane 0001; "buy a hinge" and "check out this hinge" stay with the LLM), sort sellers, "every 60 cm" (not with a `site`: then the LLM plans it), "what else do I need?" | always |
| 6 | `make_report` (F4), `see_it_installed` (F5) | "send me the report", "show me what it'll look like" | always |
| 7 | `check_quote` (F20) | "check / review / look over ... quote / estimate / bid" (not "... from the manual") | always |
| 8 | `check_safety` (F6), `check_rules` (F13) | "is it recalled?", "do I need a permit?", "any rebates?" | always |
| 9 | `set_finish` (F11) | "show it / this / that in matte black" | always |
| 10 | `coach_capture` (F12), `label_view` (F16), `start_coach` (F17), then `check_slope` (app lane) | "what did I miss?"; "what am I looking at?"; "teach me to install this", "walk me through it"; "is this gutter sloped enough to drain?" (gutter / sill / ledge + slope / drain / fall / pitch, not with find / get / need / buy) | always |
| 11 | `place_plan` (F7) | "place them" | after a plan |
| 12 | coach words (F17) | "check it", "done" → `check_step`; "next", "skip", "I did it", "back", "go back", "step back", "back up", "previous step", "repeat that" → `coach_step` | while a coach runs |
| 13 | reimagine words (F18) | "undo", "go back", "step back" → undo; "start over", "show me the original" → step 0; "darker ...", "no, lighter", "add ...", "remove ...", "swap ...", "replace ...", "paint ..." → refine ("add a note" excluded) | while a reimagine is on screen |
| 13b | measure and replace (app e2e hand-off, `server/replace.py`) | "remove / take out the dishwasher" → `remove_component`; "measure it / the gap" → `measure_cavity`; "find a dishwasher that fits" → `find_part` fitted to the gap (`SearchRequest.cavity`); "put it in there" → `place_part` (the first that fits); "next / previous one", "option 2" → `place_part` for that candidate; "put it back" → `restore_component`; up to four joined by and / then / commas | a removable part by that name in the site's `parts.r<rev>.json`; the rest while a part is out (`context.removed`), placing and switching once there are candidates |
| 14 | `survey_query` (app lane) | "which one is the widest / tallest / narrowest / shortest / largest / smallest?" | while a tape survey is stored (after a `POST /agent/observe` report) |

So with both a coach and a reimagine live: every "back" is the coach's previous step (row 12
comes first, and F17's matcher now takes every "back" F18's does), "undo" is always the
picture's, "start over" is the picture's, "done" / "next" / "repeat that" are the coach's. A
finish, a packet, a share or a quote check wins over both stacks mid-refine. Without a coach,
"go back" undoes the picture.

The app lane's rows (its B1 tape hand-off, rebased onto this branch): "survey" is shared by
two tools, so row 4 decides by the thing surveyed ("survey every cabinet door" measures with the
headset's tape; "survey the roof", "survey the gutters", "survey the windows for damage" triage
with F10). "Survey the gutter slope" is F10's (gutters); "is this gutter sloped enough?" is the
tape's `check_slope`, which sits after rows 6–10 so "show me the report on the gutter slope"
stays a report and "any rebates for gutter drainage?" stays a rules check. Tests:
`tests/test_tape_survey.py::test_survey_phrasings_route_between_tape_and_condition`,
`test_both_surveys_end_to_end_on_one_site`. Row 0 cuts spoken purchase limits out of any command before
routing the rest; money in a rules or quote command ("any rebates up to $500?", "check this
quote, is it under $2000?") isn't a limit (`tests/test_mandate.py`).

## Known gaps

- Over `WS /voice/realtime` the model calls `agent._run_tool` directly. So the `handle_command`
  fast paths (such as "place them" to `place_plan`, "pin f1" to `fix_pin`) and the tool hiding
  don't apply there. `place_plan` and `fix_pin` aren't in `TOOLS` at all, so by voice use
  `/agent/command` for those. F8's "finished clip rides along on the next command" wrapper also
  only runs on `/agent/command`. The headset should poll `GET /scene/flythrough/{job_id}` and
  `GET /scene/survey/{survey_id}` anyway.
- A voice `set_finish` holds its tool call for up to 20 s on a first render (then tints and says
  the render is still cooking). Warm the demo finishes first.
- The same holds for the F17/F18 words: by voice, "next" / "back" / "check it" reach the coach
  only through the model's `coach_step` / `check_step` calls (the relay says their `spoken`
  verbatim), and "darker blue" only through `refine_reimagine`. `undo_reimagine` and
  `start_over_reimagine` aren't in `TOOLS`, so "undo" and "start over" work on `/agent/command`
  only. The priority table above is `/agent/command`'s.
- `label_view` by voice uses the relay's last `frame` without F17's 10 s freshness check, and its
  `show_labels` echoes `context.frame_id` (else `"frame"`), which the `frame` message doesn't
  update: the headset should keep the pose of the frame it last sent.
- `.env.example` doesn't list the new variables yet (see above).
- F14 packets nobody revokes keep their xAI file after the link expires (see
  `docs/research/grok-ideas/f14-packet.md`, open issues); sweep `data/packets/*.json`.
- The packet's survey and rules sections (and so the booth wall's rebate figure) need those jobs
  to have run in this server process.
- `ruff format --check` flags two files that `main` already had unformatted (`server/search.py`,
  `tests/test_jobs.py`); left alone here.

## Demo script (~8 min): one building, outside then in

One story: a facilities manager gets an old school building ready for work. The drone scan of
the outside finds the problems and the fix; inside, the staff kitchen gets planned, refined,
checked, taught and quoted, and everything ends up in one packet for the contractor.

**Credit (on the title slide and under every Zabel shot):** *"Haus Schiller – Zabelgymnasium
Gera – Drohnenflug" by zabelgymnasium, CC BY 3.0, via Wikimedia Commons.* The scan is that public
footage (`scene/zabel-gymnasium`, scaled to the OSM footprint, see
`docs/research/p1-buildings-bench.md`); the job address in the demo is the server default
(Atlanta, GA), so permits and rebates are Georgia's. Say so once.

**Before the demo:** run every step below once online with the same words, so each Grok call is
cached, then run with `OFFLINE=true`: the Zabel survey, the kitchen labels (`uv run python -m
server.warm --labels kitchen`), the reimagine chain and its clip, the manual and coach checks for
the Midea, the LG rules check, the quote (`tests/fixtures/quotes/bad.png`, SYNTHETIC), the packet
summary and the booth caption. Keep the realtime voice (F1) open for questions; the scripted
lines below go through `/agent/command` (the push-to-talk path), which has every fast path.

1. **Fly-in and capture coach (0:00–0:40).** Load the Zabel scan (`context.site =
   "zabel-gymnasium"`). "What did I miss?" `show_coverage` draws the ring: 1 of 8 sides (the
   front) green, with the orbit from front-left to front-right and a roof grid dashed in; the
   coach reads the legs out. Computed from the cameras; Grok only phrases it (offline: the
   template says the same).
2. **Condition survey (0:40–1:30).** "Survey the roof." `survey_started`, then `show_survey`
   pins findings on the mesh by severity, labelled "AI triage from drone frames, not an
   inspection". Pinch the worst pin: "find a fix for pin f1" searches for its part and drops a
   notebook note.
3. **Do the whole job (1:30–2:30).** "Do the whole job." `job_started` draws the rail: survey
   (cached), part, then safety + rules + postcard in parallel, packet, pay panel. Each dot turns
   green or grey with a spoken line (a gutter part has no permit rules on file, so `rules` skips
   and says so). It stops at the hold-to-pay panel: **it never pays**, and a recalled part would
   stop it before the panel. `job_done` shows the total; a rebate comes off only when F13 found
   it eligible.
4. **Inside: live labels (2:30–3:00).** Switch to the kitchen scan. "What am I looking at?"
   `show_labels` puts the pre-labelled outlets, switch, sink and cabinets on the mesh; sizes show
   only when printed on the part. Tap one to shop for it.
5. **Planner (3:00–3:30).** Point at the wall cabinets: "LED strip under these cabinets, it
   comes in 1 metre strips." `show_plan` draws the runs and speaks length and strip count; "place
   them" opens the BOM for that count.
6. **Reimagine and refine (3:30–4:30).** "What would navy cabinets with brass pulls look like?"
   `show_reimagined` puts the edit on the frame's camera. Then "darker blue", then "add a
   butcher-block counter", then "undo": each is a `show_reimagined` step (pinch flips before and
   after, the original frame rides in every edit, so the rest stays put). "Walk me into it":
   `show_video` plays the 3 s clip landing on the refined picture.
7. **Safety and the install coach (4:30–5:40).** Select the Midea window AC: "is this
   recalled?" A red RECALLED pill with the CPSC headline. "Teach me to install this":
   `coach_started`, steps from its manual with page numbers. On the drill step, aim the crosshair
   straight above the outlet and "check it": `coach_stop` ("Stop: that spot is straight above an
   outlet...", plus the stud and wire finder line). Say "back": the coach's previous step, even
   with the reimagine still on screen ("undo" would have been the picture's; see the priority
   table). "I did it" moves on; the notebook records it was the user's word, not the camera's.
8. **Rules and money (5:40–6:15).** Select the LG mini-split: "do I need a permit for this?"
   `show_rules`: the permits and office, code editions, and each rebate with its status. A
   program that needs ENERGY STAR shows `not_eligible` for an unlisted unit, or `unverified`
   with its reason; only eligible amounts count. "Not legal advice; confirm with the permitting
   office."
9. **Check the contractor's quote (6:15–6:50).** Hold up the printed quote (the SYNTHETIC
   `bad.png`): "check this quote". `show_quote_check` flags the line whose math doesn't add up,
   the missing permit line, the recalled AC, a price spread against a verified seller (a spread,
   never a verdict) and the rebate, with the same eligibility wording.
10. **Packet and booth wall (6:50–7:30).** "Send it to my contractor." `show_packet` draws the QR
    of a public 7-day link; the PDF carries the survey pins, recall check, permits and eligible
    rebates. "Add it to the wall as Maya": `show_share_preview`, and the booth TV (`/booth`)
    shows the card, caption and leaderboards. The X post only goes out on the hold-to-post ring
    (and only with the `X_*` keys set); skip it offline.
11. **Report (7:30–8:00).** "Send me the report." `show_report` opens `/report/<session>` (as a
    QR for a phone), and `POST /report/<session>/narrate` reads the recap in the `XAI_VOICE`
    voice to close. Leave the Zabel credit on screen.
