# R5: academic survey of CAD-like structure (planes, edges, corners) from photogrammetry

Agent R5 wrote this on 2026-09-25 for the P2/P3 structure bench
(`docs/research/p2-structure-bench.md`). It is research only: nothing here was run on hfbox, and
no accounts or paid APIs were used.

**Evidence tags** (every claim carries one):
- **[V]** verified this session against a primary source: README, LICENSE file, the GitHub or
  Hugging Face API, an arXiv abstract or HTML, or CGAL docs.
- **[P]** a paper number read from the paper's HTML or PDF via a fetch summary. The table is
  right, but check a digit before quoting it in a decision.
- **[S]** a secondary source only (a search snippet or blog).
- **[M]** from memory or background knowledge and not re-checked this session.
- **[I]** our own inference or estimate.

Setting (from `p1-recon-bench.md` §5):
- **Poses:** COLMAP 4.2, 175 frames at 3840×2160, f ≈ 1458 px at 1920 wide, reprojection
  0.62 px.
- **Geometry:** the hybrid dense cloud (OpenMVS res1 plus MoGe-2 fill), then ReconstructMesh and
  TextureMesh, giving 200k tris.
- **Hardware:** hfbox has an RX 9070 XT (gfx1201) and ROCm 7.2.1 with a working `hipcc`. torch
  `cpp_extension` JIT builds work, and E2 ported gsplat and the 2DGS `diff-surfel-rasterization`
  (`bench/e2-rocm-builds.md`). The laptop has no GPU.

---

## 0. TL;DR

**The one insight that organises the field.** CAD-like structure splits into three sub-problems,
and the literature solves each best with a different signal:

| Sub-problem | Best signal | Why |
|---|---|---|
| **Which pixels or points belong to one plane** (membership) | Learned priors: planarity, normals, masks (PxwPlanar, MoGe-2 normals, SAM-style masks) | Mono normals are only good to 16–20° mean / 8–10° median error [P] (DSINE, StableNormal), which is far too loose for plane *parameters* but ideal for *grouping* textureless cabinet faces. |
| **Where the plane is** (normal, offset) | Multi-view geometry: least squares or robust fit over thousands of MVS points, then Manhattan regularization | Averaging N points shrinks noise by √N. The residual error is MVS bias, a few mm [I]. |
| **Where the boundaries are** (edges, corners) | Plane–plane intersections where two planes meet. Triangulated **image lines** (LIMAP / DeepLSD) where they don't: door gaps, counter lips, window frames | Plane intersections are exactly straight and sharp by construction. Image lines localise to sub-pixel, about 1 mm at 1.5 m with our focal [I]. |

**Metric scale stays the dominant error for absolute measurements.** MoGe-2 (0.2545) and the
object check (~0.27) differ by ~6%, or ±3.6 cm on a 60 cm dishwasher [I]. No structure method
fixes this. The structure layer does make relative measurements (angles, ratios, repeatability)
exact, and it supplies clean scale cues (counter height, door widths) for the tape-measure check.

**Top 5 for our goal** (the experiments are in §9):

| Rank | Candidate | Output | Runs on | Licence | Why |
|---|---|---|---|---|---|
| **1** | **Geometric plane layer:** plane detection on the hybrid dense cloud (Open3D `detect_planar_patches` or CGAL region growing), IRLS refit, Manhattan regularization, plane–plane intersections giving edges and triple intersections giving corners. Optionally PxwPlanar masks for membership. | planes, edges, corners | CPU, seconds | Open3D MIT [V]; CGAL GPL [V]; PxwPlanar MIT [V] | Exactly our target format, no training domain gap, deterministic, zero porting. |
| **2** | **LIMAP 2.0** point-line triangulation on our COLMAP model (DeepLSD + GlueStick), fused with the plane layer | 3D line segments, VPs, line–plane links | C++ core on CPU; torch detectors on ROCm torch [I] | BSD-3 [V]; DeepLSD/GlueStick MIT [V] | Released 2026-09-01 as "fully compatible with COLMAP 4.2.0" [V], so it reads our model as-is. It is the only mature source of *boundary* edges that are not plane intersections. |
| **3** | **PlanarSplatting** (CVPR 2025) via a HIP port of `diff-rect-rasterization`. The same port unlocks **LiP-Map** (TPAMI 2026, lines and planes jointly). | 3D planar rectangles (LiP-Map: plus lines) | NVIDIA ~3 min per scene; needs a HIP port like E2's 2DGS one [I] | Apache-2.0 [V] (LiP-Map: no licence file [V], so research only) | SOTA multi-view planes: ScanNet++ Chamfer 9.33 vs AirPlanes 13.75, F 47.0 vs 32.6 [P]. Has `run_demo_colmap.py` [V]. |
| **4** | **Regularized mesh:** snap vertices to planes, edges and corners from #1/#2, then planar-quadric decimation and re-texture. KSR / COMPOD as a low-poly collision proxy. | mesh with exact planes and sharp edges; a few-k-tri proxy | CPU | pymeshlab GPL [M]; CGAL KSR GPL [V]; COMPOD "contact INRIA" [V] | Gives the "truly sharp mesh" option and a clean collision mesh from the same plane set. |
| **5** | **Labels and parametric openings:** PxwPlanar plus Grounded-SAM-2 masks voted onto planes, with door, window and cabinet-door rectangles fitted in plane coordinates (Cloud2BIM-style heuristics). SpatialLM 1.1 as a research-only comparator. | labeled planes, door/window rectangles | GPU inference (ROCm torch) | Grounded-SAM-2 Apache-2.0 [V]; Cloud2BIM MIT [V]; SpatialLM encoder CC-BY-NC [V] | Semantic snap targets (door width, window corners) plus the repeatability metric. |

**Start now** (already sent to the lead as an interim):
- #1 and #2 are independent and CPU-friendly. Start both immediately.
- #3 needs one HIP-port job, similar to E2's 2DGS port.
- #4 and #5 consume #1 and #2.

---

## 1. Problem decomposition and what "accurate" can mean for us

- **What the bench says goes wrong** (`p2-structure-bench.md` §1): edges are rounded over
  1–5 cm, faces wobble, and a ray-hit snap lands anywhere.
- **Where the rounding comes from:**
  - ReconstructMesh's Delaunay/graph-cut surface on noisy fused points [M].
  - Screened-Poisson-like smoothing of the MoGe fill [I].
  - 200k-tri decimation, which cuts corners [I].
- **Why flat faces wobble:** noise in the monocular fill on textureless faces. The fill is
  aligned per frame by scale and shift, so neighbouring frames disagree by mm to cm [I].

**Two families of fix appear in the literature:**
- **Abstraction:** estimate primitives (planes, lines) and publish them. That covers §2, §3 and
  §6. Snapping to an analytic primitive is exact, and the mesh can stay as it is for display.
- **Regularization:** make the mesh itself piecewise planar and sharp. That covers §4 and §5.
  It is harder to keep texture quality, but it fixes the collision mesh too.

The bench wants both: a structure layer first, the regularized mesh optional. The ranking
follows that.

---

## 2. Planar and piecewise-planar reconstruction

### 2.1 Classic, geometry only (input: a point cloud or mesh, typically ours)

| Method | Output | Inputs | Runtime | Licence / code | AMD / CPU | Reported accuracy / notes |
|---|---|---|---|---|---|---|
| RANSAC (Open3D `segment_plane`; pyransac3d) | 1 plane per call (iterate) | points | ms–s | Open3D MIT [M] | CPU | Greedy sequential RANSAC merges parallel near-coplanar faces (a cabinet front and the counter lip) [M]. Baseline only. |
| **Efficient RANSAC** (Schnabel et al. 2007), CGAL Shape Detection | planes (and other primitives) with inliers | points + normals | s | CGAL GPL [V] | CPU | Standard in CGAL since 4.7 [V]. |
| **Region growing**, CGAL Shape Detection (also on meshes: face graphs) | planes + inlier regions | points + normals, or a mesh | s | CGAL GPL [M] | CPU | Better than RANSAC on large smooth faces; parameters `max_distance`, `max_angle`, `min_region_size` [V via KSR docs]. |
| **Open3D `detect_planar_patches`** (Araújo & Oliveira 2020, robust statistics, octree split-and-merge) | oriented bounded patches (OBB-like) | points + normals | s | Open3D MIT [M] | CPU | "Virtually independent of parameter tuning" (paper claim [S]). Defaults: normal variance 60°, coplanarity 75°, outlier ratio 0.75 [V]. The easiest drop-in. |
| **PSDR / pypsdr** (Sulzer; region growing + GoCoPP-style refinement) | refined planes, vertex groups (.vg/.npz) | points + normals | s–min | "Contact INRIA for licensing" [V], so research only | CPU | Feeds COMPOD. |
| **GoCoPP** (Yu & Lafarge, CVPR 2022) | a plane configuration optimized for fidelity, simplicity and completeness | points + normals | min | code on GitHub [V], no licence file [V] | CPU | Better configurations than region growing on noisy MVS clouds (paper claim [M]). |
| Planar shape detection at structural scales (Fang, Lafarge, Desbrun, CVPR 2018) | multi-scale planes | points | min | [M] | CPU | Useful idea for exteriors (facade vs window reveal) [I]. |
| **CGAL Shape Regularization** | planes snapped to parallel, orthogonal and coplanar | planes + inliers | ms | GPL [V] | CPU | Exactly the "Manhattan snap" step. Parameters are an angle tolerance and a max offset [V]. |
| **PolyFit** (Nan & Wonka, ICCV 2017), CGAL Polygonal Surface Reconstruction | a watertight polyhedral mesh from candidate faces (MIP) | planes + points | s–min; the MIP scales poorly past ~100 planes [M] | GPL-3 [V] | CPU (SCIP/GLPK) | Object or building scale. Not a cluttered kitchen [I]. |
| **Kinetic Shape Reconstruction** (Bauchet & Lafarge, ToG 2020), CGAL 6.0 KSP + KSR | a watertight polygon mesh by min-cut over a kinetic partition | points + normals | Foam box: 382k pts in 9.9 s. Meeting room: 3.07M pts, 1652→777 shapes, in 142 s [V] | GPL [V] | CPU | Built for architecture and LiDAR. Good for **building exteriors** and a low-poly room shell [I]. |
| **COMPOD** (Sulzer & Lafarge, ECCV 2024) | a concise plane arrangement (BSP), low-poly surface, convex decomposition | planes (.vg/.npz) + points | fast (claimed) [V] | "Contact INRIA" [V] | CPU | Newer alternative to KSR and PolyFit [V]. |
| Variational Shape Approximation (Cohen-Steiner et al. 2004), CGAL Surface Mesh Approximation | K planar proxies + a remeshed approximation of the input mesh | mesh | s | GPL [V] | CPU | **Direct 200k → few-k proxy**, but proxies are not regularized and boundaries are jagged unless post-processed [M]. |
| Structure-aware mesh decimation (Salinas, Lafarge & Alliez, CGF 2015) | a decimated mesh that preserves planar proxies and their adjacency | mesh + detected planar proxies | s–min | no public code found [V: search] | CPU | The exact recipe for "200k → few k with exact planes" [V abstract]. Reimplement via QEM plus proxy quadrics (§4) [I]. |
| Manhattan-world stereo (Furukawa et al. 2009); Atlanta world (Schindler & Dellaert 2004); Manhattan-frame estimation (Straub et al.) | dominant orthogonal (Manhattan) or vertical+horizontal (Atlanta) directions | normals or VPs | ms | papers [M] | CPU | For us: estimate the frame from plane normals or LIMAP VPs [V: LIMAP has JLinkage VPs], then snap plane normals within ~5° [I]. Kitchens are Manhattan except appliances. |

### 2.2 Learned plane reconstruction

| Method | Output | Inputs | Runtime | Licence / code | AMD / CPU | Reported accuracy |
|---|---|---|---|---|---|---|
| PlaneRCNN (NVIDIA, CVPR 2019) | per-image plane masks + parameters | 1 image (+ refinement with a neighbour) | ~fps on GPU [M] | NVIDIA licence, no SPDX [V]; NC [M] | old CUDA ops [M] | ScanNet-trained. cm–dm depth error [M]. |
| PlaneRecTR (ICCV 2023) | per-image planes (transformer queries) | 1 image | fast | Apache-2.0 [V] | torch, probably ROCm [I] | ScanNet / NYUv2 [M]. |
| PlaneFormers (ECCV 2022) | planes + relative pose from sparse views | 2–few images | fast | MIT [V] | torch [I] | Wide-baseline oriented. |
| MonoPlane (2024) | zero-shot planes from depth and normal priors plus clustering | 1 image | fast | no licence [V] | torch [I] | [M] |
| ZeroPlane (CVPR 2025) | in-the-wild single-image planes | 1 image | fast | MIT [V] | torch [I] | Indoor and outdoor zero-shot [V abstract]. |
| **PxwPlanar** (Yavuz, Ozkan, Pautrat, Liu, Pollefeys; ECCV 2026) | per-pixel **planarity probability** + metric depth + normal + mask from a 4-head **MoGe-2-vitl-normal** fine-tune, then 8-connected region growing into plane segments | 1 image | MoGe-2 speed (~60 ms on A100 for ViT-L [V MoGe README]) | **MIT code and weights** [V: LICENSE + HF card] | torch; we already run MoGe-2 on hfbox, so likely it just works [I] | "Improved geometric precision" over prior SOTA; the abstract gives no numbers [V]. **Integrated into LIMAP 2.0 as its plane detector** [V]. |
| PlanarRecon (CVPR 2022) | 3D planes, incremental | posed monocular video | real-time on GPU [M] | Apache-2.0 [V] | torchsparse (CUDA) [M], so a port is needed | ScanNetV2 Chamfer 9.89, F 43.5 [P via PlanarSplatting table]. |
| AirPlanes (Niantic, CVPR 2024) | 3D planes: SimpleRecon depth → TSDF mesh, then clustering of learned 3D-consistent plane embeddings | posed RGB | [M] | **non-commercial, patent pending** [V] | CUDA [M] | ScanNetV2 Chamfer 5.30, F 64.9, VOI 2.27, RI 0.957, SC 0.568. ScanNet++ Chamfer 13.75, F 32.6 [P]. |
| **PlanarSplatting** (Tan, Yu, Shen, Xue; CVPR 2025) | 3D planar rectangles optimized by splatting into depth and normal maps, merged (<25° normal, small offset) [P] | posed images (COLMAP supported [V]) or pose-free via VGGT; Metric3D v2 depth init + Omnidata / Metric3D normals [V/P] | "3 minutes" per scene [V]; 2.5 min in the NVS table [P] | **Apache-2.0** [V] | CUDA `diff-rect-rasterization` + pytorch3d [V], so a HIP port is needed [I] | ScanNetV2 Chamfer **4.83**, F **68.9** (AirPlanes 5.30 / 64.9). ScanNet++ Chamfer **9.33**, F **47.0** (AirPlanes 13.75 / 32.6; 2DGS+RANSAC 20.4 / 26.5) [P]. Stated limitation: not for curved surfaces [P]. |
| NeuralPlane (ICLR 2025 oral) | planar primitives + semantics from neural fields | RGB-D / posed RGB + SAM + hLoc + normal priors [V] | slow (nerfstudio training) [I] | no licence file [V] | CUDA 11.8, nerfstudio [V] | Claims SOTA on ScanNetv2/++ [V]. |
| PLANA3R (NeurIPS 2025) | sparse metric planar primitives + relative pose | **two unposed views** [V] | feed-forward | Apache-2.0 code + weights [V] | same rect rasterizer [V] | ScanNetV2 planar F 93.16 (its own protocol) [V README]. Two-view only, so a preview idea, not a full-scene tool [I]. |
| TopoGS (ECCV 2026, Jul 2026) | connected planar model with topological adjacency from multi-view segmentations | posed images | [M] | project page only [V] | [I] | ScanNet++ gains claimed [V abstract]. Adjacency is exactly what gives us edges, so a paper to watch. |
| PlanarGS (NeurIPS 2025) | 3DGS with language-prompted planar priors, mesh | posed images + a VLM segmenter + geometric priors | ~1 h on an RTX 3090 [V] | MIT [V] | CUDA 3DGS rasterizer [I] | Chamfer < 5 cm on MuSHRoom and Replica, up to 43% better [V]. Too slow for us [I]. |
| 3D Gaussian Flats (Sep 2025) | hybrid 2D (planar) + 3D Gaussians, mesh | posed images | [M] | no code link [V] | [I] | SOTA depth on ScanNet++/v2 claimed [V]. |
| AlphaTablets (NeurIPS 2024) | planar "tablet" primitives with alpha | posed video | [M] | [M] | [M] | [M] |

**Take-aways:**
- Single-image plane nets (PlaneRCNN through ZeroPlane) give per-frame planes that are only
  cm–dm accurate in 3D [M]. Use them for **membership**, never for parameters.
- Among multi-view learned methods, PlanarSplatting is the clear SOTA with permissive code.
  AirPlanes is NC and ScanNet-specific.
- **PlanarSplatting, PLANA3R and LiP-Map share one rasterizer** (`diff-rect-rasterization`)
  [V], so one HIP port serves three methods.

---

## 3. 3D line and edge mapping from multi-view images

| Method | Output | Inputs | Runtime | Licence / code | AMD / CPU | Reported accuracy |
|---|---|---|---|---|---|---|
| **LIMAP 2.0.0** (cvg, released 2026-09-01; CVPR 2023, ECCV 2024, ECCV 2026) | 3D line segments with tracks. Also VPs, planes (via PxwPlanar), wireframe, and a holistic SfM with joint BA of points, lines, VPs and planes [V] | **an existing COLMAP model + images** (`automatic_point_line_triangulation -m -i -o`) [V] | minutes per ~100 images [M] | **BSD-3** [V]; `pip install pylimap` [V] | CUDA only "for deep learning based detectors/matchers" [V]; ROCm torch presents as CUDA, so likely OK [I] | ScanNetV2 (DeepLSD): line precision 0.645, recall 0.106, F 0.181 [P, LiP-Map Table I]. Hypersim with depth: precision 0.993 [P]. It is **precise but incomplete**. |
| CLMAP (ECCV 2024) | consistent line maps; joint points, lines, planes and VPs with coplanarity | posed images | ≈ LIMAP [I] | BSD-3 [V] | as LIMAP | ScanNetV2 F 0.259, ScanNet++ F 0.151 (LIMAP 0.181 / 0.077) [P]. |
| **LiP-Map** (TPAMI 2026) | 3D lines + planar patches, jointly optimized ("a 3D line emerges as the edge of a planar patch") [V] | posed images, depth (Metric3D v2 or MoGe), normals (Omnidata), 2D lines (DeepLSD, HAWP, LSD, ScaleLSD) [V] | 3–5 min per scene on an A6000; 275 s for 100 views [P] | **LEGAL.md only, no licence** [V], so research use only | CUDA + `diff-rect-rasterization` [V] | ScanNetV2 F 0.465 (LIMAP 0.181); ScanNet++ F 0.400 (LIMAP 0.077); 3–5× more lines at similar accuracy [P]. |
| Line3D++ (Hofer et al. 2017) | 3D line segments (STL/OBJ/TXT) | SfM (COLMAP reader exists, may be stale) + images [V] | fast with CUDA [V] | MPL-2.0 [V] | CUDA optional, so it runs on CPU [V] | Older baseline; LIMAP beats it on Hypersim and TnT [M]. |
| DeepLSD (CVPR 2023) | sub-pixel 2D line segments + line attraction fields + VPs | image | ~real-time on GPU [M] | MIT, code + weights [V] | torch; CPU inference possible [V] | Two weights: `deeplsd_wireframe` (indoor) and `deeplsd_md` [V]. |
| GlueStick (ICCV 2023) | joint point + line matches | image pairs | GPU fast, CPU slow [M] | MIT [V] | torch | Used inside LIMAP [V]. |
| HAWP v3 / NEAT (CVPR 2023) | 2D wireframes / 3D wireframe via neural attraction fields | image / posed images | NEAT needs hours of NeRF-style training [M] | MIT [V] | torch/CUDA [I] | Indoor wireframes [M]. |
| NEF (CVPR 2023) | 3D parametric edges (lines + curves) from a neural edge density field | posed images + 2D edge maps | hours [M] | [M] | CUDA [M] | ABC-NEF (per EMAP's table): Acc 15.1 mm, F@5 12.3 [P]. Object scale [M]. |
| EMAP (CVPR 2024) | 3D parametric edges (lines + curves) from a UDF | posed images + PiDiNet / DexiNed edge maps [P] | 50k–200k iterations [P], hours [I] | MIT [V] | CUDA (NeuS-style) [M] | ABC-NEF: Acc 8.8 mm, Comp 8.9 mm, F@5 59.1 [P]. "Restricted" at large scale [P]. |
| EdgeGaussians (WACV 2025) | parametric 3D edges from edge-specialized Gaussians, "an order of magnitude faster" than EMAP [V] | posed images + DexiNed / PidiNet edge maps; COLMAP parser "limited testing" [V] | minutes [M] | **no licence file** [V] | CUDA GS [V], portable like gsplat [I] | ≥ SOTA on ABC-NEF / DTU / Replica [V]. |
| PIE-NET, NerVE (2020 / CVPR 2023) | parametric edges from point clouds | a CAD-like clean point cloud | fast | [M] | [M] | Trained on ABC CAD models. Not for noisy MVS [I]. |

**Take-aways:**
- Edge-field methods (NEF, EMAP, EdgeGaussians) target object-scale CAD shapes and curves. For
  a kitchen, straight segments dominate, and LIMAP's line triangulation with plane coupling is
  the right tool.
- The 2026 trend (LiP-Map, CLMAP, LIMAP 2.0's holistic BA) is **joint line–plane
  optimization**. That is exactly our "planes + edges + corners" layer.
- We can do the coupling cheaply in post-processing: associate LIMAP lines with the §2 planes
  (§9, S2).

---

## 4. Feature-preserving mesh processing

| Method | What it does | Code | AMD / CPU | Notes for us |
|---|---|---|---|---|
| Bilateral mesh denoising (Fleishman et al. 2003) | vertex-domain bilateral filter | many | CPU | Smooths faces but also erodes edges a little [M]. |
| **Bilateral normal filtering** (Zheng et al. 2011), then vertex update | filter face normals with range weights, then refit vertices to the normals | many; pymeshlab's "two steps smoothing" is the related Belyaev–Ohtake scheme [V filter exists] | CPU | Flattens wobble while keeping creases. Needs a feature-angle threshold [M]. |
| **Guided normal filtering** (Zhang et al. 2015) | a joint bilateral filter with a patch-consistent guidance normal: sharper edges than bilateral | `bldeng/GuidedDenoising`, LGPL-3.0 [V] | CPU | The best classic denoiser for piecewise-planar CAD-like shapes [M]. Still gives *nearly* planar, not exactly planar, output. |
| L0 mesh denoising (He & Schaefer 2013) | L0 minimisation of a discrete differential, giving piecewise-constant normals | research code [M] | CPU, slow on 200k [M] | Produces flat patches, but can create staircase artefacts [M]. |
| Rolling-guidance / static-dynamic normal filters (Wang 2015; Zhang 2019) | scale-aware feature-preserving filtering | research [M] | CPU | Incremental over guided [M]. |
| Sharp-feature detection: CGAL PMP `detect_sharp_edges`, pymeshlab `compute_selection_crease_per_edge` | dihedral-angle crease tagging | CGAL GPL [V]; pymeshlab [V filter] | CPU | On our mesh the edges are *rounded*, so dihedral tagging fails. Tag edges from plane adjacency instead [I]. |
| Feature-preserving remeshing: CGAL isotropic remeshing with protected constraints; Instant Meshes creases | remesh while keeping tagged edges | GPL / BSD [M] | CPU | Use after snapping, with the snapped edges as constraints [I]. |
| **Plane snapping of vertices** (Holzmann et al., "plane-based surface regularization", BMVC 2017 / ECCV 2018 urban work; also Bódis-Szomorú et al. CVPR 2015 superpixel meshes) | project the vertices of each plane region onto the plane; boundary vertices onto the intersection line; corner vertices onto the corner | papers [M] | CPU | **The mechanism for an exactly planar and sharp mesh.** Simple to implement with numpy (§9 S4) [I]. |
| QEM with planar constraints: pymeshlab `meshing_decimation_quadric_edge_collapse(planarquadric=True)`, the `_with_texture` variant, `meshing_edge_flip_by_planar_optimization`, `generate_plane_fitting_to_selection` | decimation that keeps flat regions flat | pymeshlab (GPL-3 [M]) [V filters exist] | CPU | After snapping, flat regions collapse to a handful of triangles, so 200k → a few k tris [I]. |
| Structure-aware mesh decimation (Salinas et al. 2015) | QEM plus proxy quadrics plus a proxy-adjacency (structure) term | none found [V] | CPU | Reimplement as QEM on the snapped mesh [I]. |
| VSA / KSR / PolyFit / COMPOD (see §2.1) | a planar-proxy mesh of a few hundred to a few thousand faces | CGAL GPL, INRIA | CPU | Best as the **collision proxy** or for building exteriors. For a kitchen, pair it with the textured detail mesh [I]. |

**Take-away:**
- Denoisers make faces *flatter* but never *exact*.
- Exact planes and straight edges come only from projecting onto fitted primitives (snapping)
  or from rebuilding from primitives (KSR, COMPOD, VSA).
- So mesh regularization should consume the §2/§3 structure layer, not replace it.

---

## 5. Normal and depth priors, and planar regularization in neural surfaces

### 5.1 Monocular normal and depth priors

| Model | Output | Licence | Reported accuracy | For us |
|---|---|---|---|---|
| DSINE (CVPR 2024) | normals (+ uncertainty variant) | **Imperial non-commercial** [V] | NYUv2 mean 16.4°, median 8.4°, 59.6% within 11.25°; ScanNet 16.2° / 8.3° / 61.0% [P] | NC, so reference only. |
| StableNormal (SIGGRAPH Asia 2024) | sharp, low-variance diffusion normals | Apache-2.0 [V] | ScanNet mean 18.1°, median 10.1°, 56.0% within 11.25° (DSINE 18.6 / 9.9 in the same table) [S] | Slower (diffusion); crisper edges [M]. |
| Metric3D v2 | metric depth + normals | BSD-2 [V] | [M] | PlanarSplatting's default depth init [V]. |
| **MoGe-2** (`moge-2-vit{s,b,l}-normal`) | metric point map + normals | MIT (DINOv2 parts Apache-2.0) [V] | the paper reports no normal metrics [V] | **Already on hfbox**; use its normals for membership and PlanarSplatting supervision [I]. |
| **PxwPlanar** (MoGe-2 + planarity head) | planarity + depth + normal | MIT [V] | no numbers in the abstract [V] | The best "is this pixel on a plane" signal we found [I]. |
| Lotus (ICLR 2025) | diffusion depth / normals | Apache-2.0 [V] | [M] | Alternative. |
| TransNormal-2 (Sep 2026) | edge-aware rectified-flow normals | [not checked] | [S] | Watch list. |

**Rule:**
- Mono normals are 8–10° median off [P], so a plane fitted to mono normals alone is off by
  degrees.
- Use them to **group** points: normal agreement plus planarity mask plus region growing.
- Then **fit** the plane on MVS points: with OpenMVS points only, or with fill points whose
  per-frame affine is anchored to MVS.

### 5.2 Planar and normal regularization in neural surfaces

| Method | Idea | Runtime | Licence | Reported | For us |
|---|---|---|---|---|---|
| MonoSDF (NeurIPS 2022) | SDF + mono depth/normal cues | hours [M] | MIT [V] | ScanNet F ≈ 0.73 [M] | Too slow. |
| Manhattan-SDF (CVPR 2022) | semantic floor/wall Manhattan loss | hours [M] | "Project Registration License" [V] | ScanNet F ≈ 0.60 [M] | Too slow; the idea is reused in §2.1 regularization. |
| PGSR (TVCG 2024) | planar Gaussians, single/multi-view geometric consistency, TSDF mesh | DTU ~0.5 h, TnT 45 min [V] | **non-commercial** (ZJU) [V] | DTU CD 0.47, TnT F1 0.51 [V] | NC and slow. Our 2DGS+priors trial (E6) already lost to depthfusion. |
| 2DGS + mono priors (our E6) | surfels + MoGe-2 priors | ~976 s | — | Chamfer 3.44 cm, F@2 0.60 | Measured; rejected for P1. |
| PlanarGS, 3D Gaussian Flats, TopoGS, PlanarSplatting | see §2.2 | | | | Only PlanarSplatting fits the time budget. |

**Take-away:** the neural-surface route makes flat things flatter in the *mesh*, but it costs
30–60 min and gives no explicit primitives. PlanarSplatting inverts this: the primitives *are*
the representation. It takes 3 min and publishes planes, and it is the only one we rank.

---

## 6. Layout, CAD retrieval and scan-to-BIM

| Method | Output | Inputs | Licence | AMD / CPU | Reported | For us |
|---|---|---|---|---|---|---|
| **SpatialLM 1.0/1.1** (NeurIPS 2025) | walls, doors, windows + oriented object boxes, as structured text | a z-up, axis-aligned point cloud (their examples come from MASt3R-SLAM) [V] | code Apache-2.0; LLM Qwen Apache or Llama; **Sonata / SceneScript encoders CC-BY-NC-4.0** [V] | CUDA 12.4, torchsparse or Sonata + flash-attn [V], so ROCm is painful [I] | Structured3D layout F1@0.25 IoU 94.3% [V] | Research comparator for walls, doors and windows. Boxes, not mm corners. |
| SceneScript (Meta, ECCV 2024) | `make_wall / make_door / make_window / make_bbox` commands | a semi-dense point cloud (trained on synthetic ASE / Aria) [M] | **CC-BY-NC-4.0** [V] | torch [I] | [M] | NC and a synthetic domain. Skip. |
| RoomFormer (CVPR 2023) | floor-plan room polygons | a top-down density map | MIT [V] | torch | Structured3D SOTA at the time [M] | 2D only. Useful for exteriors or whole floors, not cabinets. |
| Plane-DUSt3R (ICLR 2025) | multi-view room layout from unposed images | images | [M] | [M] | [M] | Room layout, not fine structure. |
| Scan2CAD (CVPR 2019) / ROCA (CVPR 2022) / DiffCAD (2024) | ShapeNet CAD models aligned to scans or images (9-DoF) | an RGB-D scan / single image | Scan2CAD and ROCA: no SPDX [V], NC terms [M] | torch | Scan2CAD "correct" = ≤20 cm, ≤20°, ≤20% scale [M] | Tolerances two orders of magnitude too loose. Kitchens are not in ShapeNet. Skip. |
| CAD-Recode (2025) | CadQuery Python code from a point cloud (object level) | a clean object point cloud | CC-BY-NC-4.0 [V] | torch | [M] | Object CAD only. Skip. |
| Point2CAD (CVPR 2024) | B-rep-like surfaces + edges + corners from a segmented point cloud | a clean object point cloud + segmentation | Apache-2.0 [V] | torch | [M] | Object scale. The idea (intersect fitted primitives to get edges and corners) is ours in §9 S1. |
| **Cloud2BIM** (Automation in Construction 2025) | IFC walls, slabs, **openings (doors and windows by histogram along wall axes + sill, aspect and size rules)**, rooms | a building point cloud | **MIT** [V] | CPU | up to 7× faster than competitors [V abstract] | **Heuristics to borrow** for door and window rectangles in plane coordinates (S5). |
| Scan2LoD3 (CVPRW 2023); MLS2LoD3; Texture2LoD3; SVI2LoD3 (2026) | LoD3 facade openings (windows, doors) by fusing point-cloud, visibility-conflict and image (Mask R-CNN) probabilities [V] | MLS / images + LoD2 model | research [M] | torch / CPU | [V abstract] | For **building exteriors**: planar facade (KSR / COMPOD) plus window rectangles from image masks and LIMAP lines. |
| Apple RoomPlan (industry reference) | parametric walls, doors, windows | LiDAR + RGB | proprietary | — | 95% precision/recall for walls and windows, 90% for doors [S: Apple ML page]; iPad LiDAR linear error 1–2 cm [S] | **The accuracy bar a consumer tool sets:** a few cm. |

**Take-away:**
- Layout and CAD-retrieval models give *labels* and coarse boxes.
- None gives mm-accurate corners, and the good ones are NC or synthetic-trained.
- Labels are better done by open-vocabulary 2D masks voted onto our own planes (S5).

---

## 7. Measurement accuracy: what the literature says and our error budget

### 7.1 Reported accuracies

- **LiDAR tablets.** iPad Pro LiDAR vs TLS indoors: linear readings 1–2 cm off [S] (HBRC
  Journal 2024). RoomPlan's own report is detection rates, not mm [S].
- **Photogrammetry of interiors.** "Centimetre-level" against TLS (ISPRS accuracy assessment of
  automated photogrammetry for complex interiors) [S]. Practitioner figures are 1–2 cm with
  dense, calibrated capture and 3–5 cm typical [S, low credibility].
- **UAV oblique facades.** Clouds sit 12 cm (roof) to 16 cm (facade) from LiDAR [S]. But
  digitised **door and window details** at LoD3 reached RMSE 1.4 / 0.9 / 0.7 cm (X/Y/Z) against a
  total station (npj Heritage Science 2023) [S]. Hand-digitised corners on images beat the mesh
  surface by roughly 10×. That is the same effect we exploit with image lines [I].
- **Scale from a known object** (Rashidi & Brilakis 2015): a letter-size sheet indoors or a
  pre-measured cube outdoors [S]. Our P1 already uses standard-object checks.
- **Line-map benchmarks.**
  - LIMAP / LiP-Map evaluate line length-recall R_τ and precision P_τ at τ = 1/5/10 mm, and
    ACC / COMP / F at 5 mm, on Hypersim, ScanNet and ScanNet++ [P].
  - On Hypersim (synthetic, GT poses) LIMAP with depth reaches precision@5 mm of 0.993 [P].
    Real ScanNet numbers are far lower (F 0.18–0.47) because of pose and GT-mesh noise [P].
- **Plane benchmarks** (AirPlanes protocol, reused by PlanarSplatting): geometry as Chamfer / F
  against the GT mesh, segmentation as VOI / RI / SC [P].
  - **Chamfer at ScanNet scale is 5–10 cm even for SOTA** [P], but that is the whole-scene
    coverage error, not plane-offset accuracy.
- **CAD retrieval** (Scan2CAD): success at ≤20 cm / 20° / 20% [M]. Useless as a mm reference.

### 7.2 Our error budget (estimate [I])

Numbers use f = 1458 px at 1920 and 0.62 px reprojection (E1).

| Source | Magnitude | Notes |
|---|---|---|
| Lateral position of an image line or corner, 1 view at z = 1.5 m | 1 px ≈ 1.0 mm; DeepLSD sub-pixel, so ~0.5 mm | Averages further over 10–30 views. |
| Depth of a triangulated line, baseline b = 0.5 m, z = 2 m, σ = 0.5 px | σ_z ≈ z²σ/(f·b) ≈ 2.7 mm | A drone orbit gives wide baselines, so this is good. |
| Plane offset from ≥1000 MVS inliers (σ_point ≈ 5–8 mm) | statistical ≈ 0.2 mm; **bias** from MoGe-fill alignment of 2–10 mm | Check by fitting with and without fill points. |
| Corner from 3 orthogonal planes | ≈ the plane offset errors (conditioning ≈ 1 at 90°) | Degrades as 1/sin θ for shallow dihedrals. |
| Corner as a line endpoint | **poor** (endpoints get cut by occlusion) | Use a plane or line intersection instead. |
| Mesh surface today | edges rounded over 1–5 cm (bench §1) | The baseline to beat. |
| **Metric scale** | ~6% spread between cues (MoGe-2 0.2545 vs objects ~0.27) | 60 cm → ±3.6 cm. Dominates absolute measurements until the tape check. |

**Implication for the bench metrics:**
- Report snap accuracy in the **SfM frame converted by one fixed scale**, so geometry is judged
  separately from scale.
- Add **scale-free** metrics (angle error, ratio of door width to counter depth, repeatability of
  identical doors). These are the ones structure can actually drive to about 1%.

---

## 8. AMD / CPU feasibility summary

| Candidate | Laptop CPU | hfbox CPU | hfbox GPU (ROCm) | Porting work |
|---|---|---|---|---|
| Open3D / CGAL / pymeshlab plane + mesh ops | yes | yes | – | none |
| LIMAP 2.0 (`pylimap`) | slow (DeepLSD / GlueStick on CPU) [I] | yes | torch parts on ROCm torch [I] | Watch pip pulling a CUDA torch and shadowing ROCm torch (`p1-gpu-rocm.md` hit this twice). Install with `--no-deps` where needed [I]. |
| PxwPlanar | slow | – | yes (MoGe-2 already runs) [I] | It pins its own MoGe fork; use a separate venv [V: README warns about shadowing]. |
| PlanarSplatting / LiP-Map / PLANA3R | no | no | after a port | HIP-port `diff-rect-rasterization` (a 3DGS-rasterizer derivative [I]) + `quaternion-utils` + pytorch3d (a ROCm build, or stub the few ops used) [I]. E2 did the same class of job for 2DGS in ~15 min [V]. |
| EdgeGaussians | no | no | after a port | a gsplat-like rasterizer [I] |
| SpatialLM | no | no | hard | torchsparse or Sonata + flash-attn on ROCm [I] |
| AirPlanes / PlanarRecon / NeuralPlane | no | no | hard | CUDA-specific stacks, ScanNet-trained, AirPlanes NC. Not worth it. |

---

## 9. Ranked candidates with a concrete kitchen experiment each

**Common inputs:**
- `kitchen-0095` undistorted COLMAP model (PINHOLE) and images, i.e. `$W/dense/{sparse,images}`.
- The hybrid dense cloud with normals.
- The 200k textured mesh.
- The P2 ground-truth corners, triangulated from 2D clicks, once the bench has them.

**Common metrics** (`p2-structure-bench.md` §2): snap median/p90 mm and recall@3 cm, planarity
RMS, angle error, edge reprojection px, repeatability, runtime.

### S1 (rank 1): geometric plane layer (CPU, start now)

1. Load the hybrid dense cloud and voxel-downsample to 5 mm. Keep a per-point source flag
   (OpenMVS vs MoGe fill) if the fused `.ply` carries one. If not, re-derive it from the hybrid
   step's mask.
2. **Membership:** run two variants side by side.
   - **(a)** Open3D `detect_planar_patches(normal_variance_threshold_deg=30, coplanarity_deg=75,
     outlier_ratio=0.75, min_plane_edge_length=0.15, min_num_points=200)`.
   - **(b)** CGAL region growing (`max_distance` 1 cm, `max_angle` 10°, `min_region_size` 200)
     through CGAL's Python bindings, or `psdr` if the bindings lack it.
   - **(c)**, optional: per-frame PxwPlanar plane masks lifted with the COLMAP poses and voted
     onto points, to split coplanar-but-distinct faces (door vs carcass) and merge textureless
     ones.
3. **Fit:** IRLS / Huber plane fit per region.
   - Refit on OpenMVS-only inliers when there are more than 200 of them.
   - Log the offset difference between fill-only and MVS-only fits. That difference is the
     fill-bias measurement.
4. **Regularize:**
   - Estimate the Manhattan frame by mean-shift on the normal sphere, or from LIMAP VPs.
   - Snap normals within 5° of it.
   - Merge planes that are parallel within 3° and within 1 cm of offset and adjacent.
   - This is CGAL Shape_regularization, or 30 lines of numpy.
5. **Structure:**
   - Plane polygons: the 2D alpha-shape of the inliers in plane coordinates.
   - Adjacency: planes i and j are adjacent if more than 20 inlier pairs lie within 2 cm.
   - Edge: the intersection line clipped to the overlap of both supports, with the dihedral
     angle recorded.
   - Corner: the intersection of three mutually adjacent planes, kept if it lies within 3 cm of
     both support sets.
6. **Output:** `structure.json` (planes, edges, corners, confidences) in the mesh frame.
7. **Measure:**
   - Planarity RMS per plane, before (mesh) and after.
   - Angle error of counter vs cabinet-front vs wall pairs.
   - Corner snap median/p90 and recall@3 cm against the GT corners.
   - Runtime.
8. **Expect** [I]: under 30 s on CPU; angle error under 1° after regularization; corner median
   of a few mm where three planes meet. **Recall is limited to plane-intersection corners.**
   Door-gap and handle corners need S2.

### S2 (rank 2): LIMAP 2.0 lines, fused with S1 (CPU / ROCm torch, start now)

1. Install `pylimap` 2.0.0 into a fresh venv that inherits hfbox's ROCm torch, and check
   `torch.version.hip` after the install. Then run:
   `python -m limap.cli.automatic_point_line_triangulation -m $W/dense/sparse -i $W/dense/images -o $W/limap`.
   - Detector: DeepLSD `wireframe` weights (indoor).
   - Matcher: GlueStick.
   - Images at max 1600 px; ~20 visual neighbours per image, from COLMAP covisibility.
2. Also try the depth-assisted variant, which LiP-Map's table shows as "LIMAP w/ depth"
   [P: precision 0.99 on Hypersim]. Feed it our OpenMVS or hybrid depth maps if the CLI exposes
   it [M: LIMAP has a fit-and-merge-from-depth mode].
3. **Fuse with S1:**
   - A line is **on plane P** if both endpoints are within 1.5 cm of P and it is parallel within
     3°. Project it into P. These give door and drawer outlines and window frames.
   - A line near **two** adjacent planes is replaced by their intersection; the line–intersection
     distance is logged as a cross-check.
   - **Corners:** in-plane line–line intersections at about 90°, plus S1's triple corners.
4. **Measure:**
   - Edge reprojection median px against DeepLSD lines on held-out frames, and the recall of
     strong image lines.
   - Distance from GT corners to the nearest line or corner.
   - Number of lines; runtime.
5. **Expect** [I]:
   - A few hundred to ~1000 lines: LIMAP reports 397 lines per ScanNet scene [P]. It is precise
     and incomplete.
   - Sub-cm accuracy on strong cabinet edges.
   - A textureless fridge side gives few lines; S1 covers it.

### S3 (rank 3): PlanarSplatting on hfbox (ROCm port; LiP-Map comes for free)

1. Clone `ant-research/PlanarSplatting`, `source /workspace/rocm-env.sh`, and build
   `submodules/diff-rect-rasterization` and `quaternion-utils` with `PYTORCH_ROCM_ARCH=gfx1201`
   (hipify through torch `cpp_extension`, as in E2 §4b).
2. Check which pytorch3d ops are used. Build pytorch3d for ROCm, or replace them with torch
   equivalents.
3. Run `python run_demo_colmap.py -d <kitchen colmap dir>` on the undistorted model. Variant:
   swap the Metric3D normals for MoGe-2 normals, since `utils_demo/run_metric3d.py` is the only
   prior hook [V: the script imports `extract_mono_geo_demo`].
4. **Compare its planes to S1:**
   - The angle and offset difference per matched plane.
   - The fraction of mesh area explained by planes.
   - Snap metrics after running S1's step 5 (adjacency, edges, corners) on its planes.
5. If the port works, run **LiP-Map** on the same data for joint lines and planes, research
   licence only. Compare its line recall against S2 (paper: 3–5× more lines than LIMAP [P]).
6. **Expect** [I]:
   - Around 3–6 min per run on the 9070 XT.
   - Cleaner separation of textureless faces than S1, because depth and normal supervision works
     in image space.
   - Plane offsets tied to Metric3D / MoGe depth scale, so re-anchor to COLMAP scale via the
     script's `get_scales` (it already fits a mono-to-COLMAP scale [V]).

### S4 (rank 4): regularized, sharp mesh and a low-poly collision proxy (CPU)

1. **Label faces** of the 200k mesh with S1 planes: face-to-plane distance under 1.5 cm and
   normal within 20°. Optionally smooth the labels with a graph cut.
2. **Snap vertices:**
   - Vertices whose faces touch one plane label → project onto that plane.
   - Two labels → project onto the intersection line.
   - Three or more → onto the corner.
   - Unlabeled vertices within 2 rings of a snapped one → Laplacian blend, to avoid folds.
3. `pymeshlab.meshing_decimation_quadric_edge_collapse(targetfacenum=…, planarquadric=True,
   preserveboundary=True, preservenormal=True)`. Then:
   - (a) keep ~200k for display and re-run OpenMVS **TextureMesh** on the snapped mesh;
   - (b) push to 5–10k for `collision.glb`.
   - Alternative for (b): CGAL KSR (λ 0.5) or COMPOD from S1's planes, which gives a
     watertight low-poly shell. Better for **building exteriors**.
4. **Measure:**
   - Edge-sharpness band width (target: under 5 mm, from 1–5 cm).
   - Planarity RMS.
   - **PSNR/SSIM of the re-textured mesh must stay ≥ 21.5 / 0.807** (bench §2).
   - Collision tri count and size.
   - Ray-snap error when the tools use only the new collision mesh (no structure layer).

### S5 (rank 5): labels and parametric doors, windows and cabinet doors (ROCm torch)

1. Per frame, get the PxwPlanar planarity segments plus Grounded-SAM-2 masks (Apache-2.0) for
   the prompts "cabinet door", "drawer front", "countertop", "dishwasher", "refrigerator",
   "window", "door". SAM 3 has a custom licence and gated weights, so avoid it without the user.
2. **Project the masks** with the COLMAP poses onto S1 planes: rasterize the plane polygons per
   frame and vote on labels. That gives a label and a confidence per plane.
3. **For each labeled vertical plane:**
   - Fit axis-aligned rectangles in plane coordinates from mask boundaries, snapping sides to S2
     in-plane lines.
   - Apply Cloud2BIM-style rules: min/max width, sill height for windows, a door touching the
     floor.
   - Rectangle corners become corner snaps, and their widths become the **repeatability** metric
     (identical cabinet doors).
4. Optional research comparator: SpatialLM 1.1 (Qwen-0.5B) on the z-up-aligned dense cloud
   (walls, doors, windows, boxes). Record it only if it builds; it is NC because of the encoder.
5. **Measure:**
   - Label accuracy on hand-annotated planes.
   - Door-width spread (mm) across identical doors.
   - Measurement error on door and dishwasher widths.

### Suggested order

- S1 ∥ S2 now (CPU); S3's port in parallel on the GPU.
- S4 and S5 once S1 and S2 produce `structure.json`.
- The snapping order for the tools stays corner → edge → plane → mesh (bench §1).

---

## 10. Checked and deprioritized (why)

- **AirPlanes:** non-commercial and patent pending [V]. Its ScanNet-trained SimpleRecon depth
  adds a domain gap, and PlanarSplatting beats it [P].
- **PlanarRecon, PlaneRCNN, PlaneRecTR, PlaneFormers, MonoPlane, ZeroPlane:** single-view or
  ScanNet-trained, cm–dm parameter error [M]. PxwPlanar supersedes them as a membership cue for
  us.
- **NeuralPlane, PlanarGS, PGSR, MonoSDF, Manhattan-SDF:** 30 min to hours per scene, CUDA, and
  NC or unlicensed in several cases [V]. They give no explicit snap primitives.
- **NEF, EMAP, EdgeGaussians, NEAT:**
  - built for object-scale curves;
  - hours of training, or CUDA splats with no licence (EdgeGaussians [V]);
  - LIMAP covers straight kitchen edges better.
  - Revisit EdgeGaussians only if S2 recall is poor.
- **SceneScript, Scan2CAD, ROCA, DiffCAD, CAD-Recode:** NC [V] and/or synthetic or ShapeNet
  catalogues, with 20 cm-class tolerances.
- **PolyFit on the whole kitchen:** the MIP does not scale to hundreds of planes, and a cluttered
  interior is not a closed polyhedron [M]. Keep it and KSR for building exteriors.
- **Pure denoisers** (bilateral, guided, L0) as the main fix: they make faces flatter but never
  exact. Useful only as a pre-pass before S1 fitting, if at all.

---

## Sources

Planes:
[PlanarSplatting code](https://github.com/ant-research/PlanarSplatting) ·
[PlanarSplatting arXiv 2412.03451](https://arxiv.org/abs/2412.03451) and [HTML tables](https://arxiv.org/html/2412.03451v1) ·
[AirPlanes code](https://github.com/nianticlabs/airplanes) / [arXiv 2406.08960](https://arxiv.org/abs/2406.08960) / [page](https://nianticlabs.github.io/airplanes/) ·
[PxwPlanar code](https://github.com/alpayozkan/PixelwisePlanarity) / [arXiv 2609.13246](https://arxiv.org/abs/2609.13246) / [HF weights](https://huggingface.co/alpayozkan/pxwplanar-moge2-planarity) ·
[PLANA3R](https://arxiv.org/abs/2510.18714) / [code](https://github.com/lck666666/plana3r) ·
[TopoGS arXiv 2607.16838](https://arxiv.org/abs/2607.16838) ·
[PlanarGS](https://github.com/SJTU-ViSYS-team/PlanarGS) / [arXiv 2510.23930](https://arxiv.org/abs/2510.23930) ·
[3D Gaussian Flats](https://arxiv.org/abs/2509.16423) ·
[NeuralPlane](https://github.com/3dv-casia/NeuralPlane) ·
[ZeroPlane](https://github.com/jcliu0428/ZeroPlane) / [arXiv 2506.02493](https://arxiv.org/abs/2506.02493) ·
[PlanarRecon](https://github.com/neu-vi/PlanarRecon) · [PlaneRecTR](https://github.com/SJingjia/PlaneRecTR) ·
[PlaneFormers](https://github.com/samiragarwala/PlaneFormers) · [PlaneRCNN](https://github.com/NVlabs/planercnn) ·
[MonoPlane](https://github.com/thuzhaowang/MonoPlane) ·
[GoCoPP](https://github.com/Ylannl/GoCoPP) · [psdr](https://github.com/raphaelsulzer/psdr) ·
[COMPOD](https://github.com/raphaelsulzer/compod) / [arXiv 2404.06154](https://arxiv.org/abs/2404.06154) ·
[CGAL KSR manual](https://doc.cgal.org/latest/Kinetic_surface_reconstruction/index.html) ·
[CGAL package list](https://doc.cgal.org/latest/Manual/packages.html) · [CGAL licence](https://www.cgal.org/license.html) ·
[PolyFit](https://github.com/LiangliangNan/PolyFit) ·
[Open3D PointCloud API](https://www.open3d.org/docs/release/python_api/open3d.geometry.PointCloud.html) ·
[Araújo & Oliveira 2020](https://www.inf.ufrgs.br/~oliveira/pubs_files/RE/Araujo_Oliveira_plane_detection_low_res.pdf) ·
[Structure-aware mesh decimation (HAL)](https://inria.hal.science/hal-01111203)

Lines and edges:
[LIMAP](https://github.com/cvg/limap) / [arXiv 2303.17504](https://arxiv.org/abs/2303.17504) / [ECCV'24 hybrid SfM 2409.19811](https://arxiv.org/abs/2409.19811) / [ECCV'26 holistic BA 2609.04026](https://arxiv.org/abs/2609.04026) ·
[LiP-Map](https://github.com/calmke/LiPMAP) / [arXiv 2602.01296](https://arxiv.org/abs/2602.01296) / [HTML tables](https://arxiv.org/html/2602.01296v1) ·
[CLMAP](https://github.com/3dv-casia/clmap) · [Line3D++](https://github.com/manhofer/Line3Dpp) ·
[DeepLSD](https://github.com/cvg/DeepLSD) · [GlueStick](https://github.com/cvg/GlueStick) ·
[EMAP](https://github.com/cvg/EMAP) / [arXiv HTML 2405.19295](https://arxiv.org/html/2405.19295) ·
[EdgeGaussians](https://github.com/kunalchelani/EdgeGaussians) / [arXiv 2409.12886](https://arxiv.org/abs/2409.12886) ·
[HAWP](https://github.com/cherubicXN/hawp) · [NEAT](https://github.com/cherubicXN/neat)

Mesh, priors and neural surfaces:
[GuidedDenoising](https://github.com/bldeng/GuidedDenoising) ·
[PyMeshLab filter list](https://pymeshlab.readthedocs.io/en/latest/filter_list.html) ·
[DSINE](https://github.com/baegwangbin/DSINE) / [arXiv HTML 2403.00712](https://arxiv.org/html/2403.00712) ·
[StableNormal](https://github.com/Stable-X/StableNormal) · [MoGe](https://github.com/microsoft/MoGe) / [MoGe-2 HTML](https://arxiv.org/html/2507.02546) ·
[Metric3D](https://github.com/YvanYin/Metric3D) · [Lotus](https://github.com/EnVision-Research/Lotus) ·
[PGSR](https://github.com/zju3dv/PGSR) · [MonoSDF](https://github.com/autonomousvision/monosdf) · [Manhattan-SDF](https://github.com/zju3dv/manhattan_sdf)

Layout, CAD and BIM:
[SpatialLM](https://github.com/manycore-research/SpatialLM) · [SceneScript](https://github.com/facebookresearch/scenescript) ·
[RoomFormer](https://github.com/ywyue/RoomFormer) · [CAD-Recode](https://github.com/filaPro/cad-recode) ·
[Point2CAD](https://github.com/prs-eth/point2cad) · [Scan2CAD](https://github.com/skanti/Scan2CAD) · [ROCA](https://github.com/cangumeli/ROCA) ·
[Cloud2BIM](https://github.com/VaclavNezerka/Cloud2BIM) / [arXiv 2503.11498](https://arxiv.org/abs/2503.11498) ·
[Scan2LoD3](https://arxiv.org/html/2305.06314v1) · [SVI2LoD3](https://arxiv.org/pdf/2608.29992) ·
[Grounded-SAM-2](https://github.com/IDEA-Research/Grounded-SAM-2) · [SAM 3](https://github.com/facebookresearch/sam3)

Accuracy:
[Apple RoomPlan](https://machinelearning.apple.com/research/roomplan) ·
[Apple LiDAR vs TLS indoor (HBRC 2024)](https://www.tandfonline.com/doi/full/10.1080/16874048.2024.2408839) ·
[Photogrammetry accuracy for complex interiors (ISPRS)](https://www.researchgate.net/publication/276021644_AN_ACCURACY_ASSESSMENT_OF_AUTOMATED_PHOTOGRAMMETRIC_TECHNIQUES_FOR_3D_MODELING_OF_COMPLEX_INTERIORS) ·
[LoD UAV facade details (npj Heritage Science 2023)](https://www.nature.com/articles/s40494-023-01041-z) ·
[Rashidi & Brilakis 2015](https://ascelibrary.org/doi/10.1061/%28ASCE%29CP.1943-5487.0000414) ·
[UAV oblique first analysis (ISPRS 2016)](https://isprs-archives.copernicus.org/articles/XLI-B1/835/2016/isprs-archives-XLI-B1-835-2016.pdf)
