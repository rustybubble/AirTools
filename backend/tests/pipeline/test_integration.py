"""Full video -> scene package, on real footage, through the real tools. Slow (SfM + OpenMVS
dense/mesh/texture on ~24 frames takes minutes even at a low resolution level) -- excluded by
default (`addopts = "-m 'not live and not slow'"`), run explicitly with `-m slow`.
"""

import shutil
from pathlib import Path

import pytest

pytest.importorskip("pycolmap")
pytest.importorskip("pymeshlab")

from pipeline import openmvs
from pipeline.package import validate_package
from pipeline.run import run

_VIDEO = Path(__file__).resolve().parents[2] / "data" / "corpus" / "strasbourg-cathedral-spire.mp4"

pytestmark = [
    pytest.mark.slow,
    pytest.mark.skipif(not _VIDEO.exists(), reason=f"{_VIDEO} not present"),
    pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not on PATH"),
    pytest.mark.skipif(
        openmvs.find_bin_dir() is None,
        reason="OpenMVS not installed (pipeline.openmvs.ensure_installed)",
    ),
]


def test_run_end_to_end_produces_a_valid_package(tmp_path):
    out_dir = tmp_path / "scene"
    work_dir = tmp_path / "work"

    report = run(
        _VIDEO,
        site="strasbourg-cathedral-spire-test",
        out_dir=out_dir,
        work_dir=work_dir,
        fps=1.5,  # ~24 frames from the 16s clip
        long_edge=960,  # keep the smoke test fast
        resolution_level=2,  # ditto -- quarter-res depth maps
        target_triangles=20_000,
        collision_triangles=5_000,
    )

    assert (out_dir / "mesh.glb").exists()
    assert (out_dir / "collision.glb").exists()
    assert (out_dir / "scene.json").exists()
    assert (out_dir / "cameras.json").exists()

    problems = validate_package(out_dir)
    assert problems == [], f"validate_package found problems: {problems}"

    assert report["sfm"]["num_registered"] >= 2
    assert report["mesh"]["triangles"] > 0
