import logging

import httpx
import pytest
import respx

from server import assets, keys, sellers
from server.config import get_settings
from server.keys import KeyPool, KeysExhausted


@pytest.fixture(autouse=True)
def _data_dir(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))  # never hit or write the real disk cache
    get_settings.cache_clear()


class Limited(Exception):
    pass


def _pool(**kw):
    return KeyPool("TEST_KEY", is_limited=lambda e: isinstance(e, Limited), cooldown_s=60, **kw)


@pytest.fixture
def three_keys(monkeypatch):
    monkeypatch.setenv("TEST_KEY", "k1")
    monkeypatch.setenv("TEST_KEY_3", "k3")  # gaps are fine, order is by number
    monkeypatch.setenv("TEST_KEY_2", "k2")
    monkeypatch.setenv("TEST_KEY_4", "")  # empty = absent
    get_settings.cache_clear()


def test_settings_keys_order_skips_empty_and_dupes(three_keys, monkeypatch):
    monkeypatch.setenv("TEST_KEY_5", "k1")
    get_settings.cache_clear()
    assert get_settings().keys("TEST_KEY") == ["k1", "k2", "k3"]
    assert get_settings().keys("MISSING_KEY") == []


@pytest.mark.asyncio
async def test_rotates_on_limit_and_remembers_cooldown(three_keys, caplog):
    pool, tried = _pool(), []

    async def fn(key):
        tried.append(key)
        if key in ("k1", "k2"):
            raise Limited()
        return key

    with caplog.at_level(logging.WARNING):
        assert await pool.call(fn) == "k3"
        assert await pool.call(fn) == "k3"  # k1/k2 still cooling: not retried
    assert tried == ["k1", "k2", "k3", "k3"]
    assert "k1" not in caplog.text and "TEST_KEY #1" in caplog.text


@pytest.mark.asyncio
async def test_other_errors_raise_without_rotating(three_keys):
    tried = []

    async def fn(key):
        tried.append(key)
        raise ValueError("bad request")

    with pytest.raises(ValueError):
        await _pool().call(fn)
    assert tried == ["k1"]


@pytest.mark.asyncio
async def test_all_limited_raises_exhausted_then_reset_retries(three_keys):
    pool = _pool()

    async def fn(key):
        raise Limited()

    with pytest.raises(KeysExhausted, match="all 3 keys"):
        await pool.call(fn)
    with pytest.raises(KeysExhausted):
        await pool.call(fn)
    keys.reset_all()

    async def ok(key):
        return key

    assert await pool.call(ok) == "k1"


@pytest.mark.asyncio
async def test_anonymous_last_and_no_keys():
    tried = []

    async def fn(key):
        tried.append(key)
        return "anon" if key is None else key

    assert await _pool(anonymous_last=True).call(fn) == "anon"
    assert tried == [None]
    with pytest.raises(KeysExhausted, match="no keys configured"):
        await _pool().call(fn)


@pytest.mark.asyncio
async def test_serpapi_falls_back_to_second_key_on_429(monkeypatch, caplog):
    monkeypatch.setenv("SERP_API_KEY", "serp-one")
    monkeypatch.setenv("SERP_API_KEY_2", "serp-two")
    get_settings.cache_clear()

    def respond(request):
        if request.url.params["api_key"] == "serp-one":
            return httpx.Response(429, json={"error": "Your account has run out of searches."})
        return httpx.Response(200, json={"shopping_results": []})

    with caplog.at_level(logging.DEBUG), respx.mock() as router:
        route = router.get(sellers.BASE_URL).mock(side_effect=respond)
        assert await sellers._serpapi_get({"engine": "google_shopping", "q": "hanger"}) == {
            "shopping_results": []
        }
    assert [c.request.url.params["api_key"] for c in route.calls] == ["serp-one", "serp-two"]
    assert "serp-one" not in caplog.text and "serp-two" not in caplog.text


@pytest.mark.asyncio
async def test_ai_mesh_moves_to_next_hf_token_on_quota(monkeypatch, tmp_path):
    monkeypatch.setenv("HF_TOKEN", "hf-one")
    monkeypatch.setenv("HF_TOKEN_2", "hf-two")
    get_settings.cache_clear()
    tokens = []

    def fake_generation(space_id, image_path, token):
        tokens.append(token)
        if token == "hf-one":
            raise RuntimeError("You have exceeded your free ZeroGPU quota (90s requested).")
        return b"glb-bytes"

    monkeypatch.setattr(assets, "_hunyuan_shape_generation", fake_generation)
    out = await assets.ai_mesh(tmp_path / "image.jpg")
    assert out is not None and out.read_bytes() == b"glb-bytes"
    out.unlink()
    assert tokens == ["hf-one", "hf-two"]


@pytest.mark.asyncio
async def test_llm_chat_moves_to_next_groq_key_on_429_per_model(monkeypatch):
    from server import llm

    monkeypatch.setenv("GROQ_API_KEY", "groq-one")
    monkeypatch.setenv("GROQ_API_KEY_2", "groq-two")
    get_settings.cache_clear()
    ok = {
        "id": "x",
        "object": "chat.completion",
        "created": 0,
        "model": "m",
        "choices": [
            {"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": "hi"}}
        ],
    }

    def respond(request):
        if request.headers["authorization"] == "Bearer groq-one":
            # daily cap on key one: the SDK retries it (retry-after-ms keeps that instant),
            # then the pool moves on
            return httpx.Response(
                429,
                headers={"retry-after-ms": "1"},
                json={"error": {"message": "tokens per day (TPD)"}},
            )
        return httpx.Response(200, json=ok)

    with respx.mock() as router:
        route = router.post("https://api.groq.com/openai/v1/chat/completions").mock(
            side_effect=respond
        )
        resp = await llm.chat("vision", [{"role": "user", "content": "hi"}])
        assert resp.choices[0].message.content == "hi"
        used = [c.request.headers["authorization"] for c in route.calls]
        assert used[-1] == "Bearer groq-two" and "Bearer groq-one" in used
        # key one cools only for the vision model; the text model still starts with it
        route.calls.reset()
        await llm.chat("agent", [{"role": "user", "content": "hi"}])
        assert route.calls[0].request.headers["authorization"] == "Bearer groq-one"
