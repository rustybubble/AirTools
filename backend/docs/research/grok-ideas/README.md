# Grok features: review index

Overnight cycle, 2026-09-26: six research rounds (g1–g6), then one feature per branch off
`main`. None is merged into `main`. `integration/grok-all` merges `main` + F1–F20 into one server to try together (1027 tests; see
`docs/integration-grok-features.md` on that branch, with a ~8 min demo script and the fast-path
priority table). Features that chain others (F15, F17–F20) branch off it.

| # | Branch | What it does | Grok used | Live spend | Write-up |
|---|---|---|---|---|---|
| F1 | `feat/grok-realtime-quartermaster` | `WS /voice/realtime`: full-duplex voice agent, ~1 s replies, our tools server-side | Voice realtime | ~$0.013 per turn | g2 §5 |
| F2 | `feat/grok-imagine-reimagine` | "What would navy cabinets look like?" edits the captured view, pixel-aligned | Imagine edits | $0.07 per edit | g1 |
| F3 | `feat/grok-installer-intel` | "Who installs this near me?": 5 real local businesses with cited evidence | web + x search | $0.47 | f3-installers.md |
| F4 | `feat/grok-site-report` | One-page site-walk report (PDF), Grok summary, spoken recap | grok-4.3, TTS | $0.02 | f4-report.md |
| F5 | `feat/grok-imagine-postcard` | "See it installed": the product photoreal in the user's own frame, scale from a drawn box | Imagine edits (2 images) | $0.71 | f5-postcard.md |
| F6 | `feat/grok-safety-radar` | Recall and defect radar: CPSC + Grok web/X, found a real recall on a cached AC | web + x search | $0.27 | f6-safety.md |
| F7 | `feat/grok-placement-planner` | "LED strip under these cabinets": Grok picks the planner, geometry is local; 6/6 correct | fast chat | $0.05 | f7-planner.md |
| F8 | `feat/grok-imagine-flythrough` | Walk-in clip: real frame to reimagined frame, 3 s video | Imagine video | $0.99 | f8-flythrough.md |
| F9 | `feat/grok-manual-qa` | Install-manual Q&A with page citations; quotes checked against the PDF text | web search + chat | $0.46 | f9-manuals.md |
| F10 | `feat/grok-condition-survey` | Drone condition survey: roof/facade findings pinned in 3D, pin, then find a fix | vision | $0.04 | f10-survey.md |
| F11 | `feat/grok-finish-variants` | "Show it in matte black" re-textures the GLB, not a tint | Imagine edits | $0.35 | f11-finish.md |
| F12 | `feat/grok-capture-coach` | Which sides the drone missed, suggested reshoot legs | fast chat | $0.004 | f12-coverage.md |
| F13 | `feat/grok-rules-check` | Permits, code editions, rebates for the job's real jurisdiction; every claim checked. Eligibility fix (f1ec9dd): a rebate whose program needs ENERGY STAR is `not_eligible` for an unlisted unit and `unverified` when the lookup fails; only eligible amounts come off a price | web search | $0.36 | f13-rules.md |
| F14 | `feat/grok-job-packet` | 4-page contractor/homeowner PDF on a revocable public xAI Files link | chat + Files | $0.001 | f14-packet.md |
| F15 | `feat/grok-do-the-whole-job` (off `integration/grok-all`) | "Do the whole job": survey, part, safety + rules + postcard, packet, pay panel; never pays, stops on a recall | chains F3–F14 | $0.37 per cold run, $0 warm | f15-whole-job.md |
| F16 | `feat/grok-live-labels` | "What am I looking at?": labels on the headset frame, first label ~1.25 s; kitchen pre-labelled in 3D | streamed vision | $0.07 | f16-labels.md |
| F17 | `feat/grok-install-coach` (off `integration/grok-all`) | "Teach me to install this": steps from the F9 manual or a template, each checked yes/no on the camera, a drill-over-outlet stop, "I did it" always wins | fast vision + chat | $0.015 | f17-coach.md |
| F18 | `feat/grok-refine-reimagine` (off `integration/grok-all`) | "Darker blue", "add brass pulls", "undo", "start over" refine F2's picture, original frame in every edit, drift retry, walk-in from the refined frame | Imagine edits (2 images) | $0.60 | f18-refine.md |
| F19 | `feat/grok-booth-wall` (off `integration/grok-all`) | Booth TV wall: shared design cards, local leaderboard, number-checked caption; consent-gated share, optional hold-to-confirm X post | fast chat (+ X API) | $0.002 | f19-booth.md |
| F20 | `feat/grok-quote-checker` (off `integration/grok-all`) | "Check this quote": reads a contractor's paper quote, checks its math, prices, permit, rebates and recalls; spreads, never verdicts | strict-schema vision | $0.02 | f20-quote.md |

Research: `g1-imagine.md`, `g2-voice-agents.md`, `g3-round2.md`, `g4-round3.md`, `g5-round4.md`,
`g6-round5.md` (about $2.0 of live checks in total).

**Pattern that held across features:** Grok researches, picks, writes and renders; code decides the facts.
Geometry (F7, F10, F12), code editions and program status (F13), quotes (F9) and evidence URLs (F3, F6)
are all checked in code, because every live run caught Grok inventing something plausible.

**Before a demo:** each branch's live cache sits in its own worktree or a scratchpad, not in `main`'s
`data/`. Warm the demo requests once online, then run with `OFFLINE=true`.
