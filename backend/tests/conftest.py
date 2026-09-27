import socket

import pytest

from server import keys
from server.config import Settings, get_settings

_DUMMY_KEYS = [
    "GROQ_API_KEY",
    "DEEPSEEK_API_KEY",
    "GROK_API_KEY",
    "SERP_API_KEY",
    "EXA_API_KEY",
    "TAVILY_API_KEY",
    "FIRECRAWL_API_KEY",
    "YOUDCOM_API_KEY",
    "OLLAMA_WEB_API_KEY",
]


# Optional integrations: tests default to "not configured".
_UNSET_KEYS = [
    "CYBERSOURCE_MERCHANT_ID",
    "CYBERSOURCE_KEY_ID",
    "CYBERSOURCE_SECRET_KEY",
    "HF_TOKEN",
    "X_CLIENT_ID",
    "X_CLIENT_SECRET",
    "X_REFRESH_TOKEN",
    "LLM_FALLBACK",  # a Groq failure a test simulates stays a failure (tests opt in)
    "LLM_ASSET_RETRY",  # assetgen: no Grok retry of a poor asset answer (tests opt in)
    "LLM_EXTRACT_RETRY",  # assetgen: nor of an incomplete dims extraction
]


@pytest.fixture(autouse=True)
def _no_network_by_default(request, monkeypatch):
    """Offline tests never see real keys from .env; `live` tests use the real .env keys."""
    if request.node.get_closest_marker("live"):
        get_settings.cache_clear()
        yield
        get_settings.cache_clear()
        return
    # Never read .env: its numbered fallback keys (SERP_API_KEY_2, ...) can't be blanked by env.
    monkeypatch.setitem(Settings.model_config, "env_file", None)
    keys.reset_all()
    for key in _DUMMY_KEYS:
        monkeypatch.setenv(key, "test-dummy-key")
    for key in _UNSET_KEYS:  # empty beats a real value in .env
        monkeypatch.setenv(key, "")
    monkeypatch.setenv("HF_WARM", "false")  # asset-mode: no background Hunyuan queue (tests opt in)
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


@pytest.fixture(autouse=True)
def _block_real_network(request, monkeypatch):
    """Offline tests must never reach the internet (respx/monkeypatch work above the socket).
    `live` tests are exempt. Local sockets (event loop self-pipes, TestClient) stay allowed."""
    if request.node.get_closest_marker("live"):
        return
    real_connect = socket.socket.connect

    def guarded(sock, address):
        if sock.family == socket.AF_UNIX or (
            isinstance(address, tuple) and address[0] in ("127.0.0.1", "::1", "localhost")
        ):
            return real_connect(sock, address)
        raise RuntimeError(f"offline test tried to reach the network: {address}")

    monkeypatch.setattr(socket.socket, "connect", guarded)
