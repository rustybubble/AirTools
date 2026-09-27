"""Novel-view coverage for bench candidates (E3; r4-online-tools.md's "back 0.6 m / 0.9 m sideways"
views): each candidate is Sim(3)-aligned exactly as `harness score` does, then rendered from every
reference evaluation camera moved 0.6 m backwards and 0.9 m to the right -- poses no input frame
had -- and the covered-pixel fraction is averaged. Coverage only: there is no photo to compare.

    python -m pipeline.experiments.bench.novel_coverage --ref /workspace/bench/ref SCENE_DIR...
"""

import argparse
import json
from pathlib import Path

import numpy as np
import trimesh

from pipeline.calibrate import interpolate_positions_at_times, similarity_matrix
from pipeline.experiments.bench.harness import id_times, robust_sim3
from pipeline.package import Camera
from pipeline.preview import render
from pipeline.qa import _load_cameras, _resolve_package_files

OFFSETS = {"back_0.6m": (0.0, -0.6), "right_0.9m": (0.9, 0.0)}  # (along camera +x, along +z)


def moved(e: dict, right_m: float, fwd_m: float) -> Camera:
    R = np.array(e["R"])
    c = -R.T @ np.array(e["t_wc"]) + right_m * R[0] + fwd_m * R[2]
    return Camera(id=e["id"], file="", thumb="", R=R, t=-R @ c, fx=e["fx"], fy=e["fy"],
                  cx=e["cx"], cy=e["cy"], w=e["w"], h=e["h"])  # fmt: skip


def novel_coverage(scene: Path, ref_dir: Path, id_fps: float = 10.0, every: int = 2) -> dict:
    ref = json.loads((ref_dir / "reference.json").read_text())
    tr_t, tr_p = np.array(ref["track"]["times"]), np.array(ref["track"]["positions"])
    cams = _load_cameras(scene)
    ids = sorted(cams)
    times = id_times(ids, None, id_fps)
    C = np.array([cams[i].position for i in ids])
    m = (times >= tr_t.min()) & (times <= tr_t.max())
    s, R, t, _, _ = robust_sim3(C[m], interpolate_positions_at_times(tr_t, tr_p, times[m]))
    mesh = trimesh.load(scene / _resolve_package_files(scene)[0], force="scene", process=False)
    mesh = mesh.to_geometry()
    mesh.apply_transform(similarity_matrix(s, R, t))
    out = {}
    for name, (right, fwd) in OFFSETS.items():
        cov = [render(mesh, moved(e, right, fwd), 480, 270)[1].mean() for e in ref["eval"][::every]]
        out[name] = float(np.mean(cov))
    return out


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("scenes", nargs="+", type=Path)
    p.add_argument("--ref", type=Path, required=True)
    a = p.parse_args()
    for scene in a.scenes:
        print(json.dumps({"scene": str(scene), **novel_coverage(scene, a.ref)}), flush=True)


if __name__ == "__main__":
    main()
