# AirTools spatial UI — design system

The look is "liquid glass with a solid floor": every surface — windows, cards, buttons, chips, the heads-up pills, the
wrist strip, world labels — is the tool ring's Liquid Glass (a lensing bezel that catches the light, a clearer edge),
over a dark body dense enough that text stays crisp over a white wall; crisp high-contrast Inter text; controls that
respond physically. When glass would hurt legibility, the surface turns solid (high contrast). See §3.

Code: `Assets/AirTools/Runtime/UI` (runtime), `Assets/AirTools/Editor/UiBuild.cs` + `UiAssetsBuilder.cs` (building).
Assets: `Assets/AirTools/UI` (font, materials), `Assets/AirTools/Resources/AirToolsTheme.asset` (the theme).
Rebuild everything with **AirTools ▸ Build UI Assets**, then **AirTools ▸ Wire Main Scene** (which also runs the
asset build).

## 1. Audit (before → after)

| Issue found | Resolution |
|---|---|
| Legacy `TextMesh` everywhere (bitmap font, soft, no weights, no rich text) | TextMeshPro SDF on **Inter** (static atlas, 175 glyphs incl. × · ′ ″ ° ² ⅛–⅞ → ✓), Regular / Medium / Semibold |
| ALL-CAPS labels ("MEASURE: ON", "FIND PARTS", "TAKE") | Sentence case ("Measure", "Find parts", "Take"); on/off shown by the selected state, not text |
| Flat opaque unlit quads and cubes as panels; per-screen colours | One glass shader, three semantic tiers, tokens for every colour |
| Stock Building Block poke buttons (grey rounded boxes) cloned everywhere | `GlassButton`: keeps the ISDK PokeInteractable, adds a RayInteractable on the same surface, glass visual with every state |
| Hip toolbox of 5 buttons, "DONE" needed after every shape | Palm menu (open left palm → tools; close → gone); thumb + middle pinch = done |
| Ship's chest as the entry point | One primary "Enter world" pill with a first-use hint |
| Wrist notebook fighting the palm for the left hand | Notebook window 50 cm ahead in the lower view, stays put, movable with a grab bar |
| Measurement labels, part callouts and level readings overlapped each other | Screen-space label layout every frame (priorities, shortest move, leader lines, declutter) |
| Labels cut in half by the wall they annotate when seen at an angle | World annotations draw as an overlay under the UI (never hidden by scene geometry) |
| Crate menu 80 cm away (beyond reach), fixed height with empty slots | Within reach (~55 cm), grows to fit its results, segmented search control |
| Spec card placed separately and hidden behind the toolbox | Inspector section inside the palm menu's single glass panel |
| No hover/press/disabled states, no accessibility options | Full state set on every button; High contrast and Reduce motion toggles |
| No status feedback | Toasts: saved reading, placed part (fit), export result, first-use hint |

Before/after captures (Simulator, Game view): `docs/ui/before_crate_chest_toolbox.png`, `before_spec_card.png` →
`after_palm_menu_inspector.png`, `after_find_parts.png`, `after_notebook.png`, `after_labels_overlay.png`.
(The palm-menu capture is forced open from the side, hence the foreshortening.) Note: the AC callout in
`after_find_parts.png` still shows the rich-text bug fixed afterwards (tabular figures now skip `<…>` tags).

## 2. Tokens (`UiTheme`)

Tokens live in code (`UiTheme` defaults); the asset carries them plus asset references. Never hard-code a colour,
size, radius or duration in UI code — read `UiTheme.Current`.

**Colour roles** (dark, spatial; `UiTheme.Colors` is the source): the window's dimming glass #14161B at 94% (liquid glass;
was smoked #1C1F27 at 95%) · rows / cards / heads-up pills #22252D at 95% · `textPrimary` #EDEEF2 ·
`textSecondary` 68% · `textDisabled` 40% · `danger` #FF5C54 (text / status) · `dangerFill` #C62828 (armed destructive,
`onDanger` white, 5.6:1) · `success` #40D185 · `warning` **#FF9F43** (caution: orange, clear of the ink) · `separator`
white 12% · `control` / `controlHover` / `controlPressed` / `controlDisabled` white 6 / 7 / 11 / 5% (liquid glass: the rim
carries hover and press, so the veil steps less than the old 8 / 14 / 22 % and the label keeps 4.5:1) · `handle` white 35%.

**UX D3 (decided; SPEC §9 "UX D1–D7"):** one meaning per colour.
- **White = "do it"**: `primary` #F5F6F8 (hover #FFFFFF, pressed #D5D9E0) with `onPrimary` #0F1115 labels (17.5:1).
  One primary per view.
- **Tape-yellow ink = selected / active / measured**: `ink` #FFD23F (hover #FFDB66, pressed #E8B923) with `onInk`
  #17140A text (12.8:1): selected toggles, chips and segments, the active tool on the ring (label + lens), a coached
  primary, the Best pick badge, "you are here", the teleport disc, a dragged grab bar, the hold-to-pay ring, the part
  box before its fit check, measure lines, the transition scan band. A selected row is `UiTheme.Wash(elevatedSolid,
  inkWash)` (ink at 20%).
- **Blue = information only**: `info` #7CC4FF — toast and reply tone dots, the busy status dot, the ask pin
  (`ColorRole.Info`).
- The old `accent` / `onAccent` / `ColorRole.Accent` remain only as `[Obsolete]` aliases (ink / onInk / info) so code
  from parallel branches compiles; don't use them.
- `ErgonomicsTests` checks the pairs from the tokens: labels on primary and ink ≥ 7:1 in every state, secondary text
  on a window and on a (selected) row ≥ 4.5:1 with a white wall, grey or black behind the window.

**Shape**: radius small 6 mm · medium 9 mm · large 13 mm · pill = half height (the 12 / 18 / 26 scale at hand
distance). Rim 0.9 mm, rim strength 0.22; the top-lit gradient is per glass role now (§3; `shape.gradient` 0.035 is legacy).

**Spacing**: 4 / 8 / 12 / 16 / 24 / 32 mm at hand distance.

**Elevation**: none · subtle (1.5 mm drop, 4 mm soft, 22%) · panel (3 mm, 10 mm, 30%) · modal (5 mm, 18 mm, 40%).

**Motion**: fast 100 ms (press) · standard 160 ms (hover, open/close, toasts) · modal 220 ms; exponential ease-out;
hover scale 1.02, press 0.97 and 3 mm inward. Reduce motion → instant, no scale.

**Typography** — **em heights** in dmm (mm of em seen at 1 m; `UiText` converts for the surface's *real* read
distance, set with `UiBuild.Distance`): display 32 · heading 26 · title 22 · body 20 · label 18 · caption 16 ·
numeric 22. Nothing below the 16 dmm floor (`UiTheme.EmFloorDmm`; `ErgonomicsTests` walks Main.unity), no
`<size=%>` shrinks except the 70 % window-title subtitles (26 × 0.7 = 18.2).
Weights: Semibold titles/selected, Medium controls/labels/values, Regular body. Live numbers use tabular figures
(`UiText.Tabular`). Body and caption get extra leading.

## 3. Liquid glass on every surface (glass roles)

Every UI surface draws the tool ring's **Liquid Glass** (§8) through the one shader `AirTools/Glass`: a squircle bezel
whose normal tilts outward in the rim (the lens), a key light (world up tilted toward the eye by `keyLift` 0.55, the
ring's) that puts a specular highlight on the top rim and a dimmer fill on the bottom one, Fresnel and a studio-gradient
reflection at grazing angles (the pre-blurred room), a thin inner stroke, a noise-normal ripple that makes the highlight
liquid, and a **clearer edge over a solid floor**: the bezel band is more transparent than the body, the body behind text
stays dense. Light fills (the D3 white primary, tape-yellow ink) keep their bevel instead (lit top, darker keyline).

**The look is the surface's role**, and it travels in vertex data (`GlassMesh`: UV2.w glow, UV3 bezel · highlight · edge
clearness · press, UV4 sheen · frost), so each role keeps **one shared material** and nothing is ever instanced (no
per-instance materials or MaterialPropertyBlocks — they would also take the surfaces out of the SRP batcher; only the
ring, whose shape changes every frame, uses a property block). `GlassSurface.role` defaults to **Auto**, which
`GlassLooks.Resolve` reads from the surface (so every builder, including ones written before roles, inherits the look):

| Role | Auto picks it for | Material (shared) | Bezel · highlight · edge clear · sheen · frost · gradient | Use it for |
|---|---|---|---|---|
| **Window** | a `GlassRegular` surface (`UiBuild.Panel`) | Glass Regular (queue 3100, writes depth) | 6.5 mm (≤ ¼ of the half height) · 0.8 · 0.45 · 0 · 0.006 · 0.008 | every window / menu panel (Settings, Find parts / catalog, notebook, Adjust, sellers, checkout, receipt window, the spec card, Grok / coach / overlay cards) and floating glass pills (the wrist strip, the G1 chip). Body text goes here |
| **Card** | an `ElevatedSolid` surface | Elevated Solid | 4 mm · 0.4 · 0.15 · 0 · 0.004 · 0.008 | rows and cards *on* a window (candidates, notebook rows, the receipt card, Grok rows): quieter, nearly solid |
| **Control** | a tinted surface (`customTint`); `UiBuild.Button` sets it | Glass Clear | 5.5 mm (≤ 45 % of the half height) · 1.0 · 0.35 · 0.012 · 0.004 · 0.012 | every `GlassButton` style, chips, toggles, segments, Model view cards, tinted status pills: a glass capsule on the glass (the ring's Undo / Redo) |
| **Hud** | `hud = true` | Glass HUD (overlay, 3200) | 4.5 mm · 0.6 · 0.25 · 0 · 0.004 · 0.005 | the status line, toasts, the reply card, the job rail: near-opaque behind text, a light rim |
| **Label** | `overlay = true` | Glass Label (overlay, 3040) | 30 % of the half height (scaled spaces) · 0.5 · 0.2 · 0 · 0.004 · 0.02 | pills behind world annotations |
| **Mark** | any side ≤ 12 mm (and the Shadow tier) | its tier's | flat (none) | status dots, the selected bar, separators, the grab bar, swatch dots, stems |

Highlight 1 = the ring's own amounts (specular 0.42, Fresnel 0.2, stroke 0.28, reflection 0.18). **Frost is the blur
stand-in**: there is no background blur on Quest (below), so the dimming tint plus a fine fixed grain does its job.
Shared lighting (all glass materials *and* the ring's Liquid Glass material, set by Build UI Assets from `UiTheme.glass`):
`envTop` #EBF5FF / `envBottom` #33383F (the reflected room), `specPower` 20, `keyLift` 0.55.

**When to pick a role by hand** (`GlassSurface.role`, or `UiBuild.Panel(…, role:)`): only when Auto would guess wrong —
a big tinted surface that isn't a control (`Window`), a tiny control that must still read as glass (`Control`), a
decorative bar that is larger than 12 mm (`Mark`). Don't restyle surfaces by hand: change the role's tokens.

**Text contrast on the glass's own light** (`LiquidGlassTests.TextKeepsItsContrastOnTheLiquidGlass`): each role's body
brightens by `InteriorLift` = gradient + frost/2 + sheen (linear); the checks lay that over the tint, over a white wall,
grey and black: text on a window 7.1:1 (secondary 5.1:1, ink 5.7:1), on a row 7.9:1, on the heads-up pill 7.3:1
(secondary 5.3:1), a button label 4.9:1 at rest, 4.7:1 on hover, 3.9:1 during the 100 ms press, onPrimary / onInk ≥ 7:1 at
their darkest bevel. That is why the window tint is darker than before and the veils step less. The bezel stays in the
outer 6.5 mm, clear of window content (≥ 12 mm in) and of a chip's centred label.

**States on the glass** (`GlassButton`, `GlassLooks.Glow`): hover brightens the rim, stroke and highlight (glow 0.6),
a press lights it fully and turns the bezel **concave** on top of the 3 mm press depth, selected keeps a faint glow (0.3),
disabled has none. Glow and press ease with the motion tokens and settle exactly (no mesh writes at rest). A borderless
control stays clear at rest: the liquid layer scales with the veil ("presence").

**High contrast**: GlassRegular and GlassClear draw as ElevatedSolid, and every liquid role takes `glass.highContrast`
(the bezel shape and a faint rim; no clear edge, sheen, frost or gradient).

Other materials: Glass Shadow (elevation cue behind panels, soft analytic falloff, queue 3090) and the ring's Liquid Glass
(§8). Text materials: Inter Regular / Medium / Semibold (`TextMeshPro/Mobile/Distance Field`, weight by SDF dilation
0 / 0.10 / 0.20, queue 3110) and Inter Label (the Mobile Overlay variant, queue 3045) for world annotations.
Render order: annotations → UI shadows → UI glass → UI text.

**No stray backgrounds**: inside the UI, a renderer is glass on its role's shared material, text, the ring, or a picture
(photos, thumbnails, QR codes). `GlassCensus` checks it (`LiquidGlassSceneTests.TheWireBuiltSceneHasNoStrayBackgrounds`
on Main.unity; `AgentHarness.GlassState()` live).

## 4. Components and state matrix

`GlassButton` styles: Primary, Secondary, Borderless, Destructive, Toggle, Chip. Input: ISDK PokeInteractable
(direct touch) + RayInteractable (hand ray pinch / controller trigger) on the same clipped plane surface, so the
target bounds are the visual bounds. `Clicked` fires once on entering Select; cooldown 0.3 s. Hooks:
`UiFeedback.Press` (generated tick sound + controller haptic), `UiFeedback.Hover` (light haptic, controllers only).

| State | Secondary / Toggle / Chip | Primary | Destructive | Borderless |
|---|---|---|---|---|
| Rest | white 6% glass capsule (liquid rim), Medium label | white `primary` glass, dark Semibold `onPrimary` label | danger 28% | clear |
| Hover / focus | 7% veil, brighter rim (glow 0.6), +2% scale | `primaryHover` #FFFFFF, brighter rim | danger +15% | 6% veil |
| Press | 11% veil, full rim, concave bezel, 97% scale, 3 mm in | `primaryPressed` #D5D9E0, bevel inverts | darker danger | 7% veil |
| Selected (toggle) | `ink` fill + dark `onInk` bar under the label + Semibold `onInk` label | ink (the coach points at it) | — | — |
| Disabled | 5% veil, textDisabled label, no press | same | same | same |
| Confirm (destructive) | — | — | first press arms: solid danger + "Tap again" for 3 s; second press fires | — |

Light fills stay glass (D3): `AirTools/Glass` detects a light, opaque vertex colour (the white primary, ink) and turns
its top light into a ±10% multiplier and its rim into a bevel — white along the top, a darker keyline at the bottom —
so a white pill still reads as lit glass, not a flat sticker. Dark panels and the translucent veils draw as before.

Other components: `GlassSurface` (panel/card/row/chip/indicator), `UiText` (text by role), `UiToast` (status pill,
tone dot + words — never colour alone), `WindowHandle` (grab bar: ray pinch-drag moves the window, Y-billboarded),
`PalmMenu` (hand menu, see §5), `MeasureLabel` + `LabelLayout` (world annotations), `SpecCard` (inspector),
`CandidateCardView` (selectable rows), segmented control = Toggle buttons sharing a row (`PartsBrowser.searchButtons`).

**Search field + glass keyboard (catalog, 2026-09-26):** the Catalog's field is a pill `GlassButton` (selected = ink
while typing, a caret, a Phosphor magnifying glass) and its keyboard (`CatalogKeyboard`, `CatalogBuilder`) hangs under the
window, tilted back 25° and 4 cm nearer: digits, QWERTY, `-`, `"`, Delete (Phosphor backspace), Clear, space and Search
(the primary); every key a 3 × 3 cm `GlassButton` (poke + ray, 0.1 s repeat). Ours, not the system keyboard (not in this
SDK): the same in passthrough and the world, hands or controllers. The key set and field rules are `CatalogTextField`.

Not built: dropdowns, sliders, dialogs — the app has no continuous settings, and the only destructive action (Remove)
uses the in-place confirm above. Build them from `GlassSurface` + `GlassButton` following §6 when they are needed.

## 5. Placement

One map of head-relative placements (docs/ux/declutter.md §3.1): `UiZones` in `Runtime/UI/UiZones.cs`. Builders and
views read their zone there; `DeclutterTests` fails on any new overlap between head-relative surfaces (its
`KnownDebts` lists the ones still open) and `AgentHarness.Surfaces()` / `SurfaceCheck(activity)` count what's up
against the §2 budget. A new surface follows declutter §3.3: a fact about the scene is a world label; a sentence is
`StatusLine.Flash` / `SetProgress` / `UiToast.Reply`; a decision or rows are a main-slot card; nothing else gets its own
head-relative pose.

| Zone | Distance · pitch · yaw | Max size | Holds | Behaviour |
|---|---|---|---|---|
| `Status` | 0.9 m · 17° below the **gaze** · 0° | 0.44 × 0.12 m | the one heads-up line: the Step's status + coach line, toasts (a 2.4–6 s flash on line 1), replies (take its place for 5–10 s, ≤ 4 lines), "do the whole job" progress (the 7-glyph strip, then the summary for 8 s) | heads-up (overlay glass + text); follows the gaze softly; while a main-slot window is open it **docks** on the window's top rim — **wholly above it** (its bottom edge 5 mm over the rim, `StatusLine.DockCentre`; facing the eyes, scaled so its angular size holds; line 1 only). Rail off: the toast pill (0.6 m, 12°, 26 cm) is the fallback and waits while a reply is up; with a main-slot window open the toast and the reply dock there too (above the line when it shows), never over the panel |
| `Main` | 0.45 m · 20° below the eye line · 0° | 0.36 × 0.44 m | cards, one at a time (`WindowSlot`): Notebook, Sellers → Checkout → Receipt, Survey, Ladder, Grok card, overlay card | world-locked, re-centres past 40°, movable by its grab bar; Checkout has Back to Sellers; the receipt has Take it home |
| `SideLeft` | 0.5 m · 15° · −35° | 0.30 × 0.40 m | Find parts (after the first tape or a parts search) | 5 cm behind the main slot: its inner edge may overlap a main window by ≤ 6° |
| `SideRight` | 0.5 m · 18° · +35° | 0.34 × 0.46 m | the Scene window (Settings; once More) | as SideLeft |
| `Pill` | 0.45 m · 24° · +22° | 0.20 × 0.084 m | the Next-step pill (primary + ≤ 2 chips hanging under it) | poke range; hidden while the main slot or Settings is open (the window's own primary is the next step) |
| `Enter` | 0.45 m · 25° · 0° | 0.15 × 0.06 m | Enter world + hint | passthrough only (`HeadAnchor`: first tracked frame, recentre, back to passthrough) |
| Wrist | 0.05 m above the left wrist / controller | 0.16 × 0.05 m | the limits chip | hand |
| Palm | 3 cm off the left palm (hands) / 12 cm above the left controller (menu button) | ring radius 0.105 m | the tool ring | opens after 0.12 s with the left palm turned **up** (dot ≥ 0.6), in view and above the chest, ≥4 fingers extended; stays open down to 3 fingers; closes 0.25 s after a fist or turning away; presses ignored for 0.35 s after opening; faces the eyes, smoothed. Copy says "turn your left palm up" (hands) / "press the left menu button" (controllers) |
| Quad | 1.5 m at the capture camera (floating fallback 1.5 m, 8° down) | ≤ 1.2 m wide | one picture or video | world |
| World labels | on the thing they measure, lifted 10 cm | – | – | constant angular size, overlay, laid out without overlaps |
| Edit view (edit-touch, `EditViewMath.Layout`) | centred 0.42 m · 20° · 0°: the arrows on the 0.42 m sphere round the eye, the panel 0.44 m (tucked into the ring's lower right, ~25 cm down) | ≈ 0.57 × 0.46 m | the item (largest side ≤ 0.25 m), 12 poke arrows (3.2 cm, hold to repeat), the panel | world-locked when it opens; poke only; not a slot — it closes every window while it's up (docs/edit-view.md) |
| Part menu (edit-touch) | 0.42 m · 18° · 14° right of the part | 0.15 × 0.17 m | Edit / View similar / Delete | placed once when it opens; poke only; presses armed after 0.35 s |

Hands: the right index finger pokes. Buttons are **poke-only** by default: the measuring ray once pressed a button in
its path (e61c87f). UX D5 (`GlassButton.RayOnWindows`, off) turns on ray targets for window and Enter buttons, with a
120 ms hover dwell. While a ray rests on a button, tool presses are suppressed.

## 6. Building new UI (checklist)

1. Build in the editor script with `UiBuild` — never hand-place meshes/fonts (and never an opaque quad or a Lit cube as a
   background: the glass census fails it): `UiBuild.Distance = <read distance>`,
   `UiBuild.Panel(...)` (one GlassRegular panel per window), `UiBuild.Text(parent, name, text, TypeRole, pos, ColorRole, align, width)`,
   `UiBuild.Button(parent, name, "Sentence case", size, ButtonStyle, template, pos)`, `UiBuild.Handle(...)` for movable windows.
2. One glass panel per window (Window role); controls sit on it as glass capsules (Control); rows/lists use ElevatedSolid
   (Card); nothing else stacks glass on glass. The look comes from the role (§3) — pick a role only when Auto guesses
   wrong, never restyle a surface by hand.
   Button and chip labels fit their button by themselves (`UiBuild.FitLabel`: auto size down to the caption floor, a
   second line, then "…"); set a label's box yourself only when you lay it out elsewhere on the button.
3. Targets ≥ 3 × 2.6 cm at hand distance; keep 4–8 mm gaps; put windows 45–70 cm away, below eye level.
4. Colours/sizes/durations from `UiTheme.Current` only; text by `TypeRole`; numbers through `UiText.Tabular`.
5. State by more than colour (selected bar, words, weight). Destructive actions confirm.
6. Hook behaviour to `GlassButton.Clicked`, not to raw interactable events.
7. Add the new glyphs to `UiAssetsBuilder.Charset` and rebuild if you print new symbols.
8. Verify in the headset (text size and contrast), with hands and controllers, in passthrough and in the world.

## 7. Quest 3S performance note

- Materials: one shared glass material per role (Glass Regular / Clear / Elevated Solid / HUD / Label / Shadow) + the
  ring's Liquid Glass + 4 text materials for the whole UI; per-surface shape, look and state are vertex data, so no
  material instances or property blocks, and the SRP batcher keeps batching them.
- **No scene sampling.** The ring's Liquid Glass never sampled the scene, and neither does the panel glass: no grab
  pass, no URP opaque texture, no blur taps, no render textures (`LiquidGlassTests` fails on any texture sample in
  `Glass.shader`). An opaque-texture glass would cost a resolve + copy of both eye buffers (~1.5 ms on the 3S, and it
  breaks the tile-based pass), then 9–13 blur taps per pixel over every panel — and in passthrough it would refract a
  black eye buffer anyway (the room is composited under the app by the compositor). The "refraction" is faked: bezel
  normals + a pre-blurred studio gradient + Fresnel + a noise-normal highlight.
- **Cost of the liquid layer.** A main-slot window (0.36 × 0.44 m at 0.45 m) covers ≈ 44° × 52°, ≈ 0.85 M px per eye
  (1.7 M for both). Its interior runs the flat glass's path (distance field, top light, frost, rim line: ≈ 45 ALU); only
  the bezel band (≈ 7 % of a window's pixels) and the small controls run the lighting (≈ +70 ALU: three normalises, two
  `pow` highlights, Fresnel, the reflection, the stroke, four `sin` ripples). Estimate ≈ +0.05–0.1 ms GPU with one
  window, a side window and the ring up; overdraw is unchanged (one panel per window, controls are small, the shadow's
  early-out path is cheap). The kitchen runs at 80 % GPU median / 94 % max, so this is measured, not assumed:
- **The A/B switch.** `AIRTOOLS_GLASS_LITE` (a global keyword, `GlassQuality`) draws the flat glass for every
  AirTools/Glass surface without touching a material: `AgentHarness.GlassLite(true|false)`, or at launch
  `adb shell am start -n com.airtools.quest/com.unity3d.player.UnityPlayerGameActivity -e glass lite`. Each switch logs
  `Glass: lite` / `Glass: liquid`, so `python3 tools/demo/fps.py <log> 'Glass: lite' --until 'Glass: liquid'` splits the
  VrApi samples. If the liquid glass costs a frame, ship Lite for the big roles (set their `highlight` to 0) before
  touching the ring.
- Text: one static 2048² Alpha8 atlas (4 MB); TMP meshes rebuild only when their text changes; the label layout
  moves transforms (no text rebuilds) and allocates nothing per frame after warm-up. Button labels auto-size (TMP) only
  when their text or colour changes.
- Measured on the Quest 3S: see SPEC §10 (UI entry) for FPS / GPU % from `adb logcat` VrApi lines.

## 8. Liquid Glass and the tool ring

The palm toolbox is a **tool ring** (`ToolRing`, built by `MainSceneBuilder.BuildPalmMenu`): Apple Photos' edit dial as a
prize wheel, in **Liquid Glass** (`AirTools/LiquidGlass`, material `UiTheme.materials.liquidGlass`).
- **Material:** glass that bends light at its rim rather than frosting it. Since the glass lane every other surface
  draws the same look through `AirTools/Glass` (§3), and Build UI Assets gives the ring's material the same room and key
  light (`UiTheme.glass`). Analytic shape in surface metres on a
  tight mesh (`GlassRingMesh`), squircle bezel normals, a world-fixed key light (highlight slides as the hand moves)
  plus an opposite-edge fill, Fresnel and a studio-gradient reflection, a thin inner stroke, a dark neutral body (the
  visionOS dimming layer, so white glyphs read over a bright room), a soft outer halo instead of a shadow; the
  selection lens is clearer and merges into the arc (smooth union); `_Touch` glows under the fingertip, `_Pulse`
  flashes the lens on each detent. One transparent pass, no grab pass or blur. `_Shape 1` draws capsules.
  D3: while the tool in hand sits under the lens its label is ink and `_LensInk` (set per frame with
  `MaterialPropertyBlock.SetColor`, never `SetVector`: the token is sRGB, the project Linear) turns the lens's inner
  stroke into a tape-yellow keyline over a faint wash; a mode that is only previewed (D6) or an action is not inked.
- **Icons:** Phosphor (MIT) SDF fonts, `UiTheme.icons` (Regular idle, Fill for the item in the lens), codepoints in
  `Icons` (static atlas: add the codepoint there, then Build UI Assets). Glyphs get a soft underlay and a cool vertical
  gradient; no glass-on-glass slabs per icon.
- **Ring:** open with the left palm facing up; the item at the top (under the lens) is current. Pinch on the ring
  with the other hand and move to turn it (1:1 under the fingers); release to coast (release speed averaged over the
  last 60 ms, capped at 13 rad/s, friction 3.4/s), then spring onto an item; a tick (sound, lens pulse, controller
  buzz) as each item passes. A quick pinch or poke on a side item spins to it. Modes (Move, Measure, Level) are
  previewed when the wheel settles and equipped by a pinch on the lens (D6); actions (Notebook, Model view, Settings) fire on
  a pinch on the lens. Undo / Redo capsules at the arc's ends (`EditHistory`, any tool, any mode) go faint with nothing
  to do.
- **Catalog (2026-09-26):** a seventh item, Catalog (Phosphor storefront U+E470, `ToolboxAction.ToggleCatalog`), after
  Level in W1.5's Parts seat: 51.4° apart; with Measure on the lens Move and Level sit at ±51°, Settings and Catalog at
  ±103° (fully opaque), Notebook and Model view hidden one detent from the back (±154°, behind Undo / Redo at ±150°). A
  pinch on the lens opens the Catalog (the left side slot) with no tape needed. The v2 notes below were for six items.
- **Ring v2 (declutter DC3):** six fixed items 60° apart — Move · Measure · Level · Notebook · Model view · Settings
  (the gear, Phosphor gear-six U+E272; it was "More" with the cube-focus icon until 2026-09-26). With
  Measure on the lens, Move and Level sit at ±60°, Settings and Notebook at ±120° (74 % opaque: `visibleHalfAngle` 128°,
  `fadeBandDeg` 12°) and Model view is hidden one detent away at 180°; Undo / Redo at ±150° (`buttonAngle`), 5.4 cm
  from the ±120° items. Step in is cut (the "You are here" pin and Model view both step in).
- **Settings** (once "More"; the Scene window, renamed; the right side slot, 0.5 m, 18° down, 35° right). Settings
  first, scene actions below:
  - "Settings" + Close;
  - the status: scene · scale ("Scale ×1.63 · set for this scan" at a site's default, "Scale ✓ set (×k)" after a
    tape) · the scan credit · "Models on the headset: 5 of 7";
  - Snapping / Contrast / Less motion;
  - the scale keypad and units chip (Reset goes back to the scan's default scale);
  - Layers: Show edges / Labels / Fall edges;
  - Ask + hold to talk;
  - Home / Ladder / Exit world (tap twice);
  - Next scene / Reload / Built-in;
  - Take out: one chip per removable scene part (the file's label; ink while it's out; the panel grew to 0.46 m for
    it).

  Picking Ladder or Exit world closes it. Title rows hold only a title and Close.
- **Modes:** Move is the default (entering the world and after placing a part; UX D1 `ToolManager.Default`, back to Move on 09-27), and two quick
  pinches with the menu hand go back to Move (`OvrToolInputSource.DoublePinchToMove`) — a pinch that finishes a tape
  never counts toward it. There is no Done button: finish shapes with the other hand's pinch, thumb + middle pinch,
  B / Y, or by closing the polygon; an on-cursor chip says "Pinch your left hand to save" / "Press B to save" (UX
  W0.3). UX D4 `MeasureTool.AutoSaveTwoPointTapes` (off) saves a tape at its 2nd point instead ("area" keeps going).
  Pinches during Meta's system gesture are ignored.
