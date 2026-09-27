# De-clutter: one owner for the whole screen

*Sat 2026-09-26, 14:30 EDT. Design owner's spec for the build session. Feature freeze Sun 02:00, demo freeze Sun 07:00.*

| | |
|---|---|
| **Grounded on** | `backend-integration` @ `415523b` (tag `green-0926-grok` `e69aa38` plus the demo.md F4 note). Paths are under `Assets/AirTools/` unless they start with `docs/`. |
| **Builds on** | `docs/ux/README.md`: §2.1 principles 4 ("the place is the hero") and 6 ("placed from the person"), §2.3 guide rail and one window slot, W0.7, W1.3 (`specs/W1.3-nextstep.md`), W1.5 ring v2, W1.9 label diet, and acceptance rows A7, A10 and A14. D1–D7 stand as decided. (`docs/ux/` is untracked in `~/AirTools`; this file sits beside it.) |
| **Evidence** | The builders in `Editor/`, the runtime in `Runtime/`, and the captures in `~/AirTools-backend/SpikeData/{grok,d3,b1b3,ladder,uxw0}`. The angles below are computed from builder constants for a standing eye with the gaze level (method in §1.3). |
| **Lane** | Every slice touches a builder or a rig surface, so it's XR-lane work. The pure maths (`UiZones`, `WorldLabels`) can be written in the offline C# lane. |

> **Update 2026-09-26 (scalemodels, the user's call):** the window this spec first called "More" is now **Settings**, with a
> gear on the ring (Phosphor gear-six). Its rows put the settings first (snapping, contrast, less motion, scale, units)
> and the scene actions below (layers, Ask, Home / Ladder / Exit world, sites, Take out). The name is updated below;
> the decisions stand as made.

## 0. Summary

**Why it's cluttered.** Eight lanes (the UX waves, B1, B3, P6/P7, G1–G4) each placed their own UI from the head,
with their own constants. There is no
registry: `WindowSlot` is the only thing that closes anything, and it covers the main slot only. The only clutter
controls are two unrelated 12-label budgets.

- **15 surfaces are up at once** in the worst scripted moment (§1.4). Seven of them are head-relative, and three
  are heads-up overlays (ZTest Always) that draw *over* the windows.
- **18 overlapping pairs** among head-relative surfaces (§1.3). Three examples:
  - the status line lies across the top third of every main window;
  - the job rail "steps aside" straight onto Find parts;
  - the pill and the coach card cut into the main window at the same 0.45 m depth.
- **Labels are capped per owner, not in total:** G2 12 + B1 survey 12 + fall edges 3, plus uncapped tapes (2n+1
  pills per shape), part callouts, level, ladder and ask pins.
  - G2 captions are never hidden, so G2's "12" can overrun.
  - G2 chips aren't in `LabelLayout`, so they overlap tape labels.
- **The ring has 11 items.** Seven are visible and four sit behind the wrist. The palm cluster (ring, the 0.28 m
  inspector and two strips) is about 0.53 m wide; R08 A13 allows 0.26 m.

**The fix: seven homes, one pool, one guard.**

1. **One heads-up line.** The W1.3 status line becomes the only heads-up surface:
   - a toast becomes a flash on it;
   - a reply takes its place for 5–10 s;
   - the job rail becomes its progress mode;
   - while a window is open it docks on the window's top rim instead of lying across it.
2. **One main slot** for anything with buttons, rows or pictures. Seven cards already share it; the pill yields
   while it's taken.
3. **One left side slot** for what you keep open while working in the scene: Find parts *or* the coach card.
4. **Settings**, the Scene window renamed (right side slot), for settings, layers and recovery. The ring drops to 6:
   Move · Measure · Level · Notebook · Model view · Settings.
5. **One wrist strip** for glanceable context: limits, scale and the scan credit.
6. **One label pool of 12** across every world overlay, with a priority order (§5), and **one Grok layer at a time**.
7. **A guard:**
   - `UiZones` holds every head-relative placement;
   - an EditMode footprint test fails on any new overlap;
   - `AgentHarness.Surfaces()` counts what's up against the §2 budget.

**After the fix,** the worst moment has 3 head-relative surfaces (1 heads-up), 0 overlaps and ≤ 12 labels.

**Cut line:**
- **Before the 02:00 feature freeze:** S1–S7, about 8 h of builder-first work.
- **If the Editor is free:** S8–S10.
- **After the hackathon:** S11–S12 (§6).

**User decisions:** DC1–DC7 (§7).

---

## 1. Inventory

### 1.1 Head-relative and hand surfaces

"Gaze" angles follow the head's pitch; "eye line" angles are measured from the horizontal.

| Surface | Built in (constants) | Placement: d · pitch · yaw · size | Shows when | Draws |
|---|---|---|---|---|
| **Status line** + coach line | `GuideRailBuilder.BuildStatusLine`; `StatusLine`: `distance` 0.9, `belowGazeDeg` 17, `maxWidth` 0.36, 72-char clip | 0.9 m · −17° gaze · 0° · ≤ 0.44 × 0.08 m | `GuideRail.Enabled` (DemoMode) and the current Step has text | heads-up overlay |
| **Toast** | `MainSceneBuilder.BuildToast` "Toast": 0.6 m, `belowGazeDeg` 12, `maxWidth` 0.26, 2 lines, 2.4–6 s | 0.6 · −12° gaze · 0° | any of about 75 `UiToast.Show` call sites. **Not gated by the rail**: `ToastEvents` hands only 4 kinds over | heads-up overlay |
| **Reply card** | same builder, "ReplyCard": 24°, 0.30 m, 4 lines, 5–10 s | 0.6 · −24° gaze · 0° | agent, voice and "What is this?" answers | heads-up overlay |
| **Job rail** (G4) | `GrokRailsBuilder.BuildJobRail`; `JobRailView`: 0.9 m, 14°, yaw −25 (−38 while the main slot is taken), W 0.32, 8 rows, 8 s summary | 0.9 · −14° gaze · −25° · 0.32 × ≈ 0.26 m | while `GrokRails.Job` exists; **no mode check** | heads-up overlay |
| **Next-step pill** | `GuideRailBuilder.BuildPill`: 0.45 m, 24°, +22°; 0.20 × 0.044 m plus 2 chips of 0.097 × 0.032 m | 0.45 · −24° · +22° | rail on and `Step.PillVisible` | world, re-centres |
| **Main slot** (`WindowSlot`) | `Window(rig, …, 0.45f, 20f, 0f)` and siblings. Notebook 0.34 × 0.37 (`BuildNotebookPanel`; claims the slot itself) · Sellers 0.36 × 0.37 · Checkout 0.36 × 0.44 · Survey card 0.36 × 0.20 (`BuildSurveyCard`) · Ladder card 0.32 × 0.23 (`LadderBuilder.BuildCard`) · GrokCard 0.36 × fit (`GrokPanelsBuilder.BuildCard`) · GrokOverlayCard 0.36 × 0.22 (`GrokOverlaysBuilder.BuildCard`) | 0.45 · −20° · 0° · up to 43.6° × 52° | one window at a time | world, re-centres |
| **Find parts** | `BuildPartsMenu` (`UiBuild.Distance` 0.5); `PartsBrowser` 0.5 / 15 / −35; 0.30 × 0.371 m. Not a FloatingWindow | 0.5 · −15° · −35° | World only, after the first tape or search; hidden in Tabletop | world |
| **Coach card** (G4) | `GrokRailsBuilder.BuildCoachCard`: FloatingWindow 0.45 / 12 / −24, `mainSlot` false; `CoachRailView` moves it to −44 while the main slot is taken; W 0.25 m | 0.45 · hangs from −12° · −24° (−44°) | while a coach runs and 12 s after; **no mode check** | world |
| **Scene window** | `BuildScenePanel`: `Window("ScenePanel", 0.5f, 18f, 35f, mainSlot: false)`, 0.34 × 0.392 m. Holds the **units chip** (`UnitsChipBuilder`, 0.1 × 0.026 m) and the **"Labels" toggle** in its title row (`GrokOverlaysBuilder.BuildSceneLabelsToggle`, at (0.045, H/2 − 0.026)) | 0.5 · −18° · +35° | ring ▸ Scene | world |
| **Enter world** | `CreateWorldButton`: `HeadAnchor` 0.45 / 25; 0.15 × 0.042 m plus a hint | 0.45 · −25° · 0° | Passthrough | world |
| **Scan credit chip** (G1) | `G1Builder.BuildCreditChip`; `SceneCreditChip` 0.62 / 36 / +30; 0.26 × 0.034 m, 2 lines | 0.62 · −36° · +30° | a Zabel scan is visible (World or Tabletop) | follows the head |
| **Palm ring** | `BuildPalmMenu`: 11 items, radius 0.105, arc ±118°, items visible to ±112°, Undo/Redo at ±142°, hint above the lens | 3 cm off the palm | palm up, or ☰ on the controller | hand |
| **Spec inspector** | `BuildSpecInspector`: 0.28 × 0.244 m left of the ring | beside the ring | a part is selected and the mode is World | hand |
| ↳ **Safety banner** (G3) | `GrokPanelsBuilder.BuildSpecBanner`: a 0.056 m strip above the inspector (a second one sits on Sellers) | | the verdict is recalled or caution | hand |
| ↳ **Rules chip** (G3) | `BuildRulesChip`: 0.04 m, moves up 0.062 m while the banner shows | | a rules check exists | hand |
| **Limits chip** (B3) | `BuildLimitsChip`: 0.12 × 0.026 m, 0.05 m above the left wrist; with no tracked left hand it falls back to 0.5 / 32 / −40 | wrist | limits are set, not in Passthrough | hand or head |

### 1.2 World surfaces and label caps

| Layer | Owner | Labels | Cap today |
|---|---|---|---|
| Tapes and areas | `MeasureView` | 2n+1 pills per n-gon | none (`LabelLayout` hides priority < 3 labels that don't fit) |
| B1 survey W×H | `MeasureTool.SurveyLabelBudget = 12` | 1 per object | 12, its own budget |
| G2 plan, survey pins, coverage, live labels, scene labels | `GrokOverlays` + `GrokOverlayMath.LabelBudget.Max = 12` | chips and captions; live labels expire after 20 s | 12 items across G2. **Captions are never hidden**; WorldChips aren't in `LabelLayout` |
| Fall edges | `FallEdges.maxLabels = 3` | "fall protection" | 3 |
| Part callouts | `PartOutline` | 1 per placed part, each array clone included | none; never hidden (priority 3) |
| Level, ladder, ask pins | `LevelGizmo`, `LadderView`, `SceneAsk` | 1 each | none |
| Coach overlay and drill crosshair | `CoachOverlay` (≤ 4 boxes plus the red stop card), `DrillCrosshair` | 1 | 1 |
| Reimagine / walk-in quad | `GrokPanelsBuilder.BuildQuad`: 1.5 m at the capture camera's pose. Otherwise it floats 1.5 m out, 8° down, **1.2 m wide (43.6°)**, with a 1.0 × 0.15 m label bar | – | 1 |
| Take-home parts, spawn pin, truth bar | `TakeItHome`, `BuildSpawnMarker`, `WirePresence` | 1 per part / 1 / 1 | Passthrough and Tabletop only |

### 1.3 Collisions

**Method.**
- Each surface is an angular box. Its centre comes from the builder's distance, down and yaw. Its half-extents are
  atan(W/2d) and atan(H/2d); the coach card hangs down from its pose.
- Assumptions: eye at 1.6 m, gaze level, typical sizes for panels that size themselves at runtime.
- Heads-up surfaces are drawn above everything, so any overlap with them hides content.

The first 18 rows are debts. The last two edge overlaps are tolerated, because side slots sit 5 cm behind the main
slot.

| # | Pair | Overlap (yaw × pitch) | Effect |
|---|---|---|---|
| 1 | Status line × main window | 27.5° × 4.8° | heads-up line drawn across the window's upper third |
| 2 | Toast × main window | 29.9° × 5.7° | same; seen in `uxw0/w0_slots_toast_reply.png` |
| 3 | Reply card × main window | 33.4° × 11.4° | 4 lines over the window |
| 4 | Reply × pill | 7.2° × 9.0° | reply covers the next step |
| 5–7 | Status / toast / reply × coach card | 5–8° × 3–11° | heads-up text over the coach's buttons |
| 8 | Reply × job rail | 1.8° × 3.9° | two heads-up panels touching |
| 9 | Job rail × main window | 6.9° × 16.4° | fixed by stepping aside to −38°… |
| 10 | Job rail (−38°) × Find parts | 20.2° × 16.4° | …which lands the rail entirely on Find parts |
| 11 | Job rail × coach card | 20.2° × 10.2° | heads-up rail over the coach card |
| 12 | Pill × main window | 12.3° × 10.7° | same 0.45 m depth: the two interpenetrate, and two primaries show |
| 13 | Pill × Scene window | 18.3° × 10.7° | |
| 14 | Coach card × main window | 13.3° × 26.4° | same 0.45 m depth |
| 15 | Coach card (−44°) × Find parts | 23.2° × 23.4° | both on the left; −44° is also outside A7's 35° |
| 16 | Credit chip × Scene window | 23.7° × 3.1° | |
| 17 | Credit chip × main window | 3.6° × 3.1° | |
| 18 | Limits fallback (−40°) × Find parts | 13.7° × 3.0° | |
| — | Status × toast × reply | stacked at 12° / 17° / 24° below the gaze | three heads-up pills in one 20° column |
| ok | Main × Find parts / Scene | 3.5° / 5.6° edges | tolerated: the side slot is 5 cm behind the main slot |

### 1.4 The worst demo moment

The worst moment is backend script step 7 ("Safety and the install coach"), played in the kitchen after the app's
measure → find flow. DemoMode is on, the Midea is selected, and the reimagine from step 6 is still up. The drill step
has just stopped.

| Zone | Up at once | n |
|---|---|---|
| Heads-up | status line + coach line · reply card (the spoken "Stop: that spot is straight above an outlet…") · toast (the miss) | 3 |
| In front | Next-step pill ("Compare prices") · GrokCard (the recall notice, opened from the RECALLED strip) | 2 |
| Sides | coach card (at −44°, because the slot is taken) · Find parts (a search has run) | 2 |
| Hand | the palm cluster (ring + inspector + RECALLED banner + rules chip; palm up to read the recall) · limits chip | 2 |
| World | reimagine quad + bar · drill crosshair · stop box + red card · plan layer from step 5 (runs, ghosts, "Place 3", lengths) · scan labels from step 4 · tapes + part callouts | 6 |
| **Total** | **15 surfaces.** 7 head-relative, 3 of them heads-up; 6 overlapping pairs (#1–4, #12, #15); labels = 12 (G2) + captions + 2n+1 per tape + 1 per part | |

**After S1–S7:**
- status line, docked on the GrokCard, with the reply taking its place for 5–10 s;
- GrokCard;
- coach card (left slot; Find parts hidden);
- the wrist strip;
- palm on demand;
- quad, crosshair and stop card, and one Grok layer.

That is **3 head-relative surfaces, 0 overlaps and ≤ 12 labels** (S10).

### 1.5 Defects found along the way (fixed in S1 unless noted)

1. **Demo reset leaves Grok state behind.** `Voice/DemoReset.cs:45-96` never clears `GrokRails`, `GrokOverlays`,
   `GrokCard` or `ReimagineQuad`. `JobRailView` and `CoachRailView` have no mode check, so the last judge's rail or
   coach card can greet the next judge in passthrough.
2. **`AppCommands.CloseWindows` misses some cards.** It closes Sellers, Checkout, Notebook and Scene, but not GrokCard,
   GrokOverlayCard, SurveyCard or LadderCard.
3. **G2 captions escape G2's cap**, and G2 WorldChips skip `LabelLayout` (fixed in S10).
4. **`UiToast` ignores the rail**: about 75 call sites still toast under the status line (fixed in S3).
5. **The limits chip's −40° fallback** is outside A7's |azimuth| ≤ 35° (fixed in S9).
6. **Windows are oversized:** main windows are 43.6° wide and up to 52° tall, against Fieldglass's ≤ 37° × 28°. That
   is Wave 3 work and not in scope here; S6 docks the line so it holds even for Checkout.

---

## 2. Screen-real-estate budget

These are maximums. "–" means the zone stays empty; "docked" means the heads-up line sits on the open window's rim
(M3).

| Mode · activity | Heads-up line | Main slot | Left side | Settings (right) | Pill | Hand | World layers | Labels | Head-relative max |
|---|---|---|---|---|---|---|---|---|---|
| Passthrough · entry | 1 (coach) | – | – | – | Enter control instead | ring on demand | – | – | 2 |
| Passthrough · take it home | 1 | ≤ 1 (Notebook or report QR) | – | – | ≤ 1 if no window | ring | take-home parts | ≤ 4 | 2 |
| Tabletop (Model view) | 1 | ≤ 1 | – (Find parts hidden, as today) | ≤ 1 | ≤ 1 if no window | ring, wrist strip ("Model 1:N") | the model floating centred in front of you at eye height (modelwheel; fitted ≤ 0.9 m), the pin, the wheel of models under it facing you (world-locked, placed on entry); **no annotation layers** (modelview, the user's call: the model only) | 0 | 3 |
| World · measuring | 1 | – | ≤ 1 (Find parts after the first tape) | – | 1 | ring on demand | tapes + reticle | ≤ 12; the focused shape shows in full | 3 |
| World · finding parts | 1 | – | 1 (Find parts) | – | 1 | ring | held part + outline | ≤ 12 | 3 |
| World · checkout | 1, docked | 1 (Sellers → Checkout → Receipt) | – (Find parts yields) | – | – | wrist strip | the world dimmed (W1.6) | ≤ 6 | 2 |
| World · Grok job running | 1 (progress) | ≤ 1 (the stop: the pay panel or a recall) | ≤ 1 | – | – (the job *is* the next step) | wrist strip | 1 Grok layer | ≤ 12 | 3 |
| World · coaching | 1 | ≤ 1, on demand | 1 (coach card; Find parts yields) | – | – | – | crosshair + stop card + ≤ 1 Grok layer + quad | ≤ 8 (stop card first) | 3 |
| World · survey on screen | 1, docked | 1 (survey or condition card) | ≤ 1 | – | – | – | 1 survey layer | ≤ 12 | 3 |
| Any · settings | 1 | ≤ 1 | – | 1 | – | – | – | – | 3 |

**Invariants** (tested in S2):
- There are 0 or 1 heads-up surfaces.
- There are ≤ 3 head-relative panels. The pill counts only while the main slot and Settings are empty.
- Only the ring on the palm; the inspector leaves after the hackathon (M12). Until then it is the one exception.
- In the world, ≤ 1 Grok layer plus the coach overlay and quad, and ≤ 12 labels.

For comparison, today's worst case has 7 head-relative surfaces plus the palm cluster, 3 of them heads-up, and labels
capped at 12 + 12 + 3 + uncapped.

---

## 3. One home per class of information

### 3.1 The zone map: `Runtime/UI/UiZones.cs` (new; builders read it instead of literals)

| Zone | Distance | Pitch | Yaw | Max size | Draws | Holds |
|---|---|---|---|---|---|---|
| `Status` | 0.9 m | −17° from the gaze | 0° | 0.44 × 0.12 m (28° × 8°) | heads-up overlay; **docks** on the open main window (M3) | status, flash, reply, progress, coach |
| `Main` | 0.45 m | −20° from the eye line | 0° | 0.36 × 0.44 m today (Wave 3 target 0.30 × 0.22) | world, re-centres past 40° | cards: one at a time (`WindowSlot`) |
| `SideLeft` | 0.50 m | −15° | −35° | 0.30 × 0.40 m | world | Find parts **or** the coach card |
| `SideRight` | 0.50 m | −18° | +35° | 0.34 × 0.43 m | world | Settings |
| `Pill` | 0.45 m | −24° | +22° | 0.20 × 0.084 m | world | the next step, only while `Main` and `SideRight` are empty |
| `Enter` | 0.45 m | −25° | 0° | 0.15 × 0.06 m | world | Passthrough only |
| `Wrist` | 0.05 m above the left wrist or controller | – | – | 0.16 × 0.05 m | hand | limits · scale · credit |
| `Palm` | 0.03 m off the palm | – | – | ring radius 0.105 m | hand | the ring |
| `Quad` | 1.5 m at the capture camera's pose (floating fallback 1.5 m, −8°) | | | ≤ 1.2 m wide | world | one picture or video |

- Side slots sit 5 cm behind the main slot. Their inner edge may overlap the main window by ≤ 6°, because the main
  window is in front.
- No other pair of head-relative surfaces may overlap (the S2 test).

### 3.2 Homes

| Class | Home | Rule | Moves in |
|---|---|---|---|
| **Status and guidance**: one sentence you only read | the **status line** | Line 1 ≤ 48 chars: status, a 2.4–6 s flash, or progress. Line 2 ≤ 72 chars: coach, spoken step or detail. A reply takes the line's place for 5–10 s (≤ 4 lines). Never a second heads-up surface | the toast (M1), the reply card (M1), the job rail (M2) |
| **Decisions and rich content**: buttons, rows, pictures, QR | the **main slot** | Claims `WindowSlot`, closing the previous card. One primary per view, so the pill yields (M3) | already: Notebook, Sellers → Checkout → Receipt, Survey, Ladder, GrokCard, GrokOverlayCard |
| **Reference kept open while you work in the scene** | the **left side slot** | One at a time | the coach card (M4) |
| **Settings, layers, recovery** | **Settings** (right side slot) | Opened from the ring. Picking a tool or Exit closes it | Home, Ladder, Exit world, the layer toggles, Snapping (M6–M8) |
| **Glanceable context** | the **wrist strip** | ≤ 2 lines. Hidden when the wrist isn't tracked; never head-locked | limits, scale, the scan credit (M9) |
| **Tools and navigation** | the **ring** | ≤ 6 items | – |
| **Facts about a thing in the scene** | **on the thing**, through the pool of 12 | One label per fact; beyond the pool a label becomes a dot | every overlay's labels (§5) |
| **Pictures and video** | the **quad** | One at a time, at the capture camera. Stills ≤ 0.2 m may sit inside a card (postcard, QR) | – |

### 3.3 The rule for a new surface (the PR checklist)

1. **Is it a fact about something in the scene?** It is a label or overlay on that thing, claimed from
   `WorldLabels`. Stop.
2. **Is it a sentence the user only reads?** Use `StatusLine.Flash`, `SetProgress` or `UiToast.Reply`. Stop.
3. **Does it ask for a decision, or show rows, pictures or a QR?** It is a card in the main slot (`FloatingWindow`,
   `mainSlot = true`).
   - If it must stay up while the user works in the scene, it goes in the left side slot.
   - If it is a setting or a recovery action, it is a row in Settings.
4. **Is it context the user glances at now and then?** It is a line on the wrist strip.
5. **Is it a picture or video of the place?** It goes on the quad.
6. **Nothing else gets its own head-relative pose.** New placements are added to `UiZones`, and `DeclutterTests` must
   stay green.

---

## 4. Merge, move, cut (decided)

**M1 · Toasts and replies go into the status line (DC2).**
- **Toasts.** While `GuideRail.Enabled` is on, `UiToast.Show` calls `StatusLine.Flash(text, tone,
  UiToast.Duration(…))`. Line 1 shows the flash, then the Step's text returns.
- **Replies.** `UiToast.Reply` draws at the status line's pose (0.9 m, −17°, 4 lines, `maxWidth` 0.45 m, the same
  28° as today's 0.30 m at 0.6 m), and the status line yields while the reply is up.
- **Fallback.** With the rail off, today's toast and reply card come back.
- **Logs.** Keep the `Toast:` and `Reply:` log lines, which `tools/demo/hcheck.py` reads.
- **Result.** Two heads-up surfaces and the stacked column go, clearing debts #2, #4 and #8. The reply's overlap with
  the window (#3) folds into #1, which M3 clears.

**M2 · The job rail becomes a mode of the status line (DC1). Yes.**
- **Line 1** while `GrokRails.Job` shows: "Do the whole job · 5 of 7" followed by a strip such as
  `✓ ✓ ✓ – • · ·`.
  - The glyphs reuse `GrokRailText.Glyph`/`Dot`: ✓ Done/Said, – Skipped, ✗ Stopped, plus • Active and · Pending.
  - All are in the Inter atlas. Each has a glyph plus a `UiTheme` colour, never colour alone.
- **Line 2** is the newest spoken result (≤ 72 chars).
- **job_done** shows the summary ("✓ Done · $899.00 · $749.00 after rebates") for 8 s, then NextStep takes over.
- **Rail off.** Progress shows even with the rail off.
- **Where the old panel goes.** `JobRailView` stays as the model → text bridge, with a `standalone` flag as the
  fallback. The 8-row panel is no longer shown; the per-step results already go to the notebook and the packet.
- **Result.** This removes a 0.32 × 0.26 m heads-up panel and debts #9–11.

**M3 · The line docks and the pill yields.**
- **Docking.** While `WindowSlot.Current` is open, the status line straddles the window's top rim like a visionOS
  ornament:
  - centre = panel top + 5 mm;
  - the window's rotation;
  - scale 0.45 / 0.9, so its angular size holds.

  The coach line hides. Flash, Busy, Bad, progress and replies still show. On Checkout, the tallest window (0.44 m),
  the line's centre lands at ≈ +6.7°, under the +10° ceiling. Floating a clear gap above it instead would put it at
  +11°.
  - **Glass lane (Sat night, after the gate capture of a toast lying on the Adjust panel's Size & finish row):** with
    DemoMode off the rail is off, so toasts use the fallback pill, which never docked. Now the line, the reply and the
    fallback toast all dock **wholly above** the rim: each pill's bottom edge sits on panel top + 5 mm
    (`StatusLine.DockCentre`, lifted across the line of sight), each scaled so its own angular size holds, and a toast
    stacks above a showing line. The line's centre lands at ≈ +8° on the 0.44 m windows; a four-line reply's or a
    toast's at ≤ +13° (`DeclutterTests.DockedLineClearsTheWindow`; live: `AgentHarness.GlassDock()`).
- **The pill.** It hides while the main slot or Settings is open. The window's own primary is the next step (one
  primary per view).
- **The receipt.** Checkout's receipt block gets its own **Take it home** primary (the W1.6 CTA), so the judge path
  stays at 10 actions.
- **Result.** This clears debts #1, #3, #12 and #13.

**M4 · The coach card moves to the left side slot, not the main slot (DC4).**
- **Why not the main slot.**
  - A drill step needs the lower centre clear to aim the crosshair at the wall.
  - In the main slot the coach would evict every Grok answer asked for mid-coach (script steps 7 → 8).
  - It would also fight them: `CoachRailView` reopens its window whenever the coach wants it.
- **The new placement.**
  - 0.5 m, −15°, −35°, W 0.25 m (28°), built at `UiBuild.Distance` 0.5.
  - No step-aside to −44°.
  - Find parts hides while a coach is up, and during Sellers and Checkout.
  - The crosshair's 22° hold cone follows the card.
- **Result.** Clears #5–7, #14 and #15. The remaining 0.8° edge against a 0.36 m main window is tolerated.

**M5 · The G2 overlay card and the G3 card: don't merge before the freeze.**
- **Why wait.** They already share the one main slot, so they are never on screen together. They also share the same
  chrome: W 0.36, title and Close in the top row, primary at the bottom left. A merge buys no screen space for the
  risk it puts on G2 (14/14 + 10/10) and G3 (14/14).
- **After the hackathon.** Merge them into one `GrokCard` with the kinds Plan, Survey, Coverage and Labels (Place →
  primary, Hide → secondary).
- **Now.** `CloseWindows` closes every main-slot card (S1).

**M6 · The ring goes from 11 items to 6 (DC3).**
- **The six:** Move · Measure · Level · Notebook · Model view · Settings. This is W1.5's list, with Model view in the
  Parts seat until a Parts mode exists.
- **Moved to Settings:** Ladder, Fall edges, Home, Exit world and Scene.
- **Cut:** Step in. The "You are here" pin and Model view's toggle both step in.
- **Geometry.**
  - Spacing becomes 60°. With Measure on the lens, Level and Move sit at ±60°, Notebook and Settings at ±120°, and Model
    view one detent away at 180°.
  - `ToolRing.visibleHalfAngle` 112 → 128°.
  - A new `fadeBandDeg` goes from 28 → 12°. With the old 28° band the ±120° items would be 6% opaque; with 12° they are
    74%.
  - `buttonAngle` 142 → 150°. Undo/Redo then sit 5.4 cm from the ±120° items (today 4.0 cm).
- **Icon.** The gear: Phosphor gear-six, U+E272 (`Icons.Settings`, after Build UI Assets). Until 2026-09-26 the item was
  "More" with `Icons.Scene`.

**M7 · Settings is the Scene window, renamed (right side slot).** The rows, top to bottom:
1. "Settings" + Close.
2. Status: scene · Scale ✓ · credit.
3. [Home] [Ladder] [Exit world, tap twice].
4. Layers: [Show edges] [Labels] [Fall edges].
5. [Snapping] [Contrast] [Less motion].
6. The scale keypad and the units chip.
7. Ask / Hold to talk.
8. Sites: Next scene / Reload / Built-in.

That is one extra row (0.034 m), so H goes from 0.392 to 0.426 m. Wave 3 splits Settings into sheets. Picking Ladder or
Exit closes Settings.

**M8 · The "Labels" toggle moves out of the title row into Settings' Layers row.**
- Title rows hold only a title and Close, as in every other window.
- Layer toggles live together: Show edges (structure), Labels (scan labels) and Fall edges.

**M9 · One wrist strip (DC5).**
- `LimitsChip` becomes the wrist strip: ≤ 2 lines, 0.16 m wide, 0.05 m above the left wrist or controller.
- It carries the limits, the scale state ("Scale ✓" / "Scale not set") and the scan credit.
- Hidden when neither the wrist nor a controller is tracked; the −40° head fallback goes.
- The credit chip follows the head only for 8 s after a Zabel scan becomes visible. After that it lives on the wrist
  strip and in Settings' status line.
- **Units stays a control in Settings.** It changes every label (D2); it is a setting, not status.

**M10 · One Grok world layer at a time (DC6).**
- A new G2 kind hides the other kinds' sets:
  - a plan hides live labels and pins;
  - a survey hides the plan.
- Scene labels show only through Settings ▸ Labels.
- The coach overlay and the quad are separate from this rule.
- Closing the overlay card keeps its layer; the next Grok layer replaces it.

**M11 · One label pool (DC7):** see §5.

**M12 · After the hackathon.**
- The spec inspector moves off the palm into a main-slot Part sheet (W1.5), taking its RECALLED and rules strips.
  The palm cluster goes from 0.53 m to 0.25 m.
- Merge the Grok cards (M5).
- Put WorldChips in `LabelLayout`.
- Size windows to 37° × 28°.
- Count labels in view, with gaze focus tiers (W2.6).

---

## 5. One label budget

### 5.1 Rules

- **One pool: `WorldLabels.Max = 12`** visible world labels across all producers.
  - Text labels and chips count. Markers, lines, outlines and dots don't.
- **Allocation.** Allocate by class (§5.2), and within a class newest first.
  - Mandatory labels count first but are clipped to the pool too. Today G2's captions escape it.
- **Over the pool,** a label drops to its anchor dot (1.2°) or its outline, never to a smaller label.
  - The focused shape (made in the last 6 s, or hovered) keeps its full set of sides and angles. It counts as one
    claim of up to 4 labels.
- **One label per fact:**
  - a saved shape gets one summary ("W × H", a length or an area);
  - an array gets one group label ("8 × hanger · all fit ✓");
  - a B1 size group keeps its label on the focused or newest member, and the rest become dots.
- **Freeze scope.** Count everything active, not just what is in view. That keeps it deterministic and testable;
  in-view counting and focus tiers are W2.6.
- **Recompute on change,** from a producer's dirty flag, not every frame. No allocation per frame.

### 5.2 Priority (highest first)

| Class | Labels | Share |
|---|---|---|
| **P0 Safety** | the coach's "Don't drill here" card · fall-edge labels while Fall edges is on · RECALLED on a placed part | ≤ 3 |
| **P1 Honesty + focus** | the Grok layer's honesty caption ("AI triage from drone frames, not an inspection", "AI preview") · the live or just-made shape · the held part's fit label · a new ask pin (30 s) | 1 caption + ≤ 4 + 1 + 1 |
| **P2 The active task** | the newest Grok layer's items (plan lengths and "Place N"; pins by severity; live labels by confidence) **or** the B1 survey (unverified → focus → newest), whichever is newer | the rest |
| **P3 Saved measurements** | one summary pill per shape, newest first | the rest |
| **P4 Placed parts** | one callout per part; arrays get one group label | the rest |
| **P5 Readings** | level, ladder | the rest |
| **P6 Passive** | scene labels (toggle), ask pins older than 30 s | the rest |

**Why this order.**
- Safety and honesty are never optional: principle 2 ("every no says why"), and "not an inspection" is the Grok
  features' legal line.
- What you are doing now beats what you did.
- Measurements, which are the product, beat parts and passive labels.

### 5.3 Producers

| Producer | Today | In the pool |
|---|---|---|
| `GrokOverlays.ApplyBudget` | 12 across G2; captions uncapped | caption → P1 (one: the active layer, after M10); items → P2 |
| `MeasureTool.RefreshSurveyLabels` | `SurveyLabelBudget = 12` | one P2 claim that keeps its own order |
| `MeasureView` | 2n+1 per shape | focused shape → P1 (≤ 4); other shapes → P3 × 1 |
| `FallEdges` | `maxLabels = 3` | P0 (≤ 3) |
| `PartOutline` | 1 per part, never hidden | P4; an array → 1 group label |
| `LevelGizmo`, `LadderView` | uncapped | P5 |
| `SceneAsk` | uncapped | P1 while new, then P6 |
| `CoachOverlay` stop card | 1 | P0 |

**Code.** A new `Runtime/UI/WorldLabels.cs` holds:
- a pure `Allocate(IReadOnlyList<Claim>, int max)`. It generalises `GrokOverlayMath.LabelBudget.Split`: each claim
  is a class, a mandatory count, an item count and a newest time;
- a small registry that each producer notifies on change.

`LabelBudget.Split` stays as a wrapper, so its tests keep passing.

---

## 6. Implementation order

**The gate for every slice** (README §3):
1. `recompile`, then 0 `error CS` in `console`.
2. `run_tests --mode editor --filter AirTools --filter_type assembly`.
3. `RunDemo` 27/27.
4. The slice's own check.
5. A capture, if the slice is visual.

One commit per slice.

**Capture recipe (C-kitchen).**
1. Enter Play with `AgentHarness.DemoOn(true)` and `GuideOn(true)`, and load the kitchen.
2. Run `GrokLook("kitchen-labels")` (camera 0221) and stage the state.
3. `unity command capture_game_view --source screen --save_path Temp/declutter-<slice>.png`.
4. Move the file to `SpikeData/declutter/` and delete `Assets/Temp`.

**The census** (added in S2):
- `AgentHarness.Surfaces()` prints one line, for example `hud=1 docked=Checkout main=Checkout sideL=- sideR=- pill=0
  wrist=limits palm=closed quad=0 layers=[plan] labels=9/12 overlaps=0`.
- `SurfaceCheck("<activity>")` logs `[AirTools.Check] UI.budget.<activity>` against that activity's §2 row.

| # | Slice | h | Files | Acceptance check | Risk |
|---|---|---|---|---|---|
| **S1** | **Grok hygiene** (§1.5 #1–2) | 0.5 | `Voice/DemoReset.cs`: `GrokRails.Clear()`, clear `GrokOverlays`, close GrokCard, GrokOverlayCard and ReimagineQuad. `Agent/Grok/JobRailView.cs` and `CoachRailView.cs`: nothing shows in Passthrough. `Voice/AppCommands.cs` `CloseWindows`: add GrokCard, GrokOverlayCard, SurveyCard and LadderCard | **Harness:** `GrokStage("g4:job")` → `ResetDemo()` → `ResetCheck()` PASS with a new "grok" need; `GrokRailsStatus()` shows no job. **EditMode:** new `DemoControlsTests` case | Low |
| **S2** | **Census + guard** | 1 | **New:** `Runtime/UI/UiZones.cs` (zone constants; pure `Footprint(d, down, yaw, w, h)` and `Overlap`), `Runtime/Dev/UiCensus.cs`, `Tests/EditMode/DeclutterTests.cs`. **Changed:** `Runtime/Dev/AgentHarness.cs` (`Surfaces()`, `SurfaceCheck()`) | **EditMode:** `DeclutterTests.HeadRelativeSurfacesDontOverlap` opens Main.unity (as `ErgonomicsTests` does), reads each surface's own constants and panel size, and asserts overlaps == `KnownDebts` (the 18 rows of §1.3); later slices delete their rows. **Harness:** `Surfaces()` prints in Play | None; no behaviour change |
| **S3** | **One heads-up line** (M1) | 1 | `UI/UiToast.cs` (route to the line while the rail is on) · `UI/StatusLine.cs` (`Flash`, `Yield`) · `Editor/MainSceneBuilder.cs` `BuildToast` (reply: `UiBuild.Distance` 0.9, `distance` 0.9, `belowGazeDeg` 17, `maxWidth` 0.45) · `ErgonomicsTests` A9 (reply at 17°) | **EditMode:** `DeclutterTests.ToastBecomesStatusFlash` (rail on; `UiToast.Show("✓ Saved · Width 1′ 3⅜″")` → `StatusLine.Message` equals it, the toast content is inactive, and after `Duration` the Step's text is back) and `ReplyTakesTheLine`. **Harness:** `GuideOn(true)`, `RunM2(3)`, then the max `hud` in `Surfaces()` is 1. A1 stays green. Debts #2, #4, #8 and the stack go (#3 folds into #1) | Low–medium: keep the `Toast:` log line for hcheck |
| **S4** | **Coach card → left side slot** (M4) | 1 | `Editor/GrokRailsBuilder.cs` `BuildCoachCard` (`UiBuild.Distance` 0.5; 0.5 / 15 / −35) · `Agent/Grok/CoachRailView.cs` (yaw −35, drop `yawDegBesideWindow`) · `Parts/PartsBrowser.cs` (hidden while `GrokRails.Coach` shows or `WindowSlot.Current` is Sellers or Checkout) | **Harness:** `GrokStage("g4:coach-check")` → `Surfaces()` shows `sideL=coach` and Find parts hidden; `GrokCheck("g4")` 19/19. Debts #5–7, #14 and #15 go; A1 green. **Capture:** C-kitchen + `GrokStage("g4:coach-stop")` | Low |
| **S5** | **Job strip** (M2) | 1.5 | `Agent/Grok/GrokRailText.cs` (`JobStrip`, `JobLine`) · `Agent/Grok/JobRailView.cs` (`standalone = false` → push to the status line) · `UI/StatusLine.cs` (`SetProgress`, which shows with the rail off) · `Editor/GrokRailsBuilder.cs` · `Runtime/Dev/GrokRailsHarness.cs` (`G4.job.view` reads the status line) | **EditMode** (`GrokRailsTests`): with tags stripped, `JobStrip` gives one glyph per dot in order; `JobHappy` done == 7 × `✓`; `JobRecall` ends in `✗`; a run fed to its first `job_step` has `•` and `·` after the first `✓`; line 1 ≤ 48 chars + the strip. **Harness:** `GrokStage("g4:job")` → `Surfaces()` shows `hud=1` and no job rail; `GrokCheck("g4")` 19/19. Debts #9–11 go. **Capture:** C-kitchen + `GrokStage("g4:job")` | Medium: the G4 check and backend step 3's wording |
| **S6** | **Dock + pill yields** (M3) | 1.5 | `UI/StatusLine.cs` (dock pose) · `UI/FloatingWindow.cs` and `Notebook/NotebookPanel.cs` (`PanelTop`, live from the "Panel" `GlassSurface`, for runtime-sized cards) · `UI/NextStepPill.cs` (hidden while the main slot or Settings is open) · `Editor/MainSceneBuilder.cs` `BuildCheckoutPanel` + `Parts/CheckoutPanel.cs` (a **Take it home** primary on the receipt) | **EditMode:** `DeclutterTests.DockedLineClearsTheWindow`. For every main window at eye heights 1.20–1.76 m, the docked line covers no window content below the title row, its centre pitch is ≤ +10°, and its angular size is within 5% of the undocked line. **Harness:** `ShowNotebook(true)` → `Surfaces()` shows `pill=0 docked=Notebook`; `JudgeCheck(10)` PASS. Debts #1, #3, #12 and #13 go. **Capture:** C-kitchen with Checkout open (`RunM5` up to the hold) | Medium: runtime-sized panels |
| **S7** | **Ring v2 + Settings** (M6–M8) | 1.5 | `Editor/MainSceneBuilder.cs` (`BuildPalmMenu` defs; `BuildScenePanel` title and rows) · `Editor/GrokOverlaysBuilder.cs` `BuildSceneLabelsToggle` (Layers row) · `Input/ToolRing.cs` (`visibleHalfAngle` 128, `fadeBandDeg` 12, `buttonAngle` 150) · `Runtime/Dev/DemoWalkthrough.cs` (ring labels "Exit world" / "Tabletop") · `docs/demo.md` (§8) | **EditMode:** `ToolRingTests.MainRingHasSixItems`. The Main.unity ring has 6 items. With Measure selected, 5 have fade ≥ 0.7 and the sixth is hidden. Every item with fade > 0.3 is ≥ 0.04 m from Undo/Redo. Also `DeclutterTests.TitleRowsHoldOnlyTitleAndClose`. **Gate:** `RunDemo` 27/27; A1 green. **Capture:** `Services.Get<PalmMenu>().Force(true)` with `controllerAnchor = null`, with Settings open | Medium: RunDemo and hcheck beats; the ring's feel is an [H] check |
| — | **Feature-freeze cut line (~8 h)** | | | | |
| **S8** | **One Grok layer** (M10) | 1 | `Agent/Grok/GrokOverlays.cs` (showing a kind hides the other kinds, except SceneLabels) · `Runtime/Dev/GrokG2Check.cs` (new `G2.one_layer`) | **Harness:** `GrokCheck("g2")` 14/14 on the kitchen and 10/10 on the facade, plus `G2.one_layer` (after `show_labels` then `show_plan`, the labels set is inactive). **Capture:** `GrokLook("kitchen-plan")` | Low–medium: backend steps 4 → 5 |
| **S9** | **Wrist strip** (M9) | 1 | `Editor/MainSceneBuilder.cs` `BuildLimitsChip` · `Parts/LimitsChip.cs` (limits / scale / credit lines; no head fallback) · `Scene/SceneCreditChip.cs` (8 s after it becomes visible) · `Editor/G1Builder.cs` · `Runtime/Dev/GrokHarnessG1.cs` | **EditMode:** a pure timing test: the credit is shown from 0–8 s and hidden at 9 s, and the wrist text contains "CC BY 3.0" while a Zabel scan is loaded. **Harness:** `GrokCheck("g1")` 13/13. Debts #16–18 go, leaving `KnownDebts` empty. **Capture:** Zabel with the controller at the wrist-view pose (Operator) | DC5 licence sign-off; the G1 credit check |
| **S10** | **Label pool** (M11, §5) | 2 | **New:** `Runtime/UI/WorldLabels.cs`. **Changed:** `Agent/Grok/GrokOverlays.cs`, `Agent/Grok/GrokOverlayMath.cs`, `Tools/MeasureTool.cs`, `Tools/MeasureView.cs`, `Structure/FallEdges.cs`, `Parts/PartOutline.cs`, and `AgentHarness.LabelCount()` | **EditMode:** `WorldLabelsTests` covers the priority order, mandatory labels clipped to 12, newest first, and a stable result across calls. **Harness:** on the kitchen, `GrokCheck("g2")` (includes the fixture B1 survey card) + `BackendHarnessB1B3.RunB1()` (18 doors; needs the patched backend) + `RunM2(5)` + `RunM3(5)` → `LabelCount()` ≤ 12 and the newest shape fully labelled. **Capture:** `GrokLook("kitchen-survey")` before and after | Medium–high: five producers, and the B1, G2 label-diet and M2/M3 checks. Split into **S10a** (G2 + survey + fall edges share the pool, 1 h) and **S10b** (tapes, parts, level) if needed |
| — | **After the hackathon** | | | | |
| S11 | Part sheet (M12, W1.5) | 2 | `BuildPalmMenu`, `BuildSpecInspector`, `GrokPanelsBuilder.BuildSpecBanner`, `SpecCard.host = null` | `PartInfo()` unchanged; palm ≤ 0.26 m wide; M4/M5/B3 green | High before the freeze: it sits on the demo path |
| S12 | Merge the Grok cards (M5) · WorldChips in `LabelLayout` · windows to 37° × 28° · in-view label counting | – | | | |

**[H] items the Simulator can't cover:**
- the ring's 60° detents and that ±120° items are readable;
- the docked line's legibility over a bright kitchen;
- that the wrist strip is readable with the wrist raised (hands and controller);
- the coach card at −35° while drilling.

Add them to `docs/headset-checklist.md` with S7 and S9.

---

## 7. Decisions for the user

| # | Decision | Options | Recommendation |
|---|---|---|---|
| DC1 | Where "do the whole job" progress lives | Its own heads-up rail at −25° (today) · a mode of the status line · a main-slot card | **A mode of the status line**: "Do the whole job · 5 of 7" with a 7-glyph strip, the newest spoken line under it, and the summary for 8 s. The 8-row detail goes to the notebook and the packet. Backend step 3 then reads "the status line becomes the job strip" |
| DC2 | Toasts and replies while the guide rail is on | Separate pills at 0.6 m (today) · into the status line | **Into the status line.** A toast is a 2.4–6 s flash on line 1; a reply takes the line's place (4 lines, 5–10 s). With the rail off, today's toast is the fallback |
| DC3 | The ring | 11 items (today) · 6 fixed · filtered by mode | **6 fixed: Move · Measure · Level · Notebook · Model view · Settings.** Settings (the Scene window, renamed) holds Home, Ladder, Exit world, the layer toggles and settings. Step in is cut. The demo leaves through the "Take it home" button |
| DC4 | The install-coach card | A side card at −24° that jumps to −44° (today) · the main slot · the left side slot | **The left side slot** (0.5 m, −15°, −35°), taking Find parts' place while a coach runs. The lower centre stays clear for the drill crosshair, and Grok answers can still use the main slot mid-coach |
| DC5 | Glanceable chips and the Zabel credit | Three chips in three places (today) · one wrist strip | **One wrist strip** (limits · scale · credit). The credit also shows head-anchored for 8 s when the Zabel scan loads, and stays in Settings' status line, on the slides and in the video captions. **Confirm with the backend team** that this meets their "under every Zabel shot" (CC BY 3.0 asks for a reasonable credit, not a permanent HUD). Units stays a control in Settings |
| DC6 | Grok world overlays | All layers coexist (today) · one Grok layer at a time | **One at a time.** A plan replaces live labels and pins; a survey replaces the plan; scan labels show only through Settings ▸ Labels. Backend step 5 then shows the plan without step 4's labels |
| DC7 | Which labels win the 12 | Per-overlay caps (today: 12 + 12 + 3 + uncapped) · one pool with a priority order | **One pool of 12:** safety → honesty caption and the thing you're doing now → the active task (Grok layer or survey) → saved measurements → parts → level and ladder → passive. Over the pool, a label becomes its dot |

**Decided within design** (no call needed; each is reversible behind its slice's flag or commit):
- M3: dock the line; the pill yields; the receipt gets Take it home.
- M5: no Grok-card merge before the freeze.
- M7 and M8: Settings' rows; Labels moves into Layers.
- M12: after the hackathon.

When the user decides, record DC1–DC7 as one SPEC §9 row, as was done for D1–D7.

---

## 8. Knock-on edits (in the same commit as the slice that causes them)

**`docs/demo.md` (S7):**
- §3 step 6a: "ring ▸ Scene" → "ring ▸ Settings".
- §4 beat 2: the ring list becomes "Move, Measure, Level, Notebook, Model view and Settings".
- Beat 12: "Ring ▸ Tabletop" → "Ring ▸ Model view".
- Beat 13: "Ring ▸ Exit world, pinch twice" → "**Take it home** on the receipt, or Settings ▸ Exit world (tap twice)".
- §5: "Scene window ▸ Reload / Next scene / Built-in" → "Settings ▸ …".

**`docs/demo.md` (S3):** with DemoMode on, the toast lines in beats 1, 4, 5, 8, 11 and 13 are read on the status line.

**The backend demo script** (`~/airtools-backend-demo/docs/integration-grok-features.md`, "Demo script"; hand it to
P4):
- Step 3: "draws the rail" → "turns the status line into the job strip".
- Steps 4–5: the labels go when the plan draws; Settings ▸ Labels brings them back.
- Step 7: the coach card sits on the left.
- Step 11: the credit is on the wrist strip and the slide.

**Also:**
- `docs/UI.md` §5 Placement: replace its table with §3.1's zone map.
- §8 of that file: the ring's items.
- SPEC §10: a progress entry per slice.
