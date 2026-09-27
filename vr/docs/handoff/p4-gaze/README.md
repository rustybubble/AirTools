# Backend hand-off: the catalog follows what you look at (`GET /catalog?focus=`)

*Sun 2026-09-27, app gaze-catalog lane. One patch on top of `catalog-on-asset-mode` @ `42c0899` (what `:8004` runs),
made and tested in the scratch worktree `backend-gaze` (branch `gaze-catalog`) on `:8010` with a scratch `DATA_DIR`
seeded from `~/airtools-backend-demo/data`. Nothing changed on `:8004` or in `~/airtools-backend-demo`'s checkout, and
nothing was pushed.*

The user's words: "For the case of the catalog for Zabel and Hospital, the catalog should be fully dynamic based on the
current scene it is looking at (if possible/not computationally expensive). If looking at the roof of Zabel, it would
suggest panels, gutters, etc. If looking at the walls, it would suggest wall mounted hvacs, window frames, etc."

The app works out the focus on the headset (no LLM per glance). About 4 times a second, the centre eye's ray is
classified in the scan's own up frame. A focus has to hold for about 1.2 s. Only then does the app send
`GET /catalog?site=…&focus=roof|wall|ground|ceiling|counter|opening`. It keeps a copy per (site, focus). The app side is
on `feat/gaze-catalog`.

## How to apply (the :8004 steps)

```sh
cd ~/airtools-backend-demo                     # app-handoff-vrnext @ 42c0899 = catalog-on-asset-mode
git merge --ff-only gaze-catalog               # the branch is in this repo; or: git am <app>/docs/handoff/p4-gaze/*.patch
uv run pytest -q tests/test_catalog_focus.py tests/test_catalog.py tests/test_warm.py   # 76 passed
# The warm's results, so :8004 doesn't buy them again (60 SerpApi calls). -k keeps every file :8004 already has:
tar -xzkf /private/tmp/claude-501/-Users-ravil-AirTools/gaze-logs/gaze-warm-delta.tgz -C ~/airtools-backend-demo/data
#   (or re-run: uv run python -m server.warm --catalog --sites zabel-gymnasium,hospital-bg,kitchen --no-models)
# restart uvicorn on :8004, then:
curl -s 'http://localhost:8004/catalog?site=zabel-gymnasium&focus=roof' | jq -c '[.focus, .foci, [.categories[:5][].id], .took_ms]'
curl -s 'http://localhost:8004/catalog?site=zabel-gymnasium&focus=wall' | jq -c '[.focus, [.categories[:5][].id]]'
```

- No new dependency, env var or data migration. The stored detections (`data/catalog/*.json`, `VERSION` 3) stay valid.
- With no `focus`, or one the site has no table for (a counter outside, a roof in a kitchen), the answer is today's list
  byte for byte, except for two new keys: `focus` (null) and `foci`.
- Full suite: 1544 passed, 1 skipped and 3 failed. The 3 failures are `tests/pipeline/test_structure_*`, which fail the
  same way on untouched `42c0899` (1529 passed there).
- The tarball has 162 files: 60 `cache/serpapi`, 37 `cache/web`, 7 + 7 `cache/search*`, 1 `cache/catalog_empty`
  ("cordless blinds" found nothing) and 50 `parts/*` (`part.json` + `image.jpg`). The file list is in
  `/private/tmp/claude-501/-Users-ravil-AirTools/gaze-logs/warm-delta.txt`.

## The patch

`0001-catalog-gaze-focus-…`:

- **Tables** (`catalog_tables.py`): `FOCUS_TABLES` gives, for each setting and focus, the categories to lead with, in
  order.
  - Settings:
    - `exterior`: rooftop, facade and pavilion. Zabel and the hospital are "facade".
    - `kitchen`.
    - `room`: bathroom, laundry, garage, interior, gym and hospital room. A room keeps its own environment's categories
      and the shared ones (`ROOM_SHARED`).
  - Exterior lists:
    - **roof**: solar panels, gutters, roof vents, HVAC, skylights, chimney caps, flashing, snow guards, satellite mounts.
    - **wall**: wall-mounted HVAC, windows & frames, window AC, doors, siding, exterior lights, wall vents, downspouts.
    - **ground**: pavers, planters, benches, drainage, picnic tables, outdoor lighting, HVAC (a condenser on its pad).
    - **ceiling**: exterior lights, ceiling fans, siding (soffit).
    - **opening**: windows, doors, window AC, blinds.
  - Kitchen lists:
    - **counter**: microwaves, sinks & faucets, cooktops, backsplash, lighting, cabinet hardware.
    - **ground** (the floor): dishwashers, fridges, ranges, cabinet hardware.
    - **wall**: range hoods, microwaves, cabinet hardware, lighting, outlets, backsplash, shelving.
    - **ceiling**: ceiling lights, smoke detectors, range hoods, ceiling fans.
    - **opening**: windows, blinds, doors.
  - `normalize_focus` accepts aliases (floor → ground, window / door → opening, …). `focus_setting`, `focus_ids`,
    `foci_for`.
  - `foci_of_text` puts one of Grok's site extras under a focus by its words. A roof word wins alone, so Zabel's
    "Red Tile Roofs" and "Dormer Windows" go on the roof.
- **New categories**: wall-hvac (mini-split heads, PTAC), wall-vents, downspouts, snow-guards, satellite-mounts, pavers,
  drainage and backsplash.
  - Gutters no longer take the word "downspout", so a downspout lands in its own category.
  - Every existing category keeps exactly the items it had (checked category by category against the index of the
    demo data).
- **`catalog.focus_categories(det, focus)`** builds the focused list:
  - the curated list, in its order, then the site's Grok extras whose words put them there;
  - in-scene evidence as today;
  - `None` when there is no table, so the caller shows the unfocused list.
- **`categories_for(det, focus=None)`** delegates to it. **`catalog.build(site, session_id, focus)`**:
  - Stocked categories come first, as a stable sort, so the table's order holds among them.
  - Only the first `FOCUS_SEARCH_LEAD` = 6 thin categories are searched in the background, to save SerpApi credit.
  - The answer carries `focus` (what was applied, or null) and `foci` (what the site answers).
- **`GET /catalog?focus=`** (`app.py`).
- **Warm**: `python -m server.warm --catalog` also fills each focus variant a site answers, using the same lead of 6.
  - A category is warmed once per run however many lists show it.
  - `--no-focus` skips the variants; `--max-searches N` caps the searches (the rest print "over budget").
- **Tests**: `tests/test_catalog_focus.py` has 15 tests:
  - the tables' integrity, the user's two examples leading, the kitchen, rooms, aliases, extras' foci, and new
    categories not stealing items;
  - Zabel's roof and wall order, today's list with no or an unknown focus, the hospital's extras on the roof;
  - the endpoint's order (stocked first), the lead-only searches, the kitchen floor and counter;
  - under 10 ms from the cache;
  - warm: the focus variants within a budget, idempotent.
  - `tests/test_catalog.py` was adapted: the old warm test runs with `focus=False`, and the CLI test covers the new flags.

## Probe on :8010 after the warm (`probe.txt`, `warm.txt`)

The first 5 categories as "title:items". `took_ms` is the server's own time; `http` is the full round trip on
localhost. The detection is memoised after a site's first request.

| Site | focus | Applied | First 5 | took_ms | http ms |
|---|---|---|---|---|---|
| zabel-gymnasium | – | – | Windows & frames:3, Doors:1, Siding:2, Gutters & hangers:4, Exterior lights:4 | 0.6 | 1.1 |
| | roof | roof | Solar panels:4, Gutters & hangers:4, Roof vents:4, HVAC units:1, Skylights:4 | 0.7 | 1.1 |
| | wall | wall | Wall-mounted HVAC:4, Windows & frames:3, Window AC units:4, Doors:1, Siding:2 | 0.5 | 0.9 |
| | ground | ground | Pavers:4, Planters:4, Benches:1, Drainage:4, Picnic tables:4 | 0.5 | 0.9 |
| | ceiling | ceiling | Exterior lights:4, Ceiling fans:2, Siding:2 | 0.4 | 0.7 |
| | counter | – (none outside) | today's list | 0.6 | 0.9 |
| | opening | opening | Windows & frames:3, Doors:1, Window AC units:4, Blinds & shades:0 | 0.4 | 0.6 |
| hospital-bg | – | – | Windows & frames:3, Doors:1, Siding:2, Gutters & hangers:4, Exterior lights:4 | 0.9 | 1.2 |
| | roof | roof | Solar panels:4, Gutters & hangers:4, Roof vents:4, HVAC units:1, Skylights:4 | 0.5 | 0.8 |
| | wall | wall | Wall-mounted HVAC:4, Windows & frames:3, Window AC units:4, Doors:1, Siding:2 | 0.4 | 0.8 |
| | ground | ground | Pavers:4, Planters:4, Benches:1, Drainage:4, Picnic tables:4 | 0.5 | 0.8 |
| kitchen | – | – | Fridges:8, Dishwashers:8, Ranges & ovens:6, Microwaves:4, Range hoods:4 | 0.9 | 1.3 |
| | roof | – (none indoors) | today's list | 0.9 | 1.3 |
| | counter | counter | Microwaves:4, Sinks & faucets:1, Cooktops:4, Backsplash tile:3, Lighting:3 | 0.6 | 0.9 |
| | ground | ground | Dishwashers:8, Fridges:8, Ranges & ovens:6, Cabinet hardware:8 | 0.7 | 1.2 |
| | wall | wall | Range hoods:4, Microwaves:4, Cabinet hardware:8, Lighting:3, Outlets & switches:4 | 0.6 | 0.9 |
| | ceiling | ceiling | Ceiling lights:4, Smoke detectors:4, Range hoods:4, Ceiling fans:2 | 0.4 | 0.7 |

The Zabel roof list continues: chimney caps, Red Tile Roofs (Grok's extra), windows (Grok's dormer windows), then the
empty ones (flashing, snow guards, satellite mounts).

**Warm spend** (`--sites zabel-gymnasium,hospital-bg,kitchen --no-models`):

- 8 searches: skylight, chimney cap, ductless mini split, patio paver, channel drain, ceiling fan, cordless blinds (found
  nothing) and backsplash tile.
- **60 SerpApi calls**, so about 294 of the 354 are left. 37 web (Exa) calls, 42 LLM calls, **$0.05**. 195 s.
- The first-page items come from the existing cache. Among the new categories' parts, the photos are fetched but the 3D
  models aren't built (`--no-models`). On `:8004`, the first 3 per category are built in the background the first time
  the app shows that list (the existing catalog behaviour).
