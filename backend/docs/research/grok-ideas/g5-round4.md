# G5: Grok round 4, rules and money, people, and the glue

Research agent G5, 2026-09-26. Question: which Grok uses would add the most in areas that rounds
1–3 left alone? Those areas are:

- **rules and money:** permits and code checks for the actual job and city, rebates and tax
  credits, and energy savings;
- **persuasion and people:** a contractor–homeowner handoff packet, a two-voice "second opinion",
  and a voice-only mode for low-vision users;
- **the glue:** one command that chains the features we already have into one judge moment, and
  whether `grok-4.20-multi-agent` can orchestrate our own tools;
- anything genuinely new in xAI's September 2026 releases.

Already built or in progress on branches, so not proposed again:
- realtime voice relay (F1);
- reimagine view (F2);
- installer intel (F3);
- site report (F4);
- postcard (F5);
- recall radar (F6);
- placement planner (F7);
- walk-in video (F8);
- install-manual Q&A (F9);
- drone condition survey (F10);
- capture coach;
- finish variants.

Tags: **[V]** verified from xAI docs or by our own live call. **[S]** secondary source: a blog,
review, repo or post. **[I]** our inference. "[V sub]" means we fetched the page and read it
today (in this round the main agent did the fetching, not a sub-agent).

**Bottom line.**
- Six Grok calls cost **$0.422**, out of a $0.50 cap: five billed, and one rejected with a
  400 at no cost. The Files and `/v1/models` checks were free. The calls settled five things.
  1. **A permit lookup for a real job works, but the facts need guard rails.**
     - The job: a 12k BTU mini-split with a new 240 V circuit, City of Atlanta.
     - Grok returned "permit required: mechanical + electrical, City of Atlanta Office of
       Buildings". That took 14 web searches and 30 citations, and every URL it returned was among
       them: **$0.115, 38.5 s**.
     - But it dated Georgia's 2024 IMC/IRC and 2023 NEC to "permits beginning Jan 1, 2026". The DCA
       notice in its own citation list says **effective January 1, 2027**.
     - atlantaga.gov bot-walls both curl and WebFetch (403), so the server can't check the city
       quotes.
  2. **A rebate lookup got one of three programs wrong in a way that matters.**
     - The Georgia Power "Up to $500 Ductless Mini Split Heat Pump" rebate is real, verified on
       the page.
     - The federal 25C credit is correctly `ended`: the IRS page says "through December 31, 2025".
     - It called Georgia's HEAR rebate **`active`, up to $8,000**. The program's own site says
       "**The HEAR program is not accepting new applications.**" The $8,000 isn't on the cited
       page.
     - $0.070, 18 s. So program status and amounts must be checked by code, not the model.
  3. **Multi-agent still can't call our functions, but the answer is now "beta", not "no".**
     - `grok-4.20-multi-agent` with a `function` tool returns
       `400 "Client-side tools for multi-agent models require beta access"`.
     - The same model with a **remote MCP tool works**: 2 MCP calls, **$0.015, 6.7 s** at low
       effort.
     - So "multi-agent over our tools" means exposing them as a public MCP server, or asking xAI
       for the beta.
  4. **The share-link path from G4 works end to end.**
     - A 2-page PDF made with PIL was uploaded to `/v1/files`, and `public-url` put it on
       `files-cdn.x.ai`.
     - An anonymous GET returned 200 `application/pdf` in 0.30 s. A GET after `revoke` gave 404,
       and then we deleted the file. No token cost.
  5. **A two-voice debate needs its claims computed first.**
     - $0.0014, 5.6 s, fluent. But it wrote 8 turns when asked for 6.
     - One voice said a mini-split "still qualifies for the federal tax credit if installed before
       end of 2025". The cited fact says the credit is gone for 2026, and that line passed a
       numbers-only check.
- Free deterministic sources carry most of the weight here, as CPSC did for F6:
  - the **Census geocoder** gives the permitting jurisdiction. A "Decatur, GA 30032" mailing
    address comes back as unincorporated DeKalb County.
  - **ENERGY STAR's open data** has our cached LG KNSAH121B: 20.00 SEER2, 9.00 HSPF2, but only
    certified as a pair with the outdoor KUSAH121B.
  - the DCA code notice, the IRS page, and each program's own status page.
- The three to build first:
  1. a **rules and money check**: permit, code editions, rebates, ENERGY STAR;
  2. a **job packet**: a public PDF with a QR, for the contractor and the homeowner;
  3. **"do the whole job"**: one command chains survey → part → safety → rules → postcard →
     packet → pay panel.

  All three reuse `main`'s `cache.cached`, `jobs.py` and the agent action list, plus the branches'
  `llm.responses()` and F3's honesty rule. The only new dependency is the optional `segno`, for a
  server-side QR.

---

## 1. Live checks (6 calls, $0.422 total)

The probe scripts and raw responses are in the session scratchpad only, not committed. The key was
never printed. Costs are from `usage.cost_in_usd_ticks / 1e10`.

| # | Call | Result | Cost | Time |
|---|---|---|---|---|
| 1 | `POST /v1/responses`, `grok-4.20-multi-agent`, `reasoning.effort: low`, one `{"type":"function","name":"find_part",...}` tool | **400** `{"code":"invalid-argument","error":"Client-side tools for multi-agent models require beta access"}`. The docs still say client-side tools are "not currently supported" [V] | $0 | 0.6 s |
| 2 | Same with `grok-4.20-multi-agent-0309` and `{"type":"mcp","server_url":"https://mcp.deepwiki.com/mcp","server_label":"deepwiki","allowed_tools":["read_wiki_structure"]}`, `max_turns: 3` | 200. One `mcp_call` output item, `mcp_calls: 2` in usage (likely list + call). The answer was 3 FastAPI wiki topics; we didn't check them against DeepWiki. 9.4k in, 2.0k out (1.9k reasoning) [V] | **$0.0150** | 6.7 s |
| 3 | `grok-4.20-0309-non-reasoning`, `web_search` with `allowed_domains` [atlantaga.gov, sos.ga.gov, dca.georgia.gov, library.municode.com, aca-prod.accela.com], strict `json_schema` (`permit_required` enum, `permits[] {name, office, fee_note, url, quote}`, `who_can_pull`, `inspections[]`, `code_basis[]`, `caveats[]`, `summary`), `no_inline_citations`, `max_turns: 4`. Job: 12k BTU mini-split, outdoor pad, new 240 V 20 A circuit, line set through the wall, single-family house, City of Atlanta 30318 | `permit_required: yes`: a mechanical (HVAC) permit and an electrical permit from the Office of Buildings, applied for online. See the details below [V] | **$0.1145** | 38.5 s |
| 4 | Same model, `web_search` with `allowed_domains` [georgiapower.com, gefa.georgia.gov, irs.gov, energystar.gov, rewiringamerica.org], strict schema `incentives[] {name, provider, kind, amount, eligibility, status enum, dates, url, quote}`. Household: Atlanta 30318, Georgia Power, $70k, 3 people, ENERGY STAR mini-split replacing a window AC plus baseboard heat | 3 programs: Georgia Power $500 (`active`), 25C (`ended`), Georgia HEAR/HOMES (`active`, "up to $8,000"). See the check below [V] | **$0.0700** | 18.0 s |
| 5 | Same model, `x_search` only (`from_date` 2026-08-15, `to_date` 2026-09-26), asking for developer posts on multi-agent tools, Files URLs, voice accessibility, permits, rebates and new API releases | 8 posts (§3). Every post id was in the citations. **Citations come back as `x.com/i/status/<id>`, but the model writes `x.com/<handle>/status/<id>`, so match on the id.** 8 X searches, **38 posts fetched**, which is what made it the dearest call. Nothing on permits, rebates or accessibility [V] | **$0.2215** | 17.4 s |
| 6 | Same model, no tools, strict schema `turns[] {speaker: ara\|rex, line, facts[]}` + `open_question`. Input: 7 numbered facts (our cached Midea and DuctlessAire parts, F6's recall, calls #3/#4, a tape reading). Rules: "6 turns, ≤30 words, only the numbered facts, no number not in a cited fact, no verdict" | Fluent, but **8 turns**. The problems are listed below [V] | **$0.0014** | 5.6 s |
| – | `GET /v1/models` | 13 ids: `grok-4.20-0309-{non-reasoning,reasoning}`, `grok-4.20-multi-agent-0309`, `grok-4.3`, `grok-4.5`, `grok-4.6`, `grok-4.7`, `grok-build-0.1`, and 5 Imagine ids. Nothing new since G4 [V] | $0 | <1 s |
| – | `POST /v1/files` (PIL 2-page PDF, 72 KB) → `POST /v1/files/{id}/public-url {expires_after: 3600}` → anonymous GET → `.../public-url/revoke` → GET → `DELETE /v1/files/{id}` | Upload 200 (`purpose: ""`, `expires_at: null`). public-url 200 `{"public_url":"https://files-cdn.x.ai/<token>/file_<uuid>.pdf","expires_at":…}`. GET **200 `application/pdf`, 72,309 bytes, starts `%PDF-`, 0.30 s**. Revoke 200 `{revoked: true}`, then GET **404**. Delete 200 [V] | $0 (no usage) | ~2 s |

**Call 3 in detail (permit, Atlanta).**
- **Right:**
  - Both permits come from one office, applied for online.
  - The job is outside the listed exemptions, which cover "repair or replacement of existing air
    conditioner condensers".
  - Mechanical work needs a Georgia conditioned air contractor, and the new circuit needs an
    electrical contractor.
  - Inspections are mechanical and electrical rough-in plus final.
  - Its `homeowner_allowed` is **`unclear`**, backed by a city form (`ShowDocument?id=9620`) with
    a "Homeowner's Affidavit". It gave the office phone numbers and "Do not rely on this as legal
    advice".
  - The three URLs it returned were all in the call's citations. F3's rule would have kept them.
- **Wrong:**
  1. **Code dates.** It said the 2024 IMC/IRC and the 2023 NEC apply "for permits beginning Jan 1,
     2026", while its own caveat said "after Feb 1, 2026". DCA's notice of 2025-12-09, which Grok
     had in its citations, says: "voted to adopt the following codes with an effective date of
     **January 1, 2027**: 2026 Georgia Amendments to the 2023 National Electrical Code, 2024
     International Residential Code … 2024 International Mechanical Code …" [V: we fetched the
     page]. DCA's "current codes" page already lists the 2024 editions with no dates, which is
     probably what misled it.
  2. **A quote that doesn't back its claim.** The mechanical-permit quote was "…Reinspection
     permits for Plumbing, Electrical, HVAC". That is about reinspections, not about needing a
     permit.
  3. **Fees** ("Base fee $150, minimum $175") come from a page we can't read. atlantaga.gov
     returns 403 to curl and to WebFetch, so neither the server nor we can check these quotes.
- **Takeaway:** the verdict is useful. Code editions must come from a checked-in DCA table, and
  city quotes are shown as "model-extracted".

**Call 4 in detail (rebates).** We fetched each cited page and compared quotes as letters and
digits, NFKC-normalised, as F9 does.

| Program | Grok said | Check | Verdict |
|---|---|---|---|
| Georgia Power, Home Energy Improvement Program | Up to $500, `active`, instant rebate through an affiliated installer, "Effective August 1, 2026–December 31, 2028" | "Up to $500 Ductless Mini Split Heat Pump - Combined" and "Instant Rebate. Applied Through Affiliated Installer." are **on the page**. So is "within 60 days of the date on your paid-in-full invoice". **The Aug 2026–Dec 2028 dates are not** | Amount right; dates unbacked |
| Federal 25C | `ended`, "through December 31, 2025" | The quote is **on** irs.gov | Right |
| Georgia Home Energy Rebates (HEAR/HOMES), GEFA | `active`, "up to $8,000 for heat pump space heating/cooling", "likely qualifies" at $70k | The cited April 2026 GEFA press release exists and its quotes match. But it doesn't say $8,000. The program site `energyrebates.georgia.gov` (not in our allowlist; our mistake) has the banner "**The HEAR program is not accepting new applications.**" and "up to $16,000 … depending on household income and/or expected energy savings" [V: fetched with curl today] | **Wrong status**; the amount and eligibility came from the model |

**Call 6 in detail (debate).** A local check that every number in a line appears in one of its
cited facts flagged 2 of the 8 lines: "8k BTU is plenty", and the 25C line with "2025". The real
failures were about meaning:
- rex: "A new mini-split **still qualifies for the federal tax credit if installed before end of
  2025**". That is true of a date in the past and useless in September 2026.
- ara: "The window unit needs no permits" cites f4, which only covers the mini-split.
- ara argues for the recalled Midea and never mentions the recall. rex brings it up.
- The 27k BTU **3-zone** unit was pitched for one 15 m² bedroom. Nobody said it's oversized.

**Free cross-checks (not xAI).**
- **Jurisdiction.** Census geocoder
  (`geocoding.geo.census.gov/geocoder/geographies/onelineaddress`, `layers=all`), no key:
  - "225 North Ave NW, Atlanta, GA 30332" → Fulton County, **Atlanta city**;
  - "4380 Memorial Dr, Decatur, GA 30032" → DeKalb County, **no incorporated place**, so
    unincorporated DeKalb and the county's permit office, not the City of Decatur's.

  Mailing city ≠ jurisdiction; that is the trap to avoid [V].
- **Efficiency and rebate eligibility.** ENERGY STAR open data (Socrata, no key):
  - The dataset is "ENERGY STAR Certified Mini-Split Heat Pumps", `akti-mt5s`, updated
    2026-09-25. There is also `tzuf-wwcc`, "Tax Credit Eligible … Air-Source Heat Pumps".
  - `?$q=KNSAH121B` returns **LG KUSAH121B (outdoor) + KNSAH121B (indoor): 20.00 SEER2,
    9.00 HSPF2, 11,000 BTU/h cooling**.
  - Our cached part `lg-knsah121b-04da2e` is the **indoor unit only** ($498). A BOM that lacks the
    outdoor unit isn't a certified system, and a code rule can say so.
  - `?$q=DA27` returns DuctlessAire DA27-4Z-O rows (21–23 SEER2) [V].
- **Rewiring America Incentives API.**
  - `GET /api/v1/calculator` needs `owner_status`, `household_income` and `household_size`; `zip`,
    `address`, `utility`, `items[]`, `authority_types[]` and `language` (en/es) are optional.
  - It returns `incentives[]` with the amount, the payment method, `start_date`/`end_date` and a
    URL, plus `coverage` and AMI flags.
  - Free, but it needs a key, which isn't in `.env`, so we didn't test it and don't know its
    Georgia coverage [V sub] ([docs](https://docs.rewiringamerica.org/api)).
- **Electricity price.** EIA's Georgia profile gives an 11.40 ¢/kWh average retail price for 2024,
  all sectors, not residential. A residential figure needs the EIA API (free key) [V sub].

---

## 2. API facts new since round 3

| Fact | Detail | Date | Tag |
|---|---|---|---|
| **Multi-agent client tools are a gated beta** | `400 "Client-side tools for multi-agent models require beta access"`. The docs page still says "Client-side tools (function calling) and custom tools are not currently supported by the multi-agent model variant". So there is a beta; ask xAI for it | 2026-09-26 | [V] call #1, [multi-agent](https://docs.x.ai/developers/model-capabilities/text/multi-agent) |
| **Multi-agent + remote MCP works** | 4 agents (low effort), `type: mcp` with `allowed_tools`, 6.7 s, $0.015. Only the leader's `mcp_call` items come back. Streamable HTTP or SSE only; no `require_approval` | 2026-09-26 | [V] call #2, [remote MCP](https://docs.x.ai/developers/tools/remote-mcp) |
| Multi-agent model id | Our key lists `grok-4.20-multi-agent-0309`. The alias `grok-4.20-multi-agent` is accepted too (call #1 failed on the tool, not the model). $1.25 / $0.20 / $2.50 per 1M, all agents' tokens billed | 2026-09-26 | [V] `/v1/models`; pricing [V sub] [models](https://docs.x.ai/developers/models), [S] [@grok, 2026-08-27](https://x.com/grok/status/2092933926935228653) |
| **Files public URLs, verified end to end** | Upload reply `purpose: ""`, `expires_at: null`. `public-url` returns `{public_url, expires_at}` on `files-cdn.x.ai`. Anonymous GET 200 with the right content type in 0.3 s. **Revoke takes effect at once (404)**. Delete works. File endpoints return no `usage`, and the storage price still isn't documented | 2026-09-26 | [V] Files probe; G4 had [V sub] only |
| Search results don't bound the answer | Call #3 cited DCA's "effective January 1, 2027" page and still wrote 2026. Call #4 cited a press release and reported a program as `active` that its own site says is closed. Our allowlist left out `energyrebates.georgia.gov`. Pair with G4's "`allowed_domains` doesn't bound distance" | 2026-09-26 | [V] calls #3/#4 |
| Quote fidelity again | The Georgia Power "quote" joins two footnotes; the Atlanta "quote" doesn't support its claim. As F3 found (4 of 9 verbatim), treat `quote` as a pointer, not evidence, unless the server finds it on the page | 2026-09-26 | [V] |
| Government sites bot-wall | atlantaga.gov answers 403 to curl and to WebFetch. The server can check the **URL** (it's in the citations) but not the **quote**. dca.georgia.gov, irs.gov, georgiapower.com and gefa.georgia.gov served fine | 2026-09-26 | [V] |
| X citation URL form | x_search citations are `https://x.com/i/status/<id>`, while the model's own URLs use the handle. F3's "URL must be in the citations" rule must compare **status ids** for X | 2026-09-26 | [V] call #5 |
| x_search cost is per post | 38 posts fetched → $0.22, 3× a 14-search web call. Bound it with `max_turns` and a narrow ask | 2026-09-26 | [V] call #5 |
| **Transcribe 2.0** | `grok-voice-transcribe-2.0`: **speaker diarization**, word timestamps, 100 keyterms, 24 languages, up to 8 channels, 500 MB files, streaming at `wss://api.x.ai/v1/stt` with "Smart Turn" end-of-turn detection. $0.10/h REST, $0.20/h streaming. **The STT doc says 2.0 is the default when `model` is omitted; the release notes say the default stays 1.0. Pin `model`** | released 2026-09-18 | [V sub] [STT](https://docs.x.ai/developers/model-capabilities/audio/speech-to-text), [release notes](https://docs.x.ai/developers/release-notes), [S] [@SpaceXAI](https://x.com/SpaceXAI/status/2101005253395308817) |
| Grok 4.7 usage notes | "We highly recommend setting a `prompt_cache_key`" on Responses; pass reasoning items back unchanged; use context compaction for long loops. Reasoning effort defaults to **high**. "Grok 4.7 Fast" exists only in Cursor and Grok Build, not the API | 2026-09-21 | [V sub] [grok-4.7](https://docs.x.ai/developers/grok-4-7), [S] [@SpaceXAI](https://x.com/SpaceXAI/status/2102069822288720022) |
| Realtime voice knobs for accessibility | `server_vad` with `silence_duration_ms` and `idle_timeout_ms`, `language_hint` (BCP-47), 20+ languages, barge-in; in-session tools: `function`, `web_search`, `x_search`, `file_search` and `mcp`. Mostly known from G2. What's new is the use: a longer silence window for speakers who pause | current | [V sub] [voice agent](https://docs.x.ai/developers/model-capabilities/audio/voice-agent) |
| September API releases, in full | Grok 4.7 (09-21), Transcribe 2.0 (09-18), and the notice that `grok-imagine-image-quality` retires on 2026-11-02. August added Imagine `quality: auto`, **up to 5 source images per edit** (was 3), and 21:9 and 5:2 aspect ratios. Nothing else for the API | Aug–Sep 2026 | [V sub] release notes |
| Grok Bot | Still no developer API. xAI's design post ([2026-09-04](https://x.ai/news/designing-grok-bot)) and community fleet formats exist (§3) | Sep 2026 | [S] |

---

## 3. What people are building (Aug–Sep 2026)

**Coverage caveat.**
- The shared WebSearch quota was **already used up when this round started** ("200 of 200").
- Reddit's JSON endpoints return **403** from this machine, and the `gh` CLI isn't installed.
- So we used the HN Algolia API, the unauthenticated GitHub REST search, WebFetch, and one Grok
  `x_search` call (#5).
- The social signal on (a) and (b) is thin: X had nothing on permits, rebates or accessibility.

**Grok as an orchestrator**
- **Multi-agent fleets live in Grok Bot, not the API.** Posts show PM + Dev + Designer bot crews
  shipping from a Linear board
  ([@NeoGrinderAI, 2026-09-22](https://x.com/NeoGrinderAI/status/2102490870184636519)), and an
  Obsidian specialist bot that other bots write notes into
  ([@weeb3dev, 2026-09-26](https://x.com/weeb3dev/status/2103663899677696207)) [S].
  [GBDL](https://github.com/jcpsimmons/gbdl) (Show HN, 2026-09-09) proposes one Markdown/YAML file
  for a "dispatcher + specialists" Grok Bot fleet [V sub].
- **MCP is the bridge people use.**
  - [grok-critic-mcp](https://github.com/Bondartsov/grok-critic-mcp) (2026-09-18) wraps the
    16-agent setup behind MCP for code review.
  - [ContextX](https://github.com/KayanoLiam/ContextX) (175★) serves multi-agent "deep search"
    over MCP.
  - A "grok-plugin" MCP server lets Claude Code hand work to Grok
    ([@socialwithaayan, 2026-08-20](https://x.com/socialwithaayan/status/2090381902482129397))
    [S].
  - This matches call #2: to put multi-agent over our tools, expose them as MCP.
- **Debate as an architecture.**
  [allen2c/openai-420](https://github.com/allen2c/openai-420) describes itself as "Grok 4.20's
  inference-time multi-agent chatroom, rebuilt from scratch — specialist agents debate via tool
  call" [S, repo description only; no README].
- **Human approval stays in the loop.** A content bot drafts with Grok and publishes only after a
  review queue
  ([@Noderunner_Hex, 2026-09-25](https://x.com/Noderunner_Hex/status/2103452502054064408)) [S].
  That is the same stance as our "pay panel opens, never pays".
- HN: Grok 4.7 (608 points, [2026-09-21](https://news.ycombinator.com/item?id=49788838)),
  Transcribe 2.0 (28 points, [2026-09-18](https://news.ycombinator.com/item?id=49757859)), and a
  Grok-native coding CLI ([grok-cli](https://github.com/abhayKashyap03/grok-cli), 2026-09-09)
  [V sub].

**Permits and rules (incumbents; no Grok builds found)**
- **PermitFlow** sells "Construction's AI Workforce": agents for permit intake, research,
  submission and closeout across "thousands of jurisdictions", for contractors and builders. It
  claims 2.5× faster approvals ([permitflow.com](https://www.permitflow.com/)) [V sub, vendor
  claims].
- **Symbium** offers "instant permitting" with automated building, zoning and energy code checks.
  Its jurisdictions are in California, Colorado and Maryland, it's aimed at solar installers, and
  it doesn't mention HVAC ([symbium.com](https://symbium.com/)) [V sub].
- The gap for us: neither is homeowner-facing, and neither starts from a scan of *this* house with
  the part already chosen. Our job is research with sources for one job, not a permit
  application.

**Rebates and energy**
- The deterministic sources are free and current:
  - ENERGY STAR open data (updated 2026-09-25);
  - the Rewiring America API (free key; English and Spanish);
  - IRS pages;
  - program status pages.

  Call #4 shows the model is the weak link on **status** and **amounts** [V].
- The federal 25C/25D credits ended for work after 2025-12-31 (IRS) [V]. Any "tax credit!" pitch
  in 2026 is wrong by default, and the tool should say so first.

**Accessibility**
- [grok-voice-accessibility](https://github.com/Errrmind/grok-voice-accessibility) (2026-09-23)
  collects reports from dyslexic and speech-different users "being cut off mid-thought" by the
  Grok voice agent [V sub]. For a voice-only mode that points to push-to-talk (F1 already uses
  `turn_detection: null`) or a long `silence_duration_ms`, not default VAD.
- [OutLoud](https://github.com/rhishi99/OutLoud) (2026-09-14) adds auto-narration to coding
  agents [S]. No one we found narrates a spatial or AR scene for low-vision users with Grok.

**Gaps.**
- No Grok permit, code or rebate project turned up on X, HN or GitHub.
- No debate-style consumer app.
- No public benchmark of LLM accuracy on permit rules. Calls #3/#4 are our only data.

---

## 4. Ideas, ranked

Scores and columns as in G3/G4: **Wow** × **Feas** (a few hours, one person, server-side, Unity
limited to a new action) × **Fit** (reuses our endpoints and modules). Costs are per use.

| Rank | Idea | Wow | Feas | Fit | Score | Cost / latency | Main risk |
|---|---|---|---|---|---|---|---|
| **1** | **Rules and money check**: jurisdiction (Census) → permit research (Grok, gov domains) + code editions (DCA table) + rebates (Grok) checked against program status pages, ENERGY STAR and the IRS rule | 5 | 4 | 5 | 100 | $0.115 + $0.07, 18–38 s live [V]; free sources < 1 s; cached per jurisdiction and job | Wrong status or dates (calls #3/#4); bot-walled city pages; sounding like legal advice |
| **2** | **Job packet**: a multi-page PDF (homeowner page, contractor page, sources) → xAI Files public URL → QR, revocable | 4 | 5 | 4 | 80 | ~$0.003 text; Files $0 in tokens [V]; ~3 s | A public link with an address on it; storage price unknown |
| **3** | **"Do the whole job"**: one command runs survey → part → safety → rules → postcard → packet → pay panel, streaming each step's action | 5 | 3 | 5 | 75 | ~$0.35 and ~40 s live (parallel steps) [I from the parts]; ~$0 and a few s warmed | Depends on 5 branches being merged; a failure mid-chain |
| 4 | **Bill-impact card**: yearly kWh and $ before and after, from `btu_for_room`, ENERGY STAR SEER2/HSPF2, climate hours and the EIA price, all local; Grok writes one line | 3 | 5 | 4 | 60 | $0.001 [I]; < 1 s | Climate constants need a real source; homeowners hear "savings" as a promise |
| 5 | **Second opinion, two voices** (ara vs rex over TTS): claims come from local "stance cards", and Grok only phrases them | 4 | 4 | 3 | 48 | $0.0014 text [V] + TTS ≈ $0.006 for 400 characters [I] | Meaning drifts (call #6); may look like a gimmick |
| 6 | **Voice-only mode** for low-vision users: every action carries a `say` line; clock-face directions from the head pose; push-to-talk or long VAD | 4 | 3 | 4 | 48 | Voice $0.08/min (F1); the rest is local | Unity work for spatial audio cues; testing with real users |
| 7 | **Paper-quote checker**: photo of a contractor's quote → Grok vision lines → compared with our BOM prices, permit fees and the rebate | 4 | 4 | 3 | 48 | ~$0.005 vision [I from G4 token rates] | Handwriting; judging someone else's price is sensitive |
| 8 | **Diarized walk-through notes**: record the contractor–homeowner walk-through → Transcribe 2.0 with `diarize` → decisions and to-dos into the notebook and packet | 3 | 4 | 3 | 36 | $0.10/h audio + ~$0.003 extraction [V sub prices] | Consent to record; speaker labels are integers, not names |
| 9 | **Inspection-ready checklist**: from the permit result and F9's manual: disconnect in sight, condensate route, clearances | 3 | 3 | 3 | 27 | ~$0.01 [I] | ICC code text is paywalled; invented clauses |
| 10 | **Spanish homeowner mode**: `language_hint: es`, Rewiring America `language: es`, packet in Spanish | 3 | 4 | 2 | 24 | Same as English | Needs a native reviewer for the demo copy |
| 11 | **Multi-agent over our tools via remote MCP**: `grok-4.20-multi-agent` plans a whole renovation calling `find_parts`, `check_safety` and `rules` through a public MCP endpoint | 4 | 2 | 3 | 24 | $0.015 per 2 MCP calls at low effort [V]; realistic runs are tens of cents [I] | Needs a public URL (tunnel) and an MCP server; client tools are beta-gated |

**4. Bill-impact card.**
- Cooling kWh/yr ≈ `load_btu × cooling_hours / (SEER2 × 1000)`, and the same with HSPF2 for
  heating.
  - The load comes from `btu_for_room` (F1/F7).
  - SEER2 and HSPF2 come from ENERGY STAR for the chosen model.
  - A window AC's CEER comes from its spec.
  - Electric baseboard heat is COP 1.
- Hours and the ¢/kWh are one checked-in table per climate zone, with sources. The residential
  price comes from EIA; the constants still need a real reference [I].
- Grok writes one sentence and may not add numbers (the same number check as below).
- Ranked 4th because it can ride inside pick 1 as a third section once the constants are sourced.

**5. Second opinion.**
- Call #6 is the argument against letting the model argue. Build **stance cards** locally from
  facts we own: price gap, recall, permit needed or not, rebate, oversize ratio
  (`unit_btu / btu_for_room`), and reviews count.
- Each card is `{side, claim_template, facts}`. Grok only rewrites each card as ≤ 30 spoken words
  and must keep its numbers.
- Two TTS voices (`ara`, `rex`) play it back. The closing line is always the open question, never
  a verdict.
- Good for the demo; weak as a product. Build it after pick 1, whose facts feed it.

**6. Voice-only mode.**
- Server side:
  - `context.mode: "voice_only"` makes every action carry `say` (for example, "Sellers: cheapest
    is Home Depot, $498, arrives Oct 6").
  - A `where_is(target)` tool turns a pin or part position and the head pose into "2 o'clock,
    3 metres, at chest height" (local maths).
  - `ask_scene` (G1/main) describes the view on request.
- The session uses push-to-talk or `silence_duration_ms` ≥ 1500, because of the cut-off reports
  in §3.
- Most of the work is Unity audio.

**7. Paper-quote checker.**
- One vision call with a strict schema: `lines[] {item, qty, unit_price, total}`, plus permit and
  labour lines.
- Then local maths against our BOM, the permit fee from pick 1 and the rebate.
- The output is "questions to ask", never "you're being overcharged".

**8. Diarized notes.**
- Transcribe 2.0 labels speakers 0/1. One cheap Grok call pulls out `{decisions[], todos[],
  measurements_said[]}` into the notebook.
- The recording needs a spoken consent line at the start.

**9. Inspection checklist.**
- Only items that cite a Georgia amendment PDF (free on DCA) or the manufacturer's manual (F9's
  honesty rules). ICC base text isn't freely fetchable.
- Low wow per hour of work.

**10. Spanish.** Cheap to switch on, but the demo gains little unless a judge speaks Spanish.

**11. Multi-agent over MCP.**
- Call #2 proves the mechanism. The cost is:
  - an MCP server, either ~60 lines of streamable-HTTP JSON-RPC or a new `mcp` dependency;
  - a public tunnel from the hall;
  - an auth header, since `authorization` is supported.
- If xAI grants the client-tools beta, the tunnel goes away and our own function tools work
  directly.
- Pick 3 gets the same judge moment deterministically, so this waits.

---

## 5. The three to build first

### Pick 1: Rules and money check (`server/rules.py`, `POST /rules/check`, tool `check_rules`)

**Why.**
- "Do I need a permit?" and "Is there a rebate?" are what a homeowner asks right after "what does
  it cost?". Nothing in the stack answers either today.
- Calls #3/#4 show Grok finds the right offices, programs and pages in 18–38 s for $0.07–0.12. They
  also show which fields it gets wrong: code dates, program status, amounts.
- As with F6, free deterministic sources set the verdicts and Grok supplies the research and
  links:
  - Census for jurisdiction;
  - a DCA table for code editions;
  - ENERGY STAR for eligibility;
  - status probes for program state;
  - the IRS rule for 25C.
- The demo line: "Yes, Atlanta wants a mechanical and an electrical permit, pulled by a licensed
  conditioned-air contractor. Georgia Power takes $500 off at install. The federal tax credit
  ended last year, and the state rebate isn't taking applications."

**Module.** `server/rules.py`:
- `async def jurisdiction(address: str | None, location: str | None) -> Juris`
  - Census `onelineaddress` (`benchmark=Public_AR_Current`, `vintage=Current_Current`,
    `layers=all`) returns `{state, county, place | None}`.
  - `place is None` → the county is the authority.
  - With no address, parse "City, ST" (F3's `DEFAULT_LOCATION`) and mark it
    `confidence: "city_only"`.
  - Cached forever by the normalised address.
- `JOBS: dict[str, JobSpec]`, a small fixed map from the part category or keywords to a job:
  `minisplit_install`, `window_ac`, `water_heater_swap`, `gutter_replace`, `roof_repair`,
  `electrical_circuit`.
  - Each `JobSpec` holds the scope sentence sent to Grok (call #3's wording), `trades`
    (mechanical/electrical/plumbing) and the ENERGY STAR dataset id if any.
  - Unknown jobs get `permit_required: unknown` with no call.
- `GOV_DOMAINS: dict[(state, authority), list[str]]`, checked in, up to 5 per call as in calls
  #3/#4:
  - `("GA", "Atlanta city")`: atlantaga.gov, library.municode.com, sos.ga.gov, dca.georgia.gov;
  - `("GA", "DeKalb County")`: dekalbcountyga.gov, library.municode.com, sos.ga.gov,
    dca.georgia.gov.
  - Fallback: `<county>.gov` search with no allowlist and a `label: "unverified jurisdiction
    sources"`.
- `CODE_TABLE`, a checked-in JSON per state:
  - `{code, edition, effective, source_url}`. For GA: the prior editions, then the 2024 IRC/IMC
    and the 2023 NEC with 2026 GA amendments, effective **2027-01-01**
    ([DCA](https://dca.georgia.gov/announcement/2025-12-09/new-codes-jan-2027)).
  - The model's `code_basis` is dropped. The answer quotes the table and the date.
- `async def permit(job, juris) -> PermitResult`
  - Call #3's request and schema with `grok-4.20-0309-non-reasoning`, `max_turns: 4` and
    `no_inline_citations`.
  - Rules in code:
    - F3's rule: every URL must be in the call's citations, or the item is dropped.
    - `quote_status` is `verified` when a plain httpx GET of the page contains the quote (F9's
      normaliser), `unreadable` on 403 or timeout, else `not_found`.
    - `homeowner_allowed: unclear` stays unclear; it is never upgraded.
    - Fees are shown only with `quote_status: verified`.
    - The office phone number comes from `GOV_DOMAINS` metadata, not the model.
  - Cached as `cache.cached("rules_permit", {juris, job}, ttl_s=7 days)`.
- `async def money(part, juris, household) -> MoneyResult`
  1. `energy_star(model_no)`: Socrata `akti-mt5s` (`$q=<model>`) gives `{certified, seer2, hspf2,
     cooling_btu, pair: {indoor, outdoor}}`. If the BOM lacks the pair's other unit, flag it:
     "Rebates need the certified pair: add KUSAH121B." Cached for 1 day.
  2. `grok_incentives(...)`: call #4's request, with the allowlist from a `PROGRAMS` table that
     **must include each program's own status page** (the fix for our HEAR miss).
  3. `STATUS_PROBES`: `{program_key: (url, closed_regex, open_regex)}`, fetched locally. For
     example HEAR is `energyrebates.georgia.gov` with `not accepting new applications`. A probe
     hit overrides the model's `status`; an unreadable probe makes the status `unknown`.
  4. Fixed federal rules: `25C`/`25D` are `ended` for installs after 2025-12-31 (IRS), whatever
     the model says.
  5. An amount is kept only if its digits appear in a `verified` quote; otherwise
     `amount: null, amount_note: "see program page"`.
  6. Eligibility ("likely qualifies") is never passed through. With a household given and
     Rewiring America's key set, call its `/api/v1/calculator` for the AMI flags; otherwise say
     "income limits apply".

  Cached as `rules_money/{part_id, utility, juris, income_band}` for 1 day.
- `async def check(...) -> RulesResult`
  - Runs `jurisdiction`, then `asyncio.gather(permit, money)`. One source failing gives `None`
    for that part, never a raise.
  - `spoken` comes from templates, not model text (F6's pattern).

**Endpoints.**
- `POST /rules/check {part_id?, job?, address?, location?, household?: {income, size, owner}}`
  returns `{check_id, status}` on the `jobs.py` table, because a cold call takes about 40 s. A
  cache hit returns `done` at once.
- `GET /rules/check/{check_id}` returns:

```json
{"status": "done",
 "jurisdiction": {"state": "GA", "county": "Fulton County", "place": "Atlanta city", "source": "census"},
 "permit": {"required": "yes", "items": [{"name": "Mechanical (HVAC) permit", "office": "City of Atlanta Office of Buildings",
            "url": "https://www.atlantaga.gov/residents/city-hall/online-services/apply-for-a-technical-permit", "quote_status": "unreadable"},
           {"name": "Electrical permit", "office": "City of Atlanta Office of Buildings", "url": "…", "quote_status": "unreadable"}],
            "who_can_pull": {"homeowner_allowed": "unclear", "licence": "Georgia conditioned air contractor"},
            "inspections": ["mechanical rough-in and final", "electrical rough-in and final"],
            "codes": [{"code": "IMC", "edition": "2024 + GA amendments", "effective": "2027-01-01", "source_url": "https://dca.georgia.gov/announcement/2025-12-09/new-codes-jan-2027"}]},
 "money": {"energy_star": {"certified": true, "seer2": 20.0, "hspf2": 9.0, "pair": {"indoor": "KNSAH121B", "outdoor": "KUSAH121B"}, "bom_has_pair": false},
           "incentives": [{"name": "Georgia Power ductless mini-split rebate", "amount_usd": 500, "status": "active", "how": "instant, through an affiliated installer", "url": "https://www.georgiapower.com/residential/solutions/home-solutions/heip.html", "quote_status": "verified"},
                          {"name": "Federal 25C credit", "amount_usd": null, "status": "ended", "rule": "installs after 2025-12-31"},
                          {"name": "Georgia Home Energy Rebates (HEAR)", "amount_usd": null, "status": "closed", "status_source": "https://energyrebates.georgia.gov/"}]},
 "spoken": "You'll need a mechanical and an electrical permit from Atlanta's Office of Buildings, pulled by a licensed conditioned-air contractor. Georgia Power takes 500 dollars off through their installer. The federal tax credit ended last year, and the state rebate isn't taking applications.",
 "label": "Research with sources, not a permit determination or tax advice. Call the office to confirm.",
 "cost_usd": 0.1845}
```

- Errors: `400` when there's no part and no job; `422` for an address Census can't match with no
  location fallback; `503` OFFLINE with a miss. A source failing gives 200 with that section
  `null`.

**Agent.**
- Tool `check_rules(part_id?, address?)`. It uses the selected part, `context.address` (new,
  optional) or else `context.location`.
- Fast path regex: `\b(permit|inspection|rebate|tax credit|incentive)s?\b`, so it works
  offline from cache.
- It first emits `rules_started {check_id}` with the filler "Checking Atlanta's rules and the
  rebates…". It waits up to 20 s. If the check is done in time it emits `show_rules`; otherwise it
  says "I'll put it up when it's ready" and the next command carries the finished action (F8's
  pattern).
- Add it to the early-exit tuple, since `spoken` is complete.

**Unity contract.** One new action, `show_rules`, on the spec card as two tabs.

| Field | Unity |
|---|---|
| `permit.required` | A pill: yes (amber), no (green), depends or unknown (grey) |
| `permit.items[]` | Rows: name · office. `quote_status != verified` shows a small "unverified quote" tag |
| `money.incentives[]` | Rows: name · amount or "see page" · status pill (active green, ended or closed grey) |
| `energy_star.bom_has_pair == false` | A warning row: "Rebate needs the outdoor unit KUSAH121B", with a button that sends "find KUSAH121B" as a normal command |
| `label` | Always shown |

**Cost per use.** Permit $0.115 and 38 s, money $0.07 and 18 s [V calls #3/#4]. They run
together, so a cold check takes about 40 s and about $0.19. The free sources take under 1 s each.
Warmed: $0 and under 100 ms. Warming the demo, Atlanta and DeKalb × mini-split and window AC,
costs about $0.75.

**Test plan.** `tests/test_rules.py`, offline:
1. `jurisdiction`: the Census fixture for 225 North Ave gives `Atlanta city`; the 4380 Memorial Dr
   fixture gives DeKalb County with `place: None`; with no address, "Atlanta, GA" gives
   `city_only`.
2. `CODE_TABLE`: on 2026-09-26 the GA IMC is the prior edition, and on 2027-01-01 it's 2024 (use
   a date parameter, not today). The model's `code_basis` never reaches the output.
3. `permit`: a respx replay of call #3's trimmed response. Every returned URL is kept, because
   they're all in the citations. An injected URL that isn't in the citations is dropped.
   `homeowner_allowed` stays `unclear`.
4. `quote_status`: verified for a fixture page that contains the quote, not_found when absent,
   unreadable on 403. A fee without a verified quote is removed.
5. `money`: the call #4 replay plus the HEAR status fixture (the real banner) gives HEAR `closed`
   and its amount `null`. The 25C row is `ended` even if the model says `active`. The Georgia
   Power $500 is kept, because "500" is in a verified quote.
6. `energy_star`: the Socrata fixture for KNSAH121B gives 20.0/9.0 and the pair. A BOM with only
   the indoor unit gives `bom_has_pair: false`.
7. Cache: the second call makes no HTTP call. OFFLINE with a miss gives 503; with a hit, 200.
8. Census down → 200 with `jurisdiction.source: "location_text"`. Grok down → `permit: null` and
   the money section still present.
9. Agent: "do I need a permit for this?" hits the fast path and emits `rules_started` then
   `show_rules`, and the spoken line comes from the template.

One `-m live` test runs the Atlanta mini-split check (about $0.19).

**What to mock.** respx for `api.x.ai/v1/responses` (fixtures trimmed from calls #3/#4),
`geocoding.geo.census.gov`, `data.energystar.gov`, the status-probe URLs and the quote-check page
fetches. No network.

### Pick 2: Job packet (`server/packet.py`, `POST /packet/{session_id}`, tool `send_packet`)

**Why.**
- G4 found the hosting (Files public URLs); today's probe proved it works end to end, and that
  revoke really kills the link.
- F4's report is an HTML page on `localhost`, useless to a contractor across town.
- The packet is the physical output of the whole demo. The judge scans a QR on the Quest or the
  table and holds a PDF on their phone with:
  - the scan's measurements, pins and BOM with exact model numbers;
  - the permit and rebate facts with sources (pick 1);
  - the recall verdicts (F6) and the postcard (F5).
- Two audiences in one file:
  - page 2 is for the homeowner: what, why, cost after rebates, next steps;
  - page 3 is for the contractor: models, dimensions, placement, permit office, photos of each
    pin.

**Module.** `server/packet.py`:
- `def gather(session_id) -> PacketFacts` reads F4's `report.build_report(session_id)`, then the
  cached F10 survey, pick 1's rules check, F6's `safety.peek()` per part and F5's postcard path.
  Each part is optional.
- `async def blurbs(facts) -> {homeowner: str, contractor: str}`
  - One `grok-4.20-0309-non-reasoning` call with a strict schema, each blurb ≤ 90 words.
  - The input is the `PacketFacts` JSON as numbered facts. The rule is call #6's: every number in
    a blurb must appear in the facts, checked by the same regex. A blurb that fails is replaced by
    a template.
  - Cached by a hash of the facts.
- `def render(facts, blurbs) -> Path` draws Letter pages at 150 dpi (1275×1650) with PIL and saves
  them with `save_all=True`, which is how today's probe made its PDF. The pages:
  1. cover: the postcard or hero frame, the site, the date, the total after rebates;
  2. homeowner;
  3. contractor, including frame thumbnails with the pin boxes;
  4. sources and labels: every URL, every honesty label verbatim, and the quote status.

  Output: `data/packets/<packet_id>.pdf`, about 0.3–1 MB with the images.
- `async def publish(path, days=7) -> {url, expires_at, file_id}`: `POST /v1/files`, then
  `public-url {expires_after: days*86400}`, capped at 30 days (2,592,000 s, G4).
- `async def revoke(packet_id)`: `.../public-url/revoke`, then delete.

**Endpoints.**
- `POST /packet/{session_id} {days?: 7, publish?: true}` returns
  `{packet_id, pdf_url: "https://files-cdn.x.ai/…pdf", local_url: "/packet/<id>.pdf", qr_url: "/packet/<id>/qr.png", expires_at, pages, label}`.
- `GET /packet/{packet_id}.pdf` and `GET /packet/{packet_id}/qr.png` are local.
- `DELETE /packet/{packet_id}` revokes the public URL.
- `label`: "Public link: anyone with it can open this until {date}. Say 'take the packet down' to
  revoke it."
- Errors: `404` for an unknown session; `503` OFFLINE with `publish: true`, where `local_url` is
  still returned so the booth can print it; `502` when the xAI Files API fails, with `local_url`
  still returned.

**Agent.**
- Tool `send_packet(days?)`. Phrases: "send it to my contractor", "share the job", "give me the
  packet".
- It returns `show_packet {pdf_url, qr_url, expires_at, label}` and speaks "Scan the code. The
  link works for a week, and I can take it down any time."
- Tool `revoke_packet()` answers "take the packet down".
- Add both to the fast path, so they need no LLM turn.

**Unity contract.**
- `show_packet` shows the QR (`qr_url`, a PNG) on a panel, with `expires_at` and the `label`.
- Pinch-and-hold copies `pdf_url` to the phone companion if one exists; otherwise the QR is
  enough.
- The QR is drawn server-side with `segno` (pure Python, no dependencies; the only new
  dependency). The alternative is ZXing in Unity, if the Unity side already has it.

**Privacy rule (in code).**
- The packet shows the street and city, never the unit number or the owner's name.
- The contractor page lists measurements and parts, not the notebook's free-text notes, unless
  they are tagged `share`.
- A public, unauthenticated CDN link is the product. That is why the spoken line says so and
  revoke is one sentence away.

**Cost per use.** About $0.003 for the blurbs (about 2k tokens on 4.20 non-reasoning) [I from
G4's token rates]. Files calls report no token cost [V], but the storage price is still
undocumented. Rendering takes about 1 s of CPU, uploading well under 1 s for a 1 MB file [I from
the 72 KB probe at 0.3 s].

**Test plan.** `tests/test_packet.py`:
1. `gather` on F4's `demo-kitchen` seed plus a fixture rules result gives the facts with totals
   after rebates. A missing survey or postcard is skipped without error.
2. `render` writes a PDF that starts with `%PDF-` and has 4 pages (re-read with `pypdf`, in the lock
   on the F9 branch, or count PIL frames). The sources page contains every URL from the facts.
3. `blurbs`: a mocked reply with an unbacked number ("saves $900") falls back to the template, and
   a clean reply is used.
4. `publish`: respx for `/v1/files` and `/public-url` (today's response shapes). The returned
   `pdf_url` is the `public_url`, and `expires_after` is `days*86400`, capped at 2,592,000.
5. `revoke` calls revoke, then delete.
6. OFFLINE → `local_url` only, no xAI call.
7. The privacy rule: the owner's name and the untagged notes never appear in the extracted PDF
   text.
8. Agent: "send it to my contractor" → `show_packet`. "take the packet down" → revoke.

**What to mock.** respx for `/v1/responses`, `/v1/files`, `/v1/files/{id}/public-url`,
`/revoke` and `DELETE`. Rules, safety and postcard results come from fixtures.

### Pick 3: "Do the whole job" (`server/runjob.py`, `POST /job/run`, tool `run_job`)

**Why.**
- We now have about ten Grok features, and a judge sees each for 30 s. One sentence that runs
  them all, each step appearing in the headset as it lands, is the memorable moment: "Quartermaster,
  fix the roof."
  1. Survey pins drop on the building.
  2. The repair part appears at true size.
  3. The recall check clears it.
  4. The permit and rebate card fills in.
  5. The postcard shows it installed.
  6. The packet QR appears.
  7. The pay panel opens, and Grok stops there: "Hold to pay when you're ready."
- **It's a fixed chain, not an LLM planner.** Multi-agent can't call our functions without beta
  access (call #1). Having the Groq agent pick 6 tools in a row is slow, and it breaks when one
  pick is wrong. The chain is deterministic, and Grok's part is each step's own feature plus one
  closing line.

**Module.** `server/runjob.py`:
- `STEPS = ["survey", "part", "safety", "rules", "postcard", "packet", "checkout"]`. Each step is
  an `async def step_x(run, ctx) -> list[Action]` wrapping an existing module call:
  - `survey`: F10's `survey.run(site, model="fast")`, skipped when the user named a part or
    there's no scene. The top pin is the highest severity and then the highest confidence.
  - `part`: `search.find_parts(pin.part_query or goal)`, then pick the first verified, in-stock
    candidate, cheapest by `total_usd`, and emit `select_candidate`. The existing search job
    gives a warmed demo instant results.
  - `safety`, `rules`, `postcard`: run **together** with `asyncio.gather` (F6 `safety.check`,
    pick 1 `rules.check`, F5 postcard).
    - A `recalled` verdict **stops the chain before checkout**: "This model is recalled; I
      stopped before checkout."
    - A rules check with `bom_has_pair: false` adds the missing unit to the BOM with a spoken
      note.
  - `packet`: pick 2 with `publish: true`.
  - `checkout`: emits the existing `start_checkout` action (the pay panel). **It never
    authorises.**
- Every step has a 45 s cap. A step that fails or isn't available emits
  `job_step {status: "skipped", why}` and the chain continues, except that a failed `part`
  stops it.
- Narration is one template line per step, spoken over F1's relay as it lands. The closing
  summary is one Grok line built from the collected facts, number-checked, with a template
  fallback.
- The run state (steps, actions, cost) lives in `jobs.py`'s table and is persisted through
  `cache`, so a warmed replay makes no paid call.

**Endpoints.**
- `POST /job/run {session_id, site?, goal?, context}` returns `{run_id, steps}`.
- `GET /job/run/{run_id}?after=<n>` returns `{status, actions[n:], steps: [{name, status, spoken, cost_usd}]}`,
  so Unity polls with the last seen index.
- On `WS /voice/realtime` (F1) the relay pushes each new action as it's appended, so no polling.
- Errors: `404` for an unknown session; `409` when a run is already active for the session.

**Agent.**
- Tool `run_job(goal?)`. Fast path: "do the whole job / fix the <x> / run it end to end".
- It returns `job_started {run_id, steps}` at once and speaks "On it: survey, part, safety,
  rules, preview, packet."
- Put it in `agent.TOOLS`, which F1's relay exposes by voice. The fast path doesn't apply over
  realtime (the integration doc's known gap: the relay calls `_run_tool` directly), so the
  tool's description must carry the trigger phrases.

**Unity contract.**

| Action | Unity |
|---|---|
| `job_started {run_id, steps[]}` | A progress rail with one dot per step |
| `job_step {i, name, status: running\|done\|skipped\|stopped, spoken}` | The dot turns green or grey; `spoken` becomes a caption |
| the existing actions (`show_survey`, `select_candidate`, `show_safety`, `show_rules`, `show_postcard`, `show_packet`, `start_checkout`) | Unchanged; they arrive in order |
| `job_done {total_usd, after_rebates_usd, packet_url, cost_usd}` | A summary chip; the rail fades |

**Cost per use.**
- Live and cold:
  - survey $0.01 (F10);
  - safety $0.09 (F6);
  - rules $0.19 (pick 1);
  - postcard about $0.07 (F5, Imagine edit);
  - packet $0.003;
  - closing line $0.001.

  That is **about $0.36** [I, summed from measured parts], and about 40–60 s wall time, since the
  middle three run in parallel.
- Warmed: $0, and a few seconds, most of it Unity animation. The warm script runs the chain once
  per demo scene.

**Test plan.** `tests/test_runjob.py`, all step functions monkeypatched:
1. The happy path emits, in order: `job_started`, then survey, part, safety/rules/postcard (any
   order within the parallel group), packet, `start_checkout`, then `job_done`.
2. `recalled` from safety → the chain stops before `start_checkout`, and the spoken line names
   the recall.
3. A postcard failure → `job_step skipped` and the chain continues. A part failure → the chain
   stops.
4. The 45 s cap: a step that hangs is skipped (use a fake clock or a small cap in the test).
5. `GET /job/run/{id}?after=3` returns only the new actions.
6. The closing line with an unbacked number → the template.
7. A second `POST` while running → 409.
8. The agent fast path "do the whole job" → `job_started`. The realtime relay forwards
   `job_step` actions (use a stubbed socket).
9. No step ever emits a checkout authorisation (assert on action names).

**What to mock.** Every step's underlying module (`survey.run`, `search.find_parts`,
`safety.check`, `rules.check`, the postcard, `packet.publish`), with fixtures from their own
tests. No network.

---

## 6. Open questions

- **Code editions today.** DCA says the 2024 codes take effect 2027-01-01, and its "current
  codes" page lists 2024 editions with no dates. What exactly applies to a permit pulled in
  October 2026 (the prior editions with GA amendments) needs a call to DCA
  (404-679-3118) or its memo PDF before `CODE_TABLE` ships. Cities can also have local
  amendments on file.
- **atlantaga.gov is bot-walled.** Do we accept "URL cited, quote unreadable" for the city's
  pages, or find a mirror (Municode is readable) for each claim?
- **Rewiring America key.** It's free. With it, eligibility and AMI flags become deterministic,
  and Spanish comes for free. Someone should sign up; we didn't test its Georgia coverage.
- **Multi-agent client-tools beta.** Worth one email to xAI. With it, idea 11 needs no public MCP
  tunnel.
- **Files storage price and retention.** Still undocumented. Uploads have no `expires_at` unless
  we set one, so the packet code should delete on revoke and sweep stale files.
- **Transcribe 2.0 default.** The docs disagree, so pin the model if we use it (idea 8).
- **Legal tone.** Permit research and incentive facts are not advice. The labels above are the
  minimum. Do we want a "call the office" button with the number?
- **DeKalb check.** Not run live in this round, for budget. It is the obvious second fixture for
  pick 1: an unincorporated county with a different office and domain.

## Sources

- Our live calls (§1). Free checks:
  - [DCA new codes notice (2025-12-09)](https://dca.georgia.gov/announcement/2025-12-09/new-codes-jan-2027)
  - [DCA current codes](https://dca.georgia.gov/community-assistance/construction-codes/current-state-minimum-codes-construction)
  - [Georgia Power HEIP rebates](https://www.georgiapower.com/residential/solutions/home-solutions/heip.html)
  - [IRS 25C](https://www.irs.gov/credits-deductions/energy-efficient-home-improvement-credit)
  - [GEFA press release 2026-04-09](https://gefa.georgia.gov/press-releases/2026-04-09/georgias-home-energy-rebates-surpasses-25-million-milestone)
  - [Georgia Home Energy Rebates site](https://energyrebates.georgia.gov/)
  - [Census geocoder](https://geocoding.geo.census.gov/geocoder/)
  - [ENERGY STAR mini-split data (akti-mt5s)](https://data.energystar.gov/resource/akti-mt5s.json)
  - [Rewiring America API](https://docs.rewiringamerica.org/api)
  - [EIA Georgia profile](https://www.eia.gov/electricity/state/georgia/)
  - Atlanta pages (403 to us):
    [technical permits](https://www.atlantaga.gov/residents/city-hall/online-services/apply-for-a-technical-permit),
    [construction codes](https://www.atlantaga.gov/government/departments/city-planning/ordinances-regulations/construction-codes)
- xAI docs (fetched 2026-09-26):
  - [release notes](https://docs.x.ai/developers/release-notes)
  - [models](https://docs.x.ai/developers/models)
  - [grok-4.7](https://docs.x.ai/developers/grok-4-7)
  - [multi-agent](https://docs.x.ai/developers/model-capabilities/text/multi-agent)
  - [remote MCP](https://docs.x.ai/developers/tools/remote-mcp)
  - [voice agent](https://docs.x.ai/developers/model-capabilities/audio/voice-agent)
  - [speech to text](https://docs.x.ai/developers/model-capabilities/audio/speech-to-text)
- X (via Grok `x_search`, call #5):
  - [@SpaceXAI Grok 4.7](https://x.com/SpaceXAI/status/2102069822288720022)
  - [@SpaceXAI Transcribe 2.0](https://x.com/SpaceXAI/status/2101005253395308817)
  - [@grok multi-agent pricing](https://x.com/grok/status/2092933926935228653)
  - [@NeoGrinderAI](https://x.com/NeoGrinderAI/status/2102490870184636519)
  - [@weeb3dev](https://x.com/weeb3dev/status/2103663899677696207)
  - [@socialwithaayan](https://x.com/socialwithaayan/status/2090381902482129397)
  - [@Noderunner_Hex](https://x.com/Noderunner_Hex/status/2103452502054064408)
- HN and GitHub:
  - [Grok 4.7 on HN](https://news.ycombinator.com/item?id=49788838)
  - [Transcribe 2.0 on HN](https://news.ycombinator.com/item?id=49757859)
  - [Designing Grok Bot](https://x.ai/news/designing-grok-bot)
  - [GBDL](https://github.com/jcpsimmons/gbdl)
  - [grok-cli](https://github.com/abhayKashyap03/grok-cli)
  - [grok-critic-mcp](https://github.com/Bondartsov/grok-critic-mcp)
  - [ContextX](https://github.com/KayanoLiam/ContextX)
  - [openai-420](https://github.com/allen2c/openai-420)
  - [grok-voice-accessibility](https://github.com/Errrmind/grok-voice-accessibility)
  - [OutLoud](https://github.com/rhishi99/OutLoud)
- Vendors: [PermitFlow](https://www.permitflow.com/), [Symbium](https://symbium.com/).
