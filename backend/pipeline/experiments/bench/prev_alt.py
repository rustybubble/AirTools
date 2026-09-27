import json
import sys
from pathlib import Path

import numpy as np
import pycolmap

from pipeline import telemetry
from pipeline.calibrate import (
    interpolate_positions_at_times,
    scale_from_caption_altitude,
    umeyama,
    up_from_cameras,
)

W = Path(sys.argv[1] if len(sys.argv) > 1 else "work/dji0095")
recs = telemetry.parse_srt((W / "telemetry.srt").read_text())
pv = json.loads((W / "preview/raw_poses.json").read_text())
times = json.loads((W / "preview/frame_times.json").read_text())
Rp = np.array([np.array(r["R_wc"]) for r in pv])
tp = np.array([np.array(r["t_wc"]) for r in pv])
Cp = np.einsum("nji,nj->ni", Rp, -tp)
tpv = np.array([times[Path(r["name"]).stem] for r in pv])


def alt(C, Rwc, ts):
    up = up_from_cameras(np.transpose(Rwc, (0, 2, 1)))
    res = telemetry.resample(recs, list(ts))
    h = np.array([r.rel_alt_m if r.rel_alt_m is not None else np.nan for r in res])
    return up, scale_from_caption_altitude(C @ up, h), h


up_p, est_p, hp = alt(Cp, Rp, tpv)
rec = pycolmap.Reconstruction(W / "sfm/dense/sparse")
ims = sorted(rec.images.values(), key=lambda i: i.name)
Rf = np.array([i.cam_from_world().rotation.matrix() for i in ims])
tf_ = np.array([i.cam_from_world().translation for i in ims])
Cf = np.einsum("nji,nj->ni", Rf, -tf_)
tfull = np.array([(int(Path(i.name).stem) - 1) / 10.0 for i in ims])
up_f, est_f, hf = alt(Cf, Rf, tfull)
print("preview alt:", est_p.scale, est_p.residual_m, est_p.notes)
print("full alt:", est_f.scale, est_f.residual_m)
m = (tpv >= tfull.min()) & (tpv <= tfull.max())
src = Cp[m]
dst = interpolate_positions_at_times(tfull, Cf, tpv[m])
s, R, t, r = umeyama(src, dst)
print("preview->full sim scale", s, "resid(full units)", r, "n", m.sum())
print(
    "implied metric preview scale",
    s * est_f.scale,
    "vs preview alt",
    est_p.scale,
    "ratio",
    est_p.scale / (s * est_f.scale),
)
print("angle between ups (deg)", np.degrees(np.arccos(np.clip(abs((R @ up_p) @ up_f), 0, 1))))
# how well each explains H: correlation
for nm, C, up, h in (("preview", Cp, up_p, hp), ("full", Cf, up_f, hf)):
    u = C @ up
    f = np.isfinite(h)
    print(nm, f"corr(up,H)={np.corrcoef(u[f], h[f])[0, 1]:.3f}", "n", f.sum())
# preview up-axis signal using the full stage's up mapped into preview frame
up_f_in_p = R.T @ up_f
e2 = scale_from_caption_altitude(Cp @ up_f_in_p, hp)
print("preview alt using full's up:", e2.scale, e2.residual_m)
