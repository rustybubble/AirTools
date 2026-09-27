"""E5: E1 template geometry + E3 photo projection (docs/research/assets-llm/e5-combo.md).

    uv run --with manifold3d --with rtree --with pyrender --with scipy --with open_clip_torch \
        --index https://download.pytorch.org/whl/cpu \
        python -m server.experiments.assets_llm.e5_run [part ids...] [--rerun] [--gallery]

Per part: rebuild E1's best variant from its cached answers (e1b where the keep gate kept it,
else e1; critique colour changes pass a photo-colour gate), then project the product photo along
the face E3's cached view call chose onto the triangles facing it. Every other triangle keeps
its template colour. `--rerun` spends new Qwen calls on the E1 weak spots: a fresh generation
call with the `bent_strip` template for the gutter hangers (STRIP) and a small finish call
(PBR metal) for the metal parts (PBR). `--gallery` renders photo | Hunyuan | E3 | E1 | E5 per
part into work/assets-llm/e5/gallery/ and gallery.html.

Rows (work/assets-llm/results.jsonl): e5, e1-strip / e5-strip, e1-pbr / e5-pbr, e1b-cgate.
All LLM answers are cached under work/assets-llm/e5/<id>/, so a rerun costs no tokens.
"""

import argparse
import html
import io
import json
import time
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image, ImageDraw

from server import part_templates as T
from server.experiments.assets_llm import decal
from server.experiments.assets_llm.e1_run import (
    ROOT,
    _gen_prompt,
    _patched,
    _user,
    ask,
    pieces_floating,
)
from server.experiments.assets_llm.e3_run import _patch_pyrender_textures
from server.experiments.assets_llm.groq_smoke import _photo_data_url
from server.experiments.assets_llm.llm import WORK
from server.experiments.assets_llm.score import photo_mask, score_part

OUT = WORK / "e5"
RESULTS = WORK / "results.jsonl"
FACE_DOT = 0.35  # triangles facing the photo axis at least this much get the photo
TEX_MAX = 1024
JPEG_Q = 80
SIL = 160  # silhouette raster for the profile flip test
COLOR_DIST = 48  # RGB distance for "this colour occurs in the photo"
COLOR_SUPPORT = 0.05  # a role colour is supported when >= 5 % of product pixels are near it
# Catalogue sink photos look down into the bowl; the v2 view call said "front" (E3 §4). An
# open-top bowl's photo face is its top.
TEMPLATE_FACE = {"sink": "top"}
# "Photo mode": front relief the photo already shows (slats, nozzles) is dropped, so the photo
# and the geometry don't draw the same pattern twice, offset (moire on the wall AC).
FLAT = {
    "box_appliance": {"grille1_style": "none", "grille2_style": "none", "control": False},
    "fixture": {"nozzle_rings": 0, "center_disc": False},
}
STRIP = ["amerimax-home-products-21812-846830", "amerimax-home-products-m0722b-d6dfa5"]
PBR = [
    "simpson-strong-tie-lus26-a9ce07",
    "kraus-ke1us32-ec0e5f",
    "delta-75641-d491ed",
    "samsung-rf70f29mer-f13bd2",
]
FINISH_NOTE = (
    'Also give "finishes": {{"role": "{names}"}} for every colour role (chrome = mirror-like'
    " plated metal, stainless / brushed = satin metal, galvanized = dull zinc-coated steel)."
)
FINISH_PROMPT = """Product: {name}. Material/finish from the listing: {material} / {finish}.
Our 3D model paints these parts (role: colour): {roles}.
For each role, which surface finish does the photo show? Options: {names}.
Reply JSON only: {{"finishes": {{"role": "finish"}}}}"""


# ---------------------------------------------------------------- inputs


def _part(pid: str) -> dict:
    pdir = ROOT / "data" / "parts" / pid
    return json.loads((pdir / "part.json").read_text()) | {
        "_dir": pdir,
        "_photo": pdir / "image.jpg",
    }


def _cached(path: Path) -> tuple[dict, list]:
    c = json.loads(path.read_text())
    return c["json"], c["records"]


def _e1b_kept(pid: str) -> bool:
    rows = [json.loads(line) for line in RESULTS.read_text().splitlines()]
    return any(
        r.get("method") == "e1b" and r["part"] == pid and r["notes"].startswith("kept")
        for r in rows
    )


def _product_pixels(photo: Path) -> np.ndarray:
    img = Image.open(photo).convert("RGB")
    mask = photo_mask(photo)
    px = np.asarray(img, np.int16)[mask]
    return px[:: max(1, len(px) // 20000)]


def colour_support(px: np.ndarray, hex_str: str) -> float:
    c = np.array([int(hex_str.lstrip("#")[i : i + 2], 16) for i in (0, 2, 4)])
    return float((np.linalg.norm(px - c, axis=1) < COLOR_DIST).mean())


def gate_colours(photo: Path, old: dict, new: dict) -> tuple[dict, list[str]]:
    """Keep a critique colour change only if the photo contradicts the old colour (under
    COLOR_SUPPORT of product pixels near it) and supports the new one better."""
    px = _product_pixels(photo)
    out, log = dict(old), []
    for role, c in new.items():
        if role not in old or c.upper() == old[role].upper():
            continue
        so, sn = colour_support(px, old[role]), colour_support(px, c)
        ok = so < COLOR_SUPPORT and sn > so
        out[role] = c if ok else old[role]
        log.append(f"{role} {old[role]}->{c} {'ok' if ok else 'dropped'} ({so:.2f}/{sn:.2f})")
    return out, log


def e1_best(part: dict) -> tuple[dict, list, list[str], bool]:
    """(choice, ledger records, colour-gate log, e1b kept) from E1's cached answers."""
    d = WORK / "e1" / part["id"]
    gen, recs = _cached(d / "gen.json")
    template, params, colors = T.validate(gen.get("template"), gen.get("params"), gen.get("colors"))
    choice = {"template": template, "params": params, "colors": colors}
    kept = _e1b_kept(part["id"])
    if not kept:
        return choice, recs, [], False
    crit, crecs = _cached(d / "critique.json")
    switch = crit.get("template")
    if switch in T.TEMPLATES and switch != template:  # never kept on this set; same rule as e1_run
        return (
            {"template": switch, "params": crit.get("params"), "colors": crit.get("colors")},
            recs + crecs,
            [],
            True,
        )
    patched = _patched(choice, {"params": crit.get("params")})
    patched["colors"], log = gate_colours(part["_photo"], colors, crit.get("colors") or {})
    return patched, recs + crecs, log, True


def view_of(part: dict, template: str) -> tuple[dict, dict | None, str]:
    """E3's cached view answer (no new call), the template prior applied on top."""
    v = json.loads((WORK / "e3" / part["id"] / "view.json").read_text())
    ans = dict(v["answer"])
    note = f"view {ans['face']}"
    if template in TEMPLATE_FACE and ans["face"] in ("front", "back"):
        ans["face"] = TEMPLATE_FACE[template]
        note += f"->{ans['face']} (template prior)"
    return ans, v["rec"], note


# ---------------------------------------------------------------- projection


def _world_meshes(scene: trimesh.Scene) -> list[tuple[str, trimesh.Trimesh]]:
    out = []
    for node in scene.graph.nodes_geometry:
        tf, name = scene.graph[node]
        m = scene.geometry[name].copy()
        m.apply_transform(tf)
        out.append((name, m))
    return out


def _silhouette(meshes, r, up, lo, ext) -> np.ndarray:
    """Orthographic silhouette along the photo axis, stretched to SIL x SIL over the bbox face."""
    img = Image.new("1", (SIL, SIL), 0)
    draw = ImageDraw.Draw(img)
    for _, m in meshes:
        tri = m.vertices[m.faces]
        u = (tri @ r - lo[0]) / ext[0] * (SIL - 1)
        v = (1 - (tri @ up - lo[1]) / ext[1]) * (SIL - 1)
        for pu, pv in zip(u, v):
            draw.polygon(list(zip(pu, pv)), fill=1)
    return np.asarray(img, bool)


def project(scene: trimesh.Scene, photo: Path, face: str, rotate_cw: int = 0):
    """-> (textured Scene, info). The cropped photo is stretched over the geometry's bbox on
    `face` (E1 geometry fills the product's outline, so the photo's product bbox maps onto it)
    and put on every triangle facing that way; a side (profile) photo goes on both sides with
    one world mapping, mirrored or not, whichever silhouette matches the photo better.
    A triangle gets the photo only if a ray from it toward the camera escapes the part (so side
    louvres and grille rings don't pick up photo streaks). ponytail: hard FACE_DOT cut, no
    normal-weighted fade to the template colour (needs a baked texture)."""
    crop, _ = decal.cut_product(photo)
    crop = crop.rotate(-rotate_cw, expand=True)
    n, r, up = (np.array(v, float) for v in decal.FACES[face])
    meshes = _world_meshes(scene)
    allv = np.vstack([m.vertices for _, m in meshes])
    lo = np.array([(allv @ r).min(), (allv @ up).min()])
    ext = np.maximum(np.array([np.ptp(allv @ r), np.ptp(allv @ up)]), 1e-9)
    side = face in decal.OPPOSITE
    flip, sil_iou = False, None
    if side:  # which way round does the profile photo run? compare silhouettes both ways
        mask = photo_mask(photo)
        ys, xs = np.nonzero(mask)
        pm = Image.fromarray(mask[ys.min() : ys.max() + 1, xs.min() : xs.max() + 1])
        pm = np.asarray(pm.rotate(-rotate_cw, expand=True).resize((SIL, SIL)), bool)
        geo = _silhouette(meshes, r, up, lo, ext)
        scores = [float((pm & g).sum() / max((pm | g).sum(), 1)) for g in (geo, geo[:, ::-1])]
        flip, sil_iou = scores[1] > scores[0], round(max(scores), 3)

    tex = crop.copy()
    tex.thumbnail((TEX_MAX, TEX_MAX))
    buf = io.BytesIO()
    tex.save(buf, "JPEG", quality=JPEG_Q)
    tex = Image.open(io.BytesIO(buf.getvalue()))  # format JPEG: kept on export

    out, photo_parts = trimesh.Scene(), []
    whole = trimesh.util.concatenate([m for _, m in meshes])
    eps = 1e-4 * float(np.linalg.norm(whole.extents))
    for name, m in meshes:
        dots = m.face_normals @ n
        hit = np.abs(dots) >= FACE_DOT if side else dots >= FACE_DOT
        idx = np.nonzero(hit)[0]
        if len(idx):  # occlusion: some ray toward the camera (centre, 3 corners) must escape
            d = np.sign(dots[idx])[:, None] * n
            c = m.triangles_center[idx]
            pts = [c] + [c + 0.9 * (m.triangles[idx][:, k] - c) for k in range(3)]
            blocked = np.logical_and.reduce([whole.ray.intersects_any(p + d * eps, d) for p in pts])
            hit[idx[blocked]] = False
        if (~hit).any():
            rest = m.submesh([np.nonzero(~hit)[0]], append=True)
            rest.visual = trimesh.visual.TextureVisuals(material=m.visual.material)
            out.add_geometry(rest, node_name=name, geom_name=name)
        if hit.any():
            photo_parts.append(m.submesh([np.nonzero(hit)[0]], append=True))
    if photo_parts:
        pm_ = trimesh.util.concatenate(photo_parts)
        pm_.unmerge_vertices()
        u = (pm_.vertices @ r - lo[0]) / ext[0]
        uv = np.c_[1 - u if flip else u, (pm_.vertices @ up - lo[1]) / ext[1]]
        pm_.visual = trimesh.visual.TextureVisuals(
            uv=uv,
            material=trimesh.visual.material.PBRMaterial(
                baseColorTexture=tex, metallicFactor=0.0, roughnessFactor=0.8
            ),
        )
        pm_.merge_vertices()
        out.add_geometry(pm_, node_name="photo", geom_name="photo")
    info = {
        "face": face,
        "flip": flip,
        "sil_iou": sil_iou,
        "photo_tris": int(sum(len(p.faces) for p in photo_parts)),
        "tex_px": list(tex.size),
    }
    return out, info


# ---------------------------------------------------------------- rows


def _row(method, part, glb, geom_glb, recs, wall, notes, extra=None) -> dict:
    s = score_part(part["_dir"], glb)
    pieces, floating = pieces_floating(geom_glb)  # geometry only: the photo split adds none
    r = {
        "method": method,
        "part": part["id"],
        "valid": bool(s["loads"] and s["bbox_ok"] and s["origin_ok"]),
        "bbox_err_pct": round(max(abs(v) for v in s["dims_err_pct"]), 3),
        "pieces": pieces,
        "floating": floating,
        "iou": s.get("sil_iou"),
        "clip": s.get("clip_sim"),
        "calls": len(recs),
        "in_tokens": sum(x.get("in_tokens") or 0 for x in recs),
        "out_tokens": sum(x.get("out_tokens") or 0 for x in recs),
        "wall_s": round(wall, 2),
        "notes": notes[:300],
        "triangles": s["triangles"],
        "file_kb": s["file_kb"],
        **(extra or {}),
    }
    lines = RESULTS.read_text().splitlines()
    done = {(x["method"], x["part"]) for x in map(json.loads, lines) if "method" in x}
    if (method, part["id"]) not in done:
        with open(RESULTS, "a") as f:
            f.write(json.dumps(r) + "\n")
    print(json.dumps(r), flush=True)
    return r


def _build(part, choice, finishes, path: Path) -> tuple[trimesh.Scene, dict]:
    scene, info = T.build(
        choice["template"], part["dims_mm"], choice.get("params"), choice.get("colors"), finishes
    )
    scene.export(path)
    return scene, info


def combo(part, choice, finishes, recs, out: Path, geo_name: str, method: str, note: str):
    """Build the geometry, project the photo, score both; returns the E5 row."""
    t0 = time.time()
    geo = out / f"{geo_name}.glb"
    scene, info = _build(part, choice, finishes, geo)
    view, vrec, vnote = view_of(part, info["template"])
    textured, pinfo = project(scene, part["_photo"], view["face"], view.get("rotate_cw", 0))
    glb = out / f"{method}.glb"
    textured.export(glb)
    wall = time.time() - t0 + sum(x["latency_s"] for x in recs) + vrec["latency_s"]
    note = f"{info['template']}; {vnote}{' flipped' if pinfo['flip'] else ''}; {note}"
    return _row(method, part, glb, geo, recs + [vrec], wall, note, {"template": info["template"]})


def run_part(part: dict, rerun: bool) -> None:
    pid = part["id"]
    out = OUT / pid
    out.mkdir(parents=True, exist_ok=True)
    choice, recs, clog, kept = e1_best(part)
    base = "e1b" if kept else "e1"
    if clog:  # the colour gate changed what e1b would have been: score that geometry alone
        glb = out / "e1b_cgate.glb"
        _build(part, choice, None, glb)
        _row("e1b-cgate", part, glb, glb, recs, 0.0, "; ".join(clog))
    combo(part, choice, None, recs, out, "base", "e5", f"base {base}; " + "; ".join(clog))
    if choice["template"] in FLAT and view_of(part, choice["template"])[0]["face"] == "front":
        flat = {**choice, "params": {**choice["params"], **FLAT[choice["template"]]}}
        combo(part, flat, None, recs, out, "flat", "e5-flat", f"base {base}; front relief off")

    if rerun and pid in STRIP:
        size = {k: round(part["dims_mm"][k], 1) for k in ("w", "h", "d")}
        prompt = (
            _gen_prompt(part, size, None) + "\n" + FINISH_NOTE.format(names="|".join(T.FINISHES))
        )
        prompt = prompt.replace('"front_face_note"', '"finishes": {...}, "front_face_note"')
        url = _photo_data_url(part["_photo"], 512)
        gen, grecs = ask(_user(prompt, url), f"e5/{pid}/gen-strip", out / "gen_strip.json")
        if gen is not None:
            t, p, c = T.validate(gen.get("template"), gen.get("params"), gen.get("colors"))
            ch = {"template": t, "params": p, "colors": c}
            fin = gen.get("finishes") or {}
            glb = out / "e1s.glb"
            _build(part, ch, fin, glb)
            _row(
                "e1-strip",
                part,
                glb,
                glb,
                grecs,
                sum(x["latency_s"] for x in grecs),
                t,
                {"template": t},
            )
            combo(part, ch, fin, grecs, out, "e1s", "e5-strip", f"finishes {fin}")
    if rerun and pid in PBR:
        names = "|".join(T.FINISHES)
        roles = ", ".join(f"{k}: {v}" for k, v in choice["colors"].items())
        prompt = FINISH_PROMPT.format(
            name=part["name"],
            material=part.get("material"),
            finish=part.get("finish"),
            roles=roles,
            names=names,
        )
        url = _photo_data_url(part["_photo"], 384)
        ans, frecs = ask(_user(prompt, url), f"e5/{pid}/finish", out / "finish.json")
        if ans is not None:
            fin = ans.get("finishes") or {}
            glb = out / "e1p.glb"
            _build(part, choice, fin, glb)
            _row(
                "e1-pbr",
                part,
                glb,
                glb,
                recs + frecs,
                0.0,
                f"base {base}; finishes {fin}",
                {"template": choice["template"]},
            )
            combo(part, choice, fin, recs + frecs, out, "e1p", "e5-pbr", f"finishes {fin}")


# ---------------------------------------------------------------- gallery


def _results() -> dict:
    rows = {}
    for line in RESULTS.read_text().splitlines():
        r = json.loads(line)
        if "method" in r:
            rows[(r["method"], r["part"])] = r
    return rows


def _columns(part: dict, rows: dict) -> list[tuple[str, Path | None, dict | None]]:
    pid = part["id"]
    hy = part.get("baseline_glb")
    hy = ROOT / hy if hy and part.get("tier") == "ai_mesh" else None
    hrow = next(
        (
            json.loads(line)
            for line in (WORK / "baseline_scores.jsonl").read_text().splitlines()
            if json.loads(line)["id"] == pid
        ),
        None,
    )
    hrow = (
        hy and hy.exists() and hrow and {"iou": hrow.get("sil_iou"), "clip": hrow.get("clip_sim")}
    )
    e3 = WORK / "e3" / pid
    e3m = "e3-decal-view" if (e3 / "decal_view.glb").exists() else "e3-decal"
    e3g = e3 / ("decal_view.glb" if e3m == "e3-decal-view" else "decal.glb")
    e1m = "e1b" if _e1b_kept(pid) else "e1"
    e5m = next(m for m in ("e5-strip", "e5-flat", "e5-pbr", "e5") if (m, pid) in rows)
    return [
        ("Hunyuan", hy if hy and hy.exists() else None, hrow),
        (e3m, e3g, rows.get((e3m, pid))),
        (e1m, WORK / "e1" / pid / f"{e1m}.glb", rows.get((e1m, pid))),
        (e5m, OUT / pid / f"{e5m}.glb", rows.get((e5m, pid))),
    ]


def gallery(parts: list[dict]) -> None:
    from server.experiments.assets_llm.render import BG, load_scene, render_views

    size = 256
    gdir = OUT / "gallery"
    gdir.mkdir(parents=True, exist_ok=True)
    rows = _results()
    cards = []
    for part in parts:
        pid = part["id"]
        cols = _columns(part, rows)
        face = view_of(part, rows[("e5", pid)]["template"])[0]["face"]
        n = np.array(decal.FACES[face][0], float)
        views = {"photo side": n + [0.35, 0.45, 0.35], "3/4": (1.0, 0.75, 1.25)}
        sheet = Image.new("RGB", (size * 5, size * 2 + 40), (255, 255, 255))
        d = ImageDraw.Draw(sheet)
        d.text((6, 4), f"{pid}  {part['dims_mm']}", fill=(0, 0, 0))
        p = Image.open(part["_photo"]).convert("RGB")
        p.thumbnail((size, size * 2))
        sheet.paste(p, ((size - p.width) // 2, 40 + (2 * size - p.height) // 2))
        d.text((6, 22), "photo", fill=(0, 0, 0))
        for i, (label, glb, r) in enumerate(cols, start=1):
            score = f"  IoU {r['iou']:.2f} CLIP {r['clip']:.2f}" if r and r.get("clip") else ""
            d.text((i * size + 6, 22), label + score, fill=(0, 0, 0))
            if glb is None:
                sheet.paste(Image.new("RGB", (size, 2 * size), BG), (i * size, 40))
                d.text((i * size + 80, 40 + size), "no Hunyuan mesh", fill=(60, 60, 60))
                continue
            vs = render_views(load_scene(glb), size, views)
            for j, k in enumerate(views):
                sheet.paste(Image.fromarray(vs[k][0]), (i * size, 40 + j * size))
        path = gdir / f"{pid}.jpg"
        sheet.save(path, quality=85)
        cards.append((pid, part["name"], cols))
        print("gallery", path)
    _html(cards, OUT / "gallery.html")


def _html(cards, path: Path) -> None:
    body = []
    for pid, name, cols in cards:
        cells = "".join(
            f"<td>{html.escape(lbl)}<br>"
            + (f"IoU {r['iou']:.2f} / CLIP {r['clip']:.2f}" if r and r.get("clip") else "–")
            + (f"<br>{r['file_kb']:.0f} KB" if r and r.get("file_kb") else "")
            + "</td>"
            for lbl, _, r in cols
        )
        body.append(
            f'<section id="{pid}"><h2>{html.escape(name)}</h2><p class="id">{pid}</p>'
            f'<a href="gallery/{pid}.jpg"><img src="gallery/{pid}.jpg" alt="{pid}"></a>'
            f"<table><tr><td>photo</td>{cells}</tr></table></section>"
        )
    nav = " · ".join(f'<a href="#{pid}">{pid[:22]}</a>' for pid, _, _ in cards)
    path.write_text(
        "<!doctype html><meta charset=utf-8><title>E5 gallery</title>"
        "<style>body{font:14px system-ui,sans-serif;margin:16px;background:#fafafa;color:#222}"
        "img{max-width:100%;height:auto;border:1px solid #ddd}section{margin:24px 0}"
        "table{border-collapse:collapse;width:100%;max-width:1280px}"
        "td{border:1px solid #ddd;padding:4px 6px;width:20%;vertical-align:top}"
        ".id{color:#777;margin:0 0 6px}h2{margin:0;font-size:16px}</style>"
        "<h1>E5: template geometry + photo projection</h1>"
        "<p>Columns: photo | Hunyuan3D-2.1 | E3 photo decal on the exact box | E1 template"
        " (e1b where kept) | E5 (e5-strip, e5-flat or e5-pbr where run). Top row: the side the photo"
        f" shows; bottom row: 3/4 view.</p><p>{nav}</p>" + "".join(body)
    )


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("ids", nargs="*")
    ap.add_argument("--rerun", action="store_true", help="spend Qwen calls on STRIP/PBR parts")
    ap.add_argument("--gallery", action="store_true", help="only (re)build the gallery")
    a = ap.parse_args()
    _patch_pyrender_textures()
    testset = {p["id"]: p for p in json.loads((WORK / "testset.json").read_text())}
    ids = a.ids or list(testset)
    parts = [_part(pid) | {k: testset[pid][k] for k in ("baseline_glb", "tier")} for pid in ids]
    if not a.gallery:
        for part in parts:
            run_part(part, a.rerun)
    gallery(parts)
