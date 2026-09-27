# AirTool — HackGT 13, Immersive (AR/VR/XR) track

*Written 2026-09-22; rev 2026-09-24 adds the parts layer (voice-found parts at true size, fit check, sellers, Visa checkout); rev 2026-09-24 (later) drops Gaussian splats after an on-device fps test and ships a textured mesh instead (§5, §9). Theme: seaside plaza; track name "The Lighthouse Laboratory". Hardware: Meta Quest 3S, DJI Mini 4K. Supersedes the Crow's Nest / Through the Door sections of "HackGT 13 - Immersive track (Quest 3S + DJI Mini 4K).md"; the research in that file (constraints, past winners, prior art) still applies.*

---

## 1. One-liner

**Fly the drone to the thing you can't reach. Stand next to it in VR with a toolbox on your belt and measure it. Then ask for the real replacement part, hold it at true size, fit it on the building, and buy it.**

AirTool turns one manual drone flight into a true-scale, gravity-aligned 3D copy of a roof, chimney, gutter or facade. Inside it you get hand-tracked tools: tape measure, level, protractor, plumb line, area, notes. Every measurement is logged with the photo it came from.

Then the ship's chest the toolbox came out of becomes a parts store:
- Say "find a hanger for this gutter". Grok searches the web for real parts that fit your measurement and pulls their published specs (dimensions, colour, material, sellers, a 3D model where one exists).
- The part lands in your hand **at true size**. You place it on the building and it snaps to the surface; the outline turns green, amber or red for clearance.
- "Every 60 cm along this" fills the run and sets the quantity.
- Scroll the real sellers, and pay with Visa (sandbox) with a physical pinch-and-hold, or open the seller's page.

You walk away with the numbers, the part list, and the order. Or you cut the part yourself.

## 2. Why this wins the track

| What judges reward here | AirTool |
|---|---|
| Headset doing something only a headset can do | Reaching out and measuring at 1:1 with your hands; a toolbox anchored to your hip |
| A physical object on the table (Dispatch, Holo-cade, Lightning McScream, Dose all had one) | The drone, props off, next to the headset |
| A purpose stated in one sentence | "Roofers climb ladders to measure gutters. We don't." |
| A measured number on stage | VR tape vs. real tape on the same window: "within X cm over 3 m" |
| Theme fit | Nautical measurement (fathom, plumb line, sounding); the toolbox opens like a ship's chest |
| Novelty | Drone roof reports exist as 2D PDFs (EagleView, Hover). Nobody puts you on the roof with tools. No hackathon precedent found for hand tools inside a captured twin |
| Presence you can hold | A real part, found by voice, appears **in your hand at its published size** on the building you just measured, and physically doesn't fit if it doesn't fit. Then it follows you back into passthrough and sits on the real table next to the laser-cut one |
| Talking to the world | Push-to-talk voice agent (Grok) that answers out loud, from the toolbox on your hip |
| Thin field | ~7 XR entries among 120 sampled HackGT 12 submissions |

Answer to "isn't this just Apple's Measure app?": Measure works on what your phone can see from where you stand. AirTool measures what only the drone could reach, hours after it landed, at true scale, with the evidence photo attached.

## 2b. Pre-event packet (read 2026-09-22) and how AirTool uses it

Source: hexlabs.notion.site/HackGT-13-Pre-Event-Packet-cf10438064318246b668017b1b3030e4

**Facts that change the plan**
- **Hacking ends Sunday 8:00 AM** (not Sunday noon). Expo is Sun 9:00–11:15 AM in Klaus; closing 12–1 PM at Ferst. Devpost must be done by 8 AM.
- **One track only** per project; any number of sponsor challenges if requirements are met (verified). Immersive it is; Aramco's Social Good track is off the table.
- **Team max 4.** Winners each get their own copy of the prize.
- **Prizes:** Lighthouse Laboratory 1st = ASUS TUF 27" monitor, 2nd = AirPods 4. Grand: Canon G7X III / Switch 2 / iPad. Prizes collected in person after closing.
- **The Hive makerspace is open Sat 3–9 PM** (laser cutters, fablight, electronics benches, machine shop). Only for hardware checked out from the Hardware Desk; bring school ID.
- Hardware Desk (badge required; full list at event start). Listed as "main/popular": Arduino Unos, Raspberry Pi 3s, motors/servos/microservos, small breadboards/jumper wires, **Oculus Quests (VR)**, Amazon Echos, "and more". No drones. We bring our own Quest 3S and Mini 4K regardless.
  - **Loaner Quests** are the useful item: check one out as a second headset so a second judge (or a teammate narrating) can be in the scene while the first judge measures, and as a fallback if the 3S has an issue. Check the model at the desk; an older Quest 2 will need a lower mesh/texture budget than the 3S build.
  - Checking out any one item (even an Arduino) is what satisfies the Hive's "hardware checked out from the desk" rule for the laser-cut part.
  - Servos on the desk mean the "Robot Thumbs" stick-actuator idea is possible, but it stays cut: it adds risk and no demo value to AirTool.
- Check-in Fri 2:00–3:45 PM at Ferst; needs government ID **and** student ID. Big bags go to Klaus first.
- Keynote: Thomas Dohmke (Entire, ex-GitHub CEO). Tech talks and mini-challenges by NSA, Visa, Impiricus, Meta, SpaceXAI, Aramco.
- Nothing in the packet mentions drones, so the GTPD flight plan and an email to hello@hexlabs.org remain the only permissions that matter.

**Track fit:** the Immersive brief now reads "experiences that blur the lines of reality and pull people into another world using AR/VR/XR." AirTool does both halves: passthrough with a chest on the real table (blur), then the drone-captured building at 1:1 (another world). Say those words back to the judges.

**Sponsor challenges, ranked by cost to qualify**

| Challenge | Fit | What it costs | Prize | Do it? |
|---|---|---|---|---|
| **SpaceXAI** (build with Cursor + Grok; societal problem) | **Strong.** The stated problem is worker safety (falls from height). Grok is load-bearing in four places:<br>• **Grok Voice** realtime agent with function calling drives the app hands-free<br>• **Grok web search + structured outputs** find real parts and extract specs<br>• Grok picks the seller<br>• **Grok Vision** runs "ask the scene" | Code in Cursor (everyone gets credits); one `grok.py`; say so in the write-up | Custom Cursor keyboard per member; Cursor/Grok credits for all | **Yes** |
| **Notability** ("Trust the Process") | Trivial: use Notability Pro for the team's planning/measurement notes, 2 screenshots, "Notability" tag | 10 minutes | 1 yr Notability Pro each | **Yes** |
| **Create-X** | Tick the interest box at submission | 0 | Startup Launch pipeline | **Yes** |
| **Visa** (Gen-AI commerce, secure payment) | **Now native to the demo, not a bolt-on.** It covers the whole brief:<br>• *discovery:* Grok finds parts that fit the measurement<br>• *decision:* try it on the building at 1:1 and see the fit colour<br>• *personalisation:* quantity from the measured run, finish from the real brick, seller from "cheapest" or "arrives by Friday"<br>• *secure checkout:* Visa Acceptance sandbox authorization, confirmed by a physical hold; no card data in the headset; voice can never pay<br>"Measure-to-cart", in the headset. | ~6 h, spread across P3 (carousel, checkout panel) and P4 (sellers, sandbox) | **$5,000** | **Yes.** Carousel + checkout after the Sat 6 PM checkpoint. "Open at seller" always ships |
| Meta (AI social product, human connection) | Poor: AirTool isn't social. Don't stretch it | — | Trip to Menlo Park round 2 | No |
| NSA (audio deepfake / packet CTF / Codebreaker) | None | — | — | No (but attend the talk; NSA judges may roam) |
| Impiricus (HCP engagement) | None | — | — | No |

**Makerspace beat (the user's step 6, "then build the part"):** measure the gutter bracket / flashing in VR Saturday midday, export the dimensions, laser-cut or 3D-print the part at the Hive between 3 and 9 PM, and put the physical part on the expo table next to the drone and the headset. That closes the loop "fly → measure → make" with three physical objects on the table. Check out something (anything) from the Hardware Desk to satisfy the Hive's access rule.

## 3. The judge's three minutes

1. **(0:00)** The judge picks up the Quest 3S. The drone sits on the table. Passthrough shows the room.
2. **(0:15)** A ship's chest sits on the table. Opening it swaps the room for the campus building the drone flew Saturday morning, at 1:1. They're standing beside a second-storey window and gutter they could never reach.
3. **(0:30)** They reach down: the toolbox is on their hip.
   - They pull out the **tape**, pinch one end of the gutter run, then the other. The tape unrolls and snaps: **4.20 m**.
   - Across the window: **1.52 m**.
4. **(0:55)** You hold up the real tape-measure photo: 1.50 m. Say the error out loud.
5. **(1:05)** Hold to talk: *"Quartermaster, find a hanger for this gutter."*
   - The voice answers from their hip, and three candidate cards fan up out of the chest, each with a photo, W×D×H and a price.
   - They poke one and it drops into their hand **at its real size**.
   - They press it to the fascia: it snaps flush and the outline goes **green**.
   - *"Every sixty centimetres."* Eight hangers pop in along the tape line.
   - (Alternate: the window AC unit slides into the measured window, and its outline goes **red**: 12 mm too wide.)
6. **(1:50)** *"Show me sellers, cheapest first."*
   - They scroll the carousel with a finger; Grok's pick is tagged "cheapest that arrives before Friday".
   - **Pay with Visa**: they hold a pinch while the ring fills, a chime plays, and a receipt appears with the approval code (sandbox). The order lands in the **notebook** beside the measurements.
7. **(2:20)** The chest closes and they're back in passthrough. The hanger they just bought sits **on the real table**, at true size, next to the **real bracket** we laser-cut at the Hive from the same VR measurements. "Fly, measure, fit, buy — or make."
8. *(If time or asked)*
   - **Level** on the sill (0.8° off), **protractor** on the roof edge (26°)
   - a **live** Grok search, about 30 s
   - **rain** on the roof

**Rehearse beats 3, 5, 6 and 7.** Beats 3–4 are the proof; beats 5–7 are the pitch.

## 4. What it does

### Core tools (must ship)
| Tool | Interaction | Output |
|---|---|---|
| Tape measure | Pinch two points; tape snaps to nearest surface | Distance, logged |
| Level | Place on a surface | Tilt from true gravity, bubble |
| Notebook | Automatic | Every reading + source-frame snapshot + 3D location; export CSV/sheet |

### Second tier (Saturday night)
| Tool | Interaction | Output |
|---|---|---|
| Protractor | Three pinches | Angle (roof pitch, corner squareness) |
| Plumb line | Drop from a point | Offset from vertical |
| Area | Tap polygon corners on a face | m² → shingle/paint quantity |
| Pins / voice memo | Pinch + speak (Grok voice `add_note`) | Annotation with photo evidence |

### Parts: find, fit, buy (core, after tape + level + notebook)
| Feature | Interaction | Output |
|---|---|---|
| Find by voice | Push-to-talk: "find a hanger for this gutter", "a window AC for this window". The last tape reading goes with the query | Grok web search + structured output returns real candidates with published dims, colour/finish, material, sellers, a 3D model URL and citations. The server checks every dimension and URL |
| True-size part in hand | Poke a candidate card; the part lands in your hand | GLB scaled to the published dimensions. Badge shows where the geometry came from: manufacturer model / library model / AI mesh (exact size, approximate look) / proxy (exact size) |
| Place and fit-check | Press it to the building; it snaps to the surface | Green = clear, amber = service clearance violated, red = collides. W×D×H callouts; "fits, 38 mm spare" against your tape reading |
| Array | "Every 60 cm along this" | Instances along the tape line; quantity goes to the cart |
| Finish | Poke a swatch, or "show it in brown" | Recolours in place against the real brick; the seller list switches SKU |
| Sellers | Scroll the carousel by hand, or "cheapest", "arrives by Friday" | Real sellers and prices (SerpApi Google Shopping + Home Depot), Grok's recommended pick with a reason |
| Buy | **Pay with Visa**: hold a pinch 1 s (voice alone can never pay). Or **Open at seller** | Visa Acceptance sandbox authorization + receipt in the notebook; or the real product page in the Quest browser |
| Take it home | Close the chest | Back in passthrough; the bought part sits on the real table at true size |

### Stretch (only if core is solid)
- **Rain simulation:** gravity flow over the roof heightfield; shows pooling and gutter fall. Visual, on-theme.
- **Ghost part:** drop a parametric bracket/flashing template, resize to the measurements, export STL, print at the hardware desk.
- **"What else do I need?"** Grok turns the placed parts into a bill of materials (screws, sealant, end caps) in the same cart.
- **Ask the scene:** "show me damage" → vision model over frames → pins in 3D.
- **Compare:** wipe between two captures.

## 5. How it works

```
DJI Mini 4K flight (manual, 5–10 min orbit, Video Subtitles ON)
        |  4K MP4 with 1 Hz GPS/altitude subtitle track
        v
ffmpeg: frames @ 2-3 fps, sharpest-per-window + telemetry extract
        v
Reconstruction (hfbox, remote AMD ROCm GPU box), in two passes:
   PREVIEW, ~1 min: VGGT poses + depth on <=64 frames -> TSDF mesh
             -> OpenMVS TextureMesh from the full-res photos -> served right away
   FULL, 10-30 min: pycolmap CPU SIFT, sequential matching + loop-closure pairs,
             GLOMAP global mapping (incremental fallback) -> undistort
        v
   OpenMVS CPU: DensifyPointCloud -> ReconstructMesh -> crop -> TextureMesh
             (decimate to ~200k tri, bake a 4096 JPEG atlas -- this is the visual)
             -> aligned onto the preview's frame, swapped in live on the headset
        v
Calibration: (a) scale, ranked scalebar_aruco > known_dimension > gps > metric_depth > none
                 (ArUco board triangulated across views, a known dimension, or GPS Umeyama fit)
             (b) "up" from the GPS altitude track, or camera roll~=0 when no GPS
             (c) report residual error
        v
Package: mesh.glb (textured, the visual) + collision.glb (untextured, for snapping)
         + camera poses + frame thumbnails
        v
Quest 3S, Unity 6 + Meta XR SDK (Interaction SDK hands, glTFast mesh rendering)
   hands -> tools -> measurements -> notebook -> export
        |
        v  parts layer (laptop server holds every key; the Quest only gets a 5-min voice token)
Grok Voice realtime agent (push-to-talk, function calls = the same methods as the buttons)
   -> find_part(query + last measurement)
   -> Grok web_search + JSON schema: dims, colour, material, sellers, model URL, citations
   -> validate (dims present, HEAD every URL, Home Depot spec overrides) 
   -> asset tiers: manufacturer/BIM model -> Sketchfab/Poly Pizza -> Tripo image-to-3D -> exact-size proxy,
      all scaled to the published dims
   -> Quest: glTFast runtime load -> grab -> snap to mesh -> OverlapBox fit check -> array
   -> sellers (SerpApi Google Shopping + Home Depot, cached) -> Visa Acceptance sandbox auth -> receipt
```

**Where the engineering depth is (say this on stage):**
1. **Metric scale.** GPS is good to ~1–3 m, so it can't scale a window. A known-size reference in the scene fixes scale; GPS then only orients north and sanity-checks. Report the error bar.
2. **Gravity.** The level and plumb line are lies unless "up" is right. Estimate from the altitude track and vertical structure; verify against a real level on the real sill.
3. **Snapping on textured geometry.** The textured mesh is what you see, but tools shouldn't snap to its exact (sometimes noisy) surface. A second, untextured collision mesh from the same reconstruction, same frame, is what tools actually raycast against.
4. **Performance.** Gaussian splats were tested on the 3S and dropped: 50k splats held 72 fps, 200k held 60 fps, 400k fell to 36 fps, and a real building needs well over 200k to look good. The visual is a textured mesh instead — ~200k triangles, one 4096 JPEG atlas as a starting budget, tuned on device — plus hand tracking, at 72 fps.
5. **Trusting an LLM with geometry.** Grok finds parts, but its dimensions and URLs are checked before anything is drawn. Every dim must be present; every URL must answer a HEAD request; Home Depot's published spec overrides Grok when both exist. AI-generated meshes only ever dress a size that came from a published spec, and the badge says so.
6. **Secure checkout in a headset.** Card data never reaches the Quest. Payment needs a deliberate physical hold, never voice alone. The server makes the Visa call with sandbox credentials, and the APK ships without any API key.

## 6. Constraints that shaped it (verified 2026-09-21)

- Mini 4K has **no SDK** (not in DJI MSDK v4 or v5), no waypoints; a human flies. Live video only via phone mirroring (~0.4 s). AirTool needs neither: it is post-flight by design.
- Telemetry is post-flight only: 1 Hz GPS + altitude embedded as MP4 subtitles when **Video Subtitles** is on. No attitude/gimbal angles.
- Reconstruction: a ~1 min preview mesh first (VGGT + TSDF, measured 19 s cold on 48 frames), then the full mesh, measured 9–31 min per site end to end on `hfbox` (remote AMD ROCm GPU box), against online test footage; `DensifyPointCloud` dominant (`docs/research/p1-quality-runs.md`).
- Georgia Tech: **flight plan filed with GTPD per flight, 24–48 h ahead** (flyright.police.gatech.edu). Couch Park is the approved site. Carry TRUST certificate. Indoor flight is up to HexLabs; assume none at judging.
- Hacking starts after sunset Friday, so the first capture is Saturday ~7:30–10 AM.
- **Grok (verified 2026-09-24 on docs.x.ai):**
  - Realtime voice agent: `grok-voice-latest`, PCM16, function calling, ephemeral client secrets. $0.08/min.
  - `web_search` server-side tool, combinable with JSON-schema structured output on Grok 4 models. $5 per 1k calls plus tokens.
  - Agentic search latency is unmeasured; plan for 10–60 s, so demo queries are **pre-cached**.
- **Visa:**
  - Visa Intelligent Commerce docs are gated, so ask at the Visa table.
  - The Visa Acceptance (Cybersource) REST **sandbox** is self-serve and uses test cards. That is the checkout rail.
  - No real order is placed with the seller; "Open at seller" is the real purchase path today.
- **Sellers and 3D:**
  - Amazon PA-API retired May 2026.
  - SerpApi (Google Shopping, Home Depot) gives multi-seller prices and published dimensions, on a tiny free quota, so cache everything.
  - Exact-SKU 3D models exist for maybe 1 in 10 construction parts, hence the tiers.

## 7. Capture plan

- **Target:** a textured feature at height: brick wall with a window, chimney, flashing, gutter run. Avoid plain roofs and grass (COLMAP fails on low texture).
- **Reference:** tape a printed 1 m scale bar (or ArUco board of known size) visibly near the target; also photograph a standard door. Measure two or three features by hand with a real tape and a real level for the accuracy test.
- **Flight:** slow orbit at two heights, gimbal 30–45° down, 4K/30, high overlap; then a closer half-orbit of the feature. 2–3 minutes per pass. Fly two sites in case one fails.
- **File plans** for Sat and Sun mornings by Wed Sep 23.

## 8. 36-hour plan (3–4 people)

| When | Pipeline (A) | Headset (B) | Tools & demo (C) |
|---|---|---|---|
| Fri 8 PM–2 AM | Pipeline script end-to-end on practice footage; scale-bar calibration | Unity shell: passthrough + hands Building Blocks, load a practice textured mesh at 1:1, APK on the 3S; runtime-load + grab a proxy part GLB | Toolbox anchor on hip; tape measure prototype. **Parts server skeleton** (P4): Grok search → one structured candidate; voice token relay |
| Sat 7:30–10 AM | **Capture** (2 sites, references placed, hand measurements taken) | Snapping to mesh; performance pass | Level |
| Sat 10 AM–1 PM | Reconstruct site 1, calibrate, export package | Load real scene | Notebook + source-frame snapshots. P4: asset resolver, sellers cache, sandbox auth from the command line; **pick and cache the two demo parts** (gutter hanger + window AC) |
| Sat 1–3 PM | Site 2; gravity estimate; error report; **measure the bracket in VR, export dims** | Chest-open transition; voice client | **Part tool**: crate, snap, fit colours. Parts server: search jobs, asset tiers, sellers cache |
| Sat 3–6 PM | **At the Hive (open 3–9 PM): laser-cut / print the part from the exported dims** | Polish, perf; "take it home" transition | Spec card, finish swatches; Visa sandbox wired to `/checkout` |
| Sat 6–9 PM | **Checkpoint (~h22):** real scene, tape + level working, error measured, part in hand, **a cached part found by voice, placed and fit-checked**. If not, cut in order: array → carousel → voice → parts layer. Tape + notebook are never cut. | | |
| Sat 9 PM–2 AM | Export sheet; thumbnails | Comfort pass | **Seller carousel + hold-to-pay checkout**, then array. Rain sim / area only if all of that is done |
| Sun 2–8 AM | **Devpost due 8:00 AM:** write-up, 2-min video, accuracy table, Notability tag, Create-X box | Rehearse beats 3, 5, 6 and 7 twice | Freeze by 7 AM |
| Sun 9–11:15 AM | **Expo.** Drone, headset, and the cut part on the table. Laptop shows the accuracy table and the cast view (headset view + the receipt when it lands) | | |

## 9. Risks

| Risk | Mitigation |
|---|---|
| Reconstruction fails on the target | Two sites, textured features, close half-orbit; RealityScan/Postshot as alternate aligners |
| Accuracy is 5–10% | Report it honestly with the cause (scale reference distance, GPS). A stated error beats a hidden one |
| 3S frame rate | Splats tested and dropped (50k=72fps, 200k=60fps, 400k=36fps on-device, §5); mesh triangle count and texture atlas size are the levers that are left, tune down if needed |
| Venue Wi-Fi blocks laptop<->headset | Travel router / laptop hotspot; everything served locally |
| Scope creep on tools | Tape + level + notebook first. Nothing else until those measure the real window correctly |
| Grok search slow or wrong on stage | The demo parts are cached end to end. Live search is a labelled bonus. Validation drops candidates with missing dims or dead URLs |
| Voice fails in a loud hall | Push-to-talk; recognised command shown on the wrist before it runs; STT+chat fallback; crate menu always works |
| Asset looks bad | Badge says what it is. The fit check depends on the published dims, not on the mesh |
| Visa sandbox not approved in time | "Open at seller" and a labelled offline receipt. Never claim an authorization that didn't happen |
| Uplink dies at the expo | Everything the scripted demo needs is cached locally; only the live search is lost |

## 10. Pitch by judge

Keep the build identical; change the first sentence.
- **Immersive / Meta judge:** "Measure what your hands can't reach, with your hands."
- **Aramco / Social Good:** "Nobody climbs a ladder to measure a gutter again." (Falls from height are the leading cause of construction deaths.)
- **Insurance / business judge:** "Adjusters measure storm damage from the ground, with photo evidence attached to every number."
- **Visa judge:** "Measure-to-cart. You try the part on the actual building at true size before you pay, and the quantity comes from the tape, not a guess."
- **SpaceXAI judge:** "Grok is the voice on your hip, the shopper that reads spec sheets, and the eyes that answer 'what is this part?'. Nobody climbs to find out."
- **NSA judge (platinum sponsor; Fri 8:30 PM talk):** "Know a structure's exact dimensions before anyone goes up." Keep it there; no surveillance features.

## 11. Same model, other toolboxes (one slide, one sentence)

Roofing and gutters (demo) · insurance adjusters · solar installers (area, pitch, azimuth, sun tool) · facade and bridge inspection · arborists (canopy height) · location scouting · heritage preservation. Non-measurement actions the model supports: rain/sun/line-of-sight simulation, fit-a-part, route planning, before/after comparison, ask-the-scene, crew safety walkthroughs, dated evidence record.

## 12. Before Friday

1. File GTPD flight plans (Sat + Sun mornings) by **Wed Sep 23**. TRUST certificate in hand. Check B4UFLY at the launch spot.
2. Email hello@hexlabs.org: props-off drone at the expo table OK? any indoor flight? must footage be captured during the event? which Quest models are on the Hardware Desk? Can the Hive cut a part for an Immersive-track project if we check out hardware from the desk?
2a. Friday 8 PM: check out a loaner Quest (second headset + Hive access) before the desk runs out.
2b. Bring government ID **and** student ID (both required at check-in). Register on Match if the team is under 4. Install Cursor and claim Grok credits at the SpaceXAI table Friday; install Notability Pro.
3. DJI Fly: Video Subtitles ON, 4K/30. Fly one practice orbit of any brick building; time the full pipeline; measure one real window to test scale.
4. Get a Unity 6 + Meta XR SDK project with passthrough and hand tracking building to the 3S; import one sample textured `mesh.glb` via glTFast and read the fps.
5. Print the 1 m scale bar and an ArUco board. Pack a real tape measure and a real level for the accuracy test. Pack a travel router.
6. **Accounts only, no code:** xAI API key, SerpApi (free), **Cybersource / Visa Acceptance sandbox (sign up tonight; approval may not be instant)**, Tripo (300 free credits), Sketchfab.
   - Hand-download 3–5 manufacturer/BIMobject/Sketchfab models for likely demo parts: gutter hanger, window AC unit.
   - Confirm glTFast can runtime-load a GLB over HTTP on the 3S.

## 13. Devpost skeleton

- **Tagline:** Fly to it. Stand next to it. Measure it.
- **Inspiration:** ladders, roofs, and a drone with no SDK, so we made the flight the easy part.
- **What it does:** tools list, notebook, export; **parts**: voice-find a real part, hold it at true size, fit-check it on the building, pick a seller, pay with Visa (sandbox).
- **How we built it:** pipeline diagram, calibration, snapping, Unity + Interaction SDK on the 3S.
- **Accuracy:** table of VR vs. real measurements with error.
- **Challenges:** scale, gravity, splats tested and dropped for a textured mesh, 72 fps; keeping an LLM honest about dimensions; a payment flow you can't trigger by accident.
- **What's next:** compare over time, ghost part to STL, adjuster hand-off.

## Sources
HackGT 13 Pre-Event Packet (hexlabs.notion.site, read 2026-09-22: schedule, prizes, one-track rule, sponsor challenges, Hive hours) · hack.gt (tracks; sponsors: Meta, NSA, Aramco, Impiricus, Visa platinum; Citadel, SpaceX/xAI, T-Mobile gold; Goldman Sachs, Notability silver) · hackgt-12/11/x/9 Devpost galleries · dji-sdk/Mobile-SDK-Android-V5 issue #599 · CallMarcus/dji-drone-metadata-embedder issue #205 · github.com/ninjamode/Unity-VR-Gaussian-Splatting · flyright.police.gatech.edu · faa.gov/uas/recreational_flyers · docs.x.ai (models, voice agent, tools, structured outputs; read 2026-09-24) · developer.cybersource.com sandbox · developer.visa.com (VIC, Trusted Agent Protocol) · serpapi.com (Google immersive product, Home Depot) · sketchfab.com/developers · developers.tripo3d.ai
