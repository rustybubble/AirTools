"""CPU textured renderer for GLBs (pyrender's PyOpenGL fails on this Mac): trimesh loads the
scene, numpy rasterises it with a z-buffer, perspective-correct UVs, the base colour texture x
baseColorFactor, and a simple key + fill + ambient Lambert light with a Blinn-Phong highlight
scaled by metallic/roughness (enough to see the material cue; not PBR).

    python render.py model.glb out.png [--view front|three_quarter|back|side] [--size 512]
"""

import argparse
import sys

import numpy as np
import trimesh
from PIL import Image, ImageDraw


def _srgb_to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def _lin_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def _mat(m):
    """(factor rgb linear, texture array srgb->linear or None, metallic, roughness)"""
    mat = getattr(m.visual, "material", None)
    f = np.array([1.0, 1.0, 1.0])
    tex, metal, rough = None, 0.0, 0.8
    if mat is not None:
        bc = getattr(mat, "baseColorFactor", None)
        if bc is not None:
            bc = np.asarray(bc, float)[:3]
            f = bc / 255.0 if bc.max() > 1.0 else bc
            # trimesh stores baseColorFactor as uint8 of the LINEAR value
        t = getattr(mat, "baseColorTexture", None)
        if t is not None:
            tex = _srgb_to_lin(np.asarray(t.convert("RGB"), float) / 255.0)
        metal = float(getattr(mat, "metallicFactor", 0.0) or 0.0)
        r = getattr(mat, "roughnessFactor", None)
        rough = float(r) if r is not None else 1.0
    return f, tex, metal, rough


def look_at(eye, target, up=(0, 1, 0)):
    eye, target, up = (np.asarray(v, float) for v in (eye, target, up))
    fwd = target - eye
    fwd /= np.linalg.norm(fwd)
    right = np.cross(fwd, up)
    right /= np.linalg.norm(right)
    u = np.cross(right, fwd)
    return eye, right, u, fwd


VIEWS = {
    "front": (0.0, 8.0),
    "three_quarter": (35.0, 18.0),
    "back": (180.0, 8.0),
    "side": (90.0, 8.0),
    "three_quarter_back": (145.0, 18.0),
}


def render(scene, view="three_quarter", size=512, bg=(245, 246, 248), fov=30.0):
    if isinstance(scene, trimesh.Trimesh):
        scene = trimesh.Scene(scene)
    meshes = []
    for node in scene.graph.nodes_geometry:
        tf, name = scene.graph[node]
        g = scene.geometry[name]
        if not isinstance(g, trimesh.Trimesh) or len(g.faces) == 0:
            continue
        m = g.copy()
        m.apply_transform(tf)
        meshes.append(m)
    allv = np.vstack([m.vertices for m in meshes])
    lo, hi = allv.min(0), allv.max(0)
    centre = (lo + hi) / 2
    radius = np.linalg.norm(hi - lo) / 2
    yaw, pitch = VIEWS.get(view, VIEWS["three_quarter"])
    yaw_r, pitch_r = np.radians(yaw), np.radians(pitch)
    direction = np.array(
        [np.sin(yaw_r) * np.cos(pitch_r), np.sin(pitch_r), np.cos(yaw_r) * np.cos(pitch_r)]
    )
    dist = radius / np.tan(np.radians(fov / 2)) * 1.08
    eye, right, up, fwd = look_at(centre + direction * dist, centre)
    W = H = size
    f = (W / 2) / np.tan(np.radians(fov / 2))
    zbuf = np.full((H, W), np.inf)
    img = np.zeros((H, W, 3))
    img[:] = _srgb_to_lin(np.array(bg) / 255.0)
    key = np.array([0.45, 0.75, 0.6])
    key /= np.linalg.norm(key)
    fill = np.array([-0.6, 0.3, 0.4])
    fill /= np.linalg.norm(fill)
    for m in meshes:
        factor, tex, metal, rough = _mat(m)
        uv = getattr(m.visual, "uv", None)
        has_uv = tex is not None and uv is not None and len(uv) == len(m.vertices)
        v = m.vertices - eye
        cx, cy, cz = v @ right, v @ up, v @ fwd
        px = W / 2 + f * cx / cz
        py = H / 2 - f * cy / cz
        fn = m.face_normals
        for t, face in enumerate(m.faces):
            zs = cz[face]
            if (zs <= 1e-6).any():
                continue
            n = fn[t]
            if n @ (m.vertices[face[0]] - eye) > 0:  # back face: flip for two-sided look
                n = -n
            xs_, ys_ = px[face], py[face]
            x0, x1 = max(int(np.floor(xs_.min())), 0), min(int(np.ceil(xs_.max())), W - 1)
            y0, y1 = max(int(np.floor(ys_.min())), 0), min(int(np.ceil(ys_.max())), H - 1)
            if x1 < x0 or y1 < y0:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            (ax, bx, cxx), (ay, by, cyy) = xs_, ys_
            den = (by - cyy) * (ax - cxx) + (cxx - bx) * (ay - cyy)
            if abs(den) < 1e-12:
                continue
            w0 = ((by - cyy) * (gx - cxx) + (cxx - bx) * (gy - cyy)) / den
            w1 = ((cyy - ay) * (gx - cxx) + (ax - cxx) * (gy - cyy)) / den
            w2 = 1 - w0 - w1
            inside = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
            if not inside.any():
                continue
            iz = w0 / zs[0] + w1 / zs[1] + w2 / zs[2]
            z = 1 / iz
            sub = zbuf[y0 : y1 + 1, x0 : x1 + 1]
            better = inside & (z < sub)
            if not better.any():
                continue
            sub[better] = z[better]
            if has_uv:
                tuv = uv[face]
                uu = (w0 * tuv[0, 0] / zs[0] + w1 * tuv[1, 0] / zs[1] + w2 * tuv[2, 0] / zs[2]) * z
                vv = (w0 * tuv[0, 1] / zs[0] + w1 * tuv[1, 1] / zs[1] + w2 * tuv[2, 1] / zs[2]) * z
                th, tw = tex.shape[:2]
                ti = np.clip(((1 - vv[better]) * (th - 1)).round().astype(int), 0, th - 1)
                tj = np.clip((uu[better] * (tw - 1)).round().astype(int), 0, tw - 1)
                base = tex[ti, tj] * factor
            else:
                base = np.broadcast_to(factor, (int(better.sum()), 3))
            lam = 0.30 + 0.62 * max(n @ key, 0) + 0.22 * max(n @ fill, 0)
            diffuse = base * (1 - 0.6 * metal) * lam
            h = key - fwd
            h /= np.linalg.norm(h)
            shin = 2 + (1 - rough) * 60
            spec = (0.04 + 0.5 * metal) * (1 - rough) * max(n @ h, 0) ** shin
            col = diffuse + spec
            img[y0 : y1 + 1, x0 : x1 + 1][better] = col
    return Image.fromarray((_lin_to_srgb(img) * 255).astype(np.uint8))


def label(img, text):
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, img.width, 18], fill=(255, 255, 255))
    d.text((4, 3), text, fill=(20, 20, 20))
    return img


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("glb")
    ap.add_argument("out")
    ap.add_argument("--view", default="three_quarter")
    ap.add_argument("--size", type=int, default=512)
    a = ap.parse_args()
    render(trimesh.load(a.glb, force="scene"), a.view, a.size).save(a.out)
    sys.exit(0)
