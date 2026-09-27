import numpy as np

from pipeline.experiments.bench.novel_coverage import moved


def test_moved_camera_steps_back_along_its_view_axis_and_keeps_orientation():
    R = np.array([[0.0, 0, 1], [0, 1, 0], [-1, 0, 0]])  # looks along world -x
    c = np.array([1.0, 2.0, 3.0])
    e = {"id": "x", "R": R.tolist(), "t_wc": (-R @ c).tolist(), "fx": 1, "fy": 1, "cx": 0,
         "cy": 0, "w": 2, "h": 2}  # fmt: skip
    cam = moved(e, 0.0, -0.6)
    assert np.allclose(cam.R, R)
    assert np.allclose(-cam.R.T @ cam.t, c - 0.6 * R[2])  # 0.6 m behind: +x in world
    assert np.allclose(-cam.R.T @ moved(e, 0.9, 0.0).t, c + 0.9 * R[0])
