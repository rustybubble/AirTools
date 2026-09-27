"""Scale check against standard-size objects in bench eval frame 0003 (E1 §3b).

    python pipeline/experiments/bench/object_scale.py /workspace/bench/ref

Projects the altitude-metric reference cloud into eval camera 0003, takes the median depth near
each object edge, back-projects the hand-picked edge pixels (3840-wide undistorted frame) and
prints true size / reference size. Edge pixels were read off a gridded zoom of the frame.
"""

import json
import sys
from pathlib import Path

import numpy as np

ref_dir = Path(sys.argv[1])
ref = json.loads((ref_dir / "reference.json").read_text())
e = next(x for x in ref["eval"] if x["id"] == "0003")
R, t = np.array(e["R"]), np.array(e["t_wc"])
k = 3840 / e["w"]  # edge pixels are in the 3840-wide frame
f, cx, cy = e["fx"] * k, e["cx"] * k, e["cy"] * k

X = np.load(ref_dir / "cloud.npy").astype(np.float64) @ R.T + t
X = X[X[:, 2] > 0.05]
u, v = f * X[:, 0] / X[:, 2] + cx, f * X[:, 1] / X[:, 2] + cy


def point(px: float, py: float, box: tuple[int, int, int, int]) -> np.ndarray:
    """Pixel back-projected at the 10th-percentile (front-most surface) depth inside `box`."""
    x0, y0, x1, y1 = box
    z = float(np.percentile(X[(u >= x0) & (u < x1) & (v >= y0) & (v < y1), 2], 10))
    return np.array([(px - cx) / f * z, (py - cy) / f * z, z])


# name, true metres, (edge pixel, depth box) x2
OBJECTS = [
    (
        "microwave width (OTR 29.9 in)",
        0.759,
        ((1103, 400), (1128, 80, 1272, 720)),
        ((2880, 400), (2440, 80, 2860, 720)),
    ),
    (
        "range front lip width (30 in)",
        0.762,
        ((830, 1850), (828, 1808, 900, 1872)),
        ((3140, 1840), (3068, 1808, 3140, 1872)),
    ),
    (
        "Decora receptacle width (33.5 mm)",
        0.0335,
        ((1148, 1100), (1136, 980, 1248, 1168)),
        ((1207, 1100), (1136, 980, 1248, 1168)),
    ),
    (
        "Decora receptacle height (66.8 mm)",
        0.0668,
        ((1178, 1030), (1136, 980, 1248, 1168)),
        ((1178, 1149), (1136, 980, 1248, 1168)),
    ),
]

for name, true, (pa, ba), (pb, bb) in OBJECTS:
    d = float(np.linalg.norm(point(*pa, ba) - point(*pb, bb)))
    print(f"{name}: reference {d:.4f} m, true/reference {true / d:.3f}")
print(f"altitude scale {ref['altitude_scale']:.5f} m/unit")
