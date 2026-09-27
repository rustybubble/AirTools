# Backend hand-off 2: the agent uses the tape (B1) and the measured mandate (B3)

*Sat 2026-09-26, app lane → P4. Specs: `docs/ideas/brain.md` §4 S1 (B1) and S2 (B3), on `m7`.*

These are three more patches for `airtools-drone-backend`. They go **on top of the 8 patches in
`docs/handoff/p4/`**, and together they sit on `main` @ `715d119`. We made and tested them in a
private scratch clone. Nothing changed in your repo or on your running `:8000` server, and nothing
was pushed. No test calls an LLM or the network.

The second half of this file is the **app-side contract**: everything the Unity side needs
without reading the server code.

## How to apply

```sh
cd airtools-drone-backend                     # main @ 715d119
git switch -c app-handoff-p4
git am /path/to/AirTools/docs/handoff/p4/*.patch          # 0001–0008 (first hand-off)
git am /path/to/AirTools/docs/handoff/p4-b1-b3/*.patch    # 0009–0011 (this one)
uv sync --extra pipeline
uv run pytest -q
```

- All 11 patches apply cleanly with `git am` on `715d119`.
- The result is byte-identical to the tested tree (tree `b5a790f0`).
- No new dependencies.
- Restart uvicorn afterwards.

## Patches

| Patch | Spec | What it changes | Tests |
|---|---|---|---|
| `0009-feat-the-agent-measures-with-the-headset-s-tape-…` | S1 / B1 | New `server/scene_digest.py`, reading the site's `structure.r<rev>.json`: counts, 10 mm size groups, objects, gutter-edge candidates. New `server/survey.py`, which holds the `/agent/observe` models, the grouping, the spoken summaries, the gutter verdict and template answers. In `agent.py`: fast paths plus the LLM tools `survey`, `check_slope` and `survey_query`; context `site`/`scale`; "numbers only from tools" in the system prompt. New endpoint **`POST /agent/observe`**. Documented in `api.md` | `tests/test_survey.py`: 60 |
| `0010-feat-measured-mandate-checkout-…` | S2 / B3 | New `server/mandate.py`: intent (limits), cart (hash), checks, hold nonces. New endpoints **`POST /commerce/limits`** and **`POST /checkout/prepare`**. **`POST /checkout`** now verifies the hold proof and adds `receipt.mandate`. The Cybersource request carries the cart hash and the merchant-defined data. New `REQUIRE_HOLD_PROOF` setting (default `false`). Documented in `api.md` | `tests/test_mandate.py`: 36 |
| `0011-feat-voice-narrows-the-purchase-limits-…` | S2 / B3 | Limit phrases in any command become `set_limits` plus the `show_limits` action; the rest of the command is then routed as usual. New LLM tool `set_limits`. Voice can't loosen a limit | `tests/test_mandate.py`: +17 |

### Test results

| After | passed | failed | deselected |
|---|---|---|---|
| the first 8 patches | 650 | 1 | 7 |
| 0009 | 710 | 1 | 7 |
| 0010 | 746 | 1 | 7 |
| 0011 | 763 | 1 | 7 |

- The single failure is the known macOS-only one from the first hand-off (`os.sched_getaffinity`):
  `tests/pipeline/test_structure_layer.py::test_lines_only_when_planes_fail`.
- `ruff check` is clean.
- `ruff format --check` flags only the 3 upstream files that were already unformatted.

### Decisions (please confirm or override)

1. **Action name `survey`, not `measure_objects`.** S1 already specifies `survey {label, where,
   measure}` along with `check_slope`, `show_survey` and `/agent/observe`, so we followed the spec
   rather than inventing a second name. `measure: "size"|"width"|"height"` covers "width / height /
   both".
2. **`REQUIRE_HOLD_PROOF` defaults to `false`.** S2 asks for `true`, but the current APK calls
   `/checkout` without a proof and would get 400s.
   - Any request that *carries* a proof is always verified strictly.
   - Set `REQUIRE_HOLD_PROOF=true` in `.env` once the app build that sends the proof is installed.
3. **`clientReferenceInformation.code` is `"at-"` plus 47 hex digits (50 characters).** S2 says
   `hash[:48]` (51 characters) and cites a 59-character limit, but that limit is only [R]eported,
   and Cybersource documents 50 for this field. Merchant-defined values are cut to 64 ASCII
   characters.
4. **The kitchen's 18 cabinet doors form 8 size groups**, not the "5 groups" in the spec:
   - 4 repeat sizes (6 at 262×279, 4 at 208×525, 2 at 261×426, 2 at 205×422 mm), the same as
     the file's `groups[]` g0–g3;
   - 4 one-off doors, each its own group.

   The spoken summary names the 3 most common sizes and says "5 more sizes on the card".
5. **Where state lives.**
   - In memory, reset by a server restart: pending survey requests, the last survey, hold nonces.
     After a restart the panel just prepares again.
   - On disk: intents in `data/mandates/intents/` (24 h) and carts in `data/mandates/carts/`.
6. **Not done, following S2's cut order:**
   - TAP request signing and JWKS (so there is no `seller_verified` via a signed HEAD; that check
     uses `Seller.verified` instead);
   - the phase-2 payment link.

   Also not done: B10/B16 survey follow-ups (pulls for every door, dishwasher fit) and the mock
   server mirror (see the contract, §8).

---

# APP-SIDE CONTRACT

All paths are relative to the parts server, the same base URL as today. Every change here is
additive: an app build that ignores it keeps working (see decision 2).

## 1. Context: two new fields (`AgentContext.Build`)

Send these with every `/agent/command` and `/voice/command`:

| field | value | why |
|---|---|---|
| `site` | the scene package the headset has open, as named by `GET /scenes` (e.g. `"kitchen"`, `"synthetic-facade"`). Omit it for the built-in scene | With it, the server can say "Surveying 18 doors.", read "doors" as `cabinet_door` in a kitchen, answer "No windows in this scene's structure layer." instead of sending an empty sweep, and suggest gutter `edge_ids` |
| `scale` | the scale correction factor currently applied (1.0 = none) | multiplies the structure sizes the LLM sees |

Without `site`, `survey` and `check_slope` still work: the action always goes to the headset, and
the reply is simply less specific ("Surveying the doors.").

## 2. New actions (add them to `AgentActions.Known` and the switch)

| `name` | args | what the app does |
|---|---|---|
| `survey` | `{label, where, measure, request_id}` | Measure every structure-layer object with `objects[].label == label` (`any` means all labels) using the **real** measure tool: 4 corners, auto-finish, one notebook entry per object. Then report once to `POST /agent/observe` (§3) with the same `request_id` |
| `check_slope` | `{target, request_id, edge_ids}` | Tape the target edge end to end (2-point tape). For `gutter`, `edge_ids` lists structure `edges[].id` candidates, best first; it may be `[]`, and then the app picks its own. Report `slope_result` (§4) |
| `show_survey` | `{request_id, label, groups, unverified, skipped, focus}` | Survey card. Colour `unverified` ids amber and highlight the `focus` ids (the answer to "which is the widest?") |
| `stop_survey` | `{}` | Abort a running survey. Completed entries stay; report `status: "aborted"`. If no survey is running, do nothing |
| `show_limits` | `{intent_id, max_total_usd, deliver_by, seller_policy, refused}` | Wrist chip such as `≤ $40 · by Fri · fastest` (a null means no limit). A non-empty `refused` means voice tried to loosen a limit; the spoken reply already says "use the panel" |

The arguments of `survey`:
- `label`: `cabinet_door`, `drawer`, `panel`, `appliance`, `window`, `door` or `any`.
- `where`: one of the following.
  - `all`
  - `visible`: inside the view frustum.
  - `upper` / `lower`: the object's centre y compared with the median for that label.
  - `left` / `right`: relative to the head's right vector.
  - `nearest`: a single object.
- `measure`: `size`, `width` or `height`. It changes only the spoken summary; always report both
  `w_m` and `h_m`.

`check_slope.target` is one of `gutter`, `sill`, `ledge` or `nearest_edge`.

Existing actions are unchanged. `add_note` also arrives as the answer to a slope report.

Example (`/voice/command` "measure every cabinet door", context `{"site": "kitchen"}`):
```json
{"reply": "Surveying 18 doors.",
 "actions": [{"name": "survey", "args": {"label": "cabinet_door", "where": "all", "measure": "size", "request_id": "sv-0c45d47f"}}],
 "job_id": null}
```
Other replies:
- "measure all the windows" in the kitchen: `{"reply": "No windows in this scene's structure layer.", "actions": []}`.
- A site without a structure layer: "No structure layer for this scene; mark the corners yourself."

Phrasings the server catches without an LLM:
- "measure / survey / size up" + "every / all (of) the / each / the" + an optional
  upper / lower / left / right / top / bottom / nearest + cabinet door(s) / door(s) / drawer(s) /
  window(s) / panel(s) / appliance(s).
  - "the" with a singular noun means `nearest`.
  - "I can see" or "in view" means `visible`.
  - "width" or "wide" means `measure: width`; "height" or "tall" means `measure: height`.
- A gutter / sill / ledge together with slope / drain / fall / pitch gives `check_slope`, unless
  the command also says find / get / need / buy.
- A bare "stop" or "cancel" gives `stop_survey`.

The LLM has the same tools for other phrasings.

## 3. `POST /agent/observe`: reporting a survey

Send this **once per `request_id`**:
- when the sweep ends;
- when it is aborted (`status: "aborted"` with what finished, plus `planned`);
- or when it can't start (`status: "no_structure"`, with an empty `results`).

```json
{"session_id": "quest-1a2b3c4d5e", "request_id": "sv-0c45d47f", "kind": "survey_result",
 "status": "done", "planned": 18, "tts": true,
 "results": [
   {"id": "o0", "label": "cabinet_door", "group": "g0", "w_m": 0.262, "h_m": 0.279, "area_m2": 0.0731,
    "angles_deg": [90.1, 89.8, 90.2, 89.9], "off_plane_m": 0.004,
    "snap": ["corner", "corner", "edge", "corner"], "unverified": false, "notebook_id": 12, "camera_id": 316},
   {"id": "o1", "label": "cabinet_door", "w_m": 0.263, "h_m": 0.278, "unverified": true, "notebook_id": 13, "camera_id": 316},
   {"id": "o6", "label": "cabinet_door", "w_m": 0.208, "h_m": 0.525, "notebook_id": 14, "camera_id": 318}],
 "skipped": [{"id": "o9", "reason": "corner behind scanned surface"}]}
```

Field rules:
- Only `session_id`, `kind` and, per result, `id` are required.
- `label` and `measure` may be left out when `request_id` is the one from the action.
- `unverified: true` means a corner didn't snap within 2 cm, so the raw corner was used and the
  object is drawn amber.
- `notebook_id` may be an int (`NotebookEntry.Id`) or a string.
- Use metres for everything the app sends.
- At most 500 results and 500 skips.

Response (the same shape as `/agent/command`, taken from the real server):
```json
{"reply": "3 doors in 2 sizes: 2 at 26 by 28 and 1 at 21 by 53 centimetres. One didn't lock onto its corners; it's amber. One skipped.",
 "actions": [{"name": "show_survey", "args": {"request_id": "sv-0c45d47f", "label": "cabinet_door",
   "groups": [{"w_mm": 262, "h_mm": 278, "count": 2, "ids": ["o0", "o1"]},
              {"w_mm": 208, "h_mm": 525, "count": 1, "ids": ["o6"]}],
   "unverified": ["o1"], "skipped": ["o9"], "focus": []}}],
 "job_id": null}
```

How to handle it:
- Speak `reply`. With `"tts": true` the response also carries `audio_b64`/`audio_mime`
  (+ `tts_error`), exactly like `/voice/command`. Otherwise call `/voice/speak`.
- Run `actions[]` through `AgentActions`, the same path as other agent replies.
- `groups` are sorted most common first. `ids` keep the order you sent them in.

Other survey replies:
- aborted: "Stopped after 6 of 18. 6 doors, all 26 by 28 centimetres."
- `no_structure`: "No structure layer for this scene; mark the corners yourself."
- nothing measured: "Found no doors to measure."

A result list that isn't empty becomes the session's stored survey. Follow-ups use it:
- "which one is the tallest?" / "widest" / "narrowest" / "shortest" / "largest" / "smallest"
  → `{"reply": "The tallest door is o6: 21 by 53 centimetres.", "actions": [{"name": "show_survey", "args": {…, "focus": ["o6"]}}]}`;
- anything else ("how many are taller than 40 cm?") goes to the LLM with a CSV of your numbers.

## 4. `POST /agent/observe`: reporting a slope

`run_m` and `fall_mm` are required; without them the server answers 422.

```json
{"session_id": "quest-facade", "request_id": "sl-5f6ccf4a", "kind": "slope_result", "target": "gutter",
 "run_m": 4.2, "fall_mm": 0.4, "low_end": [2.1, 6.2, 0.03], "gravity_residual_deg": 0.0,
 "uncertainty_mm": 2.0, "notebook_id": 15, "tts": false}
```
- `fall_mm = |Δy| × 1000` along the taped edge.
- `uncertainty_mm` is optional. The default is `run_m × 1000 × tan(gravity_residual_deg) + 2`.
- `gravity_residual_deg` is also optional; the default comes from the site's `scene.json`.

Response:
```json
{"reply": "Flat: 0 ± 2 mm of fall over 4.20 m. It needs about 9 mm, so water will pond.",
 "actions": [{"name": "add_note", "args": {"text": "Gutter slope: won't drain (0.4 ± 2 mm of fall over 4.20 m; needs 8.7 mm)"}}],
 "job_id": null}
```

The verdict uses a required fall of `run_m × 2.083 mm` (¼ in per 10 ft):

| verdict | condition |
|---|---|
| drains | `fall − unc ≥ required` |
| won't drain | `fall + unc < required` |
| inconclusive | anything else ("… that's inconclusive. Put a real level on it.") |

## 5. Checkout: prepare → hold → pay (`CheckoutPanel`)

1. **When the panel opens, and after every change** of seller, quantity (the +/− buttons) or BOM
   lines, call `POST /checkout/prepare`:
   ```json
   {"session_id": "quest-1a2b3c4d5e", "part_id": "gutter-hanger-hidden-k-style", "seller_idx": 0,
    "units_needed": 8, "bom_id": null, "bom_lines": [],
    "evidence": [{"notebook_id": 3, "label": "tape #3", "value_m": 4.2, "camera_id": 316,
                  "photo": "/scenes/synthetic-facade/thumbs/0316.jpg", "array": {"spacing_mm": 600, "count": 8}}]}
   ```
   - `units_needed` is **pieces** (`CheckoutPanel.Quantity`); the server computes the packs.
   - `evidence` (at most 20 entries, every field optional) should come from `PartTool`: the array
     group's tape entry, its length, the spacing and the count, plus the notebook entry's evidence
     camera.
   - `photo` is any path the app can display later; the server passes it through verbatim.

   The response (real):
   ```json
   {"cart": {"id": "cart-06182d8e", "intent_id": "int-f1e5b3fb",
             "lines": [{"part_id": "gutter-hanger-hidden-k-style", "seller_idx": 0, "seller": "Home Depot",
                        "units_needed": 8, "pack_qty": 1, "packs": 8, "unit_price_usd": 2.98,
                        "shipping_usd": 0.0, "line_total_usd": 23.84}],
             "bom_id": null, "bom_lines": [], "shipping_usd": 0.0, "total_usd": 23.84,
             "evidence": [{"notebook_id": 3, "label": "tape #3", "value_m": 4.2, "camera_id": 316,
                           "photo": "/scenes/synthetic-facade/thumbs/0316.jpg", "array": {"spacing_mm": 600.0, "count": 8}}],
             "created_at": "2026-09-26T08:31:26.179791Z"},
    "cart_hash": "sha256:e846d33b8d4814db3bdbfa7dd91c7f8774f689e50a494ee27100486e0c063a72",
    "hold_nonce": "hn_DvNRPiYp44orLO4XstHSV0BXiOIx0STxggYOXe8th0c",
    "nonce_expires_at": "2026-09-26T08:33:26.179791+00:00",
    "intent": {"id": "int-f1e5b3fb", "max_total_usd": 40.0, "deliver_by": "2026-10-02",
               "seller_policy": "fastest", "source": "panel", "text": "panel: fastest"},
    "checks": [
      {"id": "price_reread", "status": "ok", "ok": true, "detail": "$2.98 × 8 from the saved listing"},
      {"id": "within_limit", "status": "ok", "ok": true, "detail": "$23.84 ≤ $40"},
      {"id": "delivery", "status": "ok", "ok": true, "detail": "arrives Thu Oct 1, by Fri Oct 2"},
      {"id": "qty_evidence", "status": "ok", "ok": true, "detail": "tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)"},
      {"id": "seller_verified", "status": "ok", "ok": true, "detail": "Home Depot: structured seller listing"},
      {"id": "fit", "status": "ok", "ok": true, "detail": "green, 38 mm spare"},
      {"id": "card", "status": "ok", "ok": true, "detail": "Visa test card •••• 1111, held by the server"}],
    "all_ok": true}
   ```
   - Render one row per check: `ok` is a green ✓, `warn` an amber ⚠ (it doesn't block), `fail` a
     red ✗. If `all_ok` is false, **disable Pay**.
   - Show `cart.total_usd` (the server's price) and `intent` (the limits).
   - The nonce lives for 120 s, and a newer prepare revokes the older nonce. Re-prepare when it
     expires, or simply before each hold if more than about 100 s have passed.
2. **When the 1 s hold fires** (`HoldToConfirm.Confirmed`; this is the only code path that may
   send a nonce), send `POST /checkout`:
   ```json
   {"part_id": "gutter-hanger-hidden-k-style", "seller_idx": 0, "qty": 8, "session_id": "quest-1a2b3c4d5e",
    "bom_id": null, "bom_lines": [],
    "cart_hash": "sha256:e846d33b…3a72", "hold_nonce": "hn_DvNR…h0c", "hold_ms": 1043}
   ```
   - `qty` is the prepared `cart.lines[0].packs`, not the unit count.
   - `bom_id` and `bom_lines` must be exactly what was prepared.
   - `hold_ms` is how long Pay was actually held, in whole milliseconds. It must be ≥ 1000; the
     app needs to expose the held duration (e.g. `HoldToConfirm.HeldMs`).
3. **The receipt** is today's fields plus `mandate` (real, offline):
   ```json
   "mandate": {
     "intent": {"id": "int-f1e5b3fb", "max_total_usd": 40.0, "deliver_by": "2026-10-02", "seller_policy": "fastest", "source": "panel", "text": "panel: fastest"},
     "cart": {"id": "cart-06182d8e", "hash": "sha256:e846d33b…3a72", "total_usd": 23.84, "units_needed": 8, "packs": 8},
     "authorization": {"mode": "offline", "status": "OFFLINE_RECEIPT", "approval_code": null, "hold_ms": 1043},
     "evidence": [{"notebook_id": 3, "label": "tape #3", "value_m": 4.2, "camera_id": 316, "photo": "/scenes/synthetic-facade/thumbs/0316.jpg", "array": {"spacing_mm": 600.0, "count": 8}}],
     "checks": [ /* same rows as prepare */ ]}
   ```
   Show the chain as rows:

   | row | source | content |
   |---|---|---|
   | Intent ✓ | `intent.text` or the limits | `null` when no limits were set, so show "no limits" |
   | Cart ✓ | the last 4 hex digits of `cart.hash` | |
   | Authorization | `authorization.mode == "sandbox"`: ✓ plus `approval_code`; `"offline"`: ✗ "offline, no payment" | |
   | Evidence | the evidence thumbnail from `photo`, or `camera_id` | |

   Keep showing `label` verbatim (§7 honesty rule).
4. **Errors.** On any 4xx, go back to *Ready* and prepare again. The nonce is gone after any attempt
   that got past the `hold_ms` check.

   | code | `detail` | UI |
   |---|---|---|
   | 400 | `"hold_ms 600 < 1000: paying needs the full hold"`, `"hold proof required: …"` | "hold the full second" (the nonce is kept on a short hold) |
   | 409 | `"hold nonce unknown or already used; reopen checkout"`, `"… expired (2 min) …"`, `"cart changed since prepare …"`, `"the price or listing changed since prepare; reopen checkout"` | re-prepare and show the new total |
   | 422 | `{"error": "a mandate check failed", "checks": [ … ]}` (e.g. `"$23.84 is over your $20 limit"`) | render the failing rows; Pay stays disabled |

   Note that `detail` is a **string** for 400/409 and an **object** for 422.

## 6. Limits

- **By voice** (server-side, no app work): "under $40", "no more than 40 dollars", "arriving by
  Friday", "here by tomorrow". Any command can carry them ("Find hangers, under $40, arriving by
  Friday."). The server answers with a `show_limits` action and "Limits set: under $40, by Fri Oct 2."
  Voice can only tighten. "Up to $100" after $40 gives "You'd need to raise that on the panel. Still
  under $40, by Fri Oct 2." plus `refused[]`.
- **From the panel** (the user's hand; the only place limits can be loosened or cleared), call
  `POST /commerce/limits`:
  ```json
  {"session_id": "quest-1a2b3c4d5e", "source": "panel", "max_total_usd": 100, "deliver_by": null, "text": "panel"}
  ```
  - Only the fields present change. `null` clears a field (panel only).
  - `deliver_by` takes an ISO date, a weekday, `today` or `tomorrow`.
  - `seller_policy` takes `cheapest`, `fastest` or `best`.

  The response:
  `{"intent": {id, max_total_usd, deliver_by, seller_policy, source, text}, "refused": [], "changed": ["max_total_usd", "deliver_by"]}`.
  After it, re-prepare an open checkout.
- `max_total_usd` caps the **order total**: part, BOM lines and shipping.

## 7. Suggested app tests (from S1/S2 acceptance)

- `AgentActionsTests`: `Known` includes `survey`, `check_slope`, `show_survey`, `stop_survey` and `show_limits`.
- EditMode survey on the synthetic facade: `window` gives 1 target measuring 1.500 × 1.200 ± 0.005 m,
  and the report JSON matches §3. Abort leaves only complete entries, and one undo removes the survey.
- EditMode checkout:
  - a 0.6 s hold sends nothing;
  - a 1.0 s hold sends exactly one `/checkout` with `cart_hash`, `hold_nonce` and `hold_ms ≥ 1000`;
  - a quantity change triggers a new prepare;
  - the receipt chain renders from the §5 fixture.
- A grep test: only the hold handler sends `hold_nonce`.

## 8. Mock server (`tools/mock_parts_server.py`, app repo)

It does not have these endpoints yet. To keep the Unity lane unblocked, mirror the shapes above:
- `/agent/observe`: echo a `show_survey` built from `results`;
- `/commerce/limits`;
- `/checkout/prepare`: fixed checks and a random nonce;
- `/checkout`: accept the proof and add `mandate`.
