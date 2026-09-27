import ipaddress
import json
import socket
from pathlib import Path

import httpx
import pytest
import respx

from server import web
from server.config import get_settings

FIXTURES = Path(__file__).parent / "fixtures" / "search"


def _load(name: str) -> dict:
    return json.loads((FIXTURES / f"{name}.json").read_text())


def _stub_getaddrinfo(monkeypatch, ip: str):
    """Stub `socket.getaddrinfo`: a literal IP host resolves to itself (matching real
    getaddrinfo, no DNS needed -- so the 10.x/127.0.0.1/::1 SSRF cases stay rejected), any
    other (name) host resolves to `ip`."""

    def fake(host, *args, **kwargs):
        try:
            target = str(ipaddress.ip_address(host))
        except ValueError:
            target = ip
        family = socket.AF_INET6 if ":" in target else socket.AF_INET
        return [(family, socket.SOCK_STREAM, 6, "", (target, 0))]

    monkeypatch.setattr(socket, "getaddrinfo", fake)


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    yield tmp_path
    get_settings.cache_clear()


@pytest.fixture(autouse=True)
def _resolve_example_com_as_public(monkeypatch):
    """`url_alive` now DNS-checks every hop via `is_public_url` -- stub the resolver so the
    example.com-based tests below don't need (or risk) a real DNS lookup. SSRF-specific tests
    further down override this per-test via their own `monkeypatch.setattr`."""
    _stub_getaddrinfo(monkeypatch, "93.184.216.34")


# --- search: provider chain ----------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_uses_exa_and_parses_hits():
    fixture = _load("exa_search")
    with respx.mock(assert_all_called=True) as router:
        router.post("https://api.exa.ai/search").mock(
            return_value=httpx.Response(200, json=fixture)
        )
        hits = await web.search("Amerimax 5 in. K-style hidden gutter hanger", n=5)

    assert len(hits) == len(fixture["results"])
    first = hits[0]
    assert first.source == "exa"
    assert first.title == fixture["results"][0]["title"]
    assert first.url == fixture["results"][0]["url"]
    assert first.text == fixture["results"][0]["text"]


@pytest.mark.asyncio
async def test_search_falls_back_to_youdotcom_when_exa_500s():
    you_fixture = _load("youdotcom_search")
    with respx.mock(assert_all_called=True) as router:
        router.post("https://api.exa.ai/search").mock(return_value=httpx.Response(500))
        router.post("https://ydc-index.io/v1/search").mock(
            return_value=httpx.Response(200, json=you_fixture)
        )
        hits = await web.search("Amerimax 5 in. K-style hidden gutter hanger buy", n=5)

    assert hits, "expected You.com fallback hits"
    assert all(h.source == "you" for h in hits)
    web_results = you_fixture["results"]["web"]
    assert len(hits) == len(web_results)
    assert hits[0].url == web_results[0]["url"]
    assert hits[0].title == web_results[0]["title"]


@pytest.mark.asyncio
async def test_search_falls_back_through_tavily(monkeypatch):
    monkeypatch.setenv("YOUDCOM_API_KEY", "")
    get_settings.cache_clear()
    tavily_fixture = _load("tavily_search")
    with respx.mock(assert_all_called=True) as router:
        router.post("https://api.exa.ai/search").mock(return_value=httpx.Response(500))
        router.post("https://api.tavily.com/search").mock(
            return_value=httpx.Response(200, json=tavily_fixture)
        )
        hits = await web.search("Amerimax 5 in. K-style hidden gutter hanger buy", n=5)

    assert hits and all(h.source == "tavily" for h in hits)
    assert hits[0].url == tavily_fixture["results"][0]["url"]
    assert hits[0].text == tavily_fixture["results"][0]["content"]


@pytest.mark.asyncio
async def test_search_falls_back_to_ollama_when_all_else_empty(monkeypatch):
    monkeypatch.setenv("YOUDCOM_API_KEY", "")
    monkeypatch.setenv("TAVILY_API_KEY", "")
    get_settings.cache_clear()
    ollama_fixture = _load("ollama_web_search")
    with respx.mock(assert_all_called=True) as router:
        router.post("https://api.exa.ai/search").mock(return_value=httpx.Response(500))
        router.post("https://ollama.com/api/web_search").mock(
            return_value=httpx.Response(200, json=ollama_fixture)
        )
        hits = await web.search("Amerimax 5 in. K-style hidden gutter hanger", n=5)

    assert hits and all(h.source == "ollama" for h in hits)
    assert hits[0].text == ollama_fixture["results"][0]["content"]


@pytest.mark.asyncio
async def test_search_missing_key_skips_provider(monkeypatch):
    monkeypatch.setenv("EXA_API_KEY", "")
    get_settings.cache_clear()
    you_fixture = _load("youdotcom_search")
    with respx.mock(assert_all_called=True) as router:
        router.post("https://ydc-index.io/v1/search").mock(
            return_value=httpx.Response(200, json=you_fixture)
        )
        hits = await web.search("Amerimax 5 in. K-style hidden gutter hanger", n=5)

    assert hits and hits[0].source == "you"


@pytest.mark.asyncio
async def test_search_no_provider_returns_empty(monkeypatch):
    for key in ("EXA_API_KEY", "YOUDCOM_API_KEY", "TAVILY_API_KEY", "OLLAMA_WEB_API_KEY"):
        monkeypatch.setenv(key, "")
    get_settings.cache_clear()

    with respx.mock():  # no routes registered -- any escaped call fails loudly
        hits = await web.search("nothing configured", n=5)

    assert hits == []


# --- fetch -----------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_fetch_skips_homedepot():
    with respx.mock(assert_all_called=True) as router:
        text = await web.fetch("https://www.homedepot.com/p/some-product/12345")

    assert text is None
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_fetch_skips_lowes():
    with respx.mock(assert_all_called=True) as router:
        text = await web.fetch("https://www.lowes.com/pd/some-product/12345")

    assert text is None
    assert router.calls.call_count == 0


@pytest.mark.asyncio
async def test_fetch_parses_ollama_web_fetch_fixture():
    fixture = _load("ollama_web_fetch_lowes_amazon")
    amazon = fixture["amazon_ac"]
    with respx.mock(assert_all_called=True) as router:
        router.post("https://ollama.com/api/web_fetch").mock(
            return_value=httpx.Response(amazon["status_code"], json=amazon["response"])
        )
        text = await web.fetch("https://www.amazon.com/dp/B0CKZWMWYB", max_chars=500)

    assert text is not None
    assert text == amazon["response"]["content"][:500]
    assert len(text) <= 500


@pytest.mark.asyncio
async def test_fetch_falls_back_to_exa_contents_when_ollama_404s():
    exa_fixture = _load("exa_contents_homedepot")  # reuse as generic contents-shaped fixture
    with respx.mock(assert_all_called=True) as router:
        router.post("https://ollama.com/api/web_fetch").mock(
            return_value=httpx.Response(404, json={"error": "not found"})
        )
        router.post("https://api.exa.ai/contents").mock(
            return_value=httpx.Response(200, json=exa_fixture)
        )
        text = await web.fetch("https://www.dunnlumber.com/some-product")

    assert text == exa_fixture["results"][0]["text"]


@pytest.mark.asyncio
async def test_fetch_returns_none_on_total_failure(monkeypatch):
    monkeypatch.setenv("OLLAMA_WEB_API_KEY", "")
    monkeypatch.setenv("EXA_API_KEY", "")
    get_settings.cache_clear()

    with respx.mock():  # no routes registered -- any escaped call fails loudly
        text = await web.fetch("https://www.dunnlumber.com/some-product")

    assert text is None


# --- condense --------------------------------------------------------------------------


def test_condense_shape_and_length():
    hits = [
        web.Hit(
            title="5 in. Aluminum Hidden Gutter Hanger with Screw",
            url="https://example.com/p/1",
            text=(
                "Shop All\nLog In\nCart\n"
                "Amerimax Home Products 5 in. Aluminum Hidden Gutter Hanger with Screw 21812\n"
                "Secures gutter to fascia board with 1/4 in hex head screw, fast easy install.\n"
                "Privacy & Security Center\n"
            ),
            source="exa",
        ),
        web.Hit(title="Second hit", url="https://example.com/p/2", text="short", source="tavily"),
    ]

    out = web.condense(hits, per_hit_chars=40)

    lines = out.split("\n")
    assert (
        lines[0] == "[1] 5 in. Aluminum Hidden Gutter Hanger with Screw — https://example.com/p/1"
    )
    assert "Shop All" not in out  # nav line dropped
    assert "Log In" not in out
    # snippet for hit 1 respects per_hit_chars
    snippet_line_1 = lines[1]
    assert len(snippet_line_1) <= 40
    assert lines[2] == "[2] Second hit — https://example.com/p/2"
    assert lines[3] == "short"  # no matching lines survive the nav filter -> raw fallback


def test_condense_empty_hits():
    assert web.condense([]) == ""


# --- url_alive -------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_url_alive_404_is_false():
    with respx.mock(assert_all_called=True) as router:
        router.head("https://example.com/dead").mock(return_value=httpx.Response(404))
        alive = await web.url_alive("https://example.com/dead")

    assert alive is False


@pytest.mark.asyncio
async def test_url_alive_403_is_true_after_get_fallback():
    with respx.mock(assert_all_called=True) as router:
        router.head("https://example.com/blocked").mock(return_value=httpx.Response(403))
        router.get("https://example.com/blocked").mock(return_value=httpx.Response(403))
        alive = await web.url_alive("https://example.com/blocked")

    assert alive is True


@pytest.mark.asyncio
async def test_url_alive_connect_error_is_false():
    with respx.mock(assert_all_called=True) as router:
        router.head("https://example.com/unreachable").mock(
            side_effect=httpx.ConnectError("no route to host")
        )
        alive = await web.url_alive("https://example.com/unreachable")

    assert alive is False


@pytest.mark.asyncio
async def test_url_alive_caches_result(tmp_path):
    with respx.mock(assert_all_called=True) as router:
        route = router.head("https://example.com/ok").mock(return_value=httpx.Response(200))
        first = await web.url_alive("https://example.com/ok")
        second = await web.url_alive("https://example.com/ok")

    assert first is True
    assert second is True
    assert route.call_count == 1  # second call served from disk cache


# --- SSRF guard: is_public_url / safe_get -----------------------------------------------


@pytest.mark.parametrize(
    "url",
    [
        "http://169.254.169.254/latest/meta-data/",  # cloud metadata endpoint
        "http://10.1.2.3/",  # RFC1918 private
        "http://127.0.0.1/",  # loopback
        "http://[::1]/",  # IPv6 loopback
        "file:///etc/passwd",  # non-http(s) scheme
    ],
)
def test_is_public_url_rejects_non_public_targets(url):
    assert web.is_public_url(url) is False


def test_is_public_url_accepts_public_host(monkeypatch):
    _stub_getaddrinfo(monkeypatch, "93.184.216.34")
    assert web.is_public_url("https://example.com/page") is True


@pytest.mark.asyncio
async def test_safe_get_passes_public_host(monkeypatch):
    _stub_getaddrinfo(monkeypatch, "93.184.216.34")
    with respx.mock(assert_all_called=True) as router:
        router.get("https://example.com/ok").mock(return_value=httpx.Response(200, text="hi"))
        resp = await web.safe_get("https://example.com/ok")

    assert resp is not None
    assert resp.status_code == 200


@pytest.mark.asyncio
async def test_safe_get_rejects_redirect_to_private_hop(monkeypatch):
    """The first hop resolves public and passes; the redirect target is a literal private/
    link-local address (cloud metadata) -- `safe_get` must re-check it, not just the origin."""
    _stub_getaddrinfo(monkeypatch, "93.184.216.34")
    with respx.mock(assert_all_called=True) as router:
        router.get("https://public.example/start").mock(
            return_value=httpx.Response(
                302, headers={"location": "http://169.254.169.254/latest/meta-data/"}
            )
        )
        resp = await web.safe_get("https://public.example/start")

    assert resp is None
