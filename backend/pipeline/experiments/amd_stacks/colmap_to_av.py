"""Our undistorted COLMAP model (PINHOLE, `sfm/dense`) -> AliceVision SfMData JSON, so Cheshire's
dense nodes (DepthMap -> Filter -> Meshing -> Texturing) run on our poses instead of AliceVision's SfM.

    python colmap_to_av.py SPARSE_DIR IMAGES_DIR OUT.sfm

AliceVision JSON conventions (checked against a convertSfMFormat dump of an AliceVision SfM,
reprojection < 1.5 px): `rotation` is world->camera R stored column-major, `center` is the camera
centre, `focalLength` is mm over `sensorWidth`, `principalPoint` is the offset from the image centre.
"""

import json
import sys
from pathlib import Path

import numpy as np
import pycolmap

SENSOR_W = 36.0


def s(xs):
    return [str(float(x)) for x in xs]  # repr(np.float64) would write "np.float64(..)"


def convert(sparse: Path, images: Path) -> dict:
    rec = pycolmap.Reconstruction(str(sparse))
    intr, views, poses, structure = [], [], [], []
    for cid, cam in rec.cameras.items():
        fx, fy, cx, cy = cam.params  # PINHOLE
        w, h = cam.width, cam.height
        intr.append(
            {
                "intrinsicId": str(cid),
                "width": str(w),
                "height": str(h),
                "sensorWidth": str(SENSOR_W),
                "sensorHeight": str(SENSOR_W * h / w),
                "serialNumber": "colmap",
                "type": "pinhole",
                "initializationMode": "calibrated",
                "initialFocalLength": "-1",
                "focalLength": str(float(fx * SENSOR_W / w)),
                "pixelRatio": str(float(fy / fx)),
                "pixelRatioLocked": "true",
                "offsetLocked": "true",
                "scaleLocked": "true",
                "principalPoint": s([cx - w / 2, cy - h / 2]),
                "distortionLocked": "true",
                "distortionInitializationMode": "none",
                "distortionParams": "",
                "undistortionOffset": ["0", "0"],
                "undistortionParams": "",
                "distortionType": "none",
                "undistortionType": "none",
                "locked": "true",
            }
        )
    for iid, img in rec.images.items():
        if not img.has_pose:
            continue
        pose = img.cam_from_world()
        pose = pose() if callable(pose) else pose
        R = np.asarray(pose.rotation.matrix())
        C = -R.T @ np.asarray(pose.translation)
        vid = str(iid)
        cam = rec.cameras[img.camera_id]
        views.append(
            {
                "viewId": vid,
                "poseId": vid,
                "frameId": str(int(Path(img.name).stem)),
                "intrinsicId": str(img.camera_id),
                "path": str(images / img.name),
                "width": str(cam.width),
                "height": str(cam.height),
                "metadata": {},
            }
        )
        poses.append(
            {
                "poseId": vid,
                "pose": {
                    "transform": {"rotation": s(R.flatten(order="F")), "center": s(C)},
                    "locked": "true",
                },
            }
        )
    for pid, p in rec.points3D.items():
        obs = []
        for el in p.track.elements:
            xy = rec.images[el.image_id].points2D[el.point2D_idx].xy
            obs.append(
                {
                    "observationId": str(el.image_id),
                    "featureId": str(el.point2D_idx),
                    "x": s(xy),
                    "scale": "0",
                }
            )
        structure.append(
            {
                "landmarkId": str(pid),
                "descType": "sift",
                "color": [str(int(c)) for c in p.color],
                "X": s(p.xyz),
                "observations": obs,
            }
        )
    return {
        "version": ["1", "2", "14"],
        "featuresFolders": [],
        "matchesFolders": [],
        "views": views,
        "intrinsics": intr,
        "poses": poses,
        "structure": structure,
    }


if __name__ == "__main__":
    sparse, images, out = map(Path, sys.argv[1:4])
    d = convert(sparse, images)
    out.write_text(json.dumps(d))
    print(f"{len(d['views'])} views, {len(d['structure'])} landmarks -> {out}")
