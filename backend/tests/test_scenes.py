"""GET /scenes, GET /scenes/<site>/<file> (plan §1a/§1b): read-only scene package serving."""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from server.app import _safe_scene_path, app
from server.config import get_settings


@pytest.fixture
def scene_root(tmp_path, monkeypatch):
    root = tmp_path / "scene"
    monkeypatch.setenv("SCENE_DIR", str(root))
    get_settings.cache_clear()
    yield root
    get_settings.cache_clear()


def _write_scene(root: Path, site: str, *, revision: int = 2, quality: str = "full") -> Path:
    site_dir = root / site
    site_dir.mkdir(parents=True)
    (site_dir / f"mesh.r{revision}.glb").write_bytes(b"glb-bytes")
    (site_dir / f"collision.r{revision}.glb").write_bytes(b"collision-bytes")
    (site_dir / f"cameras.r{revision}.json").write_text("[]")
    (site_dir / "thumbs").mkdir()
    (site_dir / "thumbs" / "0001.jpg").write_bytes(b"\xff\xd8\xff\xe0jpeg")
    scene_json = {
        "name": site,
        "units": "meters",
        "mesh": {"file": f"mesh.r{revision}.glb"},
        "quality": quality,
        "revision": revision,
    }
    (site_dir / "scene.json").write_text(json.dumps(scene_json))
    return site_dir


# --- _safe_scene_path: pure traversal-safety logic --------------------------


def test_safe_scene_path_rejects_dotdot_segments(scene_root):
    (scene_root / "site-a").mkdir(parents=True)
    assert _safe_scene_path("site-a", "../secret.txt") is None
    assert _safe_scene_path("site-a", "thumbs/../../secret.txt") is None
    assert _safe_scene_path("site-a", "..") is None


def test_safe_scene_path_rejects_bad_site_name(scene_root):
    assert _safe_scene_path("UPPER", "scene.json") is None
    assert _safe_scene_path("a_b", "scene.json") is None


def test_safe_scene_path_accepts_nested_thumb(scene_root):
    site_dir = _write_scene(scene_root, "site-a")
    resolved = _safe_scene_path("site-a", "thumbs/0001.jpg")
    assert resolved == (site_dir / "thumbs" / "0001.jpg").resolve()


def test_safe_scene_path_rejects_symlink_escape(scene_root):
    site_dir = scene_root / "site-a"
    site_dir.mkdir(parents=True)
    secret = scene_root.parent / "secret.txt"
    secret.write_text("nope")
    (site_dir / "escape.json").symlink_to(secret)
    assert _safe_scene_path("site-a", "escape.json") is None


# --- GET /scenes --------------------------------------------------------


def test_scenes_list(scene_root):
    _write_scene(scene_root, "strasbourg-cathedral-spire", revision=2, quality="full")

    with TestClient(app) as client:
        resp = client.get("/scenes")
        assert resp.status_code == 200
        body = resp.json()
        assert body == [
            {
                "site": "strasbourg-cathedral-spire",
                "revision": 2,
                "quality": "full",
                "updated_at": body[0]["updated_at"],  # asserted non-empty below
            }
        ]
        assert body[0]["updated_at"]


def test_scenes_list_defaults_revision_and_quality_when_absent(scene_root):
    """Packages written before §1a's revision naming landed have no "revision"/"quality" field --
    the listing must still work, treating them as a single already-published full revision."""
    site_dir = scene_root / "legacy-site"
    site_dir.mkdir(parents=True)
    (site_dir / "scene.json").write_text(json.dumps({"name": "legacy-site"}))

    with TestClient(app) as client:
        body = client.get("/scenes").json()
        assert body == [
            {
                "site": "legacy-site",
                "revision": 1,
                "quality": "full",
                "updated_at": body[0]["updated_at"],
            }
        ]


def test_scenes_list_empty_or_missing_dir(scene_root):
    with TestClient(app) as client:
        assert client.get("/scenes").json() == []

    other_root = scene_root.parent / "does-not-exist"
    import os

    os.environ["SCENE_DIR"] = str(other_root)
    get_settings.cache_clear()
    try:
        with TestClient(app) as client:
            assert client.get("/scenes").json() == []
    finally:
        os.environ["SCENE_DIR"] = str(scene_root)
        get_settings.cache_clear()


def test_scenes_list_skips_dirs_without_scene_json(scene_root):
    (scene_root / "half-written").mkdir(parents=True)
    with TestClient(app) as client:
        assert client.get("/scenes").json() == []


# --- GET /scenes/<site>/<file>: content types + static serving --------------


def test_scene_json_served_with_correct_content_type(scene_root):
    _write_scene(scene_root, "site-a")
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/scene.json")
        assert resp.status_code == 200
        assert resp.headers["content-type"].startswith("application/json")
        assert resp.json()["revision"] == 2


def test_scene_glb_and_jpg_content_types(scene_root):
    _write_scene(scene_root, "site-a", revision=3)
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/mesh.r3.glb")
        assert resp.status_code == 200
        assert resp.headers["content-type"] == "model/gltf-binary"
        assert resp.content == b"glb-bytes"

        resp = client.get("/scenes/site-a/thumbs/0001.jpg")
        assert resp.status_code == 200
        assert resp.headers["content-type"] == "image/jpeg"


def test_scene_unknown_file_and_site_404(scene_root):
    _write_scene(scene_root, "site-a")
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/does-not-exist.json")
        assert resp.status_code == 404

        resp = client.get("/scenes/no-such-site/scene.json")
        assert resp.status_code == 404


def test_scene_traversal_via_url_rejected(scene_root):
    """Percent-encoded ".." segments reach the route handler as literal ".." after FastAPI's
    path-param decoding (unlike a literal ".." in the URL, which httpx/the browser would collapse
    before the request is even sent) -- this is the case `_safe_scene_path` has to catch."""
    _write_scene(scene_root, "site-a")
    secret = scene_root.parent / "secret.txt"
    secret.write_text("nope")

    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/thumbs/%2e%2e/%2e%2e/secret.txt")
        assert resp.status_code == 404

        resp = client.get("/scenes/UPPER_BAD/scene.json")
        assert resp.status_code == 404  # site regex fails inside _safe_scene_path -> not found


# --- cache headers: scene.json (no-cache + ETag/304) vs revisioned files (immutable) ----


def test_scene_json_etag_and_conditional_304(scene_root):
    _write_scene(scene_root, "site-a")
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/scene.json")
        assert resp.status_code == 200
        assert resp.headers["cache-control"] == "no-cache"
        etag = resp.headers["etag"]
        assert etag

        resp2 = client.get("/scenes/site-a/scene.json", headers={"If-None-Match": etag})
        assert resp2.status_code == 304
        assert resp2.content == b""

        resp3 = client.get("/scenes/site-a/scene.json", headers={"If-None-Match": '"stale-etag"'})
        assert resp3.status_code == 200


def test_scene_revisioned_file_has_long_immutable_cache(scene_root):
    _write_scene(scene_root, "site-a", revision=3)
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/mesh.r3.glb")
        assert resp.status_code == 200
        cache_control = resp.headers["cache-control"]
        assert "immutable" in cache_control
        assert "max-age=31536000" in cache_control


def test_scene_non_revisioned_file_has_no_special_cache_header(scene_root):
    _write_scene(scene_root, "site-a")
    with TestClient(app) as client:
        resp = client.get("/scenes/site-a/thumbs/0001.jpg")
        assert resp.status_code == 200
        cache_control = resp.headers.get("cache-control", "")
        assert "immutable" not in cache_control
        assert cache_control != "no-cache"
