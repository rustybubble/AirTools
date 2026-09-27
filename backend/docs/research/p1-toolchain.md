# P1 toolchain research: drone video → poses → textured mesh → decimated GLB (AMD/ROCm, no NVIDIA)

Scope: replace the plan's `docs/AirTool implementation plan.md` §2 pipeline (`SPLAT` branch via
Brush/gsplat + `MESH` branch via Open3D Poisson) with a **mesh-only** chain, per this task's
explicit direction that Gaussian splats are dropped (too heavy for standalone Quest 3S). Target:
4K MP4 → ffmpeg frames → camera poses → dense reconstruction → **textured** triangle mesh →
decimated (~200k tri) GLB with 1-2 embedded 4096×4096 JPEG atlases. Real-world scale is applied
afterward by the plan's own Umeyama/ArUco calibration code (§2.3) — nothing here needs to produce
metric scale itself.

**Conflict to flag for P1 (same pattern as `assets-3d.md`'s Tripo3D flag):** the plan's §2/§7b still
names Brush, gsplat, SuperSplat and Open3D Poisson meshing as the pipeline. This doc treats those
as superseded for the mesh deliverable — the plan document itself hasn't been edited (out of scope
here, only this file and throwaway scratch venvs were touched). It also drops the plan's own GLOMAP
guidance ("COLMAP/GLOMAP... or a feed-forward model") in favor of a more specific finding: GLOMAP is
no longer a separate tool to install at all (§1).

**Hardware constraint recap:** hfbox container, root, only `/workspace` persists; AMD RX 9070 XT
16GB (gfx1201, RDNA4), PyTorch 2.14 + ROCm 7.2 preinstalled in `/opt/venv`, Python 3.12, no NVIDIA.
Dev laptop: Fedora 44 x86_64, 16 cores, 15GB RAM, no GPU. Both boxes turned out to be directly
testable in this research pass — the laptop *is* the environment this research ran in (confirmed:
OpenMVS's own `--help` banner below prints `CPU: 13th Gen Intel(R) Core(TM) i7-1360P (16 cores)`,
`RAM: 15.30GB Physical Memory`, matching the stated laptop spec exactly). The hfbox container itself
was **not** reachable from this session — anything ROCm-specific below is doc/repo evidence, not a
hands-on ROCm run, and is labeled as such.

---

## 1. Pose estimation

### 1.1 pycolmap / GLOMAP / colmap — verified hands-on

Installed in a scratch venv (`uv venv --python 3.12`, `uv pip install pycolmap`, no other deps):
36.5MB wheel, pulled only `numpy==2.5.3`, **no torch, no compilation**.

```
pycolmap.__version__        -> not exposed as __version__; pycolmap.COLMAP_version -> "COLMAP 4.2.0"
pip package version         -> pycolmap==4.2.0 (matches COLMAP 4.2.0, released 2026-08-31)
wheel platforms             -> manylinux_2_28_x86_64, macosx_14_0_arm64, win_amd64; cp310-cp314
license                     -> BSD-3-Clause
```

**GLOMAP is no longer a separate install.** `colmap/colmap` changelog: "Integrated GLOMAP global SfM
pipeline into COLMAP as a first-class alternative to the incremental/hierarchical mappers... GLOMAP
is maintained through the COLMAP repository going forward" (COLMAP 4.0.0, 2026-03-14). The standalone
`colmap/glomap` repo is now archived/deprecated. Confirmed hands-on in the pip wheel:

```python
>>> hasattr(pycolmap, "global_mapping")       # True
>>> hasattr(pycolmap, "global_mapper")        # False — it's global_mapping, not global_mapper
>>> pycolmap.global_mapping.__doc__
'global_mapping(database_path, image_path, output_path, options=GlobalPipelineOptions()) -> dict[int, Reconstruction]\n\nRecover 3D points and camera poses using global SfM (GLOMAP)'
>>> hasattr(pycolmap, "incremental_mapping")  # True — classic COLMAP mapper, still available
```

**CPU SIFT extraction + matching is the default**, not an opt-in — confirmed hands-on:

```python
>>> pycolmap.FeatureExtractionOptions().use_gpu   # False (default)
>>> pycolmap.FeatureMatchingOptions().use_gpu     # False (default)
>>> pycolmap.extract_features.__doc__  # ...device: Device = Device.auto...
>>> list(pycolmap.Device.__members__)  # ['auto', 'cpu', 'cuda'] — no hip/rocm entry
```
`Device` only knows `cpu`/`cuda`/`auto`; there's no ROCm path for COLMAP's own optional SiftGPU/
OpenGL-based GPU extraction, but that's moot — `use_gpu` defaults `False` on the CPU wheel, so
`extract_features`/`match_sequential` just run CPU SIFT + CPU matching with `num_threads=-1` (all
cores) by default. This is exactly the "CPU SIFT extraction+matching, sequential matcher" the task
asked about, confirmed live rather than assumed.

**`pycolmap.Reconstruction` writes COLMAP's binary sparse format directly**, which is what OpenMVS's
`InterfaceCOLMAP` reads:
```python
>>> [a for a in dir(pycolmap.Reconstruction) if 'write' in a or 'export' in a]
['export_PLY', 'write', 'write_binary', 'write_text']
```

**One real pitfall found only by reading OpenMVS's own `--help` text (not in any pycolmap doc):**
`InterfaceCOLMAP --help` says *"In order to import a scene, run COLMAP SfM and next undistort the
images (only PINHOLE camera model supported for the moment)."* A DJI drone's real lens model is not
PINHOLE. Fix: `pycolmap.undistort_images(output_path=..., input_path=..., image_path=...,
output_type="COLMAP")` between mapping and `InterfaceCOLMAP` — confirmed the function exists and its
exact signature hands-on (`pycolmap.undistort_images.__doc__`).

**Dense stereo has no CPU path, confirmed from the docstring itself, not inferred:**
```python
>>> pycolmap.patch_match_stereo.__doc__
'...Runs Patch-Match-Stereo (requires CUDA)'
```
This directly answers the task's "I believe not" — confirmed. Don't reach for COLMAP's own dense
step on the AMD box; that's what OpenMVS is for (§2).

**colmap on conda-forge:** exists (`conda install -c conda-forge colmap`), CPU-only by default per
the feedstock/install docs (CUDA needs a from-source build) — but it's redundant here. Everything
needed for pose estimation (extraction, matching, incremental **and** global/GLOMAP mapping,
undistortion, COLMAP-format export) is already in the `pycolmap` wheel; no separate `colmap` CLI or
GLOMAP binary install is needed at all. This is the single biggest simplification versus the old
plan.

**Reliability on drone orbit video:** `global_mapping` (GLOMAP) is far faster (the GLOMAP paper's own
claim, reproduced in its README: "1-2 orders of magnitude faster... with on-par or superior
reconstruction quality" vs. incremental) but rotation-averaging-based global SfM is more sensitive to
bad pairwise matches on repetitive/textureless facades than incremental SfM's sequential
re-triangulation. COLMAP 4.2.0's changelog shows this is a known failure mode they just patched
around: "Added multi-component support to the global mapper. Disconnected view-graph components are
reconstructed independently and returned as separate models" — i.e. a fragmented result is now
detectable (`len(reconstructions) > 1`) rather than silently wrong. **Recommended order: try
`global_mapping` first (fast); if it returns >1 component or an implausibly small point count, fall
back to `incremental_mapping`** (slower, but a single ordered drone-orbit video is exactly the case
incremental SfM with a sequential matcher was designed for — implicit loop closure at the end of the
orbit, handled by re-triangulation/bundle adjustment).

### 1.2 Feed-forward learned options (ROCm feasibility from docs/repos, not hands-on — no ROCm box here)

| Tool | Code license | Weights license | ROCm feasibility | VRAM / frame ceiling | COLMAP export |
|---|---|---|---|---|---|
| **VGGT** (facebookresearch/vggt) | Meta Research license, updated Jul 2025 to allow **commercial use of the code** | Default checkpoint: **non-commercial**. New `VGGT-1B-Commercial` checkpoint: commercial, but gated/apply-for-access | Pure PyTorch (`torch`,`torchvision`,`einops`,`safetensors`,`huggingface_hub` — no CUDA-only extension in `requirements.txt`); should run on ROCm 7.2/PyTorch 2.14 as-is | Global attention over all frame tokens: community reports "~35GB attention matrix at 100 frames" causing single-GPU OOM even at 24GB; practical ceiling **60-80 frames un-chunked on a 24GB RTX 4090**. Our 16GB card is tighter — plan on **≤50-64 frames per chunk** | **Yes** — `demo_colmap.py --use_ba` writes directly to `sparse/` in COLMAP format, zero glue code |
| **MASt3R / MASt3R-SfM** (naver/mast3r) | **CC BY-NC-SA 4.0** (non-commercial, share-alike) | same | Pure PyTorch except an *optional* CUDA RoPE kernel (`croco/models/curope`, a `.cu` extension); confirmed via `naver/dust3r` issue #201 and `naver/croco` issue #32 that it **auto-falls back to a pure-PyTorch RoPE2D implementation** with a printed warning if the extension isn't compiled — works on ROCm by simply not building it | Raw MASt3R (non-SfM, all-pairs) reportedly struggles past ~30 images on a 4090. **Use the `MASt3R-SfM` entrypoint specifically** — it does retrieval-based pair selection instead of all-pairs, built to scale past that ceiling | Exports COLMAP-style camera pose data per its own docs/README |
| **Pi3 / Pi3X** (yyfz/Pi3) | Repo `LICENSE`: **BSD-3-Clause** (permissive) | HF `yyfz233/Pi3` model card license tag: **BSD-2-Clause** ("For academic use, this project is licensed under the 2-clause BSD License") | Pure PyTorch, `pip install -r requirements.txt`, no CUDA-only ops found in the README's usage path | No VRAM/frame-count figures published in the README (gap — not found in this pass, would need our own smoke test) | **No** — output is a dict (`points`, `local_points`, `conf`, `camera_poses` as 4×4 OpenCV c2w); no shipped COLMAP exporter, we'd write our own converter |
| **MapAnything** (facebookresearch/map-anything) | **Apache-2.0** (code) | `facebook/map-anything-v1` on HF: **CC-BY-NC-4.0** (non-commercial) | Meta/CMU research code, feed-forward, genuinely metric-scale output (factored depth+ray-map+pose+scale-factor representation) — newest of the four, least battle-tested for this exact use case | Not found in this pass | Not found in this pass |
| **DUSt3R** (naver/dust3r) | CC BY-NC-SA 4.0 | same | Same optional-CUDA-kernel/pure-PyTorch-fallback story as MASt3R (MASt3R is built on it) | Pairwise-only (2 views) + a separate global-alignment optimization to merge >2 views — the part that scales badly for N≈300. **Superseded by MASt3R-SfM** for this use case, not a separate option to build against | — |

**Correction to the existing plan doc:** `docs/AirTool implementation plan.md` §7 states "Pi3/Pi3X...
weights CC BY-NC." The HF model card for the actual Pi3 checkpoint (`yyfz233/Pi3`) tags it
**BSD-2-Clause**, not CC BY-NC — checked directly via `WebFetch` on the model card, not inferred.
Pi3/Pi3X is, license-wise, the least restrictive of the four feed-forward options.

**Verdict §1:** pycolmap alone (`global_mapping` → fallback `incremental_mapping`) is the primary
pose path — one `pip install`, no compiling, CPU by default, no VRAM ceiling tied to frame count
(scales with time, not memory, unlike the transformer options). VGGT is the fallback for scenes where
COLMAP SfM fails outright (feature-poor facades, motion blur) — chunk to ≤64 frames, it already
exports COLMAP format so it drops into the same OpenMVS step. MASt3R-SfM is a second-tier fallback
(more restrictive license, sometimes more robust on textureless surfaces via dense matching). Pi3/
MapAnything are "watch" options, not chosen for the hackathon: Pi3 lacks a COLMAP exporter (real
integration cost) and published VRAM numbers; MapAnything is the newest/least-verified and
NC-licensed.

---

## 2. Dense reconstruction + mesh + texture

### 2.1 OpenMVS — verified hands-on (downloaded and ran the actual release binary)

Not on conda-forge — confirmed (`GitHub search: org:conda-forge openmvs` → 0 results;
`prefix.dev/channels/conda-forge/packages/openmvs` → 404). License: **AGPL-3.0** (`COPYRIGHT.md`).
Build-from-source deps (`Building` wiki, CMake+vcpkg): Eigen ≥3.4, OpenCV ≥2.4, Ceres ≥1.10 (only
required for OpenMVS's *native* SfM module, which we don't use — the whole point is we bring poses
from pycolmap), CGAL ≥4.2, Boost ≥1.56, CUDA optional, GLFW optional — real but skippable, because:

**Prebuilt binaries ship on every tagged release, and the Ubuntu one has zero CUDA linkage.**
Downloaded `v2.4.0`'s `OpenMVS_Ubuntu_x64.zip` (165MB) in this session, unzipped, and ran it directly
on the Fedora laptop (no CMake, no vcpkg, no compiling):

```
$ ldd bin/TextureMesh
        libgomp.so.1 => ...   libstdc++.so.6 => ...   libm.so.6 => ...
        libgcc_s.so.1 => ...  libc.so.6 => ...  /lib64/ld-linux-x86-64.so.2 => ...
        (no libcuda*, no libcudart* — confirmed non-CUDA build)

$ ./TextureMesh --help
14:42:13 [App] OpenMVS x64 v2.4.0
14:42:13 [App] CPU: 13th Gen Intel(R) Core(TM) i7-1360P (16 cores)
14:42:13 [App] RAM: 15.30GB Physical Memory 8.00GB Virtual Memory
...no --cuda-device flag present at all (it's compiled out via #ifdef _USE_CUDA in a CUDA build)...
```
It ran natively, printed real hardware info, and **`--cuda-device` doesn't even exist in this
binary** — this specific prebuilt Ubuntu x64 release is unconditionally CPU. (The source also shows
the flag's semantics for CUDA-enabled builds, for completeness: `--cuda-device -2` forces CPU even
when CUDA is compiled in, `-1` = best GPU, `>=0` = device index — moot for the prebuilt Ubuntu zip
since it has no CUDA branch to select from.)

Confirmed live flags relevant to this task (from the actual `--help` output, not source-reading):

| Tool | Flag | Default | What it does |
|---|---|---|---|
| `InterfaceCOLMAP` | `-i <dir> -o scene.mvs --image-folder <dir>/images` | — | Imports **only PINHOLE** camera model — undistort first (§1.1) |
| `DensifyPointCloud` | `--resolution-level` | `1` (already half-res) | Raise to `2` (quarter-res) to buy back time if over budget |
| `DensifyPointCloud` | `--max-resolution` | `2560` | Our 1920px frames pass through untouched |
| `DensifyPointCloud` | `--number-views` | `5` | Neighbor views per depth-map; lower = faster, noisier |
| `ReconstructMesh` | `--export-type` | `ply` | `ply` or `obj` only (no glb here — texture first) |
| `TextureMesh` | `--decimate` | `1` (disabled) | `0..1` fraction, decimates the **input surface before texturing** — this is the "decimate then texture" approach the task suggested is often best, confirmed as a real, working flag |
| `TextureMesh` | `--resolution-level`, `--min-resolution 640` | `0` | Downscale source images before baking the atlas |
| `TextureMesh` | `--max-texture-size` | `8192` | Set to `4096` to match the "1-2×4096²" texture budget |
| `TextureMesh` | `--export-type` | `ply` | **`ply`, `obj`, `glb`, or `gltf`** — can export straight to GLB, no separate conversion step |

A Python API also exists (`OpenMVS_USE_PYTHON=ON` at build time → `import openmvs as omvs`), but that
requires building from source (the prebuilt zips are CLI-only) — stick to the CLI binaries given the
prebuilt path needs zero compiling.

### 2.2 mvs-texturing / texrecon

`nmoehrle/mvs-texturing` (mirrored under `OpenDroneMap/mvs-texturing`): **BSD-3-Clause** (more
permissive than OpenMVS's AGPL), CMake build that auto-downloads its own deps, genuinely CPU-only —
it only bakes an atlas from a mesh + posed images, no MVS/densify step of its own. Good drop-in swap
for just the `TextureMesh` step if OpenMVS's AGPL is ever a blocker; still needs OpenMVS (or another
MVS tool) upstream for `DensifyPointCloud`/`ReconstructMesh`. Not chosen as primary — OpenMVS's own
`TextureMesh` already does the same job plus direct GLB export and `--decimate`.

### 2.3 COLMAP CPU patch-match stereo

Confirmed does not exist — see §1.1, `pycolmap.patch_match_stereo.__doc__` literally says "requires
CUDA," with no CPU fallback exposed anywhere in the library. Ruled out.

### 2.4 Open3D TSDF from learned depth

Viable specifically as a companion to the §1.2 feed-forward fallback path: VGGT/MASt3R already emit
per-frame depth maps as a side output, so when using that path, skip `DensifyPointCloud` entirely and
fuse those depth maps directly with Open3D's TSDF integration → `extract_triangle_mesh`, instead of
re-deriving depth via patch-match. `open3d==0.20.0` PyPI wheel checked (not installed — 400MB
`manylinux_2_35_x86_64` wheel, didn't download given time budget): CPU-capable without a GPU present
(well-established Open3d behavior, CUDA is opt-in per-call not required to import). **Pitfall**:
`manylinux_2_35` requires glibc ≥2.35 (Ubuntu 22.04+) — one floor newer than pycolmap's
`manylinux_2_28`; worth checking the hfbox base image before relying on this sub-path. TSDF-fused
meshes have **no UVs by construction** (marching-cubes output) — texturing is a separate projection
pass afterward regardless (OpenMVS's `TextureMesh` accepts an externally-produced mesh + poses too).

### 2.5 Pure-Python texturing (xatlas UV unwrap + custom projection bake)

`xatlas==0.0.11` (`mworchel/xatlas-python`, bindings around `jpcy/xatlas`) — installed cleanly in the
scratch venv, no compile, no torch:
```python
>>> import xatlas
>>> dir(xatlas)  # ['Atlas', 'Chart', 'ChartOptions', 'ChartType', 'PackOptions', 'export', 'parametrize']
```
The UV-unwrap half is a real one-call library (`xatlas.parametrize`/`Atlas`). The *baking* half
(best-view-per-face selection, visibility testing, rasterizing into the atlas, seam blending) is
**not** provided by xatlas — that's genuinely from-scratch numpy/torch work. Feasible for a hackathon
(a few hundred lines) but is exactly what OpenMVS's `TextureMesh` already does in one CLI call, with
seam leveling and sharpness weighting we'd otherwise have to reimplement. **Verdict: don't build
this** — reinventing `TextureMesh`. Keep xatlas in the back pocket only if OpenMVS ever becomes
unusable (e.g. a future licensing constraint), not as work to do now.

### 2.6 Meshroom / AliceVision

Confirmed **CUDA-required, no CPU fallback** for the `DepthMap` node — multiple GitHub issues, and
AliceVision's own wiki page is literally titled *"Error: This program needs a CUDA Enabled GPU."* An
unofficial ZLUDA-patched Windows build exists but is Windows-only. **Ruled out** for the Linux/AMD
box.

### 2.7 OpenDroneMap / ODM

CPU-only by design (OpenSfM for poses, not COLMAP; OpenMVS or mvs-texturing internally for the rest,
depending on version), drone-imagery-focused (native GPS/EXIF handling). Takes **images, not video**
— same as everything else here, moot since we already extract frames via ffmpeg. Docker is the
documented/supported install; native install is a real from-source SuperBuild
(`bash configure.sh install` → CMake, compiling OpenCV/PDAL/PCL/Ceres/OpenGV/etc. from source),
historically reported as tested only up to Ubuntu ~20.04/21.04 and a long build. Output is
`odm_texturing/odm_textured_model_geo.obj` (OBJ+MTL) — needs a trimesh OBJ→GLB pass afterward, same
as every non-OpenMVS path. **Verdict: FALLBACK/smoke-test only, and only if pre-built once ahead of
the event into `/workspace` (persists across resets)** — never plan to build it live during a
60-minute site reconstruction; it duplicates OpenMVS under the hood anyway.

**Verdict §2:** `InterfaceCOLMAP` (import) → `DensifyPointCloud` → `ReconstructMesh` →
`TextureMesh --decimate ... --resolution-level ... --max-texture-size 4096 --export-type glb`,
using the prebuilt `OpenMVS_Ubuntu_x64.zip` CLI binaries, unzipped straight into `/workspace`. No
CMake, no CUDA, no conda-forge. FALLBACK: Open3D TSDF fusion of VGGT/MASt3R depth maps when pose
estimation itself needed the feed-forward fallback.

---

## 3. Decimation preserving UVs/texture

| Tool | Verified | UV/texture preserved? |
|---|---|---|
| **pymeshlab** `meshing_decimation_quadric_edge_collapse_with_texture` | Hands-on: `pymeshlab==2025.7.post1` installs clean (manylinux wheel, no compile, no torch); filter confirmed present, params dumped via `print_filter_parameter_list`: `targetfacenum`, `targetperc`, `qualitythr=0.3`, `extratcoordw=1` (the UV-space error weight), `preserveboundary`, `boundaryweight`, `optimalplacement=True`, `preservenormal`, `planarquadric` | **Yes** — Garland-Heckbert extended 5D (XYZ+UV) quadric metric, seam-aware |
| **fast-simplification** (already a project dep, used today in `assets-3d.md` §3.6) | Confirmed via its own README: core API is `simplify(points, faces, ratio)` / `simplify_mesh(pyvista_mesh, target_reduction)` — geometry-only, no UV parameter anywhere in the interface | **No** — fine for the untextured/vertex-color `ai_mesh` proxy path it's used for today, wrong tool once a mesh has a baked atlas |
| **Open3D** `simplify_quadric_decimation` | Confirmed via a live upstream issue (isl-org/Open3D#5184): drops UVs/materials with an explicit runtime warning ("mesh contains triangle uvs that are not handled in this function") | **No** |
| **OpenMVS `TextureMesh --decimate`** | Confirmed live via `--help` (§2.1) | **N/A — sidesteps the problem entirely**: decimates the surface *before* the atlas is baked, so there's no UV-preservation problem to solve |

**Recommended order:** `ReconstructMesh` → `TextureMesh --decimate 0.05 --resolution-level 1
--max-texture-size 4096 --export-type glb` directly (skip `RefineMesh` for the hackathon timeline,
skip a separate decimation tool) if the raw mesh is in the few-million-triangle range and one
`--decimate` pass lands close to ~200k tris. `--decimate` is a *fraction*, not an exact count, so if
the result over/undershoots, do one more explicit pass with pymeshlab's
`meshing_decimation_quadric_edge_collapse_with_texture(targetfacenum=200000, extratcoordw=1)` on the
already-textured GLB/OBJ — this is the exact-triangle-count control OpenMVS's fractional flag
doesn't give you, and it's texture-aware so it's safe to run post-texture, unlike fast-simplification
or Open3D.

---

## 4. GLB export with embedded JPEG texture

**trimesh — verified hands-on this session.** Built a small box mesh, attached a
`PBRMaterial(baseColorTexture=<PIL JPEG image>)`, exported to GLB, reloaded with `pygltflib`:
```python
>>> g.images
[Image(mimeType='image/jpeg', bufferView=2, ...)]   # embedded in the binary chunk, NOT re-encoded to PNG, NOT left as an external URI
```
`trimesh==5.1.0` (already `pyproject.toml`'s pin) does the right thing by default — preserves JPEG,
embeds it. Reuse the exact `Scene.to_geometry()` → orient → `mesh.export(path)` pattern already
written and tested in `docs/research/assets-3d.md` §3 (site meshes don't need the fit-to-published-
dims step that recipe has, since scale is applied later by the plan's own calibration code — just the
load/flatten/export parts apply).

**pygltflib** — already used in this project (`assets-3d.md`) purely as a read-back/verification
tool after export, same role here. `pygltflib==1.16.5` installs clean, no compile.

**KTX2/Basis, meshopt, Draco — real, but not worth it for this deliverable.** glTFast supports
`KHR_texture_basisu` (KTX2), `EXT_meshopt_compression`, and `KHR_draco_mesh_compression`, but each
needs its own separate Unity package on the P2 side ("KTX for Unity," a meshopt decompression
package, "Draco for Unity" respectively — confirmed via Unity's glTFast docs) and, for KTX2/Draco/
meshopt encoding, tooling that isn't part of this Python pipeline at all (`toktx`/`basisu` from
Khronos KTX-Software, or the Node-based `gltf-transform` CLI). At the target budget — ≤200k tris,
1-2×4096² JPEG atlases — total GLB size is realistically a few MB, not a real memory-pressure problem
on a Quest 3S. **Recommend skipping compression for the hackathon**; note as a leftover if on-device
texture VRAM ever becomes the actual bottleneck (it wasn't measured here — no headset in this
session).

---

## 5. Recommended pipeline

### PRIMARY (pose: pycolmap; dense/mesh/texture: OpenMVS prebuilt binary; decimate: OpenMVS + pymeshlab touch-up; export: trimesh)

```bash
ffmpeg -i clip.mp4 -vf fps=3,scale=1920:-1 frames/%04d.jpg      # ~300-500 frames

python - <<'PY'
import pycolmap, pathlib
db, img, out = "db.db", "frames", "sparse"
pathlib.Path(out).mkdir(exist_ok=True)
pycolmap.extract_features(db, img)                              # CPU SIFT, use_gpu=False default
pycolmap.match_sequential(db)                                    # CPU, default
recons = pycolmap.global_mapping(db, img, out)                   # GLOMAP, ex-standalone tool
if len(recons) != 1:                                              # fragmented -> fall back
    recons = pycolmap.incremental_mapping(db, img, out)
recons[0].write(out + "/0")
pycolmap.undistort_images(output_path="undistorted", input_path=out+"/0",
                           image_path=img, output_type="COLMAP")  # InterfaceCOLMAP needs PINHOLE
PY

InterfaceCOLMAP -i undistorted -o scene.mvs --image-folder undistorted/images
DensifyPointCloud scene.mvs --resolution-level 1 --number-views 5
ReconstructMesh scene_dense.mvs
TextureMesh scene_dense_mesh.mvs --decimate 0.05 --resolution-level 1 \
    --max-texture-size 4096 --export-type glb -o textured.glb
# optional exact-count touch-up + final coordinate cleanup with pymeshlab / trimesh
```

### FALLBACK (pose estimation failed / feature-poor scene): VGGT depth+poses → Open3D TSDF → OpenMVS TextureMesh only

```bash
# chunk to <=64 frames per VGGT call (VRAM ceiling, see §1.2), align chunks with the plan's
# existing Umeyama/ArUco code, then:
python demo_colmap.py --scene_dir=. --use_ba          # writes sparse/ in COLMAP format directly
# feed VGGT's own depth maps to Open3D TSDF integration -> extract_triangle_mesh (skips DensifyPointCloud)
# then OpenMVS TextureMesh on that mesh + the same undistorted images/poses, same flags as above
```
Second-tier fallback within the fallback: swap VGGT for MASt3R-SfM (naver/mast3r) if VGGT's global
attention still OOMs after chunking, or the scene is textureless enough that dense matching helps —
more restrictive license (CC BY-NC-SA 4.0), same COLMAP-format output.

### Runtime estimate, ~300 frames @ 1920px — **all figures below are estimates from documented tool
### behavior, not benchmarked in this session** (no 300-frame drone dataset or the target hardware
### was available here)

| Step | Tool | Est. time |
|---|---|---|
| Frame extraction | ffmpeg | 1-2 min |
| SIFT extraction (CPU) | `pycolmap.extract_features` | 3-8 min |
| Sequential matching | `pycolmap.match_sequential` | 2-5 min |
| Global SfM | `pycolmap.global_mapping` | 1-4 min (GLOMAP claims 1-2 orders of magnitude faster than incremental) |
| *(fallback)* Incremental SfM | `pycolmap.incremental_mapping` | 8-20 min |
| Undistort | `pycolmap.undistort_images` | 1-2 min |
| **Dense stereo (biggest risk)** | `OpenMVS DensifyPointCloud` | **15-35 min** — CPU PatchMatch with no GPU is the known bottleneck; tune `--resolution-level`/`--number-views` if over budget |
| Mesh reconstruct | `OpenMVS ReconstructMesh` | 1-3 min |
| *(skip for hackathon)* Refine | `OpenMVS RefineMesh` | 10-20 min — skip per the plan's own "skip unless visibly wrong" precedent (§2.2 of the plan, applied to 2DGS/SuGaR, same logic here) |
| Texture + decimate | `OpenMVS TextureMesh --decimate` | 3-8 min |
| Final polish/export | pymeshlab / trimesh | <1 min |
| **Total (skip RefineMesh)** | | **~30-60 min** — tight against the 60-min budget; `DensifyPointCloud` tuning is the main lever, matches the plan's own instruction to "run the whole chain once on practice footage and write down the minutes per step" |

---

## 6. Install commands

### 6.1 hfbox container (root, `/workspace`-only — not verified hands-on, no access to this container this session; standard venv/pip semantics, verify on first real run)

```bash
# --- PRIMARY: CPU-only pose+mesh+decimate+export env, no torch needed ---
python3.12 -m venv /workspace/venv-recon
/workspace/venv-recon/bin/pip install -U pip
/workspace/venv-recon/bin/pip install pycolmap==4.2.0 "trimesh>=5.1.0" pygltflib pymeshlab \
    xatlas fast-simplification numpy pillow

# --- OpenMVS: prebuilt CPU binary, no compile, no CUDA/vcpkg ---
mkdir -p /workspace/openmvs && cd /workspace/openmvs
curl -L -o OpenMVS_Ubuntu_x64.zip \
  https://github.com/cdcseacave/openMVS/releases/download/v2.4.0/OpenMVS_Ubuntu_x64.zip
unzip -o OpenMVS_Ubuntu_x64.zip -d bin && chmod +x bin/*
export PATH="/workspace/openmvs/bin:$PATH"     # persist this in whatever profile/script survives resets

# --- FALLBACK: VGGT, inherits the preinstalled ROCm torch instead of reinstalling it ---
python3.12 -m venv /workspace/venv-vggt --system-site-packages   # --system-site-packages -> sees /opt/venv's torch 2.14/ROCm 7.2
git clone https://github.com/facebookresearch/vggt /workspace/vggt
cd /workspace/vggt
# requirements.txt pins torch==2.3.1/torchvision==0.18.1 -- installing it plain would try to
# downgrade the preinstalled ROCm stack. Install everything except torch/torchvision:
/workspace/venv-vggt/bin/pip install --no-deps -r requirements.txt
/workspace/venv-vggt/bin/pip install einops safetensors huggingface_hub pillow numpy
```

### 6.2 Fedora laptop (throwaway venvs only — this is what was actually run in this session)

```bash
uv venv --python 3.12 /tmp/scratch/.venv312
uv pip install --python /tmp/scratch/.venv312/bin/python \
    pycolmap trimesh pygltflib pymeshlab xatlas fast-simplification
# all five installed clean in this session: pycolmap 4.2.0, pymeshlab 2025.7.post1, xatlas 0.0.11,
# trimesh 5.1.0, pygltflib 1.16.5 -- manylinux wheels, zero compilation, zero torch pulled in

mkdir -p /tmp/scratch/openmvs && cd /tmp/scratch/openmvs
curl -L -o OpenMVS_Ubuntu_x64.zip \
  https://github.com/cdcseacave/openMVS/releases/download/v2.4.0/OpenMVS_Ubuntu_x64.zip
unzip -o OpenMVS_Ubuntu_x64.zip -d bin && chmod +x bin/*
./bin/TextureMesh --help    # confirmed: runs natively on Fedora 44, no CUDA libs linked
```
No VGGT/MASt3R on the laptop — no GPU here, and installing CPU-only torch just to smoke-test the
feed-forward fallback wasn't in scope for this pass (nothing in the task required it; flagging as a
leftover if someone wants a laptop-side CPU smoke test of VGGT before the event).

---

## 7. Known pitfalls (all confirmed present, not assumed)

1. **`InterfaceCOLMAP` only imports PINHOLE cameras** — run `pycolmap.undistort_images(...,
   output_type="COLMAP")` between mapping and import, or the OpenMVS import silently mishandles a
   real drone lens's distortion. Confirmed from the tool's own `--help` text.
2. **COLMAP's dense patch-match stereo requires CUDA, period** — confirmed from the pip wheel's own
   docstring. Use OpenMVS `DensifyPointCloud` for dense reconstruction on the AMD box, never
   `pycolmap.patch_match_stereo`.
3. **`global_mapping` (GLOMAP) can fragment a scene** into multiple disconnected `Reconstruction`s on
   hard footage — check `len(reconstructions) > 1` and fall back to `incremental_mapping`. COLMAP
   4.2.0 added explicit multi-component support *because* this happens.
4. **VGGT's (and any transformer feed-forward model's) memory grows with frame count via global
   attention** — un-chunked runs top out around 60-80 frames even on a 24GB NVIDIA card; our 16GB
   RDNA4 card is tighter. Chunk to ≤50-64 frames for a 300-400 frame orbit, never call it un-chunked.
5. **VGGT's `requirements.txt` pins `torch==2.3.1`/`torchvision==0.18.1`** — installing it naively
   into the hfbox venv would try to downgrade the preinstalled ROCm 2.14 stack. Install with
   `--no-deps` and hand-pick the non-torch deps (see §6.1).
6. **Open3D's manylinux wheel needs glibc ≥2.35** (`manylinux_2_35`), one floor newer than
   pycolmap's `manylinux_2_28` — only matters for the §2.4 TSDF sub-path, check the hfbox base image
   before relying on it.
7. **`fast-simplification` (already a project dep) and Open3D's `simplify_quadric_decimation` both
   silently drop UVs/textures** — confirmed for Open3D via a live upstream issue, for
   fast-simplification via its own points/faces-only API. Never run either on an already-textured
   mesh; use pymeshlab's `..._with_texture` filter or OpenMVS's `--decimate` (which decimates
   *before* texturing) instead.
8. **OpenMVS is AGPL-3.0** (confirmed via `COPYRIGHT.md`) — fine as an internal offline CLI build
   step; `mvs-texturing`/`texrecon` (BSD-3-Clause) is the swap-in for just the texturing step if AGPL
   ever becomes a concern for how this ships.
9. **ODM's native (non-Docker) install is a real from-source SuperBuild** (OpenCV, PDAL, PCL, Ceres,
   OpenGV, etc.) — only build it once, ahead of the event, into `/workspace` (persists); never plan
   to build it live during a 60-minute site reconstruction.
10. **`DensifyPointCloud` (CPU PatchMatch) is the most likely step to blow the 60-minute budget** —
    the two real levers are `--resolution-level` (bump from default `1` to `2`) and `--number-views`
    (lower from default `5`). Budget a practice run to tune these, per the plan's own existing
    instruction to time each step once beforehand.

---

## 8. License summary

| Tool | License | Restriction that matters here |
|---|---|---|
| pycolmap / COLMAP | BSD-3-Clause | none |
| GLOMAP (now part of COLMAP) | BSD-3-Clause (inherited) | none |
| OpenMVS | AGPL-3.0 | copyleft on the tool itself, not on its output; fine for internal offline use |
| mvs-texturing / texrecon | BSD-3-Clause | none |
| pymeshlab | GPL-3.0 (MeshLab core license) | copyleft on the tool; used offline, output unaffected |
| xatlas / xatlas-python | MIT | none |
| trimesh, pygltflib, fast-simplification | MIT/BSD-family | none |
| VGGT code | Meta Research (commercial-use-friendly since Jul 2025) | code is fine; default checkpoint is non-commercial |
| VGGT default checkpoint | Non-commercial | fine for a hackathon demo |
| MASt3R / MASt3R-SfM / DUSt3R | CC BY-NC-SA 4.0 | non-commercial, share-alike |
| Pi3 / Pi3X code | BSD-3-Clause | none |
| Pi3 / Pi3X weights | BSD-2-Clause | none — most permissive of the feed-forward options |
| MapAnything code | Apache-2.0 | none |
| MapAnything v1 weights | CC-BY-NC-4.0 | non-commercial |

---

## 9. What was verified hands-on vs. from docs

**Hands-on this session** (Fedora 44 laptop, throwaway `uv venv`/scratch dirs under
`/tmp/claude-1000/...`, nothing installed system-wide):
- `pycolmap==4.2.0` installs clean, no compile, no torch; `pycolmap.COLMAP_version`,
  `global_mapping`/`global_mapper` existence, `incremental_mapping`, `FeatureExtractionOptions.
  use_gpu`/`FeatureMatchingOptions.use_gpu` defaults, `Device` enum members, `patch_match_stereo`'s
  "(requires CUDA)" docstring, `Reconstruction.write`, `undistort_images` signature — all read
  directly from the installed package's live docstrings/attributes, not from documentation.
- `xatlas==0.0.11` and `pymeshlab==2025.7.post1` install clean (manylinux wheels, no compile);
  pymeshlab's `meshing_decimation_quadric_edge_collapse_with_texture` filter and its exact parameter
  names/defaults dumped via `pymeshlab.print_filter_parameter_list(...)`.
- `trimesh==5.1.0` GLB export with an embedded JPEG texture: built a test mesh, exported, reloaded
  with `pygltflib`, confirmed `mimeType == 'image/jpeg'` in the binary chunk (not re-encoded, not
  external).
- Downloaded the actual `OpenMVS v2.4.0` `OpenMVS_Ubuntu_x64.zip` release binary and ran
  `TextureMesh --help` / `DensifyPointCloud --help` / `ReconstructMesh --help` / `InterfaceCOLMAP
  --help` natively on the Fedora laptop: confirmed no CUDA libraries linked (`ldd`), confirmed the
  `--decimate`/`--resolution-level`/`--max-texture-size`/`--export-type glb`/`--number-views` flags
  exist with the defaults quoted above, and confirmed the PINHOLE-only import constraint from the
  tool's own help text.
- Confirmed OpenMVS has no conda-forge package (GitHub API search + prefix.dev 404).

**From docs/repos, not hands-on** (no ROCm/AMD GPU box reachable from this session, and the hfbox
container itself wasn't accessible here):
- All VGGT/MASt3R/Pi3/MapAnything/DUSt3R ROCm-runs-fine, VRAM-ceiling, and license claims — read from
  each project's `README.md`/`LICENSE`/`requirements.txt` on GitHub and HF model cards directly (not
  secondhand summaries), but not executed.
- The COLMAP changelog's GLOMAP-merge and multi-component claims — read from
  `colmap.github.io/changelog.html` directly.
- All runtime estimates in §5's table — explicitly labeled as estimates from documented tool
  behavior; no 300-frame drone dataset or the target 8-16 core box was available to benchmark in this
  session.
- Meshroom/AliceVision's CUDA-only `DepthMap` — from multiple GitHub issues and the project's own
  wiki page, not executed (no reason to, the "no CPU fallback" claim is corroborated by the
  maintainers' own wiki page title).
- ODM's native SuperBuild length/fragility — from GitHub/community-forum reports, not attempted (out
  of scope for this pass — would take the better part of an hour to actually build).

---

## 10. Sources

- pycolmap PyPI: https://pypi.org/project/pycolmap/ (checked via `pypi.org/pypi/pycolmap/json`)
- COLMAP changelog (GLOMAP merge, 4.0.0/4.2.0): https://colmap.github.io/changelog.html
- COLMAP / GLOMAP repos: https://github.com/colmap/colmap · https://github.com/colmap/glomap (archived/deprecated)
- colmap conda-forge feedstock: https://github.com/conda-forge/colmap-feedstock
- OpenMVS: https://github.com/cdcseacave/openMVS (README, `COPYRIGHT.md`, wiki `Building`/`Usage` pages, `v2.4.0` release)
- mvs-texturing / texrecon: https://github.com/nmoehrle/mvs-texturing
- Open3D: https://github.com/isl-org/Open3D · UV-drop issue: https://github.com/isl-org/Open3D/issues/5184 · PyPI: https://pypi.org/project/open3d/
- xatlas-python: https://github.com/mworchel/xatlas-python
- pymeshlab: https://github.com/cnr-isti-vclab/PyMeshLab
- fast-simplification: https://github.com/pyvista/fast-simplification
- trimesh: https://github.com/mikedh/trimesh · pygltflib: https://github.com/KhronosGroup (see also `docs/research/assets-3d.md` §3 for the prior tested recipe)
- VGGT: https://github.com/facebookresearch/vggt (README, `requirements.txt`, `LICENSE.txt`)
- MASt3R: https://github.com/naver/mast3r (README, `requirements.txt`) · DUSt3R: https://github.com/naver/dust3r · curope fallback: https://github.com/naver/dust3r/issues/201 · https://github.com/naver/croco/issues/32
- Pi3/Pi3X: https://github.com/yyfz/Pi3 (README, `LICENSE`) · weights: https://huggingface.co/yyfz233/Pi3
- MapAnything: https://github.com/facebookresearch/map-anything (`LICENSE`) · weights: https://huggingface.co/facebook/map-anything-v1
- Meshroom/AliceVision CUDA requirement: https://github.com/alicevision/Meshroom/wiki/Error:-This-program-needs-a-CUDA-Enabled-GPU
- OpenDroneMap/ODM: https://github.com/OpenDroneMap/ODM/wiki/Installation
- Unity glTFast compression support (KTX2/Draco/meshopt): https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.1/manual/installation.html

## 11. DJI.Meta stream (`vid/DJI_0095.MP4` stream 1, "DJI.Meta") -- hands-on, 20-minute time-box

Extracted with `ffmpeg -i vid/DJI_0095.MP4 -map 0:1 -c copy -f data out.bin` (stream 1's codec tag
is `priv`/`0x76697270`, container reports it as generic `data`, not a named codec). Result:
**not useful, no decoder built** -- confirmed, not assumed:

- 21,750 bytes total, splitting evenly into 87 fixed-size 250-byte records (`21750 / 250 == 87`,
  no remainder) -- one record roughly per second of the 88s clip, i.e. the **same ~1Hz rate as the
  SRT track**, not higher-rate.
- All 87 records are **byte-for-byte identical** (`len(set(records)) == 1`). Each record's only
  non-zero bytes are a short ASCII tag near the start -- `4e 33 20 30 30 20 30 30` = `"N3 00 00"`
  (offsets 2-9) -- followed by zero-fill to the full 250 bytes. No structure resembling
  protobuf/TLV (no varint tags, no per-record field markers, nothing that varies frame-to-frame).
- Given every record is identical across an 88s flight that visibly climbs/descends per the SRT's
  own `H` (1.0m to 1.9m), this stream carries no per-frame attitude/height/gimbal data in this
  clip -- it reads like a constant device/session marker (matches `SN=9J2FN8H0BL0GC1` in the
  container's own `comment` metadata tag, though the exact meaning of "N3 00 00" wasn't decoded).

Conclusion: no decoder built (assignment: "don't build one unless trivial") -- there is nothing
here to decode. This is specific to this one clip; a different DJI model/firmware or a clip with
GPS could plausibly write real data into this stream, but that would need a second real corpus
clip to check, out of this pass's 20-minute box.
