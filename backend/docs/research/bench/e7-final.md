# E7: fill distance, sharpest-of-2 and KTX2 atlas compression (kitchen-0095)

Experiment log for the last slot of the 2026-09-25 bench session (13:18–13:50 UTC on hfbox).
`docs/research/p1-recon-bench.md` §3 and §5 hold the merged view.

**Setup.** hfbox was idle: no other agent, both job queues empty, `idle_check.sh` logged before
each timed job. Every run used `taskset -c 0-15`, `--threads 16` and E1's harness.
- Job chain: `pipeline/experiments/bench/k_jobs.sh`, which calls `pipejob.sh`, `score.sh` and
  `novel.sh`.
- Afterwards the chain deleted the runs' own `*.dmap`, `*.npy`/`*.npz`, `scene_dense*.ply` and
  depth caches.
- `work/dji0095` was only read, through `mkwork.sh` hardlinks.

## 0. Results

| run | what | wall s | est. total s | view cov | novel back / right | PSNR / SSIM | lap corr | F@2 / F@5cm | Chamfer cm | fill pts |
|---|---|---|---|---|---|---|---|---|---|---|
| `i-hybrid-moge2` (E1 §6) | hybrid, fill 2 cm, cached SfM | 451 | 604* | 0.996 | 0.911 / 0.791 | 21.88 / 0.806 | 0.54 | 0.609 / 0.753 | 3.62 | 80,515 |
| **`k-fill1cm`** | hybrid, `--densify-args "--fill-dist-m 0.01"`, cached SfM | **451** | 604* | 0.996 | 0.910 / 0.791 | **21.88 / 0.809** | 0.56 | 0.612 / 0.757 | 3.64 | 91,330 |
| `k-fill2cm-ctl` | paired control: `k-fill1cm`'s own DensifyPointCloud output, fill 2 cm | (140, densify skipped) | – | 0.996 | 0.910 / 0.791 | 21.87 / 0.806 | 0.56 | 0.609 / 0.755 | 3.65 | 80,891 |
| `i-e2e-fixed` (E1 §7) | defaults, fresh, `--stages preview,full` | 626 | 626 | 0.996 | 0.915 / 0.789 | 21.51 / 0.807 | 0.54 | 0.596 / 0.738 | 4.06 | 74,213 |
| **`k-sw2-e2e`** | defaults + `--fps 4 --sharpen-window 2`, fresh, `--stages preview,full` | **644** | 644 | 0.995 | 0.911 / 0.792 | **21.70 / 0.802** | 0.50 | 0.611 / 0.754 | 3.57 | 80,971 |

`*` = the 451 s wall time plus E1's 153 s SfM charge (§6 convention).

## 1. Hybrid fill distance: 1 cm vs 2 cm

- **Command:** `pipejob.sh k-fill1cm dji0095 sfm --crop none --densify hybrid --densify-args "--fill-dist-m 0.01"`.
  It ran on the cached SfM, the same way as `i-hybrid-moge2`.
- **Stage times:**
  - DensifyPointCloud: 335 s. Hybrid densify total: 349 s. The worker fuse took 13.6 s, with
    MoGe inference hidden behind DensifyPointCloud.
  - ReconstructMesh: 22 s. TextureMesh: 51 s. QA: 14 s.
  - Wall time: 451 s, identical to `i-hybrid-moge2`.
- **Paired control.**
  - Across runs, OpenMVS noise is about ±0.3 dB. So `k-fill2cm-ctl` reused `k-fill1cm`'s own
    `scene_dense.ply` and depth maps (`mkwork.sh … dense`), and only the worker's union step
    and the mesh/texture tail were re-run.
  - The two runs differ only in `--fill-dist-m`. The 1 cm fill adds 10,439 more TSDF fill
    points (91,330 vs 80,891 on 680,519 MVS points).
- **Result: a tie.** On the paired comparison, 1 cm changed PSNR by +0.01 dB, SSIM by +0.003,
  F@2cm by +0.003 and lap-corr by 0.00. Coverage and novel-view coverage were equal. The run
  matches `i-hybrid-moge2` too.
  - E5's +0.13 dB (`h47` vs `h41`) was measured with **WorldMirror** fill and did not carry
    over to MoGe-2 fill.
  - **Keep 2 cm.** The gain is below every noise band.

## 2. Sharpest-of-2 at fps 4, with the hybrid default, fresh SfM

- **Command:** `pipejob.sh k-sw2-e2e none sfm --crop none --stages preview,full --fps 4 --sharpen-window 2`.
  - These are the same stages as `i-e2e-defaults`/`i-e2e-fixed`, so both totals include the
    preview.
  - It was scored with `--id-fps 20` (E1's `--id-fps` = 5 × `--fps` rule).
  - `novel.sh` has no id-fps flag and first mis-timed the cameras (0.06 / 0.03). It was
    re-run through `novel_coverage(..., id_fps=20.0)`, and the corrected row is in
    `results/novel.jsonl`.
- **SfM:** 165 of 175 selected frames registered, at 0.60 px. The fps-2 default registers
  175/175.
  - SfM total: 91 s against 51 s (feature extraction 19, matching 11, mapping 61). The run
    extracts 1,745 candidate frames at 20 fps (12.8 s) and then keeps the sharper of each pair.
  - Densify: 350 s. ReconstructMesh: 22 s. TextureMesh: 53 s. QA: 14 s.
- **Against the fresh default (`i-e2e-fixed`):** +18 s (644 vs 626), +0.19 dB PSNR, −0.005
  SSIM, +0.015 F@2cm, and lap-corr 0.50 vs 0.54. Coverage and novel-view coverage are equal.
  - Every delta is inside the noise band (~0.3 dB, ~0.05 F@2cm). Lap-corr moves the wrong way.
  - E1's F@2cm lead for sharpest-of-2 (0.832 vs 0.762 for plain OpenMVS res1) does not repeat
    under hybrid: 0.611 vs 0.596. Hybrid's fill surface dominates F@2cm against the OpenMVS
    reference (E1 §5.1).
  - **Keep `--fps 2`.** Sharpest-of-2 costs more SfM time and loses 10 registered frames, with
    no measurable gain.

## 3. KTX2 atlas compression (measure only, no pipeline change)

- **Tools:** prebuilt releases, downloaded on hfbox into `/workspace/tools/e7-ktx`. gltfpack 1.2
  is `meshoptimizer` v1.2 `gltfpack-ubuntu.zip`, MIT. KTX-Software 4.4.2 is the Linux x86_64
  tarball (Apache-2.0), used for `ktx info`/`ktx extract --transcode rgba8`.
- **Input:** `scene/bench/i-e2e-fixed/mesh.r1.glb`, the fixed fresh-default mesh. It is 5.83 MB:
  0.73 MB of JPEG atlas (4096×2047) plus 5.10 MB of geometry.
- **Scripts:** `pipeline/experiments/bench/ktx_eval.py`, then `swap_tex.py`. The second one puts
  the decoded KTX2 atlas back into the original GLB as a PNG, so the E1 harness can render it.
  The harness's trimesh cannot read KTX2.

| variant (gltfpack flags) | GLB MB | atlas bytes | GPU texture, with mips | atlas PSNR vs original (used texels) | held-out render PSNR / SSIM | lap corr |
|---|---|---|---|---|---|---|
| original (JPEG) | 5.83 | 0.73 MB | RGBA8 4096×2047: **42.7 MiB** (32.0 without mips) | – | 21.51 / 0.807 | 0.54 |
| ETC1S (`-tc -noq`) | 6.08 | 0.98 MB | ETC2 RGB: **5.3 MiB**; ASTC 4×4: 10.7 MiB | 33.2 dB | 21.42 / 0.804 | 0.52 |
| ETC1S q10 (`-tc -tq 10 -noq`) | 6.15 | 1.05 MB | same | 33.4 dB | not rendered | – |
| UASTC (`-tc -tu -noq`) | 10.69 | 5.59 MB | ASTC 4×4: **10.7 MiB** | 36.9 dB | 21.50 / 0.806 | 0.53 |
| ETC1S + gltfpack's default quantization (`-tc`) | **5.01** | 0.98 MB | 5.3 / 10.7 MiB | 33.2 dB | not rendered (same atlas as ETC1S) | – |

- **GPU memory is the win: 4–8× less texture memory.**
  - The KTX2 files carry a full 13-level mip chain.
  - GPU figures are computed as width × height × bytes per pixel × 4/3: RGBA8 is 4 B/px, ASTC
    4×4 is 1 B/px and ETC2 RGB is 0.5 B/px.
  - Which format the Quest runtime transcodes BasisU to (ASTC or ETC2) was not measured.
- **GLB size does not shrink.** The JPEG atlas is already small (0.73 MB), so ETC1S grows it
  slightly and UASTC grows it by 4.9 MB.
  - Only gltfpack's default vertex quantization makes the file smaller: 5.83 to 5.01 MB. That
    needs `KHR_mesh_quantization` support in the viewer, which was not checked.
- **Quality:**
  - ETC1S costs 0.09 dB and 0.003 SSIM of held-out render PSNR, and lap-corr drops by 0.02.
    That is below the noise band, though the loss is systematic.
  - UASTC costs 0.01 dB.
  - Atlas-level PSNR is measured against the decoded JPEG, over texels brighter than 8/255
    (77 % of the atlas).
  - gltfpack rounds the atlas from 2047 to 2048 rows, so the decode is resampled back. That
    round trip alone limits PSNR to 43.3 dB.
- **Recommendation (not implemented):**
  - ETC1S via `gltfpack -tc` is the Quest option when texture memory is the limit. It frees
    room for a second 4K atlas or a larger one at under 0.1 dB.
  - UASTC is nearly lossless, but it doubles the download.
  - Both need a KTX2/BasisU-capable loader on the Quest client (`KHR_texture_basisu`), which
    was not checked.
