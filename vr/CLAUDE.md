# AirTools — agent guide

AirTools is a VR app for **Meta Quest 3S**, built in **Unity 6000.3.24f1 (6.3 LTS)** on an Apple Silicon Mac.
Build spec: `docs/SPEC.md` — source of truth (milestones, contracts, acceptance checks, lanes, decisions log). Read §0 and §5 first.
Background only: `docs/ref/writeup.md` (pitch/demo) and `docs/ref/implementation-plan.md` (whole-team plan). SPEC.md wins on conflicts.
Before starting work, check SPEC §5 for which lane you are in; only the XR lane may enter Play mode or use XR Operator.

## Stack (verified working 2026-09-24 — do not change versions without being asked)
- Meta XR SDK All-in-One 205.0.0 (Core, Interaction, Interaction.OVR, Haptics, Platform, MRUK, Audio, Voice)
- glTFast (`com.unity.cloud.gltfast`) 6.20.0 — runtime GLB loading (added in M0)
- Unity OpenXR Plugin 1.18.0 — OpenXR on Desktop (for Simulator/Play mode) and Android (device); Meta XR feature group enabled
- Input System 1.20.0, URP 17.3.0, `com.unity.pipeline` 0.7.0-exp.1 (Unity CLI bridge)
- Active build target: Android. Editor Play mode uses the **Desktop** OpenXR settings + **Meta XR Simulator** (standalone app, /Applications/MetaXRSimulator.app)

## Your three control channels
1. **Unity CLI → Editor** (authoring, compile, play, console). Server auto-starts in the Editor (port file: `Library/Pipeline/.unity-pipeline-port`).
   - `unity command` lists all commands; `unity command <name> --arg value`.
   - Key commands: `recompile` / `recompile_status`, `console` / `console_status`, `editor_play` / `editor_stop` / `editor_status`,
     `get_scene_hierarchy`, `create_gameobject`, `add_component`, `set_component_properties`, `set_serialized_field`,
     `instantiate_prefab`, `create_prefab`, `save_scene`, `screenshot`, `capture_game_view`, `run_tests`, `eval`, `package_*`, `build`.
   - Full reference: `Library/PackageCache/com.unity.pipeline@*/Documentation~/commands/*.md`.
2. **`meta-xr-operator` MCP** (runtime, only while in Play mode with Simulator active): head/controller poses, controller input, hand input,
   composited screenshots, scene queries (`get_scene_root_objects`, `get_children`, `get_world_pose`, `find_interactables`).
3. **`meta-xr-unity-runtime` MCP** (AI Agent Bridge): Runtime Optimizer / Immersive Debugger / Hands optimizer recommendations.

Project skills in `.claude/skills/meta-xr-operator-*` cover runtime usage, coordinates (OpenXR is right-handed −Z forward; Unity left-handed +Z forward), grab, poke, test mechanics and the Unity workflow. Read the relevant one before runtime work.

## Iteration loop (every feature)
1. Plan against `docs/SPEC.md`; break into small verifiable steps.
2. Edit C# files directly on disk; build scenes/prefabs via Unity CLI commands (never hand-edit .unity/.prefab YAML).
3. `unity command recompile` → poll `recompile_status` → `unity command console` — **zero errors before continuing**.
4. `unity command editor_play` → drive and verify with Meta XR Operator (data first, then a composited screenshot).
5. `unity command editor_stop` before any persistent edit (Play-mode changes are lost; Operator disconnects).
6. Add/extend EditMode or PlayMode tests for logic (`run_tests`); commit a small, working increment.
A feature is "done" only when it compiles clean, is verified at runtime in the Simulator, and its tests pass.

## Project conventions
- All project code/assets under `Assets/AirTools/` using the layout in SPEC §3.1; namespace `AirTools.*`;
  assembly definitions `AirTools.Runtime`, `AirTools.Editor`, `AirTools.Tests.EditMode`, `AirTools.Tests.PlayMode`.
- Append results to SPEC §10 Progress log at the end of each milestone; record new team decisions in SPEC §9.
- Interactions via Meta **Interaction SDK** (grab/poke/ray, hands + controllers); prefer Building Blocks / ISDK prefabs over custom input code.
- Real-world units: 1 Unity unit = 1 metre. On-screen labels show one unit, imperial-primary for the HackGT build, with a units chip in the Scene window (decision D2, SPEC §9); data, exports, notebook `Label`s, requests and `[AirTools.Check]` numbers stay metric.
- Target Quest 3S performance: 72–90 Hz, URP Mobile asset, single-pass instanced, avoid per-frame allocations.

## Known gotchas (learned in M0, see SPEC §9/§10)
- **Simulator deadlock:** occasionally `editor_play` hangs forever inside `xrCreateInstance` (Meta XR Simulator gRPC shutdown/startup). Unity sits at 0% CPU, and CLI calls time out. Only fix: restart the Unity Editor. Don't cycle Play/Stop rapidly.
- **Controllers:** read them through `OVRInput`/ISDK, which needs an `OVRManager` in the scene (Building Blocks' camera rig provides one). Unity's `XR.InputDevices` sees no controllers under the Simulator. `OVRInput` pose = OpenXR grip pose + fixed offset (60° pitch, ≈ −2 cm y, +4.6 cm z).
- **Stereo:** keep OpenXR in Single Pass Instanced. Custom shaders need the stereo-instancing macros or they will be invisible in XR.
- **Device logs:** the Quest logcat buffer is tiny, so stream it to a file while testing: `adb logcat -v time Unity:I VrApi:I '*:S' > SpikeData/quest.log` (VrApi lines give FPS / GPU%).
- **Parts server:** the real one is the backend repo's FastAPI server on **:8000** (`uv run uvicorn server.app:app --host 0.0.0.0 --port 8000`; scenes need no keys, parts/voice/vision need `GROQ_API_KEY` in its `.env`). The app defaults to `http://localhost:8000` (Editor) / `http://127.0.0.1:8000` (device). **Mock** (mirrors the backend's docs/api.md, keyless): `python3 tools/mock_parts_server.py --port 8001 --scene-dir tools/.scenes [--voice-transcript "find a gutter hanger"] [--sandbox]` (request log `tools/.mock_server_data/requests.log`); contract tests `python3 -m unittest tools/test_mock_parts_server.py`; fixtures from `python3 tools/make_part_fixtures.py`. Point a Play session at another server with `BackendHarness.Server(url)` (persists in PlayerPrefs: reset with `Server(null)`).
- **Networking:** the Quest and laptop must share the iPhone hotspot. On campus Wi-Fi use the USB tunnel `adb reverse tcp:8000 tcp:8000` + `http://127.0.0.1:8000`. Override on device without rebuilding: `adb shell am start -n com.airtools.quest/com.unity3d.player.UnityPlayerGameActivity -e server http://<ip>:8000 -e site kitchen` (or push `server.txt`: URL, then site, to `/sdcard/Android/data/com.airtools.quest/files/`).
- **Mic:** choose the device explicitly. The first `Microphone.Start` after an Editor relaunch can block on the macOS permission prompt.
- **Editor Play needs `runInBackground`** (now on in Player Settings); otherwise an unfocused Editor pauses the player loop (`OnApplicationPause(true)`, frameCount stuck).
- **Operator after a Unity restart:** the `meta-xr-operator` MCP proxy doesn't reconnect by itself (calls hang or say "Server unavailable"). Ask the user to reconnect it via `/mcp`.
- **Operator poke:** move the controller **grip** pose (the Unity anchors follow grip, not aim). ISDK controller poke tip ≈ grip + (−0.015, −0.054, −0.065) m in OpenXR at identity rotation. Operator poses are in tracking space, so subtract the rig pose (rig at (0, 0, 4), yaw 180 in Main).
- Selecting a `PokeInteractable` in the Editor makes Meta's inspector gizmo spam NullReference/GUIClip errors (editor-only). Clear the selection after scripted edits.
- Rebuild scenes with the menu scripts, never by hand: **AirTools ▸ Build Synthetic Facade**, **AirTools ▸ Build Part Catalog**, **AirTools ▸ Wire Main Scene** (idempotent; Wire also rebuilds the catalog).
- Android package ID is `com.airtools.quest`.
- **See what the wearer sees:** `tools/quest_stream.sh` (scrcpy over USB, view-only; `-r` records demo footage to `Recordings/`, `-w` streams over Wi-Fi on a shared network, views `center` | `eye` | `both`). Install with `adb install -r <apk>` over USB.

## Agentic testing (M2+)
- **EditMode first:** scenario tests run the real tool on the real facade with aim noise (`AirTools.Dev.MeasureScenarios`). `run_tests --mode editor --filter AirTools --filter_type assembly` (use lowercase modes: `editor` / `playmode`).
- **Live app:** in Play mode drive the real input path through `AirTools.Dev.AgentHarness` via `unity command eval`: `Status()`, `OpenWorld()`, `Equip("measure")`, `Click(x,y,z)` / `ClickFrom(ox,oy,oz,x,y,z)` (scene-root space), `Aim`, `Drag`, `Finish()`, `Undo()`, `Clear()`, `Labels()`, `RunM2(seeds)`; M3: `Equip("level")`, `RunM3(seeds)`, `Notes()`, `ShowNotebook(true)`, `PanelText()`, `ShowRow(i)`, `Export()`; M4: `RunM4(seeds)`, `Parts()`, `PartInfo()`, `FindPart(q)`, `Take(i)`, `TakeFromCatalog(id)`, `PlaceFrom(ox,oy,oz,x,y,z)`, `SetFinish(name)`, `ServerLoad(q)` then poll `ServerCheck()` (needs the mock server). Results are logged as `[AirTools.Check]` lines.
- **Real controller input (Operator):** the ISDK controller ray follows the **grip** pose (not aim), pointing ~60° below grip-forward.
  Recipe: after Play starts, wait ~10 s for the Operator to attach; set right grip to (0.2, 1.3, −0.3) with identity rotation → `AgentHarness.CalibrateRightRay(0.2f,1.3f,-0.3f)`
  → `GripForTarget(0.2f,1.3f,-0.3f, tx,ty,tz)` gives the OpenXR quaternion that aims the ray at a scene point → set grip → verify with `RightRayHit()`
  → `openxr_set_controller_input(right, Trigger, 1, auto_release, 0.25)` places a point; `B` finishes. Toolbox buttons: poke with the grip at identity rotation
  (poke tip ≈ grip + (−0.015, −0.054, −0.065) OpenXR), moving along the button's press direction.
- **Visual evidence when the Operator capture fails:** `unity command capture_game_view --source screen --save_path Temp/x.png` (writes to `Assets/Temp/`; move it to `SpikeData/` and delete `Assets/Temp`). Move the view by setting the `OVRCameraRig` transform via eval.
- **After every recompile check `console_status`/`console` for `error CS`.** A failed compile leaves the old DLLs in place: tests "pass" against stale code and Play mode silently refuses to start.
- **Fascia shots:** from the ground the gutter's front lip hides the fascia; place hangers from a raised origin (e.g. `(0.5, 7, 1.5)`) or move the controller within ~30 cm of the fascia (Operator). Poke buttons: compute the button's world pose and press direction (`transform.forward`) via eval, convert to OpenXR tracking space, start 4 cm in front and move 5 cm along it over 0.6 s. The ISDK ray is off while the controller is near poke buttons, so move away before aiming.
- **UI:** follow `docs/UI.md` — build UI only with `UiBuild` in the scene builder, colours/sizes/durations from
  `UiTheme.Current`, behaviour on `GlassButton.Clicked`. **AirTools ▸ Build UI Assets** regenerates the font atlas,
  materials and theme (Wire runs it). New symbols → add to `UiAssetsBuilder.Charset`.
- **Palm menu in the Simulator:** hands can't be simulated; open it with `Services.Get<PalmMenu>().Force(true)` via
  eval (set `controllerAnchor = null` to place it in front of the camera). **Avoid the left Menu button and
  switching to passthrough in the Simulator** — both crashed the Editor (native segv in OVRPlugin
  `HandleSingleOpenXREvent`); test those on the headset.
- `recompile_status` can answer `up_to_date` instead of `completed`; poll for either.
- **Play-mode `Destroy` is deferred:** anything removed and re-tested in the same eval must be deactivated first (PartTool does this).
- **One MonoBehaviour per file, named after the class** (EditMode test enforces it), or components go missing when the scene reloads.
- **The Simulator can crash the Editor** (SIGSEGV in `SIMULATOR.so` telemetry) or deadlock it. Commit before long Play sessions; after a relaunch answer the scene-backup prompt "Yes" and ask the user to reconnect the Operator via `/mcp`.

## Backend integration (airtools-drone-backend; report + contract findings: `docs/backend-integration.md`)
- Scene packages load at runtime (`SceneStreamer`, auto-loads `kitchen`; Scene window ▸ Next scene / Built-in). Synthetic
  packages with exact ground truth: **AirTools ▸ Export Synthetic Scene Packages** → `tools/.scenes/` (serve with the
  real server's `SCENE_DIR` or the mock's `--scene-dir`). Revision-named files are cached forever in
  `persistentDataPath/scenes/`: delete that folder after regenerating a fixture under the same revision.
- Checks (Play mode): `BackendHarness.LoadSite(site)`, `SceneStatus()`, `CheckAlignment()` (X-flip), `CheckObjects(3)`,
  `Snap(x,y,z)`, `SetScale(m)`, `Ask(x,y,z)`/`AskResult()`, `Command(text)`/`Voice(wavPath)`/`Agent()`.
  B1/B3 (a server with the hand-off endpoints, e.g. `:8002`): `RunB1(server, "kitchen"|"facade"|"stop"|"all")`,
  `RunB3(server, limit)`, `RunB3Limit(server)`, poll `B1B3Result()`. Don't edit scripts during Play: a reload nulls the
  structure layer and orphans tool state. Make a test
  WAV: `say -o c.aiff "find a gutter hanger" && afconvert -f WAVE -d LEI16@16000 c.aiff c.wav`.
- Voice is tap or hold Talk (`VoiceClient` + the pure `VoiceCapture`: `TalkGesture`, `VoiceActivity`, pre-roll, trim;
  tunables in `VoiceClient.settings`). `BackendHarness.VoiceCheck()` (pure, no Play) and `VoiceVad(wav, press, at)` run the
  pipeline on a simulated clock; in Play `VoiceSim(wav, press, at, send)` plays a WAV "into the mic" (press < 0.35 s = a
  tap), `VoiceState()`, `TalkButtons()`, `VoicePress(bool)`. Each utterance logs `Voice utterance mode=… stop=… bytes=…`.
- Snapping on packages = the structure layer (`StructureSnapper`, R6 numbers); keep radii × `SnapService.Scale`.
  Calibration lives on `SceneRoot.Content`; tabletop on `SceneRoot`; tools implement `RescaleAll(factor)`.
- Runtime glTF must use `UrpMaterialGenerator` (glTFast's shader graphs are stripped from player builds).
- A second Editor on a git worktree may use **AirTools ▸ Dev ▸ Play Without XR** (no Simulator session) to verify runtime
  code by eval; its `persistentDataPath`/PlayerPrefs are shared with the main Editor (same product name).

## Do NOT
- Edit anything under `Library/`, `Temp/`, or `Packages/packages-lock.json` by hand.
- Remove the `com.unity.modules.*` entries from `Packages/manifest.json` (Meta Core needs AssetBundle and others; removing them broke compilation once).
- Change package versions, build target, XR plug-in or OpenXR feature settings unless the task requires it.
- Commit the `meta-xr-unity-runtime` bearer token or any credentials. Unity writes that token into `Assets/Resources/DevAgentSettings.asset` (`accessToken:`); the repo copy is blank. After cloning, run
  `git update-index --skip-worktree Assets/Resources/DevAgentSettings.asset` so the local token is never staged.
- Trigger modal dialogs where avoidable — the Editor is not in automated mode and a modal will block the CLI; if a command hangs, tell the user a dialog may need dismissing.
