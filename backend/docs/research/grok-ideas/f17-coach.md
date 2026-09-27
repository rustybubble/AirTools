# F17: install coach ("teach me to install this")

Built from G6's pick 3 (`g6-round5.md` §6): `server/coach.py`, `POST /coach/start`,
`POST /coach/{id}/check`, `POST /coach/{id}/advance`, `GET /coach/{id}`, agent tools
`start_coach` / `coach_step` / `check_step`, Unity actions `coach_started`, `coach_step`,
`coach_check`, `coach_stop`, `coach_done`, and a `/debug` section. Branch
`feat/grok-install-coach`, 2026-09-26.

**This branch is cut from `integration/grok-all`, not `main`.** The coach chains three features
that only exist together there:
- F9 manual Q&A (`server/manuals.py`): the manual, its page texts and the quote check;
- F13 rules check (`server/rules.py`): which jobs are licensed-trade-only;
- F1 realtime voice (`server/realtime.py`): the relay that speaks the steps.

The drill rule is F16-style (live labels), but F16's `labels.py` is on another branch, so the
coach asks for outlet and switch boxes in its own check call instead. It merges only after
F9, F13 and F1 are on `main`.

**Design rule (G6 live checks #13 and #14).** Never ask Grok "is this step done?" (#13 said
"done" with bottles still by the sink). Ask one to three yes/no questions about what's visible,
and let code decide.

| Decision | Made by | Grok's part |
|---|---|---|
| Which steps exist | the manual's text (quote must be found in the PDF, F9's `locate_quote`) or our checked-in template | writes the steps and their checks from the manual pages |
| Which page a step is on | where the quote was found | its `page` is only a hint |
| Whether a check question is allowed | code: ends with "?", 4+ words, no status words (done, complete, finished, correct, properly) | proposes the questions |
| The answer to each check | Grok vision: `yes`, `no` or `cant_see`, with evidence and a box | all of it |
| Verdict (`passed`, `not_yet`, `look`) | code: a contradicting answer wins, then `cant_see`, else passed | nothing |
| Drill `stop` | code: the drill point is in the column above (or on) an outlet/switch box, within 2 plate widths sideways (~15 cm) | lists the outlets and switches it sees |
| Licensed trade only | `rules.JobSpec.licensed` (new field, set for the mini-split), or a finished F13 rules check whose permit page says a homeowner can't pull it | nothing |
| Moving on | the user: "I did it" always advances, and the notebook says it wasn't camera-checked | nothing |

## Live runs (2026-09-26)

Job: the **Midea MAW08U1QWT window AC** (`midea-maw08u1qwt-8d6d08`). F9 had already found and
indexed its manual (`MAW12U1QWT-user-manual.pdf`, 44 pages, `model_match: true`); we reused that
cache and the PDF. **The frames are kitchen thumbs** (`scene/kitchen/thumbs/*.jpg`, 640×360),
standing in for headset frames. They don't show the AC, so the window-AC checks can only come
back `cant_see` or `no`. That was expected, and it exercises the `look` path and the drill rule
(the kitchen has outlets and a switch). A second job, the faucet template, ran on the sink-wall
thumb that G6 used for #13/#14, to exercise `not_yet` and `passed` with real answers.

Models: `grok-4.20-0309-non-reasoning` for both calls (steps via `/v1/responses`, the same
path and model as F9; checks via chat completions on the new `LLM_COACH_VISION` role),
`temperature: 0`, strict json_schema. Costs are `usage.cost_in_usd_ticks / 1e10`, from the
server's log lines.

| # | Call | Result | Cost | Time |
|---|---|---|---|---|
| 1 | Steps from the Midea manual (15 pages ranked for install words, ~20k chars) | 8 steps, pages 11–15; **all 8 quotes found** in the PDF text. Step 5 (p. 13) mentions "drilling 1/8” pilot holes", so it gets the drill rule | $0.0102 | 9.0 s |
| 2 | Step 1 checks on 0221 (sink wall) | c1, c2 `cant_see` ("no window, bracket or sill visible") → **look** | $0.0008 | 1.2 s |
| 3 | Step 5 (drill) on 0141, drill point [0.127, 0.10], just above the outlet | c1 `cant_see`. Electrical: **3 "outlets"**. One is the real outlet, one is really the light switch (G6's outlet/switch mix-up again), one is the outlet by the cleaners. Boxes are ~5 % left and ~8 % low of the real plates. The drill point is inside the 2-plate margin → **stop** | $0.0011 | 1.4 s |
| – | Same frame, drill point [0.45, 0.10] (20 % to the side) | cache hit, no call → **no stop**; "I see no outlet or switch straight below that spot. I can't see inside the wall..." then **look** | $0 | – |
| 4 | Step 5 on 0061 (range wall), drill point [0.27, 0.12] | the outlet boxed at [0.295, 0.295, 0.342, 0.38], again right and low of the real plate → **stop** | $0.0009 | 1.1 s |
| 5 | Faucet template, step 1 on 0221 via `/agent/command` "check it": "Are any objects standing within a hand's width of the sink rim?" (expect no) | `yes`, "black shaker bottle and soap bottles near sink", box [5, 52, 15, 70] (percent) → **not_yet**, spoken "Not yet: black shaker bottle and soap bottles near sink." Same as #14 | $0.0008 | 1.3 s |
| 6 | Step 2: "Are the shut-off valves under the sink visible, with their handles turned fully closed?" | `cant_see` ("under sink not visible") → **look**, "Look under the sink at the valves." | $0.0003 | 0.7 s |
| 7 | Step 4 then step 5: "Is a faucet standing on the sink deck?" (expect no, then yes) | `yes`, faucet boxed → step 4 **not_yet**, step 5 **passed** (one call: same frame and question, cached) and it moved on to step 6 | $0.0008 | 1.0 s |
| | **Total** | | **$0.0148** of the $0.15 cap | checks 0.7–1.4 s |

Then a new process with `OFFLINE=true` on the same data:
- `GET /coach/{id}` resumed at step 5 of 8;
- a new frame gave `offline`, "I can't check it offline. Say 'I did it' when it's done.";
- the cached 0141 drill check still said `stop`;
- "repeat that" through `/agent/command` read step 5 back (the session was found on disk);
- `POST /coach/start` for the Midea came back `source: manual` from the cached steps.

The "I did it" overrides in run 5–7 wrote four notebook notes ("step 1: the user said it's done;
not checked by camera"). The `/debug` section was checked in a browser against that OFFLINE
server: start (faucet), load thumb 0221, check (cached `not_yet`, the boxes drawn), "I did it"
and "check it" via the command box. No console errors.

**What the live run shows.**
- Visible-state yes/no questions keep working (#14's result held on three new questions).
- Steps the manual writes are faithful. Every quote matched, so the quote check dropped nothing
  here; the test fixture drops a tampered one.
- **Manual-written checks are the weak part.** Some are not visual: "Is the bracket width set
  to match the measured window sill opening?", "Is the side arm foam cut to the measured length
  plus 1/4 inch?". Others are status-ish in disguise: "...as checked with a level?". The code
  rule only bans status words. On a real frame these will mostly come back `cant_see`, so the
  user says "I did it". Templates don't have this problem.
- Box offsets and outlet-vs-switch naming are as unreliable as G6 found. The rule doesn't care
  about the kind (both stop), and the 2-plate margin absorbed a 5–8 % offset here. A larger
  offset, or a missed plate, gives no stop. That's why the finder line is always spoken.

## Flow

1. "Teach me to install this" (fast path; or `start_coach` via the LLM or the voice relay). The
   job comes from the words ("... the faucet"), else the selected part.
   - Refusal first: a mini-split gets "I won't coach this one: its refrigerant lines need an
     EPA-certified HVAC tech, and its new 240 volt circuit needs an electrician. I can find who
     installs it near you."
   - Steps: the part's manual if F9 already indexed it (else a background prefetch starts and
     the template is used), else the template.
2. Each step is spoken as "Step n of N: <say>". A drill step adds "Before you drill, point at
   the spot and say check it."
3. "Check it" / "done": one vision call on `frame_jpg_b64` (or the relay's last `frame` if
   under 10 s old). `passed` moves on by itself.
4. "Next" / "skip" / "I did it": moves on, notebook note. "Back", "repeat that" too.
5. After the last step: `coach_done {checked, overridden}` and a summary note, which the report
   (F4) and packet (F14) show as a note.

The state is `data/coach/<coach_id>.json`, written atomically after every move. The agent's
in-memory `Session.coach_id` is only a shortcut: after a restart `coach.current()` finds the
session's newest active coach on disk.

**Voice (F1).** The three tools are in `agent.TOOLS`, so the relay offers them. When a response's
tool calls are all coach tools, the relay skips the model follow-up and sends the tools'
`spoken` as one `force_message`: the step text goes out word for word. The headset sends
`{"type": "frame", "jpg_b64"}` before "check it"; a frame older than 10 s isn't checked ("Hold
still and look at it, then say check it.").

**Offline.** Step reading works from the cache: templates always, manual steps once written.
Checks replay cached frames; a new frame gets "say 'I did it' when it's done". The coach fast
paths run before the agent's OFFLINE branch.

## Cost per use

- Writing a manual's steps: ~$0.01 and ~9 s, once per (part, manual source).
- Each check: $0.0003–0.0011, 0.7–1.4 s (#14 measured 1.8 s). The same frame is never billed
  twice.
- A 6-step job with a couple of checks per step: about $0.01–0.02. On F1's voice, the audio
  ($0.08/min) is most of the bill.

## Tests

`tests/test_coach.py`, 19 tests, offline. The Grok replies are the live run's real ones
(`tests/fixtures/coach/checks.json`, `steps_midea.json`). The Midea page texts are trimmed to
pages 9–18 (`manual_pages_midea.json`).
- Templates: numbered, visual checks only, drill steps tagged.
- Manual steps: a tampered quote is dropped; a wrong page is corrected; a status question is
  dropped. Start from the manual makes one call and is cached (works offline); no manual falls
  back to the template.
- Refusal: the mini-split (`JobSpec.licensed`) and a rules check with `homeowner_allowed: "no"`,
  in code, at the endpoint (409) and via the agent.
- Verdicts from the real replies (`not_yet`, `look`, `passed` + advance); a frame is cached
  (billed once); a #13-style `"answer": "done"` reply gives no verdict (502 at the endpoint).
- Drill rule: above the outlet → `stop` + `coach_stop`; 20 % to the side → no stop; below the box
  → no stop; the finder line on every drill check.
- "I did it": overrides, notebook notes, `coach_done` counts. Back/repeat.
- Restart: `GET /coach/{id}` and "repeat that" after clearing the agent's sessions.
- OFFLINE: steps, a cached check, a new frame → "say I did it", the agent path.
- Agent: fast paths, no frame → "hold still" with no call, a stale relay frame, tool hiding.
- Voice: a `frame` uplink then `check_step` → one `force_message` with the exact line, and no
  `response.create` follow-up.

Full suite: 871 passed, 2 skipped; `ruff check` clean.

## Open issues

- **Manual-written checks can be unanswerable by eye** (above). Options: tighten the prompt with
  a rule that each question must name one object and a visible state, or run a second cheap
  text call that rewrites or drops non-visual questions. Not done: it would cost a call per
  manual, and `cant_see` → "I did it" is an honest fallback.
- **Manual steps can bundle two actions.** Step 5 is "check the level, insert the cotter pins,
  drill and screw", so its one check (cotter pins) says nothing about the screws. A step split
  on the manual's own lettering (A, B, C...) would be tighter.
- **The drill rule only knows what the frame shows.** It can't see wires inside the wall, a
  plate out of frame, or a box on the other side of the wall. Its 2-plate margin was tuned on
  two frames. `SIDE_MARGIN` is the knob. The rule stops only above a plate; it doesn't cover
  below one or the sink wall's pipes (G6 idea 4 has those).
- **Unity doesn't send `drill_px` yet.** The default is the frame centre (the crosshair), which
  matches look-to-aim.
- **No real headset frames were tested.** Kitchen thumbs stood in; the window-AC checks couldn't
  pass on them.
- The realtime `frame` message goes into the relay's context and lasts until the next `context`
  message. `find_part` also sees it as `frame_jpg_b64`, as it would from `/agent/command`.
- `coach.current()` scans `data/coach/*.json` per call (fine for a demo's dozens; index by
  session if it grows).
- `LLM_COACH_VISION` is in the README roles table; when this merges into
  `integration/grok-all`, add `# LLM_COACH_VISION=xai:grok-4.20-0309-non-reasoning` to the
  `.env.example` block in `docs/integration-grok-features.md`.
