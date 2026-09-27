import json
from pathlib import Path

import pytest
from openai.types.chat import ChatCompletion

from server import agent, jobs, search
from server.models import Dims, Job, Part, Seller

FIXTURES = Path(__file__).parent / "fixtures" / "llm"


@pytest.fixture(autouse=True)
def _clear_sessions(monkeypatch):
    # select/show_sellers kick off a background recall check; tests/test_safety.py covers it
    monkeypatch.setattr(agent.safety, "start", lambda part: None)
    agent._sessions.clear()
    yield
    agent._sessions.clear()


# --- scripted ChatCompletion helpers -------------------------------------------------------


def _base_message() -> dict:
    """The tool-calling fixture's message, minus `service_tier` -- the fixture's Groq value
    ("on_demand") isn't one of the openai SDK's known literals, so real Groq responses would
    trip the same validation; drop it here rather than let every test carry that noise."""
    data = json.loads((FIXTURES / "groq_tool_calling_alone.json").read_text())
    data.pop("service_tier", None)
    return data


def _tool_calls_completion(*calls: tuple[str, dict], content: str | None = None) -> ChatCompletion:
    data = _base_message()
    data["choices"][0]["finish_reason"] = "tool_calls"
    data["choices"][0]["message"]["content"] = content
    data["choices"][0]["message"]["tool_calls"] = [
        {
            "id": f"call_{i}",
            "type": "function",
            "function": {"name": name, "arguments": json.dumps(arguments)},
        }
        for i, (name, arguments) in enumerate(calls)
    ]
    return ChatCompletion.model_validate(data)


def _tool_call_completion(name: str, arguments: dict) -> ChatCompletion:
    return _tool_calls_completion((name, arguments))


def _text_completion(content: str) -> ChatCompletion:
    data = _base_message()
    data["choices"][0]["finish_reason"] = "stop"
    data["choices"][0]["message"]["content"] = content
    data["choices"][0]["message"]["tool_calls"] = None
    return ChatCompletion.model_validate(data)


def _scripted_chat(*completions: ChatCompletion):
    queue = list(completions)

    async def fake(role, messages, tools=None, **kw):
        assert role == "agent"
        return queue.pop(0)

    return fake


async def _no_llm_chat(role, messages, tools=None, **kw):
    raise AssertionError("fast path must not call the LLM")


# --- find_part flow: ends the turn immediately, never chains further tool calls ---------------


@pytest.mark.asyncio
async def test_find_part_flow(monkeypatch):
    """find_part only starts a background job -- there are no results yet, so the loop must
    stop right there with the canned reply instead of asking the model for another turn."""
    captured = {}

    def fake_start_search(req):
        captured["req"] = req
        return Job(id="job123", status="running", stage="searching", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(_tool_call_completion("find_part", {"query": "gutter hanger"})),
    )

    ctx = {"measurement": {"label": "gutter run", "value_m": 4.2, "axis": "length"}}
    result = await agent.handle_command("s1", "find a hanger for this gutter", ctx)

    assert result == {
        "reply": "Searching the supply shops.",
        "actions": [{"name": "search_started", "args": {"job_id": "job123"}}],
        "job_id": "job123",
    }
    assert captured["req"].query == "gutter hanger"
    assert captured["req"].measurement.label == "gutter run"
    assert captured["req"].measurement.value_m == 4.2


@pytest.mark.asyncio
async def test_find_part_drops_sibling_tool_calls_same_turn(monkeypatch):
    """Live bug: the model batched find_part with select_candidate(0) before any candidates
    existed. Only find_part (and equip_tool/add_note) may run from that turn; everything else
    is dropped, and the loop must not ask the model for a second turn."""
    monkeypatch.setattr(
        jobs, "start_search", lambda req: Job(id="job123", status="running", query=req.query)
    )

    completion = _tool_calls_completion(
        ("find_part", {"query": "gutter hanger"}),
        ("select_candidate", {"index": 0}),
        ("equip_tool", {"tool": "tape"}),
    )
    calls = []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append(messages)
        return completion

    monkeypatch.setattr(agent, "chat", fake_chat)

    agent._sessions["s7"] = agent.Session(candidate_ids=["p1", "p2"])
    result = await agent.handle_command("s7", "find me a gutter hanger", {})

    assert len(calls) == 1  # never chained a second turn
    assert result["reply"] == "Searching the supply shops."
    assert result["job_id"] == "job123"
    assert {a["name"] for a in result["actions"]} == {"search_started", "equip_tool"}
    assert agent._sessions["s7"].selected_part_id is None  # select_candidate was dropped


# --- ask_scene: server-side vision tool ---------------------------------------------------


@pytest.mark.asyncio
async def test_ask_scene_tool_returns_scene_pin_and_spoken_answer(monkeypatch):
    from server.vision import SceneAnswer

    async def fake_ask_scene(question, frames):
        assert question == "what is this?"
        assert [f.id for f in frames] == ["f0"]
        return SceneAnswer(answer="It's a gutter hanger.", frame_id="f0", box=[0.1, 0.1, 0.5, 0.5])

    monkeypatch.setattr(agent.vision, "ask_scene", fake_ask_scene)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(_tool_call_completion("ask_scene", {"question": "what is this?"})),
    )

    ctx = {"frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]}
    result = await agent.handle_command("sv1", "what is this thing?", ctx)

    assert result["reply"] == "It's a gutter hanger."
    assert result["actions"] == [
        {
            "name": "scene_pin",
            "args": {
                "frame_id": "f0",
                "box": [0.1, 0.1, 0.5, 0.5],
                "label": "It's a gutter hanger.",  # integration finding #6: the pin's text
            },
        }
    ]
    assert result["job_id"] is None


@pytest.mark.asyncio
async def test_find_installer_speaks_intel_summary_in_one_turn(monkeypatch):
    from server.intel import Installer, InstallerIntel

    seen = {}

    async def fake_find(trade, location, **kw):
        seen.update(trade=trade, location=location)
        inst = Installer(name="K & K Gutters", evidence_urls=["https://knkgutters.com/services/"])
        return InstallerIntel(
            query=trade, location=location, installers=[inst], summary="K & K Gutters does it."
        )

    monkeypatch.setattr(agent.intel, "find_installers", fake_find)
    monkeypatch.setattr(  # one scripted completion: a second agent turn would IndexError
        agent,
        "chat",
        _scripted_chat(
            _tool_call_completion("find_installer", {"trade": "gutter install", "location": None})
        ),
    )
    result = await agent.handle_command("fi1", "who installs gutters near me?", {})

    assert seen == {"trade": "gutter install", "location": "Atlanta, GA"}
    assert result["reply"] == "K & K Gutters does it."
    [action] = result["actions"]
    assert action["name"] == "show_installers"
    assert action["args"]["installers"][0]["evidence_urls"] == ["https://knkgutters.com/services/"]


@pytest.mark.asyncio
async def test_ask_scene_tool_no_box_means_no_action(monkeypatch):
    from server.vision import SceneAnswer

    async def fake_ask_scene(question, frames):
        return SceneAnswer(answer="Hard to tell from here.")

    monkeypatch.setattr(agent.vision, "ask_scene", fake_ask_scene)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(_tool_call_completion("ask_scene", {"question": "what is this?"})),
    )

    ctx = {"frames": [{"id": "f0", "jpg_b64": "ZmFrZQ=="}]}
    result = await agent.handle_command("sv2", "what is this thing?", ctx)

    assert result["reply"] == "Hard to tell from here."
    assert result["actions"] == []


@pytest.mark.asyncio
async def test_ask_scene_tool_without_frames_errors_without_calling_vision(monkeypatch):
    async def fail_ask_scene(question, frames):
        raise AssertionError("must not call vision.ask_scene without frames")

    monkeypatch.setattr(agent.vision, "ask_scene", fail_ask_scene)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(_tool_call_completion("ask_scene", {"question": "what is this?"})),
    )

    result = await agent.handle_command("sv3", "what is this thing?", {})

    assert result["reply"] == "Can't do that yet: no frames available."
    assert result["actions"] == []


@pytest.mark.asyncio
async def test_find_part_passes_frame_jpg_b64_from_context(monkeypatch):
    captured = {}

    def fake_start_search(req):
        captured["req"] = req
        return Job(id="job456", status="running", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(_tool_call_completion("find_part", {"query": "window ac unit"})),
    )

    ctx = {"frame_jpg_b64": "ZmFrZQ=="}
    await agent.handle_command("sv4", "find a replacement for this", ctx)

    assert captured["req"].frame_jpg_b64 == "ZmFrZQ=="


# --- show_sellers ------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_show_sellers_requires_selection(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)  # fast path, LLM must not be hit
    result = await agent.handle_command("s2", "sellers, cheapest first", {})
    assert result == {
        "reply": "Can't do that yet: no part selected.",
        "actions": [],
        "job_id": None,
    }


@pytest.mark.asyncio
async def test_show_sellers_with_selection(monkeypatch):
    part = Part(id="p1", name="Hanger", dims_mm=Dims(w=10, d=10, h=10))
    monkeypatch.setattr(jobs, "load_part", lambda pid: part if pid == "p1" else None)
    saved = {}
    monkeypatch.setattr(jobs, "save_part", lambda p: saved.setdefault("part", p))

    async def fake_refine_sellers(part_arg, sort):
        part_arg.sellers = [Seller(name="Ace Hardware", price_usd=12.5, eta="Fri")]
        part_arg.recommended_seller = 0
        part_arg.recommendation_reason = "cheapest in stock"
        return part_arg

    monkeypatch.setattr(search, "refine_sellers", fake_refine_sellers)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)  # "cheapest" is fast-path

    agent._sessions["s2b"] = agent.Session(selected_part_id="p1")
    result = await agent.handle_command("s2b", "cheapest", {})

    assert result["actions"] == [
        {"name": "show_sellers", "args": {"part_id": "p1", "sort": "cheapest"}}
    ]
    assert result["reply"] == "Ace Hardware, $12.50, arrives Fri."
    assert saved["part"].recommendation_reason == "cheapest in stock"


# --- select_candidate updates session ------------------------------------------------------


@pytest.mark.asyncio
async def test_select_candidate_updates_session(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    agent._sessions["s3"] = agent.Session(candidate_ids=["p1", "p2", "p3"])

    result = await agent.handle_command("s3", "select the second one", {})

    assert result == {
        "reply": "Locked that one in.",
        "actions": [{"name": "select_candidate", "args": {"index": 1}}],
        "job_id": None,
    }
    assert agent._sessions["s3"].selected_part_id == "p2"


# --- fast-path intents never call the LLM --------------------------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text",
    [
        "grab the level",
        "equip the tape",
        "pick the first one",
        "select number 3",
        "fastest",
        "every 60 cm",
        "every 2.5 inches",
    ],
)
async def test_fast_path_skips_llm(monkeypatch, text):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    part = Part(id="p1", name="Hanger", dims_mm=Dims(w=10, d=10, h=10))
    monkeypatch.setattr(jobs, "load_part", lambda pid: part)
    monkeypatch.setattr(jobs, "save_part", lambda p: None)

    async def fake_refine_sellers(part_arg, sort):
        return part_arg

    monkeypatch.setattr(search, "refine_sellers", fake_refine_sellers)

    agent._sessions["fp"] = agent.Session(candidate_ids=["p1", "p2", "p3"], selected_part_id="p1")
    result = await agent.handle_command("fp", text, {})
    assert result["actions"], f"expected a fast-path action for {text!r}"


def test_fast_path_array_unit_conversion():
    assert agent._match_fast_intent("every 60 cm")[1] == {"spacing_mm": 600.0}
    assert agent._match_fast_intent("every 2 inches")[1]["spacing_mm"] == pytest.approx(50.8)
    assert agent._match_fast_intent("every 5 millimetres")[1] == {"spacing_mm": 5.0}


def test_fast_path_ignores_ambiguous_text():
    assert agent._match_fast_intent("find a hanger for this gutter") is None
    assert agent._match_fast_intent("every sixty centimetres") is None  # number words: LLM path


# The merged Grok features' fast paths must not steal each other's phrases (integration branch).
@pytest.mark.parametrize(
    ("text", "tool", "args"),
    [
        ("show it in matte black", "set_finish", {"name": "matte black"}),
        ("Show this in a brushed nickel finish.", "set_finish", {"name": "brushed nickel"}),
        ("show that in navy blue", "set_finish", {"name": "navy blue"}),
        ("show me what it'll look like", "see_it_installed", {"placement": ""}),
        ("show me what it would look like in there", "see_it_installed", {"placement": ""}),
        ("what will it look like", "see_it_installed", {"placement": ""}),
        ("send me the report", "make_report", {}),
        ("show me the report", "make_report", {}),
        ("is this recalled?", "check_safety", {"part_id": None}),
        ("survey the roof", "survey_condition", {"focus": "roof", "careful": False}),
        ("find a fix for pin f2", "fix_pin", {"pin_id": "f2"}),
        ("what did I miss?", "coach_capture", {}),
        ("What did we miss on the north side", "coach_capture", {}),
        ("which sides did I get?", "coach_capture", {}),
        ("show me the coverage", "coach_capture", {}),
        ("check the scan coverage", "coach_capture", {}),
        ("what else do I need?", "what_else", {}),
        ("do I need a permit for this?", "check_rules", {"job": None}),
        ("any rebates on a mini-split?", "check_rules", {"job": "minisplit_install"}),
        ("is there a tax credit for it", "check_rules", {"job": None}),
        ("show me the report on the permits", "make_report", {}),  # the report wins
        ("is it recalled? do I need a permit?", "check_safety", {"part_id": None}),  # safety first
        ("send it to my contractor", "send_packet", {}),
        ("share the job", "send_packet", {}),
        ("give me the packet", "send_packet", {}),
        # not a new survey or rules check: the packet already carries them
        ("send the survey and the permits to my contractor", "send_packet", {}),
        ("send the report to my contractor", "send_packet", {}),  # the contractor gets the packet
        ("take the packet down", "revoke_packet", {}),
        ("revoke the link", "revoke_packet", {}),
        ("check this quote", "check_quote", {}),
        ("check the quote for recalls and permits", "check_quote", {}),  # the quote covers both
    ],
)
def test_grok_fast_paths_route_apart(text, tool, args):
    assert agent._match_fast_intent(text)[:2] == (tool, args)


@pytest.mark.parametrize(
    "text",
    [
        "show it in the kitchen",  # a place, not a finish
        "show it in my living room",
        "what would navy cabinets look like?",  # reimagine_view: LLM picks
        "walk me into it with navy cabinets",  # walk_in_preview: LLM picks
        "what drill bit do I need?",  # ask_manual: LLM picks (offline: _QUESTION_RE)
        "what's the coverage on this sealant?",  # a part question, not the capture coach
        "which sides of the cabinet get the strip?",  # plan_placement, not the coach
        "read me the quote from the manual again",  # F9/F17's manual quote, not F20's check
    ],
)
def test_grok_phrases_left_to_the_llm(text):
    assert agent._match_fast_intent(text) is None


# Stateful fast paths (F17 coach, F18 reimagine stack) against each other and the stateless ones.
# Columns: no state, a coach running, a reimagine on screen, both. None = left to the LLM.
_BACK = ("coach_step", {"move": "back"})
_UNDO = ("undo_reimagine", {})
_OVER = ("start_over_reimagine", {})
_NEXT = ("coach_step", {"move": "next"})
_CHECK = ("check_step", {})


def _refine(text):
    return ("refine_reimagine", {"prompt": text})


def _all(route):
    return (route, route, route, route)


_ROUTES = {
    # "undo" is always the picture's; every "back" is the coach's while one runs
    "undo": (None, None, _UNDO, _UNDO),
    "undo that": (None, None, _UNDO, _UNDO),
    "back": (None, _BACK, None, _BACK),
    "go back": (None, _BACK, _UNDO, _BACK),
    "step back": (None, _BACK, _UNDO, _BACK),
    "back up one step": (None, _BACK, _UNDO, _BACK),
    "previous step": (None, _BACK, None, _BACK),
    # the coach's other words never touch the picture
    "next": (None, _NEXT, None, _NEXT),
    "skip it": (None, _NEXT, None, _NEXT),
    "I did it": (None, _NEXT, None, _NEXT),
    "done": (None, _CHECK, None, _CHECK),
    "check it": (None, _CHECK, None, _CHECK),
    "repeat that": (
        None,
        ("coach_step", {"move": "repeat"}),
        None,
        ("coach_step", {"move": "repeat"}),
    ),
    # the picture's other words never touch the coach
    "start over": (None, None, _OVER, _OVER),
    "show me the original": (None, None, _OVER, _OVER),
    "darker blue": (None, None, _refine("darker blue"), _refine("darker blue")),
    "no, lighter": (None, None, _refine("no, lighter"), _refine("no, lighter")),
    "add brass pulls": (None, None, _refine("add brass pulls"), _refine("add brass pulls")),
    "swap the pulls for black ones": (
        None,
        None,
        _refine("swap the pulls for black ones"),
        _refine("swap the pulls for black ones"),
    ),
    "add a note: order extra screws": (None, None, None, None),  # the LLM's add_note
    "add the note": (None, None, None, None),
    # stateless phrases win over both stacks
    "show it in matte black": _all(("set_finish", {"name": "matte black"})),
    "send it to my contractor": _all(("send_packet", {})),
    "take the packet down": _all(("revoke_packet", {})),
    "show me the report": _all(("make_report", {})),
    "teach me to install this": _all(("start_coach", {"job": None})),
    "what am I looking at?": _all(("label_view", {"focus": None})),
    "do the whole job": _all(("run_job", {"goal": None})),
    # F19's consent phrase: the design itself goes up; anything else added to a wall is a refine
    "add it to the wall as Maya": _all(
        ("share_design", {"name": "Maya", "phrase": "add it to the wall as Maya"})
    ),
    "post it": _all(("share_design", {"name": None, "phrase": "post it"})),
    "check this quote": _all(("check_quote", {})),  # F20; not the coach's "check it"
    "add floating shelves to the wall": (
        None,
        None,
        _refine("add floating shelves to the wall"),
        _refine("add floating shelves to the wall"),
    ),
}


@pytest.mark.parametrize(("text", "routes"), _ROUTES.items())
def test_stateful_fast_paths_route_apart(monkeypatch, text, routes):
    monkeypatch.setattr(agent.coach, "current", lambda cid, sid: {"coach_id": cid} if cid else None)
    for coaching, reimagined, want in zip(
        (False, True, False, True), (False, False, True, True), routes, strict=True
    ):
        session = agent.Session(id="route")
        session.coach_id = "c1" if coaching else None
        session.reimagine = {"site": "kitchen", "steps": [{"id": "x"}]} if reimagined else None
        got = agent._fast_route(session, text, {})
        state = f"coach={coaching} reimagine={reimagined}"
        assert (got[:2] if got else None) == want, f"{text!r} with {state}"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("tool", "args", "helper"),
    [
        ("set_finish", {"name": "navy"}, "_set_finish"),
        ("survey_condition", {"focus": "all", "careful": False}, "_survey"),
        ("check_rules", {"job": None, "address": None}, "_check_rules"),
        ("send_packet", {"days": None}, "_packet_tool"),
    ],
)
async def test_speaking_tools_run_despite_model_text(monkeypatch, tool, args, helper):
    """ends_turn: a filler line next to these tools must not skip them, and their `spoken`
    (not the filler) is the reply."""

    async def fake_chat(*a, **kw):
        return _tool_calls_completion((tool, args), content="On it.")

    async def fake_helper(session, tool_args, *rest):
        return {"spoken": f"{tool} said this."}, {"name": tool, "args": {}}, None

    monkeypatch.setattr(agent, "chat", fake_chat)
    monkeypatch.setattr(agent, helper, fake_helper)
    result = await agent.handle_command("speaks", "do the thing", {"site": "kitchen"})
    assert result["reply"] == f"{tool} said this."
    assert [a["name"] for a in result["actions"]] == [tool]


@pytest.mark.asyncio
async def test_packet_tools_hidden_until_there_is_something_to_share(monkeypatch):
    """send_packet needs a notebook, revoke_packet a live public link; check_rules is always
    offered (a named job needs no part)."""
    offered: list[set[str]] = []

    async def fake_chat(*a, tools=None, **kw):
        offered.append({t["function"]["name"] for t in tools})
        return _text_completion("ok")

    monkeypatch.setattr(agent, "chat", fake_chat)
    monkeypatch.setattr(agent.report, "_notebook_entries", lambda sid: None)
    monkeypatch.setattr(agent.packet, "latest_public", lambda sid: None)
    await agent.handle_command("hide1", "hello", {})
    monkeypatch.setattr(agent.report, "_notebook_entries", lambda sid: [{"type": "note"}])
    monkeypatch.setattr(agent.packet, "latest_public", lambda sid: {"packet_id": "x"})
    await agent.handle_command("hide1", "hello", {})
    assert "check_rules" in offered[0] and not {"send_packet", "revoke_packet"} & offered[0]
    assert {"check_rules", "send_packet", "revoke_packet"} <= offered[1]


# --- history trimming ----------------------------------------------------------------------


@pytest.mark.asyncio
async def test_history_trimmed_to_max(monkeypatch):
    monkeypatch.setattr(agent, "chat", _scripted_chat(*[_text_completion("ok") for _ in range(20)]))
    for i in range(20):
        await agent.handle_command("s5", f"message {i}", {})
    assert len(agent._sessions["s5"].history) <= agent.MAX_HISTORY


# --- loop cap at 3 turns ---------------------------------------------------------------------


@pytest.mark.asyncio
async def test_loop_caps_at_three_turns(monkeypatch):
    calls = []

    async def fake_chat(role, messages, tools=None, **kw):
        calls.append(messages)
        return _tool_call_completion("add_note", {"text": "keeps going"})

    monkeypatch.setattr(agent, "chat", fake_chat)
    result = await agent.handle_command("s6", "keep adding notes forever", {})

    assert len(calls) == agent.MAX_TURNS == 3
    assert result["reply"] == "Noted."  # synthesized: last tool was add_note, no content emitted
    assert agent._sessions["s6"].notes == ["keeps going"] * 3


# --- live smoke test, never run by default (see pyproject `-m 'not live'`) -----------------


@pytest.mark.live
@pytest.mark.asyncio
async def test_live_groq_find_a_hanger(monkeypatch):
    captured = {}

    def fake_start_search(req):
        captured["req"] = req
        return Job(id="live-job", status="running", stage="searching", query=req.query)

    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    result = await agent.handle_command("live", "find a hanger for this gutter", {})

    assert captured, "expected the model to call find_part"
    assert result["job_id"] == "live-job"


@pytest.mark.asyncio
async def test_ask_scene_only_offered_with_frames(monkeypatch):
    """Live bug: "find a hanger for this gutter" went to ask_scene with no frames to look at."""
    seen = []

    async def fake(role, messages, tools=None, **kw):
        seen.append({t["function"]["name"] for t in tools})
        return _text_completion("Aye.")

    monkeypatch.setattr(agent, "chat", fake)
    await agent.handle_command("s-frames-1", "what is this thing?", {})
    await agent.handle_command("s-frames-2", "what is this thing?", {"frames": [{"id": "1"}]})
    assert "ask_scene" not in seen[0]
    assert "ask_scene" in seen[1]


@pytest.mark.asyncio
async def test_offline_llm_path_replies_without_calling_chat(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    monkeypatch.setenv("OFFLINE", "true")
    from server.config import get_settings

    get_settings.cache_clear()
    try:
        result = await agent.handle_command("s-offline", "what's in the crate?", {})
    finally:
        get_settings.cache_clear()

    assert result == {"reply": agent.OFFLINE_REPLY, "actions": [], "job_id": None}


@pytest.mark.asyncio
async def test_offline_literal_find_starts_cached_search(monkeypatch):
    queries = []

    def fake_start_search(req):
        queries.append(req.query)
        return Job(id="job-off", status="running", query=req.query)

    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    monkeypatch.setattr(jobs, "start_search", fake_start_search)
    monkeypatch.setenv("OFFLINE", "true")
    from server.config import get_settings

    get_settings.cache_clear()
    try:
        result = await agent.handle_command("s-off-find", "Find me a gutter hanger for this.", {})
    finally:
        get_settings.cache_clear()

    assert queries == ["gutter hanger"]
    assert result["job_id"] == "job-off"
    assert result["actions"][0]["name"] == "search_started"


def test_sellers_line_speaks_recommended_pick():
    from server.models import Dims, Part, Seller

    part = Part(
        id="p",
        name="hanger",
        dims_mm=Dims(w=1, d=1, h=1),
        sellers=[
            Seller(name="Zoro", price_usd=35.39),
            Seller(name="Home Depot", price_usd=2.74, eta="Sep 26"),
        ],
        recommended_seller=1,
    )
    assert agent._sellers_line(part) == "Home Depot, $2.74, arrives Sep 26. 1 more on the board."
    part.sellers[1].pack_qty, part.sellers[1].unit_price_usd = 10, 0.27
    assert agent._sellers_line(part).startswith("Home Depot, $0.27 each")
    assert agent._sellers_line(Part(id="q", name="x", dims_mm=Dims(w=1, d=1, h=1))).startswith(
        "No sellers"
    )


# --- unbounded _sessions: LRU cap --------------------------------------------------------


# --- what_else fast path ("what else do I need?", plan §4b.6) -----------------------------


def test_what_else_fast_path_matches_phrasing():
    assert agent._match_fast_intent("what else do I need?") is not None
    assert agent._match_fast_intent("What else do I need to finish this") is not None
    assert agent._match_fast_intent("what else") is None  # no "need" -> not fast-path


@pytest.mark.asyncio
async def test_what_else_fast_path_uses_selected_part(monkeypatch):
    from server.bom import Bom, BomLine

    part = Part(id="p1", name="Hanger", dims_mm=Dims(w=10, d=10, h=10))
    monkeypatch.setattr(jobs, "load_part", lambda pid: part if pid == "p1" else None)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)

    captured = {}

    async def fake_what_else(parts, counts):
        captured["ids"] = [p.id for p in parts]
        captured["counts"] = counts
        return Bom(
            id="bom-1",
            part_ids=["p1"],
            lines=[
                BomLine(
                    idx=0,
                    name="Gutter screws",
                    qty=20,
                    reason="fasten it",
                    seller=Seller(name="Ace", price_usd=12.0, pack_qty=100, unit_price_usd=0.12),
                )
            ],
            total_usd=2.4,
        )

    monkeypatch.setattr(agent.bom, "what_else", fake_what_else)

    agent._sessions["we1"] = agent.Session(selected_part_id="p1")
    result = await agent.handle_command("we1", "what else do I need?", {})

    assert captured == {"ids": ["p1"], "counts": {"p1": 1}}
    assert result["job_id"] is None
    assert len(result["actions"]) == 1
    action = result["actions"][0]
    assert action["name"] == "show_bom"
    assert action["args"]["bom_id"] == "bom-1"
    assert action["args"]["total_usd"] == 2.4
    assert [line["name"] for line in action["args"]["lines"]] == ["Gutter screws"]
    assert result["reply"] == "Also grab gutter screws. $2.40 more."


@pytest.mark.asyncio
async def test_what_else_uses_placed_context_over_selected_part(monkeypatch):
    from server.bom import Bom

    hanger = Part(id="p1", name="Hanger", dims_mm=Dims(w=1, d=1, h=1))
    monkeypatch.setattr(jobs, "load_part", lambda pid: hanger if pid == "p1" else None)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)

    captured = {}

    async def fake_what_else(parts, counts):
        captured["ids"] = [p.id for p in parts]
        captured["counts"] = counts
        return Bom(id="", part_ids=[], lines=[], total_usd=0.0)

    monkeypatch.setattr(agent.bom, "what_else", fake_what_else)

    ctx = {"placed": [{"part_id": "p1", "count": 8}], "selected_part_id": "other-part"}
    result = await agent.handle_command("we2", "what else do I need for this", ctx)

    assert captured == {"ids": ["p1"], "counts": {"p1": 8}}
    assert result["reply"] == "Nothing extra to grab."
    assert result["actions"] == []


@pytest.mark.asyncio
async def test_what_else_fast_path_errors_with_nothing_placed(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command("we3", "what else do I need?", {})
    assert result == {
        "reply": "Can't do that yet: nothing placed yet.",
        "actions": [],
        "job_id": None,
    }


def test_sessions_evicted_lru_beyond_cap():
    agent._sessions.clear()
    for i in range(agent.MAX_SESSIONS + 1):
        agent._get_session(f"s{i}")

    assert len(agent._sessions) == agent.MAX_SESSIONS
    assert "s0" not in agent._sessions  # oldest evicted
    assert f"s{agent.MAX_SESSIONS}" in agent._sessions


def test_get_session_reuses_and_refreshes_existing_session():
    agent._sessions.clear()
    first = agent._get_session("s1")
    first.notes.append("hello")

    assert agent._get_session("s1") is first
    assert agent._get_session("s1").notes == ["hello"]


def test_bom_reply_is_short_for_speech():
    from server.bom import Bom, BomLine

    lines = [
        BomLine(idx=i, name=n, qty=1, reason="")
        for i, n in enumerate(
            ["Exterior Sealant (10 oz tube)", "Drip Edge (10 ft roll)", "End Caps", "Tape"]
        )
    ]
    reply = agent._bom_reply(Bom(id="b", part_ids=["p"], lines=lines, total_usd=24.81))
    assert reply == "Also grab exterior sealant, drip edge and 2 more. $24.81 more."


# --- checkout intent ("buy it from the recommended seller", integration finding #10) ---------


@pytest.mark.parametrize(
    "text",
    [
        "buy it from the recommended seller",
        "check out with the recommended seller",
        "checkout",
        "Check out.",
        "let's check out now",
        "pay for it",
        "pay with Visa",
        "purchase this",
        "order them",
    ],
)
def test_checkout_fast_path_matches_phrasing(text):
    name, args, _ = agent._match_fast_intent(text)
    assert name == "start_checkout"
    assert args["seller_index"] is None


@pytest.mark.parametrize(
    "text",
    [
        "find me a part to buy",
        "where can I buy a cabinet hinge",
        "buy a drawer slide",
        "check out this hinge",
        "how much will I pay?",
    ],
)
def test_checkout_fast_path_leaves_searches_and_questions_alone(text):
    match = agent._match_fast_intent(text)
    assert match is None or match[0] != "start_checkout"


def _hinge_with_sellers() -> Part:
    return Part(
        id="pivot-door-hinges-1e8608",
        name="Pivot door hinge",
        dims_mm=Dims(w=10, d=10, h=10),
        sellers=[
            Seller(name="Hardware Tree", price_usd=9.10, shipping_usd=0.0, in_stock=True),
            Seller(name="Hinge Outlet", price_usd=7.59, shipping_usd=0.0, in_stock=True),
            Seller(name="Zoro", price_usd=8.20, eta="Sep 30", eta_days=4, in_stock=True),
        ],
        recommended_seller=1,
    )


# The headset's context from the live repro (a hinge selected and placed on a door).
_CHECKOUT_CTX = {
    "measurement": {"label": "tape #1", "value_m": 0.2625, "axis": "w"},
    "selected_part_id": "pivot-door-hinges-1e8608",
    "placed": [{"part_id": "pivot-door-hinges-1e8608", "count": 1}],
    "candidate_ids": ["pivot-door-hinges-1e8608", "other-a", "other-b"],
    "tool": "part",
}


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text", ["buy it from the recommended seller", "check out with the recommended seller"]
)
async def test_checkout_opens_panel_for_recommended_seller(monkeypatch, text):
    """Live bug: both phrasings came back as "Searching the supply shops." + search_started."""
    hinge = _hinge_with_sellers()
    monkeypatch.setattr(jobs, "load_part", lambda pid: hinge if pid == hinge.id else None)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)

    def no_search(req):
        raise AssertionError("a purchase must not start a search")

    monkeypatch.setattr(jobs, "start_search", no_search)

    result = await agent.handle_command("co1", text, _CHECKOUT_CTX)

    assert result == {
        "reply": "Hold-to-pay panel's open for Hinge Outlet.",
        "actions": [{"name": "start_checkout", "args": {"seller_index": 1}}],
        "job_id": None,
    }


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "text,expected",
    [
        ("buy it from hardware tree", 0),  # a seller named in the command
        ("check out with the cheapest", 1),  # Hinge Outlet, $7.59
        ("buy it, fastest", 2),  # the only seller with an eta
    ],
)
async def test_checkout_picks_named_or_sorted_seller(monkeypatch, text, expected):
    hinge = _hinge_with_sellers()
    monkeypatch.setattr(jobs, "load_part", lambda pid: hinge)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)

    result = await agent.handle_command("co2", text, _CHECKOUT_CTX)

    assert result["actions"] == [{"name": "start_checkout", "args": {"seller_index": expected}}]


@pytest.mark.asyncio
async def test_checkout_needs_a_selected_part_with_sellers(monkeypatch):
    monkeypatch.setattr(agent, "chat", _no_llm_chat)
    result = await agent.handle_command("co3", "buy it", {})
    assert result == {
        "reply": "Can't do that yet: no part selected.",
        "actions": [],
        "job_id": None,
    }

    bare = Part(id="p1", name="Hanger", dims_mm=Dims(w=10, d=10, h=10))
    monkeypatch.setattr(jobs, "load_part", lambda pid: bare)
    result = await agent.handle_command("co4", "buy it", {"selected_part_id": "p1"})
    assert result["reply"] == "Can't do that yet: no seller to check out with."
    assert result["actions"] == []


@pytest.mark.asyncio
async def test_llm_start_checkout_null_seller_index_means_recommended(monkeypatch):
    hinge = _hinge_with_sellers()
    monkeypatch.setattr(jobs, "load_part", lambda pid: hinge)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(
            _tool_call_completion("start_checkout", {"seller_index": None}),
            _text_completion("Hold to pay when ready."),
        ),
    )

    result = await agent.handle_command("co5", "I'll take the recommended one", _CHECKOUT_CTX)

    assert result["actions"] == [{"name": "start_checkout", "args": {"seller_index": 1}}]
    # The server picked the seller, so its line is said verbatim (F6's start_checkout rule): the
    # model can't know which seller that was.
    assert result["reply"] == "Hold-to-pay panel's open for Hinge Outlet."


@pytest.mark.asyncio
async def test_llm_start_checkout_explicit_seller_keeps_the_model_reply(monkeypatch):
    hinge = _hinge_with_sellers()
    monkeypatch.setattr(jobs, "load_part", lambda pid: hinge)
    monkeypatch.setattr(
        agent,
        "chat",
        _scripted_chat(
            _tool_call_completion("start_checkout", {"seller_index": 2}),
            _text_completion("Hold to pay when ready."),
        ),
    )

    result = await agent.handle_command("co6", "the Zoro one", _CHECKOUT_CTX)

    assert result["actions"] == [{"name": "start_checkout", "args": {"seller_index": 2}}]
    assert result["reply"] == "Hold to pay when ready."


@pytest.mark.asyncio
async def test_checkout_without_a_recommendation_opens_the_first_seller(monkeypatch):
    hinge = _hinge_with_sellers().model_copy(update={"recommended_seller": None})
    monkeypatch.setattr(jobs, "load_part", lambda pid: hinge)
    monkeypatch.setattr(agent, "chat", _no_llm_chat)

    result = await agent.handle_command("co7", "buy it", _CHECKOUT_CTX)

    assert result["actions"] == [{"name": "start_checkout", "args": {"seller_index": 0}}]
    assert result["reply"] == "Hold-to-pay panel's open for Hardware Tree."


def test_find_part_description_no_longer_attracts_purchases():
    tools = {t["function"]["name"]: t["function"] for t in agent.TOOLS}
    assert "to buy" not in tools["find_part"]["description"]
    assert "start_checkout" in tools["find_part"]["description"]
    seller_index = tools["start_checkout"]["parameters"]["properties"]["seller_index"]
    assert seller_index["type"] == ["integer", "null"]
