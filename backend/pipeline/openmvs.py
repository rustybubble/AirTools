"""Install the OpenMVS v2.4.0 prebuilt Ubuntu x64 CLI binaries (research doc §2.1, §6.2) -- no
conda-forge package exists and building from source pulls in Eigen/OpenCV/Ceres/CGAL/Boost, so the
tagged-release zip is the whole install. CPU-only build (`ldd`/`--help` confirmed no CUDA linkage
in the research pass) -- runs as-is on this laptop and any Linux x64 box, GPU or not.
"""

import os
import stat
import urllib.request
import zipfile
from pathlib import Path

_RELEASE_URL = (
    "https://github.com/cdcseacave/openMVS/releases/download/v2.4.0/OpenMVS_Ubuntu_x64.zip"
)
# Every binary mvs.py shells out to -- presence of all four is "already installed".
REQUIRED_BINS = ["InterfaceCOLMAP", "DensifyPointCloud", "ReconstructMesh", "TextureMesh"]


def default_dir() -> Path:
    """`$AIRTOOLS_OPENMVS_DIR` if set, else `<repo>/tools/openmvs/` -- overridable so this works
    both on this Fedora laptop and in a container where only `/workspace` persists."""
    env = os.environ.get("AIRTOOLS_OPENMVS_DIR")
    if env:
        return Path(env)
    return Path(__file__).resolve().parent.parent / "tools" / "openmvs"


def find_bin_dir(dest: Path | None = None) -> Path | None:
    """`dest/bin` (or its default) if all required binaries are already there, else None."""
    dest = Path(dest) if dest is not None else default_dir()
    bin_dir = dest / "bin"
    if all((bin_dir / name).exists() for name in REQUIRED_BINS):
        return bin_dir
    return None


def ensure_installed(dest: Path | None = None) -> Path:
    """Download + unzip the release into `dest/bin/` unless it's already there. Returns the bin
    dir. Idempotent: a second call with the same `dest` does no network I/O."""
    dest = Path(dest) if dest is not None else default_dir()
    existing = find_bin_dir(dest)
    if existing is not None:
        return existing

    dest.mkdir(parents=True, exist_ok=True)
    bin_dir = dest / "bin"
    zip_path = dest / "OpenMVS_Ubuntu_x64.zip"
    urllib.request.urlretrieve(_RELEASE_URL, zip_path)
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(bin_dir)
    zip_path.unlink()

    exec_bits = stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH
    for path in bin_dir.iterdir():
        if path.is_file():
            path.chmod(path.stat().st_mode | exec_bits)

    missing = [name for name in REQUIRED_BINS if not (bin_dir / name).exists()]
    if missing:
        raise RuntimeError(f"OpenMVS release zip did not contain expected binaries: {missing}")
    return bin_dir
