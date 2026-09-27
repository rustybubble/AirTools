# R1: the academic state of the art for video → textured metric mesh (2023 – Sep 2026)

Agent R1 wrote this for the kitchen-0095 bench (`docs/research/p1-recon-bench.md`) on 2026-09-25.
Scope: research code and papers only. No accounts, paid APIs or cloud resources were used. Nothing
here was run on hfbox. Every row cites a URL, and **(unverified)** marks claims we could not
confirm from a primary source (paper, README, LICENSE or model card).

This builds on the earlier P1 research docs and does not repeat them:
- `p1-fast-recon.md` §2–3: the VGGT, Pi3, MapAnything and MASt3R one-liners, plus the fast-splat
  training numbers.
- `p1-gpu-rocm.md`: VGGT, MoGe-2 and DA-V2 measured on hfbox.
- `p1-colmap-realityscan.md` §1.3: COLMAP 4.2's HIP patch-match stereo.

Research method. The session's shared WebSearch budget ran out early. After that, sources came
from direct fetches of arXiv abstract/HTML pages, the arXiv API, GitHub raw READMEs and LICENSE
files, `git ls-remote`, and the Hugging Face model API. We checked licences against the HF card
*and* the repo. Where the two disagree, both are given.

---

## 0. TL;DR (what is new since the P1 docs, ranked by impact on our three goals)

1. **VGGT-Ω (CVPR 2026 oral, Meta/Oxford) is the "VGGT-2".** On the 10-frame pose benchmark it is
   far ahead on indoor scenes:

   | 7-Scenes | VGGT | π³ | DA3 | VGGT-Ω |
   |---|---|---|---|---|
   | AUC@3° | 10.9 | 13.3 | 18.7 | **36.4** |
   | AUC@30° | 74.4 | 77.0 | 78.2 | **88.2** |

   - Its README shows 100 frames at 624×416 in **13.4 GB**, which fits our 16 GB card.
   - It is pure PyTorch; `requirements.txt` has no CUDA extensions.
   - Catches: the weights are gated behind a Hugging Face access request, and the licence is the
     FAIR **non-commercial** research licence.
   - This goes straight at the preview's 45% scale error, whose cause is noisy VGGT poses.
   - Sources: [arXiv 2605.15195](https://arxiv.org/abs/2605.15195),
     [repo](https://github.com/facebookresearch/vggt-omega).
2. **Plain VGGT-1B can take 3–6× more frames per pass on our card** with the inference-only
   [vggt-low-vram](https://github.com/harry7557558/vggt-low-vram) fork.
   - Its README reports 125 frames in 5.8 GB and 311 frames in 11.0 GB, with the same
     checkpoint.
   - So all 175 full-stage frames fit in one pass. That gives a coverage-complete preview and
     better-conditioned poses for the height fit.
3. **The 27-minute `DensifyPointCloud` can be replaced by learned depth plus fusion.** Three
   routes are published, each with a ROCm-plausible path:
   - **(a) Metric mono-depth, per-frame aligned to the SfM sparse points, then TSDF.** MoGe-2 is
     already measured on hfbox. Metric3D v2 is BSD-2. DA3-Metric-Large is Apache-2.0.
   - **(b) PromptDA** (Apache-2.0), which takes sparse depth as a prompt.
   - **(c) COLMAP 4.2's HIP patch-match.** [PR #4420](https://github.com/colmap/colmap/pull/4420)
     was tested on gfx1100 and gfx90a; gfx1201 is untested.

   Learned priors are the published answer to textureless cabinets and walls, where PatchMatch
   finds no photo-consistency. We found no paper that measures "learned depth beats PatchMatch on
   textureless surfaces" head to head, so treat that as inference.
4. **Gaussian splatting now runs on RDNA4.** The community
   [Painter3000 `*-rocm72-gfx1201`](https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201) ports
   were validated on an R9700, which is the same gfx1201 die as our RX 9070 XT:
   - gsplat 1.5.3, 3DGS and 2DGS paths, with depth render modes;
   - the original 2DGS repo, including its TSDF mesh extraction;
   - simple-knn and fused-ssim.

   AMD's own `amd_gsplat` wheel targets MI300X only and lacks Wave32.

   **GOF, PGSR, RaDe-GS and MILo have no ROCm port.** Those methods need a CUDA box. §4 covers
   them.
5. **Metric scale.** No published method recovers scale from a 0.1 m-quantised barometer over a
   0.9 m span.
   - The best evidence says to combine cues with a robust inverse-variance estimate:
     - metric-depth median ratio against the reconstruction (indoor metric-depth nets reach
       δ1 ≈ 0.97–0.99 on NYUv2);
     - known object sizes;
     - height, as a weak cue.
   - MapAnything's *image-only* metric scale is weak indoors. It scores rel 22.2 and τ 10.6% on
     ScanNet in its own Table 4, and the authors call it sub-optimal. So do not treat it as a scale
     oracle.

## 1. Feed-forward and learned SfM (family 1)

### 1.1 Master table

Legend for **ROCm**:
- **A**: pure PyTorch; runs or is expected to run as-is.
- **B**: optional CUDA op with a PyTorch fallback, or an xformers or flash-attn dependency that
  has a ROCm path.
- **C**: a hard CUDA kernel with no port (lietorch, custom `.cu`).

"Fit" rates coverage (C), speed (S) and detail/accuracy/scale (D) from 0 to 3 for our case.

| Method (venue) | Code | Reported accuracy (named table) | Speed / VRAM / frame limit | Metric? | Licence code / weights | ROCm | Fit C/S/D |
|---|---|---|---|---|---|---|---|
| **VGGT** (CVPR'25 best paper) [2503.11651](https://arxiv.org/abs/2503.11651) | [facebookresearch/vggt](https://github.com/facebookresearch/vggt) `a288dd0` | VGGT-Ω Table 1, 10 frames: 7-Scenes AUC@3/30 10.9/74.4; ETH3D 18.8/62.1 ([2605.15195](https://arxiv.org/html/2605.15195)) | **hfbox measured:** 48 frames 6.8 s; 64 frames 9.6 GB bf16 (`p1-gpu-rocm.md`) | no (up to scale) | Meta VGGT licence / weights CC-BY-NC; `VGGT-1B-Commercial` is gated ([HF](https://huggingface.co/facebook/VGGT-1B-Commercial)) | A (measured) | 2/3/1 |
| **vggt-low-vram** (fork, 2025) | [harry7557558/vggt-low-vram](https://github.com/harry7557558/vggt-low-vram) `75c0507` | same checkpoint, same outputs | 125 frames 5.83 GB, 311 frames 10.97 GB; on a 4090, 125 frames 16.5 s and 311 frames 66 s; 0.86–0.91× baseline speed at ≥125 frames ([README](https://github.com/harry7557558/vggt-low-vram)) | no | as VGGT | A; uses `torch.compile` on small modules, which is a gfx1201 Inductor-bug risk (§8) | 3/3/1 |
| **VGGT-Ω** (CVPR'26 oral, best-paper finalist) [2605.15195](https://arxiv.org/abs/2605.15195) | [facebookresearch/vggt-omega](https://github.com/facebookresearch/vggt-omega) `48b23c8` | Table 1, 10 frames: 7-Scenes AUC@3/30 **36.4/88.2**; NRGBD 92.5/99.1; ETH3D 56.3/90.4. Table 2 depth: ETH3D δ1.25 99.8, AbsRel 0.009 | A100 at 624×416, whole program: 50 frames 9.66 GB, **100 frames 13.37 GB**, 200 frames 20.8 GB ([README](https://github.com/facebookresearch/vggt-omega)). Paper: >1000 frames on one A100, 20–25% faster than VGGT | no | FAIR Noncommercial Research Licence; HF weights gated with a manual form ([card](https://huggingface.co/facebook/VGGT-Omega)) | A (`torch>=2.6`, no custom ops) | 3/3/**3** |
| **FastVGGT** (ICLR'26) [2509.02560](https://arxiv.org/abs/2509.02560) | [mystorm16/FastVGGT](https://github.com/mystorm16/FastVGGT) `6526e27` | "4× at 1000 images, competitive accuracy" (unverified numbers) | training-free token merging over VGGT-1B | no | as VGGT | A | 2/3/1 |
| **VGGT-Prime** (tech report, Sep 2026) [2609.23733](https://arxiv.org/abs/2609.23733) | [project page](https://vggt-prime.github.io) | "8× over VGGT, 14× with token merging, competitive pose and depth" | — | no | code not confirmed | A? | watch |
| **VGG-T³** (CVPR'26, NVIDIA) [2602.23361](https://arxiv.org/abs/2602.23361) | [project page](https://research.nvidia.com/labs/dvl/projects/vgg-ttt) | "beats linear-time baselines (TTT3R) by 2–2.5× error on DTU/ETH3D/NRGBD" | **1000 images in 54 s**, linear in views | no | code not located | ? | watch |
| **HD-VGGT** (2026) [2603.27222](https://arxiv.org/abs/2603.27222) | not located | high-res branch plus feature modulation for weak-texture tokens; "SOTA" (unverified) | — | no | ? | ? | watch; relevant to textureless surfaces |
| **VGGT-X** (2025) [2509.25191](https://arxiv.org/abs/2509.25191) | [Linketic/VGGT-X](https://github.com/Linketic/VGGT-X) `26d1b95` | "closes the fidelity gap with COLMAP-initialised 3DGS"; SOTA COLMAP-free NVS (unverified numbers) | memory-efficient VGGT to 1000+ images, `--chunk_size 512`, plus XFeat-matched **global alignment** (`--use_ga`); writes COLMAP `sparse/` (PINHOLE) | no | custom LICENSE.txt (not read) | A (roma, kornia, XFeat; pins torch 2.3.1, which must be stripped) | 3/2/2 |
| **VGGT-Long** (ICRA'26) [2507.16443](https://arxiv.org/abs/2507.16443) | [DengKaiCQ/VGGT-Long](https://github.com/DengKaiCQ/VGGT-Long) | KITTI/Waymo "comparable to classic" (no numeric table found) | chunks plus loop plus Sim(3) align; KITTI-00 ~4500 frames on a 4090 | optional via a MapAnything plug-in | inherits VGGT | A (+faiss) | 2/2/1 |
| **VGGT-Align** (MM'26) [2608.15260](https://arxiv.org/abs/2608.15260) | [WZ-CS/VGGT-Align](https://github.com/WZ-CS/VGGT-Align) | ATE up to −32% vs chunked baselines on driving sequences | plug-in over chunk pipelines | no | ? | A? | 1/2/1 |
| **π³ / Pi3** (ICLR'26) [2507.13347](https://arxiv.org/abs/2507.13347) | [yyfz/Pi3](https://github.com/yyfz/Pi3) `9fa3ddb` | VGGT-Ω Table 1: 7-Scenes AUC@3/30 13.3/77.0; ETH3D 35.3/79.6; survey Table 2 best Sintel ATE 0.074 ([2507.14501](https://arxiv.org/abs/2507.14501)) | no official VRAM table | no | code BSD / **Pi3 weights BSD-2** ([HF](https://huggingface.co/yyfz233/Pi3)) | A (pins torch 2.5.1, which must be stripped) | 2/3/2 |
| **Pi3X** (2026) | same repo | "approximate metric scale" and a conv head; no published scale error | — | approximate | **weights CC-BY-NC-4.0** ([HF](https://huggingface.co/yyfz233/Pi3X)); this corrects `p1-fast-recon.md`, which said BSD | A | 2/3/2 |
| **Depth Anything 3** [2511.10647](https://arxiv.org/abs/2511.10647) | [ByteDance-Seed/Depth-Anything-3](https://github.com/ByteDance-Seed/Depth-Anything-3) `3d835ec` | VGGT-Ω Table 1: 7-Scenes 18.7/78.2, ETH3D 46.1/87.0. README AUC3 for NESTED-GIANT: 7-Scenes 29.5, ScanNet++ 89.4 | DA3-Streaming: "ultra-long video with <12 GB" | Nested and Metric variants: yes | Apache: SMALL, BASE, METRIC-LARGE, MONO-LARGE. CC-BY-NC: GIANT(-1.1), NESTED-GIANT-LARGE(-1.1). **DA3-LARGE-1.1:** the HF card says Apache but the README table says CC-BY-NC (conflict) ([HF API](https://huggingface.co/api/models/depth-anything/DA3-LARGE-1.1)) | B (xformers in requirements; README points to a no-xformers workaround, [issue #11](https://github.com/ByteDance-Seed/Depth-Anything-3/issues/11); gsplat only for the GS head) | 3/3/2 |
| **MapAnything** (3DV'26) [2509.13414](https://arxiv.org/abs/2509.13414) | [facebookresearch/map-anything](https://github.com/facebookresearch/map-anything) `3d10cf7` | Table 4: ScanNet multi-view metric, images only: rel 22.23, τ 10.6 (authors say sub-optimal). Strong when given poses, intrinsics or depth | `memory_efficient_inference=True`: "up to 2000 views on 140 GB" | **yes** (explicit scale head, `metric_scaling_factor`) | code Apache; weights `map-anything` CC-BY-NC, **`map-anything-apache` Apache-2.0** ([HF](https://huggingface.co/facebook/map-anything-apache)) | A (core; `[colmap]` extra optional) | 2/2/2 |
| **MASt3R-SfM** (3DV'25) [2409.19152](https://arxiv.org/abs/2409.19152) | [naver/mast3r](https://github.com/naver/mast3r) | ETH3D RRA@5 81.2%, T&T ATE 0.0106 | **200 views ≈ 2.2 h**; 8–30 GB | no | CC BY-NC-SA 4.0 | B (curope has a PyTorch fallback) | 2/0/2 |
| **MASt3R-SLAM** (CVPR'25) [2412.12392](https://arxiv.org/abs/2412.12392) | [rmurai0610/MASt3R-SLAM](https://github.com/rmurai0610/MASt3R-SLAM) | EuRoC ATE 0.041 m | 15 fps | with intrinsics (unverified) | CC BY-NC-SA 4.0 | **C** (lietorch plus custom `.cu`; lietorch ROCm [PR #53](https://github.com/princeton-vl/lietorch/pull/53) abandoned) | cloud |
| **DUSt3R** (CVPR'24) [2312.14132](https://arxiv.org/abs/2312.14132) | [naver/dust3r](https://github.com/naver/dust3r) | superseded | pairwise, O(n²) | no | CC BY-NC-SA | B | 0/0/1 |
| **Fast3R** (CVPR'25) [2501.13928](https://arxiv.org/abs/2501.13928) | [facebookresearch/fast3r](https://github.com/facebookresearch/fast3r) | 1500 images in one pass (accuracy below VGGT in later tables, unverified) | built around FlashAttention-2 | no | custom | B | 1/3/1 |
| **CUT3R** (CVPR'25) [2501.12387](https://arxiv.org/abs/2501.12387) | [CUT3R/CUT3R](https://github.com/CUT3R/CUT3R) `8bc15dc` | ScanNet ATE 0.099, TUM-dyn 0.046. Degrades beyond its training length: 7-Scenes acc 0.238 at 500–1000 frames (Point3R Table 5) | 16.6 fps (KITTI 512×144) | yes (metric training) | CC BY 4.0 (reported, unverified) | B | 1/3/1 |
| **Spann3R** (3DV'25) | [HengyiWang/spann3r](https://github.com/HengyiWang/spann3r) | 7-Scenes acc 0.298 (Point3R Table 1), worse than CUT3R | streaming | no | ? | B | 0/3/0 |
| **Point3R** (NeurIPS'25) [2507.02863](https://arxiv.org/abs/2507.02863) | [YkiWu/Point3R](https://github.com/YkiWu/Point3R) `86fd829` | Table 1: 7-Scenes acc 0.085. Table 5, 500–1000 frames: 7-Scenes 0.071 vs CUT3R 0.238 | ~0.2 s/frame, **5.46 GB peak** | yes (metric training) | MIT (reported, unverified) | B | 2/2/1 |
| **TTT3R** (2025) [2509.26645](https://arxiv.org/abs/2509.26645) | [project](https://rover-xingyu.github.io/TTT3R) | "2× global pose over CUT3R" (unverified) | "thousands of images, 20 fps, 6 GB" (unverified) | as CUT3R | ? | B | 2/3/1 |
| **StreamVGGT and fixes** (InfiniteVGGT [2601.02281](https://arxiv.org/abs/2601.02281), XStreamVGGT, OVGGT, RetrieveVGGT) | e.g. [InfiniteVGGT](https://github.com/AutoLab-SAI-SJTU/InfiniteVGGT) | base StreamVGGT's KV cache blows up within hundreds of frames; the fixes bound the memory (no verified numbers) | streaming | no | ? | A | watch |
| **AMB3R** (2025) [2511.20343](https://arxiv.org/abs/2511.20343) | code "planned", not found | ETH3D SfM RRA@5 **98.2%** vs MASt3R-SfM 81.2%; TUM VO ATE 2.7 cm vs MUSt3R 7.1 cm; RE10K AUC@30 86.3 vs VGGT 81.8 | 4.2 fps VO on a 4090 | yes (scale head) | ? | ? (Point Transformer v3, sparse voxels; likely spconv, which is **C**) | watch |
| **VGGSfM** (CVPR'24) [2312.04563](https://arxiv.org/abs/2312.04563) | [facebookresearch/vggsfm](https://github.com/facebookresearch/vggsfm) | differentiable tracking plus BA; CO3D/IMC SOTA in 2024 | slower than VGGT; superseded by VGGT's own tracker | no | custom | B (pytorch3d for some ops, unverified; pytorch3d has no ROCm build) | 1/1/2 |
| **VGGT-SLAM** (2025) [2505.12549](https://arxiv.org/abs/2505.12549) | [MIT-SPARK/VGGT-SLAM](https://github.com/MIT-SPARK/VGGT-SLAM) `35327ac` | SL(4) submap alignment, SALAD loop closure | per-submap VGGT; CPU GTSAM back end | no | BSD-2 (code) + VGGT weights (NC) | A (GTSAM is CPU) | 3/2/2 |
| **VGGT-SLAM 2.0** [2601.19887](https://arxiv.org/abs/2601.19887) | "code on publication" | TUM −23% pose error vs v1; runs real-time on a Jetson Thor | — | no | paper CC BY 4.0 | A? | watch |
| **VGGT-SLAM++** (CVPR'26 workshop) [2604.06830](https://arxiv.org/abs/2604.06830) | ? | DEM-tile covisibility plus local BA; "SOTA on SLAM benchmarks" | — | no | ? | ? | watch |
| **HunyuanWorld-Mirror** [2510.10726](https://arxiv.org/abs/2510.10726) | [Tencent-Hunyuan/HunyuanWorld-Mirror](https://github.com/Tencent-Hunyuan/HunyuanWorld-Mirror) | takes optional pose, intrinsics and depth priors; "SOTA" (unverified) | one pass | via priors | Tencent licence (unverified) | A? (gsplat head) | 2/3/2 |
| **GRF-Recon** (ECCV'26 spotlight) [2609.20012](https://arxiv.org/abs/2609.20012) | not located | long-sequence ray-field optimisation; "competitive with SLAM" | — | ? | ? | ? | watch |

### 1.2 What matters for us

- **Pose accuracy is the root cause of the preview's scale error.** VGGT-Ω more than triples
  7-Scenes AUC@3 over VGGT (Table 1 above).
  - That table uses 10-frame samples. Nobody has published 100–300-frame indoor numbers for it,
    so re-measure on the kitchen.
  - Swap VGGT for VGGT-Ω inside the existing `pipeline/vggt.py` flow. The API has the same shape:
    `pose_enc`, `depth` and `depth_conf`.
  - Blocker: the weights are gated and non-commercial. The ungated fallback is VGGT-1B through
    vggt-low-vram at all 175 frames.
- **Frame count sets both coverage and pose conditioning.** At 48 frames, each frame is 1.8 s of
  flight apart.
  - vggt-low-vram's 311-frame, 11 GB point means one pass can cover the whole clip at 2–3.5 fps.
  - VGGT-Ω needs about 13.4 GB at 100 frames at 624×416. Estimate about 150 frames on 16 GB at
    512×288, since 16:9 gives fewer tokens than 3:2 (unverified).
- **Feed-forward then refinement.**
  - VGGT-X's global alignment matches XFeat features across frames and optimises poses and points
    jointly.
  - That is the cheapest published way to push VGGT poses toward COLMAP quality, and it already
    writes COLMAP `sparse/`, so the OpenMVS and 2DGS tails consume it unchanged.
  - MASt3R-SfM is too slow for us (2.2 h per 200 views).
- **Long-sequence and streaming models** (CUT3R, Point3R, TTT3R, the StreamVGGT family,
  VGGT-Long, DA3-Streaming, VGGT-SLAM) matter for the aerial-orbit future, where there are
  thousands of frames.
  - For an 87 s clip sampled at 2–4 fps, a single offline pass (vggt-low-vram or VGGT-Ω) is
    simpler and more accurate: offline models beat streaming ones on every table we saw.
- **Independent head-to-head sources:**
  - VGGT-Ω Tables 1–2 (above);
  - the survey [2507.14501](https://arxiv.org/abs/2507.14501), Tables 2–4;
  - the [SpatialBench](https://github.com/Ropedia/SpatialBench) leaderboard, which covers 50+
    models on an A100-80G (not read in detail);
  - the aerial evaluation [2507.14798](https://arxiv.org/abs/2507.14798), where VGGT beats
    DUSt3R and MASt3R on UseGeo aerial blocks at <10 images. There, pose reliability *declines*
    with more images and higher resolution, and the authors conclude these models cannot fully
    replace SfM plus MVS yet.

## 2. Learned or fast MVS, depth nets and fusion (family 2)

### 2.1 Learned MVS

| Method | Code | Benchmarks | Speed / VRAM | Licence | ROCm | Fit |
|---|---|---|---|---|---|---|
| **MVSFormer++** (ICLR'24) [2401.11673](https://arxiv.org/abs/2401.11673) | [maybeLx/MVSFormerPlusPlus](https://github.com/maybeLx/MVSFormerPlusPlus) | DTU overall 0.2805 mm; T&T intermediate 67.03, advanced 41.70 | not published; ~1 s/view class at 1–2 MP (unverified) | Apache-2.0 | A (SDPA; no custom ops found) | trained on DTU/BlendedMVS, no low-light indoor validation. Needs COLMAP → MVSNet `cams/pair.txt` glue |
| **GeoMVSNet** (CVPR'23) | [doubleZ0108/GeoMVSNet](https://github.com/doubleZ0108/GeoMVSNet) | T&T advanced #1 at release | — | Apache-2.0 | A | same caveat; the authors note `grid_sample` version sensitivity |
| **GoMVS** (CVPR'24) | [Wuuu3511/GoMVS](https://github.com/Wuuu3511/GoMVS) | best DTU completeness; normal-guided propagation helps low texture | — | MIT | A | same |
| **MVSAnywhere** (CVPR'25) [2503.22430](https://arxiv.org/abs/2503.22430) | [nianticlabs/mvsanywhere](https://github.com/nianticlabs/mvsanywhere) | zero-shot ScanNet rel 3.7, τ 62.9 (also the MapAnything Table 4 baseline) | ~0.12 s at 640×480 | **Niantic non-commercial** | A | best indoor generalisation of the MVS nets; mixes mono and multi-view cues. Research-only |
| **Murre** (CVPR'25) | [zju3dv/Murre](https://github.com/zju3dv/Murre) | SfM sparse points guide a diffusion model to dense metric depth, then TSDF | 10 denoise steps per frame (slow class) | **"Project Registration Licence": free for research; project use needs a Google-Form registration** ([LICENSE](https://github.com/zju3dv/Murre/blob/main/LICENSE)) | A (diffusers) | exactly our "densify from sparse SfM" pattern, but slow and the licence is awkward |

### 2.2 Depth nets used for densification

| Model | Indoor metric accuracy (source) | Licence (verified) | ROCm | Note |
|---|---|---|---|---|
| **MoGe-2** [2507.02546](https://arxiv.org/abs/2507.02546) | metric point maps, sharp edges; indoor-calibrated (it was 3–10× off on aerial footage, `p1-gpu-rocm.md`) | MIT ([LICENSE](https://github.com/microsoft/MoGe/blob/main/LICENSE)) | **measured on hfbox:** 3 GB, 0.1–0.15 s/frame | indoor is its home turf; the first choice to test on the kitchen |
| **Metric3D v2** [2404.15506](https://arxiv.org/abs/2404.15506) | ranks 1st on several zero-shot metric-depth and normal benchmarks (README) | **BSD-2** ([LICENSE](https://github.com/YvanYin/Metric3D/blob/main/LICENSE)) | A (mmcv-lite deps; unverified on ROCm) | also predicts normals, which help 2DGS/PGSR priors |
| **DA3-Metric-Large** | ETH3D δ1 0.917, AbsRel 0.104; SUN-RGBD AbsRel 0.105 (DA3 paper, via the survey agent; unverified table name) | **Apache-2.0** ([HF](https://huggingface.co/depth-anything/DA3METRIC-LARGE)) | B (xformers) | metres = `focal * out / 300` (README) |
| **UniDepth v2** | NYUv2 δ1 98.8 (README) | CC BY-NC 4.0 | A | research-only |
| **Depth Pro** (Apple) | 2.25 MP in 0.3 s; sharp boundaries | Apple research licence (non-commercial) | A | research-only |
| **PromptDA** (CVPR'25) | SOTA on ARKitScenes and ScanNet++ **with** a LiDAR prompt | **Apache-2.0** ([LICENSE](https://github.com/DepthAnything/PromptDA/blob/main/LICENSE)) | A | feed projected COLMAP or VGGT sparse depth as the "prompt" (sparser than LiDAR; unverified quality) |
| **Video Depth Anything** | temporally consistent; Small model 6.8 GB, 7.5 ms/frame on an A100 | Small Apache; Base/Large NC | A | good for flicker-free per-frame depth over the whole clip |
| **DepthCrafter** | video diffusion | Tencent, non-commercial | A (VRAM-heavy) | skip |
| **DepthSplat** | 12-view feed-forward GS in 0.6 s on an A100 | MIT | needs a GS rasterizer (gsplat port, B) | object/scene NVS, not our mesh path |
| **Marigold / Lotus** | diffusion mono-depth, affine-invariant | Apache (code), RAIL++ (SD weights) | A | slow; no metric output |

### 2.3 Fusion

- **Open3D TSDF on the CPU is the right default.** We already measured 1.7 s in the preview.
  - At 1 cm voxels a 5×4×3 m room is ~60 M voxels, but `ScalableTSDFVolume` hashes only the
    surface band.
  - Estimate: seconds to low tens of seconds for 175 frames at 1–2 MP depth (unverified).
  - Open3D's HIP GPU back end ([PR #7509](https://github.com/isl-org/Open3D/pull/7509)) was
    tested on an RX 9070 XT but is **unmerged**.
- **[VDBFusion](https://github.com/PRBonn/vdbfusion)** (MIT) is a sparse OpenVDB TSDF on the CPU,
  and a drop-in if Open3D's memory blows up at 5 mm voxels.
- **nvblox** is CUDA only.
- **Fusion quality levers, all cheap:**
  - weight each depth sample by the network confidence (VGGT `depth_conf`, MoGe mask);
  - weight by 1/depth² and truncate at grazing angles;
  - use per-frame **scale-and-shift alignment** (RANSAC or Huber) to SfM sparse depth before
    fusion.

  These follow the Murre and SimpleRecon pattern. Without them, mono-depth fusion gives the
  "onion-skin" double surfaces seen on our preview.

## 3. Gaussian or radiance fields to mesh (family 3)

Sources for the numbers below:
- DTU Chamfer (mm, lower is better) and T&T F1 (higher is better) come from each paper's own
  tables. DTU scripts vary by about ±0.02 mm between papers.
- Training times are the paper's own, on the stated GPU.
- "ROCm" follows the §1.1 legend, applied to the rasterizer each method needs.

### 3.1 Master table

| Method (venue) | Code | DTU CD | T&T F1 | Train time (GPU) | Mesh output | Texture | Licence | ROCm |
|---|---|---|---|---|---|---|---|---|
| **2DGS** (SIGGRAPH'24) [2403.17888](https://arxiv.org/abs/2403.17888) | [hbb1/2d-gaussian-splatting](https://github.com/hbb1/2d-gaussian-splatting) `f3e3b9f` | 0.80 (30k iterations); 0.83 (15k) | 0.32 | 10.9 min at 30k / 5.5 min at 15k (3090) | TSDF of rendered depth; mid-poly | vertex colour | Inria/MPII non-commercial | **A via port** ([Painter3000 2DGS](https://github.com/Painter3000/amd-2dgs-rocm72-gfx1201)); also gsplat's Apache `rasterization_2dgs` on the [gfx1201 gsplat port](https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201) |
| **GOF** (SIGGRAPH Asia'24) [2404.10772](https://arxiv.org/abs/2404.10772) | [autonomousvision/gaussian-opacity-fields](https://github.com/autonomousvision/gaussian-opacity-fields) | 0.74 | 0.46 | ~30 min DTU / ~1 h unbounded (A100) | marching tetrahedra; heavy (~33 M tris on a T&T scene, per the MILo comparison) | vertex colour | Inria non-commercial | **C** (own rasterizer plus Kaolin) |
| **PGSR** (TVCG'24) [2406.06521](https://arxiv.org/abs/2406.06521) | [zju3dv/PGSR](https://github.com/zju3dv/PGSR) `de24f1a` | **0.47–0.52** | **0.52** | 0.5 h DTU / 0.75 h T&T (4090) | TSDF of unbiased planar depth; multi-view photometric consistency, which helps weak texture | vertex colour; its exposure-compensation option is unverified | ZJU research-only | **C** |
| **RaDe-GS** (2024) [2406.01467](https://arxiv.org/abs/2406.01467) | [HKUST-SAIL/RaDe-GS](https://github.com/HKUST-SAIL/RaDe-GS) | 0.68 | 0.40 | **5–20 min DTU**, 11.5 min T&T (H800) | TSDF or marching tetrahedra | vertex colour | likely Inria-derived (unverified) | **C** |
| **MILo** (SIGGRAPH Asia'25) [2506.24096](https://arxiv.org/abs/2506.24096) | [Anttwo/MILo](https://github.com/Anttwo/MILo) `0c38aa6` | 0.68 | 0.49 | 25 min DTU / 50 min unbounded (4090) | **mesh-in-the-loop Delaunay; ~10× fewer vertices than GOF for the same F1; a "very low res" preset of ~250k vertices** matches our budget | vertex colour only (no UV) | Inria non-commercial | **C** (3 rasterizer variants, simple-knn, fused-ssim, nvdiffrast) |
| **SuGaR** (CVPR'24) [2311.12775](https://arxiv.org/abs/2311.12775) | [Anttwo/SuGaR](https://github.com/Anttwo/SuGaR) | 1.33 | 0.19 | ~1 h | Poisson; `--low_poly` gives 200k vertices | **UV atlas** via nvdiffrast (`--export_obj`) | Inria non-commercial | **C** (nvdiffrast, pytorch3d) |
| **Trim2DGS / Trim3DGS** [2406.07499](https://arxiv.org/abs/2406.07499) | [YuxueYang1204/TrimGS](https://github.com/YuxueYang1204/TrimGS) | small gain over its base | — | base plus pruning | as the base | as the base | no LICENSE file | as the base (Trim2DGS could ride the 2DGS port) |
| **DN-Splatter / AGS-Mesh** (WACV'25) [2403.17822](https://arxiv.org/abs/2403.17822) | [maturk/dn-splatter](https://github.com/maturk/dn-splatter) | indoor only | MuSHRoom F 0.92, ScanNet++ F 0.77 | ~37 min at 30k (4090) | Poisson, Open3D TSDF or octree iso-surface (AGS) | vertex colour | **Apache-2.0** | **B**: built on gsplat but pins `gsplat==1.0.0` and `nerfstudio==1.1.3`, while the ROCm port is 1.5.3 (API drift risk). Its mono-depth alignment script is reusable as-is |
| **GS2Mesh** (ECCV'24) [2404.01810](https://arxiv.org/abs/2404.01810) | [yanivw12/gs2mesh](https://github.com/yanivw12/gs2mesh) | — | — | 3DGS plus a stereo net on rendered pairs | TSDF of stereo depth; smooth | vertex colour | unverified | C |
| **QGS** (ICCV'25) [2411.16392](https://arxiv.org/abs/2411.16392) | [will-zzy/QGS](https://github.com/will-zzy/QGS) | "−33% vs 2DGS, −27% vs GOF" (about 0.54, derived) | — | — | TSDF | vertex colour | custom | C |
| **SOF** (SIGGRAPH Asia'25) [2506.19139](https://arxiv.org/abs/2506.19139) | not released | "> 3× faster than GOF-class, more accurate" | — | — | parallel marching tetrahedra | — | — | — |
| **OMeGa** (2025) [2509.24308](https://arxiv.org/abs/2509.24308) | ? | indoor Chamfer −47.3% vs 2DGS | — | — | explicit mesh jointly optimised with 2DGS | — | ? | C (unverified) |
| **Mesh Splatting** (2026) [2601.21400](https://arxiv.org/abs/2601.21400) | ? | "accurate" | — | **~20 min** | end-to-end mesh (softened layers) | — | ? | ? |
| **CoMe (confidence mesh)** (2026) [2603.24725](https://arxiv.org/abs/2603.24725) | [project page](https://r4dl.github.io/CoMe/) | "SOTA unbounded, efficient" | — | — | — | — | ? | ? |
| **TopoSurfel** (2026) [2608.20687](https://arxiv.org/abs/2608.20687) | [Fan-Treasure/TopoSurfel](https://github.com/Fan-Treasure/TopoSurfel) | "competitive"; fills holes and floaters in textureless regions | — | — | proxy mesh from surfels | — | ? | C (unverified) |
| **Triangle Splatting / +** (2025) [2505.19175](https://arxiv.org/abs/2505.19175), [2509.25122](https://arxiv.org/abs/2509.25122) | [project page](https://trianglesplatting.github.io/) | — | — | — | triangles are the primitive; opaque in `+` | per-triangle colour | — | C |
| **AnyGS2Mesh** (Sep 2026) [2609.03304](https://arxiv.org/abs/2609.03304) | "on acceptance" | "SOTA, near real time" | — | feed-forward | GS-guided depth, then TSDF and marching cubes | — | — | — |
| **Neuralangelo** (CVPR'23) | [NVlabs/neuralangelo](https://github.com/NVlabs/neuralangelo) | 0.61 | 0.50 | >24 h | marching cubes | vertex colour | NVIDIA non-commercial | C (tiny-cuda-nn) |
| **NeuS2** (ICCV'23) | [19reborn/NeuS2](https://github.com/19reborn/NeuS2) | ~0.70 (unverified) | — | minutes per object | marching cubes | vertex colour | NVIDIA non-commercial | C |

### 3.2 What this means for us

- **No splat-to-mesh method ships the Quest format** (≈200k tris plus one UV atlas).
  - All of them except SuGaR stop at a vertex-coloured mesh. SuGaR has the worst geometry.
  - So the proven tail stays the same: decimate to 200k, then **OpenMVS `TextureMesh -m
    <external mesh>`**, which bakes an atlas from the real photos. We already use this for the
    VGGT preview.
  - The splat method only has to supply **better geometry** on textureless surfaces and thin
    detail.
- **On our AMD card today, 2DGS is the one surface method that runs**, through the gfx1201 ports.
  - Geometry per minute on CUDA ranks PGSR > RaDe-GS ≈ MILo > GOF > 2DGS. 2DGS is last on T&T F1
    (0.32), but indoor scenes behave differently.
  - The published fixes for textureless indoor surfaces are mono depth and normal priors: the
    DN-Splatter and AGS-Mesh losses, OMeGa, and 2D-SuGaR ([2605.00569](https://arxiv.org/abs/2605.00569)).
  - Those are losses of a few lines on top of gsplat's `simple_trainer_2dgs.py`, which already
    has `depth_loss` (from SfM points), `normal_loss` and `dist_loss`.
- **For a CUDA cloud box:**
  - PGSR is the geometry-accuracy pick.
  - MILo gives the cleanest low-poly mesh directly, with the fewest vertices for a given accuracy.
  - GOF is the one for the future aerial orbits (unbounded scenes).
- **None of these fit the 1-minute preview.** The fastest, RaDe-GS, takes 5–20 min on an H800.
  They are candidates for the full-quality stage only.

## 4. Mesh texturing and atlas quality (family 4)

| Tool | Licence | View selection and seams | Exposure handling | Note |
|---|---|---|---|---|
| **mvs-texturing / texrecon** (Waechter et al., ECCV'14) [github](https://github.com/nmoehrle/mvs-texturing) `f337429` | BSD-3 | MRF view selection plus global (vertex-colour LSQ) and local (Poisson) seam leveling | none beyond seam leveling | the reference algorithm OpenMVS follows |
| **OpenMVS `TextureMesh`** (ours) | AGPL-3.0 | the same family | none beyond seam leveling | Options in the current `develop` source ([TextureMesh.cpp](https://github.com/cdcseacave/openMVS/blob/develop/apps/TextureMesh/TextureMesh.cpp)) we have not tuned yet: `--sharpness-weight`, `--outlier-threshold` (rejects outlier views per face, which helps moving shadows and noise), `--cost-smoothness-ratio`, `--virtual-face-images`, `--min-resolution`, `--max-texture-size`, `--resolution-level` |
| **COLMAP 4.x texture mapping** | BSD | added in 4.0.0 (`p1-colmap-realityscan.md` §1.1) | ? | untested by us. The texturing survey agent said COLMAP has no texturing; that is wrong for 4.x |
| **AliceVision / Meshroom Texturing** | MPL-2.0 | **multi-band blending** | pipeline-level exposure compensation (unverified node name) | the only open texturer with multi-band blending, which separates ISO noise from seam-scale colour steps |
| **SuGaR / NeRF2Mesh / nvdiffrec bakes** | Inria NC / MIT / NVIDIA NC | differentiable re-render bake onto UVs (xatlas) | implicit | all need nvdiffrast, which is **C** on ROCm |
| **xatlas** | MIT | UV unwrap | — | already in our toolchain (`p1-toolchain.md` §2.5) |

**Low-light and varying exposure.** No paper targets this for mesh texturing directly. Three
published ingredients transfer, cheapest first:

1. **Per-frame photometric normalisation before texturing.**
   - Estimate a per-image affine colour transform against a reference by fitting colours of the
     same SfM 3D points across frames.
   - Apply its inverse to the frames. This is classic gain compensation.
   - OpenMVS then only has to level seams, not exposure steps.
2. **A learned per-image appearance model**, when splats are trained anyway:
   - gsplat's `app_opt` (per-image appearance embedding) or `use_bilateral_grid` (per-image
     bilateral-grid ISP; Wang et al., SIGGRAPH'24, "Bilateral Guided Radiance Field Processing").
   - Both exist in the gfx1201 port's `examples/simple_trainer.py`.
   - Idea (not published as a tool): render the *canonical* (grid-free) splat colours from each
     training camera, then texture from those renders instead of the raw frames. That gives
     exposure-consistent, denoised texture sources.
   - SplatFacto-W ([2407.12306](https://arxiv.org/abs/2407.12306)) reports +5.3 dB PSNR from
     per-image appearance embeddings on varying-exposure photo collections.
3. **Denoise the frames before texturing.** At ISO 2000 and 1/30 s, sensor noise shows up in the
   atlas. Any video denoiser (temporal) works, and neighbouring frames are sharper references.
   This is our suggestion, not a paper.

**Sharpness per triangle budget.** Texture detail depends on the source views, not the triangle
count, as long as the atlas has enough texels.
- The standard game-asset route is decimate-then-bake: bake a normal map from the high-poly mesh
  onto the 200k mesh with Blender or xNormal.
- It keeps geometric detail visually. OpenMVS does not bake normal maps.
- It is an optional, low-priority add-on. The Quest shader cost of normal maps is small.

## 5. Indoor room-scale systems (family 5)

| System | Loop closure | Metric | Licence | ROCm | Verdict for kitchen-0095 |
|---|---|---|---|---|---|
| **DROID-SLAM** (NeurIPS'21) [github](https://github.com/princeton-vl/DROID-SLAM) | no (base) | mono: no | BSD-3 | **C** (lietorch plus a CUDA correlation volume) | cloud only |
| **GO-SLAM** (ICCV'23) [github](https://github.com/youmi-zym/GO-SLAM) | yes | mono: no | Apache-2.0 | **C** (DROID back end, tiny-cuda-nn) | cloud only |
| **GlORIE-SLAM** [github](https://github.com/zhangganlin/GlORIE-SLAM) | partial | via mono depth | Apache-2.0 | **C** (DROID) | cloud only |
| **Splat-SLAM** (Google) [github](https://github.com/google-research/Splat-SLAM) | yes, plus global BA | via mono depth | Apache-2.0 | **C** (DROID plus the 3DGS rasterizer) | cloud only |
| **HI-SLAM2** (T-RO'25) [2411.17982](https://arxiv.org/abs/2411.17982) | yes (PGBA plus Gaussian deformation) | via mono priors | BSD-3 | **C** (DROID-style, unverified) | cloud; benchmarks on ScanNet/ScanNet++/Replica |
| **MonoGS / Photo-SLAM** | weak / ORB-SLAM3 | weak | ? / GPL-3.0 | **C** | skip |
| **MASt3R-SLAM** | yes | with intrinsics | CC BY-NC-SA | **C** | cloud only |
| **VGGT-SLAM (1/2/++)** | yes (SALAD plus GTSAM) | no | BSD-2 (+NC weights) | **A** | the only loop-closing SLAM that is ROCm-plausible |
| **VGGT-GS SLAM** (Sep 2026) [2609.19628](https://arxiv.org/abs/2609.19628) | yes (Gaussian-native alignment) | camera-anchored scale refinement | ? | needs a GS rasterizer | watch |
| **NeuralRecon / SimpleRecon / FineRecon / VisFusion** | no | yes (posed) | Apache / Niantic NC / ? / ? | sparse conv (C) for NeuralRecon; SimpleRecon is A | 2021–23 generation; MVSAnywhere supersedes SimpleRecon |
| **MonoSDF / NICER-SLAM** | — | — | MIT / ? | **C** (tiny-cuda-nn) | slow per-scene optimisation |

**Take-away.** For one 87 s pass, COLMAP's global BA already acts as loop closure. SLAM buys
online tracking that we do not need. The indoor-specific lessons worth keeping are about the
*priors*:
- the mono depth and normal supervision in MonoSDF, DN-Splatter and HI-SLAM2 is what fixes
  textureless walls;
- OMeGa ([2509.24308](https://arxiv.org/abs/2509.24308)) reports −47% Chamfer over 2DGS on
  textureless indoor scenes by adding mesh constraints and mono normals.

On the ScanNet++ NVS leaderboard, NeRF-family entries are on top (Zip-NeRF, PIM-NeRF) and
SVRaster is the top 2025 entry ([leaderboard](https://scannetpp.mlsg.cit.tum.de/scannetpp/benchmark/nvs)).
We found no geometry leaderboard.

## 6. Metric scale from monocular video plus weak telemetry (family 6)

**Literature.**
- There is **no published method for barometer or altitude-based scale on monocular video.**
  arXiv API queries for barometer, altimeter or height combined with monocular scale returned
  only automotive ground-plane work.
- The closest published work:
  - ground-plane camera-height scale for VO, Zhou et al. ([1903.00912](https://arxiv.org/abs/1903.00912));
  - plane-geometry scale recovery ([2101.05995](https://arxiv.org/abs/2101.05995));
  - object-size Bayesian scale ([1711.02768](https://arxiv.org/abs/1711.02768));
  - wheel-odometry-driven recursive Bayesian rescaling of a depth foundation model (PTC-Depth,
    CVPR'26, [2604.01791](https://arxiv.org/abs/2604.01791));
  - LiDAR-VGGT ([2511.01186](https://arxiv.org/abs/2511.01186)) for sensor-anchored metric VGGT.
- Height-times-flow fusion lives inside flight-controller EKFs (PX4/ArduPilot), not in papers.

**Error budget for our caption** (our own arithmetic, not a citation):
- H is quantised to 0.1 m, so σ ≈ 0.029 m per sample.
- Over a 0.9 m span, a well-spread LSQ fit of ~165 samples gives about 2–5% from quantisation
  alone.
- A barometric or vision-height bias (ground effect, the end-of-clip landing) cannot be told apart
  from scale over such a short span. Realistically expect **5–15%**, and worse with a noisy
  up-axis.
- That matches what we saw:
  - The full stage (COLMAP poses) fit with 0.05 m residual, which is plausible.
  - The preview (VGGT on 48 frames) was 45% off, which is pose noise, not height noise.

**Recommended estimator:**
1. **Per-cue estimates**, each with a variance:
   - (a) altitude fit on *good* poses (COLMAP, VGGT-Ω, or ≥150-frame VGGT);
   - (b) metric-depth median ratio: MoGe-2, Metric3D v2 or DA3-Metric depth against rendered
     reconstruction depth, over ≥20 frames, using `pipeline/experiments/metric_scale.py`, which
     already exists;
   - (c) known dimensions: 0.91 m counter height, 0.60 m counter depth, 2.03 m door;
   - (d) odometry: ∫H.S, as a cross-check only.
2. **Combine** with an inverse-variance Huber or RANSAC fit.
   - Indoors, (b) and (c) should dominate.
   - Outdoors with GPS, use COLMAP pose priors (`p1-colmap-realityscan.md` §1.2.3), and (a)
     becomes strong at 30–100 m spans.
3. **Preview: publish at the fused scale, or unscaled.** Never fit the height alone on 48 noisy
   frames.

Other scale routes:
- MapAnything accepts COLMAP poses and intrinsics as *inputs* with `is_metric_scale=False` and
  returns `metric_scaling_factor` ([README](https://github.com/facebookresearch/map-anything)).
  That is a learned metric cue conditioned on good geometry. Its image-only ScanNet scale is weak
  (Table 4), so treat it as one more cue.
- DA3-Nested and Pi3X give metric outputs, but neither publishes an indoor scale error (unverified).

## 7. Methods we checked and dropped

Each is in one line so later agents need not re-check them:

- **MASt3R-SfM:** 2.2 h per 200 views.
- **DUSt3R:** pairwise and superseded.
- **Spann3R:** weak accuracy.
- **DepthCrafter:** non-commercial and VRAM-heavy.
- **Depth Pro:** licence.
- **MonoSDF and NICER-SLAM:** tiny-cuda-nn, and slow.
- **Photo-SLAM:** GPL plus an old libtorch.
- **SuGaR:** superseded by 2DGS/GOF in every comparison table, and Inria non-commercial
  (see §3).
- **FastGS/DashGaussian:** these are 3DGS trainers without a mesh (`p1-fast-recon.md` §3).

## 8. ROCm feasibility on gfx1201 (ROCm 7.2, torch 2.14)

| Library | Status | Source |
|---|---|---|
| gsplat upstream | HIP PRs open, not merged (#1038 targets the RX 9070) | [gsplat PRs](https://github.com/nerfstudio-project/gsplat/pulls?q=ROCm) |
| gsplat, AMD `amd_gsplat` wheel | MI300X / Wave64 only; **not for gfx1201** | [AMD-Ecosystem/gsplat-amd](https://github.com/AMD-Ecosystem/gsplat-amd) |
| **gsplat gfx1201 port** | **validated on an R9700 (gfx1201):** 3DGS and 2DGS forward and backward; RGB, D, ED and RGB+D modes; packed and unpacked. **Not validated:** SH rendering, antialiased mode, distortion | [Painter3000/amd-gsplat-rocm72-gfx1201](https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201) `e2f3319`, README_ROCM.md |
| **2DGS, original repo** (diff-surfel-rasterization, simple-knn) | **validated on an R9700:** trains 1200 iterations; TSDF mesh at 256 and 512 | [Painter3000/amd-2dgs-rocm72-gfx1201](https://github.com/Painter3000/amd-2dgs-rocm72-gfx1201) `914838b` |
| diff-gaussian-rasterization, simple-knn, fused-ssim | community gfx1201 ports | Painter3000 series; [bjoernellens1/simple-knn-rocm](https://github.com/bjoernellens1/simple-knn-rocm) |
| GOF, RaDe-GS, PGSR rasterizers | **no port** | — |
| tiny-cuda-nn, nerfacc, pytorch3d, torch_scatter, lietorch, MinkowskiEngine, spconv, torchsparse, Kaolin, warp | **no port** (pytorch3d [PR #2039](https://github.com/facebookresearch/pytorch3d/pull/2039) and lietorch [PR #53](https://github.com/princeton-vl/lietorch/pull/53) were both abandoned) | respective repos |
| nvdiffrast | unmerged community patch only ([issue #240](https://github.com/NVlabs/nvdiffrast/issues/240)) | — |
| xformers | experimental ROCm build; gfx1201 unverified | [xformers](https://github.com/facebookresearch/xformers) |
| flash-attention | README lists RDNA3/4 for the CK and Triton back ends (unverified on gfx1201). PyTorch SDPA is already the path used for VGGT | [Dao-AILab/flash-attention](https://github.com/Dao-AILab/flash-attention) |
| COLMAP `patch_match_stereo` HIP | merged; tested on gfx1100 and gfx90a; **gfx1201 untested** | [PR #4420](https://github.com/colmap/colmap/pull/4420) |
| COLMAP GPU SIFT HIP | open PR | [PR #4635](https://github.com/colmap/colmap/pull/4635) |
| Open3D GPU (HIP) | unmerged; tested on an RX 9070 XT under Windows | [PR #7509](https://github.com/isl-org/Open3D/pull/7509) |

gfx1201 PyTorch caveats (from the ROCm survey agent):
- The CK SDPA path used to be blocked for gfx12 ([pytorch#188113](https://github.com/pytorch/pytorch/issues/188113)).
  VGGT runs fine on our build, so either the fix landed or SDPA falls back.
- Inductor has open gfx1201 bugs ([#195823](https://github.com/pytorch/pytorch/issues/195823)).
- `expandable_segments` has NaN and hang bugs ([#195202](https://github.com/pytorch/pytorch/issues/195202),
  [#192259](https://github.com/pytorch/pytorch/issues/192259)).
- So: check `torch.compile` output against eager mode, and leave `expandable_segments` off.

## 9. Ranked shortlist to try on hfbox

Conventions for these recipes:
- `W` is the kitchen's full-stage work dir on hfbox, written by `pipeline/run.py` and
  `sfm.py`/`mvs.py`. It contains `frames_selected/` (175 frames), `sparse/0` (COLMAP),
  `dense/images` and `dense/sparse` (undistorted), and `scene.mvs`.
- ROCm torch is `/opt/venv` (2.14+rocm7.2).
- Rules carried over from `p1-gpu-rocm.md`:
  - Install research repos with `--no-deps`, or strip `torch`/`torchvision` pins from their
    requirements, so pip never replaces the ROCm torch.
  - Set `TORCH_HOME=/workspace/.cache/torch` and `HF_HOME=/workspace/.cache/hf`.
  - Use `torch.compile` only after an eager-vs-compiled check.
  - Leave `PYTORCH_HIP_ALLOC_CONF=expandable_segments` unset (§8).
- **All times and VRAM on hfbox below are estimates**, scaled from our measured VGGT numbers (48
  frames in 6.8 s) and the cited hardware. None were measured.

### 9.1 Ranked shortlist

| Rank | Method | Goal it serves | Box | Est. time on kitchen | Est. VRAM | Main risk |
|---|---|---|---|---|---|---|
| 1 | **Metric mono-depth, SfM-aligned, TSDF** (MoGe-2; then Metric3D v2 / DA3-Metric-Large / PromptDA) replacing `DensifyPointCloud` | speed (27 min to ~2 min); coverage of textureless surfaces | AMD | 1.5–3 min | 3–6 GB | per-frame depth inconsistency gives double surfaces; depth edge bleeding |
| 2 | **Scale-cue ensemble** (metric-depth ratio + altitude-on-good-poses + known dimension + MapAnything-apache given COLMAP poses) | metric scale (the 45% gap) | AMD | 1–3 min | 3–10 GB | all cues share one bias (e.g. MoGe indoor calibration) |
| 3 | **VGGT-1B via vggt-low-vram, all 175 frames** in one pass | preview coverage; pose conditioning for the scale fit | AMD | 45–120 s | ~7 GB | `torch.compile` on gfx1201; slower than the 1-minute budget, so it suits an intermediate "r1.5" stage |
| 4 | **VGGT-Ω 1B-512**, 100–150 frames | preview pose accuracy, hence scale and alignment | AMD | 30–90 s | 13–16 GB | **weights gated behind an HF access request** (needs the user's HF account); FAIR non-commercial |
| 5 | **2DGS (+mono depth/normal loss) on the gfx1201 ports**, then TSDF, then decimate, then TextureMesh | detail and textureless geometry in the full stage | AMD | 10–25 min (15k iterations) | 6–12 GB | **needs a HIP compiler, which hfbox likely lacks (pre-flight in §9.2)**; single-author ports; 2DGS is weakest on T&T F1; we must add the prior losses ourselves |
| 6 | **VGGT-X `--use_ga`** as a fast COLMAP replacement | speed (SfM 113 s); fixes the two-component registration split | AMD | 2–5 min | 8–14 GB | pins torch 2.3.1 and pycolmap 3.10 (use the existing `/workspace/envs/vggt` trick); PINHOLE only |
| 7 | **COLMAP 4.2 HIP `patch_match_stereo`** (source build, gfx1201) | speed with classic-MVS accuracy on textured surfaces | AMD | build 30–45 min once; stereo 5–15 min | 4–8 GB | needs a HIP compiler (§9.2 pre-flight); gfx1201 untested (PR tested gfx1100); the fusion-to-OpenMVS seam (`p1-colmap-realityscan.md` §1.3) |
| 8 | **PGSR** | best geometry accuracy per minute | CUDA (L40S/4090) | 45–60 min | 16–24 GB | research-only licence; vertex colour only, so bake with TextureMesh |
| 9 | **MILo** (low-res preset) | clean low-poly mesh at our budget, with accuracy | CUDA | ~50 min | 16–24 GB | Inria non-commercial; five CUDA extensions; no UV |
| 10 | **Depth Anything 3** (DA3-BASE Apache, or NESTED-GIANT-LARGE-1.1 NC), with DA3-Streaming for the whole clip | an all-in-one pose, depth and metric alternative to VGGT | AMD | 1–3 min | <12 GB (streaming) | xformers dependency on ROCm; the Apache variants are the weaker models |

Runners-up, if the top 10 disappoint:
- RaDe-GS (CUDA, the fastest accurate splat mesh);
- GOF (CUDA, for aerial orbits);
- DN-Splatter/AGS-Mesh (on the ROCm gsplat port, with gsplat version drift);
- VGGT-SLAM (for long aerial clips);
- MVSAnywhere (research licence).

### 9.2 Try now on AMD (hfbox)

**R1: metric mono-depth densification** (rank 1). Repos:
- MoGe `74fbce0`: already installed in `/workspace/envs/depth` (`p1-gpu-rocm.md` §2.1).
- Metric3D `eb5b6fa`.
- DA3 `3d835ec` (`DA3METRIC-LARGE`).
- PromptDA `9161547`.

```bash
# inputs: $W/dense/images (undistorted), $W/dense/sparse (COLMAP, PINHOLE after undistort)
/workspace/envs/depth/bin/python densify_mono.py --colmap $W/dense --out $W/mono \
    --model moge2 --long-edge 1280 --voxel 0.01 --trunc 0.04 --depth-max 4.0
#   per frame:  D = moge2(img)              # metric; 3 GB, ~0.15 s/frame measured
#               d_sfm = project(points3D visible in frame)          # typically 50-300 pts/frame
#               (s, t) = huber_fit(D[uv], d_sfm)  (or scale-only)   # drop frames with <20 pts
#               D' = s*D + t; mask = moge_mask & conf & grazing<75deg & depth-edge-free
#               open3d ScalableTSDFVolume.integrate(D', K, T_wc)    # CPU
#   mesh = extract_triangle_mesh() -> keep largest components -> $W/mono/mesh.ply
# then reuse the existing tail unchanged:
TextureMesh $W/scene.mvs -m $W/mono/mesh.ply -o $W/mono/textured.mvs --export-type glb
```

About the script:
- `densify_mono.py` does not exist yet. It is about 120 lines on top of `pipeline/fast.py`'s TSDF
  code and `pipeline/experiments/metric_scale.py`.
- Variants to A/B on the bench:
  - `--model metric3d_v2` (BSD-2, which also gives normals);
  - `--model da3_metric` (Apache);
  - PromptDA with the SfM sparse depth as the prompt;
  - VGGT or VGGT-Ω depth at native 518 px as a multi-view-consistent baseline.
- Score coverage (view), F@2/5 cm against the OpenMVS reference, and time.
- Expected result: much higher coverage on cabinet fronts and walls. Fine detail below 1 cm will
  be softer than PatchMatch on printed or textured surfaces.
- The obvious hybrid follows from that: keep PatchMatch points where they exist, and fill with
  mono depth elsewhere (Murre's idea).

**R2: scale ensemble** (rank 2). Repo: MapAnything `3d10cf7`, using the
`facebook/map-anything-apache` weights.

```bash
python -m venv /workspace/envs/mapany --system-site-packages   # inherit ROCm torch
/workspace/envs/mapany/bin/pip install --no-deps -e map-anything && \
/workspace/envs/mapany/bin/pip install hydra-core uniception orjson plyfile safetensors trimesh opencv-python-headless
# cues, each with a variance:
#  a) altitude fit on COLMAP poses (already in calibrate.py, 0.1847, resid 0.05 m)
#  b) median(D_metric / D_recon) over >=20 frames, MoGe-2 + Metric3D + DA3-Metric separately
#  c) counter height 0.91 m / depth 0.60 m from the mesh's horizontal-plane histogram
#  d) MapAnything.infer(views with intrinsics + COLMAP poses, is_metric_scale=False,
#     memory_efficient_inference=True) -> pred["metric_scaling_factor"]
# combine: inverse-variance weighted Huber; report per-cue spread as the confidence.
```

Expected:
- The cues agree within 5–10%. MoGe-2 is indoor-calibrated, unlike on aerial footage.
- If (b) and (c) agree and (a) does not, the altitude cue is biased. Downgrade it indoors.
- VRAM: MapAnything at 50 views, about 8–10 GB with AMP (unverified).
- Tape-measure one counter to settle it.

**R3: vggt-low-vram, all frames** (rank 3). Repo:
[harry7557558/vggt-low-vram](https://github.com/harry7557558/vggt-low-vram) `75c0507`, with the
same `facebook/VGGT-1B` checkpoint.

```bash
git clone https://github.com/harry7557558/vggt-low-vram /workspace/src/vggt-low-vram
cd /workspace/src/vggt-low-vram && git checkout 75c0507
/workspace/envs/vggt/bin/pip install --no-deps -e .       # reuse the env that already runs VGGT
# if torch.compile breaks on gfx1201: TORCHDYNAMO_DISABLE=1
TORCHDYNAMO_DISABLE=1 /workspace/envs/vggt/bin/python benchmark/benchmark.py ...   # adapt to $W/frames_selected
```

What to measure:
- Wall-clock and peak VRAM at 96, 175 and 260 frames.
- Pose ATE and Sim(3) scale against COLMAP.
- The altitude fit's agreement with the full stage, which is the 45% issue.

Expected: ~7 GB at 175 frames, and the altitude residual shrinks toward the COLMAP 0.05 m.

**R4: VGGT-Ω** (rank 4). Repo:
[facebookresearch/vggt-omega](https://github.com/facebookresearch/vggt-omega) `48b23c8`.

```bash
# needs HF access approval on facebook/VGGT-Omega (user account + token on hfbox) -- flag to user
python -m venv /workspace/envs/vggto --system-site-packages
git clone https://github.com/facebookresearch/vggt-omega /workspace/src/vggt-omega && cd $_ && git checkout 48b23c8
/workspace/envs/vggto/bin/pip install --no-deps -e . && /workspace/envs/vggto/bin/pip install einops safetensors opencv-python "numpy<2"
huggingface-cli download facebook/VGGT-Omega vggt_omega_1b_512.pt --local-dir /workspace/models/vggt-omega
# README quick-start: VGGTOmega(); load_and_preprocess_images(paths, image_resolution=512, mode="max_size")
# -> predictions["pose_enc"], ["depth"], ["depth_conf"]; encoding_to_camera(...)
```

- Use `mode="max_size"` (512×288 for 16:9) to fit 120–150 frames in 16 GB.
- Drop it into `pipeline/vggt.py` behind a flag and rerun the preview.
- Pass condition: the preview's altitude-scale disagreement drops well below 45%, and its coverage
  is at least VGGT's.

**R5: 2DGS on RDNA4** (rank 5). Two routes:
- **(a) gsplat port.** [Painter3000/amd-gsplat-rocm72-gfx1201](https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201)
  `e2f3319`, using `examples/simple_trainer_2dgs.py`. Apache, and the preferred route.
- **(b) original 2DGS port.** [Painter3000/amd-2dgs-rocm72-gfx1201](https://github.com/Painter3000/amd-2dgs-rocm72-gfx1201)
  `914838b`, which ships a release-bundle installer. Inria non-commercial.

```bash
git clone --recurse-submodules https://github.com/Painter3000/amd-gsplat-rocm72-gfx1201 /workspace/src/gsplat-rocm
cd /workspace/src/gsplat-rocm && git checkout e2f3319
python -m venv /workspace/envs/gs --system-site-packages; . /workspace/envs/gs/bin/activate
pip install ninja numpy rich jaxtyping
(cd gsplat/cuda/csrc/third_party/glm && cmake -DGLM_BUILD_TESTS=OFF -DBUILD_SHARED_LIBS=OFF \
   -DCMAKE_INSTALL_PREFIX=$HOME/.local -B build . && cmake --build build -j && cmake --install build)
PYTORCH_ROCM_ARCH=gfx1201 MAX_JOBS=8 pip install --no-build-isolation --no-deps --no-cache-dir -v .
python tests/rocm/gsplat_remaining_paths_validation.py      # must pass before trusting anything
pip install --no-deps -r examples/requirements.txt  # then add missing pure-python deps by hand
cd examples && python simple_trainer_2dgs.py --data_dir $W/dense --data_factor 2 \
   --max_steps 15000 --depth_loss --normal_loss --dist_loss --result_dir $W/2dgs
# mesh: render depth from every training camera (render_mode="D"/"ED" validated) -> Open3D TSDF
#       (same code as R1) -> decimate 200k -> TextureMesh -m (as R1)
```

- The port does not validate SH rendering. Start with `--sh_degree 0`, or check SH against a
  known scene first.
- Extension, as the next step: add a mono-depth L1 loss (R1's aligned MoGe depth) and a mono-normal
  loss (Metric3D v2 normals). This is DN-Splatter's recipe and about 30 lines.
- Pass condition: F@2 cm and coverage beat both OpenMVS and R1 on cabinets.

**R6: VGGT-X global alignment** (rank 6). Repo: [Linketic/VGGT-X](https://github.com/Linketic/VGGT-X)
`26d1b95`.

```bash
git clone --recursive https://github.com/Linketic/VGGT-X /workspace/src/vggt-x && cd $_ && git checkout 26d1b95
grep -vE '^(torch|torchvision|numpy)==' requirements.txt > req-rocm.txt
/workspace/envs/vggt/bin/pip install -r req-rocm.txt       # env already pins pycolmap 3.10 (p1-gpu-rocm.md)
mkdir -p /workspace/exp/vggtx/kitchen && ln -s $W/frames_selected /workspace/exp/vggtx/kitchen/images
/workspace/envs/vggt/bin/python demo_colmap.py --scene_dir /workspace/exp/vggtx/kitchen \
    --shared_camera --use_ga --chunk_size 64 --save_depth
# -> kitchen_vggt_x/sparse/  (COLMAP)  -> feed to the existing undistort/OpenMVS/R1/R5 tails
```

- Compare registration (175/175 frames vs COLMAP's 165), ATE against COLMAP, and time against
  pycolmap's 113 s.
- Risk: XFeat comes via torch.hub. The GA step's memory at 175 frames is unmeasured.

**R7: COLMAP HIP patch-match** (rank 7). Repo: [colmap/colmap](https://github.com/colmap/colmap)
tag `4.2.0` (`be5e291`), which includes [PR #4420](https://github.com/colmap/colmap/pull/4420).

```bash
# needs hip, hiprand, rocrand dev packages in the container (check /opt/rocm first; not verified present)
cmake -S colmap -B build -GNinja -DCUDA_ENABLED=OFF -DHIP_ENABLED=ON \
      -DCMAKE_HIP_ARCHITECTURES=gfx1201 -DCMAKE_HIP_COMPILER=/opt/rocm/llvm/bin/clang++ && ninja -C build
build/src/colmap/exe/colmap patch_match_stereo --workspace_path $W/dense \
      --PatchMatchStereo.max_image_size 2000 --PatchMatchStereo.geom_consistency true
build/src/colmap/exe/colmap stereo_fusion --workspace_path $W/dense --output_path $W/dense/fused.ply
```

- Then either COLMAP's own Poisson/Delaunay mesher plus its 4.x texture mapping, or TSDF from its
  depth maps, which reuses R1's fuser and avoids the `ReconstructMesh` visibility seam.
- The risk is entirely in the build and gfx1201. See the pre-flight note below.

**Pre-flight for every source build (R5, R7), and a real blocker.** `p1-gpu-rocm.md` §0 found that
hfbox's `/opt` holds only `venv/`: no `/opt/rocm`, no `rocminfo`, and probably no `hipcc`. The
PyTorch ROCm wheel ships HIP *runtime* libraries, not the compiler. So the first step of R5 and R7
is:

```bash
ls /opt/rocm/bin/hipcc 2>/dev/null || python -c "import torch.utils.cpp_extension as c; print(c.ROCM_HOME)"
```

If both are empty, use one of:
- TheRock's pip ROCm SDK (`rocm[devel]`), matched to 7.2 (unverified that it exists for 7.2 on
  this Python);
- ask the box owner for the `rocm-hip-sdk` apt package;
- build the extensions in a `rocm/pytorch` container elsewhere and copy the wheels. They are
  gfx1201 code objects, so they must match torch 2.14's ABI.

R1–R4, R6 and R10 need no compiler.

**R10: Depth Anything 3** (rank 10). Repo: [ByteDance-Seed/Depth-Anything-3](https://github.com/ByteDance-Seed/Depth-Anything-3)
`3d835ec`.
- Install with `--no-deps`, then try ROCm xformers. If that fails, use the SDPA workaround from
  [issue #11](https://github.com/ByteDance-Seed/Depth-Anything-3/issues/11).
- Skip gsplat; it is only needed for the GS head.
- Run `DA3-BASE` (Apache) and `DA3NESTED-GIANT-LARGE-1.1` (NC, metric) on the 175 frames. Use
  `da3_streaming/` for the full clip.
- Compare against R3 and R4 on the same metrics.

### 9.3 Needs a CUDA cloud box

These make sense only after R1–R5 set the AMD baseline. Use one L40S or 4090-class instance.
Expect a few dollars per run; all are single-GPU.

| Method | Repo @ commit | Recipe | Expected | Risk |
|---|---|---|---|---|
| **PGSR** | [zju3dv/PGSR](https://github.com/zju3dv/PGSR) `de24f1a` | `pip install submodules/diff-plane-rasterization submodules/simple-knn`; `python train.py -s $W/dense -m out --max_abs_split_points 0 --opacity_cull_threshold 0.05`; `python render.py -m out --max_depth 5.0 --voxel_size 0.01` (the README's example uses `--max_depth 10.0`; 5 m suits the kitchen); then decimate and TextureMesh | 45–60 min; best F-score of the splat methods | research-only licence; indoor low-light untested |
| **MILo** | [Anttwo/MILo](https://github.com/Anttwo/MILo) `0c38aa6` | follow the README conda recipe (5 CUDA extensions plus nvdiffrast); use the low-res mesh preset for about 250k vertices, then TextureMesh `-m` | ~50 min on a 4090; a mesh already near 200k | Inria non-commercial; heavy build |
| **RaDe-GS** | [HKUST-SAIL/RaDe-GS](https://github.com/HKUST-SAIL/RaDe-GS) | train, then its TSDF script | 15–25 min | licence unverified |
| **GOF** | [autonomousvision/gaussian-opacity-fields](https://github.com/autonomousvision/gaussian-opacity-fields) | train, then marching tetrahedra | ~1 h; for **aerial orbits** rather than the kitchen | huge meshes, so decimate hard |
| **OpenMVS CUDA or COLMAP CUDA dense** | as in `p1-fast-recon.md` §1.2 | CUDA build of `DensifyPointCloud` | the classic reference, 5–10× faster | only a baseline accelerator |
| **MASt3R-SLAM / HI-SLAM2 / Splat-SLAM** | see §5 | lietorch-based | only for streaming or long aerial clips | NC licences (MASt3R), low priority |

### 9.4 What each result would decide

- **R1 beats OpenMVS on coverage at similar F@5 cm:** `DensifyPointCloud` leaves the default full
  stage. That removes 27 of the 34 minutes.
- **R2's cues converge:** the preview publishes metric scale from the ensemble, not the
  altitude-only fit.
- **R3 or R4 poses match COLMAP within about 2% scale:** the preview's scale becomes trustworthy,
  and the preview-to-full swap stops resizing the room.
- **R5 or PGSR/MILo beat R1 on F@2 cm:** a splat stage becomes the "detail" upgrade, on AMD if R5
  wins and on cloud otherwise.

## 10. Sources

Every source is cited inline next to its claim. Primary sources this agent fetched directly:
- arXiv abstract and HTML pages for 2605.15195 (VGGT-Ω Tables 1–2), 2509.13414 (MapAnything
  Table 4), 2602.23361, 2609.20012, 2601.19887, 2605.06270, 2609.13733, 2603.27222, 2608.15260,
  2609.23733, 2507.14798, 2609.19628, 2604.06830, 2509.25191, 2507.16443, 2609.03304, 2603.24725,
  2601.21400, 2608.20687, 2605.00569 and 2509.24308;
- the READMEs, LICENSEs and requirements of vggt-omega, vggt-low-vram, VGGT-X, map-anything,
  Depth-Anything-3, Painter3000 gsplat and 2DGS, Murre, PromptDA, Metric3D and MoGe;
- the Hugging Face model API for licences and gating of VGGT-1B, VGGT-1B-Commercial, VGGT-Omega,
  map-anything(-apache/-v1), Pi3, Pi3X and every DA3 variant.

Repo commits come from `git ls-remote` on 2026-09-25.
