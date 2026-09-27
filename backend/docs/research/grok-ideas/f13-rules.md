# F13: rules and money check (permits, code editions, rebates)

Built from G5's pick 1 (`g5-round4.md` §5): `server/rules.py`, `POST /rules/check` (background
job) + `GET /rules/check/{id}`, agent tool `check_rules`, Unity action `show_rules`, a `/debug`
section. Branch `feat/grok-rules-check`, 2026-09-26.

**Design rule.** Facts come from deterministic sources; Grok only researches and cites.

| Fact | Source | What Grok may do |
|---|---|---|
| Permitting jurisdiction | Census geocoder (`onelineaddress`, `layers=all`) | nothing; the street address never goes to xAI |
| Code editions and dates | checked-in `CODE_TABLE` from DCA's 2025-12-09 notice | its `code_basis` is asked for and dropped, as are caveats that mention codes |
| Program status | each program's own page (`PROGRAMS` regexes), IRS rule for 25C | its `status` is never shown |
| Amounts and fees | only if every number is in a quote we found on the fetched page | supplies the quote |
| Permit items | Grok `web_search` limited to the office's domains | kept only if the URL is in that call's citations |
| Efficiency, certified pair | ENERGY STAR open data (Socrata `akti-mt5s`, `pbpq-swnu`, `5xn2-dv4h`) | nothing |
| Office phone | office table, read from the office's own site | nothing |

## Live runs (2026-09-26)

Model `grok-4.20-0309-non-reasoning`, `/v1/responses`, strict json_schema,
`no_inline_citations`; permit `max_turns: 4`, money `max_turns: 3`. Costs from
`usage.cost_in_usd_ticks / 1e10`. Census, ENERGY STAR and page fetches are free.

| Call | Web searches | Tokens in / out | Cost | Time |
|---|---|---|---|---|
| Atlanta mini-split, incentives | 8 | 47.6k / 0.8k | $0.0764 | 9.2 s |
| Atlanta mini-split, permit | 13 | 74.6k / 1.2k | $0.1134 | 14.4 s |
| DeKalb water heater, incentives | 8 | 49.2k / 0.7k | $0.0785 | 8.6 s |
| DeKalb water heater, permit | 10 | 71.4k / 1.1k | $0.0962 | 12.9 s |
| **Total** | | | **$0.3645** of the $0.50 cap | ~15 s per check (the two calls run in parallel) |

Every re-run after that (fixes to the rules, the `/debug` page, the agent) was served from the
cache at $0.

### Job 1: mini-split install, 225 North Ave NW, Atlanta, GA 30332

Part: cached `lg-knsah121b-04da2e` (LG KNSAH121B indoor unit, $498).

- **Jurisdiction [V, Census]:** Atlanta city, Fulton County.
- **Spoken:** "You'll need a mechanical (HVAC) permit and an electrical permit from the City of
  Atlanta Office of Buildings. Whether you can pull them yourself is unclear. Rebates need the
  certified pair: add the KUSAH121B. Georgia Power Ductless Mini-Split Heat Pump Rebate: up to
  500 dollars. The federal tax credit ended after 2025. The state HEAR rebate isn't taking
  applications. Not legal advice; confirm with the permitting office."

| Claim | Grok said | Check | Shown as |
|---|---|---|---|
| Mechanical (HVAC) permit, Office of Buildings | yes, atlantaga.gov technical-permit page | URL in citations; page **403** to our GET | kept, `unverified (site blocks automated checks)` |
| Electrical permit | yes, same page; quote stitched from 3 fragments | URL in citations; 403 | kept, unverified (blocked) |
| Homeowner may pull | **`yes`** "with affidavit for own primary residence" | no quote could be read | **`unclear`**, `model_said: yes` |
| Code editions | "2024 IMC/IRC, 2023 NEC with Georgia amendments" (no date this time) | DCA table: effective **2027-01-01** | table: prior edition applies until then |
| Caveat "Current as of 2026 code update" | caveat | mentions codes | dropped |
| Georgia Power ductless mini-split rebate | up to $500, instant; tagged program `other` | quote "Up to $500 Ductless Mini Split Heat Pump - Combined 2" **found** on heip.html; URL matches HEIP's status page; probe finds "submit your application" | **$500, active, verified** |
| 25C credit | `ended`, "up to $2,000" | quote about "through December 31, 2025" is on irs.gov, but it has no 2,000 | `ended` (fixed rule), amount **dropped** |
| Georgia HEAR | `closed`, **"$8,000"** | quote "The HEAR program is not accepting new applications." verified; no 8,000 in it | **`closed`** (probe), amount **dropped** |
| ENERGY STAR | — | KUSAH121B + KNSAH121B: 20.00 SEER2, 9.00 HSPF2, 11,000 BTU/h | certified, `bom_has_pair: false`, `missing: KUSAH121B` |

This run the model got HEAR's status right (G5's call #4 had it `active`); the probe would have
overridden it either way. It still invented both dollar figures that aren't on the pages it
quoted.

### Job 2: water heater replacement, 4380 Memorial Dr, Decatur, GA 30032

Part: cached `rheem-xg50t06ec38u1-8411d6` (Rheem Performance 50 gal natural gas).

- **Jurisdiction [V, Census]:** DeKalb County, **no incorporated place**: the county issues the
  permit, not the City of Decatur that the mailing address names.
- **Spoken:** "You'll need a plumbing permit and a mechanical permit (if gas-fired) from the
  DeKalb County Planning and Sustainability Department. A licensed contractor has to pull them.
  The XG50T06EC38U1 isn't ENERGY STAR certified. The federal tax credit ended after 2025. The
  state HEAR rebate isn't taking applications. Not legal advice; confirm with the permitting
  office."

| Claim | Grok said | Check | Shown as |
|---|---|---|---|
| Plumbing permit | yes; quote from the county's fillable PDF ("Water heaters No. ___ X $10.00") | URL in citations; a PDF, which we don't parse | kept, `unverified (not a web page we can read, e.g. a PDF)`; the $10 fee is **not** shown |
| Mechanical permit (if gas-fired) | "Must be submitted by a licensed mechanical contractor …" | **found** on dekalbcountyga.gov permits page | kept, **verified** |
| Homeowner may pull | `no`, licensed plumbing/mechanical contractor | a quote from the same call was verified, and it says a licensed contractor submits | `no` |
| Office phone | — | 404-371-2155, read on dekalbcountyga.gov | from the table |
| Code editions | 2024 IRC/IPC/IMC/IFGC with GA amendments | DCA table | prior edition until 2027-01-01 |
| 25C | `ended`, "30% of cost up to $600" | quote verified, no 600 in it | `ended`, amount dropped |
| Georgia Power HEIP | `active`; quote "Heat Pump Water Heater 50% of cost up to $800 (or $1,000 in some pages); no rebate listed for same-fuel tank replacement." | **not on the page** (the model wrote its own summary into `quote`) | status `active` from the probe, no amount |
| Georgia HEAR | `closed` | verified | `closed` |
| ENERGY STAR | — | no row for XG50T06EC38U1 in `pbpq-swnu` | not certified |

## Eligibility (fix, after F15's live run)

F15's run listed Georgia Power's "up to $500 ductless mini-split" rebate as active for a TCL
mini-split that ENERGY STAR's open data doesn't list. Job 1 above had the same hole: the rebate
stayed `active, $500` with half of the certified pair missing. Now each program has a requirement
(`Requires`: `energy_star`, `seer2_min`, `hspf2_min`, `source_url`), checked against the ENERGY
STAR result:

- **Where the requirement comes from.** First the program's own status page, which we already
  fetch. HEAR's page says "Qualifying households can get rebates on certain ENERGY STAR®
  appliances, such as heat pumps". Otherwise it comes from the checked-in `Requires` on
  `PROGRAMS`. heip.html names no tier. The table cites Georgia Power's 2026 preconditions PDF,
  which says "ENERGY STAR® certified" for its equipment rebates, and for heat pumps that the
  "indoor/outdoor unit combination must be listed". That PDF doesn't cover the ductless
  installer rebate, so the entry carries a `basis` saying this is our reading.
- **Not certified, or half a certified pair:** `eligibility: not_eligible`, and an active or
  unknown status becomes `status: not_eligible` with a plain `eligibility_reason` ("needs an
  ENERGY STAR certified system; this unit isn't listed", or "needs the whole ENERGY STAR
  certified pair; add the KUSAH121B"). The amount stays visible, but it isn't counted.
- **ENERGY STAR lookup failed** (or no model number): `eligibility: unverified`. The status stays
  `active` and the amount is shown, but `counted: false`, and the spoken line says "but it needs
  an ENERGY STAR certified system; we couldn't check this unit's listing".
- **The total.** `money.rebates_usd` sums only the `counted` rows (active, with an amount,
  eligible or with no requirement on file). Consumers of an "after rebates" figure (F14
  `packet.py`, F15 `runjob.py`) should use it rather than summing `status == "active"`.

Re-running Job 1 with this fix gives: "Georgia Power Ductless Mini-Split Heat Pump Rebate doesn't
apply: it needs the whole ENERGY STAR certified pair; add the KUSAH121B."

## Tests

`tests/test_rules.py`, 22 tests, all offline: respx replays of the four live xAI bodies (trimmed
to the search calls, the message and usage), the two Census replies, the ENERGY STAR row, and
trimmed copies of the HEAR, HEIP, IRS and DeKalb pages; atlantaga.gov is mocked as 403 and the
DeKalb PDF as `application/pdf`. Covered: jurisdiction (city, unincorporated county, text
fallback, Census down, no match), the code table by date and against a model that claims
2026, the 403 site kept as unverified, an uncited URL dropped, amounts not in the quote dropped,
the closed program and the fixed 25C rule beating a model that says `active`, the ENERGY STAR
missing-pair flag, rebate eligibility (certified pair, uncertified unit, half a pair, failed
lookup; the requirement from the page or the table), Grok down, cache (a warm check makes no HTTP call) and OFFLINE (503 on a
miss, served from cache on a hit), the endpoint errors, and the agent fast path, including a
slow check that rides on the next command.

## Open issues

- **Prior code editions aren't named.** DCA's pages list only the 2024/2023 editions. Before
  2027-01-01 we say "prior edition with Georgia amendments" and link the notice; naming them
  needs DCA's memo or a call to (404) 679-3118.
- **Quote relevance isn't machine-checked.** A verified quote is on the page, but it may not
  support the claim (G5 saw a reinspection line used for a permit claim). The UI shows the quote.
- **PDF quotes stay unverified.** DeKalb publishes its permit forms as PDFs; `pypdf` would
  let us check them.
- **atlantaga.gov stays unverified**, so the Atlanta office phone is `null` and Atlanta fees are
  never shown. We don't send browser headers to get around the wall.
- **Household eligibility is not computed** (equipment eligibility is, above). No household input; HEAR carries "income limits apply".
  Rewiring America's calculator needs a key we don't have.
- Only Atlanta city and unincorporated DeKalb have office rows. Other jurisdictions search with
  no domain allow-list and carry `sources_label: "unverified jurisdiction sources"`.
- Only three jobs (mini-split, water heater, window AC). Other parts get `required: unknown` at
  no cost.
- `server/warm.py` doesn't pre-warm rules checks yet; run one `POST /rules/check` per demo job
  before going offline.
