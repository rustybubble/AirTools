"""The structure layer at building scale: tolerances scaled by the ground sample distance, the
mesh-distance filter and S4's exterior labels (docs/research/p1-buildings-bench.md §7)."""

import json
from argparse import Namespace
from pathlib import Path

import numpy as np
import pytest

from pipeline import structure

S = 40.0  # a building-sized copy of a kitchen-sized toy


def test_scene_gsd_and_tol_scale(tmp_path):
    (tmp_path / "cameras.txt").write_text("1 PINHOLE 100 100 50 50 50 50\n")
    (tmp_path / "images.txt").write_text("1 1 0 0 0 0 0 0 1 a.jpg\n50 50 1 50 60 2\n")
    (tmp_path / "points3D.txt").write_text(
        "1 0 0 10 255 255 255 0.5 1 0\n2 0 0 20 255 255 255 0.5 1 1\n"
    )
    gsd = structure.scene_gsd(tmp_path, 0.01)  # median depth 15 units = 0.15 m, f = 50 px
    assert gsd == pytest.approx(0.003)
    assert structure.tol_scale(0.0009) == 1.0  # the kitchen keeps its constants
    assert structure.tol_scale(gsd) == pytest.approx(1.5)


def test_fuse_scales_its_tolerances():
    from pipeline.experiments.lines.fuse import fuse

    sq = np.array([[0, 0], [1, 0], [1, 1], [0, 1]]) * S
    planes = {
        "planes": [
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
        "edges": [{"a": [0.1 * S, 0, 0], "b": [0.9 * S, 0, 0], "planes": ["top", "front"]}],
    }
    # on the top plane (3 mm x S high), stopping 2 cm x S short of the front edge
    lines = {"edges": [{"a": [0.5 * S, 0.8 * S, 0.003 * S], "b": [0.5 * S, 0.02 * S, 0.003 * S],
                        "axis": 1}]}  # fmt: skip
    assert fuse(lines, planes, 1.0)["edges"][0]["planes"] == []  # 12 cm off a 1 cm tolerance
    out = fuse(lines, planes, 1.0, tol_scale=S)
    assert out["edges"][0]["planes"] == ["top"]
    ps = np.array([c["p"] for c in out["corners"]])
    assert np.linalg.norm(ps - [0.5 * S, 0, 0], axis=1).min() < 0.01 * S


def test_merge_rects_scales_its_snap():
    from pipeline.experiments.lines.rects import merge_rects

    door = np.array([[0, 0, 0], [0.404, 0, 0], [0.404, 0.6, 0], [0, 0.6, 0]]) * S
    rects = {"frame": "scene", "objects": [{"id": "o0", "plane": "p0", "corners3d": door}]}
    line = {"a": [0.4 * S, -0.1 * S, 0], "b": [0.4 * S, 0.7 * S, 0], "kind": "line"}
    fused = {"frame": "scene", "edges": [line], "corners": []}
    assert merge_rects(fused, rects)["counts"]["rect_sides_on_limap"] == 0
    assert merge_rects(fused, rects, tol_scale=S)["counts"]["rect_sides_on_limap"] == 1


def test_lift_scales_metric_options_from_the_frames():
    from pipeline.experiments.planes.pxw_lift import scale_args, tol_scale

    k = np.array([[1000.0, 0, 500], [0, 1000, 500], [0, 0, 1]])
    frames = [{"z": np.full(50, 40.0), "K": k}]  # 40 units away at f = 1000 px
    assert tol_scale(frames, upm=2.0) == pytest.approx(10.0)  # 2 cm per px
    assert tol_scale(frames, upm=100.0) == 1.0
    a = Namespace(tol_scale=10.0, tol_mvs=0.01, tol_mono=0.02, fuse_off=0.02, fuse_voxel=0.05,
                  cell=0.02, simplify=0.01, near=0.03, min_edge=0.05, edge_extend=0.02,
                  corner_ext=0.1, min_area=0.02, edge_min_area=0.05)  # fmt: skip
    a = scale_args(a)
    assert a.cell == pytest.approx(0.2) and a.min_area == pytest.approx(2.0)


def test_metric_planes_restates_moge_metres():
    doc = {"params": {"units_per_m": 2.0}, "planes": [{"rms_m": 0.1, "area_m2": 4.0}]}
    structure.metric_planes(doc, m_per_unit=1.5)  # 1 MoGe metre = 2 units = 3 m
    assert doc["planes"][0]["rms_m"] == pytest.approx(0.3)
    assert doc["planes"][0]["area_m2"] == pytest.approx(36.0)


def test_drop_off_mesh_keeps_what_lies_on_the_mesh(tmp_path):
    import trimesh

    trimesh.creation.box((10, 10, 10)).export(tmp_path / "m.glb")  # faces at +-5
    doc = {
        "edges": [
            {"id": "e0", "a": [-4, 5, 5], "b": [4, 5, 5], "planes": ["p0"]},  # a box edge
            {"id": "e1", "a": [-4, 8, 0], "b": [4, 8, 0], "planes": ["p1"]},  # floats 3 m out
        ],
        "corners": [
            {"p": [5, 5, 5], "edges": ["e0", "e1"], "planes": ["p0", "p1"]},
            {"p": [0, 9, 0], "edges": ["e1"]},
        ],
        "planes": [
            {
                "id": "p0",
                "normal": [0, 1, 0],
                "offset": -5.0,
                "polygon3d": [[-4, 5, -4], [4, 5, -4], [4, 5, 4], [-4, 5, 4]],
            },
            {
                "id": "p1",
                "normal": [0, 1, 0],
                "offset": -8.0,
                "polygon3d": [[-4, 8, -4], [4, 8, -4], [4, 8, 4], [-4, 8, 4]],
            },
        ],
    }
    out = structure.drop_off_mesh(doc, tmp_path / "m.glb", 0.5)
    assert [e["id"] for e in out["edges"]] == ["e0"]
    assert out["corners"] == [{"p": [5, 5, 5], "edges": ["e0"], "planes": ["p0"]}]
    assert [p["id"] for p in out["planes"]] == ["p0"]
    assert out["counts"] == {"off_mesh_edges": 1, "off_mesh_corners": 1, "off_mesh_planes": 1}


def test_s4_args_indoors_and_on_a_building():
    assert structure.s4_args(1.0) == []
    args = structure.s4_args(20.0)
    assert "--exterior" in args and args[args.index("--mesh-tol") + 1] == pytest.approx(0.4)


@pytest.mark.parametrize(
    "w, h, above, label",
    [(0.3, 0.15, 3.0, None),  # a drawer-sized patch of brickwork
     (1.2, 1.5, 6.0, "window"), (1.0, 2.2, 0.1, "door"), (1.0, 2.2, 6.0, "window"),
     (8.0, 3.0, 0.0, None)],  # a whole bay
)  # fmt: skip
def test_exterior_labels_are_windows_and_doors(w, h, above, label):
    from pipeline.experiments.objects.run import label_of

    assert label_of(w, h, exterior=True, above_floor=above, depth=-0.15) == label
    assert label_of(w, h, exterior=True, above_floor=above, depth=0.1) is None  # proud: brick
    assert label_of(0.3, 0.12) == "drawer"  # indoors unchanged


def test_structure_job_records_its_scale(monkeypatch, tmp_path):
    def fake_run(cmd, log, *a, **k):
        raise RuntimeError("stop")

    for var in ("AIRTOOLS_LIMAP_ENV", "AIRTOOLS_PXW_ENV"):
        env = tmp_path / var
        (env / "bin").mkdir(parents=True)
        (env / "bin" / "python").touch()
        (env / structure._LIMAP_CONFIG).parent.mkdir(parents=True)
        (env / structure._LIMAP_CONFIG).touch()
        monkeypatch.setenv(var, str(env))
    monkeypatch.setattr(structure, "_run", fake_run)
    monkeypatch.setattr(structure, "scene_gsd", lambda *a: 0.03)
    job, _ = structure.prepare(
        True, tmp_path / "dense", tmp_path / "mvs", tmp_path / "work",
        m_per_unit=5.7, up_cameras=tmp_path / "up.json",
    )  # fmt: skip
    job.start()
    job._thread.join()
    assert job.report["gsd_m"] == 0.03 and job.report["tol_scale"] == 15.0
    json.dumps(job.report)


def test_finish_filters_by_the_mesh_and_gives_s4_the_kept_planes(monkeypatch, tmp_path):
    import trimesh

    trimesh.creation.box((10, 10, 10)).export(tmp_path / "mesh.r2.glb")
    on = [[-4, 5, -4], [4, 5, -4], [4, 5, 4], [-4, 5, 4]]
    planes = {
        "frame": "cameras",
        "planes": [
            {"id": "p0", "normal": [0, 1, 0], "offset": -5.0, "polygon": on},
            {
                "id": "p1",
                "normal": [0, 1, 0],
                "offset": -9.0,
                "polygon": [[x, 9, z] for x, _, z in on],
            },
        ],
        "edges": [],
        "corners": [],
    }
    lines = {"schema": "airtools.structure/1", "frame": "cameras", "planes": [], "corners": [],
             "edges": [{"id": "e0", "a": [-4, 5, 5], "b": [4, 5, 5], "kind": "line"},
                       {"id": "e1", "a": [-4, 9, 0], "b": [4, 9, 0], "kind": "line"}]}  # fmt: skip
    s4_planes = []

    def fake_run(cmd, log, *a, **k):
        cmd = [str(c) for c in cmd]
        if "pipeline.experiments.lines.limap_run" in cmd:
            out = Path(cmd[cmd.index("--out") + 1])
            out.mkdir(parents=True, exist_ok=True)
            (out / "structure.json").write_text(json.dumps(lines))
        elif cmd[1].endswith("pxw_infer.py"):
            Path(cmd[cmd.index("--out") + 1]).mkdir(parents=True)
        elif cmd[1].endswith("pxw_lift.py"):
            Path(cmd[cmd.index("--out") + 1]).write_text(json.dumps(planes))
        elif "pipeline.experiments.objects.run" in cmd:
            s4_planes.append(json.loads(Path(cmd[cmd.index("--planes") + 1]).read_text()))
            raise RuntimeError("no frames")

    for var in ("AIRTOOLS_LIMAP_ENV", "AIRTOOLS_PXW_ENV"):
        env = tmp_path / var
        (env / "bin").mkdir(parents=True)
        (env / "bin" / "python").touch()
        (env / structure._LIMAP_CONFIG).parent.mkdir(parents=True)
        (env / structure._LIMAP_CONFIG).touch()
        monkeypatch.setenv(var, str(env))
    monkeypatch.setattr(structure, "_run", fake_run)
    monkeypatch.setattr(structure, "scene_gsd", lambda *a: 0.05)  # 5 px = 25 cm
    job, _ = structure.prepare(
        True, tmp_path / "dense", tmp_path / "mvs", tmp_path / "work",
        m_per_unit=1.0, up_cameras=tmp_path / "up.json",
    )  # fmt: skip
    entry = job.finish(
        np.eye(4), tmp_path, 2, mesh_file="mesh.r2.glb", cameras_file="c.json",
        frames_dir=tmp_path, sparse_raw=tmp_path,
    )  # fmt: skip
    doc = json.loads((tmp_path / "structure.r2.json").read_text())
    assert entry["planes"] == 1 and [p["id"] for p in doc["planes"]] == ["p0"]
    assert all(np.abs(np.array([e["a"], e["b"]])[:, 1]).max() == 5 for e in doc["edges"])
    assert [p["id"] for p in s4_planes[0]["planes"]] == ["p0"] and s4_planes[0]["frame"] == "scene"
    assert job.report["off_mesh"]["off_mesh_planes"] == 1
