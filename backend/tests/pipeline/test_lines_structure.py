import itertools

import numpy as np
from scipy.spatial.transform import Rotation

from pipeline.experiments.lines.structure import (
    Seg,
    extend_to_corners,
    junctions,
    manhattan_axes,
    merge_collinear,
    snap_to_axes,
)


def _box_fragments(rot: np.ndarray, rng: np.random.Generator) -> tuple[list[Seg], np.ndarray]:
    """A 0.6 x 0.4 x 0.8 box's 12 edges, each broken into 2 noisy, slightly short fragments."""
    size = np.array([0.6, 0.4, 0.8])
    corners = np.array(list(itertools.product([0, 1], repeat=3))) * size
    segs = []
    for i, j in itertools.combinations(range(8), 2):
        if np.count_nonzero(corners[i] != corners[j]) != 1:
            continue
        a, b = rot @ corners[i], rot @ corners[j]
        for lo, hi in ((0.03, 0.45), (0.5, 0.97)):
            p0 = a + lo * (b - a) + rng.normal(0, 0.002, 3)
            p1 = a + hi * (b - a) + rng.normal(0, 0.002, 3)
            segs.append(Seg(p0, p1, {len(segs)}))
    return segs, corners @ rot.T


def test_box_edges_and_corners_recovered():
    rng = np.random.default_rng(0)
    rot = Rotation.from_euler("xyz", [20, -35, 50], degrees=True).as_matrix()
    segs, gt = _box_fragments(rot, rng)
    # a floater that must not change the axes
    segs.append(Seg(np.array([2.0, 2, 2]), np.array([2.1, 2.3, 2.05]), {99}))

    axes = manhattan_axes(segs)
    # each recovered axis matches one box axis to < 1 degree
    align = np.abs(axes.T @ rot).max(axis=1)
    assert np.all(align > np.cos(np.radians(1.0)))

    snap_to_axes(segs, axes)
    assert sum(s.axis >= 0 for s in segs) == 24

    merged = merge_collinear(segs, dist_tol=0.01, gap_tol=0.05)
    box = [s for s in merged if s.axis >= 0]
    assert len(box) == 12
    assert all(len(s.views) == 2 for s in box)

    corners = junctions(box, dist_tol=0.01, ext_tol=0.05)
    assert len(corners) == 8
    assert all(len(c["edges"]) == 3 for c in corners)
    err = [np.linalg.norm(gt - c["p"], axis=1).min() for c in corners]
    assert max(err) < 0.005

    extend_to_corners(box, corners, ext_tol=0.05)
    ends = np.array([p for s in box for p in (s.p0, s.p1)])
    assert np.linalg.norm(ends[:, None] - gt[None], axis=2).min(axis=1).max() < 0.005


def test_fuse_lines_with_planes():
    from pipeline.experiments.lines.fuse import fuse

    sq = [[0, 0], [1, 0], [1, 1], [0, 1]]
    planes = {
        "method": "toy",
        "planes": [  # counter top z=0 (y in [0,1]) and its front face y=0 (z in [-1,0])
            {
                "id": "top",
                "normal": [0, 0, 1],
                "offset": 0.0,
                "polygon": [[x, y, 0] for x, y in sq],
            },
            {
                "id": "front",
                "normal": [0, 1, 0],
                "offset": 0.0,
                "polygon": [[x, 0, -z] for x, z in sq],
            },
        ],
        "edges": [{"a": [0.1, 0, 0], "b": [0.9, 0, 0], "planes": ["top", "front"]}],
    }
    lines = {
        "params": {"m_per_unit": 1.0},
        "edges": [
            # on the top plane (3 mm high), running to the front edge but stopping 2 cm short
            {"a": [0.5, 0.8, 0.003], "b": [0.5, 0.02, 0.003], "axis": 1},
            # a free off-axis pair crossing in mid-air: must not give a corner
            {"a": [2.0, 2.0, 1.0], "b": [2.3, 2.1, 1.2], "axis": -1},
            {"a": [2.0, 2.1, 1.2], "b": [2.3, 2.0, 1.0], "axis": -1},
        ],
    }
    out = fuse(lines, planes, m_per_unit=1.0, snap_lines=True, drop_free_corners="offaxis")
    e = out["edges"]
    assert e[0]["planes"] == ["top"] and abs(e[0]["a"][2]) < 1e-9  # snapped into the plane
    assert e[1]["planes"] == [] and e[2]["planes"] == []
    creases = [x for x in e if x["kind"] == "crease"]
    assert len(creases) == 1 and sorted(creases[0]["planes"]) == ["front", "top"]
    # the crease end is not moved away from its support; the line gets a corner on the crease
    ps = np.array([c["p"] for c in out["corners"]])
    assert np.linalg.norm(ps - [0.5, 0, 0], axis=1).min() < 1e-6
    assert all(np.linalg.norm(ps - [2.15, 2.05, 1.1], axis=1) > 0.05)


def test_merge_corners_needs_shared_support():
    from pipeline.experiments.lines.fuse import Planes, merge_corners

    planes = Planes(
        {
            "planes": [
                {"id": "a", "normal": [0, 0, 1], "offset": 0.0},
                {"id": "b", "normal": [0, 1, 0], "offset": 0.0},
            ]
        }
    )
    segs = [Seg(np.array([0.0, 0, 0]), np.array([0.0, 0, 1]))]  # vertical line x=y=0
    corners = [
        {"p": np.array([0.003, 0, 0.001]), "edges": [0], "planes": [0], "residual": 0},
        {"p": np.array([-0.002, 0.001, 0]), "edges": [], "planes": [0, 1], "residual": 0},
        {"p": np.array([0.004, 0.004, 0.004]), "edges": [], "planes": [], "residual": 0},
    ]
    out = merge_corners(segs, planes, corners, dist=0.01, res_tol=0.005)
    assert len(out) == 2  # the unsupported third corner stays separate
    assert np.linalg.norm(out[0]["p"]) < 1e-4  # line x plane a x plane b = origin, not the mean


def test_to_scene_keeps_points_on_planes():
    from pipeline.calibrate import similarity_matrix
    from pipeline.experiments.lines.frame import to_scene

    rot = Rotation.from_euler("xyz", [10, 70, -30], degrees=True).as_matrix()
    m = similarity_matrix(0.26, rot, np.array([1.0, -2.0, 0.5]))
    n = np.array([0.0, 0.6, 0.8])
    x = np.array([0.3, 1.0, -0.75])  # on the plane n.x + 0.0 = 0
    doc = {
        "frame": "cameras",
        "cameras": "x",
        "edges": [{"a": x.tolist(), "b": [0, 0, 0]}],
        "corners": [{"p": x.tolist()}],
        "planes": [{"normal": n.tolist(), "offset": 0.0, "polygon": [x.tolist(), [0, 0, 0]]}],
    }
    out = to_scene(doc, m)
    p, q = out["planes"][0], np.array(out["corners"][0]["p"])
    assert out["frame"] == "scene" and "cameras" not in out
    assert abs(np.dot(p["normal"], q) + p["offset"]) < 1e-12
    assert np.allclose(q, 0.26 * rot @ x + [1.0, -2.0, 0.5])
    assert np.allclose(p["polygon"][0], q)


def test_merge_rects_snaps_sides_to_lines():
    from pipeline.experiments.lines.rects import merge_rects

    # a 0.4 x 0.6 door in the z=0 plane, fitted 4 mm too far right on its right side
    door = [[0, 0, 0], [0.404, 0, 0], [0.404, 0.6, 0], [0, 0.6, 0]]
    rects = {
        "frame": "scene",
        "objects": [{"id": "o0", "plane": "p0", "corners3d": door}],
        "planes": [{"id": "p0r0", "source": "s4-relief", "normal": [0, 0, 1], "offset": 0}],
        "groups": [],
    }
    fused = {
        "frame": "scene",
        "planes": [],
        "edges": [{"a": [0.4, -0.1, 0], "b": [0.4, 0.7, 0], "kind": "line"}],  # LIMAP right side
        "corners": [{"p": [0.0, 0.0, 0.003]}],  # a LIMAP corner at the bottom-left
    }
    assert not [c for c in merge_rects(fused, rects)["corners"] if c.get("kind") == "rect"]
    out = merge_rects(fused, rects, rect_corners="all")  # no corner has both sides on lines
    obj = np.array(out["objects"][0]["corners3d"])
    assert np.allclose(obj[1], [0.4, 0, 0]) and np.allclose(obj[2], [0.4, 0.6, 0])
    assert np.allclose(obj[0], [0, 0, 0])  # unsnapped sides keep their own line
    rect_c = [c for c in out["corners"] if c.get("kind") == "rect"]
    assert len(rect_c) == 3  # the bottom-left rect corner yields to the LIMAP corner 3 mm away
    assert len(out["planes"]) == 1 and out["counts"]["rect_sides_on_limap"] == 1
