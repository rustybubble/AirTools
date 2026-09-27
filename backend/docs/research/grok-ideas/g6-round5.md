# G6: Grok round 5, the headset itself, learning and safety, and the judges

Research agent G6, 2026-09-26. Question: where can Grok change what the user sees or hears in the
Quest in real time? The areas are:

- **in-headset moments:** live labels on the passthrough view, a spatial voice, hands-free
  confirms, and "what changed since the last visit";
- **learning and safety:** "teach me to do this" coaching tied to the placed part and its manual,
  checked from headset frames, and hazard spotting at a drill point;
- **the judges:** a live X post of the finished design at the booth, and a gallery or
  leaderboard;
- anything else fresh, plus a sanity pass over the ranked lists in G1–G5.

Already built or in progress, so not proposed again:
- realtime voice (F1);
- reimagine (F2);
- installers (F3);
- site report (F4);
- postcard (F5);
- recall radar (F6);
- placement planner (F7);
- walk-in video (F8);
- manual Q&A (F9);
- condition survey (F10);
- finish variants (F11);
- capture coach (F12);
- rules and rebates check (F13);
- job packet (F14);
- the queued "do the whole job" chain.

Tags: **[V]** verified from xAI or vendor docs, or by our own live call. **[S]** secondary source:
a blog, repo or post. **[I]** our inference. "[V sub]" means we fetched the page and read it today.

**Bottom line.**
- Sixteen Grok calls cost **$0.221**, out of a $0.40 cap. Fifteen were vision or text calls
  ($0.044), and one was `x_search` ($0.177). They settled five things.
  1. **"Live labels" work as look-and-hold labels. They are too slow for per-frame labels.**
     - `grok-4.20-0309-non-reasoning` labels a 640×360 kitchen frame with 6 named, boxed items
       in **2.65–3.46 s** end to end.
     - Streamed, the **first token arrives in 0.61–0.78 s**, so the first label can appear in
       about 1 s.
     - Each call costs **$0.0008–0.0014**. The prompt cache takes it to the low end on repeat
       calls.
     - That is a label refresh every ~3 s, or on a pinch, at up to $0.03 per minute. It isn't
       30 fps.
     - `grok-4.7` at its default effort took **58.7 s and $0.029** for the same frame. It is out
       for anything live.
  2. **The labels are good but not honest by themselves.**
     - Names and boxes were right on sinks, faucets, ranges, dishwashers and cabinets. It read
       "Whirlpool" off the dishwasher, and called the range electric with a glass cooktop at 0.95
       confidence.
     - It called the **light switch an outlet** ("two duplex receptacles") in 5 of 7 runs on the
       same frame. `grok-4.7` made the same mistake.
     - When the prompt carried a spec example ("duplex 15 A receptacle"), the model **copied it
       as a finding**. With no examples, it wrote "rating not visible".
  3. **Hazard spotting must be rules over labels, not a question to the model.**
     - Asked "is there a gas line?" at a drill point behind the range, it answered "caution:
       possible gas line, turn off gas". The same model labels that range **electric** (point 2
       above).
     - At a drill point straight above an outlet, it said "caution, electrical". But its box for
       the outlet landed on the shaker bottle at the frame edge, and its advice was generic.
  4. **Progress checks work when phrased as yes/no questions about what is visible.**
     - Asked for step status ("countertop cleared: done?"), it said **done** with bottles and a
       sponge beside the sink. That is wrong.
     - The same frame with the same facts as visual yes/no questions got **4 of 4 right**,
       including two honest `cant_see` answers for the parts under the sink.
  5. **"What changed" narration is cheap and right on a real pair.** The G1 before/after pair
     (navy cabinets and new pulls) came back as "restyled: cabinet colour, cabinet hardware", with
     the fridge, stove, sink and signs listed as unchanged. It took 2.7 s and cost $0.0014.
- The realtime voice API takes **no image input** (docs). Labels need a separate vision call, and
  the voice can speak its result through `force_message`.
- Quest 3's Passthrough Camera API gives 1280×960 or 1280×1280 frames at 60 Hz, with 20–40 ms
  capture latency [V sub]. The network call to Grok is the bottleneck, not the camera.
- An X post with media and **no URL** costs $0.015; with a URL it costs $0.20 [V sub]. So the booth
  post puts a QR code on the image instead of a link in the text. We have no X keys; §6 pick 2
  lists what the user needs.
- The three to build first:
  1. **live labels**: look-and-hold labels on the passthrough view, plus pre-labelled scans, tap
     to shop;
  2. **the booth wall**: a gallery and leaderboard at the booth, plus a "post it" X post once keys
     exist;
  3. **an install coach**: manual-backed steps, checked by yes/no questions on headset frames,
     driven by voice.

---

## 1. Live checks (16 calls, $0.221 total)

The probe script and raw responses are in the session scratchpad only, not committed. The key was
never printed. Costs are from `usage.cost_in_usd_ticks / 1e10`. All vision calls went to
`POST /v1/chat/completions`, `temperature: 0`, with a strict `json_schema`. The frames are
`scene/kitchen/thumbs/*.jpg`: 640×360, about 40 KB.

| # | Call | Result | Cost | Time |
|---|---|---|---|---|
| 1–3 | Labels, `grok-4.20-0309-non-reasoning`, frame 0221 (sink wall), `detail: high`. Schema `labels[] {name, detail, box, confidence}`, ≤ 6 labels. Call 1's prompt had spec examples; calls 2–3 had none | 6 labels each: sink, faucet, countertop, soap, wipes, outlets. **Call 1 wrote "duplex 15 A receptacle", the example from our prompt, word for word.** Calls 2–3 wrote "undermount stainless steel single basin" and "chrome single handle pull-down" (both right), but "**two** white duplex receptacles" for one outlet and one switch [V] | $0.0013–0.0014 each | 2.90–3.22 s |
| 4–5 | Same, `detail: low` | 620 prompt tokens instead of 716. Same labels, and the same outlet/switch mistake in 1 of 2 runs. **No faster** (2.92, 3.24 s) [V] | $0.0009–0.0012 | 2.9–3.2 s |
| 6–7 | Same, `stream: true`, frames 0221 and 0141 (sink, dishwasher and range) | **First token at 0.63 s and 0.61 s**; complete at 3.06 s and 2.65 s. 0141 read "Dishwasher: white **Whirlpool**" (the logo is on the door) and "Stove: white electric range". Boxes in [`g6/live-labels.jpg`](g6/live-labels.jpg) [V] | $0.0008, $0.0010 | 3.1 s, 2.7 s |
| 8 | Same, frame downscaled to 448 px | 588 prompt tokens. **Not faster** (3.46 s). Image size isn't the latency lever at these sizes; output length is [V] | $0.0009 | 3.5 s |
| 9 | Same prompt on `grok-4.7` (default effort, which is **high**; we forgot to set `low`) | 4,221 reasoning tokens. Real 0–1000 boxes and careful wording ("rating not visible"). It still called the switch "a pair of receptacles" [V] | **$0.0285** | **58.7 s** |
| 10 | Labels, streamed, frame 0061 (range) | First token 0.78 s, done 2.95 s. "**Electric Range**: white freestanding with smooth black glass cooktop", confidence 0.95; outlet; microwave [V] | $0.0010 | 3.0 s |
| 11 | Hazard, frame 0141 with a red crosshair drawn 30 px above the outlet. Schema `{verdict go/caution/stop, hazards[] {kind, what, evidence visible/inferred, box, advice}, spoken}`. Ask: "drill a 1/4 in hole, 1.5 in deep, at the crosshair" | `caution`. Electrical, "wires or outlet box", `visible`, but **its box is on the shaker bottle**, not the outlet. Plumbing "inferred" near the sink. Advice generic ("avoid drilling into wires"). It didn't mention that wires run vertically from boxes, or suggest a breaker or a voltage tester. See [`g6/hazard-checks.jpg`](g6/hazard-checks.jpg) [V] | $0.0011 | 1.9 s |
| 12 | Same schema, frame 0061, crosshair on the backsplash behind the range. Ask: "**Is there a gas line** or anything else I could hit?" | `caution`, **"Possible gas line or flex connector behind range"**, "turn off gas". Call 10 labelled the same range electric. The question led it [V] | $0.0010 | 1.5 s |
| 13 | Step status, frame 0221. Faucet-swap checklist: 1 counter cleared, 2 valves closed, 3 faucet seated with handle, 4 sprayer hose connected. Answers `done/not_done/cant_see` | 1 **done: "countertop is clear around sink"**, wrong: a bottle, a sponge and a shaker cup sit at the rim. 2 and 4 `cant_see`, right. 3 `not_done`, "no new faucet body", which can't be judged from one frame. Every box was `[0,0,0,0]` [V] | $0.0011 | 5.4 s |
| 14 | The same four facts as **visual yes/no questions** ("Are any objects standing within a hand's width of the sink rim?", "Are the shut-off valves under the sink visible, and turned …?") with `yes/no/cant_see` | **4/4 right**: c1 yes ("bottles, sponge near sink", with a box), c2 `cant_see`, c3 yes (faucet box), c4 `cant_see` [V] | $0.0011 | 1.8 s |
| 15 | Change narration: G1's `reno-before-after.jpg` split into image 1 (before) and image 2 (after). Schema `changes[] {what, kind, box_after}`, `unchanged[]`, `spoken` | "cabinet colour: restyled", "cabinet hardware: restyled". Unchanged: refrigerator, microwave, stove, sink, countertop items, signs. Spoken: "Cabinets and hardware got updated. Everything else looks the same." Right [V] | $0.0014 | 2.7 s |
| 16 | `POST /v1/responses`, `x_search` (2026-07-01 to 2026-09-26), `max_turns: 2`, strict schema. Ask: builders of headset/glasses vision labelling, install coaching, hazard spotting, and booth auto-posters on X | 8 X searches, **32 posts fetched**, 4 posts returned (§3). The top-level `citations` list came back **empty** (with `no_inline_citations`). We checked all 4 through `api.fxtwitter.com/<handle>/status/<id>` (free, no key): all real, dates right [V] | **$0.1772** | 8.6 s |
| – | Groq `qwen/qwen3.8-27b` baseline for call 1, with `GROQ_API_KEY` | **401 "Invalid API Key"**. A retry with `GROQ_API_KEY_2` was **denied by the session's permission check**, so we have no Groq baseline. `LLM_VISION` on `main` defaults to that model; someone should check key #1 [V] | $0 | 0.5 s |

**Box scale.** In 12 of its 13 replies with non-empty boxes, `grok-4.20` non-reasoning returned
**0–100 (percent) boxes**, although the prompt said "1/1000 of the image, NOT percent" and gave a
worked example. Only its call 12 used 0–1000, as did `grok-4.7` (#9). F10 found the opposite on its own survey
prompt, so the scale depends on the prompt. **Ask for percent.** That is what this model does by
default, and a box whose corners are all ≤ 100 is otherwise ambiguous (0–100, or a small box near
the top-left in 0–1000). Keep F10's parser, which accepts 0–1, 0–100 and 0–1000.

**Latency budget for "live".**
- Quest camera capture: 20–40 ms [V sub].
- JPEG encode and upload of about 100 KB over hall Wi-Fi: 50–200 ms [I].
- Grok first token: 0.6–0.8 s. Full 6-label JSON: 2.7–3.5 s [V].
- Parsing the stream label by label, the first label can land at about 1.0–1.3 s and the last at
  about 3.5 s [I from #6–7, #10].
- Output tokens set the time (230–255 tokens per call). Fewer labels, or shorter `detail`, is the
  way to go faster; a smaller image isn't (#8).

---

## 2. API and platform facts new since round 4

| Fact | Detail | Date | Tag |
|---|---|---|---|
| **Realtime voice has no image input** | The voice-agent docs list no `input_image`, video or vision field for `conversation.item.create` or the session. Frames need a separate call; the voice speaks the result with `force_message` (verbatim, optionally `interruptible: false`; no `response.create` after it) | 2026-09-26 | [V sub] [voice agent](https://docs.x.ai/developers/model-capabilities/audio/voice-agent) |
| No spatial audio in the API | The voice docs don't mention it. Spatialisation is Unity's job (`AudioSource.spatialBlend = 1` on an object placed where the speech should come from) | 2026-09-26 | [V sub], [I] |
| Per-response `instructions` override | `response.create {instructions}` applies to one reply, then the session prompt is back. Handy for "you are now coaching step 3" without a session update | current | [V sub] voice agent |
| Image understanding limits | JPEG or PNG, up to 20 MiB per image, **no limit on image count** in one request. The docs use `grok-4.7` in the examples, and no video input | current | [V sub] [image understanding](https://docs.x.ai/developers/model-capabilities/images/understanding) |
| **Vision latency and cost on `grok-4.20` non-reasoning** | 640 px frame: 588–742 prompt tokens, 0.61–0.78 s to first token, 2.65–3.46 s for about 240 output tokens, $0.0008–0.0014. Cached prompt prefixes (128–704 tokens) cut cost, not time | 2026-09-26 | [V] #1–8, #10 |
| `detail: low` isn't faster | It saves ~96 prompt tokens and about $0.0002, with the same labels | 2026-09-26 | [V] #4–5 |
| `grok-4.7` default effort is too slow for live | 58.7 s and 4.2k reasoning tokens on one frame. F10 measured `low` at 7.3 s on 3 frames; still too slow for a label refresh | 2026-09-26 | [V] #9, F10 |
| Prompt examples leak into answers | A spec example in the system prompt came back as a finding. Keep examples out of vision prompts, or make them obviously fake | 2026-09-26 | [V] #1 |
| Leading questions make hazards | "Is there a gas line?" produced one behind an electric range | 2026-09-26 | [V] #12 vs #10 |
| x_search citations can be empty | With `include: ["no_inline_citations"]` and a strict schema, `citations` was `[]` while the answer had 4 real X URLs. G5 got citations in the same setup. So verify post ids another way: `api.fxtwitter.com` works with no key | 2026-09-26 | [V] #16 |
| x_search cost again | 32 posts fetched → $0.177 in 8.6 s, even with `max_turns: 2` and "use at most 3 searches" (it ran 8). The prompt doesn't bound it; `max_turns` bounds it only loosely | 2026-09-26 | [V] #16 |
| **Quest Passthrough Camera API** | Quest 3/3S, Horizon OS v74+, `horizonos.permission.HEADSET_CAMERA`. Left and right RGB cameras, 1280×960 or 1280×1280 (v83+), **60 Hz, 20–40 ms capture latency**, about 1–2 % GPU and 45 MB per stream. Camera data is "Device User Data" under Meta's developer data policy. Not in the XR Simulator. Apps must handle more resolutions later | current | [V sub] [Meta PCA overview](https://developers.meta.com/horizon/documentation/unity/unity-pca-overview) |
| **X API for a booth post** | $0.015 per post, **$0.200 per post containing a URL**, $0.005 per post read. Media: `POST /2/media/upload/initialize` → `/{id}/append` (≤ 5 MB chunks) → `/{id}/finalize`, then `POST /2/tweets` with `media.media_ids`. OAuth 2.0 with PKCE, scopes `tweet.write`, `media.write` (plus `tweet.read`, `users.read`), and `offline.access` for a refresh token. Access tokens last 2 h. xAI credit back: 0 % under $200 of X spend, then 10/15/20 % | current | [V sub] [pricing](https://docs.x.com/x-api/getting-started/pricing), [chunked upload](https://docs.x.com/x-api/media/quickstart/media-upload-chunked), [OAuth 2.0](https://docs.x.com/fundamentals/authentication/oauth-2-0/authorization-code) |
| Release notes | Nothing new since G5: Grok 4.7, Transcribe 2.0, the `grok-imagine-image-quality` retirement | 2026-09-26 | [V sub] [release notes](https://docs.x.ai/developers/release-notes) |

---

## 3. What people are building

**Coverage caveat.**
- WebSearch was exhausted, Reddit returns 403, and there's no `gh` CLI. So we used the HN Algolia
  API, unauthenticated GitHub REST search, WebFetch, and one Grok `x_search` (#16).
- GitHub's anonymous search hit its rate limit after 10 queries.
- **No Grok-on-Quest project turned up anywhere.** Everything below uses OpenAI, local models, or
  on-device detectors.

**Headset camera + LLM (the pattern we'd copy)**
- [xrdevrob/QuestCameraKit](https://github.com/xrdevrob/QuestCameraKit) (577★, pushed
  2026-09-23) has six Quest 3 samples. The relevant ones:
  - **"Image + voice AI"**: record a question, transcribe it, send the text and a camera frame to
    OpenAI, and speak the reply;
  - WebRTC streaming of the camera to another device;
  - a colour picker that maps a room point to a camera pixel.

  Its README says to route requests through your own backend and keep keys out of APKs. That is
  what our server does [V sub].
- [oculus-samples/Unity-PassthroughCameraApiSamples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)
  (509★) is Meta's official sample.
  - `CameraToWorld` turns a 2D camera pixel into a 3D world ray. That is exactly what Unity needs
    to anchor a label box.
  - `MultiObjectDetection` runs YOLO on-device with Unity Inference Engine (Sentis) [V sub].
- On-device detectors with world-anchored markers:
  [sandeepv6/questvision](https://github.com/sandeepv6/questvision) (Sentis YOLO) and
  [TREXKS/Unity-PassthroughCamera-ObjectDetection](https://github.com/TREXKS/Unity-PassthroughCamera-ObjectDetection)
  (YOLOv9, Unity 6). They have 80 COCO classes, so no "shut-off valve" or "GFCI" [S].
  - The hybrid is the point: a local detector can track at frame rate, and Grok names and
    specifies things every few seconds.
- [hyunaseo/OpenAI-for-Meta-Quest-3](https://github.com/hyunaseo/OpenAI-for-Meta-Quest-3) is a
  Unity pipeline for a multimodal XR agent: voice plus camera to OpenAI [S, description].
- [genesisinteractive/QuestRoomScan](https://github.com/genesisinteractive/QuestRoomScan) (69★,
  2026-09-19) does real-time TSDF room reconstruction on Quest 3, plus server Gaussian splats [V
  sub].
  - A second scan made in the headset is how idea 8 ("what changed") would get its second visit
    without a drone.
- HN: [Show HN: tracking a real golf ball in MR at 90 FPS on the Quest 3 GPU](https://puttdojo.com/devlog/tracking-a-real-golf-ball.html)
  (2026-06-23) shows that on-device tracking is fast. Naming is the LLM's job [S].

**Coaching and safety from a camera**
- [furniture-assembly-assistant](https://github.com/BinuShefieldShifani/furniture-assembly-assistant)
  (2026-05) runs GroundingDINO to detect IKEA parts and tools, then Llama 3.2 for "what to do
  next", and emits Unity-ready AR JSON. The README pitch is the one we want: "looks at what's in
  front of you and tells you exactly what to do next" [V sub]. Nothing checks the result.
- X, via #16, all verified through fxtwitter:
  - [@Sentdex, 2026-09-03](https://x.com/Sentdex/status/2095556673855221835): off-the-shelf
    multimodal LLMs, not robotics models, doing high-level planning over live robot camera frames;
  - [@inventorkhalifa, 2026-08-19](https://x.com/inventorkhalifa/status/2090027683698581563):
    camera glasses that detect obstacles for visually impaired users and alarm or vibrate, with
    voice description next;
  - [@akinyi__wendy, 2026-07-22](https://x.com/akinyi__wendy/status/2079775535379742850): a school
    safety badge with a tiny in-house vision model under 1 s end to end;
  - [@ardchain, 2026-09-16](https://x.com/ardchain/status/2100313769478127829): "before a robot can
    understand a room, it needs to notice one thing reliably". Sensors first, then vision.

  The common thread is that safety calls run on something deterministic or small, and the LLM
  describes. That matches #11–12 [S].
- HN: [Ally Solos smart glasses for low-vision users](https://www.theverge.com/news/759160/ally-solos-smart-glasses-ai-envision-low-vision)
  (2025-08) [S].

**Hands-free**
- [BSchoolland/hey-grok-wakeword](https://github.com/BSchoolland/hey-grok-wakeword) (2026-07) is a
  34k-parameter "Hey Grok" wake-word model: 108 KiB, 1.2 ms per window, about 1 % of one CPU core.
  It was built because Whisper spells "Grok" five ways [V sub]. It's ONNX, so it could run in
  Unity Sentis on the Quest, and it would replace pinch-to-talk for coaching (pick 3).

**Booth posting**
- X's pay-per-use launched 2026-02 ([HN](https://news.ycombinator.com/item?id=46921507)). A Show HN
  wraps it as an agent skill ([x-twitter skill](https://skills.sh/alberduris/skills/x-twitter)) [S].
- We found no booth auto-poster built on Grok. #16 found none on X either.

**Gaps.**
- No one checks install steps from a headset frame.
- No one does hazard rules over LLM labels.
- No "what changed since last visit" narration on scans.

---

## 4. Ideas, ranked

Scores and columns as in G3–G5: **Wow** × **Feas** (a few hours, one person, server-side, Unity
limited to new actions) × **Fit** (reuses our endpoints and modules). Costs are per use.

| Rank | Idea | Wow | Feas | Fit | Score | Cost / latency | Main risk |
|---|---|---|---|---|---|---|---|
| **1** | **Live labels**: look-and-hold (or pinch) labels on the passthrough view, world-anchored. Pre-labelled scan scenes. Tap a label to shop for it (`find_part`) | 5 | 4 | 5 | 100 | $0.0008–0.0014 per frame; first label ~1 s, all ~3 s [V]; ≤ $0.03/min at one frame per 3 s | Wrong names (switch → outlet, #2–3); specs copied from prompts (#1); hall Wi-Fi |
| **2** | **Booth wall**: a gallery and leaderboard of every finished design on the booth screen, plus a "post it" X post with a QR code on the card (no URL) | 5 | 4 | 4 | 80 | Caption ~$0.001 [I from #15]; X post $0.015 [V sub] | No X keys; a public post of someone's design needs consent |
| **3** | **Install coach**: manual-backed steps (F9), each checked by yes/no questions on a headset frame, voice-driven ("done", "check it", "next") | 5 | 3 | 5 | 75 | ~$0.001 and 1.8–5.4 s per check [V #13–14]; ~$0.003 per job to write the checks [I] | Wrong "pass" on a status-style check (#13); users skip checks |
| 4 | **"Safe to drill here?"**: drill point from the pointer + this frame's labels (pick 1) + local rules (vertical band above and below boxes, sink wall, range type) → go/caution/stop, spoken from templates | 4 | 4 | 4 | 64 | Labels' cost only; rules < 1 ms [I] | Rules miss what isn't visible (in-wall pipes). Must say "can't see inside the wall" every time |
| 5 | **"Where did I see…?"** label memory: every anchored label goes into a per-site store; "where's the shut-off valve?" gives a direction and distance, with a spatial audio ping from there | 4 | 4 | 4 | 64 | $0 (local search over stored labels) [I] | Only knows what was labelled; duplicate labels across views need merging (F10's radius merge) |
| 6 | **Spatial voice**: the quartermaster's voice comes from the thing it's talking about. A new `speak_at {pos}` action with each spoken reply that has a subject, plus clock-face directions ("2 o'clock, 3 metres") | 3 | 5 | 4 | 60 | $0; Unity audio [I] | Mostly Unity work; bad in a noisy hall without headphones |
| 7 | **"Right bit?"**: hold up a drill bit, anchor or fastener; Grok reads the stamped size and compares it with the manual's spec (F9 found "1/8 in drill bit" for the Midea) | 4 | 3 | 4 | 48 | ~$0.001, ~2 s [I from #1–8] | Stamped sizes are tiny in a 1280 px frame; untested |
| 8 | **"What changed since last visit"**: a local diff of two scans' structure layers (objects added, removed or moved) + a before/after frame pair for each change → one spoken summary | 4 | 3 | 4 | 48 | $0.0014, 2.7 s per pair [V #15] | Needs two registered scans of one site; we have one kitchen revision |
| 9 | **Read-back confirm**: before anything irreversible (checkout, X post, revoking a packet), `force_message` with `interruptible: false` reads back what will happen and a 3-digit code; the user says the code | 3 | 4 | 3 | 36 | $0.08/min voice only [V G2] | Hold-to-pay already covers checkout; adds a step |
| 10 | **"Show me how"**: an Imagine video from the user's own frame, with hands doing the current step | 5 | 2 | 3 | 30 | ~$0.26 per 3 s, ~45 s [V G3] | Invented, possibly unsafe technique shown as real; must be labelled "illustration" |

**4. Safe to drill here.** Calls #11–12 are the case for keeping the model away from the verdict.
- The rules, checked in and each with a source link and plain wording:
  - Electrical: the drill point is within the vertical column above or below a labelled outlet or
    switch (±15 cm sideways) → **stop**. Cables usually run vertically from boxes; cite a
    homeowner-facing source.
  - Within 40 cm of a labelled sink or dishwasher, on the same wall → **caution** (supply and
    drain lines).
  - A range labelled "gas" (Grok's labels named the fuel right in #10) → caution behind it. A
    range labelled electric → "240 V receptacle behind the range", caution.
  - Always add: "I can't see inside the wall. Use a stud finder with AC detection, or turn off
    the breaker."
- The geometry is 2D, in the frame: the drill point is projected into the same camera (Unity
  sends it as a pixel), so no 3D is needed.
- The **ladder angle** needs no Grok at all. The headset's protractor tool reads the rail angle,
  and the 4-to-1 rule (about 75°) is one comparison. Add it as a rule, and have the voice say it.

**5. Label memory.**
- Pick 1's anchored labels go to `data/labels/<site>.json`: `{name, detail, pos, frame_id, ts}`.
- "Where's the …" matches the name locally, with no LLM. The maths is the same as G5's voice-only
  `where_is`.
- It turns labelling into a notebook the contractor can use later, and F4's report can list the
  labels.

**6. Spatial voice.** One server change: actions that have a subject (a label, pin, placed part or
survey finding) get `speak_at: [x,y,z]`, and Unity plays the reply from an audio source there. The
voice API itself stays mono (§2).

**7. Right bit.** Cheap to try. Put a bit on a white table and read the frame at 1280 px. Round 6
could test it; it isn't tested here.

**8. What changed.**
- Match objects by label and centroid (within 10 cm), and planes by normal and offset. Grok only
  narrates the local diff and, for each change, looks at the nearest before/after frames (#15).
- The demo blocker is a second, registered scan. A QuestRoomScan-style rescan in the headset, or a
  second capture of the kitchen, would unblock it.
- An F2 reimagined frame can stand in for "after" in a demo, labelled as such.

**9 and 10** are in the table for completeness; neither beats the first eight for the hours.

---

## 5. Sanity pass over G1–G5: the three strongest unbuilt ideas

We went through every ranked list and dropped what has been built (F1–F14), what is queued ("do
the whole job"), and what this round supersedes:
- G4 #4 "public share and X handoff": pick 2 below absorbs its X part, and F14 already built the
  public link.
- G5 #6 "voice-only mode": ideas 5 and 6 above take its `where_is` and clock-face parts.

Still standing:

| Round, rank | Idea | Why it's still strong | What changed since |
|---|---|---|---|
| **G3 #4** (score 75) | **Voice-refined reimagine**: "darker blue", "brass pulls" as chained edits of F2's image | Cheapest wow left: F2's route exists, and each step is one $0.07, 11 s edit. It's the most natural next thing a judge says after seeing F2 | Imagine now takes up to 5 source images per edit (G5), so the original frame can ride along with each edit and limit drift |
| **G5 #4** (score 60) | **Bill-impact card**: yearly kWh and $ before and after, from `btu_for_room` + ENERGY STAR SEER2/HSPF2 + a climate-hours table + the EIA price. Grok writes one number-checked line | F13 already fetches ENERGY STAR ratings; this adds one local calculation and a card section. Homeowners ask "what will it save?" right after "is there a rebate?" | F13 is on its branch, so the ratings are there. The climate constants still need a sourced table |
| **G5 #7** (score 48) | **Paper-quote checker**: a photo of a contractor's quote → lines → compared with our BOM, F13's permit fee and rebate → "questions to ask" | It's a headset moment too: hold the quote up to the Quest and look. This round shows 4.20 NR reads printed text ("Whirlpool") at ~$0.001 | Pick 1's camera path makes it one more schema on the same frame upload |

Runners-up: G4 #5 storm check and claim pack (it needs the site's lat/lon, and `origin_gps` is
still null), and G2 #7 spectator co-pilot, which pick 2's booth screen partly covers.

---

## 6. The three to build first

### Pick 1: Live labels (`server/labels.py`, `POST /scene/labels`, tool `label_view`)

**Why.**
- It is the most "Grok changes what I see" moment we can build: look at the sink wall, hold still
  or pinch, and within a second labels start appearing on the real faucet, the outlet and the
  dishwasher, each with a short trade description.
- Tap one and it's a `find_part` query. That closes the loop to the rest of the demo.
- Calls #1–10 set the design:
  - look-and-hold, not per-frame;
  - stream, so the first label comes at about 1 s;
  - no spec examples in the prompt;
  - numbers in `detail` only when read from printed text;
  - percent boxes.
- The pre-labelled scan is what makes it look instant in the scanned-scene mode: warmed labels
  load with the scene, and live calls happen only on the passthrough view.

**Module.** `server/labels.py`:
- `LLM_LABELS` role, default `xai:grok-4.20-0309-non-reasoning` (it follows F7's `LLM_PLAN`
  pattern in `llm.py`/`config.py`).
- `SCHEMA`: `labels[] {name, detail, box, confidence, source: "seen"|"printed_text", part_query}`,
  at most 6 (said in the prompt). The prompt asks for **percent boxes** and gives no spec examples (#1).
- `async def label_frame(jpg: bytes, focus: str | None) -> AsyncIterator[Label]`
  1. Downscale to a 960 px long edge, JPEG q80 (the Quest gives 1280×960).
  2. One streamed `chat.completions` call.
  3. An incremental parser yields each complete `{…}` inside `"labels": [` as it closes. A
     brace-depth scan over the text so far is enough, with no new dependency.
- `normalise(label, w, h) -> Label | None`, the honesty rules in code:
  - Boxes become 0–1, with F10's box-scale rule: all values ≤ 1.5 → unit; else ≤ 100 →
    percent; else 0–1000. Clamp, and swap reversed corners.
  - Drop confidence < 0.5.
  - Any number with a unit in `detail` (regex over `\d+(\.\d+)?\s*(A|amp|V|W|in|"|mm|cm|BTU|psi|gal|ft)`)
    is kept only if `source == "printed_text"`. Otherwise it's replaced with "size not visible".
    This is the fix for #1.
  - `name` is lower-cased into a small alias map (`receptacle|outlet` → `outlet`,
    `light switch|switch` → `switch`, `range|stove` → `range`) so pick 3 and idea 4 can use
    labels as data.
- **Cache.** `cache.cached("labels", sha1(jpg_bytes) + focus)`. The same frame is never billed
  twice, and OFFLINE replays warmed frames.
- **Guard.** One call in flight per `session_id`; a new request cancels the old stream (close the
  httpx stream). At most `LABELS_PER_MIN` (default 20) per session, then 429. That caps spend at
  about $0.03 per minute.
- **Scan pre-labels.** `async def label_scene(site, every=4)` labels every Nth thumb (42 for the
  kitchen). It lifts each box centre to 3D with F10's `survey.to_pin` (a ray against the collision
  mesh) and merges same-name anchors closer than 0.3 m, keeping the higher confidence and
  `also_in`. Output: `data/labels/<site>.r<rev>.json`. It runs only from
  `python -m server.warm --labels <site>`, never on the request path.

**Endpoints.**
- `POST /scene/labels` with `{session_id, frame: {id, jpg_b64}, focus?: "plumbing"}` returns:

```json
{"frame_id": "pca-000812",
 "labels": [{"id": "l0", "name": "faucet", "detail": "chrome single handle pull-down", "box": [0.39, 0.39, 0.48, 0.52],
             "confidence": 0.85, "source": "seen", "part_query": "chrome single handle pull-down kitchen faucet"},
            {"id": "l1", "name": "outlet", "detail": "white duplex receptacle", "box": [0.13, 0.25, 0.26, 0.35],
             "confidence": 0.70, "source": "seen", "part_query": null}],
 "latency_ms": 2710, "cost_usd": 0.0010,
 "label": "Grok's reading of the camera view. Sizes are shown only when printed on the part."}
```

- `?stream=1` returns `application/x-ndjson`: one label object per line as soon as it parses, then
  a final `{"done": true, "latency_ms", "cost_usd"}` line.
- `GET /scenes/{site}/labels` returns the pre-labelled 3D anchors `[{name, detail, pos, confidence,
  frames[]}]`, or 404 if not warmed.
- Errors:
  - `400` for an empty or bad JPEG, or one over 4 MB (`vision.MAX_FRAME_BYTES`);
  - `429` for the rate guard, with `retry_after_s`;
  - `503` OFFLINE with a cache miss (warmed scene labels still work);
  - `502` when xAI fails.

**Agent.**
- Tool `label_view(focus?)`, hidden unless `context.frames` or `frame_jpg_b64` is present, like
  `ask_scene`.
- Fast path: `\b(label|what am i looking at|what('s| is) (all )?this stuff)\b`.
- Emits `show_labels {frame_id, labels}` and speaks a template: "I see a faucet, a sink and an
  outlet. Tap one to shop for it." Add it to the early-exit tuple.
- On the F1 relay the tool's output is spoken through `force_message` (verbatim, as
  `_announce_job` does), so label names are never paraphrased.
- The relay needs a frame. Add an uplink message `{"type": "frame", "id", "jpg_b64"}` that stores
  `self.last_frame`, which tools use when it's under 5 s old. This is a small addition to
  `_uplink`'s dispatch.

**Unity contract.**

| Action / call | Unity |
|---|---|
| Look-and-hold (head still for ~0.7 s) or pinch | Grab a PCA frame, `POST /scene/labels?stream=1`, keep the camera pose with the request |
| each NDJSON label | Ray from the **stored** pose through the box centre (Meta's `CameraToWorld` sample), then a world-anchored billboard: `name` large, `detail` small. `confidence < 0.7` greyed. Fade after 20 s or when the next set lands |
| tap a label | Send `"find " + part_query` as a normal `/agent/command` (null `part_query` → "not a part") |
| `show_labels` from the agent or relay | Same rendering, frame pose from the `frame_id` Unity sent |
| scanned-scene mode | On load, `GET /scenes/{site}/labels` → anchors at `pos` (scene frame, apply the usual glTF → Unity flip) |

**Cost per use.** Measured $0.0008–0.0014 per frame, first label at ~1 s, all at 2.7–3.5 s [V].
Continuous look-and-hold with the 20/min cap is at most about $0.03 per minute. Warming the
kitchen at every 4th thumb: 42 × ~$0.0011 ≈ **$0.05** [I from the measured per-frame cost].

**Test plan.** `tests/test_labels.py`, offline:
1. Streaming parser: #7's real output, cut into 7-byte chunks, yields 6 labels in order. A
   truncated stream yields the complete ones and no error.
2. Box scales: percent (#2's real boxes), 0–1000 (#9), unit, reversed corners, out of range →
   clamped or dropped.
3. Honesty: `"duplex 15 A receptacle"` with `source: "seen"` → "white duplex receptacle, size not
   visible". The same text with `printed_text` is kept.
4. Aliases: "Electrical outlets" → `outlet`; "Electric Range" → `range`.
5. Cache: the same bytes twice → one HTTP call. OFFLINE miss → 503.
6. Rate guard: 21 calls in a minute → 429. A new request cancels an in-flight one (mock stream
   that hangs).
7. `label_scene` on F10's trimmed kitchen fixture (collision mesh + 2 cameras): two frames that
   see the same faucet merge into one anchor with `also_in`.
8. Agent: "what am I looking at" with a frame → `show_labels`; without a frame the tool is hidden.
   Relay: a `frame` message then the tool → it uses `last_frame`, and the reply goes through
   `force_message`.

**What to mock.** respx for `api.x.ai/v1/chat/completions`: one JSON fixture and one SSE fixture
built from calls #7 and #9's real outputs. The F10 mesh fixture. No network.

### Pick 2: Booth wall (`server/booth.py`, `GET /booth`, `POST /booth/{session_id}/share`, tool `share_design`)

**Why.**
- Judges see ten features in five minutes, and then the next team. A screen at the booth showing
  every design made today, with a leaderboard, keeps the demo on show between judges.
- A "post it" moment puts it on X under the SpaceXAI track's own platform: the judge's design
  appears on the booth account's timeline while they watch.
- The gallery needs no keys and works offline. The X post is an add-on that switches on when the
  keys exist.
- The per-post price decides the design. A URL in the post costs $0.20 against $0.015 [V sub],
  so the link goes into a QR code on the image (F14's packet URL).

**Module.** `server/booth.py`:
- `Entry {entry_id, session_id, name, image, part, total_usd, after_rebates_usd?,
  safety_verdict, seconds_to_cart, parts_placed, caption, created_at}`, built from F4/F14's
  report data. Image priority: F5 postcard, then F2 reimagine, then the top pin frame. Stored in
  `data/booth/entries.json`.
- Consent: an entry is added **only** when the user says "add it to the wall", or on
  `POST /booth/{session_id}/share`. `name` is whatever they say ("Judge 3" by default). No faces:
  images come from the scan, never the passthrough camera.
- `caption(entry)`: one `grok-4.20-0309-non-reasoning` call, ≤ 90 characters. Every number must
  be in the entry facts (F14's `number check`), else a template. Cached by a facts hash.
- `leaderboard(entries)`, local: "fastest scan to cart" (`seconds_to_cart`), "most parts placed",
  "biggest rebate share" (from F13 when present). Ties go to the earlier entry.
- `card(entry) -> Path`: PIL, 1200×675. The image, the part name and total, the safety pill, and a
  QR to the F14 packet `pdf_url` when it exists (the optional `segno`, as in F14). Otherwise no
  QR.
- **X, optional.** `x_ready()` is true when `X_CLIENT_ID` and `X_REFRESH_TOKEN` are set.
  - `async def x_post(card_path, text) -> {tweet_id, url}`:
    1. refresh the access token (`POST https://api.x.com/2/oauth2/token`,
       `grant_type=refresh_token`), and save the new refresh token if one comes back;
    2. `POST /2/media/upload/initialize {media_type: "image/jpeg", total_bytes, media_category: "tweet_image"}`;
    3. `append` (one chunk, the card is under 5 MB);
    4. `finalize`;
    5. `POST /2/tweets {text, media: {media_ids: [id]}}`.
  - `x_text(entry)`: Grok, ≤ 240 characters, number-checked. The code rejects any `http`, `www.`
    or `.com` (the $0.20 trap) and any `@` mention, and appends a fixed hashtag.
  - `x_delete(tweet_id)`: `DELETE /2/tweets/{id}`.

**Endpoints.**
- `GET /booth`: an HTML page for the booth TV. It polls `GET /booth.json` every 5 s and shows a
  grid of cards and the leaderboard. No build step, the same style as F4's report page.
- `POST /booth/{session_id}/share {name?}` adds the entry and returns
  `{entry_id, card_url, caption, x: {ready, preview_text?, preview_id?, needs?}}`.
  - With X not ready, `needs` lists the missing env vars.
  - The preview is valid for 5 minutes.
- `POST /booth/x/confirm {preview_id}` posts and returns `{tweet_id, url}`.
- `DELETE /booth/x/{tweet_id}` takes the post down.
- `GET /booth/x/login` and `/booth/x/callback`: the one-time PKCE flow. It prints the refresh token
  into the server log **once**, for `.env`, and never serves it.
- Errors:
  - `404` for an unknown session;
  - `409` for a preview that expired or was already used;
  - `503` X not configured, or OFFLINE, on `confirm`;
  - `502` for an X API error, with the X error `title` passed through.

**Agent.**
- Tools `share_design(name?)` ("add it to the wall", "post it", "share my design") and
  `unpost_design()` ("take it down").
- `share_design` returns `show_share_preview`. **Posting always needs the Unity hold-to-post
  gesture** (`confirm`), never a voice turn alone. It is the same stance as the pay panel.
- Fast path on both, and both go in the early-exit tuple.

**Unity contract.**

| Action | Unity |
|---|---|
| `show_share_preview {entry_id, card_url, caption, x_ready, preview_text, preview_id}` | A panel with the card and the post text. When `x_ready`: a hold-to-post ring that calls `POST /booth/x/confirm`; otherwise "Added to the booth wall" |
| `show_posted {url, qr_url}` | A small panel with a QR code to the post, so the judge can open it on their phone |

**What the user needs for the X post** (none of it is in `.env` today):
1. An **X developer account** on pay-per-use, with a few dollars of credit: 100 posts is $1.50
   at $0.015 [V sub].
2. A **Project and App** with OAuth 2.0 user authentication on:
   - type "Web App, Automated App or Bot" (a confidential client, so it gets a client secret);
   - callback `http://localhost:8000/booth/x/callback`;
   - scopes `tweet.read tweet.write users.read media.write offline.access` [V sub scope names].
3. A **bot account** for the booth (e.g. `@AirToolDemo`), not a personal one. Set its "automated"
   label in the account settings [I: X's automation rules ask for it; not re-read today].
4. Run `GET /booth/x/login` once, logged in as the bot. Put `X_CLIENT_ID`, `X_CLIENT_SECRET` and
   the printed `X_REFRESH_TOKEN` in `.env`.
   - Access tokens last 2 h, and `offline.access` gives the refresh token [V sub].
   - We assume refresh tokens rotate on use, so the server rewrites the stored token after each
     refresh (`data/booth/x_token.json`, git-ignored) [I, unverified].
5. Optional: link the X developer account to the xAI team for credit back. It only pays from
   $200 of X spend (10 %), so ignore it for a booth [V sub].

**Cost per use.**
- Caption ~$0.001 and post text ~$0.001 [I from #15's token counts].
- X post $0.015 (media, no URL) [V sub]. The media upload has no listed price on the pricing page.
- Card render under 1 s [I].
- A day of 60 judge posts is about $1.00 in total.

**Test plan.** `tests/test_booth.py`:
1. `share` without consent never creates an entry (only the tool or endpoint does). The name
   defaults to "Judge n".
2. The leaderboard maths on 3 fixture entries, including a tie.
3. Caption: a mocked reply with an unbacked number falls back to the template.
4. `x_text`: `https://`, `www.`, `foo.com` and `@someone` are all rejected, with a template
   fallback. The hashtag is appended once, within 240 characters.
5. X flow with respx: token refresh (new refresh token saved), initialize → append → finalize →
   tweets, in order with the right shapes. `confirm` with an expired or reused `preview_id` →
   409.
6. Not configured → `x.ready: false` and `needs: ["X_CLIENT_ID", "X_REFRESH_TOKEN"]`; `confirm`
   → 503.
7. `GET /booth.json` lists entries newest first. `GET /booth` returns HTML with no user text
   unescaped (a name of `<script>`).
8. Agent: "post it" → `show_share_preview`, and never `show_posted` without `confirm`.

**What to mock.** respx for `api.x.ai` (caption), `api.x.com/2/oauth2/token`,
`/2/media/upload/*`, `/2/tweets`. F14's packet and F5's postcard come from fixtures.

### Pick 3: Install coach (`server/coach.py`, `POST /coach/start`, `POST /coach/{coach_id}/check`, tools `start_coach`, `coach_step`, `check_step`)

**Why.**
- "Teach me to do this" is what a homeowner in a headset wants after buying the part.
- F9 has the manual, with verified quotes and pages. F1 has the voice. The placed part shows where
  the work happens.
- The missing piece is checking the work. Calls #13–14 show how to do it honestly: never ask
  Grok "is the step done?". Ask it yes/no questions about what is visible, and let code decide.
- A judge moment: "Check it." "The valves aren't visible from here. Look under the sink." Then,
  after the user looks down: "Both valves are closed. Step 3: disconnect the supply lines."

**Module.** `server/coach.py`:
- `Step {i, say, page?, quote?, checks: [Check], tool?: "drill"|"wrench"|"tape"|None,
  anchor?: "part"|"pin:<id>"}` and `Check {id, question, expect: "yes"|"no", look_at?: str}`.
- **Where the steps come from**, first match wins:
  1. **The F9 manual.** One Grok text call over the manual's install pages returns ≤ 8 steps, each
     with a `quote`. F9's normalised-quote check keeps a step only if its quote is in the PDF text,
     and sets `page` from where the quote was found. Cached by `(part_id, manual sha)`.
  2. **A checked-in template per job** (`COACH_JOBS`, keyed like F13's `JOBS`): faucet swap,
     window AC, under-cabinet LED, gutter hanger. The template text is ours, with a
     `source_url` per job.
  3. Otherwise: "I don't have steps for this one. Ask me about the manual instead" (F9).
- **Jobs F13 marks as licensed-trade-only** (a mini-split refrigerant line, a new 240 V circuit)
  get **no** coaching: "This part needs a licensed installer. I can show you who installs it"
  (F3). That comes from F13's `who_can_pull`, not the model.
- **Checks.** For each step, 1–3 yes/no questions about things a camera can see ("Is the old
  faucet removed from the deck?", "Are both shut-off valves turned across the pipe?"). Templates
  carry them; manual steps get them from the same call, with a rule in code: each question must
  name a visible object and end with "?". Stored with the steps.
- `async def check(coach_id, i, jpg) -> CheckResult`
  1. One vision call with #14's schema (`id, answer yes/no/cant_see, evidence, box`), percent
     boxes.
  2. Verdict **in code**:
     - all answers equal `expect` → `passed`;
     - any `cant_see` → `look` (spoken "I can't see {thing} from here. {look_at}"), with no
       verdict;
     - any mismatch → `not_yet`, spoken with the failed check and its evidence ("Not yet: bottles
       and a sponge are still by the sink").
  3. Cached by frame hash, like pick 1.
- **The user always wins.** "Skip" or "I did it" advances and records `override: true`. The coach
  never blocks, and the report says which steps were checked by camera and which were only said.
- **The safety hook.** A step with `tool: "drill"` runs idea 4's local rule over the latest pick 1
  labels before saying the step. If the drill point is in the vertical band of an outlet or
  switch → the step is replaced by "Stop: that spot is straight above a switch box. Wires usually
  run up from it; move it 15 cm sideways or check with a detector." Until idea 4 ships, the hook
  does only that one rule.
- Session state lives in `jobs.py`'s table (like F10's tasks) and is persisted through `cache`, so
  a restart resumes at the step.

**Endpoints.**
- `POST /coach/start {session_id, part_id?}` → `{coach_id, job, source: "manual"|"template",
  steps: [{i, say, page, quote, checks}]}`. It uses the selected part.
  - `409` when the part is trade-only (with the reason).
  - `404` when no steps exist.
- `POST /coach/{coach_id}/check {i, frame: {id, jpg_b64}}` →
  `{i, verdict: "passed"|"not_yet"|"look", results: [...], spoken, next_i}`.
- `POST /coach/{coach_id}/advance {i, override?: true}` → the next step.
- `GET /coach/{coach_id}` → the state, for a reconnecting headset.
- Errors: `400` for a bad frame; `503` OFFLINE with a check miss (step text still works offline).

**Agent.**
- Tools:
  - `start_coach()`: "teach me", "walk me through it", "how do I install this";
  - `coach_step(move: next|back|repeat)`;
  - `check_step()`: "check it", "check my work", "done". "Done" checks first and advances only on
    `passed` or `override`.
- All three go in `agent.TOOLS`, so F1's relay exposes them by voice. The trigger phrases go in
  the tool descriptions, because fast paths don't run over the relay (integration doc).
- Step lines are spoken with `force_message` (verbatim, `interruptible: true`), so the manual's
  wording isn't paraphrased. The `page` goes on screen, not into speech.
- `check_step` uses the relay's `last_frame` (pick 1's `frame` uplink).
- Hands-free: for now a pinch or button starts talk. A "Hey Grok" wake word (§3) in Unity would
  remove the pinch.

**Unity contract.**

| Action | Unity |
|---|---|
| `coach_started {coach_id, steps[], source}` | A progress rail on the wrist with one dot per step. A ghost of the placed part at its anchor |
| `coach_step {i, say, page, quote, checks[]}` | A caption card with the step text, "Manual p. {page}", and the check questions as grey chips |
| `coach_check {i, verdict, results[], spoken}` | Chips turn green, amber (`not_yet`) or blue (`look`). `results[].box` highlights the evidence on the frame's pose (as pick 1) |
| `coach_stop {i, reason}` | A red card (the drill rule); the rail pauses until "next" |
| `coach_done {checked, overridden}` | A summary; adds a notebook entry for F4/F14 |

**Cost per use.**
- Writing steps from the manual: ~$0.003 once per part [I from F9's $0.002–0.014 answer calls].
- Each check: $0.0011, 1.8–5.4 s [V #13–14].
- A 6-step faucet swap with 2 checks per step, some repeated: about **$0.015** [I].
- Voice time on F1: $0.08 per minute. That is the real cost of a coached session: a 10-minute job
  is about $0.80.

**Test plan.** `tests/test_coach.py`:
1. Template path: the faucet job gives 6 steps with checks. A trade-only job (F13 fixture) → 409
   with the reason.
2. Manual path: F9's Midea PDF fixture. A step whose quote isn't in the text is dropped; `page` is
   where the quote was found.
3. Verdicts: #14's real reply against expectations → `passed` for c3, `look` for c2 and c4, and
   `not_yet` for c1 with its evidence in `spoken`. #13's status-style reply is never accepted
   (schema mismatch → 502, no verdict).
4. Override: "I did it" advances with `override: true`, and `coach_done` counts it.
5. Drill hook: fixture labels with a `switch` box and a drill pixel above it → `coach_stop`; the
   pixel 20 % to the side → the normal step.
6. Agent: "check it" with no frame → "hold still and look at it" and no call. Over the relay:
   `check_step` uses `last_frame` and speaks through `force_message`.
7. Resume: `GET /coach/{id}` after a simulated restart returns the same `i`.
8. OFFLINE: steps and cached checks work; a new check → 503.

**What to mock.** respx for `api.x.ai/v1/chat/completions` (#13/#14 replies as fixtures) and the
F9 manual fixture. Pick 1's labels come from a fixture file. No network.

---

## 7. Open questions

- **GROQ_API_KEY #1 is rejected** (401 "Invalid API Key" today). `LLM_VISION` and the agent on
  `main` default to Groq. Does `KeyPool` fall through to key #2 on a 401, or only on 429? We
  couldn't try key #2, because the session's permission check denied it.
- **Label naming errors** (switch vs outlet, 5 of 7 runs). Would a second tiny call on the crop
  ("outlet or switch?") fix it for $0.001? Or a 2-frame request from slightly different poses? It
  matters for idea 4's rules.
- **Hall Wi-Fi.** All the latency figures are from a home connection. Measure a 100 KB upload
  and time to first token from the venue before promising "about a second".
- **The Meta data policy.** Passthrough frames are "Device User Data". Sending them to xAI needs a
  line in the app's privacy text and, at the booth, a spoken "I'm sending this view to Grok"
  once.
- **X automation rules and refresh-token rotation.** We didn't re-read X's automation policy or
  confirm that refresh tokens rotate. Check both when the account is made.
- **A second kitchen scan** for idea 8. One more capture of the same kitchen with a few things
  moved would give a real before/after test.
- **Wake word.** `hey-grok-wakeword` is ONNX. Does it run under Unity Sentis on the Quest within
  budget? It isn't tested.

## Sources

- Our live calls (§1). The images:
  - [`g6/live-labels.jpg`](g6/live-labels.jpg): calls #3 and #7;
  - [`g6/hazard-checks.jpg`](g6/hazard-checks.jpg): calls #11 and #12.
- xAI docs (fetched 2026-09-26):
  - [voice agent](https://docs.x.ai/developers/model-capabilities/audio/voice-agent)
  - [image understanding](https://docs.x.ai/developers/model-capabilities/images/understanding)
  - [release notes](https://docs.x.ai/developers/release-notes)
- Meta: [Passthrough Camera API overview](https://developers.meta.com/horizon/documentation/unity/unity-pca-overview).
- X:
  - [pricing](https://docs.x.com/x-api/getting-started/pricing)
  - [chunked media upload](https://docs.x.com/x-api/media/quickstart/media-upload-chunked)
  - [OAuth 2.0 authorization code with PKCE](https://docs.x.com/fundamentals/authentication/oauth-2-0/authorization-code)
- GitHub:
  - [QuestCameraKit](https://github.com/xrdevrob/QuestCameraKit)
  - [Unity-PassthroughCameraApiSamples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)
  - [questvision](https://github.com/sandeepv6/questvision)
  - [Unity-PassthroughCamera-ObjectDetection](https://github.com/TREXKS/Unity-PassthroughCamera-ObjectDetection)
  - [OpenAI-for-Meta-Quest-3](https://github.com/hyunaseo/OpenAI-for-Meta-Quest-3)
  - [QuestRoomScan](https://github.com/genesisinteractive/QuestRoomScan)
  - [furniture-assembly-assistant](https://github.com/BinuShefieldShifani/furniture-assembly-assistant)
  - [hey-grok-wakeword](https://github.com/BSchoolland/hey-grok-wakeword)
- HN:
  - [Quest 3 golf ball tracking](https://puttdojo.com/devlog/tracking-a-real-golf-ball.html)
  - [Ally Solos glasses](https://www.theverge.com/news/759160/ally-solos-smart-glasses-ai-envision-low-vision)
  - [X API pay-per-use](https://news.ycombinator.com/item?id=46921507)
  - [x-twitter skill](https://skills.sh/alberduris/skills/x-twitter)
- X (via Grok `x_search` #16, checked through fxtwitter):
  - [@Sentdex](https://x.com/Sentdex/status/2095556673855221835)
  - [@inventorkhalifa](https://x.com/inventorkhalifa/status/2090027683698581563)
  - [@akinyi__wendy](https://x.com/akinyi__wendy/status/2079775535379742850)
  - [@ardchain](https://x.com/ardchain/status/2100313769478127829)
- Earlier rounds: [G1](g1-imagine.md), [G2](g2-voice-agents.md), [G3](g3-round2.md),
  [G4](g4-round3.md), [G5](g5-round4.md). The F1–F14 write-ups are on their branches.
