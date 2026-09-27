"""E6: score the 2DGS splat itself at E1's held-out evaluation frames (reference only; we ship the mesh).

    cams:  the reference eval cameras in the COLMAP frame of `cameras.npz`, via the harness's own
           timestamp-matched Sim(3) (COLMAP camera centres -> reference track) -> eval_cams.npz
    score: `train.py`'s eval renders vs the eval photos with `qa.psnr`/`qa.ssim` on alpha > 0.5,
           at the harness's 960x540 -> JSON on stdout

Runs in the airtools venv (`python -m pipeline.experiments.gs2d.evalsplat ...`).
"""

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.calibrate import interpolate_positions_at_times
from pipeline.experiments.bench.harness import id_times, robust_sim3


def cams(a) -> None:
    ref = json.loads((Path(a.ref) / "reference.json").read_text())
    tr_t, tr_p = np.array(ref["track"]["times"]), np.array(ref["track"]["positions"])
    c = np.load(a.cameras)
    ids = [Path(str(n)).stem for n in c["names"]]
    T = c["T_cw"]
    C = -np.einsum("nji,nj->ni", T[:, :3, :3], T[:, :3, 3])
    t = id_times(ids, None, a.id_fps)
    m = (t >= tr_t.min()) & (t <= tr_t.max())
    s, R, tt, resid, keep = robust_sim3(C[m], interpolate_positions_at_times(tr_t, tr_p, t[m]))
    Ks, Tcw, eids = [], [], []
    for e in ref["eval"]:
        Re, te = np.array(e["R"]), np.array(e["t_wc"])
        M = np.eye(
            4
        )  # X_ref = s R X_col + tt  =>  cam = Re R X_col + (Re tt + te) / s (COLMAP units)
        M[:3, :3], M[:3, 3] = Re @ R, (Re @ tt + te) / s
        sx, sy = a.width / e["w"], a.height / e["h"]
        Ks.append([[e["fx"] * sx, 0, e["cx"] * sx], [0, e["fy"] * sy, e["cy"] * sy], [0, 0, 1]])
        Tcw.append(M)
        eids.append(e["id"])
    np.savez(a.out, ids=np.array(eids), K=np.array(Ks), T_cw=np.array(Tcw))
    print(
        json.dumps(
            {"n_eval": len(eids), "sim3_scale": s, "resid": resid, "inliers": int(keep.sum())}
        )
    )


def score(a) -> None:
    from pipeline.qa import psnr, ssim

    ref = json.loads((Path(a.ref) / "reference.json").read_text())
    rows = []
    for e in ref["eval"]:
        r = Path(a.renders) / f"{e['id']}.png"
        if not r.exists():
            continue
        with Image.open(Path(a.ref) / "eval" / f"{e['id']}.jpg") as im:
            photo = np.asarray(im.convert("RGB").resize((a.width, a.height), Image.LANCZOS))
        img = np.asarray(Image.open(r).convert("RGB"))
        cov = np.asarray(Image.open(Path(a.renders) / f"{e['id']}_a.png")) > 127
        rows.append({
            "id": e["id"], "coverage": float(cov.mean()),
            "psnr": psnr(img, photo, mask=cov) if cov.sum() > 100 else None,
            "ssim": ssim(img, photo, mask=cov) if cov.sum() > 100 else None,
        })  # fmt: skip
    mean = lambda k: float(np.mean([r[k] for r in rows if r[k] is not None]))
    print(json.dumps({"n": len(rows), "coverage": mean("coverage"), "psnr": mean("psnr"),
                      "ssim": mean("ssim"), "per_frame": rows}))  # fmt: skip


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("cams")
    c.add_argument("--cameras", required=True)
    c.add_argument("--out", required=True)
    s = sub.add_parser("score")
    s.add_argument("--renders", required=True)
    for q in (c, s):
        q.add_argument("--ref", default="/workspace/bench/ref")
        q.add_argument("--id-fps", type=float, default=10.0)
        q.add_argument("--width", type=int, default=960)
        q.add_argument("--height", type=int, default=540)
    a = p.parse_args()
    {"cams": cams, "score": score}[a.cmd](a)
