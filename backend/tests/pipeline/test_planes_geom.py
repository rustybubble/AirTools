import numpy as np

from pipeline.experiments.planes import geom


def _face(axis: int, rng, n=2000, noise=0.002):
    """Points on the face {x_axis = 0, other coords in [0, 1]} of the unit cube's corner."""
    p = rng.uniform(0, 1, (n, 3))
    p[:, axis] = rng.normal(0, noise, n)
    return p


def test_ransac_plane_ignores_outliers():
    rng = np.random.default_rng(0)
    p = np.vstack([_face(2, rng), rng.uniform(-1, 1, (500, 3))])
    n, d, rms, inl = geom.ransac_plane(p, 0.01)
    assert abs(abs(n[2]) - 1) < 1e-3 and abs(d) < 1e-3 and rms < 0.004
    assert inl[:2000].mean() > 0.99 and inl[2000:].mean() < 0.1


def test_fuse_split_and_intersect_cube_corner():
    rng = np.random.default_rng(1)
    normals, offsets, supports = [], [], []
    for axis in range(3):
        for _ in range(4):  # four "frames" per face, each a slightly perturbed fit
            n = np.eye(3)[axis] + rng.normal(0, 0.01, 3)
            normals.append(n / np.linalg.norm(n))
            offsets.append(rng.normal(0, 0.002))
            supports.append(_face(axis, rng, 300))
    # a parallel plane 5 cm in front of face z must not merge with it
    normals.append(np.eye(3)[2])
    offsets.append(-0.05)
    supports.append(_face(2, rng, 300) + [0, 0, 0.05])
    lab = geom.fuse_planes(np.array(normals), np.array(offsets), supports, 5, 0.02, 0.1)
    assert len(set(lab[:12])) == 3 and lab[12] not in set(lab[:12])
    assert all(len(set(lab[4 * k : 4 * k + 4])) == 1 for k in range(3))

    faces = []
    for axis in range(3):
        S = _face(axis, rng, 20000)  # dense support: ~8 points per 2 cm cell
        n, d, _, _ = geom.ransac_plane(S, 0.01)
        n, d = (n, d) if n[axis] > 0 else (-n, -d)
        S = S - np.outer(S @ n + d, n)
        comps = geom.support_components(n, d, S, np.arange(len(S)) % 4, 0.02, 2, 0.1)
        assert len(comps) == 1 and abs(comps[0]["area"] - 1) < 0.1
        faces.append((n, d, comps[0]["cells"]))

    seg = geom.plane_edge(*faces[0], *faces[1], near=0.03, min_len=0.05)
    assert seg is not None
    a, b = sorted(seg, key=lambda p: p[2])
    assert np.linalg.norm(a[:2]) < 0.01 and np.linalg.norm(b[:2]) < 0.01
    assert a[2] < 0.05 and b[2] > 0.95  # the x=0 / y=0 edge spans the z range [0, 1]

    ns = np.stack([f[0] for f in faces])
    p = geom.three_plane_point(ns, np.array([f[1] for f in faces]))
    assert np.linalg.norm(p) < 0.01
    assert (
        geom.plane_edge(*faces[0], faces[0][0], faces[0][1] + 0.5, faces[0][2], 0.03, 0.05) is None
    )


def test_manhattan_snap():
    rng = np.random.default_rng(2)
    R = np.linalg.qr(rng.normal(size=(3, 3)))[0]
    base = np.vstack([R.T, -R.T, [[0.6, 0.8, 0]]])  # 6 axis normals and one off-axis
    noisy = base + np.vstack([rng.normal(0, 0.02, (6, 3)), np.zeros((1, 3))])
    noisy /= np.linalg.norm(noisy, axis=1, keepdims=True)
    out, snap = geom.manhattan_snap(noisy, np.ones(7), 5)
    assert snap[:6].all() and not snap[6]
    assert np.allclose(out[:3] @ out[:3].T, np.eye(3)) and np.allclose(out[3:6], -out[:3])
    assert (np.abs(out[:6] @ R).max(1) > np.cos(np.radians(3))).all()
