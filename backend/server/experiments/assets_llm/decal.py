"""E3: product photo projected onto a contract GLB (docs/research/assets-llm/e3-decal.md).

`decal_proxy()` builds the exact-size proxy box with the cropped product photo on one face and
the photo's product-edge colour everywhere else. `project_photo()` does the same planar
projection onto any contract mesh (E1/E2/Hunyuan geometry). `detect_view()` asks Groq which face
of the product the photo shows. Background removal is `score.photo_mask` (border flood fill,
largest blob): the photos are plain-background catalogue shots.

    uv run --with scipy python -m server.experiments.assets_llm.decal data/parts/<id> out.glb \
        [--face front|back|left|right|top|bottom] [--rotate 0|90|180|270] [--mesh model.glb]
"""

import argparse
import io
import json
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image
from scipy import ndimage

from server.assets import _move_origin_to_mount_face
from server.experiments.assets_llm.score import photo_mask

TEX_MAX = 1024  # longest texture side; Quest-friendly
STRIP = 16  # px of solid edge colour at the texture's right edge, for non-photo triangles
FRONT_DOT = 0.3  # triangles facing the projection axis at least this much get the photo

# face -> (outward normal, image-right axis, image-up axis), seen by a camera outside the face
# with +Y up (top/bottom: the part's back at the top of the image).
FACES = {
    "front": ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    "back": ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
    "right": ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
    "left": ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
    "top": ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
    "bottom": ((0, -1, 0), (-1, 0, 0), (0, 0, -1)),
}
OPPOSITE = {"left": "right", "right": "left"}  # a side profile shows mirrored from the other side


def cut_product(photo: Path) -> tuple[Image.Image, tuple[int, int, int]]:
    """Crop to the product and paint its background with the product's edge colour.

    Edge colour = median of the product pixels in a thin band inside its outline: what a viewer
    sees on the sides, and a letterbox fill that blends with the silhouette."""
    img = np.asarray(Image.open(photo).convert("RGB"))
    mask = photo_mask(photo)
    ys, xs = np.nonzero(mask)
    if not len(ys):  # nothing found: use the whole photo
        mask[:] = True
        ys, xs = np.nonzero(mask)
    k = max(2, min(mask.shape) // 100)  # skip the anti-aliased rim (k px), sample the next 2k px
    inner = ndimage.binary_erosion(mask, iterations=k)
    band = inner & ~ndimage.binary_erosion(inner, iterations=2 * k)
    band = band if band.any() else mask
    edge = tuple(int(v) for v in np.median(img[band], axis=0))
    out = np.where(mask[..., None], img, np.array(edge, np.uint8))
    crop = out[ys.min() : ys.max() + 1, xs.min() : xs.max() + 1]
    return Image.fromarray(crop), edge


def face_texture(
    crop: Image.Image, edge: tuple, aspect: float, rotate_cw: int = 0
) -> tuple[Image.Image, float]:
    """Letterbox the (rotated) crop into a face of width/height `aspect`, add the edge-colour
    strip, JPEG-encode. Returns (texture, u_max): photo region is u in [0, u_max]."""
    crop = crop.rotate(-rotate_cw, expand=True)
    fw, fh = (TEX_MAX - STRIP, round((TEX_MAX - STRIP) / aspect))
    if fh > TEX_MAX:
        fw, fh = max(round(TEX_MAX * aspect), 1), TEX_MAX
    scale = min(fw / crop.width, fh / crop.height)  # fit inside the face, keep aspect
    fit = crop.resize((max(1, round(crop.width * scale)), max(1, round(crop.height * scale))))
    tex = Image.new("RGB", (fw + STRIP, fh), edge)
    tex.paste(fit, ((fw - fit.width) // 2, (fh - fit.height) // 2))
    buf = io.BytesIO()
    tex.save(buf, "JPEG", quality=85)
    return Image.open(io.BytesIO(buf.getvalue())), fw / (fw + STRIP)  # format JPEG: kept on export


def project_photo(
    mesh: trimesh.Trimesh,
    photo: Path,
    face: str = "front",
    rotate_cw: int = 0,
    mirror_opposite: bool = True,
) -> trimesh.Trimesh:
    """Planar-project the cropped photo along `face`'s normal onto triangles facing it; every
    other triangle samples the edge-colour strip. The projection spans the mesh bbox on that
    face, so the photo is fitted (letterboxed), never stretched. ponytail: no occlusion test,
    so hidden inner triangles facing the same way get the photo too (invisible from outside);
    add a depth-buffer visibility pass if a mesh with through-holes shows it."""
    crop, edge = cut_product(photo)
    mesh = mesh.copy()
    mesh.unmerge_vertices()  # one vertex per triangle corner, so each triangle picks its own UV
    n, r, up = (np.array(v, float) for v in FACES[face])
    ext_r, ext_u = float(np.ptp(mesh.vertices @ r)), float(np.ptp(mesh.vertices @ up))
    tex, u_max = face_texture(crop, edge, max(ext_r, 1e-9) / max(ext_u, 1e-9), rotate_cw)

    faces_uv = [(n, r, up)]
    if mirror_opposite and face in OPPOSITE:
        # the other side sees the same profile mirrored: its image-right is -r
        faces_uv.append(tuple(np.array(v, float) for v in FACES[OPPOSITE[face]]))
    uv = np.tile([(1 + u_max) / 2, 0.5], (len(mesh.vertices), 1))  # strip centre
    tri_vid = mesh.faces
    for fn, fr, fu in faces_uv:
        hit = mesh.face_normals @ fn >= FRONT_DOT
        v = np.unique(tri_vid[hit])
        p = mesh.vertices[v]
        pr, pu = p @ fr, p @ fu
        lo_r, lo_u = (mesh.vertices @ fr).min(), (mesh.vertices @ fu).min()
        uv[v, 0] = (pr - lo_r) / max(ext_r, 1e-9) * u_max
        uv[v, 1] = (pu - lo_u) / max(ext_u, 1e-9)
    mesh.visual = trimesh.visual.TextureVisuals(
        uv=uv,
        material=trimesh.visual.material.PBRMaterial(
            baseColorTexture=tex, metallicFactor=0.0, roughnessFactor=0.8
        ),
    )
    mesh.merge_vertices()  # re-share corners with equal UVs: ~6x fewer vertices on dense meshes
    return mesh


def decal_proxy(
    dims_mm: dict, photo: Path, face: str = "front", rotate_cw: int = 0
) -> trimesh.Trimesh:
    """Exact-size proxy box (X=w, Y=h, Z=d, origin at the -z mount face) with the photo on `face`."""
    box = trimesh.creation.box(extents=np.array([dims_mm["w"], dims_mm["h"], dims_mm["d"]]) / 1000)
    _move_origin_to_mount_face(box, "-z")
    return project_photo(box, photo, face, rotate_cw)


def textured_glb(glb: Path, photo: Path, face: str = "front", rotate_cw: int = 0):
    """Load any contract GLB as one mesh and project the photo onto it."""
    scene = trimesh.load(str(glb), force="scene")
    mesh = scene.to_geometry() if hasattr(scene, "to_geometry") else scene.dump(concatenate=True)
    return project_photo(mesh, photo, face, rotate_cw)


VIEW_PROMPT = """Product: {name}.
Installed size: {w:.0f} mm wide (left-right), {h:.0f} mm tall, {d:.0f} mm deep (from its mounting
surface toward a person standing in front of it). "front" is the side facing that person, "back" is
the side against the mounting surface ({surface}). In a left/right view you see the product's depth:
the mounting surface is at the left or right edge of the product. In a top view you look down on it.
Which one face of the product does this photo mostly show? Also: catalogue photos are nearly always
upright, so rotate_cw is 0 unless the product is clearly lying on its side in the photo.
Reply with JSON only: {{"face": "front|back|left|right|top|bottom", "rotate_cw": 0|90|180|270,
"why": "<10 words>"}}"""


def detect_view(part: dict, photo: Path, tag: str) -> tuple[dict, dict]:
    """One small Groq vision call. Returns (answer, ledger record); face 'front' on a bad reply."""
    from server.experiments.assets_llm.groq_smoke import _photo_data_url
    from server.experiments.assets_llm.llm import chat

    d = part["dims_mm"]
    text = VIEW_PROMPT.format(
        name=part["name"], w=d["w"], h=d["h"], d=d["d"], surface=part["mount"].get("surface", "")
    )
    msgs = [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": text},
                {"type": "image_url", "image_url": {"url": _photo_data_url(photo, 512)}},
            ],
        }
    ]
    reply, rec = chat(msgs, tag=tag, max_tokens=200, json_mode=True, temperature=0.0)
    try:
        ans = json.loads(reply)
    except json.JSONDecodeError:
        ans = {}
    if ans.get("face") not in FACES:
        ans["face"] = "front"
    if ans.get("rotate_cw") not in (0, 90, 180, 270):
        ans["rotate_cw"] = 0
    return ans, rec


def aspect_guess(dims_mm: dict, photo: Path) -> str:
    """No-LLM baseline: the face whose aspect best matches the product crop's aspect."""
    crop, _ = cut_product(photo)
    a = np.log(crop.width / crop.height)
    w, h, d = dims_mm["w"], dims_mm["h"], dims_mm["d"]
    cand = {"front": w / h, "right": d / h, "top": w / d}
    return min(cand, key=lambda k: abs(a - np.log(cand[k])))


def _selfcheck() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        photo = Path(tmp) / "p.jpg"
        img = Image.new("RGB", (400, 300), (255, 255, 255))
        img.paste((200, 30, 30), (100, 50, 300, 250))  # red 200x200 product on white
        img.save(photo)
        m = decal_proxy({"w": 400, "h": 100, "d": 50}, photo)
        out = Path(tmp) / "d.glb"
        m.export(out)
        back = trimesh.load(out, force="scene").to_geometry()
        assert np.allclose(back.extents, [0.4, 0.1, 0.05], atol=1e-6)
        assert abs(back.bounds[0][2]) < 1e-9
        tex = back.visual.material.baseColorTexture
        assert b'"mimeType":"image/jpeg"' in out.read_bytes() and max(tex.size) <= TEX_MAX
        # front face centre samples the red product, sides sample the edge colour (also red)
        px = np.asarray(tex.convert("RGB"))
        assert px[px.shape[0] // 2, (px.shape[1] - STRIP) // 2, 0] > 150
        # white background repainted with the product's edge colour (red) across the face
        assert (px[px.shape[0] // 2, : px.shape[1] - STRIP, 1] < 100).mean() > 0.9
    print("selfcheck ok")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("part_dir", type=Path, nargs="?")
    ap.add_argument("out", type=Path, nargs="?")
    ap.add_argument("--face", default="front", choices=FACES)
    ap.add_argument("--rotate", type=int, default=0)
    ap.add_argument("--mesh", type=Path, help="texture this contract GLB instead of the proxy box")
    ap.add_argument("--selfcheck", action="store_true")
    a = ap.parse_args()
    if a.selfcheck:
        _selfcheck()
    else:
        part = json.loads((a.part_dir / "part.json").read_text())
        photo = a.part_dir / "image.jpg"
        m = (
            textured_glb(a.mesh, photo, a.face, a.rotate)
            if a.mesh
            else decal_proxy(part["dims_mm"], photo, a.face, a.rotate)
        )
        m.export(a.out)
