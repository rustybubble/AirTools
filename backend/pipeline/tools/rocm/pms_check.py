"""Compare COLMAP patch-match depth maps with the SfM points each image observes (depth at the
observation pixel). Usage: pms_check.py DENSE_DIR TXT_MODEL_DIR [kind=photometric]"""
import os
import sys

import numpy as np


def read(p):
    """COLMAP depth/normal map .bin: ASCII 'w&h&c&' header, then float32 column-major data."""
    with open(p, "rb") as f:
        hdr = b""
        while hdr.count(b"&") < 3:
            hdr += f.read(1)
        w, h, c = map(int, hdr.decode().strip("&").split("&"))
        return np.fromfile(f, np.float32).reshape(c, h, w).transpose(1, 2, 0).squeeze()


dense, txt = sys.argv[1], sys.argv[2]; kind = sys.argv[3] if len(sys.argv) > 3 else "photometric"
cams = {}
for l in open(f"{txt}/cameras.txt"):
    if l[0] == "#": continue
    p = l.split(); cams[int(p[0])] = (int(p[2]), int(p[3]))
pts = {}
for l in open(f"{txt}/points3D.txt"):
    if l[0] == "#": continue
    p = l.split(); pts[int(p[0])] = np.array(p[1:4], float)
lines = [l for l in open(f"{txt}/images.txt") if l[0] != "#"]
for head, obs in zip(lines[0::2], lines[1::2]):
    h = head.split(); name = h[9]
    f = f"{dense}/stereo/depth_maps/{name}.{kind}.bin"
    if not os.path.exists(f): continue
    q = np.array(h[1:5], float); t = np.array(h[5:8], float)
    w, x, y, z = q
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)], [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)], [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    W, H = cams[int(h[8])]
    d = read(f); s = d.shape[1] / W
    o = obs.split(); err = []
    for i in range(0, len(o), 3):
        pid = int(o[i+2])
        if pid < 0 or pid not in pts: continue
        u, v = int(float(o[i]) * s), int(float(o[i+1]) * s)
        if not (0 <= u < d.shape[1] and 0 <= v < d.shape[0]) or d[v, u] <= 0: continue
        zs = (R @ pts[pid] + t)[2]; err.append(abs(d[v, u] - zs) / zs)
    e = np.array(err)
    print(f"{name}: valid={np.mean(d > 0):.1%}  sfm pts with depth={e.size}  median rel err={np.median(e):.3f}  within 5%={np.mean(e < 0.05):.1%}" if e.size else f"{name}: no overlap")
