# G1: Grok Imagine (image + video) for AirTool

Research agent G1, 2026-09-26. Question: what can the Grok Imagine API do today, what are people
building with it, and which uses would make AirTool better or more memorable for the judges?

Tags: **[V]** verified from xAI docs or our own API call, **[S]** secondary source (blog, third-party
host, social post), **[I]** inferred by us.

**Bottom line.**
- `grok-imagine-image-2.0` edits a real capture frame and keeps every pixel we did not ask it to
  change. Measured shift against the source frame: 0 px. It costs $0.07–0.08 and takes about 11 s
  per edit.
- That makes three features cheap and demo-safe:
  1. "Reimagine this view": a renovation preview on the captured building.
  2. "See it installed": the bought part composited into the user's own photo, as a postcard on
     the receipt.
  3. A clean catalogue front view of each part, to feed our photo-projection asset path.
- Video (`grok-imagine-video-1.5`) is real and good, but it runs asynchronously and can take
  minutes, at $0.08–0.25 per second. Use it for pre-baked checkout and Devpost clips, not for live
  beats.

## 1. API facts

| Fact | Value | Tag |
|---|---|---|
| Base URL / auth | `https://api.x.ai/v1`, `Authorization: Bearer $GROK_API_KEY` (our key works) | [V] |
| Text-to-image | `POST /v1/images/generations` `{model, prompt, n, aspect_ratio, resolution, quality, response_format}` | [V] docs |
| Image edit | `POST /v1/images/edits`. Same fields plus `image: {url}` (one image) or `images: [{url}, ...]` (up to 5). `url` = public URL, base64 data URI, or `file_id` from the Files API | [V] docs + our calls |
| Multi-image prompts | Refer to the images by order ("image 1", "image 2"). The output aspect follows the first image unless `aspect_ratio` is set | [V] docs + our call |
| Masks / inpainting | There is **no mask field** in the REST schema. Region edits are prompt-driven; the magic-wand and segmentation tools are features of the grok.com app | [V] schema, [S] launch post |
| OpenAI SDK | `images.edit()` is **not** supported (it sends multipart). Use raw JSON over `httpx`, or `xai_sdk` `client.image.sample(..., image_url=...)` | [V] docs |
| `n` | 1–10 images per request | [V] docs |
| `aspect_ratio` | `1:1 16:9 9:16 4:3 3:4 3:2 2:3 2:1 1:2 19.5:9 9:19.5 20:9 9:20 21:9 5:2 auto` | [V] docs |
| `resolution` | `1k` (default) or `2k`. The schema also lists `1.5k` | [V] docs |
| `quality` (image-2.0) | `low`, `medium`, `auto` (default). `auto` = low for generation, medium for editing | [V] docs |
| Seed / negative prompt / transparent background | Not in the API. Background removal is a grok.com app feature | [V] schema, [S] |
| Response | `data[].{url, b64_json, mime_type, file_output}`, plus `usage.cost_in_usd_ticks` (1 USD = 1e10 ticks). No `revised_prompt` | [V] our calls |
| Output | JPEG. A 640×360 input came back as 1280×720 on a 1k edit; a 1:1 request came back 1024×1024 | [V] our calls |
| Image models | `grok-imagine-image` ($0.02), `grok-imagine-image-quality` ($0.05, **retires 2026-11-02**, then redirects to 2.0), `grok-imagine-image-2.0` ($0.04 base; released 2026-08-07; ranked #2 on Arena text-to-image and image-edit at launch) | [V] pricing page, [S] orcarouter, x.ai news |
| Measured edit cost | $0.07 for a 1-image edit and $0.08 for a 2-image edit (`auto` = medium) | [V] our calls |
| Measured latency | 10.9–11.1 s per 1k edit, 1–2 input images | [V] our calls |
| Video endpoints | `POST /v1/videos/generations` (text-, image- and reference-to-video), `/v1/videos/edits`, `/v1/videos/extensions`. Poll `GET /v1/videos/{request_id}` for `pending / done / failed / expired`; when done it returns `video.url` (a temporary URL) | [V] docs |
| Video params | `duration` 1–15 s (edits: input ≤ 8.7 s), `resolution` 480p (default) / 720p / 1080p (1.5 only; reference-to-video ≤ 720p), `aspect_ratio` 1:1 16:9 9:16 4:3 3:4 3:2 2:3, `image`/`image_url`, `reference_images` (up to 7), `last_frame`, `keyframes` (1.5), `generate_audio` (default true) | [V] docs |
| Video models | `grok-imagine-video` ($0.05/s) and `grok-imagine-video-1.5` ($0.08/s base). One secondary source gives 1.5 at $0.08/s for 480p, $0.14 for 720p, $0.25 for 1080p. 1.5 went GA 2026-06-16; references, 1080p and text-to-video were added 2026-07-31 | [V] pricing, [S] kie.ai, techtimes |
| Video latency | Docs say "typically up to several minutes". The API ranked #1 on Artificial Analysis at launch (2026-01-28) on quality, price and P50 latency (720p, 8 s); users call it "by far the fastest" | [V] docs, [S] latent.space, DataCamp |
| Rate limits | Not published per model; check the console | [V] (absence) |
| Content policy | Moderation runs on every request (`invalid_argument` on a block). After the January 2026 deepfake scandal, edits of real people are restricted. Buildings and products are low-risk; blur bystanders | [V] docs, [S] news |
| Privacy | API outputs are not used for training. Consumer grok.com creations get public URLs; the API with `response_format: b64_json` does not host anything | [V] docs, [S] X post |

**Our three calls** (logged in `work/assets-llm/calls.jsonl`, tags `g1/imagine/*`). Total spend
**$0.22**. No video calls: the docs settle that image-to-video exists.

| Tag | Input | Result | Cost | Time |
|---|---|---|---|---|
| `clean` | Midea window AC seller photo: 3/4 view, remote, two award badges | Clean orthographic front on white (background ≥ 252/255). W/H 1.448 vs the published 1.424 (1.7 % off). Small UI text garbled. [`g1/clean-front-view.jpg`](g1/clean-front-view.jpg) | $0.07 | 11.1 s |
| `install` | Kitchen capture frame `0241` + Karran black quartz sink product photo | Sink swapped in place. **0 px shift**; median abs diff to the source 3/255 outside the edit. It looks like a drop-in sink, not an undermount, and is not to scale. [`g1/install-before-after.jpg`](g1/install-before-after.jpg) | $0.08 | 10.9 s |
| `reno` | Kitchen frame `0481`, "navy cabinets, brass pulls, change nothing else" | Every door recoloured, pulls added, fridge magnets, appliances and bottles kept. **0 px shift.** Sign text re-hallucinated. [`g1/reno-before-after.jpg`](g1/reno-before-after.jpg) | $0.07 | 10.9 s |

The 0 px shift matters most: an edited frame registers with its `cameras.r<rev>.json` pose. So it
can be shown in the headset at that camera's pose, or projected back onto the mesh, and it lines up.

## 2. What people build with it

- **Product and e-commerce shots.** The launch post lists product colour changes, e-commerce
  photos, packaging and merch mock-ups. In PicLumen's review, Grok 2.0 "keeps the bottle structure
  and label accurate" but renders reflections more weakly than GPT Image 2.
  ([x.ai/news/grok-imagine-image-2](https://x.ai/news/grok-imagine-image-2),
  [piclumen review](https://www.piclumen.com/blog/grok-imagine-image-2-0-review/))
- **Region edits that keep the rest.** The same review: it "changed selected areas without
  rebuilding the whole image". Our two frame edits confirm this.
- **Interior redesign from a room photo.** People use Grok as a "scratchpad" for room ideas, and
  RoomGPT's blog compares against it
  ([room-gpt.app](https://room-gpt.app/blog/redesign-a-room-with-grok)). The exterior
  "curb-appeal" and virtual-renovation market is served by dedicated apps (BoxBrownie, Styldod,
  REimagineHome). We found **no** Grok-based exterior-renovation-on-a-3D-capture project: open
  ground for us.
- **Image → 3D.** The common hobby pipeline is: prompt Grok for a "full front view, plain white
  background" image, then run Hunyuan3D or Tripo on it
  ([Kinomoto, Medium](https://medium.com/kinomoto-mag/from-grok-to-a-3d-model-in-just-2-minutes-a-quick-guide-bf5dc1fc5ef2)).
  This is the same trick as our clean-front-view idea.
- **Video.** Image-to-video demos of FPV drone shots, time-lapses and photo zooms
  ([DataCamp](https://www.datacamp.com/tutorial/grok-imagine-api)); product showcases and
  "locking product placement" with reference images
  ([x.ai 1.5 references](https://x.ai/news/grok-imagine-video-1-5-references)); a multi-agent
  cinematic studio; clip chainers with subtitles
  ([GitHub topic grok-imagine](https://github.com/topics/grok-imagine)).
- **Tooling.** MCP servers, OpenAI-compatible image consoles, and third-party hosts (fal, Runware,
  Replicate, OpenRouter, MuAPI). Launch partners: fal, ComfyUI, InVideo, Flora, HeyGen
  ([x.ai/news/grok-imagine-api](https://x.ai/news/grok-imagine-api)).
- **Sentiment.** HN and X: strong on speed and price; complaints centre on moderation history, and
  on the consumer app making creations public
  ([HN](https://news.ycombinator.com/item?id=46806353),
  [X warning](https://x.com/MachinesBeFree/status/2001303884761817560)).
- **AR/VR.** We found no one using Imagine on a metric 3D capture or in a headset: another open
  gap [I].

## 3. Ideas, ranked

Scoring: user value × demo wow ÷ build risk. Costs are per use at `auto` quality and 1k.

### 1. "Reimagine this view": renovation preview on the captured building ★ pick

- **User value.** "Show my house with brown trim / new gutters / solar panels" on *your* building,
  from the exact spot you are standing in VR. This is the decision a homeowner makes before buying
  anything.
- **Wow.** High. In the headset a framed "magic window" appears, exactly aligned with the real
  mesh behind it, and the judge flips before/after with a pinch. It uses the SpaceXAI track's
  newest model on real drone data.
- **Architecture.**
  - New `POST /scene/reimagine {site, frame_id, prompt}` → `{frame_id, image_url, camera}`.
  - The server reads `scene/<site>/thumbs/<id>.jpg`, or the full frame if the package has one,
    and calls `images/edits`. It saves `data/imagine/<sha>.jpg` behind `cache.cached()`, so
    OFFLINE replays the rehearsed prompts.
  - The headset already picks the nearest frames for `/scene/ask`. It sends the nearest one and
    places a quad on that camera's frustum (pose `R,t`, `fx,fy,cx,cy` from `cameras.r<rev>.json`)
    about 1.5 m out. The image lines up with the mesh when viewed from the camera centre.
  - Voice tool `reimagine(prompt)` in `agent.py`.
  - V2, later: a projective-texture shader so the edit paints onto the mesh itself.
- **Cost / latency.** $0.07, about 11 s. The voice agent fills the wait ("Sketching it up…").
- **Risks.**
  - It is not metric, so label it "AI preview, not to scale".
  - Drone thumbnails are small; use `frames/` when present (outputs come back at 2× anyway).
  - Moderation if a person is in frame.
  - Prompt drift (sign text changes). The fix is the "change nothing else" clause, which worked
    in our run.
- **Build (1–1.5 h).**
  - `server/imagine.py`: `async def edit(images: list[bytes], prompt: str, *, aspect_ratio=None) -> bytes` (httpx, b64_json, logs cost ticks) + `reimagine(site, frame_id, prompt)`.
  - The route goes in `app.py`, the tool in `agent.py`, a section in `docs/api.md`.
  - Tests in `tests/test_imagine.py` with respx: the request shape (`image` vs `images`), a data
    URI, a 400 on an unknown frame, the OFFLINE cache hit, and a moderation error → 422 with a
    spoken fallback.

### 2. "See it installed": part-in-context postcard at checkout ★ pick

- **User value.** Before paying, see the actual product (its seller photo) in your own window,
  counter or fascia. After paying, the receipt carries that picture, which the user can share.
- **Wow.** High and on brief for **Visa**: discovery → decision → a personalised visual → pay. The
  Devpost gets a "postcard of your purchase".
- **Architecture.**
  - A multi-image edit: image 1 = the frame nearest the placed part (the headset sends
    `frame_id`), image 2 = `data/parts/<id>/image.jpg`.
  - The prompt carries the part name, the published dims and the placement ("in the window on the
    left, 486 mm wide").
  - New `POST /parts/{part_id}/postcard {site, frame_id}` → jpg. `checkout.authorize()` adds
    `postcard_url` to the receipt when one exists; the notebook entry shows it.
  - Start it when the seller carousel opens, so it is ready by the hold-to-pay.
- **Cost / latency.** $0.08, about 11 s, hidden behind the seller carousel.
- **Risks.**
  - Scale is approximate: in our test the sink came out slightly large. The true-size fit stays
    the job of the 3D part; the postcard is labelled "illustration".
  - A wrong frame choice.
  - Product text garbled.
- **Build (1 h, on top of #1's `imagine.edit`).**
  - `postcard(part, site, frame_id)` in `imagine.py`, the route, and the receipt field in
    `checkout.py`.
  - Tests: the prompt includes the dims and the name; the images are ordered frame-first; a
    postcard failure never fails a checkout (the receipt still goes out without it).

### 3. Clean catalogue front view for the asset pipeline ★ pick

- **User value.** Every part looks right in the hand. E3/E5 photo projection needs one clean,
  straight-on front photo, but seller photos are 3/4 views with badges, remotes and lifestyle
  backgrounds. The view-choice call also got the sink wrong ("front" for a top-down photo).
- **Wow.** Indirect but visible on every part. The Midea AC went from a badge-covered 3/4 shot to
  a flat front at 1.7 % aspect error, which drops straight onto the proxy's W×H face.
- **Architecture.**
  - In `assets.resolve_asset()`, after `fetch_image`: `imagine.clean_front(image.jpg) → front.jpg`.
    Cache it per part id, and let `warm.py` pre-generate it for the demo parts.
  - Pass it to the decal/E5 projection (and to Hunyuan's input) as the front texture.
    `part.image_url` and `image.jpg` stay the seller's real photo for the spec card (honesty).
- **Gate.** Check front.jpg's non-white bbox aspect against the published W/H (±10 %). Otherwise
  fall back to the raw photo.
- **Cost / latency.** $0.07 once per part, off the live path (the asset worker is already async).
- **Risks.**
  - Invented details on unseen faces: ask only for the front.
  - Garbled small print.
  - OFFLINE: the cache miss path keeps today's behaviour.
- **Build (1–1.5 h).**
  - `imagine.clean_front()` + `_aspect_ok(img, dims)`, and a hook in `assets.py`.
  - A bench row: rerun the E3 scorer (`score.py`, CLIP/IoU) on the 12-part test set, decal with
    the raw photo vs the clean front. About $0.84 of edits.
  - Tests: the aspect gate accepts/rejects synthetic images; a respx-mocked call writes
    `front.jpg`; a failure falls back.

### 4. Checkout "delivery" clip: image-to-video from the postcard

- **What it is.** Animate #2's postcard with `grok-imagine-video-1.5`, 6 s at 480p: "a delivery
  drone lowers the box to the door, the installer fits the unit, camera pushes in". Native audio
  included. It plays on the receipt panel.
- **Wow.** Very high, and the drone ties back to our capture story.
- **Cost / latency.** About $0.48 per clip. Async, up to minutes. Pre-bake it for the demo parts
  and show "live" only as a bonus.
- **Build (1 h).** `POST /parts/{id}/clip` starts the job and stores the `request_id`, and
  `GET` polls it. Download the mp4 into `data/imagine/` at once, because the URLs are temporary.
  Tests mock the pending → done poll.

### 5. Devpost / stage before-after transformation clip

- **What it is.** An offline script. It takes a drone frame and the #1 edit of the same frame as
  first frame and `last_frame` (video-1.5 keyframes), and produces a 5–8 s "house transforms" shot
  for the 2-minute video and the booth loop.
- **Cost.** About $0.40–1.12 per clip. Zero demo risk.
- **Build (30 min).** `tools/imagine_clip.py`. No server change.

### 6. Finish variants as textures (`set_finish` "show it in brown")

- **What it is.** Edit the clean front view (#3) into each `finishes[]` colour ("same product in
  bronze"). Unity then swaps the decal texture instead of only tinting it.
- **Cost.** $0.07 per finish, pre-warmed.
- **Why it ranks here.** It is a moderate gain over the tint, which already works.

### 7. Illustrated install card for the bill of materials

- **What it is.** Image-2.0 renders text well (it placed #3 on Arena text rendering), so it can
  draw a one-page "how it goes on" card: "8 hangers, every 60 cm, 1¼ in. screws". The card sits in
  the notebook next to `POST /parts/bom`.
- **Risk.** Invented instructions. Label it "illustration", and take the numbers only from our
  BOM.

### 8. Tileable material textures for template parts

- **What it is.** Text-to-image galvanized-steel / vinyl / brick swatches for the E1/E5 templates.
- **Why it ranks last.** Low wow, tiling is not guaranteed, and E5 already has PBR finishes.

## 4. The three to build first

1. **Reimagine this view (#1).** It is the most memorable beat for the SpaceXAI judges, and it is
   verified to stay pixel-registered with our cameras. Build it first; it lands
   `server/imagine.py::edit()`.
2. **See-it-installed postcard (#2).** It is the Visa story made visual, and it reuses `edit()`.
   Branch from #1.
3. **Clean front view for assets (#3).** It lifts every part's look, and it can be measured with
   the existing E3 bench. It reuses `edit()`, or copies the ~30-line helper if built in parallel.

Keep video (#4, #5) as pre-baked assets, not live beats, until someone times a real 480p job on
our key.

## Sources

- xAI docs: [Imagine overview](https://docs.x.ai/developers/model-capabilities/imagine),
  [image generation](https://docs.x.ai/developers/model-capabilities/images/generation),
  [image editing](https://docs.x.ai/developers/model-capabilities/images/editing),
  [multi-image editing](https://docs.x.ai/developers/model-capabilities/images/multi-image-editing),
  [video generation](https://docs.x.ai/developers/model-capabilities/video/generation),
  [REST images schema](https://docs.x.ai/developers/rest-api-reference/inference/images),
  [pricing](https://docs.x.ai/developers/pricing), [models](https://docs.x.ai/developers/models),
  [cost tracking](https://docs.x.ai/developers/cost-tracking)
- xAI news: [Imagine API](https://x.ai/news/grok-imagine-api),
  [Image 2.0](https://x.ai/news/grok-imagine-image-2),
  [Video 1.5 references](https://x.ai/news/grok-imagine-video-1-5-references)
- Secondary: [latent.space AINews](https://www.latent.space/p/ainews-spacexai-grok-imagine-api),
  [orcarouter on 2.0 / quality retirement](https://www.orcarouter.ai/blog/grok-imagine-image-2-0-quality-mode),
  [PicLumen review](https://www.piclumen.com/blog/grok-imagine-image-2-0-review/),
  [DataCamp tutorial](https://www.datacamp.com/tutorial/grok-imagine-api),
  [techtimes 1.5 update](https://www.techtimes.com/articles/322670/20260802/grok-imagine-video-update-adds-1080p-voice-cloning-seven-reference-scene-control.htm),
  [kie.ai 1.5 deep dive](https://kie.ai/blog/grok-imagine-video-1-5),
  [RoomGPT on Grok](https://room-gpt.app/blog/redesign-a-room-with-grok),
  [Kinomoto Grok→3D](https://medium.com/kinomoto-mag/from-grok-to-a-3d-model-in-just-2-minutes-a-quick-guide-bf5dc1fc5ef2),
  [GitHub topic grok-imagine](https://github.com/topics/grok-imagine),
  [HN thread](https://news.ycombinator.com/item?id=46806353),
  [Gaurav (xAI) on X](https://x.com/gauravisnotme/status/2016771039989289187)
