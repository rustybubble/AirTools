"""Axis-aligned rectangles in a plane orthophoto (S4, docs/research/structure/s4-objects.md).

LSD segments in the orthophoto are split into horizontal and vertical families (the plane frame
has v = up, so cabinet and appliance outlines are axis-aligned), clustered into lines, and every
(left, right, top, bottom) line quadruple is scored by how much of each side the segments cover.
A rectangle is kept when all four sides are well covered and no other line crosses it end to
end (so a cabinet pair's outer box loses to its two doors). Pure numpy + cv2; pixels here are
orthophoto pixels with integer coordinates at pixel centres.
"""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np


@dataclass
class Line:
    pos: float  # col for a vertical line, row for a horizontal one (length-weighted mean)
    cover: np.ndarray  # bool per row (vertical) / per col (horizontal): a segment lies there
    length: float
    step: np.ndarray | None = None  # across-line grey step per row/col (from edge_cover)


def lsd_segments(gray: np.ndarray, min_len: float = 12.0) -> np.ndarray:
    """(N, 4) x0, y0, x1, y1 LSD segments at least `min_len` px long."""
    lines = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(gray)[0]
    if lines is None:
        return np.zeros((0, 4))
    s = lines.reshape(-1, 4).astype(float)
    return s[np.hypot(s[:, 2] - s[:, 0], s[:, 3] - s[:, 1]) >= min_len]


def families(segs: np.ndarray, tol_deg: float = 3.0) -> tuple[np.ndarray, np.ndarray]:
    """Split segments into (horizontal, vertical) within `tol_deg` of the axes."""
    ang = np.degrees(np.arctan2(segs[:, 3] - segs[:, 1], segs[:, 2] - segs[:, 0])) % 180
    hor = (ang < tol_deg) | (ang > 180 - tol_deg)
    ver = np.abs(ang - 90) < tol_deg
    return segs[hor], segs[ver]


def cluster_lines(
    segs: np.ndarray, vertical: bool, size: int, tol: float = 2.5, min_len: float = 20.0
) -> list[Line]:
    """Group near-collinear axis-aligned segments into lines. `size` is the image extent along
    the line (rows for vertical lines, cols for horizontal ones)."""
    if not len(segs):
        return []
    pos = (segs[:, 0] + segs[:, 2]) / 2 if vertical else (segs[:, 1] + segs[:, 3]) / 2
    a = np.minimum(segs[:, 1], segs[:, 3]) if vertical else np.minimum(segs[:, 0], segs[:, 2])
    b = np.maximum(segs[:, 1], segs[:, 3]) if vertical else np.maximum(segs[:, 0], segs[:, 2])
    ln = b - a
    order = np.argsort(pos)
    groups, cur = [], [order[0]]
    for i in order[1:]:
        if pos[i] - np.average(pos[cur], weights=ln[cur]) <= tol:
            cur.append(i)
        else:
            groups.append(cur)
            cur = [i]
    groups.append(cur)
    out = []
    for g in groups:
        cover = np.zeros(size, bool)
        for i in g:
            cover[int(max(a[i], 0)) : int(min(np.ceil(b[i]), size - 1)) + 1] = True
        if cover.sum() >= min_len:
            out.append(Line(float(np.average(pos[g], weights=ln[g])), cover, float(ln[g].sum())))
    return out


def edge_cover(
    gray: np.ndarray, lines: list[Line], vertical: bool, thr: float = 6.0, band: int = 1
) -> list[Line]:
    """Replace each line's LSD coverage with gradient evidence: a row (vertical line) or col
    (horizontal line) counts as covered when the across-line intensity step, max over +-`band`
    px, is at least `thr` grey levels. LSD fragments a low-contrast door gap; the step doesn't."""
    g = cv2.GaussianBlur(gray.astype(np.float32), (0, 0), 0.8)
    step = (
        np.abs(
            cv2.Sobel(g, cv2.CV_32F, 1, 0, ksize=3)
            if vertical
            else cv2.Sobel(g, cv2.CV_32F, 0, 1, ksize=3)
        )
        / 4
    )  # grey levels per px
    valid = cv2.erode((gray > 0).astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
    out = []
    for ln in lines:
        p = round(ln.pos)
        lo, hi = max(p - band, 0), min(p + band, step.shape[1 if vertical else 0] - 1)
        if vertical:
            s, ok = step[:, lo : hi + 1].max(1), valid[:, p]
        else:
            s, ok = step[lo : hi + 1, :].max(0), valid[p, :]
        out.append(Line(ln.pos, (s >= thr) & ok | ln.cover, ln.length, np.where(ok, s, 0)))
    return out


def fit_rects(
    vlines: list[Line],
    hlines: list[Line],
    min_w: float,
    max_w: float,
    min_h: float,
    max_h: float,
    min_side: float = 0.55,
    min_mean: float = 0.7,
    cross: float = 0.6,
    margin: float = 4.0,
    max_iou: float = 0.2,
    outer_min: float = 0.95,
    new_corner_px: float = 5.0,
) -> tuple[list[dict], list[dict]]:
    """Score every line quadruple. Returns (atomic, outer), each [{c0, c1, r0, r1, score,
    sides}] with c0 < c1, r0 < r1, sizes in px, chosen greedily by score with IoU <= `max_iou`.
    Atomic rects have no other line crossing them end to end (a door, not its cabinet pair).
    Outer rects are crossed ones scoring >= `outer_min` that add at least two corners no atomic
    rect has (a dishwasher's outline around its door and control strip, a microwave's body)."""
    if len(vlines) < 2 or len(hlines) < 2:
        return [], []
    vc = np.array([ln.pos for ln in vlines])
    hr = np.array([ln.pos for ln in hlines])
    # cumulative coverage: V[k, r] = rows < r covered by vertical line k (same for H over cols)
    V = np.concatenate([np.zeros((len(vlines), 1)), np.cumsum([ln.cover for ln in vlines], 1)], 1)
    H = np.concatenate([np.zeros((len(hlines), 1)), np.cumsum([ln.cover for ln in hlines], 1)], 1)
    vi = np.clip(np.round(vc).astype(int), 0, H.shape[1] - 1)  # col index into H
    hi = np.clip(np.round(hr).astype(int), 0, V.shape[1] - 1)  # row index into V
    # the same for the grey step: contrast breaks ties between near-parallel lines 2-4 px apart
    SV = np.concatenate([np.zeros((len(vlines), 1)), np.cumsum([_step(ln) for ln in vlines], 1)], 1)
    SH = np.concatenate([np.zeros((len(hlines), 1)), np.cumsum([_step(ln) for ln in hlines], 1)], 1)
    atomic, crossed = [], []
    for i, j in zip(
        *np.nonzero((vc[None] - vc[:, None] >= min_w) & (vc[None] - vc[:, None] <= max_w))
    ):
        c0, c1 = vi[i], vi[j]
        inner_v = (vc > vc[i] + margin) & (vc < vc[j] - margin)
        for k, m in zip(
            *np.nonzero((hr[None] - hr[:, None] >= min_h) & (hr[None] - hr[:, None] <= max_h))
        ):
            r0, r1 = hi[k], hi[m]
            side = np.array(
                [
                    (V[i, r1] - V[i, r0]) / (r1 - r0),
                    (V[j, r1] - V[j, r0]) / (r1 - r0),
                    (H[k, c1] - H[k, c0]) / (c1 - c0),
                    (H[m, c1] - H[m, c0]) / (c1 - c0),
                ]
            )
            if side.min() < min_side or side.mean() < min_mean:
                continue
            inner_h = (hr > hr[k] + margin) & (hr < hr[m] - margin)
            x = inner_v.any() and ((V[inner_v, r1] - V[inner_v, r0]) / (r1 - r0)).max() > cross
            x = x or inner_h.any() and ((H[inner_h, c1] - H[inner_h, c0]) / (c1 - c0)).max() > cross
            rect = {
                "c0": vc[i],
                "c1": vc[j],
                "r0": hr[k],
                "r1": hr[m],
                "score": float(side.mean()),
                "contrast": float(
                    (SV[i, r1] - SV[i, r0] + SV[j, r1] - SV[j, r0]) / (r1 - r0)
                    + (SH[k, c1] - SH[k, c0] + SH[m, c1] - SH[m, c0]) / (c1 - c0)
                ),
                "sides": side.round(3).tolist(),
            }
            (crossed if x else atomic).append(rect)
    atomic = _nms(atomic, max_iou)
    have = np.array([q for r in atomic for q in _corners(r)]).reshape(-1, 2)

    def new_corners(r):
        return sum(np.hypot(*(have - q).T).min(initial=np.inf) > new_corner_px for q in _corners(r))

    outer = _nms([r for r in crossed if r["score"] >= outer_min and new_corners(r) >= 2], max_iou)
    return atomic, outer


def _step(ln: Line) -> np.ndarray:
    return np.minimum(ln.step, 50.0) if ln.step is not None else ln.cover.astype(float)


def _corners(r: dict) -> list[tuple[float, float]]:
    return [(r["c0"], r["r0"]), (r["c1"], r["r0"]), (r["c1"], r["r1"]), (r["c0"], r["r1"])]


def _nms(cands: list[dict], max_iou: float) -> list[dict]:
    keep: list[dict] = []
    for c in sorted(cands, key=lambda c: (-round(c["score"], 2), -c.get("contrast", 0))):
        if all(_iou(c, k) <= max_iou for k in keep):
            keep.append(c)
    return keep


def _iou(a: dict, b: dict) -> float:
    iw = max(0.0, min(a["c1"], b["c1"]) - max(a["c0"], b["c0"]))
    ih = max(0.0, min(a["r1"], b["r1"]) - max(a["r0"], b["r0"]))
    inter = iw * ih
    area = lambda r: (r["c1"] - r["c0"]) * (r["r1"] - r["r0"])
    return inter / (area(a) + area(b) - inter)


def group_repeats(objs: list[dict], rel_tol: float = 0.06) -> list[list[int]]:
    """Indices of objects with the same width and height (within `rel_tol`), 2+ members each.
    `objs` carry `w` and `h` (any unit). Greedy around the largest cluster first."""
    left = set(range(len(objs)))
    groups = []
    while left:
        best = []
        for i in left:
            m = [
                j
                for j in left
                if abs(objs[j]["w"] / objs[i]["w"] - 1) <= rel_tol
                and abs(objs[j]["h"] / objs[i]["h"] - 1) <= rel_tol
            ]
            if len(m) > len(best):
                best = m
        if len(best) < 2:
            break
        groups.append(sorted(best))
        left -= set(best)
    return groups


def regularize(
    objs: list[dict],
    members: list[int],
    row_tol: float,
    mode: str = "chain",
    chain_gap: float = 0.01,
) -> None:
    """Snap a repeat group in place. `objs` carry `plane` and `uv` = [u0, v0, u1, v1].

    Members on one plane whose bottoms are within `row_tol` form a row and share its median
    bottom and top (every mode). Then, per mode:
    - "rows": nothing more;
    - "chain": members of a row that touch (gap < `chain_gap`), e.g. a cabinet's door pair, keep
      the chain's outer edges and split it into equal widths with one shared seam;
    - "median": every member gets the group's median width about its own centre, and a member
      alone in its row the median height (this moves seams apart; see s4-objects.md)."""
    uv = np.array([objs[m]["uv"] for m in members], float)
    rows, left = [], set(range(len(members)))
    while left:
        i = min(left)
        row = [
            j
            for j in left
            if objs[members[j]]["plane"] == objs[members[i]]["plane"]
            and abs(uv[j, 1] - uv[i, 1]) < row_tol
        ]
        left -= set(row)
        rows.append(sorted(row, key=lambda j: uv[j, 0]))
    for row in rows:
        if len(row) > 1:
            uv[row, 1], uv[row, 3] = np.median(uv[row, 1]), np.median(uv[row, 3])
    if mode == "chain":
        for row in rows:
            chains = [[row[0]]]
            for j in row[1:]:
                if uv[j, 0] - uv[chains[-1][-1], 2] < chain_gap:
                    chains[-1].append(j)
                else:
                    chains.append([j])
            for ch in chains:
                edges = np.linspace(uv[ch[0], 0], uv[ch[-1], 2], len(ch) + 1)
                uv[ch, 0], uv[ch, 2] = edges[:-1], edges[1:]
    elif mode == "median":
        w = np.median(uv[:, 2] - uv[:, 0])
        h = np.median(uv[:, 3] - uv[:, 1])
        cu = (uv[:, 0] + uv[:, 2]) / 2
        uv[:, 0], uv[:, 2] = cu - w / 2, cu + w / 2
        for row in rows:
            if len(row) == 1:
                cv = (uv[row[0], 1] + uv[row[0], 3]) / 2
                uv[row[0], 1], uv[row[0], 3] = cv - h / 2, cv + h / 2
    for m, r in zip(members, uv):
        objs[m]["uv"] = r.tolist()
        objs[m]["w"], objs[m]["h"] = r[2] - r[0], r[3] - r[1]
