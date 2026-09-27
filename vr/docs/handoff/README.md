# Backend hand-off: patches for the P4 parts server

*Fri 2026-09-26, app lane → P4 (and notes for P1).* These patches answer the integration report's
"Contract findings" 1–15 (`docs/backend-integration.md` on `backend-integration`). They were made and
tested in a private scratch clone of `airtools-drone-backend` `main` @ `715d119`. Nothing was
changed in your repo or your running `:8000` server, and nothing was pushed.

**Asset generation (assetgen):** [`p4-asset/`](p4-asset/README.md): a window template, a Grok retry for a poor asset
answer and who made each asset, `POST /parts/{id}/resize` (made to size), and a search fitted to a taped opening; on
`e2e-measure-replace`.

**Second hand-off:** [`p4-b1-b3/`](p4-b1-b3/README.md) adds patches 0009–0011 on top of these 8
(the agent measures with the tape, B1; the measured-mandate checkout, B3) with the app-side
contract.

## How to apply

```sh
cd airtools-drone-backend            # on main @ 715d119 (or later)
git switch -c app-handoff-p4
git am /path/to/AirTools/docs/handoff/p4/*.patch
uv sync --extra pipeline             # the pipeline tests import scipy/pycolmap; server-only work: uv sync
uv run pytest -q
```

- The series applies cleanly with `git am` on `715d119`, and the result is byte-identical to the
  tested tree.
- Restart uvicorn to make the changes live.
- No test needs a key, `.env` or the network. `tests/conftest.py` already blanks the keys and
  blocks sockets. Every LLM call in the new tests is monkeypatched or answered by respx.

## Patches

| Patch | Finding | What it changes | Tests added |
|---|---|---|---|
| `0001-fix-open-checkout-…` | #10 | Adds a fast path `_CHECKOUT_RE` (see below). `start_checkout` now runs on the server and needs a selected part with sellers. `seller_index` may be null: that picks a seller named in the command, else the cheapest/fastest if asked, else `recommended_seller`. The reply names the seller. `find_part` no longer says "to buy". | `test_agent.py`: 22 cases. Phrasing matches and non-matches, the exact live context (both live phrasings → `start_checkout {seller_index: 1}`, no search, no LLM), named/cheapest/fastest seller, no selection / no sellers, LLM path with null index, tool schema |
| `0002-fix-say-fits-the-run-…` | #11 | A `fits` result with no `spare_mm` (axis `length`) now reads "The first fits the run." / "It fits the run." | `test_jobs.py`: 1 |
| `0003-fix-cap-vision-output-…` | #12 | `llm.ROLE_MAX_TOKENS = {"vision": 400}`, applied in `chat()` unless the caller sets its own. The `ask_scene` prompt now requires `frame_id` + `box` whenever the thing is visible ("a rough box beats none") and drops "leave … null if you're not sure". | `test_llm.py` 1, `test_vision.py` 1 (respx: request body has `max_tokens: 400` and the new prompt) |
| `0004-fix-return-503-…` | #12 | One app-level handler maps `openai.RateLimitError` to **503** with `Retry-After` (the provider's value rounded up, else 60 s). It covers `/scene/ask`, `/agent/command` and `/voice/command`. `api.md` documents it. | `test_app.py`: 2 (Retry-After `7.2` → `8`; missing → `60`) |
| `0005-fix-read-Home-Depot-s-cabinet-hardware-…` | #15 | `_parse_dims` now reads Home Depot's hardware fields: `Knob Diameter` → w and h, `* Projection` → d, `Pull Length` → w. A pull's thickness is never published, so h takes its projection. Centre-to-centre is still ignored. | `test_sellers.py`: 3, using the verbatim spec groups from the live responses |
| `0006-feat-sizes-printed-in-the-listing-title-…` | #15 | `sellers.parse_title_sizes()` is a conservative title parser. `search._dims_from_title()` uses it only after the page fallback, and only to complete a box. `dims_source: "title"`. | `test_sellers.py`: 23-row parser table. `test_search.py`: 7 (W/D/H labels, flat-goods rule, fill the one missing side, never guess, end-to-end rescue) |
| `0007-feat-scene_pin-carries-…` | #6 | The `scene_pin` action adds `label` (the vision answer). Additive. | Existing `test_agent` case extended |
| `0008-docs-receipt-status-…` | #1, #2, #4 | `api.md` only. Receipt `status` is `AUTHORIZED` or `OFFLINE_RECEIPT`, and §7 gains a status column. `qty` counts listings (packs). `recommended_spawn.pos` is the eye point and `look` is a point to face. | none |

**#10 fast path:** these phrasings open checkout:
- "buy / purchase / order" followed by it / this / that / these / those / them
- "pay for it", "pay now", "pay with …"
- "check out" on its own, or followed by with / from / now / at / using

"buy a hinge", "where can I buy …", "check out this hinge" and "how much will I pay?" still go to
the LLM.

## Test results

| | passed | failed | deselected |
|---|---|---|---|
| Before (`715d119`) | 590 | 1 | 7 (`live`/`slow` markers) |
| After (8 patches) | 650 | 1 (the same test) | 7 |

The suite was run after every patch.

- `ruff check` is clean.
- `ruff format --check` flags the same 3 files before and after. These are upstream files that were
  already unformatted, and we left them alone.
- **The one failure is pre-existing and not ours:**
  `tests/pipeline/test_structure_layer.py::test_lines_only_when_planes_fail`. The cause is at
  `pipeline/structure.py:269`, which calls `os.sched_getaffinity(0)`. That call exists only on
  Linux, so it fails on macOS with "module 'os' has no attribute 'sched_getaffinity'". This is
  for P1; `len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else os.cpu_count()`
  would fix it.
- With plain `uv sync`, 4 pipeline test modules fail to import scipy. They need
  `--extra pipeline`.

## Evidence for #15 (why two patches)

The failing search had tied its listings to Home Depot products. The `home_depot_product`
responses are in your server's SerpApi cache (`data/cache/serpapi/`). They do carry sizes, just
not as Width/Depth/Height:

- **206951260** "European Style 3 in. (76 mm) Center-to-Center … Pull (25-Pack)": `Center to Center Measurement (mm)` 76, `Pull Length (in.)` 5.75 in, `Pull Projection (in.)` 1.25 in.
- **202824439** "Garrett 1-1/4 in. (32 mm) … Knob": `Knob Diameter (in.)` 1.25 in, `Knob Projection (in.)` 1.13 in.

We re-parsed all 25 cached `home_depot_product` responses:

- **5/5 pulls and 4/4 knobs now resolve** (0/9 before). The 206951260 pull comes out as
  146 × 32 × 32 mm.
- The other 16 parse exactly as before.
- 4 shelf brackets (HD gives only "Product Length") and 1 door hinge (height + width, no depth)
  are still `None`.

The title's "3 in. (76 mm)" is the screw spacing, not the pull's size. That is why the title
parser ignores centre-to-centre sizes, and why patch 0005, not 0006, fixes the handle search.

## Left for P4 (decisions or bigger than a patch)

- **#12, retries before the 503.** `llm._client()` uses `max_retries=4`, and the openai SDK waits
  out `Retry-After` for up to 120 s per retry. A vision 429 can therefore hold the headset's
  request for minutes before our 503 arrives.
  - Suggestion: 0–1 retries for the `vision` role.
- **#12, the prompt fix is not verified live.** We had no keys here.
  - If `qwen/qwen3.8-27b` still won't give a box, consider rescaling boxes in the 0–1000 range.
    Qwen-VL models often emit those, and `SceneAnswer._sanitize_box` drops any coordinate > 1.
  - Or accept an optional per-frame `point: [u, v]`. The app knows the camera poses and can
    project the asked spot into each thumb.
- **#5, `/scenes` lists packages the app can't load** (e.g. `strasbourg-cathedral-spire`, which
  has no `mesh.file`). We didn't change this, for two reasons:
  - Your test `test_scenes_list_defaults_revision_and_quality_when_absent` deliberately lists
    legacy packages.
  - `validate_package` loads the whole mesh, which is too heavy for every `/scenes` call.
  - Options: skip, or add `"loadable": false` when `scene.json` has no `mesh.file` or it's
    missing on disk. The app already hides these.
- **#2 `qty`:** we documented today's behaviour (listings, not pieces). If you'd rather take
  units, change `checkout.authorize`; the app would then send units.
- **#15, still dropped by design:**
  - shelf brackets ("12 in. x 8 in." and HD "Product Length" only);
  - a hinge with no published depth.
  - Filling a third side would be a guess, so estimating a bracket's width is your product call.
- **#10, seller order:** `seller_index` indexes the saved part's `sellers[]`, i.e. the order of the
  last `/parts/{id}/sellers` response. The app shows that same list.
- **#13** (the Groq Orpheus terms need the org admin) and **#14** (Hunyuan3D-2 returns 403 / quota
  used up) are config and quota issues, not code. Pre-warm the demo parts.
- **#8, #9:** no server change needed.

## Left for P1 (pipeline)

- **#3:** keep revisions monotonic across re-runs of a site. The headset caches `*.r<rev>.*`
  forever.
- **#4:** `recommended_spawn` puts the eye 3 package-units outside the bounding box. Indoors (the
  kitchen) that's a floating diorama. The app steps in until the scene spans ~90°; consider doing
  the same for indoor scans. The eye-point convention is now documented (patch 0008).
- **#7:** `schema.md`'s object example uses `rect` in (u, v); `api.md` says `corners3d`. Pick one.
  The app reads both.
- **§A** collision-mesh holes, **§B** rectangle corners behind or off the surface, **§C** scale
  (dishwasher ×1.491 vs altitude ≈1.47). Numbers are in `docs/backend-integration.md`, "P1
  findings".
- The macOS `os.sched_getaffinity` test failure described above.
