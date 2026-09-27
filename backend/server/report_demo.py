"""Seed a realistic site-walk session so `GET /report/<session>` can be shown without the headset.

    uv run python -m server.report_demo            # session demo-kitchen on scene/kitchen
    open http://localhost:8000/report/demo-kitchen

Writes (under DATA_DIR): the session's notebook (replaced), one BOM and one checkout receipt
(`checkout.authorize`, so it is a real offline receipt unless Cybersource keys are set, in which
case it's a real sandbox authorization -- the label says which). The part must already be in
`DATA_DIR/parts` (default: the Karran quartz sink from the demo cache). The caulk price was
copied from a real Google Shopping listing cached on 2026-09-24; nothing else is priced here.
"""

import argparse
import asyncio

from server import app, bom, checkout, jobs
from server.models import Seller

DEMO_PART = "karran-qu-670-bl-2d781c"


def demo_entries(site: str, part_id: str) -> list[dict]:
    """Tape readings, a level reading, a pin, a note and a placement, as the headset logs them.
    Camera ids are real `scene/kitchen/thumbs/*.jpg` frames."""
    tape = {"type": "measurement", "tool": "tape", "site": site}
    return [
        {**tape, "label": "Counter run, sink wall", "value_m": 2.41, "nearest_camera_id": "0021"},
        {
            **tape,
            "label": "Sink cabinet inside width",
            "value_m": 0.84,
            "nearest_camera_id": "0041",
        },
        {
            **tape,
            "label": "Window sill height",
            "value_m": 1.07,
            "nearest_camera_id": "0061",
            "quality": "preview",
        },
        {
            "type": "measurement",
            "tool": "level",
            "site": site,
            "label": "Countertop level, left to right",
            "value": 0.4,
            "unit": "°",
            "nearest_camera_id": "0081",
        },
        {
            "type": "pin",
            "site": site,
            "label": "Water stain under the sink: check for a leak before install",
            "nearest_camera_id": "0101",
        },
        {"type": "note", "text": "Customer wants the black quartz sink; keep the existing faucet."},
        {"type": "placement", "part_id": part_id, "count": 1},
    ]


def demo_bom(part_id: str) -> bom.Bom:
    caulk = Seller(
        name="Home Depot",
        title="DAP Alex Fast Dry Acrylic Latex Caulk",
        price_usd=4.98,
        unit_price_usd=4.98,
        rating=4.6,
        reviews=2800,
        verified=True,
    )
    lines = [
        bom.BomLine(
            idx=0,
            name="Kitchen and bath caulk (10 oz tube)",
            qty=1,
            reason="Seal the sink rim to the countertop",
            seller=caulk,
        ),
        bom.BomLine(
            idx=1,
            name="Undermount sink mounting clips",
            qty=1,
            reason="Hold the sink to the underside of the counter",
        ),
    ]
    total = round(sum(bom.line_cost(line) for line in lines), 2)
    return bom.Bom(id="bom-demo-kitchen", part_ids=[part_id], lines=lines, total_usd=total)


async def seed(session_id: str = "demo-kitchen", site: str = "kitchen", part_id: str = DEMO_PART):
    part = jobs.load_part(part_id)
    if part is None:
        raise SystemExit(f"part {part_id!r} is not in DATA_DIR/parts; pass --part <id>")
    app._notebook_path(session_id).unlink(missing_ok=True)
    app._append_notebook(session_id, demo_entries(site, part_id))

    a_bom = demo_bom(part_id)
    bom._persist(a_bom)
    app._append_notebook(session_id, [{"type": "bom", "bom_id": a_bom.id}])

    seller_idx = part.recommended_seller if part.recommended_seller is not None else 0
    receipt = await checkout.authorize(part, seller_idx, 1, bom_lines=[a_bom.lines[0]])
    app._append_notebook(session_id, [{"type": "order", **receipt, "bom_id": a_bom.id}])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--session", default="demo-kitchen")
    parser.add_argument("--site", default="kitchen")
    parser.add_argument("--part", default=DEMO_PART)
    args = parser.parse_args()
    asyncio.run(seed(args.session, args.site, args.part))
    print(f"seeded; open /report/{args.session}")
