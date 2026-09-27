"""S4 rectangle fitting and lifting (pipeline/experiments/objects)."""

import cv2
import numpy as np

from pipeline.experiments.objects import ortho as O
from pipeline.experiments.objects import rects as Rx
from pipeline.experiments.objects.run import grid_res


def _doors() -> np.ndarray:
    """Two equal doors 100x160 px with a 2 px gap, on a mid-grey wall."""
    img = np.full((240, 300), 90, np.uint8)
    img[40:200, 40:140] = 170
    img[40:200, 142:242] = 170
    return cv2.GaussianBlur(img, (0, 0), 0.7)


def test_fit_rects_finds_each_door_not_the_pair():
    g = _doors()
    h, w = g.shape
    hs, vs = Rx.families(Rx.lsd_segments(g))
    vl = Rx.edge_cover(g, Rx.cluster_lines(vs, True, h), True)
    hl = Rx.edge_cover(g, Rx.cluster_lines(hs, False, w), False)
    atomic, _ = Rx.fit_rects(vl, hl, 30, 250, 30, 250)
    got = sorted((r["c0"], r["c1"], r["r0"], r["r1"]) for r in atomic)
    assert len(got) == 2, got
    # edges sit half a pixel outside each bright block (pixel-centre convention); LSD pushes
    # the two sides of a 2 px gap apart by ~0.5 px, so allow 1 px
    np.testing.assert_allclose(got[0], [39.5, 139.5, 39.5, 199.5], atol=1.0)
    np.testing.assert_allclose(got[1], [141.5, 241.5, 39.5, 199.5], atol=1.0)
    groups = Rx.group_repeats([{"w": c1 - c0, "h": r1 - r0} for c0, c1, r0, r1 in got])
    assert groups == [[0, 1]]


def test_grid_lift_is_exactly_on_plane_and_round_trips():
    o, n, u, v = O.plane_frame(np.array([1.0, 0.02, 0.05]), np.array([-1.1, -0.6, 0.2]))
    assert abs(v @ [0, 1, 0]) > 0.99 and abs(u @ n) < 1e-12  # v is up, frame orthonormal
    g = O.Grid(o, u, v, n, -0.5, -0.3, 0.5, 0.3, 0.002)
    uv = np.array([[-0.21, 0.05], [0.33, -0.12]])
    xyz = g.lift(uv)
    np.testing.assert_allclose((xyz - o) @ n, 0, atol=1e-12)
    np.testing.assert_allclose(g.to_uv(xyz), uv, atol=1e-12)
    np.testing.assert_allclose(g.px_to_uv(g.uv_to_px(uv)), uv, atol=1e-12)


def test_poisson_recovers_image_from_gradients():
    y, x = np.mgrid[:40, :60]
    img = np.sin(x / 7) + np.cos(y / 5) + 2.0 * (x > 30)
    rec = O.poisson(np.diff(img, axis=1), np.diff(img, axis=0))
    np.testing.assert_allclose(rec - rec.mean(), img - img.mean(), atol=1e-9)


def test_regularize_chain_shares_row_and_splits_door_pair_evenly():
    objs = [
        {"plane": "p1", "uv": [0.000, 0.002, 0.260, 0.280]},
        {"plane": "p1", "uv": [0.262, 0.000, 0.530, 0.281]},  # touches the first: one cabinet
        {"plane": "p2", "uv": [0.000, 0.500, 0.265, 0.780]},  # other plane: its own row
    ]
    Rx.regularize(objs, [0, 1, 2], row_tol=0.005, mode="chain")
    a, b, c = (o["uv"] for o in objs)
    assert a[1] == b[1] and a[3] == b[3]  # shared bottom and top
    assert a[2] == b[0] and np.isclose(a[2] - a[0], b[2] - b[0])  # one seam, equal widths
    assert a[0] == 0.0 and b[2] == 0.530  # outer edges kept
    assert c == [0.000, 0.500, 0.265, 0.780]


def test_grid_res_keeps_kitchen_planes_and_caps_a_facade():
    wall = np.array([[0.0, 0.0], [1.4, 0.7]])  # the kitchen's biggest plane: 2 mm stays
    assert grid_res(wall, 0.05, 0.002, 1e6) == 0.002
    facade = np.array([[0.0, 0.0], [12.0, 5.0]])  # a 60 m^2 building face
    res = grid_res(facade, 0.05, 0.002, 1e6)
    assert res > 0.002 and 12.1 * 5.1 / res**2 <= 1e6 * 1.001
