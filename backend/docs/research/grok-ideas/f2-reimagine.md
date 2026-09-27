# F2: "Reimagine this view" (renovation preview)

Feature agent F2, 2026-09-26. Builds idea #1 from [g1-imagine.md](g1-imagine.md): Grok Imagine
edits a capture frame of the scanned building, and the headset shows the edit at that frame's
camera, lined up with the mesh. Branch `feat/grok-imagine-reimagine`.

## What was built

| Piece | Where | What it does |
|---|---|---|
| Edit client | `server/imagine.py` `edit(images, prompt, *, model, resolution)` | Plain-JSON `POST https://api.x.ai/v1/images/edits` over httpx (`image` for one input, `images` for 2–5, as base64 data URIs, `response_format: b64_json`). Logs cost from `usage.cost_in_usd_ticks` (1e10 ticks = $1) and latency. Errors map to `ModerationBlocked`, `RateLimited` and `ImagineError`. Every call goes through `cache.cached("imagine", ...)`: the JSON cache holds only `{id, cost, latency}`, and the JPEG lands at `<DATA_DIR>/imagine/<id>.jpg` (~170 KB). OFFLINE replays rehearsed prompts. |
| Frame loader + `reimagine()` | `server/imagine.py` | Reads `scene.json` → `cameras.r<rev>.json`, finds the frame by id, uses `file` (full frame) when the package has it, else the 640 px `thumb`. Appends "keep the camera, framing and everything else unchanged" to the prompt. |
| Endpoint | `POST /scene/reimagine {site, frame_id, prompt, resolution?}` + `GET /scene/reimagine/{id}.jpg` | Returns `{image_url, before_url, frame_id, camera, label, cost_usd, latency_s, cached}`. Errors: `400` unknown site/frame or empty prompt, `422` moderation, `429` rate limit, `502` other xAI failure, `503` offline cache miss. |
| Agent tool | `reimagine_view(prompt)` in `server/agent.py` | Uses `context.site` + `context.frame_id` (falls back to `frames[0].id`). Returns the action `show_reimagined {image_url, frame_id, camera, label}` and ends the turn with a canned line (no second LLM turn). The tool is only offered when the context has a site and a frame, so ordinary commands pay no extra Groq tokens. |
| Browser try-out | `/debug`, section "Reimagine this view" | Pick a site and a frame thumbnail, type a prompt, and compare before and after with a slider. The label is always shown. The chosen site and frame also ride along as agent context. |
| Docs | `docs/api.md` | The endpoint, the Unity quad-placement recipe from `R, t, fx, fy, cx, cy, w, h`, the action row, and the context fields. |
| Tests | `tests/test_imagine.py` (15, respx-mocked) | Request shape (model, data URI, prompt, `image` vs `images`), cost parsing, cache hit and OFFLINE replay, error mapping, route 400/422/503, image serving, the agent tool → action, and tool gating. |

## How to demo

1. `uv run uvicorn server.app:app --host 0.0.0.0 --port 8000` with `GROK_API_KEY` in `.env` and
   `scene/kitchen` present.
2. Open `http://<laptop>:8000/debug`, then "Reimagine this view": site `kitchen`, click thumb
   `0481`, prompt "Paint all the cabinet doors navy blue and add brass pulls.", press Reimagine.
   About 9 s later drag the slider.
3. Through the agent: with a frame selected in the same page, type "What would this look like
   with a white subway tile backsplash?" in Command. The reply is "Here's the sketch. Pinch to
   flip before and after." and the slider shows the edit.
4. Before going on stage, run each demo prompt once with the network up. Then `OFFLINE=true`
   replays them for $0 (the cache is keyed on the prompt's text, case- and punctuation-
   insensitive, plus the frame's bytes).

## Live results (4 edits, $0.28 total)

| # | Frame | Prompt | Cost | Latency | Shift vs source | Result |
|---|---|---|---|---|---|---|
| 1 | 0481 | Paint all the cabinet doors navy blue and add brass pulls. | $0.07 | 9.2 s | 0 px | Every door navy with brass bar pulls; fridge magnets, bottles and appliances kept. It also gave the flat doors a shaker profile (a shape change nobody asked for), and it redrew the sign text. |
| 2 | 0241 | Replace the stainless sink with a black undermount sink. | $0.07 | 7.3 s | 0 px | Clean black undermount basin in the same cut-out, tap and counter untouched. [`f2/sink-before-after.jpg`](f2/sink-before-after.jpg) |
| 3 | 0481 | Add warm under-cabinet LED strip lighting beneath the upper cabinets. | $0.07 | 8.5 s | 0 px | Warm light strips under both upper runs, with a plausible glow on the backsplash; nothing else moved. [`f2/led-before-after.jpg`](f2/led-before-after.jpg) |
| 4 | 0241 | (via the agent) "What would this look like with a white subway tile backsplash?" | $0.07 | 8.7 s edit, 9.4 s end to end (Groq 0.6 s) | n/a | White subway tile behind the outlets and sign; outlets kept. Sign text garbled ("PUT FOOD IIS SINIG"). |

"Shift" is the best integer offset (±4 px search) between the edit, downscaled to 640×360, and the
source thumb. It was 0 every time, confirming G1: the edit registers with the frame's camera. The
median absolute difference to the source was 3.7/255 (sink) and 4.3/255 (LED), so almost every
pixel outside the edit is unchanged. For the cabinets it was 12.7/255, because the doors cover a
third of the frame. Every output was 1280×720 JPEG at `1k` from the 640×360 thumb. A repeat
request came back from the cache in milliseconds with `cost_usd: 0`.

## Limitations

- **640 px input.** Packages ship only `thumbs/` (640×360); the `file: frames/NNNN.jpg` that
  `cameras.r<rev>.json` names is not in the package. The model upscales to 1280×720 and
  sharpens, which reads fine on a 1.5 m quad but invents fine texture. When the pipeline starts
  packaging full frames, `load_frame` picks them up automatically.
- **Not to scale.** The edit is an illustration. The label "AI preview, not to scale" is in
  every response and must be shown. True-size fit stays the job of the 3D parts.
- **Small text is redrawn.** Signs and labels come back garbled in every run. Doors can gain
  detail (the shaker profile in #1).
- **The agent call blocks for ~9 s.** `/agent/command` returns only after the edit. The headset
  should play a filler ("Sketching it up…") or show a spinner.
- **Moderation.** Not triggered in our runs. The `422` mapping keys on a 4xx whose body mentions
  moderation, safety or policy, and on `respect_moderation: false`. The exact xAI error body for
  a blocked edit is unverified.
- **Offline via voice.** OFFLINE skips the LLM path, so "reimagine" by voice needs the uplink;
  offline, use the endpoint (the debug page or a headset button) with a rehearsed prompt.

## Prompt tips

- Name the object and the change, including the style: "Replace the stainless sink with a black
  undermount sink" gave an undermount; G1's photo-driven swap, which named no style, came back
  as a drop-in.
- Keep to one or two changes per prompt; each result is a separate edit of the original frame.
- Say where: "beneath the upper cabinets" put the LEDs exactly there.
- Keep people out of the frame (moderation) and do not ask for text.
- The server already adds "keep the camera, framing and everything else unchanged", so there is
  no need to repeat it.
