# R2: how practitioners get a general LLM to build 3D models

Researched 2026-09-25 for the P4 parts server (`server/assets.py`, contract in `docs/api.md` §2).
Question: can a general LLM (Groq `qwen/qwen3.8-27b` first, xAI `grok-4.7` as fallback), given
the right tools, build `model.glb` for our parts (window AC, condenser, water heater, sink,
faucet, fridge, solar panel, gutter hanger...) at the published W×H×D, without a dedicated 3D
model. Sources are practitioner material (GitHub, HN, blogs, product docs, press), not papers;
R1 covers papers.

Tags: **[V]** verified by fetching the repo/post/docs or checking PyPI/dnf myself, **[S]**
secondary (press, aggregator, search snippet, or a fetch summary I couldn't cross-check),
**[I]** my inference.

## TL;DR

1. **Everyone who gets usable results runs a render → look → fix loop.** One-shot code is
   consistently reported as wrong in ways that "look mostly fine until examined closely".
   Tool access is not the bottleneck; spatial reasoning is. [V: ModelRift, HN threads, davesnider.com]
2. **The winning tool shape is small and the same everywhere:** `execute(code)`,
   `render(views)` → PNG, `inspect/measure` (bbox, volume, validity), `export`. blender-mcp,
   the OpenSCAD MCP servers, build123d-mcp and CADGenBench's baseline all converge on it. [V]
3. **Numeric checks before pictures.** build123d-mcp's own guidance says measure first and
   render only once measurements pass ("a failed boolean leaves counts unchanged"); it lifted
   CADGenBench validity from 88 % to 100 % for the same model. [V README/llms.md; score is the repo's claim, S]
4. **A 27B open model is weak at real CAD APIs.** CadBench (May 2026) measured Qwen 3.5 27B at
   0.336 valid-program rate and ~0.01 IoU on CadQuery vs 0.81 / 0.31 for Claude Opus 4.7. [S: fetch summary of arXiv 2605.10873]
   Our parts are much simpler than those mechanical parts, but it says: don't make Qwen write
   OCCT-flavoured code. Make it fill in a **small declarative spec** that *our* code compiles. [I]
5. **Dimensions must come from text, not vision.** PrintPal's image-to-CAD docs: "vision models
   aren't measuring tapes", so exact mm belong in the prompt. We already have `dims_mm`. [V]
6. **Photo grounding works as a comparison target, not a measurement.** PrintPal, img2threejs
   and ModelRift all put a render next to the reference and ask the model what differs, capped
   at a few passes (PrintPal: max 2 automatic passes). [V]
7. "Grok makes 3D": real but demo-grade. "Grok in Blender" (Musk, 2026-08-06, Grok 4.5 +
   MCP + bpy, 86k-tri rigged spaceship) is a showcase, not an API feature, and seems to be the
   same blender-mcp pattern. [S: press only; x.com returned 402]

## 1. Tool / MCP servers

| Tool | What it is | Tools exposed to the LLM | Language | Licence / cost | Usable by us offline, free? |
|---|---|---|---|---|---|
| **ahujasid/blender-mcp** (now PyPI `mcp-for-blender`, ~29k★) [V] | Blender add-on (socket :9876) + MCP server | `get_scene_info`, `get_object_info`, `get_viewport_screenshot(max_size)`, `execute_blender_code`, `bpy_api_lookup`, `describe_node_type`, `export_scene` (GLB/FBX); Poly Haven search/preview/download/`set_texture`; Sketchfab; Poly Pizza; Hyper3D Rodin text/image; Hunyuan3D | bpy | MIT; Blender GPL | Yes for the core (execute + screenshot + export). Sketchfab/Poly Pizza/Rodin/Hunyuan Cloud need keys/accounts (out). Has telemetry: call `disable_telemetry`. `BLENDER_MCP_SAFE_MODE=1` blocks file/network/process access in scripts. |
| **fboldo/openscad-mcp-server** [V] | OpenSCAD render server + an "iterative modeling skill" | `render_scad_png` (iso/front/back/left/right/top/bottom or custom camera), `export_scad_stl` | OpenSCAD | MIT | Yes (needs OpenSCAD CLI). Skill = SCAD → PNG → critique → refine. |
| **jabberjabberjabber/openscad-mcp** [V] | Same idea, fastmcp | `create/show/save_openscad_script`, `view_render` (7 angles, base64 PNG "optimized for vision models"), `export_model_to_stl` | OpenSCAD | MIT | Yes |
| other OpenSCAD MCPs (petrijr, quellant, rahulgarg123, jkoets, jhacksman) [S] | validate / render / export wrappers | similar | OpenSCAD | mostly MIT | Yes |
| **build123d-mcp** (Wyrd-Group; pzfreo fork, Apache-2.0) [V] | Persistent build123d session | `execute`, `session_state`, `render_view` (PNG/SVG/DXF, clipping, labels), `measure` (volume, topology, bbox, inertia), `validate` (manifold/watertight), `design_audit`, `verify_spec`, `clearance`, `interference`, `cross_sections`, `shape_compare`, `import_cad_file`, `search_library`/`load_part`, `last_error`, `repair_hints`, `workflow_hints`, `save/restore/diff_snapshot`, `reset`, `export` (STEP/STL/DXF/SVG, **no GLB**) | build123d (Python/OCCT) | Apache-2.0 | Yes: `uv tool run --python 3.12 build123d-mcp@latest`. We'd add GLB ourselves (build123d has `export_gltf(..., binary=True)` [S docs]). |
| Casys-AI/mcp-build123d [S] | build123d MCP with mass props, STEP/STL/**GLTF** export | execute/export/measure | build123d | see repo | Probably |
| CadQuery MCPs (rishigundakaram, bertvanbrakel) [S] | generate + verify CadQuery | execute, verify, export | CadQuery | see repos | Probably |
| FreeCAD MCPs: neka-nat/freecad-mcp, yuri-schmaltz/mcp_freecad (53 tools incl. screenshots, parts library), gchen19/AnkusDrive (280+ tools), contextform, tspspi [S] | add-in bridge into FreeCAD | primitives, booleans, pad, screenshots, FEM | FreeCAD Python | mostly MIT/LGPL | Yes but heavy; nothing we need over build123d |
| Fusion 360 MCPs (Joe-Spencer, frankhommers, faust-machines, ...) [S] | add-in inside Fusion | session control, code exec | Fusion API | free code, **Fusion licence/account** | No (account, Windows/mac) |
| Onshape MCPs (hedless, altendky) [S] | REST API wrappers | features, parts | Onshape API | **account** | No |
| **KittyCAD/mcp** (Zoo MCP) [S] | Zoo's modelling/file/Text-to-CAD tools | text-to-CAD, conversions | KCL | needs Zoo API token | No (account) |
| **img2threejs** (Apache-2.0, ~16.8k★) [V] | Agent skill: one photo → procedural three.js factory + `ObjectSculptSpec` JSON | staged passes blockout → structural → form → material → surface → ...; each pass renders and compares with the photo; "scripts enforce, the model judges"; reference-camera block for camera matching | TypeScript/three.js + Python gates | Apache-2.0 | Yes (host agent is Claude Code/Codex/OpenCode; GLB export plugin uses a hosted TRELLIS space) |
| **CADGenBench baseline** (huggingface/cadgenbench, Apache-2.0) [V] | reference agent for the benchmark | writes build123d, renders STEP with PyVista/VTK **headless**, reviews renders, loops until valid | build123d/CadQuery | Apache-2.0 | Yes: good template for a headless render loop |
| threedle/LL3M [V] | multi-agent Blender code writer (plan, BlenderRAG retrieval, code, critic) | n/a | bpy (Blender 4.4) | academic/eval licence; **hosted server discontinued** | Ideas only |
| SynapsCAD (ierror/synaps-cad) [S] | desktop OpenSCAD editor + viewport + LLM chat (Rust/Bevy) | n/a | OpenSCAD | open source | Not needed |

## 2. Products

| Product | What | Language / output | Cost | Usable by us? |
|---|---|---|---|---|
| **Adam / CADAM** (YC W25) [V] | web text/image → parametric OpenSCAD, parameters surfaced as sliders, OpenSCAD WASM in browser, BOSL/BOSL2/MCAD bundled | OpenSCAD → STL/SCAD/DXF | GPLv3, BYO key (Claude default; OpenRouter/OpenAI/Google supported). Founders: "Gemini 3.1 Pro performed surprisingly well" [S] | Ideas + prompts; GPL so don't vendor code into the server |
| **Zoo Text-to-CAD / Zookeeper** [S] | text/image/sketch → KCL parametric CAD; ranked #2 on CAD Arena (19/20 valid) | KCL → STEP/GLTF/etc. | free tier inside Zoo Design Studio; API/MCP metered; **account + token** | No (account) |
| **PrintPal image-to-CAD** [V] | photo/sketch/spec-sheet screenshot → OpenSCAD; auto-renders a **4-view technical sheet**, self-critiques against the reference, **max 2 auto passes** | OpenSCAD | commercial | Pattern only |
| **ModelRift** [V] | OpenSCAD agent with human visual annotations on the render; ran the "Pantheon" OpenSCAD benchmark | OpenSCAD, multicolour 3MF | commercial | Pattern only |
| **Backflip AI** [S] | mesh/scan → feature-tree CAD (Fusion add-in + web, GA Aug 2026, ~$10/part) | foundation model, not an LLM writing code | paid | No |
| **Meshy 3D Agent** [S] | chat agent that plans, searches references, then chains Meshy's own generate/texture/rig; "Auto Split" into parts | neural mesh, LLM only plans | paid/account | No; confirms "LLM plans, specialist makes geometry" |
| Tripo [S] | image/text → mesh; no conversational layer | neural mesh | account | No (already dropped) |
| Grok3D (grok3d.art), "Grok + Tripo" YouTube, Meshy+Grok 3 blog [S] | Grok Imagine makes an image, Meshy/Tripo make the mesh | neural mesh | accounts | No: Grok isn't writing geometry here |
| "Grok 3D Modeler" (Poe bot) [S] | prompt → three.js HTML | three.js | Poe account | No |
| Spline AI [I] | text → stylised 3D in Spline's editor | proprietary | account | No |

**Leaderboards worth knowing:**
- **CAD Arena** (cadarena.dev, 2026-03, 20 prompts, API-only, manually reviewed) [V]: Claude Opus 4.6
  19/20 valid; Zoo ML-Ephant 19/20; Gemini 2.5 Flash 14/20; GPT-5 lost points to output truncation on long programs.
- **ModelRift Pantheon benchmark** (2026-05-21; two reference photos, "use openscad CLI to preview
  your work and iterate") [V]: Antigravity 2.0/Gemini 3.5 Flash 4.5/5 (~12 min), ModelRift+human 3.8,
  Sonnet 4.6 3.4, Codex 5.5 3.0, Opus 4.7 3.0, Cursor Composer 2.5 1.4 (fastest). Slow iterative
  agents beat fast ones. Codex's PNG looked good but its **exported STL was non-manifold**.
- **Design Arena 3D (three.js as code, human Elo)** via modelgrep [S]: GPT-6 Astra 1464, Kimi K3
  1417, Claude Fable 5.1 1414; Qwen3.7 Max #16 (1290), Qwen3.7 Plus #24; no Grok listed.
- **CADGenBench** (HF, Apache-2.0, drawing → STEP, build123d/CadQuery/Onshape) [V]: live table
  didn't render in my fetch; the build123d-mcp +0.10 score claim is from that repo [S].

## 3. Which language works best for an LLM

| Language | Practitioner verdict | For us |
|---|---|---|
| **OpenSCAD** | Most reported hobbyist successes (HN, XDA, Medium, Adam, ModelRift, PrintPal). Tiny language, CSG only, so it compiles more reliably than CadQuery for simple shapes [S getleo.ai]. Weak spots: no fillets without BOSL2, colour lost in STL, CGAL slow (manifold backend only in nightlies). HN: "hobbyist toy" for pro work. | Good fit for boxy appliances. Fedora ships **2021.01** (no manifold) [V dnf]; use the nightly AppImage. |
| **CadQuery / build123d** | Preferred by pros (BREP, STEP, fillets, revolves). Academic benches centre on CadQuery; build123d has highest Pass@1 but worse IoU than alternatives [S Text2CAD-Bench abstract]. Qwen 3.5 27B very poor at CadQuery [S CadBench]. | Best for sinks/faucets (revolve, fillet), but expect Qwen to fail on API details; route to Grok, or hide the API behind helpers. |
| **Blender bpy** | Needed for materials/UVs/bevels/rigging (blender-mcp, LL3M, Grok demo). Big API surface, so the RAG/API-lookup tools exist for a reason. | Heavy but best-looking. `pip install bpy` 5.1+ is **cp313 only**, ≤5.0.1 is cp311 [V PyPI]; Fedora `blender` 5.2.2 [V dnf]. |
| **three.js** | Huge vibe-coding corpus (Design Arena, game jams, img2threejs). Needs a browser/Node to render and export GLB. | Output is our format family, but adds a JS runtime to a Python server. [I] |
| **Plain Python + trimesh/manifold3d** | Not a practitioner favourite (no one markets it), but it's what `server/assets.py` already uses; primitives + transforms + booleans, GLB export with per-part colour. | Lowest-friction route; LLM never needs to know OCCT. [I] |
| **Declarative JSON spec compiled by us** | img2threejs's `ObjectSculptSpec`, CADAM's slider parameters and HN's "describe it as geometric primitives" all point this way. | Best fit for a 27B model on an 8K tokens/min budget. [I] |

## 4. Practical tips (reported)

1. **Render feedback loop is mandatory**: write code → render PNG(s) → model critiques → edit. [V: fboldo skill, CADGenBench baseline, davesnider, HN 45327538 "an MCP server that renders and sends images back... significantly improved"]
2. **Several fixed views, not one**: iso + front/side/top presets are standard (fboldo, jabberjabberjabber: 7 presets). PrintPal packs **4 orthographic views into one sheet**. That matters for us because Groq allows ≤3 images per request. [V]
3. **Let the model move the camera** when it needs to (davesnider's `capture-view --pos x,y,z --look-at`; HN: Claude "strategically positions cameras"). Add **debug markers** (red spheres at known coordinates) so the model can tie pixels to coordinates. [V davesnider]
4. **Measure before render**: bbox, volume, watertight, per-boolean topology counts; `repair_hints`/`last_error` tools for compile errors. [V build123d-mcp]
5. **Snapshots/restore** so a bad edit can be rolled back instead of spiralling. [V build123d-mcp]
6. **Put exact dimensions in the prompt**; vision is for layout and features only. Ruler in frame / spec-sheet screenshots help if you must use photos. [V PrintPal]
7. **Decompose**: build in named sub-parts / steps ("create a plane W×H, rotate, translate") instead of one monolithic script. [V HN 45327538, HN 47365299]
8. **Reference image in the prompt is "a huge step"** over text-only. [V HN 48234090 comment]
9. **Cap iterations**: PrintPal 2 automatic passes; one HN dev disabled refinement because it "would quadruple the costs". ModelRift runs took ~10–12 min for the best agents. [V]
10. **Parametric first**: top-level named parameters (CADAM sliders, build123d-mcp `design_audit` nudges them) so fixes are number edits, not rewrites. [V]
11. **Library parts beat regenerating**: blender-mcp (Poly Haven CC0 textures/HDRIs), build123d-mcp `search_library/load_part`, CADAM bundling BOSL2. [V]
12. **Scripts enforce, model judges**: push validation/formatting into deterministic code and spend tokens only on visual judgement and code. [V img2threejs]
13. **Check the exported file, not the preview**: Codex's preview looked right but its STL was broken. Validate the final GLB. [V ModelRift]
14. **Human-style annotation beats prose**: ModelRift found feedback drawn on the render converged better than text descriptions. We can emulate this by overlaying bbox/dimension lines on the render. [V/I]

## 5. Failure modes (reported)

- **Orientation/axis errors**: wrong up-axis, parts extruded sideways ("a phone stand extruded
  sideways with disconnected components", GPT-5.5 in XDA's test); "the moment you add that extra
  dimension any semblance of intelligence goes right out". [V XDA, HN]
- **Floating/disconnected parts** and centring mistakes (primitive centred vs corner-anchored). [V XDA, Show HN SynapsCAD thread]
- **Wrong proportions / oversimplified placeholders** (Cursor 1.4/5 in ModelRift). [V]
- **Boolean failures**: subtractions that silently do nothing; non-manifold export. Fix with a
  volume check after every difference. [V build123d-mcp llms.md, Show HN thread, ModelRift]
- **Wrong facts, not wrong geometry**: Claude insisted a maker publishes no dimensions (XDA). We
  side-step this by supplying `dims_mm`. [V]
- **Hallucinated API/library calls** (BOSL2, bpy): why blender-mcp adds `bpy_api_lookup` and LL3M a
  BlenderRAG. [V tools / I motive]
- **Output truncation** on long programs (GPT-5 on CAD Arena). With Groq's ~8K tokens/min, keep
  programs short. [V/I]
- **Organic shapes** (fortune cookie, faucets with sweeps) are much worse than boxy parts. [V HN]
- **Colour/material weakness**: OpenSCAD colour doesn't survive STL; multicolour 3MF works only in
  2025 nightlies and has regressions (openscad#5849/#5994/#6159); Opus/Sonnet Pantheons "monochromatic". [S/V]
- **Can't see small errors**: the model misses a misaligned button until a human points at it; then it self-corrects. [V XDA]

## 6. Photo grounding (image → code)

- Reported pattern: photo + one-line prompt (part name + units + key dims) → code → **render in
  the photo's viewpoint** → side-by-side compare → targeted edits (PrintPal, img2threejs with its
  "reference-camera block", ModelRift). [V]
- img2threejs is the closest to what we need: staged passes (blockout first, detail later), each
  gated by a render/photo comparison, and it states the limit plainly: "a single image cannot
  reveal hidden sides or guarantee exact geometry". [V]
- Accuracy degrades with perspective distortion, reflections, and hidden cavities. Our photos are
  retailer shots (usually 3/4 view on white), which is the good case. [V PrintPal / I]
- Cheap realism without geometry: many AR pipelines just texture the front face with the
  product photo. blender-mcp's `set_texture` does this for Poly Haven maps. For us: a planar UV
  projection of the cropped photo onto the part's front face in trimesh. [I]

## 7. What "good enough for AR/VR preview" means in practice

E-commerce AR guidance (Khronos 3D Commerce asset guidelines as cited by several vendors) [S]:
1:1 scale in metres, correct pivot, silhouette and proportions that match the product, plausible
colour/finish, watertight/no non-manifold edges, sensible triangle count for mobile. "Customers
will forgive a simple interface but not a sofa at the wrong size." Our contract already
guarantees scale and pivot. So the LLM's job is **silhouette + major features + colour**, judged
by: (a) bbox equals `dims_mm` within ~1 %, (b) connected (no floating islands) and watertight,
(c) a VLM judge says it's recognisable as the product from 2–3 views vs the photo, (d) tris under
a budget suited to Quest 3S. [I]

## 8. Ranked setups to try (16-core laptop, 15 GB RAM, no NVIDIA, uv)

Note: the brief says Python 3.11, but `pyproject.toml` pins `requires-python >=3.13`. That
matters for `bpy` wheels (cp313 from 5.1 on). [V]

1. **Primitive-spec compiler (JSON → trimesh GLB) + render-compare loop.** The LLM emits a short
   JSON list of named parts (`box/cylinder/cone/torus/rounded_box/extrude(polygon)/grille(n)`,
   position/rotation in mm in a mount-face frame, colour hex, optional `"texture":"photo_front"`).
   Our code compiles it with trimesh (+ `manifold3d` for booleans, Apache-2.0), rescales the
   union to exact W×H×D, runs deterministic checks (floating parts, bbox, watertight), renders
   a 4-view sheet, and sends it plus the product photo to Qwen as 2 images: "list up to 5
   concrete differences as JSON edits". 2–3 passes max. Validity is guaranteed by construction,
   it fits 8K TPM, uses deps we already have, and runs fully offline except the LLM call. Covers
   AC units, condensers, heaters, fridges, panels, hangers. **Try first.** [I, built from tips 1–12]
2. **Same loop with restricted Python instead of JSON** (a `cad.py` helper module exposing the same
   primitives + `union/subtract/array/mirror`, exec'd in a subprocess with a timeout and no
   imports). More expressive (fin arrays, louvres, tapers), still no OCCT. Feed `last_error`
   tracebacks back for repair (max 2). [I, pattern from build123d-mcp `execute`/`last_error`]
3. **build123d headless** (Apache-2.0, py ≥3.11) for revolve/fillet parts (sinks, faucets,
   water-heater domes): helper-wrapped build123d → `export_gltf(binary=True)` → same render/compare
   loop (PyVista off-screen as in the CADGenBench baseline, or trimesh). Expect Qwen to struggle
   (CadBench); use this tier with `grok-4.7` or only for the revolve-heavy categories. Could reuse
   `build123d-mcp` as-is if we ever drive it from an MCP client. [V tools / I routing]
4. **OpenSCAD nightly AppImage (manifold backend) + CLI PNG renders.** Most practitioner mileage,
   GPL-2 binary called as a subprocess (no linking concerns [I]). STL/3MF → trimesh → GLB, colour
   re-applied per module by exporting modules separately. Worth an A/B against setup 1 to see
   whether Qwen writes OpenSCAD more reliably than our JSON. [V ecosystem / I plan]
5. **Headless Blender (`bpy` 5.2.2 cp313 wheel, or Fedora `blender -b`)** with blender-mcp's tool
   *shapes* re-implemented in-process (scene info, screenshot, exec, GLB export): bevels,
   materials, photo projection, EEVEE/Cycles CPU renders. Best-looking output, but the largest API
   surface for a 27B model to hallucinate against, and a ~400 MB dependency. Only if 1–2 look too
   crude in the headset. [V packages / I]
6. **Photo-texture shortcut on the exact-size proxy** (no LLM geometry at all): crop the product
   photo onto the front face of the proxy box, body colour from `finish`. Near-zero cost, a
   baseline to beat for setups 1–5, and a better fallback than today's plain box. [I]

Not recommended: Zoo, Fusion/Onshape MCPs, Rodin/Tripo/Meshy/Sketchfab (accounts or paid); LL3M (server retired, academic licence); three.js (adds a JS runtime for no gain over trimesh).

## 9. Licences of things we might actually use

| Component | Licence | Note |
|---|---|---|
| trimesh 5.1 | MIT [V PyPI] | already a dep |
| manifold3d 3.5.4 | Apache-2.0 [I; PyPI field empty] | booleans |
| build123d 0.13 | Apache-2.0 [V PyPI] | py ≥3.11,<3.15 |
| cadquery 2.8 | Apache-2.0 [V PyPI] | |
| bpy 5.2.2 | GPL-3.0 [V PyPI] | cp313 only; GPL matters only if we distribute it linked |
| OpenSCAD | GPL-2.0 w/ CGAL exception [V dnf] | Fedora pkg is 2021.01 |
| blender-mcp | MIT [V] | telemetry on by default |
| build123d-mcp | Apache-2.0 [V] | |
| fboldo / jabberjabberjabber openscad-mcp | MIT [V] | |
| img2threejs | Apache-2.0 [V] | |
| CADAM | GPLv3 [V] | don't copy into server code |
| cadgenbench | Apache-2.0 [V] | baseline render loop reference |
| Poly Haven assets | CC0 [V via blender-mcp README] | textures for finishes |

## Sources

- blender-mcp: https://github.com/ahujasid/blender-mcp (README + `src/blender_mcp/server.py`)
- OpenSCAD MCPs: https://github.com/fboldo/openscad-mcp-server, https://github.com/jabberjabberjabber/openscad-mcp, https://github.com/petrijr/openscad-mcp, https://github.com/quellant/openscad-mcp
- build123d-mcp: https://github.com/pzfreo/build123d-mcp, https://github.com/pzfreo/build123d-mcp/blob/main/llms.md, https://github.com/Wyrd-Group/build123d-mcp, https://github.com/Casys-AI/mcp-build123d
- CadQuery MCPs: https://github.com/rishigundakaram/cadquery-mcp-server, https://github.com/bertvanbrakel/mcp-cadquery
- FreeCAD MCPs: https://github.com/neka-nat/freecad-mcp, https://github.com/yuri-schmaltz/mcp_freecad, https://github.com/gchen19/AnkusDrive
- Fusion/Onshape MCPs: https://github.com/Joe-Spencer/fusion-mcp-server, https://github.com/hedless/onshape-mcp
- CADGenBench: https://github.com/huggingface/cadgenbench, https://huggingface.co/spaces/HuggingAI4Engineering/CADGenBench
- CAD Arena: https://cadarena.dev
- CadBench (Qwen 27B numbers): https://arxiv.org/html/2605.10873v1
- ModelRift Pantheon benchmark: https://modelrift.com/blog/openscad-llm-benchmark/ ; HN https://news.ycombinator.com/item?id=48234090
- HN threads: https://news.ycombinator.com/item?id=45327538, https://news.ycombinator.com/item?id=47365299, https://news.ycombinator.com/item?id=47183039 (SynapsCAD)
- Dave Snider, Claude for 3D: https://www.davesnider.com/posts/claude-3d
- XDA, Claude vs GPT-5.5 3D prints (2026-06-10): https://www.xda-developers.com/asked-claude-gpt-design-3d-prints-failed-opposite-ways/
- CADAM: https://github.com/Adam-CAD/CADAM ; https://www.developersdigest.tech/blog/adam-ai-cad-yc-w25-open-source-text-to-cad
- PrintPal image-to-CAD: https://printpal.io/docs/image-to-cad-workflow
- img2threejs: https://github.com/img2threejs/img2threejs
- LL3M: https://github.com/threedle/ll3m
- Zoo: https://zoo.dev/docs/developer-tools/mcp, https://zoo.dev/api-pricing
- Backflip: https://www.businesswire.com/news/home/20260803007022/en/
- Meshy 3D Agent: https://www.meshy.ai/tutorials/meshy-3d-agent-guide
- Grok in Blender: https://cryptobriefing.com/grok-3d-spaceship-blender-text-prompt/ (x.com post https://x.com/elonmusk/status/2085190872564699581 not fetchable)
- Design Arena 3D via https://modelgrep.com/best/3d
- OpenSCAD colour export issues: https://github.com/openscad/openscad/issues/5849
- build123d export docs: https://build123d.readthedocs.io/en/latest/import_export.html
- AR quality criteria: https://acquireconvert.com/ar-visualization/ar-product-visualization/
