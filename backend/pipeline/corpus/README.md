# Test-footage corpus

`corpus.json` is a manifest of 11 online video/dataset clips chosen to stand in
for DJI Mini 4K drone footage while we have none of our own yet. It is input
to the photogrammetry pipeline test harness (drone video -> textured 3D mesh
of a building), not footage itself -- nothing here has been downloaded in
full, only verified via `yt-dlp -J --no-download` (or, for the two research
datasets, by fetching their project pages) and a ranged `curl` request
against each `direct_url` to confirm it resolves (HTTP 206).

## What's in it

| Priority | Meaning | Count |
|---|---|---|
| 1 | Orbit of a single tall building/tower/church/lighthouse/water tower | 5 |
| 2 | Facade pass / close pedestal shot | 3 (incl. 1 dataset) |
| 3 | Top-down or high-oblique roof pass | 3 (incl. 1 dataset) |

Clips: `eiffel-tower-orbit-4k`, `strasbourg-cathedral-spire`,
`lighthouse-orbit-coastal-1`, `lighthouse-breakwater-flag`,
`watertower-forest-orbit`, `facade-glass-greenroof`,
`facade-highrise-corner`, `roof-highrise-corner`, `campus-dorms-flyover`,
`dataset-zurich-urban-mav`, `dataset-urbanscene3d-polytech`.

Two named landmarks carry a published real-world dimension for scale
testing: the Eiffel Tower (330 m to antenna tip) and Strasbourg Cathedral
(142 m spire), both cited to Wikipedia in `corpus.json`. The Zurich MAV
dataset additionally carries a precise physical calibration-checkerboard
size (2.45 cm/square) straight from its paper.

## How clips were chosen

1. Searched Pexels, Pixabay, Mixkit, Wikimedia Commons, and public
   photogrammetry/SLAM datasets for drone orbits of tall single buildings,
   facade/pedestal passes, and roof/top-down passes, per the brief's
   priority order.
2. Rejected candidates on sight of their thumbnails or metadata for: night,
   fog/haze on the subject, timelapse, heavy people/water/landscape
   dominance, or too-short duration (e.g. a 15s "church tower" clip with a
   timelapse tag and heavy backlight/haze, a 6s clock-tower clip, and a
   45s Florence cathedral clip that returned a persistent Cloudflare 403 and
   could not be verified at all -- all three were dropped, not included).
3. Every surviving candidate was checked with `yt-dlp -J --no-download`
   (duration, resolution, fps, direct mp4/webm URL), and for ambiguous
   candidates a small thumbnail image (a few hundred KB, not the video) was
   downloaded and visually inspected to confirm motion type and subject --
   no full video was downloaded.
4. Every `direct_url` in `corpus.json` was re-confirmed live with a 1KB
   ranged `curl` request (HTTP 206) right before writing the manifest.

## Licenses present

- **Pexels License** (8 clips) -- free for commercial and personal use, no
  attribution required.
- **CC BY 3.0** (1 clip, Eiffel Tower, via Wikimedia Commons) -- attribution
  to "the Dronalist" required.
- **No-restriction / commercial-OK** (Zurich Urban MAV dataset) -- please
  cite the paper.
- **Non-commercial / academic only** (UrbanScene3D) -- do not use if the
  pipeline ships commercially without separate clearance from the dataset
  authors.

## How to fetch

One `yt-dlp` command per clip (uses each clip's page `url`, not the raw
`direct_url`, so yt-dlp re-resolves the freshest link):

```bash
# Stock clips (Pexels / Wikimedia) -- grab the best <=2160p stream
yt-dlp -f 'bv*[height<=2160]+ba/b[height<=2160]' -o '<id>.%(ext)s' '<url>'

# e.g.
yt-dlp -f 'bv*[height<=2160]' -o 'eiffel-tower-orbit-4k.%(ext)s' \
  'https://commons.wikimedia.org/wiki/File:Eiffel_Tower_Drone_4k-Qx_c1X3zfEc-313-251.webm'
yt-dlp -f 'bv*[height<=2160]' -o 'strasbourg-cathedral-spire.%(ext)s' \
  'https://www.pexels.com/video/strasbourg-s-cathedral-drone-17338280/'
```

```bash
# Zurich Urban MAV dataset -- direct zip, no yt-dlp needed
curl -L -o dataset-zurich-urban-mav_sample.zip \
  https://download.ifi.uzh.ch/rpg/AGZ_data/AGZ_subset.zip   # 200MB sample
# full 28GB set: https://download.ifi.uzh.ch/rpg/AGZ_data/AGZ.zip

# UrbanScene3D -- no direct link found; browse and download manually from
# https://vcc.tech/UrbanScene3D (PolyTech / ArtSci real-scene capture)
```

## Known gaps / not included

- No real DJI Mini 4K footage yet (that's the point of this corpus).
- Could not confirm a specific published height for either lighthouse clip
  or the water tower -- their `known_dimension` fields are `null` rather
  than a guessed figure (a visual guess for one lighthouse -- Kadikoy
  Inciburnu, Istanbul -- is noted but explicitly flagged unconfirmed).
- Tanks and Temples benchmark (Church/Courthouse scenes) was considered but
  **not included**: its raw 4K video is captured from a ground-level
  motorized rig, not a drone, so it doesn't match the brief's "drone video
  or dense drone image sequence" requirement -- worth a look separately if
  ground-level MVS test data is ever wanted.
- `eiffel-tower-orbit-4k` is a 5-minute clip; only a portion of it is
  likely one continuous orbit. `use_segment_s` was left `null` because
  picking the exact sub-segment requires watching the file, which was out
  of scope for this manifest (no full downloads). Watch it once before
  using it in the pipeline and trim to the best continuous ~30-60s orbit.
