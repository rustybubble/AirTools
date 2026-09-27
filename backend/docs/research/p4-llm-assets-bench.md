# LLM-made part assets: bench

Can a general LLM, given the right tools, build a part's `model.glb` itself instead of the
Hunyuan3D-2.1 Space? Contract (`docs/api.md` §2): metres, +Y up, origin at the mounting-face
centre, part toward +Z, exactly the published W×H×D.

Research behind this: `docs/research/assets-llm/r1-academic.md`, `r2-practitioners.md`,
`r3-toolchain.md`.

## 1. Setup

- **Models.** Groq `qwen/qwen3.8-27b` (vision, ≤3 images/request, 131K context) for everything.
  `grok-4.7` only for a task Groq demonstrably fails, with the reason logged (credit is saved for
  demos).
- **Rate limit.** Groq free tier allows about 1000 output tokens/min on this model (R3). All
  experiments share one key through `server/experiments/assets_llm/llm.py`, which serialises
  calls across processes and logs each one to `work/assets-llm/calls.jsonl` (tag, tokens,
  latency).
- **Test set.** 12 parts in `work/assets-llm/testset.json` (R3): brackets and hangers, slab,
  condenser, water heater, sink, faucet, solar panel, window AC, fridge, fan. Eight have a
  Hunyuan baseline. The joist hanger's `part.json` dims look swapped (h 51 mm, real ≈121 mm).
- **Tools.** Renderer `server/experiments/assets_llm/render.py` (pyrender EGL, 0.15 s/sheet),
  scorer `score.py`, CadQuery → contract GLB `cq_to_glb.py`, OpenSCAD nightly AppImage, trimesh +
  manifold3d. Commands in `r3-toolchain.md`.

## 2. Metrics

Per part and method, appended to `work/assets-llm/results.jsonl`:

| Metric | Meaning |
|---|---|
| valid | produced a loadable GLB that passes the contract checks |
| bbox_err_pct | worst axis error of the bbox vs published dims |
| pieces / floating | connected components; any not touching the main body |
| iou | silhouette IoU, render vs product photo (rewards boxes on its own) |
| clip | CLIP ViT-B/32 image similarity, render vs photo |
| calls, in/out tokens, wall s | cost per asset, from the ledger |
| judge | visual review of the contact sheet (photo, front, side, 3/4) |

Report IoU and CLIP together: a plain box scores high IoU on box-like parts.

## 3. Baselines (R3)

| Method | Parts | Valid | Mean IoU | Mean CLIP | Cost |
|---|---|---|---|---|---|
| Hunyuan3D-2.1 Space (today's `ai_mesh`) | 8 | 8/8 | 0.66 | 0.59 | ~15–20 s, ~12/day quota |
| Exact-size proxy box | 4 | 4/4 | 0.77 | 0.44 | 0 |
| One-shot CadQuery by Qwen (R3 smoke) | 3 | 3/3 compile, 1/3 right frame | – | – | ~4–6 s, 1.3–2.5K out tokens |

## 4. Experiments

| Row | Method | Owner | Status |
|---|---|---|---|
| E1 | Parametric templates in trimesh; Qwen picks the template and fills parameters and colours from photo + dims (one vision call); E1b adds one render-critique pass | E1 | done, `assets-llm/e1-templates.md` |
| E2 | Qwen writes code against a small frame-safe helper library, OpenSCAD vs CadQuery A/B; compile-error retry ×2, bbox feedback, one render-critique pass | E2 | done, `assets-llm/e2-code.md` |
| E3 | Photo-projected texture and correct colours: product photo on the front face of the proxy box and of E1/E2 geometry; fix the sRGB→linear colour bug | E3 | done, `assets-llm/e3-decal.md` |
| E4 | `grok-4.7` on the two gutter hangers every Groq approach lost to Hunyuan on: `bent_strip` params, E2 OpenSCAD loop, E5 photo on the best | E4 | done, `assets-llm/e4-grok.md` |
| E5 | E1 template geometry + E3 photo projected on the face the view call chose (ray-tested, template colours elsewhere); adds a `bent_strip` template, PBR finishes, a critique colour gate and a photo mode that drops front relief | E5 | done, `assets-llm/e5-combo.md` |

### E3 results (photo decal, `assets-llm/e3-decal.md`)

- **Colour fix.** `_paint` now converts sRGB to linear (commit `3e31a93`).
- **Decal proxy, 12 parts.** 12/12 valid, 5–110 KB, under 1 s, no LLM. Mean CLIP 0.67,
  against 0.42 for the grey proxy. With one Groq face call (about 1.5K in / 33 out tokens) it
  reaches 0.72. On the 8 Hunyuan parts: decal 0.66, decal + view 0.72, Hunyuan 0.60. The box
  IoU (0.64) is below Hunyuan (0.66) on those parts.
- **View call accuracy.** The face was right on 11/12 parts; the v2 prompt also got the
  rotation right on 12/12. The one miss was the sink, called "front" when the photo is a
  top-down view.
- **Hunyuan mesh + photo** (task 4). Water heater CLIP 0.89, the best of the run.
- **Verdict.** The decal should replace the grey proxy tier now. Photo-textured geometry is
  better still for non-box shapes, once the geometry is oriented correctly.

### E1 results (parametric templates, `assets-llm/e1-templates.md`)

- **Validity.** 10 trimesh templates. 12/12 valid, bbox error 0.00 %, no floating pieces,
  mean 1.8K triangles / 34 KB (Hunyuan: 20K / 352 KB).
- **Scores.** e1 mean IoU 0.65, CLIP 0.61 on all 12 (proxy 0.68 / 0.42). On the 8 Hunyuan
  parts: e1 0.59 / 0.60, Hunyuan 0.66 / 0.60.
- **Contact sheets.** Better than Hunyuan on 5/8: wall AC, fridge, mini-split, sink, joist
  hanger. Same on the water heater. Worse on the 2 gutter hangers (curved strips).
- **Cost.** One Qwen call per asset: ~3.6K in / 205 out, about $0.004, 1–2 s when Groq is idle.
  No compile failures, no JSON retries.
- **Critique pass (E1b).** +1 call (~6.5K tokens total), IoU +0.01, CLIP −0.03. Useful only
  behind a keep gate: it finds missing features but mis-sets colours and once switched a
  template wrongly.
- **Text-only fallback.** e1-text (gpt-oss-120b on a hand-written description) matches e1 on
  IoU, CLIP −0.03. e1b-num (numeric feedback only) gave no gain.

### E2 results (helper-library code, OpenSCAD vs CadQuery, `assets-llm/e2-code.md`)

- **Frame.** The per-face helper library, the envelope clip and a ≤5 % rescale fixed R3's frame
  errors. 15/15 Qwen runs came out valid and the right way up (OpenSCAD 9 parts, CadQuery 6;
  2 needed a ≤1 % rescale). There were 0 compile errors in 39 Qwen builds, in both languages.
- **OpenSCAD vs CadQuery** (6 shared parts). IoU is the same (0.725 / 0.724). CLIP is 0.60 vs
  0.54, and OpenSCAD had 1 piece on 5/6 parts, while CadQuery had 2–27 pieces on 4/6 (mixed-up
  face coordinates). Prefer OpenSCAD.
- **Against the baselines.** e2-scad scores IoU 0.742 / CLIP 0.591 on 9 parts, against
  Hunyuan or proxy at 0.739 / 0.479 on the same parts, with higher CLIP on 7/9. The wall AC and
  the shower head are clear wins. The fridge loses to Hunyuan, and the sink is a solid block in
  every variant (the bowl cut stops 5 mm short of the top).
- **Critique.** Photo + render → revised code. ΔCLIP +0.02 in OpenSCAD, −0.01 in CadQuery.
  Mostly it acts as code review (paint order, holes outside `difference()`), with a few
  hallucinated complaints. Keep it behind a keep-better gate.
- **Cost.** About 2.7 Qwen calls, 7.5K in / 2.5K out per asset, about 10 s of model time. Qwen's
  free cap of 200K tokens/day allows about 20 assets per key per day.
- **Text fallback.** gpt-oss-120b on a written description (5 rows): comparable IoU, CLIP
  0.37–0.43. It needed one extra OpenSCAD syntax rule, and CadQuery failed 1/2.
- **E4 candidates for Grok.** The sink bowl (after trying a `pocket`-through-face helper) and
  the condenser's louvred sides. The mini-split and the joist hanger are data or dims problems.

### E5 results (template + projected photo, `assets-llm/e5-combo.md`)

- **Validity.** 36 rows: all valid, bbox error 0.00 %, 0 floating pieces. The final variant is
  23–194 KB (mean 82 KB) and takes about 1 s of CPU.
- **Scores (IoU / CLIP).**

  | subset | E5 | E3 box | E1 | Hunyuan |
  |---|---|---|---|---|
  | all 12 | 0.68 / 0.72 | 0.68 / 0.72 | 0.66 / 0.61 | – |
  | 8 Hunyuan parts | 0.63 / 0.72 | 0.64 / 0.72 | 0.60 / 0.60 | 0.66 / 0.60 |

  The water heater reaches CLIP 0.91, the best of the whole bench.
- **Contact sheets.** Better than Hunyuan on 6/8; worse only on the 2 gutter hangers. Better
  than E1 on 8/12 and worse on 2 (the kit photo and the tilted 4-pack photo). Better than the
  E3 box on 7/12, the same on 5, and never worse. The metrics don't show this, because CLIP's
  best view of a decal box is the photo itself.
- **Cost.** 2 Qwen calls (E1 generation + view), about 5.1K in / 240 out, about $0.005.
  Folding the view question into the generation call makes it 1.
- **E1 fixes.**
  - `bent_strip`: alu hanger IoU 0.37 → 0.51, vinyl hanger 0.31 → 0.34. Still below Hunyuan
    on moulded parts.
  - PBR finishes: CLIP ±0.03, metals render sensibly with metallic ≤0.8.
  - Colour gate: blocks 5/6 critique recolours, neutral on CLIP.
  - Photo mode: wall AC 0.70 → 0.82, shower head 0.70 → 0.76 (and 299 → 102 KB).
- **Verdict.** `ai_mesh` should become E5 (one call, photo mode, no critique pass). Keep
  Hunyuan as the fallback for moulded or organic small parts.

### E4 results (Grok on the gutter hangers, `assets-llm/e4-grok.md`)

- **Scope.** 11 of the 12 allowed Grok calls, counting 2 assumed SDK timeout retries (fixed in
  `6db28a5`). All rows are valid, with bbox error 0.00 %.
- **Scores (IoU / CLIP).**

  | part | Hunyuan | Qwen E5 best | Grok strip | Grok scad (kept) | Grok scad + photo |
  |---|---|---|---|---|---|
  | vinyl hanger | 0.46 / 0.83 | 0.34 / 0.74 | 0.62 / 0.73 | 0.64 / 0.78 | **0.64 / 0.87** |
  | alu hanger | **0.62 / 0.77** | 0.51 / 0.72 | 0.53 / 0.69 | 0.62 / 0.71 | 0.62 / 0.77 |

- **Vinyl.** Grok's OpenSCAD model reads as the product: back plate with a hole, tapered web,
  split front clip. It beats Hunyuan visually, and on IoU and CLIP once the photo is on. Grok's
  `bent_strip` alone fixes Qwen's reversed slope.
- **Alu.** Tied on the metrics. Hunyuan still wins visually, because Grok's strap has no Z-step.
- **Template choice.** Given the whole catalogue, Grok picked the generic `strap_hanger` both
  times and did worse than Qwen.
- **Cost.** About 13–33K reasoning tokens per call (156K over 7 calls), and 83–523 s per call
  (median 284 s). A strip asset is 1 call; the OpenSCAD loop is 2–3 calls and 5–17 min.
- **Verdict.** Worth it only for moulded clips and hangers, as an offline precompute-once job
  per SKU. It is far too slow for a live request. Keep Hunyuan or E5 on the request path.

## 5. Findings and recommendation

**Findings.**

- A general LLM with a template catalogue does better than an image-to-3D model on most of our
  parts. E5 (template + projected photo) beats Hunyuan visually on 6/8. It is exact-size by
  construction, one Qwen call (~5K tokens, ~$0.005), about 1 s of CPU, and 23–194 KB. The
  Hunyuan quota is about 12 meshes a day; Qwen's free cap allows about 40 E5 assets per key per
  day.
- The photo matters more than the geometry. The decal box alone lifts CLIP from 0.42 to 0.67–0.72,
  and every photo-textured variant beats its untextured one.
- Free-form code (E2 OpenSCAD) is reliable with the frame-safe helper library, but it costs about
  3× the tokens of a template and gains little over E5 on box-like parts. It only pays off on
  shapes no template covers.
- Moulded clips and hangers are the one class Qwen can't model. Grok 4.7 can (E4 vinyl hanger
  0.64 / 0.87 against Hunyuan's 0.46 / 0.83), but at about 5 min per call it only works offline.
- Critique passes are roughly neutral and sometimes harmful: they recolour or switch templates.
  Use them only behind a keep-better gate, and not on the request path.

**Recommendation for `server/assets.py`.**

1. CAD URL (unchanged).
2. **`llm` tier (new default for `ai_mesh`)**: one vision call (`LLM_ASSET`, default
   `groq:qwen/qwen3.8-27b`, switchable to `xai:`) returns template, params, colours, finishes and
   photo face. Build the template and project the photo in photo mode when the photo shows one
   product. Falls to the text model on a written description when there is no photo.
3. **Cached Grok OpenSCAD** for parts the call tags as moulded or organic: an offline job via
   `server.warm`, never on the request path.
4. **Hunyuan + photo**, when there's quota, for moulded parts without a cached Grok asset (needs
   the `normalize_mesh` orientation fix first).
5. **Decal box** replaces the grey proxy as the instant and last-resort tier.

**Status (2026-09-26).** Wired into `server/assets.py` as above (`server/meshgen.py`, tiers
`cad` → `llm` → `scad` / `ai_mesh` → `proxy` decal box; `asset.tier` records which one).
Production smoke on 3 test-set parts, one Groq call each (4.2–4.3K in / 268–425 out):
wall AC `llm` IoU 0.81 / CLIP 0.80 (96 KB), water heater `llm` 0.84 / 0.91 (39 KB), vinyl hanger
tagged moulded → `ai_mesh` + photo 0.46 / 0.83 (433 KB; its `llm` template alone 0.61 / 0.70).
The orientation fix came out different from the plan: Hunyuan3D-2.1 does not return the mesh in
the photo's camera frame but in a canonical pose that already matches the contract (raw wall AC
from a front photo: grille on +Z; raw vinyl hanger from a 3/4 photo: mount side −Z, profile on
±X). `normalize_mesh(upright=True)` keeps its +Y and picks only among the 4 turns about it (ties
keep the generated pose); the wall AC mesh + photo now scores 0.81 / 0.76 (was 0.80 / 0.42,
grille up).
