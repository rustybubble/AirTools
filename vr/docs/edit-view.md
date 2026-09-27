# The Edit view: one part, on its own, in 6 DoF

The user asked for "an isolated UI for just editing components": change the colour (a shade over the texture), turn
it in 6 DoF with arrows, and move it. Then, on the headset (edit-touch): "make the edit menu touch buttons instead of
pinching and bring it closer to us so touch is actually convenient" — every control is now a **poke button within arm's
reach** (§2) — and "remove edge snapping from reorienting items that are being moved from the edit mode (the whole point
is to preserve orientation) and only apply x,y,z translations" — Move and the new part's placing **only translate** it. Everything else greys out and closes. The item flies to the centre of the
view, then flies back to its place in the room when you click Save. Move lets you drag only that item until you save.

- **A placed part:** a gesture on it opens a small menu: Edit, View similar, Delete.
- **A new part:** you orient it first in the same view. Then Place puts it on the pointer, and the pinch that places it
  saves and closes the view.

Code: `Runtime/Parts/EditView.cs` (the controller) and `EditViewMath.cs` / `Edit6DofMath.cs` (the pure maths, the
state machine, the filters and the hold). The other pieces:

| Piece | Where |
|---|---|
| The 12 arrows | `EditArrows.cs`, `EditArrowButton.cs` (a knob's poke), `UI/HoldRepeat.cs` (hold to repeat) |
| The panel and the move bar | `EditViewPanel.cs` |
| The dimmed world | `EditDim.cs`, `Shaders/DimShell.shader` |
| The context menu | `PartContextMenu.cs` |
| Buttons | `EditViewButton.cs` |
| Shade and draw order | `PartInstance.Edit.cs` |
| The model: one undo step, Move, shade | `PlacementEditor.EditView.cs` |
| Wire builds it | `Editor/EditViewBuilder.cs` |
| Harness | `Dev/EditViewCheck.cs`, `AgentHarness.EditViewCheck()` / `Edit*()` |
| Tests | `EditViewMathTests` (pure, offline too), `EditViewSceneTests` (Editor gate) |

## 1. Getting in

| From | How |
|---|---|
| A placed part, **controllers** (and the Simulator) | Point at it and **squeeze the grip**. The grip isn't used anywhere else in the app (`OVRInput.Button.PrimaryHandTrigger` becomes `ToolButton.Context`). The menu opens beside the part: Edit / View similar / Delete. |
| A placed part, **hands** (and controllers) | A **long pinch / trigger hold (0.6 s)**, held still, on the part, with the Part tool, Move or nothing in hand. It doesn't work while measuring (tape, level, ladder), where a long pinch is a careful measurement. |
| The part's card (palm menu inspector) | The chip that said "Adjust" now reads **Edit**. |
| Voice | "edit it", "adjust the dishwasher": `adjust_placement {mode: on}` (or a bare `adjust_placement`), `edit_part {part?}`; also `delete_part {part?}` and `view_similar {part?}`. `part` is a placed part's id, name or kind; without it, the selected part, else the last placed. The backend on `demo-next` doesn't declare these tools yet: see `docs/handoff/p4-edit6dof`. |
| A new part from the Catalog | Taking a card opens the part here first (`EditView.OpenOnTake`). Grok's own `place_part` (the autonomous job) skips this and places it directly. Voice / harness `SelectCandidate` still puts it straight in the hand. |

**Input audit (why these gestures).**

- **Hands:**
  - index pinch = the tool press;
  - thumb + middle pinch, or a pinch with the menu hand, = Finish;
  - two quick pinches with the menu hand = back to Move;
  - palm up = the ring;
  - Meta's system gesture is ignored.
- **Controllers:**
  - trigger = press;
  - B / Y = Finish;
  - A / X = Undo;
  - thumbsticks = fly / rise / snap turn;
  - left Menu = the ring (it crashes the Simulator: don't use it there);
  - the **grip was unused**.
- **Hub:** no hold gesture existed. A long hold on a placed part was free with the Part tool (it only selects) and with Move (a still press doesn't teleport).

## 2. Inside the view

When the view opens:
- **Every other UI closes:** the Catalog, the notebook, Settings, sellers, checkout (never mid-payment), the Grok and
  overlay cards, survey, ladder, the adjust panel and toasts. The Catalog and the notebook come back afterwards.
- **World text steps back:** the label pool (`PlacementFocus`) and the annotations (`ModelViewDeclutter.EditViewHides`,
  the same set Model view hides).
- **The tool in hand is put down;** it comes back afterwards.
- **The world is dimmed** (`EditDim`).
- **The item flies (0.5 s, ease in-out; at once with Reduce motion)** to the centre of the view, **within arm's reach**
  (edit-touch), its front turned to you.
  - A big item is shown scaled down so its **largest side is at most 0.25 m** (a fridge at ~14 %). The panel says
    "shown at NN %".
  - Every change applies at true size.

**Where it all sits (edit-touch, `EditViewMath.Layout`, placed once when it opens, world-locked):** the arrows, the
panel and the readout pill are laid out on a plane 0.42 m ahead, 20° below the eye line, facing you; the whole
composition is centred on that point. The item's centre sits behind its ring by half its largest side (its near face on
the plane, 0.42–0.55 m from the eye, ~12 cm below it). Every knob is then drawn onto the 0.42 m sphere round the eye,
facing it (`EditArrows.Curve`: the edge of the ring is as near as its middle, each knob pressed straight in); the panel
is tucked into the ring's lower right, clear of every knob and of the item, 0.44 m from the eye, ~25 cm below it, facing
it. Everything stays within ±48° of your heading and between ~9° above and ~48° below the eye line. Text is built for
0.45 m (`EditView.ReadDistance`; `ErgonomicsTests` reads it).

### The arrows

Twelve knobs sit round the item on a world-locked stage:
- **Curved:** Tilt ↑ (top) / Tilt ↓ (bottom), Turn ← (left: the front turns to your left) / Turn → (right),
  Roll ↻ (upper right, clockwise as you face it) / Roll ↺ (upper left).
- **Straight:** Right / Left / Up / Down on the outer ring, Out (lower right, towards you) / In (lower left).

A new part has no room pose yet, so it gets only the six curved arrows.

The knobs are **poke buttons** (edit-touch): 3.2 cm round `GlassButton`s over the ISDK poke (`UiBuild.Button`, the
poke template, no ray), with Phosphor glyphs. A fingertip presses them, or a controller's poke tip. Pinches and rays
don't take them.
- **A poke** steps by 5° / ⅜″ (1 cm), or with **Step** on fine, 1° / ⅛″ (2 mm).
- **Holding the poke** repeats: a step after 0.4 s, then every 0.12 s (`HoldRepeat`; a tick each), until the finger
  comes out — seven steps in a second. It moves **along that axis only**. (The continuous drag of the pinch design is
  gone: a held poke replaces it.)
- The button draws its own hover and press (rim glow, 3 mm in). The knob in use is selected (ink with its bar). A finger
  near a knob shows its axis and value in the readout pill beside the top knob ("Tilt 5°", "Right 1⅛″", "In ¼″").
- The axes are the placement's (`PlacementMath.Rotation`: turn about Up, tilt about the turned Right, roll about the
  part's facing), so the panel's readout is the same as in Adjust.

**Turn it by hand** (kept): pinch the item itself and turn your wrist. It turns with the controller's grip pose or the
tracked hand's wrist (the hub's grip pose; the pointer ray has no roll).

### The panel

The panel (0.225 × 0.228 m) is tucked into the arrows' lower right, 0.44 m from your eyes, and is **poke only**. From
the top:
- Title and true size.
- The readout: "Right 0″ · Up 0″ · Out ¼″ / Turn 5° · Tilt 0° · Roll 0°".
- **Swatches** (3 cm): Original, Stainless, Black, White, Slate / Navy, Red, Bronze, Wood, **Shade: light / full**.
- Step · Reset turn · Fit to opening.
- Cancel · Move · **Save**. A new part has Cancel · **Place**.

About the controls:
- **Colour** is a shade over the texture. Each material's own colour (which multiplies the texture) is blended toward
  the swatch: 50 % (light) or 85 % (full).
  - The glass is skipped.
  - It changes the part's own material copies, not property blocks, so it stays in the SRP batcher.
  - It becomes the part's finish (`FinishColor` / `FinishName`): the spec card, the notebook row (`finish`) and a saved
    placement carry it.
- **Size:** only **Fit to opening** is kept, and it shows only when there's a taped opening the part can be made for
  (`PlacementEditor.CanFitToOpening`: window frames). It is its own undo step, as in Size & finish (it may reload a
  made-to-size model). The W / H / D pads stay in the adjust panel, reachable by `AppCommands.AdjustPlacement`.

### Save, Cancel and undo

- **Save** flies the item back into the room with its new turn, offset and colour. The dim lifts and the UI returns.
  The session is **one undo step** (`PlacementEditor`'s adjust session with the shade and the spot before a Move on it).
- **Cancel** reverts everything and flies it back. It leaves no step.
- The view shows a **copy** at the centre (the real part's model, sharing its materials). The real part stays at its
  true pose in the room. Everything that watches it (site scoping, swaps, a world switch, undo) works as before.
- A tool picked from the ring, leaving the world or a world switch ends the session as Adjust did (kept).
- B / Y (and the other hand's pinch, or thumb + middle) is Save. A / X undoes the session so far.

### Move

**Move** flies it back to the room. Then:
- **Only it moves, and only by translation** (edit-touch). No tool is in hand. Press and drag: it rests on the surface
  under the pointer (its box touching it along the surface's normal, slid clear of overlaps: `PartPlacer.Seat`), with
  **exactly the rotation you gave it** — no surface-normal alignment, no gap or opening snap, no facing the wall.
- The **move bar** (a main-slot card) shows the fit where it is ("Fits the gap · ⅜″ spare · Save to keep it here") with
  Cancel and **Save**.
- The world stays dimmed. The item draws over the dim, depth-tested against the room.
- Save keeps the new spot: the part's spot is rebuilt where it is, and the undo step puts it back in the old one.

### A new part

A Catalog card's Take opens the new part at the centre: orient it and colour it. **Place** then puts it on the pointer
(PartTool) with **exactly the rotation it had in the view** (edit-touch: `PlacementEditor.SetHeldRotation` →
`PartTool.KeepRotation`): the preview and the placement only translate it (`PartPlacer.Seat`), and no saved placement or
opening snap turns it once it's down. The pinch or trigger that places it **saves and closes** the view. There is no extra Save; the undo step is the placement. Cancel puts it back (its card
still has it).

### The context menu

**Edit** opens the view. **View similar** opens the Catalog on the part's kind, with Fits on when it's in a gap.
**Delete** removes it, undoably: the menu turns into "Deleted · Undo" for 6 s, and A / X or the ring's Undo also bring it
back. edit-touch: the menu is a **poke-only** card **within reach** — 0.42 m from the eye, 18° down, on the line to the
part turned 14° to your right (`EditViewMath.MenuPose`), facing you, placed once and then still. Presses in its first
0.35 s are ignored (it may open where the pinching hand is). It claims one slot of the label pool and closes after 8 s or
on the next press elsewhere.

## 3. The dim: how it stays cheap

The dim is `AirTools/DimShell` on an inverted 3 m sphere on the centre eye. It is a translucent dark veil, the theme's
`background` at 55 %, drawn at queue 3050 with ZTest Always. The draw order:

| Order | What |
|---|---|
| World | the scan, parts, measurements |
| 3040 / 3045 | world labels |
| **3050** | **the veil** |
| **3060** | **the edited item** (its materials' queue raised while the view is open) |
| 3090–3110 | UI glass and text: the arrows, the panel |

- While the item sits at the centre, the veil also **writes its depth** (3 m, behind the item). So the item, its arrows
  and its panel draw over a wall nearer than them (they sit within half a metre).
- While it flies, in Move and while placing, it doesn't. The item is then depth-tested against the room: a part flying
  in from 5 m isn't hidden behind the veil's 3 m depth.
- It is one full-view translucent pass: no post-processing, no grab pass. It is stereo-instancing aware.

## 4. In the Meta XR Simulator (controllers only)

The palm ring needs the left Menu button, which crashes the Editor in the Simulator, so nothing here uses it.

edit-touch: every control of the view is poked — move the controller's **poke tip** through the button (the grip pose;
CLAUDE.md "Operator poke"), no trigger.

1. **Place a part.** Open the Catalog: say "show the catalog", or run `AgentHarness.CatalogFixture(true)` then
   `AppCommands.ShowCatalog()` via `unity command eval`. Ray at a card, **trigger**: the Edit view opens with the part at
   the centre. Orient it (below), **poke Place**. Then aim at the floor or a surface and **trigger** to place it (the
   view closes; it keeps the rotation you gave it).
2. **Open its menu.** Ray at the placed part and **squeeze the grip** (Operator: `Grip` 1 for ~0.25 s). Or, with the
   Part tool in hand (it is, after placing), hold the **trigger for 0.6 s** on it. The menu appears within reach: **poke
   Edit**.
3. **Arrows.** Poke a knob to step; keep the tip in to repeat. The readout pill says the value.
4. **Turn it by hand.** Ray at the item itself, hold the trigger and roll or tilt the controller.
5. **Colour.** Poke a swatch. Shade toggles light or full.
6. **Move.** Poke Move. Wait for the fly, then hold the trigger with the ray on the floor and drag (it only translates).
   Release, then poke the move bar's **Save**.
7. **Save / Cancel** on the panel, or **B** for Save.

### Operator recipe

The ISDK controller ray follows the **grip** pose, pointing ~60° below grip-forward (CLAUDE.md). The Unity rig sits in
Main at (0, 0, 4), yaw 180; Operator poses are in tracking space, so subtract the rig pose.

1. After `unity command editor_play`, wait ~10 s for the Operator to attach.
2. Calibrate the ray: `openxr_set_controller_pose(right, grip, (0.2, 1.3, −0.3), identity)`, then
   `AgentHarness.CalibrateRightRay(0.2f, 1.3f, -0.3f)`.
3. **Aim at a thing:** `AgentHarness.GripForTarget(0.2f, 1.3f, -0.3f, tx, ty, tz)` gives the OpenXR quaternion that aims
   the ray at a scene point. Get the points from:
   - a part: `AgentHarness.PartInfo()`;
   - a knob: `AgentHarness.EditKnob("TiltUp")`, which prints its world and scene-root position
     (`AgentHarness.EditReach()` prints every control's distance from the eye);
   - a button: its transform via `eval` (poke it along its `transform.forward`, CLAUDE.md "Operator poke").

   Set that grip rotation with `openxr_set_controller_pose`, then check the aim with `AgentHarness.RightRayHit()`.
4. **Press:**

   | Action | Operator call |
   |---|---|
   | tap | `openxr_set_controller_input(right, Trigger, 1, auto_release, 0.25)` |
   | long hold (the menu) | `openxr_set_controller_input(right, Trigger, 1, auto_release, 0.8)` |
   | menu by grip | `openxr_set_controller_input(right, Grip, 1, auto_release, 0.25)` (hold across frames: an immediate 0 is missed) |
   | Save | `openxr_set_controller_input(right, B, 1, auto_release, 0.2)` |

5. **Poke an arrow** (edit-touch): start the poke tip 4 cm in front of the knob, move it 5 cm along the knob's forward
   over 0.3 s; leave it there ≥ 0.4 s to see it repeat, then pull it back.
6. **Turn by hand:** aim at the item's centre, Trigger 1, then rotate the grip (e.g. roll 20° about the aim axis), then
   Trigger 0.
7. **Check each step:** `AgentHarness.EditState()`, then a composited screenshot
   (`openxr_capture_composited_image`). If the capture fails: `unity command capture_game_view --source screen
   --save_path Temp/x.png`.

### Harness (no headset)

- `BackendHarness.LoadSite("synthetic-facade-parts")`, then `AgentHarness.EditViewCheck()`. It prints
  `[AirTools.Check] editview.*`:

  | Checks | What |
  |---|---|
  | `menu.grip`, `menu.hold` | the menu by grip and by long pinch |
  | `menu.reach` | the menu card within reach (0.35–0.5 m, 5–25 cm below the eye) |
  | `open`, `isolated`, `reach`, `poke_only` | the view opens (Edit poked), isolated; every arrow 0.36–0.48 m from the eye, the panel 0.40–0.47 m, below it and facing it, the item's largest side ≤ 25 cm; every button of the view, its move bar and the menu poke-only |
  | `tap.tilt`, `tap.fine`, `hold.turn`, `hold.right`, `hand.roll` | pokes, fine steps, held pokes repeating on one axis only, turning by hand |
  | `shade` | a swatch and its strength |
  | `save`, `restored`, `undo` | one undo step, the UI back, undo / redo |
  | `cancel` | nothing kept |
  | `move`, `move.keeps_turn`, `move.undo` | a new spot on the floor, its rotation unchanged (< 0.01°), and undo back into the gap, turned as it was |
  | `new`, `new.keeps_turn` | a new part oriented, shaded and placed (auto-saves) with the view's rotation (< 0.01°) |
  | `delete`, `similar` | delete + undo, View similar |

  It drives the real input path: hub pointer and grip overrides, presses, the Context button, and every control
  **poked by name** through its own `GlassButton.Press` (`EditViewCheck.Poke`: "TiltUp", "Save", "Swatch5", "bar:Save",
  "menu:Edit"; nothing falls back to the view's calls). Flies and holds run on the view's clock. 25 checks. It leaves
  the AC open in the view, with the world dimmed, for a capture.
- Step by step: `AgentHarness.EditMenu()`, `EditOpen()`, `EditPoke("TurnLeft")` (any control by name), `EditTap(…)`,
  `EditSwatch(5)`, `EditMove()`, `EditSave()`, `EditCancel()`, `EditPlace()`, `EditState()`, `EditReach()`.
- Held, for a capture: `AgentHarness.EditHold("TurnLeft", 1, false)` pokes the knob and keeps it in for a second
  (seven steps: "Turn 35°"); the knob stays inked. `EditHoldEnd()` lets go.

## 5. What was decided

- **Two-hand scale:** not built. The menu hand's pinch is Finish (e61c87f), so a two-handed grab would need an
  input-routing change. A part's size is the listing's, and resizable ones have Fit to opening.
- **Upright / Free** (the first design's toggle) is gone. Turning by hand is always free, and each axis has its own
  arrows.
- **Touch, not pinch (edit-touch):** the arrows were picked from the hub's ray, so they took a pinch or trigger and a
  drag; now they're poke buttons and a held poke repeats. The continuous arrow drag is gone (turning by hand stays).
- **Move preserves orientation (edit-touch):** it used to re-seat the part on each surface (mount face to the surface,
  facing the viewer or the wall) and snap it into gaps and taped openings, which undid the turn you had just set. Now a
  Move and the new part's placing only translate it; a part taken straight into the hand (no Edit view) and Grok's
  `place_part` keep the surface placement and snaps.
- **The view is world-locked:** it's placed in front of you when it opens and doesn't follow your head. The move bar is
  a main-slot card, which re-centres like the other windows.
