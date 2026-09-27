# Vort3xed/TreeHacks: a Pi3X-based real-time point-cloud streaming demo

Scope: one repo, `github.com/Vort3xed/TreeHacks` (cloned read-only, 8 commits, single `main` branch,
HEAD `8ee4a92`, 2026-05-03). Read `docs/research/p1-prior-hackathons.md`, `p1-fast-recon.md`,
`p1-cloud-world-models.md`, `p1-colmap-realityscan.md` first per the brief; this doc doesn't repeat
their general Pi3/Pi3X model-landscape coverage (`p1-fast-recon.md` §2.1 already has Pi3/Pi3X in its
comparison table) except where this repo's code changes or corrects what those docs say.

**TL;DR: this is not a video→metric-mesh pipeline and doesn't try to be — it's a real, working
webcam-to-point-cloud *live streaming* demo built on top of a vendored copy of Meta/`yyfz`'s Pi3
repo. Nothing here fills any of our seven flagged gaps directly (no scale, no mesh/splat export, no
Quest/Unity, no cloud orchestration, no drone/telemetry, no voice/parts/commerce). The one thing
worth reading (not copying) is `realtime/pipeline.py`'s confidence-masked Umeyama Sim3 chunk-stitching
implementation — a clean, working reference for aligning overlapping feed-forward-model chunks with
explicit degenerate-transform rejection, useful if our own VGGT path ever needs to chunk a longer
sequence. Everything else — including the repo's own vendored `pi3/` model code — is either already
known to us, non-functional, or has no reuse value for this project.**

---

## 1. What it does, architecture, and the real pipeline in the code

The repo is two things bolted together:

1. **`pi3/`, `example.py`, `example_vo.py`, `example_mm.py`, `demo_gradio.py`, `examples/`** — a
   near-verbatim vendored copy of the upstream `yyfz/Pi3` repo (confirmed: `pi3/models/pi3.py` is
   byte-identical to `github.com/yyfz/Pi3`'s current `main`; `pi3/models/pi3x.py` differs only by
   version drift — TreeHacks' copy is missing upstream's newer `disable_multimodal`/
   `_chunked_conv_head` methods and still has training-only scale-augmentation code upstream has since
   dropped, i.e. vendored from an earlier snapshot of a moving-target repo, not edited by TreeHacks).
   `pi3/pipe/pi3x_vo.py` also exists upstream at the same path (confirmed via a direct fetch, HTTP
   200) — it's Pi3's own visual-odometry pipeline, not something TreeHacks wrote. None of this is
   TreeHacks' own work; it's the base model + its own example scripts, carried along as a dependency.
2. **`realtime/`** — TreeHacks' actual contribution: a FastAPI+WebSocket server
   (`realtime/server.py`, 937 lines) wrapping an incremental streaming version of Pi3X
   (`realtime/pipeline.py`, 930 lines, its own docstring says "Adapted from
   `pi3.pipe.pi3x_vo.Pi3XVO` for streaming use"), a SAM3-based 2D→3D object labeler
   (`realtime/object_labeler.py`, 434 lines), and a three.js browser frontend
   (`realtime/webserver/src/{sender,viewer}.ts`, ~1,380 lines TypeScript combined).

### 1.1 The real, working pipeline

```
browser webcam (getUserMedia, sender.html/sender.ts)
  -> base64 JPEG frames over WebSocket -> /ws/sender  (server.py:785-832)
  -> frame_queue (bounded, drops oldest on overflow, server.py:806-817)
  -> background processing_loop thread (server.py:180-305)
  -> IncrementalPi3.add_frame() buffers until a chunk_size=16 (default) window is full
  -> IncrementalPi3.process_chunk() / process_chunks_parallel():
       - Pi3X forward pass (bf16/fp16 autocast) on the chunk, with the PREVIOUS chunk's
         overlap=6 frames re-injected as pose/depth/ray conditioning (pipeline.py:198-233)
       - confidence + depth-edge masking (pipeline.py:246-256)
       - Sim3 (Umeyama, scale+rotation+translation) alignment of the new chunk onto the
         previous chunk's overlap region (pipeline.py:258-279, 867-918), with explicit
         rejection of degenerate transforms (NaN/Inf, scale <0.01 or >100 -> falls back to
         identity, pipeline.py:271-276)
       - NaN/outlier point filtering (>50 units from the running median, pipeline.py:320-329)
       - accumulates into a growing global point cloud (CPU-side lists of numpy arrays)
  -> binary-encoded new points (xyz f32 + rgb u8, server.py:123-135) streamed to all
     connected viewers over WebSocket -> /ws/viewer
  -> three.js Points/BufferGeometry, dynamically grown, rendered live with OrbitControls
     (viewer.ts:130-291)
```

A video-file path exists too (`feed_video_file`, `server.py:311-349`, driven by
`POST /upload-video`) — it just pushes decoded frames into the same `frame_queue` at a target FPS,
simulating a live camera. This is the only way to test the pipeline without a webcam, and is how a
`--video` CLI flag (`server.py:872-874, 903-908`) can drive the whole thing from a file at startup.

There is also a `process_chunks_parallel()` path (`pipeline.py:394-604`) that runs multiple chunks
concurrently on separate CUDA streams sharing one model's weights, then stitches them sequentially —
a real, non-trivial piece of engineering (auto-detects worker count from free VRAM,
`pipeline.py:376-387`), used only when `--workers` > 1 or auto-detection finds headroom.

### 1.2 Object labeling (SAM3 → 3D OBB)

`realtime/object_labeler.py` takes the incrementally-stored per-frame `{image, point_map,
conf_mask}` (kept by `pipeline.py:294-306`/`669-681` specifically to feed this), runs Meta's SAM3
text-prompted 2D segmentation per prompt per sampled frame (`object_labeler.py:170-255`), maps each
2D mask to 3D by indexing directly into Pi3X's own dense per-pixel `point_map` (no separate
depth/reprojection step needed — Pi3X already outputs world-space points per pixel), then computes a
tight oriented bounding box via Open3D: DBSCAN to isolate the largest point cluster, statistical
outlier removal, `get_oriented_bounding_box()`, with corners re-derived manually for a guaranteed
wireframe-render-safe ordering (`object_labeler.py:257-354`). Cross-frame detections for the same
prompt are deduplicated by simple AABB-overlap NMS (`object_labeler.py:356-398`). Triggered via
`POST /label-objects` (`server.py:584-692`), fire-and-forget in a background thread, results
broadcast over the viewer WebSocket when done.

A `POST /parse-nl-prompt` endpoint (`server.py:524-571`) calls OpenAI's `gpt-4o-mini` to turn a
free-text description ("I want to find the shelves so we can place the boxes into the shelves")
into a comma-separated object-noun list, which then feeds the labeler above — this is the repo's
only LLM-agent-shaped code.

---

## 2. Does it solve any of our problems?

| Our gap | Verdict | Evidence |
|---|---|---|
| Video/images → 3D | **Partial, not new to us** | Pi3X is already in `p1-fast-recon.md` §2.1's comparison table (BSD, ICLR 2026, "no shipped exporter, real integration cost"). This repo doesn't add a new model — it adds a *streaming wrapper* around one we already knew about. See §4 for what's actually worth reading. |
| Metric scale | **No** | Grepped the full repo for scale/calibration/ArUco/GPS/known-dimension logic: zero hits outside unrelated model-internal code (`pi3/models/dinov2/utils/utils.py`, `pi3/models/layers/pos_embed.py` — positional-embedding scale factors, not real-world scale). The Sim3 alignment (`pipeline.py:867-918`) only enforces *scale-consistency between chunks*, in whatever arbitrary units Pi3X's own output uses — never anchored to meters. |
| Mesh or splat export | **No** | Output is a raw point cloud only: `.ply` via `pi3.utils.basic.write_ply` (vendored, `example.py:61`) or `.npz` via `pipeline.py:756-798` (`save_to_disk`). No Poisson surfacing, no TSDF fusion, no OpenMVS/COLMAP anywhere in the repo (grepped, zero hits), no `.glb`/`.gltf` export anywhere (grepped; the only "mesh" string hits are `np.meshgrid` calls in `pi3/utils/geometry.py:36,96,122` and one comment, `viewer.ts:128`, "Ambient light (for potential future mesh rendering)" — i.e. explicitly *not implemented*). |
| Quest/Unity loading | **No** | Grepped for "Quest"/"Unity" across the repo: zero hits. The only 3D viewer is a browser three.js page (`viewer.ts`), desktop/mobile-web only, not WebXR. |
| Cloud GPU orchestration | **No** | Designed for one local machine with one local GPU only. `run.sh` activates a local venv (`~/pi3venv/bin/activate`) and runs `python server.py` directly — no cloud spin-up, no remote-run driver, no Slurm, nothing analogous to our laptop/hfbox split. |
| Drone control/telemetry | **No** | Grepped for "drone"/"DJI"/"telemetry"/"SRT"/"GPS" (case-insensitive) across the whole repo: zero hits. Input is exclusively a browser `getUserMedia` webcam feed or an uploaded video file fed through the identical code path — there is no capture-device-specific code of any kind. |
| Voice agent, parts, commerce | **No** | The only LLM-adjacent code is the `/parse-nl-prompt` text→object-list call (§1.2) and a frontend "AI Analysis" chat panel that is **non-functional** — see §3.2. Neither is a voice agent, a parts catalog, or a commerce flow; P4's domain is untouched here. |

---

## 3. Code quality, does it work as claimed, and license

### 3.1 What genuinely works

The core streaming reconstruction path (§1.1) is real, coherent, working code — not a stub. It
handles real failure modes explicitly and non-trivially: degenerate Sim3 rejection
(`pipeline.py:271-276, 649-653`), NaN/outlier point filtering (`pipeline.py:312-329, 688-704`),
frame-queue backpressure with oldest-frame-drop (`server.py:806-817`), and a genuinely useful
parallel-chunk inference path with VRAM-based auto-worker-detection (`pipeline.py:376-387`). This is
a legitimately non-trivial piece of systems engineering for a hackathon timeframe, and it reads as
authored with real understanding of the failure modes of chunked feed-forward reconstruction, not
copy-pasted.

### 3.2 What doesn't work / isn't reproducible, confirmed

Three concrete, verified gaps (a fourth, minor one noted but not counted against the 3-item
threshold since it's cosmetic doc rot, not a functional break):

1. **The frontend's "AI Analysis" chat panel calls a backend route that doesn't exist.**
   `viewer.ts:1069` does `fetch(`https://${location.hostname}:5000/analyze`, ...)` — but
   `realtime/server.py` has no `/analyze` route anywhere. Confirmed by grepping the full git history
   of `server.py` (`git log --all -p -- realtime/server.py | grep analyze` → zero hits) — this route
   was never implemented, in any commit, despite the commit that added the chat panel being titled
   "simple modifications + gemini" (`4d4de8b`). Clicking "Send" in this panel will always fail with a
   fetch error; it's dead/aspirational UI shipped as if functional.
2. **Four hard runtime dependencies are never listed in any requirements file.** `python-dotenv`
   (`server.py:29`), `openai` (`server.py:538`, lazy import), `open3d`
   (`object_labeler.py:264`, lazy import), and `sam3` (`object_labeler.py:71-72`, lazy import) are
   all real imports the code needs to run its own features — none appear in either
   `realtime/requirements.txt` (only `fastapi`, `uvicorn[standard]`, `python-multipart`,
   `websockets`) or the root `requirements.txt` (`torch`, `torchvision`, `numpy`, `pillow`,
   `opencv-python`, `plyfile`, `huggingface_hub`, `safetensors`). `sam3` in particular — Meta's
   newest Segment Anything model, powering the object-labeling feature — has no install
   instructions, pip index, or submodule reference anywhere in the repo. `realtime/README.md` is a
   single line ("# Treehacks 2026"), with no setup instructions at all. **The object-labeling
   feature, the repo's most novel piece beyond streaming reconstruction, is not reproducible from
   what's committed.**
3. **Two contradictory venv-activation instructions.** `realtime/run.sh:8` does
   `source ~/pi3venv/bin/activate`; `realtime/README.md:1` says `source ~/vggt/bin/activate` — a
   different, VGGT-named venv. Neither venv is committed or documented further; the mismatch (and
   the fact the README's env is literally named for a different model family) reads as doc rot from
   reusing scripts/notes across projects under hackathon time pressure.
4. *(Minor, not counted)* the committed `realtime/.env` contains `OPENAI_API_KEY=<REDACTED>` — this
   is a literal placeholder string, not a real key (confirmed by reading the file directly and by a
   length/prefix check before reading it). No real secret is committed.

### 3.3 License

The repo's single `LICENSE` file (root) is the file as vendored from Pi3, but it does **not** match
current upstream. Confirmed by fetching `github.com/yyfz/Pi3/main/LICENSE` directly: upstream today
is a clean, unambiguous 3-clause BSD (title "Copyright (c) 2025, the authors", clause 3 = the
standard "neither the name... may be used to endorse" non-endorsement clause) — matching what
`p1-fast-recon.md` §2.1 already cites for Pi3/Pi3X. **This repo's `LICENSE` file instead titles
itself "Pi3 for non-commercial purposes"** while keeping the permissive BSD-style body text
verbatim (and dropping upstream's clause 3, making the body 2-clause-shaped) — i.e., the title
asserts a non-commercial restriction the body's actual legal text never states. This is an internal
contradiction in the file as committed in *this* repo specifically (not present in current
upstream). No separate `LICENSE` covers `realtime/` (TreeHacks' own original code) — it inherits
whatever this one ambiguous file says, or defaults to all-rights-reserved if the file is read as
non-binding due to the contradiction. **Practical read: don't rely on this fork's `LICENSE` for
anything — if Pi3/Pi3X itself is ever adopted, pull the clean BSD-3-Clause text from
`github.com/yyfz/Pi3` directly, and treat anything from `realtime/` as ideas-only (reimplement, don't
copy), same posture `p1-prior-hackathons.md` already took with every unlicensed repo it audited.**

---

## 4. Concrete reuse

**Nothing here is worth copying verbatim**, both because of the license ambiguity above and because
nothing in this repo does something we don't already have a cleaner path to. Two patterns are worth
reading as a design reference (reimplement in our own code if ever needed, not copy-paste):

1. **Confidence-masked Umeyama Sim3 chunk-stitching**, `realtime/pipeline.py:867-918`
   (`_compute_sim3_umeyama_masked`) + `:920-930` (`_apply_sim3_to_points/poses`) + the calling
   pattern at `:258-279`/`:635-656` (compute transform from the overlap region, validate it — reject
   NaN/Inf or scale outside `[0.01, 100]` — fall back to identity otherwise). Our own VGGT path
   doesn't currently need to chunk (orbit clips fit in one VGGT call at ≤64 frames per
   `p1-fast-recon.md` §2.1), but if capture length ever grows past that (the same problem
   `p1-fast-recon.md` flagged VGGT-Long as solving for VGGT), this is a small, clean, real
   working example of the alignment math and — more usefully — of *what safety checks a real
   implementation needed in practice* (degenerate-transform rejection, NaN/outlier point filtering).
2. **Open3D tight-OBB-from-masked-points recipe**, `realtime/object_labeler.py:257-354`
   (`_compute_obb`): DBSCAN-isolate-largest-cluster → statistical outlier removal →
   `get_oriented_bounding_box()` → manually-ordered 8-corner output for safe wireframe rendering.
   If P1 or P4 ever wants "text-prompt an object, get a 3D box" (e.g. a parts-server AR pick flow),
   this is a small, legible reference for the Open3D calls involved — nothing here is Pi3-specific.

Everything else — the vendored `pi3/` package, `example*.py`, `demo_gradio.py` — is just upstream
Pi3, already better obtained directly from `github.com/yyfz/Pi3` (clean license, current version)
than through this fork.

---

## 5. Pitfalls to avoid

1. **Chunked feed-forward reconstruction drifts/fails often enough that explicit guard rails are
   not optional.** This repo's own code needed hard-coded scale bounds and NaN/Inf checks on every
   single Sim3 alignment (§4.1) and an outlier filter on every chunk's output points — real,
   hands-on evidence (not ours) that naive "just trust the model's chunk-to-chunk consistency" fails
   in practice, relevant if we ever extend VGGT beyond a single window.
2. **A "202 Accepted, results come later over WebSocket" pattern for a slow model call**
   (`label-objects`, `server.py:584-692`) is a clean pattern worth remembering generally for any
   slow background job in our own FastAPI parts server — fire a background thread, return
   immediately, broadcast the result when done — independent of this repo's specific bug (below).
3. **Ship-time check worth stealing directly for our own demo prep**: grep the frontend's `fetch(...)`
   call targets against the backend's actual route list before a live demo. This repo shipped a UI
   button (`/analyze`) with zero matching backend route, in every commit since it was added — a
   30-second check that would have caught it. Worth doing once for our own `/scenes` server + any
   UI before the Sat 6 PM checkpoint the plan already has.
4. **Undocumented/unpinned dependencies for a "demoed" feature are a real trap under hackathon time
   pressure**, confirmed here for `sam3`/`open3d`/`openai`/`python-dotenv` (§3.2) — a feature that
   worked on the author's own machine but can't be reproduced from the committed repo. Worth a final
   pass on our own `requirements.txt`/`pyproject.toml` completeness before the event, not as a new
   finding but as independent corroboration it's a real class of hackathon failure.

---

## 6. Verified vs. unverified

**Verified hands-on this session** (repo cloned to a scratch dir, read directly, not just the
README): repo structure and file listing (§1); `pipeline.py`/`server.py`/`object_labeler.py`/
`viewer.ts`/`sender.ts` read in full; grep sweeps for drone/DJI/telemetry/SRT/GPS, mesh/glb, Quest/
Unity, scale/calibration terms (all zero relevant hits, §2); `realtime/.env`'s committed value
confirmed to be the literal placeholder string `<REDACTED>` (10 chars), not a real key; `/analyze`
confirmed absent from `server.py` across its entire git history; `python-dotenv`/`openai`/`open3d`/
`sam3` confirmed absent from both requirements files by direct diff against the actual `import`
statements; `pi3/models/pi3.py` confirmed byte-identical to a fresh fetch of upstream
`yyfz/Pi3/main`; `pi3/models/pi3x.py` diffed against the same fetch (differs by version drift, not
TreeHacks edits); `pi3/pipe/pi3x_vo.py` confirmed to exist upstream at the same path (HTTP 200);
this repo's `LICENSE` diffed directly against a fresh fetch of upstream `yyfz/Pi3/main/LICENSE`.

**Not run**: the server itself was not started, no webcam/video was fed through it, no SAM3/OpenAI
API call was made (per the task's own "don't run code that calls paid APIs" constraint — a real
`OPENAI_API_KEY` would be needed for `/parse-nl-prompt` and the repo has no real key committed
either way; `sam3` isn't installed in this environment and no attempt was made to install it). All
"this code works" claims in §3.1 are from reading the code's logic and structure, not from executing
it — labeled as such.

**Unverified / not checked this pass**: whether `demo_gradio.py`/`example.py`/`example_vo.py`/
`example_mm.py` differ from current upstream beyond a `diff -q` byte-difference check (they do
differ; not diffed line-by-line — low priority since none of it is TreeHacks' own work either way);
exact intent behind the `~/pi3venv` vs `~/vggt` venv-naming mismatch (§3.2 point 3) — flagged as
"reads like doc rot," not confirmed from any commit message or comment.

---

## 7. Sources

- Repo: https://github.com/Vort3xed/TreeHacks (cloned to a local scratch dir, read-only, not
  committed anywhere in this repo)
- Upstream Pi3 (for license/version-drift comparison): https://github.com/yyfz/Pi3
  (`LICENSE`, `pi3/models/pi3.py`, `pi3/models/pi3x.py`, `demo_gradio.py`, `example.py` fetched
  directly from `raw.githubusercontent.com/yyfz/Pi3/main/...`)

**Local repo (read, not re-cited inline above)**
- `docs/research/p1-prior-hackathons.md` (format/precedent for this doc)
- `docs/research/p1-fast-recon.md` §2.1 (existing Pi3/Pi3X coverage)
- `docs/research/p1-cloud-world-models.md`, `docs/research/p1-colmap-realityscan.md` (read for
  context, no overlap found worth citing inline)
