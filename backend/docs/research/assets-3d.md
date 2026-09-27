# 3D asset layer research: HF Spaces image-to-3D, free libraries, trimesh recipe

Scope: `server/assets.py` asset resolver, tiers `cad → library → ai_mesh → proxy`
(plan §4b.3), producing `model.glb` per the parts-server contract (plan §1b:
meters, +Y up, origin at mounting-face centre, scaled to published dims).
Constraint from the user: no Tripo/Sketchfab accounts; anonymous HF Spaces via
`gradio_client` for `ai_mesh`, exact-size `trimesh` proxy as the guaranteed
fallback. No GPU on this machine; nothing here needs one — `gradio_client` has
no torch dependency and everything below ran in throwaway `uv run --with ...`
environments, not the project venv.

**Conflict to flag for P4:** plan §4b.3 and §7b currently name **Tripo3D** as
the `ai_mesh` source. That contradicts the user's "no Tripo account" direction
given for this research. This doc treats HF Spaces as the actual `ai_mesh`
source and Tripo as dropped; the implementation plan itself hasn't been edited
(out of scope for this task — only this file and `tests/fixtures/assets/` were
touched).

## 1. HF Spaces image-to-3D — tested

Test image: `Single-room AC unit-external.jpg`, public domain (Wikimedia
Commons, Mark Wagner / User:Carnildo, `LicenseShortName: Public domain`),
downloaded from
`https://upload.wikimedia.org/wikipedia/commons/2/2f/Single-room_AC_unit-external.jpg`.
Saved as `tests/fixtures/assets/source_photo_window_ac.jpg`. It's a real,
messy product photo (unit mounted in a textured wall, rust stains, off-angle),
not a clean studio shot — a fair test of background removal.

Runtime status checked via `GET https://huggingface.co/api/spaces/<id>/runtime`
before spending attempts on anything not `RUNNING`:

| Space | Runtime stage | Notes |
|---|---|---|
| `stabilityai/TripoSR` | `RUNTIME_ERROR` | dead, skipped |
| `tencent/Hunyuan3D-2mini` | 404 (no such space) | doesn't exist under that name |
| `JeffreyXiang/TRELLIS` | 404 (no such space) | moved to `trellis-community/TRELLIS` |
| `microsoft/TRELLIS` | `CONFIG_ERROR` | dead, skipped |
| `stabilityai/stable-fast-3d` | `RUNNING` | tested |
| `VAST-AI/TripoSG` | `RUNNING` | tested |
| `trellis-community/TRELLIS` | `RUNNING` | inspected via `view_api()` only, not run (budget spent on the 3 below) |
| `tencent/Hunyuan3D-2` | `RUNNING` | not run — same API shape as 2.1, noted as an immediate fallback |
| `tencent/Hunyuan3D-2.1` | `RUNNING` | tested, **works** |

All of the `RUNNING` spaces above connected and returned a full `view_api()`
schema with a bare `Client(space_id)` — **no login, no token, no
`hf_token=` needed** to see the API surface. The question was always whether
the actual generate call worked anonymously, not whether the API was visible.

### 1.1 `stabilityai/stable-fast-3d` — anon generation broken (confirmed bug, not just me)

`view_api()` shows 4 endpoints: `/update_foreground_ratio`,
`/lambda`, `/requires_bg_remove`, `/run_button`. `/run_button` is the one that
actually runs TripoSR-style fast reconstruction and returns
`(preview_background_removal, 3d_model)`.

Calling it anonymously raises:
```
gradio_client.exceptions.AppError: The upstream Gradio app has raised an
exception but has not enabled verbose error reporting.
```
on **every** input tried (the real AC-unit photo, and gradio's own tiny
`bus.png` test file). This is a known, already-reported bug, not something
specific to my image or my quota: [Space discussion #11, "Unable to call
`/run_button` API endpoint via Gradio Python client"](https://huggingface.co/spaces/stabilityai/stable-fast-3d/discussions/11)
has the identical traceback, filed months ago, still open, no fix. I also
tried `/requires_bg_remove` (2nd attempt) as a workaround — it doesn't do
generation at all, it just returns Gradio visibility-toggle updates for the
UI (`{'visible': False, ...}`), confirming it's a UI-wiring handler, not a
generation endpoint. **Verdict: this Space is not usable via `gradio_client`
right now**, anon or otherwise, regardless of local fixes.

### 1.2 `VAST-AI/TripoSG` — anon generation broken (confirmed bug, not just me)

`view_api()` shows `/run_segmentation` (background removal, standalone),
`/get_random_seed`, `/image_to_3d` (main generation), `/run_texture`.
Both `/run_segmentation` and `/image_to_3d` (with and without a
pre-segmented image, 2 attempts total) raised:
```
gradio_client.exceptions.AppError: RuntimeError
```
with no further detail. The Space's own discussion board corroborates this is
broken for everyone right now, not an anon-only or image-only problem:
[discussion #7 "Runtime error"](https://huggingface.co/spaces/VAST-AI/TripoSG/discussions/7) —
five separate users, one from the *web UI itself* ("I've tried all kinds of
images but I just keep getting a runtime error"), and a fresh
[discussion #3](https://huggingface.co/spaces/VAST-AI/TripoSG/discussions/3)
reporting the same failure through the hosted TripoSG SaaS API ("2002: This
task type is not supported"). **Verdict: dead right now**, don't build on it
without re-checking closer to demo day.

### 1.3 `tencent/Hunyuan3D-2.1` — works anonymously, this is the pick

`view_api()` shows the real generation entry points: `/shape_generation`
(mesh only) and `/generation_all` (mesh + texture), plus export/UI-state
lambdas. Ran `/shape_generation` twice, both succeeded:

| Attempt | Wall time (client) | Server-reported compute | Output |
|---|---|---|---|
| 1 | 17.8 s | 14.5 s (bg-remove 1.0s + shape-gen 13.4s + export 0.16s) | 10,534,304-byte GLB, 585,166 tris |
| 2 | 21.7 s | 15.2 s (bg-remove 1.1s + shape-gen 13.9s + export 0.16s) | identical output (same seed) |

`mesh_stats` returned by the endpoint confirms **background removal runs
inside the Space** (`"remove background": 1.0-1.1s`, gated by
`check_box_rembg=True`, the default) — nothing extra needed client-side (see
§4).

Tried `/generation_all` (shape **+ texture**) once, as the 2nd attempt on this
Space. It failed immediately, before any GPU time was spent, with an explicit,
informative error — not a generic AppError:
```
AppError: The requested GPU duration (270s) is larger than the maximum
allowed. Subscribe to Hugging Face PRO to allow GPU tasks up to 40 min -
https://huggingface.co/subscribe/pro?from=ZeroGPU
```
So: **shape-only generation is anonymously usable, textured generation is
not** (the Space's `@spaces.GPU` decorator on that function requests a fixed
270s slot that anon/free quota can't grant at all, independent of my step
count). This lines up exactly with the plan's own `ai_mesh` badge text —
*"AI mesh — exact size, approximate look"* — the anonymous path really can
only get you the untextured/vertex-colour mesh, not a textured one.

Output mesh has no material/texture (`ColorVisuals`, flat white per the
filename `white_mesh.glb`), 585K tris — too dense to ship as-is (10.5 MB, and
way more geometry than a small hardware part needs). §3 below decimates it
through the same `trimesh` pass that does the scale/origin normalization,
down to 142 KB, which is what's saved as the test fixture.

**Working snippet** (the actual thing that produced the numbers above,
including the one non-obvious gotcha: `/shape_generation`'s file output comes
back as a raw Gradio component-update dict pointing at a *remote* path, not
an auto-downloaded local path, so `gradio_client`'s usual auto-download never
fires):

```python
import requests
from gradio_client import Client, handle_file

def resolve_file(client: Client, value):
    if isinstance(value, dict) and "value" in value:
        remote_path = value["value"]
        r = requests.get(f"{client.src}/file={remote_path}", timeout=120)
        r.raise_for_status()
        return r.content
    return open(value, "rb").read()

def generate_glb(image_path: str) -> bytes:
    client = Client("tencent/Hunyuan3D-2.1")  # anonymous, no hf_token
    file_out, _html, mesh_stats, _seed = client.predict(
        image=handle_file(image_path),
        mv_image_front=None, mv_image_back=None,
        mv_image_left=None, mv_image_right=None,
        steps=20, guidance_scale=5.0, seed=1234,
        octree_resolution=256, check_box_rembg=True,
        num_chunks=8000, randomize_seed=False,
        api_name="/shape_generation",
    )
    print("mesh_stats:", mesh_stats)  # confirms bg-removal + timing
    return resolve_file(client, file_out)
```

Ran with `uv run --with gradio_client --with requests python3 <script>.py` —
no project dependency added.

### 1.4 Recommendation

- **Primary:** `tencent/Hunyuan3D-2.1`, `/shape_generation`, anonymous,
  ~15-20 s. Accept untextured mesh (matches the `ai_mesh` badge's own
  honesty claim). Fall back to `tencent/Hunyuan3D-2` (identical API shape,
  same code, just swap the space id) if 2.1 is down — not run, but the two
  Spaces mirror each other closely enough that this is a same-day swap, not
  a rewrite.
- **Secondary:** `trellis-community/TRELLIS`. Not run end-to-end (3-space,
  2-attempt budget was spent on the spaces above), but its API surface
  (`start_session` → `preprocess_image` → `generate_and_extract_glb` →
  `extract_gaussian`) is a real, richer pipeline (outputs a Gaussian +
  extracted GLB, more knobs) if Hunyuan3D goes down closer to the event.
  **Worth a 10-minute smoke test the night before**, since it's untested.
- **Do not build on** `stable-fast-3d` or `TripoSG` right now — both have
  confirmed, reported, unresolved anonymous-generation bugs (§1.1, §1.2).
- Exact-size **`proxy` tier is not a fallback of last resort here, it's the
  load-bearing tier**: with 2 of 3 candidate AI spaces dead, the honest plan
  for the two demo parts is proxy-first, `ai_mesh` as an opportunistic
  upgrade if Hunyuan3D is still up on the day.

## 2. Free 3D model libraries — verdicts for automated use

| Source | Auth | Automatable? | Verdict |
|---|---|---|---|
| **Poly Pizza** | Free account required for an API key (`api.poly.pizza`) | Confirmed: `curl` without a key → `401 {"error":"You need an API key to do that dingus"}` | Not anonymous. Free, self-serve signup is realistic for a hackathon (do it once Thursday), but it's a `library` tier tool, not a no-auth one. Fine as manual/hand-curated source (plan already does this: "3-5 BIMobject/Sketchfab models downloaded by hand") |
| **Objaverse** (`pip install objaverse`, Allen AI) | **None** | Confirmed: `objaverse.load_uids()` → 798,759 uids, `load_annotations()` → license/name/format metadata, no token anywhere | Real, no-auth, works. But **no hosted text-search API** — you get UIDs + a metadata dump, not a `search("gutter hanger")` call. Automating "find a hanger" means downloading/filtering the whole annotation set client-side, which is too slow for a live `/parts/search` request. Useful only as a **pre-curated, hand-picked background library**, not the live resolver path. Source objects are originally Sketchfab uploads with mixed CC licenses (sample checked: `license: "by"` = CC-BY) — check per-object license before use |
| **Smithsonian Open Access** | Free instant key via `api.data.gov/signup` (self-serve, no approval wait) | Not tested (clearly documented, low priority) | Technically solid — 2,000+ CC0 GLB/glTF/OBJ models — but it's a museum/cultural-heritage collection (specimens, artifacts, sculpture). **Zero overlap with hardware parts** (gutter hangers, window ACs). Not useful for this project regardless of auth ease |
| **Thingiverse API** | Requires a free developer app + OAuth token | Not tested (clearly documented) | Auth friction similar to Poly Pizza; content skews 3D-printable novelty objects, not manufacturer-accurate hardware. Low value even as a manual source |
| **GrabCAD** | No public API at all | N/A | Manual download only, as the plan already assumes |
| **BIMobject / manufacturer BIM** | No public API | N/A | Manual, as the plan already assumes (Revit/IFC → Blender → GLB by hand) |
| **Google Poly successors** | — | — | Google Poly itself shut down in 2021; Poly Pizza and Sketchfab are the direct successors already covered above. No other live "Poly-like" no-auth catalog found |

**Net:** no free, no-auth, text-searchable 3D model library exists for
hardware parts. The `library` tier in §4b.3 is inherently a "free account,
hand-curated ahead of time" tier (Poly Pizza key or hand-downloaded
BIMobject/manufacturer files), not something `assets.py` can query live
without at least one lightweight signup done in advance.

## 3. `trimesh` recipe — tested

Tested against the real `ai_mesh` output from §1.3
(`hunyuan_ac_unit_raw.glb`, 585K tris, extents `[1.985, 1.756, 1.196]` in
model-space units — not meters, not aligned to any particular axis) and
against synthetic proxy shapes. `trimesh==5.1.0`, `pygltflib`, `numpy`,
`shapely`, `mapbox_earcut`, `fast-simplification` — all via
`uv run --with ...`, nothing added to the project.

### 3.1 Load, then flatten to one mesh

```python
loaded = trimesh.load(path, force="scene")
mesh = loaded.to_geometry() if hasattr(loaded, "to_geometry") else loaded.dump(concatenate=True)
```
`trimesh.load(..., force="scene")` always returns a `Scene` (works uniformly
for GLB, OBJ, or anything with multiple sub-meshes); `Scene.dump(concatenate=True)`
is deprecated-for-removal in current trimesh in favor of `Scene.to_geometry()`
— use the latter, it emitted a `DeprecationWarning` during this test.

### 3.2 Up-axis + scale: search, don't guess

**Gotcha:** trimesh does **no** up-axis normalization on load, for any
format. GLB is the one format where the *spec* mandates +Y-up, so a
spec-compliant GLB is already Y-up when trimesh hands it to you — but OBJ has
no up-axis convention at all (Blender-exported OBJ is often Z-up, others are
Y-up), and there's nothing in the file trimesh can check. You cannot detect
"up" from geometry alone for a boxy object like an AC unit (no single axis is
obviously "the short one").

What actually worked, matching the plan's own stated heuristic (§9: *"the
resolver tries the up axis whose extents best match the published W/D/H
order"*): try all 24 orientation-preserving relabelings of the mesh's local
XYZ onto the target `(X=w, Y=h, Z=d)` frame (permutations × sign flips,
filtered to `det(R) == +1` so you rotate, never mirror, the mesh), and for
each compute the per-axis scale factor `target_dims_m / rotated_extents`.
Pick the rotation whose 3 scale factors are **most uniform**
(`std/mean` minimized) — that's the orientation where the mesh's real
proportions actually agree with the published spec, as opposed to one that
only "fits" by being squashed unevenly.

```python
def best_orientation_and_scale(mesh, dims_m):
    target = np.array([dims_m["w"], dims_m["h"], dims_m["d"]])
    verts = mesh.vertices - mesh.vertices.mean(axis=0)
    best = None
    for R in proper_rotations_24():          # 24 permutation+sign matrices, det=+1
        extents = (verts @ R.T).ptp(axis=0)
        factors = target / np.maximum(extents, 1e-9)
        cv = factors.std() / factors.mean()  # coefficient of variation
        if best is None or cv < best["cv"]:
            best = {"R": R, "factors": factors, "cv": cv}
    return best
```

Tested on the real AC-unit mesh against illustrative target dims
`w=406mm, d=559mm, h=305mm` (a plausible window-AC size — **not a looked-up
published spec**, just a number to test the recipe against):

| Mode | Achieved (mm) | Residual vs target |
|---|---|---|
| Uniform scale (mean of 3 factors) | 449.4 / 306.2 / 508.0 | **+10.7% / +0.4% / -9.1%** |
| Non-uniform (exact per-axis) | 406.0 / 305.0 / 559.0 | 0% / 0% / 0% (by construction) |

This is the real, useful finding: the AI mesh's proportions don't exactly
match the target box (expected — it's a single-image reconstruction), so a
uniform scale carries a real ~10% residual on two axes — exactly the
`scale_residual_pct` field the schema asks for. Non-uniform scaling zeroes
the residual but anisotropically stretches the mesh; for a boxy household
part the distortion is mild and worth it (this is what `part.json`'s
`asset.scale_residual_pct` is for: report it either way, non-uniform if the
distortion is acceptable, uniform + honest residual if it isn't).

### 3.3 Origin to mounting-face centre

```python
FACE_AXIS = {"+x": (0, 1), "-x": (0, -1), "+y": (1, 1), "-y": (1, -1), "+z": (2, 1), "-z": (2, -1)}

def move_origin_to_mount_face(mesh, face):
    axis, sign = FACE_AXIS[face]
    bmin, bmax = mesh.bounds
    center = (bmin + bmax) / 2.0
    center[axis] = bmax[axis] if sign > 0 else bmin[axis]
    mesh.apply_translation(-center)
```
Confirmed by re-loading every exported GLB and checking bounds: mount face
`-z` puts the mounting-face plane exactly at `z=0` with the object extending
into `+z` (`bounds[0][2] == 0.0` on every exported file, see §3.5).

### 3.4 Export

`mesh.export("model.glb")` — trimesh writes valid glTF 2.0 binary (`file`
confirms `glTF binary model, version 2` on every output here), and since the
target frame was built with Y as the "up" axis from the start (§3.2), no
separate up-axis conversion step is needed at export time — the GLB spec's
native Y-up and our target frame already agree by construction.

### 3.5 Proxy tier: box + L-bracket, with a real PBR colour (not vertex colour)

**Gotcha:** setting `mesh.visual = trimesh.visual.color.ColorVisuals(vertex_colors=...)`
produces per-vertex colour, which glTFast will render but which is not a
glTF *material* — some import paths and lighting setups treat it
differently. Use an actual PBR material instead, which round-trips into the
GLB's `materials[].pbrMetallicRoughness.baseColorFactor` (verified below):

```python
def hex_to_rgba(hex_str):
    h = hex_str.lstrip("#")
    return [int(h[i:i+2], 16) / 255.0 for i in (0, 2, 4)] + [1.0]

box = trimesh.creation.box(extents=(w_m, h_m, d_m))
box.visual = trimesh.visual.TextureVisuals(
    material=trimesh.visual.material.PBRMaterial(
        baseColorFactor=hex_to_rgba(color_hex), metallicFactor=0.1, roughnessFactor=0.7
    )
)
move_origin_to_mount_face(box, "-z")
box.export("proxy.glb")
```
Verified with `pygltflib`: re-loading `proxy_hanger_box.glb` (built for
`color_hex="#F2F2F0"`) shows
`materials[0].pbrMetallicRoughness.baseColorFactor == [0.949, 0.949, 0.941, 1.0]`
— exact round-trip. Bounds check: extents `127.0mm × 45.0mm × 38.0mm`, exactly
matching the `w/d/h` from the plan's own `part.json` example.

**L-bracket** (extrusion, for corner braces / hidden hangers): build a 2D
`shapely.geometry.Polygon` cross-section, extrude with
`trimesh.creation.extrude_polygon(poly, height=length)`, then rotate 90° about
X to re-home the extrusion axis onto the target frame:

```python
poly = Polygon([(0,0), (leg1,0), (leg1,t), (t,t), (t,leg2), (0,leg2)])
mesh = trimesh.creation.extrude_polygon(poly, height=length)
mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi/2, [1,0,0]))
```
**Gotcha:** `extrude_polygon` needs a triangulation backend for the polygon
cap faces; with none installed it raises
`ValueError: No available triangulation engine!`. `pip install mapbox_earcut`
(pure-wheel, no compilation, no torch) fixes it — that's the one extra
dependency this recipe needs beyond `trimesh` itself.

### 3.6 Decimation (needed for the real `ai_mesh` output specifically)

The Hunyuan3D-2.1 mesh (585K tris, 10.5 MB) is far denser than a small part
needs. `trimesh`'s built-in decimator needs one more optional package:
```python
simplified = mesh.simplify_quadric_decimation(face_count=8000)
```
`pip install fast-simplification` (the backend trimesh calls). Tested:
585,166 → 8,000 target faces (4,013 verts), file size 10.5 MB → 142 KB,
bounds essentially unchanged (sub-mm drift). This ran *after* the
normalize step above, i.e. the resolver's real order should be: rotate/scale
→ origin → decimate → export, so the decimation error is measured in final
real-world mm, not in arbitrary model units.

### 3.7 Files produced (all re-loaded and bounds-checked after export)

| File | What | Bounds check |
|---|---|---|
| `tests/fixtures/assets/hunyuan3d_ac_unit_normalized.glb` | Real HF-generated AI mesh, normalized (non-uniform exact scale) + decimated to 8k faces, 142 KB | extents ≈ 406×305×559mm (target), mount face at z=0 |
| `tests/fixtures/assets/proxy_hanger_box.glb` | `trimesh.creation.box` proxy, `color_hex=#F2F2F0` | extents exactly 127×45×38mm, mount face at z=0, baseColorFactor verified |
| `tests/fixtures/assets/proxy_l_bracket.glb` | L-bracket extrusion proxy, `color_hex=#5A3E2B` | extents 50×100×50mm, mount face at z=0, baseColorFactor verified |
| `tests/fixtures/assets/source_photo_window_ac.jpg` | Source product photo (public domain, Wikimedia) | — |

**Not saved to the repo** (too big for the 5MB fixture budget, per the
brief): the raw Hunyuan3D-2.1 output (`hunyuan_ac_unit_raw.glb`, 10.5MB,
585K tris) and its un-decimated normalized versions (also ~10MB each). These
only exist in the session scratchpad
(`/tmp/claude-1000/.../scratchpad/`), which is ephemeral — regenerate with
the §1.3 snippet if needed again (~15-20s).

## 4. Background removal — handled by the Space, not by us

Confirmed via `mesh_stats` on every successful Hunyuan3D-2.1 call:
`"time": {"remove background": 1.0-1.1s, ...}`, gated by the
`check_box_rembg` parameter (defaults to `True`). **No client-side background
removal is needed** for the recommended path — don't add `rembg` or any
other bg-removal step in `assets.py`, it's redundant and would just slow
things down. `stable-fast-3d` similarly does its own (via
`foreground_ratio` + an internal bg-removal call), and `TripoSG` exposes it
as an explicit standalone step (`/run_segmentation`) that its own UI calls
before generation — i.e. even the one Space that separates the step still
expects the caller to run it as part of the same flow, not skip it.

## 5. Summary for `server/assets.py`

- `ai_mesh` tier: call `tencent/Hunyuan3D-2.1` `/shape_generation`
  anonymously (§1.3 snippet), expect an untextured mesh in ~15-20s, decimate
  to a few thousand faces, run it through the §3 normalize pass, badge stays
  honest ("approximate look") because texture generation isn't reachable
  anonymously. Keep `tencent/Hunyuan3D-2` as a same-code fallback space id.
  Smoke-test `trellis-community/TRELLIS` once before the event as a second
  fallback — untested here.
- `library` tier: Poly Pizza needs one free signup done in advance (not
  live-anonymous); Objaverse has no live search API despite being fully
  anonymous, so it's a pre-curation source at best. Neither replaces the
  plan's existing hand-download step.
- `proxy` tier: the box/extrusion recipe in §3.5 is solid, tested, and cheap
  — treat it as the primary safety net, not the last resort, given 2 of 3
  candidate AI spaces were dead on test day.

## Addendum 2026-09-24 ~06:00 — anonymous ZeroGPU quota exhausted

The anonymous ZeroGPU quota is per-IP and shared by every ZeroGPU space; ours ran out after a
few meshes ("Try again in 21:43:33"). A sweep of 794 image-to-3D spaces via the HF Hub API found
83 running: 60 ZeroGPU (same quota), 23 CPU-only, and exactly one on dedicated GPU
(`AguaLeo/Image-to-3D-2-test`, t4-small, `/gen_shape`). It works anonymously but took 447 s
(~408 s queue on a single T4) and is a personal test space — not wired in. Dedicated-hardware
Hunyuan3D forks (`Jbowyer/Hunyuan3D-2.1`, `johndelavega/Hunyuan3D-2mv`) were paused. No free
no-auth non-HF image-to-3D API found. Fix: set `HF_TOKEN` (free account quota) or wait for the
reset; the server serves exact-size proxy boxes meanwhile.
