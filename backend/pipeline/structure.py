"""Structure layer for the full stage: straight edges, corners, planes and in-plane rectangles as
`airtools.structure/1` (docs/research/structure/schema.md), published as `structure.r<rev>.json`
in the scene frame of `mesh.r<rev>.glb` (docs/research/structure/s2-lines.md, "Pipeline").

Background part (`StructureJob.start`, called once DensifyPointCloud has written its depth maps, so
it overlaps the hybrid fill, ReconstructMesh and TextureMesh):
  - LIMAP LSD lines on the undistorted model, no holdout (S2, CPU, `AIRTOOLS_LIMAP_ENV`);
  - PxwPlanar plane segments per frame (S3, GPU, `AIRTOOLS_PXW_ENV`), lifted to 3D on the OpenMVS
    depth maps with S3's v6 parameters;
  - lines and planes fused (S2), all in raw COLMAP units.
Foreground part (`StructureJob.finish`, once mesh.r<rev>.glb and cameras.r<rev>.json are written):
  - `to_scene(cameras_matrix)`, then `drop_off_mesh` (what floats off the published mesh), then
    S4's rectangles on the remaining planes, merged into the edges.

The metric tolerances scale with the scene's ground sample distance (`tol_scale`): 1 indoors, so
the kitchen keeps its constants, 14-90 on a building (docs/research/p1-buildings-bench.md §7).

The layer is optional. A missing env skips it with a warning. A failed step falls back to what
exists (lines only, planes only, no rects), and nothing here raises into the mesh publish.
"""

from __future__ import annotations

import atexit
import json
import logging
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np

from pipeline.package import write_json_atomic

logger = logging.getLogger(__name__)

_REPO = Path(__file__).resolve().parents[1]
_TIMEOUT_S = 1800  # per subprocess; LIMAP takes ~3 min on 4 cores for 175 frames
_S4_TIMEOUT_S = 300  # S4 takes 10-40 s; it is the last step before the publish
_WAIT_S = 600  # how long the publish waits for the background part once the mesh is ready
# After that wait, how long the killed background part gets to fall back to the layer that did
# finish: LIMAP on a 223-frame close pass (gt-lcc) ran 13 minutes while the planes were long done.
_GRACE_S = 30
_LIVE: set[subprocess.Popen] = set()  # running steps; each leads its own process group
_ABORT = threading.Event()  # set once the publish stopped waiting: start no further step
_LIMAP_CONFIG = "cfgs/structure_triangulation/default_cpu.yaml"  # LSD, under $AIRTOOLS_LIMAP_ENV
# Keep the longest 600 LSD lines per image. The CPU config pairs every line with every line in 20
# neighbours, so cost grows with lines squared: the kitchen averages ~380 lines per frame (LIMAP
# 182 s), a brick facade ~2150, and its triangulation never finished in 30 min; capped: 388 s.
LIMAP_ARGS = ("--_image_description.line_detection._detector_options.base_options.max_num_2d_segs",
              "600")  # fmt: skip
# S3's PxwPlanar v6 (bench row 3b): edges at full support plus 2 cm, gravity-fixed Manhattan 3 deg
PXW_LIFT_ARGS = ("--edge-extend", "0.02", "--edge-pct", "0", "--manhattan", "3")


def structure_filename(revision: int) -> str:
    return f"structure.r{revision}.json"


# The layer's metric tolerances (merge 6 mm, corner 1 cm, RANSAC 1 cm, 2 cm cells...) were tuned on
# the kitchen, whose frames resolve ~0.9 mm per pixel. A drone over a building resolves 1-20 cm, so
# every tolerance is multiplied by gsd / GSD_REF_M, floored at 1: the kitchen keeps its constants.
GSD_REF_M = 0.002


def scene_gsd(sparse_dir: Path, m_per_unit: float) -> float:
    """Ground sample distance in metres per pixel: median over the registered images of their
    points' median depth divided by the focal length."""
    import pycolmap

    rec = pycolmap.Reconstruction(str(sparse_dir))
    xyz = {i: p.xyz for i, p in rec.points3D.items()}
    gsd = []
    for im in rec.images.values():
        pts = [xyz[p.point3D_id] for p in im.points2D if p.has_point3D()]
        if not pts:
            continue
        t = im.cam_from_world().matrix()
        z = np.asarray(pts) @ t[2, :3] + t[2, 3]
        gsd.append(np.median(z) * m_per_unit / rec.cameras[im.camera_id].focal_length_x)
    return float(np.median(gsd))


# structure farther from the published mesh than max(MESH_MAX_M, MESH_MAX_PX x gsd) is dropped
MESH_MAX_M, MESH_MAX_PX = 0.05, 5.0
EXTERIOR_SCALE = 5.0  # tol_scale from which a scene counts as a building (1 cm per pixel)


def tol_scale(gsd_m: float) -> float:
    return max(1.0, gsd_m / GSD_REF_M)


def metric_planes(planes_doc: dict, m_per_unit: float) -> None:
    """In place: the lift reports `rms_m` and `area_m2` in MoGe's metres (its `units_per_m`);
    restate them in the run's own metres, the scene frame's."""
    k = planes_doc.get("params", {}).get("units_per_m", 1 / m_per_unit) * m_per_unit
    for p in planes_doc["planes"]:
        for key, power in (("rms_m", 1), ("area_m2", 2)):
            if p.get(key) is not None:
                p[key] = p[key] * k**power


def s4_args(scale: float) -> list:
    """S4's metric options for a scene at `scale` (tol_scale): none indoors; on a building the
    mesh and crease tolerances grow with it and rectangles must be window or door sized (a
    drawer-sized rectangle on a facade is brickwork)."""
    if scale < EXTERIOR_SCALE:
        return []
    out = ["--exterior", "--min-side", 0.4, "--max-side", 4.0]
    for k, v in (("--margin", 0.05), ("--mesh-tol", 0.02), ("--crease-dist", 0.02),
                 ("--crease-inset", 0.01), ("--relief-min", 0.008), ("--relief-max", 0.06),
                 ("--relief-tol", 0.005), ("--relief-size", 0.15)):  # fmt: skip
        out += [k, v * scale]
    return out


def plane_samples(planes: list[dict], n: int = 12) -> list[np.ndarray | None]:
    """Per plane, the points of an n x n grid over its polygon's bounding box that fall inside the
    polygon (3D), or None for a plane without a polygon."""
    from pipeline.experiments.structure.score import Structure, point_in_polygon

    s = Structure({"planes": planes})
    out = []
    for o, u, v, poly in zip(s.origin, s.u, s.v, s.poly):
        if poly is None:
            out.append(None)
            continue
        g = np.stack(np.meshgrid(*np.linspace(poly.min(0), poly.max(0), n + 2)[1:-1].T), -1)
        g = g.reshape(-1, 2)
        g = g[point_in_polygon(g, poly)]
        g = g if len(g) else poly.mean(0, keepdims=True)
        out.append(o + g[:, :1] * u + g[:, 1:] * v)
    return out


def drop_off_mesh(doc: dict, mesh_path: Path, max_m: float) -> dict:
    """Drop edges whose median distance to the mesh (over 5 points along them) exceeds `max_m`,
    corners farther than that, and planes whose polygon interior (`plane_samples`) is mostly
    farther; the rest forget dropped edges and planes. Scene frame, metres. On a
    building a third of the LIMAP lines float in the air around it (narrow-baseline
    triangulations, trees, the sky line) and read as noise to snap to."""
    import trimesh

    from pipeline.experiments.structure.score import MeshQuery

    q = MeshQuery(trimesh.load(mesh_path, force="mesh"))
    out = dict(doc)
    if doc["edges"]:
        t = np.linspace(0, 1, 5)[None, :, None]
        a, b = (np.array([e[k] for e in doc["edges"]], float)[:, None] for k in "ab")
        d = q.closest((a + t * (b - a)).reshape(-1, 3))[1].reshape(-1, 5)
        out["edges"] = [e for e, x in zip(doc["edges"], np.median(d, 1)) if x <= max_m]
    kept = {e["id"] for e in out["edges"]}
    if doc["corners"]:
        d = q.closest(np.array([c["p"] for c in doc["corners"]], float))[1]
        out["corners"] = [
            dict(c, edges=[k for k in c.get("edges", []) if k in kept])
            for c, x in zip(doc["corners"], d)
            if x <= max_m
        ]
    samples = plane_samples(doc.get("planes", []))
    out["planes"] = [
        p for p, x in zip(doc.get("planes", []), samples)
        if x is None or np.median(q.closest(x)[1]) <= max_m
    ]  # fmt: skip
    ids = {p["id"] for p in out["planes"]}
    for key in ("edges", "corners"):
        out[key] = [dict(x, planes=[k for k in x.get("planes", []) if k in ids]) for x in out[key]]
    out["counts"] = dict(
        doc.get("counts", {}),
        off_mesh_edges=len(doc["edges"]) - len(out["edges"]),
        off_mesh_corners=len(doc["corners"]) - len(out["corners"]),
        off_mesh_planes=len(doc.get("planes", [])) - len(out["planes"]),
    )
    out["params"] = dict(doc.get("params") or {}, mesh_max_m=max_m)
    return out


def env_pythons() -> tuple[Path | None, Path | None]:
    """(LIMAP python, PxwPlanar python) from AIRTOOLS_LIMAP_ENV / AIRTOOLS_PXW_ENV (venv dirs),
    None where unset or missing. The LIMAP venv must also hold LIMAP's `cfgs/` (copied from its git
    checkout: the CLI reads its configs relative to the working directory)."""

    def py(var: str, needs: str = "") -> Path | None:
        env = os.environ.get(var)
        ok = env and (Path(env) / "bin" / "python").exists() and (Path(env) / needs).exists()
        if env and not ok:
            logger.warning("structure: %s=%s has no bin/python or %s", var, env, needs or "-")
        return Path(env) / "bin" / "python" if ok else None

    return py("AIRTOOLS_LIMAP_ENV", _LIMAP_CONFIG), py("AIRTOOLS_PXW_ENV")


def _kill_group(p: subprocess.Popen) -> None:
    """SIGKILL the step's whole process group: a pool's workers too, not just its parent."""
    try:
        os.killpg(p.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass
    p.wait()


# steps run in their own sessions, so a dying pipeline would not take them down itself
atexit.register(lambda: [_kill_group(p) for p in list(_LIVE)])


def _run(cmd: list, log: Path, timeout: float = _TIMEOUT_S, **env) -> None:
    """Run one step in its own session with a wall-clock timeout; on timeout (or any exit) its
    whole process group is killed, so no orphaned worker outlives it."""
    if _ABORT.is_set():
        raise RuntimeError("aborted, the package was published without the structure layer")
    log.parent.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PYTHONPATH=str(_REPO), **env)
    with open(log, "w") as f:
        p = subprocess.Popen(
            [str(c) for c in cmd],
            cwd=_REPO,
            env=env,
            stdout=f,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
    _LIVE.add(p)
    try:
        rc = p.wait(timeout)
    except subprocess.TimeoutExpired:
        raise RuntimeError(f"timed out after {timeout:.0f} s, killed, see {log}") from None
    finally:
        _kill_group(p)
        _LIVE.discard(p)
    if rc:
        raise RuntimeError(f"exit {rc}, see {log}")


class StructureJob:
    """One full-stage structure run. `start` is idempotent and never raises; `finish` joins it."""

    def __init__(
        self,
        dense_dir: Path,
        mvs_dir: Path,
        work: Path,
        *,
        m_per_unit: float,
        up_cameras: Path,
        force: bool = False,
    ):
        self.dense_dir, self.mvs_dir, self.work = Path(dense_dir), Path(mvs_dir), Path(work)
        self.m_per_unit, self.up_cameras, self.force = m_per_unit, Path(up_cameras), force
        self.limap_py, self.pxw_py = env_pythons()
        self.report: dict = {"ran": [], "failed": {}, "timings_s": {}}
        self.doc: dict | None = None  # fused (or fallback) doc, raw COLMAP frame
        self._thread: threading.Thread | None = None
        self._t0 = 0.0
        self.gsd, self.scale = 0.0, 1.0  # scene_gsd and its tol_scale, set by _set_scale

    def _set_scale(self) -> None:
        try:
            gsd = scene_gsd(self.dense_dir / "sparse", self.m_per_unit)
        except Exception as e:  # noqa: BLE001 -- keep the kitchen constants
            logger.warning("structure: no ground sample distance (%s), tolerances unscaled", e)
            return
        self.gsd, self.scale = gsd, tol_scale(gsd)
        self.report.update(gsd_m=gsd, tol_scale=self.scale)

    def _step(self, name: str, out: Path, fn) -> Path | None:
        """Run `fn` unless `out` exists (resume); record it. Returns `out`, or None on failure."""
        t0 = time.monotonic()
        try:
            if self.force or not out.exists():
                fn()
            self.report["ran"].append(name)
            return out
        except Exception as e:  # noqa: BLE001 -- a structure step must never fail the run
            msg = str(e).splitlines()[0] if str(e) else type(e).__name__
            logger.warning("structure: %s failed (%s), continuing without it", name, msg)
            self.report["failed"][name] = msg
            return None
        finally:
            self.report["timings_s"][f"{name}_s"] = time.monotonic() - t0

    def _lines(self) -> Path | None:
        out = self.work / "lines"
        cmd = [self.limap_py, "-m", "pipeline.experiments.lines.limap_run"]
        cmd += ["--sfm", self.dense_dir.parent, "--out", out, "--holdout-mod", "0"]
        cmd += ["--limap-src", self.limap_py.parents[1], "--config", _LIMAP_CONFIG]
        cmd += ["--m-per-unit", self.m_per_unit, *LIMAP_ARGS]
        return self._step("limap", out / "structure.json", lambda: _run(cmd, out / "limap.log"))

    def _planes(self) -> Path | None:
        cache, out = self.work / "pxw_cache", self.work / "planes.json"
        if out.exists() and not self.force:  # resume: the cache is deleted after the lift
            return self._step("pxw_lift", out, None)
        script = _REPO / "pipeline" / "experiments" / "planes"
        cmd = [self.pxw_py, script / "pxw_infer.py", "--dense", self.dense_dir, "--out", cache]
        done = cache / "done.txt"

        def infer():
            _run(cmd, self.work / "pxw_infer.log")
            done.write_text("ok")

        if self._step("pxw_infer", done, infer) is None:
            return None
        cmd = [self.pxw_py, script / "pxw_lift.py", "--dense", self.dense_dir, "--cache", cache]
        cmd += ["--out", out, *PXW_LIFT_ARGS, "--up-from", self.up_cameras]
        dmaps = any(self.mvs_dir.glob("depth*.dmap"))
        if dmaps:
            cmd += ["--mvs", self.mvs_dir]
        self.report["planes_on_mvs_depth"] = dmaps
        if self._step("pxw_lift", out, lambda: _run(cmd, self.work / "pxw_lift.log")) is None:
            return None
        shutil.rmtree(cache, ignore_errors=True)  # ~2.4 MB per frame, only the lift reads it
        return out

    def _background(self) -> None:
        self._set_scale()
        with ThreadPoolExecutor(2) as ex:
            lines = ex.submit(self._lines) if self.limap_py else None
            planes = ex.submit(self._planes) if self.pxw_py else None
            lines = lines.result() if lines else None
            planes = planes.result() if planes else None
        lines_doc = json.loads(lines.read_text()) if lines else None
        planes_doc = json.loads(planes.read_text()) if planes else None
        if planes_doc:
            metric_planes(planes_doc, self.m_per_unit)
        if lines_doc and planes_doc:
            from pipeline.experiments.lines.fuse import fuse

            out = self.work / "fused.json"

            def run_fuse():
                doc = fuse(lines_doc, planes_doc, self.m_per_unit, tol_scale=self.scale)
                out.write_text(json.dumps(doc))

            if self._step("fuse", out, run_fuse):
                self.doc = json.loads(out.read_text())
                return
        self.doc = lines_doc or planes_doc  # fallback: one layer on its own

    def start(self) -> None:
        if self._thread is not None:
            return
        self.work.mkdir(parents=True, exist_ok=True)
        self._t0 = time.monotonic()
        _ABORT.clear()

        def target():
            try:
                self._background()
            except Exception as e:  # noqa: BLE001
                logger.warning("structure: background part failed: %s", e)
                self.report["failed"]["background"] = str(e)

        self._thread = threading.Thread(target=target, daemon=True)
        self._thread.start()

    def finish(
        self,
        cameras_matrix: np.ndarray,
        out_dir: Path,
        revision: int,
        *,
        mesh_file: str,
        cameras_file: str,
        frames_dir: Path,
        sparse_raw: Path,
    ) -> dict | None:
        """Join the background part, move it to the scene frame, add S4 rects, write
        structure.r<rev>.json. Returns the scene.json `structure` entry, or None if nothing was
        produced. Never raises."""
        try:
            return self._finish(
                cameras_matrix, Path(out_dir), revision, mesh_file, cameras_file,
                Path(frames_dir), Path(sparse_raw),
            )  # fmt: skip
        except Exception as e:  # noqa: BLE001
            logger.warning("structure: finish failed: %s", e)
            self.report["failed"]["finish"] = str(e)
            return None

    def _finish(self, m, out_dir, revision, mesh_file, cameras_file, frames_dir, sparse_raw):
        from pipeline.experiments.lines.frame import to_scene
        from pipeline.experiments.lines.rects import merge_rects

        self.start()  # no-op if the densify hook already started it
        t0 = time.monotonic()
        self._thread.join(_WAIT_S)
        self.report["timings_s"]["wait_s"] = time.monotonic() - t0  # time added after the mesh
        if self._thread.is_alive():  # kill what is still running, keep a layer that finished
            logger.warning("structure: not done %d s after the mesh, killing the rest", _WAIT_S)
            self.report["failed"]["wait"] = f"background part not done after {_WAIT_S} s"
            _ABORT.set()
            for p in list(_LIVE):
                _kill_group(p)
            self._thread.join(_GRACE_S)  # it falls back to the finished layer in milliseconds
            if self._thread.is_alive():
                logger.warning("structure: publishing without it")
                return None
            _ABORT.clear()  # S4 below is a step of its own
        if not self.doc:
            return None
        doc = to_scene(self.doc, m)
        max_m = max(MESH_MAX_M, MESH_MAX_PX * self.gsd)
        try:
            doc = drop_off_mesh(doc, out_dir / mesh_file, max_m)
            self.report["off_mesh"] = {k: v for k, v in doc["counts"].items() if "off_mesh" in k}
        except Exception as e:  # noqa: BLE001 -- publish unfiltered rather than nothing
            logger.warning("structure: mesh-distance filter failed (%s), publishing unfiltered", e)
            self.report["failed"]["mesh_filter"] = str(e).splitlines()[0] if str(e) else "error"
        # S4 fits rectangles on the planes that survived the mesh filter, in the scene frame, and
        # keeps them off the lift's plane-plane creases
        planes, lifted = self.work / "planes_scene.json", self.work / "planes.json"
        if doc.get("planes") and lifted.exists():
            creases = json.loads(lifted.read_text())["edges"]
            creases = to_scene({"edges": creases, "corners": []}, m)["edges"]
            planes.write_text(
                json.dumps({"frame": "scene", "planes": doc["planes"], "edges": creases})
            )
        else:
            planes.unlink(missing_ok=True)
        s4 = self.work / "s4" / "structure.s4.json"
        cmd = [sys.executable, "-m", "pipeline.experiments.objects.run", "--planes", planes]
        cmd += ["--scene", out_dir, "--mesh", mesh_file]
        cmd += ["--cameras", out_dir / cameras_file, "--frames", frames_dir]
        cmd += ["--sparse-raw", sparse_raw, "--out", s4.parent]
        # all frames; 8 workers at most, each holds its frames and the mesh (~1 GB on a building)
        cmd += ["--workers", min(8, len(os.sched_getaffinity(0))), "--hold-every", 0]
        cmd += s4_args(self.scale)
        # S4 runs a pool per plane. A *fork* pool deadlocked on hfbox (the children blocked on
        # a futex held by OpenCV's thread pool), so objects/run.py spawns its workers; keep it
        # that way. One OpenCV thread per process and the short timeout stay as guards.
        s4_log = self.work / "s4.log"
        s4_run = lambda: _run(cmd, s4_log, _S4_TIMEOUT_S, OPENCV_FOR_THREADS_NUM="1")
        if planes.exists() and self._step("s4_rects", s4, s4_run):
            doc = merge_rects(doc, json.loads(s4.read_text()), tol_scale=self.scale)
        name = structure_filename(revision)
        write_json_atomic(out_dir / name, json.dumps(doc))
        self.report["timings_s"]["total_s"] = time.monotonic() - self._t0
        counts = {k: len(doc.get(k, [])) for k in ("edges", "corners", "planes", "objects")}
        self.report.update(file=name, method=doc.get("method"), counts=counts)
        return {"file": name, "schema": doc.get("schema", "airtools.structure/1"), **counts}


def prepare(
    enabled: bool,
    dense_dir: Path,
    mvs_dir: Path,
    work: Path,
    *,
    m_per_unit: float | None,
    up_cameras: Path,
    force: bool = False,
) -> tuple[StructureJob | None, dict]:
    """(job, report). The job is None, with the reason in the report, when the layer is skipped."""
    limap_py, pxw_py = env_pythons()
    reason = None
    if not enabled:
        reason = "--structure off"
    elif limap_py is None and pxw_py is None:
        reason = "neither AIRTOOLS_LIMAP_ENV nor AIRTOOLS_PXW_ENV is set to an existing venv"
    elif m_per_unit is None:
        reason = "no metric scale (the structure tolerances are in metres)"
    if reason:
        if enabled:
            logger.warning("structure: skipped, %s", reason)
        return None, {"skipped": reason}
    for var, py in (("AIRTOOLS_LIMAP_ENV", limap_py), ("AIRTOOLS_PXW_ENV", pxw_py)):
        if py is None:
            logger.warning("structure: %s is not set, running without that layer", var)
    job = StructureJob(
        dense_dir, mvs_dir, work, m_per_unit=m_per_unit, up_cameras=up_cameras, force=force
    )
    return job, job.report
