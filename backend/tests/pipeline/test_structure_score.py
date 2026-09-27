import numpy as np
import trimesh

from pipeline.experiments.structure.gt import refine_corner, reproj, robust_triangulate
from pipeline.experiments.structure.score import (
    MeshQuery,
    Structure,
    closest_on_segments,
    edge_sharpness,
    gt_planes,
    point_in_polygon,
)


def test_refine_corner_subpixel():
    """A bright quadrant, anti-aliased by 4x supersampling, whose corner sits at (100, 80) in
    OpenCV px (pixel centres at integers): the LSD line intersection lands within 0.3 px of it
    from a pick 2.5 px off (within near_px of both lines)."""
    big = np.zeros((800, 1200), np.uint8)
    big[322:, 402:] = 200  # boundary at 402/4 = 100.5 in pixel-edge units = 100.0 in OpenCV px
    img = big.reshape(200, 4, 300, 4).mean((1, 3)).astype(np.uint8)
    q, info = refine_corner(img, np.array([102.5, 82.5]))
    assert q is not None, info
    assert np.allclose(q, [100.0, 80.0], atol=0.3)


def test_robust_triangulate_rejects_jumped_track():
    rng = np.random.default_rng(1)
    X = np.array([0.2, -0.1, 3.0])
    P = []
    for k in range(12):
        c = np.array([k * 0.1 - 0.6, 0.0, 0.0])
        P.append(
            np.array([[1000, 0, 960], [0, 1000, 540], [0, 0, 1.0]])
            @ np.hstack([np.eye(3), -c[:, None]])
        )
    P = np.array(P)
    uv = np.array([(p @ np.append(X, 1))[:2] / (p @ np.append(X, 1))[2] for p in P])
    uv += rng.normal(0, 0.3, uv.shape)
    uv[:4] += [25.0, 0.0]  # a third of the track jumped to a neighbouring corner
    Xh, keep = robust_triangulate(P, uv, max_px=1.5)
    assert keep[4:].all() and not keep[:4].any()
    assert np.linalg.norm(Xh - X) < 0.01 and reproj(P[keep], uv[keep], Xh).max() < 1.5


def test_segments_polygon_and_priority_snap():
    d, p = closest_on_segments(
        np.array([[0.5, 1.0, 0.0]]), np.zeros((1, 3)), np.array([[1.0, 0, 0]])
    )
    assert np.isclose(d[0, 0], 1.0) and np.allclose(p[0, 0], [0.5, 0, 0])
    sq = np.array([[0, 0], [1, 0], [1, 1], [0, 1.0]])
    assert point_in_polygon(np.array([[0.5, 0.5], [1.5, 0.5]]), sq).tolist() == [True, False]
    s = Structure(
        {
            "corners": [{"p": [0, 0, 0]}],
            "edges": [{"a": [0, 0, 0], "b": [1, 0, 0]}],
            "planes": [
                {
                    "normal": [0, 1, 0],
                    "offset": 0.0,
                    "origin": [0.5, 0, 0.5],
                    "u": [1, 0, 0],
                    "polygon": [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]],
                }
            ],
        }
    )
    q = np.array([[0.01, 0.01, 0], [0.5, 0.02, 0], [0.5, 0.02, 0.3], [2, 2, 2]])
    kinds, out = s.snap(q, 0.03, lambda x: x * 0)
    assert kinds == ["corner", "edge", "plane", "mesh"]
    assert np.allclose(out[2], [0.5, 0, 0.3])


def test_mesh_query_and_sharpness_box_vs_rounded():
    box = trimesh.creation.box([1.0, 1.0, 1.0])
    box = box.subdivide_to_size(0.01)
    _, d = MeshQuery(box).closest(np.array([[0.6, 0.6, 0.0]]))
    assert np.isclose(d[0], np.hypot(0.1, 0.1), atol=1e-6)
    X = {"a": np.array([-0.4, 0.5, 0.5]), "b": np.array([0.4, 0.5, 0.5])}
    gt = {"planes": [], "edges": [{"name": "e", "a": "a", "b": "b", "normals": ["+y", "front"]}]}
    cam = np.array([0.0, 2.0, 5.0])
    sharp = edge_sharpness(box, gt, X, gt_planes(gt, X, cam), cam)["e"]
    assert sharp["width_m"] is not None and sharp["width_m"] <= 0.008


def test_planes_creases_corner_on_a_box_corner():
    """Three faces of a box meeting at (0, 0, 0) (outward normals +x, +y, +z) -> three Manhattan
    planes, three convex creases and a 3-plane corner at the origin."""
    from pipeline.experiments.structure.planes import corners, creases, detect_planes

    rng = np.random.default_rng(0)
    g = rng.random((20000, 2)) * 0.3
    z = np.zeros(len(g))
    P = np.vstack(
        [np.c_[z, -g[:, 0], -g[:, 1]], np.c_[-g[:, 0], z, -g[:, 1]], np.c_[-g[:, 0], -g[:, 1], z]]
    )
    N = np.repeat(np.array([[1.0, 0, 0], [0, 1.0, 0], [0, 0, 1.0]]), len(g), 0)
    planes = detect_planes(P, N, np.eye(3), eps=0.003, min_pts=100)
    assert len(planes) == 3
    edges = creases(P, planes, line_r=0.02)
    assert len(edges) == 3 and all(e["convex"] for e in edges)
    cs = corners(P, planes, edges, corner_r=0.02)
    assert len(cs) == 1 and np.linalg.norm(cs[0]["p"]) < 1e-3


def test_regularize_glb_roundtrip(tmp_path):
    """A noisy flat grid is snapped onto its plane; the GLB keeps its layout (UVs untouched)."""
    from pipeline.experiments.structure.regularize import _read_glb, regularize_glb

    n = 21
    xy = np.stack(np.meshgrid(np.linspace(0, 1, n), np.linspace(0, 1, n)), -1).reshape(-1, 2)
    V = np.c_[xy, np.random.default_rng(0).normal(0, 0.002, len(xy))]
    k = np.arange(n * n).reshape(n, n)[:-1, :-1].ravel()
    F = np.r_[np.c_[k, k + 1, k + n + 1], np.c_[k, k + n + 1, k + n]]
    grid = trimesh.Trimesh(V, F, process=False)
    grid.visual = trimesh.visual.TextureVisuals(
        uv=np.random.default_rng(1).random((len(grid.vertices), 2))
    )
    grid.export(tmp_path / "in.glb")
    st = Structure({"planes": [{"normal": [0, 0, 1], "offset": -0.0005}]})

    class A:
        plane_tol, normal_dot, margin, edge_r, corner_r = 0.01, 0.9, 0.02, 0.0, 0.0

    stats = regularize_glb(tmp_path / "in.glb", tmp_path / "out.glb", st, A)
    assert stats["plane_frac"] == 1.0
    out = trimesh.load(tmp_path / "out.glb", force="mesh", process=False)
    assert np.allclose(out.vertices[:, 2], 0.0005, atol=1e-6)
    js_in, _ = _read_glb(tmp_path / "in.glb")
    js_out, _ = _read_glb(tmp_path / "out.glb")
    assert js_in["bufferViews"] == js_out["bufferViews"]


def test_r6_snap_policy_corner_beats_edge_only_when_close():
    s = Structure(
        {"corners": [{"p": [0, 0, 0]}], "edges": [{"a": [-1, 0.03, 0], "b": [1, 0.03, 0]}]}
    )
    q = np.array([[0, 0.022, 0], [0, 0.005, 0], [0.5, 0.055, 0]])
    fb = lambda x: x * 0
    assert s.snap(q, 0.03, fb)[0] == ["corner", "corner", "edge"]
    kinds, _ = s.snap(q, 0.03, fb, corner_r=0.025, edge_r=0.02, beat=0.01)
    assert kinds == ["edge", "corner", "mesh"]
