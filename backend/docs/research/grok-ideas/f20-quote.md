# F20: paper-quote checker

Built from G5 idea 7 (`g5-round4.md` §4) and G6's "older ideas still strongest" (`g6-round5.md`
§5: 4.20 non-reasoning reads printed text cheaply). `server/quote.py`, `POST /quote/check`, agent
tool `check_quote` (fast path "check this quote"), Unity action `show_quote_check`, a `/debug`
section. Branch `feat/grok-quote-checker`, 2026-09-26.

**Branch base.** This branch is cut from `integration/grok-all`, not `main`. The checker needs
F13's permit and rebate check (`server/rules.py`) and F6's recall check (`server/safety.py`), and
those two only exist together on the integration branch. Merging it into `main` needs F6 and F13
first.

**Design rule.** Grok only transcribes. Every value it returns carries `source: printed_text |
inferred`, and only printed values count as read. The arithmetic, the price comparison, the
permit, rebate and pair checks and the recall verdict are all computed in code from our own
sources.

| Step | Source | What Grok may do |
|---|---|---|
| Read the quote | one `grok-4.20-0309-non-reasoning` vision call, strict json_schema, `temperature: 0`, cached by the file's sha256 | transcribe; mark each value printed or inferred. For a PDF with text, a "printed" number that isn't in the PDF's text is demoted to inferred |
| Arithmetic | code: qty × unit = line total, the lines = subtotal, tax vs the printed rate (on the subtotal or on equipment and materials), subtotal + tax = total | nothing. A check runs only when every value it uses is printed |
| Prices | our cached parts by model number; at most one live parts search per quote, exact model only; the lowest *verified* seller | nothing |
| Missing permit, rebates, ENERGY STAR pair | F13's `rules.check` for the job read from the lines (cached per jurisdiction and job) | nothing new (F13's own rules apply) |
| Recalls | F6's `merge()` over CPSC by model number, plus F6's Grok result only if already cached | nothing new |

Output is questions to ask, never a verdict: "quote $1,195.00 vs lowest verified seller $498.00
(Home Depot)", never "you're being overcharged". The label says the reading is AI and that the
spread compares listed equipment prices, not installed cost.

## Synthetic quotes

Drawn with PIL by `tests/fixtures/quotes/make_quotes.py`, which also writes the ground truth
(`truth.json`) from the same data. Both pages say SYNTHETIC top and bottom, and the contractors
("Totally Fake HVAC Co. (fictional)", "Sample Bros. Heating & Air (fake)", 100 Example Street,
Nowhere, GA 00000) are made up. The customer address is the repo's usual Atlanta demo address, so
F13's cached Atlanta check applies.

| | [`f20/synthetic-clean-quote.png`](f20/synthetic-clean-quote.png) | [`f20/synthetic-bad-quote.png`](f20/synthetic-bad-quote.png) |
|---|---|---|
| Job | LG mini-split, both halves of the ENERGY STAR pair (KNSAH121B + KUSAH121B), line set, pad, circuit, lump-sum labour (no qty or unit price printed), permit line | the same mini-split plus a Midea MAW08U1QWT window AC |
| Planted problems | none: tax is 8.9 % of the equipment and materials only | line 5 prints 2 × $45.00 = **$100.00**; **no permit line**; the Midea is the **recalled** model (CPSC #25320) |

## Live runs (2026-09-26)

`POST /v1/chat/completions`, `grok-4.20-0309-non-reasoning`, strict json_schema, 2048 px JPEG,
`detail: high`. Costs from `usage.cost_in_usd_ticks / 1e10`, logged per call by `llm.chat()`
(`role=quote`) and by `quote._ask`.

| Call | Tokens in / out | Cost | Time |
|---|---|---|---|
| clean, run 1 | 2,826 / 707 | $0.0051 | 6.2 s |
| clean, run 2 | 2,826 / 707 | $0.0023 (prompt cache) | 5.8 s |
| bad, run 1 | 2,826 / 624 | $0.0049 | 5.0 s |
| bad, run 2 | 2,826 / 624 | $0.0045 | 5.2 s |
| bad as an image-only PDF (pypdf page image) | 2,826 / 624 | $0.0045 | 6.1 s |
| **Total Grok spend** | | **$0.0213** of the $0.30 cap | |

- Everything else cost $0 in Grok: F13's Atlanta mini-split permit and incentive research, the
  Census match and the program pages came from an earlier F15 session's cache (under 3 h old), and
  F6's Midea result and the CPSC Midea/LG records from F6's. ENERGY STAR for KNSAH121B was fetched
  live (free).
- The one live parts search (KUSAH121B, not in our cached parts) ran on Groq and SerpApi, not
  Grok, and found the LG outdoor unit at Home Depot, $749.
- A quote costs about $0.005, not G6's ~$0.001: a quote is ~650–700 output tokens (G6's labels
  were ~250), and output tokens set both the cost and the ~5–6 s.

### Extraction accuracy, field by field

Each run is scored against `truth.json`. "Value" is right when a printed value is read exactly
(numbers to the cent, text ignoring case and punctuation). "Source" is right when a printed value
is marked `printed_text` and an unprinted one `inferred`.

**Clean quote (8 lines), both runs identical:**

| Field | Printed values read right | Source label right |
|---|---|---|
| contractor | 1/1 | 1/1 |
| subtotal, tax rate, tax, total | 4/4 | 4/4 |
| line count | 8/8 | |
| description | 8/8 | |
| kind (equipment/material/labor/permit) | 8/8 | |
| brand (2 printed, 6 blank) | 2/2 | 8/8 |
| model_no (2 printed, 6 blank) | 2/2 | 8/8 |
| qty (7 printed, labour blank) | 7/7 | 8/8 |
| unit_price (7 printed, labour blank) | 7/7 | **7/8** |
| line_total | 8/8 | 8/8 |
| **All** | **31/31 printed values** | **44/45 source labels** |

The one miss, in both runs: the lump-sum labour line has no unit price, and the model filled
`unit_price` with the line's $1,400.00 amount and marked it `printed_text`. The number is printed
on the line, but in the Amount column. It does no harm here: the line's `qty` came back null and
`inferred`, so the qty × unit check is skipped for that line, as it should be.

**Bad quote (7 lines), both runs identical, and the same again from the image-only PDF:**

| Field | Printed values read right | Source label right |
|---|---|---|
| contractor | 1/1 | 1/1 |
| subtotal, tax rate, tax, total | 4/4 | 4/4 |
| line count, description, kind | 7/7 each | |
| brand (3 printed, 4 blank) | 3/3 | 7/7 |
| model_no (3 printed, 4 blank) | 3/3 | 7/7 |
| qty, unit_price, line_total | 7/7 each | 7/7 each |
| **All** | **32/32 printed values** | **40/40 source labels** |

The wrong $100.00 was copied as printed, not corrected to $90.00, which is what the prompt asks
for ("never compute or correct anything") and what the arithmetic check needs.

### Full checks (reads from the cache, F13/F6 from their caches)

**Bad quote**, `/debug` in [`f20/debug-bad-quote.png`](f20/debug-bad-quote.png):

| Flag | Line | Text |
|---|---|---|
| `math_line` (warn) | 5 | Line 5: 2 × $45.00 is $90.00, but it says $100.00. |
| `recall` (warn) | 3 | The MAW08U1QWT on line 3: Recalled June 2025: risk of mold exposure (CPSC #25320). |
| `missing_permit` (warn) | – | No permit line, but Atlanta requires a mechanical (HVAC) permit and an electrical permit for this job. Ask who pulls it. |
| `rebate` (info) | – | You may be eligible for Ductless Mini-Split Heat Pump Rebate: up to $500 (Georgia Power customers only). |
| `price_spread` (info) | 1, 2, 3 | quote $1,195.00 vs $498.00; $1,695.00 vs $749.00; $429.00 vs $379.00 (all Home Depot) |

Spoken: "I read 7 lines. Line 5: 2 × $45.00 is $90.00, but it says $100.00. No permit line, but
Atlanta requires permits for this job; ask the permit office. Line 3: this exact model was
recalled in June 2025 for risk of mold exposure. Georgia Power may have a rebate of up to $500.
The 3 units I could price come to $3,319.00 here; verified sellers list them from $1,626.00."

**Clean quote:** no warnings. The math adds up (the tax check found the 8.9 % on the $3,294.00
of equipment and materials), the permit line is there, the pair is complete, the Georgia Power
rebate is listed, and the two LG units show their spreads. Spoken: "I read 8 lines. The printed
math adds up. Georgia Power may have a rebate of up to $500. The 2 units I could price come to
$2,800.00 here; verified sellers list them from $1,247.00."

**OFFLINE** (`OFFLINE=true`, same data dir): both quotes replay in ~0.1 s with the same flags, via
the endpoint and via "check this quote" on `/agent/command`. A file never read gives `503`.

## Demo

1. Warm once online: `POST /quote/check` with each synthetic PNG and
   `address=225 North Ave NW, Atlanta, GA 30332` (~$0.01 in total), and run F13's Atlanta
   mini-split check and F6's Midea check if they aren't cached. Then run with `OFFLINE=true`.
2. `/debug` → "Paper-quote checker": choose `tests/fixtures/quotes/bad.png`, press Check. The
   table shows the red math and recall flags, the permit and rebate lines under it, and the seller
   spreads as links.
3. Or by voice/command: with the file chosen and the box ticked, type "check this quote" in the
   Command box. The image goes as `context.frame_jpg_b64`; the reply is the spoken summary and
   `show_quote_check` fills the same table.
4. On the headset: hold the paper quote up, say "check this quote"; the frame rides in
   `context.frame_jpg_b64`. A printed synthetic quote is the safe demo prop.

## Tests

`tests/test_quote.py` (22 tests; the Grok read is faked with `truth.json`, F13's check and CPSC
with small dicts): arithmetic on the clean and bad quotes, subtotal/total/tax mismatches, tax
sanity without a printed rate; an inferred value never flagged; PDF text demotion; image-only
PDF and junk input; the bad quote's math, missing-permit, recall, rebate and spread flags and
spoken line; the clean quote with no warnings; the pair flag, and no permit flag when F13 says
"depends"; one live search at most, exact model only; the read cache and OFFLINE (served, and
`503` when never read); F13 unavailable as a note; agent routing and the `check_quote` tool.
`tests/test_agent.py`'s shared fast-path table gains "check this quote" rows.

Routing: "check/read/review/look over/go over ... quote/estimate/bid" goes to `check_quote`. It
sits after the packet and report matchers ("send the quote to my contractor" stays a packet,
"show me the report on the quote" stays the report) and before safety and rules, because the quote
check covers both ("check the quote for recalls and permits"). "Do I need a permit?" and "is this
recalled?" still go to F13 and F6; "what's a fair quote for a mini-split?" goes to the LLM.

## Open issues

- **Base branch.** Needs F6 and F13; it can't go to `main` on its own.
- **One job per quote.** The job is the first F13 job whose pattern matches the line descriptions
  (mini-split before water heater before window AC). A quote with two permit-worthy jobs gets
  the permit check for the first only. The bad quote's window AC needs no permit, so it doesn't
  matter there.
- **F6's Grok leg only from cache.** A quote never starts F6's ~$0.10 web and X search; CPSC by
  model is the evidence. A recall CPSC words differently is missed unless F6 already ran for
  that part (selecting it on the headset starts F6).
- **Column confusion.** The labour line's amount was labelled a printed unit price. Harmless when
  qty isn't printed; a line with a printed qty and an amount-only price could in principle get a
  false `math_line`. Not seen on these quotes.
- **Only printed, synthetic quotes tested.** Handwritten quotes, phone photos at an angle,
  multi-page quotes and real PDFs with text (the text path is covered by a unit test only) are
  untested live.
- **Lowest verified seller is a listed price.** Contractors' equipment prices include delivery,
  warranty and margin; the label says so, but the spread still reads large.
- **A cold F13 check** (no cache for that jurisdiction and job) takes 15–40 s and ~$0.19. The quote
  check waits 30 s and then reports "still running" in `rules_note`; the F13 job finishes and
  caches, so asking again is instant.
