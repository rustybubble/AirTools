"""Shared, rate-limited chat client for the LLM-made asset experiments.

Every experiment process calls `chat()`; calls are serialised across processes with a file lock
and paced to Groq's free-tier output limit on `qwen/qwen3.8-27b` (measured by R3: ~1000 output
tokens/min, not shown in the rate-limit headers), per key, rotating keys on the daily cap. Each call is appended to
`work/assets-llm/calls.jsonl` so the bench can report tokens and time per asset.

Grok (`provider="xai"`) is for tasks Groq demonstrably can't do: it needs a `reason` and is logged
the same way. The Grok credit is being saved for demos, so keep those calls few.
"""

from __future__ import annotations

import fcntl
import json
import re
import time
from pathlib import Path

from openai import OpenAI, RateLimitError

from server.config import get_settings

GROQ_MODEL = "qwen/qwen3.8-27b"  # the only Groq vision model; 200K tokens/day on the free tier
GROQ_TEXT_MODEL = (
    "openai/gpt-oss-120b"  # text only, its own daily quota (shared with the live server)
)
GROK_MODEL = "grok-4.7"
OUT_TOKENS_PER_MIN = 900  # ponytail: stays under the measured 1000/min; raise if Groq raises it
WORK = Path(__file__).resolve().parents[3] / "work" / "assets-llm"
LOCK = WORK / "groq.lock"
LEDGER = WORK / "calls.jsonl"


class DailyQuotaExceeded(RuntimeError):
    """Groq's per-day token cap (TPD) for this model is used up on every key; retrying won't help
    for hours."""


CAP_RECHECK_S = 1800  # a key capped for a model is skipped this long, then tried again


def _ledger(window_s: float) -> list[dict]:
    if not LEDGER.exists():
        return []
    now = time.time()
    recs = (json.loads(line) for line in LEDGER.read_text().splitlines()[-500:])
    return [r for r in recs if now - r["t_end"] < window_s]


def _recent_out_tokens(model: str, key: int, window_s: float = 60.0) -> int:
    return sum(
        r.get("out_tokens") or 0
        for r in _ledger(window_s)
        if r.get("model") == model and r.get("key", 1) == key
    )


def _capped(model: str) -> set[int]:
    return {r["key"] for r in _ledger(CAP_RECHECK_S) if r.get("capped") and r.get("model") == model}


def _log(rec: dict) -> None:
    with open(LEDGER, "a") as f:
        f.write(json.dumps(rec) + "\n")


def _retry_after(exc: RateLimitError) -> float:
    header = exc.response.headers.get("retry-after") if exc.response is not None else None
    if header:
        return float(header) + 1
    m = re.search(r"try again in (?:(\d+)m)?([\d.]+)s", str(exc))  # Groq: "7m23.5s" or "4.2s"
    return 60 * float(m.group(1) or 0) + float(m.group(2)) + 1 if m else 30.0


def chat(
    messages: list[dict],
    *,
    tag: str,
    provider: str = "groq",
    model: str | None = None,
    reason: str | None = None,
    max_tokens: int = 2048,
    json_mode: bool = False,
    temperature: float = 0.2,
) -> tuple[str, dict]:
    """One chat completion. `tag` names the experiment/part/step for the ledger, e.g.
    "e1/rheem-xe40/gen". Returns (text, record). Blocks until a Groq key has budget.

    Groq keys rotate like the server's (`GROQ_API_KEY`, `_2`, `_3`, ...): a key that hits the
    model's daily cap is logged as capped and skipped for `CAP_RECHECK_S`; the per-minute output
    budget is tracked per key. Records name keys by position only ("key": 2), never by value."""
    s = get_settings()
    if provider == "xai":
        if not reason:
            raise ValueError("Grok calls need a reason (Groq tried and was not good enough)")
        keys, model = [s.GROK_API_KEY], GROK_MODEL
        base_url, timeout = "https://api.x.ai/v1", 600  # grok-4.7 reasons; >180 s seen
    else:
        keys, model = s.keys("GROQ_API_KEY"), model or GROQ_MODEL
        base_url, timeout = "https://api.groq.com/openai/v1", 120
    kwargs = {"response_format": {"type": "json_object"}} if json_mode else {}
    # expect about half of max_tokens, but never more than half the budget, or a large
    # max_tokens could never fit and the wait below would never end
    need = min(max_tokens // 2, OUT_TOKENS_PER_MIN // 2)
    WORK.mkdir(parents=True, exist_ok=True)
    resp, wait = None, 0.0
    for attempt in range(8):
        with open(LOCK, "a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)  # one call at a time across all experiment processes
            try:
                while True:
                    usable = [i for i in range(1, len(keys) + 1) if i not in _capped(model)]
                    if provider == "groq" and not usable:
                        raise DailyQuotaExceeded(
                            f"{model}: daily cap reached on all {len(keys)} keys"
                        )
                    ready = [
                        i
                        for i in usable
                        if provider != "groq"
                        or _recent_out_tokens(model, i) + need <= OUT_TOKENS_PER_MIN
                    ]
                    if ready:
                        key = ready[0]
                        break
                    time.sleep(5)
                # no silent SDK retries: a timed-out Grok call may still be billed, so retrying it blind
                # doubles the cost; our own loop handles 429s
                client = OpenAI(
                    base_url=base_url, api_key=keys[key - 1], timeout=timeout, max_retries=0
                )
                t0 = time.time()
                try:
                    resp = client.chat.completions.create(
                        model=model,
                        messages=messages,
                        max_tokens=max_tokens,
                        temperature=temperature,
                        **kwargs,
                    )
                except RateLimitError as exc:
                    base = {
                        "tag": tag,
                        "provider": provider,
                        "model": model,
                        "key": key,
                        "rate_limited": str(exc)[:300],
                        "t_end": time.time(),
                    }
                    if "tokens per day" in str(exc) or "requests per day" in str(exc):
                        _log({**base, "capped": True})
                        continue  # next key straight away
                    if attempt == 7:
                        raise
                    wait = min(_retry_after(exc), 90)  # capped: a long retry-after starved everyone
                    _log({**base, "wait_s": wait})
            finally:
                fcntl.flock(lock, fcntl.LOCK_UN)
        if resp is not None:
            break
        time.sleep(wait)  # outside the lock, so other processes keep going
        wait = 0.0
    if resp is None:
        raise RuntimeError(f"{model}: no response after {attempt + 1} attempts")
    usage = resp.usage
    rec = {
        "tag": tag,
        "provider": provider,
        "model": model,
        "key": key,
        "reason": reason,
        "t_start": t0,
        "t_end": time.time(),
        "latency_s": round(time.time() - t0, 2),
        "in_tokens": usage.prompt_tokens if usage else None,
        "out_tokens": usage.completion_tokens if usage else None,
    }
    _log(rec)
    return resp.choices[0].message.content or "", rec
