"""Shared contract types. part.json (plan §1b) is `Part.model_dump_json()`."""

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field, field_validator


class Dims(BaseModel):
    """Millimetres. w = width (left-right), d = depth (out of the mounting face), h = height."""

    w: float
    d: float
    h: float

    @field_validator("w", "d", "h")
    @classmethod
    def _round(cls, v: float) -> float:
        return round(v, 2)  # inch conversions leave float noise (90.42399999)


class Measurement(BaseModel):
    """A tape reading from the headset, sent along with a search."""

    label: str = ""  # e.g. "window width", "gutter run"
    value_m: float
    axis: Literal["w", "d", "h", "length"] | None = None  # which part dim it constrains


class Seller(BaseModel):
    name: str
    title: str | None = None  # the listing/store's own title, e.g. "(50-Pack)"
    price_usd: float | None = None
    pack_qty: int = 1  # units per listing, parsed from `title` (parse_pack_qty)
    unit_price_usd: float | None = None  # price_usd / pack_qty
    shipping_usd: float | None = None  # 0 = free, None = unknown
    shipping: str | None = None  # raw text, e.g. "Free delivery", "+ $20.00"
    total_usd: float | None = None
    eta: str | None = None  # human text, e.g. "Sep 26" or "Sep 25 - Oct 1"
    eta_days: int | None = None  # latest estimated arrival, days from fetch
    rating: float | None = None
    reviews: int | None = None
    in_stock: bool | None = None
    url: str | None = None
    verified: bool = False  # came from a structured seller API, not an LLM


class Finish(BaseModel):
    name: str
    hex: str | None = None
    part_id: str | None = None


class Asset(BaseModel):
    tier: Literal["cad", "library", "llm", "scad", "ai_mesh", "proxy"] = "proxy"
    source_url: str | None = None
    license: str | None = None
    scale_residual_pct: float | None = None
    status: Literal["pending", "ready", "failed"] = "pending"
    # assetgen: who made it and how long it took (logged by the headset): the provider:model that
    # answered the template call ("groq:qwen/qwen3.8-27b", "xai:grok-4.20-0309-non-reasoning"),
    # the template it picked, whether the Grok retry made it, and the seconds from start to GLB.
    made_by: str | None = None
    template: str | None = None
    retried: bool | None = None
    seconds: float | None = None
    # asset-mode: the generator preference that made this model ("auto" | "hf" | "llm_scad"), and
    # why the preferred tier didn't make it when it didn't (no OpenSCAD on the server, the HF
    # ZeroGPU quota spent, no product photo). None from older servers.
    mode: str | None = None
    note: str | None = None
    # cad: for an "llm_scad" answer, how Grok's CAD model is doing (server/assets.py cad_status):
    # {status: "writing" | "ready" | "failed" | "none", started_at, eta_s, made_by, reason}.
    # Only on responses (part.json?mode=, POST /parts/{id}/model); never stored.
    cad: dict | None = None
    # texture: how the model was textured (server/assets.py _textured): {cut: "flood" | "u2netp" |
    # "frame" | "whole", background, keystone, photo (projected), front_tris, body, trim, finish,
    # recolored: [pieces], turned (a CAD model turned around), retextured_at}.
    texture: dict | None = None


class Fit(BaseModel):
    """Server-side fit against the measurement that came with the search."""

    status: Literal["fits", "too_big", "too_small", "unknown"] = "unknown"
    spare_mm: float | None = None  # negative = too big by that much
    axis: str | None = None
    note: str = ""


class Part(BaseModel):
    id: str
    name: str
    manufacturer: str | None = None
    model_no: str | None = None
    dims_mm: Dims
    dims_source: str = "llm"  # "home_depot", "page", "llm", "title" (sizes printed in the title)
    weight_g: float | None = None
    color_hex: str | None = None
    finish: str | None = None
    material: str | None = None
    finishes: list[Finish] = []
    mount: dict = Field(default_factory=lambda: {"face": "-z", "surface": None})
    clearance_mm: dict = Field(default_factory=dict)
    spacing_mm: float | None = None
    fit_range_mm: dict | None = None  # published {"min": mm, "max": mm}, e.g. a window opening
    asset: Asset = Field(default_factory=Asset)
    image_url: str | None = None
    spec_url: str | None = None
    citations: list[str] = []
    sellers: list[Seller] = []
    sellers_expanded: bool = False  # lazy expansion already attempted (search.expand_sellers)
    recommended_seller: int | None = None
    recommendation_reason: str | None = None
    fit: Fit = Field(default_factory=Fit)
    fetched_at: datetime | None = None
    cached: bool = False


class Cavity(BaseModel):
    """The gap a removed scene part leaves (the app's context `cavity`, e2e hand-off p4-e2e): its
    real size in metres (the parts file's `cavity.size_m` x the headset's scale calibration), and
    the headset's own tape readings in `measured` once it taped the gap ("measure it")."""

    w_m: float = Field(gt=0)
    h_m: float = Field(gt=0)
    d_m: float = Field(gt=0)
    component_id: str | None = None
    label: str | None = None
    estimated: bool = True
    measured: dict[str, float] | None = None

    def size_m(self) -> tuple[float, float, float]:
        """(w, h, d): the tape where it measured, else the file's."""
        m = self.measured or {}

        def pick(key: str, fallback: float) -> float:
            v = m.get(key)
            return float(v) if isinstance(v, (int, float)) and v > 0 else fallback

        return pick("w_m", self.w_m), pick("h_m", self.h_m), pick("d_m", self.d_m)

    def size_mm(self) -> tuple[float, float, float]:
        w, h, d = self.size_m()
        return w * 1000.0, h * 1000.0, d * 1000.0


class Opening(BaseModel):
    """assetgen: an opening the headset taped (a window, a door): its width and height tapes in
    real metres, the depth when taped. A part for it must fit the width and height (search
    `fit_to_opening`); a window frame is sized to it minus a standard clearance."""

    w_m: float = Field(gt=0)
    h_m: float = Field(gt=0)
    d_m: float | None = Field(default=None, gt=0)
    label: str | None = None
    kind: str | None = None  # "window", "door" when known

    def size_mm(self) -> tuple[float, float]:
        return self.w_m * 1000.0, self.h_m * 1000.0


class SearchRequest(BaseModel):
    query: str
    measurement: Measurement | None = None
    frame_jpg_b64: str | None = None
    max_candidates: int = 3
    # e2e: a part for a removed component's gap -- every candidate's fit is against all three
    # axes of it (search.find_parts), fitting ones first.
    cavity: Cavity | None = None
    # assetgen: a taped opening (a window's width x height): candidates are fitted to it and the
    # ones made for that size come first (search.fit_to_opening).
    opening: Opening | None = None
    # asset-mode: which generator makes the candidates' 3D models ("auto" | "hf" | "llm_scad",
    # server/assets.py); anything else is "auto".
    asset_mode: str = "auto"
    # catalog: a browse search (server/catalog.py) skips the per-candidate store lookups
    # (sellers.stores, up to 2 SerpApi calls each): the Home Depot record and the listings' own
    # sellers only. "Show sellers" still expands them later (search.expand_sellers).
    lite: bool = False


class Job(BaseModel):
    id: str
    status: Literal["queued", "running", "done", "failed"] = "queued"
    stage: str = ""  # human-readable progress, e.g. "searching the supply shops"
    query: str = ""
    candidates: list[Part] = []
    error: str | None = None
    result: dict | None = None  # non-search jobs (jobs.start_task): a flythrough, a rules check
