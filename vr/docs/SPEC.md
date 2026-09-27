# AirTool — XR build spec (the Unity app)

*Rev 1, Thu 2026-09-24. Scope: everything that runs on the Quest (roles P2 engine + P3 tools in the implementation plan).
Background, pitch and the full-team plan: `docs/ref/writeup.md` (take design choices loosely) and `docs/ref/implementation-plan.md` (more rigid).
When this file and the reference docs disagree, **this file wins**. When the team makes a new call, record it in §9 Decisions log.*

---

## 0. How to use this spec (agents read this first)

- Work milestone by milestone (§6). A milestone is **done** only when every acceptance check in it passes, and you have written the result in §10 Progress log.
- Checks are marked:
  - **[A]** you (the agent) verify it yourself, using the Unity CLI, EditMode/PlayMode tests and XR Operator in the Simulator
  - **[H]** a human must verify it on the Quest 3S headset (hand feel, performance, passthrough). List every open [H] check in your final message so the human can run them.
- Build against the **synthetic facade** (§4) first. It has exact, known dimensions, so every measuring and fit check has a ground truth. Real drone scenes arrive Saturday and replace it through the same loader.
- Build against the **mock parts server** (§5.3) first. The real parts server (P4, Python) implements the same contract later, and swapping to it is a URL change.
- Make every interaction work with **controllers as well as hands** (Interaction SDK supports both). The Simulator and XR Operator drive controllers reliably, which makes everything agent-testable. Hand-specific feel is an [H] check.
- Keep to the conventions in `CLAUDE.md` (folders, asmdefs, the "Do NOT" list).

## 1. Hackathon rule check (resolved)

The team decided (Thu 09-24) that HackGT does not restrict pre-written code, so there's no need to wait for Fri 8 PM. The useful parts of M0 are already on `main` (glTFast, Android manifest, package ID); the spike code stays on `spike/m0` as reference. Work continues straight into M1.

## 2. What we're building (XR side, one paragraph)

The judge starts in passthrough with a ship's chest on the real table. Opening it swaps the room for a drone-captured building at 1:1 (a textured mesh for looks — Gaussian splats were cut after M0, see §9 — and an invisible collider mesh for snapping). A toolbox on the hip gives a **point-measure** tool (mark 2 points for a distance; 3–4 points for the shape's corner angles, side lengths and area), a **level**, and a **notebook** that logs every reading with its source photo. The chest is also a parts store. Voice or the crate menu finds a real part, and the part lands in your hand at true size. You press it onto the building, where it snaps and shows a green/amber/red fit outline. You array it along the tape run, scroll real sellers, and pay with a 1-second pinch-hold (Visa sandbox, run on the server) or open the seller's page. Closing the chest brings you back to passthrough with the bought part on the real table.

**Must ship:** passthrough ↔ world transition, scene at 1:1, point measure (distance + 3/4-point angles/sides/area), level, notebook, one cached part placed and fit-checked.
**Should ship:** voice, array, seller carousel, checkout, take-it-home, tabletop scale.
**Stretch:** plumb, ask-the-scene pins, "what else do I need?".

## 3. Architecture (Unity)

### 3.1 Folders and assemblies
```
Assets/AirTools/
  Runtime/            AirTools.Runtime.asmdef   (refs: Meta ISDK, Oculus.VR, glTFast, TextMeshPro, Unity.InputSystem)
    Core/             AppState, Units, Log, Services (tiny service locator)
    Scene/            ScenePackage (SO), SceneLoader, SceneRoot, SnapService
    Input/            ToolInput (pinch/trigger abstraction over hands + controllers)
    Tools/            Toolbox, MeasureTool + MeasureMath (2–4 points), LevelTool, (Plumb)
    Notebook/         Notebook, NotebookEntry, NotebookView, Exporter
    Parts/            PartsClient, PartInstance, PartLoader, FitChecker, ArrayPlacer, SpecCard, SellerCarousel, CheckoutPanel
    Voice/            VoiceClient, VoiceRouter, AppCommands
  Editor/             AirTools.Editor.asmdef    (SyntheticFacadeBuilder, menu items, build helpers)
  Tests/EditMode/     AirTools.Tests.EditMode   (pure math: units, snapping, fit, array, notebook)
  Tests/PlayMode/     AirTools.Tests.PlayMode
  Scenes/             Main.unity (the only shipping scene), Sandbox_*.unity (per-lane scratch scenes)
  Prefabs/  Materials/  Fixtures/ (synthetic facade + mock part data)
tools/mock_parts_server.py      (§5.3)
```
One shipping scene (`Main.unity`). Each lane edits **its own prefabs** and a `Sandbox_<lane>.unity`, and wires into `Main.unity` only at merge points. This follows the plan's rule to avoid scene-file merges.

### 3.2 Core contracts (agree these first; lanes code against them)
```csharp
// App state (writeup §3 state diagram)
public enum AppMode { Passthrough, World, Tabletop }
public static class AppState { AppMode Mode; event Action<AppMode,AppMode> Changed; void Set(AppMode m); }

// One input abstraction for all tools. Hands: index pinch. Controllers: trigger. Ray or tip position.
public interface IToolInput {
  event Action<ToolHand, Pose> PressStart, PressMove, PressEnd;   // Pose = tip/ray-hit position + orientation
  Pose GetPointer(ToolHand hand);
}

// Snapping against the scene mesh (MeshCollider on layer "SceneSurface")
public static class SnapService {
  bool TrySnap(Vector3 p, out SurfaceHit hit, float radius = 0.15f);          // nearest surface point + normal
  bool TryRaySnap(Ray r, out SurfaceHit hit, float maxDist = 20f);
}
public struct SurfaceHit { public Vector3 point, normal; public Collider collider; }

// Every tool result goes to the notebook
public record NotebookEntry(string Tool, double ValueSI, string Unit, Vector3[] Points,
                            DateTime Time, int NearestCameraId, string Label);
public static class Notebook { IReadOnlyList<NotebookEntry> Entries; event Action<NotebookEntry> Added; void Add(NotebookEntry e); }

// Voice and UI call the SAME methods (plan §3 "voice never has its own code path")
public static class AppCommands {
  void EquipTool(string tool); void FindPart(string query); void SelectCandidate(int i);
  void SetFinish(string name); void PlaceArray(float? spacingMm); void ShowSellers(string sort);
  void StartCheckout(int sellerIndex);   // opens the panel only; payment needs the physical hold
  void AddNote(string text); void OpenChest(); void CloseChest();
}
```
Units: SI (metres) internally, everywhere. Display metric first, then ft-in (`Units.Format(double metres)` → `"4.20 m · 13′ 9⅜″"`).

### 3.3 Scene package (contract §1 of the implementation plan, unchanged)
`scene/<site>/ scene.json, splat.ply|spz (optional, unused by the app since M0), mesh.glb, cameras.json, thumbs/NNNN.jpg, path.json`. In Unity, `ScenePackage` (a ScriptableObject) references the imported splat asset, the mesh prefab, the camera list and the thumbnails. `SceneLoader` applies `up`/`north`, puts the mesh on layer `SceneSurface` with an invisible material, and places the rig at `recommended_spawn`. The synthetic facade is just another `ScenePackage`.

### 3.4 Parts runtime (contract §1b, unchanged)
`GET {base}/parts/<id>/part.json | model.glb | image.jpg`, `POST {base}/parts/search`, `GET {base}/parts/jobs/<id>`, `POST {base}/checkout`, `POST {base}/voice/token`. Load models with glTFast (`GltfImport.Load(url)`). `PartInstance`: BoxCollider sized from `dims_mm`, kinematic Rigidbody, ISDK Grabbable with a **one-hand transformer only** (scale locked at 1:1). `base` is a setting: `http://localhost:8765` in the Editor, `http://<laptop-LAN-IP>:8765` on device.

## 4. The synthetic facade (ground-truth scene, built by an editor script)

`AirTools/Editor/SyntheticFacadeBuilder.cs` → menu **AirTools ▸ Build Synthetic Facade**, which writes `Fixtures/SyntheticFacade/` (mesh prefab + ScenePackage). It's procedural, so it can be regenerated and every number is exact:
- Ground plane, and a brick-textured wall 8.00 m wide × 6.50 m tall in the XY plane at z = 0, facing +Z (towards the spawn).
- **Window opening** 1.500 m wide × 1.200 m tall, sill at y = 3.500 m, centred at x = 0; recessed 0.10 m; the sill is a box 1.60 × 0.05 × 0.15 m.
- **Fascia board** along the top: 4.200 m long from x = −2.10 to +2.10, y = 6.00–6.20 m, 0.025 m proud of the wall.
- **Gutter**: a 127 mm (5″) K-style proxy along the fascia, same 4.200 m run.
- **Door** (scale reference, like the real capture): 0.914 × 2.032 m at ground level, x = +2.80.
- One tilted surface for the level: a 2 m ledge at y = 1.0 m, pitched **2.0°** about X.
- Spawn: rig at (0, 0, 4.0) looking at the window; `cameras.json` = 12 synthetic cameras on an arc, with thumbnails rendered from them (so the notebook evidence-photo feature works before the real capture).

Ground truth, used by the acceptance tests: window width **1.500**, window height **1.200**, gutter run **4.200**, door **0.914 × 2.032**, ledge tilt **2.0°**, sill height **3.500**.

## 5. Lanes (how to split local agents)

The Unity Editor, Play mode, the Simulator and XR Operator are **one shared resource per project folder**. Two agents both recompiling or entering Play mode in the same Editor will break each other's runs.

| Lane | Agent | Works in | Owns | Uses Unity? |
|---|---|---|---|---|
| **XR** (P2+P3) | Agent 1, the only one that drives the Editor, Play mode and the Operator | `~/AirTools` | Everything in §6 | Yes, exclusively |
| **Logic** (optional, for speed) | Agent 2 | a `git worktree` at `~/AirTools-logic` on branch `lane/logic`, with its **own Unity Editor instance** (open the worktree as a separate project; it has its own `Library/`) | Pure C# with no scene work: `Units`, `SnapService` math, `FitChecker`, `ArrayPlacer`, `Notebook` + `Exporter`, `PartsClient` (HTTP + JSON), and their EditMode tests | Only `recompile` + `run_tests` (EditMode). **Never Play mode or Operator** |
| **Server** (P4) | Agent 3 (or a teammate) | `server/` in the team repo | FastAPI parts server per contract §1b; starts by serving the §5.3 fixtures unchanged | No |

The XR lane merges `lane/logic` at each milestone boundary (`git merge`, then recompile, then run all tests). Only the XR lane edits `.unity`/`.prefab` files.

### 5.3 Mock parts server (build first, in M3)
`tools/mock_parts_server.py`: Python standard library only, port 8765. It serves `Assets/AirTools/Fixtures/Parts/<id>/{part.json, model.glb, image.jpg}`.
- `POST /parts/search` returns a job, and `GET /parts/jobs/<id>` reports `done` with the fixture candidates after 1.5 s.
- `POST /checkout` returns a fake `AUTHORIZED` receipt with `"sandbox": "mock"`.
- `POST /voice/token` returns 501 until P4 provides the real one.

Fixtures to ship:
1. `hidden-hanger-5k`: 127 × 38 × 45 mm, white, `spacing_mm` 600, mount face −z, 3 sellers.
2. `window-ac-small`: body 470 × 380 × 300 mm, `min_window_width_mm` 590, `max_window_width_mm` 1000, clearance top 25 mm, 3 sellers. Against the 1.500 m synthetic window it **won't fit**: the width exceeds its max, so it reads red or amber (this is the "too wide" beat).

GLBs are exact-size proxy boxes, tier `proxy`, generated by the editor script or `trimesh`.

## 6. Milestones and acceptance checks

### M0: Feasibility spikes (allowed before Fri 8 PM; throwaway branch `spike/m0`)
Goal: remove the risks that could force a redesign. Timebox: 3 h total.
- **Splats in Unity 6.3 + URP 17.** `aras-p/UnityGaussianSplatting` is maintenance-only, tested on Unity 2022.3, and states it is "not tested on mobile". The `ninjamode` VR fork is an "unmaintained demo", Quest 3 at 72 fps to ~400k. URP 17 uses Render Graph, which older integrations may not support.
  - [A] Import the package (use the upstream if its VR support is present, else the fork), create a splat asset from any sample `.ply`, and see it in Play mode in the Simulator. Stereo must be correct: `openxr_capture_composited_image` shows it in both eyes, with no doubled image.
  - [H] Build the APK and read the fps on the 3S at 200k and 400k splats.
  - If Render Graph breaks it, try URP's compatibility mode. If it's still broken, **fall back to mesh-only visuals** (plan ladder #4): the textured Poisson mesh visible, splat skipped. Record the decision in §9.
- **glTFast runtime load.** [A] Add `com.unity.cloud.gltfast`, load a GLB from `http://localhost:8765` in Play mode. [H] Load the same file from the Mac's LAN IP on the Quest.
- **Cleartext HTTP on Android.** Android blocks `http://` by default. Add a network security config that allows the LAN (or `usesCleartextTraffic`) through a custom AndroidManifest. [H] Confirm the Quest reaches the laptop.
- **Mic in Editor + device.** [A] `Microphone.Start` records 1 s at 24 kHz in the Editor. [H] Same on the Quest (RECORD_AUDIO permission prompt).
- **Operator drive test.** [A] Enter Play mode, move the right controller with `openxr_set_controller_pose`, press the trigger, and read the pose back. This confirms the agent loop works for everything that follows.

### M1: Rig, scene, state (hack start)
- Building Blocks in `Main.unity`: Camera Rig, Passthrough, Hand Tracking, Controller Tracking, ISDK Poke/Grab/Ray interactions.
- `ScenePackage`, `SceneLoader`, `SyntheticFacadeBuilder`; `AppState` with a chest object that toggles Passthrough ↔ World through a 1 s fade (passthrough layer off, scene root on, skybox).
- **Accept:**
  - [A] Play mode starts in `Passthrough`. Poking the chest (controller poke via Operator) gives `World` within 1.5 s, and a screenshot shows the facade in front of the camera at spawn.
  - [A] `get_world_pose` on the window centre is (0, 4.10, ±0.1) ± 1 mm, so scale is 1:1.
  - [A] Zero Console errors during a 30 s Play session.
  - [H] Passthrough visible on the 3S; chest opens by hand poke; no judder.

### M2: Toolbox + point measure (the core beat)
Design change (Thu 09-24, §9): no rendered tape or protractor. You **mark points**, and the measurement graphic appears instantly between them.
- `ToolInput` (hand pinch / controller trigger, ray or tip), `Toolbox` hip tray (centre-eye −0.25 m Y, +0.30 m right, yaw-follow only).
- `MeasureTool`: each press places a point, snapped to `SceneSurface` (≤ 0.15 m). A live preview segment follows the pointer from the last point.
  - **2 points** → a line with its length label (`Units.Format`).
  - **3 points** → a closed triangle; each side labelled with its length, each corner with its angle, the area at the centre.
  - **4 points** → the same for the quadrilateral, in placement order. Reflex corners are allowed; the area is on the best-fit plane, and the tool warns when points are > 1 cm off-plane.
  - "Done" (press the last point again / secondary button) finishes the shape; each finished shape is one notebook entry. Placed points can be grabbed and dragged, with live update.
  - Maths in `MeasureMath` (double precision, Newell area, planarity error). SI values in scene-root local space, so readings don't change at tabletop scale.
- **Accept:**
  - [A] EditMode: `Units.Format(4.2)` → `"4.20 m · 13′ 9⅜″"`; `MeasureMath` 3-4-5 triangle (sides, angles 53.13/90/36.87, area 6), window rectangle (1.8 m², 4 × 90°), arrowhead reflex corner, non-planar quad error; snapping returns the nearest point within 0.15 m and nothing beyond it.
  - [A] Operator script: mark the left and right window jambs (points from `get_world_pose`, ±3 cm noise) → notebook shows **1.500 m ± 0.010**. Gutter ends → **4.200 ± 0.015**. The four window corners → **1.80 m² ± 0.03**, four angles **90° ± 1°**.
  - [A] Screenshot shows the lines and readable labels facing the camera.
  - [H] Marking points by hand pinch feels right; labels are readable at 2 m.

### M3: Level + notebook + mock server
- `LevelTool`: place on a surface; tilt from `Vector3.up` using the surface normal; bubble visual; label in degrees.
- `Notebook`: wrist canvas listing entries with the nearest-camera thumbnail; poke a row to highlight its points; **Export** writes CSV + HTML to `persistentDataPath` and `POST /notebook` if the server is up.
- `tools/mock_parts_server.py` + fixtures (§5.3).
- **Accept:**
  - [A] Level on the ledge reads **2.0° ± 0.2**; on the sill, **0.0° ± 0.2**.
  - [A] After M2+M3 checks, the notebook has ≥3 entries, each with a thumbnail id whose camera frustum contains the points.
  - [A] Export files exist and parse.
  - [A] `curl localhost:8765/parts/hidden-hanger-5k/part.json` is valid JSON matching the contract.

### M4: Parts in hand (checkpoint beat, "a cached part placed and fit-checked")
- Crate menu (poke) → `PartsClient.Search` → candidate cards fan out (photo, name, W×D×H, price).
- Poke a card → `PartLoader` (glTFast) → `PartInstance` in the hand.
- Release within 15 cm of the surface → snap: rotate so `mount.face` faces into the normal, seat it on the surface, play a click, add a haptic tick.
- `FitChecker`: `OverlapBox` against `SceneSurface` + other parts; again inflated by `clearance_mm` → **green / amber / red** outline; W×D×H callouts; "fits, N mm spare / too wide by N mm" when a tape reading exists.
- `SpecCard` beside the selected part: dims mm + in, material, finish swatches (recolour), tier badge, citations.
- **Accept:**
  - [A] Operator: open crate → select hanger → move the controller to the fascia (from `get_world_pose`) → release. The part's back face is within 2 mm of the fascia plane, and the outline is **green**.
  - [A] AC unit into the window gives **red or amber**, and the callout states the width mismatch against the 1.500 m tape reading.
  - [A] `PartInstance` bounds equal `dims_mm` ± 1 mm (true size).
  - [A] Swatch "brown" recolours the proxy material to `#5A3E2B`.
  - [H] Grab/release by hand; the part feels right-sized.

### M5: Array + sellers + checkout
- `ArrayPlacer` along the last tape segment at `spacing_mm`, each instance snapped; qty = ⌊L/s⌋+1. For 4.200 m at 600 mm that is **8**.
- `SellerCarousel` (world-space ISDK canvas: ray + pinch-drag scroll, poke), sort by price/ETA, recommended-seller tag, **Pay with Visa** / **Open at seller** (`Application.OpenURL`).
- `CheckoutPanel`: qty, unit, total, •••• 1111; **hold 1.0 s** with a filling ring → `POST /checkout` → receipt card + chime → notebook entry. **No code path lets voice complete a payment.**
- **Accept:**
  - [A] Array along the 4.200 m gutter tape gives 8 instances, all green, spacing 600 ± 2 mm.
  - [A] Holding for 0.6 s then releasing sends no request (check the mock server log); a full 1.0 s hold sends exactly one request, shows the receipt and adds a notebook entry.
  - [A] EditMode test: `AppCommands.StartCheckout` only opens the panel.
  - [H] Carousel scrolls smoothly by hand.

### M6: Voice (needs P4's `/voice/token`; build against a stub until then)
- `VoiceClient`: push-to-talk (left-palm pinch hold / controller X), `Microphone` PCM16 24 kHz → NativeWebSocket → xAI realtime with the ephemeral token; stream `response.output_audio.delta` to an `AudioSource` on the toolbox.
- `VoiceRouter`: function calls map 1:1 to `AppCommands`; reply with `function_call_output` + `response.create`. Show the recognised command as text on the wrist before it runs.
- **Plan B flag:** record → `POST /voice/stt` (via laptop) → chat with the same tools.
- **Accept:**
  - [A] EditMode: every tool in the plan's list maps to an `AppCommands` method; unknown calls are refused with a spoken error.
  - [A] With a stub websocket server replaying a recorded `find_part` event, the candidate cards appear.
  - [H] Real voice round trip on the Quest in < 3 s for `equip_tool`.

### M7: Real scene + take it home + tabletop
- Import the practice/real package (**textured mesh visuals**, per the M0 decision) → new `ScenePackage`. The tools must work unchanged.
- "Take it home": on close-chest, purchased `PartInstance`s re-parent to a passthrough anchor on the table, at true size.
- Tabletop: two-hand scale of `SceneRoot` (ISDK `TwoGrabFreeTransformer`), clamped to 1:1 or 1:50 with a vignette during the change; tools measure in **scene** units, so readings are unchanged at 1:50.
- **Accept:**
  - [A] Tape across the real window matches the hand-measured value within the package's `scale_residual_m` + 1 cm.
  - [A] At 1:50 the tape still reads the same metres.
  - [H] 72 fps sustained on the 3S with the real scene (OVR Metrics); take-it-home part sits on the real table.

### M8: Stretch (only after the Sat 6 PM checkpoint passes)
Plumb, ask-the-scene pins, BOM ("what else do I need?"). Each needs one [A] ground-truth check on the synthetic facade.

## 7. Agent verification recipes (XR Operator)
- **Always:** exit Play mode before editing; after edits `unity command recompile` → poll `recompile_status` → `unity command console` must show 0 errors → then Play.
- **Find a target:** `get_world_pose("SceneRoot/Facade/Window")` → convert Unity (left-handed, +Z fwd) to OpenXR (right-handed, −Z fwd), per the `meta-xr-operator-coordinates` skill → `openxr_set_controller_pose(right, pos, duration=0.3)`.
- **Press:** `openxr_set_controller_input(right, Trigger, 1)`, then wait ≥ 3 frames (~50 ms) before setting 0; inputs that flip within one frame are missed.
- **Read results from data, not pixels:** expose `AirTools.Debug.LastReading` / `Notebook.Entries` so they can be read through Operator scene queries or `unity command eval`. Take a composited screenshot as secondary evidence.
- **Log line per check:** `[AirTools.Check] M2.tape.window value=1.5003 expected=1.500 tol=0.010 PASS`, so the human can grep the Console.

## 8. Cut order and fallbacks (from the plan §8, adapted)
At the Sat 6 PM checkpoint, if behind, cut in this order: array → carousel (keep **Open at seller**) → voice (keep the crate menu) → the whole parts layer. **Point measure + notebook are never cut.** If splats fail on the Quest: mesh-only visuals (ladder #4). If standalone fps fails: Quest Link from the RTX laptop (Windows build; Link does not run on the Mac).

## 9. Decisions log
| When | Decision | Why |
|---|---|---|
| Thu 09-24 | Unity 6.3 (6000.3.24f1), Meta XR SDK 205, OpenXR 1.18, URP 17.3, Android target; versions frozen | Verified working with Simulator + XR Operator; plan says never upgrade mid-event |
| Thu 09-24 | Dev loop = Mac Editor + Meta XR Simulator + XR Operator; device checks are [H] | Makes the loop agent-verifiable; perf/hands/passthrough need the real 3S |
| Thu 09-24 | Build against a procedural **synthetic facade** with exact ground truth before any capture exists | Every tool gets an automatic accuracy test; the real package drops in via the same loader |
| Thu 09-24 | Controllers are a first-class input alongside hands | Operator drives controllers deterministically; also the plan's hands-flaky fallback |
| Thu 09-24 | **Mock parts server** in `tools/` matching contract §1b | App lanes aren't blocked on P4; swap = URL change |
| Thu 09-24 | Splat renderer undecided until the M0 spike; mesh-only visuals is the pre-agreed fallback | Upstream is maintenance-only, untested on Unity 6 / URP Render Graph / mobile |
| Thu 09-24 | Unity project lives at repo root `~/AirTools` (plan's `app/` folder = this project) | Already set up; moving it risks the working config |
| Thu 09-24 (M0) | **Splats: upstream `aras-p/UnityGaussianSplatting` v1.1.1, pinned `#2c6fed37`** (git URL `?path=/package`). No fork: upstream already has the ninjamode VR PR (Nov 2024) and URP Render Graph support (Dec 2024). | Imports, compiles, renders in Unity 6.3 / URP 17.3 Render Graph with 0 errors; asset creation scriptable (no dialogs) |
| Thu 09-24 (M0) | ~~Splats require OpenXR stereo mode = Multi-pass (Desktop + Android), pending the [H] fps check~~ → superseded by the next row. | In Single Pass Instanced the splats are **invisible in both eyes** (shaders have no stereo instancing; view data is computed once per camera in a compute shader). In Multi-pass they render in both eyes with correct disparity. The ninjamode fork's own project also uses Multi-pass. Render Graph is not the problem, so URP compatibility mode was not needed (and upstream has no non-RG path). Cost: every draw is issued twice on CPU, and splat view-data + sort run per eye. |
| Thu 09-24 (M0, after [H]) | **Splats are cut from the shipping path. Visuals are mesh-only (ladder #4), and OpenXR stays Single Pass Instanced** (Desktop + Android). Splat code and package live on `spike/m0` only. Possible stretch: a ≤100k-splat tabletop "hero" view, which would need Multi-pass | Quest 3S, Multi-pass, VrApi logcat: **50k = 72 fps** (GPU 10.6 ms, 84%), **200k = 60 fps** (14.5 ms), **400k = 36 fps** (20.4 ms). A real capture needs far more than 200k to look good, and Multi-pass doubles the cost of every other draw |
| Thu 09-24 (M2 design) | **Point measure replaces the rendered tape and protractor.** 2 points = distance; 3–4 points = corner angles + side lengths + area, drawn instantly between the points | The team's call: faster to use and read than dragging virtual instruments. The area tool moves from stretch into M2 |
| Thu 09-24 (M4 design) | **Part models are shown at real scale from the listing:** `PartLoader` scales each generated GLB so its bounds match `part.json` `dims_mm`; `FitChecker` uses `dims_mm` | Generated models (Fedora pipeline) can come out at any scale; the product listing's dimensions are the ground truth |
| Thu 09-24 (M1) | **No artificial locomotion (room-scale only).** The Interactions Rig's `Locomotor` (ISDK `FirstPersonLocomotor`) is deactivated by `MainSceneBuilder` | It owns the rig pose and reset the spawn (yaw 180 → 0) on the first tracked frame, and it wants a ground collider, which is hidden in passthrough. Re-enable only with teleport targets designed for it |
| Thu 09-24 (M1) | **The chest is a child of the rig root** (55 cm ahead, table height 0.72 m) with the Poke building block's button on its front | It follows spawn placement in both modes, so you can always close it. Placing it on the real table (MRUK) is a later polish item |
| Thu 09-24 (M1) | `ModeController` is a tickable state machine (fade 0.35 s → swap → hold 0.2 s → fade 0.35 s) instead of a coroutine | EditMode tests step it deterministically without entering Play mode (Play mode = XR + Simulator = slow and flaky) |
| Thu 09-24 (M2) | **Snapping = feature snap within the surface you hit:** corners ≤ 6 cm, else edges ≤ 4 cm, including inside corners and edges where other surfaces meet the hit face. Mesh colliders snap to hit-triangle vertices for now | Makes readings independent of ±3 cm aim noise. Randomised ground-truth runs: 1500/1500 within tolerance. Edge 3 → 4 cm came from test failures on gutter ends and sill corners |
| Thu 09-24 (M2) | **Measure input semantics:** a click (press + release, < 1.5 cm drag) places a point; press on an existing point and drag moves it; clicking the last point again, B/Y, or toolbox DONE finishes; a 4th point auto-finishes; A/X or UNDO removes the last point, then the last shape | One trigger/pinch covers place, finish and edit; buttons are shortcuts. Voice uses `AppCommands.EquipTool` |
| Thu 09-24 (M2) | Labels float 10 cm toward the viewer and keep a constant angular size (3.5 cm tall at 2 m, growing with distance). Lines and markers widen with distance | Recessed features (window jambs, sills) hid labels drawn on the surface; fixed-size lines vanished at 7 m |
| Thu 09-24 (M2) | **One MonoBehaviour per file, named after the class** (enforced by an EditMode test) | Components in mismatched files work when added from code but become "missing script" when the scene reloads. Hit twice (M0 probes, M2 toolbox) |
| Fri 09-25 (M3) | Level reads **Level** (slope from horizontal) for surfaces within 45° of flat and **Plumb** (deviation from vertical) for steeper ones; normal averaged over an 8-ray, 3 cm ring so noisy capture meshes read steadily. Readings are stored as degrees (Unit "°") | Walls need plumb, ledges and sills need level; one tool covers both |
| Fri 09-25 (M3) | Notebook = static store + `NotebookController` (highlight, export, upload, notes) + a wrist `NotebookPanel` built from ISDK poke buttons and TextMesh, not UGUI | Same poke path as the chest and toolbox (already verified with the Operator); no canvas/pointer-module setup |
| Fri 09-25 (M3) | Export always writes CSV + HTML (evidence photos embedded as JPEG data URIs) to `persistentDataPath/Notebook`, then POSTs JSON to `{base}/notebook`; offline is not an error | The headset copy is the source of truth; the laptop copy is a convenience |
| Fri 09-25 (M4) | **Part placement:** the pointer ray's surface within reach (hold 15 cm + snap 15 cm), else the nearest surface within 15 cm of the held part, else the surface the ray hits at any distance (fascias and sills are out of reach from the ground). The mounting face goes flat against the surface; wall-type parts stay upright. Sill/floor (`-y`) parts aimed at a sill's front edge go on top of it, and square up to the nearest wall or glass (8 horizontal probes, 60 cm) instead of turning toward the viewer. After seating, the part slides within the surface plane (≤ 30 cm) out of overlaps | Window units sit square in the opening; parts pushed into a recess back off the glass instead of reading red |
| Fri 09-25 (M4) | **Fit:** red = the true-size box (dims_mm, shrunk 1 mm) overlaps the scene or another part, or a spec range (window width) or nearby tape (≤ 1 m from the part) is violated. Amber = a `clearance_mm` slab is blocked, or the mounting face rests on < 2 of 5 sample points. Window range uses the nearby tape, else a side-to-side scan of the opening | Maps each listing field to one check; the tape wins because the user measured it |
| Fri 09-25 (M4) | Parts come from the server (glTFast) and fall back per call to the shipped `PartCatalog` (fixture part.json/GLB/photo), recorded on the part (`Source`). Each part owns copies of its materials, so finishes recolour it alone (`baseColorFactor`/`_BaseColor`) | The demo never dead-ends on a network hiccup; the fallback is visible in the spec card ("OFFLINE") |
| Fri 09-25 (M4) | Crate menu (preset searches + 3 candidate cards) left of the chest; the spec card rides on the right end of the hip toolbox, so they never overlap and the swatches stay within reach | Parts on the facade are out of reach, so the card can't live beside the part as first specced |
| Fri 09-25 (UI) | **Spatial glass design system** (docs/UI.md): tokens in `UiTheme`, one `AirTools/Glass` shader with three surface tiers (Glass Regular / Glass Clear / Elevated Solid) + shadow + overlay label pill, TMP Inter SDF with Regular/Medium/Semibold presets, `GlassButton` = ISDK poke + ray on one surface with full states. High contrast and Reduce motion options | Legibility and consistency in the headset; Quest-cheap (no blur, no material instances) |
| Fri 09-25 (UI) | **Palm menu replaces the hip toolbox**: open the left palm toward your face → tools arc above it (Measure, Level, Notes / Undo, Done, Exit world / accessibility chips) + the spec inspector; close the hand → gone. Controllers: left menu button. **Thumb + middle pinch = Done**. The chest is replaced by an "Enter world" pill; the notebook floats 50 cm ahead (movable) | User feedback from the first headset test: chest too loud, toolbox clunky, pressing DONE after every shape annoying |
| Fri 09-25 (UI) | **World labels never overlap and are never cut by walls**: every frame they are laid out in screen space (priority: part callout 3 > distances/areas/levels 2 > sides/angles 1; shortest move up/down; leader line when moved; low-priority labels that can't fit within 3 heights are hidden until there is room) and drawn as an overlay beneath the UI panels | Headset test: AC callout and window tape labels piled up; at an angle, labels swung into the wall |
| Fri 09-25 (UI) | While a pointer ray rests on a UI button, tool presses are suppressed (the pinch belongs to the button) | Rays now drive UI buttons as well as tools |
| Thu 09-24 (M0) | Controller input comes through **OVRInput / ISDK only**. Unity `XR.InputDevices` sees no controllers under the Simulator. | Simulator binds `/interaction_profiles/facebook/touch_controller_pro`; Unity's own OpenXR actions (Oculus Touch profile only on Desktop) get no device. `OVRInput` sees pose + trigger. Note: `OVRInput` controller pose = OpenXR grip pose + fixed offset (60° pitch, ≈ −2 cm y / +4.6 cm z) |
| Thu 09-24 (M0) | Voice capture picks the mic device **explicitly** (prefer the built-in / headset mic), never `Microphone.Start(null…)` blindly | On the Mac, the default device (AirPods) records all-zero samples and the iPhone Continuity mic fails to open; the built-in mic records correctly |
| Thu 09-24 (M0) | Cleartext HTTP on Android via `android:usesCleartextTraffic="true"` + `INTERNET` + `RECORD_AUDIO` in `Assets/Plugins/Android/AndroidManifest.xml` | Simplest option; no `.androidlib` needed for a network-security-config. Meta's manifest tool may regenerate the file — re-check after "Update AndroidManifest" |
| Thu 09-24 (M0) | Android package ID **`com.airtools.quest`** (was Unity's template default `com.UnityTechnologies.com.unity.template.urpblank`) | The template ID clashed with an old, differently signed app on the team 3S (`INSTALL_FAILED_UPDATE_INCOMPATIBLE`) |
| Thu 09-24 (M0) | **Headset ↔ laptop networking: campus Wi-Fi can't be used.** Dev uses a USB tunnel (`adb reverse tcp:8765 tcp:8765`, app base URL `http://127.0.0.1:8765`). **Demo + dev network: iPhone Personal Hotspot** with the MacBook and the Quest both joined (clients share `172.20.10.0/28`, so base URL = `http://<Mac hotspot IP>:8765`; check reachability once both join; allow python through the macOS firewall). | Quest on GTvisitor (10.66.x) could not reach the Mac (10.90.x): ping and TCP 8765 both time out. The USB tunnel returns HTTP 200. GT eduroam is EAP-TLS with a per-device SecureW2 client certificate (JoinNow `.run` is the Linux installer), which has no practical path onto the Quest, and campus networks isolate clients anyway |
| Fri 09-25 (backend) | **Scene packages load at runtime over HTTP** (`SceneStreamer`): GET /scenes → scene.json (ETag, polled 5 s) → the files it names; revision swap = collider + structure, then the visual a frame later, same content object; revision-named files cached on the headset (last good package loads offline); the synthetic facade stays as the built-in fallback. Auto-loads site `kitchen` when the server has it | Backend contract (api.md, plan §1a); the pipeline's preview → full swap must not move anything |
| Fri 09-25 (backend) | **glTF → Unity = negate X for everything we parse** (structure, cameras, spawn), like glTFast does for meshes; plane-local (u, v) keep the file's meaning with v = −(n × u) in Unity | One convention for mesh and data; mirrored layers fail `BackendHarness.CheckAlignment` by metres |
| Fri 09-25 (backend) | **Structure-layer snapping** (`StructureSnapper`, R6 §7.3 numbers unchanged) replaces vertex snapping on packages; no structure (preview) = the plain mesh hit. Hysteresis grows the depth gate too | Measured best by P1 (2.7–3.6 mm vs 6.9 mm mesh); photogrammetry vertices are noise |
| Fri 09-25 (backend) | **Scene root → Content → meshes**: TabletopController scales SceneRoot, "set scale from a known dimension" scales Content (calibration, saved per site + frame.id); tools keep measuring in SceneRoot space, so tabletop readings are unchanged and calibration corrects them (SceneRoot.Rescaled → tools' RescaleAll) | The kitchen is 1.47× too small; the two scales must not fight |
| Fri 09-25 (backend) | **Runtime glTF uses our URP materials** (`UrpMaterialGenerator`): server parts URP/Lit, scene meshes URP/Unlit | Nothing references glTFast's shader graphs, so player builds strip them (parts would render without a shader on the Quest); unlit = baked photo lighting, cheapest on the 3S |
| Fri 09-25 (backend) | Checkout sends **listings**: qty = ⌈units / pack_qty⌉; the labelled **offline receipt** (`OFFLINE_RECEIPT`, mode offline) is a recorded order, shown with `receipt.label` verbatim; BOM lines ride in the same cart. Only the panel's 1 s hold calls /checkout | Real server: price_usd × qty per listing; no Cybersource keys in the demo (api.md §7 honesty labels) |
| Fri 09-25 (backend) | Agent actions (api.md §5) all go through `AppCommands` (`AgentActions`); voice = push-to-talk (Scene window "Hold to talk") → POST /voice/command with the context + the 3 nearest scene photos; realtime voice not used | Voice never has its own code path; /voice/token is 503 on the server |
| Fri 09-25 (backend) | Default server `:8000` (real FastAPI server; `adb reverse tcp:8000 tcp:8000` on device), overridable by launch extra / `server.txt` / PlayerPrefs; the mock mirrors api.md and stays for keyless tests | Swap = URL change, as planned |
| Fri 09-25 (kitchen) | **No mesh hit → structure plane** (`StructureSnapper.SnapWithoutHit`): the nearest plane the ray crosses inside its outline stands in for the missing collider hit (parts, level and fit-support probes use it too); corners/edges along the ray still win, and the plane hides features behind it only when crossed ≥ 3 cm inside its outline | The kitchen's 50k-tri collider has holes on 7/40 object centres where the visual mesh is closed; parts fell through door o29. Near an outline (scan edge) the fitted polygon is centimetres off, so it must not occlude there; tape results unchanged |
| Fri 09-25 (kitchen) | **`Measurement.axis` follows the tape**: vertical (within 20°) `h`, horizontal ≤ 1.5 m `w`, longer `length` | Sending `length` for every tape turned the server's size check off ("length run, no size constraint") |
| Fri 09-25 (kitchen) | **Ask-the-scene without a box pins the asked spot** (frame id `focus`) | The live vision model answers without `frame_id`/`box`; the wearer still gets the answer on the thing they pointed at |
| Sat 09-26 (UI) | **Palm toolbox → Liquid Glass tool ring** (palm up; pinch-drag spin with momentum and ticks; tap a side item to spin to it; modes equip on settle, actions on a lens pinch); Move is the default mode (world entry, menu-hand double pinch); Done removed; global Undo **and Redo** (`EditHistory`) at the ring's ends; Contrast / Less motion moved to the Scene window; Phosphor icons | User direction (Apple Photos' dial, Liquid Glass); Meta hand-UI guidance: operate a hand-anchored menu indirectly (pinch-scrub), freeze it while the other hand works it |
| Sat 09-26 (demo) | **An array run within 3° of level is level** (`ArrayPlanner.LevelDeg`): the row stays at the reference part's height; steeper runs (ramp rails 4.8°, stairs) keep their slope | A live gutter tape often snaps its ends to opposite corners of the 12.7 cm lip (5.986 → 6.100 m over 4.2 m, 1.6°); following that slope put the two low hangers into the gutter back (6/8 green) |
| Sat 09-26 (presence S1) | **Mode changes animate instead of fading** (`TransitionDirector`, Reduce motion off): Enter/Exit world pours the world in/out of a sphere 1.2 m ahead; Tabletop → World grows the model around you anchored under your feet; World → Tabletop shrinks it back; Passthrough ⇄ Tabletop scans it in/out. Reduce motion keeps the fade unchanged | No black frame: the passthrough underlay stays on during the effect and the scene / sky write alpha 1 where revealed (presence.md S1) |
| Sat 09-26 (presence S1) | **The World sky is a dome** (`SkyDome`, 80 m icosphere, colours sampled from Default-Skybox), cameras clear to opaque black behind it; the scene draws with `AirTools/SceneReveal` (unlit; the synthetic facade gets fixed per-face shade instead of URP/Lit) | The transition can close the sky over the room cone by cone and end on exactly the World look (a skybox can't be revealed partially) |
| Sat 09-26 (presence S1) | **Exit world after stepping in from the table shrinks back onto the table** (Tabletop, bought parts beside the model); entered by the pill, Exit still goes to passthrough | The demo ends where the physical objects are; a second Exit (from Tabletop) scans the model out to passthrough |
| Sat 09-26 (presence S1) | **Table point = site mat > environment raycast > assumed 0.75 m** (`RealTable`); MRUK (QR tracking, world lock off, no scene load) and `EnvironmentRaycastManager` are created at runtime **on the headset only** by `SiteMatTracker` | The Editor / Simulator never start MRUK; both need the device (spec §2.2) |
| Sat 09-26 (UX W0) | **Display copy is rewritten at display time only** (`Copy.cs`, docs/ux/specs/W0.9-copy.md): logs, `NotebookEntry.Label`, CSV/JSON, `LastAction`, `LastError`, `FitReport.Headline` and statuses keep their raw text; toasts, labels, windows and the HTML report say it like a tradesperson ("✓ Fits the door", "Receipt saved · $7.59 · no charge"). D2 (units) is pending: the unit hooks wrap today's formatters. ✗ (not ✕) is the won't-fit glyph: Inter has no U+2715 | Tests, RunDemo and tools/demo/hcheck.py parse the raw strings; one scrub (`Copy.Clean`) keeps "(mock)", URLs and HTTP errors off the screen |
| Sat 09-26 (UX W0) | **Undecided UX calls ship as one-line flags at today's behaviour**: D1 `ToolManager.Default` (Move), D4 `MeasureTool.AutoSaveTwoPointTapes` (false), D5 `GlassButton.RayOnWindows` (false, poke only), D6 `ToolRing.CommitOnPinch` (false); D3 uses the interim accent #2563EB with white labels (5.2:1) | The team decides D1–D7 (docs/ux/README.md); flipping one is a one-line change plus the demo beat / hcheck line that expects today's value |
| Sat 09-26 (UX W0) | **Type tokens are em heights with a 16 dmm floor** (caption 16, label 18, body 20, title 22, heading 26), sized for each surface's real distance; windows sit 0.45 m, 20° down, one at a time (Checkout replaces Sellers, Back returns); Find parts and the Scene window take side slots at 0.5 m; the Enter pill is placed from the head (0.45 m, 25° down) | Meta's legibility floor (~15–20 dmm) and touch band (0.4–0.5 m, 10–35° down); ErgonomicsTests hold it |
| Sat 09-26 (UX W0) | **Mode switches never destroy work**: a tape in progress is parked when Measure is put away; a held part goes back to Find parts (same instance, back on re-equip or Take) | A soft flick of the ring used to clear a tape or discard the part in hand |
| Sat 09-26 (B1/B3) | **A hold while the server is still checking, or with a failing check, sends nothing** (no deferred payment): Pay is off until `/checkout/prepare` answers `all_ok`; `new HoldProof` exists only in `CheckoutPanel.OnHoldConfirmed` (grep test). A 404 / no answer on prepare = an older server: pay exactly as before (Legacy) | Only a hold on a cart the user saw verified may carry a nonce; RunM5 runs its holds from M5Check once the prepare has answered |
| Sat 09-26 (B1) | **Survey upper / lower split at the largest height gap (> 15 cm), else the median** (`SurveyPlanner.Split`); agent-measured outlines are light with one "W × H" label each (≤ 12 labelled: amber, focus, most recent), unverified ones amber + ⚠, the user's tapes stay yellow; one Undo removes a survey | A median split cuts a base-cabinet row in two when wall and base counts differ; UX W1.9 label diet (18 × 9 pills would bury the kitchen) |
| Sat 09-26 (UX D1–D7, user) | **The seven UX calls, decided by the user (all as the UX plan recommended, `docs/ux/README.md` §0):** D1 the world opens with **Measure** (Line), Move one ring detent away; D4 a **2-point tape auto-saves at point 2** (Area mode still closes with the other hand); D2 **one unit per label, imperial-primary** for the HackGT build, with a units chip to switch (export keeps both); D3 **primary buttons are white with dark text** (17.5:1), tape-yellow `#FFD23F` marks selected / active / measured; D5 **ray + poke on windows and Enter**, behind `GlassButton.RayOnWindows`, 120 ms hover dwell, the measuring ray never presses buttons it crosses; D6 **the ring commits on pinch**, settling only previews; D7 **in-app demo controls**: DemoMode, in-app reset, presenter page (never pays) | W1.3 measured the judge path at 10 actions with D1 + D4 vs 12 today; the other calls are the UX research recommendations (R01–R09). Reverses the M2 "one trigger covers place and finish" decision (D4) and CLAUDE.md's metric-by-default for this build (D2) |
| Sat 09-26 (UX D3, build) | **D3 as tokens** (`UiTheme.Colors`): `primary` #F5F6F8 + `onPrimary` #0F1115, `ink` #FFD23F + `onInk` #17140A, `info` #7CC4FF. Every former `accent` use is re-mapped: actions → primary; selected / active / "you are here" / measured → ink (toggles, chips, the ring's active label + lens, the Best pick badge, hold ring, teleport disc, measure lines); neutral toasts and the busy dot → info. `accent` / `onAccent` / `ColorRole.Accent` stay only as `[Obsolete]` aliases. Also: **window glass α 0.84 → 0.95**, **caution #FFB529 → #FF9F43**, and `AirTools/Glass` gives light fills a bevel so a white pill stays glass | Secondary text with a white wall behind a window: 3.1:1 at α 0.84, 5.8:1 at 0.95 (ErgonomicsTests, pure maths); the old amber sat 14° of OKLCH hue from the ink, so "check" read as "selected" |
| Sat 09-26 (measure) | **Area-mode polygons follow the clicks, concave included** (L, U, notched walls), replacing the convex hull of d3423c7: best-fit (Newell) plane, shoelace area, interior angles 0–360° (reflex > 180, sum (n−2)·180). An outline that crosses or touches itself (1 mm) has no area and **is not saved**: the finish is refused ("Edges cross · undo the last point", R01 / R32 "cross"), the points stay for Undo, and a drag that would cross puts the point back. Exception kept from the hull: when the clicks cross but **every point is a hull corner** (an out-of-order rectangle), the hull order is used, since it is the only non-crossing outline. The area label sits inside (centroid, else a pole of inaccessibility) | User: "we can't even make an L shape"; the hull turned an L into its hull (wrong outline, area and angles). Refusing a bow-tie beats silently saving a wrong area |
| Sat 09-26 (UX de-clutter DC1–DC7, user) | **All seven de-clutter calls as `docs/ux/declutter.md` §7 recommends:** DC1 "do the whole job" progress is a mode of the W1.3 status line (a 7-glyph step strip); DC2 toasts and agent replies flash on the status line while the rail is on (the toast stays the fallback with the rail off); DC3 the ring is 6 items — Move · Measure · Level · Notebook · Model view · More (More = the Scene window: Home, Ladder, Exit world, layers, units; Step in cut); DC4 the install-coach card uses the left side slot (replacing Find parts while it runs); DC5 limits / scale / Zabel credit collapse into one wrist strip (the credit also shows 8 s when the scan loads); DC6 one Grok world overlay at a time (scan labels via More ▸ Labels); DC7 one pool of 12 world labels across every overlay (safety → honesty/live task → saved measurements → parts → level/ladder → passive; the rest shrink to dots) | Each lane placed its own UI and the worst demo moment had 15 surfaces up with 18 overlapping pairs (declutter.md §1). DC5 needs the backend team's OK that the credit placement meets their "under every Zabel shot" note |
| Sat 09-26 (UI round 3) | **Every web request runs on our own clock** (`HttpDeadline`: small JSON calls give up after 6 s without a byte, downloads 15 s, thinking calls at their timeout + 1 s) and **a scene load can't stick** (`SceneLoadWatchdog`: 20 s without progress, 60 s of work; "Couldn't load the kitchen · Retry") | Since 15:40 the headset sat on "Loading the site…" with no log line at all: every request hung (presenter, parts search too) and Unity's own timeout never fired; a stopped coroutine never runs its finally |
| Sat 09-26 (UI round 3) | **A library launch finds the demo server itself**: no override → probe the default, then `ServerConfig.candidates` (:8004, :8000) with `GET /health`, first answer wins for the session (not saved) | Launched from the Quest library the app pointed at :8000, which has no USB tunnel |
| Sat 09-26 (UI round 3) | **Palm menu: palm down or away closes it even while held; only real work on the ring holds it** (fingertip ≤ 3.5 cm of the glass, a ring pinch/spin, a finger on a button; 0.3 s grace, 2.5 s cap for a resting hand; lost hand 0.8 s) | Headset log: "held (other hand on it)" 0.1–0.7 s after nearly every open, for 3–23 s, and a held menu ignored the palm |
| Sat 09-26 (UI round 3) | **Model view is the model only, fitted to the table** (`ModelViewDeclutter` turns off every world annotation's renderers; a round 1:N with the longest side 0.6–0.8 m: kitchen 1:4, Zabel 1:125, hospital 1:400, GT 1:8–1:10) **with a switcher row on the table** (server scans minus `synthetic-*` test packages, built-in facade last); the rig never moves while switching, Walk in lands at the new site's spawn | User: remove text in Model view, add the other models |
| Sat 09-26 (round 4, modelwheel) | **Model view floats the model centred in your view and switches models on a wide endless wheel under it.** The model's box centre goes straight ahead at eye height (6 cm below), ~1 m away, fitted to ≤ 0.9 m (≈ 40–50° of view; a very tall model rises just enough for the wheel), placed once on entry and world-locked; Recentre (chip) or walking > 1.5 m away / turning > 60° for 0.75 s brings it back (a 0.35 s glide). The wheel is the palm ring's prize wheel at 0.55 m radius (`ModelWheel`: 7 card slots 20° apart, ~110° visible, an endless loop), its lens 0.85 m away 16–34° below the eye line under the model's lowest edge, facing the eyes; ray pinch / trigger and drag spins it with the ring's flick physics (`DialPhysics.Strip`, `DialGesture`, `WheelMath`), "Not downloaded" cards are never rested on, a tap on a side card spins it to the lens and a pinch on the lens opens that model; Walk in · Recentre · Exit chips under it. The table placement stays as `TabletopController.placement = OnTable` (no Settings row: every row is full). Bought parts stand on the floor right of the floating model | User: "it is at the bottom, almost in one's lap … very difficult to use"; "recreate the toolbox without it being a super small circle" |
| Sat 09-26 (UI round 3) | **Placed parts are locked outside Adjust**; Adjust = hand grab (yaw only) + move/turn/tilt/roll pads in the gap's frame, live fit, Snap, Reset to fit, saved placements A–D (`placement_pose` notebook rows, scene-file coordinates), model swaps keep the front-bottom-centre in a gap; one adjust session = one undo; cycling models is one undo step, and cycling back to the start cancels it | User: 6-DoF placement, toggleable and saveable; a no-op undo after next → previous looked broken |
| Sat 09-26 (e2e) | **The kitchen's scale for the replace demo comes from the dishwasher opening: "the opening is 34 and a half inches tall"** (`scale_gap`, ×1.48; the backend README says the kitchen reads ≈1.47× small) and **a fit within 5 mm over is amber "tight"**, not red (the gap is an estimate, appliance legs adjust ~1″) | At scale 1 the gap is 17¼ × 23¼ × 17½″ and every dishwasher is red; the Whirlpool WDP540HAMW is 876 mm in an 876 mm opening |
| Sat 09-26 (e2e) | **"Measure and replace" by voice runs on our demo server via hand-off `docs/handoff/p4-e2e/` (3 patches)** until the backend's `feat/voice-measure-replace` lands; the headset also has a narrow fallback (`LocalIntents`) for the same phrases when a reply carries no action | The backend branch isn't pushed; "remove the dishwasher" returned only a how-to |
| Sat 09-26 (user) | **The kitchen scan starts at ×1.63** (`SiteScales`, a per-site default applied unless a user scale is saved; Reset / Demo reset return to it; the context sends `scale_source`) | User: "the 1.63x estimate was correct, begin with that" (the backend's object check said ≈1.45–1.47×; a Set scale still overrides) |
| Sat 09-26 (user) | **The toolbox's More is Settings** (gear-six U+E272, window title "Settings", settings rows first) | User request |
| Sat 09-26 (network) | **Plain HTTP to the laptop is allowed** (`insecureHttpOption` NotAllowed → AlwaysAllowed) | With NotAllowed only the `127.0.0.1` USB tunnel worked; a hotspot / laptop-IP server was refused before sending ("Insecure connection not allowed"). The laptop server is HTTP-only on the LAN |
| Sat 09-26 (autonomy) | **One sentence runs the whole replace as a server job** (`replace_component`: remove → tape → scale check → gap-fitted search → pick fits/tight → model → place → spoken summary; never places a red fit) and **the agent is Grok `grok-4.20-0309-non-reasoning`** (24/24 right tool, 0.8 s median vs grok-4.7 22/24 up to 11 s, Groq gpt-oss 15/24); Groq calls that fail fall back to Grok (`LLM_FALLBACK`), STT to xAI grok-transcribe | User: an autonomous Grok agent that removes, measures, searches, renders and places |
| Sat 09-26 evening (user) | **DemoMode is off by default everywhere** (`DemoMode.DefaultOn` = false; D7's stage-safe mode is opt-in only: `-e demo on`, the presenter switch, the harness). So by default: no forced guide rail/coach, preferences are saved, "Open at seller" opens, no reset gestures, no demo receipt label | User: "indefinitely disable the demo mode and all restrictions associated with it … use it as like a free world" (supersedes D7's default) |
| Sat 09-26 evening (sitescope, user bug) | **Every world item belongs to the site it was made in** (`SiteScope`: the package's site id, `built-in` for the facade; revisions share). A site change parks the other sites' tapes, levels, ladders, placed parts and generated assets, scene-part removals, Grok layers, pins, coach boxes and reimagine quad: **hidden (inactive, no collider), never destroyed**, back exactly as they were on return (the same scene-root poses at the scale the site loads with). **Undo/redo are per site**: each owner parks its stacks with the site, so EditHistory only sees the live site and a redo can't land in another scan. The notebook keeps every row (a log) and lists the loaded site's, with an **All sites** toggle; the context's `measurement`, `opening`, `cavity`, `removed`, `placed`, `selected_part_id`, `survey_id`, `drill_px` are the current site's. Demo reset clears every site. Take-it-home parts on the real table stay the room's | User: "switching models via the model view renders items from the previous models … items are only rendered in their own model". Per-site stacks (vs a site filter in EditHistory) keep every existing reader (context, snapping, labels, checkout counts) correct without a filter at each read |
| Sun 09-27 (user) | **LLM+CAD is real: OpenSCAD (snapshot, manifold backend) on the laptop; Grok `grok-4.7` at low effort writes the SCAD** (median ~45 s, ~$0.03 a part; `LLM_ASSET_SCAD` / `ASSET_SCAD_EFFORT`), CAD models pre-built with `warm --scad` (85 parts, ~27 min, ~$2.7), placed parts upgrade from the template when the CAD model lands | User: "Install OpenSCAD and add it to the pipeline so we can directly compare"; the six-writer benchmark in `SpikeData/cad-compare/speed-cost.md` (Hunyuan: 22 % off on the dishwasher, knob a stalk) |
| Sun 09-27 (user) | **A world-model switch closes every open window and stops a running job** (the job's own switches kept; `POST /job/run/{id}/cancel`); **measurements are strictly per model** (audit of 14 kinds) ; **the catalog follows the gaze** (roof / wall / ground / ceiling / counter / opening, 1.2 s hold, pin; `GET /catalog?focus=`) | User requests |
| Sun 09-27 (user) | **D1 reversed: Move is the tool in hand on entering the world, and again right after a part lands** (placed from the hand, the Edit view's new part, or Grok's place_part; not while the next part is held); Measure is one ring detent away (a pinch on it previews, a pinch on the lens commits). Editing an already-placed part still gives back the tool you had | User on the headset: "measure is really annoying to have as a default" — it was only the first tool we built. Supersedes the Sat 09-26 D1 row |
| Sun 09-27 (user) | **Removing a placed part is always one undo step** (spec card Remove, the Edit view menu's Delete, voice `delete_part`, a model swapped in a gap, tool Clear): the part is hidden, not destroyed, until a new edit drops the redo (or a hard clear / reset); Undo brings back pose, finish, fit, selection and its notebook row. `EditHistory.Step()` groups one action's edits across tools. Removing one member of an array takes out that copy only (Undo the array or Clear for the run); Remove on a part in hand returns it to its Find parts card | User: "when you delete an item you cant undo and bring it back". Gate: DeleteUndoCheck 18/18, E2E kitchen 9/9, PartsCheck 30/30, RunDemo 28/28, EditMode 2059 + 1 |
| Sun 09-27 (user) | **The Edit view is worked by touch, within reach:** every control (12 arrows, swatches, Step / Shade / Reset, Cancel · Move · Save, the part menu) is a poke button (hands and controller tips; no pinch, no ray); a held poke repeats after 0.4 s, then every 0.12 s. The arrows are 0.42 m from the eye; the panel is 0.44 m, lower right of the ring (under it would be ~50° down); the item is shown ≤ 0.25 m, ~12 cm below the eye; the part menu 0.42 m. **Move and placing a new part translate only**: rotation is kept exactly; no opening / saved-spot snap turns it (the move bar still reports the gap fit); a new part keeps the orientation set in the view | User: "make the edit menu touch buttons … bring it closer", "remove edge snapping from reorienting items … only apply x,y,z translations". Gate: EditViewCheck 25/25 (35 buttons poke-only), DeleteUndoCheck 18/18, RunDemo 28/28, EditMode 2065 + 1 |

## 10. Progress log
*(Agents append: date/time, milestone, checks passed/failed, open [H] checks, notes.)*

**Thu 2026-09-24 — M0 feasibility spikes (branch `spike/m0`, XR lane, ~1 h of the 3 h timebox)**
Scene `Assets/AirTools/Spike/Sandbox_M0.unity`, built by the menu **AirTools ▸ Spike ▸ 1/2/3** (`Assets/AirTools/Spike/Editor/M0SpikeEditor.cs`). Test data comes from `tools/spike/make_splat_ply.py` (synthetic 3DGS PLY at exact 50k/200k/400k counts: a facade-like checker wall + a sphere at z = 2 m) and `tools/spike/make_box_glb.py` (exact-size 127×45×38 mm proxy GLB). Generated inputs live in `/SpikeData/` (gitignored).
- [A] PASS **Splats in Unity 6.3 + URP 17**: upstream v1.1.1 imports and compiles with 0 errors; splat assets are created by script. In **Single Pass Instanced** the splats are **invisible in both eyes** (mesh objects are fine), so that FAILS. In **Multi-pass**, `openxr_capture_composited_image` shows them in both eyes with correct stereo: disparity L−R at 840 px is sphere 214 px vs mesh cube at the same 2 m depth 216 px, and the wall at 4 m is 210 px (expected Δ ≈ 7 px). No doubled image. Checked at 50k and 400k. Editor on M-series Mac: 72 fps at 400k (GPU 8.6 ms). Mac numbers say nothing about Quest perf.
- [A] PASS **glTFast** (`com.unity.cloud.gltfast` 6.20.0): `GltfImport.Load("http://localhost:8765/box.glb")` in Play mode; the instantiated bounds are 0.1270 × 0.0450 × 0.0380 m (exact).
- [A] PASS **Mic**: `Microphone.Start` recorded 1 s at 24 kHz on the built-in MacBook mic (24000 samples, 20k non-zero, RMS 0.0013–0.004). The default device (AirPods) returned all zeros, and the iPhone Continuity mic failed to open (see §9). The first `Microphone.Start` after an Editor launch blocks the main thread on the macOS mic-permission prompt.
- [A] PASS **Operator drive test**: `openxr_set_controller_pose(right, [0.25, 1.3, −0.4])` + Trigger 1 → the app's `OVRInput` logged one trigger press (value 1.00), and `openxr_get_controller_pose` read back exactly [0.25, 1.3, −0.4]. The Operator A button also cycled the splat assets. Needs an `OVRManager` in the scene; Unity `XR.InputDevices` sees no controllers (see §9).
- [A] Done **Cleartext HTTP (Android)**: manifest edited, and the merged APK manifest shows `usesCleartextTraffic=true`, `INTERNET` and `RECORD_AUDIO`.
- [A] Done **Spike APK**: `Builds/AirTools-M0-spike.apk` (81 MB, release, Vulkan, IL2CPP ARM64, OpenXR Multi-pass, 0 build errors). It contains Sandbox_M0 with the 50k/200k/400k assets; the device glTF URL `http://10.90.41.76:8765/box.glb` is baked in (rebuild if the Mac's LAN IP changes).
- Open [H]: splat fps on the 3S at 200k and 400k (Multi-pass); glTF load from the Mac LAN IP; the Quest reaches the laptop over cleartext HTTP; mic on the Quest (RECORD_AUDIO prompt).
- Notes: (1) The **Meta XR Simulator deadlocked** once on a Play → Stop → Play cycle (main thread stuck in `xrCreateInstance` → `SimRpcManager::startServer` joining a gRPC shutdown thread). The only fix is to restart the Editor. (2) Spike-only settings changes to carry into M1 if the splat decision holds: packages (glTFast, splats), `GaussianSplatURPFeature` on PC_Renderer + Mobile_Renderer, OpenXR Multi-pass (Desktop + Android), and the manifest.
- Update (same day): the first install failed on a package-signature clash, so the app ID changed to `com.airtools.quest` and the APK was rebuilt and **installed on the 3S over USB** (`340YC10GB0043Y`). The glTF probe now tries `http://127.0.0.1:8765` (USB tunnel) first, then the LAN IP. The bench auto-cycles 50k → 200k → 400k every 20 s and logs `[AirTools.Perf]` to logcat. Wi-Fi reachability FAILS on GTvisitor (see §9); the tunnel works (HTTP 200 from the headset shell). The [H] checks are still open and need someone wearing the headset.
- Security: `Assets/Resources/DevAgentSettings.asset` (Meta Immersive Debugger / AI Agent Bridge) stores the `meta-xr-unity-runtime` bearer token as `accessToken`. It had been committed in the setup commit and was scrubbed from all unpushed history before the first push; the repo copy is blank. Each clone must run `git update-index --skip-worktree Assets/Resources/DevAgentSettings.asset` (see CLAUDE.md).

**Thu 2026-09-24 — M0 [H] results + wrap-up (XR lane)**
- [H] PASS **Splat fps on the 3S** (measured, headset worn, log streamed over USB to `SpikeData/quest_run.log`): 50k → 72 fps, 200k → 60 fps, 400k → 36 fps. GPU-bound at GPU level 4. → Decision: mesh-only (§9).
- [H] PASS **glTF on the Quest** via USB tunnel `http://127.0.0.1:8765` (exact size 0.1270 × 0.0450 × 0.0380 m). Wi-Fi path on the iPhone hotspot still to confirm.
- [H] PASS **Mic on the Quest**: "Android audio input", 24000 samples @ 24 kHz, permission prompt accepted.
- [H] PASS (tunnel) / OPEN (Wi-Fi) **Quest reaches the laptop**: cleartext HTTP works end to end; Wi-Fi reachability pending the hotspot.
- Integrated into `main`: `com.unity.cloud.gltfast` 6.20.0, AndroidManifest (cleartext, INTERNET, RECORD_AUDIO), package ID `com.airtools.quest`, `tools/make_box_glb.py`. OpenXR stays Single Pass Instanced. Compiles with 0 errors.
- **M0 closed.** Next is M1.

**Thu 2026-09-24 — M1 Rig, scene, state (branch `m1`, XR lane)**
Built: `Assets/AirTools/Scenes/Main.unity` from Building Blocks installed by script (`meta-xr-unity-runtime` BuildingBlocksTools): Camera Rig, Passthrough, Hand Tracking, Controller Tracking, Interactions Rig (controller + hand poke/grab/ray/distance-grab interactors), Poke Interaction. App objects are wired by **AirTools ▸ Wire Main Scene** (`MainSceneBuilder`), and the facade by **AirTools ▸ Build Synthetic Facade** (`SyntheticFacadeBuilder` → `Fixtures/SyntheticFacade/`: prefab, materials, 12 cameras, `cameras.json`, thumbnails, `SyntheticFacade.asset` ScenePackage).
- [A] PASS **Starts in Passthrough**: `AppState.Mode = Passthrough`, passthrough initialised, and the composited capture shows the Simulator's synthetic room plus the chest.
- [A] PASS **Chest poke → World**: the Operator drove the right controller's grip so the ISDK `ControllerPokeInteractor` tip went 15 cm in front of the button and then 4 cm through it. Mode was World at contact (+1 ms), the transition finished at +0.94 s (budget 1.5 s), and the capture shows the facade in front of the camera at spawn (sky, wall, window, fascia, door, ledge). A second poke returned to Passthrough.
- [A] PASS **Scale 1:1**: `unity_get_world_pose("SceneRoot/Facade/Window")` = (0.000, 4.100, −0.100), exact.
- [A] PASS **Zero console errors**: a 31-minute Play session (128k frames, ~70 fps) with two chest transitions logged 0 errors.
- [A] PASS **Tests**: EditMode 25/25. That covers every §4 ground-truth number by raycast (window 1.500 × 1.200, sill 3.500 and level, glass recess 0.10, gutter 4.200 / 0.127, fascia 4.200 and 25 mm proud, door 0.914 × 2.032, ledge 2.00°, wall 8 × 6.5), plus mesh winding, AppState/AppCommands, the chest cooldown, SceneLoader up/north/spawn, ModeController timing/midpoint swap/reversal, and the check-line format. The PlayMode test (1) compiles but **the run aborts**: entering Play for a test run also starts OpenXR, the Simulator session exits, and the Test Runner reports "player was stopped". Follow-up: an editor hook that turns off `InitManagerOnStart` during test runs.
- APK `Builds/AirTools-M1.apk` (0 build errors) is installed on the 3S.
- Open [H]: passthrough visible on the 3S; chest opens and closes by **hand** poke; no judder during the fade; chest height and reach feel right.
- Notes: Operator poke recipe: the Simulator's controller **grip** pose drives the Unity anchors, and the poke tip = grip + (−0.015, −0.054, −0.065) m (OpenXR, identity rotation). Operator poses are in tracking space: rig at (0, 0, 4) yaw 180 ⇒ world (x, y, z) → OpenXR (−x, y, z − 4); e.g. button world (0, 0.83, 3.622) → OpenXR (0, 0.83, −0.378). The Editor was killed once on a Stop→Play cycle (Simulator); after the relaunch the Operator MCP proxy stayed disconnected and needs a manual `/mcp` reconnect.

**Thu 2026-09-24/25 — M2 Toolbox + point measure (branch `m2`, XR lane)**
Built: `ToolInputHub` (the single `IToolInput`) fed by `OvrToolInputSource` (ISDK controller/hand rays, trigger/pinch, B/Y finish, A/X undo) or by injected input. `SnapService` (ray/point snapping with feature snap). `MeasureTool` + `MeasureSession` + `MeasureView`/`MeasureLabel` (2 points = distance; 3–4 = sides, angles, area, off-plane warning; live preview + snap cursor; drag-to-edit; undo/clear). `Notebook` + `CameraEvidence`. `ToolManager` + `AppCommands.EquipTool`. Hip `Toolbox` (MEASURE / DONE / UNDO poke buttons, yaw-follow). Dev: `MeasureScenarios` (5 ground-truth tasks with aim noise) and `AgentHarness` (drive the live app via `unity command eval`).
- [A] PASS **EditMode 195/195**: units, MeasureMath, SnapService (face/edge/corner/inside corners, radius limits), MeasureTool behaviour (finish/auto-finish/duplicate/undo/clear/drag/jitter/preview/unequipped/miss), notebook + evidence camera, input hub lifecycle, AppCommands, script-file naming. Scenario ground truth × 25 seeds each.
- [A] PASS **Stress**: 300 seeds × 5 scenarios = 1500/1500 within tolerance (window 1.500 ± 0.010, gutter 4.200 ± 0.015, window area 1.80 ± 0.03 m² with 4 × 90° ± 1°, door 0.914 ± 0.010, ledge triangle 0.30 ± 0.01 m² with 8.5/90/81.5°).
- [A] PASS **Live Simulator** (`AgentHarness.RunM2`): 25/25 through the real hub → tool → snap → notebook path. Composite view (Game view capture) shows the lines, snap-coloured markers and readable labels (distance, sides, 90° corners, area).
- [A] Zero app errors in the Play sessions (the only console error was the CLI's own eval timing out during Play startup).
- [A] PASS **Real controller path via Operator**: grip pose aimed the ISDK controller ray at the left and right window jambs (`RightRayHit` = (∓0.750, 4.100, −0.050)); live preview read 1.5000 m before the second click. Trigger pulls placed both points and **B** finished → notebook **#1 1.5000 m** "Distance 1.50 m · 4′ 11″", evidence camera 11. Poking the toolbox MEASURE button with the controller tip toggled the tool off and back on (label "MEASURE: ON"). The Operator's screenshot tool fails in this Simulator session ("staging buffer too small"), so visuals come from Game view captures.
- [H] Open: pinch/trigger placement feels right; labels readable at 2 m; toolbox reachable at the hip.
- Notes: the Editor crashed once inside the Meta XR Simulator's telemetry thread (`SIMULATOR.so` Tigon `onUploadProgress` SIGSEGV) while stopping Play; relaunched and the "keep scene backups" dialog was answered Yes (→ `Assets/_Recovery`, git-ignored).

**Fri 2026-09-25 — M3 Level + notebook + mock server (branch `m3`, XR lane)**
Built: `LevelTool` + `LevelMath` + `LevelGizmo` (bar along the fall line, bubble, green/amber/red, live ghost + placed readings, undo/clear). `NotebookExporter` (CSV/HTML/JSON), `NotebookController`, wrist `NotebookPanel` (newest first, 4 rows with evidence thumbnails, SHOW highlight, PREV/NEXT/EXPORT/CLOSE, export status). `ServerConfig` (editor localhost / device USB tunnel). `AppCommands.AddNote/ExportNotebook/ShowNotebook`. Toolbox now MEASURE / LEVEL / DONE / UNDO / NOTES. `tools/mock_parts_server.py` (+ `test_mock_parts_server.py`) and `tools/make_part_fixtures.py` → `Fixtures/Parts/{hidden-hanger-5k, window-ac-small}`; GLB origin at the mounting face (`make_box_glb.py --mount`).
- [A] PASS **Level**: ledge **2.000°** (±0.2), sill **0.000°**, wall and door plumb **0.000°**. EditMode 100/100 randomised, stress 1200/1200, live 20/20.
- [A] PASS **Notebook evidence**: 9/9 live entries (≥ 3 required) have an evidence camera whose frustum contains all their points; also checked for every scenario target in EditMode.
- [A] PASS **Export**: CSV and HTML written to `persistentDataPath/Notebook`, both parse back to 9 rows; `POST /notebook` → 200 (mock server log), status "uploaded".
- [A] PASS **Mock server**: `curl localhost:8765/parts/hidden-hanger-5k/part.json` is valid JSON with every contract field; 9/9 Python contract tests (search job pending → done after the delay, cache hit immediate, checkout receipt `"sandbox": "mock"`, voice token 501, notebook stored, 404s).
- [A] EditMode 314/314. Visual (Game view): level gizmos with readings on the door, wall and ledge; wrist panel "9 readings · page 1/3" with thumbnails.
- [H] Open: the wrist panel is readable and pokeable by hand; the level gizmo reads clearly on a real surface.

**Fri 2026-09-25 — M4 Parts in hand (branch `m4`, XR lane)**
Built (`Runtime/Parts`): `PartSpec` (Newtonsoft, null-tolerant), `PartsClient` (search → job poll → part.json / GLB / photo, per-call offline catalog fallback), `PartLoader` (glTFast from bytes, catalog fallback), `PartInstance` (true size: uniform median-ratio scale to dims_mm with a residual log, re-origined to the mounting face centre, BoxCollider on layer 9 `Parts`, owned materials for finishes), `PartPlacer` (reach/proximity/ray snap, sill matching, square-to-wall facing, in-plane depenetration), `FitChecker`, `PartOutline` (12 distance-scaled edges + W×D×H/fit callout), `PartTool` (`ToolKind.Part`: live seated preview with fit, trigger places + click + haptic, press on a part picks it up, undo/clear, notebook entry per part), `PartsBrowser` crate menu + `CandidateCardView` + `PartsButton`, `SpecCard` (dims mm + in, material/finish/weight, price at the recommended seller, window range, tier badge, citations, fit, finish swatches, REMOVE). `AppCommands.FindPart/SelectCandidate/SetFinish`. Editor: `PartCatalogBuilder` (AirTools ▸ Build Part Catalog, adds layer `Parts`); Wire Main Scene adds the client, loader, part tool, crate menu and spec card. Dev: `PartScenarios` + `AgentHarness` M4 API (`Parts`, `PartInfo`, `FindPart`, `Take`, `TakeFromCatalog`, `PlaceFrom`, `SetFinish`, `RunM4`, `ServerLoad`/`ServerCheck`).
- [A] PASS **Operator, real controller**: a controller poke on the crate menu's GUTTER HANGER → server search; a poke on TAKE → hanger loaded from the server via glTFast; controller moved 27.5 cm in front of the fascia, ray aimed (`GripForTarget`), live preview green → trigger → **back face z = 0.0250 (fascia plane 0.025, 0.0 mm error), outline green**, `[AirTools.Check] M4.operator.hanger.fascia … PASS`.
- [A] PASS **AC in the window → red**, callout "Window too wide by 500 mm / 1500 mm opening, unit fits 590–1000 mm" against the 1.500 m tape (and 1500 ± 2 mm from the opening scan without a tape); sits on the sill, square to the glass, slid out of the glass.
- [A] PASS **True size**: bounds = dims_mm ± 1 mm for catalog and server-loaded parts (127.0 × 38.0 × 45.0, 470 × 380 × 300), including a generated model mis-scaled ×3.7 with its origin off the mounting face.
- [A] PASS **Brown swatch → #5A3E2B**: by a real controller poke on the spec card on a server-loaded glTF part, and in EditMode / RunM4.
- [A] PASS **Live harness** `RunM4(10)`: **42/42** (hanger near 10/10, hanger by ray 10/10, AC with scan 10/10, AC with tape 10/10, true size, brown). **Server path** `ServerLoad/ServerCheck`: search (fresh and cached) → part.json → GLB → true-size part, `source=server`.
- [A] EditMode **446/446** (131 new: parsing, formatting, placement rotation, true size, fit scaling, finishes, placement/fit on the facade, amber clearance, red collision, slide-clear, tape spare, pick-up/undo/clear, pointer fallback, 4 × 25 randomised placement scenarios).
- Found live, fixed: parts removed in Play mode kept colliders until end of frame (deferred `Destroy`) and collided with the next placement → deactivated before destroy. An AC yawed toward the viewer caught the glass and read a slanted opening → squares to the wall. The spec card sat behind the head-following toolbox → mounted on the toolbox. With the ray off near poke buttons (right after TAKE), the held part sat at the origin → uses the other hand's ray, else floats in view.
- [H] Open: grab/release by hand; the part feels right-sized; spec card and card text readable on the Quest.

**Fri 2026-09-25 — UI revisions + spatial glass design system (branch `ui-sleek`, XR lane)**
Requested after the first headset test: subtler entry than the chest, a palm menu instead of the hip toolbox, a
"done" gesture, no overlapping labels, and the "Liquid Glass"-inspired sleekification brief. Built: `Runtime/UI`
(UiTheme tokens, UiSettings, GlassMesh + `AirTools/Glass` shader, GlassSurface, UiText, GlassButton, UiToast,
ToastEvents, WindowHandle, UiFeedback), `Input/PalmMenu` + `PalmMath`/`PalmGesture`, done gesture in
`OvrToolInputSource`, `Tools/LabelLayout` + MeasureLabel layout/overlay/declutter; Editor `UiAssetsBuilder` (Inter
SDF, materials, theme) and `UiBuild`; every screen rebuilt (entry, palm menu + inspector, Find parts, notebook,
toast, world labels). Docs: `docs/UI.md` (audit, tokens, palette, state matrix, placement, perf, how-to).
- [A] EditMode **464/464** (18 new: label layout incl. declutter, palm facing/extension/hysteresis, done gesture,
  text tokens/tabular, theme asset + glyph coverage, glass vertex data, high contrast, button states/confirm).
- [A] Live harness after the rebuild: M2 25/25, M3 22/22, M4 22/22 (and 10/10 each on later runs).
- [A] Simulator: palm menu opens by the controller menu button; glass panels, button states, spec inspector, Find
  parts (auto-height, within reach), notebook, overlay labels at oblique angles all verified by capture.
- Found and fixed live: labels cut by walls at an angle (→ overlay pass); off-axis label boxes underestimated
  (→ project corners); crowded periphery (→ declutter); stale GPU copy of the font atlas drew digits as blocks in a
  long Editor session (→ re-upload on build); tabular figures broke `<color=#…>` tags; inspector/notebook/crate
  layout overlaps; Find parts beyond reach.
- The Meta XR Simulator crashed the Editor three times (native segv in OVRPlugin `HandleSingleOpenXREvent`) on
  session events (left Menu button, switching to passthrough, stopping Play) and its window also exited mid-session.
  Controller haptics are now headset-only. Hand gestures (palm menu, done pinch) can't be simulated → [H].
- [H] Open: palm menu open/close by hand and poking tools with the other hand; thumb+middle "done"; text sizes and
  contrast in passthrough; Quest 3S FPS/GPU with the new UI (`SpikeData/quest-ui.log`).


**Fri 2026-09-25 — Backend integration (branch `backend-integration` on `m7`, a second Editor in a git worktree; no Play/Simulator/Operator)**
Brief: integrate the airtools-drone-backend contract (docs/api.md). Full report for P1/P4: `docs/backend-integration.md`.
Built: `Scene/` SceneManifest, GltfFrame, SceneCameras, StructureLayer, StructureSnapper, SceneStreamer, UrpMaterialGenerator;
SceneRoot calibration + Rescaled; SnapService structure branch + axis lock + toggle; `Structure/` ScaleCalibration,
CalibrationSync, SnapCursor, StructureOverlay, Scene window (ScenePanel); `Agent/` AgentClient, AgentActions,
AgentContext, VoiceClient, WavUtil, SceneAsk; PartsClient/PartSpec on the real contract; checkout packs + offline
receipt + BOM; ServerConfig :8000 + overrides; Editor ScenePackageExporter (synthetic packages that pass the backend's
validate_package), PlayWithoutXr; Dev BackendHarness + PackageFixtures; mock server mirrors api.md (11 tests).
- [A] PASS real server (scenes): load + spawn, ETag poll/304, revision swap in place (no jump, identical snaps), preview without structure → mesh hits.
- [A] PASS X flip: door corner snaps to (3.2570, 2.0320, 0.0300); CheckAlignment median 0.0 mm over 99 visible corners.
- [A] PASS tape repeatability on structure: door 914.0/914.5/914.0 mm, window 1500.0 ×3 (±1 cm aim); EditMode 50 seeds ≤ 1 mm.
- [A] PASS scale from a known dimension: ×1/1.47 package, 1.0204 m → 150 cm → ×1.4700, tape 1.5000, restored on reload.
- [A] PASS (mock) agent text + voice → search → load (asset.status wait) → place (green/red) → server seller re-rank → BOM → checkout opens only; 0.6 s hold 0 requests, 1.0 s hold 1 request; offline receipt label verbatim.
- [A] PASS ask-the-scene pin 0.00 mm from ground truth (3 nearest thumbs, camera intrinsics, X flip).
- [A] Regression: live M2 15/15, M3 14/14, M4 14/14, M5 6/6 + receipt; EditMode 527/527.
- Open: kitchen package + GROQ key (re-run checks 1–7 on real data and the real agent); [H] kitchen fps on the 3S, hold-to-talk, pins, snap glyphs, Scene window by hand.

**Sat 2026-09-26 — M5 Array + sellers + checkout (built Fri on `m5`/`m7`, backend contract on `backend-integration`; verified in a Play Without XR Editor, no Simulator)**
Built: `ArrayPlanner` + `PartTool.PlaceArray` (along the last tape, ⌊L/s⌋+1 centred on the run, keeps the reference part's
offset, one notebook entry, one-step undo, clones share the glTF owner), `SellerPanel` (price / delivery sort, recommended
seller + reason, Pay with Visa / Open at seller; the server re-ranks via `POST /parts/{id}/sellers`), `CheckoutPanel` +
`HoldToConfirm` (qty from the array, listings vs packs, shipping, total, Visa test card •••• 1111; a 1 s hold ring →
`POST /checkout` → receipt card, chime, notebook entry; the offline receipt's label shown verbatim),
`AppCommands.PlaceArray / ShowSellers / StartCheckout` (opens the panel only), `RunM5` / `M5Check`.
- [A] PASS **Array**: 8 instances along the 4.200 m gutter tape, all green, worst spacing error 0.00 mm (RunM5, and the demo
  run below with a live-taped 4.2015 m gutter).
- [A] PASS **Hold**: a 0.6 s hold sends nothing (client counter and the mock's request log); a full hold confirms at 1.01 s
  and sends exactly one `POST /checkout`; receipt shown, notebook "purchase" entry. The mock and the real server (Fri,
  kitchen) both return the labelled **offline receipt** (no Cybersource keys), so an AUTHORIZED sandbox payment is untested.
- [A] PASS EditMode: `AppCommands.StartCheckout` only opens the panel; source guard (only `CheckoutPanel` calls
  `PartsClient.Checkout`; no agent action pays); hold timer; seller sort.
- Found live today, fixed: (1) a live gutter tape often snaps its ends to opposite corners of the lip (1.6° off level); the
  row followed it and 2 of 8 hangers hit the gutter back → near-level runs are level (§9). (2) Undoing an array destroyed its
  clones, so Redo could never bring it back → clones are hidden and kept for Redo, the notebook entry returns with its id,
  and the reference's before/after poses are stored in scene-root space (undo/redo at 1:50 stay on the model).
- Not verified: Open at seller only logs in the Editor (`Application.OpenURL` runs on the device).
- [H] Open: sellers and checkout readable and pokeable by hand; the 1 s hold by hand poke / ray pinch (the agent forces the
  pressed state); Open at seller opens the Quest browser; chime and toast in the headset.

**Sat 2026-09-26 — M6 Voice (as built on `backend-integration`)**
The planned realtime voice (xAI realtime over NativeWebSocket with an ephemeral `/voice/token`) was **not built and is not
used**: the backend answers `/voice/token` with 503. Voice is **push-to-talk through the backend**: hold *Talk* (Find parts
or the Scene window) → 16 kHz mono WAV from an explicitly chosen mic → `POST /voice/command` with the app context and the 3
nearest scene photos → transcript + reply (spoken: Groq TTS is refused, the server's edge-tts MP3 is decoded and played) +
actions, which run through `AgentActions` → `AppCommands` exactly like button taps (`start_checkout` only opens the panel).
Typed commands use `POST /agent/command`. The spec's "Plan B" became the plan.
- [A] PASS EditMode: all 10 contract actions handled, unknown ones refused (warning toast, not spoken), none pays; WAV round
  trip; mic pick prefers the built-in mic over AirPods.
- [A] PASS (mock, today): a `say` WAV → transcript "find a gutter hanger" → "Searching the supply shops." + `search_started`
  → Find parts shows the hanger. (Replaces the spec's stub-websocket check.)
- [A] PASS with findings (real server, Fri, kitchen): voice → " Find me a cabinet door handle" → reply + search, MP3 reply
  played. The agent never opens checkout by voice or text (P4 #10), and cabinet pulls return no parts (P4 #15).
- [H] Open: hold-to-talk on the Quest (RECORD_AUDIO prompt, the round trip time; the spec's < 3 s target was for realtime
  `equip_tool`); the spoken reply is audible.

**Sat 2026-09-26 — M7 Real scene + tabletop + take it home**
Built: runtime scene packages (`SceneStreamer`: `GET /scenes` → scene.json with ETag polling → mesh, collider, structure,
cameras; revision swap in place; headset cache; auto-loads `kitchen`; the synthetic facade stays built in; Scene window ▸
next scene / built-in), structure-layer snapping, scale from a known dimension. `TabletopController`: the scene as a 1:50
model on a table in passthrough, swapped behind the mode fade; toggled from the ring (and voice) instead of the spec's
two-hand scale, since the left hand is the menu hand. `TakeItHome`: leaving the world puts one of each bought part on the
table at true size, labelled with quantity and total; re-entering clears it.
- [A] PASS **Kitchen package** (real server, Fri): loads in 1.6–1.9 s (1.68 s in today's session), 199,997 visual tris, 165
  cameras, structure 61 planes / 671 edges / 594 corners / 40 objects; X-flip alignment median 5.4 mm over 309 visible
  corners; revision swap in place; the tools work unchanged on it (tape, level, parts, fit).
- [A] PARTIAL **Tape on the real scene**: the kitchen capture has no window and no hand-measured values, so the spec's check
  can't run as written. Instead: 20/40 structure objects repeat within 5 mm over 3 tapes and land at median 1.6 mm from their
  own `w_m`; the misses are P1 §B (rectangle corners off the scanned surface). Scale from a known dimension (dishwasher, 24″)
  gives ×1.491 against the pipeline's ≈1.47; after it doors o5 / o29 read 391 / 389 mm against `w_m` × 1.491 = 389 mm.
- [A] PASS **Tabletop**: in the demo run the model sits on the table (ground 0.800 m, 1:50, snapping scaled to 0.02); the tape
  taken at 1:1 still reads 1.5003 m, and a new tape across the model window reads 1.5050 m (the same aim at 1:1 also reads
  1.5050: one end snaps to the glass edge 0.10 m deeper); back to 1:1 the scene returns exactly (0.00 mm). EditMode: model
  on the table, same metres, snapping scales, array undo/redo at 1:50.
- [A] PASS **Take it home**: after the purchase, Exit world puts the hanger on the table: 127.0 × 45.0 × 38.0 mm, scale
  1.0000, bottom at 0.750 m (assumed table height), 0.47 m ahead.
- [H] done (Fri late): the kitchen loads in 1.0 s on the 3S and holds 72 fps (250/259 one-second samples, GPU 72 % median).
- [H] Open: the take-it-home part sits on the *real* table (the height is assumed, no MRUK table), tabletop comfort (the
  fade doubles as the vignette), fps with the ring UI and in tabletop, the tools by hand on the kitchen.

**Sat 2026-09-26 — Tool ring (Liquid Glass palm toolbox, commit b2fd73a)**
Built: `ToolRing` + `DialPhysics` (palm-up horseshoe; pinch-drag spin 1:1, coast with the flick's speed (capped), a tick per
item, spring onto one; a tap on a side item spins it to the lens; modes equip on settle, actions fire on a lens pinch, Exit
world asks twice; Undo / Redo at the arc's ends through `EditHistory` in any mode), `AirTools/LiquidGlass` shader, Phosphor
icons, Move as the default mode.
- [A] PASS EditMode: flick physics (a harder flick passes more items, one tick each, settles exactly on one), speed cap,
  short-way spin, drag under the fingers, double tap, Undo reaches the latest edit in any tool / Redo the latest undo / a new
  edit forgets the future, ring layout.
- [A] PASS live (demo run, through the ring's own API with the menu forced open): a tap on Measure spins it into the lens and
  equips it; a −4 rad/s flick coasts one item on to Level (1 tick) and equips it; lens pinches run Notebook (opens, 3 rows),
  Tabletop (on, then off) and Exit world (first pinch arms, second exits); Undo ×3 steps back array → hanger → level reading
  and Redo ×3 restores all of it (8 parts green, 2 tapes, 1 level, notebook ids 1–6 unchanged).
- Found live today, fixed: Redo after undoing an array did nothing (M5 entry).
- [H] Open: palm-up open/close by hand; pinch-drag spin, momentum and ticks (sound, lens pulse, controller buzz); poke on side
  items; the menu holds still while spinning; Liquid Glass legibility in passthrough and in the world; fps on the 3S with the
  LiquidGlass shader.

**Sat 2026-09-26 — MVP demo path end to end (`AgentHarness.RunDemo()`, branch `backend-integration`)**
`Dev/DemoWalkthrough` + `DemoRunner`: the judge's walkthrough as one scripted run on the built-in facade through the real
input paths (the Enter world button, ToolInputHub injection, the ring's Tap / Flick / SpinTo / lens pinch / Undo / Redo with
`PalmMenu.Force`, AppCommands, the seller row's Pay button, the checkout hold); parts and checkout against the mock on :8001,
then the default server is restored. One `[AirTools.Check] Demo.<beat>` line per beat; poll `AgentHarness.DemoResult()`.
- [A] PASS **27/27 beats in 17.9 s**: starts in passthrough → built-in facade (after the kitchen auto-load) → Enter world →
  World in 0.90 s → Move is the default → ring equips Measure → window 1.5003 m, finished by the other hand → gutter 4.2015 m →
  ring equips Level → ledge 2.000° → notebook 3 entries, each with an evidence camera that sees its points and a thumbnail →
  export CSV 3 / HTML 3 rows, uploaded → Find parts "gutter hanger" → take (server part, 127 × 45 × 38 mm) → fascia: back
  face 0.00 mm, tilt 0.00°, green → array 8/8 green → sellers (qty 8, recommended + reason) → Pay row opens checkout (0
  requests) → 0.6 s hold: 0 requests → 1.0 s hold: exactly 1 → receipt + notebook purchase → ring Undo/Redo round trip across
  tools → Exit world (asks twice) → hanger on the table at true size → Tabletop 1:50 → tape 1.500 → back to 1:1.
  First run: 24/27 (the array and redo fixes above, plus a harness check bug).
- [A] Regression live: M2 25/25, M3 22/22, M4 22/22, M5 6/6 + receipt; EditMode **549/549**; zero console errors in the Play
  sessions.
- Scope note: hands and controllers aren't simulated here (no Simulator on a second Editor), so every "press" is the harness
  calling the same method a poke, pinch or trigger ends in. Everything by hand is [H] (above).

**Sat 2026-09-26 — presence S1: grow in / shrink out + site mat (branch `feat/grow-in`, Play Without XR)**
`TransitionMath` (pure), `TransitionDirector`, `RevealField` + `AirTools/SceneReveal`, `SkyDome` + `AirTools/SkyDome`,
`TabletopController` split into pure poses + `Commit`, `RealTable` / `SiteMatSpec` / `SiteMatTracker`, `SpawnMarker`
("You are here" pin → `AppCommands.StepIn`), ring item **Step in**, `TakeItHome` beside the model, `TruthBar`.
- [A] PASS EditMode **574/574** (+25): grow pose exact at both ends (1e-4 m / 0.01°), anchor distance strictly falls,
  s(0.5) = √0.02; Passthrough → World 1.20 s and Tabletop → World 1.40 s with no fade and ≤ 1 Swapped; Reduce motion →
  the fade (black at the midpoint); `RevealField.Contains` = the shader formula on 1000 random points; window tape
  1.500 ± 0.010 after stepping in; table chain mat > raycast > assumed; mat frame level within 5° for any QR axis convention.
- [A] PASS live: TableTop → StepIn sampled at 5 Hz: scale 0.0200 → 1.0000 rising in every sample, phases grow → sky → idle,
  World in 1.41 s; Exit → shrink 1.60 s back onto the table (within 0.56 mm of the locked mat's spot); RunM2(3) 15/15 after
  stepping in; RunM2/3/4 25/25, 22/22, 22/22 on the unlit facade; **RunDemo 27/27** (world.enter now a 1.20 s pour,
  tabletop.off a grow, root back within 0.00 mm); captures in `SpikeData/growin/`.
- [H] Open: no black flash on grow / shrink / pour / scan; fps ≥ 71 during them (VrApi); comfort 3/3 testers; the mat QR
  locks within 3 s (log the `Site mat QR axes` line once); model on the mat within 1 cm (QR) / 2 cm (raycast); take-it-home
  parts rest on the mat; truth bar within 5 mm over 1 m; the "You are here" pin by poke and by pinch.

**Sat 2026-09-26 — UX Wave 0: rails, trust, legibility (branch `feat/ux-wave0`, Play Without XR)**
- W0.1 scale chip in the Scene window ("Scale ✓ set (×1.49)" / "Scale not set · tape a door to set it") + runbook
  step; W0.2 floor-only teleport ("Aim at the floor to walk") + floor disc; W0.3 on-cursor save chip, system-gesture
  guard, a finishing pinch never counts toward the double pinch; W0.4 parked tape / parked part; W0.5 SfxPlayer +
  FeedbackEvents + InputHints; W0.6 em type tokens (16 floor), #2563EB + white labels, ring label halo; W0.7 Enter
  pill from the head, windows 0.45 m / 20° down, one window slot (+ Back), side slots; W0.8 heads-up toast + reply
  card (overlay, gaze-pitch, 2 / 4-line wrap, duration by length); W0.9 display copy (Copy.cs, FitReport verdict /
  reason / action, SurfaceNames, notebook rows, error copy, seller pack-total fix); W0.10 controller ring closes after
  a pick, blocks only presses that start on it, A = global undo; W0.11 dynamic-resolution floor 1.0, FFR Low in the
  world, upright labels, no per-frame allocs. Search resilience: a tape search that fails retries once without it.
- **[A]** EditMode 649/649 (new CopyTests, FeedbackTests, ErgonomicsTests incl. A1 16 dmm floor over Main.unity,
  A4 ≥ 4.5:1, A7–A10); RunDemo 27/27; RunM2 25/25, RunM3 22/22, RunM4 22/22; hcheck tests 23/23. Captures:
  `SpikeData/uxw0/`. Flags D1/D4/D5/D6 at today's behaviour (§9).
- **[H]** open: docs/headset-checklist.md "UX Wave 0 [H] checks".

**Sat 2026-09-26 — B1 "the agent measures for you" + B3 measured mandate, app side (branch `feat/b1-b3`, Play Without XR)**
- §1 context `site` + `scale`; B1 `survey` / `check_slope` / `show_survey` / `stop_survey` (SurveyPlanner, SurveyRunner through
  the real SnapService + MeasureTool, one `/agent/observe` per request, SurveyCard, label diet, grouped undo); B3
  `/checkout/prepare` on open / change / stale nonce, check rows, Pay gated on `all_ok`, hold proof (cart_hash, hold_nonce,
  `HoldToConfirm.HeldMs`), 400/409/422 handling, receipt mandate chain + evidence photo, `show_limits` wrist chip, panel
  limits (`/commerce/limits`). Contract: `docs/handoff/p4-b1-b3/README.md` §1–§7.
- **[A]** EditMode 670/670 (+21: SurveyTests, SurveyLabelTests, MandateCheckoutTests); RunDemo 27/27 against :8000/:8001
  (prepare 404 → legacy checkout). Live against the patched server (:8002) via `BackendHarness.RunB1` / `RunB3`: kitchen
  "measure every cabinet door" → 18 tapes in 4.3 s, 4 unverified, one observe → card 8 groups (6/4/2/2/1/1/1/1), 12 labels
  visible, one undo −18 / redo +18, verified 14/18 with every group median within 5 mm; facade window 1.5000 × 1.2000 m;
  "check the gutter slope" → e89, 4.20 m, fall 0 → "Flat … water will pond" + note; "stop" after 1.5 s → aborted 6/18;
  checkout prepare $23.84 / 8 packs, 0.6 s hold sends nothing, 1.0 s hold → one /checkout (hold_ms 1000) → offline receipt
  with the mandate chain; a $20 panel limit → ✗ row, Pay off, hold sends nothing. Captures: `SpikeData/b1b3/`.
- **[H]** open: survey readable on the headset (labels, amber), limits chip on the wrist, Pay hold on the device.
| Sat 09-26 afternoon | **Grok integration + UX decisions + de-clutter, all on `backend-integration` (tags `green-0926-grok` → `green-0926-declutter`), APK on the Quest 15:40 against `~/airtools-backend-demo` :8004 (grok-all + hand-off p4-grok, online).** D1–D7 applied (Measure on entry, Line auto-save + lens Area toggle, ray on windows, commit on pinch, imperial single-unit + chip, white primary / ink, DemoMode + reset + presenter :8766). W1.2 commit-what-you-saw. Grok G1–G4: all 37 api.md §5 actions (context, notebook types, finish swaps, overlays, panels + in-app QR, job/coach rails). Concave polygons (click order, reflex angles, crossed outlines refused). De-clutter S1–S10 (DC1–DC7): one heads-up line (toasts/replies/job strip), dock + pill yields + receipt Take it home, 6-item ring + More, coach card left slot, wrist strip, one Grok layer, one 12-label pool; census guard `DeclutterTests` (debts left: #17, E1) | EditMode 1372 + 1 inconclusive; RunDemo 27/27; GrokCheck g1 13/13 (kitchen + Zabel), g2 15/15 kitchen + 11/11 facade, g3 14/14, g4 19/19; RunM2/M3/Concave green; live B1 10/10 + B3 7/7 on :8004. Zabel r3 + hospital-bg scenes installed; hfbox Funnel fixed (tailscale control reconnect). Open: P4 follow-ups F1–F4 (docs/handoff), [H] ring feel, wrist strip, white-fill glare, Zabel spawn height / orange flecks |
| Sat 09-26 evening | **UI round 3 + end-to-end replace, on `backend-integration` (tags `green-0926-ui3` → `green-0926-e2e`), APK on the Quest 19:03; demo server :8004 on `app-handoff-vrnext` + p4-e2e.** Four parallel lanes (offline compile/test, one Editor gate each batch): palm menu hold fix; scene load watchdog + own-clock HTTP everywhere + server probe; Model view (model only, fitted scale, switcher over kitchen / Zabel / hospital / GT canopy / pavilion / tower / facade); placement editor (Adjust, 6 DoF, slots A–D, model swaps); measure-and-replace (remove → scale from the opening → tape the gap → gap-fitted search → put it in → cycle models → undo → put it back). Kitchen parts from the backend's `scenes-glb` branch (dw1 / bc1 / fr1 / rg1 with cavities) are served on :8004. Gate: EditMode 1629 + 1 inconclusive; RunDemo 27/27; ModelCheck 14/14; PlacementCheck 19/19; E2E kitchen 10/10 (Whirlpool tight in the 876 mm opening, 3 models cycled) and synthetic-facade-parts 9/9; presenter beat "replace" 10/10. Captures `SpikeData/gate/` (Retry, Model view gym, Adjust panel, kitchen with the Whirlpool). Fixed at the gate: the adjust panel's Done is the title row's Close; the site-thumbnail request uses HttpDeadline; RunDemo restores the server override; cycling back cancels its undo step; the replace beat goes to the kitchen from a scene without parts. The Editor was SIGTERMed once (Play Without XR resets on relaunch → the Simulator started; swap 8.8/10 GB). **[H]** open: palm-down close, library-launch probe, switcher cards at table height, Adjust pads, the voice replace flow with Whisper ("34 and a half"). |
| Sat 09-26 late | **Round 4 on `backend-integration` (tag `green-0926-round4`), APK 23:43 (installs when the headset is next idle); :8004 on `catalog-on-asset-mode` (42c0899) with Grok `grok-4.20` as the agent.** Merged and gated:
- **Window asset flow:** openings on the wall face, a search sized to the opening, Grok-built frame, snap into the opening, Size & finish. AssetE2E 13/13 live.
- **Liquid glass on every surface:** GlassDock 4/4, census stray 0.
- **Catalog:** 7th ring item; no tape needed; categories by place from `GET /catalog` (60 categories over 11 environments, warmed); glass keyboard with lazy search. CatalogCheck 8/8 live.
- **Settings:** Talk Hold/Toggle/Auto (TalkStyleCheck 9/9); 3D models HF/LLM+CAD/Auto with Compare (AssetModeCheck 20/20 on :8009; the Hunyuan download fix; no OpenSCAD on this Mac, so LLM+CAD = template).
- **Model view:** centred float plus a wide endless wheel. ModelCheck 24/24.
- **Site-scoped items:** SiteScopeCheck 8/8 and 9/9 via Model view.
- **Totals:** EditMode 1923 + 1 inconclusive; E2EAuto kitchen 11/11; RunDemo 28/28.
- **Fixed at the gate:** AssetGenEditorTests (the opening's face from both sides, the wall-plane fit), the finish-colour tolerance, merge seams (PartsBrowser, PartsClient, AgentContext, PartSpec), and CatalogCheck / ModelCheck aiming from a standing eye (Play Without XR has none). |
| Sun 09-27 early | **Round 5 on `backend-integration` (tag `green-0927-editview`); :8004 on `texture` (39e4825).** Merged and gated:
- **Isolated Edit view** (`feat/edit-6dof`): the world dims and other UI closes; the part flies to the centre. 12 pinchable arrows (turn/tilt/roll, right/up/out); turn by hand; colour shades over the texture; Cancel · Move · Save back into the room.
- **New and placed parts:** a new part orients first, then places and auto-saves. A placed part opens Edit · View similar · Delete on a grip squeeze or a 0.6 s hold. EditViewCheck 21/21.
- **Textures** (`feat/texture`): product photo cut-out (border flood or rembg u2netp), photo on the front only, sides coloured from the product. `warm --retexture` over 230 models at $0; `--remake-flat` rebuilt 24 flat-front CAD appliances (24/24, $1.25).
- **Grok + OpenSCAD in the headset UI:** "Replace the dishwasher / the stove…" in LLM+CAD mode places CAD models facing out of the gap: the photo front, "Fits the gap" (captures in `SpikeData/gate/cad/`).
- **Fixed at the gate:** back on the built-in facade, Home (and a demo reset) still went to the kitchen's spawn. RestoreBuiltIn now moves Locomotion.Home, with a test.
- **Totals:** EditMode 2027 + 1 inconclusive; RunDemo 28/28; CatalogCheck 9/9; PlacementCheck 19/19. |
