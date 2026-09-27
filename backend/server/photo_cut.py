"""texture: the product cut out of its photo, for the photo texture (server/meshgen.py `project`).

The pipeline paints the product photo onto the model's front (every tier). A catalogue photo on a
white studio background only needs its background removed; a showroom photo (the LG fridge between
its neighbours, on carpet) needs the one product picked out. `cut()`:

1. **Plain background** (>= PLAIN_BORDER_FRAC of the border within TOL of the border's median
   colour): flood that colour in from the edges, keep the biggest blob, fill holes (the old
   `meshgen.photo_mask`, ~10-30 ms). A white product on white keeps its body: the flood only takes
   background connected to the border.
2. **Cluttered**: U^2-Netp salient-object mask (4.6 MB onnx, CPU, ~0.1 s a photo after a ~0.3 s
   load; rembg's model, run here through onnxruntime without rembg) -> components -> the largest
   central one whose box aspect matches the part's listed size (`aspect`, W/H of the face the photo
   shows). No onnxruntime or no model file (and OFFLINE, so no download): the flood result when it
   looks like one object, else the whole photo (method "whole", the old behaviour).
3. **Keystone**: a boxy product (the mask fills >= KEY_FILL of the quadrilateral fitted to its
   four edges) shot slightly off-axis is warped back to a rectangle when its corners are 1.5-20 %
   off the bounding box. A real 3/4 view (a range's cooktop seen from above) is left as is.

The cut-out is cached next to the photo: `image.cut.png` (RGBA, cropped to the product, alpha =
the product) and `image.cut.json` {version, photo_sha1, aspect, method, background, box (x0, y0, x1,
y1 in photo px), quad (4 corners in photo px when warped) or null, seconds, colors {body, trim}}.

`colors()` samples the product's dominant material colours (a small k-means over the cut-out's
pixels): the body (the largest cluster: stainless ~#B9BCC0, black, white) and the trim (the next
big cluster that differs from it). `finish_for()` names the material cue (metal / plastic /
painted) from the part's listing text, else the colours.
"""

from __future__ import annotations

import hashlib
import json
import logging
import re
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

from server import cache
from server.config import get_settings

logger = logging.getLogger(__name__)

VERSION = 3  # 2: frame-filling crop, any face; 3: background seen through the product
PLAIN_BORDER_FRAC = 0.4  # this share of the border near its median colour = a studio background
TOL = 12  # max per-channel distance from the border colour that still counts as background
MAX_SIDE = 1600  # photos are worked on at most this big (catalogue photos are <= 1000 px)
HOLE_MIN = 0.002  # an enclosed background-coloured patch this big is background seen through
HOLE_CONTRAST = 40  # ... when the product's median colour is this far from the background's
RIM = 2  # px of the outline that are part background (anti-aliased), replaced from inside
EXTEND = 0.015  # background this near the outline (share of the size) takes the edge's colour
MIN_BLOB = 0.01  # a component smaller than this share of the photo isn't a candidate
FG_MIN, FG_MAX = 0.03, 0.97  # a flood "foreground" outside this share of the photo isn't one object
SAL_MATCH = 0.25  # |ln| aspect mismatch of the model's object still taken as the product
FRAME_MATCH = 0.06  # |ln(photo aspect / product aspect)| under this: the product fills the frame
KEY_FILL = 0.95  # IoU of mask vs fitted quad: the product is a box face seen in perspective
KEY_MIN, KEY_MAX = 0.02, 0.20  # corner offset (share of the size) worth warping / too big to
KEY_STRAIGHT = 0.01  # median edge-fit residual (share of the size) of a straight edge

MODEL_NAME = "u2netp"
MODEL_URL = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx"
MODEL_MD5 = "8e83ca70e441ab06c318d82300c84806"
_MEAN = np.array([0.485, 0.456, 0.406], np.float32)
_STD = np.array([0.229, 0.224, 0.225], np.float32)
_session = None
_session_lock = threading.Lock()
_session_failed = False

# Listing words -> the material cue (first match wins; "matte black steel" is painted metal).
_FINISH_WORDS = [
    ("painted", r"matte?|powder[- ]?coat(?:ed)?|enamel(?:ed)?|painted"),
    (
        "metal",
        (
            r"stainless|steel|chrome|nickel|alumin(?:i)?um|brass|bronze|copper|zinc|galvani[sz]ed"
            r"|metal(?:lic)?|iron|titanium"
        ),
    ),
    ("plastic", r"plastic|vinyl|resin|pvc|abs|polypropylene|rubber|acrylic|polycarbonate"),
]
# (metallicFactor, roughnessFactor) of the sides and top per cue; "slightly" metallic because a
# Quest scene without a reflection probe renders full metal almost black (part_templates.FINISHES).
FINISH_PBR = {"metal": (0.35, 0.35), "plastic": (0.0, 0.7), "painted": (0.0, 0.5)}
# ... and of the photo itself: its reflections are baked in, so only a hint of sheen.
PHOTO_PBR = {"metal": (0.12, 0.5), "plastic": (0.0, 0.8), "painted": (0.0, 0.7)}


@dataclass
class Cut:
    """The product cut out of its photo. `image`: RGBA cropped to the product's box, alpha = the
    product. `quad`: when the product is a box face seen slightly off-axis, its four corners (tl,
    tr, br, bl) in `image` px; `rectified()` warps them to a rectangle (the caller decides: only a
    model whose front is a rectangle wants it, a tapered planter doesn't)."""

    image: Image.Image
    box: tuple[int, int, int, int]
    method: str
    background: str
    seconds: float = 0.0
    quad: list | None = None
    colors: dict = field(default_factory=dict)
    aspect: float | None = None

    @property
    def mask(self) -> np.ndarray:
        return np.asarray(self.image.getchannel("A")) > 127

    def rectified(self) -> Image.Image:
        return warp_quad(self.image, self.quad) if self.quad else self.image

    def texture(self, rectify: bool = False) -> Image.Image:
        """RGB for the texture, never the background: a pixel just outside the outline (texture
        filtering, a silhouette a little off the model's) takes the nearest product pixel's
        colour; further out (a round knob's corners on a box front) it fades to the body colour."""
        img = self.rectified() if rectify else self.image
        rgb = np.asarray(img.convert("RGB")).astype(np.float32)
        fg = np.asarray(img.getchannel("A")) > 127
        if fg.all() or not fg.any():
            return Image.fromarray(rgb.astype(np.uint8))
        # the outline's own pixels are part background (anti-aliasing on white): the core's
        core = ndimage.binary_erosion(fg, iterations=RIM)
        core = core if core.any() else fg
        dist, (iy, ix) = ndimage.distance_transform_edt(~core, return_indices=True)
        reach = max(2.0, EXTEND * min(fg.shape))
        near = ndimage.gaussian_filter(rgb[iy, ix], sigma=(reach / 2, reach / 2, 0))  # no streaks
        body = hex_rgb(self.colors.get("body") or "#B0B0B0").astype(np.float32)
        k = np.clip(dist / reach - 0.5, 0.0, 1.0)[..., None]  # 0 at the edge, 1 a reach out
        out = np.where(core[..., None], rgb, near * (1 - k) + body * k)
        return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8))

    def info(self) -> dict:
        return {
            "cut": self.method,
            "background": self.background,
            "cut_s": round(self.seconds, 3),
            "box": list(self.box),
        }


def cut_paths(photo: Path) -> tuple[Path, Path]:
    """(image.cut.png, image.cut.json) for image.jpg."""
    return photo.with_name(f"{photo.stem}.cut.png"), photo.with_name(f"{photo.stem}.cut.json")


def _sha1(path: Path) -> str:
    return hashlib.sha1(path.read_bytes()).hexdigest()


def _same_aspect(a: float | None, b: float | None) -> bool:
    if a is None or b is None:
        return a is b
    return abs(np.log(a / b)) < 0.01


def cached(photo: Path, aspect: float | None = None) -> Cut | None:
    """The cut-out made for this very photo (and aspect) by this version, else None. A cluttered
    photo cut without the model is made again once the model is there."""
    png, meta_path = cut_paths(photo)
    try:
        meta = json.loads(meta_path.read_text())
        if meta.get("version") != VERSION or meta.get("photo_sha1") != _sha1(photo):
            return None
        # a plain-background flood doesn't depend on the aspect; picking among objects does
        if meta.get("aspect_used", True) and not _same_aspect(meta.get("aspect"), aspect):
            return None
        if (
            meta.get("background") == "cluttered"
            and meta.get("method") != MODEL_NAME
            and _model_path().exists()
            and not _session_failed
        ):
            return None
        image = Image.open(png)
        image.load()
    except (OSError, ValueError):
        return None
    return Cut(
        image=image.convert("RGBA"),
        box=tuple(meta["box"]),
        method=meta["method"],
        background=meta["background"],
        seconds=meta.get("seconds", 0.0),
        quad=meta.get("quad"),
        colors=meta.get("colors") or {},
        aspect=meta.get("aspect"),
    )


def cut(
    photo: Path, aspect: float | None = None, *, size: tuple | None = None, save: bool = True
) -> Cut:
    """The product cut out of `photo` (module docstring), cached next to it. `aspect`: width /
    height of the product face the photo shows (the part's W/H for a front photo), used to pick
    the product among several objects. `size`: the product's (W, H, D) -- a catalogue crop the
    product fills matches one of its faces even when the listing swapped two dimensions. Raises
    OSError when the photo won't load."""
    hit = cached(photo, aspect)
    if hit is not None:
        return hit
    t0 = time.monotonic()
    img = Image.open(photo).convert("RGB")
    scale = 1.0
    if max(img.size) > MAX_SIDE:
        scale = MAX_SIDE / max(img.size)
        img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
    rgb = np.asarray(img)
    alpha, method, background = segment(rgb, aspect, face_aspects(size))
    fg = alpha > 127
    ys, xs = np.nonzero(fg)
    box = (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
    rgba = np.dstack([rgb, alpha]).astype(np.uint8)[box[1] : box[3], box[0] : box[2]]
    quad = (
        keystone_quad(fg[box[1] : box[3], box[0] : box[2]])
        if method in ("flood", MODEL_NAME)
        else None
    )
    out = Image.fromarray(rgba, "RGBA")
    result = Cut(
        image=out,
        box=tuple(round(v / scale) for v in box),
        method=method,
        background=background,
        quad=[[round(x, 1), round(y, 1)] for x, y in quad] if quad else None,
        aspect=aspect,
    )
    result.colors = colors(np.asarray(out.convert("RGB")), result.mask)
    result.seconds = time.monotonic() - t0
    if save:
        _save(photo, result)
    logger.info(
        "photo_cut %s: %s (%s) box=%s keystone=%s body=%s in %.2f s",
        photo.parent.name or photo.name,
        method,
        background,
        result.box,
        quad is not None,
        result.colors.get("body"),
        result.seconds,
    )
    return result


def _save(photo: Path, c: Cut) -> None:
    png, meta_path = cut_paths(photo)
    try:
        tmp = png.with_name(f".{png.name}.tmp")
        c.image.save(tmp, "PNG")
        tmp.replace(png)
        meta = {
            "version": VERSION,
            "photo_sha1": _sha1(photo),
            "aspect": c.aspect,
            "aspect_used": not (c.method == "flood" and c.background == "plain"),
            "method": c.method,
            "background": c.background,
            "box": list(c.box),
            "quad": c.quad,
            "size": list(c.image.size),
            "seconds": round(c.seconds, 3),
            "colors": c.colors,
        }
        cache.write_json_atomic(meta_path, json.dumps(meta, indent=2))
    except OSError as exc:  # a read-only photo dir still gets its texture
        logger.info("photo_cut: couldn't cache %s: %s", png, exc)


# --------------------------------------------------------------------------------- segmentation


def border_pixels(rgb: np.ndarray, width: int = 2) -> np.ndarray:
    w = width
    return np.concatenate(
        [
            rgb[:w].reshape(-1, 3),
            rgb[-w:].reshape(-1, 3),
            rgb[:, :w].reshape(-1, 3),
            rgb[:, -w:].reshape(-1, 3),
        ]
    ).astype(np.int16)


def is_plain(rgb: np.ndarray) -> bool:
    """A studio background: most of the border is one colour (a product touching an edge still
    leaves the rest of the border)."""
    border = border_pixels(rgb)
    near = np.abs(border - np.median(border, axis=0)).max(axis=1) <= TOL
    return float(near.mean()) >= PLAIN_BORDER_FRAC


def flood_mask(rgb: np.ndarray, tol: int = TOL) -> np.ndarray:
    """Foreground of a plain-background photo: the border colour flooded in from the edges, the
    biggest remaining blob, holes filled (meshgen.photo_mask's method) -- except background seen
    through the product (between a bench's slats) when the product isn't itself that colour: an
    enclosed patch of the background colour of at least HOLE_MIN of the photo stays background."""
    img = rgb.astype(np.int16)
    bg = np.median(border_pixels(rgb, 1), axis=0)
    near_bg = np.abs(img - bg).max(axis=2) <= tol
    lab, _ = ndimage.label(near_bg)
    edge = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    fg = ndimage.binary_opening(~np.isin(lab, edge[edge > 0]))
    lab2, n = ndimage.label(fg)
    if n > 1:
        fg = lab2 == (np.argmax(np.bincount(lab2.ravel())[1:]) + 1)
    filled = ndimage.binary_fill_holes(fg)
    body = img[filled & ~near_bg]
    if body.size and np.abs(np.median(body, axis=0) - bg).max() > HOLE_CONTRAST:
        sizes = np.bincount(lab.ravel())
        big = np.nonzero(sizes >= HOLE_MIN * near_bg.size)[0]
        for i in big[(big > 0) & ~np.isin(big, edge)]:
            patch = lab == i
            ring = ndimage.binary_dilation(patch, iterations=3) & ~patch
            # a sharp edge to the product (a gap between slats), not a highlight fading into steel
            if np.abs(np.median(img[ring], axis=0) - bg).max() > HOLE_CONTRAST:
                filled &= ~patch
    return filled


def pick_object(fg: np.ndarray, aspect: float | None) -> np.ndarray:
    """The product among the mask's components: the largest central one whose box aspect matches
    `aspect` (score = area x aspect match x centrality); holes filled."""
    lab, n = ndimage.label(fg)
    if n == 0:
        return fg
    h, w = fg.shape
    areas = np.bincount(lab.ravel())[1:]
    best, best_score = 0, -1.0
    for i, sl in enumerate(ndimage.find_objects(lab)):
        area = areas[i] / fg.size
        if area < MIN_BLOB and i != int(np.argmax(areas)):
            continue
        bh, bw = sl[0].stop - sl[0].start, sl[1].stop - sl[1].start
        cy, cx = (sl[0].start + sl[0].stop) / 2 / h, (sl[1].start + sl[1].stop) / 2 / w
        central = 1.0 - 0.8 * min(1.0, np.hypot(cx - 0.5, cy - 0.5) / 0.7071)
        match = 1.0 if aspect is None else float(np.exp(-1.5 * abs(np.log((bw / bh) / aspect))))
        score = area * match * central
        if score > best_score:
            best, best_score = i + 1, score
    return ndimage.binary_fill_holes(lab == best)


def _aspect_off(fg: np.ndarray, aspect: float | None) -> float:
    """|ln(box aspect / aspect)| of the mask's bounding box (0 without an aspect)."""
    if aspect is None or not fg.any():
        return 0.0
    ys, xs = np.nonzero(fg)
    return float(abs(np.log((np.ptp(xs) + 1) / (np.ptp(ys) + 1) / aspect)))


def face_aspects(size: tuple | None) -> tuple[float, ...]:
    """Width / height of each face of a (W, H, D) product: front, side, top."""
    if not size or min(size) <= 0:
        return ()
    w, h, d = (float(v) for v in size)
    return (w / h, d / h, w / d)


def segment(
    rgb: np.ndarray, aspect: float | None = None, frames: tuple[float, ...] = ()
) -> tuple[np.ndarray, str, str]:
    """(alpha uint8 HxW, method, background) for the product in `rgb`: "flood" on a plain
    background; on a cluttered one the saliency model's object when its box has about the
    product's aspect, else "frame" when the photo itself has it (a catalogue crop the product
    fills: the border is the product, not a background), else the model's object anyway, else a
    plausible flood, else "whole". `frames`: the aspects of the product's other faces (a listing's
    swapped dimensions still find the frame-filling crop)."""
    h, w = rgb.shape[:2]
    whole = np.full((h, w), 255, np.uint8)
    background = "cluttered"
    if is_plain(rgb):
        fg = flood_mask(rgb)
        if fg.mean() >= FG_MIN:
            return _feather(fg), "flood", "plain"
        background = "plain"  # a product the flood swallowed (white on white): try the model
    shapes = [a for a in (aspect, *frames) if a]
    frame = any(abs(np.log((w / h) / a)) < FRAME_MATCH for a in shapes)
    pred = saliency(rgb)
    if pred is not None:
        fg = ndimage.binary_opening(pred > 0.5, iterations=2)
        if fg.mean() >= FG_MIN:
            obj = pick_object(fg, aspect)
            if not frame or _aspect_off(obj, aspect) < SAL_MATCH:
                # soft edges from the model, only around the chosen object
                near = ndimage.binary_dilation(obj, iterations=3)
                alpha = np.where(near, np.clip(pred * 255.0, 0, 255), 0).astype(np.uint8)
                alpha[obj & (alpha < 128)] = 255  # holes the fill closed stay solid
                return alpha, MODEL_NAME, background
    if frame:
        return whole, "frame", background
    fg = flood_mask(rgb)
    if FG_MIN <= fg.mean() <= FG_MAX:
        return _feather(pick_object(fg, aspect)), "flood", background
    return whole, "whole", background


def _feather(fg: np.ndarray) -> np.ndarray:
    """A binary mask as alpha with a one-pixel soft rim (no staircase on the outline)."""
    soft = ndimage.uniform_filter(fg.astype(np.float32), size=3)
    return (np.where(fg, np.maximum(soft, 0.5), 0.0) * 255).astype(np.uint8)


# ------------------------------------------------------------------------------ U^2-Netp (onnx)


def _model_path() -> Path:
    return Path(get_settings().DATA_DIR) / "cache" / "models" / f"{MODEL_NAME}.onnx"


def ensure_model() -> Path | None:
    """The U^2-Netp onnx file, downloaded once (4.6 MB, checksummed) into <DATA_DIR>/cache/models;
    None when it isn't there and can't be fetched (OFFLINE, no network)."""
    path = _model_path()
    if path.exists():
        return path
    if get_settings().OFFLINE:
        return None
    import httpx

    try:
        resp = httpx.get(MODEL_URL, timeout=60, follow_redirects=True)
        resp.raise_for_status()
    except httpx.HTTPError as exc:
        logger.info("photo_cut: model download failed: %s", exc)
        return None
    if hashlib.md5(resp.content).hexdigest() != MODEL_MD5:
        logger.info("photo_cut: model download has the wrong checksum; not used")
        return None
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f".{path.name}.tmp")
    tmp.write_bytes(resp.content)
    tmp.replace(path)
    return path


def _get_session():
    global _session, _session_failed
    with _session_lock:
        if _session is not None or _session_failed:
            return _session
        try:
            import onnxruntime as ort

            path = ensure_model()
            if path is None:
                return None
            opts = ort.SessionOptions()
            opts.intra_op_num_threads = 2
            opts.log_severity_level = 3
            _session = ort.InferenceSession(
                str(path), sess_options=opts, providers=["CPUExecutionProvider"]
            )
        except Exception as exc:  # noqa: BLE001 -- no onnxruntime / a broken model: no saliency
            logger.info("photo_cut: no saliency model: %s", exc)
            _session_failed = True
        return _session


def saliency(rgb: np.ndarray) -> np.ndarray | None:
    """U^2-Netp's salient-object map (0..1, photo size), None without the model. rembg's
    preprocessing: 320x320 LANCZOS, / max, ImageNet mean/std, min-max normalised output."""
    session = _get_session()
    if session is None:
        return None
    small = np.asarray(Image.fromarray(rgb).resize((320, 320), Image.LANCZOS), np.float32)
    small = small / max(float(small.max()), 1e-6)
    x = ((small - _MEAN) / _STD).transpose(2, 0, 1)[None].astype(np.float32)
    pred = session.run(None, {session.get_inputs()[0].name: x})[0][0, 0]
    pred = (pred - pred.min()) / max(float(pred.max() - pred.min()), 1e-6)
    up = Image.fromarray((pred * 255).astype(np.uint8)).resize(
        (rgb.shape[1], rgb.shape[0]), Image.BILINEAR
    )
    return np.asarray(up, np.float32) / 255.0


# ------------------------------------------------------------------------------------ keystone


def _fit_line(t: np.ndarray, v: np.ndarray) -> tuple[float, float, float]:
    """v = a t + b, least squares with one round of outlier trimming; (a, b, median |residual|)."""
    a, b = np.polyfit(t, v, 1)
    resid = np.abs(v - (a * t + b))
    keep = resid <= max(2.5 * np.median(resid), 1.0)
    if keep.sum() >= 4:
        a, b = np.polyfit(t[keep], v[keep], 1)
    return float(a), float(b), float(np.median(np.abs(v - (a * t + b))))


def _edges(fg: np.ndarray, axis: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """For each row (axis=1) or column (axis=0) of the middle 80 % that the mask crosses: (index,
    first, last) mask pixel."""
    m = fg if axis == 1 else fg.T
    idx = np.nonzero(m.any(axis=1))[0]
    if len(idx) < 10:
        return idx[:0], idx[:0], idx[:0]
    lo, hi = idx[0], idx[-1]
    span = hi - lo
    idx = idx[(idx >= lo + 0.1 * span) & (idx <= hi - 0.1 * span)]
    first = np.argmax(m[idx], axis=1)
    last = m.shape[1] - 1 - np.argmax(m[idx, ::-1], axis=1)
    return idx.astype(float), first.astype(float), last.astype(float)


def keystone_quad(fg: np.ndarray) -> list | None:
    """The product's four corners (tl, tr, br, bl; x, y px) when it's a box face seen slightly off
    axis: the mask fills the quadrilateral fitted to its four edges (IoU >= KEY_FILL) and a corner
    sits KEY_MIN..KEY_MAX of the size off the bounding box. None otherwise (already square-on, not
    a box, or a real 3/4 view)."""
    ys, xl, xr = _edges(fg, 1)
    xs, yt, yb = _edges(fg, 0)
    if len(ys) < 10 or len(xs) < 10:
        return None
    left, right = _fit_line(ys, xl), _fit_line(ys, xr + 1)
    top, bottom = _fit_line(xs, yt), _fit_line(xs, yb + 1)
    h, w = fg.shape
    # four straight edges (a leaky or curved outline isn't a box face)
    if max(left[2], right[2]) > KEY_STRAIGHT * w or max(top[2], bottom[2]) > KEY_STRAIGHT * h:
        return None

    def meet(vert, horiz):  # x = a y + b  and  y = c x + d
        a, b, _ = vert
        c, d, _ = horiz
        y = (c * b + d) / max(1 - c * a, 1e-9)
        return [a * y + b, y]

    quad = [meet(left, top), meet(right, top), meet(right, bottom), meet(left, bottom)]
    poly = Image.new("1", (w, h), 0)
    ImageDraw.Draw(poly).polygon([tuple(p) for p in quad], fill=1)
    q = np.asarray(poly, bool)
    iou = (q & fg).sum() / max((q | fg).sum(), 1)
    if iou < KEY_FILL:
        return None
    ys_, xs_ = np.nonzero(fg)
    x0, x1, y0, y1 = xs_.min(), xs_.max() + 1, ys_.min(), ys_.max() + 1
    corners = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
    size = np.array([x1 - x0, y1 - y0], float)
    off = max(float(np.max(np.abs(np.array(p) - c) / size)) for p, c in zip(quad, corners))
    if not KEY_MIN <= off <= KEY_MAX:
        return None
    return [[float(x), float(y)] for x, y in quad]


def _perspective_coeffs(src: list, dst: list) -> list[float]:
    """PIL PERSPECTIVE coefficients mapping output points `dst` to input points `src`."""
    rows, rhs = [], []
    for (x, y), (u, v) in zip(dst, src):
        rows.append([x, y, 1, 0, 0, 0, -u * x, -u * y])
        rows.append([0, 0, 0, x, y, 1, -v * x, -v * y])
        rhs += [u, v]
    return np.linalg.solve(np.array(rows, float), np.array(rhs, float)).tolist()


def warp_quad(img: Image.Image, quad: list) -> Image.Image:
    """The quad (tl, tr, br, bl) warped to an upright rectangle as big as its mean sides."""
    q = np.array(quad, float)
    w = (np.linalg.norm(q[1] - q[0]) + np.linalg.norm(q[2] - q[3])) / 2
    h = (np.linalg.norm(q[3] - q[0]) + np.linalg.norm(q[2] - q[1])) / 2
    w, h = max(2, round(w)), max(2, round(h))
    dst = [[0, 0], [w, 0], [w, h], [0, h]]
    coeffs = _perspective_coeffs(q.tolist(), dst)
    out = img.transform((w, h), Image.PERSPECTIVE, coeffs, Image.BICUBIC)
    a = np.asarray(out.getchannel("A")).copy()  # inside the quad is the product: solid
    a[:] = np.maximum(a, 255 * (ndimage.binary_erosion(a > 127, iterations=2)))
    out.putalpha(Image.fromarray(a))
    return out


# -------------------------------------------------------------------------------------- colours


def _hex(c) -> str:
    return "#{:02X}{:02X}{:02X}".format(*(round(float(v)) for v in c))


def hex_rgb(h: str) -> np.ndarray:
    h = h.lstrip("#")
    return np.array([int(h[i : i + 2], 16) for i in (0, 2, 4)], float)


def _kmeans(x: np.ndarray, k: int, iters: int = 12) -> tuple[np.ndarray, np.ndarray]:
    """Deterministic k-means (centres seeded at luminance quantiles)."""
    order = np.argsort(x @ np.array([0.299, 0.587, 0.114]))
    centres = x[order[((np.arange(k) + 0.5) / k * (len(x) - 1)).astype(int)]].astype(float)
    labels = np.zeros(len(x), int)
    for _ in range(iters):
        labels = ((x[:, None, :] - centres[None]) ** 2).sum(axis=2).argmin(axis=1)
        for j in range(k):
            members = x[labels == j]
            if len(members):
                centres[j] = members.mean(axis=0)
    return centres, labels


BAND = 0.08  # the outline band the body colour is read from (share of the product's size)
SAME = 75.0  # sRGB distance: a neighbouring slice of the same material (a steel gradient)
TRIM_GAP = 60.0  # the trim differs from the body by at least this (sRGB distance)


def colors(rgb: np.ndarray, fg: np.ndarray, k: int = 4) -> dict:
    """{body, trim, body_share, trim_share}: the product's dominant colours (sRGB hex). The
    product's pixels in k colour clusters; the body is the cluster with the most pixels within
    SAME of it (the light and dark slices of brushed steel are one material), overall plus in a
    band just inside the outline (the shell wraps the product; an oven window or a microwave door
    sits inside it), read as the median of those pixels; the trim is the biggest cluster that isn't
    a slice of the body's material (a steel highlight isn't trim) and differs from it by >= TRIM_GAP
    (else a darker body)."""
    inner = ndimage.binary_erosion(fg, iterations=max(1, min(fg.shape) // 100))
    region = inner if inner.sum() >= 50 else fg
    if not region.any():
        return {"body": "#B0B0B0", "trim": "#505050", "body_share": 0.0, "trim_share": 0.0}
    depth = max(2, int(BAND * min(fg.shape)))
    band = region & ~ndimage.binary_erosion(region, iterations=depth)
    step = max(1, int(np.sqrt(region.sum() / 20000)))
    sub = np.zeros_like(region)
    sub[::step, ::step] = True
    px = rgb[region & sub].reshape(-1, 3).astype(float)
    in_band = band[region & sub]
    if not in_band.any():
        in_band = np.ones(len(px), bool)
    k = max(1, min(k, len(px)))
    centres, labels = _kmeans(px, k)
    dist = np.linalg.norm(px[:, None, :] - centres[None], axis=2) < SAME  # N x k; slices count
    score = dist.mean(axis=0) + (dist & in_band[:, None]).sum(axis=0) / in_band.sum()
    b = int(np.argmax(score))
    near = dist[:, b]
    body = np.median(px[near], axis=0)
    shares = np.bincount(labels, minlength=k) / len(px)
    gap = np.linalg.norm(centres[:, None] - centres[None], axis=2) < SAME
    slices = gap[b] | (gap & gap[b][:, None]).any(axis=0)  # the body's material, two hops
    trim, trim_share = body * 0.45, 0.0
    for i in np.argsort(shares)[::-1]:
        c = np.median(px[labels == i], axis=0) if shares[i] else body
        if not slices[i] and shares[i] >= 0.04 and np.linalg.norm(c - body) >= TRIM_GAP:
            trim, trim_share = c, float(shares[i])
            break
    return {
        "body": _hex(body),
        "trim": _hex(trim),
        "body_share": round(float(near.mean()), 3),
        "trim_share": round(trim_share, 3),
    }


def _chroma(c: np.ndarray) -> float:
    return float((c.max() - c.min()) / 255.0)


def finish_for(texts: list[str | None], body_hex: str) -> str:
    """ "metal" | "plastic" | "painted": the listing's words first -- "matte"/"painted" anywhere,
    then metal words (a "Plastic Tub" dishwasher in "Stainless Steel" is steel outside), then
    plastic ones -- else a light low-chroma body reads as metal and anything else as painted."""
    for cue, words in _FINISH_WORDS:  # "matte" anywhere wins, then metal words, then plastic
        for text in texts:
            if re.search(rf"\b(?:{words})\b", (text or "").lower()):
                return cue
    c = hex_rgb(body_hex)
    return "metal" if _chroma(c) < 0.06 and 120 <= c.mean() <= 215 else "painted"
