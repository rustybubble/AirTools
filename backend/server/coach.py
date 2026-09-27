"""Install coach (G6 pick 3, docs/research/grok-ideas/g6-round5.md, f17-coach.md): "teach me to
install this", one step at a time, each step checked on a headset camera frame.

Steps, first match wins:
1. The part's F9 manual (`server/manuals.py`). One Grok call over its install pages writes <= 8
   steps, each with a verbatim quote. Code keeps a step only if its quote is in the PDF text
   (F9's `locate_quote`) and takes `page` from where it was found. Cached per (part, manual).
2. A checked-in template (`JOBS`): window AC, gutter hangers, under-cabinet LED strip, faucet.
Jobs F13 marks licensed-trade-only (`rules.JobSpec.licensed`, or a finished rules check whose
permit page says a homeowner can't pull it) get no coaching.

Checks follow G6 live check #14: never ask Grok "is this step done?" (#13 said "done" wrongly).
Each step carries 1-3 yes/no questions about what is visible; one fast vision call
(`LLM_COACH_VISION`) answers yes / no / cant_see, and code decides:
- any answer that contradicts `expect` -> not_yet (spoken with the evidence);
- else any cant_see -> look (reposition);
- else passed, and the coach moves on.
The user always wins: "I did it" moves on too, and the notebook says it wasn't camera-checked.

Drill steps: the same call lists visible outlets and switches. A drill point (the headset's pixel,
default the frame centre) in the column straight above one is a local "stop". It is a hint, never
a guarantee, so every drill step also says to check with a stud/wire finder.

State lives in `data/coach/<coach_id>.json`, so a restart resumes at the same step.
"""

import base64
import binascii
import hashlib
import json
import logging
import re
import time
import uuid
from pathlib import Path
from typing import Any, Literal

import httpx
import openai
import pydantic

from server import cache, jobs, llm, manuals, report, rules, survey, vision
from server.config import get_settings
from server.models import Part

logger = logging.getLogger(__name__)

MAX_STEPS = 8
MAX_CHECKS = 3
STEP_PAGES_CHARS = 20_000  # ~5k tokens of install pages for the step-writing call
STEP_QUERY = "install installation bracket mount screw drill level window sill"  # ranks pages
SIDE_MARGIN = 2.0  # plate widths either side of an outlet/switch: ~15 cm for a 7-8 cm plate
FRAME_MAX_AGE_S = 10  # the voice relay's last frame is too stale to check after this
WIRE_HINT = "I can't see inside the wall: check the spot with a stud and wire finder first."
OFFLINE_LINE = "I can't check it offline. Say 'I did it' when it's done."
FAILED_LINE = "I couldn't check that just now. Say 'I did it' when it's done."
HOLD_STILL = "Hold still and look at it, then say check it."
NO_CHECKS = "Nothing on this step I can check by eye. Say 'I did it' when it's done."
NO_STEPS = "I don't have steps for this one. Ask me about the manual instead."
LABEL = "Coaching from the manual or our own checklist. Camera checks can miss things."


class CantCoach(Exception):
    """No coaching for this job; `spoken` says why. `refused` = licensed trade only."""

    def __init__(self, spoken: str, refused: bool = False):
        super().__init__(spoken)
        self.spoken, self.refused = spoken, refused


class CheckFailed(Exception):
    """The vision call failed or answered off-schema (G6 #13's status reply): no verdict."""


# --- templates: our own wording, (question, expect, where to look) per check -------------------

JOBS: dict[str, dict[str, Any]] = {
    "window_ac": {
        "label": "window air conditioner",
        "match": r"window (?:air|a/?c|unit)|room air",
        "steps": [
            ("Open the lower sash and measure the width of the window opening.",
             [("Is the lower window sash raised, leaving an open gap?", "yes", "Look at the window.")]),
            ("Fit the support bracket on the sill and level it with a slight tilt to the outside.",
             [("Is a support bracket sitting on the window sill?", "yes", "Look at the sill.")]),
            ("Drill pilot holes and screw the bracket to the sill.",
             [("Are screw heads visible holding the bracket to the sill?", "yes", "Look at the bracket on the sill.")]),
            ("Set the air conditioner on the bracket and lower the sash onto its top.",
             [("Is an air conditioner sitting in the window opening?", "yes", "Look at the window."),
              ("Is the window sash resting on top of the air conditioner?", "yes", "Look at the top of the unit.")]),
            ("Pull the side panels out to the window frame and seal the gaps with foam.",
             [("Do the side panels reach the window frame on both sides?", "yes", "Look at the whole window."),
              ("Is an open gap to the outside visible around the unit?", "no", "Look at the whole window.")]),
        ],
    },
    "gutter_hanger": {
        "label": "gutter hangers",
        "match": r"gutter",
        "steps": [
            ("Mark a hanger spot on the fascia every 60 centimetres along the run.",
             [("Are pencil marks visible along the fascia board?", "yes", "Look along the fascia.")]),
            ("Hook a hanger under the gutter's front lip at each mark.",
             [("Is a hanger clipped across the inside of the gutter?", "yes", "Look into the gutter.")]),
            ("Drive each hanger's screw through the back of the gutter into the fascia.",
             [("Is a screw head visible in the hanger at the back of the gutter?", "yes", "Look into the gutter.")]),
            ("Check the gutter falls toward the downspout, about 6 millimetres every 3 metres.",
             [("Is a level resting on the gutter?", "yes", "Look at the gutter.")]),
        ],
    },
    "led_strip": {
        "label": "under-cabinet LED strip",
        "match": r"\bled\b.*\b(?:strip|tape)\b|under[- ]cabinet|light strip",
        "steps": [
            ("Wipe the underside of the cabinets clean and dry.",
             [("Is the underside of the cabinet clear of objects?", "yes", "Look up under the cabinet.")]),
            ("Hold the strip along the front edge underneath and mark where it ends.",
             [("Are pencil marks visible under the cabinet?", "yes", "Look up under the cabinet.")]),
            ("Drill a 10 millimetre hole through the cabinet side for the power lead.",
             [("Is there a round hole in the cabinet's side panel?", "yes", "Look at the cabinet side.")]),
            ("Peel the backing, press the strip on along the marks, and feed the lead through the hole.",
             [("Is an LED strip stuck along the underside of the cabinet?", "yes", "Look up under the cabinet.")]),
            ("Plug the lead into the outlet and switch the strip on.",
             [("Is the LED strip lit?", "yes", "Look up under the cabinet.")]),
        ],
    },
    "faucet_swap": {
        "label": "kitchen faucet",
        "match": r"faucet",
        "steps": [
            ("Clear the counter around the sink and empty the cabinet underneath.",
             [("Are any objects standing within a hand's width of the sink rim?", "no", "Look at the sink.")]),
            ("Close both shut-off valves under the sink, then open the faucet to let the pressure out.",
             [("Are the shut-off valves under the sink visible, with their handles turned fully closed?", "yes", "Look under the sink at the valves.")]),
            ("Put a bucket under the valves and disconnect both supply lines from them.",
             [("Is a bucket or container sitting under the valves?", "yes", "Look under the sink."),
              ("Are supply hoses still attached to the shut-off valves?", "no", "Look under the sink.")]),
            ("Undo the mounting nut under the counter and lift the old faucet out.",
             [("Is a faucet standing on the sink deck?", "no", "Look at the sink.")]),
            ("Set the new faucet in place with its gasket and tighten the mounting nut from below.",
             [("Is a faucet standing on the sink deck?", "yes", "Look at the sink.")]),
            ("Connect the supply lines, open the valves slowly and look for drips.",
             [("Are supply hoses connected to both shut-off valves?", "yes", "Look under the sink."),
              ("Is water dripping or pooling under the sink?", "no", "Look under the sink.")]),
        ],
    },
}  # fmt: skip


def job_for(text: str) -> str | None:
    """The JOBS key whose pattern matches a part name or a spoken job, else None."""
    return next(
        (k for k, j in JOBS.items() if re.search(j["match"], text or "", re.IGNORECASE)), None
    )


def _numbered(steps: list[dict]) -> list[dict]:
    """Index steps and checks (c1..c3); a step that mentions drilling gets the drill rule."""
    for i, s in enumerate(steps):
        s["i"] = i
        text = f"{s['say']} {s.get('quote') or ''}"
        s["tool"] = "drill" if re.search(r"\bdrill", text, re.IGNORECASE) else None
        s["checks"] = [
            {"id": f"c{n}", "question": q, "expect": e, "look_at": look}
            for n, (q, e, look) in enumerate(s["checks"][:MAX_CHECKS], 1)
        ]
    return steps


def template_steps(job: str) -> list[dict]:
    return _numbered(
        [{"say": say, "page": None, "quote": None, "checks": c} for say, c in JOBS[job]["steps"]]
    )


# --- manual steps (F9) ------------------------------------------------------------------------

STEPS_INSTRUCTIONS = (
    "You turn a product's installation instructions into at most 8 steps a homeowner follows in "
    "order, using ONLY the manual pages given, each headed '=== PAGE n ==='. Installation only: "
    "skip safety notices, operation, cleaning and warranty. say: one or two short sentences to "
    "read aloud, in plain words, keeping the manual's numbers and units. page: the n of the page "
    "the step is on. quote: the sentence on that page that states the step, copied character "
    "for character as one unbroken run of its text (max 30 words). checks: one to three yes/no "
    "questions that a photo taken after the step could answer just by looking, each about a "
    "visible object and ending with '?'; expect: the answer once the step is done. Never ask "
    "whether a step is done, complete or correct."
)
_STATUS_WORDS = re.compile(
    r"\b(?:done|complete[d]?|finished|correct(?:ly)?|properly)\b", re.IGNORECASE
)


class _Check(pydantic.BaseModel):
    question: str
    expect: Literal["yes", "no"]


class _Step(pydantic.BaseModel):
    say: str
    page: int | None
    quote: str
    checks: list[_Check]


class _Steps(pydantic.BaseModel):
    steps: list[_Step]


def visual_question(q: str) -> bool:
    """The code rule on a check: a question about what's visible, not about status (#13)."""
    return q.strip().endswith("?") and len(q.split()) >= 4 and not _STATUS_WORDS.search(q)


def keep_quoted(raw: list[dict], pages: list[str]) -> list[dict]:
    """Grok's steps -> the ones whose quote is in the manual's text, `page` from where it is."""
    kept = []
    for s in raw[:MAX_STEPS]:
        page = manuals.locate_quote(s["quote"], pages, s.get("page"))
        if page is None:
            logger.info("coach: step dropped, quote not in the manual: %r", s["quote"])
            continue
        checks = [(c["question"].strip(), c["expect"], None) for c in s["checks"]]
        kept.append(
            {
                "say": s["say"].strip(),
                "page": page,
                "quote": s["quote"].strip(),
                "checks": [c for c in checks if visual_question(c[0])],
            }
        )
    return _numbered(kept)


async def _write_steps(part: Part, manual: manuals.Manual, pages: list[str]) -> dict:
    chosen = manuals.select_pages(pages, STEP_QUERY, STEP_PAGES_CHARS)
    chosen = [n for n in chosen if pages[n - 1].strip()]  # blank pages are scans or covers
    context = "\n\n".join(f"=== PAGE {n} ===\n{pages[n - 1]}" for n in chosen)
    result = await llm.responses(
        manuals.MODEL,
        f"Manual: {manual.title} ({part.manufacturer} {part.model_no or part.name})\n\n{context}",
        instructions=STEPS_INSTRUCTIONS,
        text=manuals._json_format("coach_steps", _Steps),
        timeout_s=90,
    )
    reply = _Steps.model_validate_json(result.text)
    logger.info(
        "coach: wrote %d steps for %s cost_usd=%s", len(reply.steps), part.id, result.cost_usd
    )
    return {"steps": [s.model_dump() for s in reply.steps], "cost_usd": result.cost_usd}


async def manual_steps(part: Part) -> list[dict] | None:
    """Quote-checked steps from the part's cached manual; None if there is none (a first find
    takes 10-130 s, so this only starts one in the background) or the call fails."""
    if not manuals.indexed(part.id):
        manuals.prefetch(part)
        return None
    manual = manuals.Manual.model_validate(cache.get("manual", part.id))
    pages = cache.get("manual_text", part.id) or []
    key = {"part_id": part.id, "source": manual.source_url}
    try:
        raw = await cache.cached("coach_steps", key, lambda: _write_steps(part, manual, pages))
    except cache.OfflineMiss:
        return None
    except (RuntimeError, httpx.HTTPError, pydantic.ValidationError) as exc:
        logger.warning("coach: step writing for %s failed: %s", part.id, exc)
        return None
    return keep_quoted(raw["steps"], pages) or None


def refusal(text: str) -> str | None:
    """Why F13 says this job is for a licensed trade, else None."""
    key = rules.job_for(text)
    if key is None:
        return None
    spec = rules.JOBS[key]
    if spec.licensed:
        return spec.licensed
    done = jobs.latest(rules.STAGE, lambda r: r.get("job") == key) or {}
    if (done.get("permit") or {}).get("who_can_pull", {}).get("homeowner_allowed") == "no":
        return f"the permit office says a licensed contractor has to pull the {spec.label} permit"
    return None


# --- state -------------------------------------------------------------------------------------


def _path(coach_id: str) -> Path:
    return Path(get_settings().DATA_DIR) / "coach" / f"{coach_id}.json"


def save(state: dict) -> None:
    cache.write_json_atomic(_path(state["coach_id"]), json.dumps(state, indent=2))


def load(coach_id: str) -> dict | None:
    if not re.fullmatch(r"[0-9a-f]{12}", coach_id or ""):
        return None
    path = _path(coach_id)
    return json.loads(path.read_text()) if path.is_file() else None


def current(coach_id: str | None, session_id: str) -> dict | None:
    """The session's running coach: by id, else the newest one on disk (after a restart the
    agent's session memory is gone). ponytail: scans data/coach/, fine for a demo's dozens."""
    state = load(coach_id) if coach_id else None
    if state is None:
        mine = [
            s
            for p in (Path(get_settings().DATA_DIR) / "coach").glob("*.json")
            if (s := json.loads(p.read_text())).get("session_id") == session_id
        ]
        state = max(mine, key=lambda s: s["created_at"], default=None)
    return state if state and state["status"] == "active" else None


def _note(session_id: str, text: str) -> None:
    """A notebook `note` (report + packet show it). Same file rule as app._notebook_path."""
    safe = re.sub(r"[^a-zA-Z0-9_-]", "_", session_id)
    path = Path(get_settings().DATA_DIR) / "notebook" / f"{safe}.json"
    entries = report._notebook_entries(session_id) or []
    entry = {"type": "note", "text": text, "source": "coach"}
    cache.write_json_atomic(path, json.dumps([*entries, entry], indent=2))


# --- speaking and moving -------------------------------------------------------------------------


def step_line(state: dict) -> str:
    """The current step, spoken word for word (the page goes on screen, not into speech)."""
    s, n = state["steps"][state["i"]], len(state["steps"])
    line = f"Step {s['i'] + 1} of {n}: {s['say']}"
    if s["tool"] == "drill":
        line += " Before you drill, point at the spot and say check it."
    return line


def _step_action(state: dict) -> dict:
    s = state["steps"][state["i"]]
    pdf = state["pdf_url"]
    return {
        "name": "coach_step",
        "args": {
            "coach_id": state["coach_id"],
            "i": s["i"],
            "of": len(state["steps"]),
            "say": s["say"],
            "page": s["page"],
            "quote": s["quote"],
            "pdf_url": f"{pdf}#page={s['page']}" if pdf and s["page"] else pdf,
            "tool": s["tool"],
            "checks": [c["question"] for c in s["checks"]],
        },
    }


def _out(state: dict, spoken: str, actions: list[dict], **extra: Any) -> dict:
    return {
        "coach_id": state["coach_id"],
        "i": state["i"],
        "status": state["status"],
        "spoken": spoken,
        "actions": actions,
        **extra,
    }


def view(state: dict) -> dict:
    """`GET /coach/{id}`: the whole state plus the current step, for a reconnecting headset."""
    line = step_line(state) if state["status"] == "active" else "That job's done."
    actions = [_step_action(state)] if state["status"] == "active" else []
    return {**state, **_out(state, line, actions)}


async def start(session_id: str, part: Part | None, job: str | None = None) -> dict:
    """A new coach for the part (its manual first) or a template job. Raises CantCoach."""
    if part and job and job_for(part.name) != job:  # "teach me the faucet" with the AC selected
        part = None
    text = part.name if part else JOBS[job]["label"] if job in JOBS else ""
    why = refusal(text)
    if why:
        raise CantCoach(
            f"I won't coach this one: {why}. I can find who installs it near you.", refused=True
        )
    steps = await manual_steps(part) if part else None
    source = "manual" if steps else "template"
    key = job or job_for(text)
    if not steps and key:
        steps = template_steps(key)
    if not steps:
        raise CantCoach(NO_STEPS)
    label = f"{part.manufacturer or ''} {part.model_no or part.name}".strip() if part else ""
    state = {
        "coach_id": uuid.uuid4().hex[:12],
        "session_id": session_id,
        "job": key,
        "label": label or JOBS[key]["label"],
        "part_id": part.id if part else None,
        "source": source,
        "pdf_url": f"/parts/{part.id}/manual.pdf" if source == "manual" else None,
        "steps": steps,
        "i": 0,
        "status": "active",
        "done": {},  # step index -> "camera" | "said"
        "created_at": time.time(),
    }
    save(state)
    whence = " from its manual" if source == "manual" else ""
    spoken = f"Let's install the {state['label']}: {len(steps)} steps{whence}. {step_line(state)}"
    started = {
        "name": "coach_started",
        "args": {
            "coach_id": state["coach_id"],
            "job": key,
            "label": state["label"],
            "source": source,
            "steps": [{"i": s["i"], "say": s["say"], "page": s["page"]} for s in steps],
            "label_note": LABEL,
        },
    }
    return _out(state, spoken, [started, _step_action(state)], job=key, source=source, steps=steps)


def _finish(state: dict) -> tuple[str, list[dict]]:
    state["status"] = "done"
    state["i"] = len(state["steps"]) - 1
    checked = sum(v == "camera" for v in state["done"].values())
    said = sum(v == "said" for v in state["done"].values())
    _note(
        state["session_id"],
        f"Install coach, {state['label']}: all {len(state['steps'])} steps, {checked} checked "
        f"by camera, {said} on the user's word.",
    )
    spoken = f"That was the last step. {checked} checked by camera, {said} on your word."
    return spoken, [
        {
            "name": "coach_done",
            "args": {"coach_id": state["coach_id"], "checked": checked, "overridden": said},
        }
    ]


def _advance(state: dict, how: str) -> tuple[str, list[dict]]:
    """Mark the current step done (`camera` or `said`) and move on."""
    i = state["i"]
    if str(i) not in state["done"]:  # back, then next again: keep how it was first done
        state["done"][str(i)] = how
    if state["done"][str(i)] == how == "said":
        _note(
            state["session_id"],
            f"Install coach, {state['label']} step {i + 1}: the user said it's done; "
            "not checked by camera.",
        )
    if i + 1 >= len(state["steps"]):
        return _finish(state)
    state["i"] = i + 1
    return step_line(state), [_step_action(state)]


def move(state: dict, how: str = "next") -> dict:
    """ "next" / "I did it" / "skip" (the user's word wins), "back", "repeat"."""
    if state["status"] != "active":
        return _out(state, "That job's done.", [])
    if how == "next":
        spoken, actions = _advance(state, "said")
    else:
        if how == "back":
            state["i"] = max(0, state["i"] - 1)
        spoken, actions = step_line(state), [_step_action(state)]
    save(state)
    return _out(state, spoken, actions)


# --- checking a step on a frame --------------------------------------------------------------

CHECK_PROMPT = (
    "This is one photo from a headset camera. Answer each question only from what is visible "
    "in this photo. answer: 'yes' or 'no' when the photo shows it; 'cant_see' when what the "
    "question is about is out of view, hidden, or too small to judge. evidence: a few words on "
    "what you see that decides it. box: where that evidence is, as [x0, y0, x1, y1] in percent "
    "of the image width and height, or null."
)
DRILL_PROMPT = (
    " electrical: every electrical outlet (receptacle) and every light switch visible in the "
    "photo, with kind and box (percent); an empty list if there are none."
)


class _Answer(pydantic.BaseModel):
    id: str
    answer: Literal["yes", "no", "cant_see"]
    evidence: str
    box: list[float] | None


class _Electrical(pydantic.BaseModel):
    kind: Literal["outlet", "switch"]
    box: list[float]


class _Reply(pydantic.BaseModel):
    answers: list[_Answer]
    electrical: list[_Electrical] = []


def _schema(ids: list[str], drill: bool) -> dict:
    answer = {
        "type": "object",
        "properties": {
            "id": {"type": "string", "enum": ids},
            "answer": {"type": "string", "enum": ["yes", "no", "cant_see"]},
            "evidence": {"type": "string"},
            "box": {"anyOf": [{"type": "array", "items": {"type": "number"}}, {"type": "null"}]},
        },
    }
    props: dict[str, Any] = {"answers": {"type": "array", "items": answer}}
    if drill:
        elec = {
            "type": "object",
            "properties": {
                "kind": {"type": "string", "enum": ["outlet", "switch"]},
                "box": {"type": "array", "items": {"type": "number"}},
            },
        }
        props["electrical"] = {"type": "array", "items": elec}
    return llm._strict_schema({"type": "object", "properties": props})


async def _ask(step: dict, jpg_b64: str) -> dict:
    drill = step["tool"] == "drill"
    questions = "\n".join(f"{c['id']}: {c['question']}" for c in step["checks"])
    text = f"{CHECK_PROMPT}{DRILL_PROMPT if drill else ''}\n\nQuestions:\n{questions}"
    start = time.monotonic()
    try:
        resp = await llm.chat(
            "coach_vision",
            [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": text},
                        {
                            "type": "image_url",
                            "image_url": {"url": f"data:image/jpeg;base64,{jpg_b64}"},
                        },
                    ],
                }
            ],
            response_format={
                "type": "json_schema",
                "json_schema": {
                    "name": "StepCheck",
                    "strict": True,
                    "schema": _schema([c["id"] for c in step["checks"]], drill),
                },
            },
            temperature=0,
            max_retries=0,  # one call = one bill
            timeout=30,
        )
        reply = _Reply.model_validate_json(resp.choices[0].message.content or "")
    except (openai.OpenAIError, httpx.HTTPError, pydantic.ValidationError) as exc:
        raise CheckFailed(str(exc)) from exc
    cost = llm.cost_usd(resp.usage)
    return {
        **reply.model_dump(),
        "cost_usd": round(cost, 5) if cost is not None else None,
        "latency_s": round(time.monotonic() - start, 2),
    }


def above(px: list[float], box: list[float]) -> bool:
    """Drill point in the column straight above (or on) an outlet/switch box, all 0-1."""
    x0, _, x1, y1 = box
    margin = SIDE_MARGIN * (x1 - x0)
    return x0 - margin <= px[0] <= x1 + margin and px[1] <= y1


def judge(step: dict, reply: dict, drill_px: list[float] | None = None) -> tuple[str, list, dict]:
    """(verdict, per-check results, hazard or {}) from the model's answers, all in code."""
    answers = {a["id"]: a for a in reply.get("answers") or []}
    elec = reply.get("electrical") or []
    scale = survey.box_scale([b for b in [a["box"] for a in answers.values()] if b])
    scale = max(scale, survey.box_scale([e["box"] for e in elec]))
    results = []
    for c in step["checks"]:
        a = answers.get(c["id"]) or {"answer": "cant_see", "evidence": "", "box": None}
        results.append(
            {
                **c,
                "answer": a["answer"],
                "evidence": a["evidence"],
                "box": survey.normalise_box(a["box"], scale) if a["box"] else None,
                "ok": a["answer"] == c["expect"],
            }
        )
    hazard: dict = {}
    if step["tool"] == "drill":
        px = drill_px or [0.5, 0.5]  # the crosshair: where the user is looking
        for e in elec:
            box = survey.normalise_box(e["box"], scale)
            if box and above(px, box):
                hazard = {"kind": e["kind"], "box": box, "drill_px": px}
                break
    if hazard:
        verdict = "stop"
    elif any(r["answer"] not in ("cant_see", r["expect"]) for r in results):
        verdict = "not_yet"
    elif any(r["answer"] == "cant_see" for r in results):
        verdict = "look"
    else:
        verdict = "passed"
    return verdict, results, hazard


def _frame_bytes(jpg_b64: str) -> bytes:
    try:
        raw = base64.b64decode(jpg_b64, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ValueError("frame is not valid base64") from exc
    if not raw.startswith(b"\xff\xd8"):
        raise ValueError("frame is not a JPEG")
    if len(raw) > vision.MAX_FRAME_BYTES:
        raise ValueError(f"frame is over {vision.MAX_FRAME_BYTES} bytes")
    return raw


async def check(
    state: dict, jpg_b64: str, drill_px: list[float] | None = None, frame_id: str | None = None
) -> dict:
    """Check the current step on one frame. ValueError on a bad frame or drill point;
    CheckFailed when the call fails. OFFLINE with no cached answer: "say I did it"."""
    if state["status"] != "active":
        return _out(state, "That job's done.", [])
    if drill_px is not None and (len(drill_px) != 2 or not all(0 <= v <= 1 for v in drill_px)):
        raise ValueError("drill_px must be [x, y] in 0-1")
    raw = _frame_bytes(jpg_b64)
    step = state["steps"][state["i"]]
    if not step["checks"]:
        return _out(state, NO_CHECKS, [], verdict="no_checks")
    key = {
        "checks": [c["question"] for c in step["checks"]],
        "drill": step["tool"] == "drill",
        "frame": hashlib.sha1(raw).hexdigest(),
    }
    fresh = cache.get("coach_check", key) is None
    try:
        reply = await cache.cached("coach_check", key, lambda: _ask(step, jpg_b64))
    except cache.OfflineMiss:
        return _out(state, OFFLINE_LINE, [], verdict="offline")
    verdict, results, hazard = judge(step, reply, drill_px)
    i = state["i"]
    shown = {
        "name": "coach_check",
        "args": {
            "coach_id": state["coach_id"],
            "i": i,
            "frame_id": frame_id,
            "verdict": verdict,
            "results": results,
        },
    }
    actions = [shown]
    lead = ""
    if step["tool"] == "drill" and not hazard:
        lead = f"I see no outlet or switch straight below that spot. {WIRE_HINT} "
    if verdict == "stop":
        kind = hazard["kind"]
        spoken = (
            f"Stop: that spot is straight above {'an' if kind == 'outlet' else 'a'} {kind}. "
            f"Wires usually run up from it; move about 15 centimetres sideways. {WIRE_HINT}"
        )
        actions.append(
            {"name": "coach_stop", "args": {"coach_id": state["coach_id"], "i": i, **hazard}}
        )
    elif verdict == "not_yet":
        bad = next(r for r in results if r["answer"] not in ("cant_see", r["expect"]))
        spoken = f"{lead}Not yet: {(bad['evidence'] or bad['question']).rstrip('.')}."
    elif verdict == "look":
        where = next(r for r in results if r["answer"] == "cant_see")["look_at"]
        spoken = f"{lead}I can't see that from here. {where or 'Point the camera at it.'}"
    else:
        line, more = _advance(state, "camera")
        spoken, actions = f"{lead}Looks right. {line}", actions + more
        save(state)
    shown["args"]["spoken"] = spoken
    cost = reply.get("cost_usd") if fresh else 0.0
    return _out(state, spoken, actions, verdict=verdict, results=results, cost_usd=cost)
