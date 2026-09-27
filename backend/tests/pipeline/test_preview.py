import time

import numpy as np
import pytest
import trimesh
from PIL import Image

from pipeline.package import Camera
from pipeline.preview import _fill_see_through, _sample_texture, render

_TEXTURE_COLOR = (200, 80, 40)


def test_sample_texture_matches_trimesh_to_color():
    """Ground truth is trimesh itself: after a glb round trip, `to_color()` gives each vertex
    the texel its UV points at. Our sampler must agree, or every render (and QA score) reads
    the wrong part of the atlas -- the flat-orange-render bug."""
    image = np.zeros((64, 64, 3), dtype=np.uint8)
    image[:32] = (255, 0, 0)  # top half red
    image[32:] = (0, 0, 255)  # bottom half blue
    quad = trimesh.Trimesh(
        vertices=[[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]],
        faces=[[0, 1, 2], [0, 2, 3]],
        visual=trimesh.visual.TextureVisuals(
            uv=[[0.1, 0.1], [0.9, 0.1], [0.9, 0.9], [0.1, 0.9]], image=Image.fromarray(image)
        ),
        process=False,
    )
    loaded = trimesh.load(
        trimesh.util.wrap_as_stream(quad.export(file_type="glb")), file_type="glb", force="mesh"
    )

    expected = loaded.visual.to_color().vertex_colors[:, :3]
    colors = _sample_texture(np.asarray(loaded.visual.material.baseColorTexture), loaded.visual.uv)

    assert colors.tolist() == expected.tolist()
    assert tuple(colors[2]) == (255, 0, 0)  # v=0.9 -> near the image's top row


def _textured_box(extents=(4.0, 2.0, 2.0)) -> trimesh.Trimesh:
    """A solid-color textured box centred at the origin -- easy to check rendered colours against
    (whole visible surface should be one known colour)."""
    box = trimesh.creation.box(extents=extents)
    image = Image.new("RGB", (8, 8), _TEXTURE_COLOR)
    uv = np.zeros((len(box.vertices), 2))  # constant UV -> samples the same texel everywhere
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, baseColorFactor=[1, 1, 1, 1]
    )
    box.visual = trimesh.visual.TextureVisuals(uv=uv, image=image, material=material)
    return box


def _front_camera(width=200, height=150) -> Camera:
    """Camera at world (0, 0, 10) looking at the origin (-Z), up = +Y -- same look-at convention
    `run.py`/`test_run.py` use (`[right, down, forward]` camera-to-world columns)."""
    pos = np.array([0.0, 0.0, 10.0])
    forward = -pos / np.linalg.norm(pos)
    up = np.array([0.0, 1.0, 0.0])
    right = np.cross(forward, up)
    down = np.cross(forward, right)
    R_cw = np.stack([right, down, forward], axis=1)
    R_wc = R_cw.T
    t = -R_wc @ pos
    return Camera(
        id="0001",
        file="frames/0001.jpg",
        thumb="thumbs/0001.jpg",
        R=R_wc,
        t=t,
        fx=300.0,
        fy=300.0,
        cx=width / 2,
        cy=height / 2,
        w=width,
        h=height,
    )


def test_render_silhouette_and_colour_match_expectations():
    mesh = _textured_box()
    camera = _front_camera(width=200, height=150)

    image, covered = render(mesh, camera, width=200, height=150)

    assert image.shape == (150, 200, 3)
    assert covered.shape == (150, 200)

    # centre of the image: the box's front face, dead ahead of the camera -- must be covered and
    # the known texture colour
    assert covered[75, 100]
    assert image[75, 100] == pytest.approx(_TEXTURE_COLOR, abs=5)

    # corner of the image: nothing there but background
    assert not covered[0, 0]
    assert tuple(image[0, 0]) == (0, 0, 0)

    # a solid box, centred and fully inside the frame, covers a real fraction of the image but
    # not all of it (background is real too)
    coverage = covered.mean()
    assert 0.10 < coverage < 0.5

    # every covered pixel is (approximately) the one known texture colour -- solid surface, not
    # noise/dots
    covered_pixels = image[covered]
    assert covered_pixels.mean(axis=0) == pytest.approx(_TEXTURE_COLOR, abs=5)
    assert covered_pixels.std(axis=0).max() < 5.0  # low variance -> genuinely solid, not spotty


def test_render_accepts_mesh_path(tmp_path):
    mesh = _textured_box()
    path = tmp_path / "mesh.glb"
    mesh.export(path)
    camera = _front_camera()

    image, covered = render(path, camera, width=200, height=150)

    assert covered.any()
    assert image[covered].mean(axis=0) == pytest.approx(_TEXTURE_COLOR, abs=5)


def test_render_intrinsics_scale_with_output_size():
    """Rendering at half the camera's native resolution with the same camera object should give
    (about) the same silhouette fraction -- render() must scale fx/fy/cx/cy, not just crop."""
    mesh = _textured_box()
    camera = _front_camera(width=200, height=150)

    _, covered_full = render(mesh, camera, width=200, height=150)
    _, covered_half = render(mesh, camera, width=100, height=75)

    assert covered_half.mean() == pytest.approx(covered_full.mean(), abs=0.05)


@pytest.mark.slow
def test_render_handles_200k_triangles_under_30s():
    """Assignment's hard budget: ~200k triangles at 960x540 in well under 30s. Excluded from the
    default run (`-m slow` to opt in) since building a ~200k-face mesh itself takes real time."""
    box = trimesh.creation.box(extents=(4.0, 2.0, 2.0))
    for _ in range(7):  # 12 faces * 4**7 ~= 196.6k triangles
        box = box.subdivide()
    image = Image.new("RGB", (8, 8), _TEXTURE_COLOR)
    uv = np.zeros((len(box.vertices), 2))
    material = trimesh.visual.material.PBRMaterial(
        baseColorTexture=image, baseColorFactor=[1, 1, 1, 1]
    )
    box.visual = trimesh.visual.TextureVisuals(uv=uv, image=image, material=material)
    assert len(box.faces) > 150_000

    camera = _front_camera(width=960, height=540)
    camera.fx = camera.fy = 300.0 * (960 / 200)  # keep the same framing at the larger resolution
    camera.cx, camera.cy = 480.0, 270.0

    t0 = time.monotonic()
    _, covered = render(box, camera, width=960, height=540)
    elapsed = time.monotonic() - t0

    assert elapsed < 30.0, f"render took {elapsed:.1f}s, budget is 30s"
    assert covered.mean() > 0.05


def test_fill_see_through_patches_speckles_but_keeps_silhouettes():
    out = np.zeros((9, 9, 3), dtype=np.uint8)
    depth = np.full((9, 9), 1.0)
    out[:, :] = 200  # front surface at depth 1
    out[:, 6:] = 50  # a far wall at depth 3 on the right: a real silhouette edge at x=6
    depth[:, 6:] = 3.0
    out[3, 2], depth[3, 2] = 50, 3.0  # a front-surface pixel the sampling missed
    _fill_see_through(out, depth)
    assert out[3, 2, 0] == 200
    assert (out[:, 6:] == 50).all()
