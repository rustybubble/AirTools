"""Structure-from-motion: pycolmap CPU SIFT + sequential matching + global/incremental mapping,
then undistort to PINHOLE for OpenMVS (research doc §1.1, §5 PRIMARY, §7 pitfall 1: `InterfaceCOLMAP`
only imports the PINHOLE camera model, so a drone lens's real distortion must be undistorted away
first via `pycolmap.undistort_images` between mapping and the OpenMVS import step).
"""

import json
import logging
import time
from dataclasses import dataclass
from pathlib import Path

import pycolmap

logger = logging.getLogger(__name__)

# Below this fraction of images registered, `global_mapping` (GLOMAP) is considered to have failed
# on this footage and we retry with `incremental_mapping` (assignment §3; research doc §1.1's
# documented GLOMAP-on-repetitive-facades failure mode).
_MIN_REGISTERED_FRACTION = 0.6

# Loop closure for a closed drone orbit: pycolmap's own `SequentialPairingOptions.loop_detection`
# needs a vocab tree file on disk (`vocab_tree_path`, no built-in download in this pip wheel --
# confirmed by inspecting the live option object, not assumed) so it's skipped. Cheap substitute:
# explicitly match the first `_LOOP_CLOSURE_K` frames against the last `_LOOP_CLOSURE_K` frames --
# for an orbit, those are the two ends of the sequential chain that are physically closest to each
# other (the "seam" where the camera comes back around), which a purely sequential/windowed matcher
# never pairs on its own.
_LOOP_CLOSURE_K = 10

_META_NAME = "sfm_meta.json"


@dataclass
class SfmResult:
    reconstruction_path: Path
    num_registered: int
    num_images: int
    mean_reprojection_error: float
    num_points3d: int
    mapper_used: str  # "global" | "incremental"
    timings_s: dict[str, float]


def image_names(image_dir: Path) -> list[str]:
    exts = (".jpg", ".jpeg", ".png")
    return sorted(p.name for p in Path(image_dir).iterdir() if p.suffix.lower() in exts)


def _largest_reconstruction(recons: dict[int, pycolmap.Reconstruction]) -> pycolmap.Reconstruction:
    if not recons:
        raise RuntimeError("SfM mapping produced zero reconstructions (0/N images registered)")
    return max(recons.values(), key=lambda r: r.num_reg_images())


def _match_orbit_closure(
    db_path: Path, image_names: list[str], work_dir: Path, num_threads: int = 4
) -> None:
    k = min(_LOOP_CLOSURE_K, len(image_names) // 4)
    if k < 1:
        return
    head, tail = image_names[:k], image_names[-k:]
    pairs = [(a, b) for a in head for b in tail if a != b]
    if not pairs:
        return
    list_path = work_dir / "loop_closure_pairs.txt"
    list_path.write_text("\n".join(f"{a} {b}" for a, b in pairs) + "\n")
    pycolmap.match_image_pairs(
        db_path,
        matching_options=pycolmap.FeatureMatchingOptions(num_threads=num_threads),
        pairing_options=pycolmap.ImportedPairingOptions(match_list_path=list_path),
    )


def _map(
    db_path: Path, image_dir: Path, sparse_dir: Path, mapper: str, num_images: int
) -> tuple[pycolmap.Reconstruction, str]:
    if mapper == "incremental":
        recons = pycolmap.incremental_mapping(db_path, image_dir, sparse_dir)
        return _largest_reconstruction(recons), "incremental"

    if mapper != "global":
        raise ValueError(f"mapper must be 'global' or 'incremental', got {mapper!r}")

    recons = pycolmap.global_mapping(db_path, image_dir, sparse_dir)
    recon = _largest_reconstruction(recons)
    fraction = recon.num_reg_images() / num_images if num_images else 0.0
    if len(recons) > 1 or fraction < _MIN_REGISTERED_FRACTION:
        logger.warning(
            "global_mapping registered %d/%d images (%.0f%%, %d components) -- retrying incremental",
            recon.num_reg_images(),
            num_images,
            fraction * 100,
            len(recons),
        )
        incr_recons = pycolmap.incremental_mapping(db_path, image_dir, sparse_dir)
        incr_recon = _largest_reconstruction(incr_recons)
        if incr_recon.num_reg_images() > recon.num_reg_images():
            return incr_recon, "incremental"
    return recon, "global"


def run_sfm(
    image_dir: Path,
    work_dir: Path,
    *,
    matcher: str = "sequential",
    overlap: int = 10,
    camera_model: str = "SIMPLE_RADIAL",
    single_camera: bool = True,
    mapper: str = "global",
    num_threads: int = 4,
) -> SfmResult:
    """CPU SIFT extract -> sequential match (+ orbit loop closure) -> global mapping with an
    automatic incremental fallback (research doc §1.1's recommended order) -> pick the largest
    component if the mapper fragmented the scene. Writes `work_dir/sparse/0` (COLMAP binary
    reconstruction) and `work_dir/sfm_meta.json` (read back by `undistort`).

    `num_threads` defaults to a conservative 4, not `os.cpu_count()`: SIFT extraction holds a full
    decoded image + octave pyramid per thread, and pycolmap's own extractor prints a RAM warning at
    the default (-1 = all cores) for exactly this reason -- confirmed the hard way (SIGKILL/exit
    137, system swap nearly exhausted) at `num_threads=-1` on 1920px frames on this 15GB laptop.
    """
    image_dir, work_dir = Path(image_dir), Path(work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)
    db_path = work_dir / "database.db"
    sparse_dir = work_dir / "sparse"
    sparse_dir.mkdir(exist_ok=True)

    names = image_names(image_dir)
    if len(names) < 2:
        raise ValueError(f"run_sfm: need >=2 images in {image_dir}, found {len(names)}")

    timings: dict[str, float] = {}

    t0 = time.monotonic()
    camera_mode = pycolmap.CameraMode.SINGLE if single_camera else pycolmap.CameraMode.AUTO
    reader_options = pycolmap.ImageReaderOptions(camera_model=camera_model)
    extraction_options = pycolmap.FeatureExtractionOptions(num_threads=num_threads)
    pycolmap.extract_features(
        db_path,
        image_dir,
        camera_mode=camera_mode,
        reader_options=reader_options,
        extraction_options=extraction_options,
    )
    timings["extract_features_s"] = time.monotonic() - t0

    t0 = time.monotonic()
    if matcher == "sequential":
        matching_options = pycolmap.FeatureMatchingOptions(num_threads=num_threads)
        pycolmap.match_sequential(
            db_path,
            matching_options=matching_options,
            pairing_options=pycolmap.SequentialPairingOptions(overlap=overlap),
        )
        _match_orbit_closure(db_path, names, work_dir, num_threads=num_threads)
    else:
        raise ValueError(f"matcher must be 'sequential', got {matcher!r}")
    timings["match_s"] = time.monotonic() - t0

    t0 = time.monotonic()
    recon, mapper_used = _map(db_path, image_dir, sparse_dir, mapper, len(names))
    timings["map_s"] = time.monotonic() - t0

    reconstruction_path = sparse_dir / "0"
    reconstruction_path.mkdir(parents=True, exist_ok=True)
    recon.write(reconstruction_path)

    (work_dir / _META_NAME).write_text(
        json.dumps({"image_dir": str(image_dir), "mapper_used": mapper_used, "timings_s": timings})
    )

    return SfmResult(
        reconstruction_path=reconstruction_path,
        num_registered=recon.num_reg_images(),
        num_images=len(names),
        mean_reprojection_error=recon.compute_mean_reprojection_error(),
        num_points3d=recon.num_points3D(),
        mapper_used=mapper_used,
        timings_s=timings,
    )


def undistort(work_dir: Path) -> Path:
    """PINHOLE-undistort the SfM output (research doc §7 pitfall 1) for OpenMVS's
    `InterfaceCOLMAP`. Reads `work_dir/sfm_meta.json` (written by `run_sfm`) for the original image
    dir. Returns `work_dir/dense` (COLMAP-format: `images/`, `sparse/`)."""
    work_dir = Path(work_dir)
    meta = json.loads((work_dir / _META_NAME).read_text())
    dense_dir = work_dir / "dense"
    pycolmap.undistort_images(
        output_path=dense_dir,
        input_path=work_dir / "sparse" / "0",
        image_path=Path(meta["image_dir"]),
        output_type="COLMAP",
    )
    return dense_dir
