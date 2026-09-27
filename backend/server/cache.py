"""Disk JSON cache. Every paid API call in this project goes through `cached()`.

Layout: `<DATA_DIR>/cache/<namespace>/<sha1(normalized key)>.json` holding
`{"key": ..., "fetched_at": <epoch seconds>, "value": ...}`.
"""

import hashlib
import json
import os
import re
import tempfile
import time
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

from server.config import get_settings

_PUNCT_RE = re.compile(r"[^\w\s.\-/]")
_WS_RE = re.compile(r"\s+")


class OfflineMiss(Exception):
    """`cached()` raised this: `Settings.OFFLINE` is set and there's no cached value for `key`."""


def write_json_atomic(path: Path, text: str) -> None:
    """Write `text` to `path` via a same-dir temp file + `os.replace` -- a reader never sees a
    torn/partial file, and a crash mid-write leaves the previous version (or nothing) instead of
    corrupt JSON. The one choke point every part.json/notebook/order write goes through
    (jobs.save_part, assets.resolve_asset, checkout receipts, app.py's notebook)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w") as f:
            f.write(text)
        os.replace(tmp_path, path)
    except BaseException:
        Path(tmp_path).unlink(missing_ok=True)
        raise


def normalize(text: str) -> str:
    """Lowercase, strip punctuation except `. - /`, collapse whitespace."""
    text = _PUNCT_RE.sub("", text.lower())
    return _WS_RE.sub(" ", text).strip()


def _key_string(key: str | dict) -> str:
    if isinstance(key, dict):
        return json.dumps(key, sort_keys=True, default=str)
    return str(key)


def _path(namespace: str, key: str | dict) -> Path:
    # ponytail: normalize() strips JSON punctuation from dict keys before hashing, so two
    # differently-shaped dicts could in theory collapse to the same digest. Not observed in
    # practice at this key volume; upgrade to hashing the raw json string if it ever bites.
    digest = hashlib.sha1(normalize(_key_string(key)).encode()).hexdigest()
    return Path(get_settings().DATA_DIR) / "cache" / namespace / f"{digest}.json"


def get(namespace: str, key: str | dict) -> Any | None:
    path = _path(namespace, key)
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text())["value"]
    except (json.JSONDecodeError, OSError, KeyError):
        return None


def put(namespace: str, key: str | dict, value: Any) -> None:
    path = _path(namespace, key)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"key": key, "fetched_at": time.time(), "value": value}))


async def cached(
    namespace: str,
    key: str | dict,
    fn: Callable[[], Awaitable[Any]],
    ttl_s: float | None = None,
) -> Any:
    """Return the cached value for `key`, or call `fn()`, cache it, and return that.

    `Settings.OFFLINE`: a cache hit still returns (TTL ignored -- stale beats nothing when the
    uplink is down); a miss raises `OfflineMiss` instead of calling `fn()`. This is the one
    network choke point every paid call in `web.py`/`sellers.py` goes through, so it's the single
    check that keeps both modules offline.
    """
    offline = get_settings().OFFLINE
    path = _path(namespace, key)
    if path.exists():
        try:
            data = json.loads(path.read_text())
            if offline or ttl_s is None or (time.time() - data["fetched_at"]) < ttl_s:
                return data["value"]
        except (json.JSONDecodeError, OSError, KeyError):
            pass
    if offline:
        raise OfflineMiss(f"offline: no cached value for {namespace}/{key!r}")
    value = await fn()
    put(namespace, key, value)
    return value
