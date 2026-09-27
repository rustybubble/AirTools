# Backend follow-ups for P4 (app lane, Sat 2026-09-26)

These are issues we found while building the app side. They are not patches: P4 owns `server/`, so each item gives
the exact evidence and a suggested fix. They build on `docs/handoff/p4/` (0001–0008) and
`docs/handoff/p4-b1-b3/` (0009–0011).

## F1. Some Home Depot listings have ratings 1000× too large (skews "best")

The Home Depot listings in `data/cache/search/324f48f5291f1b6f13d2e0f6aecfc95b6c5263f3.json` carry ratings like `4406.0`
and `4396.0` (their review counts, 1904 and 4008, look normal). They are most likely 4.406★ and 4.396★ scaled by
1000.

```text
/value[1]/sellers[0] {'name': 'Home Depot', 'rating': 4396.0, 'reviews': 4008, 'price_usd': 299.0, ...W1W12/319772228}
/value[2]/sellers[0] {'name': 'Home Depot', 'rating': 4406.0, 'reviews': 1904, 'price_usd': 379.0, ...MAW08U1QWT/336424813}
```

The effects:
- `sellers.py:542` prints them as-is, so the seller panel's reason reads "best match, in stock, 4406.0★ (1904 reviews)".
- `sellers.py:512` ranks sellers by `(s.rating or 0) * math.log1p(s.reviews or 0)`, so any listing with a scaled
  rating wins the "best" sort outright.

Suggested fix: clamp at ingestion, `rating = r / 1000 if r > 5 and r <= 5000 else r`, wherever the Home Depot
listings are condensed (`sellers.py` around lines 451 and 481). Also treat any rating over 5 as unknown in `recommend`.
The app shows the reason verbatim, so this is only fixable on the server.

## F2. The B1/B3 contract (from the mock mirror, verified against the patched server)

The mock (`tools/mock_parts_server.py`, 46 tests) was checked against the patched server on 49 request shapes and
matches all of them. Building it showed where the README and the server differ:

1. **422 `detail` is not always an object.** README §5.4 says it is an object for 422, but that is true only for
   the mandate-check failure (`{"error": "a mandate check failed", "checks": [...]}`). Body validation on
   `/checkout`, `/checkout/prepare`, `/commerce/limits` and `/agent/observe` returns FastAPI's list, for example
   `{"detail":[{"type":"value_error","loc":["body"],"msg":"Value error, slope_result needs run_m and fall_mm",...}]}`.
   Suggested fix: document both shapes in `api.md`. The app accepts both.
2. **The slope uncertainty can drop silently to ±2 mm.** `/agent/observe` has no `site` field; the server learns
   the site only from earlier command context in the same `session_id`. A slope report from a session that never
   sent `site` uses a 0° residual (±2 mm instead of the kitchen's ±8 mm), and after a restart the `label`/`measure`
   fallback says "objects". Suggested fix: accept an optional `site` (and `scale`) on `/agent/observe`. The app
   will send them.
3. **Evidence isn't passed through verbatim** (README §5 says it is). Unknown fields are dropped, missing ones
   come back `null`, and `spacing_mm` comes back as a float (`600.0`). Either keep extra fields (`extra="allow"`)
   or change the README to say "normalised".
4. **A limits call that changes nothing returns an intent id that isn't stored.** Examples: a session's first call
   being a voice raise, or setting a field to its current value. The next call returns a different `intent.id`.
   Suggested fix: return the stored intent (or `null`) when nothing changed.
5. **Observe TTS is MP3** (edge-tts) on the server, while `/voice/command` replies may be WAV. That's fine, but
   `api.md` should name `audio_mime` as the only source of truth.
6. **Running without keys and without `OFFLINE=true` returns 500s** for any phrase outside the fast paths
   (`openai.OpenAIError: Missing credentials`). One example: `/agent/command` "Find hangers, under $40, arriving by
   Friday.". Suggested fix: fall back to OFFLINE behaviour when no LLM key is configured, instead of raising.

## F3. Findings from the live B1/B3 run of the app against the patched server

1. **`check_slope` suggests the wall-top edge before the gutter lip.** On the synthetic facade, "check the gutter slope"
   returned `edge_ids [e89, e95, e101, e107, e125]`, and e89 runs (−2.1, 6.2, 0.0) → (2.1, 6.2, 0.0): the top edge at the
   wall face. The gutter's front lip is at y 6.10, z 0.152. The app taped e89 and reported
   `{"kind":"slope_result","target":"gutter","run_m":4.2,"fall_mm":0.0,"low_end":[-2.1,6.2,0.0],...}`. On this level facade
   the verdict is the same, but on a real sloped gutter it would measure the wrong edge. Suggested fix: rank gutter
   candidates by distance from the wall plane (outermost horizontal edge in the gutter band first).
2. **The `fit` check isn't deterministic.** For the same part and the same request, `/checkout/prepare` returned
   `fit: warn "fit not checked against a measurement"` in some runs and `fit: ok "green"` in others. It seems to depend on
   which search last returned the part (server-side state). Suggested fix: take the measurement from the prepare
   request's `evidence` (the tape the headset sends) rather than from the last search.
3. **A 404 from `/checkout/prepare` is ambiguous.** It means both "route missing" (older server) and "unknown part". The
   app treats both as an older server and pays without a proof. With `REQUIRE_HOLD_PROOF=true`, an unknown part fails at
   `/checkout` anyway. Suggested fix: return 422 for an unknown part.
4. **A 500 still commits side effects.** Before the server was restarted with OFFLINE, `"find hangers, under $20,
   arriving by Friday"` returned 500 (no LLM key) but had already stored the $20 limit. The next "under $40" then said
   "You'd need to raise that on the panel". Suggested fix: store the limits only after the command succeeds, or reply
   with the limits even when the search part fails.

## F4. The Grok demo on the real Zabel scan (grok-all + our patches, online, Sat 09-26 14:10)

Run against `scene/zabel-gymnasium` r3 with `context.site = "zabel-gymnasium"`, using the demo script's words:
1. **"what did I miss?"** works as scripted: "You've covered 1 of 8 sides…", with `show_coverage` in exterior mode, the front
   side seen, and the orbit and roof-grid legs.
2. **"survey the roof"** and **"survey the gutters"** both answer "Nothing wrong that I can see in the footage." (no pins,
   about $0.004 each). **"survey the facade"** finds 2 pinned problems: f1 moderate, "overgrown vegetation near base potentially
   causing moisture issues" (0.75), and f2 minor, "staining and discoloration on lower brickwork" (0.70). Neither pin has a
   `part_query`, so "find a fix for pin f1" can only add a note.
3. **"do the whole job" stops at step 1.** The survey finds the vegetation pin, and then part, safety, rules, postcard, packet
   and checkout all skip ("Nothing in the survey needs a part."). A goal doesn't change that: "do the whole job: replace the
   gutters on this building" (run `460ac077833c`) produced the same seven dots, and `run_job(goal)` ignores the goal's part
   when no pin carries a `part_query`. Suggested fix: when the survey yields no fixable pin, fall back to a `find_part` on the
   goal's words ("gutters") or on the selected part, so the rest of the chain has something to check. Otherwise the demo's
   centrepiece beat ends after one dot on real footage.
