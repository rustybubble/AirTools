"""Install-manual Q&A: finding + fetching the PDF, the honesty rules (quote must be in the text,
"not covered" said plainly), caching, OFFLINE, the endpoints and the ask_manual agent tool.
`api.x.ai/v1/responses` and the manufacturer's site are respx-mocked; the PDF is built here."""

import json

import httpx
import pytest
import respx
from fastapi.testclient import TestClient
from test_agent import _scripted_chat, _tool_calls_completion

from server import agent, cache, jobs, llm, manuals
from server.app import app
from server.config import get_settings
from server.models import Dims, Part

PDF_URL = "https://www.midea.com/content/dam/us/MAW12U1QWT-user-manual.pdf"
HTML_URL = "https://www.midea.com/us/support/maw08u1qwt"
PAGES = [
    "Window air conditioner MAW08U1QWT / MAW12U1QWT user manual.",
    "Tools needed: Phillips screwdriver\nDrill and 1/8” drill bit\nTape measure",
    (
        "Fits double hung windows with opening widths of 22 to 36 inches.\n"
        '10d for single 2x\n10d para un solo 2x\n(0.148" x 3")'
    ),
]


def _pdf(pages: list[str]) -> bytes:
    """A minimal text PDF (Helvetica, one line per text line) that pypdf can read."""

    def esc(s: str) -> str:
        return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

    n = len(pages)
    kids = " ".join(f"{4 + 2 * i} 0 R" for i in range(n))
    objs = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        f"<< /Type /Pages /Kids [{kids}] /Count {n} >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    for i, text in enumerate(pages):
        lines = " T* ".join(f"({esc(ln)}) Tj" for ln in text.split("\n"))
        stream = f"BT /F1 10 Tf 14 TL 40 750 Td {lines} ET".encode("cp1252").decode("latin-1")
        objs.append(
            f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            f"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>"
        )
        objs.append(f"<< /Length {len(stream)} >>\nstream\n{stream}\nendstream")
    out, offsets = "%PDF-1.4\n", []
    for i, body in enumerate(objs, 1):
        offsets.append(len(out.encode("latin-1")))
        out += f"{i} 0 obj\n{body}\nendobj\n"
    xref = len(out.encode("latin-1"))
    out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n"
    out += "".join(f"{o:010d} 00000 n \n" for o in offsets)
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"
    return out.encode("latin-1")


def _xai(payload: dict, citations: list[str] = (), ticks: int = 520_000_000) -> dict:
    """A `/v1/responses` body in the live shape: a web_search_call, then the JSON message."""
    return {
        "output": [
            {
                "type": "web_search_call",
                "action": {"type": "search", "sources": [{"url": u} for u in citations]},
            },
            {
                "type": "message",
                "content": [{"type": "output_text", "text": json.dumps(payload)}],
            },
        ],
        "citations": list(citations),
        "usage": {"cost_in_usd_ticks": ticks},
    }


def _found(*urls: str) -> httpx.Response:
    return httpx.Response(
        200, json=_xai({"manuals": [{"url": u, "title": "manual"} for u in urls]}, [PDF_URL])
    )


def _reply(
    covered=True, answer="Use a 1/8 in drill bit.", page=2, quote="Drill and 1/8” drill bit"
):
    body = {"covered": covered, "answer": answer, "page": page, "quote": quote}
    return httpx.Response(200, json=_xai(body, ticks=26_000_000))


@pytest.fixture(autouse=True)
def _tmp_data(tmp_path, monkeypatch):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    agent._sessions.clear()
    manuals._background.clear()


@pytest.fixture
def part() -> Part:
    p = Part(
        id="midea-maw08u1qwt-8d6d08",
        name="8,000 BTU U+ Window Air Conditioner",
        manufacturer="Midea",
        model_no="MAW08U1QWT",
        dims_mm=Dims(w=560, d=530, h=340),
    )
    jobs.save_part(p)
    return p


def _mock_site(router: respx.MockRouter) -> None:
    router.get(HTML_URL).mock(return_value=httpx.Response(200, text="<html>support</html>"))
    router.get(PDF_URL).mock(return_value=httpx.Response(200, content=_pdf(PAGES)))


# --- finding + fetching ---------------------------------------------------------------------


@pytest.mark.asyncio
async def test_index_finds_downloads_and_caches_the_manual(part):
    with respx.mock(assert_all_called=True) as router:
        # an off-domain URL is never fetched (respx would raise on the unmocked GET)
        grok = router.post(llm.XAI_RESPONSES_URL).mock(
            return_value=_found(HTML_URL, "https://evil.example.com/x.pdf", PDF_URL)
        )
        _mock_site(router)
        manual = await manuals.index(part)
        again = await manuals.index(part)  # cache hit: no second search

    assert grok.call_count == 1
    body = json.loads(grok.calls.last.request.content)
    assert body["model"] == manuals.MODEL
    assert body["tools"] == [{"type": "web_search", "filters": {"allowed_domains": ["midea.com"]}}]
    assert body["text"]["format"]["type"] == "json_schema"
    assert "MAW08U1QWT" in body["input"]

    assert manual.found and manual.model_match and manual.source_url == PDF_URL
    assert manual.pages == 3 and manual.tried == [HTML_URL]  # the HTML page isn't a PDF
    assert manual.pdf_url == "/parts/midea-maw08u1qwt-8d6d08/manual.pdf"
    assert manual.cost_usd == pytest.approx(0.052)
    assert again == manual
    assert "1/8" in cache.get("manual_text", part.id)[1]
    assert (jobs.assets.part_dir(part.id) / "manual.pdf").read_bytes().startswith(b"%PDF")


@pytest.mark.asyncio
async def test_no_manual_found_is_an_honest_answer(part):
    with respx.mock(assert_all_called=True) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(
            return_value=httpx.Response(200, json=_xai({"manuals": []}))
        )
        manual = await manuals.index(part)
        answer = await manuals.ask(part, "What size drill bit?")  # no second Grok call

    assert not manual.found and manual.note == "no PDF found on midea.com"
    assert not answer.covered and answer.page is None and answer.quote is None
    assert (
        answer.answer
        == "I couldn't find the install manual for the Midea MAW08U1QWT, so I won't guess."
    )


def test_domains_for_known_and_guessed_manufacturers():
    assert manuals.domains_for("Simpson Strong-Tie") == ["strongtie.com"]
    assert manuals.domains_for("Hessaire") == ["hessaire.com"]
    assert manuals._on_domains("https://www2.strongtie.com/a.pdf", ["strongtie.com"])
    assert not manuals._on_domains("https://notstrongtie.com/a.pdf", ["strongtie.com"])


# --- answering + honesty --------------------------------------------------------------------


async def _indexed(part: Part) -> None:
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(return_value=_found(PDF_URL))
        _mock_site(router)
        await manuals.index(part)


@pytest.mark.asyncio
async def test_ask_answers_with_verified_quote_and_page_then_hits_cache(part):
    await _indexed(part)
    with respx.mock(assert_all_called=True) as router:
        grok = router.post(llm.XAI_RESPONSES_URL).mock(return_value=_reply(page=3))  # wrong page
        answer = await manuals.ask(part, "What size drill bit for the pilot holes?")
        again = await manuals.ask(part, "what size drill bit for the pilot holes")

    assert grok.call_count == 1  # the second (same question, other punctuation) is cached
    assert "=== PAGE 2 ===" in json.loads(grok.calls.last.request.content)["input"]
    assert answer.covered and not answer.rejected
    assert answer.page == 2  # corrected to where the quote actually is
    assert answer.pdf_url == "/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=2"
    assert answer.spoken == "Use a 1/8 in drill bit. Page 2 of the manual."
    assert answer.cost_usd == pytest.approx(0.0026)
    assert again == answer


@pytest.mark.asyncio
async def test_quote_not_in_the_manual_is_rejected(part):
    await _indexed(part)
    # stitched across an interleaved translation line -- real failure seen live on Simpson
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(
            return_value=_reply(
                answer="10d nails.", page=3, quote='10d for single 2x (0.148" x 3")'
            )
        )
        answer = await manuals.ask(part, "What nails for a single 2x?")

    assert answer.rejected and not answer.covered
    assert answer.page is None and answer.quote is None
    assert answer.answer == "I couldn't back that up from the manual's text, so I won't guess."


@pytest.mark.asyncio
async def test_not_covered_says_so_plainly(part):
    await _indexed(part)
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(
            return_value=_reply(covered=False, answer="Probably 12 in-lb.", page=None, quote=None)
        )
        answer = await manuals.ask(part, "What torque for the bracket screws?")

    assert not answer.covered and not answer.rejected and answer.page is None
    assert answer.answer == "The manual doesn't cover that, so I won't guess."  # not the guess


def test_locate_quote_ignores_spacing_case_and_ligatures():
    pages = ["nothing here", "Lower sash must open sufﬁ ciently to 13.75 inches."]
    assert manuals.locate_quote("lower sash must open sufficiently", pages, hint=1) == 2
    assert manuals.locate_quote("16d", pages, hint=2) is None  # too short to prove anything


def test_select_pages_short_manual_whole_long_manual_by_keyword():
    assert manuals.select_pages(PAGES, "anything") == [1, 2, 3]
    long = ["filler text " * 50] * 30
    long[17] = "Drill a 3/16 in hole for each anchor. " + "x" * 400
    assert manuals.select_pages(long, "What drill bit for the anchors?", budget=2000) == [18]


# --- OFFLINE --------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_offline_cached_answer_then_page_pointer_then_503(part, monkeypatch):
    await _indexed(part)
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(return_value=_reply())
        online = await manuals.ask(part, "What size drill bit?")

    monkeypatch.setenv("OFFLINE", "1")
    get_settings.cache_clear()
    with respx.mock(assert_all_mocked=True):  # any HTTP call would raise
        assert await manuals.ask(part, "What size drill bit?") == online
        pointer = await manuals.ask(part, "Which drill bit?")  # never asked online

    assert pointer.offline and not pointer.covered and pointer.page == 2
    assert pointer.quote == "Drill and 1/8” drill bit"  # verbatim from the page text
    client = TestClient(app)
    resp = client.post("/parts/unindexed-part/manual/ask", json={"question": "x"})
    assert resp.status_code == 404
    jobs.save_part(part.model_copy(update={"id": "other-part"}))
    resp = client.post("/parts/other-part/manual/ask", json={"question": "What bit?"})
    assert resp.status_code == 503


# --- endpoints ------------------------------------------------------------------------------


def test_endpoints_find_serve_pdf_and_ask(part):
    client = TestClient(app)
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(side_effect=[_found(PDF_URL), _reply()])
        _mock_site(router)
        found = client.post(f"/parts/{part.id}/manual", json={"domains": ["Midea.com"]})
        asked = client.post(f"/parts/{part.id}/manual/ask", json={"question": "Drill bit?"})

    assert found.status_code == 200 and found.json()["found"] is True
    assert asked.status_code == 200 and asked.json()["page"] == 2
    pdf = client.get(f"/parts/{part.id}/manual.pdf")
    assert pdf.status_code == 200 and pdf.headers["content-type"] == "application/pdf"
    assert client.post("/parts/nope/manual").status_code == 404
    assert client.post(f"/parts/{part.id}/manual/ask", json={"question": " "}).status_code == 400


# --- agent ----------------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_agent_ask_manual_emits_show_manual_answer(part, monkeypatch):
    await _indexed(part)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(
            _tool_calls_completion(
                ("ask_manual", {"question": "What drill bit?", "part_id": None}),
                content="Checking the manual.",  # filler must not swallow the tool call
            )
        ),
    )
    with respx.mock(assert_all_called=False) as router:
        router.post(llm.XAI_RESPONSES_URL).mock(return_value=_reply())
        result = await agent.handle_command(
            "s1", "what drill bit do I need?", {"selected_part_id": part.id}
        )

    assert result["reply"] == "Use a 1/8 in drill bit. Page 2 of the manual."
    assert result["actions"] == [
        {
            "name": "show_manual_answer",
            "args": {
                "part_id": part.id,
                "answer": "Use a 1/8 in drill bit.",
                "page": 2,
                "quote": "Drill and 1/8” drill bit",
                "pdf_url": f"/parts/{part.id}/manual.pdf#page=2",
            },
        }
    ]


@pytest.mark.asyncio
async def test_agent_ask_manual_before_indexing_prefetches_and_says_so(part, monkeypatch):
    started = []
    monkeypatch.setattr(manuals, "prefetch", lambda p: started.append(p.id))
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(
            _tool_calls_completion(("ask_manual", {"question": "Bit?", "part_id": None}))
        ),
    )
    result = await agent.handle_command("s2", "what bit?", {"selected_part_id": part.id})
    assert started == [part.id]
    assert result["reply"] == "Fetching the manual now; ask me again in a minute."
    assert result["actions"] == []


@pytest.mark.asyncio
async def test_agent_offline_question_uses_cached_manual(part, monkeypatch):
    await _indexed(part)
    monkeypatch.setenv("OFFLINE", "1")
    get_settings.cache_clear()
    ctx = {"selected_part_id": part.id}
    # "need" would also match the offline find-a-part regex; the manual question wins
    result = await agent.handle_command("s3", "What drill bit do I need?", ctx)
    assert result["reply"] == "Offline, so I can't read it for you: page 2 looks relevant."
    assert result["actions"][0]["name"] == "show_manual_answer"
    assert result["actions"][0]["args"]["page"] == 2
    assert result["job_id"] is None
    found = await agent.handle_command("s3", "Can you find a gutter hanger?", ctx)
    assert found["actions"][0]["name"] == "search_started"
