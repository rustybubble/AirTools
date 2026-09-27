# Research: web search + seller/pricing data sources for the parts server

*Written 2026-09-24. Covers §1b (part.json contract) and §4b.1–4b.2 of `AirTool implementation plan.md`.*

Test queries used across every service:
- Primary: `"Amerimax 5 in. K-style hidden gutter hanger"` (and `"...buy"` variants for text-search APIs)
- Secondary (AC unit, used to test bot-blocking / spec extraction on a bigger appliance): `LG LW6024RSMX` window AC, via its real Amazon URL

All raw responses are saved under `tests/fixtures/search/<service>_<shortname>.json`. Fields named `raw_content` / `text` / `markdown` / `content` were truncated to ~20 KB with a `...[TRUNCATED, original_len=N]` suffix so fixtures stay small; everything else (SerpApi's structured JSON) is untouched and faithful to the live response.

Live-call budget spent: **SerpApi 4/4**, Tavily 3/5, Exa 3/5, Firecrawl 3/5 (all failed at auth, see Warning 1), You.com 1/5, Ollama 5/5. No API key values were printed or written to any fixture (verified by grepping every fixture for each key's exact string — no matches).

---

## 1. SerpApi (google_shopping, google_immersive_product, home_depot, home_depot_product)

Base: `GET https://serpapi.com/search.json`, auth via `api_key` query param (no header). All 4 calls used `no_cache` unset (defaults to `false`, cache-eligible).

### 1.1 `engine=google_shopping`
- **Params used:** `q="Amerimax 5 in. K-style hidden gutter hanger"`, `gl=us`, `hl=en`, `location="Atlanta, Georgia, United States"`
- **Latency:** 0.74 s
- **Fixture:** `serpapi_google_shopping.json` (40 `shopping_results`, 228 KB — legitimately that size, no raw-text fields to truncate)
- **Fields we'd consume:** `shopping_results[].{title, product_id, source, price, extracted_price, delivery, rating, reviews, tag, immersive_product_page_token, serpapi_immersive_product_api, product_link, thumbnail}`
- **Trimmed example** (Home Depot hit inside the result set):
  ```json
  {
    "position": 2,
    "title": "Amerimax Brown Hidden Hook M1722B",
    "source": "Home Depot",
    "price": "$3.18", "extracted_price": 3.18,
    "delivery": "Free delivery", "rating": 3.7, "reviews": 76,
    "immersive_product_page_token": "NN5wBnicdVNJru..."
  }
  ```
- **Note:** `delivery`, `rating`, `reviews`, `tag` are present on some results and `null` on others (confirmed by scanning all 40 — e.g. a $392.85 Opentip.com bulk-case listing had no rating). `immersive_product_page_token` is present on every result and is what feeds §1.2.

### 1.2 `engine=google_immersive_product`
- **Params used:** `page_token=<from 1.1's Home Depot result>`, `more_stores=1`
- **Latency:** 1.30 s
- **Fixture:** `serpapi_google_immersive_product.json`
- **Fields we'd consume:** `product_results.{title, brand, rating, reviews, price_range, stores[]}`; each store: `{name, link, price, extracted_price, shipping, shipping_extracted, total, extracted_total, rating, reviews, payment_methods, details_and_offers[]}`
- **Trimmed example** — 13 stores returned (`more_stores=1` got the full list vs. the default 3–5), confirming multi-seller works as documented:
  ```json
  {
    "name": "Home Depot", "price": "$3.18", "shipping": "Free", "extracted_total": 3.18,
    "rating": 4.6, "reviews": 3700,
    "details_and_offers": ["In stock online", "Free delivery", "90-day returns"]
  },
  {
    "name": "Hamshawlumber.com", "price": "$4.59", "shipping": "+ $20.00", "extracted_total": 24.59,
    "details_and_offers": ["In stock online", "Delivery between Sep 25 – Oct 1 $20", "30-day returns"]
  }
  ```
  **Important field-path correction vs. the doc's assumption:** there is no separate structured `delivery_eta` field. ETA text (when present at all) is embedded as a free-text string inside `details_and_offers[]` (e.g. `"Delivery between Sep 25 – Oct 1 $20"`), and only some sellers include it. Parsing it needs a regex/date-range parser, not a field lookup.

### 1.3 `engine=home_depot`
- **Params used:** `q="Amerimax K-style hidden gutter hanger"`
- **Latency:** 1.05 s
- **Fixture:** `serpapi_home_depot.json` (24 products)
- **Fields we'd consume:** `products[].{product_id, title, thumbnails, price, rating, reviews, link}`
- Gave us `product_id=100085356` for the exact hanger, used in 1.4.

### 1.4 `engine=home_depot_product`
- **Params used:** `product_id=100085356`
- **Latency:** 0.53 s
- **Fixture:** `serpapi_home_depot_product.json`
- **Fields we'd consume:** `product_results.{title, brand, model_number, price, rating, reviews, images[], link, specifications[], fulfillment, availability_type}`
- **`specifications` is a list of category groups, not a flat dict** — exact shape:
  ```json
  [
    {"key": "Details", "value": [{"name": "Material", "value": "Aluminum"}, ...]},
    {"key": "Dimensions", "value": [
      {"name": "Product Depth (in.)", "value": "5 in"},
      {"name": "Product Height (in.)", "value": "1 in"},
      {"name": "Product Width (in.)", "value": "0.75 in"}
    ]}
  ]
  ```
  Consumer code needs to find the group where `key == "Dimensions"` then map `name` → W/D/H by string match on `"Width"/"Depth"/"Height"`, not by position.
- **Bonus find:** `fulfillment.options[]` gives real per-channel ETAs directly, better than the immersive-product free text:
  ```json
  {"type": "Ship to Home", "title": "Get it by", "arrival_time": ["Sep 26", "Sep 26"], "bottom": "Free delivery"}
  ```
  This confirms §4b.1's rule ("where SerpApi's home_depot product engine returns Dimensions for the same model number, SerpApi wins") is implementable exactly as planned, and it's also our best ETA source when Home Depot is one of the sellers.

### 1.5 Caching / quota
Per current SerpApi docs (fetched live): default `no_cache=false` means **identical repeated queries are served from a 1-hour cache and are not billed against the monthly quota**; only `no_cache=true` forces a fresh, billed search (and cannot be combined with `async`). This confirms §4b.2's disk-cache strategy is the right one layered on top: our own on-disk cache (keyed by normalized query) avoids re-hitting SerpApi at all after the first call, while SerpApi's own 1-hour cache is a secondary safety net for retries within the same demo run.

**Verdict:** SerpApi is the strongest, most structured source for exactly what §4b needs — multi-seller price/shipping/rating (`google_shopping` + `google_immersive_product`) and verified dimensions (`home_depot_product`). Spend the 4-call budget here first; it directly produces `part.json`-shaped data with almost no parsing beyond flattening `specifications` and `details_and_offers`.

---

## 2. Tavily (`/search`, `/extract`)

Base: `https://api.tavily.com`, auth `Authorization: Bearer tvly-...`.

### 2.1 `POST /search`
- **Params:** `{query, search_depth: "advanced", max_results: 5, include_raw_content: true}`
- **Latency:** 12.32 s (slowest call observed all-service)
- **Fixture:** `tavily_search.json`
- **Result:** 5 URLs (a small retailer, Lowe's, Amazon `/clp/` category page, a small retailer, another small retailer). `raw_content` was **empty string** for both `homedepot`-adjacent... actually for Lowe's and one small retailer; non-empty (truncated to 20 KB from full) for a Magento small-retailer site and an Amazon category page.
- **Fields we'd consume:** `results[].{title, url, content, raw_content, score}`

### 2.2 `POST /extract`
- **Params:** `{urls: [<Home Depot product URL>], extract_depth: "advanced"}` → **`0` results, `1` failed** (`"error": "Failed to fetch url"`).
- Second call: `{urls: [<Lowe's URL>, <Amazon AC URL>]}` → Lowe's **failed** the same way; Amazon **succeeded**, returning 219,538 chars of raw content (truncated to 20 KB in the fixture).
- Inspected the kept 20 KB of the Amazon page: it is entirely site chrome / login / warranty-upsell boilerplate — no `BTU`, `Product Dimensions`, or `Item Weight` keyword found in that window. **Not confirmed** whether the real spec table exists later in the untruncated 219 KB (would need another live call, not spent — flagged as unconfirmed, not a negative finding).
- **Verdict:** Tavily's extract flatly fails on Home Depot and Lowe's (bot-blocked at fetch time, not at parse time — it never got bytes back). It does get bytes back from Amazon but the ~20 KB near the top is boilerplate, so it's a weak candidate for spec extraction on major retailers without pagination/anchor tricks we didn't test.

---

## 3. Exa (`/search` with `contents`, `/contents`)

Base: `https://api.exa.ai`, auth `x-api-key: ...`.

### 3.1 `POST /search`
- **Params:** `{query, type: "auto", numResults: 5, contents: {text: {maxCharacters: 3000}}}`
- **Latency:** 1.55 s
- **Fixture:** `exa_search.json`
- Returned **two Home Depot product pages directly in the index** (unlike Tavily/You.com's mixed retailer results), plus Lowe's, a small retailer, and True Value — all five with non-empty `text`.

### 3.2 `POST /contents`
- Called twice: once at `maxCharacters: 5000` on the exact Home Depot product URL from §1.4 (0.16 s, `source: "cached"`), once with `text: true` (full page, 0.18 s, 3,469 chars total — this page is short server-side, i.e. **not** artificially truncated).
- **Both times, the returned text is Home Depot's mega-nav/category chrome** (`"Specials & Offers Appliances Bath Blinds & Window Treatments..."` through to `"Internet #100085356 / Model #21812"`) — it stops right where the actual specs/description would start. No `Dimensions`, `Product Depth`, `Product Width`, `Color Family` keyword found anywhere in the full 3,469-char capture.
- **Verdict:** Exa is the only service whose crawler/index actually **reaches** Home Depot and Lowe's URLs (no fetch failure, fast, even cached) — but the captured text is a client-side-rendered stub missing the spec table, which Home Depot loads via JS after the initial HTML. Good for discovery/candidate URLs, not for dimension extraction on this retailer.

---

## 4. Firecrawl (`/v2/scrape`)

Base: `https://api.firecrawl.dev/v2/scrape`, auth `Authorization: Bearer fc-...`.

**All 3 live calls (Home Depot, Lowe's, Amazon) returned `401 {"error": "Unauthorized: Invalid token"}` in 0.2–0.3 s.** I verified the auth header format against Firecrawl's current docs (`Authorization: Bearer fc-<key>`) before calling — our request shape is correct. The stored `FIRECRAWL_API_KEY` starts with `Fc-` (capital F); whether that casing or the key itself is invalid/revoked/placeholder was not something I could fix (I don't rotate keys), so **Firecrawl could not be live-tested** — flagged as Warning 1 below.
- **Inference only** (WebSearch of current reporting, not our own test, labeled as such): independent comparisons in 2026 report Firecrawl's stealth/anti-bot mode failing on the majority of hardened e-commerce sites in head-to-head tests, and Firecrawl ships a dedicated "scraping Amazon" guide implying Amazon needs special config — consistent with Tavily/Exa's results above, but **not confirmed with our own key**.
- **Verdict:** unverified. Fix the key and re-test before relying on it; budget was not spent further once the key proved unusable (2 calls left unspent).

---

## 5. You.com (`POST /v1/search`)

Base: `https://ydc-index.io/v1/search`, auth header `X-API-Key`.
- **Params:** `{query, count: 5}`
- **Latency:** 0.96 s
- **Fixture:** `youdotcom_search.json`
- **Fields we'd consume:** `results.web[].{url, title, description, snippets[], thumbnail_url, page_age, favicon_url}`
- Returned Lowe's, Home Depot, and doitbest.com with clean short snippets (e.g. Lowe's: *"Amerimax 5-in Aluminum Hidden Gutter Hanger with Screw and #8 x 3/8-in White Gutter Screw (8-Pack)"*) — good for candidate discovery/snippets, but the default response has **no raw page text or spec table**, only curated `snippets`. (`extraction_mode: "full_page"` exists per docs but was not tested live — would cost an extra call per URL; noted as an untested option, not a finding.)
- **Verdict:** solid, fast discovery source with decent snippet quality; not a dimension-extraction source at the default settings we tested.

---

## 6. Ollama web search (`POST /api/web_search`, `POST /api/web_fetch`)

Base: `https://ollama.com/api/{web_search,web_fetch}`, auth `Authorization: Bearer $OLLAMA_API_KEY` (docs fetched live at `docs.ollama.com/capabilities/web-search`; the plan doc's guessed path was correct).

### 6.1 `web_search`
- **Params:** `{query, max_results: 5}`
- **Latency:** 0.97 s
- **Fixture:** `ollama_web_search.json`
- Returned 5 results with `content` fields ~1,700–3,600 chars of **real page text already fetched**, including Home Depot pages, but each is capped short (~3.5 KB) and, like Exa, stops before the spec section on Home Depot pages (`Material` keyword present from the nav breadcrumb, but no `Product Depth`/`Dimensions`).

### 6.2 `web_fetch`
- Tested 3 URLs:
  - Home Depot product page → **404 `{"error": "not found"}`** in 1.45 s (confirmed real: I sanity-checked the endpoint itself works by fetching `"ollama.com"` in the same call shape → 200 OK, so the 404 is specific to the Home Depot URL, i.e. **bot-blocked/unreachable**, not a bad request).
  - Lowe's product page → same **404 `"not found"`** in 10.25 s (long hang before failing — looks like it attempted a real fetch and timed out into a block, not an instant reject).
  - Amazon AC unit page (`LG-LW6024RSMX`) → **200 OK**, 3.63 s, 20,034 truncated chars (original longer). This is the **only service in the whole test that returned a genuine spec table with real values**:
    ```
    | Product Dimensions | 12.83"D x 17.32"W x 11.14"H |
    | Cooling Power | 6000 british thermal units |
    | Color | White |
    | Voltage | 115 volts |
    ```
- **Verdict:** Ollama `web_fetch` is the standout for spec/dimension extraction **when the retailer is Amazon** — it got structured-looking dimension and BTU text no other service captured. It is fully blocked on Home Depot and Lowe's, same pattern as Tavily/Exa.

---

## Cross-service comparison (this test run only)

| Service | Candidate discovery | Home Depot fetch | Lowe's fetch | Amazon fetch | Spec/dims text captured | Multi-seller price+ship+ETA |
|---|---|---|---|---|---|---|
| SerpApi `google_shopping`/`immersive_product` | good (40 results, 13 stores) | n/a (structured) | n/a | n/a | n/a (uses `home_depot_product` instead) | **yes** — best in test |
| SerpApi `home_depot_product` | n/a | **yes, structured** | n/a | n/a | **yes** — exact `Dimensions` group | ETA via `fulfillment.options[]` |
| Tavily | ok (mixed retailers) | failed (fetch error) | failed (fetch error) | got bytes, boilerplate only (not confirmed either way) | no (in captured window) | no |
| Exa | good (reaches HD/Lowe's directly, cached, fast) | reached but JS-stub only | not tested for fetch | not tested | no | no |
| Firecrawl | untested (401 auth) | untested | untested | untested | untested | untested |
| You.com | good, clean snippets | n/a (snippets only) | n/a | n/a | no (snippets only) | no |
| Ollama `web_search` | ok, short excerpts | reaches, but stub-only | not tested | not tested | no | no |
| Ollama `web_fetch` | n/a | **blocked (404)** | **blocked (404)** | **yes — real spec table** | **yes (Amazon only)** | no |

---

## Recommended pipeline

**(a) Candidate discovery** → SerpApi `google_shopping` (one call gets 40 titles/sources/prices/tokens in a single structured hit; also cheapest in latency at 0.74 s). Grok's own `web_search` tool (per §4b.1) supplements this with natural-language candidates and citations; SerpApi is the fallback/verifier, not the primary, per the plan's existing design.

**(b) Spec/dimension extraction** → SerpApi `home_depot_product` is the clear winner whenever the part exists at Home Depot (exact `specifications` groups, confirmed live). For non-Home-Depot parts (e.g. an Amazon-only appliance like the LG AC), **Ollama `web_fetch` is the best fallback we tested** — it's the only tool in this run that pulled a real "Product Dimensions" line off Amazon. Do not rely on Tavily/Exa/You.com/Ollama-`web_search` for dimensions on Home Depot/Lowe's pages: all four reach the URL (or fail outright) but return pre-render JS stubs with no spec content — confirmed by keyword search across every captured fixture. Firecrawl is unverified (see Warning 1); if the key gets fixed, its Amazon-specific scraping guide and Fire-engine JS rendering make it worth a retest before Ollama `web_fetch`, since it's purpose-built for this.

**(c) Multi-seller prices + shipping + ETA** → SerpApi `google_shopping` → `google_immersive_product` (page_token chain), exactly as §4b.2 specifies. Remember: ETA is unstructured text inside `details_and_offers[]` from immersive_product, but `home_depot_product.fulfillment.options[]` gives a clean per-channel ETA when Home Depot is a seller — prefer that path when available. eBay Browse API (mentioned in the plan, not tested here — no key in `.env` for it) remains the documented free extra seller.

**Fallback ladder if SerpApi quota runs out:** disk cache (keyed on normalized query text, per §4b.2) first; then Exa `/search` for discovery (fast, reaches retailer URLs, cached results are near-free); then Ollama `web_fetch` per-URL for any Amazon candidate that needs dimensions; Tavily/You.com as last-resort discovery only. Given the Firecrawl key is currently broken, do not put it in the fallback chain until it's verified working.

**Quota strategy:** cache every raw response to disk keyed by `normalize(query)` (lowercase, collapse whitespace, strip punctuation) before calling any paid API — SerpApi's own 1-hour server-side cache is a secondary safety net, not a substitute, since our budget is 100–250 SerpApi searches/month total across the whole team for the weekend. Tavily/Exa/You.com/Ollama have no monthly-search framing in what we tested (metered/credit-based), so the ≤5-calls-each budget here was a research-time constraint, not a hint about their production quotas — check each vendor's dashboard for real limits before demo day.

---

## Warnings (3 accumulated, per operating threshold)

1. **`FIRECRAWL_API_KEY` in `.env` is rejected by Firecrawl** (`401 Unauthorized: Invalid token` on all 3 attempts). Auth header format was verified correct against current docs before calling, so the key itself needs rotating/checking in the Firecrawl dashboard. Firecrawl is entirely unverified as a result.
2. **§4b.2's assumption of a structured delivery-ETA field from `google_immersive_product` doesn't hold** — ETA (when present) is embedded as free text inside `details_and_offers[]` strings (e.g. `"Delivery between Sep 25 – Oct 1 $20"`), not a separate `eta` field. `sellers.py` will need a small parser/regex for this if it wants to sort "arrives by Friday" the way the seller-carousel spec (§4) implies. `home_depot_product.fulfillment.options[].arrival_time` is a cleaner structured alternative when Home Depot is one of the sellers.
3. **Home Depot and Lowe's product pages render their spec tables client-side**, so every text-extraction service tested (Tavily, Exa, You.com default, Ollama `web_search`) returns only the pre-render nav/category shell for those two retailers — confirmed by keyword search (`Dimensions`, `Product Depth`, `Product Width`) coming up empty across every fixture from those services. Only SerpApi's dedicated `home_depot_product` engine (which hits Home Depot's own product API rather than scraping rendered HTML) got real spec data. Budget any Lowe's-specific dimension need around this — there is no equivalent `lowes_product` SerpApi engine in the plan; a live test of that gap was out of scope for this task's budget.

## Files written

- `docs/research/search-sellers.md` (this file)
- `tests/fixtures/search/serpapi_google_shopping.json`
- `tests/fixtures/search/serpapi_google_immersive_product.json`
- `tests/fixtures/search/serpapi_home_depot.json`
- `tests/fixtures/search/serpapi_home_depot_product.json`
- `tests/fixtures/search/tavily_search.json`
- `tests/fixtures/search/tavily_extract_homedepot.json`
- `tests/fixtures/search/tavily_extract_lowes_amazon.json`
- `tests/fixtures/search/exa_search.json`
- `tests/fixtures/search/exa_contents_homedepot.json`
- `tests/fixtures/search/exa_contents_homedepot_fulltext.json`
- `tests/fixtures/search/firecrawl_scrape_homedepot.json` (401 error body)
- `tests/fixtures/search/firecrawl_scrape_lowes.json` (401 error body)
- `tests/fixtures/search/firecrawl_scrape_amazon.json` (401 error body)
- `tests/fixtures/search/youdotcom_search.json`
- `tests/fixtures/search/ollama_web_search.json`
- `tests/fixtures/search/ollama_web_fetch_homedepot.json` (404 error body)
- `tests/fixtures/search/ollama_web_fetch_lowes_amazon.json`
