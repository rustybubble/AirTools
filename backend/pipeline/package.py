"""Write the scene package (plan §1, §1a, §2.4): `scene.json`, revision-named `mesh.r<rev>.glb` /
`collision.r<rev>.glb` / `cameras.r<rev>.json`, `thumbs/`.

A scene package directory looks like:
    scene/<site>/
        scene.json               # metadata, written LAST (publish_revision) -- the only file the
                                  # app polls; points at this revision's files below
        mesh.r<rev>.glb           # textured surface mesh (written by the reconstruct step, not
                                  # here) -- the visual
        collision.r<rev>.glb      # lighter, untextured -- for tool snapping (ditto)
        cameras.r<rev>.json       # per-frame pose -- write_cameras_json
        thumbs/NNNN.jpg           # 640px evidence photos -- make_thumbs, shared across revisions
        path.json                 # optional GPS track (written by the reconstruct step, not here)

This module only writes the tool-independent metadata (`scene.json`, `cameras.r<rev>.json`,
`thumbs/`) and validates a finished package; `mesh.r<rev>.glb`/`collision.r<rev>.glb`/`path.json`
come from whichever pose + meshing stage published this revision (`pipeline.fast`/`pipeline.run`).
"""

import json
import re
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import trimesh
from PIL import Image

from pipeline import PIPELINE_VERSION
from pipeline.calibrate import ScaleEstimate


def write_json_atomic(path: Path, text: str) -> None:
    """Temp file + rename, so a reader never sees half a file. Local copy of server.cache's helper:
    the pipeline runs on the GPU box without the parts server's settings/deps."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f".{path.name}.tmp")
    tmp.write_text(text)
    tmp.replace(path)


_MESH_DIAGONAL_RANGE_M = (2.0, 500.0)  # a facade-and-a-bit; catches a units bug (mm vs m) early


@dataclass
class Camera:
    """One camera pose + intrinsics, metres, after calibration.

    `R`/`t` are the world-to-camera pose: `x_cam = R @ x_world + t` (OpenCV convention).
    """

    id: str
    file: str
    thumb: str
    R: np.ndarray
    t: np.ndarray
    fx: float
    fy: float
    cx: float
    cy: float
    w: int
    h: int

    @property
    def position(self) -> np.ndarray:
        """Camera centre in world coordinates: `-R^T @ t`."""
        return -np.asarray(self.R).T @ np.asarray(self.t)


def write_cameras_json(cameras: list[Camera], path: Path) -> None:
    """Write `cameras.r<rev>.json`: one entry per `Camera`, `position` derived from `R`/`t`."""
    payload = [
        {
            "id": c.id,
            "file": c.file,
            "thumb": c.thumb,
            "R": c.R.tolist(),
            "t": c.t.tolist(),
            "position": c.position.tolist(),
            "fx": c.fx,
            "fy": c.fy,
            "cx": c.cx,
            "cy": c.cy,
            "w": c.w,
            "h": c.h,
        }
        for c in cameras
    ]
    write_json_atomic(path, json.dumps(payload, indent=2))


def make_thumbs(frame_paths: list[Path], out_dir: Path, long_edge: int = 640) -> list[Path]:
    """Downscale each frame to `long_edge`px (longest side) JPEGs in `out_dir`, same filenames."""
    out_dir.mkdir(parents=True, exist_ok=True)
    out_paths = []
    for frame_path in frame_paths:
        out_path = out_dir / Path(frame_path).with_suffix(".jpg").name
        with Image.open(frame_path) as img:
            img = img.convert("RGB")
            img.thumbnail((long_edge, long_edge))
            img.save(out_path, "JPEG")
        out_paths.append(out_path)
    return out_paths


def recommended_spawn(mesh_bounds: np.ndarray, cameras: list[Camera]) -> dict:
    """Stand on the ground (lowest mesh point + 1.6 m eye height), a few metres out from the
    bounding box on the side the cameras mostly shot from, looking at the box's centre."""
    bmin, bmax = np.asarray(mesh_bounds, dtype=np.float64)
    center = (bmin + bmax) / 2.0

    if cameras:
        mean_cam_pos = np.mean([c.position for c in cameras], axis=0)
        direction = mean_cam_pos - center
        direction[1] = 0.0  # horizontal only, we set the height ourselves
    else:
        direction = np.zeros(3)
    norm = np.linalg.norm(direction)
    direction = (
        direction / norm if norm > 1e-6 else np.array([0.0, 0.0, 1.0])
    )  # deterministic default

    half_extent_horiz = max(bmax[0] - bmin[0], bmax[2] - bmin[2]) / 2.0
    pos = center + direction * (half_extent_horiz + 3.0)  # "a few metres in front of" the bbox edge
    pos[1] = bmin[1] + 1.6
    return {"pos": pos.tolist(), "look": center.tolist()}


# --- revision-named publishing (plan §1a) -------------------------------------------------------

_FRAME_ID_FILE = "frame_id.txt"


def load_or_create_frame_id(work_dir: Path, site: str) -> str:
    """The scene package's stable `frame.id` (plan §1a: "frame.id stays the same across
    revisions") -- cached once under `work_dir` (the top-level work root shared by both pipeline
    stages, each with their own subdirectory) so whichever stage runs next (preview after full,
    full after preview, or a resumed re-run of either) reuses the same id instead of minting a new
    one. Not derived from content, just a short random suffix on the site slug generated once."""
    path = Path(work_dir) / _FRAME_ID_FILE
    if path.exists():
        return path.read_text().strip()
    slug = re.sub(r"[^a-z0-9]+", "-", site.lower()).strip("-") or "site"
    frame_id = f"{slug}-{uuid.uuid4().hex[:4]}"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(frame_id)
    return frame_id


def revision_filenames(revision: int) -> dict[str, str]:
    """Revision-named package filenames (plan §1a): `mesh.r<rev>.glb`, `collision.r<rev>.glb`,
    `cameras.r<rev>.json`. `thumbs/` is shared across revisions, not revisioned."""
    return {
        "mesh": f"mesh.r{revision}.glb",
        "collision": f"collision.r{revision}.glb",
        "cameras": f"cameras.r{revision}.json",
    }


def _prune_revision_files(out_dir: Path, revision: int) -> None:
    """Delete a stale revision's mesh/collision/cameras files (plan §1a: "Old revision files are
    deleted one revision later") -- no-op for `revision < 1` (nothing was ever published there)."""
    if revision < 1:
        return
    for name in revision_filenames(revision).values():
        (Path(out_dir) / name).unlink(missing_ok=True)
    for name in ("structure.r{}.json", "parts.r{}.json", "mesh.parts.r{}.glb", "cavity.r{}.glb",
                 "collision.parts.r{}.glb"):  # fmt: skip
        (Path(out_dir) / name.format(revision)).unlink(missing_ok=True)


def publish_revision(
    out_dir: Path,
    *,
    revision: int,
    quality: str,  # "preview" | "full"
    name: str,
    scale: ScaleEstimate,
    gravity_residual_deg: float,
    frame_id: str,
    aligned_to_preview: bool,
    alignment_residual_m: float | None,
    origin_gps: list[float] | None = None,
    north: list[float] | None = None,
    spawn: dict | None = None,
    mesh_stats: dict[str, Any] | None = None,
    scale_candidates: list[ScaleEstimate] | None = None,
    structure: dict | None = None,
) -> None:
    """Publish one revision (plan §1a). The caller has already written this revision's
    `mesh.r<rev>.glb`/`collision.r<rev>.glb`/`cameras.r<rev>.json` (`revision_filenames`) under
    `out_dir`; this writes `scene.json` LAST via atomic rename (`write_json_atomic`: a reader
    either sees the old revision complete or the new one complete, never a torn read), then
    deletes revision `revision - 2`'s files (`thumbs/` is shared, never touched by pruning).

    `mesh_stats` (all optional, default 0): `{"triangles", "texture_px", "collision_triangles"}`.
    `scale_candidates`: every `ScaleEstimate` this stage had available (not just the winner, and
    for the full stage, the preview's winning estimate too) -- so a disagreement is visible in the
    package, not just in a log line.
    `structure`: the `structure.r<rev>.json` entry (`{"file", "schema", "edges", ...}`) when the
    full stage's structure layer wrote one (`pipeline/structure.py`), else None.
    """
    stats = mesh_stats or {}
    filenames = revision_filenames(revision)
    payload = {
        "name": name,
        "units": "meters",
        "up": [0, 1, 0],
        "north": north,
        "scale_method": scale.method,
        "scale_residual_m": scale.residual_m,
        "scale_notes": scale.notes,
        "scale_candidates": [
            {"method": c.method, "scale": c.scale, "residual_m": c.residual_m}
            for c in (scale_candidates or [])
        ],
        "gravity_residual_deg": gravity_residual_deg,
        "origin_gps": origin_gps,
        "recommended_spawn": spawn,
        "mesh": {
            "file": filenames["mesh"],
            "triangles": stats.get("triangles", 0),
            "texture_px": stats.get("texture_px", 0),
        },
        "collision": {
            "file": filenames["collision"],
            "triangles": stats.get("collision_triangles", 0),
        },
        "cameras": filenames["cameras"],
        "structure": structure,
        "quality": quality,
        "revision": revision,
        "frame": {
            "id": frame_id,
            "aligned_to_preview": aligned_to_preview,
            "alignment_residual_m": alignment_residual_m,
        },
        "pipeline_version": PIPELINE_VERSION,
    }
    write_json_atomic(Path(out_dir) / "scene.json", json.dumps(payload, indent=2))
    _prune_revision_files(out_dir, revision - 2)


def _mesh_has_texture(mesh: trimesh.Trimesh) -> bool:
    material = getattr(mesh.visual, "material", None)
    return getattr(material, "baseColorTexture", None) is not None


def _validate_structure(path: Path, mesh_path: Path) -> list[str]:
    """The structure layer is in the mesh's frame: scene-frame schema doc, finite points, and most
    corners inside the mesh bbox (+10% of its diagonal; a wrong transform puts them far outside)."""
    try:
        doc = json.loads(path.read_text())
    except (json.JSONDecodeError, OSError) as exc:
        return [f"{path.name} missing or unreadable: {exc}"]
    if doc.get("schema") != "airtools.structure/1" or doc.get("frame") != "scene":
        return [f"{path.name} is not an airtools.structure/1 doc in the scene frame"]
    pts = np.array(
        [c["p"] for c in doc.get("corners", [])]
        + [x for e in doc.get("edges", []) for x in (e["a"], e["b"])],
        float,
    ).reshape(-1, 3)
    if not np.isfinite(pts).all():
        return [f"{path.name} has non-finite coordinates"]
    try:
        lo, hi = trimesh.load(mesh_path, force="scene").bounds
    except Exception:  # noqa: BLE001 -- the mesh check above already reports this
        return []
    pad = 0.1 * np.linalg.norm(hi - lo)
    inside = np.all((pts >= lo - pad) & (pts <= hi + pad), axis=1)
    if len(pts) and inside.mean() < 0.5:
        return [f"{path.name}: only {inside.mean():.0%} of its points are near the mesh bbox"]
    return []


def _validate_parts(directory: Path, entry: dict) -> list[str]:
    """`parts.r<rev>.json` (pipeline/parts.py): an airtools.parts/1 doc whose GLBs exist and hold
    a node for every component (`part_<id>` in the split mesh, `cavity_<id>` in the cavities)."""
    try:
        doc = json.loads((directory / entry["file"]).read_text())
    except (json.JSONDecodeError, OSError, KeyError) as exc:
        return [f"parts file missing or unreadable: {exc}"]
    if doc.get("schema") != "airtools.parts/1" or doc.get("frame") != "scene":
        return [f"{entry['file']} is not an airtools.parts/1 doc in the scene frame"]
    problems = []
    for key in ("mesh", "cavities", "collision"):
        try:
            names = set(trimesh.load(directory / doc[key], force="scene").graph.nodes_geometry)
        except Exception as exc:  # noqa: BLE001 -- whatever the glTF parser raises
            problems.append(f"{doc.get(key)} failed to load: {exc}")
            continue
        for c in doc.get("components", []):
            node = c["cavity"]["node"] if key == "cavities" else c["node"]
            if c.get("faces") and node not in names:
                problems.append(f"{doc[key]} has no node {node!r}")
    return problems


def validate_package(directory: Path) -> list[str]:
    """Sanity-check a finished scene package: validates whatever the current `scene.json` actually
    points at (plan §1a's revision-named layout), not a fixed `mesh.glb`/`collision.glb`/
    `cameras.json`. Returns a list of human-readable problems (empty = passes). Checks file
    presence, mesh plausibility, and that the mesh file is actually textured -- the collision file
    is deliberately untextured (plan: "lighter, untextured, for tool snapping") so it's exempt from
    the texture check.
    """
    directory = Path(directory)
    scene_path = directory / "scene.json"
    if not scene_path.exists():
        return ["missing scene.json"]
    try:
        scene = json.loads(scene_path.read_text())
    except (json.JSONDecodeError, OSError) as exc:
        return [f"scene.json unreadable: {exc}"]

    if "revision" not in scene or "cameras" not in scene:
        message = (
            "scene.json has no 'revision'/'cameras' field -- this looks like an old flat package "
            "(mesh.glb/collision.glb/cameras.json from before pipeline_version 0.3.0) that this "
            "validator no longer supports; re-run the pipeline to get the revision-named layout "
            "(mesh.r<rev>.glb, collision.r<rev>.glb, cameras.r<rev>.json)"
        )
        return [message]

    mesh_file = scene.get("mesh", {}).get("file")
    collision_file = scene.get("collision", {}).get("file")
    cameras_file = scene.get("cameras")
    problems: list[str] = []
    for label, fname in (
        ("mesh", mesh_file),
        ("collision", collision_file),
        ("cameras", cameras_file),
    ):
        if not fname:
            problems.append(f"scene.json has no {label} file reference")
        elif not (directory / fname).exists():
            problems.append(f"missing {fname} (referenced by scene.json's {label!r})")
    if problems:  # nothing else below can run meaningfully without the referenced files
        return problems

    mesh_path = directory / mesh_file
    try:
        mesh = trimesh.load(mesh_path, force="scene").to_geometry()
        diag = float(np.linalg.norm(mesh.bounds[1] - mesh.bounds[0]))
        lo, hi = _MESH_DIAGONAL_RANGE_M
        if not (lo <= diag <= hi):
            problems.append(
                f"{mesh_file} bbox diagonal {diag:.2f} m outside plausible range {lo}-{hi} m"
            )
        if not _mesh_has_texture(mesh):
            problems.append(f"{mesh_file} has no texture/material image")
    except Exception as exc:  # noqa: BLE001 -- trimesh's loaders raise whatever the parser raises
        problems.append(f"{mesh_file} failed to load: {exc}")

    try:
        cameras = json.loads((directory / cameras_file).read_text())
        for cam in cameras:
            thumb = cam.get("thumb")
            if thumb and not (directory / thumb).exists():
                problems.append(f"{cameras_file} references missing thumb: {thumb}")
    except (json.JSONDecodeError, OSError) as exc:
        problems.append(f"{cameras_file} unreadable: {exc}")

    structure_file = (scene.get("structure") or {}).get("file")
    if structure_file:
        problems += _validate_structure(directory / structure_file, mesh_path)
    if scene.get("parts"):
        problems += _validate_parts(directory, scene["parts"])

    if scene.get("scale_method") == "none":
        problems.append(
            "scale_method is 'none' -- no metric scale reference was found, the mesh is NOT at "
            "real-world scale (1 unit != 1 m)"
        )

    return problems
