import pytest

pytest.importorskip(
    "pycolmap"
)  # pipeline.sfm needs the `pipeline` extra (uv sync --extra pipeline)

from pipeline.sfm import _largest_reconstruction, image_names


class _FakeRecon:
    def __init__(self, n_reg):
        self._n_reg = n_reg

    def num_reg_images(self):
        return self._n_reg


# --- image_names -----------------------------------------------------------------------


def test_image_names_filters_to_images_and_sorts(tmp_path):
    (tmp_path / "0002.jpg").write_bytes(b"")
    (tmp_path / "0001.jpg").write_bytes(b"")
    (tmp_path / "notes.txt").write_bytes(b"")
    (tmp_path / "0003.PNG").write_bytes(b"")

    names = image_names(tmp_path)

    assert names == ["0001.jpg", "0002.jpg", "0003.PNG"]


# --- _largest_reconstruction -------------------------------------------------------------


def test_largest_reconstruction_picks_most_registered():
    recons = {0: _FakeRecon(5), 1: _FakeRecon(20), 2: _FakeRecon(12)}
    assert _largest_reconstruction(recons) is recons[1]


def test_largest_reconstruction_raises_on_empty():
    with pytest.raises(RuntimeError):
        _largest_reconstruction({})
