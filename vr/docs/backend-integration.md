# Backend integration: status report (P2/P3 → P1/P4)

*Fri 2026-09-25, branch `backend-integration` (on top of `m7`). Covers the brief "integrating the backend (P1 pipeline
+ P4 parts server) into the Unity app" against `airtools-drone-backend` `main` @ `715d119` (docs/api.md,
plan §1/§1a/§1b, schema.md, R6 §7.3, p2-structure-bench §5). No backend files were changed.
Updated late Fri 09-25 with the **real kitchen** (`scene/kitchen` r1 from hfbox) and the **real server with its
`.env`** (Groq agent, voice, vision): see the kitchen column below and the new findings (P1 §A–C, P4 10–15).*

## How it was tested

- **Real parts server, unmodified:** `uv run uvicorn server.app:app --port 8000`. First with synthetic packages and an
  empty `.env`; then with **`scene/kitchen` (r1, full, structure 61 planes / 671 edges / 594 corners / 40 objects)**
  and hfbox's `.env`, so the Groq agent (`gpt-oss-120b`/`20b`), Whisper STT, TTS and the vision model ran for real.
- **Synthetic packages:** the app's ground-truth facade exported in the exact package layout by
  **AirTools ▸ Export Synthetic Scene Packages** into `tools/.scenes/`. That gives `scene.json`, `mesh/collision.r1.glb`,
  `cameras.r1.json`, `structure.r1.json` and `thumbs/`. All three pass the backend's own
  `pipeline.package.validate_package`:
  - `synthetic-facade`: true scale;
  - `synthetic-facade-small`: everything × 1/1.47, like the kitchen's altitude scale;
  - `synthetic-facade-preview`: no structure.

  Every dimension is known exactly, so each number below is an error against ground truth.
- **Parts / agent / voice / ask:** first against `tools/mock_parts_server.py` (mirrors `docs/api.md` and the real
  server's code; 11 contract tests), then **against the real server** on the kitchen (checks 6–7 below).
- **B1 / B3 on the mock (hand-off `docs/handoff/p4-b1-b3/`, contract §8):** the mock also serves
  `POST /agent/observe`, `POST /commerce/limits`, `POST /checkout/prepare` and the hold-proof `POST /checkout`, plus the
  survey / check_slope / stop / "which is the tallest?" / "under $40, by Friday" fast paths (context `site`, `scale`).
  It is the patched server in OFFLINE mode; 46 tests, and side by side with the patched server on `:8002` all 49
  comparisons match. The kitchen lives in the backend's scene dir, so point the mock at it:
  ```
  python3 tools/mock_parts_server.py --port 8765 --scene-dir ~/airtools-drone-backend/scene \
      --voice-transcript "measure every cabinet door|which one is the tallest?|under \$40, arriving by Friday"
  ```
  `--voice-transcript "a|b|c"` makes each recording say the next phrase. `POST /mock/voice-transcript {"text": "…"}`
  swaps the script at runtime. `--require-hold-proof` = the server's `REQUIRE_HOLD_PROOF=true`.
- **Where it ran:** Unity Play mode in a second Editor (git worktree), with XR init off. The XR lane owns the Simulator.
  Every check is scripted in `AirTools.Dev.BackendHarness`. Rerun it on the kitchen with:
  ```
  unity command eval --code 'return AirTools.Dev.BackendHarness.LoadSite("kitchen");'
  unity command eval --code 'return AirTools.Dev.BackendHarness.SceneStatus();'
  unity command eval --code 'return AirTools.Dev.BackendHarness.CheckAlignment();'   # the X-flip check
  unity command eval --code 'return AirTools.Dev.BackendHarness.CheckObjects(3);'   # every door/drawer taped 3×
  ```

## Checklist (brief §8)

| # | Check | Synthetic packages (ground truth) | **Real kitchen, real server** |
|---|---|---|---|
| 1 | List `/scenes`, load a site: visual mesh, collider, spawn | **PASS:** 0.1 s load, rig at `recommended_spawn` (feet = pos − 1.6 m up), facing `look` | **PASS:** kitchen auto-loads in 1.6–1.9 s from the laptop (0.16 s from the headset cache): 199,997 visual tris + 4K atlas, 50k-tri collider, 165 cameras, full structure. Spawn faces the cabinets. Quest 3S (Development build, USB tunnel): loads in **1.0 s** and **holds 72 fps**: 250 of 259 one-second samples at the 72 Hz target (median 73), GPU 72 % median / 85 % max, app GPU time ≈ 10 ms of the 13.9 ms budget. The 200k-tri + 4K budget is fine |
| 2 | Revision swap, same files at r2 | **PASS:** ETag `304` until the bump, then collider + structure first, visual next frame; rig pose unchanged, a window-corner snap bit-identical | **PASS** on a throwaway copy (`kitchen-swaptest`, so the real kitchen never reuses a revision): r1 → r2 in place, `swaps=1`, rig pose and three door/appliance corner snaps identical before and after |
| 3 | Structure overlay; one corner lines up (X flip); tabletop | **PASS:** door corner snaps to (3.2570, 2.0320, 0.0300); 99 visible corners hit the mesh at median 0.0 mm; tabletop 1:50 still snaps | **PASS:** the overlay traces the cabinet edges (screenshot `SpikeData/kitchen-hinge-o29.png`); `CheckAlignment`: 309/400 corners visible, mesh hit vs corner median 5.4 mm, p90 25.3 mm, 92 % within 3 cm. Mirrored would be metres off |
| 4 | Tape a cabinet door corner to corner ×3 | **PASS:** door 914.0 / 914.5 / 914.0 mm, window 1500.0 ×3 (±1 cm aim noise) | **PARTIAL:** all 40 objects taped 3× from 0.6 m in front (±1 cm aim noise): **20/40 repeat within 5 mm**, and those land at median 1.6 mm from the object's own `w_m`. Doors: 9/18 within 5 mm (e.g. o29 262.5 / 262.6 / 262.5, o6 211.6 / 211.6 / 212.5, o27 264.9 ×3). The misses are one corner that never snaps; see P1 §B |
| 5 | Set scale from a known dimension | **PASS:** ×1/1.47 package, 1.0204 m → 150 cm → ×1.4700, tape 1.5000, restored on reload | **PASS:** the dishwasher (o32) tapes 408.8 mm; typing 24″ (609.6 mm, a standard dishwasher) gives **×1.491** (your altitude estimate says ≈1.47). After it, doors o5 / o29 tape 391 / 389 mm against `w_m` × 1.491 = 389 mm |
| 6 | Parts search by text and voice → placed, fit colours, sellers, offline receipt with label | **PASS (mock)** | **PASS with findings:** text "find me a cabinet door hinge" → `search_started` → 3 hinges, *"The first fits with 253 millimetres to spare"* (the app now sends `axis: "w"` for a 26 cm door tape). "select the first one" → loads after `asset.status` ready (tier `proxy`: Hunyuan3D returned 403) → placed on door o29 → **Green, fits 253 mm spare**. "show me the sellers, cheapest first" → `POST /parts/{id}/sellers` → Hinge Outlet $7.59 (recommended, "cheapest in stock, $7.59 with free delivery") and Hardware Tree. Checkout: 0.6 s hold sends nothing, 1.0 s hold sends one `/checkout` → `OFFLINE_RECEIPT`, label shown verbatim, notebook entry. **Voice:** a `say` WAV → transcript " Find me a cabinet door handle" → reply + `search_started`, MP3 reply (edge-tts) decoded and played. But: the agent never opens checkout by voice/text (P4 #10), and the handle search found nothing (P4 #15) |
| 7 | `/scene/ask` pin lands on the right object | **PASS:** pin ray through the box centre lands 0.00 mm from ground truth | **PARTIAL:** asked "What is this?" at the dishwasher: answers "kitchenette" and "The very best sink - Stainless steel sink 23''x14''", but **no `frame_id`/`box`** either time (P4 #12), so there is nothing to localise. The app now pins the answer on the spot that was asked about (3 mm from the target) |

## Snap policy, as shipped (R6 §7.3, unchanged numbers)

- **Depth gate:** max(3 cm, 2 % of the hit distance) from the mesh hit, or from the ray segment in front of it.
- **Corner:** 2.5 cm from the tip, or 1.5° of the ray.
- **Edge:** 2 cm, or 1.2°, as the closest point on the segment; midpoints are offered at corner priority. A corner
  beats an in-range edge only if d_corner ≤ d_edge + 1 cm.
- **Plane:** 3 cm, ray/plane intersection plus point-in-polygon (holes supported). The normal is the plane's, used by
  the level and part seating.
- **Otherwise:** the plain collision-mesh hit.
- **No mesh hit** (added for the kitchen; a documented deviation, R6 assumes a hit): the ray crosses a hole in the
  collision mesh or passes just outside the scan. The nearest structure plane the ray crosses inside its outline
  stands in for the hit; corners/edges within the angular tolerances along the ray win over it, and the plane hides
  the ones behind it (depth gate) only where the ray crosses it ≥ 3 cm inside its outline. Parts, the level and the
  fit checker's support probes use the plane alone. Kitchen tape results are identical with and without this branch.
- **Hysteresis:** 1.5×, depth gate included. Without the gate, a 3.75 cm hold could never survive a 3 cm gate.
- **Queries:** all in the content's local space, with tolerances in scene metres, so calibration and tabletop both
  work.
- **Axis lock:** the tape's second point locks within 3° of an axis or a nearby edge direction.
- **Snapping off:** a toggle in the Scene window.
- **Feedback:** a glyph and colour per kind, a highlight on the snapped edge or plane, and a haptic tick on acquire.

Note for tuning: at range the angular criteria dominate. At 4 m, 1.5° is about 10 cm laterally, capped by the 2 %
gate, so a ray grazing a ledge snaps to its front edge's midpoint. That follows the policy; the kitchen will show
whether it feels right.

## Contract findings: please confirm or fix

1. **Offline receipt `status` isn't documented.** Without Cybersource keys, `checkout.py` returns
   `"status": "OFFLINE_RECEIPT"` and `"approval_code": null`. api.md §4 only shows `AUTHORIZED`, and §7 only
   `mode`/`label`. The old app treated anything but `AUTHORIZED` as "Payment failed". It now accepts
   `mode == "offline"` / `OFFLINE_RECEIPT` as a recorded order and shows the label. Please list the `status` values in
   api.md.
2. **`qty` is listings, not units.** The server charges `price_usd × qty + shipping_usd` per listing. A listing can be a
   pack (`pack_qty`, e.g. "(50-Pack)"), but api.md's example (`qty: 10`, `unit_price_usd: 1.49`) reads like units. The
   app sends `qty = ⌈units / pack_qty⌉` and shows "8 needed → 1 × pack of 50". If you meant units, tell us and we'll
   send units.
3. **Revision numbers must never be reused.** Per §1a the app caches `*.r<rev>.*` on the headset forever. If a re-run
   publishes the same revision number with new content, headsets keep the old files. (We hit this with our own
   regenerated fixtures.) Please keep revisions monotonic across re-runs of a site.
4. **`recommended_spawn.pos` is the eye point** (lowest mesh point + 1.6 in package units, `pipeline/package.py`).
   api.md doesn't say so; the app puts the feet 1.6 below it. On a mis-scaled capture that is 1.6 × the scale error in
   real metres until the scale is corrected. Suggest documenting it, or adding `"floor_y"`.
5. **Old packages in `/scenes`.** `strasbourg-cathedral-spire` lists as `revision 1 / full` although it has no
   `mesh.file`. The app hides it by name, and `SceneManifest.Parse` rejects any package without `mesh.file`. Suggest
   `/scenes` skip, or flag, packages that fail `validate_package`.
6. **`scene_pin` carries no text.** It has `{frame_id, box}` only. The app labels the pin with the agent's `reply`, and
   a `label` or `answer` arg would be cleaner.
7. **Object rectangles: two forms.** api.md says `corners3d`; schema.md's example uses `rect` in the plane's (u, v).
   The app reads both. Note that `rect`/2D-polygon (u, v) coordinates are in the file's frame (v = n × u). After the X
   flip, Unity's v is −(n × u), and the app accounts for it.
8. **Parts need internet on the Quest.** Candidates' `image_url` points at retailer CDNs. The app tries
   `/parts/{id}/image.jpg` first and falls back to `image_url`. That's fine on the hotspot; on the USB tunnel there's
   no internet, so photos stay blank until `image.jpg` exists.
9. **The offline catalog can't be bought on the real server.** The app's fallback parts (`hidden-hanger-5k`,
   `window-ac-small`) aren't in your `data/`, so `POST /checkout` would 404 for them. This is expected (they only show
   when the laptop is unreachable), but the M5 harness uses them, so run it against the mock.

10. **The agent never opens checkout.** With a part selected and placed (context:
    `{"measurement":{"label":"tape #1","value_m":0.2625,"axis":"w"},"selected_part_id":"pivot-door-hinges-…-1e8608",
    "placed":[{"part_id":"pivot-door-hinges-…-1e8608","count":1}],"candidate_ids":[…3 ids…],"tool":"part"}`),
    `POST /agent/command` with `"buy it from the recommended seller"` and with `"check out with the recommended seller"`
    both returned `{"reply":"Searching the supply shops.","actions":[{"type":"search_started",…}]}`. There's no
    fast-path intent for checkout, and `find_part`'s description ("Find a real part to **buy**") attracts these.
    Suggest a fast path (`check ?out|buy (it|this|that)|pay|purchase` → `start_checkout` with the selected part's
    `recommended_seller`) and dropping "to buy" from `find_part`. The app side is fine: tapping Buy opens the panel.
11. **"Fit … unknown" for a length run.** With `axis: "length"`, `compute_fit` returns `fits` with `spare_mm: null`, and
    `jobs.py`'s summary falls through to *"Fit against the measurement is unknown."* Suggest "It fits the run." The app
    used to send `"length"` for every tape (so the server's size check never ran); it now sends `h` for a vertical
    tape, `w` for a horizontal one up to 1.5 m, and `length` for longer runs. Tell us if you'd rather choose.
12. **`/scene/ask` doesn't localise, and rate-limits as a 500.** `qwen/qwen3.8-27b` answered in 22 and 9 completion
    tokens with no `frame_id`/`box` (request: "What is this?" + frames 0316, 0321, 0311, the photos nearest the
    dishwasher). A second call within the minute failed: Groq 429, output tokens per minute limit 1000, requested 1707,
    returned to the headset as **HTTP 500**. Suggest `max_tokens` ≈ 400 for vision, a prompt that insists on the box,
    and 429 → 503 with `Retry-After` (the app already shows 503 as "needs the live model").
13. **Groq TTS is refused.** `canopylabs/orpheus-v1-english` needs the org admin to accept its terms in the Groq
    console; `voice.py` falls back to edge-tts (`audio/mpeg`), which the app decodes and plays. Fine for the demo.
14. **AI meshes: Hunyuan3D-2 returns 403 Forbidden**, so every part so far is tier `proxy` (exact-size box, badged).
    Pre-warm the demo parts while the quota lasts.
15. **Cabinet pulls return nothing.** Voice "find me a cabinet door handle" → 5 listings, all dropped by `find_parts`:
    "no dims from LLM, Home Depot, or page", e.g. *"European Style 3 in. (76 mm) Center-to-Center Satin Nickel Bar
    Cabinet Pull (25-Pack)"* → reply "No parts found that fit". The size is in the title (76 mm centre-to-centre).

## P1 findings (the kitchen package)

- **§A. The collision mesh has holes where the visual mesh is closed.** On 7 of the 40 object centres (o2, o19, o24,
  o26, o29, o35, o39) a ray along the object's plane normal (±30 cm) misses `collision.r1.glb` from both sides,
  while the visual mesh has a surface within 0–19 mm of the centre on five of them. Parts placed on door o29 fell
  through ("no surface under the pointer") until the app fell back to structure planes. Please check the 200k → 50k
  decimation (hole filling / keep closed surfaces).
- **§B. Some object rectangles' corners don't snap.** In the misses, one corner always resolves to a mesh face
  200–314 mm away (o9, o11, o37) or to a different corner, from every aim. Probing each rectangle corner from 0.6 m
  in front: the corners of o9, o10, o36 and o37 sit 9–62 cm **behind** the scanned surface, and three corners each of
  o3 and o7 hang ~24 cm off it, while o5 and o29 sit within 5 mm. The depth gate rightly refuses those corners (they
  aren't where the photos' surface is), which is why their tapes don't repeat. Please check S4's rectangle depth /
  plane assignment for those objects.
- **§C. Scale:** the dishwasher (24″ standard) gives ×1.491 against your altitude estimate's ≈1.47×.

## App-side changes that affect you

- Default base URL is **`http://<laptop>:8000`**. On the Quest the default is `http://127.0.0.1:8000` over
  `adb reverse tcp:8000 tcp:8000`. Override without rebuilding:
  - `adb shell am start -n com.airtools.quest/com.unity3d.player.UnityPlayerGameActivity -e server http://<ip>:8000 -e site kitchen`,
  - or push a `server.txt` (URL, then site) to `/sdcard/Android/data/com.airtools.quest/files/`.
- **The app auto-loads site `kitchen` at start**, if the server has it. Otherwise the built-in facade stays. Scene
  window ▸ *Next scene* cycles through `/scenes`.
- **`session_id`** is `quest-<10 hex>`, one per app run. It's sent with agent, voice and checkout calls, and with
  `POST /notebook`, which now uploads `{session_id, entries[]}` and only entries not yet sent.
- **Every agent call carries the context object:**
  - `measurement` `{label: "tape #n", value_m, axis}` from the latest two-point tape, `axis` = `h` (vertical), `w`
    (horizontal ≤ 1.5 m) or `length` (longer runs);
  - `selected_part_id`, `candidate_ids`, `placed` `[{part_id, count}]`, `tool`;
  - for voice, `frames`: the 3 nearest package thumbs as base64 JPEG. They're only ~15–60 KB each, but tell us if
    Groq's budget prefers fewer.

## Still open

- **P4:** #10 (checkout intent), #12 (vision box + 429), #15 (pull dims). **P1:** §A (collision holes), §B.
- **[H] on the headset, still to do by hand:** hold-to-talk (Scene window and Find parts), "What is this?" pins, the
  Scene window keypad, and the fixes below.

## Headset session (Fri 09-25 late, real kitchen over the USB tunnel)

- The kitchen loaded in 1.0 s at 72 fps; palm tools, the parts search, measuring (a 0.42 m tape across the
  dishwasher, area and angle readings, corner glyphs) all worked once close to the cabinets.
- Found and fixed on the app side:
  - **Spawn 4 m back**: the pipeline spawns 3 package-metres outside the bounding box (right for a facade, a
    floating diorama indoors at the kitchen's altitude scale). The app now steps in along the view until the scene
    spans ~90° (half its width from the near face, ≥ 1.2 m, never backward): 2.5 m closer in the kitchen, the facade
    unchanged. Suggest P1 do the same in `recommended_spawn` for indoor scans.
  - **Moving with hands only**: the Move tool existed but wasn't discoverable; a scanned scene now opens with
    "Left palm ▸ Move: pinch the floor to walk there".
  - **Parts presets**: six chips (cabinet hinge, drawer slide, shelf bracket, gutter hanger, window AC, cabinet knob,
    each checked for results on the real server) and a Hold to talk button in Find parts for anything else.
  - **Palm menu flicker**: with the right hand over the open left palm the headset loses the left fingers and the
    menu closed and reopened 8 times in 17 s. It now holds still and open while the other hand is on it (0.6 s grace)
    and through 0.8 s tracking drop-outs.
  - **Part placement on the scan**: the preview jittered between the scan's many small planes and raw mesh bits
    (normals swinging 37–90° at a door's edge) and nearly every placement read red against ±2 cm scan bumps. Now:
    the part stays on its plane until the pointer clearly leaves it (4 cm seat margin, also when the ray misses the
    mesh), raw scan hits seat on the nearest plane within 4 cm or an axis-snapped normal, the preview glides, scan
    contacts shallower than 2 cm don't count, and the per-frame brute-force nearest-triangle search on the 50k-tri
    collider (≈1 MB garbage a frame) is gone. A hinge placed on 7 doors/panels/appliances reads Green; a 305 mm
    bracket jammed into the toe-kick still reads Red.
  - **Placement guides**: while a part is in hand over a scan, the fitted edges within 35 cm are drawn around it,
    its true-size box is outlined in its fit colour, and the edges it cuts and the scan contacts it goes into are red.
