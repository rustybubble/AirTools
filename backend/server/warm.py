"""Demo warm-up CLI (plan §4b.5, docs "AirTool final writeup.md" §9 risks: "Everything the
scripted demo needs is cached locally; only the live search is lost").

    uv run python -m server.warm [--queries-file demo.json] [--retry-proxies] [--grok-moulded]
    uv run python -m server.warm --labels <site> [--every 4]
    uv run python -m server.warm --hf [--hf-limit N] [--hf-no-wait]
    uv run python -m server.warm --catalog [--sites kitchen,zabel-gymnasium] [--no-models]
                                 [--no-focus] [--max-searches N]
    uv run python -m server.warm --scad [--sites kitchen] [--scad-match window] [--scad-jobs 3]
        [--scad-parts id,id] [--no-demo-parts] [--retry-failed] [--estimate]
    uv run python -m server.warm --retexture [--parts id,id] [--match fridge] [--modes auto,hf,llm_scad]
    uv run python -m server.warm --scad --remake-flat [--estimate]

For each `{"query": ..., "measurement": {...}}` entry: runs the same `find_parts` +
`resolve_asset` path `jobs.py` runs for a live search (sequential -- `assets.py` rate-limits the
AI-mesh tier globally, same reason `jobs._resolve_assets` is sequential), then prints a compact
table (id, name, dims, fit, sellers, asset tier). Also pre-generates TTS audio, via `voice.speak`
(which checks the "tts" disk cache before calling any provider -- see server/voice.py), for the
agent's fixed canned replies and for the spoken summary of each warmed job, so offline TTS has
audio for those lines too.

`--grok-moulded` also runs the offline Grok OpenSCAD job (`meshgen.grok_scad`, E4: minutes and
~50K reasoning tokens per part) for every warmed part the asset call tagged moulded/organic, and
re-resolves those parts so they pick up the new `scad.glb`. Never on the request path.

`--labels <site>` does only the live-labels warm instead (`labels.label_scene`): Grok labels
every Nth thumb of the scan (~$0.001 each), the labels are lifted onto the collision mesh and
merged, and `GET /scenes/<site>/labels` serves the result. The frames are cached by hash, so a
live `POST /scene/labels` of a warmed thumb is free and works OFFLINE.

`--hf` (asset-mode) does only the HF warm instead: every part already on disk (data/parts/*) gets
its Hunyuan3D image-to-3D model (`model.hf.glb`, `assets.warm_hf`), one at a time; a part that has
one is skipped (HF meshes are made once and kept, the raw mesh cached by photo hash). When every
HF token's and the IP's ZeroGPU quota is spent it waits (5 min, doubling to 1 h) and goes on, or
stops with `--hf-no-wait`. The model each part shows is unchanged.

`--catalog` fills every served site's catalog instead (`catalog.warm`): one cached Grok call per
site names the place, every category with fewer than 3 known parts is searched (lite: no store
lookups), the first 3 parts per category get their models and every listed part its photo. It
also fills each focus variant the site answers (GET /catalog?focus=roof|wall|...: the first
thin categories of each list; `--no-focus` skips them); `--max-searches N` caps the searches. It
prints each site's categories and the SerpApi calls, LLM calls and dollars it spent.

`--scad` (cad) makes Grok's OpenSCAD model (`meshgen.grok_scad_run`, role `asset_scad` =
LLM_ASSET_SCAD) for every catalog item that has a model (every served site, or `--sites`) plus
the demo's key parts (`SCAD_DEMO_PARTS`: the kitchen dishwashers, the window frames, the gutter
hangers), `--scad-jobs` at once (default 3), then rebuilds each part's llm_scad variant so the
headset's LLM+CAD mode serves it at once. Resumable: a part with scad.glb is skipped, a part
whose run failed (scad.failed.json: why, model, cost) too unless `--retry-failed`, and one
another process is writing. Prints the plan with an estimate first (`--estimate`: only that),
one row per part, and the totals; the run's rows land in <DATA_DIR>/scad_warm.json. The model
each part shows is unchanged.

`--retexture` (texture) re-textures the models already on disk (every part's auto / hf / llm_scad
variants, or `--parts` / `--match`) with today's texturing: the product cut out of its photo
(image.cut.png), the photo on the front footprint only, the sides and top in the product's sampled
colour with a material cue, and a CAD model built backwards turned around. The geometry is reused
(scad.glb, the raw Hunyuan mesh cached by photo hash, the template from the cached plan, the box);
it runs OFFLINE, so it makes no LLM, Hunyuan or search call: $0. The U^2-Netp cut-out model (4.6
MB, free) is fetched first if it isn't in <DATA_DIR>/cache/models. Prints one row per variant and
the totals; the rows land in <DATA_DIR>/retexture.json.

Idempotent: `find_parts` (whole-result cache), `resolve_asset` (skips re-resolving a part whose
model.glb + "ready" part.json already exist) and `voice.speak` (tts cache) all check their own
disk cache first, so re-running this after a successful warm costs nothing.

IMPORTANT -- query wording: `DEFAULT_QUERIES` must use the exact, already-normalized query
strings the agent's `find_part` tool call actually emits for the scripted demo phrases (e.g. the
LLM turns "find a hanger for this gutter" into "gutter hanger") -- `find_parts`' cache key is
`cache.normalize(query)` plus the measurement, so a live query worded differently is a cache
miss even if it means the same thing. Verify these against a real agent transcript/log (or
`docs/AirTool final writeup.md` §3's demo script) before changing them.
"""

import argparse
import asyncio
import json
import logging
import time
from pathlib import Path

import trimesh

from server import agent, assets, bom, cache, catalog, jobs, labels, meshgen, search, voice
from server.config import get_settings
from server.models import Job, Measurement, Part, SearchRequest

logger = logging.getLogger(__name__)

# The gutter array beat sets 8 hangers -- warm the "what else do I need?" BOM for exactly that
# count so the offline demo's agent fast path has a cached answer (see `warm_bom`).
_GUTTER_QUERY = "gutter hanger"
_GUTTER_ARRAY_QTY = 8
# Fast-path phrasings of the scripted demo beats (docs/api.md §5); their replies get TTS-warmed.
DEMO_COMMANDS = [
    "select the first",
    "cheapest first",
    "fastest first",
    "every 60 cm",
    "what else do I need?",
    "equip tape",
]

DEFAULT_QUERIES: list[dict] = [
    {"query": "gutter hanger", "measurement": {"label": "gutter width", "value_m": 0.127}},
    {
        "query": "window air conditioner",
        "measurement": {"label": "window width", "value_m": 0.76, "axis": "w"},
    },
]

_COLUMNS = ("id", "name", "dims (mm)", "fit", "sellers", "asset")


def _row(part: Part) -> tuple[str, ...]:
    dims = f"{part.dims_mm.w:.0f}x{part.dims_mm.d:.0f}x{part.dims_mm.h:.0f}"
    return (part.id, part.name, dims, part.fit.status, str(len(part.sellers)), part.asset.tier)


def _print_table(rows: list[tuple[str, ...]]) -> None:
    if not rows:
        print("  (no candidates)")
        return
    widths = [max(len(h), *(len(r[i]) for r in rows)) for i, h in enumerate(_COLUMNS)]
    line = lambda row: "  ".join(cell.ljust(w) for cell, w in zip(row, widths, strict=True))
    print("  " + line(_COLUMNS))
    for row in rows:
        print("  " + line(row))


async def warm_query(entry: dict, retry_proxies: bool = False) -> list[Part]:
    """find_parts (whole-result cached) -> resolve_asset per candidate, sequential -- the same
    two calls `jobs._run_search`/`jobs._resolve_assets` make for a live search. `retry_proxies`
    re-runs the asset tiers for parts that ended up a proxy box (e.g. AI-mesh quota ran out)."""
    measurement = Measurement(**entry["measurement"]) if entry.get("measurement") else None
    req = SearchRequest(query=entry["query"], measurement=measurement)
    parts = await search.find_parts(req)
    resolved = []
    for part in parts:
        old = jobs.load_part(part.id) if retry_proxies else None
        if old and old.asset.tier == "proxy":
            (assets.part_dir(part.id) / "model.glb").unlink(missing_ok=True)
        part.asset.status = "pending"
        jobs.save_part(part)
        part = await assets.resolve_asset(part)
        resolved.append(part)
    return resolved


async def warm_grok_moulded(parts: list[Part]) -> list[Part]:
    """Grok OpenSCAD for the moulded/organic parts among `parts` (the asset call's cached answer
    says which; asking again is a cache hit), then re-resolve each one that got a `scad.glb`."""
    out = []
    for part in parts:
        pdir = assets.part_dir(part.id)
        photo = pdir / "image.jpg"
        plan = await meshgen.ask(part, photo) if photo.exists() else None
        if (
            plan is not None
            and plan.shape_class != "template"
            and await meshgen.grok_scad(part, photo, pdir / "scad.glb")
            and part.asset.tier != "scad"
        ):
            (pdir / "model.glb").unlink(missing_ok=True)
            part.asset.status = "pending"
            jobs.save_part(part)
            part = await assets.resolve_asset(part)
        out.append(part)
    return out


async def warm_bom(warmed: dict[str, list[Part]]) -> None:
    """Pre-cache `what_else` for the first gutter-hanger candidate x `_GUTTER_ARRAY_QTY` -- the
    array beat sets that many hangers, so the agent's offline "what else do I need?" fast path
    needs a cached answer for exactly that count (see server/bom.py's whole-result cache)."""
    gutter_parts = warmed.get(_GUTTER_QUERY) or []
    if not gutter_parts:
        return
    part = gutter_parts[0]
    print(f"  what else for {part.id!r} x{_GUTTER_ARRAY_QTY}")
    result = await bom.what_else([part], {part.id: _GUTTER_ARRAY_QTY})
    if not result.lines:
        print("    (no bom items)")
        return
    for line in result.lines:
        print(f"    {line.name} x{line.qty}  {line.seller.name if line.seller else '-'}")
    print(f"    total: ${result.total_usd:.2f}")


async def warm_demo_replies(warmed: dict[str, list[Part]]) -> set[str]:
    """Run the scripted demo's fast-path commands through the real agent (no LLM call; show
    sellers expands the demo part's sellers once) and return each spoken reply for TTS."""
    gutter_parts = warmed.get(_GUTTER_QUERY) or []
    if not gutter_parts:
        return set()
    part = gutter_parts[0]
    ctx = {
        "selected_part_id": part.id,
        "candidate_ids": [p.id for p in gutter_parts],
        "placed": [{"part_id": part.id, "count": _GUTTER_ARRAY_QTY}],
    }
    replies = set()
    for text in DEMO_COMMANDS:
        result = await agent.handle_command("warm", text, ctx)
        print(f"  {text!r} -> {result['reply']!r}")
        replies.add(result["reply"])
    return replies


async def warm_tts(warmed: dict[str, list[Part]], extra: set[str] = frozenset()) -> None:
    """TTS for the agent's fixed canned replies, each warmed job's spoken summary and `extra`."""
    lines = {*agent._CANNED_REPLY.values(), agent.OFFLINE_REPLY, jobs.OFFLINE_NO_CANDIDATES, *extra}
    for query, parts in warmed.items():
        job = Job(id="warm", status="done", stage="done", query=query, candidates=parts)
        lines.add(jobs.summary(job))
    for line in sorted(lines):
        try:
            await voice.speak(line)
        except voice.VoiceError as exc:
            print(f"  tts warm failed for {line!r}: {exc}")


async def warm_labels(site: str, every: int) -> None:
    print(f"labels: {site}, every {every}th thumb")
    out = await labels.label_scene(site, every)
    for a in out["labels"]:
        pos = ", ".join(f"{v:.2f}" for v in a["pos"])
        print(f"  {a['id']:>4}  {a['kind']:<13} {a['name']:<28} [{pos}]  x{len(a['frames'])}")
    print(
        f"  {len(out['labels'])} anchors from {out['frames']} frames, {out['unpinned']} labels "
        f"missed the mesh, ${out['cost_usd']:.4f}"
    )


HF_BACKOFF_START_S = 300.0
HF_BACKOFF_MAX_S = 3600.0


def cached_parts() -> list[Part]:
    """Every part on disk (data/parts/<id>/part.json), by id."""
    root = assets.part_dir("x").parent
    parts = []
    for pj in sorted(root.glob("*/part.json")) if root.is_dir() else []:
        part = jobs.load_part(pj.parent.name)
        if part is not None:
            parts.append(part)
    return parts


async def warm_hf(limit: int | None = None, wait: bool = True, sleep=asyncio.sleep) -> dict:
    """asset-mode: the Hunyuan model for every cached part, sequential, backing off on the ZeroGPU
    quota. {outcome: count}; prints one row per part (id, outcome, tier, made_by, seconds)."""
    counts: dict[str, int] = {}
    backoff = 0.0
    parts = cached_parts()[: limit or None]
    print(f"hf: {len(parts)} cached parts")
    i = 0
    while i < len(parts):
        part = parts[i]
        t0 = time.monotonic()
        outcome = await assets.warm_hf(part)
        took = time.monotonic() - t0
        if outcome == "quota":
            msg = (
                assets.HF_QUOTA_SEEN[-1]["message"]
                if assets.HF_QUOTA_SEEN
                else "all HF keys cooling"
            )
            if not wait:
                print(f"  {part.id}: ZeroGPU quota spent ({msg}); stopping (--hf-no-wait)")
                counts["quota"] = counts.get("quota", 0) + 1
                break
            backoff = min(max(backoff * 2, HF_BACKOFF_START_S), HF_BACKOFF_MAX_S)
            print(f"  {part.id}: ZeroGPU quota spent ({msg}); waiting {backoff:.0f} s")
            await sleep(backoff)
            assets.HF_KEYS.reset()  # try the tokens again after the wait
            continue
        backoff = 0.0
        counts[outcome] = counts.get(outcome, 0) + 1
        asset = assets.variant_asset(part.id, "hf")
        tier = asset.tier if asset else "-"
        by = (asset.made_by if asset else None) or "-"
        print(f"  {part.id:<60} {outcome:<9} {tier:<8} {by:<32} {took:6.1f} s")
        i += 1
    print(f"hf: {counts}")
    return counts


# cad: the demo's key parts, CAD first (the kitchen dishwasher the replace job swaps, its
# runner-ups, the window frames, the gutter hangers); those not on disk are skipped.
SCAD_DEMO_PARTS = [
    "frigidaire-fdpc4221as-341c87",
    "frigidaire-fdpc4314as-87a659",
    "whirlpool-wdp540hamz-a67f69",
    "jeld-wen-thdjw235100032-d0809e",
    "jeld-wen-thdjw140400129-e52d6c",
    "jeld-wen-thdjw138500119-d5f663",
    "amerimax-home-products-21812-846830",
    "amerimax-home-products-m0722b-d6dfa5",
]


async def scad_parts(
    sites: list[str] | None = None,
    ids: list[str] | None = None,
    match: str | None = None,
    demo: bool = True,
) -> list[Part]:
    """cad: the parts `--scad` works on, in order: `ids`, the demo's key parts, then every
    catalog item with a model (model.glb) for `sites` (default: every served site), each once;
    `match` keeps those whose id or name holds it (case-insensitive)."""
    order: list[str] = list(ids or [])
    if demo:
        order += SCAD_DEMO_PARTS
    catalog.INDEX.load()
    for site in sites or catalog.served_sites():
        try:
            det = await catalog.detect(site, wait_s=None)
        except LookupError:
            print(f"  {site}: not a served site")
            continue
        for cat, _ in catalog.categories_for(det):
            for doc in catalog.INDEX.in_category(cat)[: catalog.MAX_ITEMS]:
                if catalog.model_ready(doc.part_id):
                    order.append(doc.part_id)
    parts = []
    for pid in dict.fromkeys(order):
        part = jobs.load_part(pid)
        if part is None:
            continue
        if match and match.lower() not in f"{part.id} {part.name}".lower():
            continue
        parts.append(part)
    return parts


def set_aside_flat(parts: list[Part], dry_run: bool = False) -> list[str]:
    """texture: the appliances whose CAD front is one flat slab (meshgen.front_relief under
    RELIEF_MIN: handles and controls painted flush, so the front reads as a back). Their scad.glb
    and scad.json move to scad.flat.glb / scad.flat.json (kept to roll back), so `--scad` makes
    them again with the front-and-back prompt and the relief pass. `dry_run`: only list them."""
    flat = []
    for part in parts:
        out = assets.scad_path(part.id)
        if not out.exists() or not meshgen.wants_relief(part):
            continue
        try:
            relief = meshgen.front_relief(trimesh.load(out, force="scene"))
        except Exception:  # noqa: BLE001 -- an unreadable GLB: made again too
            relief = 0.0
        if relief >= meshgen.RELIEF_MIN:
            continue
        flat.append(part.id)
        if not dry_run:
            out.replace(out.with_name("scad.flat.glb"))
            rec = meshgen.scad_sidecar(out, "json")
            if rec.exists():
                rec.replace(out.with_name("scad.flat.json"))
    return flat


def scad_estimate(todo: int, jobs_n: int) -> dict:
    """cad: {seconds, usd} to make `todo` CAD models `jobs_n` at a time with the configured
    model and effort (meshgen.SCAD_TYPICAL)."""
    model, profile = meshgen.scad_model(), meshgen.scad_profile()
    each_s = meshgen.scad_eta_s(profile)
    usd = meshgen.scad_cost_usd(profile)
    return {
        "model": model,
        "profile": profile,
        "parts": todo,
        "seconds": round(each_s * -(-todo // max(1, jobs_n)), 0),
        "usd": round(usd * todo, 3),
        "each_s": each_s,
        "each_usd": usd,
    }


def _scad_log_path() -> Path:
    return Path(get_settings().DATA_DIR) / "scad_warm.json"


async def warm_scad(
    parts: list[Part],
    jobs_n: int = 3,
    retry_failed: bool = False,
    estimate_only: bool = False,
) -> dict:
    """cad: Grok's OpenSCAD model for each part, `jobs_n` at a time, then its llm_scad variant
    rebuilt (not selected). {outcome: count, seconds, usd, ...}; one row per part; resumable."""
    model = meshgen.scad_model()
    exe = meshgen.openscad()
    todo, skip = [], {}
    for part in parts:
        out = assets.scad_path(part.id)
        if out.exists():
            skip[part.id] = "exists"
        elif meshgen.scad_writing(out) is not None:
            skip[part.id] = "writing"
        elif meshgen.scad_failure(out) is not None and not retry_failed:
            skip[part.id] = "failed_before"
        else:
            todo.append(part)
    est = scad_estimate(len(todo), jobs_n)
    print(
        f"scad: {len(parts)} parts, {len(todo)} to make with {est['profile']} ({jobs_n} at a time), "
        f"{sum(v == 'exists' for v in skip.values())} made, "
        f"{sum(v == 'failed_before' for v in skip.values())} failed before"
        + ("" if retry_failed else " (--retry-failed)")
        + f"; estimate ~{est['seconds'] / 60:.0f} min, ~${est['usd']:.2f}"
        f" ({est['each_s']:.0f} s, ${est['each_usd']:.3f} a part)"
    )
    counts: dict = {"exists": 0, "failed_before": 0, "writing": 0}
    for v in skip.values():
        counts[v] += 1
    if estimate_only or exe is None:
        if exe is None:
            print("scad: OpenSCAD isn't installed (openscad on PATH): nothing made")
        return {**counts, "estimate": est}
    log = _scad_log_path()
    rows: dict = {}
    try:
        rows = json.loads(log.read_text()) if log.exists() else {}
    except ValueError:
        rows = {}
    gate = asyncio.Semaphore(max(1, jobs_n))
    t_all = time.monotonic()
    usd = 0.0

    async def one(part: Part) -> None:
        nonlocal usd
        pdir = assets.part_dir(part.id)
        photo = pdir / "image.jpg"
        async with gate:
            if not photo.exists() and not (
                part.image_url and await assets.fetch_image(part.image_url, photo)
            ):
                outcome, rec = "no_photo", {}
            else:
                rec = await meshgen.grok_scad_run(part, photo, assets.scad_path(part.id))
                outcome = "made" if rec.get("ok") else "failed"
        if outcome == "made":
            try:
                got = await assets.resolve_asset(
                    part, mode="llm_scad", select_it=False, rebuild=True
                )
                variant = got.asset.tier
            except Exception as exc:  # noqa: BLE001 -- the CAD model stays; the server rebuilds
                logger.warning("scad warm: %s variant failed: %s", part.id, exc)
                variant = "-"
        else:
            variant = "-"
        usd += rec.get("cost_usd") or 0.0
        counts[outcome] = counts.get(outcome, 0) + 1
        row = {
            "outcome": outcome,
            "model": rec.get("model", model),
            "seconds": rec.get("seconds"),
            "calls": rec.get("calls"),
            "fixes": rec.get("fixes"),
            "cost_usd": rec.get("cost_usd"),
            "bbox_err_pct": rec.get("bbox_err_pct"),
            "variant": variant,
            "error": rec.get("error"),
            "at": time.time(),
        }
        rows[part.id] = row
        cache.write_json_atomic(log, json.dumps(rows, indent=2))
        print(
            f"  {part.id[:58]:<58} {outcome:<8} {row['calls'] or 0:>2} calls"
            f" {row['seconds'] or 0:6.1f} s ${row['cost_usd'] or 0:.4f}"
            f" bbox {row['bbox_err_pct'] if row['bbox_err_pct'] is not None else '-'}%"
            f" variant={variant}" + (f"  ({str(row['error'])[:70]})" if row["error"] else ""),
            flush=True,
        )

    await asyncio.gather(*(one(p) for p in todo))
    took = time.monotonic() - t_all
    made = counts.get("made", 0)
    print(
        f"scad: {counts} in {took / 60:.1f} min, ${usd:.3f}"
        + (f" (${usd / max(1, len(todo)):.4f} and {took / max(1, len(todo)):.1f} s a part)")
    )
    return {**counts, "seconds": round(took, 1), "usd": round(usd, 4), "made": made}


RETEXTURE_MODES = ("auto", "hf", "llm_scad")


async def warm_retexture(
    ids: list[str] | None = None, match: str | None = None, modes=RETEXTURE_MODES
) -> dict:
    """texture: `assets.retexture` for every variant of every part on disk (or `ids`; `match`
    keeps those whose id or name holds it). {outcome: count, seconds, turned, parts}; one row per
    variant, the rows in <DATA_DIR>/retexture.json."""
    parts = [p for p in (jobs.load_part(i) for i in ids) if p] if ids else cached_parts()
    if match:
        parts = [p for p in parts if match.lower() in f"{p.id} {p.name}".lower()]
    print(
        f"retexture: {len(parts)} parts, modes {', '.join(modes)} (offline: no LLM/Hunyuan calls)"
    )
    counts: dict = {}
    rows: dict = {}
    turned: list[str] = []
    t_all = time.monotonic()
    for part in parts:
        for mode in modes:
            res = await assets.retexture(part, mode)
            if res["outcome"] == "none":
                continue
            counts[res["outcome"]] = counts.get(res["outcome"], 0) + 1
            tex = res.get("texture") or {}
            if tex.get("turned"):
                turned.append(f"{part.id} ({mode})")
            rows.setdefault(part.id, {})[mode] = res
            print(
                f"  {part.id[:52]:<52} {mode:<8} {res['tier'] or '-':<7} {res['outcome']:<5}"
                f" cut={tex.get('cut', '-'):<6} key={'y' if tex.get('keystone') else '-'}"
                f" body={tex.get('body', '-')} {tex.get('finish', '-'):<7}"
                f" photo_tris={tex.get('front_tris', '-')!s:<5}"
                f"{' TURNED' if tex.get('turned') else ''}"
                f" {res.get('seconds', 0):5.2f} s"
                + (f"  ({res['reason']})" if res["reason"] else ""),
                flush=True,
            )
    took = time.monotonic() - t_all
    log = Path(get_settings().DATA_DIR) / "retexture.json"
    cache.write_json_atomic(log, json.dumps(rows, indent=2, default=str))
    print(
        f"retexture: {counts} in {took:.1f} s"
        f" ({took / max(1, sum(counts.values())):.2f} s a model),"
        f" {len(turned)} CAD models turned around, $0 (no paid calls)"
    )
    return {**counts, "seconds": round(took, 1), "turned": turned, "parts": len(parts)}


def _retexture_main(ids: list[str], match: str | None, modes: list[str]) -> None:
    """Fetch the cut-out model while online, then re-texture OFFLINE (a cache miss can't call)."""
    import os

    from server import photo_cut

    if photo_cut.ensure_model() is None:
        print("retexture: no U^2-Netp model (offline?): cluttered photos keep the old crop")
    os.environ["OFFLINE"] = "true"
    get_settings.cache_clear()
    asyncio.run(warm_retexture(ids or None, match, tuple(modes)))


async def _run(
    queries: list[dict], retry_proxies: bool = False, grok_moulded: bool = False
) -> None:
    warmed: dict[str, list[Part]] = {}
    for entry in queries:
        print(f"query: {entry['query']!r}")
        parts = await warm_query(entry, retry_proxies)
        if grok_moulded:
            parts = await warm_grok_moulded(parts)
        warmed[entry["query"]] = parts
        _print_table([_row(p) for p in parts])
    print("bom:")
    await warm_bom(warmed)
    print("demo commands:")
    replies = await warm_demo_replies(warmed)
    print("tts:")
    await warm_tts(warmed, replies)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Pre-cache find_parts/resolve_asset/TTS for the scripted demo (offline mode)."
    )
    parser.add_argument(
        "--queries-file", type=Path, default=None, help="JSON list of {query, measurement}"
    )
    parser.add_argument(
        "--retry-proxies", action="store_true", help="re-mesh parts that are proxy boxes"
    )
    parser.add_argument(
        "--grok-moulded",
        action="store_true",
        help="offline Grok OpenSCAD models for moulded parts (slow, spends xAI credit)",
    )
    parser.add_argument(
        "--labels", metavar="SITE", help="only pre-label SITE's thumbs (spends xAI credit)"
    )
    parser.add_argument("--every", type=int, default=4, help="with --labels: every Nth thumb")
    parser.add_argument(
        "--hf",
        action="store_true",
        help="only pre-generate Hunyuan3D (HF) models for every cached part (ZeroGPU quota)",
    )
    parser.add_argument("--hf-limit", type=int, default=None, help="with --hf: the first N parts")
    parser.add_argument(
        "--hf-no-wait", action="store_true", help="with --hf: stop when the quota is spent"
    )
    parser.add_argument(
        "--catalog",
        action="store_true",
        help="fill every site's catalog: place, category searches, first models (spends credit)",
    )
    parser.add_argument("--sites", help="with --catalog: comma-separated sites (default: all)")
    parser.add_argument(
        "--no-models", action="store_true", help="with --catalog: skip building models"
    )
    parser.add_argument(
        "--no-focus", action="store_true", help="with --catalog: skip the focus variants"
    )
    parser.add_argument(
        "--max-searches", type=int, default=None, help="with --catalog: search at most N queries"
    )
    parser.add_argument(
        "--scad",
        action="store_true",
        help="only make Grok OpenSCAD (LLM+CAD) models: catalog items with a model + demo parts",
    )
    parser.add_argument("--scad-jobs", type=int, default=3, help="with --scad: runs at once")
    parser.add_argument("--scad-parts", help="with --scad: comma-separated part ids first")
    parser.add_argument("--scad-match", help="with --scad: only parts whose id/name holds this")
    parser.add_argument(
        "--no-demo-parts", action="store_true", help="with --scad: skip SCAD_DEMO_PARTS"
    )
    parser.add_argument(
        "--retry-failed", action="store_true", help="with --scad: retry parts that failed before"
    )
    parser.add_argument(
        "--estimate", action="store_true", help="with --scad: print the plan and estimate only"
    )
    parser.add_argument(
        "--remake-flat",
        action="store_true",
        help="with --scad: remake appliance CAD models whose front is flat (spends xAI credit)",
    )
    parser.add_argument(
        "--retexture",
        action="store_true",
        help="only re-texture the models on disk (cut-out photo, front-only, side colours); $0",
    )
    parser.add_argument("--parts", help="with --retexture: comma-separated part ids")
    parser.add_argument("--match", help="with --retexture: only parts whose id/name holds this")
    parser.add_argument(
        "--modes", default=",".join(RETEXTURE_MODES), help="with --retexture: variants to redo"
    )
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    logging.getLogger("httpx").setLevel(logging.WARNING)  # its INFO lines carry SerpApi api_key
    if args.retexture:
        ids = [x.strip() for x in (args.parts or "").split(",") if x.strip()]
        modes = [assets.normalize_mode(m) for m in args.modes.split(",") if m.strip()]
        _retexture_main(ids, args.match, list(dict.fromkeys(modes)))
        return
    if args.labels:
        asyncio.run(warm_labels(args.labels, max(1, args.every)))
        return
    if args.hf:
        asyncio.run(warm_hf(args.hf_limit, wait=not args.hf_no_wait))
        return
    if args.scad:
        sites = [s.strip() for s in args.sites.split(",") if s.strip()] if args.sites else None
        ids = (
            [s.strip() for s in args.scad_parts.split(",") if s.strip()] if args.scad_parts else []
        )

        async def scad() -> None:
            parts = await scad_parts(sites, ids, args.scad_match, demo=not args.no_demo_parts)
            if args.remake_flat:  # texture: flat CAD fronts made again (prompt + relief pass)
                flat = set_aside_flat(parts, dry_run=args.estimate)
                print(f"scad: {len(flat)} appliance CAD models with a flat front to make again")
                parts = [p for p in parts if p.id in flat]
                if args.estimate:  # nothing moved aside: count them as to-make
                    est = scad_estimate(len(parts), args.scad_jobs)
                    print(f"scad: ~{est['seconds'] / 60:.0f} min, ~${est['usd']:.2f}")
                    return
            await warm_scad(parts, args.scad_jobs, args.retry_failed, args.estimate)

        asyncio.run(scad())
        return
    if args.catalog:
        sites = [s.strip() for s in args.sites.split(",") if s.strip()] if args.sites else None
        asyncio.run(
            catalog.warm(
                sites,
                models=not args.no_models,
                focus=not args.no_focus,
                max_searches=args.max_searches,
            )
        )
        return
    queries = json.loads(args.queries_file.read_text()) if args.queries_file else DEFAULT_QUERIES
    asyncio.run(_run(queries, args.retry_proxies, args.grok_moulded))


if __name__ == "__main__":
    main()
