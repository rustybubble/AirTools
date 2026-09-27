# P1 GPU/ROCm evaluation: VGGT fallback pose + monocular metric depth (hfbox, gfx1201)

Scope: hands-on evaluation of the two GPU-side "watch" items from `docs/research/p1-toolchain.md`
§1.2/§5 FALLBACK/§6.1 that couldn't be tested there because the hfbox container wasn't reachable in
that session. Everything below **was** run, live, on the actual container: AMD RX 9070 XT 16GB
(gfx1201, RDNA4), PyTorch 2.14+ROCm7.2 preinstalled in `/opt/venv`, root, only `/workspace`
persists. Test clip: `/workspace/airtools/data/corpus/strasbourg-cathedral-spire.mp4` (16.35s,
1920×1080, ~60fps, 12.3MB — confirmed a complete, non-growing file before use).

**Everything in this document was run hands-on on hfbox this session** — no "from docs" claims,
unlike parts of `p1-toolchain.md`. Where something is inferred rather than directly observed
(e.g. *why* a number looks the way it does), it's labeled "inference," not stated as fact.

---

## 0. Environment facts, confirmed hands-on

```
torch.__version__            2.14.0+rocm7.2
torch.cuda.is_available()    True
torch.cuda.get_device_properties(0).gcnArchName   gfx1201
torch.version.hip            7.2.53211
torch.cuda.is_bf16_supported()   True
torch.backends.cuda.flash_sdp_enabled()        True
torch.backends.cuda.mem_efficient_sdp_enabled() True
torch.backends.cuda.math_sdp_enabled()          True
```

- **No `HSA_OVERRIDE_GFX_VERSION` or any `MIOPEN_*` env var was needed** — gfx1201 is recognized
  natively by this PyTorch/ROCm build out of the box, every test below ran with a stock shell env.
- **No `rocminfo`/`rocm-smi` binaries exist in this container** (`which` returns nothing, `/opt/`
  only has `venv/`) — all VRAM numbers below are `torch.cuda.max_memory_allocated()`, not a
  system-level GPU memory reading.
- **No separate `flash_attn` pip package is installed or needed.** PyTorch's own
  `F.scaled_dot_product_attention` (what VGGT's attention blocks call, `fused_attn=True` by
  default) dispatches to a ROCm flash-attention backend itself (`flash_sdp_enabled() == True`); a
  standalone `flash-attn` wheel was never installed and nothing needed it.
- **First-kernel-compile warmup is real and consistent across three independent model families**
  (VGGT, MoGe-2, Depth-Anything-V2) — a fresh shape hits a one-time compile cost of **~7-15s**,
  then drops to **0.1-0.2s** on a repeat of the same call. Confirmed by re-running the identical
  call twice in-process (`warmup_cold_2frames` 12.71s → `warm_2frames_repeat` 0.16s, same process,
  see §1.2 table). This is presumably MIOpen/AOTriton kernel-selection+compile on first use of a
  given tensor shape (inference from the timing pattern, not read from ROCm's own logs — no
  `MIOPEN_ENABLE_LOGGING` was set to confirm the mechanism directly).
- **VGGT additionally showed a second, larger compile-like jump specifically the first time frame
  count grew past the tiny warmup size** — `bf16_24frames` took 42.9s where `bf16_48frames` and
  `bf16_64frames` (larger, later) only took 8.2s/10.7s. Inference: a coarse/large-batch kernel
  variant gets selected/compiled once the first time total sequence length crosses some threshold,
  then reused regardless of exact size after that — not confirmed against ROCm internals, only
  observed as a timing artifact.

---

## 1. VGGT as fallback pose estimator

### 1.1 Install — real pitfalls, all hit and fixed live

Setup script: `/workspace/exp/setup_gpu_envs.sh`, copied to
`pipeline/tools/setup_gpu_envs.sh` — ran twice against the real container (once from a clean
`/workspace/envs/vggt`, once idempotently against the already-populated one) and both times ended
with `torch=2.14.0+rocm7.2 cuda_available=True`.

1. **`uv venv --system-site-packages --python /opt/venv/bin/python3.12 ...` does NOT inherit
   `/opt/venv`'s torch.** `/opt/venv` is itself a venv (`pyvenv.cfg`: `home = /usr/bin`), and a
   venv's own "system site-packages" always resolves to its *true base* Python (`/usr`), not an
   intermediate venv layer — standard Python venv semantics, confirmed by inspecting
   `/workspace/envs/vggt/pyvenv.cfg` after creation (`home = /usr/bin`) and the resulting
   `ModuleNotFoundError: No module named 'torch'`. **Fix**: create a plain venv, then drop a
   `.pth` file containing `/opt/venv/lib/python3.12/site-packages` into the new venv's own
   site-packages dir. Confirmed working: `torch 2.14.0+rocm7.2, cuda available True` afterward.
2. **Any package with loosely-declared-but-still-real torch deps will silently reinstall a plain
   CUDA torch, shadowing the inherited ROCm one.** Hit this twice, independently:
   - `pip install git+https://github.com/jytime/LightGlue.git` (needed for `demo_colmap.py`, see
     below) pulled `torch==2.14.0` (plain, `+cu130`) + `torchvision` + `triton` + 17 `nvidia-*`
     wheels, ~1.3GB, via its `kornia` dependency. Confirmed: `torch.__version__` became
     `2.14.0+cu130`, `torch.cuda.is_available()` became `False`.
   - Same thing again from `pip install transformers accelerate` (for the metric-depth work, §2) —
     `accelerate` pulled the same CUDA stack back in.
   - **Fix (same both times)**: `pip uninstall torch torchvision triton` + all the `nvidia-*`/
     `cuda-*` packages it added, then re-check. Local venv site-packages take priority over the
     `.pth`-appended path, so once the bad packages are gone, the inherited ROCm torch becomes
     visible again automatically — no re-install of torch itself needed.
3. **`opencv-python` (GUI build) and `opencv-python-headless` collide** — they share the `cv2`
   namespace and can't coexist; LightGlue's own deps pulled in the GUI `opencv-python` on top of
   the headless one already installed, and importing `cv2` then failed with
   `ImportError: libxcb.so.1: cannot open shared object file` (no X11 libs in this minimal
   container). **Fix**: uninstall both, `pip install --reinstall opencv-python-headless`.
4. **`demo_colmap.py` imports `lightglue` and `hydra-core` unconditionally at module load time —
   not just for `--use_ba`.** The task briefing (and `requirements_demo.txt`'s own comment: "feel
   free to skip the dependencies below if you do not need demo_colmap.py") implies these are
   BA-only. They are not: `demo_colmap.py` → `vggt.dependency.track_predict` →
   `vggt.dependency.vggsfm_utils` → `from lightglue import ALIKED, SIFT, SuperPoint` and
   `vggt.dependency.vggsfm_tracker` → `from hydra.utils import instantiate`, both at the top of
   the file, both hit and confirmed with `ModuleNotFoundError` before either was installed — even
   with `--use_ba` omitted. **The whole script needs pycolmap + lightglue + hydra-core +
   omegaconf just to import, regardless of `--use_ba`.**
5. **pycolmap version mismatch, confirmed via a live `AttributeError` and the constructor's own
   signature.** `docs/research/p1-toolchain.md`'s PRIMARY (CPU) pipeline uses `pycolmap==4.2.0`.
   VGGT's own `vggt/dependency/np_to_pycolmap.py` calls
   `pycolmap.Image(id=fidx+1, name=..., camera_id=..., cam_from_world=...)`. On pycolmap 4.2.0
   this raises `AttributeError: 'pycolmap._core.Image' object has no attribute 'id'` — confirmed
   the constructor was renamed: `pycolmap.Image.__init__` in 4.2.0 takes `image_id=`, not `id=`
   (dumped via `help(pycolmap.Image.__init__)`). `requirements_demo.txt` itself pins
   `pycolmap==3.10.0`; installing exactly that version fixed it. **The VGGT venv needs a
   different, older pycolmap than the CPU pipeline venv** — not a conflict in practice since
   they're separate venvs already by the task's own design, but worth knowing if anyone ever tries
   to share one venv. Cross-version **read** compatibility was checked and is fine: pycolmap 4.2.0
   opened the sparse models pycolmap 3.10.0 wrote (both the no-BA and the BA-8 ones) without
   error, correct point/camera counts — it's a stable on-disk binary format, only the *Python
   construction API* changed.
6. **`demo_colmap.py`'s own weight download bypasses the HF cache entirely and writes outside
   `/workspace`.** It uses `torch.hub.load_state_dict_from_url(...)`, which defaults to
   `~/.cache/torch/hub` = `/root/.cache/torch` in this container — not `/workspace`, so it won't
   survive a container reset, and it's a **second, separate 4.68GB download** of the same
   `facebook/VGGT-1B` weights already fetched once via `huggingface_hub`/`VGGT.from_pretrained`
   into `/workspace/.hf`. **Fix**: set `TORCH_HOME=/workspace/.cache/torch` before running
   `demo_colmap.py` (done for all runs below). Not fixed: the double-download itself (a real
   leftover — `demo_colmap.py`'s model-loading code would need a small patch to use
   `VGGT.from_pretrained` like the rest of the codebase instead of its own `torch.hub` call).
7. **Weights**: `facebook/VGGT-1B` (default checkpoint) downloads with **no token, no gating**
   — confirmed, plain `hf_hub_download` succeeded unauthenticated. License: **CC-BY-NC-4.0**
   (confirmed via `huggingface_hub.model_info(...).card_data["license"]`), matches
   `p1-toolchain.md`. `facebook/VGGT-1B-Commercial` **is gated**: confirmed
   `GatedRepoError: 401` — "Access to model ... is restricted. You must have access to it and be
   authenticated" — no HF token exists on this box, so the commercial checkpoint could not be
   fetched or tested this session.

### 1.2 dtype + frame-count sweep (`facebook/VGGT-1B`, raw `model(images)` forward pass, no BA)

All 24/48-frame runs use the 49-frame, 3fps, 518px-long-side extraction
(`/workspace/exp/strasbourg/frames`); the 64-frame run uses a separate 4fps extraction of the same
16.35s clip (65 frames) since 3fps of a 16s clip only yields 49 frames total — noted explicitly,
not silently substituted. Peak VRAM = `torch.cuda.max_memory_allocated()`, reset before each call,
same process (model loaded once, `model_load: 7.7s` from a warm HF cache).

| frames | dtype | elapsed (s) | peak VRAM | note |
|---|---|---|---|---|
| 2 (warmup) | bf16 | 12.71 | 7.36 GB | first-ever forward pass, cold kernel compile |
| 2 (repeat) | bf16 | 0.16 | 6.92 GB | same call again, warm |
| 24 | bf16 | 42.88 | 8.46 GB | first "large" shape — second compile tier, see §0 |
| 48 | bf16 | 8.22 | 9.07 GB | |
| 64 | bf16 | 10.74 | 9.63 GB | **well under the 16GB ceiling — 6.4GB headroom** |
| 24 | fp16 | 6.66 | 8.37 GB | |
| 48 | fp16 | 7.24 | 9.07 GB | |
| 64 | fp16 | 10.59 | 9.63 GB | |
| 24 | fp32 | 24.74 | 6.68 GB | slower **and** lower peak VRAM than bf16/fp16 — see note |
| 48 | fp32 | 66.44 | 7.65 GB | 8x slower than bf16 at the same frame count |

**bf16 and fp16 both work cleanly on gfx1201** — no crashes, no NaNs observed, comparable speed
and VRAM (fp16 marginally faster/lower at 24 frames, essentially tied at 48/64). **fp32 works but
is 6-8x slower**, and, counter-intuitively, uses *less* peak VRAM than bf16/fp16 at the same frame
count. Inference (not confirmed by direct backend introspection): PyTorch's SDPA flash-attention
backend on ROCm is bf16/fp16-only, so an fp32 forward pass falls back to a different SDPA backend
(mem-efficient or math) that is both slower and has different memory characteristics than the
flash path — consistent with the observed slowdown, but not verified by explicitly querying which
SDPA backend was dispatched for each dtype.

**Direct update to `p1-toolchain.md` §1.2's VRAM estimate**: that doc (written without hfbox
access) cited secondhand community reports suggesting a 60-80 frame ceiling on a 24GB card and
recommended chunking to ≤50-64 frames on this 16GB card "to be safe." **Measured here: 64 frames
at bf16 peaks at 9.63GB, i.e. 60% of the card, 6.4GB of headroom** — chunking to 64 is comfortably
conservative, not tight. This also lines up with a change noted in VGGT's own README changelog
(May 15, 2026): "we fixed an implementation issue that was keeping redundant intermediate tensors
in memory... VGGT can now run on roughly 2-3x more input frames" on the same budget — this
measurement is consistent with that fix being live in the current checkout, though the *cause*
(the README's own claim) wasn't independently re-derived, only the *effect* (actual measured VRAM)
was. Pushing past 64 frames was not attempted (out of scope — the task specified up to 64), but
there's clearly room to.

### 1.3 `demo_colmap.py` — with and without `--use_ba`

**Without `--use_ba`** (49 frames, defaults): ran cleanly, ~model-load + a few seconds. Output
`sparse/` (COLMAP binary format) opened with pycolmap 4.2.0:

```
num_reg_images = 49   (49/49 = 100% registered)
num_cameras    = 49
num_points3D   = 0     (batch_np_matrix_to_pycolmap_wo_track: poses only, no point cloud by design)
```

**Camera trajectory plausibility** (`/workspace/exp/circle_fit.py`: SVD best-fit plane through the
49 camera centers, then a Kasa algebraic circle fit in that plane):

| metric | value |
|---|---|
| planarity RMS / radius | 0.25% |
| radius-spread RMS / radius | 0.99% |
| sequential arc coverage (unwrapped) | 43.7° |
| consecutive-step direction monotonicity | 100% |

Very tight, physically plausible fit — sub-1% radius spread, sub-0.3%-of-radius planarity, and a
smooth single-direction sweep with zero direction reversals. **Not a full 360° orbit** — a 43.7°
partial arc — which is a property of this specific 16s clip, not a VGGT limitation; worth knowing
before assuming "orbit" numbers from `p1-toolchain.md` apply literally to this footage.

**With `--use_ba`**: fails with `torch.OutOfMemoryError` at 16, 24, and the full 49 frames
(518px long side, all default BA params: `query_frame_num=8, max_query_pts=4096,
fine_tracking=True`). **Works at 8 frames**: `8/8` registered, `num_points3D = 15592`,
`mean_track_length = 5.63`. So the practical `--use_ba` ceiling on this card at this resolution is
**somewhere between 8 (works) and 16 (OOMs)** — narrowed no further (diminishing returns for the
time budget).

What actually breaks, confirmed from the traceback: the OOM is inside the fine-tracking stage's
`CorrBlock.__init__` → `fmaps.reshape(B * S, C, H, W)` (`vggt/dependency/track_modules/blocks.py`),
where `S` is the **total number of input frames**, not the query-related knobs. Concretely:
- Reducing `--max_query_pts 512 --query_frame_num 3` (defaults 4096/8) at 24 frames **still
  OOMed**, in the same `CorrBlock` reshape — these flags don't help because they don't touch `S`.
- The tool prints its own remediation hint, `"For faster inference, consider disabling
  fine_tracking"` — **but there is no way to do that from the CLI**: `demo_colmap.py`'s argparse
  defines `--fine_tracking` as `action="store_true", default=True`, which can only ever leave it
  `True` (a real upstream bug, confirmed by reading `parse_args()` directly — passing the flag
  adds nothing, and there's no `--no-fine_tracking`/`BooleanOptionalAction` counterpart).
- At the point of the 49-frame OOM, `12.26GB` was "already allocated by PyTorch" before the
  failing 7.53GB request — the track-refinement stage loads a second full model stack (DINOv2
  ViT-B/14, ALIKED, SuperPoint, the VGGSfM tracker) *on top of* VGGT's own already-resident
  activations, which is most of why this is so much tighter than the plain VGGT forward pass in
  §1.2.

### 1.4 Part A verdict

**Recommend VGGT as the fallback pose estimator, no-BA path only.** It's fast, 100% camera
registration on this clip, a tight/plausible trajectory fit, and comfortable VRAM headroom to 64
frames (and likely beyond, untested). **Do not recommend `--use_ba` on this hardware** — it's
memory-fragile below 16 frames at 518px with default settings, the tool's own suggested fix isn't
reachable via its CLI, and the no-BA path already gives clean camera poses (its only real gap is
`num_points3D = 0` — no fallback point cloud). If a fallback point cloud is actually needed
(matches `p1-toolchain.md` §2.4's plan — VGGT depth maps → Open3D TSDF, *not* `--use_ba`'s sparse
points), call VGGT's own `model(images)` forward pass directly for `depth_map`/`point_map` output
(§1.2's benchmark already does this) rather than routing through `demo_colmap.py --use_ba`.

---

## 2. Metric depth for scale (no GPS/scale-bar footage)

Two models tried, as the task allowed ("ideally two") — both installed cleanly on ROCm, no compile
step for either. Setup in the same `setup_gpu_envs.sh depth` stage, verified idempotent end-to-end
on the box.

### 2.1 Install

- **MoGe-2** (`Ruicheng/moge-2-vitl-normal`, via `git+https://github.com/microsoft/MoGe`): the
  upstream repo has moved on to a "MoGe-3" release (`pyproject.toml` now says `name = "moge",
  version = "3.0.0"`), but the v2 model class and checkpoint are still present and directly
  selectable (`moge.model.import_model_class_by_version("v2")`) — the task's exact suggested
  checkpoint ID still resolves. Installed with `--no-deps` + hand-picked deps, **skipping**
  `flex-gemm` and `pipeline` (both pinned git deps in `pyproject.toml`): confirmed by reading the
  import graph that `moge/model/v2.py` never imports `flex_gemm`/`sparse_unet` (those are
  MoGe-3-only, used by `moge/model/v3.py` and the training scripts) — not by attempting the
  install and hitting a wall, i.e. this was verified via source inspection before skipping it, not
  assumed. `utils3d` (the plain PyPI package `v2.py` also accepts as a fallback) pulls `open3d`,
  which **has no `cp312` wheel** — used the project's own fork
  (`utils3d_moge @ git+...EasternJournalist/utils3d-moge`) instead, which installs clean. License:
  **MIT** (confirmed via `model_info(...).card_data["license"]`).
- **Depth-Anything-V2-Metric-Outdoor-Large** (`depth-anything/Depth-Anything-V2-Metric-Outdoor-Large-hf`,
  via `transformers.AutoModelForDepthEstimation`): installing `transformers`+`accelerate` hit the
  same torch-shadowing trap as §1.1 point 2 — fixed the same way. License: **Apache-2.0**
  (confirmed via the same `model_info` check).

### 2.2 Results — 8 frames spread evenly across the clip

Task's plausibility anchor: the Strasbourg cathedral spire is 142m tall; a drone shooting it is
plausibly 50-200m away.

| model | center-of-frame depth (8 frames) | depth range seen | steady-state speed | peak VRAM | plausible? |
|---|---|---|---|---|---|
| MoGe-2 (`moge-2-vitl-normal`) | 440-949 m | 8.5-1398 m | 0.09-0.15s/frame (15.0s first) | 3.03 GB | **No** — 3-10x too far |
| Depth-Anything-V2-Metric-Outdoor-Large | 53-71 m | 7.9-79.2 m | 0.17s/frame (7.2s first) | 2.15 GB | Plausible, **but capped** |

**MoGe-2's absolute metric numbers are not usable for this footage as-is.** Center-of-image depth
ranged 440-949m across the 8 frames — a factor of 3-10x beyond the expected 50-200m. Inference,
not confirmed against MoGe-2's training data directly: its metric-scale head is most likely
calibrated on its training distribution's typical scene scale (indoor/street-level), and doesn't
generalize to a wide-FOV aerial shot dominated by sky and a distant horizon.

**Depth-Anything-V2-Metric-Outdoor's numbers are plausible on their face (53-71m) but show a hard
ceiling**: `depth_max` landed at **78.7-79.2m on every single one of the 8 frames independently**
— not a coincidence, a saturation ceiling. Inference: this checkpoint's metric head was very
likely trained/calibrated against an outdoor dataset with an ~80m depth range (VKITTI-style),
meaning it **cannot represent true distances beyond ~80m at all** — it will clip, not
extrapolate. If the drone is genuinely 100-200m from the spire (the task's own plausibility
range), this checkpoint's output for anything past the building's near face should be treated as
"at least 80m," not a real value.

### 2.3 Part B verdict

**Neither checkpoint's absolute output is trustworthy as ground truth for this footage without
caveats.** Depth-Anything-V2-Metric-Outdoor is the more usable starting point (right order of
magnitude, permissive license, fast, low VRAM) but its ~80m ceiling means it should be treated as a
lower-bound / sanity check, not literal ground truth, for anything shot from further than ~80m —
worth re-checking whether a longer-range outdoor metric checkpoint exists before committing to it.
MoGe-2's numbers are off by roughly an order of magnitude for this specific footage and shouldn't
be used un-corrected. Both are fast enough (sub-0.2s/frame, <4GB VRAM) that cost isn't the
blocker — plausibility is.

---

## 3. Metric scale from depth: the sketch

`/workspace/exp/metric_scale.py`, copied to `pipeline/experiments/metric_scale.py` (identical,
diffed to confirm). Standalone — **not** wired into `pipeline/calibrate.py` yet (that file already
has a `ScaleEstimate(method="metric_depth", ...)` slot reserved for this; hooking it up is a real
leftover, not done here, out of this task's scope).

Two functions:
- `scale_from_depth_pair(metric_depth, recon_depth, valid)` — robust **median ratio**
  `metric/recon` over valid, pixel-aligned depth samples for one frame (SfM keypoints' triangulated
  distance, or VGGT's own dense depth-head output).
- `combine_frame_scales(per_frame, min_samples)` — **median across frames' per-frame medians**,
  with **MAD** as the residual; frames with too few valid samples (e.g. a mostly-sky drone frame)
  are dropped before that median so they can't skew the estimate.

Self-check (`python pipeline/experiments/metric_scale.py`, `assert`-based, no framework): builds a
synthetic scene at a known scale with 3% per-pixel noise, 15% grossly-mismatched pixels per frame,
one whole-frame outlier (50x off), and one near-empty frame (should be dropped by
`min_samples`) — recovers the true scale within 5% and MAD within 10% of the recovered value. Ran
both locally and on hfbox (`/opt/venv/bin/python3.12`):

```
$ python3 pipeline/experiments/metric_scale.py
true_scale=23.7  recovered=23.762  mad=0.007
OK
```

**Real caveat this sketch does not solve**: MAD-of-per-frame-medians is robust to *noisy* frames,
not to a *systematically biased* model (e.g. Depth-Anything-V2-Metric's ~80m clip from §2.2) — a
consistent ceiling would show up as a consistent scale bias across every frame, not get diluted
away by the median. `pipeline/calibrate.py`'s existing `_METHOD_RANK` already ranks
`"metric_depth"` below `"gps"` and above only `"none"` for exactly this reason — nothing here
changes that ranking, this just confirms it's the right call.

---

## 4. Recommendation

1. **Use VGGT (no-BA) as the fallback pose estimator** when `pycolmap` SfM fails outright. Chunk
   to ≤64 frames per call (confirmed comfortable at 9.6GB/16GB on this card; likely room to push
   higher, untested). Use a dedicated venv with `pycolmap==3.10.0` (not 4.2.0) inside it. Set
   `TORCH_HOME=/workspace/.cache/torch` before any `torch.hub`-based weight download.
2. **Do not use `--use_ba`** on this hardware beyond ~8-15 frames at 518px — it OOMs, and its own
   suggested fix (`disable fine_tracking`) isn't reachable from the CLI as shipped. If a fallback
   point cloud (not just poses) is needed, pull VGGT's own depth/point-head output directly instead
   of routing through `demo_colmap.py --use_ba`.
3. **For metric scale from monocular depth, start with Depth-Anything-V2-Metric-Outdoor-Large**
   (Apache-2.0, plausible order-of-magnitude, fast, low VRAM) over MoGe-2 (MIT, but 3-10x off on
   this footage) — but treat its output as capped at ~80m and verify whether a longer-range
   checkpoint exists before trusting it past that distance. Either way, metric-depth scale stays
   ranked below GPS/ArUco/known-dimension in `calibrate.py`'s existing method ranking — this
   evaluation is a reason to keep it there, not to promote it.

---

## 5. Files left behind

- `/workspace/exp/setup_gpu_envs.sh` == `pipeline/tools/setup_gpu_envs.sh` (identical; ran
  end-to-end twice on hfbox, both times ending in a confirmed ROCm torch).
- `/workspace/exp/metric_scale.py` == `pipeline/experiments/metric_scale.py` (identical; self-check
  passed both locally and on hfbox).
- `/workspace/exp/bench_vggt_sweep.py`, `/workspace/exp/circle_fit.py`, `/workspace/exp/run_moge2.py`,
  `/workspace/exp/run_dav2.py` — the scripts behind every number in §1.2/§1.3/§2.2 (left in
  `/workspace/exp` for reference, not copied locally — not part of the assigned deliverable).
- `/workspace/exp/strasbourg/vggt_bench_results.json`, `moge2_results.json`, `dav2_results.json`,
  `demo_noba2.log`, `demo_ba8.log`, `demo_ba16.log`, `demo_ba24.log`, `demo_ba24b.log` — raw logs
  behind the tables above.
- `/workspace/envs/vggt`, `/workspace/envs/depth` — the two working venvs, reusable as-is by
  whoever runs the next experiment on this box (persist under `/workspace`).

**Flag, confirmed**: the repo's own `.gitignore` line 8 is a bare `tools/` (no leading slash),
which git matches against a directory named `tools` at *any* depth — including
`pipeline/tools/`, not just a hypothetical top-level `tools/`. Confirmed via
`git check-ignore -v pipeline/tools/setup_gpu_envs.sh` → matches `.gitignore:8:tools/`. The file
is correctly placed on disk at the exact requested path, but a plain `git add -A`/`git add .`
will silently skip it; committing it needs `git add -f pipeline/tools/setup_gpu_envs.sh` or a
`.gitignore` fix. Not changed here (`.gitignore` wasn't part of this assignment and `work/`/
`scene/` next to it look like other intentional scratch-dir ignores this pattern might be
piggybacking on) — flagging so it isn't lost when committing.

**Leftover, not built here** (noted per the task, not actioned — out of the assigned scope):
double weight download in `demo_colmap.py` (torch.hub vs. huggingface_hub cache, ~9.4GB total for
one model if both paths are used); wiring `metric_scale.py` into `pipeline/calibrate.py`'s
`ScaleEstimate` machinery; narrowing the exact `--use_ba` frame ceiling between 8 and 16; checking
whether a longer-range (>80m) outdoor metric-depth checkpoint exists.
