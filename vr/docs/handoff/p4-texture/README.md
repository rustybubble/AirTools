# p4-texture: product cut-out, front-only photo, sides from the product, CAD front check

- **Backend branch:** `texture` in `~/airtools-backend-demo`, 6 commits on `demo-next2` (8420b3c) →
  `39e4825`. Worktree: `/private/tmp/claude-501/-Users-ravil-AirTools/backend-texture`.
  Patches `0001`–`0006` in this folder.
- **App changes:** none. The app still loads `model.glb`. `part.json`'s asset gains a `texture`
  object, which the app's `MissingMemberHandling.Ignore` skips.
- **Tests:**
  - `tests/test_texture.py`: 24 new tests.
  - The full suite: 1276 passed, 1 skipped (`tests/pipeline` excluded; it needs pymeshlab).
  - `ruff` is clean.

## What changed (all three fixes the benchmark asked for, on every tier)

**1. Cut the product out of its photo** (`server/photo_cut.py`, flag `ASSET_PHOTO_CUT`, on by default)

| Photo | Method | Share of 162 photos | Time (median / max) |
|---|---|---|---|
| Studio background, i.e. ≥ 40 % of the border is one colour | Flood the border colour in from the edges, keep the biggest blob, fill holes. Background seen through the product (a bench's slat gaps) stays background; a steel highlight doesn't. | 153 | 105 / 230 ms |
| Cluttered (the LG showroom) | U²-Netp salient object: 4.6 MB onnx on CPU through `onnxruntime`, without rembg. Picks the largest central object whose box aspect matches the listed W × H. | 5 | 177 / 251 ms |
| Cluttered, but the product fills the frame | Whole photo (`frame`), when the photo's aspect matches any face of W × H × D. Some listings swap W and D. | 2 | 174 / 190 ms |
| Neither of the above | Old behaviour: a plausible flood, else the whole photo | 2 | |

- **Why this method:**
  - It runs on this Mac with no GPU.
  - It costs $0 and makes no network call once the model file is present.
  - The model loads in about 0.1–0.5 s, once per process.
- **Other options benchmarked on the 4 benchmark photos:**
  - `u2net` (176 MB): 0.26 s. It cut the range's bottom drawer and lost the dishwasher.
  - `isnet-general-use` (179 MB): 0.7 s. It failed on the fridge.
  - `u2netp` (4.6 MB): 0.1 s. It gave the cleanest fridge.
  - The border flood beats all three on white studio backgrounds.
  - Grok vision + GrabCut would cost a call per part.
  - The pipeline's SAM 2.1 needs torch in a separate python, which isn't installed here.
- **Keystone:** a box face shot slightly off-axis is warped back to a rectangle.
  - It applies when the mask fills ≥ 95 % of a quad with four straight edges, and the corners are 2–20 % off the bounding box. Example: the LG fridge photo is wider at the top.
  - The warp is used only when the model's front footprint is itself a rectangle, so a tapered planter isn't straightened.
  - A real 3/4 view is left as it is: the GE range shows its cooktop from above.
- **Cache:** `image.cut.png` (RGBA, cropped to the product) and `image.cut.json` (box, method, quad, colours, version 3), both next to `image.jpg`.
- **Model file:**
  - It lives at `<DATA_DIR>/cache/models/u2netp.onnx`, downloaded once with an md5 check.
  - Without it (offline, or no onnxruntime), cluttered photos keep the old crop.
  - `onnxruntime` is a new dependency, which also pulls in flatbuffers and protobuf, so `uv sync` is needed.

**2. The photo only on the product's front** (`meshgen.project`)

- **Old projection** (still available as `_project_legacy`, used when the flag is off):
  - It was a planar orthographic projection along the face normal, the same for every tier, Hunyuan included.
  - It covered every triangle within 70° of the face normal that can see the camera.
  - It stretched the photo over the whole bounding-box face.
- **New projection:**
  - The cut-out is fitted to the front footprint: the extent of the triangles within 60° that can see the camera.
  - Nothing else gets the photo. That means no photo smears on a Hunyuan tub's sides or a knob's rim.
  - Texels just outside the outline take the product's edge colour. Further out they fade to the body colour, so no showroom or white corners show.

**3. Colour the sides and top from the product** (`meshgen.recolor`, flag `ASSET_SIDE_COLOR`, on by default)

- **Colour sampling:**
  - The product's pixels are clustered by k-means.
  - **Body:** the colour that owns the most pixels, overall and in a band just inside the outline, merging the light and dark slices of brushed steel. Examples: dishwasher `#A3A3A3`, GE microwave `#B9B9B9`, LG fridge `#87847C`, knob `#9A876B`, bench `#3A4A63`.
  - **Trim:** the biggest other material, for example black.
- **Where the body colour goes:**
  - Hunyuan meshes and boxes: entirely.
  - Grok CAD pieces left the default grey `#B0B0B0`: small ones take the trim colour.
  - Template shell roles still in the template's default colour.
- **Kept as it is:** Grok's own meaningful paint.
- **Material cue:**
  - `metal` (listing says stainless, steel, bronze, …): metallic 0.35, roughness 0.35 on the sides; 0.12 / 0.5 on the photo.
  - `plastic`: 0 / 0.7.
  - `painted`: 0 / 0.5. "Matte" in the listing wins over metal.
  - CAD pieces painted near the body colour get the cue.

## Grok + OpenSCAD orientation: the diagnosis

**Measurement across all 81 CAD models on disk:**

- **Method:** orthographic views of +Z and −Z, counting the pieces seen, the non-body share, the relief, and the correlation of each side's paint with the product photo.
- **Results:**
  - **No model is built backwards: 0 of 81.**
  - The front shows more pieces than the back in 43 models, the same number in 38, and fewer in none.
  - In 35 of the 38 appliances (dishwashers, ranges, fridges, microwaves, hoods, ACs) the front has more detail than the back; the other 3 are equal.
  - Photo correlation (front NCC vs back NCC) is 0.7–0.9 on the front for the dishwashers.
  - Photo check: the photo lands on +Z in every served `model.scad.glb`.
- Full numbers: `cad_orientation_81_models.json`.

**The pose is not the problem either.**

- The kitchen's `insert.axes` are right-handed: `d = r × up`, and `d` points out of the opening.
- The app's `Facing = FromBasis(-Right, Up, Out)` maps glTF +Z (the front) to Out, and glTF +X to r. I checked this numerically on `dw1`: determinant +1.
- CAD, template and HF all share that one frame and one `model_url`.

**What the user saw ("the front is a flat surface") is real, but it isn't a reversal.**

- **Symptom:** 25 of those 38 appliance CAD fronts are one flat slab. Grok paints the handles, control strip and kick plate flush, because `panel()` sits inside the full-depth body, so less than 2 % of the front view is off-plane.
- **How it reads:** from an angle, or before the photo is on, such a front reads like the back.

**Fixes, at the source:**

- **`SCAD_PROMPT`:** now spells out that the front carries the handles, controls and seams at Z = D, and that those features stand proud: `box(z1=D-t)` plus a piece per feature.
- **Relief pass:** an appliance whose front is still flat gets one feedback pass (`RELIEF_NOTE`). The answer is kept only if it fits and adds relief.
- **Live check:** Frigidaire dishwasher, grok-4.7 low.
  - The first answer was still flush; the relief pass made the handle and controls proud (25 % of the front view off-plane).
  - 2 calls, 102 s, $0.047.
  - Render: `renders/orientation_flat_front_relief.png`.
- **Post-check** (`meshgen.orientation`, flag `ASSET_SCAD_ORIENT`):
  - It turns a model 180° about Y when its back matches the photo, or shows the detail, clearly better than its front.
  - It runs after every CAD build and in every texture step, including `--retexture`. It turned 0 of the real models.
  - Synthetic demonstration: `renders/orientation_backwards_synthetic.png`.
- **`place_part`:** its `model_url` now carries `?mode=llm_scad` or `?mode=hf` for the session's generator (replace job and agent). Before, it was whichever variant was selected last.

## Rebuild: `python -m server.warm --retexture`

- **What it does:**
  - It re-textures every variant (auto, hf, llm_scad) from its own geometry: `scad.glb`, the raw Hunyuan mesh cached by photo hash, the template from the cached plan, or the box.
  - It runs OFFLINE: no LLM, Hunyuan or search call, and 0 network connections, which I checked.
  - The only download is the U²-Netp file, fetched first if it's missing.
- **Run on :8014's data:**
  - **4 benchmark parts + microwave, window frame, range hood, faucet, bench and planter:** 25 models in 5.3 s (6.3 s wall), $0. Log: `retexture_10_parts.txt`.
  - **All 162 parts:** 230 models re-textured in 48 s, $0.
    - 6 were kept: `auto` templates whose plan isn't in the LLM cache.
    - 0 CAD models were turned.
- **Optional, paid:** `python -m server.warm --scad --remake-flat [--scad-parts ids] [--estimate]` remakes the flat-front appliance CAD models with the new prompt.
  - Scope: 3 of the demo parts, or 25 across all parts.
  - Cost: about $0.03–0.05 and 45–100 s each.
  - The old model is kept as `scad.flat.glb`.

## Backend probe (:8014, OFFLINE, CAD parts: dishwasher, LG fridge, GE microwave): all pass

- **`GET /parts/{id}/part.json?mode=llm_scad`:** tier `scad`, ready, `asset.cad.status` ready, and `asset.texture` filled in.
- **`POST /parts/{id}/model?mode=llm_scad`:** ready, with `model_url …/model.glb?mode=llm_scad`.
- **`GET /parts/{id}/model.glb?mode=llm_scad`:** 200, `X-Asset-Tier: scad`, `X-Asset-Cad: ready`. The photo triangles all face +Z.
- **`GET model.glb`** (no mode) returns the same bytes once llm_scad was selected.
- **`/catalog?site=kitchen`:** 48 items, 45 of them `model_ready`.
- **Replace job:** "replace the dishwasher" with context `asset_mode: llm_scad` runs all 7 steps.
  - The model step says "Grok's OpenSCAD model".
  - The job picks the Whirlpool WDP540HAMW, and `place_part` has `model_url …?mode=llm_scad` (200, tier scad).
- **What the app could trip on:**
  - Nothing new.
  - `part.json` without `?mode` is whichever mode was asked last; the app already appends `?mode=` through `AssetModes`.

## Renders (`renders/`, CPU rasteriser `render.py`, since pyrender fails here)

- **Benchmark parts, textured, before (:8004's data) and after, front and 3/4, for CAD / HF / auto:**
  - `easy_dishwasher.png`
  - `medium_range.png`
  - `hard_fridge.png`: the neighbours and floor are gone and the keystone is straightened.
  - `veryhard_knob.png`
- **More parts:**
  - `six_more_cad.png` and `six_more_auto.png`: microwave, window, range hood, faucet, bench, planter.
  - `edge_cases_auto.png`: the Insignia microwave (the frame), a wall lantern (the lifestyle scene is gone), and the LG "neu" fridge (a U²-Netp miss, see below).
  - `cutouts_12_parts.png`: photo, cut-out on magenta, texture, and body/trim swatches.
- **Orientation:**
  - `orientation_backwards_synthetic.png`
  - `orientation_flat_front_relief.png`

## :8004 steps for the orchestrator (about 2 minutes, $0)

```sh
cd ~/airtools-backend-demo
cp -R data/parts data/parts.bak-texture             # 40 MB; the rollback
git merge --ff-only texture                          # app-handoff-vrnext 8420b3c -> 39e4825
uv sync                                              # + onnxruntime
mkdir -p data/cache/models && cp /private/tmp/claude-501/-Users-ravil-AirTools/texture-data/cache/models/u2netp.onnx data/cache/models/   # or let --retexture fetch it
# stop :8004, then:
set -a; . .env; set +a; uv run python -m server.warm --retexture     # ~50 s, prints "$0"
SCENE_DIR=/Users/ravil/airtools-drone-backend/scene LLM_AGENT=xai:grok-4.20-0309-non-reasoning \
  .venv/bin/uvicorn server.app:app --host 0.0.0.0 --port 8004
```

- **Rollback:** `git reset --hard 8420b3c`, then restore `data/parts.bak-texture`.
- **Flags:** `ASSET_PHOTO_CUT=false`, `ASSET_SIDE_COLOR=false` and `ASSET_SCAD_ORIENT=false` switch the fixes off for new builds. Then run `--retexture` again.

## Editor gate (the orchestrator's Unity captures)

- **What to load:** the 4 benchmark parts' `model.scad.glb` and `model.hf.glb` after the :8004 steps (or from :8014).
- **Same setup as `textured_compare.png`:** front and 3/4 views in the kitchen, with the Main scene lights.
- **What to look at:**
  - The fridge front has no neighbours or floor.
  - The HF sides are stainless, not grey or smeared photo.
  - The knob's rim is bronze.
  - Whether the sides' metallic 0.35 reads as satin steel or as too dark under URP without a reflection probe. If it's too dark, lower `photo_cut.FINISH_PBR["metal"]` and run `--retexture`.

## What only the headset can verify, and what is still open

- **Headset only:** the metal cue in passthrough, and texture sharpness at 1 m.
- **U²-Netp misses:** it misses on 1–2 busy photos.
  - The LG "neu" InstaView fridge on purple wallpaper: the dark glass panel and one door are lost, and it is no better than before.
  - Photos where the listing's dimensions are garbage, like the Insignia top-freezer at w = 1692 mm, fall back to the flood.
- **3/4 product photos:** they are still mapped straight onto the front. The GE range's cooktop band and the knob's side view on its dome are examples. Only a slight keystone is corrected.
- **HF yaw:** Hunyuan meshes aren't checked for yaw. `normalize_mesh` picks among 4 turns by aspect, so a near-square W ≈ D part could, in principle, face a side.
- **Colour sampling:** it picks the photo's dominant material. For a built-in dishwasher that is steel (`#A3A3A3`), so the Grok-painted CAD sides keep Grok's `#8E9498`, as the brief says: Grok's paint wins where it's meaningful.
