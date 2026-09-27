# F5: "See it installed" postcard

Feature build F5, 2026-09-26. Spec: [g1-imagine.md](g1-imagine.md) §3 idea 2. Branch
`feat/grok-imagine-postcard`.

**Bottom line.**
- `POST /parts/{part_id}/postcard` edits a captured site view so that the part's real seller
  photo appears installed in it. The result is served at `/parts/{id}/postcard.jpg`, and the
  checkout receipt carries its URL.
- It uses one Grok Imagine multi-image edit per postcard: $0.08 and 9–17 s (median 10.8 s).
- **The screen-space box is what makes it believable.** With a box, the part sits where it was
  placed at about the right size. With text placement alone, a sink came out ~1.3x too wide.
- Everything outside the edit is kept: median pixel change 2–4/255, and the framing is
  unchanged, so the output registers with the input camera.
- Live spend: **$0.71 over 9 calls** (cap $1.00).

## 1. How it works

- **Image 1** is the site view: a scene thumb (`site` + `frame_id`, via `imagine.load_frame`)
  or the headset's own JPEG (`frame_jpg_b64`). It is downscaled to ≤1280 px, and when a `box`
  is sent, a magenta rectangle is drawn around it.
- **Image 2** is `data/parts/<id>/image.jpg`, the seller photo.
- **The prompt** (`postcard.build_prompt`) has five parts:
  1. What each image is.
  2. The part's brand, full name and published dims (`W x D x H mm`).
  3. Where it goes: "exactly inside the magenta rectangle (…placement…)", or the placement text
     alone.
  4. "Use only the main product from image 2 -- not its accessories, badges, labels or text".
  5. A mount hint (undermount, wall-mounted), the lighting and perspective match, and G1's
     "keep everything else unchanged" clause.
- **The client.** `server/imagine.py` is the sibling branch's thin xAI edits client (plain
  httpx JSON, `images: [...]`), copied with the same function names. `reimagine()` was left out.
- **Caching.** Every call goes through `cache.cached("imagine", …)`, so an identical request
  replays for $0, also when `OFFLINE`.
- **Direct response, not a job.** A 10 s call suits the `/scene/ask` pattern, and the headset
  fires it when the seller panel opens.
- **Voice.** The agent tool `see_it_installed` returns the client action `show_postcard`. A fast
  path catches "show me what it'll look like" and "see it installed" without the LLM.

## 2. Live results

Site views are kitchen scene thumbs (640×360; no building thumbs exist in `work/viewer/`, and
the spire capture has nowhere to install a part). Outputs are 1280×720. "Outside diff" is the
median absolute pixel difference to the upscaled input, excluding the box grown by 10 % of the
frame.

| # | Try | Part | Frame | Mode | Cost | Latency | Outside diff | Verdict |
|---|---|---|---|---|---|---|---|---|
| 1 | sink-M | Karran black quartz undermount | 0241 | multi-image, text placement | $0.08 | 13.2 s | 3.0 | Right spot, but **~1.3x too wide** and reads as a drop-in; the rim covers the counter. Usable only as a mood shot |
| 2 | sink-MB | same | 0241 | multi-image + red box | $0.08 | 11.6 s | 4.0 | **Good.** Sized to the cut-out, box removed cleanly, colour and shape right. Still a thin drop-in rim |
| 3 | sink-C | same | 0241 | composite paste + blend (1 image) | $0.07 | 8.9 s | 4.0 | Good, similar to #2, and it keeps the drain. But see the cutout problem below |
| 4 | split-MB | LG indoor mini split | 0481 | multi-image + red box | $0.08 | 9.7 s | 2.3 | **Good.** A clean unit on the wall under the cabinet, LG logo kept, the "Indoor Component Only" banner ignored. It sits a little above the box |
| 5 | fridge-MB | Samsung Bespoke 4-door | 0441 | multi-image + red box | $0.08 | 10.8 s | 2.3 | **Excellent.** The old fridge is swapped, the Home Depot badge and ice-maker banner are dropped, cabinets and counter untouched |
| 6 | e2e sink | Karran | 0241 | via `POST /parts/{id}/postcard`, red box + mount hint | $0.08 | 10.6 s | 3.3 | Good. The rim is thinner and flush with the counter; a true undermount look is still not reached |
| 7 | e2e fridge-M | Samsung Bespoke | 0441 | via the endpoint, text only | $0.08 | 17.4 s | 2.3 | **Excellent.** Replacing a same-size object needs no box |
| 8 | e2e split upload | LG mini split | 0521 as `frame_jpg_b64` | via the endpoint, red box | $0.08 | 14.9 s | 2.7 | Unit good, but **the red wall sign under the box was erased** ("remove the red outline") |
| 9 | e2e split upload | same | same | magenta box | $0.08 | 10.5 s | 3.0 | **Good.** The sign is kept (its text is re-drawn); the unit fills the box |

- **Totals.** $0.71 in 9 calls: 8 at $0.08 (two-image edits) and 1 at $0.07 (one image).
- **Latency.** 8.9–17.4 s, median 10.8 s.
- **Zero-cost replays.** The agent fast path and the `/debug` console both replayed from cache
  at $0 (checked in the server log), and the checkout receipt came back with `postcard_url`.

Images:
- [`f5/sink-before-after.jpg`](f5/sink-before-after.jpg): try #6.
- [`f5/fridge-before-after.jpg`](f5/fridge-before-after.jpg): try #7.
- [`f5/minisplit-before-after.jpg`](f5/minisplit-before-after.jpg): try #9, a headset upload.
- [`f5/sink-box-vs-text.jpg`](f5/sink-box-vs-text.jpg): tries #1, #2, #3 and #8 side by side.

## 3. Prompt findings

- **Multi-image works.** `images: [site, product]` with "image 1 / image 2" wording puts the
  product from image 2 into image 1. The output follows image 1's aspect and framing. So the
  composite fallback is not needed.
- **The box beats words for scale.** The dims in the prompt did not stop the sink from
  over-sizing (#1). The model scales to the gap, not to millimetres. A drawn rectangle plus
  "It fills the rectangle; remove the outline" gave the right size every time (#2, #4, #5, #6,
  #9), and no outline pixels survived in any output.
- **The outline colour matters.** "Remove the red outline" also removed a red sign next to the
  box (#8). Magenta kept it (#9, n=1). Magenta is the default now.
- **"Use only the main product … not its accessories, badges, labels or text" works.** Seller
  photos with a remote, an outdoor unit, a Home Depot badge or a promo banner all came out as
  the bare product.
- **Replacements need no box.** When the part replaces a same-size object (fridge for fridge),
  text placement alone is enough (#7).
- **Mount style is weak.** "Undermount sink below the counter edge, no rim showing" only
  thinned the rim (#6 vs #2). The seller photo shows the rim, and the model trusts the photo.
- **Known Imagine habits (as in G1).** Small text is re-drawn: the "PLEASE DON'T PUT FOOD IN
  SINK" sign, the dishwasher badge. Fridge magnets go with the fridge they were on.

## 4. What didn't work or wasn't tried

- **The composite approach** (paste the product cutout into the box, then ask Imagine to blend)
  gave a result as good as the box approach on a dark sink (#3). But the cutout
  (threshold-on-white, biggest blob) **fails on white products on white backgrounds**: for the
  LG mini split it picked the red "Indoor Component Only" banner instead of the unit. Kept as a
  finding, not in the code.
- **No exterior test.** There are no house or building thumbs in the repo (`work/viewer/` has
  only meshes, and the spire capture is a cathedral). The gutter-on-fascia case in the spec is
  untested.
- **Wrong published dims.** The mini-split parts carry outdoor-unit or box dims (LG: 839 × 324 ×
  760 mm; a real indoor head is ~840 × 190 × 300). They go into the prompt as-is. The box
  overrode them in #4 and #9, but a text-only mini split would be scaled from bad numbers.

## 5. Demo

1. `uv run uvicorn server.app:app --host 0.0.0.0` with `GROK_API_KEY` in `.env`.
2. Open `/debug` → "See it installed". Type a part id (or press Select on a search card), pick
   the kitchen frame `0441`, and type "replacing the white fridge on the left". Generate
   (~10 s). Before and after appear, with the label.
3. Or drag a box on the frame (e.g. frame `0241` over the sink with a sink part), then
   Generate.
4. Voice: with a card selected and a frame picked, say or type "show me what it'll look like".
   The reply is "Here's how it'll look installed…" plus a `show_postcard` action, and the
   console shows the picture.
5. Press Checkout on the card: the receipt JSON has `postcard_url`.
6. Pre-warm: run each demo request once online. The same request then replays from the cache
   for $0, also with `OFFLINE=true`.
