# G2: Grok Voice API and Grok agents, and what they can do for AirTool

Research agent G2, 2026-09-26. Scope: the xAI realtime voice API, xAI's agent tools, the "Grok bot" products, what people build with them, and ranked feature ideas for AirTool's server.

Tags: **[V]** verified from xAI docs (docs.x.ai, fetched today as markdown) or by our own live call. **[S]** secondary source: a blog, review, news story or X post. **[I]** my inference.

**Headline.** The realtime voice API is ready for the quartermaster we already designed, and our key works on it: we minted an ephemeral secret, ran a live session, got a function call and heard a spoken answer about 1 s after we sent the tool result. The best use of Grok for the demo is a **server-side realtime relay** that reuses `agent._run_tool`. Unity then only streams PCM and runs the same `actions[]` it already knows. X search works but found little local-contractor signal (one relevant post for Atlanta gutters), so pair it with web search. "Grok Bot" is a Cursor/SuperGrok product with no public API, so it's not something we can build on this weekend.

---

## 1. Live checks (4 calls, about $0.14 total)

| # | Call | Result | Cost |
|---|---|---|---|
| 1 | `server.voice.realtime_token()` → `POST /v1/realtime/client_secrets` `{"expires_after":{"seconds":300}}` | 200 in 1.2 s. Body is `{value, expires_at}`; `value` is 119 chars; `expires_at` is a **unix int**, not a string (fix in `docs/api.md` §`/voice/token`) [V] | free [V: no usage returned] |
| 2 | `GET /v1/tts/voices` | 200. **28 built-in voices**: altair, ara, atlas, aurora, carina, castor, celeste, cosmo, eve, helios, helix, iris, kepler, leo, liora, lumen, luna, lux, naksh, orion, perseus, **rex**, rigel, sal, sirius, ursa, zagan, zenith. All are "multilingual" [V] | free |
| 3 | `wss://api.x.ai/v1/realtime?model=grok-voice-latest` with the ephemeral secret as `Authorization: Bearer`. Session: voice `rex`, `turn_detection: null`, `reasoning.effort: "none"`, one `find_part` function. We sent the text turn "Find me a hanger for this K-style gutter." | `session.updated` came back at 454 ms. The model **spoke a short preamble first** (audio from 810 ms), then `response.function_call_arguments.done` `find_part {"query":"hanger for K-style gutter"}` at 1.85 s. We sent `function_call_output` + `response.create`; first audio came 1.29 s later, transcript: *"Hidden K-style hanger, 5 inch for $2.48. Spike and ferrule also works for $1.10."* 10.7 s of audio over the two turns. `response.done.usage` was `{}` [V] | ≈ $0.014 audio + $0.004 text input |
| 4 | `POST /v1/responses`, `grok-4.20-0309-non-reasoning`, `tools:[{"type":"x_search","from_date":"2025-09-01"}]`, asking who installs gutters around Atlanta per X | 200 in **5.4 s**. 5 `x_search` calls, 21 posts fetched. Found **one** real business (Dr. Roof, @DrRoofAtlantaGA), with one citation. The model rewrote the query itself (keyword and semantic, `mode: Latest`). `usage.cost_in_usd_ticks` = 1,172,912,500 = **$0.117** [V] | $0.117 |

`session.updated` also echoed fields the docs don't mention: `enable_noise_suppression`, `enable_phonetic_spelling`, `keep_context`, `temperature`, `max_response_output_tokens`, `modalities`, `tool_choice` [V observed, undocumented]. Probe script: session scratchpad only, not committed. The key was never printed.

---

## 2. Grok Voice / realtime API facts

| Topic | Fact | Tag |
|---|---|---|
| Endpoint | `wss://api.x.ai/v1/realtime?model=grok-voice-latest`. It's compatible with the OpenAI Realtime API (swap the base URL) | [V] [docs](https://docs.x.ai/developers/model-capabilities/audio/speech-to-speech) |
| Models | `grok-voice-latest` is an alias for `grok-voice-think-fast-2.0` (the default since 2026-08-05; 1.0 is deprecated). The docs say to pin the versioned name in production | [V] release notes |
| Other transports | WebRTC, SIP, LiveKit plugin (`livekit-agents[xai]`), Pipecat `GrokRealtimeLLMService` | [V] docs; [S] [LiveKit](https://docs.livekit.io/agents/models/realtime/plugins/spacexai/), [Pipecat](https://docs.pipecat.ai/api-reference/server/services/s2s/grok) |
| Auth | Server key as Bearer, or an ephemeral secret from `POST /v1/realtime/client_secrets` (`expires_after.seconds`). The docs say the endpoint "does not support `session` or `expires_after.anchor`", so **`docs/research/llm-providers.md` §5's "optional bound session config" is wrong**. Browser: subprotocol `xai-client-secret.<token>` | [V] [ephemeral tokens](https://docs.x.ai/developers/model-capabilities/audio/ephemeral-tokens), our call #1 |
| Session fields | `instructions`, `voice`, `reasoning.effort` (`"high"` default or `"none"`, nothing in between), `tools`, `turn_detection` (`server_vad` with `threshold` 0.1–0.9, default 0.85; `silence_duration_ms`; `prefix_padding_ms`; `idle_timeout_ms`; or `null` for manual commit, i.e. push-to-talk), `audio.input/output.format` (`audio/pcm` at 8–48 kHz, default 24 kHz; `pcmu`; `pcma`; `opus`), `audio.*.transport` `json` (base64) or `binary` (raw WS frames), `audio.input.transcription.keyterms` (≤100 terms, bias toward "fascia", "K-style"...), `language_hint`, `audio.output.speed` 0.7–1.5, `replace` (pronunciation map), `resumption.enabled` | [V] |
| Tools in a voice session | `function` (client-executed; flat shape `{"type":"function","name",...}`, **not** nested under `function` like chat completions), plus server-side `web_search` (with `location`, domain filters), `x_search` (handle filters, dates, image/video understanding), `file_search` (xAI Collections), and **remote `mcp`** (`server_url`, `server_label`, `allowed_tools`; Streaming HTTP/SSE only; xAI calls the MCP server itself) | [V] |
| Function flow | `response.function_call_arguments.done` (name, `call_id`, arguments) → client sends `conversation.item.create {type:function_call_output, call_id, output}` → `response.create`. Parallel calls: answer **all** of them, then send one `response.create`. The docs say to wait until the current audio finishes playing before `response.create`, or the next reply overlaps it | [V] docs, and our call #3 |
| Barge-in | With server VAD, interruption is automatic (`input_audio_buffer.speech_started`). With manual turns, use `response.cancel` and `conversation.item.truncate` | [V] |
| xAI extras | `force_message` (speak a line verbatim, optionally non-interruptible, no model call: good for "Payment authorised" confirmations), per-response `instructions` override on `response.create`, session resumption via `?conversation_id=` | [V] |
| Languages | 20+ listed (en, es, fr, de, hi, bn, ja, zh, ar...), with auto-detect | [V] |
| Voices | 28 built-in (call #2), plus **Custom Voices**: clone from a clip of ≤120 s via `POST /v1/custom-voices`; the returned `voice_id` works in realtime and TTS | [V] |
| Limits | Max session 120 min. Concurrent sessions per team by tier: T0 10, T1 20, T2 50, T3 100, T4 200. Region us-east-1 | [V] rate-limits page; [S] [eesel review](https://www.eesel.ai/blog/grok-voice-think-fast-2-review) |
| Price | **$0.08/min audio** ($4.80/h) + $0.004 per text input item. `function_call_output` and `response.create` are not billed. (1.0 was $0.05/min; the alias moved the price up on Aug 5) | [V] [pricing](https://docs.x.ai/developers/pricing); [S] eesel |
| Latency | Artificial Analysis: 0.70 s average time-to-first-audio, #1 on τ-Voice (56.5%), #2 on their S2S index (82.9%). **Ours: ~0.36 s from `response.created` to first audio item, 1.0–1.3 s after a tool result, with reasoning off** | [S] [Artificial Analysis on X](https://x.com/ArtificialAnlys/status/2082528987272957960); [V] call #3 |
| Standalone speech | TTS `POST /v1/tts` (`voice_id`, speech tags `[pause]`, `[laugh]`, `<whisper>`; $15 per 1M chars; streaming WS). STT `POST /v1/stt` (`grok-voice-transcribe-2.0`, $0.10/h REST, keyterms, diarization) | [V] |
| Samples | The xai-cookbook has web (WS), WebRTC, Twilio, iOS and **Android** (`Android/VoiceApiAndroidExample`) voice agents. Quest runs Android, so the Android sample is the closest reference for P2 | [V] [xai-cookbook tree](https://github.com/xai-org/xai-cookbook) |
| No-code | The Voice Agent Builder (beta, 2026-07-01) has a knowledge base, tools, phone numbers and cloned voices. It's for telephony, and there's no export to code | [S] [x.ai news](https://x.ai/news/grok-voice-agent-builder) |

**Unity/Quest.** No Grok-specific Unity package exists [S, search]. Because the protocol is OpenAI-Realtime-compatible, `mapluisch/OpenAI-Realtime-API-for-Unity` (push-to-talk, function calling) and `danieloquelis/Unity-QuestConversationalAI` (Quest speech-to-speech) should work after a URL swap [S] [repo](https://github.com/mapluisch/OpenAI-Realtime-API-for-Unity), [repo](https://github.com/danieloquelis/Unity-QuestConversationalAI). Rename caveat: `input_audio_transcription.delta` becomes `.updated` (cumulative) [V]. Even with those repos, Unity would still own tool routing, so the server-relay design in §5 keeps P2's work to PCM in and out plus the existing action handler [I].

---

## 3. Grok agents and "bots": facts

| Thing | What it is | Usable by us? | Tag |
|---|---|---|---|
| **Agent Tools API** (`POST /v1/responses`) | Server-side tools: `web_search` ($5/1k calls, `enable_image_search`, domain and location filters), `x_search` ($5/1k **posts fetched**, $10/1k profiles), `code_interpreter`/`code_execution` ($5/1k, sandboxed Python with NumPy/Pandas/SciPy), `collections_search`/`file_search` ($2.50/1k), `attachment_search`, `image_generation`, remote `mcp` (token cost only), `view_image`/`view_x_video`. Only the Responses API has these; chat completions is "legacy" with function calling only. `usage.server_side_tool_usage_details` and `cost_in_usd_ticks` (1e-10 USD) come back on every response | **Yes**, needs a Responses-API call path (our `server/llm.py` is chat-completions) | [V] [tools](https://docs.x.ai/developers/tools/overview), call #4 |
| **Multi-agent** `grok-4.20-multi-agent-0309` | 4 agents (`reasoning.effort` low/medium) or 16 (high/xhigh). Built-in tools and remote MCP only, **no client function tools**, no chat completions, no `max_tokens`. Beta. $1.25/$2.50 per 1M tokens | Yes, for background research jobs; too slow for voice [I] | [V] [multi-agent](https://docs.x.ai/developers/model-capabilities/text/multi-agent) |
| **grok-build-0.1** / Grok Build CLI | Coding model ($1/$2) and agent TUI with headless mode and the Agent Client Protocol | Dev tool only, not a product feature | [V] |
| **Grok Bot** (Aug 2026) | Persistent "AI teammates" on a cloud computer with browser, terminal, connectors and scheduled routines. Comes with paid Cursor plans / SuperGrok. Desktop and mobile apps; routines can fire from Slack/GitHub events | **No public API**; could run a team routine but can't be embedded [I] | [V] [overview](https://docs.x.ai/grok-bot/overview) |
| **@grok on X** | Anyone can tag @grok for a reply. A 169k-post study found it's mostly verification ("is this true?"), 76.8% one-time users, low reach | Not programmable; our own X bot would need X API write access and a bot account | [S] [paper summary](https://pith.science/paper/2605.19720), [arXiv 2602.11286](https://arxiv.org/html/2602.11286v2) |
| Telegram/Discord Grok bots | Third-party only: `jdmsharpe/discord-grok` (chat, image, video, TTS), `xlviitheroman/grokbot` (web search, reminders), `sahibkhokhar/grok-discord-bot` (imitates @grok), `artastakenaka/grok-ai-telegram-bot`. No official xAI Discord bot | Pattern reference only | [S] GitHub |
| Tesla / Optimus | Tesla 2026.26 "Hey Grok" does 116 vehicle commands, multi-intent in one sentence (navigate + climate + steering at once). The Optimus V3 voice demo was reportedly sluggish | Inspiration for the "one sentence, three actions" beat | [S] [Not a Tesla App](https://www.notateslaapp.com/news/4499/teslas-grok-vehicle-commands-in-action), [Motor1](https://www.motor1.com/news/806866/tesla-grok-summer-update-brings/) |
| Grokathon 2026 winners | 1st "Nova" (binaries → C, up to a car ECU); 3rd "ThinkVoice" (brain-sensing hardware + Grok Voice Think Fast 2.0). Commentators said the winners "treated Grok as infrastructure" inside a bigger system | Judges reward Grok embedded in a physical-world system, which is our pitch [I] | [S] [SpaceXAI on X](https://x.com/SpaceXAI/status/2088015995810238827), [basenor](https://www.basenor.com/blogs/news/xai-grokathon-2026-4-standout-moments-that-reveal-groks-range) |

**What people build (current sources):** phone and customer-support agents (xAI's own cookbook Twilio sample, Voximplant, the Voice Agent Builder); in-car multi-intent assistants (Tesla); X-sentiment and monitoring CLIs (`0xNyk/xint`, the cookbook's `sentiment_analysis_on_x`); local-business discovery ("best X near me" is a common Grok query, and local-SEO firms now track "Grok visibility" [S] [Local Falcon](https://www.localfalcon.com/knowledge-base/kb90-how-to-track-your-grok-visibility)); chat bots on Discord/Telegram. I found **no** public Grok voice + VR/XR project [S, search], so a Grok quartermaster in a Quest would be a first as far as I can tell [I].

---

## 4. Ranked ideas

Scores are 1–5. "Fit" means server-side, reuses existing modules, and needs little Unity work.

| Rank | Idea | User value | Demo wow | Fit | Cost / latency | Main risk |
|---|---|---|---|---|---|---|
| **1** | **Realtime Quartermaster relay** `WS /voice/realtime`: server bridges headset ↔ Grok Voice, runs our existing tools server-side, pushes `actions[]` down | 5 | 5 | 5 | $0.08/min; ~1 s to first audio after a tool call [V] | Hall noise (use push-to-talk: `turn_detection:null`); Unity must stream PCM16 |
| **2** | **"Who installs this near me?"** `POST /intel/installers` + agent tool `find_installer`: web_search + x_search, returns cited installers with sentiment | 4 | 4 | 5 | ~$0.12–0.20 and 5–15 s per query [V call #4], cached | Sparse X data for local trades [V]; hallucinated businesses (need citations) |
| **3** | **Site-walk report** `GET /report/{session_id}`: notebook + measurements + BOM + receipt → printable HTML (browser print gives PDF), Grok-written summary, optional narrated audio in the quartermaster voice | 5 | 3 | 5 | one grok-4.3 call ≈ $0.01; TTS ≈ $0.01 | Low; depends on the notebook holding useful entries |
| 4 | **Deterministic sizing tools** (`hanger_plan`, `btu_for_room`) as agent tools: local Python, not `code_execution` | 4 | 3 | 5 | free, <1 ms | None; tiny. **Fold into #1** so voice answers carry exact numbers |
| 5 | **Quartermaster voice identity**: custom-cloned voice (≤120 s clip of a teammate) or `rex`/`sal`, plus `replace` pronunciations, `keyterms`, and a `force_message` greeting and payment confirmation | 3 | 4 | 5 | free to create | Consent for cloned voices; mostly config. **Fold into #1** |
| 6 | **Renovation planner** (multi-agent, 4 agents, web+x search): measurements + placed parts → BOM, cost range, schedule, permit notes, DIY vs pro | 4 | 3 | 3 | tens of seconds [I], maybe $0.10–0.50 [I] | Slow; no function tools on multi-agent [V]; overlaps `server/bom.py` |
| 7 | **Spectator co-pilot**: a second person on a laptop/phone talks to Grok about what the headset sees (shared `session_id`, last frame, per-response `instructions`) | 3 | 4 | 2 | $0.08/min per extra session; T0 allows 10 concurrent [V] | Needs frame casting from Unity; two voices at a noisy table |
| 8 | **AirTool bot on X**: tag it with a gutter photo, it replies with a parts list and scene link | 2 | 3 | 1 | X API write tier + vision call | Bot account, X API access and cost, moderation; @grok itself isn't programmable [S]. **Skip**; at most a "share to X" intent link on the report |

Also worth doing as a small change: switch `voice.speak()`'s first choice to xAI TTS (`POST /v1/tts`, same voice as realtime) so the push-to-talk fallback sounds like the same character [I]. Keep Groq/edge-tts as fallbacks.

---

## 5. The three to build first

### Pick 1: Realtime Quartermaster relay (`server/realtime.py`, `WS /voice/realtime`)

**Why this design.** Our key can already run a realtime session (§1). If the headset connects to xAI directly with `/voice/token`, P2 must re-implement tool routing in C#. With a relay, the server owns the xAI socket (API key stays server-side, no ephemeral token), reuses `agent.TOOLS` and `agent._run_tool`, and sends Unity the same action JSON documented in `docs/api.md` §5. A LAN hop adds roughly 1 ms [I].

**Wire protocol (client ↔ our server).**
- Client → server:
  - binary frames: PCM16 24 kHz mono mic audio, while the pinch is held
  - JSON `{"type":"context", ...}`: the `docs/api.md` §6 context object
  - JSON `{"type":"commit"}`: pinch released
  - JSON `{"type":"cancel"}`: barge-in
- Server → client:
  - binary frames: PCM16 24 kHz reply audio
  - JSON `{"type":"transcript","role","text"}`
  - JSON `{"type":"action","action":{...}}`: identical to `actions[]`
  - JSON `{"type":"error"}`: the client then falls back to `/voice/command`

**Files.**
- `server/realtime.py`:
  - `realtime_tools()`: flattens `agent.TOOLS` into the realtime shape and adds `hanger_plan` (run length, spacing → count and positions) and `btu_for_room`. Both are pure functions, also used by `agent._run_tool`.
  - `session_config()`: `voice` from a new `Settings.XAI_VOICE` (default `rex`), a short quartermaster persona (the voice-model docs say to keep it short), `turn_detection: null`, `reasoning.effort: "none"`, keyterms taken from `voice.DEFAULT_STT_PROMPT`, a `replace` map such as `{"mm": "millimetres"}`, binary audio transport.
  - `async bridge(client_ws, session_id)`: two tasks. The upstream task forwards audio and, on commit, sends `input_audio_buffer.commit` + `response.create`. The downstream task relays audio and transcripts. On `response.function_call_arguments.done` it runs `_run_tool`, sends `function_call_output`, and pushes the action. It sends the single `response.create` only after `response.done`, delayed by the remaining audio time (bytes / 48,000 s) so replies don't overlap (docs §"Avoid Audio Overlap").
  - Payments: `start_checkout` only opens the panel, as today. Server-side confirmations use `force_message`.
- `server/app.py`: `@app.websocket("/voice/realtime")`.
- `server/static/realtime.html`: mic via AudioWorklet → PCM16 → WS, playback, action log. This is the judge-free test rig.
- `tests/test_realtime.py`: monkeypatch `websockets.connect` with a fake that replays scripted xAI events from our call-#3 transcript. Assert that:
  - the tools are flattened correctly
  - a `find_part` call yields a `function_call_output` containing a `job_id`, and a `search_started` action reaches the client
  - exactly one `response.create` goes out after `response.done`
  - OFFLINE or connect failure sends `{"type":"error"}`

  Also unit-test `hanger_plan`.

**Cost.** A 5-minute judge demo is about 2–3 min of audio, roughly $0.25; 30 judges ≈ $8 [I from $0.08/min].

**Risk.** Unity PCM streaming is P2's job. The fallback is the existing `/voice/command`.

### Pick 2: Installer intel (`server/intel.py`, `POST /intel/installers`, tool `find_installer`)

**What it returns.** A Responses-API call on `grok-4.20-0309-non-reasoning` with:
- `web_search` (`location: {country: "US", city: <new Settings.SITE_CITY>}`)
- `x_search` (`from_date` = one year back)
- an instruction to return JSON: `installers[{name, url_or_handle, evidence_url, sentiment: pos|mixed|neg, quote}]` plus a one-sentence `spoken`

**Validation and cost control.**
- Validate with Pydantic; drop any installer without an `evidence_url`. X data is sparse (§1), so web results are the main source and X adds sentiment "colour".
- Cache through `cache.cached("intel", f"{part}|{city}")` so rehearsals are free.
- Run it as a background job like `find_part` (5–15 s).
- The agent tool `find_installer(part)` returns an action `show_installers {items}`. The spoken line: "Two crews on X say Dr. Roof did right by them; one mixed review."

**Code.** Add `llm.responses(model, input, tools)` (plain httpx POST to `/v1/responses`, logs `cost_in_usd_ticks`) rather than bending the chat-completions client.

**Tests.** A respx mock of `/v1/responses`, using a fixture trimmed from call #4's shape (`custom_tool_call` items, `message` with `annotations`). Check parsing, drop-without-evidence, and the cache hit.

**Why judges care.** It's the xAI-only differentiator: nobody else can search X, and it turns "buy the part" into "buy the part, or hire the person locals trust".

### Pick 3: Site-walk report (`server/report.py`, `GET /report/{session_id}`)

**Inputs.**
- the notebook JSON
- `data/boms/*.json` and `data/orders/*.json` for the session
- measurement entries
- any `scene_pin` or frame thumbnails the notebook references

**Summary call.** One `grok-4.3` call writes a 4-line summary and "next steps" (what was measured, what fits, what was bought, what's still needed). It's cached, and OFFLINE falls back to a plain template.

**Rendering.**
- Render with a string-template HTML page: measurement table, parts with honesty labels, receipt label verbatim, photos, and a "share to X" intent link. The browser's print dialog makes the PDF, so no new dependency.
- Optional `?audio=1`: a 20-second narrated recap via xAI TTS in the quartermaster voice, with `[pause]` tags, through `cache.cached("tts", ...)`.

**Demo beat.** The judge takes off the headset, and the laptop (or a QR code on the table) shows *their* report with the numbers they just measured.

**Tests.** A fixture notebook plus an order builds the HTML with the mocked LLM; OFFLINE still renders; a receipt label is present verbatim.

---

## 6. Open questions

- Does `$0.08/min` count input audio, output audio, or wall-clock session time? The docs say "per minute of audio"; `response.done.usage` was empty in our session. Check the console after a demo rehearsal.
- What do the undocumented `enable_noise_suppression` and `keep_context` fields do? Worth one test in the hall.
- Can Responses-API `json_schema` output be used together with server-side tools on grok-4.20? The plan asserts it; not re-verified here.
