# Backend hand-off: asset modes, HF image-to-3D vs LLM + OpenSCAD (settings-assets)

*Sat 2026-09-26, app settings-assets lane. Two patches on top of `demo-next` @ `f4331b1` (`e2e-measure-replace` +
`asset-window`), made and tested in the scratch worktree `backend-assetmode` (branch `asset-mode`) on `:8009`. Nothing
changed on `:8004` or in `~/airtools-backend-demo`'s checked-out branch, and nothing was pushed.*

The user's words: "In the settings we should also be able to switch between using HF models to generate dynamic assets
and using LLM + opencad instead, with HF models always being aggressively cached. We will compare the quality ourselves."
The app's side is on `feat/settings-talk-assets` (Settings ▸ 3D models: HF · LLM+CAD · Auto, the card's Compare chip).

## How to apply

```sh
cd ~/airtools-backend-demo            # or any checkout of demo-next (f4331b1)
git switch demo-next
git am /path/to/AirTools/docs/handoff/p4-assetmode/*.patch
uv run pytest -q tests/test_asset_mode.py tests/test_assets.py tests/test_app.py tests/test_jobs.py tests/test_warm.py
```

- No new dependencies. One new env var, `HF_WARM` (default `true`; `false` = Hunyuan only on demand). Restart uvicorn.
- Tests: `tests/test_asset_mode.py` has 32 tests (HF, the LLM and OpenSCAD are all stubbed). The full suite gives
  1472 passed, 2 skipped and 3 failed. The 3 are `tests/pipeline/test_structure_*` (`os.sched_getaffinity` doesn't exist
  on macOS) and fail the same way on untouched `demo-next` (1440 passed there).
- `tests/conftest.py` sets `HF_WARM=false` for every offline test (the warm queue test opts in).

## Patches

| Patch | What |
|---|---|
| `0001-feat-assets-asset-modes-…` | `asset_mode` = `"hf"` \| `"llm_scad"` \| `"auto"` per request: in the context of `/agent/command` and `/voice/command` (the session keeps it; `find_part`, `find_in_gap` and the replace job search with it), in the `/parts/search` body (`SearchRequest.asset_mode`), and `?mode=` on `part.json` / `model.glb`. New `POST /parts/{id}/model?mode=` → `{status: ready\|pending, asset, model_url, hf_queued, scad_pending}` and `GET /assets/hf-warm`. **Variants side by side**: `model.hf.glb`, `model.scad.glb`, `model.auto.glb` (+ `asset.<v>.json`); `model.glb` and `part.json` follow the mode asked for last; legacy parts are adopted (an `ai_mesh` model.glb is the hf variant, never remade). `asset.mode`, `asset.note` (why the preferred tier didn't make it) and `asset.made_by` for every tier (`hf:tencent/Hunyuan3D-2.1`, `xai:grok-4.7 + openscad`, the template's LLM). **HF cached aggressively**: the raw Hunyuan mesh by photo hash (`data/cache/hunyuan/<sha1>.glb`, one Space call per photo, ever); `server/hf_warm.py` warms every searched part ahead of need, one at a time, backing off 5 min → 1 h on a ZeroGPU quota error; `python -m server.warm --hf [--hf-limit N] [--hf-no-wait]` does every cached part. **LLM + OpenSCAD**: Grok writes SCAD (role `asset_scad`) and the `openscad` binary renders it, in the background (minutes) with the template meanwhile; no binary = the template, labelled. `docs/api.md` §2 "Asset modes", §4, §6 |
| `0002-fix-assets-Hunyuan-results-were-never-read-…` | **A live bug, in `auto` too.** Both Hunyuan Spaces generated fine (2.1: 13 s of shape generation, 2: 4 s) and then every call "failed" with 403 Forbidden: `_resolve_remote_file` fetched `{src}/file=<path>`, but with gradio_client 2.7 that path is the local file the client already downloaded. It now reads the local path first. So on this venv's gradio_client `ai_mesh` could never make a model: every moulded part fell back to its template (none of the 58 parts cached on the demo server is `ai_mesh`). Also: a photo the Space just failed on isn't re-sent for 10 min (on-demand + the warm queue retried the same photo back to back), and `hf` mode keeps Hunyuan's mesh up to 200 % scale residual (a thin window came back 74 % off on depth, a guess squashed to the listed size; `auto` still drops > 60 %) |

## Live probe on :8009 (`probe.json`, `hf-vs-llmcad.png`)

`POST /parts/{id}/model?mode=…&wait_s=60`, one kitchen part and one window frame, cold (no variant yet; the LLM template
plans were already in the cache).

| Part | Mode | Tier · made_by | Time | Note |
|---|---|---|---|---|
| Frigidaire FDPC4221AS dishwasher 24″ | hf | `ai_mesh` · hf:tencent/Hunyuan3D-2.1 | 20.8 s (Space 19.7 s) | — |
| | llm_scad | `llm` (box template) · groq:qwen/qwen3.8-27b | 0.2 s | OpenSCAD isn't installed on the server: the LLM template instead |
| JELD-WEN V-2500 window 47.5 × 35.5″ | hf | `ai_mesh` · hf:tencent/Hunyuan3D-2.1 | 19.0 s (Space 18.6 s); 0.96 s rebuilt from the cache | depth guess 74 % off, squashed to size (before 0002's hf threshold: dropped for the template) |
| | llm_scad | `llm` (window template) · xai:grok-4.20 | 0.1 s | OpenSCAD isn't installed on the server: … |

- **OpenSCAD is not installed on this Mac** (not on PATH, no app, no `work/assets-llm/tools/openscad.AppImage`), so
  "LLM+CAD" is the LLM template here, labelled in `asset.note` and on the headset ("Template · no OpenSCAD"). To get real
  Grok CAD: install an OpenSCAD with the manifold backend (`meshgen._scad_run` passes `--backend=manifold`; the 2021
  release lacks it; a 2024+ snapshot, e.g. `brew install --cask openscad@snapshot`, with its binary on PATH), then
  request `llm_scad` again: Grok's loop runs in the background (minutes, ~50K reasoning tokens per part per E4).
- **ZeroGPU quota**: works from this Mac with `HF_TOKEN` (key #1 answered every call) and anonymously (one call, 18.9 s,
  shape 14.0 s). About 11 Hunyuan generations today (~150 s of GPU) and no quota refusal. `GET /assets/hf-warm` keeps the
  last refusal messages (`quota_messages`) when one comes.
- `hf-vs-llmcad.png`: an offline preview (a PIL painter's-algorithm render: no GL on this Mac, so treat it as a rough
  look, not the headset). On a straight-on product photo of the dishwasher, Hunyuan made a rounded front with side
  "panels" and a base plate (it seems to guess an installed context); the template is a clean box with the photo. For the
  window the two are close. The headset captures from the Editor gate are the real comparison.

## For the catalog lane

The catalog window's endpoint (`/catalog`, not on `demo-next`) should take `asset_mode` like `/parts/search`: pass it
to `assets.resolve_shared(part, mode=...)` for the items it builds, and call `hf_warm.enqueue_many(parts)` so HF meshes
are made ahead of need. The app sends `UserPrefs.AssetModeWire` (`AssetModes.AddTo(body, UserPrefs.Assets)`).
