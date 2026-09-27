"""cad: time Grok's OpenSCAD loop (`meshgen.grok_scad_run`) per model on a few parts.

Every call is live: point DATA_DIR at an empty scratch dir (the LLM cache lives there) and read
the parts from another data dir. One process per model (the role is resolved from the env):

    DATA_DIR=/tmp/bench LLM_ASSET_SCAD=xai:grok-4.20-0309-non-reasoning \
        uv run python -m server.experiments.scad_bench --parts-dir data/parts \
        --out /tmp/bench/out frigidaire-fdpc4221as-341c87 jeld-wen-thdjw235100032-d0809e

Prints one row per part (compiled, calls, fixes, fit pass, seconds, cost, raw bbox error) and
writes <out>/<model>/results.json.
"""

import argparse
import asyncio
import json
import logging
import time
from pathlib import Path

from server import meshgen
from server.models import Part


async def run(parts_dir: Path, out: Path, ids: list[str], jobs: int) -> list[dict]:
    model = meshgen.scad_model()
    root = out / model.replace(":", "_").replace("/", "_")
    gate = asyncio.Semaphore(max(1, jobs))

    async def one(pid: str) -> dict:
        part = Part.model_validate_json((parts_dir / pid / "part.json").read_text())
        photo = parts_dir / pid / "image.jpg"
        async with gate:
            t0 = time.monotonic()
            rec = await meshgen.grok_scad_run(part, photo, root / pid / "scad.glb")
            rec.setdefault("seconds", round(time.monotonic() - t0, 1))
        rec["part_id"] = pid
        rec["dims_mm"] = part.dims_mm.model_dump()
        return rec

    results = await asyncio.gather(*(one(p) for p in ids))
    root.mkdir(parents=True, exist_ok=True)
    (root / "results.json").write_text(json.dumps(results, indent=2))
    print(f"\n{model}")
    head = f"{'part':<42} {'ok':<3} {'calls':>5} {'fix':>3} {'fit':>3} {'s':>7} {'$':>8} {'bbox0%':>7} {'bbox%':>6}"
    print(head)
    for r in results:
        cost = r.get("cost_usd")
        print(
            f"{r['part_id'][:42]:<42} {'y' if r.get('ok') else 'n':<3} {r.get('calls', 0):>5} "
            f"{r.get('fixes', 0):>3} {'y' if r.get('fit_pass') else '-':>3} "
            f"{r.get('seconds', 0):>7.1f} {cost if cost is not None else float('nan'):>8.4f} "
            f"{r.get('first_bbox_err_pct', float('nan')):>7.1f} {r.get('bbox_err_pct', float('nan')):>6.1f}"
            + (f"  {r['error'][:80]}" if r.get("error") else "")
        )
    return results


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("ids", nargs="+")
    ap.add_argument("--parts-dir", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--jobs", type=int, default=3)
    args = ap.parse_args()
    logging.basicConfig(
        level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s"
    )
    logging.getLogger("httpx").setLevel(logging.WARNING)
    asyncio.run(run(args.parts_dir, args.out, args.ids, args.jobs))


if __name__ == "__main__":
    main()
