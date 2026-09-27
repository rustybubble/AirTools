# Headset checklist (Quest 3S): one session, about 15 minutes

This file turns every open **[H]** check into one ordered session on the headset. `tools/demo/hcheck.py` then
reads the session's logcat and ticks off everything the log can prove. It is built from these sources:

- SPEC §6 (the [H] checks for every milestone) and the "Open [H]" lines in §10;
- `docs/backend-integration.md` ("Still open" and "Headset session");
- `docs/UI.md` §8 (the tool ring).

Build: `backend-integration` @ `b2fd73a`, which adds the tool ring.

**The UI has changed since most of these checks were written:**

- The palm toolbox is now a Liquid Glass **tool ring**. It opens when you turn the left palm **up**.
- **Move** is the default mode.
- Two quick pinches with the left hand go back to Move.
- **Undo** and **Redo** are the capsules at the two ends of the ring.
- There is **no Done button**. To finish a shape, do one of these:
  - pinch with the other hand;
  - pinch thumb + middle finger;
  - pinch the first point again to close the polygon.
- The chest is now an **Enter world** pill.
- The hip toolbox and the wrist panel are gone.
- The seller carousel is now a **seller panel** with sort buttons.

Retired checks are listed at the end.

Each check has an id, and `hcheck.py` prints one row per id. The **kind** of a check says how it is proven:

| kind | meaning | how many |
|---|---|---|
| `log` | the log proves the whole check | 22 |
| `log+eye` | the log proves the event happened; you still confirm the look or feel | 22 |
| `eye` | nothing in the log can prove it; tick it by hand | 6 |

Tick the `log+eye` and `eye` items in the table at the end while they are fresh.

## Before you put it on (laptop, 3 min, not counted)

1. **Server.** In the backend repo, run:

   ```
   uv run uvicorn server.app:app --host 0.0.0.0 --port 8000
   ```

   It needs the kitchen in its `SCENE_DIR`, and `GROQ_API_KEY` in `.env` for voice and "What is this?".

2. **Network.** Pick one:
   - **USB tunnel:**
     ```
     adb reverse tcp:8000 tcp:8000
     ```
   - **iPhone hotspot** (proves `net.wifi`): join both devices to the hotspot, allow python through the macOS
     firewall, and add `-e server http://<Mac hotspot IP>:8000` to the launch command in step 4.

3. **Log.** Start the logcat in its own terminal and leave it running:

   ```
   adb logcat -c && adb logcat -v time Unity:I VrApi:I '*:S' > SpikeData/hcheck-$(date +%m%d-%H%M).log
   ```

4. **Launch fresh** and write down the time for `--since`:

   ```
   adb shell am force-stop com.airtools.quest
   adb shell am start -n com.airtools.quest/com.unity3d.player.UnityPlayerGameActivity -e site kitchen
   date +%H:%M:%S
   ```

5. **Hands only.** Put the controllers down, out of reach, so every check runs on hand tracking. `ring.open` shows
   how the menu was opened.
6. **Optional:** run `tools/quest_stream.sh -r` to record what the wearer sees, for the eye checks and the demo.
7. **Stand at a table** about 75 cm high with some free floor around you. Without the printed site mat, Take it home
   and the tabletop model use an environment raycast at the table, else an assumed 0.75 m.

## The session

Times are a budget, not a rule. The clock on each step is cumulative.

### 1. Put it on (0:30, 0:30)

- **Do:** put the headset on. Wait in passthrough for a few seconds.
- **Expect:**
  - your room, sharp;
  - the **Enter world** pill about 45 cm ahead and 25° below your eyes, facing you, with the hint "Then turn your
    left palm up for tools" (controllers: "…press the left menu button…");
  - the kitchen loads in the background in about 1 s.
- **Log:** `SceneStreamer: kitchen · kitchen r1 full · … in 1.0 s (collision mesh, 165 cameras)`
- **Checks:** `scene.real`, `net.server`, `net.wifi` (hotspot only) · eye: `m1.passthrough`, `ui.text`

### 2. Enter the world (0:30, 1:00)

- **Do:** poke **Enter world** with your right index finger.
- **Expect:**
  - a fade of 1 s or less with no judder, into the kitchen at 1:1;
  - the toast "Move: pinch the floor to walk there" (controllers: "Move: aim at the floor, pull the trigger");
  - you are in Move mode.
- **Log:**
  - `World button pressed (#1) in Passthrough`
  - `AppState Passthrough -> World`
  - `Move tool equipped`
  - `[AirTools.Check] M1.mode.transition to=World seconds=0.93 budget=0.90 PASS`
- **Checks:** `m1.entry` (eye: done by hand, and the pill is easy to reach), `m1.fade` (eye: no judder),
  `ui.default_tool`

### 3. Move (0:30, 1:30)

- **Do:**
  - Aim the right hand at the floor near the cabinets: a ring on the floor shows where you'll land. Make a quick
    pinch. You teleport there.
  - Aim at the counter top and pinch: you **don't** move, and the toast says "Aim at the floor to walk".
  - Then pinch, hold, and drag the air to shift yourself a little.
- **Log:** `Locomotion: teleported to (…)`. The drag is not logged (see appendix line 6).
- **Checks:** `move.teleport`

### 4. Tool ring (1:15, 2:45)

- **Do:**
  1. Turn your left palm **up** in front of you. The glass ring appears over it, with Move under the lens at the top.
  2. With the right hand, pinch the ring and drag it round, then let go. It should coast and spring onto an item.
     Settle on **Measure**.
  3. Make a quick pinch (or a poke) on **Level** at the side. It spins to the top and Level is equipped.
  4. Do the same to go back to **Measure**.
  5. Keep the right hand on the ring for about 5 s.
- **Expect:**
  - a tick for every item that passes: a sound, a pulse of the lens and a buzz;
  - the ring stays **open and still** while the right hand is on it, with no close and reopen flicker.
- **Log:**
  - `Palm menu open (hand)`
  - `Ring: equipped Level`
  - `Ring: equipped Measure`
  - no `Palm menu closed` followed by `Palm menu open` within 0.5 s
- **Checks:** `ring.open`, `ring.stable`, `ring.spin` (eye: ticks, pulse, buzz) · eye: `ring.tap`
- **[H] Ring v2 (declutter S7, eye):** six items, Move · Measure · Level · Notebook · Model view · Settings (the gear;
  it was "More"), one 60° detent apart. With Measure on the lens, **Settings** and **Notebook** at ±120° are still
  readable (about 74 % opaque), the gear reads as settings, and Model
  view is hidden at the back; Undo / Redo at the ends are easy to hit without catching the ±120° items.

### 5. Measure (2:00, 4:45)

- **Do:**
  1. Close the left hand; the ring goes away.
  2. With the right-hand ray, pinch the two front corners of the **dishwasher**. Watch for the corner glyphs and a
     haptic tick.
  3. Place 3 points on a cabinet door. A chip under the cursor says "Pinch your left hand to save". Finish with a
     **thumb + middle pinch** of the right hand. This makes a triangle.
  4. Place 4 corners of another door and close the polygon by pinching the first point again. This makes a quad.
  5. Step back to about 2 m.
- **Expect:**
  - a distance label, then sides, angles and area;
  - at 2 m every label is readable, none overlap, and none is cut by the door.
- **Log:**
  - `Notebook #1 measure: Distance 0.41 m · …`
  - `Done gesture (Right)`
  - `Notebook #2 measure: Triangle …`
  - `Notebook #3 measure: Quad …`
- **Checks:** `measure.distance` (eye: placing points feels right), `measure.shape`, `measure.done_gesture` ·
  eye: `measure.labels`, `measure.snap`

### 6. Undo, Redo, back to Move (0:30, 5:15)

- **Do:**
  1. Open the ring. Tap **Undo** (the left end capsule): the quad disappears.
  2. Tap **Redo** (the right end capsule): the quad comes back.
  3. Close the ring. Make two quick pinches with the **left** hand.
- **Expect:** the toast "Back to Move", and Move is equipped.
- **Log:**
  - `Notebook #3 restored: Quad …` (Undo itself is not logged; the redo implies it)
  - `Double pinch (Left): back to the default mode`
- **Checks:** `ring.undo_redo`, `ring.double_pinch`

### 7. Settings: scale, contrast, units (1:00, 6:15)

- **Do:**
  1. On the ring, spin to **Settings** (the gear) and pinch the lens. Settings (once More; the Scene window) opens on
     the right, settings first.
  2. The status reads "Kitchen · Scale ×1.63 · set for this scan" (the kitchen's default, the user's calibration),
     and the wrist strip says "Scale ×1.63". To check the override, type a real size for the last tape on the
     keypad and tap **Set scale**: the status turns to "Scale ✓ set (×…)". Then tap **Reset** twice: back to
     "Scale ×1.63 · set for this scan".
  3. Tap **Contrast** on and off, then **Less motion** on and off.
  4. Tap **Units · ft·in** (under Set scale): it reads **Units · m**, and every label in the world (the tape, the
     survey sizes, a part's callout) now reads metres in place. Tap it again to go back to ft·in (D2: imperial first).
- **Expect:** after Set scale, the toast "✓ Scale ×k · tape now reads …" and the chip "Scale ✓ set (×k)"; after
  Reset, the toast "Scale back to ×1.63 · set for this scan".
- **Log:**
  - `SceneStreamer: kitchen r1 at its default scale ×1.63 (SiteScales; …)` (at the load)
  - `Ring: ran ToggleScenePanel`
  - `Scene scale ×…`
  - `[AirTools.Check] scale.known_dimension … PASS`
  - `Menu: ToggleContrast`
  - `Units: m (Metric)`, then `Units: ft·in (Imperial)`
- **Checks:** `ring.action`, `scene.window` (eye: works by hand), `scene.default_scale` (`SceneStreamer: kitchen r1 at
  its default scale ×1.63`), `scene.scale`, `ui.units` (eye: labels re-word in place), `ui.contrast` (eye: the look
  changes)
- **Note:** a Set scale is saved for the kitchen on this headset and overrides ×1.63 from then on. **Reset** (tap
  twice) or a demo reset goes back to ×1.63.

### 8. Ask and voice (1:15, 7:30)

- **Do:**
  1. Still in Settings, look at the sink and tap **What is this?**
  2. Tap **Tap to talk** once, say *"switch to the level"*, and stop talking (don't tap again).
  3. Hold **Tap to talk**, say *"the opening is 34 and a half inches tall"*, and let go.
  4. Tap it, say nothing for 6 s.
- **Expect:**
  - a toast with the answer, and a pin **on the sink**;
  - while listening the Talk button turns yellow with a moving level bar and reads "Listening… tap to send" (held:
    "Listening… release to send"); the heads-up line says the same and its dot pulses with your voice;
  - step 2 sends by itself about 1 s after you stop talking: a spoken reply, and Level equipped within 3 s;
  - step 3: the transcript has the whole sentence, first words included;
  - step 4: "Didn't catch that", nothing sent.
- **Log:**
  - `Scene ask "What is this?" → "…"`
  - `Scene pin #1 from frame …`
  - `Voice: mic on (…, pre-warm)` when your hand or ray reaches the Talk button (and `Voice: mic off (idle)` 5 s after)
  - `Voice: listening (pre-roll 0.40 s, mic warm …)`, then one line per utterance:
    `Voice utterance mode=tap stop=end-of-speech press=0.2s speech=… trailing=1.00s … bytes=…` (step 4: `stop=no-speech … bytes=0`)
  - `Voice: Thinking…`, then `Agent: "switch to the level" → …`, then `Agent action equip_tool {"tool":"level"} → ok`
- **Checks:** `scene.ask` (eye: the pin sits on the right object), `voice.talk`, `voice.latency` (from the moment
  it sends until the tool is equipped), `voice.utterance` (eye: the meter moves with your voice)

### 9. Level (0:30, 8:00)

- **Do:** Level is in hand. Pinch the countertop, then the side of a cabinet.
- **Expect:** a Level reading near 0°, then a Plumb reading.
- **Log:** `Notebook #4 level: Level 0.4° (0.7% slope)`
- **Checks:** `level.reading` (eye: the gizmo and bubble read clearly on a real surface)

### 10. Notebook (0:45, 8:45)

- **Do:**
  1. On the ring, pick **Notebook** and pinch the lens.
  2. Poke a row: its points highlight.
  3. Tap **Next** and **Previous**, then **Send report**. Close the notebook.
- **Expect:** rows like "Width · 1′ 4⅛″ · 0.41 m" (both units, the chip's first) with the time and photo thumbnails, and the toast "✓ Report sent
  to the laptop".
- **Log:**
  - `Ring: ran ToggleNotebook`
  - `Notebook exported: … | …`
  - `Notebook POST … → …`
- **Checks:** `notebook.open` (eye: readable, and pokes work by hand), `notebook.export`

### 11. Find, take, place (1:45, 10:30)

- **Do:**
  1. In **Find parts** (it appeared on your left after the first tape), poke **Cabinet hinge**.
  2. Poke **Take** on the first result. The hinge is now in your right hand at true size.
  3. Aim at a cabinet door. The preview glides along the door with guides and a green box. Pinch to place it.
  4. Pinch the placed hinge to **pick it up again**, and seat it again.
  5. Open the ring. The **inspector card** beside it shows the hinge. Tap a finish swatch.
- **Expect:** a clunk and the toast "✓ Fits the door", and the hinge changes colour.
- **Log:**
  - `Parts search "cabinet hinge": 3 candidates from server`
  - `Part … in hand (server)`
  - `Part placed … : Green: …` (twice for the same part id)
  - `Part …: finish …`
- **Checks:** `parts.search`, `parts.take` (eye: it feels right-sized), `parts.place` (eye: the colour matches
  reality), `parts.grab`, `parts.inspector` (eye: readable), `perf.hold` · eye: `parts.preview`

### 12. Array (1:00, 11:30)

- **Do:**
  1. Switch the ring to **Measure** and tape a long straight run, 1.2 m or more (for example the counter front).
  2. In Find parts, poke **Gutter hanger**, then **Take**. Place the hanger at one end of that run.
  3. On the ring's inspector card, tap **Fill the run**.
- **Expect:** ⌊L / spacing⌋ + 1 hangers along the run.
- **Log:** `Part array: N × … every 600 mm along 1.8 m, N green`. The script checks N against the length and
  spacing.
- **Checks:** `array.place`
- **If Fill the run is dimmed:** the listing is a single unit with no spacing. By voice, the toast says "No spacing
  listed · place each … by hand".
  Try another preset. If no listing has a spacing, `array.place` stays NOT SEEN; write that down.

### 13. Sellers and checkout (1:15, 12:45)

- **Do:**
  1. On the inspector card, tap **Compare prices**. The seller panel opens with the best pick tagged.
  2. Tap **Fastest**, then **Cheapest**.
  3. On the recommended row, tap **Pay with Visa**. The checkout opens with the quantity from the array and
     •••• 1111.
  4. **Tap Pay briefly** (under 0.5 s): nothing happens.
  5. Then **hold Pay** for 1 s while the ring fills.
- **Expect:** the checkout replaces the seller window (Back returns to it); "Keep holding…" while you hold; a
  receipt card, a chime, and a toast ("✓ Paid …" or "Receipt saved · … · no charge").
- **Log:**
  - `Sellers for …, by eta`
  - `Sellers for …, by price`
  - `Checkout opened: …`
  - `Hold confirmed`, then `Checkout request: …`, then `Checkout authorized: …`
  - `Notebook #n purchase: …`
- **Checks:** `sellers.sort` (eye: rows readable and easy to hit), `checkout.hold`, `checkout.gate` (every request
  follows a completed 1-s hold)

### 14. Exit and take it home (0:45, 13:30)

- **Do:**
  1. Face the table. Tap **Take it home** on the receipt, or open ring ▸ **Settings** and tap **Exit world**: it
     reads "Tap to leave".
  2. Tap it again.
- **Expect:** the world pours back into a sphere ahead of you (no black frame; a fade only with Reduce motion), then
  the bought hanger stands **on the real table** at true size, labelled "Bought · N × hangers".
- **Log:**
  - `Button ExitWorld (poke, hand)` twice, then `Menu: ToggleWorld` (older builds: `Ring: ran ToggleWorld`)
  - `AppState World -> Passthrough`
  - `Take it home: 1 part(s) on the table`
- **Checks:** `ring.exit_confirm` (eye: it asked twice), `home.table` (eye: the part stands on the table at true
  size)

### 15. Tabletop (1:00, 14:30)

- **Do:**
  1. Poke **Enter world** again.
  2. On the ring, pick **Model view** and pinch the lens. The kitchen appears as a 1:50 model on the table.
  3. Switch the ring to **Measure** and tape the same counter run as in step 12, on the model.
  4. Pick **Model view** on the ring again to go back to 1:1.
- **Expect:** the tape reads **metres**, the same as at 1:1.
- **Log:**
  - `Tabletop on (1:50)`
  - `Notebook #n measure: Distance 1.8x m` (while in Tabletop)
  - `AppState Tabletop -> World`
- **Checks:** `tabletop.toggle` (eye: the model scans in on the table; going back it grows around you, anchored
  under your feet, and the sky closes last — no black frame), `tabletop.tape`. The script prints the closest 1:1
  tape for comparison.
- **[H] Model view in front of you (modelwheel, eye):** the model floats centred in your view at eye height about a
  metre away (not on the table at your waist), and a wide wheel of model cards sits right under it, tilted toward
  your face, the model on view inked under the lens. Point a hand (or controller) ray at the wheel, pinch (trigger) and
  drag sideways: the cards follow the ray, a flick coasts and ticks card by card, and it settles on a card; the loop
  has no end. A quick pinch on a side card spins it to the lens; pinch the lens card to open that model (Loading… →
  on view). Turn right round, or walk ~2 m away: after a moment the model and the wheel glide back in front of you;
  the **Recentre** chip does the same (bought parts on the floor to its right move with it). No "Model view · …" toast
  sits on the wheel (the lens line says the scale); with DemoMode's status line up, the wheel sits a little lower, clear
  of it. **Walk in** grows you into the model; **Exit** goes back to the room. Log:
  `Tabletop on (1:N) in front of you`, `Model wheel: released at … rad/s`, `Model wheel: open <site>: chose <site>`,
  `Model view: recentred (turned away)`.
- **[H] The models on the headset (scalemodels):** the wheel under the model lists the kitchen, Zabel
  gym, hospital, GT canopy / pavilion / tower, then the test facade. Settings reads "Models on the headset: N of 7";
  it reaches 7 of 7 a few minutes after launch with the laptop reachable (one `Prefetch: <site> r<rev> on the
  headset …` line per model). Then take the laptop away (stop the tunnel or the server) and open Model view again:
  every model still shows, each downloaded one loads from the headset, and any not downloaded read "Not downloaded"
  (dimmed; a tap says to connect the laptop). Check: `models.headset`.
- **Also (presence S1, eye):** from the model, poke or pinch the **You are here** pin: you grow in to where the pin
  stood. Then Exit world: the sky opens, the building shrinks back onto the table, the bought hanger beside it.
  Log: `Transition GrowIn done in 1.4x s → World`, `Transition ShrinkOut done in 1.6x s → Tabletop`.

### 16. Home and a quiet minute for FPS (1:00, 15:30)

- **Do:**
  1. On the ring, pick **Settings** and tap **Home**. You are back at the spawn point.
  2. Look slowly around the kitchen for 45 s. Keep the ring open for about 10 s of that time.
  3. Settings ▸ Exit world (tap twice) and take the headset off.
- **[H] Wrist strip (declutter S9, eye):** raise the left wrist (hands, then a controller). While there are purchase
  limits or a scan credit, a strip 5 cm above it reads the limits and the scale ("Scale ✓") and, on the Zabel scan,
  the credit verbatim under them; it's readable with the wrist raised and hides when the wrist isn't tracked. When
  the Zabel scan appears, the credit also shows low in view for 8 s.
- **Log:** `Locomotion: home`, and the VrApi `FPS=73/72,…,GPU%=0.8…` lines for the whole session.
- **Checks:** `move.home`, `perf.world72`, `perf.ring`, `app.errors`

### 17. Optional, last: Open at seller

This leaves the app for the Quest browser, so do it last or skip it.

- **Do:** in the seller panel, tap **See listing**.
- **Log:** `Open at seller: https://…`
- **Checks:** `sellers.open_url` (eye: the product page opens)
- **D7:** DemoMode is off by default since Sat 09-26 evening (user); only when it is switched on (`-e demo on`, presenter) is the link saved instead: `Open at seller (DemoMode): link saved to the notebook, not opened`, toast "Link saved to your notebook · open it after the demo". Launch with `up.sh --restart --demo off` to test the browser.

### 18. D7 demo controls (UX W1.8), 2 min, not counted

`up.sh` has started the presenter relay and `adb reverse tcp:8766`; open http://localhost:8766/ on the laptop.

- **Do:**
  1. The page shows **Headset online**, the status line text and the beat. Press **Hint** (`H`): the coach line appears on the status line.
  2. Press **Skip to beat ▸ Door measured** (`1`), then **Hinges found** (`2`). The door gets a tape; the search starts.
  3. Press **Reset demo** twice. The world fades to passthrough; the Enter pill says "Touch Enter world…".
  4. Enter again, tape anything, then with **controllers** click in both thumbsticks and hold 2 s ("Reset for the next person? Keep holding…" after 0.5 s).
  5. Put the controllers down. Enter, tape anything, then with **hands**: both hands up at eye height, flat, palms away, fingers up, still for 2 s. Also check that dropping your hands at 1 s cancels ("Reset cancelled"), and that normal pinching, poking and the palm-up ring never start it.
- **Log:** `Presenter relay connected: http://127.0.0.1:8766`, `Presenter: c… hint → ok`, `[AirTools.Check] D7.reset … clean PASS`, `Demo reset (controllers, 2 s hold): done`, `Demo reset (hands, 2 s hold): done`.
- **Never pays:** `curl -s -XPOST localhost:8766/presenter/cmd -d '{"cmd":"pay"}'` answers 403 and nothing reaches the headset.

## After (laptop, 1 min)

1. Stop the logcat with Ctrl-C.
2. Run:

   ```
   python3 tools/demo/hcheck.py SpikeData/hcheck-<MMDD-HHMM>.log --since <launch time>
   ```

   Add `--json` for a machine-readable copy.

3. Exit code 1 means some check **FAIL**ed. Read the evidence column: the log line and its time.
4. **NOT SEEN** means the log has no evidence. Redo that step, or confirm it by eye.
5. Tick the `log+eye` and `eye` rows in the table below.
6. Paste the result into SPEC §10 as a progress-log entry.

The FPS table covers only the app's own VrApi lines. The system shell logs VrApi lines too, and they are ignored.

- One sample per second.
- "At target" means at least 71 fps at 72 Hz.
- The script leaves out the first 2 s after each mode or scene change, and the first sample after a start or
  resume.
- **Sustained** means at least 95 % of samples at target, and a median of 71 or more.

The script's own tests: `python3 -m unittest tools/demo/test_hcheck.py`. They use synthetic log snippets, plus a
smoke run over the real headset logs when they are present (`$HCHECK_LOGS`, or the `SpikeData/quest*.log` files).

## Baseline: the existing logs

These are the Fri 09-25 builds, before the ring, run through `hcheck.py`. The current session should improve on
them.

| log | World | at 72 Hz | GPU % median / max | notes |
|---|---|---|---|---|
| `quest-backend.log` (kitchen) | 701 s, median 73, p10 72 | 97.0 % | 80 / 94 | **`perf.hold` FAIL**: 51 % at target with a part in hand, 32–64 fps for 12 s while an AC unit was held over the scan (22:50:43–54). The per-frame collider search behind this was fixed in `bda44cd`. **`ring.stable` FAIL**: 6 reopenings within 0.5 s (palm-menu flicker, fixed in `bda44cd`) |
| `quest-m5b.log` (facade) | 830 s, median 73 | 99.8 % | 81 / 87 | `ring.stable` FAIL (3) |
| `quest-ui.log` (facade) | 509 s, median 73 | 99.2 % | 80 / 86 | old palm menu open for 343 s at 99.7 % |
| `quest-ui3.log` (facade) | 272 s, median 72 | 99.3 % | 84 / 92 | |

All the other logs are older builds:

- `ui.default_tool` FAILs on builds from before D1 (Sat 09-26): they open with Move, not Measure.
- There are no `Ring:` lines, because the ring didn't exist yet.

The GPU headroom in the kitchen is thin (94 % max). The Liquid Glass ring shader has not been measured on the
device yet; that is what `perf.ring` is for.

## UX Wave 0 [H] checks (feat/ux-wave0)

Run these inside the session above where they fit; each needs eyes or hands, so the script can't prove them.
Flags for the undecided calls are listed after the table (all default to today's behaviour).

| ☐ | Check | Where | Pass when |
|---|---|---|---|
| ☐ | `ux.scale_chip` (W0.1) | §7 | The Scene window reads "Scale ✓ set (×1.49…)" on the demo headset **before** the first judge; a calibrated door reads 389–391 mm. |
| ☐ | `ux.entry_placement` (W0.7) | §1, §14 | The Enter world pill is in view without looking down, standing **and** seated, and within easy reach (0.45 m, 25° down). After Exit world it comes back in front of you. Poke works (ray only with D5 on). |
| ☐ | `ux.floor_teleport` (W0.2) | §3 | A quick pinch on the counter or a wall never moves you and says "Aim at the floor to walk"; the floor ring only shows on the floor. |
| ☐ | `ux.finish_chip` (W0.3) | §5 | A first-timer reads "Pinch your left hand to save" under the cursor and saves a tape without help; no Meta system menu from that pinch. A second nervous left pinch after saving does **not** switch to Move. |
| ☐ | `ux.work_kept` (W0.4) | §4–§5, §11 | Spin the ring to Level mid-tape and back to Measure: the points are still there ("Tape kept · pick Measure to finish it"). Spin away while holding a hinge: "Hinge put back in Find parts"; Take brings the same hinge back. |
| ☐ | `ux.sound_haptics` (W0.5) | throughout | A rising pip on each point, a 4-note arpeggio on save, a glide for level / undo / redo, a clunk on a part, a low double pulse on a miss. Audible at the expo, not harsh. Controller haptics match the events and nothing buzzes stuck. Misses get words (one hint per 4 s). |
| ☐ | `ux.legibility` (W0.6) | throughout | Ring labels (with their halo), windows, chips and the toast read crisply over the bright kitchen and in passthrough; D3: the white primary buttons' dark labels and the ink (selected) chips' dark text are crisp, the white pills still look like glass (bevel, top light) and don't glare (R07 §3.4 Fresnel god-rays), and the ring's active tool shows an ink label + ink lens keyline. |
| ☐ | `ux.no_blur` (W0.11) | §16 | logcat: the eye buffer never drops below native (dynamic-resolution floor 1.0) and `Render: FFR Low (World)` on entering the world; 72 fps held. World labels stay upright when you tilt your head. |
| ☐ | `ux.windows` (W0.7) | §10, §13 | One window at a time in front of you (0.45 m, 20° down, facing you): the notebook closes Sellers, Pay with Visa replaces Sellers and **Back** returns; the Scene window opens on the right, Find parts on the left. |
| ☐ | `ux.toast` (W0.8) | throughout | Looking up at the cabinets and down at the counter, the toast stays just below your gaze and in front of any window; long messages wrap to two lines; a voice / "What is this?" answer shows on the reply card below it. |
| ☐ | `ux.controller_ring` (W0.10) | controllers | ☰ opens the ring; picking a tool closes it; with the ring open, the right trigger still measures away from the ring; **A** undoes the last level while in Move. |

**Decision flags** (one line each; defaults = today):
- D1 entry tool: `ToolManager.Default` (`ToolKind.Move`; `ToolKind.Measure` to enter with the tape armed — then
  update `DemoWalkthrough` beat `tool.default` and hcheck `ui.default_tool`).
- D4 auto-save 2-point tapes: `MeasureTool.AutoSaveTwoPointTapes` (false). True: a tape saves at its 2nd point;
  "area" / "angle" (voice) keep going until finished.
- D5 ray on windows and Enter: `GlassButton.RayOnWindows` (false = poke only). True: ray presses after a 120 ms hover.
- D6 ring commits on pinch: `ToolRing.CommitOnPinch` (false = modes equip when the wheel settles).
- Menu-hand double pinch → Move: `OvrToolInputSource.DoublePinchToMove` (true).

## Check index (sources, kinds, tick boxes)

| ✓ | id | step | kind | from | proven by |
|---|---|---|---|---|---|
| ☐ | `net.server` | 0 | log | M0 "Quest reaches the laptop" (now the real server on :8000) | a scene load not from the cache, a server search or part, a notebook POST, or an agent reply |
| ☐ | `net.wifi` | 0 | log | M0 §10 "OPEN (Wi-Fi)" (optional) | a `ServerConfig: server override http://<LAN>` line, then a server round trip |
| ☐ | `scene.real` | 0 | log | M7 real package; backend "[H] kitchen" | `SceneStreamer: kitchen · … in X s` |
| ☐ | `app.errors` | 0 | log | M1 "zero console errors", on the device | no `E/Unity [AirTools]` lines and no exceptions (other Unity errors are counted) |
| ☐ | `m1.passthrough` | 1 | eye | M1 "Passthrough visible on the 3S" | nothing to log |
| ☐ | `ui.text` | 1 | eye | UI "text sizes and contrast in passthrough"; M3/M4 "readable" | nothing to log |
| ☐ | `m1.entry` | 2 | log+eye | M1 "chest opens by hand poke" (now the Enter world pill), plus its reach | the pill press, then `AppState Passthrough -> World` |
| ☐ | `m1.fade` | 2 | log+eye | M1 "no judder" | `M1.mode.transition … PASS` and the lowest fps around each fade |
| ☐ | `ui.default_tool` | 2 | log | §9 Sat (D1): "the world opens with Measure" | `Measure tool equipped` within 2 s of entering the world |
| ☐ | `move.teleport` | 3 | log+eye | backend "moving with hands only" | `Locomotion: teleported to` |
| ☐ | `ring.open` | 4 | log | UI "palm menu open/close by hand" (now the palm-up ring) | `Palm menu open (hand)` |
| ☐ | `ring.stable` | 4 | log | backend "palm menu flicker" fix | at most one close→reopen within 0.5 s |
| ☐ | `ring.spin` | 4 | log+eye | UI.md §8 spin, settle, ticks | `Ring: equipped X` |
| ☐ | `ring.tap` | 4 | eye | UI.md §8 "tap a side item" | nothing to log yet (appendix line 2) |
| ☐ | `measure.distance` | 5 | log+eye | M2 "marking points by hand pinch feels right" | `Notebook #n measure: Distance` |
| ☐ | `measure.shape` | 5 | log+eye | M2 3–4 points by hand | `Notebook #n measure: Triangle/Quad/Polygon` |
| ☐ | `measure.done_gesture` | 5 | log | UI "thumb+middle done" | `Done gesture (Right)` |
| ☐ | `measure.labels` | 5 | eye | M2 "labels readable at 2 m"; UI "labels never overlap" | nothing to log |
| ☐ | `measure.snap` | 5 | eye | backend "snap glyphs" | nothing to log yet (appendix line 4) |
| ☐ | `ring.undo_redo` | 6 | log+eye | §9 Sat global Undo/Redo | `Notebook #n restored` (a redo implies the undo) |
| ☐ | `ring.double_pinch` | 6 | log | §9 Sat "menu-hand double pinch" | `Double pinch (Left): back to the default mode` |
| ☐ | `ring.action` | 7 | log | UI.md §8 "actions fire on a lens pinch" (Notebook, Model view, Settings) | `Ring: ran X` |
| ☐ | `scene.window` | 7 | log+eye | backend "Scene window by hand" (now Settings, once More) | `Ring: ran ToggleScenePanel` |
| ☐ | `scene.default_scale` | 7 | log+eye | the user (09-26): "the 1.63x estimate was correct, begin with that" | `SceneStreamer: kitchen r1 at its default scale ×1.63` (a saved Set scale shows `restored scale ×…` instead: NOT SEEN, Reset) |
| ☐ | `scene.scale` | 7 | log | backend "Scene window keypad" | `[AirTools.Check] scale.known_dimension … PASS` |
| ☐ | `ui.units` | 7 | log+eye | §9 Sat D2 "one unit per label, imperial first, a units chip" | `Units: m (Metric)` / `Units: ft·in (Imperial)` (or `[AirTools.Check] D2.units.relabel … PASS`) |
| ☐ | `ui.contrast` | 7 | log+eye | §9 Sat "Contrast / Less motion moved to the Scene window" | `Menu: ToggleContrast/Motion` |
| ☐ | `scene.ask` | 8 | log+eye | backend "What is this? pins" | `Scene ask …`, then `Scene pin #n` |
| ☐ | `voice.talk` | 8 | log | backend "hold-to-talk (Scene window, Find parts)" | `Voice: Thinking…`, then `Agent: "…" → …` |
| ☐ | `voice.latency` | 8 | log | M6 "voice round trip < 3 s for equip_tool" | from `Voice: Thinking…` to `Agent action equip_tool … → ok` in 3 s or less |
| ☐ | `level.reading` | 9 | log+eye | M3 "level gizmo reads clearly on a real surface" | `Notebook #n level:` |
| ☐ | `notebook.open` | 10 | log+eye | M3 "wrist panel readable and pokeable" (now a floating window) | `Ring: ran ToggleNotebook` |
| ☐ | `notebook.export` | 10 | log | M3 export, on the device | `Notebook exported:` and `Notebook POST … →` |
| ☐ | `parts.search` | 11 | log | M4 crate menu by poke; backend "six presets" | `Parts search "…": N candidates from server` |
| ☐ | `parts.take` | 11 | log+eye | M4 "part feels right-sized" | `Part X loaded from server: W × D × H mm` and `in hand` |
| ☐ | `parts.place` | 11 | log+eye | M4 "grab/release by hand"; backend "placement on the scan" | `Part placed … Green/Amber/Red` |
| ☐ | `parts.grab` | 11 | log+eye | M4 "grab/release by hand" | the same part placed twice with no new take in between |
| ☐ | `parts.preview` | 11 | eye | backend "preview glides, placement guides" | nothing to log |
| ☐ | `parts.inspector` | 11 | log+eye | M4 "spec card readable", swatches | `Part X: finish name #hex` |
| ☐ | `array.place` | 12 | log | M5 array, on the device | `Part array: N × …` with N = ⌊L/s + 0.03⌋ + 1 |
| ☐ | `sellers.sort` | 13 | log+eye | M5 "carousel scrolls smoothly by hand" (now a seller panel) | `Sellers for …, by eta` and `by price` |
| ☐ | `checkout.hold` | 13 | log | M5 hold-to-pay by hand | `Hold confirmed`, `Checkout authorized`, `Notebook #n purchase` |
| ☐ | `checkout.gate` | 13 | log | M5 "a short hold sends nothing", on the device | every `Checkout request` has its own `Hold confirmed` in the 1 s before it |
| ☐ | `ring.exit_confirm` | 14 | log+eye | UI.md §8 "Exit asks twice" (now in Settings) | two `Button ExitWorld (…)`, `Menu: ToggleWorld`, then `AppState World -> Passthrough` |
| ☐ | `home.table` | 14 | log+eye | M7 "take-it-home part sits on the real table" | `Take it home: N part(s) on the table` with N ≥ 1 |
| ☐ | `tabletop.toggle` | 15 | log+eye | M7 tabletop | `Tabletop on (1:50)` |
| ☐ | `tabletop.tape` | 15 | log+eye | M7 "at 1:50 the tape reads the same metres", by hand | a measure entry while the mode is Tabletop, compared with the closest 1:1 tape |
| ☐ | `models.headset` | 15 | log | the user (09-26): "Model view … should add the rest of the models" | `SceneStreamer: N models known on this headset (M downloaded)` with M ≥ 2, or a `Prefetch: <site> r<rev> on the headset …` line |
| ☐ | `move.home` | 16 | log | Home in Settings (M7 locomotion) | `Locomotion: home` |
| ☐ | `perf.world72` | 16 | log | M7 "72 fps sustained with the real scene"; backend "kitchen fps" | VrApi samples in the World with a scanned scene: sustained (see "After") |
| ☐ | `perf.ring` | 16 | log | UI "FPS/GPU with the new UI" (the Liquid Glass ring) | VrApi samples while the palm menu is open in the World |
| ☐ | `perf.hold` | 11/16 | log | backend headset session: no per-frame collider search | VrApi samples while a part is in hand in the World |
| ☐ | `sellers.open_url` | 17 | log+eye | M5 "Open at seller" on the device (optional) | `Open at seller: <url>` |

## Retired [H] checks (superseded, or already passed)

| retired check | why | covered now by |
|---|---|---|
| M0 splat fps at 200k/400k | splats were cut (§9); passed on 09-24 | none |
| M0 glTF from the Mac, mic on the Quest, cleartext HTTP over the tunnel | PASS in §10 (M0 [H] results) | the Wi-Fi half is `net.wifi` |
| M1 "chest opens by hand poke", "chest height and reach" | the chest became the Enter world pill | `m1.entry` |
| M2 "toolbox reachable at the hip" | hip toolbox → palm menu → tool ring | `ring.*` |
| M3 "wrist panel readable and pokeable" | the notebook is a floating window | `notebook.open` |
| M5 "carousel scrolls smoothly by hand" | a seller panel with sort buttons | `sellers.sort` |
| M6 realtime voice (xAI) < 3 s, "text on the wrist" | push-to-talk to `/voice/command` (§9 backend) | `voice.talk`, `voice.latency` |
| M7 two-hand tabletop scale | a toggle behind the mode fade (a documented deviation in `TabletopController`) | `tabletop.toggle` |
| UI "palm menu open toward the face", "Done button" | the palm-up ring; Done was removed | `ring.open`, `measure.done_gesture` |

## Appendix: log lines that would let the script prove more

The app code was not changed for this checklist. Each line below turns an `eye` check, or the eye half of a
`log+eye` check, into log evidence. Line numbers are at `b2fd73a`. `hcheck.py` already recognises the formats
of lines 1, 2, 3, 4, 5, 6 and 7, so they count as soon as the app writes them.

1. **Which input pressed a button** (most useful; covers every "by hand" check).
   - **Where:** `Assets/AirTools/Runtime/UI/GlassButton.cs:82` in `OnState`, before `Press()`.
   - **Line:**
     ```
     Log.Info($"Button {name} ({(poke != null && poke.State == InteractableState.Select ? "poke" : "ray")}, {((OVRInput.GetConnectedControllers() & OVRInput.Controller.Touch) != 0 ? "controller" : "hand")})")
     ```
   - **Proves:** "by hand poke" for `m1.entry`, and the same for Find parts, the seller panel, Pay, the notebook
     rows and the keypad. The script already appends it to `m1.entry`.
2. **Ring gestures.**
   - **Where:** `Assets/AirTools/Runtime/Input/ToolRing.cs`. Add `Log.Info($"Ring: {LastAction}")` after `:349`
     (`spin to X`), `:382` (`armed Exit world`), `:395` (`undo`) and `:402` (`redo`). Also add
     `Log.Info($"Ring: released at {Dial.Velocity:0.0} rad/s, {Ticks} ticks")` after `Dial.EndDrag()` at `:312`.
   - **Proves:** `ring.tap`, `ring.exit_confirm` (it asked twice), `ring.undo_redo` (Undo is silent today:
     `Notebook.Remove` doesn't log), and the drag-and-coast half of `ring.spin`.
3. **Judder during a fade.**
   - **Where:** `Assets/AirTools/Runtime/Core/ModeController.cs`. Track the longest frame next to
     `m_Elapsed += dt` (`:85`) and add `max_frame_ms={m_MaxDt * 1000:0}` to the `M1.mode.transition` check line
     (`:114`–`116`).
   - **Proves:** `m1.fade` "no judder". VrApi's 1-s samples are too coarse for a 0.9-s fade.
4. **Snap kind per point.**
   - **Where:** `Assets/AirTools/Runtime/Tools/MeasureTool.cs:268`, after
     `LastAction = $"point {Session.Count} ({hit.kind})"`.
   - **Line:** `Log.Info($"Measure {LastAction}")`
   - **Proves:** `measure.snap` (which snaps were acquired). The glyph's look stays an eye check.
5. **Picking a part up.**
   - **Where:** `Assets/AirTools/Runtime/Parts/PartTool.cs:215`, after `LastAction = $"picked up …"`.
   - **Line:** `Log.Info($"Part {LastAction}")`
   - **Proves:** `parts.grab` directly. It is only inferred today.
6. **Grab-the-air drag.**
   - **Where:** `Assets/AirTools/Runtime/Input/Locomotion.cs:128` (the `moved to` branch) and `:100` (snap turn).
   - **Line:** `Log.Info($"Locomotion: {LastAction}")`
   - **Proves:** the drag half of `move.teleport`.
7. **Notebook row pokes.**
   - **Where:** `Assets/AirTools/Runtime/Notebook/NotebookPanel.cs:124` in `Show(int row)`.
   - **Line:** `Log.Info($"Notebook row {row} highlighted")`
   - **Proves:** the "pokeable" half of `notebook.open`.
8. **Palm menu hold.**
   - **Where:** `Assets/AirTools/Runtime/Input/PalmMenu.cs:121`, when `Holding` changes.
   - **Line:** `Log.Info($"Palm menu {(hold ? "held (other hand on it)" : "released")}")`
   - **Proves:** that the hold engaged (`ring.stable`), not just that there was no flicker. This one needs a
     small script update.
