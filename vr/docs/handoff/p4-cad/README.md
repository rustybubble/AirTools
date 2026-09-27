# Backend hand-off: LLM+CAD (Grok writes OpenSCAD) made honest, fast and pre-generated (cad)

*Sun 2026-09-27, cad lane. Two patches on top of `catalog-on-asset-mode` @ `42c0899` (what :8004 runs), made in the
scratch worktree `backend-cad` (branch `cad`) and tested on `:8011` with a copy of the demo data
(`/private/tmp/claude-501/-Users-ravil-AirTools/cad-data`). Nothing changed on `:8004` or in
`~/airtools-backend-demo/data`, nothing pushed.*

The user's words: "Install OpenSCAD and add it to the pipeline so we can directly compare the dynamic asset
generation." OpenSCAD is installed (`/opt/homebrew/bin/openscad`, the 2026.09.23 snapshot, manifold backend). The app's
side is on `feat/cad-compare` (the CAD upgrade while placed, the badges).

## How to apply to :8004

```sh
cd ~/airtools-backend-demo                      # on app-handoff-vrnext @ 42c0899 (= catalog-on-asset-mode)
git merge --ff-only cad                         # or: git am /path/to/AirTools/docs/handoff/p4-cad/*.patch
# no new dependency; the defaults are the recommendation (nothing to add to .env):
#   LLM_ASSET_SCAD unset -> xai:grok-4.7, ASSET_SCAD_EFFORT unset -> "low", ASSET_SCAD_PHOTO unset -> true
# restart uvicorn on :8004 the way it runs now (cwd ~/airtools-backend-demo, .env, SCENE_DIR, LLM_AGENT)
set -a; . ./.env; set +a
SCENE_DIR=~/airtools-drone-backend/scene .venv/bin/python -m server.warm --scad --estimate   # the plan, no calls
SCENE_DIR=~/airtools-drone-backend/scene .venv/bin/python -m server.warm --scad --scad-jobs 3
```

- The warm can run while :8004 serves (restart first, so the server knows the lock files and picks each CAD model up
  as it lands). Resumable: run it again after an interruption; `--retry-failed` retries the recorded failures.
- Tests: `uv run pytest -q tests/test_cad.py tests/test_asset_mode.py tests/test_meshgen.py tests/test_warm.py`
  (73 pass; `tests/test_cad.py` is 16 of them; OpenSCAD and the LLM stubbed). Full suite: 1545 passed, 1 skipped, 3 failed: the 3 are
  `tests/pipeline/test_structure_*` and fail the same way on untouched `42c0899`. Opt-in live test (real Grok + OpenSCAD,
  ~95 s, ~$0.03): `set -a; . .env; set +a; uv run pytest -q -m live tests/test_cad.py -k live_grok` (passed).

## Patches

| Patch | What |
|---|---|
| `0001-feat-cad-LLM-CAD-made-honest-and-fast-…` | **(a) stale stand-ins**: an `llm_scad` template stored while OpenSCAD was missing (the dishwasher on :8004 today), offline, lost to a restart, or before its CAD model landed (made by `warm --scad` in another process) is remade on the next request (`assets.scad_stale`; `part.json?mode=` / `POST /model` wait up to 15 s for the rebuild). A failure counts for its model for an hour. **(b) `asset.cad`** on `llm_scad` answers: `{status writing\|ready\|failed\|none, started_at, eta_s, made_by, seconds, cost_usd, reason}` (also top-level `cad` on `POST /model`, header `X-Asset-Cad`), cross-process through `scad.writing.json`; at most 2 live CAD runs at once. **Records**: `scad.json` / `scad.failed.json` (model, effort, calls, fixes, seconds, cost, tokens, raw bbox error, the error). **Loop**: OpenSCAD errors now carry the model's TRACE lines (the library's line numbers used to be shown as the model's, pointing Grok at the wrong line); `ASSET_SCAD_EFFORT` (default `low`), `ASSET_SCAD_FIXES`, `ASSET_SCAD_SAMPLES`. **(d) `python -m server.warm --scad`**. `server/experiments/scad_bench.py`, `cad_compare.py`. `docs/api.md` "CAD models" |
| `0002-feat-cad-ASSET_SCAD_PHOTO-…` | `ASSET_SCAD_PHOTO=false` serves Grok's geometry and paint without the product photo on the front (the generator as written; see the window below). Default `true` (as before) |
| `0003-fix-cad-the-grok-4.7-low-effort-ETA-…` | The ETA/estimate table from the kitchen warm (grok-4.7 low: 55 s, $0.032 a part) |

HF (Hunyuan) is unchanged: the photo-hash cache, the warm queue.

## Speed and cost: which model writes the OpenSCAD (`speed-cost.md` has every run)

Same three parts, same prompt and loop, every call live (fresh cache), `grok_scad_run` on this Mac:

| Writer (`LLM_ASSET_SCAD` · `ASSET_SCAD_EFFORT`) | Compiled | LLM calls | Seconds a part | $ a part | Raw bbox error | Look (renders) |
|---|---|---|---|---|---|---|
| `xai:grok-4.7` · model default | 3/3 first time | 1 | 216 · 400 · 229 | 0.099 · 0.204 · 0.107 | 0 % | best: the window with bars and glass, knob right way round |
| `xai:grok-4.7` · `medium` | 3/3 first time | 1 | 202 · 332 · 178 | 0.094 · 0.165 · 0.084 | 0 % | like the default |
| **`xai:grok-4.7` · `low`** (2 rounds + the 51-part kitchen warm) | 57/57 (2 needed a fix) | 1-2 | 34 · 80 · 64, 66 · 127 · 91; warm median 45 (15-157) | 0.016-0.073, ~$0.03 | 0 % | close to the default; one of two windows came back a plain slab |
| `xai:grok-4.20-0309-reasoning` | 3/3 | 1 | 118 · 127 · 180 | 0.029 · 0.031 · 0.047 | 0-1 % | good dishwasher and knob, window a perforated slab |
| `xai:grok-4.3` · `low` | 3/3 | 1-2 | 14 · 17 · 27 | 0.004 · 0.005 · 0.010 | 0 % | flat: dishwasher door black, window blank |
| `xai:grok-4.20-0309-non-reasoning` (1 answer, 2 fixes) | 3/3 | 2-3 | 12 · 19 · 12 | 0.005 · 0.009 · 0.005 | 2-49 % | flattest: knob mounted backwards, window a slab with faint bars |
| same, before the TRACE fix (1 answer, 1 fix) | **1/3** | 2 | 10-12 | 0.006-0.008 | — | the window and the knob never compiled |
| same, 3 answers at once, 2 fixes | 3/3 | 3-4 | 9 · 15 · 12 | 0.007-0.013 | 0-50 % | the knob became a cube once (a bare envelope box "fits" perfectly); not better than 1 answer |

Raw bbox error = how far Grok's model missed the W×H×D envelope before the harness snaps it (every served CAD model
is exactly the listed size). Seconds are wall clock for the whole loop (the OpenSCAD renders take 0.04 s each).

**Recommendation: `xai:grok-4.7` at `low` effort, the new default** (no env needed; `LLM_ASSET_SCAD` /
`ASSET_SCAD_EFFORT` tune it). It keeps grok-4.7's geometry at ~5× the speed and a fifth of the cost: median 45 s and
~$0.03 a part, so a live request shows "Grok is writing the CAD model… 1 min" and upgrades in about a minute. For the
best look on the handful of demo hero parts, pre-generate them at the model's default effort
(delete their `scad.glb`, then `ASSET_SCAD_EFFORT= python -m server.warm --scad --scad-parts <ids> --no-demo-parts`:
~15-20 min for 8 parts 3 at a time, ~$1.10). grok-4.20 non-reasoning is 10-20 s but its shapes are too flat to be worth comparing against Hunyuan.

## Warm run on :8011's data (kitchen catalog + demo parts: the dishwashers, window frames, gutter hangers)

`python -m server.warm --scad --sites kitchen --scad-jobs 3` (grok-4.7 low) on the copy of :8004's data: 52 parts =
the kitchen catalog's items with a model (45) + `SCAD_DEMO_PARTS` (3 more dishwashers, 3 window frames, 2 gutter
hangers); the Frigidaire dishwasher already had its CAD model from the live end-to-end test.

| Parts | Made | Failed | Wall time | Per part (run) | LLM cost | Fix rounds needed |
|---|---|---|---|---|---|---|
| 51 to make, 3 at a time | **51** (every llm_scad variant rebuilt to tier `scad`) | 0 | **16.1 min** (19 s a part at 3 at once) | median 45 s, mean 54 s, 15-157 s | **$1.62** (median $0.030, max $0.073) | 2 of 51 |

Rows: `<DATA_DIR>/scad_warm.json`; each part's `scad.json`. **Estimate for :8004's whole demo set** (every served
site's catalog items with a model + the demo parts = 85 parts on the current data): `--estimate` says ~26 min, ~$2.7 at
3 at a time (the measured 19 s wall and $0.032 per part). The kitchen + demo part share above is already 52 of them.

End to end on :8011 before the warm (the stale-variant fix, live): `POST /parts/frigidaire-fdpc4221as-341c87/model?mode=llm_scad`
on the stored "OpenSCAD isn't installed" template came back in 0.2 s as the template, `cad = {status: "writing",
eta_s: 60}`; `part.json?mode=llm_scad` every 10 s counted 56 → 46 → 36 → 26, then tier `scad`, made_by
`xai:grok-4.7 + openscad` (38 s, $0.018).

## The comparison (HF vs CAD vs template)

`compare-dishwasher.png`, `compare-window.png`, `compare-knob.png`: product photo, then each model from 3/4 and the
front (pyrender, one scale per sheet), as `:8011` serves them after the warm. `compare-writers-*.png`: the CAD geometry
of every writer above side by side.

| Part | Model | Faces | GLB | bbox vs listed | Shape error | Seconds | $ |
|---|---|---|---|---|---|---|---|
| Dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | HF · Hunyuan3D-2.1 | 20,000 | 414 KB | 0.0 % | 21.7 % (uniform-scale residual) | 18 | $0 (ZeroGPU quota) |
| | CAD · Grok 4.7 low, as served (photo on the front) | 2,640 | 95 KB | 0.0 % | 0.0 % (raw bbox) | 38 | $0.018 |
| | CAD geometry only (`ASSET_SCAD_PHOTO=false`) | 2,640 | 51 KB | 0.0 % | 0.0 % | 38 | $0.018 |
| | Template (box, photo) | 72 | 43 KB | 0.0 % | exact by construction | ~2-5 (one vision call) | ~$0.001 |
| Window frame (JELD-WEN V-2500, 1511×1207×83 mm) | HF · Hunyuan3D-2.1 | — | — | — | — | not made: every HF token's ZeroGPU quota is spent until ~05:00 (the asset-mode probe saw a 74 % depth guess on a sibling window) | — |
| | CAD · Grok 4.7 low, as served | 788 | 72 KB | 0.0 % | 0.0 % | 52 | $0.035 |
| | CAD geometry only | 788 | 15 KB | 0.0 % | 0.0 % | 52 | $0.035 |
| | Template (window, photo) | 168 | 60 KB | 0.0 % | exact | 2 | ~$0.005 (Grok fallback: Groq was rate limited) |
| Cabinet knob (Liberty Charmaine, 28×28×28 mm) | HF · Hunyuan3D-2.1 | 20,000 | 411 KB | 0.3 % | 66.3 % | 22 | $0 |
| | CAD · Grok 4.7 low, as served | 2,076 | 84 KB | 0.0 % | 0.0 % | 132 | $0.036 |
| | CAD geometry only | 2,076 | 37 KB | 0.0 % | 0.0 % | 132 | $0.036 |
| | Template (box, photo) | 12 | 46 KB | 0.0 % | exact | ~2-5 | ~$0.001 |

What the renders show:
- **Dishwasher.** Hunyuan guessed an installed context: a rounded tub with loose side "panels" and a base plate,
  squashed to the listed box (22 % off in proportion). The CAD model is a clean appliance (steel door, black control
  strip and toe kick, grey carcass) and with the photo on its front reads like the product; the template is similar
  with the photo, flatter from the side.
- **Window.** The CAD geometry has frame, sashes and bars; with the photo projected over that relief the bars show
  twice (photo + geometry), so here the template (whose bars are the photo alone) looks cleaner. `ASSET_SCAD_PHOTO=false`
  shows the CAD as written. One of two grok-4.7-low windows came back a plain slab (`compare-writers-window.png`).
- **Knob.** Hunyuan made a thin stalk on a plate (66 % off); the CAD model is a proper knob (dome, neck, rose) at 28 mm;
  the template is a 28 mm box with the photo on it. The biggest win for CAD: small moulded or turned parts.

## For the app (`feat/cad-compare`)

- The card: "AI mesh · Hunyuan", "CAD · Grok 4.7 · OpenSCAD" (the writer from `made_by`), "Template"; while Grok
  writes, "Template · Grok is writing the CAD model… 1 min" counting down between polls.
- Placed and held parts in LLM+CAD (the setting, or Compare showing CAD) with `cad.status = "writing"` are polled
  (`part.json?mode=llm_scad` every 10-15 s) and the CAD model is swapped in through the existing swap (anchor kept).
- Compare stays two-way (HF ⇄ LLM+CAD): LLM+CAD already shows the template until its CAD model lands, and "auto" is not
  a template (it is the moulded parts' CAD or Hunyuan), so a third stop would need a new server mode for little gain.

## Editor gate (app `feat/cad-compare`)

No scene or builder change (the watch lives in the existing `AssetModeSwitcher`; `CadUpgrade` is a static class), so
no Wire is needed.

1. `unity command recompile` → `recompile_status` → `console`: zero `error CS`.
2. `run_tests --mode editor --filter AirTools.Tests.CadUpgradeTests` (7), `AirTools.Tests.SettingsAssetsTests` (29) and
   `AirTools.Tests.SettingsAssetsGateTests` (6, Unity-side swap/anchor).
3. Backend: `:8011` (this lane's data: the kitchen and demo parts have CAD) or `:8004` after the steps above:
   `eval AirTools.Core.ServerConfig.SetOverride("http://127.0.0.1:8011")`.
4. Play, `AgentHarness.OpenWorld()` in the kitchen, `AgentHarness.RemoveComponent("dw1")` (the dishwasher out).
5. **Writing → upgrade while placed:** `AgentHarness.AssetCadCheck("lg-ldntm545s-1a49ae")` (an LG dishwasher that no
   warm made CAD for; `lg-ldnph753s-b50af2` / `lg-ldnpq44hs-6de824` if it has one by then), then poll
   `AgentHarness.AssetModeResult()` (~1-2 min). Expect `assetmode.cad.first` (LLM+CAD, tier `llm`, badge
   "Template · Grok is writing the CAD model… 1 min"), `assetmode.cad.writing_badge`, `assetmode.cad.watched`,
   `assetmode.cad.upgraded`, `assetmode.cad.model` (tier `scad`, "CAD · Grok 4.7 · OpenSCAD") and
   `assetmode.cad.anchor` (≤ 1 mm, ≤ 0.1°). `AgentHarness.AssetCadState()` shows the watch (polls, eta) meanwhile.
   Captures: `AgentHarness.AssetModeLook()` + `capture_game_view` once while writing and once after the upgrade.
6. **Ready at once:** `AgentHarness.AssetCadCheck("lg-ldntm545s-1a49ae")` again (now CAD) → "ready at once" path.
7. **Compare:** `AgentHarness.AssetModeCheck("lg-ldntm545s-1a49ae", twin: true)` (the existing check: setting HF then
   LLM+CAD, Compare both ways, a twin in the other mode) → `AssetModeLook()` → capture: HF next to CAD.

## What only the headset shows

- How HF and CAD compare in the kitchen scan at true scale and in passthrough light (the renders are flat-lit).
- The swap from the template to the CAD model while you look at it (no jump; the gate checks the anchor numerically).
- The card's countdown and the toasts readable at arm's length; Compare poked with a hand.
- Frame time with CAD models (1-5K faces, 15-100 KB) vs Hunyuan meshes (20K faces, ~410 KB).
- Polling over the hotspot (one `part.json` every 10-15 s per waiting part).
