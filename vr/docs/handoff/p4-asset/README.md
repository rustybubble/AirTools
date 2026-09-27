# Backend hand-off 5: generate a window frame that fits a taped opening, and make it to size (assetgen)

*Sat 2026-09-26, app assetgen lane → P4. Five patches on top of `e2e-measure-replace` @ `4398ffb` (it carries the
autonomy lane's Groq → Grok fallback, `1f29cfe`). Made and tested in a scratch worktree (`backend-asset`, branch
`asset-window`). Nothing changed in your repo, on `:8000` or on the demo server `:8004`, and nothing was pushed.
`server/llm.py` is untouched (the autonomy lane owns role routing).*

The demo flow: tape a window's height and width with the Measure tool → "find a window frame that fits" → the server's
asset pipeline builds each candidate's model → the frame snaps into the opening when it fits → Adjust ▸ Size & finish
changes its width / height / depth ("Fit to opening") and finish, and the server rebuilds the model at that size.

## How to apply

```sh
cd airtools-drone-backend
git switch e2e-measure-replace            # 4398ffb or later; also applies on its tip cae56b6
git am /path/to/AirTools/docs/handoff/p4-asset/*.patch
uv run pytest -q tests/test_asset_window.py tests/test_resize.py tests/test_opening.py
```

- `git am` on `4398ffb` gives a tree byte-identical to the tested one (`5562f62`). It also applies on the branch's
  current tip `cae56b6` (full suite there: 1440 passed, the same 3 macOS `sched_getaffinity` failures as before).
  It does **not** apply on `ca14bb4` / `1f29cfe` alone (0001 touches lines `4398ffb` changed).
- No new dependencies. Two new env vars, both optional: `LLM_ASSET_RETRY` and `LLM_EXTRACT_RETRY` (default
  `xai:grok-4.20-0309-non-reasoning`, `""` = off). Restart uvicorn afterwards.
- Tests: +43 (`test_asset_window.py` 24, `test_resize.py` 6, `test_opening.py` 13). Full suite after each patch:
  1387 / 1393 / 1405 / 1405 / 1406 passed (base 1363), the same 3 pre-existing failures. `ruff check` clean;
  `ruff format` flags only `search.py`'s pre-existing hunk. No test calls an LLM or the network (conftest blanks both
  retry settings like `LLM_FALLBACK`; tests opt in).

## Patches

| Patch | What | Tests |
|---|---|---|
| `0001-feat-assets-a-window-template-…` | **`window` template** (`part_templates`): outer frame filling W × H × D; double / single hung, slider, casement, fixed; sashes set back from the frame's front, glass at mid depth, grille bars, a sash lock; glass gets the `glass` finish by default (`DEFAULT_FINISH`); `flat_panel`'s description points windows at it; photo mode drops the grille and lock. **A Grok retry for a poor answer** (`meshgen.ask`): none at all, or not the template the product's name needs (`expected_template`: a "… Vinyl Window" → `window`, a window AC → `box_appliance`, a water heater → `cylinder_tank`, …; window screens / locks / cranks / film excluded) → one more call on role `asset_retry`; Grok's answer wins when it's better. **Who made it**: `cached_chat_by` records the provider:model that answered (a Groq call `llm.chat` re-sent to xAI says `xai:…`) next to the cached answer; `part.json` `asset` gains `made_by`, `template`, `retried`, `seconds` (additive) | `test_asset_window.py` |
| `0002-feat-parts-made-to-size-…` | **`POST /parts/{id}/resize` {w, h, d, finish?}** (new `server/resize.py`): the model rebuilt the way its tier made `model.glb` — `llm`: the cached plan (a cache hit, no LLM call) built at the new size, photo on the same face; `proxy`: the decal box; `cad`/`scad`/`ai_mesh`: stretched per axis (`scaled`). With a finish: F11 first (Imagine, cached), its photo and colours on the rebuilt model (`finish.build` gains `dims`); a tint-only finish → `finish_source: "tint"`. Built once per size (`model-size-<w>x<h>x<d>[-<finish>].glb`, the existing `model-{slug}.glb` route). 409 before the model is ready, 422 outside 0.5–2× the listing / under 10 mm. `finish.product_roles` keeps glass (`KEEP_ROLES`): a window's panes are its largest role, so a bronze window had bronze glass | `test_resize.py` |
| `0003-feat-search-fit-to-a-taped-opening-…` | **`SearchRequest.opening`** `{w_m, h_m, d_m?, label, kind}` and context `opening` (`Session.opening`, `find_part` passes it). The shops are searched with its size ("window frame 59 x 47 in", `discovery_query`), the planner is told the product goes into it, the cache key includes it (to the cm), every candidate is fitted to it (`opening_fit`: > 3 mm over `too_big`, > 51 mm under `too_small`), fitting ones first. **Window dims**: `sellers._parse_dims` skips "Rough Opening Width/Height" (the hole, not the unit: the 70 Pro Series came out as its 28 × 46 in. rough opening) and the "Width x Height" string; a window / door with only W × H gets its jamb depth, else 3¼ in. (`_window_dims`, `dims_source: "home_depot_typical_depth"`), and so does a window sold by its size in the title (`"title_typical_depth"`). Before, every Jeld-Wen V-2500 slider was dropped for "no dims" | `test_opening.py` |
| `0004-docs-api-…` | `docs/api.md`: the resize endpoint, `opening`, the asset fields, the window depths; README: `LLM_ASSET_RETRY` | — |
| `0005-feat-search-a-Grok-retry-when-the-cheap-model-…` | **"Extraction gave no dims"**: `_extract_dims` (role `cheap`, Groq gpt-oss-20b) that reads a page without finding a complete size (or fails) gets one more try on role `extract_retry` (`LLM_EXTRACT_RETRY`, Grok) | `test_opening.py` +1 |

## The pipeline, verified (and what it costs)

The asset for a search candidate, as it runs today (nothing new on this path but the window template, the retry and
the provenance):

```
POST /parts/search {query, measurement, opening}            search.find_parts (discover → extract (LLM) → dims → sellers → fit)
  → job done: candidates (asset.status "pending")            jobs._run_search saves part.json
  → jobs._resolve_assets (one worker per job, in order)      assets.resolve_shared → resolve_asset
      fetch image.jpg                                        assets.fetch_image
      meshgen.ask(part, photo)                               ONE vision call, role `asset` (Groq qwen/qwen3.8-27b;
                                                             llm.chat → xAI grok-4.20-non-reasoning when Groq can't answer)
        poor? (none / not the expected template)             ONE more call, role `asset_retry` (Grok)        [0001]
      meshgen.build(plan, dims)                              part_templates.build: the template at exactly W × H × D
      meshgen.project(geometry, photo, face)                 the product photo on the face it shows
      → model.glb (~55 KB) + part.json asset {tier llm, template, made_by, retried, seconds}
GET /parts/{id}/part.json until asset.status == "ready", then GET /parts/{id}/model.glb
POST /parts/{id}/resize {w, h, d, finish?} → model-size-….glb                                            [0002]
```

Measured on the test server `:8007` (this worktree; `SCENE_DIR` the drone backend's scenes; a scratch `DATA_DIR` seeded
from the demo's cache), 2026-09-26 20:30–21:20:

| Step | Result |
|---|---|
| Search "window frame", opening 1500 × 1200 mm (the facade window) | 9 s live, 0 s cached: 3 Jeld-Wen V-2500 59.5 × 47.5 in. sliders, 1511 × 1206 × 83 mm (`home_depot_typical_depth`), each "too wide for the opening by 11 mm" (their rough opening is 60 × 48 in.; ours is 59 × 47¼). Before 0003 the same search returned a 5/16 × 84 in. screen frame and a 28 × 46 in. window (its rough opening, not its size) |
| `/agent/command` "find a window frame that fits" with context `opening` | the agent (Groq gpt-oss-120b, 0.6 s) → `find_part("window frame")` → the same fitted candidates |
| Each window's model | template `window` / `slider`, 2 panes, 2 × 3 grille; `made_by xai:grok-4.20-0309-non-reasoning`; 2.1–2.6 s for the call, 2.4–3.0 s to the GLB; $0.0048 per call |
| Groq vs Grok, fresh (no cache), same part | Groq's keys were exhausted all evening (`KeysExhausted` on qwen/qwen3.8-27b and gpt-oss-120b): default role → llm.chat fell back to Grok, 2.35–2.43 s; `LLM_ASSET=xai:…` 2.17–2.22 s (the ~0.2 s is the Groq key pool refusing). With `LLM_FALLBACK=""` Groq failed outright and 0001's retry made it (`retried: true`), 2.78 s. Text path (no photo): 1.3–1.6 s. Your bench's Groq figure: 1–2 s when Groq is idle (`docs/research/p4-llm-assets-bench.md`) |
| `/resize` to 1487.3 × 1187.3 × 82.55 mm ("Fit to opening": the opening less ¼ in. each side) | `template`, 0.10 s; repeat 0.00 s (`cached`) |
| `/resize` + finish "Bronze" | Imagine edit $0.07, 7.7 s; `template` + `finish_source: "imagine"`, 8.1 s in all |

## What the headset sends and reads (the app side is on `feat/asset-gen-window`)

- Context and search body: `opening: {w_m, h_m, label: "taped opening"}` — the opening the newest tapes frame (a
  width tape across the jambs and a height tape sill to head, or a rectangle shape). Older servers ignore it.
- `part.json` `asset.made_by` / `template` / `retried` / `seconds`: logged by the headset when a candidate's model comes
  in ("Model ready · …: tier llm (window) by xai:grok-4.20-0309-non-reasoning, server 2.4 s, here 3.1 s"); the card
  says "Building the 3D model…" until then.
- `POST /parts/{id}/resize`: the Size & finish panel's W / H / D steps, "Fit to opening" and finish chips (for `llm`
  and `proxy` parts; `ai_mesh` only with a finish). The headset shows the new size at once (the model stretched,
  "resized") and swaps in `model_url` when it arrives ("made to size").

## Asks / left for P4

1. Groq's free-tier keys ran out for both `qwen/qwen3.8-27b` and `gpt-oss-120b` on 2026-09-26: every asset tonight came
   from Grok through `LLM_FALLBACK`. Worth a paid Groq key or `LLM_ASSET=xai:grok-4.20-0309-non-reasoning` for the demo
   (2.2 s, ~$0.005 per asset).
2. The typical depth (3¼ in.) is an assumption, labelled in `dims_source`. If you'd rather drop windows without a
   published depth, revert `_window_dims` / `_window_dims_from_title` — but then no Jeld-Wen slider survives.
3. `/resize` for `scad` parts stretches the GLB; the cached OpenSCAD code could be re-run at the new size instead
   (`build_scad` takes W, H, D) if the code were kept next to `scad.glb`.
4. The planner (`extract`) still says a 1511 mm window is "just under" a 1500 mm opening; the server's fit corrects it,
   but a stronger hint (or listing the rough-opening size as `fit_range_mm`) would pick right-sized stock.
