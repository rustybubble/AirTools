# E1: remove the dishwasher from the kitchen scan and measure the cavity

2026-09-26. The first run of the part-removal stage that R1 (`r1-segment-remove.md`) proposed. The
recipe: geometry proposes, the VLM names, SAM draws the pixels, the faces vote, the planes cut.
Scene: `scene/kitchen` r1 (199,997 faces, altitude scale). Hardware: laptop CPU only. No hfbox.

## 1. What the stage does

`python -m pipeline.parts scene/kitchen [--components dishwasher fridge range sink_cabinet]`
(`pipeline/parts.py`) runs after the structure layer:

1. **Name.** 16 evenly spaced thumbnails (640×360), each padded square and sent to Groq
   `qwen/qwen3.8-27b` (vision). The model returns labelled boxes. Keys rotate over
   `GROQ_API_KEY`, `_2`, `_3`. Answers are cached per frame under `work/parts/<site>/vlm/`.
2. **Propose.** Boxes are grouped per class into components. A component whose boxes match a
   structure rectangle (S4 `objects[]`, projected; mean IoU ≥ 0.4) gets that rectangle: its plane
   is the front, and its id goes into `structure_objects`.
3. **Masks.** SAM 2.1 tiny turns each box into a mask. It runs in a separate python
   (`AIRTOOLS_SAM_PYTHON`, worker `pipeline/parts_sam.py`). Masks are cached as
   `<work>/masks/<frame>_<class>.png`. Without SAM the box itself is the mask.
4. **Vote.** Mesh faces are splatted into each view through a z-buffer. A face gets a label when
   ≥ 2 views see it and ≥ 50 % of them agree. Then only the largest connected piece is kept, on
   the vertex-welded mesh.
5. **Bound.** The cavity box is built in a gravity-snapped frame (r, up, d):
   - sides: the outermost vertical edge cluster at the component's edge;
   - top: the long horizontal line or cabinet underside above it;
   - floor: the largest horizontal plane below, levelled;
   - back: the vertical plane behind it, preferring the backsplash that reaches the counter;
   - front: the rectangle's plane.

   Each bound records the planes and edges it used and a σ.
6. **Cut.** A face is removed if it is inside the box, not already taken, and either voted for
   this component, or voted for no component and not in the 2 cm band under the top. Holes
   left in the counter (1 cm cells, top-down) get flat caps. Neighbouring cavities share their
   side value.
7. **Fill and publish.** The cavity box gets flat colours sampled from the neighbouring
   surfaces, marked `estimated: true`. The stage writes `mesh.parts.r1.glb`, `cavity.r1.glb`,
   `collision.parts.r1.glb` and `parts.r1.json`, and adds a `parts` entry to `scene.json`
   (documented in `docs/api.md`). `mesh.r1.glb` and `collision.r1.glb` are untouched.
   `validate_package` checks the new files.

## 2. Results

### Cavity sizes (scene metres, σ geometric)

| id | what | W | H | D | bounded by |
|---|---|---|---|---|---|
| **dw1** | dishwasher | **0.437 ± 0.004** | **0.591 ± 0.020** | **0.445 ± 0.046** | sides e117/e83/o21e1/o29e1 and e300/e9; top e16/e184/e418/e485 (counter lip); floor p7; back p4; front p2; cap p5 |
| bc1 | sink base cabinet | 0.949 ± 0.005 | 0.583 ± 0.022 | 0.451 ± 0.051 | right side shared with dw1 |
| fr1 | fridge | 0.492 ± 0.004 | 1.178 ± 0.023 | 0.543 ± 0.053 | top p13 (cabinet underside), back p4, front p8; no S4 rectangle (IoU 0.33) |
| rg1 | range | 0.517 ± 0.003 | 0.793 ± 0.024 | 0.440 ± 0.044 | top is its own backguard (open top); left side shared with dw1 |

R1's hand-picked dishwasher probe gave 0.434 × 0.581 × 0.458, which agrees within σ. The scene
is about 31 % small against real appliances. A standard dishwasher opening is 0.60–0.61 wide.
The altitude scale (residual 5 cm) is the likely cause. At the nominal ≈ 1.47× factor, dw1 is
≈ 0.64 × 0.87 × 0.65 m. **The tape check (P3, section 5) decides this; no scale fix was applied.**

### P1: dishwasher mask IoU against 8 hand-labelled frames

Frames 0111, 0166, 0331, 0386, 0436, 0491, 0546, 0821. Each was labelled with a polygon: door
plus control panel, no toe-kick.

| mask | median | mean | p10 | min |
|---|---|---|---|---|
| SAM 2.1 tiny, from the VLM box | **0.971** | 0.926 | 0.835 | 0.625 (0331) |
| VLM box alone | 0.723 | 0.735 | 0.58 | 0.43 |
| o32 structure rectangle, projected (R1's ground-truth proxy) | 0.965 | 0.964 | | 0.934 |

On frame 0331, SAM grew onto the counter because the VLM box reached y = 123. The vote absorbs
this: the counter faces get too few votes.

### P2: face purity against the structure-plane volume

| id | voted faces | purity (count) | purity (area) | within 1 cm | recall vs box |
|---|---|---|---|---|---|
| dw1 | 3983 | **0.997** | 0.999 | 1.000 | 0.55 |
| bc1 | 10866 | 0.998 | | 1.000 | |
| fr1 | 9607 | 0.852 | | 0.992 | |
| rg1 | 16593 | 0.938 | | 0.985 | |

Against R1's probe reference volume (o32 shrunk 1 cm, 6 cm behind to 2 cm in front), dw1 scores
purity 0.80 and recall 0.985. Every face outside that volume sits in the 1 cm rim the reference
trims; none are behind or in front. Recall against the full box is low because the box also
contains the unvoted inside of the door recess and the toe-kick. The cut takes those through
the "unclaimed" rule (1580 faces for dw1).

### Gravity snap

The fitted floor is tilted 4.25° and the fitted back wall 6.17°. Both are reconstruction error:
the room is level. The stage extends the levelled planes. The ranges below show H and D if the
tilted fits were extended instead, across the footprint:

| id | H snapped | H from the tilted floor | D snapped | D from the tilted back |
|---|---|---|---|---|
| dw1 | 0.591 | 0.593–0.630 | 0.445 | 0.353–0.422 (up to −21 %) |
| bc1 | 0.583 | 0.583–0.625 | 0.451 | 0.350–0.423 |
| fr1 | 1.178 | 1.177–1.221 | 0.543 | 0.437–0.569 |
| rg1 | 0.793 | 0.798–0.836 | 0.440 | 0.353–0.445 |

Snapping matters most for D. Half the snap delta goes into σ.

### Neighbour damage: the protected cut against a plain "everything in the box" cut

| id | faces cut (protected / plain) | faces taken from other components | counter holes cm² (protected / plain) | capped cm² | band faces kept | leftover fragments |
|---|---|---|---|---|---|---|
| dw1 | 5563 / 6809 | **0** / 363 (from bc1) | 33 / 75 | 117 | 883 | 0 |
| bc1 | 16487 / 17295 | 0 / 0 | 122 / 162 | 338 | 808 | 0 |
| fr1 | 11731 / 12564 | 0 / 0 | – (no cap plane) | 0 | 833 | 0 |
| rg1 | 19395 / 19395 | 0 / 0 | – (open top) | 0 | 0 | 0 |

### Timings (laptop CPU, cold, all four components)

| step | s | notes |
|---|---|---|
| name (Groq) | 236 | 11 calls plus 5 cached, 0 failed; 2–46 s per call, mostly 429 waits |
| masks (SAM 2.1 tiny) | 285 | 41 images at 6.5 s each; model load 1.2 s |
| vote | 1.6 | |
| bound, cut, fill | 2.2 | |
| publish | 6.4 | the GLBs and a JPEG q92 atlas; `mesh.parts` is 6.0 MB |
| **total** | **532** | a cached rerun takes 11–14 s; peak RSS ~1.2 GB |

## 3. Renders

- `e1-dw1-0141.jpg`, `e1-dw1-0426.jpg`: the photo, then the mesh before, then the mesh with the
  dishwasher removed, from two recorded cameras.
- `e1-dw1-cavity-closeup.jpg`: the dw1 cavity from the front, left and right.
- `e1-dw1-bc1-fr1-rg1-0426.jpg`, `e1-dw1-bc1-fr1-rg1-0606.jpg`: all four components removed.
- `e1-viewer-dw1.jpg`: the web viewer with dw1 toggled off, the cavity shown, and its size in the
  panel.

Viewer (`work/viewer/index.html`, gitignored, so not committed): a "Parts: remove and measure"
panel with one checkbox per component. The page loads `parts.json`, `mesh.parts.glb` and
`cavity.glb`. Ticking a box hides `part_<id>`, shows `cavity_<id>` with a green outline, and
prints W × H × D ± σ (times the scale input). Checked in headless Chrome: all 4 part and cavity
nodes resolve; a real click toggles them; the readout says "dishwasher removed: 43.7 × 59.1 ×
44.5 cm". The only console error is the missing favicon.

## 4. What is weak

- **Scale.** Everything is about 31 % small until the tape check. σ excludes the scale error.
- **D is a model, not a measurement.** Nobody has seen the back of the dishwasher bay. D is the
  distance to the backsplash plane, levelled (σ 4.6 cm). A real bay can be shallower (pipes,
  the sink trap).
- **Fridge.** It has no structure rectangle (best IoU 0.33), so its front comes from the votes.
  Its W and D are unverified, and its purity is the lowest (0.85).
- **Range.** It has no top cap: its own backguard is the top, so the cavity is open above.
- **Sink base cabinet.** Removing it takes the sink basin with it. The hole is capped flat.
  Counter-top objects need a protector class ("countertop", "sink": never cut).
- **Colours.** The cap colour is darker than the lit counter: it is sampled from the up-facing
  ring, which includes shadowed faces. Flat colours only. Grok Imagine was not used ($0 spent).
- **Side heuristic.** "Outermost vertical edge cluster" works here because the neighbours have
  sharp door edges. A flush panel run will need the rectangle groups.
- **Groq 429s** dominate the runtime. The stage retries after a 30 s sleep and resets the key
  pool, up to 6 times.
- **Provenance.** Masks read back from the cache report `"masks": "precomputed"`.
- **Node names changed** from R1's `part/dw1` to `part_dw1`: three.js strips `/`, and Unity
  reads `/` as a path.
- **SAM** is not in the project env. It runs in a scratch venv through `AIRTOOLS_SAM_PYTHON`
  (torch, torchvision, transformers ≥ 5.17, pillow, numpy). torch is not a clean or light
  install, so it did not become a `parts` extra. The stage also accepts precomputed masks.

## 5. The tape check (P3): measurements needed

Three readings of each, in cm:

1. Dishwasher opening **width** at the front: the inside faces of the two neighbouring cabinets.
2. Floor to the **underside of the counter** at the front.
3. Door plane to the **back wall** at floor level. If the dishwasher cannot be pulled out, give
   the counter depth from the wall to its front edge.
4. Dishwasher front panel width (for the scale).

| quantity | stage (scene m) | tape (m) | ratio |
|---|---|---|---|
| W | 0.437 ± 0.004 | | |
| H | 0.591 ± 0.020 | | |
| D | 0.445 ± 0.046 | | |
| panel width (o32) | | | |

## 6. Next steps

1. **P8 on hfbox:** SAM 2 speed on ROCm (RX 9070 XT), then video propagation instead of
   per-frame boxes, so one prompt covers all frames and the 285 s mask step shrinks. Wait until
   hfbox is free.
2. Enter the tape numbers. Apply the panel-width scale if the ratio holds for all three dims.
3. Wire the stage into `pipeline/run.py` after the structure layer (optional, full quality only).
4. Add a countertop/sink protector class so bc1 keeps the basin.
5. P7: the Unity side hides `part_<id>`, shows `cavity_<id>`, and places the replacement at
   `cavity.insert`.
