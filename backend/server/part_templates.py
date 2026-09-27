"""Parametric part templates: the `llm` asset tier's geometry (server/meshgen.py), built in E1/E5
(docs/research/assets-llm/e1-templates.md, e5-combo.md).

An LLM picks a template and fills a few parameters (fractions of the dims, counts, enums, sRGB
hex colours); the generator builds the part in the contract frame (docs/api.md §2): metres, +Y up,
X = w, Y = h, Z = d, origin at the mounting-face centre, product toward +Z, front face at Z = d.
Each generator is designed to fill the box exactly; `build` then fits the bbox per axis (a no-op
within float error, asserted by the self-check) so the GLB is exactly the published size.

Needs trimesh + manifold3d (booleans). Self-check: `uv run python -m server.part_templates`.
"""

from __future__ import annotations

import itertools
import json
import math
import re

import numpy as np
import trimesh

HEX = re.compile(r"^#?[0-9a-fA-F]{6}$")
EPS = 1e-4


def srgb_to_linear(hex_str: str) -> list[float]:
    """glTF baseColorFactor is linear; product colours are sRGB hex."""
    h = hex_str.lstrip("#")
    c = np.array([int(h[i : i + 2], 16) for i in (0, 2, 4)]) / 255.0
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    return [*map(float, lin), 1.0]


# ---------------------------------------------------------------- geometry helpers (contract frame)


class Parts:
    """Collects (mesh, role); `build` merges one primitive per role."""

    def __init__(self) -> None:
        self.items: list[tuple[trimesh.Trimesh, str]] = []

    def add(self, role: str, mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        self.items.append((mesh, role))
        return mesh

    def box(self, role, x0, x1, y0, y1, z0, z1):
        return self.add(role, box(x0, x1, y0, y1, z0, z1))

    def cyl(self, role, p0, p1, r, sections=32):
        return self.add(role, cyl(p0, p1, r, sections))


def box(x0, x1, y0, y1, z0, z1) -> trimesh.Trimesh:
    lo = [min(x0, x1), min(y0, y1), min(z0, z1)]
    hi = [max(x0, x1), max(y0, y1), max(z0, z1)]
    hi = [max(h, lo_ + EPS) for lo_, h in zip(lo, hi)]
    return trimesh.creation.box(bounds=[lo, hi])


def cyl(p0, p1, r, sections=32) -> trimesh.Trimesh:
    return trimesh.creation.cylinder(radius=max(r, EPS), segment=[p0, p1], sections=sections)


def union(meshes):
    meshes = [m for m in meshes if m is not None]
    return meshes[0] if len(meshes) == 1 else trimesh.boolean.union(meshes, engine="manifold")


def cut(a, tools):
    tools = [t for t in tools if t is not None]
    return trimesh.boolean.difference([a, *tools], engine="manifold") if tools else a


def rrect_y(x0, x1, z0, z1, y0, y1, r) -> trimesh.Trimesh:
    """Rounded-rectangle prism along Y (corners rounded in the XZ plane)."""
    r = min(r, (x1 - x0) / 2 - EPS, (z1 - z0) / 2 - EPS)
    if r < 1e-3:
        return box(x0, x1, y0, y1, z0, z1)
    parts = [box(x0 + r, x1 - r, y0, y1, z0, z1), box(x0, x1, y0, y1, z0 + r, z1 - r)]
    parts += [cyl([x, y0, z], [x, y1, z], r) for x in (x0 + r, x1 - r) for z in (z0 + r, z1 - r)]
    return union(parts)


def rot(axis, deg, point=(0, 0, 0)):
    return trimesh.transformations.rotation_matrix(math.radians(deg), axis, point)


def region(reg, W, H):
    """Front-face region [u0, u1, v0, v1] (0..1 from the viewer's left / bottom) -> x0, x1, y0, y1."""
    u0, u1, v0, v1 = reg
    return -W / 2 + u0 * W, -W / 2 + u1 * W, -H / 2 + v0 * H, -H / 2 + v1 * H


# ---------------------------------------------------------------- templates
# Param spec: (kind, default, lo, hi) for "f"/"i"; ("b", default); ("e", default, options);
# ("r", default) = face region [u0, u1, v0, v1]. Last item of each tuple set: help text.


def t_box(P: Parts, W, H, D, p):
    fh = p["feet_h"] * H if p["feet"] else 0.0
    pr = min(0.004, 0.02 * D) if p["inset"] else 0.0
    P.box("body", -W / 2, W / 2, -H / 2 + fh, H / 2, 0, D - pr)
    if p["inset"]:
        x0, x1, y0, y1 = region(p["inset_region"], W, H)
        P.box("accent", x0, x1, max(y0, -H / 2 + fh), y1, D - pr - EPS, D)
    if fh:
        fw, fd = 0.08 * W, 0.08 * D
        for sx in (-1, 1):
            for z in (0.02 * D, D - pr - 0.02 * D - fd):
                x = sx * (W / 2 - 0.02 * W) - (fw if sx > 0 else 0)
                P.box("feet", x, x + fw, -H / 2, -H / 2 + fh + EPS, z, z + fd)


def _grille(P, reg, style, n, W, H, F, D):
    """Dark backing plate on the face plane F, slats in body colour proud to D."""
    if style == "none":
        return
    x0, x1, y0, y1 = region(reg, W, H)
    P.box("grille_bg", x0, x1, y0, y1, F - EPS, F + (D - F) * 0.3)
    horiz = style in ("louvre_h", "mesh")
    vert = style in ("louvre_v", "mesh")
    frac = 0.25 if style == "mesh" else 0.5
    if horiz:
        pitch = (y1 - y0) / n
        for i in range(n):
            yc = y0 + (i + 0.5) * pitch
            P.box("body", x0, x1, yc - pitch * frac / 2, yc + pitch * frac / 2, F - EPS, D)
    if vert:
        pitch = (x1 - x0) / n
        for i in range(n):
            xc = x0 + (i + 0.5) * pitch
            P.box("body", xc - pitch * frac / 2, xc + pitch * frac / 2, y0, y1, F - EPS, D)


def t_box_appliance(P: Parts, W, H, D, p):
    fh = 0.03 * H if p["feet"] else 0.0
    pr = min(0.01, 0.03 * D)
    F = D - pr  # face plane; grille slats and control strip stand proud to D
    zf = F * (1 - p["front_frac"])
    yb = -H / 2 + fh
    P.box("body", -W / 2, W / 2, yb, H / 2, zf, F)
    if zf > EPS:  # rear cabinet / wall sleeve, inset on every side
        iw, ih = p["sleeve_inset"] * W, p["sleeve_inset"] * H
        P.box("sleeve", -W / 2 + iw, W / 2 - iw, yb + ih, H / 2 - ih, 0, zf + EPS)
    _grille(P, p["grille1_region"], p["grille1_style"], p["grille1_count"], W, H, F, D)
    _grille(P, p["grille2_region"], p["grille2_style"], p["grille2_count"], W, H, F, D)
    if p["control"]:
        x0, x1, y0, y1 = region(p["control_region"], W, H)
        P.box("control", x0, x1, y0, y1, F - EPS, F + pr * 0.5)
    if fh:
        for sx in (-1, 1):
            for z in (zf + 0.05 * D, F - 0.15 * D):
                x = sx * 0.4 * W
                P.box("feet", x - 0.04 * W, x + 0.04 * W, -H / 2, yb + EPS, z, z + 0.1 * D)


def t_tall_box(P: Parts, W, H, D, p):
    bar = p["handle"] == "bar"
    hd = min(0.05, 0.08 * D) if bar else (0.003 if p["handle"] == "recessed" else 0.0)
    t = min(0.06, 0.1 * D)
    zd1 = D - hd  # door front
    zd0 = zd1 - t
    g = 0.004  # door gap
    base = p["base_h"] * H
    P.box("body", -W / 2, W / 2, -H / 2, H / 2, 0, zd0 + EPS)
    if base:
        P.box("base", -W / 2 + 0.01 * W, W / 2 - 0.01 * W, -H / 2, -H / 2 + base, zd0, zd1 - 0.01)
    yb, yt = -H / 2 + base, H / 2
    ys = yt - p["split"] * (yt - yb)
    L, R = -W / 2, W / 2
    doors = {  # (x0, x1, y0, y1, handle) handle: v@x (vertical at x) / h (horizontal near top)
        "french3": [(L, 0, ys, yt, "vR"), (0, R, ys, yt, "vL"), (L, R, yb, ys, "h")],
        "french4": [
            (L, 0, ys, yt, "vR"),
            (0, R, ys, yt, "vL"),
            (L, 0, yb, ys, "h"),
            (0, R, yb, ys, "h"),
        ],
        "french4_stacked": [
            (L, 0, ys, yt, "vR"),
            (0, R, ys, yt, "vL"),
            (L, R, (yb + ys) / 2, ys, "h"),
            (L, R, yb, (yb + ys) / 2, "h"),
        ],
        "side_by_side": [(L, -0.05 * W, yb, yt, "vR"), (-0.05 * W, R, yb, yt, "vL")],
        "top_freezer": [(L, R, ys, yt, "vL"), (L, R, yb, ys, "vL")],
        "bottom_freezer": [(L, R, ys, yt, "vL"), (L, R, yb, ys, "h")],
        "single": [(L, R, yb, yt, "vL")],
    }[p["layout"]]
    for x0, x1, y0, y1, hnd in doors:
        x0, x1, y0, y1 = x0 + g / 2, x1 - g / 2, y0 + g / 2, y1 - g / 2
        P.box("door", x0, x1, y0, y1, zd0, zd1)
        dw, dh = x1 - x0, y1 - y0
        if p["handle"] == "none":
            continue
        if hnd[0] == "v":
            xc = x0 + 0.07 * dw if hnd == "vL" else x1 - 0.07 * dw
            ln = p["handle_len"] * dh
            yc = (y0 + y1) / 2 if dh < 0.6 * H else y0 + 0.45 * dh
            spans = [(xc - 0.012, xc + 0.012, yc - ln / 2, yc + ln / 2)]
            stand = [
                (xc - 0.008, xc + 0.008, y - 0.015, y + 0.015)
                for y in (yc - ln * 0.42, yc + ln * 0.42)
            ]
        else:
            ln = p["handle_len"] * dw
            xc, yc = (x0 + x1) / 2, y1 - 0.12 * dh
            spans = [(xc - ln / 2, xc + ln / 2, yc - 0.012, yc + 0.012)]
            stand = [
                (x - 0.015, x + 0.015, yc - 0.008, yc + 0.008)
                for x in (xc - ln * 0.42, xc + ln * 0.42)
            ]
        for hx0, hx1, hy0, hy1 in spans:
            if bar:
                P.box("handle", hx0, hx1, hy0, hy1, D - 0.015, D)
            else:
                P.box("trim", hx0, hx1, hy0, hy1, zd1 - EPS, D)
        if bar:
            for sx0, sx1, sy0, sy1 in stand:
                P.box("handle", sx0, sx1, sy0, sy1, zd1 - EPS, D - 0.015 + EPS)
    if p["dispenser"]:
        x0, x1, y0, y1, _ = doors[0]
        xc, yc = (x0 + x1) / 2, ys + 0.55 * (yt - ys)
        dw, dh = 0.45 * (x1 - x0), 0.28 * (yt - ys)
        P.box(
            "trim",
            xc - dw / 2,
            xc + dw / 2,
            yc - dh / 2,
            yc + dh / 2,
            zd1 - EPS,
            zd1 + min(0.002, hd),
        )


def t_cylinder_tank(P: Parts, W, H, D, p):
    rx, rz, zc = W / 2, D / 2, D / 2

    def ecyl(role, y0, y1, s, sections=48):
        m = trimesh.creation.cylinder(radius=1.0, segment=[[0, 0, 0], [0, 1, 0]], sections=sections)
        m.apply_scale([rx * s, max(y1 - y0, EPS), rz * s])
        m.apply_translation([0, y0, zc])
        return P.add(role, m)

    ph = p["pipe_h"] * H if p["pipes"] else 0.0
    tc = p["top_h"] * H
    bh = p["base_h"] * H
    ytop = H / 2 - ph
    s = 0.985  # jacket; bands and the front plates reach the full W / D
    y0, y1 = -H / 2 + bh, ytop - tc  # jacket; the cap or dome fills y1..ytop
    ecyl("jacket", y0, y1, s)
    if tc and p["top"] == "flat":
        ecyl("top", y1 - EPS, ytop, s * 0.97)
    elif tc:  # dome: upper half-ellipsoid, lower half collapsed onto its base plane
        dome = trimesh.creation.icosphere(subdivisions=3)
        dome.vertices[:, 1] = np.maximum(dome.vertices[:, 1], 0)
        dome.apply_scale([rx * s * 0.99, tc, rz * s * 0.99])
        dome.apply_translation([0, y1 - EPS, zc])
        P.add("top", dome)
    if bh:
        ecyl("base", -H / 2, y0 + EPS, s * 0.95)
    for i in range(p["pipes"]):
        a = math.radians(180 / (p["pipes"] + 1) * (i + 1))
        x, z = 0.45 * rx * math.cos(a), zc - 0.35 * rz * math.sin(a)
        P.cyl("pipe", [x, ytop - max(tc, 0.01), z], [x, H / 2, z], 0.035 * min(W, D))
    if p["bands"]:
        n = p["bands"]
        for i in range(n):
            f = 0.08 + 0.84 * (i / (n - 1) if n > 1 else 0.5)
            yc = y0 + f * (y1 - y0)
            ecyl("band", yc - 0.006 * H, yc + 0.006 * H, 1.0)
    zf0, zf1 = zc + rz * 0.9, zc + rz  # front plates stand proud of the jacket at x=0
    n = p["panels"]
    for i in range(n):
        yc = y0 + (0.2 + 0.6 * i / (n - 1) if n > 1 else 0.25) * (y1 - y0)
        P.box("panel", -0.07 * W, 0.07 * W, yc - 0.06 * H, yc + 0.06 * H, zf0, zf1)
    if p["label"]:
        yc = y0 + 0.55 * (y1 - y0)
        P.box("label", -0.17 * W, 0.17 * W, yc - 0.1 * H, yc + 0.1 * H, zf0, zf1 - 0.002 * rz)


def t_joist_hanger(P: Parts, W, H, D, p):
    t = p["thickness_mm"] / 1000
    m = max(p["wall_h"], p["flange_h"])
    wall_h, fl_h = p["wall_h"] / m * H, p["flange_h"] / m * H
    sw = min(p["seat_w"] * W, W - 6 * t)  # leave at least 2t of flange each side
    xi = sw / 2 + t  # outer face of the side walls
    fw = W / 2 - xi
    yb = -H / 2
    P.box("metal", -xi, xi, yb, yb + t, 0, D)  # seat
    for sx in (-1, 1):
        wall = box(sx * sw / 2, sx * xi, yb, yb + wall_h, 0, D)
        n = p["wall_holes"]
        rr = min(0.15 * wall_h, 0.004)
        holes = [
            cyl(
                [sx * (sw / 2 - t), yb + 0.55 * wall_h, z],
                [sx * (xi + t), yb + 0.55 * wall_h, z],
                rr,
                16,
            )
            for z in (D * (i + 1) / (n + 1) for i in range(n))
        ]
        P.add("metal", cut(wall, holes))
        fl = box(sx * xi - sx * EPS, sx * W / 2, yb, yb + fl_h, 0, t)
        n = p["flange_holes"]
        rr = min(0.22 * fw, 0.005, 0.3 * fl_h / max(n, 1))
        xc = sx * (xi + fw / 2)
        holes = [
            cyl([xc, y, -t], [xc, y, 2 * t], rr, 16)
            for y in (yb + fl_h * (i + 1) / (n + 1) for i in range(n))
        ]
        P.add("metal", cut(fl, holes))


def t_strap_hanger(P: Parts, W, H, D, p):
    t = p["thickness_mm"] / 1000
    m = max(p["back_h"], p["hook_h"] if p["hook"] != "none" else 0)
    back_h, hook_h = p["back_h"] / m * H, p["hook_h"] / m * H
    sw = p["strip_w"] * W
    yt = H / 2
    P.box("body", -sw / 2, sw / 2, yt - t, yt, 0, D)  # strap
    P.box("body", -W / 2, W / 2, yt - back_h, yt, 0, t)  # back plate against the fascia
    if p["hook"] != "none":
        P.box("body", -sw / 2, sw / 2, yt - hook_h, yt, D - t, D)
    if p["hook"] == "c":
        hd = p["hook_d"] * D
        P.box("body", -sw / 2, sw / 2, yt - hook_h, yt - hook_h + t, D - hd, D)
        P.box("body", -sw / 2, sw / 2, yt - hook_h, yt - hook_h + 0.35 * hook_h, D - hd, D - hd + t)
    if p["brace"]:
        a = np.array([0, yt - back_h + t, t])
        b = np.array([0, yt - t, 0.5 * D])
        v = b - a
        m_ = trimesh.creation.box(extents=[0.7 * sw, t, np.linalg.norm(v)])
        m_.apply_transform(
            rot([1, 0, 0], -math.degrees(math.atan2(v[1], v[2])))
        )  # keeps width on X
        m_.apply_translation((a + b) / 2)
        P.add("body", m_)
    if p["screw"]:
        r = min(p["screw_d"] * H / 2, W / 2 * 0.9)
        y = yt - t - r
        z1 = D - t
        P.cyl("screw", [0, y, D * (1 - p["screw_len"])], [0, y, z1 + EPS], r * 0.6, 16)
        P.cyl("screw", [0, y, z1 - 0.25 * r], [0, y, z1 + EPS], r, 16)


def _strip_path(P: Parts, pts, t, sw):
    """Sheet strip of thickness t and width sw (along X) swept along a side-view polyline of
    (z, y) points: one box per segment, a round bend (X cylinder, r = t/2) at every point."""
    for a, b in itertools.pairwise(pts):
        v = np.subtract(b, a)
        ln = float(np.linalg.norm(v))
        if ln < EPS:
            continue
        m = trimesh.creation.box(extents=[sw, t, ln])  # length along Z, thickness along Y
        m.apply_transform(rot([1, 0, 0], -math.degrees(math.atan2(v[1], v[0]))))
        m.apply_translation([0, (a[1] + b[1]) / 2, (a[0] + b[0]) / 2])
        P.add("body", m)
    for z, y in pts:
        P.cyl("body", [-sw / 2, y, z], [sw / 2, y, z], t / 2, 16)


def t_bent_strip(P: Parts, W, H, D, p):
    t = min(p["thickness_mm"] / 1000, 0.3 * H, 0.3 * D)
    paths = [pl for pl in (p["profile"], p["profile2"]) if len(pl) >= 2]
    allp = np.array([q for pl in paths for q in pl], float)
    lo, span = allp.min(axis=0), np.maximum(np.ptp(allp, axis=0), 1e-6)
    plate = p["back_plate_h"] > 0.05
    sw = p["strip_w"] * W if plate else W  # without a plate the strip alone spans W

    def to_frame(pl):  # fractions -> centreline inset by t/2 so the strip fills the box exactly
        q = (np.array(pl, float) - lo) / span
        return np.c_[t / 2 + q[:, 0] * (D - t), -H / 2 + t / 2 + q[:, 1] * (H - t)]

    for pl in paths:
        _strip_path(P, to_frame(pl), t, sw)
    if plate:  # wider plate against the mounting face, from the top down
        P.box("body", -W / 2, W / 2, H / 2 - p["back_plate_h"] * H, H / 2, 0, t)
    if p["screw"]:
        r = min(p["screw_d"] * H / 2, W / 2 * 0.9, H / 2 * 0.9)
        y = min(max(-H / 2 + p["screw_y"] * H, -H / 2 + r), H / 2 - r)
        z0, z1 = sorted((p["screw_z0"] * D, p["screw_z1"] * D))
        z1 = max(z1, z0 + 2 * r)
        P.cyl("screw", [0, y, z0], [0, y, z1], r * 0.55, 16)
        P.cyl("screw", [0, y, z1 - 0.6 * r], [0, y, z1], r, 6)  # hex head


def t_sink(P: Parts, W, H, D, p):
    fl = p["flange"] * min(W, D)
    rim = p["rim_mm"] / 1000
    wall = p["wall_mm"] / 1000
    bx0, bx1, bz0, bz1 = -W / 2 + fl, W / 2 - fl, fl, D - fl
    r = p["corner_r"] * min(bx1 - bx0, bz1 - bz0)
    solid = [rrect_y(bx0, bx1, bz0, bz1, -H / 2, H / 2 - rim + EPS, r)]
    if fl > 1e-3:
        solid.append(rrect_y(-W / 2, W / 2, 0, D, H / 2 - rim, H / 2, r + fl))
    ix0, ix1, iz0, iz1 = bx0 + wall, bx1 - wall, bz0 + wall, bz1 - wall
    if p["bowls"] == 2:
        xs = ix0 + p["split"] * (ix1 - ix0)
        spans = [(ix0, xs - wall), (xs + wall, ix1)]
    else:
        spans = [(ix0, ix1)]
    ri = max(r - wall, 0)
    cav = [rrect_y(a, b, iz0, iz1, -H / 2 + wall, H / 2 + 0.01, ri) for a, b in spans]
    P.add("body", cut(union(solid), cav))
    floor = -H / 2 + wall
    for a, b in spans:
        zc = {
            "center": (iz0 + iz1) / 2,
            "back": iz0 + 0.25 * (iz1 - iz0),
            "front": iz0 + 0.75 * (iz1 - iz0),
        }[p["drain"]]
        rd = min(p["drain_r"] * W, 0.4 * (b - a), 0.4 * (iz1 - iz0))
        P.cyl("drain", [(a + b) / 2, floor - EPS, zc], [(a + b) / 2, floor + 0.002, zc], rd)


def t_fixture(P: Parts, W, H, D, p):
    if p["kind"] == "shower_head":
        Rf = min(W, H) / 2
        fp = min(0.03 * D, 0.004)
        zb = D * (1 - p["face_d"])  # back of the head
        zn = zb - 0.15 * D  # neck / ball joint
        rn = p["arm_r"] * W * 1.6
        prof = [[0, zn], [rn, zn], [Rf * p["back_taper"], zb], [Rf, D - 0.4 * p["face_d"] * D]]
        prof += [[Rf, D], [0.86 * Rf, D], [0.86 * Rf, D - fp], [0, D - fp]]
        P.add("body", trimesh.creation.revolve(prof, sections=48))
        P.cyl("body", [0, 0, 0], [0, 0, zn + EPS], p["arm_r"] * W)
        ball = trimesh.creation.icosphere(subdivisions=2, radius=rn * 1.1)
        ball.apply_translation([0, 0, zn])
        P.add("body", ball)
        P.cyl("face", [0, 0, D - fp - EPS], [0, 0, D - fp / 2], 0.86 * Rf, 48)
        if p["center_disc"]:
            P.cyl("body", [0, 0, D - fp - EPS], [0, 0, D - fp * 0.1], 0.25 * Rf)
        for k in range(p["nozzle_rings"]):
            rr = Rf * (0.35 + 0.45 * (k + 1) / p["nozzle_rings"])
            n = int(2 * math.pi * rr / (0.09 * Rf))
            for j in range(n):
                a = 2 * math.pi * j / n
                c = [rr * math.cos(a), rr * math.sin(a)]
                P.cyl("nozzle", [*c, D - fp / 2 - EPS], [*c, D - fp * 0.1], 0.025 * Rf, 12)
        return
    # faucet: base on the deck (bottom), body up, spout out toward +Z
    # body axis at the left edge + base radius; a single lever reaches the right edge (+X)
    br = min(W / 2 * (p["base_r"] if p["handle"] != "none" else 1.0), D / 2)
    zc, xc = br, -W / 2 + br
    sr = p["spout_r"] * W / 2
    ybase = -H / 2 + 0.05 * H
    P.cyl("body", [xc, -H / 2, zc], [xc, ybase, zc], br, 48)
    brad = p["body_r"] * W / 2
    ytop_body = -H / 2 + p["body_h"] * H
    P.cyl("body", [xc, ybase - EPS, zc], [xc, ytop_body, zc], brad, 32)
    yend = -H / 2 + p["spout_end"] * H
    z_end = D - sr
    if p["spout"] == "arc":
        ra = max((z_end - zc) / 2, sr)
        zm, ycen = (zc + z_end) / 2, H / 2 - sr - ra
        pts = [[xc, ytop_body - sr, zc]]
        pts += [
            [xc, ycen + ra * math.sin(a), zm - ra * math.cos(a)]
            for a in np.linspace(0, math.pi, 13)
        ]
        pts += [[xc, yend, z_end]]
    else:
        pts = [
            [xc, ytop_body - sr, zc],
            [xc, H / 2 - sr, zc],
            [xc, H / 2 - sr, z_end],
            [xc, yend, z_end],
        ]
    for a, b in itertools.pairwise(pts):
        if np.linalg.norm(np.subtract(a, b)) > EPS:
            P.cyl("body", a, b, sr, 16)
        s = trimesh.creation.icosphere(subdivisions=1, radius=sr)
        s.apply_translation(b)
        P.add("body", s)
    P.cyl("nozzle", [xc, yend - 1.05 * sr, z_end], [xc, yend - 0.5 * sr, z_end], sr * 0.7)
    yh = ytop_body - 0.3 * (ytop_body - ybase)
    if p["handle"] == "lever":
        P.box(
            "handle",
            xc + brad * 0.5,
            W / 2,
            yh - 0.02 * H,
            yh + 0.02 * H,
            zc - 0.25 * br,
            zc + 0.25 * br,
        )
    elif p["handle"] == "knob":
        P.cyl("handle", [xc + brad * 0.5, yh, zc], [W / 2, yh, zc], 0.25 * br)


def t_flat_panel(P: Parts, W, H, D, p):
    fw = p["frame_w"] * W
    lip = min(0.004, 0.2 * D) if fw > 1e-4 else 0.001
    zg1 = D - lip
    zg0 = max(zg1 - max(0.004, 0.15 * D), 0) if fw > 1e-4 else 0  # glass; frameless = solid
    if fw > 1e-4:
        P.box("frame", -W / 2, -W / 2 + fw, -H / 2, H / 2, 0, D)
        P.box("frame", W / 2 - fw, W / 2, -H / 2, H / 2, 0, D)
        P.box("frame", -W / 2 + fw - EPS, W / 2 - fw + EPS, -H / 2, -H / 2 + fw, 0, D)
        P.box("frame", -W / 2 + fw - EPS, W / 2 - fw + EPS, H / 2 - fw, H / 2, 0, D)
    x0, x1, y0, y1 = -W / 2 + fw, W / 2 - fw, -H / 2 + fw, H / 2 - fw
    P.box("backsheet", x0 - EPS, x1 + EPS, y0 - EPS, y1 + EPS, zg0, zg1)
    nx, ny = p["cells_x"], p["cells_y"]
    px, py = (x1 - x0) / nx, (y1 - y0) / ny
    gx, gy = px * p["cell_gap"] / 2, py * p["cell_gap"] / 2
    ztop = min(zg1 + 0.001, D)
    for i in range(nx):
        for j in range(ny):
            cx0, cy0 = x0 + i * px, y0 + j * py
            P.box("cell", cx0 + gx, cx0 + px - gx, cy0 + gy, cy0 + py - gy, zg1 - EPS, ztop)


def _fan(P: Parts, R, depth, gp, p, place):
    """One fan built along +Z (face plane z=0, recess into -Z), moved by the 4x4 `place`.
    Returns the recess cutter (in place)."""
    parts = []
    parts.append(("recess", cyl([0, 0, -depth], [0, 0, -depth + 0.002], R * 0.99, 48)))
    parts.append(("blade", cyl([0, 0, -depth], [0, 0, -depth * 0.35], 0.2 * R, 24)))
    for k in range(p["blades"]):
        b = trimesh.creation.box(extents=[0.85 * R, 0.35 * R, 0.006])
        b.apply_transform(rot([1, 0, 0], 25))
        b.apply_translation([0.425 * R, 0, -depth * 0.55])  # root inside the 0.2R hub
        b.apply_transform(rot([0, 0, 1], 360 / p["blades"] * k))
        parts.append(("blade", b))
    rw = min(0.012, 0.04 * R)
    nr = p["grille_rings"]
    for k in range(nr):
        ro = R * (0.25 + 0.75 * (k + 1) / nr) + (rw if k == nr - 1 else 0)
        parts.append(
            (
                "grille",
                trimesh.creation.annulus(
                    ro - rw, ro, segment=[[0, 0, -EPS], [0, 0, gp]], sections=64
                ),
            )
        )
    if nr or p["grille_spokes"]:
        ns = max(p["grille_spokes"], 2 if nr else 0)
        for k in range(ns):
            s = trimesh.creation.box(extents=[2 * (R + rw * 0.5), rw * 0.8, gp + EPS])
            s.apply_translation([0, 0, gp / 2 - EPS / 2])
            s.apply_transform(rot([0, 0, 1], 180 / ns * k))
            parts.append(("grille", s))
        parts.append(("grille", cyl([0, 0, -EPS], [0, 0, gp], 0.2 * R, 24)))
    for role, m in parts:
        m.apply_transform(place)
        P.add(role, m)
    c = cyl([0, 0, -depth], [0, 0, 0.05], R, 64)
    c.apply_transform(place)
    return c


def t_fan_unit(P: Parts, W, H, D, p):
    gp = min(0.008, 0.02 * min(W, H, D))
    base = p["base_h"] * H
    cap = p["cap_h"] * H
    top = p["fan_face"] == "top"
    lv = min(0.008, 0.02 * min(W, D)) if (top and p["side_louvres"]) else 0.0
    zf = D - (0 if top else gp)  # front face plane
    yt = H / 2 - (gp if top else 0)  # top face plane
    housing = []
    if base:
        P.box("base", -W / 2, W / 2, -H / 2, -H / 2 + base, 0, zf)
    housing.append(
        ("body", box(-W / 2 + lv, W / 2 - lv, -H / 2 + base - EPS, yt - cap, lv, zf - lv))
    )
    if cap:
        housing.append(("cap", box(-W / 2, W / 2, yt - cap - EPS, yt, 0, zf)))
    if lv:  # corner posts + vertical coil louvres on the four sides
        cw = 0.05 * min(W, D)
        y0, y1 = -H / 2 + base - EPS, yt - cap + EPS
        for sx in (-1, 1):
            for z in (0, D - cw):
                x = -W / 2 if sx < 0 else W / 2 - cw
                P.box("body", x, x + cw, y0, y1, z, z + cw)
        n = p["louvre_count"]
        for i in range(n):
            f = (i + 0.5) / n
            x = -W / 2 + cw + f * (W - 2 * cw)
            z = cw + f * (D - 2 * cw)
            for zz in ((0, lv + EPS), (D - lv - EPS, D)):
                P.box("louvre", x - 0.3 * lv, x + 0.3 * lv, y0, y1, *zz)
            for xx in ((-W / 2, -W / 2 + lv + EPS), (W / 2 - lv - EPS, W / 2)):
                P.box("louvre", *xx, y0, y1, z - 0.3 * lv, z + 0.3 * lv)
    n = p["fans"]
    cutters = []
    if top:
        R = p["fan_r"] * min(W, D) / (max(1, n))
        depth = min(0.4 * H, 0.25)
        for k in range(n):
            u = p["fan_cx"] if n == 1 else (k + 0.5) / n
            place = trimesh.transformations.translation_matrix([-W / 2 + u * W, yt, D / 2]) @ rot(
                [1, 0, 0], -90
            )
            cutters.append(_fan(P, R, depth, gp, p, place))
    else:
        R = p["fan_r"] * min(W, H) / (max(1, n))
        depth = min(0.4 * D, 0.2)
        for k in range(n):
            u = p["fan_cx"] if n == 1 else (k + 0.5) / n
            place = trimesh.transformations.translation_matrix(
                [-W / 2 + u * W, -H / 2 + p["fan_cy"] * H, zf]
            )
            cutters.append(_fan(P, R, depth, gp, p, place))
        if p["service_panel"]:
            x0, x1, y0, y1 = region(p["panel_region"], W, H)
            P.box("panel", x0, x1, y0, y1, zf - EPS, zf + gp * 0.5)
    for role, m in housing:
        hit = [
            c
            for c in cutters
            if np.all(c.bounds[0] < m.bounds[1]) and np.all(m.bounds[0] < c.bounds[1])
        ]
        P.add(role, cut(m, hit))


def _sash(P: Parts, x0, x1, y0, y1, z0, z1, sw, p, lock=None):
    """One sash: a profile ring `sw` wide (none when 0) round a glass pane at mid depth, grille
    bars on the glass, and a lock / latch centred at `lock` = (x, y, vertical) on its front."""
    sw = min(sw, 0.3 * (x1 - x0), 0.3 * (y1 - y0))
    if sw > 1e-4:
        P.box("sash", x0, x0 + sw, y0, y1, z0, z1)
        P.box("sash", x1 - sw, x1, y0, y1, z0, z1)
        P.box("sash", x0 + sw - EPS, x1 - sw + EPS, y0, y0 + sw, z0, z1)
        P.box("sash", x0 + sw - EPS, x1 - sw + EPS, y1 - sw, y1, z0, z1)
    gx0, gx1, gy0, gy1 = x0 + sw - EPS, x1 - sw + EPS, y0 + sw - EPS, y1 - sw + EPS
    gt = min(0.006, 0.3 * (z1 - z0))
    zm = (z0 + z1) / 2
    P.box("glass", gx0, gx1, gy0, gy1, zm - gt / 2, zm + gt / 2)
    bar = min(max(0.35 * sw, 0.008), 0.02) if sw > 1e-4 else 0.01
    zb0, zb1 = zm - gt / 2 - 0.002, min(zm + gt / 2 + 0.004, z1)
    for i in range(p["grid_x"]):
        x = gx0 + (i + 1) * (gx1 - gx0) / (p["grid_x"] + 1)
        P.box("grid", x - bar / 2, x + bar / 2, gy0, gy1, zb0, zb1)
    for j in range(p["grid_y"]):
        y = gy0 + (j + 1) * (gy1 - gy0) / (p["grid_y"] + 1)
        P.box("grid", gx0, gx1, y - bar / 2, y + bar / 2, zb0, zb1)
    if p["hardware"] and lock is not None:
        lx, ly, vertical = lock
        lw, lh = min(0.06, 0.2 * (x1 - x0), 0.2 * (y1 - y0)), min(0.012, 0.5 * max(sw, 0.01))
        hx, hy = (lh / 2, lw / 2) if vertical else (lw / 2, lh / 2)
        P.box("hardware", lx - hx, lx + hx, ly - hy, ly + hy, z1 - EPS, z1 + min(0.01, 0.002 + lh))


def t_window(P: Parts, W, H, D, p):
    """assetgen: a window unit (or a replacement window frame). The outer frame fills W x H x D;
    sashes sit in it a little behind its front face (hung: the upper sash behind the lower one;
    slider: the fixed half behind), glass at each sash's mid depth."""
    m = min(W, H)
    fw = min(max(p["frame_w"] * m, 0.01), 0.3 * m)
    P.box("frame", -W / 2, -W / 2 + fw, -H / 2, H / 2, 0, D)
    P.box("frame", W / 2 - fw, W / 2, -H / 2, H / 2, 0, D)
    P.box("frame", -W / 2 + fw - EPS, W / 2 - fw + EPS, H / 2 - fw, H / 2, 0, D)
    P.box("frame", -W / 2 + fw - EPS, W / 2 - fw + EPS, -H / 2, -H / 2 + fw, 0, D)
    x0, x1, y0, y1 = -W / 2 + fw - EPS, W / 2 - fw + EPS, -H / 2 + fw - EPS, H / 2 - fw + EPS
    p = {**p, "hardware": p["hardware"] and D >= 0.03}  # no room for a lock on a thin frame
    lock_room = 0.012 if p["hardware"] else 0.0  # the lock stands proud: keep it inside Z <= D
    zf = max(D - max(0.08 * D, 0.003, lock_room), 0.5 * D)  # front sash face, set back
    sd = min(max(0.22 * D, 0.012), 0.05, zf / 2.2)  # sash depth
    sw = p["sash_w"] * m
    style = p["style"]
    if style in ("double_hung", "single_hung"):
        ym = y0 + p["split"] * (y1 - y0)
        ov = min(max(sw, 0.01) * 0.5, 0.1 * (y1 - y0))  # the meeting rails overlap
        _sash(P, x0, x1, ym - ov, y1, zf - 2 * sd, zf - sd, sw, p)
        rail = min(max(sw, 0.01), 0.3 * (ym + ov - y0))
        _sash(P, x0, x1, y0, ym + ov, zf - sd, zf, sw, p, lock=(0.0, ym + ov - rail / 2, False))
    elif style == "slider":
        xm = x0 + p["split"] * (x1 - x0)
        ov = min(max(sw, 0.01) * 0.5, 0.1 * (x1 - x0))
        _sash(P, xm - ov, x1, y0, y1, zf - 2 * sd, zf - sd, sw, p)
        stile = min(max(sw, 0.01), 0.3 * (xm + ov - x0))
        _sash(P, x0, xm + ov, y0, y1, zf - sd, zf, sw, p, lock=(xm + ov - stile / 2, 0.0, True))
    else:  # casement / fixed: `panes` sashes side by side in one plane, mullions between them
        n = p["panes"]
        mull = min(max(fw * 0.8, 0.01), 0.2 * (x1 - x0) / n) if n > 1 else 0.0
        pw = (x1 - x0 - (n - 1) * mull) / n
        for k in range(n):
            a = x0 + k * (pw + mull)
            stile = min(max(sw, 0.01), 0.3 * pw)
            lock = (a + pw - stile / 2, 0.0, True) if style == "casement" else None
            _sash(P, a, a + pw, y0, y1, zf - sd, zf, sw, p, lock=lock)
            if k:
                P.box("frame", a - mull - EPS, a + EPS, y0, y1, 0, D)


TEMPLATES = {
    "box": (
        t_box,
        "plain box: slab, pad, cabinet, anything else",
        {
            "inset": ("b", False, "accent panel on the front face"),
            "inset_region": ("r", [0.05, 0.95, 0.05, 0.95], "accent panel region"),
            "feet": ("b", False, "4 feet"),
            "feet_h": ("f", 0.03, 0.0, 0.1, "feet height, frac of H"),
        },
        {"body": "#CCCCCC", "accent": "#333333", "feet": "#222222"},
    ),
    "box_appliance": (
        t_box_appliance,
        "box appliance with front grilles/louvres and a control strip: window/wall AC, dehumidifier, microwave",
        {
            "front_frac": (
                "f",
                0.35,
                0.05,
                1.0,
                "depth of the front fascia, frac of D; the rest is a rear cabinet/sleeve",
            ),
            "sleeve_inset": ("f", 0.02, 0.0, 0.1, "rear cabinet inset per side, frac of W/H"),
            "grille1_region": ("r", [0.05, 0.95, 0.35, 0.9], "main grille region"),
            "grille1_style": ("e", "louvre_h", ["louvre_h", "louvre_v", "mesh", "none"], ""),
            "grille1_count": ("i", 12, 3, 40, "slats"),
            "grille2_region": ("r", [0.05, 0.95, 0.05, 0.3], "second grille region"),
            "grille2_style": ("e", "none", ["louvre_h", "louvre_v", "mesh", "none"], ""),
            "grille2_count": ("i", 8, 3, 40, "slats"),
            "control": ("b", True, "control panel strip"),
            "control_region": ("r", [0.7, 0.95, 0.85, 0.95], "control panel region"),
            "feet": ("b", False, ""),
        },
        {
            "body": "#F2F2F2",
            "sleeve": "#BFBFBF",
            "grille_bg": "#2A2A2A",
            "control": "#3A3A3A",
            "feet": "#222222",
        },
    ),
    "tall_box": (
        t_tall_box,
        "tall appliance with doors and handles: refrigerator, freezer, tall cabinet",
        {
            "layout": (
                "e",
                "french3",
                [
                    "french3",
                    "french4",
                    "french4_stacked",
                    "side_by_side",
                    "top_freezer",
                    "bottom_freezer",
                    "single",
                ],
                "french3=2 top doors+1 drawer; french4=2 top+2 bottom side by side; french4_stacked=2 top+2 stacked drawers",
            ),
            "split": ("f", 0.55, 0.25, 0.8, "upper door section height, frac of door area"),
            "handle": ("e", "bar", ["bar", "recessed", "none"], ""),
            "handle_len": ("f", 0.6, 0.2, 0.95, "handle length, frac of its door"),
            "dispenser": ("b", False, "ice/water dispenser on the left top door"),
            "base_h": ("f", 0.05, 0.0, 0.12, "toe-kick grille height, frac of H"),
        },
        {
            "body": "#8E9194",
            "door": "#C9CBCC",
            "handle": "#D9DADB",
            "trim": "#2A2A2A",
            "base": "#202020",
        },
    ),
    "cylinder_tank": (
        t_cylinder_tank,
        "vertical cylinder: water heater, tank, drum",
        {
            "top": ("e", "flat", ["flat", "dome"], "top cap shape"),
            "top_h": ("f", 0.03, 0.0, 0.2, "top cap height, frac of H"),
            "pipes": ("i", 2, 0, 4, "pipes on top"),
            "pipe_h": ("f", 0.04, 0.0, 0.15, "pipe height above the top, frac of H"),
            "bands": ("i", 2, 0, 4, "jacket bands/rings"),
            "base_h": ("f", 0.02, 0.0, 0.15, "dark base ring height, frac of H"),
            "panels": ("i", 2, 0, 3, "small access panels on the front"),
            "label": ("b", True, "big label on the front"),
        },
        {
            "jacket": "#9B9EA0",
            "top": "#7A7D80",
            "band": "#6A6D70",
            "pipe": "#B87333",
            "base": "#333333",
            "panel": "#8A8D90",
            "label": "#E8E8E8",
        },
    ),
    "joist_hanger": (
        t_joist_hanger,
        "bent sheet-metal U hanger: seat + 2 side walls + 2 face flanges (joist hanger, U bracket)",
        {
            "seat_w": ("f", 0.45, 0.2, 0.9, "gap between side walls, frac of W"),
            "thickness_mm": ("f", 1.5, 0.6, 4.0, "sheet thickness"),
            "wall_h": ("f", 1.0, 0.3, 1.0, "side wall height, frac of H"),
            "flange_h": ("f", 1.0, 0.3, 1.0, "face flange height, frac of H"),
            "flange_holes": ("i", 3, 0, 8, "nail holes per flange"),
            "wall_holes": ("i", 2, 0, 6, "holes per side wall"),
        },
        {"metal": "#A9AEB2"},
    ),
    "strap_hanger": (
        t_strap_hanger,
        "thin strap/bracket running along the depth: gutter hanger, strap, shelf bracket; back plate at the wall, hook at the front",
        {
            "strip_w": ("f", 1.0, 0.3, 1.0, "strap width, frac of W"),
            "thickness_mm": ("f", 2.0, 0.8, 8.0, "strap thickness"),
            "back_h": ("f", 0.8, 0.1, 1.0, "back plate height, frac of H"),
            "hook": ("e", "c", ["c", "lip", "none"], "front end: c=C-hook, lip=straight drop"),
            "hook_h": ("f", 0.6, 0.1, 1.0, "front hook drop, frac of H"),
            "hook_d": ("f", 0.12, 0.03, 0.3, "C-hook depth, frac of D"),
            "brace": ("b", False, "diagonal brace from back plate to strap"),
            "screw": ("b", True, "screw along the strap"),
            "screw_len": ("f", 0.5, 0.1, 1.0, "frac of D"),
            "screw_d": ("f", 0.25, 0.05, 0.5, "screw diameter, frac of H"),
        },
        {"body": "#C0C4C8", "screw": "#8A8A8A"},
    ),
    "bent_strip": (
        t_bent_strip,
        (
            "bent sheet-metal or moulded strip drawn as a side-view polyline (Z = depth from the"
            " mounting face, Y = height): gutter hanger, hook, clip, strap bracket, Z/L bracket"
        ),
        {
            "profile": (
                "p",
                [[0, 0.3], [0, 1], [0.55, 1], [0.65, 0.35], [1, 0.35], [1, 0.8]],
                (
                    "strip centreline as [z, y] points, fractions of D (0 = mounting face,"
                    " 1 = front) and H (0 = bottom), in order along the strip, 2-12 points, bends included"
                ),
            ),
            "profile2": ("p", [], "optional second strip (e.g. a diagonal brace), same format"),
            "thickness_mm": ("f", 1.5, 0.5, 8.0, "strip thickness"),
            "strip_w": ("f", 0.7, 0.2, 1.0, "strip width, frac of W (only with a back plate)"),
            "back_plate_h": (
                "f",
                0.0,
                0.0,
                1.0,
                "wider plate on the mounting face, frac of H from the top (0 = none)",
            ),
            "screw": ("b", False, "screw along Z"),
            "screw_y": ("f", 0.8, 0.0, 1.0, "screw height, frac of H"),
            "screw_z0": ("f", 0.0, 0.0, 1.0, "screw start, frac of D"),
            "screw_z1": ("f", 0.5, 0.0, 1.0, "screw head end, frac of D"),
            "screw_d": ("f", 0.2, 0.05, 0.5, "screw head diameter, frac of H"),
        },
        {"body": "#C0C4C8", "screw": "#8A8A8A"},
    ),
    "sink": (
        t_sink,
        "sink / basin / tub: hollow bowl open to the top (+Y), optional rim flange",
        {
            "bowls": ("i", 1, 1, 2, ""),
            "split": ("f", 0.5, 0.3, 0.7, "divider position for 2 bowls, frac of W"),
            "flange": ("f", 0.03, 0.0, 0.15, "rim flange width, frac of min(W, D)"),
            "rim_mm": ("f", 3.0, 1.0, 20.0, "rim flange thickness"),
            "wall_mm": ("f", 3.0, 1.0, 20.0, "bowl wall thickness"),
            "corner_r": ("f", 0.08, 0.0, 0.3, "corner radius, frac of bowl size"),
            "drain": ("e", "center", ["center", "back", "front"], ""),
            "drain_r": ("f", 0.045, 0.02, 0.1, "drain radius, frac of W"),
        },
        {"body": "#B8BCC0", "drain": "#3A3A3A"},
    ),
    "fixture": (
        t_fixture,
        "plumbing fixture: shower head (round face toward +Z, arm to the wall) or faucet (base, body, spout toward +Z)",
        {
            "kind": ("e", "shower_head", ["shower_head", "faucet"], ""),
            "face_d": ("f", 0.35, 0.1, 0.6, "shower head thickness, frac of D"),
            "back_taper": ("f", 0.45, 0.2, 1.0, "shower head back radius / face radius"),
            "arm_r": ("f", 0.1, 0.05, 0.25, "shower arm radius, frac of W"),
            "nozzle_rings": ("i", 2, 0, 4, ""),
            "center_disc": ("b", True, "shower face centre disc"),
            "base_r": ("f", 0.5, 0.2, 1.0, "faucet base radius, frac of W/2"),
            "body_r": ("f", 0.3, 0.1, 0.8, "faucet body radius, frac of W/2"),
            "body_h": ("f", 0.5, 0.2, 0.95, "faucet body height, frac of H"),
            "spout": ("e", "arc", ["arc", "straight"], "faucet spout shape"),
            "spout_r": ("f", 0.15, 0.05, 0.4, "spout radius, frac of W/2"),
            "spout_end": ("f", 0.6, 0.2, 0.95, "spout outlet height, frac of H"),
            "handle": ("e", "lever", ["lever", "knob", "none"], "faucet handle"),
        },
        {"body": "#C8CCD0", "face": "#E0E3E6", "nozzle": "#3A3A3A", "handle": "#C8CCD0"},
    ),
    "flat_panel": (
        t_flat_panel,
        "thin framed panel facing +Z: solar panel, sign, glass panel (a window: use window)",
        {
            "frame_w": ("f", 0.02, 0.0, 0.1, "frame width, frac of W"),
            "cells_x": ("i", 6, 1, 24, "cell columns"),
            "cells_y": ("i", 10, 1, 40, "cell rows"),
            "cell_gap": ("f", 0.08, 0.01, 0.4, "gap between cells, frac of cell pitch"),
        },
        {"frame": "#B8BCC0", "backsheet": "#E8E8E8", "cell": "#1B2233"},
    ),
    # assetgen: windows (a window frame / replacement window), E5-style photo mode drops the grille
    "window": (
        t_window,
        (
            "window unit or replacement window frame: outer frame, sash(es) with glass panes; "
            "double or single hung, slider, casement, fixed / picture. W x H = the frame's outside "
            "size, D = the frame depth"
        ),
        {
            "style": (
                "e",
                "double_hung",
                ["double_hung", "single_hung", "slider", "casement", "fixed"],
                "",
            ),
            "frame_w": ("f", 0.05, 0.015, 0.15, "outer frame member width, frac of min(W, H)"),
            "sash_w": (
                "f",
                0.045,
                0.0,
                0.12,
                "sash profile width, frac of min(W, H); 0 = glass straight in the frame",
            ),
            "split": (
                "f",
                0.5,
                0.3,
                0.7,
                "meeting rail height (hung) or meeting stile (slider), frac of the opening",
            ),
            "panes": ("i", 1, 1, 4, "sashes side by side (casement, fixed)"),
            "grid_x": ("i", 0, 0, 6, "vertical grille bars per sash"),
            "grid_y": ("i", 0, 0, 6, "horizontal grille bars per sash"),
            "hardware": ("b", True, "sash lock / casement latch"),
        },
        {
            "frame": "#F4F4F2",
            "sash": "#F4F4F2",
            "glass": "#A9C0CE",
            "grid": "#F4F4F2",
            "hardware": "#9A9A9A",
        },
    ),
    "fan_unit": (
        t_fan_unit,
        "housing with circular fan grille(s): AC condenser (fan on top, coil louvres on the sides), mini-split outdoor unit (fan on the front), fan",
        {
            "fan_face": ("e", "front", ["front", "top"], "face the fan blows through"),
            "fans": ("i", 1, 1, 2, ""),
            "fan_r": ("f", 0.4, 0.15, 0.49, "fan radius, frac of the smaller face dimension"),
            "fan_cx": ("f", 0.5, 0.2, 0.8, "fan centre across the face (0=left, 1=right)"),
            "fan_cy": ("f", 0.5, 0.2, 0.8, "front fan centre height (0=bottom)"),
            "grille_rings": ("i", 5, 0, 12, ""),
            "grille_spokes": ("i", 4, 0, 16, ""),
            "blades": ("i", 3, 2, 7, ""),
            "side_louvres": ("b", False, "vertical coil louvres on the 4 sides (top-fan units)"),
            "louvre_count": ("i", 30, 5, 80, "louvres per side"),
            "cap_h": ("f", 0.0, 0.0, 0.2, "top cap band height, frac of H"),
            "base_h": ("f", 0.0, 0.0, 0.15, "base height, frac of H"),
            "service_panel": ("b", False, "side service panel on the front"),
            "panel_region": ("r", [0.82, 1.0, 0.1, 0.9], "service panel region"),
        },
        {
            "body": "#E8E8E8",
            "grille": "#2B2B2B",
            "blade": "#3C4A5A",
            "recess": "#151515",
            "panel": "#D0D0D0",
            "louvre": "#3A3A3A",
            "cap": "#2A2A2A",
            "base": "#2A2A2A",
        },
    ),
}

METALLIC = {
    "handle": 0.5,
    "pipe": 0.5,
    "metal": 0.6,
    "frame": 0.5,
    "screw": 0.6,
    "hardware": 0.5,
}  # role -> metallic

# assetgen: a role's finish when the answer gives none (glass reads as glass, not matte paint).
DEFAULT_FINISH = {"glass": "glass"}

# Optional per-role finish (E5): name -> (metallicFactor, roughnessFactor). Metallic stays <= 0.8
# because a viewer without an environment map (pyrender here, a Quest passthrough scene without a
# reflection probe) renders full metal almost black; metal albedo is lifted to a bright
# specular colour (METAL_MIN_LUM) for the same reason.
FINISHES = {
    "chrome": (0.8, 0.12),
    "stainless": (0.7, 0.3),
    "brushed": (0.6, 0.4),
    "galvanized": (0.55, 0.5),
    "painted": (0.0, 0.5),
    "plastic": (0.0, 0.6),
    "rubber": (0.0, 0.9),
    "glass": (0.0, 0.08),
}
METAL_MIN_LUM = 0.5  # linear


def material(color_hex: str, role: str, finish: str | None = None):
    """PBR material for a role; a known `finish` overrides the METALLIC role defaults."""
    rgba = srgb_to_linear(color_hex)
    if finish in FINISHES:
        metallic, rough = FINISHES[finish]
        lum = sum(rgba[:3]) / 3
        if metallic >= 0.5 and lum < METAL_MIN_LUM:
            k = METAL_MIN_LUM / max(lum, 1e-3)
            rgba = [min(c * k, 1.0) for c in rgba[:3]] + [1.0]
    else:
        metallic, rough = METALLIC.get(role, 0.1), (0.35 if role in METALLIC else 0.6)
    return trimesh.visual.material.PBRMaterial(
        baseColorFactor=rgba, metallicFactor=metallic, roughnessFactor=rough
    )


def validate(template: str, params: dict | None, colors: dict | None) -> tuple[str, dict, dict]:
    """Clip/cast LLM params to the template spec; unknown template -> "box"."""
    if template not in TEMPLATES:
        template = "box"
    _, _, spec, roles = TEMPLATES[template]
    params, colors = params or {}, colors or {}
    out = {}
    for k, s in spec.items():
        v, kind = params.get(k), s[0]
        try:
            if kind in ("f", "i"):
                v = min(max(float(v), s[2]), s[3])
                v = round(v) if kind == "i" else v
            elif kind == "b":
                v = v if isinstance(v, bool) else str(v).lower() in ("true", "1", "yes")
                v = s[1] if params.get(k) is None else v
            elif kind == "e":
                v = v if v in s[2] else s[1]
            elif kind == "p":  # polyline of [a, b] fraction pairs
                v = [[min(max(float(a), 0.0), 1.0), min(max(float(b), 0.0), 1.0)] for a, b in v]
                v = v[:12] if len(v) >= 2 else ([] if s[1] == [] else s[1])
            elif kind == "r":
                v = [min(max(float(x), 0.0), 1.0) for x in v][:4]
                v = [min(v[0], v[1]), max(v[0], v[1]), min(v[2], v[3]), max(v[2], v[3])]
                if v[1] - v[0] < 0.02 or v[3] - v[2] < 0.02:
                    raise ValueError
        except (TypeError, ValueError, IndexError):
            v = s[1]
        out[k] = v
    cols = {
        r: (c if isinstance(c, str) and HEX.match(c) else d)
        for r, d in roles.items()
        for c in [colors.get(r)]
    }
    cols = {r: "#" + c.lstrip("#").upper() for r, c in cols.items()}
    return template, out, cols


def catalogue() -> str:
    """Compact template catalogue for the prompt."""
    lines = []
    for name, (_, desc, spec, roles) in TEMPLATES.items():
        ps = []
        for k, s in spec.items():
            kind, help_ = s[0], s[-1]
            if kind in ("f", "i"):
                t = f"{k}={s[1]} [{s[2]}..{s[3]}]"
            elif kind == "e":
                t = f"{k}={s[1]} ({'|'.join(s[2])})"
            else:
                t = f"{k}={str(s[1]).lower() if kind == 'b' else json.dumps(s[1])}"
            ps.append(t + (f" {help_}" if help_ else ""))
        lines.append(
            f"- {name}: {desc}\n  params: " + "; ".join(ps) + "\n  colors: " + ", ".join(roles)
        )
    return "\n".join(lines)


def build(
    template: str,
    dims_mm: dict,
    params: dict | None = None,
    colors: dict | None = None,
    finishes: dict | None = None,
):
    """-> (trimesh.Scene in the contract frame at exactly dims_mm, info dict). `finishes`:
    optional {role: FINISHES key}; unknown roles/names are dropped."""
    template, params, colors = validate(template, params, colors)
    finishes = {r: f for r, f in (finishes or {}).items() if r in colors and f in FINISHES}
    if "grille_bg" in colors:  # slat gaps read as shadow; same colour as the slats hides them
        lum = [sum(srgb_to_linear(colors[r])[:3]) for r in ("grille_bg", "body")]
        if abs(lum[0] - lum[1]) < 0.6:
            colors["grille_bg"] = "#2A2A2A" if lum[1] > 1.0 else "#D0D0D0"
    W, H, D = (dims_mm[k] / 1000.0 for k in ("w", "h", "d"))
    P = Parts()
    TEMPLATES[template][0](P, W, H, D, params)
    allv = np.vstack([m.vertices for m, _ in P.items])
    lo, hi = allv.min(axis=0), allv.max(axis=0)
    target = np.array([W, H, D])
    scale = target / np.maximum(hi - lo, 1e-9)
    fit = np.diag([*scale, 1.0])
    fit[:3, 3] = -lo * scale + np.array([-W / 2, -H / 2, 0])
    scene = trimesh.Scene()
    for role in dict.fromkeys(r for _, r in P.items):
        mesh = trimesh.util.concatenate([m for m, r in P.items if r == role])
        mesh.apply_transform(fit)
        mesh.visual = trimesh.visual.TextureVisuals(
            material=material(
                colors.get(role, "#B0B0B0"), role, finishes.get(role) or DEFAULT_FINISH.get(role)
            )
        )
        scene.add_geometry(mesh, node_name=role, geom_name=role)
    info = {
        "template": template,
        "params": params,
        "colors": colors,
        "finishes": finishes,
        "fit_scale": [round(float(s), 4) for s in scale],
    }
    return scene, info


def _selfcheck() -> None:
    import tempfile
    from pathlib import Path

    assert (
        np.allclose(srgb_to_linear("#FFFFFF")[:3], 1)
        and abs(srgb_to_linear("#CCCCCC")[0] - 0.6038) < 1e-3
    )
    dims = {"w": 614.68, "h": 368.3, "d": 515.62}
    variants = {name: [{}] for name in TEMPLATES}
    variants["fixture"].append({"kind": "faucet"})
    variants["fan_unit"].append(
        {"fan_face": "top", "side_louvres": True, "cap_h": 0.1, "base_h": 0.05, "fans": 2}
    )
    variants["tall_box"] += [
        {"layout": lay, "dispenser": True} for lay in TEMPLATES["tall_box"][2]["layout"][2]
    ]
    variants["cylinder_tank"].append({"top": "dome", "top_h": 0.1, "bands": 0, "pipes": 0})
    variants["sink"].append({"bowls": 2, "flange": 0.0})
    variants["strap_hanger"].append({"brace": True, "hook": "lip"})
    variants["window"] += [  # assetgen
        {"style": s, "grid_x": 2, "grid_y": 1, "panes": 2, "sash_w": 0.0 if s == "fixed" else 0.05}
        for s in ("single_hung", "slider", "casement", "fixed")
    ]
    variants["bent_strip"] += [
        {"profile2": [[0, 0.2], [0.6, 1]], "back_plate_h": 0.8, "screw": True},
        {"profile": [[0.1, 0.1], [0.9, 0.9]], "thickness_mm": 20},  # diagonal, fraction-inset
    ]
    with tempfile.TemporaryDirectory() as tmp:
        for name, vs in variants.items():
            for v in vs:
                # a round shower face is square in X/Y; any other dims get stretched by the fit
                dm = {"w": 111.25, "h": 111.25, "d": 92.2} if name == "fixture" else dims
                scene, info = build(name, dm, v)
                # generators should fill the box by construction, so the final fit is ~a no-op
                assert np.allclose(info["fit_scale"], 1, atol=0.03), (name, v, info["fit_scale"])
                glb = Path(tmp) / "t.glb"
                scene.export(glb)
                back = trimesh.load(glb, force="scene")
                lo, hi = back.bounds
                want = np.array([dm["w"], dm["h"], dm["d"]]) / 1000
                assert np.allclose(hi - lo, want, atol=1e-5), (name, hi - lo)
                assert np.allclose(
                    [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]], 0, atol=1e-5
                ), name
    assert validate("nope", {"x": 1}, None)[0] == "box"
    assert validate("sink", {"bowls": 9, "wall_mm": "x"}, {"body": "#12ab34"})[1:] == (
        {**validate("sink", {}, None)[1], "bowls": 2},
        {"body": "#12AB34", "drain": "#3A3A3A"},
    )
    # bent strip: a polyline is normalised to fill the box; bad points fall back to the default
    assert (
        validate("bent_strip", {"profile": [[0, 2], "x"]}, None)[1]["profile"]
        == (TEMPLATES["bent_strip"][2]["profile"][1])
    )
    assert validate("bent_strip", {"profile2": [[0, 0]]}, None)[1]["profile2"] == []
    # finishes: metal lifts a dark albedo and sets metallic; unknown names are dropped
    scene, info = build("sink", dims, None, {"body": "#404040"}, {"body": "chrome", "drain": "x"})
    m = scene.geometry["body"].visual.material
    assert info["finishes"] == {"body": "chrome"} and m.metallicFactor == FINISHES["chrome"][0]
    assert abs(np.mean(m.baseColorFactor[:3]) / 255 - METAL_MIN_LUM) < 0.02
    print("templates selfcheck ok:", sum(map(len, variants.values())), "variants")


if __name__ == "__main__":
    _selfcheck()
