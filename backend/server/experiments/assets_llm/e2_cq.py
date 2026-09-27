"""E2 frame-safe helper library for LLM-written CadQuery (docs/research/assets-llm/e2-code.md).

Same API and frame as `server/scad_lib.scad`. Frame (mm): X across (-W/2..W/2, +X right when facing the
front), Y up (0..H), Z depth (0..D): Z=0 back/mounting face, Z=D front. Everything is built from
axis-aligned boxes and cylinders, so the model never rotates a workplane.

Runs in the CadQuery env (not a project dependency); writes one STL per paint colour + colors.json:
    work/assets-llm/envs/cq/bin/python -m server.experiments.assets_llm.e2_cq code.py outdir W H D
"""

import json
import sys
from pathlib import Path

import cadquery as cq

# The block the prompt shows (kept under ~25 lines).
API = """\
W, H, D                                  predefined floats (mm); cq is imported
paint(solid, "#hex")                     add a solid (cq.Workplane) with a colour; every solid
                                         must be painted; later paints win where they overlap
box(x0,x1,y0,y1,z0,z1, r=0, ax="z")      box by world ranges (defaults = whole envelope);
                                         r fillets the 4 edges parallel to axis ax
cyl(axis, r, a0, a1, c=(0,0), r2=None)   cylinder/cone along "x"|"y"|"z" from a0 to a1;
                                         c = centre in the other two axes (x:(y,z) y:(x,z) z:(x,y))
Face features. face = "front"|"back"|"top"|"bottom"|"left"|"right". c and size use the face's
two world axes: front/back (x,y), top/bottom (x,z), left/right (z,y). Each occupies the outer
t mm of the envelope at that face (inside the box).
panel(face, c, size, t, r=0)             plate (door, lid, screen, flange); .cut() it = pocket
disc(face, c, dia, t)                    round plate/knob/boss; .cut() it = round pocket
hole(face, c, dia, t=big)                through-hole along the face normal (use with .cut())
grille(face, c, size, n, dir="h", t=4)   n parallel bars ("h" horizontal, "v" vertical)
vent(face, c, size, n, dir="h", t=4)     n slots, use with .cut() to cut louvres
handle(face, c, length, dir="v", t=40, dia=20)  bar handle on two posts, t = standoff depth
Combine with a.union(b), a.cut(b), a.intersect(b).
To make a feature stand proud, stop the body short of that face: box(z1=D-30) then panel(t=30)."""

W = H = D = 100.0
PARTS: list[tuple[cq.Workplane, str]] = []
_AX = {"x": cq.Vector(1, 0, 0), "y": cq.Vector(0, 1, 0), "z": cq.Vector(0, 0, 1)}
# face -> (normal axis, in-plane axis a, in-plane axis b, face coordinate fn, inward sign)
_FACES = {
    "front": (2, 0, 1, lambda: D, -1),
    "back": (2, 0, 1, lambda: 0.0, 1),
    "top": (1, 0, 2, lambda: H, -1),
    "bottom": (1, 0, 2, lambda: 0.0, 1),
    "right": (0, 2, 1, lambda: W / 2, -1),
    "left": (0, 2, 1, lambda: -W / 2, 1),
}


def paint(solid, color: str) -> None:
    PARTS.append((solid, color))


def _box(lo, hi) -> cq.Workplane:
    lo, hi = [min(a, b) for a, b in zip(lo, hi)], [max(a, b) for a, b in zip(lo, hi)]
    size = [max(b - a, 1e-3) for a, b in zip(lo, hi)]
    return cq.Workplane("XY").box(*size, centered=False).translate(tuple(lo))


def box(x0=None, x1=None, y0=0.0, y1=None, z0=0.0, z1=None, r=0.0, ax="z") -> cq.Workplane:
    x0 = -W / 2 if x0 is None else x0
    x1 = W / 2 if x1 is None else x1
    y1 = H if y1 is None else y1
    z1 = D if z1 is None else z1
    b = _box((x0, y0, z0), (x1, y1, z1))
    if r > 0:
        ext = sorted([abs(x1 - x0), abs(y1 - y0), abs(z1 - z0)])
        b = b.edges("|" + ax.upper()).fillet(min(r, ext[0] / 2 - 0.01))
    return b


def cyl(axis, r, a0, a1, c=(0, 0), r2=None) -> cq.Workplane:
    lo, h = min(a0, a1), max(abs(a1 - a0), 1e-3)
    rb, rt = (r, r if r2 is None else r2) if a1 >= a0 else (r if r2 is None else r2, r)
    p = {"x": (lo, c[0], c[1]), "y": (c[0], lo, c[1]), "z": (c[0], c[1], lo)}[axis]
    if rb == rt:
        s = cq.Solid.makeCylinder(rb, h, cq.Vector(*p), _AX[axis])
    else:
        s = cq.Solid.makeCone(rb, rt, h, cq.Vector(*p), _AX[axis])
    return cq.Workplane("XY").add(s)


def _fbox(face, c, size, t, out=0.0) -> cq.Workplane:
    n, a, b, fc, sgn = _FACES[face]
    lo, hi = [0.0] * 3, [0.0] * 3
    lo[n], hi[n] = fc() - sgn * out, fc() + sgn * t
    lo[a], hi[a] = c[0] - size[0] / 2, c[0] + size[0] / 2
    lo[b], hi[b] = c[1] - size[1] / 2, c[1] + size[1] / 2
    return _box(lo, hi)


def _fcyl(face, c, dia, t, out=0.0) -> cq.Workplane:
    n, _, _, fc, sgn = _FACES[face]
    cc = (c[1], c[0]) if n == 0 else (c[0], c[1])  # x-axis cylinders take (y, z)
    return cyl("xyz"[n], dia / 2, fc() - sgn * out, fc() + sgn * t, cc)


def panel(face, c=(0, 0), size=(10, 10), t=5.0, r=0.0) -> cq.Workplane:
    p = _fbox(face, c, size, t)
    if r > 0:
        n = _FACES[face][0]
        p = p.edges("|" + "XYZ"[n]).fillet(min(r, size[0] / 2 - 0.01, size[1] / 2 - 0.01))
    return p


def disc(face, c=(0, 0), dia=10.0, t=5.0) -> cq.Workplane:
    return _fcyl(face, c, dia, t)


def hole(face, c=(0, 0), dia=5.0, t=1e5) -> cq.Workplane:
    return _fcyl(face, c, dia, t, out=1.0)


def _bars(face, c, size, n, dir, t, out) -> cq.Workplane:
    k = 1 if dir == "h" else 0
    pitch = size[k] / n
    res = None
    for j in range(int(n)):
        off = -size[k] / 2 + pitch * (j + 0.5)
        cc = (c[0], c[1] + off) if k == 1 else (c[0] + off, c[1])
        ss = (size[0], pitch / 2) if k == 1 else (pitch / 2, size[1])
        bar = _fbox(face, cc, ss, t, out)
        res = bar if res is None else res.union(bar)
    return res


def grille(face, c=(0, 0), size=(100, 100), n=8, dir="h", t=4.0) -> cq.Workplane:
    return _bars(face, c, size, n, dir, t, 0.0)


def vent(face, c=(0, 0), size=(100, 100), n=8, dir="h", t=4.0) -> cq.Workplane:
    return _bars(face, c, size, n, dir, t, 1.0)


def handle(face, c=(0, 0), length=200.0, dir="v", t=40.0, dia=20.0) -> cq.Workplane:
    k = 1 if dir == "v" else 0
    res = _fbox(face, c, (dia, length) if k else (length, dia), dia)
    for s in (-1, 1):
        d = s * (length / 2 - dia / 2)
        pc = (c[0], c[1] + d) if k else (c[0] + d, c[1])
        res = res.union(_fcyl(face, pc, dia * 0.8, t))
    return res


def run(code: str, out: Path, w: float, h: float, d: float) -> dict:
    global W, H, D
    W, H, D = w, h, d
    PARTS.clear()
    ns = {k: v for k, v in globals().items() if not k.startswith("__")}
    exec(compile(code, "<llm>", "exec"), ns)  # noqa: S102 -- experiment harness, local only
    if not PARTS:
        raise ValueError("no solids: call paint(solid, '#hex') for every solid")
    out.mkdir(parents=True, exist_ok=True)
    colors = []
    for i, (obj, color) in enumerate(PARTS):
        if not isinstance(obj, cq.Workplane | cq.Shape):
            raise TypeError(f"paint() #{i} got {type(obj).__name__}, expected a cq.Workplane")
        cq.exporters.export(obj, str(out / f"{i}.stl"), tolerance=0.3, angularTolerance=0.2)
        colors.append(color)
    (out / "colors.json").write_text(json.dumps(colors))
    return {"parts": len(colors)}


if __name__ == "__main__":
    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    print(json.dumps(run(src.read_text(), dst, *map(float, sys.argv[3:6]))))
