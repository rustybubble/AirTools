"""Cheap web discovery + page fetch, ahead of the LLM (docs/research/search-sellers.md).

Groq's free tier is 8K tokens/min and its built-in `browser_search` costs ~50K prompt tokens
per call, so discovery/fetch happen here with cheap search APIs and the LLM only ever sees
condensed text (`condense`). Every network call goes through `cache.cached`; API keys never
enter a cache key or a log line.
"""

import asyncio
import ipaddress
import logging
import socket
from urllib.parse import urljoin, urlparse

import httpx
from pydantic import BaseModel

from server.cache import cached
from server.config import get_settings

logger = logging.getLogger(__name__)

# Home Depot / Lowe's render their spec tables client-side; every text-fetch service tested
# returns only the pre-render nav shell for them (search-sellers.md §3, §6, warning 3). They
# need SerpApi's dedicated home_depot_product engine (server/sellers.py) instead.
_BLOCKED_FETCH_HOSTS = ("homedepot.com", "lowes.com")

_NAV_WORDS = ("shop", "sign in", "log in", "cart", "menu", "skip to", "privacy", "subscribe")


class Hit(BaseModel):
    title: str
    url: str
    text: str = ""
    source: str


def _blocked_host(url: str) -> bool:
    host = urlparse(url).netloc.lower()
    return any(blocked in host for blocked in _BLOCKED_FETCH_HOSTS)


# --- discovery providers, tried in order by search() -------------------------------------


async def _exa_search(query: str, n: int) -> list[Hit]:
    settings = get_settings()
    if not settings.EXA_API_KEY:
        return []

    async def do_fetch() -> dict:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://api.exa.ai/search",
                headers={"x-api-key": settings.EXA_API_KEY},
                json={
                    "query": query,
                    "type": "auto",
                    "numResults": n,
                    "contents": {"text": {"maxCharacters": 1500}},
                },
            )
            resp.raise_for_status()
            return resp.json()

    try:
        data = await cached("web", {"provider": "exa_search", "query": query, "n": n}, do_fetch)
    except httpx.HTTPError:
        return []
    return [
        Hit(
            title=r.get("title") or "",
            url=r.get("url") or "",
            text=r.get("text") or "",
            source="exa",
        )
        for r in (data or {}).get("results", [])
    ]


async def _you_search(query: str, n: int) -> list[Hit]:
    settings = get_settings()
    if not settings.YOUDCOM_API_KEY:
        return []

    async def do_fetch() -> dict:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://ydc-index.io/v1/search",
                headers={"X-API-Key": settings.YOUDCOM_API_KEY},
                json={"query": query, "count": n},
            )
            resp.raise_for_status()
            return resp.json()

    try:
        data = await cached("web", {"provider": "you_search", "query": query, "n": n}, do_fetch)
    except httpx.HTTPError:
        return []
    results = (data or {}).get("results", {}).get("web", [])
    return [
        Hit(
            title=r.get("title") or "",
            url=r.get("url") or "",
            text=" ".join(filter(None, [r.get("description"), *(r.get("snippets") or [])])),
            source="you",
        )
        for r in results
    ]


async def _tavily_search(query: str, n: int) -> list[Hit]:
    settings = get_settings()
    if not settings.TAVILY_API_KEY:
        return []

    async def do_fetch() -> dict:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://api.tavily.com/search",
                headers={"Authorization": f"Bearer {settings.TAVILY_API_KEY}"},
                json={
                    "query": query,
                    "search_depth": "basic",
                    "max_results": n,
                    "include_raw_content": False,
                },
            )
            resp.raise_for_status()
            return resp.json()

    try:
        data = await cached("web", {"provider": "tavily_search", "query": query, "n": n}, do_fetch)
    except httpx.HTTPError:
        return []
    return [
        Hit(
            title=r.get("title") or "",
            url=r.get("url") or "",
            text=r.get("content") or "",
            source="tavily",
        )
        for r in (data or {}).get("results", [])
    ]


async def _ollama_search(query: str, n: int) -> list[Hit]:
    settings = get_settings()
    if not settings.OLLAMA_WEB_API_KEY:
        return []

    async def do_fetch() -> dict:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://ollama.com/api/web_search",
                headers={"Authorization": f"Bearer {settings.OLLAMA_WEB_API_KEY}"},
                json={"query": query, "max_results": n},
            )
            resp.raise_for_status()
            return resp.json()

    try:
        data = await cached("web", {"provider": "ollama_search", "query": query, "n": n}, do_fetch)
    except httpx.HTTPError:
        return []
    return [
        Hit(
            title=r.get("title") or "",
            url=r.get("url") or "",
            text=r.get("content") or "",
            source="ollama",
        )
        for r in (data or {}).get("results", [])
    ]


_SEARCH_PROVIDERS = (
    ("exa", _exa_search),
    ("you", _you_search),
    ("tavily", _tavily_search),
    ("ollama", _ollama_search),
)


async def search(query: str, n: int = 6) -> list[Hit]:
    """Try Exa -> You.com -> Tavily -> Ollama web_search; return the first non-empty result."""
    for name, provider in _SEARCH_PROVIDERS:
        hits = await provider(query, n)
        if hits:
            logger.info("web.search provider=%s query=%r hits=%d", name, query, len(hits))
            return hits
    logger.warning("web.search: no provider returned results for query=%r", query)
    return []


# --- page fetch, for spec extraction ------------------------------------------------------


async def _ollama_fetch(url: str) -> str | None:
    settings = get_settings()
    if not settings.OLLAMA_WEB_API_KEY:
        return None

    async def do_fetch() -> dict | None:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://ollama.com/api/web_fetch",
                headers={"Authorization": f"Bearer {settings.OLLAMA_WEB_API_KEY}"},
                json={"url": url},
            )
            if resp.status_code >= 400:
                return None
            return resp.json()

    try:
        data = await cached("web", {"provider": "ollama_fetch", "url": url}, do_fetch)
    except httpx.HTTPError:
        return None
    return (data or {}).get("content") or None


async def _exa_fetch(url: str, max_chars: int) -> str | None:
    settings = get_settings()
    if not settings.EXA_API_KEY:
        return None

    async def do_fetch() -> dict:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                "https://api.exa.ai/contents",
                headers={"x-api-key": settings.EXA_API_KEY},
                json={"urls": [url], "text": {"maxCharacters": max_chars}},
            )
            resp.raise_for_status()
            return resp.json()

    try:
        data = await cached("web", {"provider": "exa_contents", "url": url}, do_fetch)
    except httpx.HTTPError:
        return None
    results = (data or {}).get("results") or []
    return (results[0].get("text") or None) if results else None


async def fetch(url: str, max_chars: int = 12000) -> str | None:
    """Page text for spec extraction: Ollama web_fetch (worked on Amazon) -> Exa /contents."""
    if _blocked_host(url):
        return None
    text = await _ollama_fetch(url)
    if not text:
        text = await _exa_fetch(url, max_chars)
    if not text:
        return None
    return text[:max_chars]


# --- condensing, for LLM prompts ----------------------------------------------------------


def _clean_snippet(text: str, max_chars: int) -> str:
    kept = []
    for line in text.splitlines():
        line = " ".join(line.split())  # collapse whitespace
        if not line or len(line) < 25 or any(w in line.lower() for w in _NAV_WORDS):
            continue
        kept.append(line)
    snippet = " ".join(kept) if kept else " ".join(text.split())
    return snippet[:max_chars]


def condense(hits: list[Hit], per_hit_chars: int = 400) -> str:
    """Numbered compact text block for LLM prompts: `[i] title — url\\n snippet`."""
    blocks = [
        f"[{i}] {hit.title} — {hit.url}\n{_clean_snippet(hit.text, per_hit_chars)}"
        for i, hit in enumerate(hits, 1)
    ]
    return "\n".join(blocks)


# --- SSRF guard ----------------------------------------------------------------------------
#
# assets.fetch_image / assets._try_cad / url_alive all fetch a URL that came out of web/LLM
# output (a candidate's spec_url, an SerpApi/LLM-sourced image URL, an LLM-claimed CAD
# download) -- never trust it to be a public internet host. `is_public_url` DNS-resolves and
# checks every address; `safe_get` re-checks on every redirect hop instead of handing
# `follow_redirects=True` a free pass past the first check.

_MAX_REDIRECTS = 3


def is_public_url(url: str) -> bool:
    """http(s) only, and every address the host resolves to must be a routable public address
    -- rejects private/loopback/link-local/multicast/reserved/unspecified (RFC1918, 169.254/16,
    127/8, ::1, etc). Blocking (does real DNS resolution) -- call via `asyncio.to_thread`."""
    parsed = urlparse(url)
    if parsed.scheme.lower() not in ("http", "https") or not parsed.hostname:
        return False
    try:
        infos = socket.getaddrinfo(parsed.hostname, None)
    except socket.gaierror:
        return False
    for *_rest, sockaddr in infos:
        ip = ipaddress.ip_address(sockaddr[0])
        if (
            ip.is_private
            or ip.is_loopback
            or ip.is_link_local
            or ip.is_multicast
            or ip.is_reserved
            or ip.is_unspecified
        ):
            return False
    return True


async def safe_get(
    url: str, *, method: str = "GET", timeout: float = 20, max_redirects: int = _MAX_REDIRECTS
) -> httpx.Response | None:
    """`method` request to `url`, manually following up to `max_redirects` redirects and
    re-checking `is_public_url` before every hop -- never `follow_redirects=True`, which would
    skip that check on the redirect target. None if the url (or any redirect hop) isn't a
    public http(s) host, on a request error, or on a redirect loop past `max_redirects`.
    """
    current = url
    async with httpx.AsyncClient(follow_redirects=False, timeout=timeout) as client:
        for _ in range(max_redirects + 1):
            if not await asyncio.to_thread(is_public_url, current):
                logger.info("safe_get: rejecting non-public url %s", current)
                return None
            try:
                resp = await client.request(method, current)
            except httpx.HTTPError as exc:
                logger.info("safe_get: %s %s failed: %s", method, current, exc)
                return None
            if resp.is_redirect and "location" in resp.headers:
                current = urljoin(str(resp.url), resp.headers["location"])
                continue
            return resp
    logger.info("safe_get: too many redirects for %s", url)
    return None


# --- URL liveness ------------------------------------------------------------------------


async def url_alive(url: str) -> bool:
    """HEAD (falling back to a GET on 405/403), cached for a day.

    Dead only on 404/410 or a DNS/connect/SSRF-rejected failure -- a 403 from bot protection
    still counts as alive, since that's the common outcome for Home Depot/Lowe's/Amazon
    product pages.
    """

    async def check() -> bool:
        resp = await safe_get(url, method="HEAD", timeout=6)
        if resp is None:
            return False
        status = resp.status_code
        if status in (405, 403):
            resp = await safe_get(url, method="GET", timeout=6)
            if resp is None:
                return False
            status = resp.status_code
        return status not in (404, 410)

    return await cached("web", {"provider": "url_alive", "url": url}, check, ttl_s=86400)
