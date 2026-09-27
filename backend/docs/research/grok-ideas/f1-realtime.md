# F1: Realtime Quartermaster relay (`WS /voice/realtime`)

Feature agent F1, 2026-09-26. Builds idea #1 from `g2-voice-agents.md`: a server-side relay
between the headset and xAI's realtime voice API. Branch `feat/grok-realtime-quartermaster`.

**Headline.** It works end to end against the live xAI API. A spoken "Find me a K-style gutter
hanger" becomes a `find_part` call, a `search_started` action on the client socket, a short
spoken preamble, and then the search summary spoken verbatim when the job finishes. "How many
hangers do I need for this run?" calls the local `hanger_plan` tool with the headset's tape
reading and answers "Seven hangers, spaced about 560 millimetres apart." First reply audio
arrives about **1.1 s after the pinch is released** and **0.7 s after a tool result**. Three
live sessions cost about **$0.07** in total.

---

## 1. What was built

| Piece | Where | Notes |
|---|---|---|
| Relay | `server/realtime.py` (`bridge`, `_Relay`) | Holds the xAI socket with the server key, so the headset needs no ephemeral token. Mic and reply audio are forwarded as raw binary frames (xAI "binary" transport, no base64) |
| Endpoint | `server/app.py`: `WS /voice/realtime?session_id=` | Same `session_id` and agent session state as `/agent/command` |
| Tools | `realtime_tools()` | `agent.TOOLS` flattened to the realtime shape, plus `hanger_plan` and `btu_for_room` (pure functions, realtime-only so the Groq agent's 8K-TPM prompt doesn't grow). Everything else runs through `agent._run_tool`, and actions go to the client unchanged |
| Session config | `session_config()` | Voice `Settings.XAI_VOICE` (default `rex`), short persona built on `agent.SYSTEM_PROMPT`, `turn_detection: null` (push-to-talk), `reasoning.effort: "none"`, keyterms from `voice.DEFAULT_STT_PROMPT` plus a few more, `replace` for `mm` and `BTU`, PCM16 24 kHz both ways |
| Search announcement | `_announce_job` | When a `find_part` job finishes, the relay sends `job_done` to the client and speaks `jobs.summary(job)` through `force_message` (verbatim, no model call, so no invented part names). It also stores the candidate ids, so "pick the second one" works |
| Test page | `server/static/realtime.html`, served at `GET /realtime` | Hold-to-talk (button or Space), typed turns, context editor, captions, action log, and the release-to-first-audio time |
| Settings | `XAI_VOICE`, `XAI_REALTIME_MODEL` (`grok-voice-latest`), `REALTIME_MAX_S` (600) | |
| Tests | `tests/test_realtime.py` (13 tests) | A fake xAI socket replays event transcripts shaped like the live ones: tool call, tool output, one follow-up `response.create`, audio and transcripts. Also covered: the find_part skip rule, the `force_message` announcement, hanger_plan reading the headset measurement, a cancelled response, bad messages, offline, connect failure, upstream close, tool flattening, and both calculators |
| Docs | `docs/api.md` (§3 flow, §4 `WS /voice/realtime`, `/voice/token` fix, §6, §8), `docs/research/llm-providers.md` §5 | The `/voice/token` fix: `expires_at` is a unix int, and a token can't carry a bound session config |

`/voice/command` is unchanged and remains the fallback.

### Turn logic (one `response.create` in flight)

- `commit`: the relay cancels any reply still in flight, sends `input_audio_buffer.commit`, waits
  for `input_audio_buffer.committed` (about 30 ms; it gives up waiting after 1.5 s), then sends
  `response.create`.
- `response.function_call_arguments.done`: the tool runs as its own task, so audio keeps
  flowing. The action goes to the client and the `function_call_output` goes to xAI.
- `response.done`: once all tool outputs are in and the preamble audio already sent has had
  time to play (estimated from the bytes sent), the relay sends exactly one follow-up
  `response.create`. There are two exceptions:
  - The user has started a new turn: the follow-up is dropped.
  - The response already spoke and its only tool was `find_part`: no follow-up either. The
    tool result is just a job id, and in the live check the extra reply was filler ("you'll
    hear the matches when they're ready"). The job announcement is the real answer.
- The announcement waits until nothing is playing, no reply is in flight and no follow-up is
  pending.

---

## 2. Live check (3 sessions, all under 1 minute, about $0.07)

Script: session scratchpad only (not committed). It started the app with uvicorn on localhost
and connected as a client with `websockets`. It streamed two edge-tts utterances as PCM16
24 kHz, in 40 ms frames at real-time pace: "Find me a K-style gutter hanger." (2.8 s) and "How
many hangers do I need for this run?" (3.0 s). It also sent a context message with a 3.66 m
tape reading. `jobs.start_search` was stubbed to finish after 1.5 s with a cached Amerimax
hidden hanger `part.json`, so there was no SerpApi or LLM spend and no writes to `data/`. The
key was never printed.

| # | Wall | Audio in / out | Est. cost | Result |
|---|---|---|---|---|
| 1 | 46.8 s | 5.8 s / 7.9 s | $0.022 | Turn 1 answered "Got the 3.66-metre gutter run..." and **ignored the audio**. Turn 2 called a tool while saying "Searching the supply shops." (a phrase from my prompt, which I then removed) |
| 2 | 45.1 s | 5.8 s / 6.3 s | $0.020 | Traced all events. `turn_detection: null` echoes `null`; `{"type": null}` echoes `{}`. Turn 1 got **no `input_audio_buffer.committed`**: the `response.create` ran without the user's audio. Turn 2 (no text item before it) committed normally |
| 3 | 31.2 s | 5.8 s / 16.6 s | $0.030 | **Full pass** after the fixes below. `find_part {"query":"K-style gutter hanger"}`, then `search_started` on the client, then the preamble "Starting the search.", then a filler follow-up ("Job's running in the background...", which the relay now skips), then the spoken summary ("One hanger. Fit against the measurement is unknown."). Then `hanger_plan {"spacing_mm":600}` → "Seven hangers, spaced about 560 millimetres apart..." |

Both of the session 1–2 failures share one pattern: in both, the context was sent as a
conversation text item ("tape reading: ...") just before the first audio turn, and that first
audio turn went wrong. The fix was twofold:

1. **No context items.** The relay applies the context to the agent session only, and
   `hanger_plan` reads the tape measurement itself when `run_length_m` is left out.
2. **Commit ack.** `response.create` is sent only after `input_audio_buffer.committed`.

I don't know which of the two xAI actually needed.

### Latency (measured by the relay: from `response.create` sent to first reply audio frame)

| Step | Session 1 | Session 2 | Session 3 |
|---|---|---|---|
| End of speech (commit) → first audio | 0.92, 0.93 s | 0.94, 0.90 s | 1.13, 1.09 s |
| Tool result → first audio | 0.66 s | (not reached) | 0.71, 0.73 s |
| `force_message` announcement → first audio | | | 0.32 s |

The client saw the same numbers to within 40 ms (the localhost hop). The model spoke a preamble
before each tool call, so the user hears something about 1 s after releasing the pinch, even
when a tool is involved. The tool arguments arrived 1.5–3.0 s after the commit.

### Cost

- xAI prices audio at $0.08 per minute. `response.done.usage` came back `{}` every time, so the
  relay logs its own estimate: (input + output audio) × $0.08/min + $0.004 per text item. That
  is an upper bound if xAI bills output only.
- A typical demo turn is about 3 s of speech in and 5–8 s of reply out, so about **$0.013 per
  turn**, or about $0.25 for a 5-minute demo with continuous talking. Idle socket time is not
  audio.
- All three live sessions together cost about **$0.072** by that estimate.

---

## 3. How to run the test page

```
uv run uvicorn server.app:app --host 0.0.0.0 --port 8000
# open http://localhost:8000/realtime in Chrome
```

1. Click **Connect** and allow the mic. The status reads `ready` once xAI has confirmed the
   session, and the context box is sent automatically.
2. Hold **Hold to talk** (or Space), speak, then release. Captions, `action {...}` lines and the
   release-to-first-audio time show up in the log.
3. Or type a turn and press Enter (billed as one text item).

Browser mic capture needs `localhost` or HTTPS. From another device on the LAN, open the page
over HTTPS or use Chrome's "insecure origins treated as secure" flag. The page uses a 24 kHz
`AudioContext`; Firefox refuses to connect a mic running at a different rate, so use Chrome.

The server log shows one line per session, for example:
`realtime f1-live: 31.2s wall, audio in 5.8s out 16.6s, 0 text items, 2 tool calls, first-audio latency [speech 1.13, tool 0.71, announce 0.32, speech 1.09, tool 0.73] s, ~$0.030`

---

## 4. What Unity needs to do

1. Open `ws://<laptop>:8000/voice/realtime?session_id=<same id as other calls>`. Wait for
   `{"type":"ready"}`, then send `{"type":"context","context":{...}}` (§6). Re-send the context
   whenever it changes. Include `frames` before scene questions.
2. **Mic:** while the pinch is held, capture mono audio, resample to 24 kHz (the Quest mic is
   usually 48 kHz, so take every other sample after a low-pass or averaging pair), convert
   float to Int16 little-endian, and send ~40 ms binary frames.
3. **Pinch released:** send `{"type":"commit"}`. **Pinch started** while the quartermaster is
   talking: send `{"type":"cancel"}` and flush local playback.
4. **Playback:** append the incoming binary frames to a ring buffer and play it through a
   streaming `AudioClip` (24 kHz mono, `PCMReaderCallback`), or queue small clips.
5. **Messages:**
   - `action`: the existing `actions[]` handler.
   - `transcript`: wrist-panel captions (replace the text, don't append).
   - `job_done`: keep polling the job as today, but don't TTS its `summary`, because the server
     speaks it.
   - `error` with `fatal: true`: switch to `POST /voice/command` for that turn.
6. No key, token or tool routing on the headset. The old "OpenAI Realtime for Unity" repos
   aren't needed, because this protocol is ours and much smaller.

---

## 5. Limitations and open issues

- **Not tried with a real mic or a headset.** The live check used TTS-generated PCM through a
  Python client. The page's JS passes a syntax check, but I haven't clicked through it in a
  browser with a mic.
- **Root cause unconfirmed.** Sessions 1–2 dropped the first audio turn. Removing context items
  and waiting for the commit ack fixed it in session 3, but I don't know which change did it.
  If it comes back, check the server log for `no commit ack`.
- **The model can't see the context.** It only knows the tape reading through tools
  (`find_part` search fit, `hanger_plan`). `btu_for_room` still needs the room dimensions
  spoken aloud.
- **Overlap is estimated, not tracked.** The relay assumes the client plays audio in real time
  as it arrives. A client that buffers much longer could briefly hear the follow-up overlap the
  preamble.
- **The announcement can talk over the user.** It waits for silence on the reply side only. If
  the user is mid-pinch when a search finishes, it plays anyway. It is `interruptible`, and a
  `cancel` stops it.
- **One conversation per socket.** A reconnect starts a fresh xAI conversation. Agent state
  (selection, candidates, measurement) survives because it's keyed by `session_id`.
- **Undocumented xAI events.** Even with `turn_detection: null`, xAI still sends
  `input_audio_buffer.speech_started/stopped`, and `.completed` user transcripts arrive per
  segment (sometimes several per utterance). The relay ignores the first and forwards the
  second as captions.
- **Cost is an estimate.** Billing couldn't be checked, because `usage` came back empty. See
  G2 §6 for the open billing question.
- **No offline mode.** Offline, the socket closes with a fatal error, and `/voice/command` is
  offline-limited too.
- **Idle sessions stay open.** Nothing times out an idle session before the 10-minute cap.
  Idle time costs nothing (only audio is billed), but it holds one of the tier's concurrent
  sessions (10 on T0).
