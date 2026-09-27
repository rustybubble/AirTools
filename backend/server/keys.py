"""Rotate through a service's API keys when one gets rate limited.

`.env` holds `SERP_API_KEY`, `SERP_API_KEY_2`, ... (see `Settings.keys`). A `KeyPool` tries
them in that order: a key that fails with the service's rate-limit error cools down for
`cooldown_s` and the same call is retried with the next key, until every key is cooling --
then `KeysExhausted`. Any other error is raised as-is (a bad request isn't a quota problem).

    SERPAPI_KEYS = KeyPool("SERP_API_KEY", is_limited=lambda e: ..., cooldown_s=3600)
    data = await SERPAPI_KEYS.call(lambda key: fetch(key))

Key values never reach a log line or an exception message; keys are named by position
("SERP_API_KEY #2"). Cooldowns are in-process only (a restart retries every key once).
"""

import logging
import time
from collections.abc import Awaitable, Callable
from typing import TypeVar

from server.config import get_settings

logger = logging.getLogger(__name__)

T = TypeVar("T")

_POOLS: list["KeyPool"] = []


class KeysExhausted(RuntimeError):
    """Every key for a service is rate limited (or none is configured)."""


class KeyPool:
    def __init__(
        self,
        env_name: str,
        is_limited: Callable[[Exception], bool],
        cooldown_s: float,
        anonymous_last: bool = False,
    ):
        """`anonymous_last`: after the keys, try once with key=None (services usable without
        auth at a lower quota, e.g. HF Spaces)."""
        self.env_name = env_name
        self.is_limited = is_limited
        self.cooldown_s = cooldown_s
        self.anonymous_last = anonymous_last
        self._cool_until: dict[str | None, float] = {}
        _POOLS.append(self)

    def keys(self) -> list[str | None]:
        # Read live, not at import: settings change under tests and `.env` edits on restart.
        found: list[str | None] = list(get_settings().keys(self.env_name))
        return [*found, None] if self.anonymous_last else found

    async def call(self, fn: Callable[[str | None], Awaitable[T]]) -> T:
        """`await fn(key)` with the first key not cooling down; on a rate-limit error, cool that
        key and move on. KeysExhausted once no key is left."""
        keys = self.keys()
        for position, key in enumerate(keys, start=1):
            if self._cool_until.get(key, 0.0) > time.monotonic():
                continue
            try:
                return await fn(key)
            except Exception as exc:
                if not self.is_limited(exc):
                    raise
                self._cool_until[key] = time.monotonic() + self.cooldown_s
                label = "anonymous" if key is None else f"#{position}"
                logger.warning("%s %s rate limited; trying the next key", self.env_name, label)
        raise KeysExhausted(
            f"{self.env_name}: all {len(keys)} keys rate limited"
            if keys
            else f"{self.env_name}: no keys configured"
        )

    def exhausted(self) -> bool:
        """asset-mode: every key (and anonymous, when this pool uses it) is cooling down right now
        -- the next `call` would raise KeysExhausted without trying anything."""
        now = time.monotonic()
        keys = self.keys()
        return bool(keys) and all(self._cool_until.get(k, 0.0) > now for k in keys)

    def cooling_s(self) -> float:
        """asset-mode: seconds until the first key comes off cooldown (0 when one is usable)."""
        now = time.monotonic()
        waits = [self._cool_until.get(k, 0.0) - now for k in self.keys()]
        return max(0.0, min(waits)) if waits else 0.0

    def reset(self) -> None:
        self._cool_until.clear()


def reset_all() -> None:
    """Forget every cooldown (tests; or after topping up a quota without a restart)."""
    for pool in _POOLS:
        pool.reset()
