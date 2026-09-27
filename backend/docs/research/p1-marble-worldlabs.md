# Marble by World Labs, in depth: does it fit anywhere in the P1 pipeline?

Scope: a deep pass on **World Labs Marble** specifically (product, inputs/outputs, fidelity, API,
Unity/Quest fit, aerial-capture evidence), extending — not repeating — the Marble coverage already
in `docs/research/p1-cloud-world-models.md` §2.2/§5 and `docs/research/p1-fast-recon.md` §4. Those
two docs already established, from official World Labs docs: Marble's World API accepts
text/image/multi-image/panorama/video, exports a **textured GLB mesh** in two tiers (~600k tri +
texture maps "collider" on the Standard plan, ~1M tri + vertex colors "high-quality mesh" on the
Pro plan) plus `.spz`/`.ply` splats, ships a `metric_scale_factor` + `ground_plane_offset` for
converting to real meters, costs **~$4/scene** (~1,500 credits world-gen + ~3,500 credits HQ mesh
export, $1/1,250 credits), and is **generative** — World Labs' own FAQ says it "infers" layout/
depth/lighting/spatial structure rather than measuring it, which conflicts with the plan's MUST of
faithful-not-hallucinated reconstruction. This doc does not re-derive any of that. It adds: exact
input limits, exact splat counts, confirmed API rate limits and ToS/commercial terms, confirmed
generation latency, a from-the-source Unity/Quest integration workflow (including a documented
performance number against our own fps budget), and the one real aerial/building demo found in
World Labs' portfolio — which turns out to belong to a *different*, newer, not-yet-GA product
(Atlas), not Marble.

**Bottom line: nothing here changes the prior docs' verdict. Marble is not a candidate for the
preview or the full measured mesh. Its best-fit role, if used at all, is a generated environment
"skybox" around the real measured mesh — and even that has real, newly-confirmed friction on the
Unity/Quest splat path specifically.**

---

## 1. Summary table

| Question | Finding | Confidence |
|---|---|---|
| Product / release | Marble: GA Nov 12, 2025 (no product version number). World API: launched Jan 21, 2026. API model names: `marble-1.0`, `marble-1.0-draft`, `marble-1.1`, `marble-1.1-plus`. | Confirmed (World Labs blog, docs.worldlabs.ai/api) |
| Accepts our drone video? | Yes as an input type, but **video is capped at 30 s / 100 MB** on the Marble app's own upload guidance — our 2–3 min orbit clips must be trimmed or re-sent as multi-image/panorama instead | Confirmed limit from World Labs docs; whether the *API's* video endpoint enforces the same 30 s cap as the app's upload UI is not independently re-confirmed (two official pages list slightly different accepted formats — see §2) |
| Splat output | Three export options: **2M PLY**, **2M SPZ**, **500K SPZ** | Confirmed, World Labs' own Unity export doc |
| Mesh output | Two tiers, per prior docs: ~600k tri textured GLB, ~1M tri vertex-colored GLB; **typical file size 100–200 MB** | Confirmed tri counts (repeat-fetched, consistent both times); file-size figure confirmed but its exact tier mapping is ambiguous in the source page |
| Fidelity | Generative, own FAQ says "infers" not measures (prior docs). The one real-building demo with "aerial paths" found in World Labs' materials (Stanford Main Quad, 2–25 images) belongs to **Atlas**, a separate model in early access since Sep 1, 2026 — **not Marble** | Confirmed distinction (Atlas ≠ Marble); no Marble-specific aerial/building demo found |
| API access | REST, header auth (`WLT-Api-Key`), `POST /marble/v1/worlds:generate`, export via `POST /marble/v1/worlds/<id>:export`, poll `GET /marble/v1/operations/{id}` | Confirmed, docs.worldlabs.ai/api |
| Rate limits | Default: **~3 req/min, 60 req/hr** (generation-start only). Approved higher-throughput accounts: ~30 req/min (standard models), ~90 req/min (draft models) | Confirmed, docs.worldlabs.ai/api/rate-limits |
| Commercial/hackathon ToS | Free tier = personal non-commercial only. Paid app accounts and paid API accounts own their output and may use it commercially, subject to World Labs' license-back rights taking precedence in a conflict. Explicit accuracy disclaimer: outputs "may not accurately represent real-world physics" and may be "incorrect, biased, incomplete, or technically inaccurate." Liability capped at the greater of $100 or fees paid in the prior 12 months | Confirmed, docs.worldlabs.ai/terms-of-service |
| Latency | World generation: **~5 min** (confirmed, quickstart docs). HQ mesh export: **up to an hour** (prior doc). A more granular draft(~20s)/preview(~30s) breakdown appeared in a secondary source but could not be independently re-confirmed against the primary docs page this session | Generation and mesh-export numbers confirmed from primary docs; granular breakdown unverified |
| Unity/Quest fit | World Labs itself publishes a Unity export guide, incl. Quest 3 VR settings. Real friction: 500K SPZ has a **known import bug** (needs third-party PLY conversion first); 2M splats **crash Quest 3 standalone builds**; the *recommended* 500K tier runs **~12 fps in Unity** on Quest | Confirmed, docs.worldlabs.ai/marble/export/gaussian-splat/unity |
| Mesh in Unity | GLB is standard glTF — should import directly, no special plugin needed (unlike splats) | Inference from format, not separately hands-on tested by World Labs' docs or this session |
| Recommendation | Not preview, not full/accuracy mesh. At most a generated backdrop/skybox layered behind the real mesh, or a break-glass demo curiosity; never near the accuracy-test beat | This session's synthesis |

---

## 2. Inputs, confirmed limits (extends prior docs' "yes it accepts video/multi-image/pano")

From World Labs' own Marble app input guide (`docs.worldlabs.ai/marble/create/prompt-guides`) and
the API quickstart (`docs.worldlabs.ai/api`):

- **Text**: ≤2,000 characters.
- **Single image**: ≤20 MB, PNG (preferred)/JPG/WebP, recommended 1024px on the long side, 16:9 or
  9:16 (or in between).
- **Multi-image**: multiple images, each with an optional **azimuth** (degrees) hint for camera
  position — notable because an orbit capture naturally has known relative azimuths, a better
  structural fit than single-image. Exact max image count not found in either doc fetched this
  session (**unverified**).
- **Panorama**: full 360° equirectangular, 2:1 aspect, recommended 2,560px wide.
- **Video**: per the app's upload guide, **max duration 30 seconds, max file size 100 MB**,
  formats MP4/MOV/WebM. The API quickstart's own input-type list separately gives video formats as
  MP4/MOV/**MKV** — a minor, confirmed (both fetched directly) inconsistency between two official
  pages; which one governs the actual API video endpoint's duration cap is **unverified**.
- **3D structure import ("Chisel")**: GLB/FBX, ≤100 MB — a structure-guided generation mode, not
  evaluated further here (out of scope for this brief).

**What this means for our footage**: our capture passes are 2–3 min (`p1-cloud-world-models.md`
§2.1 cites the plan's own §2.1). If the 30 s/100 MB video cap is real for the API (not just the
app UI), a full orbit pass cannot be uploaded as-is — it would need to be trimmed to a 30 s
sub-clip, or resubmitted as a handful of still frames via multi-image mode (which the plan's
pipeline already extracts anyway). This is a concrete, previously-unflagged practical constraint,
not just the "yes, video is accepted" the prior docs recorded.

---

## 3. Outputs, exact counts (extends prior docs' format-level coverage)

**Splats** (`docs.worldlabs.ai/marble/export/gaussian-splat/unity`): exactly three export options —
**2M-point PLY**, **2M-point SPZ** (Adobe's compressed splat format), and a lightweight **500K-point
SPZ**. This is the first place either research pass has pinned an exact splat count; prior docs
only had the format list (.spz/.ply), not the counts.

**Mesh** (`docs.worldlabs.ai/marble/export/mesh`, re-fetched this session): confirms the prior
docs' ~600k-tri (textured GLB) / ~1M-tri (vertex-colored GLB) split, and adds one new figure —
**typical exported file size is 100–200 MB** for the textured mesh tier. The page also references a
further "export file specs" page for exact texture resolution/format that did not resolve on fetch
this session; **texture pixel resolution and file format (JPG vs PNG) remain unconfirmed**.

For scale: our own budget is ~200k tri + one 4K JPEG atlas. Marble's lighter mesh tier (~600k tri)
is already 3× our triangle budget before any decimation, and its file size (100–200 MB) is far
heavier than our packaged `mesh.glb` — either tier would need real post-processing (decimation,
re-bake) to fit our Quest 3S budget even before considering the fidelity question in §4.

---

## 4. Fidelity: the Atlas/Marble mix-up worth flagging explicitly

The prior docs already cite World Labs' own FAQ language ("infers" layout/depth/lighting/spatial
structure) and one third-party report of missing detail. Searching specifically for an
aerial-capture or real-building comparison this session surfaced exactly one relevant demo: World
Labs demonstrating **Stanford's Main Quad** reconstructed from 2–25 ground-level images, with the
model generating "aerial paths beyond the captured camera positions" and a third-party analysis
noting the model "may accurately preserve observed façades while generating roofs, courtyards or
hidden rooms" as view count drops. **This demo is for Atlas, World Labs' second model, in early
access since Sep 1, 2026 — not Marble**, per the same source (`kingy.ai/blog/
world-labs-atlas-world-model-deep-dive`) and confirmed via a separate release-date search. Atlas is
explicitly framed as future technology that will "power future Marble versions," i.e. not yet
shipped in the product this brief is about.

**No Marble-specific aerial/drone/building-exterior demo or comparison was found in this session's
searches.** Per the verify-before-flag rule, this is reported as "not found in the searches run,"
not as "does not exist" — absence of search results is not proof of absence, but it is also not
evidence Marble has been tested this way. A separate third-party review noted Marble performs
better with photorealistic input than illustrated/stylized input (a hallucinated-fantasy-tavern
test produced "grainy and buggy" results), consistent with a model trained mostly on photoreal
data — mildly reassuring for photo-real drone input, but not a substitute for an actual accuracy
test.

The multi-image mode's **azimuth hint** (§2) is the one structural feature that could plausibly
improve fidelity for an orbit capture specifically (it's closer to giving the model real camera
poses than a single photo is) — but nothing in World Labs' docs states a geometric-accuracy bound
even when azimuths are supplied, so this remains a hypothesis, not a confirmed capability.

---

## 5. API: rate limits, auth, and commercial/hackathon terms (new — prior docs didn't cover this)

**Auth & endpoints** (`docs.worldlabs.ai/api`): header-based key, `WLT-Api-Key: <key>`; generate via
`POST /marble/v1/worlds:generate` with `display_name`, `model`, `world_prompt`; export via
`POST /marble/v1/worlds/<id>:export`; poll `GET /marble/v1/operations/{operation_id}`. Local file
uploads use a two-step prepare-upload (signed URL) → PUT pattern.

**Rate limits** (`docs.worldlabs.ai/api/rate-limits`): default tier is **~3 requests/min and 60/hr**
for starting generations; approved higher-throughput accounts get ~30/min (standard models,
`marble-1.0`/`1.1`/`1.1-plus`) or ~90/min (`marble-1.0-draft`). Enforcement is per-account (not
per-key), rolling-window, "approximate." For a hackathon use case (a handful of one-off
generations), even the default tier is not a real constraint.

**Two separate money paths, confirmed**: the Marble *app* has its own subscriptions — **Free** (4
generations, text/image/panorama, personal non-commercial only), **Standard $20/mo**, **Pro
$35/mo** (40,000 credits ≈ 25 worlds, **commercial rights included**, adds scene expansion and
high-resolution/HQ mesh export), **Max $95/mo**. The *API* is separate pay-as-you-go credits
($1/1,250 credits, per prior docs), and World Labs' own FAQ states **"Credits purchased for the
Marble app cannot be used with the API"** — the two don't share a wallet.

**Terms of Service** (`docs.worldlabs.ai/terms-of-service`), the specific commercial/hackathon
question the brief asked for: Free-tier output license is "personal, Non-Commercial Use" only.
Paid app accounts "own all rights, title, and interest in and to Outputs" and may use them for "any
purpose, including Commercial Purposes." Paid API accounts may additionally "distribute, and
sublicense the Output for Commercial Purposes" into a downstream product, though "World Labs'
rights shall take precedence" in any conflict with an end-user license. Practically: **a hackathon
demo run on a free account is fine as a stage curiosity (non-commercial), but if AirTools wants to
ship any Marble-derived asset in a product or pitch it as part of a monetized deliverable, that
needs a paid app plan (Pro, for the commercial-rights language specifically) or paid API credits**
— worth knowing before anyone assumes the free tier covers a demo used in a pitch/prize context.

The ToS also carries an explicit, sourced accuracy disclaimer directly relevant to this project's
MUST: outputs are warranted **not** to be "accurate, complete, reliable, current, error-free,"
"AI-generated 3D worlds and spatial content may not accurately represent real-world physics," and
liability is capped at the greater of $100 or the prior 12 months' fees. This is the strongest,
most directly-sourced version yet (World Labs' own legal text, not a third-party review) of the
fidelity risk the prior docs already flagged from softer evidence.

---

## 6. Latency (new — prior docs only had the "up to an hour" HQ-mesh-export figure)

World Labs' own API quickstart states **world generation should take about 5 minutes to
complete**. Combined with the prior doc's already-confirmed "up to an hour" for HQ mesh export,
a full generate-then-export round trip is realistically **5 minutes to just over an hour** — far
outside both halves of our own pipeline (VGGT preview ≈19 s–15 s steady-state per
`p1-fast-recon.md`; OpenMVS full mesh 9–31 min). A more granular draft(~20s)/first-preview(~30s)
breakdown surfaced in a secondary aggregator's paraphrase of what it claimed was official docs
content, but this session could not independently re-fetch and confirm that exact figure against
the primary docs page — **flagged as unverified**, not used as a claimed fact above.

---

## 7. Unity/Quest compatibility (new — prior docs didn't evaluate this at all)

World Labs itself publishes a Unity export guide with **Quest 3 VR-specific settings**
(`docs.worldlabs.ai/marble/export/gaussian-splat/unity`) — this is a semi-official supported path,
not just a community workaround:

- **Splat path**: no native Unity splat-to-mesh support exists; the documented route is the free,
  third-party **aras-p `UnityGaussianSplatting`** plugin (a patched fork is recommended for
  draw-order and SPZ-import bugs on the mainline branch). Of the three export options (§3), **2M
  SPZ imports directly**; **500K SPZ has a known import bug** requiring conversion to PLY via a
  third-party web tool first. Quest 3 standalone guidance: Unity 6.0 (6000.0.23f1), URP with HDR
  enabled (its absence causes a black screen in VR), Vulkan, **Multi-view rendering, not Single
  Pass Instanced** (which also causes a black screen), and **500K splats recommended — 2M splats
  crash Quest 3 standalone builds**.
- **The one number that matters most for our fps budget**: World Labs' own doc reports **500K
  splats run at ~12 fps in Unity** on this Quest 3 setup (vs. ~19 fps in PlayCanvas, a different
  renderer). Our own measured fps table (`p1-cloud-world-models.md`/plan §9): 50k splats/72 fps,
  200k/60 fps, 400k/36 fps on the Quest 3S. **Marble's lightest splat export (500K) is already
  below our own 400k/36fps floor and roughly 3× the fastest-safe point in our own table** — its
  splat output is not a good direct fit for in-headset use without either dropping well below
  World Labs' own recommended 500K tier or investing real engineering time beyond what World Labs
  documents.
- **Mesh path**: GLB is standard glTF; it should import into Unity directly with no special
  plugin, unlike the splat path. This is inferred from the format itself (Unity has native/
  first-party glTF import), not separately hands-on-verified by World Labs' docs or this session —
  flagged as an inference, not a confirmed test.

---

## 8. Recommendation for AirTools

**Where it fits: nowhere in the primary preview/full-mesh path. At most, an optional generated
backdrop layered around (not replacing) the real measured mesh, or a stage-demo curiosity —
exactly the role the prior two docs already assigned it, now with sharper reasons why:**

1. **Not the preview.** ~5 min generation + up to an hour for a mesh export is far slower than our
   existing <1 min VGGT preview (§6). No contest.
2. **Not the full/accuracy mesh.** Generative by World Labs' own FAQ and, now, its own ToS
   ("may not accurately represent real-world physics," no accuracy warranty, §5) — a direct
   conflict with the plan's measurement-accuracy MUST. The only real building-capture demo found
   in World Labs' materials belongs to Atlas, a different, not-yet-GA product (§4) — there is no
   evidence Marble itself has been validated against a real building exterior.
3. **Possible fit: a generated skybox/backdrop.** Marble is designed to generate a plausible
   *environment around* a viewpoint, not to measure a subject — that's a better match for
   "surround the real reconstructed building with a plausible city/sky backdrop" than for
   reconstructing the building itself. If pursued: generate from a single site photo (fast, cheap,
   §5 credits), export the GLB mesh tier (not splats — §7's Quest fps numbers rule out the splat
   path for in-headset use at any of Marble's three splat tiers), decimate it hard, and render it
   as a static, non-interactive backdrop dome placed well outside the real mesh's bounding volume
   so there's no possibility of confusing generated and measured geometry.
4. **Integration sketch, if pursued**: capture one hero photo (or ≤30 s trimmed clip / multi-image
   set with azimuth hints, §2) of the site → `POST worlds:generate` (marble-1.0 or 1.1, ~5 min) →
   poll `GET operations/{id}` → `POST worlds/<id>:export {"asset_type":"mesh","format":"glb"}` →
   decimate/re-bake in the existing `trimesh` post-process step already in the pipeline → tag it
   distinctly in `scene.json` (e.g. `"generated_backdrop": true`) so it can never be mistaken for
   or merged with the measured `mesh.glb`.
5. **Risks**: (a) the 30 s/100 MB video cap (§2) means our actual footage needs trimming or a
   format switch to multi-image before it can go in at all; (b) commercial/hackathon ToS requires
   a paid app plan (Pro, $35/mo) or paid API credits if the output is used beyond a personal/
   non-commercial demo (§5); (c) the Unity/Quest splat path has real, World-Labs-documented
   friction (500K SPZ import bug, 2M crashes standalone builds, ~12 fps even at the light tier,
   §7) — mesh export is the only path worth pursuing on-device; (d) API default rate limit (3
   req/min) is a non-issue at hackathon scale.
6. **Cheap validation test (not run this session, per the brief)**: reuse an already-extracted
   hero JPEG frame from an existing corpus site (zero new capture cost — the pipeline already has
   these) as a single-image `worlds:generate` call (~1,500 credits ≈ **$1.20**), then export the
   GLB mesh tier only (skip the HQ/Pro tier and skip any splat export for this first pass, ~3,500
   more credits ≈ **$2.80** if the mesh alone isn't sufficient to judge). **Estimated total: ~$1.20
   for the world alone, or ~$4 including the mesh export** — API credits have a $5 minimum
   purchase, so realistic minimum spend to run this test at all is **$5**. Trivial against the
   $10k/$10k AWS/Azure budgets (note: Marble is not AWS/Azure-marketplace-billable per the prior
   doc, so this spend comes from neither cloud credit pool — it would need a separate small direct
   payment to World Labs). Compare the generated building silhouette/proportions by eye against the
   real OpenMVS mesh already produced for that same site — not a rigorous accuracy benchmark, just
   a fast go/no-go on whether the backdrop idea is visually worth pursuing before the event.

---

## 9. Verified vs. inferred

**Verified this session, official sources fetched directly**: Marble app input limits — text/image/
panorama/video/Chisel size and format caps (`docs.worldlabs.ai/marble/create/prompt-guides`);
splat export options and exact counts, Unity plugin requirements, SPZ import bug, Quest 3 render
settings, and the 500K/~12fps performance figure (`docs.worldlabs.ai/marble/export/gaussian-splat/
unity`); mesh export triangle counts and typical file size, re-confirmed (`docs.worldlabs.ai/marble/
export/mesh`); API auth, endpoints, and input-type list (`docs.worldlabs.ai/api`); rate limits
(`docs.worldlabs.ai/api/rate-limits`); ToS commercial-use and accuracy-disclaimer language
(`docs.worldlabs.ai/terms-of-service`); Marble app subscription tiers and pricing (search-indexed
from `marble.worldlabs.ai/pricing`, not independently re-fetched directly — see below); Atlas's
Sep 1, 2026 early-access launch as a distinct, newer product from Marble, and that the Stanford
Main Quad aerial-path demo belongs to Atlas, not Marble (`kingy.ai` analysis, cross-checked against
a separate release-date search).

**Not independently re-fetched, taken from a search-engine paraphrase of vendor pages**: Marble
app subscription tier pricing (Free/$20/$35/$95) came from a WebSearch summary citing `marble.
worldlabs.ai/pricing` and aggregator pages, not a direct WebFetch of that pricing page (repeated
WebFetch attempts to `docs.worldlabs.ai` pages intermittently returned a domain-verification error
this session rather than page content — noted where it happened); the granular draft/preview/full
generation-latency breakdown in §6, explicitly flagged there as unverified.

**Not found in this session's searches** (absence noted per the verify-before-flag rule, not
asserted as non-existent): an official or independent Marble-specific test against real aerial
drone or building-exterior capture; exact texture pixel resolution/format for the mesh export;
a maximum image count for multi-image mode; confirmation of which video format/duration list
(app UI's MP4/MOV/WebM/30s, or the API quickstart's MP4/MOV/MKV) actually governs the API's video
endpoint.

---

## 10. Sources (accessed 2026-09-24)

- API quickstart (auth, endpoints, input types, ~5 min latency): https://docs.worldlabs.ai/api
- Rate limits: https://docs.worldlabs.ai/api/rate-limits
- Terms of Service (commercial use, accuracy disclaimer, liability cap): https://docs.worldlabs.ai/terms-of-service
- Marble app input limits (video/image/pano/Chisel caps): https://docs.worldlabs.ai/marble/create/prompt-guides
- Mesh export (triangle counts, file size): https://docs.worldlabs.ai/marble/export/mesh
- Gaussian splat → Unity export guide (splat counts, plugin, Quest 3 settings, fps): https://docs.worldlabs.ai/marble/export/gaussian-splat/unity
- Marble World Model overview blog (collider vs HQ mesh framing, Spark renderer): https://www.worldlabs.ai/blog/marble-world-model
- Announcing the World API (Jan 21 2026 launch): https://www.worldlabs.ai/blog/announcing-the-world-api
- World API third-party coverage: https://radiancefields.com/world-labs-introduces-world-api-for-marble
- Marble GA announcement (Nov 12 2025): https://techcrunch.com/2025/11/12/fei-fei-lis-world-labs-speeds-up-the-world-model-race-with-marble-its-first-commercial-product/
- Atlas deep-dive (Stanford Main Quad demo is Atlas, not Marble): https://kingy.ai/blog/world-labs-atlas-world-model-deep-dive/
- Atlas launch (Sep 1 2026, early access): https://www.worldlabs.ai/blog/atlas
- Marble hands-on review (photoreal vs stylized input): https://bdtechtalks.substack.com/p/what-to-know-about-world-labs-marble
- API examples repo (confirms text/image generation CLI examples, no video example present): https://github.com/worldlabsai/worldlabs-api-examples
- Marble app subscription pricing (search-indexed, not independently re-fetched): https://marble.worldlabs.ai/pricing

**Local repo (read, not re-cited inline above)**
- `docs/research/p1-cloud-world-models.md` §2.2, §5 (Marble coverage this doc extends)
- `docs/research/p1-fast-recon.md` §4 (Marble World API pricing/endpoint this doc extends)
