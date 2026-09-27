# AirTools demo runbook

*Sat 2026-09-26. For the app on `backend-integration` (tool ring build `b2fd73a`) with the backend
`~/airtools-drone-backend` (FastAPI on :8000). Scripts live in `tools/demo/`. Every script is safe to re-run.*

| Script | What it does |
|---|---|
| `tools/demo/up.sh` | Checks the headset over adb and sets the USB tunnel (`adb reverse` 8000 + 8765). Checks the internet and the backend's `/health`, and starts the backend only if nothing is on :8000 (`--offline` starts it cache-only). Optionally installs an APK (`-i`), launches the app (`--server`, `--site`, `--restart`) and streams logcat to `SpikeData/quest-<time>.log`. `-n` is a dry run. |
| `tools/demo/offline_check.sh` | Tests whether the demo would survive `OFFLINE=true` right now. It starts a throwaway cache-only backend on :8002 over a copy of `data/` and runs the six chips, with and without a tape. It never touches :8000. |
| `tools/demo/parts_check.py` | Sends the app's exact search requests to any backend and prints a hit/miss table. `--pace 25` spaces live searches out (Groq). |
| `tools/demo/fps.py` | Summarises FPS and GPU% from a logcat capture, for example `fps.py SpikeData/quest-….log 'AppState Passthrough -> World'`. |
| `tools/quest_stream.sh` | Mirrors the wearer's view on the Mac for the audience (`-r` also records to `Recordings/`). |
| `tools/presenter/presenter_server.py` | (D7) The presenter page on http://localhost:8766/: reset, hint, skip to a beat, enter/exit; never pays (§3a). `up.sh` starts it. |

> **Keep the USB tunnel up: `nohup tools/demo/tunnel_watch.sh > SpikeData/tunnel_watch.log 2>&1 &`.** A USB reconnect
> (cable, sleep, re-plug) clears the `adb reverse` forwards and the app loses the laptop ("laptop offline"); the
> watcher re-applies 8004 / 8765 / 8766 on every reconnect and restarts the logcat stream. **Don't run the headset on
> GTvisitor unsigned-in**: its captive portal isolates the client (no internet, no laptop over Wi-Fi) and, since it was
> flagged (16:09 Sat), every app request hung even over USB. Turn the headset's Wi-Fi off for a USB demo, sign in to
> the portal, or put the headset and laptop on the iPhone hotspot.

> **Demo backend since Sat 09-26 14:00: `~/airtools-backend-demo` on :8004.** Since 16:10 it is the backend team's
> `integration/vr-next` (the 20 Grok features, removable scene parts, the latest `main`) plus our 11 hand-off patches
> (`docs/handoff/p4-grok/`, branch `app-handoff-vrnext`; they apply cleanly, 1252 backend tests pass on macOS), and since 19:00 the three measure-and-replace patches (`docs/handoff/p4-e2e/`, fast-forwarded from `e2e-measure-replace`; kitchen parts from the backend's `scenes-glb` branch), online with the current `GROK_API_KEY` in its own `.env` (gitignored; copied from
> `~/airtools-drone-backend/.env`). It serves the same `scene/` folder (`SCENE_DIR=~/airtools-drone-backend/scene`)
> and its own `data/`. The backend team's own server on :8000 is left alone. Bring-up against it:
>
> ```bash
> PORT=8004 BACKEND_DIR=$HOME/airtools-backend-demo tools/demo/up.sh --restart --server http://127.0.0.1:8004
> ```
>
> Start the server by hand if it's down (from `~/airtools-backend-demo`):
> `SCENE_DIR=~/airtools-drone-backend/scene LLM_AGENT=xai:grok-4.7 nohup .venv/bin/uvicorn server.app:app --host 0.0.0.0 --port 8004 >> logs/server8004.log 2>&1 &` (since 19:55: Grok is the agent, and any Groq call that fails falls back to Grok, `LLM_FALLBACK`; STT falls back to xAI grok-transcribe).
> Warmed so far: the kitchen's scan labels (`python -m server.warm --labels kitchen`) and the Midea recall check.
> Zabel (`scene/zabel-gymnasium` r3, from hfbox via fedora): "what did I miss?" works as scripted. Say **"survey the
> facade"**, not "survey the roof": on this footage the roof and gutters come back clean, while the facade gives 2 pins. "Do the whole
> job" currently stops after the survey on Zabel (no pin names a part; `docs/handoff/p4-followups.md` F4). Demo it on the
> kitchen, or after P4's fix. The Zabel credit (CC BY 3.0) shows low in view for 8 s when the scan appears, then lives on
> the wrist strip (raise the left wrist) and in ring ▸ Settings' status line (declutter DC5); keep it on the slides too.

---

## 1. Network: pick one

| | **USB tunnel (default, use on campus Wi-Fi)** | **iPhone hotspot** |
|---|---|---|
| Quest ↔ laptop | USB-C cable plus `adb reverse tcp:8000 tcp:8000` (up.sh sets it). The app's default `http://127.0.0.1:8000` just works. | Both devices on the hotspot. Run `up.sh --wifi`, which passes `-e server http://<Mac en0 IP>:8000`. |
| Laptop internet (Groq, SerpApi) | Any network: campus Wi-Fi, or the hotspot. | The hotspot's data. |
| Quest internet | None needed. Candidate photos come from the laptop (`/parts/{id}/image.jpg`), and every warmed part has one. **Open at seller** (browser) won't load. | Yes. |
| Risks | The cable tethers the wearer (use a long USB-C data cable). Replugging or an adb restart drops `adb reverse`, so re-run `up.sh`. | Client isolation or a weak signal. Launch extras don't survive a relaunch from the Quest library. The macOS firewall is off now; if someone turns it on, allow incoming connections for python. |

Recommended setup: the **USB tunnel for the app, with the laptop on whatever has internet**. It is the fewest moving parts.

To make the hotspot URL survive relaunches, pin it on the headset:

```bash
printf 'http://172.20.10.2:8000\nkitchen\n' > /tmp/server.txt
adb push /tmp/server.txt /sdcard/Android/data/com.airtools.quest/files/server.txt
```

Use your real IP from `ipconfig getifaddr en0`. The app reads its server in this order: **launch extras > server.txt > default**. **Delete the file when you go back to the USB tunnel**:

```bash
adb shell rm /sdcard/Android/data/com.airtools.quest/files/server.txt
```

`up.sh` warns whenever the file is present.

## 2. Backend mode: online or `OFFLINE=true`

| Feature | Online (normal) | `OFFLINE=true` (cache only, no internet needed) |
|---|---|---|
| Six Find-parts chips | **With no tape in the notebook:** instant (cached). **After any tape:** every chip press is a **live search** (5–30 s, Groq tokens), because the cache key includes the exact tape value. If it fails (an error, 429, or 20 s), the app retries once **without** the tape and gets the warmed answer (UX W0 search resilience), before the offline catalog. | **Always instant.** All six hit, with or without a tape, and the fit is recomputed against the tape (checked 12/12). |
| Hold to talk (voice) | Works: Groq speech-to-text, agent, and edge-tts reply (needs internet). | 503. The toast says "Voice is offline · use the buttons". |
| "What is this?" | Groq vision: slow, often 429, and no box. The pin lands where you looked. | 503. The toast says "Can't answer now · the assistant is offline". |
| Sellers (spec card ▸ Sellers) | The first call per part may spend two SerpApi calls. | Cached seller lists only (warmed; see §8). |
| Hold-to-pay | An **offline receipt** in both modes: no Cybersource keys, so no charge. | Same. |
| Scenes (kitchen, facades) | Static files. | Same. |
| 3D models | Tier `proxy` (exact-size boxes; Hunyuan3D returns 403). | Same. |

**Decision rule:** start **online** if the laptop's internet is good, because voice is a crowd-pleaser. Switch to **OFFLINE** on the first sign of trouble: a Groq 429, internet dropping, or searches taking more than 10 s. The switch takes about 10 s and costs only voice and "What is this?".

Switching modes restarts the backend, so coordinate with P4, who owns it:

```bash
kill $(lsof -tiTCP:8000 -sTCP:LISTEN)                 # stop the running backend
tools/demo/up.sh --offline --no-launch --no-logcat    # start it cache-only (or without --offline to go back online)
```

`up.sh --offline` refuses to replace a server that is already running; it only warns.

## 3. Bring-up, in order (T-20 min)

1. **Laptop:** on power. `cd ~/AirTools` (or any checkout of this branch).
2. **Headset:** charged above 50 %. Plug in USB-C, put it on, and accept **Allow USB debugging** (tick *Always allow*). Set the boundary: stationary is fine for the kitchen, but roomscale lets the judge walk.
3. `tools/demo/up.sh -n`: the dry run shows what it would change. Then `tools/demo/up.sh`. Add `-i path/AirTools.apk` for a new build, or `--restart` for a clean app state. Every line should read `ok`; fix any `FAIL` and re-run.
4. **Pre-flight parts check (30 s, no network):** run `tools/demo/offline_check.sh`. Expect `12 hit(s), 0 miss(es)`, voice 503 and an `OFFLINE_RECEIPT` checkout. Run it again after any online rehearsal: see the caveat in §8.
5. **Audience view:** `tools/quest_stream.sh` (or `-r` to record).
6. In the headset: **hold the Meta button to recentre** at the spot where the judge will stand. The Enter-world pill is placed from the head (0.45 m ahead, 25° down) on recentre and when you come back to passthrough; windows open 0.45 m ahead, 20° down; Find parts appears on the left after the first tape.
6a. **Scale you can trust (UX W0.1):** enter the world, open ring ▸ **Settings** (the gear; it was "More"). The status must read **"Kitchen · Scale ×1.63 · set for this scan"**: the kitchen now opens at ×1.63, the user's calibration (2026-09-26; the dishwasher's opening reads ≈ 712 × 963 × 725 mm). If it reads "Scale ✓ set (×…)", a tape + **Set scale** saved on this headset overrides the default: tap **Reset** twice ("Tap to reset") to go back to ×1.63 (a demo reset does it too). A new tape + Set scale still overrides it (the keypad takes inches while the units chip reads ft·in, cm on m). Uncalibrated (×1), the kitchen is about ⅔ size (doors read ~10¼″ instead of 15⅜″).
6b. **Models on the headset:** Model view lists every scan, even with the laptop away. Leave the app open for a few minutes with the laptop reachable: it downloads every listed model in the background (six scans, ~110 MB, one at a time, paused during loads). Settings then reads **"Models on the headset: 7 of 7"**. With the laptop away, the models not yet downloaded read "Not downloaded" (dimmed), and a tap asks for the laptop.
7. Headset volume up, for the chime and spoken replies.
8. Watch the log in a second terminal:
   ```bash
   tail -f SpikeData/quest-*.log | grep --line-buffered -E 'AirTools|AndroidRuntime|FATAL'
   ```
   `up.sh` prints the exact path.

For a clean state between judges, run `tools/demo/up.sh --restart`. It clears placed parts, the notebook and the parts on the table. The kitchen reloads from the headset cache in under 1 s.

### 3a. Between judges: DemoMode, reset and the presenter page (UX W1.8, D7)

- **DemoMode** is on by default in a Development build (the expo APK); `up.sh --demo off` turns it off at launch. In DemoMode the guide rail and coach are on, accessibility toggles and coach counts aren't saved (each judge starts clean), **See listing** saves the seller link to the notebook instead of opening the browser, and an offline receipt says "Demo · offline receipt · no payment was made".
- **Reset without restarting** (the app stays loaded; exported notebook files on the headset stay): back to passthrough, no tapes / levels / parts / arrays, empty notebook, checkout and "also needed" cleared, Undo/Redo empty, rail and coach reset, a new server session. It refuses while a payment is being authorized.
  - **Presenter page** (laptop): `up.sh` starts the relay and sets `adb reverse tcp:8766 tcp:8766`; open **http://localhost:8766/**. Manually: `python3 tools/presenter/presenter_server.py` (port 8766) + `adb reverse tcp:8766 tcp:8766`. On the hotspot the app finds it at its server's host, port 8766.
  - **Controllers:** click in **both thumbsticks and hold 2 s**.
  - **Hands:** "stop" with both hands: both hands up at eye height, flat, palms facing away, fingers up, **held still 2 s**. A toast says "keep holding · lower your hands to cancel" after 0.5 s.
- **Presenter page buttons:** **Reset demo** (tap twice, or `R` `R`), **Hint** (`H`: the coach line now), **Enter world** (`E`) / **Exit world** (`X`), and **Skip to beat** (`1`–`7`): door measured · hinges found · hinge in hand · placed on the door · compare prices · take it home · send the report. Pay with Visa and the 1 s hold are shown greyed: **the presenter page never pays** (the relay and the headset both refuse any pay / checkout / hold command). The page shows the headset's status line, the §2.7 beat, mode, tool, judge inputs, the last command and the last error; a command the headset doesn't take within 20 s expires.

## 4. Judge script

The right hand is the **tool hand**; the left palm (turned **up**) is the **menu**. Controllers work too: the left ☰ button opens the ring (it closes after a pick), the right trigger is the pinch, B finishes and A undoes (any tool, even in Move).

<!-- Declutter A (S3) -->
With DemoMode on there is **one heads-up line** (declutter M1): the toasts in beats 1, 4, 5, 8, 11 and 13 are read as flashes on the status line (0.9 m, 17° below the gaze; the Step's text comes back after 2.4–6 s), and a reply (voice, agent, "What is this?") takes the line's place for 5–10 s. The log still says `Toast: …` / `Reply: …`.

| # | Beat | Say / do | What should be seen | If it goes wrong |
|---|---|---|---|---|
| 1 | **Passthrough entry** | "You're in your own room." Poke the **Enter world** pill, 45 cm ahead and 25° below the eyes. | A 0.9 s fade to the scanned **kitchen at 1:1** (only if the scale chip shows ✓, §3 step 6a). Toast: "Move: pinch the floor to walk there". | You get the synthetic facade instead of the kitchen: the server wasn't reachable at start. Open ring ▸ **Settings** ▸ **Reload**. |
| 2 | **Palm-up tool ring** | Turn the left palm up (hint: "Then turn your left palm up for tools"). | A Liquid Glass wheel with Move, Measure, Level, Notebook, Model view and Settings (the gear), plus Undo and Redo capsules at the ends. Pinch-drag with the right hand to spin it; it ticks and snaps. Settling on a mode previews it; pinch the lens to use it ("Pinch to use", D6). For actions, pinch the lens ("Pinch to open"). Home, Ladder, Exit world, the layers (Show edges, Labels, Fall edges) and the settings are in **Settings** (the gear). | The ring flickers: keep the palm in view and within 45° of your gaze. Two quick left pinches always go back to Move. |
| 3 | **Move** | Pinch the floor near the cabinets (a ring on the floor shows where you'll land). | A teleport. Ring ▸ Settings ▸ **Home** returns you to the spawn point. | A pinch at the counter or a wall doesn't move you: "Aim at the floor to walk". |
| 4 | **Measure** | Spin to **Measure**. With the right hand, pinch a door's top corner, then its other corner. | Snap glyphs (corner = yellow square, edge = cyan diamond, plane = violet circle) and an "on axis" guide. The reading is e.g. `1′ 3⅜″` (calibrated; one unit per label, **D2**: Settings' units chip switches every label to `0.39 m`). A chip under the cursor says "Pinch your left hand to save" (controllers: "Press B to save"); one **left** pinch saves it and the toast says "✓ Saved · Width 1′ 3⅜″" ("Door width" when the structure layer knows it's a door). Add 3–4 points for side lengths, corner angles and area (ft², or m² with the chip). | A corner won't snap (a few kitchen objects, P1 §B): tape a different door (o29 and o5 are good). A second nervous left pinch no longer switches to Move. Switching tools mid-tape keeps it ("Tape kept · pick Measure to finish it"). |
| 5 | **Level** | Spin to **Level** and aim at the counter. | `Level ✓` within 0.2° (else `Level 0.3°`) in green (amber up to 2°, red beyond). A pinch saves it: "✓ Saved · Level" (the log keeps "Level x° (y% slope)"). | – |
| 6 | **Notebook** | Spin to **Notebook** and pinch the lens. | "Notebook · 2 measurements · 1 level": each row reads "Width · 1′ 3⅜″ · 0.39 m" (the notebook keeps both units) with the time and the **source photo** thumbnail. **Send report** writes CSV and HTML and uploads them to the laptop ("✓ Sent to the laptop"). | "Saved on the headset · laptop not connected" still counts as a pass. |
| 7 | **Find parts + voice** | The Find parts window appears on the left after the first tape. Tap **Cabinet hinge**. | "Finding cabinet hinges for 1′ 3⅜″…" → "3 matches for your 1′ 3⅜″ tape". The cards show photo, dimensions (inches; mm with the chip on m), price and "exact-size box". **Voice (online only):** press **Hold to talk** and say "find me a cabinet hinge" → "Listening…" → the transcript and reply (reply card under the toast), then the cards. | A chip is slow: the tape made it a live search (§2); after 20 s the app retries without the tape. "Not in the saved parts · connect the laptop" means a cache miss offline: use one of the six chips. Voice 503 means the backend is offline: use the chips. |
| 8 | **Place + fit colours** | **Take** the first hinge. It rides out on the right ray; aim at the taped door ("Aim at the door, pinch to place"). | A true-size part in a **green** outline: "✓ Fits the door" / "Sits flat · nothing in the way". On a scan you also see cyan fitted edges and the box in its fit colour. Pinch to place (clunk + the toast "✓ Fits the door"). The spec card beside the ring shows W × D × H, the tier badge and finish chips. For **red**, push a Shelf bracket into the toe-kick: "✗ Too tall for the toe-kick". | Spinning the ring while holding a part now puts it back in Find parts ("Hinge put back in Find parts"); Take it again. Amber or red on a flat door: move the pointer so the part stays on one plane. |
| 9 | **Array** | With a part placed, tape the run through it (two points along it). Then **Hold to talk (online):** "place one every 40 centimetres". | Parts spaced along the tape, qty = ⌊L/s⌋+1, each fit-checked ("✓ 8 hangers placed · every 1′ 11⅝″ · all fit"). Undo removes the whole array. | The spec card's **Fill the run** button stays **disabled** for every cached server part, because their listings have no `spacing_mm` (§9). Offline, skip this beat. |
| 10 | **Sellers** | Spec card ▸ **Compare prices**. | "Checking sellers…" → "Best pick: … · in stock", rows with a **Best pick** pill, **Cheapest** / **Fastest** toggles, **Pay with Visa** / **See listing**. With a quantity, each row shows the delivered total (packs × price + shipping). Warmed lists: hinge 2, drawer slide 8, gutter hanger 8, window AC 2, knob 2, shelf bracket 1. | **See listing** leaves the app for the browser and needs Quest internet (not on the USB tunnel). Avoid it. |
| 11 | **Hold-to-pay** | **Pay with Visa** → "Review order" (it replaces the seller window; **Back** returns) → "Visa •••• 1111 · test card, no real charge". "Hold Pay for a second to pay $7.59"; hold **Pay** for 1 s ("Keep holding…"). | The ring fills and a chime plays. **"Receipt saved · $7.59"** appears with "1 × hinge · …", the label *"Offline receipt · no payment was authorized"* and "Saved to your notebook". Toast: "Receipt saved · $7.59 · no charge". Letting go early sends nothing. Say it plainly: no real charge. | "Couldn't reach the laptop · nothing was charged / Hold Pay to try again" means the laptop is unreachable: run `up.sh`. |
| 12 | **Tabletop** (do it *before* exiting) | Ring ▸ **Model view**. Optional: on the wheel of model cards under it, point and pinch-drag to spin (or a quick pinch on a side card), then pinch the centre card to open that model (Zabel gym, GT tower, …); then **Walk in**. | The scene becomes a **model floating in front of you at eye height**, about a metre away (1:N, its longest side ≤ 0.9 m; the wrist strip says "Model 1:5") in passthrough, with **no labels on it** (tapes, levels, pins and Grok layers come back when you leave). Right under it, a **wide wheel of model cards** faces you; the model on view is the inked card under the lens, and the line under the lens says its scale and "Pinch to open" for the others. A card you open says "Loading…" until the new model is in, in the same spot. Turn away or walk off and the model comes back in front of you (or pinch **Recentre**). **Walk in** (or the "You are here" pin, or ring ▸ Model view again) enters that model at its recommended spawn; **Exit** goes back to the room. Bought parts stand on the floor to its right, at real size. A tape across the model still reads real size (the reading flashes on the status line). | Find parts, the spec card and Move are hidden in tabletop. That is expected. A card that says **Retry** didn't load: check the laptop, pinch it again. A dimmed "Not downloaded" card needs the laptop. |
| 13 | **Exit + take it home** | **Take it home** on the receipt, or ring ▸ **Settings** ▸ **Exit world**, tapped **twice** ("Tap to leave"). | Fade to passthrough. The **bought parts** sit at true size about 45 cm ahead at table height (0.75 m, assumed, not detected), labelled "Bought · 1 × hinge / $total · seller". Toast: "Your hinge is on the table · real size". | Stand the judge at a real table about 75 cm high. Re-entering the world clears these parts, which is why tabletop comes first. |

## 5. Recovery

| Symptom | Fix |
|---|---|
| **App hung or frozen frame** | `tools/demo/up.sh --restart` force-stops and relaunches the app. State resets; the kitchen reloads from the headset cache. |
| **App crashed** (dropped to Quest Home) | `grep -nE 'AndroidRuntime\|FATAL\|DEBUG' SpikeData/quest-*.log \| tail` to capture the evidence, then `up.sh --restart`. |
| **Server down** (kitchen chips show "Not in the saved parts · connect the laptop"; only Gutter hanger and Window AC come back, as "2 saved parts · laptop not connected". Also "Couldn't reach the laptop · nothing was charged" and a voice error toast) | `tools/demo/up.sh` starts the backend if nothing is on :8000. If it reports "something listens on :8000 but /health doesn't answer", kill that pid and re-run. The server log is `SpikeData/server-*.log` (only when up.sh started it). |
| **Groq 429 / quota** (voice replies take 10–60 s because the SDK retries up to 4× with retry-after; chips after a tape time out at 45 s → offline catalog → "No parts") | Switch the backend to **OFFLINE** (§2). Chips, sellers and checkout keep working; skip voice and "What is this?". |
| **Internet gone** | Same as the 429 fix: OFFLINE. The USB tunnel needs no internet at all. |
| **Headset asleep** (black view, app paused; up.sh reports `Asleep`) | Put it on, or press the power button. For a long stand-by between judges: `adb shell am broadcast -a com.oculus.vrpowermanager.prox_close` keeps it awake off-head. Undo it afterwards with `adb shell am broadcast -a com.oculus.vrpowermanager.automation_disable`. |
| **USB `unauthorized` / `offline`** | Put the headset on and accept *Allow USB debugging*. If that fails: `adb kill-server && adb start-server`, replug, then re-run `up.sh`, since `adb reverse` is lost on every reconnect. |
| **View drifted / pill or windows far away** | Hold the Meta button to recentre, or use ring ▸ **Settings** ▸ **Home**. |
| **Wrong site or server** | Ring ▸ **Settings** ▸ **Next scene** / **Reload** / **Built-in**. Or relaunch: `up.sh --restart --site kitchen [--server URL]`. Check for a stale `server.txt` (§1). |
| **Logcat stopped** (after a replug) | Re-run `up.sh`. It starts a new `SpikeData/quest-<time>.log` if the old streamer died. |

## 6. After the demo

- **Frame rate:** `python3 tools/demo/fps.py SpikeData/quest-<time>.log 'AppState Passthrough -> World'`, with `--app-only` to hide the compositor's lines. The last kitchen session (09-26 02:34, 655 s) held 72 Hz for 99.7 % of seconds, with GPU at 42 % median and 2.6 ms app GPU time.
- **Receipts and notebooks:** `~/airtools-drone-backend/data/orders/` and `data/notebook/`.

## 7. What the app sends (why the cache hits or misses)

- **Chips** send `POST /parts/search {query: "<chip>", max_candidates: 3, measurement?}`. The `measurement` is `{label: "tape #N", value_m, axis}` from the latest two-point tape (`PartsClient.MeasurementContext`). `axis` is `h` for vertical, `w` for horizontal up to 1.5 m, and `length` for longer runs. The six chip queries are `cabinet hinge`, `drawer slide`, `shelf bracket`, `gutter hanger`, `window ac` and `cabinet knob`.
- **Voice and agent** requests carry the same `measurement` in `context`. Online, the agent's LLM rewrites the text into its own query. Offline, only a literal "find (me) a X" is parsed, and anything after "for" is dropped: "find a hanger for this gutter" becomes `hanger`, a miss. The headset has no typed agent input anyway.
- **Server cache** (`server/search.py find_parts`): the key is `{q: normalize(query), m: measurement}`. Online, that exact key must match: the chips match only while no tape exists. OFFLINE falls back to `search_by_query/<normalize(query)>` and recomputes the fit, so **only the query text has to match**.

## 8. What is warmed (09-26 03:38) and how to re-warm

- `search_by_query` and `search` (no tape) exist for all six chips. That covers 17 parts; every one has `part.json` (ready), `model.glb` and `image.jpg`, all proxy tier.
- **Seller lists:** the SerpApi cache is filled for each chip's first candidate, so offline "Sellers" shows the expanded list. Online and offline, every search rewrites `part.json` with the search-time seller (usually one), and the Sellers button re-expands from the cache for free.
- **Re-warm** (online, spends no Groq for cached chips, at most 2 SerpApi calls per new part):
  ```bash
  python3 tools/demo/parts_check.py --tapes none --sellers --pace 25
  ```
  To add a new query, run it once with no tape on the online server, then check it with `offline_check.sh`.
- **Caveat:** online, a live search (any chip pressed after a tape) **overwrites** that chip's `search_by_query` entry with whatever it found. A rehearsal can therefore change, or worsen, the offline answer. Re-run `offline_check.sh` after rehearsals.
- `server/warm.py` isn't needed for the headset. Its default queries (`window air conditioner`, …) don't match the chips, and its TTS and BOM warm-up only serves voice, which is online-only.

## 9. Known gaps (asks to P4 / app)

- **P4, the biggest reliability win:** in `find_parts`, when the exact key misses online, return `search_by_query` refitted (as OFFLINE already does), or at least fall back to it when the live search raises (429/timeout). (The app now retries a failed tape search once without the tape, which hits that warmed entry; a server-side fallback would skip the 20 s wait.) Today any chip pressed after a tape is a live Groq search. Also stop live results from overwriting a warmed `search_by_query`, or keep a pinned demo set.
- **Array beat:** every cached server part has `spacing_mm: null`, so the spec card's Array button is disabled and the array only works by voice (online). P4 could publish `spacing_mm` for repeatable parts (gutter hangers ≈ 600 mm). Alternatively, the app could fall back to a default or keypad spacing when it is null.
- **Summary wording (P4, cosmetic):** with no tape it says "Fit against the measurement is unknown." and pluralises "ac" as "acs" ("Three acs.").
