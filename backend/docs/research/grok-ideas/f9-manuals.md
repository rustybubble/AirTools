# F9: Install-manual Q&A

Feature agent F9, 2026-09-26. Branch `feat/grok-manual-qa`. This builds G3 idea 7 (`g3-round2.md` §4).

**Headline.** For a selected part, one xAI Responses call (`web_search` limited to the manufacturer's domain) finds the install PDF. It costs $0.05–0.09 and takes 6–133 s, including downloads. After that, each install question is one Grok call with the best-matching pages in context: **~1 s and $0.002–0.014**. Every answer carries a page citation and a quote that the server has checked against the PDF text. We found manuals for 3 of 5 parts (Midea, LG, Simpson) and none for 2 (Hessaire, DuctlessAire). Of 12 questions, 11 got an honest answer:
- **8 answered**: 7 correct, and 1 correct but over-simplified;
- **3 correctly "not covered"**;
- **1 wrongly rejected**: the model stitched a quote across two table lines, so the server refused a right answer.

The live test spent **$0.458**, out of a $0.80 cap.

## Which path shipped: local retrieval, not an xAI Collection

The Collections API (`management-api.x.ai/v1/collections`) needs a **Management API key** with the
`AddFileToCollection` permission (docs.x.ai, *Using Collections via API*). Our `GROK_API_KEY` gets:

```
GET https://management-api.x.ai/v1/collections -> 401 "Invalid bearer token. Please ensure you use a valid management key."
```

The same key lists `/v1/files` fine (200), and `.env` has no management key. Creating a
collection per site isn't possible without one, so this ships the fallback:
1. Extract the text locally with `pypdf`. This is a new pure-Python dependency and the only PDF
   library in the lock.
2. Pick pages with keyword idf scoring. A short manual (≤ 40k characters) is sent whole.
3. Make one Grok call with those pages in context.

This path is cheaper than Collections, keeps OFFLINE working, and gives exact PDF page numbers,
because we own the page split. If a management key turns up, `manuals.select_pages` is the one
place to swap in `POST /v1/documents/search` over a per-site collection.

## What was built

| Piece | Where | Notes |
|---|---|---|
| `llm.responses()` / `parse_responses()` / `ResponsesResult` | `server/llm.py` | Copied byte for byte from F3 (`feat/grok-installer-intel`) at the same spot, so the branches merge cleanly |
| `manuals.index(part, domains?, refresh?)` | `server/manuals.py` | Model `grok-4.20-0309-non-reasoning`. Sends `web_search` with `filters.allowed_domains` (the name→domain map `DOMAINS`, else `<name>.com`), strict `json_schema` `{manuals: [{url, title}]}`, `include: ["no_inline_citations"]` and `max_turns: 4`. Candidates are Grok's list plus any `.pdf` in the citations. Off-domain URLs are dropped. At most 4 are downloaded (browser UA, 40 MB cap). A candidate is kept only if it starts with `%PDF` and has a text layer. The first one whose text or URL names the model number wins; otherwise the first usable one, flagged `model_match: false`. The PDF goes to `data/parts/<id>/manual.pdf` and the page texts to `cache("manual_text")`. The result is cached in `cache("manual", part_id)` with a 7-day TTL, so a "not found" retries weekly |
| `manuals.ask(part, question)` | `server/manuals.py` | Pages go in with `=== PAGE n ===` headers. A strict schema `{covered, answer, page, quote}` comes back. Then the honesty check (below). Cached in `cache("manual_qa", {part_id, question, source})`; question punctuation and case are normalised by the cache key |
| `manuals.prefetch(part)` | `server/manuals.py` | A background index, started on agent `select_candidate`, after `/checkout`, and on a first `ask_manual`. It is deduped per part |
| `POST /parts/{id}/manual`, `POST /parts/{id}/manual/ask`, `GET /parts/{id}/manual.pdf` | `server/app.py` | Errors: `400`, `404`, `502` for xAI, `503` for OFFLINE miss. `found: false` is a 200 |
| Agent tool `ask_manual(question, part_id\|null)` | `server/agent.py` | Emits `show_manual_answer {part_id, answer, page, quote, pdf_url}` and speaks the verified answer plus "Page n of the manual" with no second agent turn. A filler line next to the tool call no longer swallows it. If the manual isn't indexed yet, it replies "Fetching the manual now; ask me again in a minute" and indexes in the background, because a first find can take 2 min. OFFLINE: a question about the selected part is answered from the cached manual, and this check runs before the find-a-part regex, which would otherwise catch "need" |
| Debug section | `server/static/debug.html` | Part id (blank = selected), Find manual and Ask buttons, then the answer, quote, "open the manual at page n" link, latency and cost. The agent's `show_manual_answer` renders in the same place |
| Tests | `tests/test_manuals.py` (13) | Covers: manual found (request shape, off-domain URL never fetched, HTML candidate skipped, PDF saved, cache hit); none found; quote-not-in-text rejection (the live Simpson case); not covered; answer cache hit; OFFLINE (cached answer, page pointer, 503); endpoints; and the agent (action, filler, prefetch, offline). The PDF is generated inside the test and respx mocks the network |

**Honesty rules, as built.** The server enforces these, not the prompt.
- **The quote must be in the manual.** It is compared as letters and digits only, lowercased and
  NFKC-normalised (PDF ligatures like "ﬁ" and the line breaks pypdf inserts would otherwise break
  exact matching). It must be at least 8 characters.
- **The page is where the server found the quote.** The model's page is checked first; if the
  quote sits on another page, that page is cited instead.
- **A quote that isn't in the text** is rejected: "I couldn't back that up from the manual's text,
  so I won't guess." (`rejected: true`).
- **`covered: false`** always becomes the fixed line "The manual doesn't cover that, so I won't
  guess." The model's own text is never used, since it may contain a guess.
- **A manual that never names the model** makes the answer say "Page n of the *series* manual".

## Live results

Parts from `data/parts/`. Index = one xAI web search, plus downloading and parsing the PDFs.
Latency is wall time from our side. Cost is `usage.cost_in_usd_ticks / 1e10`.

| Part | Manual found | Pages | Model named? | Index latency | Index cost |
|---|---|---|---|---|---|
| Midea MAW08U1QWT window AC | `MAW12U1QWT-user-manual.pdf` (midea.com; covers the whole U+ line, and MAW08U1QWT is in the text) | 44 | yes | 48.8 s | $0.052 |
| LG KNSAH121B mini-split | `SVC-Eview_SZ_K-SAC121B_Service-Manual` (media.us.lg.com). This is the **service manual for the sibling KNSAC121B**, not the install manual. First it tried an unrelated Spanish owner's manual with no text layer | 26 | no | 133.2 s | $0.076 |
| Simpson Strong-Tie LUS26 joist hanger | `S-C-INSTALL14.pdf`, the Installer's Pocket Guide (strongtie.com). It names the LUS family, not "LUS26" | 120 | no | 75.5 s | $0.085 |
| Hessaire H12FB1 mini-split | none on hessaire.com | – | – | 6.4 s | $0.052 |
| DuctlessAire DA27-9-9-9 mini-split | none on ductlessaire.com | – | – | 7.6 s | $0.073 |

| Part | Question | Answer (spoken) | Correct? | Page | Latency | Cost |
|---|---|---|---|---|---|---|
| Midea | What size drill bit do I need for the pilot holes? | "1/8” drill bit." (quote: "Drill and 1/8” drill bit") | yes | 10 | 1.0 s | $0.0027 |
| Midea | What window widths does it fit? | "22 to 36 inches (55.8 cm to 91.4 cm)." | yes | 9 | 1.5 s | $0.0075 |
| Midea | How much vertical opening does the lower sash need? | "13.75 inches (34.9 cm)." | yes (the quote gives it as the window height; p. 12 says it directly: "a clear vertical opening of 13.75 inches") | 9 | 1.2 s | $0.0059 |
| Midea | What torque should I tighten the bracket screws to? *(not in manual)* | "The manual doesn't cover that, so I won't guess." | yes, no torque anywhere in the text | – | 1.0 s | $0.0045 |
| LG | How far from the ceiling does the indoor unit need to be? *(not in this manual)* | "The manual doesn't cover that, so I won't guess." | yes; the service manual defers to the installation manual | – | 1.2 s | $0.0123 |
| LG | What is the maximum refrigerant piping length? | "49.2 ft. (15 m). Page 17 of the series manual." | yes, for KNSAC121B (quote "9.8 / 24.6 / 49.2 (3 / 7.5 / 15)" is min/std/max) | 17 | 1.2 s | $0.0121 |
| LG | What size are the liquid and gas pipes? | "Liquid ø1/4 (6.35 mm), Gas ø3/8 (9.52 mm)." | yes, for KNSAC121B | 17 | 1.3 s | $0.0108 |
| LG | How long should the power supply cord be? | "between 1.5~3 m (4.9~9.8 ft)." | yes | 8 | 1.4 s | $0.0122 |
| Simpson | What nails do I use for an LUS hanger on a single 2x joist? | "I couldn't back that up from the manual's text, so I won't guess." (rejected) | **false rejection**: Grok said 10d (0.148" x 3"), which is right, but its quote `10d for single 2x (0.148" x 3")` joins two lines that have a Spanish line between them. The same happened after tightening the prompt (2 extra calls, $0.019) | – | 1.0 s | $0.0095 |
| Simpson | Can I use framing nail-gun nails instead of 10d or 16d? | "No, .121 may NOT be used to replace 10d or 16d nails." | **partly**: the quote is right, but the page also says gun nails are fine *if* the diameter, length and count are correct. "No" over-simplifies | 12 | 1.1 s | $0.0139 |
| Simpson | What nails does double-shear nailing need? | "full length 10d or 16d common nails." | yes | 16 | 0.9 s | $0.0078 |
| Simpson | What is the allowable download load of the LUS26 in pounds? *(not in manual)* | "The manual doesn't cover that, so I won't guess." | yes, the pocket guide has no load tables | – | 0.7 s | $0.0017 |

**Totals.** The live spend was **$0.458**:
- 5 index calls: $0.339;
- 12 questions: $0.101;
- 2 re-asks to see the rejected quote: $0.019.

The Collections probe was free (a 401).

**Other observations.**
- **Retrieval.** All three manuals are over the 40k-character "send it whole" budget (Midea 64k,
  LG 53k, Simpson 41k), so every question went through keyword page selection.
- **Index latency** (48–133 s when found, 6–8 s when not) covers the search plus downloading and
  parsing each candidate PDF; the split wasn't measured. The Midea PDF alone is 6.1 MB. Either
  way it is too slow to hold a voice turn, which is why the agent indexes in the background.
- **OFFLINE check.** With the server run offline (`OFFLINE=1 uvicorn …`), cached questions came
  back instantly. A new question ("How far from the ceiling?" on the Midea) got
  `offline: true, page 37` plus that page's best line: an honest pointer, not an answer.

## Demo steps

1. Start the server: `uv run uvicorn server.app:app --host 0.0.0.0 --port 8000`. Open `/debug`.
2. Search and select a part, or type `midea-maw08u1qwt-8d6d08` into **Install manual → part_id**.
   Click **Find manual**. Live this takes about 50 s; it is instant if pre-warmed.
3. Ask "What size drill bit do I need for the pilot holes?" You get "1/8” drill bit", the quote,
   and a link that opens the PDF at page 10.
4. Voice or agent: with the part selected, say "what torque do I tighten the bracket screws to?"
   The reply is "The manual doesn't cover that, so I won't guess."
5. To pre-warm for the booth, run `POST /parts/{id}/manual` for each demo part and ask the
   rehearsed questions once. After that, `OFFLINE=1` serves them all.

## Open issues

- **Finding the PDF is still the weak point.** We found 3 of 5, and 2 of those 3 don't name the
  exact model: LG's is a sibling's *service* manual, and Simpson's is a family pocket guide. A
  tighter search, e.g. `"<model> installation manual filetype:pdf"` or a second call on retailer
  CDNs like `images.thdstatic.com`, would find more but breaks "manufacturer only". The
  `domains` override on `POST /parts/{id}/manual` is the manual escape hatch.
- **Stitched quotes from tables and bilingual layouts are rejected.** The Simpson guide
  interleaves English and Spanish lines, and Grok joins the English label to the value two lines
  down. The prompt fix didn't help. Two options: accept a quote whose pieces all appear on the
  cited page (a weaker guarantee), or ask Grok for a list of verbatim fragments.
- **Keyword retrieval.** It misses synonyms ("fastener" vs "screw"). It is fine for manuals under
  ~40k characters, which are sent whole.
- **Printed page ≠ PDF page.** `page` is the PDF index, which is what `#page=n` opens, not the
  number printed on the page.
- **Scanned PDFs** with no text layer are skipped. OCR isn't worth it for the demo.
- **The live cache lives in this worktree's `data/`.** That covers the 3 PDFs, `cache/manual*`
  and 12 answers. Copy `data/cache/manual`, `data/cache/manual_text`, `data/cache/manual_qa` and
  `data/parts/*/manual.pdf` into the main checkout's `data/` to reuse them. The first Midea answer
  was cached before the full-stop fix and is spoken "1/8” drill bit Page 10".
