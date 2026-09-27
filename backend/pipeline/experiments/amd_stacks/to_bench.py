"""Package a third-party reconstruction for the bench harness: COLMAP model + mesh -> mesh.glb + cameras.json.

    python -m pipeline.experiments.amd_stacks.to_bench SPARSE_DIR|AV_SFM.json MESH OUT_DIR [--transform T.json]

Camera ids are image stems (our `%04d` 10 fps-grid names), so `bench score --id-fps 10` times them.
`--transform` takes a 4x4 (key "transform" or a bare list) applied to the mesh to bring it back
into the COLMAP frame, for tools that recentre before meshing.
"""

import argparse
import json
from pathlib import Path

import numpy as np
import pycolmap
import trimesh

from pipeline.package import Camera, write_cameras_json


def cameras_from_colmap(sparse: Path) -> list[Camera]:
    rec = pycolmap.Reconstruction(str(sparse))
    out = []
    for img in rec.images.values():
        if not img.has_pose:
            continue
        cam = rec.cameras[img.camera_id]
        K = cam.calibration_matrix()
        pose = img.cam_from_world()
        pose = pose() if callable(pose) else pose  # pycolmap >=3.12 exposes a method
        out.append(
            Camera(
                id=Path(img.name).stem,
                file=img.name,
                thumb="",
                R=np.asarray(pose.rotation.matrix()),
                t=np.asarray(pose.translation),
                fx=float(K[0, 0]),
                fy=float(K[1, 1]),
                cx=float(K[0, 2]),
                cy=float(K[1, 2]),
                w=int(cam.width),
                h=int(cam.height),
            )
        )
    return sorted(out, key=lambda c: c.id)


def cameras_from_alicevision(sfm_json: Path) -> list[Camera]:
    """AliceVision SfMData JSON (`aliceVision_convertSfMFormat -o x.json`); conventions as in
    colmap_to_av.py. Distortion is dropped: the harness only uses centres and ids."""
    d = json.loads(Path(sfm_json).read_text())
    f = lambda xs: np.array([float(x) for x in xs])
    poses = {p["poseId"]: p["pose"]["transform"] for p in d["poses"]}
    intr = {i["intrinsicId"]: i for i in d["intrinsics"]}
    out = []
    for v in d["views"]:
        if v["poseId"] not in poses:
            continue
        T, K = poses[v["poseId"]], intr[v["intrinsicId"]]
        R = f(T["rotation"]).reshape(3, 3).T  # stored column-major
        w, h = int(K["width"]), int(K["height"])
        fpx = float(K["focalLength"]) * w / float(K["sensorWidth"])
        pp = f(K["principalPoint"]) + [w / 2, h / 2]
        out.append(
            Camera(
                id=Path(v["path"]).stem,
                file=Path(v["path"]).name,
                thumb="",
                R=R,
                t=-R @ f(T["center"]),
                fx=fpx,
                fy=fpx * float(K.get("pixelRatio", 1)),
                cx=pp[0],
                cy=pp[1],
                w=w,
                h=h,
            )
        )
    return sorted(out, key=lambda c: c.id)


def single_atlas(scene: trimesh.Scene) -> trimesh.Trimesh:
    """Merge a multi-material mesh (AliceVision writes one 4096 atlas per material) into one mesh
    with one texture: atlases tiled into a grid, UVs remapped. trimesh's own `to_geometry()` merge
    scrambles the UVs of multi-atlas meshes, which the harness renders as noise."""
    geoms = [g for g in scene.dump() if len(g.faces)]
    if len(geoms) == 1:
        return geoms[0]
    from PIL import Image

    imgs = [
        getattr(g.visual.material, "image", None) or g.visual.material.baseColorTexture
        for g in geoms
    ]
    S = max(max(im.size) for im in imgs)
    cols = int(np.ceil(np.sqrt(len(geoms))))
    rows = int(np.ceil(len(geoms) / cols))
    atlas = Image.new("RGB", (cols * S, rows * S))
    parts = []
    for k, (g, im) in enumerate(zip(geoms, imgs)):
        r, c = divmod(k, cols)
        atlas.paste(im.convert("RGB").resize((S, S)), (c * S, r * S))
        uv = np.asarray(g.visual.uv, dtype=np.float64)
        uv = np.c_[
            (c + uv[:, 0]) / cols, (rows - 1 - r + uv[:, 1]) / rows
        ]  # v=0 is the image bottom
        parts.append(
            trimesh.Trimesh(
                g.vertices, g.faces, process=False, visual=trimesh.visual.TextureVisuals(uv=uv)
            )
        )
    merged = trimesh.util.concatenate(parts)
    merged.visual = trimesh.visual.TextureVisuals(uv=merged.visual.uv, image=atlas)
    return merged


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("sparse")
    p.add_argument("mesh")
    p.add_argument("out")
    p.add_argument("--transform", default=None)
    a = p.parse_args(argv)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    src = Path(a.sparse)
    cams = cameras_from_alicevision(src) if src.suffix == ".json" else cameras_from_colmap(src)
    write_cameras_json(cams, out / "cameras.json")
    scene = trimesh.load(a.mesh, force="scene", process=False)
    if a.transform:
        T = json.loads(Path(a.transform).read_text())
        scene.apply_transform(np.array(T.get("transform", T) if isinstance(T, dict) else T))
    mesh = single_atlas(scene)
    mesh.export(out / "mesh.glb")
    print(f"{len(cams)} cameras, {len(mesh.faces)} faces -> {out}")


if __name__ == "__main__":
    main()
