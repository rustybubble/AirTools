# R1: academic survey of LLM-built 3D assets (code, tools, feedback loops)

Research agent R1, 2026-09-25. Question: could a general LLM (Groq `qwen/qwen3.8-27b`
first, xAI `grok-4.7` as fallback) produce our `model.glb` (metres, +Y up, origin at the
mounting-face centre, part toward +Z, exact published W×H×D) by writing code in the right
environment, instead of the Hunyuan3D-2.1 Space (`server/assets.py` tier `ai_mesh`)?

Markers: **[V]** checked against the primary source (arXiv abstract or PDF text, repo README
or GitHub API), **[S]** secondary (search snippet, summariser), **[I]** my inference.
Every arXiv id below was fetched. Numbers are quoted from the paper named.

## 1. Summary for the impatient

- **Code-as-3D now matches or beats native image-to-3D on hard-surface objects, but only with
  frontier models.** Procedura (Aug 2026) with Gemini 3.7 Flash scores a blind-judge composite
  of 0.828 on MechBench-36, against TRELLIS.2 at 0.810 and Hunyuan3D at 0.697. A *single call*
  to GPT-5.6-sol already scores 0.792 [V]. On P3D-Bench the Qwen family sits in the third tier:
  Qwen 3.6 gets 0.346 on the assembly composite, against 0.56 for the leaders [V].
- **Compile errors are cheap to fix and model-independent. Shape errors are not.** In
  3DCodeBench, two stateless retries with the traceback lift executability from 0.53–0.55 to
  0.93–0.98 for Gemma 4 26B/31B, which is the size class closest to our 27B model. A full
  coding-agent harness (Claude Code, Codex CLI, Gemini CLI) raises executability too, but
  **does not improve shape fidelity once the script compiles** (ΔSigLIP −0.010, ΔChamfer
  +0.001) [V].
- **Pick the target language for the weak model.** In P3D-Bench, **OpenSCAD is the strongest
  format**. For image→code, Qwen3.6-Plus is 0.995 valid in OpenSCAD but only 0.47 valid in
  CadQuery [V]. aDSL shows that relational layout operators, used instead of raw coordinates,
  cut repair rounds from 6.08 to 4.25 [V].
- **Known dimensions are a large advantage.** In 3DHarnessBench, giving the agent the target's
  bounding box and dimensions improved almost every metric [V]. BenchCAD found that replacing
  the text instruction with a render "collapses every model to near-zero, since a render
  specifies geometry but not numbers" [V]. We have the numbers. The photo only has to supply
  the layout.
- **Visual critique loops help modestly, and the first round matters most.** Procedura's refine
  stage adds 0.028 to its composite [V]. CADCodeVerify reports −7.3% point-cloud distance and
  +5% compile rate [V]. In Query2CAD, most of the gain comes from the first refinement [V].
  VLMs still misjudge spatial errors (LL3M limitation) [V].
- **Fine-tuned CAD models and mesh-token LLMs don't fit our case.** CAD-Coder and cadrille
  collapse on multi-part objects (composites of 0.135 and 0.092 on MechBench-36) [V].
  LLaMA-Mesh, MeshLLM and ShapeLLM-Omni need GPU fine-tuned weights, produce low-poly output,
  and ShapeCraft measured LLaMA-Mesh at 15.6 min per object with the lowest VQA score [V].
- **Materials in the literature are mostly flat PBR per part**, plus Blender shader nodes in
  LL3M. Photo-faithful textures use score distillation, which needs a GPU (ShapeCraft) [V].
  Decals from the product photo are not covered by any paper. This is open ground [I].

## 2. Comparison table

Relevance: ★★★ = directly usable pattern for us, ★ = background only.

| Method (venue/date) | Input | Output repr. | LLM(s) | Loop / feedback | Eval | Reported quality | Code / licence | Relevance |
|---|---|---|---|---|---|---|---|---|
| **Procedura** [2608.26238] (Aug 2026) | text (+ synthesized ref view) | OpenSCAD program: named parts + typed mates; OBJ; per-part PBR JSON | Gemini 3.7 Flash (also GPT-5.6-sol), no 3D training [V] | per-part compile gate → mate gate (contact area/penetration) → connectivity gate; decoupled vision critic, one fix per cycle [V] | blind VLM judge (geo/aes/sem), CLIP, sharp-edge length, dihedral [V] | MechBench-36 0.828 vs TRELLIS.2 0.810, Hunyuan3D 0.697; P3D-Bench assembly 0.590 [V] | MIT; Bun + OpenSCAD (Manifold) + Blender; any OpenAI-compatible endpoint [V] | ★★★ |
| **P3D-Bench** [2606.11152] (Jun 2026) | text / image / assembly | JSON, OpenSCAD, CadQuery, Three.js | 11 LLMs incl. Qwen3.6-Plus, GPT-5.5, Gemini 3.1 Pro [V] | single-shot, max thinking | Valid, Geo, Topo, MLLM Judge, Part [V] | OpenSCAD strongest format; CadQuery often invalid on weaker models; Qwen tier 3 [V] | benchmark | ★★★ (format choice) |
| **3DCodeBench** [2606.01057] (May 2026) | text / image (Infinigen-derived) | Blender 5.0 Python | 12 VLMs incl. Gemma 4 26B/31B [V] | stateless retry with traceback; native coding-agent harnesses | exec, SigLIP-2, DINOv3, Uni3D, CD, human Elo (3DCodeArena) [V] | retries raise exec to ~0.97; SigLIP-2 vs human Elo r = 0.964; failures mostly API mismatches + floating parts [V] | released (per abstract) | ★★★ (eval + loop design) |
| **3DHarnessBench** [2609.06535] (Sep 2026) | single / multi / active view / full 3D access | Blender Python via Blender MCP | Fable 5, Opus 5, GPT-5.6 Sol, Kimi K3, Qwen 3.8 Max, Gemini 3.1 Pro, MiniMax M3 [V] | 3 refine iterations (fixed views) up to free MCP exploration | SigLIP-2, DINOv2/v3, Chamfer, Uni3D, Betti L1, cost, latency [V] | richer access helps; explicit bbox/part measurements help; Qwen 3.8 Max ≈ frontier on single view (SigLIP 0.913) but ~40K output tokens and 758 s per object [V] | "will release" | ★★★ |
| **3D-CoS** [2606.10478] (Jun 2026) | single RGB image (ModelNet10) | Blender Python | Qwen2.5-VL-72B, InternVL3.5-38B, LLaVA-OV-72B, Claude Sonnet 4, o3, Gemini 3 Pro [V] | single call / planning / RAG / few-shot / part-wise agent with exec repair | CD, F@5%, SBR, NC, silhouette IoU [V] | Gemini 3 Pro few-shot CD 0.0249, beating MeshCoder, near InstantMesh (0.0191); best open model (Qwen2.5-VL-72B few-shot) CD 0.0492; few-shot helps open models most [V] | n/a | ★★★ (closest task: one photo → code) |
| **aDSL** [2608.17975] (Aug 2026) | text / image | agent-centric DSL with relational operators (→ mesh) | Gemini 3 Pro [V] | Plan–Execute–Critic | CLIP, VQA, success, rounds [V] | beats LL3M, ShapeCraft, BlenderMCP; ~195 s/round, ~31K input tokens/round, 4.25 rounds [V] | repo public, no licence file [V] | ★★ (DSL design) |
| **LL3M** [2508.08228] (Aug 2025) | text | Blender Python (BMesh, modifiers, shader nodes) | GPT-4o planner/retrieval, Claude coder, Gemini 2.0 Flash critic/verifier [S]; README says Claude Sonnet 3.7 [V] | multi-agent, BlenderRAG, auto-critique + user edits | code complexity, error rate, qualitative [S] | ~4 min create + ~6 min auto-refine [S]; VLMs miss spatial artifacts [S] | demo licence, non-commercial; hosted server discontinued [V] | ★★ |
| **ShapeCraft** [2510.17603] (NeurIPS 2025) | text | graph-based procedural shape (GPS) → Blender; texture via score distillation | Qwen3-235B-A22B (coder), Qwen-VL-Max (evaluator) [V] | parser/coder/evaluator, iterative | IoGT, Hausdorff, CLIP, VQA, runtime [V] | IoGT 0.471 vs BlenderLLM 0.455; 11.7 min, 21 API calls; compile 100% vs 60–80% for o3/R1 [V] | project page | ★★ (open Qwen stack works) |
| **CADCodeVerify / CADPrompt** [2410.05340] (ICLR 2025) | text | CadQuery | GPT-4, Gemini 1.5 Pro, CodeLlama [V] | VLM generates and answers verification questions about renders, then fixes | IoGT, point-cloud dist., Hausdorff, compile rate [V] | GPT-4 compile 96.5%; −7.3% PC dist [V] | public repo, no licence file [V] | ★★ (question-checklist critic) |
| **3D-PreMise** [2401.06437] | text | Blender code (industrial shapes) | GPT-4 / GPT-4V [V] | render → GPT-4V self-correction | dataset study | shows potential and limits [V] | — | ★ |
| **Query2CAD** [2406.00144] | text | FreeCAD macros | GPT-4 Turbo [V] | BLIP-2 captions + human | success rate | 53.6% first try, +23.1% with refinement, first iteration gives most [V] | public | ★ |
| **CAD-Assistant** [2412.13810] | multimodal | FreeCAD Python actions | VLLM planner (GPT-4o class) [S] | tool-augmented (render, cross-section, sketch parameteriser) | CAD benchmarks [V] | beats VLLM baselines [V] | — | ★★ (tool ideas) |
| **SceneCraft** [2403.01248] | text | Blender scene code | GPT-4 / GPT-4V [V] | scene graph → constraints → render critique; **library learning** of reusable functions [V] | constraint adherence, human [V] | beats prior LLM agents [V] | — | ★★ (library learning) |
| **3D-GPT** [2310.12945] | text | Infinigen procedural parameters | GPT-3.5/4 [S] | dispatch/concept/modeling agents | qualitative | LLM fills procedural params [V] | — | ★★ (params-into-generator pattern) |
| **Proc3D** [2601.12234] (Jan 2026) | text | procedural compact graph (PCG), slider params | GPT-4o ICL, fine-tuned Llama-3 [V] | param edits | ULIP (+28%), edit speed (400×) [V] | — | — | ★★ |
| **L3GO** [2402.09052] | text | Blender primitives via SimpleBlenv API | GPT-4 [V] | part-by-part trial and error with spatial feedback | GPT-4V + human | GPT-4V score 0.6 vs 0.346 for plain GPT-4 [V] | — | ★★ (primitive API) |
| **BlenderAlchemy** [2404.17672] | text/ref image | Blender edits incl. material node graphs | GPT-4V [V] | edit generator + VLM state evaluator, tree search | — | materials/lighting edits [V] | — | ★ (materials) |
| **BlenderLLM / CADBench** [2412.14203] | text | Blender Python | fine-tuned Qwen2.5-Coder-7B [V] | self-improvement data loop | CADBench, GPT-4o judge [V] | beats GPT-4o on CADBench [V] | Apache-2.0 [V] | ★ (needs GPU inference) |
| **CAD-Coder (image)** [2505.14646] | image | CadQuery | LLaVA-1.5-type VLM fine-tune, 13B [V] | none | valid syntax, 3D similarity | beats GPT-4.5 and Qwen2.5-VL-72B in distribution; "some signs" on real photos [V] | Apache-2.0 [V] | ★ |
| **CAD-Coder (text)** [2505.19713], **Text-to-CadQuery** [2505.06507], **ProCAD** [2602.03045] | text | CadQuery | fine-tuned open LLMs + GRPO (Chamfer reward) [V] | clarifying agent (ProCAD) | CD, IoU, invalid ratio | ProCAD beats Claude Sonnet 4.5, CD −79.9% [V] | mixed | ★ |
| **CAD-Recode** [2412.14042], **cadrille** [2505.22914], **CADEvolve** [2602.16317], **ReCAD** [2512.06328] | point cloud / image / text | CadQuery | small VLM fine-tunes (cadrille: Qwen2-VL-2B [V]) | SFT + online RL | IoU / CD on DeepCAD, Fusion360 | SOTA on sketch-extrude parts; collapse on multi-part objects (Procedura T1) [V] | cadrille/CADEvolve Apache-2.0 [V] | ★ |
| **Img2CAD** [2408.01437], **CADCrafter** [2504.04753], **CAD-GPT** [2412.19663], **CAD-MLLM** [2411.04954], **CAD-Llama** [2505.04481], **Text2CAD** [2409.17106], **CADFusion** [2501.19054] | image / text | CAD command sequences | fine-tuned Llama/Vicuna-class | some with visual-feedback RL (CADFusion) or DPO (CADCrafter) | CD, IoU, invalid ratio | DeepCAD-style mechanical parts only [V] | Img2CAD MIT [V] | ★ |
| **Seek-CAD** [2505.17702] | text | CAD (SSR paradigm) | local DeepSeek-R1 + VLM feedback [V] | render step-wise views → VLM + CoT feedback | — | first local open-model visual self-refine [V] | — | ★★ (open-model loop) |
| **ArtiCAD** [2604.10992], **Embodied CAD** [2606.31252] | text/image | CAD assemblies with connectors/joints | training-free multi-agent / solver-grounded [V] | validation + cross-stage rollback; experience store [V] | CADPrompt, custom | — | — | ★ (connector idea = mates) |
| **Thinking in Blender (SEIG)** [2606.02580], **VIGA** | single image | Blender scene program | pretrained VLM [V] | staged generator–verifier (geometry → material → lighting) [V] | recon metrics | — | — | ★★ |
| **Benchmarks**: Text2CAD-Bench [2605.18430], BenchCAD [2605.10865], RealCADBench [2609.03773], UniCAD [2606.05058], BlenderGym [2504.01786] | — | CadQuery / FreeCAD / Blender | frontier + open | — | exec, IoU, judge | frontier executability 0.57–0.81, solid IoU 0.28–0.54 on real industrial intents (RealCADBench); models drop sweeps and lofts for extrudes (BenchCAD) [V] | — | ★ (failure modes) |
| **LLaMA-Mesh** [2411.09595], **MeshLLM** [2508.01242], **MeshXL** [2405.20853], **ShapeLLM-Omni** [2506.01853] | text (+image for Omni) | mesh as text tokens / 3D VQ tokens | fine-tuned LLaMA-3.1-8B / Qwen2.5-VL-7B [V] | none | FID/CLIP-style | low-poly; bounded by training distribution; slow [V via ShapeCraft T1] | LLaMA-Mesh NVIDIA licence (NOASSERTION) [V] | ✗ for us |
| **VLMaterial** [2501.18623], **process-trace materials** [2607.13318] | image / text | Blender material node graphs as Python | fine-tuned VLM / pretrained LLM [V] | — | user studies | — | — | ★ (materials later) |

## 3. Key takeaways for our setup

1. **Our task is easier than the benchmarks in the ways that matter.** Every benchmark above
   has to *infer scale and proportion* from pixels or prose. We are given W×H×D exactly,
   plus a category (window AC, water heater, faucet…). The code only has to reproduce the
   layout and features *inside a known box*. The bbox experiment in 3DHarnessBench and the
   "numbers, not renders" finding in BenchCAD both say this is where frontier models gain most
   [V]. Our proxy box already meets the dimension contract. An LLM tier has to beat it on
   *looks*, not on size [I].
2. **Enforce dimensions in the harness, not the prompt.** Pass `W,H,D` as fixed variables.
   Reject any build whose bbox differs by more than 1% (like Procedura's mate/connectivity
   gates) and then run the existing `normalize_mesh`. The Hunyuan tier's cached
   `scale_residual_pct` has a median of 4.7% and a maximum of 54.8% across the 25 `ai_mesh`
   parts. A code tier should give ~0% by construction [V: our `data/parts`; I: target].
3. **A 27B model needs a narrow API.** For weaker models, failures are dominated by API
   mismatches (3DCodeBench), invalid CadQuery (P3D-Bench) and raw-coordinate drift (aDSL,
   Procedura's "solved placement") [V]. Give the model a ~15-function relational helper
   library, for example `box`, `cyl`, `grille`, `attach(child, parent, face, offset)`, `fillet`
   and `array`, over one backend. Don't expose full Blender or CadQuery [I, from aDSL and L3GO
   primitive APIs].
4. **Budget the loop for Groq's ~8K tokens/min.** Published pipelines spend 30K+ input tokens
   per round (aDSL) or ~40K output tokens per object (Qwen 3.8 Max, 3DHarnessBench) [V]. At
   8K TPM that is 5–15 minutes of quota per asset [I]. Keep system prompt + helper docs under
   ~2K tokens and send images at low resolution. Cap it at 1 generation, 2 compile retries and
   1–2 critique rounds, because the evidence says the first refinement carries most of the gain.
5. **Keep the critic separate from the generator and make it quantitative.** Procedura's
   critic reports ratios ("winch 1.25× tire diameter vs 0.6–0.7× in reference") and the fixer
   gets one fix per round [V]. CADCodeVerify's critic answers yes/no verification questions
   [V]. Both designs fit a 3-image budget: product photo + 2 renders from matching views [I].
   Use a different model family for final scoring than for generation, to avoid
   self-preference (2606.20364, 2606.18451) [V].
6. **Materials:** per-part `baseColorFactor/metallic/roughness`, chosen by the VLM from the
   photo, is state of the art for code agents (Procedura `--paint`) [V]. That is a trivial
   glTF material in trimesh [I]. A cheap step up that no paper does: planar-project the
   background-removed product photo onto the front face as a baseColor texture (a decal).
   This is untested, so measure it [I].
7. **Photo-to-code has an open-model ceiling.** In 3D-CoS the best open VLM (Qwen2.5-VL-72B)
   reaches F@5% 0.67 and CD 0.049, against 0.85 and 0.025 for Gemini 3 Pro, and few-shot
   examples help it most [V]. Expect `qwen3.8-27b` to be at or below the Qwen3.6-Plus / Gemma 4
   tier [I]. Plan for grok-4.7 escalation on complex categories such as faucets and fans, and
   use few-shot exemplars from our own categories.

## 4. Ranked approaches to try

Constraints: laptop with 16 cores, 15 GB RAM and no NVIDIA GPU. Blender and OpenSCAD are
**not installed** now, and `trimesh` is the only 3D dependency in `pyproject.toml` [V]. The
ROCm box is disk-constrained. Nothing below needs a GPU.

1. **Category templates, with the LLM filling in parameters** (3D-GPT/Infinigen, Proc3D and
   CADEvolve's 46 hand-written primitives) [I on transfer].
   - *What it is:* write ~8 parametric generators in plain Python/trimesh for our categories:
     box appliance with grille/vents/control panel (window AC, mini-split, fridge), vertical
     cylinder with top fittings (water heater), framed panel with cell grid (solar),
     basin/bowl (sink), spout-on-base (faucet), bracket/strap (gutter hanger, joist hanger),
     rectangular tube with elbow (downspout), and fan.
   - *LLM role:* one vision call with photo + spec text returns JSON (template id, feature
     params in fractions of W/H/D, part colours and PBR).
   - *Needs:* ~1 day of template code; `manifold3d` if booleans are wanted (small pip
     wheel); one Groq call of ~3K tokens per asset.
   - *Why first:* exact dims by construction, zero compile failures, fits the 27B model and
     the TPM budget, and deterministic. Ceiling: parts outside the templates fall back to
     approach 2 or the proxy.
2. **OpenSCAD code-gen with the gates from Procedura and aDSL, without the heavy machinery.**
   - *What it is:* the LLM writes OpenSCAD against a small relational helper library, with
     `W,H,D` fixed. The harness checks, in order: compile (retry ×2 with the error text), bbox
     within 1%, single connected component / no floaters (trimesh `split`), then
     `normalize_mesh`.
   - *Needs:* the `openscad` binary, a recent snapshot with the Manifold backend (Procedura
     notes CGAL is "orders of magnitude slower") [V]. Fedora package or AppImage, about
     100 MB. Plus the helper `.scad` library and 3–5 few-shot examples from our categories.
   - *Evidence:* OpenSCAD is the most robust format for weak models (P3D-Bench); retries
     reach ~0.97 executability (3DCodeBench); relational ops cut rounds (aDSL) [V].
   - *Alternative:* try Procedura itself (MIT, OpenAI-compatible endpoint, so Groq can plug
     in). It needs Bun + OpenSCAD + Blender for its renders, and its prompts are tuned for
     Gemini-class models and long contexts, so run it as a baseline, not as production [V/I].
3. **Render → VLM critique → one fix, added on top of 1 or 2.**
   - *What it is:* render 2 views (front 3/4 and side) matched to the photo's viewpoint. The
     same Groq call sees photo + 2 renders (3-image cap) and answers a fixed checklist
     (CADCodeVerify style) plus ratio complaints (Procedura style). Apply one edit, stop after
     2 rounds, and keep the best-scored version.
   - *Needs:* a headless CPU renderer. `pyrender` with OSMesa/EGL, or `trimesh` + `pyglet`
     offscreen, is enough for grey-clay + flat PBR [I]. Blender only if shader fidelity
     matters.
   - *Expected gain:* small but real (+0.03 judge composite in Procedura; 3DCodeBench shows
     harness autonomy alone does not improve shape) [V].
4. **Blender-Python agent (LL3M / BlenderMCP style) for organic or detailed parts** such as
   faucets, fan blades and bowls, where CSG looks blocky (Procedura limitation) [V].
   - *Needs:* headless Blender (~1 GB; fine on the laptop, not on the ROCm box), a
     BlenderRAG-lite (API snippets for the ~30 bpy calls we allow), and grok-4.7, since Blender
     API mismatches are the main failure for small models (3DCodeBench) [V].
   - *Why lower:* higher token cost, slower, and more failures. Only worth it if 1–3 leave
     visible gaps on curved parts [I].
5. **Photo decal and PBR pass** (independent of geometry source, can also dress the proxy).
   - *What it is:* background-remove the product photo, planar-UV-project it onto the
     mount-opposite face, and set per-part PBR from the VLM.
   - *Needs:* trimesh UVs + a PIL crop; `rembg` (onnx CPU) if a matte is needed.
   - *Status:* no paper evaluates this [I]. Cheap enough to A/B against untextured output.
6. **Not recommended now:** fine-tuned CAD VLMs (cadrille 2B, CAD-Coder 13B, BlenderLLM 7B)
   and mesh-token LLMs (LLaMA-Mesh, MeshLLM, ShapeLLM-Omni). They need local GPU inference
   (ROCm support unverified), are trained on DeepCAD-style mechanical parts or low-poly
   Objaverse subsets, collapse on multi-part objects, and ignore exact dims [V/I].

## 5. Recommended evaluation for our experiment

We have **no ground-truth meshes**: the cache holds 25 `ai_mesh` and 17 `proxy` parts, with
zero `cad` parts [V]. So Chamfer/IoU against GT is unavailable. Evaluate against the product
photo and the published dims, and compare each candidate with the Hunyuan mesh and the proxy
box on the same parts.

| Metric | How | Source of the practice |
|---|---|---|
| **Executability** at first try and after k ≤ 2 retries | compile/run succeeded and a mesh was exported | CADPrompt compile rate, 3DCodeBench [V] |
| **Dimension error** | max over W/H/D of \|bbox − published\| / published, before `normalize_mesh`; report like `scale_residual_pct` | ours; 3DHarnessBench bbox cues [V] |
| **Structural validity** | watertight, connected-component count (β0, floaters), no zero-volume parts | Betti L1 (3DHarnessBench), connectivity gate (Procedura) [V] |
| **Photo similarity** | SigLIP-2 and DINOv2 cosine between the background-removed photo and a render at the matched viewpoint; silhouette IoU after 2D alignment | SigLIP-2 r = 0.964 with human Elo in 3DCodeBench [V] |
| **VLM judge (pairwise, reference-based)** | photo + two candidate renders, both orders, keep only order-consistent verdicts; judge family ≠ generator family (grok judges qwen output and vice versa) | 2606.18451 (κ = 0.66 across judges; render-CLIP at chance), 2606.20364 [V] |
| **Feature checklist (VQA pass rate)** | 5–8 category questions from the spec, e.g. "front grille present?", "control panel on the right?", answered on renders | CADCodeVerify, ShapeCraft VQA [V] |
| **Human preference** | small blind pairwise win-rate over the 42 cached parts: LLM tier vs Hunyuan vs proxy | 3DCodeArena Elo, GPTEval3D [V] |
| **Cost and latency** | LLM calls, input/output tokens, Groq TPM wait, wall-clock, GLB size, triangle count | 3DHarnessBench T7, aDSL T2 [V] |

Do not use prompt-to-render CLIP as a primary score. It ran at chance against the judge in
2606.18451 and saturates on semantic axes (Procedura) [V].

## Sources (all fetched 2026-09-25)

arXiv abstracts via `arxiv.org/abs/<id>` for every id cited. Full PDFs were read for 2608.26238,
2609.06535, 2606.01057, 2606.11152, 2606.10478, 2608.17975, 2410.05340, 2412.14203, 2510.17603,
2402.09052, 2505.14646 and 2605.10865. Repos and licences via the GitHub API: SpatiaOS/Procedura
(MIT), threedle/ll3m (demo licence, README), FreedomIntelligence/BlenderLLM (Apache-2.0),
col14m/cadrille, anniedoris/CAD-Coder and zhemdi/CADEvolve (Apache-2.0), qq456cvb/Img2CAD
(MIT), princeton-vl/infinigen (BSD-3-Clause), ahujasid/blender-mcp → mcp-for-blender (MIT),
sig-pku/aDSL and Kamel773/CAD_Code_Generation (no licence file). LL3M agent-model assignments
come from a summariser pass over arXiv HTML v1 [S] and conflict with the README on the Claude
version.
