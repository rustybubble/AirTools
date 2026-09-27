# P1 quality tuning runs: seam-leveling, V-flip, crop-before-texture, held-out QA

Scope: this doc was originally written from QA numbers that turned out to be measured *before* two
real bugs were fixed (V-flip in the texture sampler/atlas crop, and OpenMVS seam leveling
blackening the atlas) — its old PSNR ~5-6 numbers, "confetti" description, and RefineMesh/
resolution-level comparison were all downstream of that broken metric. This revision replaces them
with numbers measured *after* the fixes, plus a new feature (crop-before-texture) and a new QA
capability (genuine held-out frames) validated against the same 4-clip corpus. All runs on `hfbox`
(16-core AMD box, 30GB RAM, `/workspace/airtools`), OpenMVS v2.4.0.

## Current `run_mvs()`/`process_mesh()` defaults

- `resolution_level=0` (full-res `DensifyPointCloud` depth maps)
- `--decimate` computed from `target_triangles=200_000` via `_auto_decimate_ratio()`
- `texture_resolution_level=0`, `--max-texture-size 4096`, `--cost-smoothness-ratio 0.1`
- `--global-seam-leveling`/`--local-seam-leveling` **off** (corrected from this doc's original
  "on" — see below; the v2.4.0 prebuilt binary's seam leveling blackens the atlas)
- `--crop auto` (new): crop the dense mesh to the subject before `TextureMesh`, via view-centrality
  (`mesh.crop_to_subject_auto`) rather than the old post-texture, ray-intersection crop
- `--holdout-every 5` (new): extract frames denser than `--fps` and feed SfM only every 5th one, so
  QA has genuine held-out frames to score
- RefineMesh still not wired into the default pipeline (see "Needs re-measurement" below)

All flag-overridable on the CLI; the above are only the defaults.

## Fixed: two real bugs, confirmed before this doc's numbers are trustworthy

**V-flip bug (`f55a1a4`).** trimesh keeps UV `v=1` as the image's *top* row after loading a glb, so
a texture row is `(1 - v) * h`, not `v * h`. Both `preview.py`'s sampler and
`mesh.py::_tight_crop_scene_textures`'s atlas crop had this backwards, so every QA render and every
cropped atlas was reading the wrong row — a large, silent source of "wrong" PSNR/SSIM independent
of the mesh's actual quality.

**Seam-leveling bug (`f886b36`).** OpenMVS v2.4.0's prebuilt `TextureMesh --global-seam-leveling 1
--local-seam-leveling 1` (the tool's own default, and what this doc originally adopted) blackens
patches of the atlas with colour blobs in this specific binary. Confirmed via
`texdbg/texcorr.py` (texture-vs-photo colour correlation at face centroids per view, strasbourg):

| seam leveling | corr/view |
|---|---|
| off (current default) | 0.78 – 0.94 |
| local only | ~0.22 |
| global only | ~0.02 |

Both fixes predate everything below; the numbers in this doc are measured after both.

## New: crop the dense mesh to the subject before texturing

**Problem.** `TextureMesh`'s 4096px atlas and the 200k-triangle budget were being spent on the
*entire* reconstructed scene — background city, forest, hillside — not just the subject, because
the only crop that existed (`camera_target`+`crop_to_subject`, a single ray-intersection point +
radius) ran *after* texturing, and its `cond(A)` guard rejects any camera path whose optical axes
don't clearly converge (a genuine flyby/facade pass, but also, per the corpus validation, footage
labeled `"orbit"` that was too close to a straight line to satisfy the heuristic in practice).
2 of 4 corpus clips (strasbourg, watertower) hit that guard and got **no crop at all**.

**Fix.** `mesh.crop_to_subject_auto` (`--crop auto`, new default) scores every vertex by how many
cameras see it within the central ~45% of frame, weighted by `1/depth` — no assumption that optical
axes converge, so it works on orbits and flybys alike. It keeps the connected high-score region(s)
above a robust (median-of-positive-scores) threshold, drops small disconnected floater components,
then crops the *whole* mesh (subject **and** nearby ground) to that region's horizontal bounding
box + 15% margin — a vertical prism, unbounded in the up direction, so ground right at the
subject's base survives while ground far away doesn't. It runs as a new Python step in
`mvs.run_mvs`, between `ReconstructMesh` and `TextureMesh`, on the *untextured* dense mesh, so the
atlas/triangle budget that follows is spent on the cropped result. The old crop
(`crop_to_subject_orbit`) is kept as the `--crop orbit` opt-in for a known-good converging orbit;
`--crop none` disables cropping.

### Before/after, all 4 corpus clips

`work/<site>/sfm` and `.../mvs/scene_dense*` were **reused** (existing SfM/dense outputs, per the
rerun instructions) — only `mvs/textured*` was deleted to force re-texturing, so what changed here
is exactly and only crop-before-texture (+ the two bug fixes already landed before this pass;
"before" below is *post*-bugfix, matching the numbers in the assignment brief for this task, and
confirmed identical to `report.json` on `hfbox` before this rerun). Because SfM/frame extraction
were reused (not re-run from scratch), `held_out` is `n=0` for all 4 — see "Held-out QA" below for
a real held-out measurement on a fresh run.

| clip | tri (before→after) | texture px (before→after) | mesh.glb (before→after) | PSNR (before→after) | SSIM (before→after) | coverage (before→after) |
|---|---|---|---|---|---|---|
| strasbourg-cathedral-spire | 199,977 → **128,472** | 4094 → **2045** | 7.92MB → **4.26MB** | 16.53 → **18.01** | 0.514 → **0.580** | 0.341 → **0.116** |
| watertower-forest-orbit | 199,985 → 199,992 | 4094 → 4093 | 7.76MB → 7.60MB | 19.63 → **19.83** | 0.345 → **0.369** | 0.297 → 0.292 |
| lighthouse-orbit-coastal-1 | 199,982 → 199,990 | 4093 → 4094 | 6.20MB → 6.16MB | 25.00 → **25.47** | 0.665 → **0.712** | 0.305 → 0.277 |
| facade-highrise-corner | 199,941 → 199,999 | 4096 → 4096 | 5.97MB → 7.37MB | 15.61 → **16.99** | 0.173 → **0.231** | 0.386 → **0.230** |

PSNR and SSIM improve on **all 4** clips (texture sharpness on the subject: confirmed, not just
expected) — cross-checked against `texdbg/texcorr.py` on the raw `work/<site>/mvs/textured.glb`
(independent of `preview.py`/QA entirely): per-view colour correlation 0.55–0.93 across all 4
post-crop, consistent with the seam-leveling-off numbers above, none of the near-zero correlation
that would indicate a broken/misassigned atlas.

**Coverage dropped, not stayed flat, on 2 of 4 clips (strasbourg, facade) — confirmed why, not
guessed:** those are exactly the two clips that got **no crop at all** before (strasbourg hit the
flyby-skip guard despite being labeled orbit; facade correctly hit it, being a genuine facade
pass). `coverage` measures what fraction of the *rendered frame* has any mesh — an uncropped mesh
covers the whole visible scene (subject + city/hillside beyond it, most of it OpenMVS's flat
"empty color" fill, not real texture), so it scores high `coverage` while being mostly unreal. A
correct crop removes that filler, so coverage on the *frame* legitimately drops even as texture
quality on the *subject* improves — visually confirmed in the side-by-side PNGs below (strasbourg's
cropped render is the spire alone, cleanly textured, no more skyline of flat-orange city). Watertower
and lighthouse (which *did* get some crop before, even if too loose per the original doc) show
coverage within a few points of before, matching the "should stay ~same" expectation more directly.

**Known imperfection, confirmed via the watertower render, not a regression:** on
watertower-forest-orbit, the crop correctly identifies and keeps the tower, but its horizontal
bounding box also keeps a wide apron of surrounding hilltop canopy (visible from most orbit angles,
so it scores nontrivial view-centrality too) — most of that canopy renders solid black (OpenMVS
never assigned it real texture; a pre-existing view-assignment limitation on oblique/self-occluding
foliage, not introduced by the crop). Tightening `score_percentile`/`central_fraction` or the crop
margin would likely help here specifically; not done in this pass (see Remaining issues).

### Per-stage timings (`hfbox`, this rerun — SfM/dense resumed, only crop+texture+postprocess ran)

| clip | crop step | TextureMesh | mesh postprocess | QA (12 frames) |
|---|---|---|---|---|
| strasbourg-cathedral-spire | 2.1s | 16.1s | 0.2s | 4.5s |
| watertower-forest-orbit | 3.3s | 31.3s | 0.5s | 11.0s |
| lighthouse-orbit-coastal-1 | 9.6s | 44.1s | 0.5s | 7.4s |
| facade-highrise-corner | 5.5s | 21.0s | 16.2s* | 13.8s |

\* facade still splits across multiple `TextureMesh` atlases (pre-existing, documented below,
unaffected by this change) — `mesh.process_mesh`'s tight-crop-and-repack of each sub-atlas is
where that extra time goes, not the new crop step.

The crop step itself (pure Python, no OpenMVS subprocess) costs 2–10s per clip — negligible next to
`DensifyPointCloud`'s 200–1600s on this corpus (this doc's original table). Full end-to-end wall
times (extract→qa, from scratch) are unchanged from the original table since SfM/dense are
unaffected by this change; this run only re-measured the crop+texture+postprocess+QA tail.

## New: held-out QA frames

**Problem (noted, not fixed, in this doc's original pass):** `run()`'s frame extraction produced
*exactly* the frames handed to SfM, 1:1, on all 4 corpus clips — there were never any spare frames
in `frames_dir` for `qa.py`'s already-built `default_ids`/`interpolate_camera` held-out path to
find. Every QA number to date was measured on the mesh's own source photos, not on unseen frames.

**Fix:** `run()` now extracts `--holdout-every` (default 5) times denser than `--fps`, and feeds SfM
only every 5th frame (`frames_nominal`) — at (very nearly) the same video timestamps a plain
`--fps` extraction would hit, so SfM sees the same frames either way. The rest of `frames_dir` are
genuine held-out frames. `qa.photo_consistency` now reports `train`/`held_out` as separate
aggregates alongside the combined `mean_*`.

The 4-clip corpus rerun above reused existing SfM/frame extraction (to save the ~200–1600s Densify
cost per clip, per the rerun instructions), so it has no held-out frames to show (`held_out.n == 0`
for all 4 — an honest "not measured here", not a bug). A fresh, from-scratch run demonstrates the
mechanism actually works: `strasbourg-cathedral-spire`, `--fps 2 --resolution-level 2
--target-triangles 20000` (low-res/low-budget smoke settings, not the quality defaults — this run
is to validate the *mechanism*, not to re-measure quality):

| | n | mean PSNR | mean SSIM | mean coverage |
|---|---|---|---|---|
| train (registered) | 6 | 16.16 | 0.436 | 0.311 |
| held out (never seen by SfM) | 6 | 16.26 | 0.438 | 0.305 |

Held-out scores land within noise of train scores — the reconstruction generalizes to frames it
never saw, which is the actual point of held-out evaluation. (33/33 frames registered, SfM 23.2s,
`DensifyPointCloud` 119.6s at `--resolution-level 2`, crop 0.5s, texture 1.9s — all on `hfbox`.)

## Preview images (post-fix, post-crop; photo | render | abs-diff)

- `docs/research/img/strasbourg-spire.jpg` — frame 0025. Spire cleanly cropped and textured; no
  more surrounding city.
- `docs/research/img/watertower-forest.jpg` — frame 0001. Tower well textured; canopy apron mostly
  untextured (see "known imperfection" above).
- `docs/research/img/lighthouse-coastal.jpg` — frame 0037. Lighthouse + outbuilding + immediate
  terrain, tightly cropped, clearly recognisable.
- `docs/research/img/facade-highrise.jpg` — frame 0022. Near-complete, sharp facade texture
  (compare to the original doc's "fragments against black patches" at the same frame).

All 4 replace the originals in place (same filenames), all under 300KB JPEG.

## Needs re-measurement (not done in this pass)

The original doc's **Config A/B/C** 3-way comparison (`resolution-level` 0 vs 1, and a RefineMesh
ablation) was measured on the same broken (pre-V-flip-fix, pre-seam-fix) metric as everything else
in that version of the doc — its PSNR numbers and the "Config A is confetti, Config B is coherent"
visual call may well still hold (the visual read didn't depend on the metric), but the specific
PSNR deltas it argued from cannot be trusted, and it wasn't cheap to safely re-run in this pass
(re-running Config A requires a second `DensifyPointCloud` pass at `resolution-level=1`, ~200–1600s
per clip, on top of everything already re-measured here). Treat "`resolution_level=0` beats `1`"
and "skip RefineMesh" as still the operating defaults (unchanged by this pass) but **not**
re-verified numbers — a follow-up pass should re-run at least the strasbourg A/B pair under the
current (fixed) metric before trusting the PSNR deltas again.

## Remaining issues (ranked)

1. **Watertower-style canopy over-inclusion** (above) — the view-centrality crop's horizontal-
   footprint heuristic can keep wide, mostly-unseen surrounding terrain when it's genuinely central
   in many orbit frames (a forested hilltop, not just a floater). Tightening `score_percentile`
   and/or `margin_factor` for canopy/foliage-heavy sites is the likely fix; needs its own
   comparison pass, not done here.
2. **Config A/B/C needs re-measurement** (above) under the corrected metric before its conclusions
   are trusted again.
3. **Facade's multi-atlas split** (pre-existing, `mesh.py::_tight_crop_scene_textures`) still costs
   real `mesh_postprocess_s` time (16.2s here) and isn't a from-scratch bin-packer — noted in the
   code's own docstring as a known, accepted limitation, unrelated to this pass's changes.
4. **`_face_components`'s union-find is a pure-Python loop over face-adjacency edges** (`mesh.py`,
   `ponytail:` comment in the code) — measured at 2–10s on this corpus (not a bottleneck yet), but
   would need `scipy.sparse.csgraph.connected_components` if ever run against a multi-million-face
   mesh.

## First real Mini 4K capture: `DJI_0095.MP4` (kitchen, indoors), 2026-09-25

Input: 87.25 s of 3840×2160 H.264 at 29.97 fps, 1.09 GB, plus a 1 Hz caption track in the
single-line OSD format. GPS is `n/a` throughout (indoors). H is 1.0–1.9 m at 0.1 m resolution,
H.S reaches 0.61 m/s, and ISO runs 100–2000 at 1/30 s. The drone flew at 1–2 m past the counters
and ended on the floor. Run on hfbox with the default `--stages preview,full`.

| Step | Time |
|---|---|
| Upload laptop → hfbox (rsync, 1.09 GB) | 439 s (7.3 min, ~2.5 MB/s) |
| **Preview published (r1)** | **67 s** after start |
| - seek-extract 48 frames from 4K | 16.7 s |
| - VGGT model load (cold) / forward | 8.0 s / 6.8 s |
| - TSDF fuse | 1.7 s |
| - OpenMVS TextureMesh on 48 × 4K frames | 20.7 s |
| - calibrate + mesh post-process + thumbs | 11.3 s |
| **Full published (r2)** | **2039 s (34 min)** after start |
| - extract 175 frames | 11.8 s |
| - SfM (features 38.9, match 10.9, map 63.0) | 112.7 s |
| - **DensifyPointCloud** | **1631 s (27 min, 82% of the run)** |
| - ReconstructMesh / crop / TextureMesh | 36.2 / 8.8 / 58.3 s |
| - QA | 14.4 s |
| Re-run with caption scale (every heavy step cached) | 41 s |

Quality:
- **SfM:** 165/175 frames registered. The global mapper split them into 2 components, and the
  incremental retry kept the global result. 11k points; mean reprojection error 0.60 px.
- **Full mesh:** 200k tris, 4K atlas. QA PSNR 20.8, SSIM 0.74, coverage 0.70. That is the best
  SSIM of any clip so far: indoor surfaces with printed texture help.
- **Preview:** 37k tris, 4K atlas.

Scale from the caption, with no GPS (new `altitude`/`odometry` candidates):

| Candidate | Scale | Notes |
|---|---|---|
| `altitude` (camera up-axis vs caption H, centred fit) | 0.1847 | residual 0.050 m over 165 frames, H range 0.90 m; **selected** |
| `odometry` (∫H.S vs horizontal camera path) | 0.1538 | 17% below altitude; vision-positioning speed indoors, cross-check only |

**Sanity check (inferred from the mesh, not tape-measured):**
- The largest horizontal-surface peak in the scaled mesh is at y = −0.35 m; this is likely the
  countertop.
- Assuming the standard 0.91 m counter height puts the floor at −1.26 m.
- That gives camera heights of 1.14–1.91 m above the floor, against caption H = 1.0–1.9 m above
  takeoff. The absolute offset agrees to about 0.1 m, and the fit never used it (both series
  were mean-centred).
- So the altitude scale looks right to roughly 10%. Hand-measure one real dimension to confirm.

Issues found:
1. **The preview's own altitude fit disagrees with the full stage by 45%** (fitted
   preview-per-full scale 0.69). Both are "metric", so Stage B aligns rigidly and keeps its own
   scale, and the room visibly grows at the swap (alignment residual 0.21 m).
   - Cause (inferred): VGGT's 48-frame poses are too noisy along the up axis for a 0.9 m signal.
   - Options: publish the preview unscaled until Stage B confirms it, or fit the preview's scale
     from a known dimension or the board instead.
2. **Densify dominates** (27 of 34 min). Knobs to measure next:
   - `--fps 1` (about 88 frames instead of 175);
   - a higher densify `resolution_level`;
   - COLMAP 4.2 HIP `patch_match_stereo` (`p1-colmap-realityscan.md`).
3. **Upload is 7 min for 1 GB.** Transcode to about 1/4 size or rsync extracted frames, since the
   pipeline only uses ~175 frames. Alternatively upload in parallel with preview extraction.
4. **The preview missed the 1-minute target by 7 s.** Two fixes: a warm VGGT worker (saves 8 s
   of load) and texturing from 2K frames instead of 4K.
5. **Fixed during this run:**
   - `probe` used the container duration (88.0 s) instead of the video's (87.25 s), so the last
     preview seek produced no frame (`1617ab6`).
   - The SRT parser dropped every GPS-`n/a` block, so the height and speed readings were lost
     (`d7d7832`).
   - Scale from the caption was added in `0e1096b`.
