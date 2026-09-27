"""pipeline/parts.py on a synthetic run of base units: an appliance front (z -0.3..0.3) between two
cabinet fronts, a counter at y 0.9 with a lip line at 0.87, a backsplash at x -0.6 and a bigger
wall-cabinet plane at x -0.3 above it (a decoy), a floor whose fitted plane tilts 4 degrees, and a
patch of counter sagging into the opening (R1 §3.4). Cameras look down -x from x = 2.5."""

import hashlib
import json

import numpy as np
import pytest
import trimesh
from PIL import Image, ImageDraw

from pipeline import parts
from pipeline.package import validate_package

W, H, FX = 160, 120, 150.0
AXES = [[0, 0, 1], [0, -1, 0], [1, 0, 0]]  # run, gravity, depth (the kitchen's layout)
RECT = [[0.02, 0.1, 0.3], [0.02, 0.1, -0.3], [0.02, 0.85, -0.3], [0.02, 0.85, 0.3]]


def _grid(o, u, v, n=12):
    o, u, v = map(np.asarray, (o, u, v))
    s, t = np.meshgrid(np.linspace(0, 1, n + 1), np.linspace(0, 1, n + 1))
    V = o + s.reshape(-1, 1) * u + t.reshape(-1, 1) * v
    i = np.arange((n + 1) ** 2).reshape(n + 1, n + 1)
    q = np.c_[i[:-1, :-1].ravel(), i[:-1, 1:].ravel(), i[1:, 1:].ravel(), i[1:, :-1].ravel()]
    return V, np.r_[q[:, [0, 1, 2]], q[:, [0, 2, 3]]]


def _mesh() -> trimesh.Trimesh:
    quads = [
        ((0.02, 0.1, -0.3), (0, 0, 0.6), (0, 0.75, 0)),  # appliance front
        ((0.0, 0.1, 0.31), (0, 0, 0.69), (0, 0.75, 0)),  # left cabinet front
        ((0.0, 0.1, -1.0), (0, 0, 0.69), (0, 0.75, 0)),  # right cabinet front
        ((-0.6, 0.0, -1.0), (1.6, 0, 0), (0, 0, 2.0)),  # floor
        ((-0.4, 0.9, -1.0), (0.45, 0, 0), (0, 0, 2.0)),  # counter
        ((-0.6, 0.83, -0.3), (0.2, 0, 0), (0, 0, 0.6)),  # counter sagging into the opening
        ((-0.6, 0.9, -1.0), (0.2, 0, 0), (0, 0, 0.7)),  # counter, beside the sag
        ((-0.6, 0.9, 0.3), (0.2, 0, 0), (0, 0, 0.7)),
        ((-0.6, 0.9, -1.0), (0, 0.6, 0), (0, 0, 2.0)),  # backsplash
    ]
    Vs, Fs, off = [], [], 0
    for q in quads:
        V, F = _grid(*q)
        Vs.append(V)
        Fs.append(F + off)
        off += len(V)
    V = np.vstack(Vs)
    img = Image.new("RGB", (8, 8), (200, 180, 160))
    uv = np.c_[(V[:, 2] + 1) / 2, V[:, 1] / 1.5]
    m = trimesh.Trimesh(V, np.vstack(Fs), process=False)
    m.visual = trimesh.visual.TextureVisuals(
        uv=uv, material=trimesh.visual.material.PBRMaterial(baseColorTexture=img)
    )
    return m


def _plane(pid, n, off, poly, area, rms=0.004):
    return {"id": pid, "normal": n, "offset": off, "polygon": poly, "area_m2": area, "rms_m": rms}


def _structure() -> dict:
    a = np.radians(4)
    n = [
        -np.sin(a),
        np.cos(a),
        0.0,
    ]  # floor fit tilting 4 deg along the depth, through y 0 at x 0.5
    floor = [[x, np.tan(a) * (x - 0.5), z] for x in (0.1, 0.9) for z in (-1, 1)]
    wall = lambda x, y0, y1: [[x, y, z] for y in (y0, y1) for z in (-1, 1)]
    return {
        "schema": "airtools.structure/1",
        "frame": "scene",
        "axes": AXES,
        "planes": [
            _plane("pf", n, float(-np.dot(n, [0.5, 0, 0])), floor, 1.6),
            _plane("pw", [1, 0, 0], 0.6, wall(-0.6, 0.9, 1.5), 1.2),  # backsplash
            _plane("pc", [1, 0, 0], 0.3, wall(-0.3, 1.6, 2.4), 1.6),  # wall cabinets: decoy
            _plane("pd", [1, 0, 0], 0.0, wall(0.0, 0.1, 0.85), 1.5),  # door plane
            _plane(
                "pt", [0, 1, 0], -0.9, [[x, 0.9, z] for x in (-0.6, 0.05) for z in (-1, 1)], 1.3
            ),
        ],
        "edges": [
            {"id": "e1", "a": [0.01, 0.05, 0.31], "b": [0.01, 0.86, 0.31], "kind": "line"},
            {"id": "e2", "a": [0.02, 0.05, -0.30], "b": [0.02, 0.86, -0.30], "kind": "line"},
            {"id": "e3", "a": [0.01, 0.05, -0.33], "b": [0.01, 0.86, -0.33], "kind": "line"},
            {"id": "e4", "a": [0.01, 0.87, 1.0], "b": [0.01, 0.87, -1.0], "kind": "line"},
            {"id": "e5", "a": [0.02, 0.86, 0.3], "b": [0.02, 0.86, -0.3], "kind": "line"},
            {"id": "o1e0", "a": RECT[2], "b": RECT[3], "kind": "boundary"},
        ],
        "corners": [{"id": "c0", "p": [0.01, 0.87, 0.31]}],
        "objects": [{"id": "o1", "label": "appliance", "plane": "pd", "corners3d": RECT}],
    }


def _cam(fid, z):
    R = np.array([[0, 0, -1], [0, -1, 0], [-1, 0, 0]], float)  # looking down -x, +y up
    c = np.array([2.5, 0.6, z])
    return {"id": fid, "file": f"frames/{fid}.jpg", "thumb": f"thumbs/{fid}.jpg",
            "R": R.tolist(), "t": (-R @ c).tolist(), "position": c.tolist(),
            "fx": FX, "fy": FX, "cx": W / 2, "cy": H / 2, "w": W, "h": H}  # fmt: skip


CAMS = [_cam("0001", -0.5), _cam("0002", 0.0), _cam("0003", 0.5)]


def _rect_mask(cam):
    uv, _ = parts.project(RECT, cam, W)
    m = Image.new("L", (W, H))
    ImageDraw.Draw(m).polygon([tuple(p) for p in uv], fill=255)
    return m, [*uv.min(0), *uv.max(0)]


def _frame():
    return parts.component_frame(np.array([1.0, 0.0, 0.05]), AXES, np.array([0.0, 1.0, 0.0]))


def _ext(fr):
    q = fr.to(RECT)
    return parts.Extent(q[:, 0].min(), q[:, 0].max(), q[:, 1].min(), q[:, 1].max(), q[:, 2].max())


def test_vote_labels_the_object_and_outvotes_a_bad_mask():
    m = _mesh()
    C, V = m.triangles_center, m.vertices
    comp = parts.Component("dishwasher", "dw1", {})
    for i, cam in enumerate(CAMS):
        mask = np.array(_rect_mask(cam)[0]) > 0
        comp.masks[cam["id"]] = ~mask if i == 0 else mask  # one view's mask is all wrong
    labels, _ = parts.vote(C, V, parts.welded_faces(V, m.faces), {c["id"]: c for c in CAMS},
                           [comp], W, H)  # fmt: skip
    front = np.isclose(C[:, 0], 0.02)
    assert (labels[front] == 0).mean() > 0.9
    assert (labels[~front] == 0).sum() < 0.02 * front.sum()


def test_cavity_box_comes_from_planes_and_edges():
    fr = _frame()
    b = parts.cavity_bounds(_structure(), fr, _ext(fr), {"o1"}, "pd", d_seen=0.02)
    size, sigma = parts.sizes(b)
    # sides: the outermost vertical cluster (e3 at z -0.33 beats the appliance's own edge e2)
    assert b["left"]["edges"] == ["e1"] and b["right"]["edges"] == ["e3"]
    assert b["top"]["edges"] == ["e4"]  # the long lip line; e5 is the appliance's own detail
    assert b["back"]["plane"] == "pw"  # the backsplash, not the bigger wall-cabinet plane
    assert b["front"]["plane"] == "pd" and b["cap"]["plane"] == "pt"
    assert size == pytest.approx({"w": 0.64, "h": 0.87, "d": 0.6}, abs=0.005)
    assert all(0 < s < 0.05 for s in sigma.values())


def test_gravity_snap_extends_the_level_floor_not_the_tilted_fit():
    fr = parts.component_frame(np.array([0.9, 0.2, 0.3]), AXES, np.array([0.0, 1.0, 0.0]))
    assert np.allclose(fr.d, [1, 0, 0]) and np.allclose(fr.up, [0, 1, 0])  # snapped to Manhattan
    b = parts.cavity_bounds(_structure(), fr, _ext(fr), {"o1"}, "pd", d_seen=0.02)
    assert b["floor"]["plane"] == "pf" and b["floor"]["value"] == pytest.approx(0.0, abs=1e-9)
    assert b["floor"]["tilt_deg"] == pytest.approx(4.0, abs=0.01)
    lo, hi = b["floor"]["snap_delta_m"]  # the tilted fit, extended back 1.1 m, drops ~7.7 cm
    assert lo == pytest.approx(np.tan(np.radians(4)) * -1.1, abs=1e-3) and hi < 0


def test_cut_protects_neighbours_and_holes_are_capped():
    b = parts.cavity_bounds(_structure(), _frame(), _ext(_frame()), {"o1"}, "pd", d_seen=0.02)
    rud = np.array([
        [0.0, 0.5, -0.3],  # inside, unclaimed: goes
        [0.0, 0.5, -0.2],  # inside, claimed by another component: stays
        [0.0, 0.86, -0.3],  # in the 2 cm under the top, unvoted: stays (a sagging neighbour)
        [0.0, 0.86, -0.2],  # same band but voted to this component: goes
        [0.5, 0.5, -0.3],  # outside the box: stays
    ])  # fmt: skip
    own = np.array([False, False, False, True, False])
    other = np.array([False, True, False, False, False])
    cut, st = parts.cut_faces(rud, b, 0.02, own, other, np.zeros(5, bool))
    assert cut.tolist() == [True, False, False, True, False]
    assert st["protected_claimed"] == 1 and st["protected_band"] == 1
    # top-down: a counter sag inside the opening that the cut removed is a hole; intact counter isn't
    counter = np.array([[r, 0.9, d] for r in np.arange(-0.305, 0.3, 0.01) for d in (-0.1, -0.2)])
    sag = np.array([[r, 0.83, d] for r in np.arange(-0.305, 0.3, 0.01) for d in (-0.5,)])
    holes = parts.top_holes(np.r_[counter, sag], counter, b, 0.93)
    assert holes.sum() == len(sag) and parts.top_holes(counter, counter, b, 0.93).sum() == 0


def test_neighbouring_cavities_share_a_side():
    fr = _frame()
    a = {"left": {"value": -0.3}, "right": {"value": 0.3, "edges": ["e9"]},
         "floor": {"value": 0.0}, "top": {"value": 0.87}}  # fmt: skip
    b = {"left": {"value": 0.28}, "right": {"value": 1.0}, "floor": {"value": 0.0},
         "top": {"value": 0.8}}  # fmt: skip
    parts.share_sides(b, fr, [("dw1", fr, a)])
    assert b["left"] == {"value": 0.3, "edges": ["e9"], "shared_with": "dw1"}


def test_run_writes_a_split_package_that_validates(tmp_path):
    sc, work = tmp_path / "scene", tmp_path / "work"
    (sc / "thumbs").mkdir(parents=True)
    (work / "masks").mkdir(parents=True)
    m = _mesh()
    m.export(sc / "mesh.r1.glb")
    m.export(sc / "collision.r1.glb")
    (sc / "structure.r1.json").write_text(json.dumps(_structure()))
    (sc / "cameras.r1.json").write_text(json.dumps(CAMS))
    dets = {}
    for cam in CAMS:
        Image.new("RGB", (W, H), (90, 90, 90)).save(sc / cam["thumb"])
        mask, box = _rect_mask(cam)
        mask.save(work / "masks" / f"{cam['id']}_dishwasher.png")
        dets[cam["id"]] = [{"label": "Dishwasher", "box": box}, {"label": "sink", "box": box}]
    scene = {"name": "t", "units": "meters", "up": [0, 1, 0], "revision": 1, "quality": "full",
             "scale_method": "altitude", "mesh": {"file": "mesh.r1.glb"},
             "collision": {"file": "collision.r1.glb"}, "cameras": "cameras.r1.json",
             "structure": {"file": "structure.r1.json"}}  # fmt: skip
    (sc / "scene.json").write_text(json.dumps(scene))
    before = hashlib.sha1((sc / "mesh.r1.glb").read_bytes()).hexdigest()

    doc = parts.run(sc, ["dishwasher"], work, n_frames=3, detections=dets)

    assert hashlib.sha1((sc / "mesh.r1.glb").read_bytes()).hexdigest() == before
    (c,) = doc["components"]
    assert doc["schema"] == "airtools.parts/1" and doc["frame"] == "scene"
    assert c["id"] == "dw1" and c["label_source"] == "vlm+s4" and c["structure_objects"] == ["o1"]
    assert c["node"] == "part_dw1" and c["cavity"]["node"] == "cavity_dw1"
    assert c["cavity"]["estimated"] is True and c["cavity"]["fill"] == "flat"
    assert c["cavity"]["size_m"] == pytest.approx({"w": 0.64, "h": 0.87, "d": 0.6}, abs=0.005)
    assert {"pd", "pf", "pw", "pt"} <= set(c["cavity"]["planes"])
    assert c["cut"]["holes_cm2"] > 0 and c["cut"]["cap_cm2"] > 0  # the sag was re-capped
    assert c["faces"] > 0 and c["evidence"]["purity_est"] > 0.95
    entry = json.loads((sc / "scene.json").read_text())["parts"]
    assert entry == {"file": "parts.r1.json", "schema": "airtools.parts/1", "components": 1,
                     "mesh": "mesh.parts.r1.glb", "cavities": "cavity.r1.glb",
                     "collision": "collision.parts.r1.glb"}  # fmt: skip
    split = trimesh.load(sc / "mesh.parts.r1.glb", force="scene")
    assert set(split.graph.nodes_geometry) == {"background", "part_dw1"}
    assert sum(len(g.faces) for g in split.geometry.values()) == len(m.faces)
    cav = trimesh.load(sc / "cavity.r1.glb", force="scene")
    assert cav.geometry["cavity_dw1"].metadata["estimated"] is True
    assert validate_package(sc) == []
