"""Install-manual Q&A: find the manufacturer's install PDF for a part, then answer install
questions ("what size drill bit for the anchors?") from its text, with a page citation.

1. `index(part)`: one xAI Responses call with `web_search` limited to the manufacturer's
   domain(s) lists candidate PDF URLs; we download them in order and keep the first real PDF
   with extractable text (preferring one that names the model). The PDF lands next to part.json
   (`data/parts/<id>/manual.pdf`, served at `GET /parts/<id>/manual.pdf`), the page texts in the
   cache.
2. `ask(part, question)`: the pages that best match the question go to one Grok call (strict
   json_schema, no tools). xAI Collections would do the retrieval for us, but creating one needs
   a Management API key (our API key gets 401 on management-api.x.ai), so retrieval is local.

Honesty rules, enforced here, not by the prompt: the quote must be found verbatim (ignoring
case, spacing and punctuation) in the manual's text, and its page is where we found it. A quote
we can't find, or a "not covered" reply, becomes a plain "the manual doesn't cover that".
"""

import asyncio
import io
import logging
import math
import re
import unicodedata
from typing import Any
from urllib.parse import urlparse

import httpx
import pydantic
import pypdf

from server import assets, cache, llm
from server.config import get_settings
from server.models import Part

logger = logging.getLogger(__name__)
logging.getLogger("pypdf").setLevel(logging.ERROR)  # font-encoding chatter on every page

MODEL = "grok-4.20-0309-non-reasoning"  # fast + cheap; strict json_schema works with web_search
MAX_CANDIDATES = 4  # PDFs we try to download per part
MAX_PDF_BYTES = 40_000_000
MAX_PAGES = 400  # a whole-line catalogue (Simpson) is ~300 pages
MAX_CONTEXT_CHARS = 40_000  # ~10k tokens of manual per question
MIN_QUOTE_CHARS = (
    8  # letters/digits; table rows are short ("9.8 / 24.6 / 49.2"), "16d" proves nothing
)
TTL_S = 7 * 86400  # a "no manual found" retries after a week (or with refresh)
USER_AGENT = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140"

# Manufacturer name -> the domains its manuals live on, where the name alone guesses wrong.
# Anything else: letters/digits of the name + ".com" ("Midea" -> midea.com).
DOMAINS = {
    "simpson strong-tie": ["strongtie.com"],
    "lg": ["lg.com", "lge.com"],
    "delta": ["deltafaucet.com"],
    "kraus": ["kraususa.com"],
    "amerimax home products": ["amerimax.com", "amerimaxhp.com"],
    "nature power": ["naturepowerproducts.com"],
}

FIND_INSTRUCTIONS = (
    "You find a product's installation manual as a PDF on the manufacturer's own website. "
    "Search the web, then list direct PDF URLs you saw in the results, best first: the "
    "installation manual or instructions for this exact model, then an owner's/use-and-care "
    "manual that includes installation, then a spec sheet or catalogue page for the model. "
    "Only URLs that appeared in this request's search results; never invent or guess one. "
    "An empty list is a fine answer."
)

QA_INSTRUCTIONS = (
    "You answer an installer's question using ONLY the manual pages given, each headed "
    "'=== PAGE n ==='. answer: one or two short sentences to be spoken aloud, with the exact "
    "numbers and units the manual gives. page: the n of the page that states it. quote: the "
    "sentence or table row that states it, copied character for character from that page "
    "as one unbroken run of its text (max 30 words; never join pieces from different lines "
    "of a table or translation). If the pages don't state the answer, set covered to false "
    "and page/quote to null; never fill a gap with general knowledge."
)


class Manual(pydantic.BaseModel):
    part_id: str
    found: bool
    title: str | None = None
    source_url: str | None = None  # where the PDF came from (manufacturer site)
    pdf_url: str | None = None  # our copy: /parts/<id>/manual.pdf
    pages: int = 0
    model_match: bool = False  # the model number appears in the PDF's text or URL
    domains: list[str] = []
    tried: list[str] = []  # candidate URLs that weren't a usable PDF
    note: str = ""
    cost_usd: float | None = None


class Answer(pydantic.BaseModel):
    part_id: str
    question: str
    covered: bool
    answer: str
    page: int | None = None  # 1-based PDF page (what `#page=n` opens), not the printed number
    quote: str | None = None
    pdf_url: str | None = None  # opens at `page` when known
    spoken: str
    rejected: bool = False  # Grok answered but its quote isn't in the manual
    offline: bool = False
    cost_usd: float | None = None


class _Candidate(pydantic.BaseModel):
    url: str
    title: str


class _Found(pydantic.BaseModel):
    manuals: list[_Candidate]


class _Reply(pydantic.BaseModel):
    covered: bool
    answer: str
    page: int | None
    quote: str | None


def _json_format(name: str, model: type[pydantic.BaseModel]) -> dict[str, Any]:
    schema = llm._strict_schema(model.model_json_schema())
    return {"format": {"type": "json_schema", "name": name, "schema": schema, "strict": True}}


def domains_for(manufacturer: str) -> list[str]:
    name = manufacturer.strip().lower()
    return DOMAINS.get(name) or [re.sub(r"[^a-z0-9]", "", name) + ".com"]


def _on_domains(url: str, domains: list[str]) -> bool:
    host = (urlparse(url).hostname or "").lower()
    return any(host == d or host.endswith("." + d) for d in domains)


def _alnum(text: str) -> str:
    """Letters and digits only, lowercased, NFKC (PDF ligatures like "ﬁ" -> "fi")."""
    return re.sub(r"[^a-z0-9]", "", unicodedata.normalize("NFKC", text).lower())


def _pdf_path(part_id: str):
    return assets.part_dir(part_id) / "manual.pdf"


def _pdf_url(part_id: str, page: int | None = None) -> str:
    return f"/parts/{part_id}/manual.pdf" + (f"#page={page}" if page else "")


# --- finding + fetching ---------------------------------------------------------------------


async def _download(client: httpx.AsyncClient, url: str) -> bytes | None:
    """The PDF's bytes, or None if it isn't one (HTML landing page, 404, too big)."""
    try:
        async with client.stream("GET", url) as resp:
            if resp.is_error:
                return None
            buf = bytearray()
            async for chunk in resp.aiter_bytes():
                buf += chunk
                if len(buf) > MAX_PDF_BYTES:
                    return None
    except httpx.HTTPError as exc:
        logger.info("manuals: download %s failed: %s", url, exc)
        return None
    return bytes(buf) if buf.startswith(b"%PDF") else None


def extract_pages(data: bytes) -> list[str]:
    reader = pypdf.PdfReader(io.BytesIO(data))
    return [(page.extract_text() or "") for page in reader.pages[:MAX_PAGES]]


async def _index(part: Part, domains: list[str]) -> dict:
    who = f"{part.manufacturer} {part.model_no or ''}".strip()
    result = await llm.responses(
        MODEL,
        f"Installation manual PDF for the {who} ({part.name}).",
        tools=[{"type": "web_search", "filters": {"allowed_domains": domains}}],
        instructions=FIND_INSTRUCTIONS,
        text=_json_format("manuals", _Found),
        include=["no_inline_citations"],
        max_turns=4,
        timeout_s=90,
    )
    try:
        urls = [c.url for c in _Found.model_validate_json(result.text).manuals]
    except pydantic.ValidationError:
        logger.warning("manuals: unparseable find reply for %s", part.id)
        urls = []
    # Grok's list first; any other manufacturer PDF its search touched is a fallback.
    urls += [u for u in result.citations if urlparse(u).path.lower().endswith(".pdf")]
    urls = [u for u in dict.fromkeys(urls) if _on_domains(u, domains)][:MAX_CANDIDATES]

    manual = Manual(part_id=part.id, found=False, domains=domains, cost_usd=result.cost_usd)
    model_key = _alnum(part.model_no or "")
    best: tuple[str, bytes, list[str], bool] | None = None
    async with httpx.AsyncClient(
        timeout=60, follow_redirects=True, headers={"User-Agent": USER_AGENT}
    ) as client:
        for url in urls:
            data = await _download(client, url)
            pages = await asyncio.to_thread(extract_pages, data) if data else []
            if not any(p.strip() for p in pages):  # not a PDF, or a scan with no text layer
                manual.tried.append(url)
                continue
            match = bool(model_key) and (
                model_key in _alnum(url) or any(model_key in _alnum(p) for p in pages)
            )
            if best is None or (match and not best[3]):
                best = (url, data, pages, match)
            if match:
                break
    if best is None:
        manual.note = (
            f"no downloadable PDF with text on {', '.join(domains)}"
            if urls
            else f"no PDF found on {', '.join(domains)}"
        )
        return manual.model_dump()

    url, data, pages, match = best
    path = _pdf_path(part.id)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    cache.put("manual_text", part.id, pages)
    title = urlparse(url).path.rsplit("/", 1)[-1]
    manual = manual.model_copy(
        update={
            "found": True,
            "title": title,
            "source_url": url,
            "pdf_url": _pdf_url(part.id),
            "pages": len(pages),
            "model_match": match,
            "note": "" if match else "model number not found in the PDF; may be a series manual",
        }
    )
    return manual.model_dump()


async def index(part: Part, domains: list[str] | None = None, refresh: bool = False) -> Manual:
    """Find, fetch and index the part's manual. Cached per part (a miss for `TTL_S`), so repeats
    and OFFLINE are free; an OFFLINE miss raises `cache.OfflineMiss`."""
    if not part.manufacturer:
        return Manual(part_id=part.id, found=False, note="part has no manufacturer")
    domains = domains or domains_for(part.manufacturer)
    if refresh and not get_settings().OFFLINE:
        data = await _index(part, domains)
        cache.put("manual", part.id, data)
    else:
        data = await cache.cached("manual", part.id, lambda: _index(part, domains), ttl_s=TTL_S)
    return Manual.model_validate(data)


_background: dict[str, asyncio.Task] = {}  # part id -> running prefetch


def prefetch(part: Part | None) -> None:
    """Start indexing in the background (on select / checkout / a first question) so answers
    are fast. ponytail: dedupes prefetches only -- a direct `index()` racing one (the HTTP
    endpoint) can still pay for a second find."""
    if part is None or not part.manufacturer or get_settings().OFFLINE:
        return
    if part.id in _background or cache.get("manual", part.id) is not None:
        return

    async def run() -> None:
        try:
            await index(part)
        except Exception as exc:  # noqa: BLE001 -- background: log, never crash the caller
            logger.warning("manuals: prefetch %s failed: %s", part.id, exc)

    _background[part.id] = asyncio.get_running_loop().create_task(run())
    _background[part.id].add_done_callback(lambda _: _background.pop(part.id, None))


def indexed(part_id: str) -> bool:
    data = cache.get("manual", part_id)
    return bool(data and data.get("found"))


# --- answering ------------------------------------------------------------------------------

_STOP = {
    "the",
    "and",
    "for",
    "does",
    "what",
    "how",
    "far",
    "from",
    "need",
    "with",
    "this",
    "that",
    "should",
    "into",
    "when",
    "where",
    "which",
    "much",
    "many",
    "have",
    "can",
    "will",
    "are",
    "was",
    "use",
    "using",
    "about",
    "there",
    "their",
    "them",
    "they",
    "you",
    "your",
    "size",
    "long",
    "big",
}


def _terms(question: str) -> set[str]:
    words = re.findall(r"[a-z0-9]+", question.lower())
    return {w.rstrip("s") if len(w) > 3 else w for w in words if len(w) > 2 and w not in _STOP}


def rank_pages(pages: list[str], question: str) -> list[int]:
    """1-based page numbers that share a keyword with `question`, best first (idf-weighted,
    crude "-s" stemming).
    ponytail: keyword overlap, no embeddings -- "anchors" finds "anchor", "fastener" doesn't
    find "screw". Swap in Collections search if a management key turns up."""
    terms, lowered = _terms(question), [p.lower() for p in pages]
    df = {t: sum(t in p for p in lowered) for t in terms}
    scores = {
        i + 1: sum(
            math.log(len(pages) / df[t]) * (1 + math.log(p.count(t))) for t in terms if t in p
        )
        for i, p in enumerate(lowered)
    }
    return sorted((n for n, s in scores.items() if s > 0), key=lambda n: -scores[n])


def select_pages(pages: list[str], question: str, budget: int = MAX_CONTEXT_CHARS) -> list[int]:
    """1-based page numbers to show Grok, in order: all of a short manual, else the best
    `rank_pages` matches that fit in `budget` characters."""
    if sum(len(p) for p in pages) <= budget:
        return list(range(1, len(pages) + 1))
    chosen, used = [], 0
    for n in rank_pages(pages, question):
        if used + len(pages[n - 1]) <= budget:
            chosen.append(n)
            used += len(pages[n - 1])
    return sorted(chosen)


def locate_quote(quote: str, pages: list[str], hint: int | None) -> int | None:
    """The 1-based page whose text contains `quote` (the model's page first), else None."""
    needle = _alnum(quote)
    if len(needle) < MIN_QUOTE_CHARS:
        return None
    order = list(range(1, len(pages) + 1))
    if hint and 1 <= hint <= len(pages):
        order.remove(hint)
        order.insert(0, hint)
    return next((n for n in order if needle in _alnum(pages[n - 1])), None)


def _not_covered(
    part: Part,
    question: str,
    answer: str = "The manual doesn't cover that, so I won't guess.",
    **kw: Any,
) -> dict:
    return Answer(
        part_id=part.id,
        question=question,
        covered=False,
        answer=answer,
        pdf_url=_pdf_url(part.id) if indexed(part.id) else None,
        spoken=answer,
        **kw,
    ).model_dump()


def check_reply(
    part: Part,
    question: str,
    reply: _Reply,
    pages: list[str],
    cost: float | None,
    series: bool = False,
) -> dict:
    """Apply the honesty rules to Grok's reply -> an `Answer` dict."""
    if not reply.covered or not reply.quote:
        return _not_covered(part, question, cost_usd=cost)
    page = locate_quote(reply.quote, pages, reply.page)
    if page is None:
        logger.warning("manuals: quote not in %s's manual, rejected: %r", part.id, reply.quote)
        return _not_covered(
            part,
            question,
            answer="I couldn't back that up from the manual's text, so I won't guess.",
            rejected=True,
            cost_usd=cost,
        )
    answer = reply.answer.strip()
    if not answer.endswith((".", "!", "?")):
        answer += "."  # spoken + " Page n ..." needs the full stop
    return Answer(
        part_id=part.id,
        question=question,
        covered=True,
        answer=answer,
        page=page,
        quote=reply.quote.strip(),
        pdf_url=_pdf_url(part.id, page),
        # a manual that never names this model (a series/sibling manual) says so out loud
        spoken=f"{answer} Page {page} of the {'series ' if series else ''}manual.",
        cost_usd=cost,
    ).model_dump()


async def _answer(part: Part, manual: Manual, question: str) -> dict:
    pages = cache.get("manual_text", part.id) or []
    chosen = select_pages(pages, question)
    if not chosen:
        return _not_covered(part, question, cost_usd=0.0)
    context = "\n\n".join(f"=== PAGE {n} ===\n{pages[n - 1]}" for n in chosen)
    result = await llm.responses(
        MODEL,
        f"Manual: {manual.title} ({part.manufacturer} {part.model_no or part.name})\n\n"
        f"{context}\n\nQuestion: {question}",
        instructions=QA_INSTRUCTIONS,
        text=_json_format("manual_answer", _Reply),
        timeout_s=60,
    )
    try:
        reply = _Reply.model_validate_json(result.text)
    except pydantic.ValidationError:
        logger.warning("manuals: unparseable answer for %s", part.id)
        return _not_covered(part, question, cost_usd=result.cost_usd)
    return check_reply(part, question, reply, pages, result.cost_usd, not manual.model_match)


def _offline_answer(part: Part, question: str) -> Answer:
    """No uplink and no cached answer: point at the best-matching page and read its most
    on-topic line verbatim -- still from the manual, never a guess."""
    pages = cache.get("manual_text", part.id) or []
    ranked = rank_pages(pages, question)
    if not ranked:
        return Answer.model_validate(
            _not_covered(part, question, answer="Offline, and nothing in the manual matches.")
        )
    page, terms = ranked[0], _terms(question)
    lines = [ln.strip() for ln in pages[page - 1].splitlines() if len(_alnum(ln)) >= 12]
    line = max(lines, key=lambda ln: sum(t in ln.lower() for t in terms), default=None)
    answer = f"Offline, so I can't read it for you: page {page} looks relevant."
    return Answer(
        part_id=part.id,
        question=question,
        covered=False,
        answer=answer,
        page=page,
        quote=line,
        pdf_url=_pdf_url(part.id, page),
        spoken=answer,
        offline=True,
    )


async def ask(part: Part, question: str) -> Answer:
    """Answer `question` from the part's manual (indexing it first if needed). Cached per
    (part, question); OFFLINE with the manual cached still points at a page."""
    manual = await index(part)
    if not manual.found:
        who = f"{part.manufacturer or ''} {part.model_no or 'this part'}".strip()
        return Answer.model_validate(
            _not_covered(
                part,
                question,
                answer=f"I couldn't find the install manual for the {who}, so I won't guess.",
            )
        )
    key = {"part_id": part.id, "question": question, "source": manual.source_url}
    try:
        data = await cache.cached("manual_qa", key, lambda: _answer(part, manual, question))
    except cache.OfflineMiss:
        return _offline_answer(part, question)
    return Answer.model_validate(data)
