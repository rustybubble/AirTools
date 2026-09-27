# Cloud world-model / 3D reconstruction services: do any take our drone video and hand back a usable mesh?

Scope: cloud services (Sep 2026 offerings) that **accept video or frames and return a format our
app can use** — a textured triangle mesh, GLB/glTF preferred, faithful to the real captured
building (not stylised/hallucinated), at roughly a 200k-tri / 4096-texture budget, ideally with
camera poses, drivable by API/SDK/CLI (not only a web UI). Companion to `docs/research/
p1-fast-recon.md`, which already covered classic desktop/cloud photogrammetry (RealityScan,
Metashape, Pix4D, DroneDeploy, Bentley, Apple Object Capture, World Labs Marble) at a survey level
and concluded a self-hosted VGGT+TSDF fast path already hits <1 min. This doc goes deeper on the
**world-model / generative** services that doc only glanced at, and fills in services it didn't
touch at all (KIRI Engine, Polycam, Niantic Scaniverse, Teleport by Varjo, NVIDIA Cosmos/NuRec/
3DGUT/Lyra, Google Genie 3, Tencent Hunyuan, Meta Hyperscape, Cesium ion, Esri, Skydio, Nira,
and the smaller 2026 world-model entrants).

**TL;DR: none of the surveyed cloud services should replace or gate either half of the plan's own
pipeline (the <1 min self-hosted VGGT preview or the 9–31 min OpenMVS full mesh, both already
working per `p1-fast-recon.md` and the plan's §2.6). Two are worth naming, both as a fallback /
side note, not as the path:**

1. **KIRI Engine** is the one service that actually matches our input shape end to end, confirmed
   from its own API docs: a REST **video-upload** endpoint capped at 1920×1080 / ≤3 min (our orbit
   passes are 2–3 min each), OBJ/FBX/GLTF mesh export, an in-app **known-dimension rescale** tool
   (the same trick our own `calibrate.py` uses), $6.99/mo. **Real gap**: no official accuracy
   benchmark for building-exterior drone scans at our triangle/texture budget exists anywhere I
   found — only anecdotal/community evidence — and no AWS/Azure-marketplace billing. Worth a 10-minute
   smoke test as a **hfbox-down fallback**, not worth integrating before the event.
2. **World Labs Marble** (already flagged in `p1-fast-recon.md`) does accept video and does export a
   textured GLB mesh (confirmed from its own docs: up to ~1M triangles, `metric_scale_factor` for
   real-world scale) — but its own FAQ and third-party testing describe it **inferring** surface
   detail rather than measuring it ("text details missing," "object quality varied"), which is a
   direct conflict with the plan's MUST of *faithful, not hallucinated* reconstruction for a
   real-world accuracy-test beat. Fine as a stage aside ("we also tried a generative world model"),
   not for the accuracy table.

**Everything else is a clear No**, for one of four confirmed reasons: (a) **generative, not
reconstructive** — Google Genie 3, Tencent HunyuanWorld-Voyager, NVIDIA Lyra, Odyssey-3, SpAItial,
Wayve GAIA; (b) **no mesh export, splats/point-cloud/tiles only** — Teleport by Varjo (`.ply`
only, confirmed from its own export docs), NVIDIA NuRec/3DGUT (Gaussian-splat USDZ for robotics
sim), Cesium ion Reconstruction (3D Tiles, not a single mesh, and photos-only, not video); (c) **no
public export/API at all** — Meta Hyperscape Capture ("Meta does not currently offer a way to
export... raw capture data or trained PLY files," confirmed from Meta's own help page), Google
Genie 3 (no public API, confirmed); (d) **hardware- or sales-gated, wrong shape for a 36h
hackathon** — Skydio 3D Scan (Skydio drones only — incompatible with the DJI Mini 4K, which per
the plan's own research has no SDK), Esri Site Scan / ArcGIS Reality, Bentley iTwin Capture,
DroneDeploy (all enterprise-sales pricing, no published per-scene rate, no confirmed <1hr
turnaround). Full reasoning and sources per service below.

---

## 1. Comparison table

Legend: **Fits** = matches the MUST list closely enough to pilot; **Partial** = real gap (format,
access, or fidelity); **No** = ruled out on a confirmed, specific ground.

| Service | Accepts video? | Output | Faithful or generative? | Metric? | API/SDK access | Turnaround (evidence) | Price | AWS/Azure billable? | Verdict |
|---|---|---|---|---|---|---|---|---|---|
| **KIRI Engine** | **Yes** — documented REST video-upload endpoint, ≤1920×1080, ≤3 min, 2 GB | OBJ/FBX/GLTF mesh (Photo Scan, Featureless, 3DGS-to-Mesh); textured | Photogrammetric reconstruction (SfM/NSR-based), not generative | No native metric; in-app measure-and-rescale against a known real dimension (same method as our own pipeline) | **Yes**, public REST API, $1/credit, 10 free credits | Not disclosed in docs (unverified) | $6.99/mo Pro (200 photos/scan, video ≤3 min), $0/mo Basic (70 photos, no video confirmed) | No (subscription SaaS only) | **Partial** — right shape, unverified building-scale accuracy and turnaround |
| **World Labs Marble (World API)** | **Yes**, documented: text/image/multi-image/panorama/**video** | **GLB mesh**, two tiers: ~600k tri + textures, or ~1M tri + vertex colors; also splat (.spz/.ply) | **Generative** — infers "layout, depth, lighting, spatial structure"; own FAQ + third-party tests report missing/wrong detail | `metric_scale_factor` + `ground_plane_offset` provided (arbitrary units otherwise) | **Yes**, public API key, live since Jan 2026 | HQ mesh export: "up to an hour" (confirmed, docs) | ~1,500 credits/world (~$1.20) + ~3,500 credits HQ mesh export (~$2.80) ≈ **~$4/scene**; $1/1,250 credits | Not found | **Partial** — real risk the geometry doesn't match the real building |
| **DroneDeploy** | Photos (JPG + GPS EXIF) via its app or a documented "non-supported drone" manual-upload path; not raw MP4 | **OBJ or GLB** textured mesh, also DSM/ortho | Photogrammetric reconstruction | Yes (GPS-referenced) | **Yes**, GraphQL API (Enterprise/Developer Partner accounts); legacy REST Export API | "Minutes to hours" per their own marketing copy | No public per-scene price found; enterprise subscription | Not found | **Partial** — right output format, sales-gated access and price, not built for a 36h event |
| **Cesium ion Reconstruction** | **No** — photos only, confirmed from its own docs ("If capturing from video, choose settings that minimize motion blur" implies pre-extracted frames) | **3D Tiles** mesh (tileset, not one glTF), optional splats/point cloud | Photogrammetric reconstruction | Geo-referenced | REST API exists generally; reconstruction-trigger-via-API not confirmed in docs (tech preview) | ~1 hr for the sample dataset (confirmed, docs) | Not disclosed | Not found | **Partial/No** — wrong output shape (tiled, not a single mesh.glb) and photos-only |
| **Bentley iTwin Capture / ContextCapture (Azure-hosted)** | Photos and/or point clouds only, confirmed from its own API overview; video not mentioned | OBJ, FBX, point cloud, 3SM/3MX (no glTF/3D-Tiles mentioned on this page) | Photogrammetric reconstruction | Yes, with control points | **Yes**, public REST API group (`developer.bentley.com`), "Start Free" signup | Not disclosed | Not disclosed (pricing page exists, not detailed) | Azure-hosted infra (Bentley's own cloud); no confirmed direct Azure Marketplace billing | **Partial** — needs pre-extracted photos and undisclosed pricing/turnaround |
| **Niantic Scaniverse / Spatial Platform** | 360° video → USDZ (mesh+splat) confirmed (Jul 2026 feature); general video-to-mesh API not confirmed | FBX mesh, PLY/SPZ splat, USDZ (mesh+splat combo for robotics) | Photogrammetric reconstruction | Not confirmed | Mobile app + web workspace confirmed; **no public REST API found** in docs | Not disclosed | Not disclosed (consumer app, no listed API pricing) | Not found | **Partial** — real mesh export exists, but no confirmed programmatic API |
| **Polycam** | Photo + "video-based capture" mentioned in enterprise marketing; exact API input not confirmed from official docs | GLB/OBJ/GLTF/FBX/USDZ/STL (Basic plan export list) | Photogrammetric reconstruction (+ LiDAR Space Mode) | LiDAR-scale on supported iOS devices | **Yes**, Enterprise API, custom pricing ($500–2,500 one-time onboarding cited) | Not disclosed | Enterprise-only for API; consumer tiers $0–$30/mo | Not found | **Partial** — API exists but gated behind Enterprise sales, specs unconfirmed |
| **NVIDIA Omniverse NuRec + 3DGUT** | **No** — photo sets (COLMAP-based), not video, confirmed from NVIDIA's own smartphone-reconstruction blog | Gaussian-splat scene packaged as **USDZ** for Isaac Sim; no confirmed mesh export | Photogrammetric reconstruction (for robotics sim) | Not designed for our use | Open-source (`nv-tlabs/3dgrut`, self-host, CUDA-only) + gRPC service API | Not disclosed | Open source (self-host compute cost only) | Could self-host on AWS/Azure NVIDIA GPU instances | **No** — wrong domain (AV/robotics sim), splat-only output, photos not video |
| **NVIDIA Lyra** | Text/image, not real-world video | Gaussian splats for Isaac Sim | **Generative** | No | Research code (`nv-tlabs/lyra`) | N/A | Open source | Self-hostable | **No** — generative, not reconstruction |
| **Teleport by Varjo** | **Yes** — v2.0 added drone/DSLR input (confirmed via press coverage), cloud GPU processing | **Point cloud (.ply) only**, confirmed from Teleport's own export docs — no mesh format listed | Photogrammetric (Gaussian-splat-based) reconstruction | Not confirmed | Developer API exists (`teleport.varjo.com/docs`) for capture management/metadata, not full pipeline control confirmed | "Minutes" for large scenes (marketing claim, not independently verified) | $30/mo (15 scans), $0 free (5 scans) | Not found | **No** — no mesh export at all, splats are already ruled out for the 3S per the plan's own fps test |
| **Luma AI (Ray3.2 / Genie / Uni-1.1)** | Ray3.2 is **generative** video (text/image → new video), not video-to-3D ingestion; Genie is **text-only** input, no image/video upload, confirmed from Luma's own API docs | Genie: GLB/OBJ/FBX/USDZ (object-scale only) | **Generative**, both products | No | Public Developer API | N/A (not a reconstruction product) | Ray3.2 $0.15–$4.32/5s clip; Uni-1.1 $0.04–$0.12/image | Not found | **No** — this is not a video-to-3D-reconstruction product; the old Luma Labs *capture app* (photogrammetry-in-your-pocket) that this brief may be recalling was already confirmed discontinued in the prior doc |
| **Google Genie 3 / Project Genie** | Text/image prompts; not designed to ingest real drone footage for reconstruction | Real-time rendered world, no static export format | **Generative** (grounded in Street View data, not user footage) | No | **No public API** confirmed (Ultra-subscriber web app only) | N/A | Google AI Ultra subscription | No | **No** — no API, no export, generative |
| **Tencent HunyuanWorld-Voyager** | **No** — single image + camera path in, generates an RGBD video out; not "footage in" | Point-cloud video sequences ("3D formats," not a static textured mesh) | **Generative** (video diffusion model) | No | Open weights (self-host) since Sep 2025 | Not disclosed | Open source (compute only) | Self-hostable | **No** — generative from one image, not reconstruction from our footage |
| **Tencent Hunyuan3D (3.1)** | Image/text/sketch, multi-view image | GLB-class mesh, configurable poly budget | **Generative** (object-level) | No | Tencent Cloud API | Not disclosed | Not disclosed | Not found | **No** — object-scale generative asset tool (same category as Tripo/Meshy, already used for *parts*, not buildings, in our own plan §4b.3) |
| **Meta Horizon Hyperscape Capture** | Quest 3/3S native capture, not drone video | Splat, viewable only in the Hyperscape app | Photogrammetric (splat) reconstruction | Not confirmed | **No export API** — confirmed directly from Meta's own help page: "Meta does not currently offer a way to export either raw capture data or trained PLY files" | N/A | Free (Quest app) | No | **No** — no export path exists, confirmed |
| **Skydio 3D Scan** | Skydio-drone-native autonomous capture only | Not confirmed (sales-gated) | Photogrammetric | Not confirmed | API integration mentioned, contact-sales only | Not disclosed | Contact sales | Not found | **No** — hardware-locked to Skydio drones, incompatible with the DJI Mini 4K |
| **Esri Site Scan / ArcGIS Reality** | Drone imagery (photo-based) | Gaussian-splat layers (2026 feature); mesh export not confirmed for this path | Photogrammetric | Yes (GIS-georeferenced) | ArcGIS API/SDK exists generally; specific reconstruction-trigger API not confirmed | Not disclosed | Enterprise licensing | Possible via Azure (Esri has Azure partnerships, not confirmed for this product) | **No** for our timeline — enterprise GIS platform, sales-gated, splat-first |
| **Agisoft Metashape cloud (Service Provider license)** | Photos (classic photogrammetry, same as prior doc's coverage) | Textured mesh | Photogrammetric | Yes (GCPs) | Documented Python API + network processing | Not disclosed | Pay-per-use: $155.90/mo minimum (~$1.56/hr at 100 hrs) | Self-hostable on AWS/Azure GPU; no confirmed direct marketplace listing | Already covered in `p1-fast-recon.md` §4 as a desktop tool; this is its SaaS billing angle, same fundamental floor as classic MVS (§1 of that doc) |
| **AWS Marketplace photogrammetry AMIs** (3Dflow, generic "Image-Based 3D Modeling") | Photos | Textured mesh (SfM/MVS) | Photogrammetric | Depends on tool | Self-hosted on EC2 GPU instances, billed through AWS Marketplace | Depends on instance/dataset | Per-instance-hour + AMI software fee | **Yes**, directly AWS-Marketplace-billable | Confirms the brief's premise (AMIs exist) but same classic-MVS time floor as `p1-fast-recon.md` §1 — no speed or fidelity advantage over self-hosting the plan's own OpenMVS chain |
| **Nira** | N/A — not a reconstruction service | Viewer/collaboration platform only; ingests outputs of RealityScan/Metashape/3DF Zephyr | N/A | N/A | Upload API + Viewer API, but for *viewing*, not generating | N/A | Not disclosed | Not found | **No** — doesn't reconstruct anything itself |
| **Odyssey-3 / SpAItial (Echo) / Wayve GAIA** | Odyssey-3: multi-domain "physical world model," not a video-in-mesh-out product confirmed; SpAItial: image/prompt → persistent 3DGS world (generative); Wayve GAIA: driving-scenario generation for AV sim | Not applicable to our shape | **Generative**, all three | No | Not confirmed public/general-purpose API for any of the three | N/A | N/A | Not found | **No** — none has a confirmed video-to-mesh path; all are research/vertical-specific (autonomous driving, avatar-scale) |
| **"Gracia"** | — | — | — | — | — | — | — | — | **Not found** — no product by this name turned up in searches; may be a different/misremembered name |
| Azure Remote Rendering | N/A | N/A | N/A | N/A | N/A | N/A | N/A | Azure-native | **Not applicable** — a GPU-streaming/rendering service, not a reconstruction service (per the brief's own framing) |
| NVIDIA DGX Cloud | N/A | N/A | N/A | N/A | N/A | N/A | N/A | Neither AWS nor Azure marketplace (own NVIDIA billing) | **Not applicable** — raw GPU compute, not a reconstruction service; would only matter as hosting for a self-run VGGT/OpenMVS-CUDA box, already covered in `p1-fast-recon.md` §5 |

---

## 2. Detail on the two services worth a second look

### 2.1 KIRI Engine — the only service whose input shape actually matches ours

Verified directly from `docs.kiriengine.app`'s Photo Scan → Video Upload page: the REST API's
video-upload endpoint states **"The video resolution must not exceed 1920x1080, and the duration
should be no longer than 3 minutes"** — almost exactly one of the plan's own capture passes
(§2.1: "2–3 min each"). The app also documents drone use for building exteriors directly
(`kiriengine.app/faq/can-i-use-a-drone-to-3d-scan-with-kiri-engine`, `kiriengine.app/blog/
from-2d-video-to-3d-magic`), and its 4.2 release added an in-app **Measure → Rescale** tool:
pick two points, the app gives an AI-estimated distance, then you type in the real measurement
and it rescales the whole model — functionally the same "apply real scale after the fact from a
known dimension" pattern as our own `pipeline/calibrate.py`'s `known_dimension` method. Mesh
export covers OBJ/STL/FBX/GLTF on the free Basic plan; the $6.99/mo Pro plan raises the photo cap
(200 vs 70) and adds 3DGS-to-Mesh, PBR materials, and priority processing.

**What's unverified, confirmed as gaps, not just "didn't look"**: no official processing-time
number anywhere in its docs or blog (turnaround is a live risk for a <1 min or even <10 min demo
beat); no published triangle-count or texture-atlas control comparable to our `--target-triangles`/
`--max-texture-size` flags, so hitting the 200k/4096 budget precisely is unverified; no official
building-exterior accuracy benchmark, only a community Facebook thread and a YouTube "dimensional
accuracy" video (both anecdotal, not cited as evidence here); and no AWS/Azure Marketplace billing
found, so it draws from neither of the team's cloud credit pools. **Recommendation: worth a single
10-minute smoke test on one of the corpus clips before the event** (upload the 1920px, ≤3 min clip
already produced by the pipeline's own `ffmpeg` step, $1 credit) purely as an **offline fallback if
`hfbox` becomes unreachable at the venue** — not worth wiring into the pipeline's happy path.

### 2.2 World Labs Marble — real mesh export, real video input, real fidelity risk

This corrects and extends the prior doc's brief note. Confirmed from `docs.worldlabs.ai/marble/
export/mesh` and `docs.worldlabs.ai/api/faq`: the World API's `POST /marble/v1/worlds/<id>:export`
does take `{"asset_type": "mesh", "format": "glb"}`, video is a documented input alongside
text/image/multi-image/panorama, and the export ships **two real triangle-mesh tiers** (~600k
triangles + texture maps on the Standard-plan "collider," ~1M triangles + vertex colors on the
Pro-plan "high-quality" mesh) plus a `metric_scale_factor` + `ground_plane_offset` pair specifically
so a consumer can convert Marble's arbitrary model units to meters — the same kind of after-the-fact
scale application the plan's own `calibrate.py` does, just from Marble's own declared factor
instead of an ArUco/GPS measurement.

The disqualifying finding, also from World Labs' own materials plus one independent test cited in
search results: Marble's own FAQ explicitly frames the model as one that **infers** "layout, depth,
lighting, and spatial structure" rather than measuring it, and for the (admittedly different)
robotics use case World Labs' own blog says the model generates "entirely synthetic, imagined
scenes." A third-party hands-on test (not this session's own, flagged as such) reported "some items
lacked clarity, text details were missing, and certain merchandise did not read as intended" even
with careful input. That is a direct conflict with the plan's MUST: "faithful to the REAL building
(reconstruction, not hallucinated/stylised generation)" and specifically with the Sat 1–3 PM
accuracy-test beat, which needs the mesh's real dimensions to match a hand-measured tape reading to
within a few percent — a generative model has no guarantee of that beyond whatever its declared
`metric_scale_factor` claims, and nothing in its docs states a geometric-accuracy bound. **Use it
only as a demo curiosity ("we also tried a generative world model") exactly as the prior doc already
recommended — do not put it anywhere near the accuracy table.**

---

## 3. How this slots into the existing plan (it mostly doesn't)

The plan already has a preview path and a full path, both self-hosted and both already measured
working (§2.6 of the plan; `p1-fast-recon.md` §6): the self-hosted VGGT+TSDF chain hits ≈10–15 s
steady-state on a warm GPU (well under the <1 min preview goal), and the existing pycolmap+OpenMVS
CPU chain produces the 200k-tri, 4096-atlas full mesh in 9–31 min. Nothing surveyed here beats
either number with a comparable or better fidelity/format guarantee:

- **As the preview (<1 min)**: no cloud service here has a documented sub-minute end-to-end
  turnaround for a ~100-frame drone orbit. Marble's own docs say its high-quality mesh export alone
  takes "up to an hour." KIRI Engine, DroneDeploy, and Bentley/Cesium don't publish a number at all.
  The self-hosted VGGT path stays the preview.
- **As the full mesh**: nothing surveyed both (a) reliably preserves real building geometry and
  (b) is cheap/fast/accessible enough for a 36-hour event without a sales call. Marble fails (a).
  DroneDeploy, Bentley, Skydio, and Esri fail (b) — all are enterprise-sales-gated with no published
  per-scene price or confirmed same-day account approval. KIRI Engine is the only one that's
  self-service and cheap, but its accuracy for a real building exterior at our budget is unverified.
  The existing OpenMVS CPU chain (or its already-planned OpenMVS-CUDA/`pycolmap.patch_match_stereo`
  GPU upgrade on a rented L40S, per `p1-fast-recon.md` §1.3/§5) stays the full path.
- **As a replacement for anything**: no. The one legitimate use for any of this is **KIRI Engine as
  a break-glass fallback** if `hfbox` is unreachable at the venue (§9 of the plan already has a
  fallback ladder for pipeline failure; this would slot in as one more rung, generating *a* mesh —
  probably out of budget on triangles/texture and unverified on accuracy — rather than nothing).
  That's worth deciding at the Sat 6 PM checkpoint if it comes to it, not before.

---

## 4. Verified from official docs vs. inferred

**Verified hands-on this session (official docs/pages fetched directly, listed per-service above
with the exact quoted claim)**: KIRI Engine's video-upload resolution/duration cap (`docs.
kiriengine.app`); World Labs Marble's mesh export tiers, plan gating, and processing time
(`docs.worldlabs.ai`); Marble's video/multi-image/panorama input list and public API availability
(`worldlabs.ai/blog/announcing-the-world-api`); Meta's confirmation that Hyperscape has no export
path (`meta.com/help/quest/...`); Teleport by Varjo's point-cloud-only export format (`teleport.
varjo.com/help/.../export`); Cesium ion Reconstruction's photos-only input and ~1 hr sample
turnaround (`cesium.com/learn/ion/reconstruction/`); Bentley's reality-modeling API input/output
list (`developer.bentley.com/api-groups/reality-modeling/`); Luma's API product list not including
any video-to-3D reconstruction product (`lumalabs.ai/api`); NVIDIA NuRec's photo-based (COLMAP),
USDZ-output, Isaac-Sim-scoped pipeline (NVIDIA's own smartphone-reconstruction blog post).

**Inferred / from secondary sources, flagged inline above where used**: KIRI Engine's real-world
building-exterior accuracy (community Facebook/YouTube posts, not an official benchmark); Marble's
qualitative fidelity shortfalls (a third-party review, not this session's own test); DroneDeploy's
"minutes to hours" turnaround and Polycam's/Skydio's/Esri's pricing (marketing copy or aggregator
sites, not a primary pricing page with numbers); AWS Marketplace AMI existence (search-result
listing pages, not independently launched or timed this session — no cloud resources were created,
consistent with the task's no-paid-API-calls / no-account-creation constraint).

**Not independently benchmarked this session** (no paid signups, no account creation, no footage
uploaded to any of these services, per the task's own constraint): all turnaround-time and
per-scene-accuracy numbers above are read from vendor docs/marketing or third-party reports, never
measured against our own footage. If KIRI Engine is actually piloted as a fallback, that first real
test (cost: $1, one credit) should happen before, not during, the event.

---

## 5. Sources (all accessed 2026-09-24)

**World Labs Marble**
- Mesh export docs: https://docs.worldlabs.ai/marble/export/mesh
- API FAQ (units, metric_scale_factor): https://docs.worldlabs.ai/api/faq
- World API announcement (video input, access): https://www.worldlabs.ai/blog/announcing-the-world-api
- Pricing: https://docs.worldlabs.ai/api/pricing · https://marble.worldlabs.ai/pricing

**KIRI Engine**
- API docs home + Photo Scan category: https://docs.kiriengine.app · https://docs.kiriengine.app/category/photo-scan
- Video-to-3D workflow / video upload limits: https://www.kiriengine.app/blog/from-2d-video-to-3d-magic
- API overview + credit pricing: https://www.kiriengine.app/api
- Drone scanning FAQ: https://www.kiriengine.app/faq/can-i-use-a-drone-to-3d-scan-with-kiri-engine
- Measure/Rescale tool (4.2 release notes): https://www.kiriengine.app/blog/kiri-engine-4.2-release
- Pricing: https://www.kiriengine.app/pricing

**DroneDeploy**
- API sections: https://help.dronedeploy.com/hc/en-us/sections/1500000794002-API
- REST Export API: https://help.dronedeploy.com/hc/en-us/articles/1500004963722-REST-Export-API (fetch blocked, 403; summarized from search index)
- Third-party API profile: https://github.com/api-evangelist/dronedeploy
- Non-supported-drone upload: https://help.dronedeploy.com/hc/en-us/articles/12983230356631 (fetch blocked, 403; summarized from search index)

**Cesium ion**
- Reconstruction workflow: https://cesium.com/learn/ion/reconstruction/
- Reality Modeling / AI-Powered Analysis announcement: https://cesium.com/blog/2025/07/22/introducing-reality-modeling-and-analysis/
- 3DGS + 3D Tiles: https://cesium.com/blog/2026/04/27/3d-gaussian-splats-lod/

**Bentley iTwin Capture**
- Reality Modeling API group: https://developer.bentley.com/api-groups/reality-modeling/
- Reality Modeling API overview: https://developer.bentley.com/apis/contextcapture/overview/

**Niantic Scaniverse / Spatial Platform**
- Docs home: https://www.nianticspatial.com/docs/scaniverse/
- USDZ export for robotics sim: https://www.nianticspatial.com/blog/usdz-scaniverse
- Platform overview: https://www.nianticspatial.com/blog/scaniverse

**Polycam**
- Export formats: https://learn.poly.cam/hc/en-us/articles/27756102599572
- Enterprise platform announcement: https://www.businesswire.com/news/home/20250226523971/en/
- AEC solutions page: https://poly.cam/solutions/architecture-engineering-construction
- Pricing: https://poly.cam/pricing

**NVIDIA**
- Omniverse NuRec: https://developer.nvidia.com/omniverse/nurec
- NuRec docs (how it works): https://docs.nvidia.com/nurec/archives/26.03/basics/how-nurec-works.html
- Smartphone-to-Isaac-Sim reconstruction workflow: https://developer.nvidia.com/blog/reconstruct-a-scene-in-nvidia-isaac-sim-using-only-a-smartphone/
- 3DGUT / 3dgrut: https://github.com/nv-tlabs/3dgrut
- Lyra: https://github.com/nv-tlabs/lyra · https://research.nvidia.com/labs/toronto-ai/lyra
- InstantNuRec: https://github.com/NVIDIA/instant-nurec

**Teleport by Varjo**
- Export docs: https://teleport.varjo.com/help/en_US/productivity/export
- Developer docs (capture management): https://teleport.varjo.com/docs/
- Teleport 2.0 / drone-DSLR input: https://varjo.com/news/varjo-launches-teleport-2-0-a-generational-leap-in-photorealistic-3d-capture-for-creators
- Pricing: https://get.teleport.varjo.com/pricing

**Luma AI**
- API product page: https://lumalabs.ai/api
- LLM-facing info page: https://lumalabs.ai/llm-info

**Google Genie 3**
- Official model page: https://deepmind.google/models/genie/
- Project Genie rollout: https://blog.google/innovation-and-ai/models-and-research/google-deepmind/project-genie/

**Tencent Hunyuan**
- HunyuanWorld-Voyager: https://github.com/Tencent-Hunyuan/HunyuanWorld-Voyager
- HunyuanWorld-1.0: https://github.com/Tencent-Hunyuan/HunyuanWorld-1.0
- Hunyuan3D global/API: https://www.tencent.com/en-us/articles/2202235.html · https://replicate.com/tencent/hunyuan-3d-3.1

**Meta Hyperscape**
- Getting started / export limitation: https://www.meta.com/help/quest/1088536553019177/
- App listing: https://www.meta.com/experiences/meta-horizon-hyperscape-capture-beta/8798130056953686/

**Skydio, Esri, Agisoft, AWS Marketplace, Nira, Odyssey/SpAItial/Wayve**
- Skydio 3D Scan: https://www.skydio.com/software/3d-scan
- Esri Site Scan / ArcGIS Reality Q2 2026 update: https://www.esri.com/arcgis-blog/products/site-scan/imagery/whats-new-in-site-scan-for-arcgis-q2-2026
- Agisoft Service Provider (pay-per-use) pricing: https://www.agisoft.com/buy/saas/service-provider-license/
- AWS Marketplace 3D reconstruction listings: https://aws.amazon.com/marketplace/pp/prodview-7k7mhczkah2zq · https://aws.amazon.com/marketplace/seller-profile?id=bc1120fb-7263-4683-9301-4b7470f9a2d9
- Nira: https://nira.app/
- Odyssey-3: https://explainx.ai/blog/odyssey-3-world-model-launch-2026
- SpAItial (Echo): https://spaitial.ai/
- Wayve GAIA-4: https://wayve.ai/thinking/gaia-4/

**Local repo (read, not re-cited inline above)**
- `docs/AirTool implementation plan.md` §1, §2
- `docs/research/p1-fast-recon.md` (classic photogrammetry survey this doc extends)
