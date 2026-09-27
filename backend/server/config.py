import os
import re
from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    # extra="allow": numbered fallback keys (SERP_API_KEY_2, HF_TOKEN_3, ...) land in
    # `model_extra` for `keys()` without a field per number.
    model_config = SettingsConfigDict(env_file=".env", extra="allow")

    GROQ_API_KEY: str | None = None
    DEEPSEEK_API_KEY: str | None = None
    GROK_API_KEY: str | None = None
    SERP_API_KEY: str | None = None
    EXA_API_KEY: str | None = None
    TAVILY_API_KEY: str | None = None
    FIRECRAWL_API_KEY: str | None = None
    YOUDCOM_API_KEY: str | None = None
    OLLAMA_WEB_API_KEY: str | None = None

    CYBERSOURCE_MERCHANT_ID: str | None = None
    CYBERSOURCE_KEY_ID: str | None = None
    CYBERSOURCE_SECRET_KEY: str | None = None  # base64 shared secret
    # Measured mandate (brain.md S2): true = POST /checkout refuses a request without the hold
    # proof from /checkout/prepare. Off by default so an app build that predates the proof can
    # still check out; a request that does carry a proof is always verified.
    REQUIRE_HOLD_PROOF: bool = False

    HF_TOKEN: str | None = None  # optional, raises HF Spaces anon GPU quota
    # asset-mode: the HF warm queue (server/hf_warm.py) makes Hunyuan3D meshes for searched and
    # catalog parts ahead of need, one at a time, backing off on a ZeroGPU quota error. Off = only
    # on demand (an "hf" request). Never offline.
    HF_WARM: bool = True

    DATA_DIR: str = "./data"
    SCENE_DIR: str = "./scene"  # scene packages served read-only at GET /scenes/<site>/<file>

    # Cache-only demo mode (docs/design.md "offline"): every paid/network call becomes
    # cache-hit-or-degrade. See server/cache.py's `cached()` and server/warm.py.
    OFFLINE: bool = False

    # Per-role LLM provider/model override, format "provider:model" (e.g. "xai:grok-4.7").
    # Unset roles fall back to server.llm.ROLE_DEFAULTS.
    LLM_AGENT: str | None = None
    LLM_EXTRACT: str | None = None
    LLM_SEARCH: str | None = None
    LLM_CHEAP: str | None = None
    LLM_HARD: str | None = None
    LLM_VISION: str | None = None
    LLM_ASSET: str | None = None
    LLM_ASSET_SCAD: str | None = None
    # cad: Grok's OpenSCAD loop (server/meshgen.py scad_budget): parallel first answers and
    # compile-fix rounds. None = 1 answer; 2 fix rounds for a non-reasoning model, else 1.
    ASSET_SCAD_SAMPLES: int | None = None
    ASSET_SCAD_FIXES: int | None = None
    # cad: reasoning effort for the OpenSCAD writer (LLM_ASSET_SCAD, default xai:grok-4.7):
    # "low" (~45-55 s, ~$0.03 a part, measured) | "medium" | "high"; "" = the model's own default
    # (grok-4.7: ~280 s, ~$0.14). Dropped for models that take none (grok-4.20).
    ASSET_SCAD_EFFORT: str | None = "low"
    # cad: the product photo on the CAD model's front, like the other tiers (true), or Grok's own
    # geometry and paint only (false: the CAD model as written, e.g. to compare generators; a
    # photo over CAD relief can draw a window's bars twice). Variants made before a change stay.
    ASSET_SCAD_PHOTO: bool = True
    # texture: the product photo on every tier's model (server/meshgen.py project, photo_cut.py).
    # ASSET_PHOTO_CUT: the product cut out of its photo (studio background removed, a showroom's
    # neighbours dropped, a slight keystone straightened) and fitted to the model's front footprint,
    # front-facing triangles only; off = the whole crop stretched over the bbox face (as before).
    # ASSET_SIDE_COLOR: the sides and top in the product's sampled body colour with a material cue
    # (slightly metallic for stainless, matte for plastic); off = grey / the photo's edge colour.
    # ASSET_SCAD_ORIENT: a CAD model whose back shows the front's detail (or matches the photo) is
    # turned 180 degrees about Y (meshgen.orientation). Variants made before a change stay until
    # `python -m server.warm --retexture`.
    ASSET_PHOTO_CUT: bool = True
    ASSET_SIDE_COLOR: bool = True
    ASSET_SCAD_ORIENT: bool = True
    # assetgen: a poor `asset` answer (none, or a known shape -- a window -- answered with another
    # template) is asked once more here (server/meshgen.py ask). "" turns the retry off.
    LLM_ASSET_RETRY: str | None = "xai:grok-4.20-0309-non-reasoning"
    # assetgen: the search's page-text dims extraction (role `cheap`) found no complete size: one
    # more try here (server/search.py _extract_dims). "" turns it off.
    LLM_EXTRACT_RETRY: str | None = "xai:grok-4.20-0309-non-reasoning"
    LLM_REPORT: str | None = None  # site-walk report summary (server/report.py)
    LLM_PLAN: str | None = None
    LLM_SURVEY: str | None = None
    LLM_SURVEY_CAREFUL: str | None = None
    LLM_COACH: str | None = None
    LLM_COACH_VISION: str | None = None  # install coach step checks (server/coach.py)
    LLM_PACKET: str | None = None  # job packet blurbs (server/packet.py), xai only
    LLM_LABELS: str | None = None
    LLM_BOOTH: str | None = None  # booth wall caption (server/booth.py)
    LLM_CATALOG: str | None = None  # catalog: the kind of place a scan is (server/catalog.py)
    # catalog: candidates per background category search (server/catalog.py); each one costs
    # about one SerpApi call (catalog searches skip the per-candidate store lookups).
    CATALOG_SEARCH_N: int = 4

    # Optional X post from the booth wall (server/booth.py): an OAuth 2.0 confidential client
    # and the bot account's refresh token (from GET /booth/x/login). Unset = no X, wall only.
    X_CLIENT_ID: str | None = None
    X_CLIENT_SECRET: str | None = None
    X_REFRESH_TOKEN: str | None = None
    LLM_QUOTE: str | None = None  # paper-quote reader (server/quote.py), vision + json_schema
    # When a Groq call can't be answered (every key rate limited, unreachable, 5xx), the same
    # call goes here instead (server/llm.py chat()); needs GROK_API_KEY. "" turns it off.
    LLM_FALLBACK: str = "xai:grok-4.20-0309-non-reasoning"

    # Groq speech models (docs/research/llm-providers.md §3), overridable per deployment.
    STT_MODEL: str = "whisper-large-v3-turbo"
    TTS_MODEL: str = "canopylabs/orpheus-v1-english"
    EDGE_TTS_VOICE: str = "en-GB-RyanNeural"  # fallback TTS voice
    TTS_VOICE: str = "troy"

    # xAI realtime voice relay (WS /voice/realtime, server/realtime.py). 28 built-in voices, see
    # docs/research/grok-ideas/g2-voice-agents.md §1; REALTIME_MAX_S caps one session's cost.
    # The report narration (POST /report/{id}/narrate) speaks in the same XAI_VOICE.
    XAI_VOICE: str = "rex"
    XAI_REALTIME_MODEL: str = "grok-voice-latest"
    REALTIME_MAX_S: float = 600

    # Where "who installs this near me?" (server/intel.py) searches, and where the
    # rules check (server/rules.py) looks, when there's no address or location.
    DEFAULT_LOCATION: str = "Atlanta, GA"

    def keys(self, name: str) -> list[str]:
        """`NAME`, then `NAME_2`, `NAME_3`, ... in number order (from .env or the environment),
        non-empty and deduped -- the rotation order for `server.keys.KeyPool`."""
        numbered: dict[int, str] = {}
        pattern = re.compile(rf"^{re.escape(name)}_(\d+)$", re.IGNORECASE)
        for source in (self.model_extra or {}, os.environ):
            for key, value in source.items():
                m = pattern.match(key)
                if m and value:
                    numbered.setdefault(int(m.group(1)), value)
        base = (
            getattr(self, name, None)
            or (self.model_extra or {}).get(name.lower())
            or os.environ.get(name)
        )
        ordered = [base, *(numbered[n] for n in sorted(numbered))]
        return list(dict.fromkeys(v for v in ordered if v))


@lru_cache
def get_settings() -> Settings:
    return Settings()
