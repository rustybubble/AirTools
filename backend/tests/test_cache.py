import json
import time

import pytest

from server.cache import OfflineMiss, _path, cached, get, normalize, put, write_json_atomic
from server.config import get_settings


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def test_normalize():
    assert normalize("  Hello,   World!!  ") == "hello world"
    assert normalize("Amerimax 5 in. K-style hidden gutter hanger") == (
        "amerimax 5 in. k-style hidden gutter hanger"
    )
    assert normalize("a/b - c.d") == "a/b - c.d"


def test_put_get_roundtrip(tmp_path):
    put("ns", "key1", {"a": 1})
    assert get("ns", "key1") == {"a": 1}
    files = list((tmp_path / "cache" / "ns").glob("*.json"))
    assert len(files) == 1
    data = json.loads(files[0].read_text())
    assert data["key"] == "key1"
    assert "fetched_at" in data
    assert data["value"] == {"a": 1}


def test_get_miss_returns_none():
    assert get("ns", "missing") is None


# --- write_json_atomic -------------------------------------------------------------------


def test_write_json_atomic_writes_full_content_and_no_leftover_tmp(tmp_path):
    path = tmp_path / "parts" / "p1" / "part.json"
    write_json_atomic(path, '{"a": 1}')

    assert path.read_text() == '{"a": 1}'
    assert list(path.parent.glob("*.tmp")) == []  # no leftover temp file


def test_write_json_atomic_overwrites_existing_file(tmp_path):
    path = tmp_path / "part.json"
    write_json_atomic(path, '{"a": 1}')
    write_json_atomic(path, '{"a": 2}')

    assert path.read_text() == '{"a": 2}'
    assert list(path.parent.glob("*.tmp")) == []


@pytest.mark.asyncio
async def test_cached_hit_skips_fn():
    calls = []

    async def fn():
        calls.append(1)
        return {"v": 42}

    v1 = await cached("ns", "q", fn)
    v2 = await cached("ns", "q", fn)
    assert v1 == v2 == {"v": 42}
    assert len(calls) == 1


@pytest.mark.asyncio
async def test_cached_dict_key():
    calls = []

    async def fn():
        calls.append(1)
        return "result"

    r1 = await cached("ns", {"engine": "x", "params": {"q": "hi"}}, fn)
    r2 = await cached("ns", {"engine": "x", "params": {"q": "hi"}}, fn)
    assert r1 == r2 == "result"
    assert len(calls) == 1


@pytest.mark.asyncio
async def test_cached_ttl_expired():
    calls = []

    async def fn():
        calls.append(1)
        return len(calls)

    v1 = await cached("ns", "ttl-key", fn, ttl_s=100)
    assert v1 == 1

    path = _path("ns", "ttl-key")
    data = json.loads(path.read_text())
    data["fetched_at"] = time.time() - 1000
    path.write_text(json.dumps(data))

    v2 = await cached("ns", "ttl-key", fn, ttl_s=100)
    assert v2 == 2
    assert len(calls) == 2


# --- OFFLINE ---------------------------------------------------------------


@pytest.mark.asyncio
async def test_offline_hit_returns_value_without_calling_fn(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    put("ns", "q", {"v": 1})

    async def fn():
        raise AssertionError("fn must not be called on an offline hit")

    assert await cached("ns", "q", fn) == {"v": 1}


@pytest.mark.asyncio
async def test_offline_miss_raises_without_calling_fn(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()

    async def fn():
        raise AssertionError("fn must not be called on an offline miss")

    with pytest.raises(OfflineMiss):
        await cached("ns", "never-cached", fn)


@pytest.mark.asyncio
async def test_offline_hit_ignores_expired_ttl(monkeypatch):
    """Stale beats nothing when the uplink is down."""
    put("ns", "stale-key", {"v": "old"})
    path = _path("ns", "stale-key")
    data = json.loads(path.read_text())
    data["fetched_at"] = time.time() - 1_000_000
    path.write_text(json.dumps(data))

    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()

    async def fn():
        raise AssertionError("fn must not be called offline even past ttl_s")

    assert await cached("ns", "stale-key", fn, ttl_s=100) == {"v": "old"}
