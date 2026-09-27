"""Multimodal command agent: text now, transcribed voice over the same path later
(plan §4b.1, design.md "Agent"). One tool-calling loop for both.

Persona: a terse nautical "quartermaster" riding the user's hip. Replies are <=2 short
sentences, spoken aloud. It never authorizes a payment -- `start_checkout` only opens the
hold-to-pay panel; the real charge is `POST /checkout`, which the headset calls after the
user's own pinch-hold (design.md "Agent").

Session state lives in a module dict keyed by `session_id` (no DB, matches jobs.py). History
is trimmed hard: Groq's free tier is 8K tokens/min, so the system prompt + tool schemas below
are kept compact and only the last ~8 messages ride along.
"""

import asyncio
import base64
import binascii
import json
import logging
import re
import time
import uuid
from collections import OrderedDict
from dataclasses import dataclass, field
from datetime import timedelta
from typing import Any
from urllib.parse import quote as url_quote

import httpx
import openai

from server import (
    assets,
    bom,
    booth,
    cache,
    coach,
    coverage,
    finish,
    flythrough,
    imagine,
    intel,
    jobs,
    labels,
    mandate,
    manuals,
    packet,
    plan,
    postcard,
    quote,
    replace,
    replace_job,
    report,
    rules,
    runjob,
    safety,
    scene_digest,
    search,
    sellers,
    sites,
    structure_measure,
    survey,
    tape_survey,
    vision,
)
from server.config import get_settings
from server.keys import KeysExhausted
from server.llm import chat
from server.models import Opening, SearchRequest

logger = logging.getLogger(__name__)

MAX_TEXT_LEN = 500
MAX_HISTORY = 8
MAX_TURNS = 3
MAX_SESSIONS = 256  # ponytail: simple LRU cap, no persistence -- a restart clears sessions anyway
SURVEY_WAIT_S = 20  # fast surveys take 9-15 s; slower ones finish via GET /scene/survey/{id}
MAX_PENDING = 16  # tape survey/check_slope requests awaiting the headset's /agent/observe report

SYSTEM_PROMPT = (
    "You are the Quartermaster: a terse nautical voice riding the user's tool belt in an AR "
    "headset. Reply in at most two short sentences, meant to be spoken aloud. Use the tools to "
    "search for parts or local installers, answer what the camera sees, answer install "
    "questions from the part's manual, list sellers, and queue actions the headset performs. "
    "You never authorize a payment yourself -- start_checkout only opens the hold-to-pay panel "
    "for the user's own pinch. Numbers come only from tool results or the notebook. Never "
    "estimate a size yourself."
)

TOOLS: list[dict[str, Any]] = [
    {
        "type": "function",
        "function": {
            "name": "find_part",
            "description": "Search the supply shops for a new part (any 'find/get/need a ...' "
            "request, even 'for this gutter'). Never for buying the selected part -- that's "
            "start_checkout. Runs in the background; returns a job id.",
            "parameters": {
                "type": "object",
                "properties": {"query": {"type": "string", "description": "e.g. 'gutter hanger'"}},
                "required": ["query"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ask_scene",
            "description": "Only for questions about what the camera sees ('what is this?', "
            "'is it damaged?'). Never for finding parts.",
            "parameters": {
                "type": "object",
                "properties": {"question": {"type": "string"}},
                "required": ["question"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "label_view",
            "description": "Label the things in the camera view ('what am I looking at?', "
            "'label this'), each tappable to shop for it.",
            "parameters": {
                "type": "object",
                "properties": {"focus": {"type": ["string", "null"]}},
                "required": ["focus"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "show_sellers",
            "description": "List top sellers for the currently selected part.",
            "parameters": {
                "type": "object",
                "properties": {"sort": {"type": "string", "enum": ["cheapest", "fastest", "best"]}},
                "required": ["sort"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "select_candidate",
            "description": "Pick a candidate part by its 0-based position in the last results.",
            "parameters": {
                "type": "object",
                "properties": {"index": {"type": "integer"}},
                "required": ["index"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "set_finish",
            "description": "Show the selected part in another colour/finish (re-renders it).",
            "parameters": {
                "type": "object",
                "properties": {"name": {"type": "string"}},
                "required": ["name"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "place_array",
            "description": "Repeat the selected part along the last tape line.",
            "parameters": {
                "type": "object",
                "properties": {"spacing_mm": {"type": ["number", "null"]}},
                "required": ["spacing_mm"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "start_checkout",
            "description": "Buy / check out / pay for the selected part: opens the hold-to-pay "
            "panel for one of its sellers. Never pays by itself.",
            "parameters": {
                "type": "object",
                "properties": {
                    "seller_index": {
                        "type": ["integer", "null"],
                        "description": "0-based seller; null = the recommended seller",
                    }
                },
                "required": ["seller_index"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "equip_tool",
            "description": "Swap the tool in the user's hand.",
            "parameters": {
                "type": "object",
                "properties": {
                    "tool": {
                        "type": "string",
                        "enum": [
                            "tape",
                            "level",
                            "protractor",
                            "plumb",
                            "area",
                            "notebook",
                            "part",
                        ],
                    }
                },
                "required": ["tool"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "add_note",
            "description": "Log a note in the notebook.",
            "parameters": {
                "type": "object",
                "properties": {"text": {"type": "string"}},
                "required": ["text"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "what_else",
            "description": "'What else do I need?' -- lists install accessories (screws, "
            "sealant, end caps, etc.) for the parts placed so far, each with a seller, so they "
            "can all go into the same cart.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "reimagine_view",
            "description": "Show an AI picture of the current view restyled ('what would navy "
            "cabinets look like?'). Visual preview only; not for buying.",
            "parameters": {
                "type": "object",
                "properties": {"prompt": {"type": "string", "description": "the change"}},
                "required": ["prompt"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "refine_reimagine",
            "description": "Change the AI picture already on screen, keeping its earlier "
            "changes ('darker blue', 'add brass pulls', 'no, lighter').",
            "parameters": {
                "type": "object",
                "properties": {"prompt": {"type": "string", "description": "the change"}},
                "required": ["prompt"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "find_installer",
            "description": "'Who installs this near me?' -- local installers/contractors for a "
            "job, each with evidence links. Takes ~15 s unless cached.",
            "parameters": {
                "type": "object",
                "properties": {
                    "trade": {
                        "type": "string",
                        "description": "e.g. 'K-style gutter installation'",
                    },
                    "location": {"type": ["string", "null"], "description": "null = user's area"},
                },
                "required": ["trade", "location"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "make_report",
            "description": "'Send me the report': opens the printable site-walk report "
            "(measurements, pins, parts, orders, summary) for this session.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "see_it_installed",
            "description": "Show a photo of the selected part installed in the user's own space "
            "('show me what it'll look like'). Visual preview only; not for buying.",
            "parameters": {
                "type": "object",
                "properties": {
                    "placement": {
                        "type": "string",
                        "description": "where it goes, e.g. 'left of the downspout'; '' if unsaid",
                    }
                },
                "required": ["placement"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "check_safety",
            "description": "'Is this safe / recalled?' -- recall and defect check for a part.",
            "parameters": {
                "type": "object",
                "properties": {
                    "part_id": {"type": ["string", "null"], "description": "null = selected"}
                },
                "required": ["part_id"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "plan_placement",
            "description": "Plan where parts go on the scanned room and draw it: LED strip "
            "under the cabinets, hangers along an edge every N cm, centre a part on something.",
            "parameters": {
                "type": "object",
                "properties": {"request": {"type": "string", "description": "the user's words"}},
                "required": ["request"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "walk_in_preview",
            "description": "Render a short AI video walking into the current view restyled "
            "('walk me into it with navy cabinets'). Visual preview only; about a minute.",
            "parameters": {
                "type": "object",
                "properties": {"prompt": {"type": "string", "description": "the change"}},
                "required": ["prompt"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ask_manual",
            "description": "Install questions about the selected part ('what drill bit for the "
            "anchors?', 'how far from the ceiling?'), answered from its install manual with "
            "a page number.",
            "parameters": {
                "type": "object",
                "properties": {
                    "question": {"type": "string"},
                    "part_id": {"type": ["string", "null"], "description": "null = selected"},
                },
                "required": ["question", "part_id"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "survey_condition",
            "description": "Survey the loaded building's condition (damage, problems) from the "
            "drone footage and pin them on the model ('survey the roof', 'anything wrong with "
            "it?', 'is the roof damaged?'). Never for sizes or lengths: that's measure.",
            "parameters": {
                "type": "object",
                "properties": {
                    "focus": {"type": "string", "enum": ["roof", "gutters", "facade", "all"]},
                    "careful": {"type": "boolean", "description": "slower, tighter boxes"},
                },
                "required": ["focus", "careful"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "set_limits",
            "description": "Narrow what may be bought: budget cap, delivery deadline, seller "
            "policy. Can only tighten existing limits. Never pays.",
            "parameters": {
                "type": "object",
                "additionalProperties": False,
                "properties": {
                    "max_total_usd": {"type": ["number", "null"]},
                    "deliver_by": {
                        "type": ["string", "null"],
                        "description": "ISO date or weekday name",
                    },
                    "seller_policy": {
                        "type": ["string", "null"],
                        "enum": ["cheapest", "fastest", "best", None],
                    },
                },
                "required": ["max_total_usd", "deliver_by", "seller_policy"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "survey",
            "description": "Make the headset measure every object of one kind with its real tape "
            "(it shows its work and logs each to the notebook). Use for 'measure/size up "
            "all/every/each <thing>'. Sizes only: a roof/gutter/facade condition check is "
            "survey_condition. Never guess sizes yourself.",
            "parameters": {
                "type": "object",
                "additionalProperties": False,
                "properties": {
                    "label": {"type": "string", "enum": list(tape_survey.LABELS)},
                    "where": {"type": "string", "enum": list(tape_survey.WHERES)},
                    "measure": {"type": "string", "enum": list(tape_survey.MEASURES)},
                },
                "required": ["label", "where", "measure"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "measure",
            "description": "Measure one thing with the headset's real tape: 'measure the roof end "
            "to end', 'how tall is the platform?', 'how wide is that window?', 'the length of the "
            "wall'. Sizes only, never a condition check. The number comes from the tape.",
            "parameters": {
                "type": "object",
                "additionalProperties": False,
                "properties": {
                    "thing": {
                        "type": ["string", "null"],
                        "description": "what to measure, as said ('top roof'); null = pointed at",
                    },
                    "dimension": {
                        "type": "string",
                        "enum": ["length", "width", "height", "depth", "size"],
                    },
                },
                "required": ["thing", "dimension"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "check_slope",
            "description": "Measure the fall along a gutter, sill or ledge and judge drainage "
            "(gutter rule: at least 1/4 inch per 10 ft).",
            "parameters": {
                "type": "object",
                "additionalProperties": False,
                "properties": {
                    "target": {"type": "string", "enum": list(tape_survey.SLOPE_TARGETS)}
                },
                "required": ["target"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "survey_query",
            "description": "Answer a question about the last survey's results (widest, count by "
            "size, does X fit).",
            "parameters": {
                "type": "object",
                "additionalProperties": False,
                "properties": {"question": {"type": "string"}},
                "required": ["question"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "coach_capture",
            "description": "'What did I miss?' after a drone scan: which sides of the building "
            "the flight covered and the legs to fly next, from the loaded scene's cameras.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "check_rules",
            "description": "Permits, code editions, rebates and tax credits for the job (the "
            "selected part, or a named job) at the user's address. Takes ~20 s unless cached.",
            "parameters": {
                "type": "object",
                "properties": {
                    "job": {"type": ["string", "null"], "enum": [*rules.JOBS, None]},
                    "address": {"type": ["string", "null"], "description": "null = user's"},
                },
                "required": ["job", "address"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "check_quote",
            "description": "'Check this quote': reads the contractor's quote the user holds up "
            "to the camera, checks its math, compares equipment prices with verified sellers, "
            "and flags a missing permit, rebates and recalled models.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "send_packet",
            "description": "'Send it to my contractor' / 'share the job': makes the job packet "
            "PDF and a public link with a QR, valid for `days` (default 7).",
            "parameters": {
                "type": "object",
                "properties": {"days": {"type": ["integer", "null"]}},
                "required": ["days"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "start_coach",
            "description": "'Teach me to install this' / 'walk me through it': step-by-step "
            "install coaching for the selected part (from its manual) or a named job, each step "
            "checked on the camera.",
            "parameters": {
                "type": "object",
                "properties": {"job": {"type": ["string", "null"], "enum": [*coach.JOBS, None]}},
                "required": ["job"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "coach_step",
            "description": "While coaching: 'next', 'I did it', 'skip' (move on; the user's word "
            "counts), 'back', 'repeat'.",
            "parameters": {
                "type": "object",
                "properties": {"move": {"type": "string", "enum": ["next", "back", "repeat"]}},
                "required": ["move"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "check_step",
            "description": "While coaching: 'check it', 'check my work', 'done': checks the "
            "current step on the camera view.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "revoke_packet",
            "description": "'Take the packet down': revokes the packet's public link.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "run_job",
            "description": "'Do the whole job' / 'handle it' / 'take care of it' / 'fix the "
            "roof': survey the scan (or take the stated need), find and pick the part, check "
            "recalls, permits and rebates, preview it installed, make the job packet, then open "
            "the pay panel. Runs in the background; never pays.",
            "parameters": {
                "type": "object",
                "properties": {
                    "goal": {
                        "type": ["string", "null"],
                        "description": "the part or fix the user named; null = survey the scan",
                    }
                },
                "required": ["goal"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "replace_component",
            "description": "Replace a part of the scanned scene with a new one that fits, on its "
            "own from start to finish: take it out, tape the gap, check the scan's scale, search "
            "for ones that fit the gap, pick the best, build its 3D model and put it in. For "
            "'replace the dishwasher', 'swap out the range for an induction one', 'the fridge is "
            "broken, get me a new one', 'get a counter-depth fridge in here'. Runs in the "
            "background; never pays.",
            "parameters": {
                "type": "object",
                "properties": {
                    "component": {
                        "type": ["string", "null"],
                        "description": "the scene part to replace, as named ('dishwasher', "
                        "'stove', 'fridge', 'base cabinet'); null = the part the user points at",
                    },
                    "query": {
                        "type": ["string", "null"],
                        "description": "what to search for when the user asked for a kind "
                        "('30 inch induction range', 'counter-depth fridge'); null = the same kind",
                    },
                    "max_price_usd": {"type": ["number", "null"]},
                },
                "required": ["component", "query", "max_price_usd"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "show_model",
            "description": "Model view: put a scanned site on the table as a model ('show me "
            "the gym model', 'open the hospital scan', 'the GT tower').",
            "parameters": {
                "type": "object",
                "properties": {"site": {"type": "string", "description": "the site's name or id"}},
                "required": ["site"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "share_design",
            "description": "'Add it to the wall' / 'share my design': puts this session's "
            "design on the booth wall with a preview card. Only when the user asked for it. "
            "It never posts to X: that needs the user's hold-to-post gesture.",
            "parameters": {
                "type": "object",
                "properties": {
                    "name": {"type": ["string", "null"], "description": "what to call them"},
                    "phrase": {"type": "string", "description": "the user's exact words"},
                },
                "required": ["name", "phrase"],
            },
        },
    },
]

# e2e: measure and replace (server/replace.py) -- fast path only, never offered to the LLM.
_REPLACE_TOOLS = (
    "remove_component",
    "restore_component",
    "measure_cavity",
    "scale_gap",
    "find_in_gap",
    "place_in_gap",
    "cycle_in_gap",
    "replace_flow",
    "undo_edit",
)
# set_finish (F11) and start_checkout (F6) run server-side now; these just echo to the headset.
_CLIENT_ONLY_TOOLS = {"place_array", "equip_tool", "stop_survey"}
# The app lane's tape tools (the headset does the work later and reports to /agent/observe) and
# set_limits: their spoken acknowledgement ends the turn (they join the early-exit tuple in
# _handle_command); when one turn calls several, their lines are joined in call order.
_OWN_LINE_TOOLS = ("survey", "check_slope", "set_limits", "measure")
FINISH_TIMEOUT_S = 20  # a first render is ~8 s; past this the client tints and the render caches

_CANNED_REPLY = {
    "find_part": "Searching the supply shops.",
    "ask_scene": "Looked it over.",
    "label_view": "Labels are up.",
    "show_sellers": "Sellers pulled up.",
    "select_candidate": "Locked that one in.",
    "set_finish": "Finish changed.",
    "place_array": "Array placed.",
    "start_checkout": "Hold-to-pay panel's open.",
    "equip_tool": "Tool swapped.",
    "add_note": "Noted.",
    "what_else": "Checking the extras.",
    "reimagine_view": "Here's the sketch. Pinch to flip before and after.",
    "refine_reimagine": "Changed it. Say undo to step back.",
    "undo_reimagine": "Stepped back.",
    "start_over_reimagine": "Back to the original photo.",
    "find_installer": "Here's who installs it nearby.",
    "make_report": "Report's on the board.",
    "see_it_installed": "Here's how it'll look installed. Illustration only, not to scale.",
    "check_safety": "Safety check done.",
    "plan_placement": "Plan's up.",
    "walk_in_preview": flythrough.RENDERING_LINE,
    "ask_manual": "Checked the manual.",
    "survey_condition": "Looking over the footage.",
    "fix_pin": "Searching the supply shops.",
    "coach_capture": "Coverage is up.",
    "check_rules": "Checking the rules and the rebates.",
    "check_quote": "Quote checked.",
    "send_packet": "Packet's up.",
    "revoke_packet": "Packet's down.",
    "run_job": "On it.",
    "start_coach": "Coaching started.",
    "coach_step": "Next step.",
    "check_step": "Checked.",
    "share_design": "It's on the booth wall.",
    "survey": "Surveying.",
    "measure": "Measure's ready.",
    "check_slope": "Checking the fall.",
    "survey_query": "Checked the survey.",
    "stop_survey": "Stopping.",
    "set_limits": "Limits set.",
    # e2e: measure and replace (server/replace.py, _replace_route)
    "remove_component": "Took it out.",
    "restore_component": "It's back.",
    "measure_cavity": "Taping the gap.",
    "scale_gap": "Scale set.",
    "find_in_gap": "Searching for one that fits the gap.",
    "place_in_gap": "Putting it in.",
    "cycle_in_gap": "Next one.",
    "replace_flow": "Done.",
    "undo_edit": "Undone.",
    # autonomy: one sentence, the whole replace (server/replace_job.py); Model view by voice
    "replace_component": "On it.",
    "show_model": "Here's the model.",
}
# Said when a provider fails (every Groq key limited and no fallback, xAI down, ...): a reply
# with no actions instead of an HTTP 500 (the voice test's p01).
BUSY_REPLY = "The assistant's busy; try again in a moment."
_PROVIDER_ERRORS = (KeysExhausted, openai.APIError, httpx.HTTPError)
# Their `spoken` is the step text: the voice relay says it word for word (force_message).
COACH_TOOLS = ("start_coach", "coach_step", "check_step")
RULES_WAIT_S = 20.0  # show_rules in this reply if the check lands in time, else on the next one

# Spoken when OFFLINE and the fast-path matcher didn't catch the command -- the LLM path never
# runs (no chat() call), so this never depends on the uplink. server/warm.py pre-caches its TTS.
OFFLINE_REPLY = "Offline: try a cached part or the crate menu."


@dataclass
class Session:
    id: str = ""
    history: list[dict[str, Any]] = field(default_factory=list)
    measurement: dict[str, Any] | None = None
    job_id: str | None = None
    candidate_ids: list[str] = field(default_factory=list)
    selected_part_id: str | None = None
    notes: list[str] = field(default_factory=list)
    placed: list[dict[str, Any]] = field(default_factory=list)  # [{part_id, count}], see what_else
    warned: set[str] = field(default_factory=set)  # part ids whose recall was spoken at checkout
    plan: dict[str, Any] | None = None  # last plan_placement result, for "place them"
    flythrough_job_id: str | None = None  # a walk-in clip still rendering, see _flythrough_ready
    survey_id: str | None = None  # last condition survey, for "find a fix for pin f1"
    rules_check_id: str | None = None  # a rules check still running, see _rules_ready
    coach_id: str | None = None  # the install coach running, see _coach_tool
    # the refine stack: {site, frame_id, camera, before_url, steps: [{prompt, id, drift}]}
    reimagine: dict[str, Any] | None = None
    rv_fresh: bool = False  # the last tool showed a reimagine step: a walk-in starts from it
    # The app lane's tape (survey/check_slope). Not F10's condition survey (`survey_id`).
    site: str | None = None  # the scene package the headset has open (context "site")
    scale: float = 1.0  # the headset's scale calibration (context "scale")
    last_text: str = ""  # the command being handled, recorded with tape survey requests
    pending: OrderedDict[str, dict[str, Any]] = field(default_factory=OrderedDict)
    tape_survey: dict[str, Any] | None = None  # the last tape survey reported to /agent/observe
    # e2e measure and replace: the scene parts taken out (oldest first; the headset's `removed`
    # context wins when sent), its `cavity` context (the open gap, its tape readings) and the
    # candidate standing in the gap (an index into candidate_ids).
    removed: list[str] = field(default_factory=list)
    cavity: dict[str, Any] | None = None
    gap_index: int | None = None
    # assetgen: the opening the headset taped (context `opening`: a window's width x height);
    # "find a window frame that fits" searches for it (SearchRequest.opening).
    opening: dict[str, Any] | None = None
    # asset-mode: how the headset wants 3D models made (context `asset_mode`: "hf" | "llm_scad" |
    # "auto", Settings ▸ 3D models); its searches resolve candidates that way.
    asset_mode: str = "auto"


_sessions: OrderedDict[str, Session] = OrderedDict()


def _get_session(session_id: str) -> Session:
    """`_sessions[session_id]`, creating it if new -- LRU-capped at `MAX_SESSIONS` (an
    unbounded number of headsets/tabs could otherwise grow this dict forever)."""
    session = _sessions.get(session_id)
    if session is None:
        session = Session(id=session_id)
        _sessions[session_id] = session
        if len(_sessions) > MAX_SESSIONS:
            _sessions.popitem(last=False)  # evict the least-recently-used session
    else:
        _sessions.move_to_end(session_id)
    return session


# --- fast path: small deterministic matcher for unambiguous short commands -------------------

_EQUIP_RE = re.compile(
    r"\b(?:equip|grab)\s+(?:the\s+)?(tape|level|protractor|plumb|area|notebook|part)\b",
    re.IGNORECASE,
)
_SELECT_RE = re.compile(
    r"\b(?:select|pick)\s+(?:the\s+)?(?:number\s+(\d+)|(first|second|third))\b", re.IGNORECASE
)
_SORT_RE = re.compile(r"\b(cheapest|fastest)\b(?:\s+(?:first|one))?[.!]?\s*$", re.IGNORECASE)
_ARRAY_RE = re.compile(
    r"\bevery\s+(\d+(?:\.\d+)?)\s*"
    r"(centimeters?|centimetres?|cm|millimeters?|millimetres?|mm|inches?|inch|in)\b",
    re.IGNORECASE,
)
_WHAT_ELSE_RE = re.compile(r"\bwhat else\b.*\bneed\b", re.IGNORECASE)
_REPORT_RE = re.compile(r"\b(?:send|show|make|give|open)\b.*\breport\b", re.IGNORECASE)
_INSTALLED_RE = re.compile(
    r"\bwhat (?:(?:it|that|this)(?:'ll| will| would)?|(?:will|would) (?:it|that|this)) look like\b"
    r"|\bsee it installed\b",
    re.IGNORECASE,
)
_SAFETY_RE = re.compile(r"\brecall(?:ed|s)?\b|\bis (?:it|this|that) safe\b", re.IGNORECASE)
# "check this quote": before the safety and rules matchers, since the quote check covers both
# ("check this quote for recalls"); after the packet ("send the quote to my contractor").
# Not a manual's quote (F9's `show_manual_answer`, F17's coach steps): "read the quote from
# the manual" goes to the LLM.
_QUOTE_RE = re.compile(
    r"^(?!.*\bmanual\b).*\b(?:check|read|review|look (?:at|over)|go over)\b.*"
    r"\b(?:quote|estimate|bid)s?\b",
    re.IGNORECASE,
)
_RULES_RE = re.compile(r"\b(?:permit|inspection|rebate|tax credit|incentive)s?\b", re.IGNORECASE)
_PLACE_RE = re.compile(r"^\W*(?:ok(?:ay)?\W+)?place (?:them|it|those|these)\b", re.IGNORECASE)
_FIX_PIN_RE = re.compile(r"\bpin\s+(f\d+)\b", re.IGNORECASE)  # what a pinch on a survey pin sends
_SURVEY_RE = re.compile(r"\bsurvey\b(?:.*?\b(roof|gutters?|facade)\b)?", re.IGNORECASE)
# "show it in matte black": needs it/this/that right after "show", so "show me what it'll look
# like" (_INSTALLED_RE) never lands here; "show it in the kitchen" / "in my room" is not a finish.
_FINISH_RE = re.compile(
    r"\bshow\s+(?:it|this|that)\s+in\s+(?!(?:the|my|your|our|place|ar)\b)(?:an?\s+)?(.+?)"
    r"(?:\s+finish)?[.!?]?\s*$",
    re.IGNORECASE,
)
# "what did I miss?" after a drone scan. Not a bare "coverage": "what's the coverage on this
# sealant?" is a part question, so only scan/capture/flight/drone coverage or "show coverage".
_COACH_RE = re.compile(
    r"\bwhat did (?:i|we) miss\b|\bwhich sides (?:did|have|are|of the (?:building|house))\b"
    r"|\bcapture coach\b|\b(?:scan|capture|flight|drone) coverage\b"
    r"|^\W*(?:show (?:me )?(?:the )?)?coverage\W*$",
    re.IGNORECASE,
)
_COACH_START_RE = re.compile(
    r"\bteach me\b|\bwalk me through\b|\bcoach me\b|\bhow do i install (?:it|this|that)\b",
    re.IGNORECASE,
)
# While a coach runs (whole-utterance only, so "check the rebates" never lands here):
_COACH_CHECK_RE = re.compile(
    r"^\W*(?:ok(?:ay)?\W+)?(?:check (?:it|this|that|my work)|(?:all |i'?m )?done)\W*$",
    re.IGNORECASE,
)
_COACH_MOVE_RE = re.compile(
    r"^\W*(?:ok(?:ay)?\W+)?(?:next(?: step)?|skip(?: it| this| that)?|i did it|i've done it"
    # every "back" _UNDO_RE takes, so a running coach owns them (fast-path priority, see
    # _session_fast_intent); "undo" stays the reimagine stack's
    r"|(?:go |step )?back(?: up)?(?: (?:one|one step|a step))?|previous(?: step)?"
    r"|repeat(?: that)?|say (?:that|it) again)\W*$",
    re.IGNORECASE,
)
_REVOKE_PACKET_RE = re.compile(
    r"\b(?:take|pull)\b.*\b(?:packet|link)\b.*\bdown\b|\brevoke\b.*\b(?:packet|link)\b",
    re.IGNORECASE,
)
_SEND_PACKET_RE = re.compile(
    r"\b(?:send|share)\b.*\b(?:packet|contractor|job)\b|\bgive\b.*\bpacket\b", re.IGNORECASE
)
# "Do the whole job": anywhere in the sentence; "handle it" / "take care of it" only as the whole
# command, so "how do I handle it?" stays a question.
_RUN_JOB_RE = re.compile(
    r"\b(?:do|run) the (?:whole|entire) job\b|\brun it end to end\b"
    r"|^\W*(?:(?:ok(?:ay)?|please|just|quartermaster|go ahead and)\W+)*"
    r"(?:handle|take care of) (?:it|this|that|everything)\b[^?]*$",
    re.IGNORECASE,
)
_LABEL_RE = re.compile(
    r"\b(?:label (?:this|that|these|it|everything|the view)|what am i looking at"
    r"|what(?:'s| is) (?:all )?this stuff)\b",
    re.IGNORECASE,
)
# Only while a reimagine is on screen, and only when no fast path above matched (see
# _session_fast_intent): "undo", "start over", and short edits like "darker blue" / "no,
# lighter" / "add brass pulls". "add a note ..." stays with the LLM's add_note.
_UNDO_RE = re.compile(
    r"^\W*(?:undo|go back|step back|back up)(?: (?:that|it|one|one step|a step))?\W*$",
    re.IGNORECASE,
)
_START_OVER_RE = re.compile(
    r"\bstart (?:over|again)\b|\bback to the original\b|^\W*show (?:me )?the original\W*$",
    re.IGNORECASE,
)
_REFINE_RE = re.compile(
    r"^\W*(?:(?:no|now|and|ok(?:ay)?)\W+)*(?:make (?:it|them|that) |a (?:bit|little) |slightly |"
    r"much |even )*(?:darker|lighter|brighter|warmer|cooler|add(?!\s+(?:an?\s+|the\s+)?note)|"
    r"remove|swap|replace|paint)\b",
    re.IGNORECASE,
)
# Consent to the booth wall: the tool only runs on one of these phrases (booth.share's rule).
_SHARE_RE = re.compile(
    # "add it to the wall", not "add floating shelves to the wall" (an F18 refine): the thing
    # added must be the design itself
    r"\b(?:add|put|stick)\s+(?:it|this|that|me|us|(?:my|our|this|the)\s+design)\s+"
    r"(?:on|to|up on)\s+the\s+(?:booth\s+)?wall\b"
    r"|\bshare\s+(?:my|the|this|our)\s+design\b|\bpost\s+(?:it|my design)\b",
    re.IGNORECASE,
)
_SHARE_NAME_RE = re.compile(r"\bas\s+([A-Za-z][\w' -]{0,39}?)[.!]?\s*$", re.IGNORECASE)
# Buying the selected part (live: "buy it from the recommended seller" became a new search).
# buy/purchase/order only with a pronoun object -- "buy a hinge" / "where can I buy ..." stay
# searches for the LLM; "pay for it/now/with ..."; "check out" only on its own or before
# with/from/now/at/using ("check out this hinge" means look at it). Opens the panel only.
_CHECKOUT_RE = re.compile(
    r"\b(?:check\s*-?\s*out(?=\s*(?:$|[.!?,]|\b(?:with|from|now|at|using)\b))"
    r"|(?:buy|purchase|order)\s+(?:it|this|that|these|those|them)\b"
    r"|pay\s+(?:for\s+(?:it|this|that|these|those|them)|now|with)\b)",
    re.IGNORECASE,
)
# Offline only: online, the LLM rewrites "a hanger for this gutter" into a far better query.
_FIND_RE = re.compile(
    r"\b(?:find|get|need)\s+(?:me\s+)?(?:an?\s+|the\s+|some\s+)?(.+?)\s*(?:\bfor\b.*)?[.!?]?\s*$",
    re.IGNORECASE,
)
_QUESTION_RE = re.compile(  # offline only: "what size drill bit ...?" -> ask_manual
    r"^\s*(?:what|how|where|which|when|do|does|can|should|is|are)\b|\?\s*$", re.IGNORECASE
)
# The app lane's tape survey: "measure every cabinet door", "size up all the windows", "measure
# the upper drawers", "measure the window" (the + one = the nearest). An optional qualifier word
# picks `where`. It shares "survey" with F10's _SURVEY_RE: see _match_fast_intent for the rule.
_TAPE_SURVEY_RE = re.compile(
    r"\b(?:measure|survey|size\s+up)\s+(every|all(?:\s+(?:of\s+)?the)?|each|the)\s+"
    r"(?:(?!cabinet\b)(\w+)\s+)?"
    r"(cabinet\s+doors?|doors?|drawers?|windows?|panels?|appliances?)\b",
    re.IGNORECASE,
)
_SURVEY_VISIBLE_RE = re.compile(
    r"\b(?:i|you)\s+can\s+see\b|\bin\s+view\b|\bvisible\b", re.IGNORECASE
)
_SURVEY_WHERE = {
    "upper": "upper",
    "top": "upper",
    "lower": "lower",
    "bottom": "lower",
    "base": "lower",
    "left": "left",
    "right": "right",
    "visible": "visible",
    "nearest": "nearest",
    "closest": "nearest",
}
_WIDTH_RE = re.compile(r"\b(?:widths?|wide)\b", re.IGNORECASE)
_HEIGHT_RE = re.compile(r"\b(?:heights?|tall|high)\b", re.IGNORECASE)
# "is this gutter sloped enough to drain?", "does the sill drain?" -- but "find a gutter drain
# outlet" is a part search, so find/get/need/buy/order keep it off this path.
_SLOPE_WORDS = r"(?:slope[sd]?|sloping|drain(?:s|ing|age)?|fall|pitch(?:ed)?)"
_SLOPE_RE = re.compile(
    rf"\b(gutter|sill|ledge)s?\b.*\b{_SLOPE_WORDS}\b|\b{_SLOPE_WORDS}\b.*\b(gutter|sill|ledge)s?\b",
    re.IGNORECASE,
)
_SHOPPING_RE = re.compile(r"\b(?:find|get|need|buy|order|search|shop)\b", re.IGNORECASE)
# Only a bare "stop" / "cancel" (the whole utterance): aborts a running survey on the headset.
_STOP_RE = re.compile(
    r"^\s*(?:ok(?:ay)?[,\s]+)?(?:stop|cancel|abort)"
    r"(?:\s+(?:it|that|now|the\s+survey|surveying|measuring))?\s*[.!]*\s*$",
    re.IGNORECASE,
)
# Follow-ups on the stored survey, only while there is one: "which is the widest?"
_SURVEY_Q_RE = re.compile(
    r"\b(?:which|what)\b.*\b(?:widest|narrowest|tallest|shortest|largest|biggest|smallest)\b",
    re.IGNORECASE,
)
# Condition words keep a "survey every window's condition" on F10's path (see _match_fast_intent).
_CONDITION_RE = re.compile(
    r"\b(?:condition|damage[ds]?|cracks?|cracked|rot(?:ten|ting)?|rust(?:y|ed|ing)?"
    r"|leak(?:s|y|ing)?)\b",
    re.IGNORECASE,
)
# Purchase limits spoken with any command (brain.md S2): "under $40", "no more than 40 dollars",
# "arriving by Friday", "delivered before 2026-10-02". Digits only; a price needs "$" or
# "dollars"/"bucks" ("under 40 mm" is a size) and caps the order total, so "under $5 each" is
# left alone. Voice can only tighten them (server/mandate.py).
_LIMIT_PRICE_RE = re.compile(
    r"\b(?:under|below|less\s+than|no\s+more\s+than|not\s+more\s+than|up\s+to|at\s+most"
    r"|max(?:imum)?(?:\s+of)?|budget(?:\s+of)?|cap(?:\s+it)?\s+at)\s+"
    # "$1,200" is twelve hundred (Whisper writes thousands with a comma), not "$1" and a stray ",200"
    r"(?:\$\s?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)"
    r"|(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*(?:dollars?|bucks|usd)\b)"
    r"(?![\d.]|\s*(?:each|apiece|per\b|a\s+piece))",  # a unit price is not a cap on the order
    re.IGNORECASE,
)
_LIMIT_DATE_RE = re.compile(
    r"\b(?:arriv\w*|deliver\w*|(?:get\s+(?:it|them)\s+)?here)\s+(by|before|no\s+later\s+than)\s+"
    r"(?:this\s+|next\s+)?(monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow"
    r"|today|\d{4}-\d{2}-\d{2})\b",
    re.IGNORECASE,
)
# Words that leave nothing to do once the limits are cut out ("keep it under $40").
_LIMIT_FILLER = frozenset(
    re.findall(
        r"\S+",
        "keep it that this the total budget limit limits spend spending and please price cost "
        "set a an of only get here make sure must should we i want to be is with for but so ok "
        "okay let lets let's us me my our needs need can you quartermaster all in has have got "
        "gotta will would it's its actually just now then also oh well fine",
    )
)
_ORDINAL_INDEX = {"first": 0, "second": 1, "third": 2}
_UNIT_TO_MM = {
    "cm": 10.0,
    "centimeter": 10.0,
    "centimeters": 10.0,
    "centimetre": 10.0,
    "centimetres": 10.0,
    "mm": 1.0,
    "millimeter": 1.0,
    "millimeters": 1.0,
    "millimetre": 1.0,
    "millimetres": 1.0,
    "in": 25.4,
    "inch": 25.4,
    "inches": 25.4,
}


def _match_fast_intent(text: str) -> tuple[str, dict[str, Any], str] | None:
    """(tool name, args, canned reply) for an unambiguous short command, else None.

    ponytail: digits only ("every 60 cm"), not number words ("every sixty centimetres") --
    that ambiguity is exactly what the LLM path is for. Keep this matcher small.
    """
    if _RUN_JOB_RE.search(text):
        return "run_job", {"goal": None}, _CANNED_REPLY["run_job"]
    # "send the survey/permits to my contractor" is a packet, not a new survey or rules check.
    if _REVOKE_PACKET_RE.search(text):
        return "revoke_packet", {}, _CANNED_REPLY["revoke_packet"]
    if _SEND_PACKET_RE.search(text):
        return "send_packet", {}, _CANNED_REPLY["send_packet"]
    if _SHARE_RE.search(text):
        m = _SHARE_NAME_RE.search(text)
        args = {"name": m.group(1) if m else None, "phrase": text}
        return "share_design", args, _CANNED_REPLY["share_design"]

    # A bare "stop" / "cancel (the survey)" is the whole utterance: it aborts the headset's tape
    # survey, before F10's matcher (which takes any "survey") can start a condition survey.
    if _STOP_RE.search(text):
        return "stop_survey", {}, _CANNED_REPLY["stop_survey"]

    m = _FIX_PIN_RE.search(text)
    if m:
        return "fix_pin", {"pin_id": m.group(1).lower()}, _CANNED_REPLY["fix_pin"]

    # "survey" is two tools. The thing surveyed decides: a structure-object noun (cabinet
    # door(s), door(s), drawer(s), window(s), panel(s), appliance(s)) right after "measure /
    # survey / size up every|all|each|the [qualifier]" is the app lane's tape; roof, gutter(s),
    # facade, building, or any condition word ("condition", "damage", "cracks", "rot", "rust",
    # "leaks") is F10's condition survey. So "survey every cabinet door" measures, "survey the
    # roof" and "survey the windows for damage" triage.
    m = _TAPE_SURVEY_RE.search(text)
    if m and not _CONDITION_RE.search(text):
        return "survey", _survey_args(m, text), _CANNED_REPLY["survey"]

    m = _SURVEY_RE.search(text)
    if m:
        focus = (m.group(1) or "all").lower()
        args = {
            "focus": "gutters" if focus.startswith("gutter") else focus,
            "careful": bool(re.search(r"\bcareful", text, re.IGNORECASE)),
        }
        return "survey_condition", args, _CANNED_REPLY["survey_condition"]

    m = _EQUIP_RE.search(text)
    if m:
        tool = m.group(1).lower()
        return "equip_tool", {"tool": tool}, f"{tool.capitalize()} in hand."

    m = _SELECT_RE.search(text)
    if m:
        index = int(m.group(1)) - 1 if m.group(1) else _ORDINAL_INDEX[m.group(2).lower()]
        return "select_candidate", {"index": index}, "Locked that one in."

    if _CHECKOUT_RE.search(text):
        # seller_index None: _run_tool picks a seller named in the command, else the recommended one.
        args = {"seller_index": None, "hint": text}
        return "start_checkout", args, _CANNED_REPLY["start_checkout"]

    m = _SORT_RE.search(text)
    if m:
        sort = m.group(1).lower()
        return "show_sellers", {"sort": sort}, f"Pulling up sellers, {sort} first."

    m = _ARRAY_RE.search(text)
    if m:
        value = float(m.group(1))
        spacing_mm = value * _UNIT_TO_MM[m.group(2).lower()]
        return (
            "place_array",
            {"spacing_mm": spacing_mm},
            f"Array set every {m.group(1)}{m.group(2)}.",
        )

    if _WHAT_ELSE_RE.search(text):
        return "what_else", {}, _CANNED_REPLY["what_else"]

    if _REPORT_RE.search(text):
        return "make_report", {}, _CANNED_REPLY["make_report"]
    if _INSTALLED_RE.search(text):
        return "see_it_installed", {"placement": ""}, _CANNED_REPLY["see_it_installed"]
    if _QUOTE_RE.search(text):
        return "check_quote", {}, _CANNED_REPLY["check_quote"]
    if _SAFETY_RE.search(text):
        return "check_safety", {"part_id": None}, _CANNED_REPLY["check_safety"]
    if _RULES_RE.search(text):
        return "check_rules", {"job": rules.job_for(text)}, _CANNED_REPLY["check_rules"]

    m = _FINISH_RE.search(text)
    if m:
        return "set_finish", {"name": m.group(1)}, _CANNED_REPLY["set_finish"]

    if _COACH_RE.search(text):
        return "coach_capture", {}, _CANNED_REPLY["coach_capture"]
    if _LABEL_RE.search(text):
        return "label_view", {"focus": None}, _CANNED_REPLY["label_view"]
    if _COACH_START_RE.search(text):
        return "start_coach", {"job": coach.job_for(text)}, _CANNED_REPLY["start_coach"]

    # After the Grok matchers, so "show me the report on the gutter slope" stays make_report and
    # "survey the gutter slope" stays F10's; "is this gutter sloped enough to drain?" lands here.
    m = _SLOPE_RE.search(text)
    if m and not _SHOPPING_RE.search(text):
        target = (m.group(1) or m.group(2)).lower()
        return "check_slope", {"target": target}, _CANNED_REPLY["check_slope"]

    return None


def _survey_args(m: re.Match, text: str) -> dict[str, Any]:
    """`survey` args from a _SURVEY_RE match: "cabinet doors" -> cabinet_door, "the window" (the +
    one) -> nearest, "upper"/"left"/... -> that filter, "I can see" -> visible, "widths" -> width."""
    det, qualifier, thing = m.group(1).lower(), (m.group(2) or "").lower(), m.group(3).lower()
    label = re.sub(r"\s+", "_", thing).rstrip("s")
    where = _SURVEY_WHERE.get(qualifier)
    if where is None:
        where = "nearest" if det == "the" and not thing.endswith("s") else "all"
    if _SURVEY_VISIBLE_RE.search(text):
        where = "visible"
    wide, tall = bool(_WIDTH_RE.search(text)), bool(_HEIGHT_RE.search(text))
    measure = "width" if wide and not tall else "height" if tall and not wide else "size"
    return {"label": label, "where": where, "measure": measure}


def _extract_limits(text: str) -> tuple[dict[str, Any], str]:
    """(set_limits args, the text with those phrases cut out)."""
    limits: dict[str, Any] = {}
    m = _LIMIT_PRICE_RE.search(text)
    if m:
        limits["max_total_usd"] = float((m.group(1) or m.group(2)).replace(",", ""))
        text = f"{text[: m.start()]} {text[m.end() :]}"
    m = _LIMIT_DATE_RE.search(text)
    if m:
        day = mandate.parse_deliver_by(m.group(2).lower(), mandate._today())
        if m.group(1).lower() == "before":
            day -= timedelta(days=1)
        limits["deliver_by"] = day.isoformat()
        text = f"{text[: m.start()]} {text[m.end() :]}"
    rest = re.sub(r"\s*,(\s*,)+", ",", text)
    rest = re.sub(r"\s+", " ", rest).strip(" ,.;")
    return limits, rest


def _has_more(rest: str) -> bool:
    return any(w not in _LIMIT_FILLER for w in re.findall(r"[a-z0-9$']+", rest.lower()))


def _coach_intent(session: Session, text: str) -> tuple[str, dict[str, Any], str] | None:
    """ "check it" / "next" / "I did it" / "back" / "repeat", only while a coach runs."""
    if _COACH_CHECK_RE.search(text):
        name, args = "check_step", {}
    elif m := _COACH_MOVE_RE.search(text):
        said = m.group().lower()
        move = "back" if re.search(r"back|previous", said) else "next"
        move = "repeat" if re.search(r"repeat|again", said) else move
        name, args = "coach_step", {"move": move}
    else:
        return None
    if coach.current(session.coach_id, session.id) is None:
        return None
    return name, args, _CANNED_REPLY[name]


def _reimagine_intent(session: Session, text: str) -> tuple[str, dict[str, Any], str] | None:
    """ "undo" / "start over" / "darker blue", only while a reimagine is on screen."""
    if not session.reimagine:
        return None
    for regex, tool, args in (
        (_UNDO_RE, "undo_reimagine", {}),
        (_START_OVER_RE, "start_over_reimagine", {}),
        (_REFINE_RE, "refine_reimagine", {"prompt": text}),
    ):
        if regex.search(text):
            return tool, args, _CANNED_REPLY[tool]
    return None


def _tape_survey_intent(session: Session, text: str) -> tuple[str, dict[str, Any], str] | None:
    """ "which one is the widest?", only while a tape survey is stored (app lane)."""
    if session.tape_survey and _SURVEY_Q_RE.search(text):
        return "survey_query", {"question": text}, _CANNED_REPLY["survey_query"]
    return None


def _fast_route(
    session: Session, text: str, ctx: dict[str, Any]
) -> tuple[str, dict[str, Any], str] | None:
    """Every fast path, in priority order (docs/integration-grok-features.md has the table):
    1. the stateless matchers (`_match_fast_intent`: packet and run_job first, then a bare
       "stop", the tape survey, the condition survey, equip, checkout, array, report, postcard,
       safety, rules, finish, capture coach, labels, start coach, gutter slope);
    2. "place them" after a plan;
    3. the install coach's words while a coach runs ("next", "back", "done", "repeat that");
    4. the reimagine stack's words while a reimagine is on screen ("undo", "start over",
       "darker ...", "add ...", "swap ...");
    5. "which is the widest?" while a tape survey is stored.
    So with both a coach and a reimagine live, every "back" is the coach's step and "undo" is
    the picture's."""
    fast = _match_fast_intent(text)
    if fast and fast[0] == "survey_condition" and _asks_to_measure(text):
        fast = (
            "measure",
            _measure_args(text),
            _CANNED_REPLY["measure"],
        )  # "survey the roof's length"
    if fast and fast[0] == "place_array" and ctx.get("site"):
        fast = None  # "hangers every 60 cm" on a scanned scene is a plan, let the LLM route it
    if session.plan and _PLACE_RE.search(text):
        fast = ("place_plan", {}, f"Placing {session.plan['count'] or 'them'}.")
    return (
        fast
        or _coach_intent(session, text)
        or _reimagine_intent(session, text)
        or _show_model_intent(text)
        or _replace_job_route(session, text, ctx)
        or _replace_route(session, text, ctx)
        or _measure_intent(text)
        or _tape_survey_intent(session, text)
    )


# --- measuring by voice (server/structure_measure.py): never a condition survey -----------------


def _measure_intent(text: str) -> tuple[str, dict[str, Any], str] | None:
    """ "measure the length of the top roof from end to end", "how long is the roof", "measure the
    height of the tower platform" -> measure {thing, dimension, end_to_end}. After the replace
    flow (a gap's "measure it" is measure_cavity) and the stateless matchers ("measure every
    cabinet door" is already the B1 survey); condition phrasings never parse."""
    ask = structure_measure.parse(text)
    if ask is None:
        return None
    args = {"thing": ask.thing, "dimension": ask.dimension, "end_to_end": ask.end_to_end}
    return "measure", args, _CANNED_REPLY["measure"]


_MEASURING_WORDS_RE = re.compile(
    r"\b(?:measure|measuring|tape|how\s+(?:long|tall|wide|high|deep|big|far)|length|height"
    r"|width|depth|distance|dimensions?|end\s+to\s+end)\b",
    re.IGNORECASE,
)


def _measure_args(text: str) -> dict[str, Any]:
    """measure's args from the words (what `_measure_intent` would have sent), else a length."""
    ask = structure_measure.parse(text)
    if ask is None:
        return {"thing": None, "dimension": "length", "end_to_end": False}
    return {"thing": ask.thing, "dimension": ask.dimension, "end_to_end": ask.end_to_end}


def _asks_to_measure(text: str) -> bool:
    """A sizes question the LLM mistook for a condition survey (the headset's "measure the length
    of the top roof from end to end" got F10's pins)."""
    return bool(_MEASURING_WORDS_RE.search(text or "")) and not structure_measure.condition(text)


def _measure_tool(session: Session, args: dict[str, Any], ctx: dict[str, Any]):
    """measure: B1's tape survey when the scene has such objects; else a structural tape
    (measure_edges with the endpoints from the planes); else the tape in hand for the user's own
    two pinches (equip_tool tape)."""
    dim = str(args.get("dimension") or "length").lower()
    dim = dim if dim in ("length", "width", "height", "depth", "size") else "length"
    thing = str(args.get("thing") or "").strip().lower() or None
    ask = structure_measure.Ask(dimension=dim, thing=thing, end_to_end=bool(args.get("end_to_end")))
    plan = structure_measure.plan(session.site, session.scale, ask, ctx.get("pointer"))
    if plan["kind"] == "survey":
        return _survey_tool(session, {k: plan[k] for k in ("label", "where", "measure")})
    what = thing or "it"
    if plan["kind"] == "edges":
        request_id = _new_request(
            session,
            "me",
            {"kind": "measure", "label": plan["label"], "query": session.last_text},
        )
        if ask.end_to_end or plan["what"] == "length":
            spoken = f"Taping the {what} end to end; the number's on the tape."
        else:  # "Taping the tower platform's height"
            spoken = f"Taping the {what}'s {plan['what']}; the number's on the tape."
        action_args = {
            "label": plan["label"],
            "request_id": request_id,
            "segments": plan["segments"],
            "what": plan["what"],
        }
        result = {"ok": True, "request_id": request_id, "spoken": spoken}
        return result, {"name": "measure_edges", "args": action_args}, None
    label = structure_measure.title(ask, "height" if dim == "height" else dim)
    spoken = (
        f"Measure's ready: pinch one end of the {thing}, then the other."
        if thing
        else "Measure's ready: pinch one end, then the other."
    )
    action = {"name": "equip_tool", "args": {"tool": "tape", "label": label}}
    return {"ok": True, "spoken": spoken, "label": label}, action, None


# --- autonomy: Model view by voice; one sentence, the whole replace (server/replace_job.py) ----

_SHOW_MODEL_RE = re.compile(
    replace._LEAD
    + r"(?:show(?:\s+me)?|open(?:\s+up)?|load|go\s+to|switch\s+to|take\s+me\s+to|bring\s+up"
    + r"|let\s+me\s+see|let'?s\s+see|put\s+(?:up|on))\s+(?:the\s+|a\s+)?"
    + r"(?:(?P<name>[a-z0-9][a-z0-9 '\-]{1,40}?)\s+(?:model|scan|scene|site)"
    + r"|(?:model|scan|scene)\s+of\s+(?:the\s+)?(?P<name2>[a-z0-9][a-z0-9 '\-]{1,40}?))"
    + r"(?:\s+(?:on\s+the\s+table|here))?"
    + replace._TAIL,
    re.IGNORECASE,
)


def _show_model_intent(text: str) -> tuple[str, dict[str, Any], str] | None:
    """ "show me the gym model" / "open the hospital scan" -> show_model {site}, when the name
    is a site (else None: "show me the next model" is the replace flow's)."""
    m = _SHOW_MODEL_RE.match(text)
    if not m:
        return None
    said = m.group("name") or m.group("name2")
    site = sites.match(said)
    if site is None:
        return None
    return "show_model", {"site": site}, f"Here's the {sites.name(site)}."


def _replace_job_route(
    session: Session, text: str, ctx: dict[str, Any]
) -> tuple[str, dict[str, Any], str] | None:
    """ "replace the dishwasher (with a new one that fits)", "swap out the range for an induction
    one", "the fridge is broken, find me a new one", "take out the dishwasher and put in a new
    one", "remove the range. find a 30-inch induction range that fits" -> replace_component, the
    autonomous job. Only for a part of the open scene (else the LLM decides); a "find a new
    dishwasher that fits" with its gap already open stays the step flow's search."""
    if not session.site:
        return None
    comps = replace.components(session.site)
    if not comps:
        return None
    want = replace_job.parse(text)
    if want is None:
        phrases = replace.match_all(text)
        if (
            len(phrases) < 2
            or phrases[0]["kind"] != "remove"
            or not any(p["kind"] in ("find", "place") for p in phrases[1:])
        ):
            return None
        find = next((p for p in phrases if p["kind"] == "find"), None)
        want = {"thing": phrases[0]["thing"], "words": (find or {}).get("thing"), "strict": False}
    c = replace_job.resolve(comps, want["thing"], ctx.get("pointer"))
    if c is None and want["thing"] is None and session.removed:
        c = replace.find(comps, session.removed[-1])  # "replace it": the part taken out last
    if c is None or not c.get("removable", True):
        return None
    if want["strict"] and c["id"] in session.removed:
        return None
    query = replace_job.query_for(replace.label(c), want["words"])
    args = {"component": c["id"], "query": query, "max_price_usd": None}
    return "replace_component", args, _CANNED_REPLY["replace_component"]


def _replace_job_tool(session: Session, args: dict[str, Any], ctx: dict[str, Any]):
    """replace_component: start the autonomous replace (job_started now; its steps, their
    actions and job_done come through GET /job/run/{run_id})."""
    site = session.site or ctx.get("site")
    comps = replace.components(site)
    if not comps:
        return {"error": "open a scanned scene with parts first"}, None, None
    thing = str(args.get("component") or "").strip() or None
    c = replace_job.resolve(comps, thing, ctx.get("pointer"))
    if c is None and thing is None and session.removed:
        c = replace.find(comps, session.removed[-1])
    if c is None:
        what = f"no {thing} in this scan" if thing else "nothing where you're pointing"
        return {"error": f"there's {what} to replace"}, None, None
    label = replace.label(c)
    if not c.get("removable", True):
        return {"error": f"the {label} can't come out"}, None, None
    query = replace_job.query_for(label, args.get("query"))
    try:
        max_usd = float(args["max_price_usd"]) if args.get("max_price_usd") else None
    except (TypeError, ValueError):
        max_usd = None
    actions: list[dict[str, Any]] = []
    if max_usd:
        limited, action, _ = _set_limits_tool(session, {"max_total_usd": max_usd})
        if action and "error" not in limited:
            actions.append(action)
    try:
        run = replace_job.start(session, site, replace_job.Want(c, query, max_usd), ctx)
    except runjob.Busy:
        return {"error": "a job is already running; give it a moment"}, None, None
    looking = f"{query}s" if not query.endswith("s") else query
    spoken = f"Replacing the {label}: out, tape the gap, then {looking} that fit."
    return {"run_id": run.id, "spoken": spoken}, [*actions, run.actions[0]], None


# --- e2e: measure and replace (server/replace.py; the app's hand-off docs/handoff/p4-e2e) ------


def _gap_component(session: Session, thing: str | None = None) -> dict[str, Any] | None:
    """The removed component `thing` names, else the one taken out last (still out)."""
    comps = replace.components(session.site)
    if thing:
        c = replace.find(comps, thing)
        return c if c and c.get("id") in session.removed else None
    for cid in reversed(session.removed):
        c = replace.find(comps, cid)
        if c:
            return c
    return None


def _gap_candidates(session: Session) -> list[str]:
    """The candidates the gap switches through: the headset's Find parts list, else the last
    search's results."""
    if session.candidate_ids:
        return list(session.candidate_ids)
    job = jobs.get(session.job_id) if session.job_id else None
    return [p.id for p in (job.candidates if job and job.status == "done" else [])]


def _replace_step(session: Session, phrase: dict[str, Any]) -> tuple[str, dict[str, Any]] | None:
    """(tool, args) for one measure-and-replace phrase when the scene state makes it unambiguous
    (a removable part by that name; a gap open; candidates to put in it), else None."""
    kind, thing = phrase["kind"], phrase.get("thing")
    comps = replace.components(session.site)
    if kind == "remove":
        c = replace.find(comps, thing)
        return ("remove_component", {"thing": thing}) if c and c.get("removable", True) else None
    gap = _gap_component(session)
    if kind == "put_back":
        target = _gap_component(session, thing) if thing else gap
        return ("restore_component", {"thing": target["id"]}) if target else None
    if gap is None:
        return None
    if kind == "measure":
        return "measure_cavity", {}
    if kind == "undo":
        return "undo_edit", {}
    if kind == "scale":
        return "scale_gap", {
            "axis": phrase["axis"],
            "real_m": phrase["metres"],
            "said": phrase["said"],
        }
    if kind == "find":
        return "find_in_gap", {"query": replace.singular(thing) if thing else None}
    if not _gap_candidates(session):
        return None
    if kind == "place":
        return "place_in_gap", {}
    if kind == "option":
        return "place_in_gap", {"index": phrase.get("index", -1), "say_option": True}
    if kind in ("next", "previous"):
        return "cycle_in_gap", {"delta": 1 if kind == "next" else -1}
    return None


def _replace_route(
    session: Session, text: str, ctx: dict[str, Any]
) -> tuple[str, dict[str, Any], str] | None:
    """ "remove the dishwasher", "measure the gap", "find one that fits", "put it in there",
    "next one", "put it back" -- or a few of them joined by "and" / "then". Only the first
    phrase's state is checked here (a later "measure it" needs the gap the removal opens)."""
    phrases = replace.match_all(text)
    if not phrases or not session.site:
        return None
    first = _replace_step(session, phrases[0])
    if first is None:
        return None
    if len(phrases) == 1:
        name, args = first
        return name, args, _CANNED_REPLY[name]
    return "replace_flow", {"phrases": phrases}, _CANNED_REPLY["replace_flow"]


def _gap_cavity(session: Session, c: dict[str, Any], ctx: dict[str, Any]):
    """The gap's size: the headset's `cavity` context, else the file x the scale. After a
    scale_gap in this reply (session.cavity cleared, session.scale corrected): the file x the new
    scale."""
    if session.cavity is None:
        return replace.cavity_for(c, session.scale, None)
    ctx_cavity = ctx.get("cavity") if isinstance(ctx.get("cavity"), dict) else session.cavity
    return replace.cavity_for(c, session.scale, ctx_cavity)


def _place_action(
    session: Session, c: dict[str, Any], cavity, index: int, count: int, part, swap: bool = False
):
    """place_part into the gap -- or, with a model already standing there (`swap`), the placement
    editor's cycle_model {index} (it keeps the model's adjusted pose and its own undo)."""
    f = replace.fit(part.dims_mm, cavity) if cavity is not None else part.fit
    cycle = {"index": index, "count": count}
    if swap:
        args = {"index": index, "part_id": part.id, "component_id": c["id"], "cycle": cycle}
        return f, {"name": "cycle_model", "args": args}
    args = {
        "part_id": part.id,
        "model_url": assets.model_url(part.id, session.asset_mode),  # texture: its generator
        "name": part.name,
        "component_id": c["id"],
        "fits": replace.fits_word(f) if cavity is not None else f.status,
        "cycle": cycle,
    }
    if cavity is not None:
        args["clearance_mm"] = replace.clearance_mm(part.dims_mm, cavity)
    return f, {"name": "place_part", "args": args}


def _model_in_gap(session: Session, ids: list[str], ctx: dict[str, Any]) -> bool:
    """A candidate already stands in the gap: this server put one there, or the headset's placed
    parts or selection is one of the candidates."""
    placed = ctx.get("placed") or session.placed or []
    placed_ids = {p.get("part_id") for p in placed if isinstance(p, dict)}
    return (
        session.gap_index is not None
        or bool(placed_ids & set(ids))
        or ctx.get("selected_part_id") in ids
    )


_short_name = replace.short_name


async def _replace_tool(session: Session, name: str, args: dict[str, Any], ctx: dict[str, Any]):
    """The measure-and-replace tools (_replace_route): (result with `spoken`, action(s), job id)."""
    comps = replace.components(session.site)
    if name == "replace_flow":
        spoken, actions, job_id = [], [], None
        for phrase in args.get("phrases") or []:
            step = _replace_step(session, phrase)
            if step is None:
                if phrase["kind"] in ("place", "option", "next", "previous") and any(
                    a.get("name") == "search_started" for a in actions
                ):  # "... find one that fits, put it in": the options aren't up yet
                    spoken.append("Say put it in when the options are up.")
                break
            if step[0] == "measure_cavity" and actions:  # the removal just said the gap's size
                step[1]["quiet"] = True
            result, action, job = await _replace_tool(session, step[0], step[1], ctx)
            if "error" in result:
                spoken.append(f"Can't do that yet: {result['error']}.")
                break
            spoken.append(result.get("spoken") or _CANNED_REPLY[step[0]])
            actions += _as_list(action)
            job_id = job or job_id
        return {"spoken": " ".join(spoken)}, actions, job_id

    if name == "remove_component":
        c = replace.find(comps, args.get("thing"))
        if c is None:
            return {"error": f"no {args.get('thing') or 'part'} to take out here"}, None, None
        if c["id"] in session.removed:
            session.removed.remove(c["id"])
        session.removed.append(c["id"])
        session.gap_index = None
        cav = replace.cavity_for(c, session.scale)
        what = replace.label(c)
        spoken = f"Took out the {what}." + (
            f" The gap is about {replace.spoken_gap(cav)}, estimated." if cav else ""
        )
        return (
            {"spoken": spoken, "component_id": c["id"]},
            {"name": "remove_component", "args": {"component_id": c["id"]}},
            None,
        )

    if name == "restore_component":
        c = replace.find(comps, args.get("thing"))
        if c is None:
            return {"error": "nothing is out"}, None, None
        if c["id"] in session.removed:
            session.removed.remove(c["id"])
        session.gap_index = None
        return (
            {"spoken": f"The {replace.label(c)}'s back."},
            {"name": "restore_component", "args": {"component_id": c["id"]}},
            None,
        )

    c = _gap_component(session)
    if c is None:
        return {"error": "take a part out first"}, None, None
    cav = _gap_cavity(session, c, ctx)

    if name == "undo_edit":
        # The headset's EditHistory.Undo: the last edit (a model into the gap, a swap, the
        # scale, the tape) comes off. Which model stands in the gap is the headset's to say now.
        session.gap_index = None
        return {"spoken": "Undone."}, {"name": "undo_edit", "args": {}}, None

    if name == "measure_cavity":
        spoken = "Taping the gap." + (
            f" The scan says about {replace.spoken_gap(cav)}."
            if cav and not args.get("quiet")
            else ""
        )
        return (
            {"spoken": spoken},
            {"name": "measure_cavity", "args": {"component_id": c["id"]}},
            None,
        )

    if name == "scale_gap":
        # "the opening is 34 and a half inches tall": the headset sets its scale from its tape of
        # the gap; the rest of this reply (and the next context) already uses the corrected gap.
        axis, real = args.get("axis") or "h", float(args.get("real_m") or 0)
        now = dict(zip("whd", cav.size_m())) if cav is not None else {}
        if real > 0 and now.get(axis):
            session.scale = (session.scale or 1.0) * real / now[axis]
            session.cavity = None
        word = {"w": "width", "h": "height", "d": "depth"}[axis]
        said = args.get("said") or f"{real:.3f} metres"
        return (
            {"spoken": f"Setting the scale from the gap's {word}: {said}."},
            {"name": "scale_gap", "args": {"component_id": c["id"], "axis": axis, "real_m": real}},
            None,
        )

    if name == "find_in_gap":
        query = (args.get("query") or replace.label(c)).strip()
        job = jobs.start_search(
            SearchRequest(
                query=query,
                measurement=session.measurement,
                frame_jpg_b64=ctx.get("frame_jpg_b64"),
                cavity=cav,
                asset_mode=session.asset_mode,  # asset-mode
            )
        )
        session.job_id = job.id
        session.candidate_ids = []  # the new results replace the old list
        session.gap_index = None
        plural = query if query.endswith("s") else f"{query}s"
        gap = f": {replace.spoken_gap(cav)}" if cav else ""
        spoken = f"Searching for {plural} that fit the gap{gap}."
        hint = replace.scale_hint(cav, [], session.scale)  # a gap no real one fits: say why
        if hint:
            spoken += f" {hint}"
        return (
            {"spoken": spoken, "job_id": job.id},
            {"name": "search_started", "args": {"job_id": job.id}},
            job.id,
        )

    ids = _gap_candidates(session)
    parts = [jobs.load_part(i) for i in ids]
    if not ids or all(p is None for p in parts):
        return {"error": "no candidates yet, find one first"}, None, None
    n = len(ids)
    if name == "cycle_in_gap":
        # The model in the gap: the headset's selection (it may have switched on its own), else ours.
        selected = ctx.get("selected_part_id")
        current = ids.index(selected) if selected in ids else session.gap_index
        if current is None:
            current = replace.best([p.dims_mm if p else None for p in parts], cav) if cav else 0
            index = current
        else:
            index = (current + int(args.get("delta") or 1)) % n
        say_option = True
    else:
        index = args.get("index")
        say_option = bool(args.get("say_option"))
        if index is None or index < 0:
            index = replace.best([p.dims_mm if p else None for p in parts], cav) if cav else 0
    if not 0 <= index < n or parts[index] is None:
        return {"error": f"there's no option {index + 1}"}, None, None
    part = parts[index]
    swap = _model_in_gap(session, ids, ctx)  # nothing in the gap yet (a stopped job): place it
    f, action = _place_action(session, c, cav, index, n, part, swap=swap)
    session.gap_index = index
    session.selected_part_id = part.id
    lead = (
        f"Option {index + 1} of {n}, the {_short_name(part)}."
        if say_option
        else f"Putting the {_short_name(part)} in."
    )
    spoken = f"{lead} {replace.spoken_fit(f)}"
    hint = (  # once, when it goes in; "next one" doesn't repeat it
        replace.scale_hint(cav, [replace.fits_word(f)], session.scale) if cav and not swap else None
    )
    if hint:
        spoken += f" {hint}"
    return {"spoken": spoken, "part_id": part.id}, action, None


# --- reimagine refine stack (F18) -------------------------------------------------------


def remember_reimagine(session_id: str, site: str, prompt: str, out: dict[str, Any]) -> None:
    """Start a session's refine stack at a fresh `imagine.reimagine()` result (the agent tool,
    or `POST /scene/reimagine` with a `session_id`)."""
    _get_session(session_id).reimagine = {
        "site": site,
        **{k: out[k] for k in ("frame_id", "camera", "before_url")},
        "steps": [{"prompt": prompt.strip()[: imagine.MAX_PROMPT_LEN], "id": out["id"]}],
    }


def _show_reimagined(session: Session) -> dict[str, Any]:
    """`show_reimagined` for the top of the stack; step 0 is the original capture frame."""
    rv = session.reimagine
    steps = rv["steps"]
    session.rv_fresh = True
    args = {
        "image_url": f"/scene/reimagine/{steps[-1]['id']}.jpg" if steps else rv["before_url"],
        "frame_id": rv["frame_id"],
        "camera": rv["camera"],
        "label": imagine.LABEL,
        "step": len(steps),
        "can_undo": bool(steps),
        "drift": steps[-1].get("drift") if steps else None,
    }
    return {"name": "show_reimagined", "args": args}


def _imagine_error(exc: Exception) -> dict[str, Any]:
    if isinstance(exc, ValueError):  # unknown site/frame, empty prompt
        return {"error": str(exc)}
    if isinstance(exc, imagine.ModerationBlocked):
        return {"error": "the image service refused that one"}
    return {"error": "no sketch available right now"}


# --- tool execution ----------------------------------------------------------------------


# --- the app lane's tape: survey / check_slope / survey_query (brain.md S1) -------------


def _digest(session: Session) -> scene_digest.SceneDigest | None:
    """The open scene's structure digest, if the headset named a site this server has."""
    if not session.site:
        return None
    try:
        return scene_digest.digest(session.site, session.scale)
    except (KeyError, TypeError, ValueError) as exc:  # a malformed structure file
        logger.warning("agent: no digest for %s: %s", session.site, exc)
        return None


def _new_request(session: Session, prefix: str, entry: dict[str, Any]) -> str:
    request_id = f"{prefix}-{uuid.uuid4().hex[:8]}"
    session.pending[request_id] = entry
    while len(session.pending) > MAX_PENDING:
        session.pending.popitem(last=False)
    return request_id


def _survey_tool(session: Session, args: dict[str, Any]):
    """`survey` (app lane; F10's condition survey is `_survey`): queue the headset's own tape over every object of one label. The server never
    measures: it checks the label against the scene's structure layer (when it has the site)
    so "measure every window" in a kitchen gets a straight answer instead of an empty sweep."""
    label = args.get("label") if args.get("label") in tape_survey.LABELS else "any"
    where = args.get("where") if args.get("where") in tape_survey.WHERES else "all"
    measure = args.get("measure") if args.get("measure") in tape_survey.MEASURES else "size"
    d = _digest(session)
    # "the doors" in a kitchen means its cabinet doors.
    if label == "door" and d is not None and not d.count("door") and d.count("cabinet_door"):
        label = "cabinet_door"
    if d is not None and not d.structure:
        spoken = "No structure layer for this scene; mark the corners yourself."
        return {"ok": False, "spoken": spoken}, None, None
    count = d.count(label) if d is not None else None
    if count == 0:
        spoken = f"No {tape_survey.noun(label)} in this scene's structure layer."
        return {"ok": False, "count": 0, "spoken": spoken}, None, None

    request_id = _new_request(
        session,
        "sv",
        {
            "kind": "survey",
            "label": label,
            "where": where,
            "measure": measure,
            "query": session.last_text,
        },
    )
    if where == "all":
        spoken = (
            f"Surveying {count} {tape_survey.noun(label, count)}."
            if count
            else f"Surveying the {tape_survey.noun(label)}."
        )
    elif where == "nearest":
        spoken = f"Measuring the nearest {tape_survey.noun(label, 1)}."
    elif where == "visible":
        spoken = f"Surveying the {tape_survey.noun(label)} in view."
    else:
        spoken = f"Surveying the {where} {tape_survey.noun(label)}."
    action_args = {"label": label, "where": where, "measure": measure, "request_id": request_id}
    result = {"ok": True, "request_id": request_id, "count": count, "spoken": spoken}
    return result, {"name": "survey", "args": action_args}, None


def _slope_tool(session: Session, args: dict[str, Any]):
    """`check_slope`: the headset tapes the edge end to end and reports the fall to
    /agent/observe, which judges drainage. For a gutter, `edge_ids` suggests the structure
    layer's long high near-horizontal edges (best first); the app may pick its own."""
    target = args.get("target") if args.get("target") in tape_survey.SLOPE_TARGETS else "gutter"
    d = _digest(session)
    edge_ids = [e["id"] for e in d.gutter_edges] if d is not None and target == "gutter" else []
    request_id = _new_request(
        session, "sl", {"kind": "slope", "target": target, "query": session.last_text}
    )
    spoken = (
        "Checking that edge's fall."
        if target == "nearest_edge"
        else f"Checking the {target}'s fall."
    )
    action_args = {"target": target, "request_id": request_id, "edge_ids": edge_ids}
    return (
        {"ok": True, "request_id": request_id, "spoken": spoken},
        {
            "name": "check_slope",
            "args": action_args,
        },
        None,
    )


def _set_limits_tool(session: Session, args: dict[str, Any]):
    """`set_limits`: voice narrows the purchase limits (server/mandate.py); a looser ask is
    refused, never applied. Queues `show_limits` for the wrist chip."""
    fields = {
        k: args[k]
        for k in ("max_total_usd", "deliver_by", "seller_policy")
        if args.get(k) is not None
    }
    if not fields:
        return {"error": "no limit given"}, None, None
    try:
        intent, refused, _changed = mandate.set_limits(
            session.id, source="voice", text=session.last_text, **fields
        )
    except ValueError as exc:
        return {"error": str(exc)}, None, None
    limits = intent.spoken()
    if refused:
        spoken = "You'd need to raise that on the panel." + (f" Still {limits}." if limits else "")
    else:
        spoken = f"Limits set: {limits}." if limits else _CANNED_REPLY["set_limits"]
    summary = intent.summary()
    action_args = {
        "intent_id": intent.id,
        "max_total_usd": summary["max_total_usd"],
        "deliver_by": summary["deliver_by"],
        "seller_policy": summary["seller_policy"],
        "refused": [r.model_dump() for r in refused],
    }
    result = {"ok": True, "limits": summary, "refused": action_args["refused"], "spoken": spoken}
    return result, {"name": "show_limits", "args": action_args}, None


def _show_tape_survey_action(stored: dict[str, Any], focus: list[str]) -> dict[str, Any]:
    return {
        "name": "show_tape_survey",
        "args": {
            "request_id": stored.get("request_id"),
            "label": stored.get("label"),
            "groups": stored.get("groups", []),
            "unverified": stored.get("unverified", []),
            "skipped": stored.get("skipped", []),
            "focus": focus,
        },
    }


def _survey_query_tool(session: Session, args: dict[str, Any]):
    """Template answers (widest, tallest, ...) with the object highlighted; anything else goes
    back to the model as a compact CSV of the headset's numbers, to answer from those only."""
    if not session.tape_survey:
        return {"error": "no survey yet"}, None, None
    question = str(args.get("question") or session.last_text)
    answer = tape_survey.answer_query(question, session.tape_survey)
    if answer is not None:
        spoken, ids = answer
        return (
            {"answer": spoken, "spoken": spoken},
            _show_tape_survey_action(session.tape_survey, ids),
            None,
        )
    result = {
        "csv": tape_survey.csv(session.tape_survey),
        "note": "Answer in at most 2 spoken sentences using only these numbers (mm). If any are "
        "unverified, say how many.",
    }
    return result, None, None


async def _run_tool(
    session: Session, name: str, args: dict[str, Any], context: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None, str | None]:
    """Execute one tool call.

    Returns (result fed back to the model, action queued for the client, job id if one started).
    Client-side tools always report {"ok": True} to the model -- the actual effect happens on
    the headset, which executes `actions` through the same methods as its buttons.
    `context` is the raw per-request payload from the headset (frames, frame_jpg_b64, ...);
    unlike session state it isn't remembered between turns.
    """
    fresh, session.rv_fresh = session.rv_fresh, False  # _show_reimagined sets it again
    if name in _REPLACE_TOOLS:  # e2e: measure and replace
        return await _replace_tool(session, name, args, context)
    if name == "find_part":
        query = str(args.get("query", "")).strip()
        job = jobs.start_search(
            SearchRequest(
                query=query,
                measurement=session.measurement,
                frame_jpg_b64=context.get("frame_jpg_b64"),
                opening=_opening(session),  # assetgen
                asset_mode=session.asset_mode,  # asset-mode
            )
        )
        session.job_id = job.id
        return (
            {"job_id": job.id, "status": "searching"},
            {"name": "search_started", "args": {"job_id": job.id}},
            job.id,
        )

    if name == "ask_scene":
        question = str(args.get("question", "")).strip()
        try:
            frames = [vision.Frame(**f) for f in (context.get("frames") or [])]
        except (TypeError, ValueError) as exc:
            return {"error": f"bad frames: {exc}"}, None, None
        if not frames:
            return {"error": "no frames available"}, None, None
        try:
            scene_answer = await vision.ask_scene(question, frames)
        except ValueError as exc:
            return {"error": str(exc)}, None, None
        result = {"answer": scene_answer.answer, "part_query": scene_answer.part_query}
        action = None
        if scene_answer.box is not None:
            action = {
                "name": "scene_pin",
                "args": {
                    "frame_id": scene_answer.frame_id,
                    "box": scene_answer.box,
                    "label": scene_answer.answer,  # the pin's own text, not the spoken reply
                },
            }
        return result, action, None

    if name == "label_view":
        return await _label_view(session, args.get("focus"), context)

    if name == "show_sellers":
        sort = args.get("sort", "cheapest")
        if not session.selected_part_id:
            return {"error": "no part selected"}, None, None
        part = jobs.load_part(session.selected_part_id)
        if part is None:
            return {"error": "part not found"}, None, None

        safety.start(part)  # cached before the seller panel's pay button is in reach
        part = await search.refine_sellers(part, sort)
        jobs.save_part(part)
        result = {
            "sellers": [
                {"name": s.name, "total": s.total_usd, "eta": s.eta} for s in part.sellers[:3]
            ],
            "reason": part.recommendation_reason,
            "spoken": _sellers_line(part),
        }
        action = {"name": "show_sellers", "args": {"part_id": part.id, "sort": sort}}
        return result, action, None

    if name == "survey":
        return _survey_tool(session, args)

    if name == "check_slope":
        return _slope_tool(session, args)

    if name == "survey_query":
        return _survey_query_tool(session, args)

    if name == "set_limits":
        return _set_limits_tool(session, args)

    if name == "what_else":
        parts, counts = _placed_parts_and_counts(session)
        if not parts:
            return {"error": "nothing placed yet"}, None, None
        result_bom = await bom.what_else(parts, counts)
        if not result_bom.lines:
            return {"lines": [], "total_usd": 0.0, "spoken": "Nothing extra to grab."}, None, None
        lines = [line.model_dump() for line in result_bom.lines]
        result = {
            "bom_id": result_bom.id,
            "lines": lines,
            "total_usd": result_bom.total_usd,
            "spoken": _bom_reply(result_bom),
        }
        action = {
            "name": "show_bom",
            "args": {"bom_id": result_bom.id, "lines": lines, "total_usd": result_bom.total_usd},
        }
        return result, action, None

    if name == "reimagine_view":
        site = context.get("site")
        frame_id = context.get("frame_id") or next(
            (f.get("id") for f in context.get("frames") or [] if isinstance(f, dict)), None
        )
        if not site or not frame_id:
            return {"error": "no camera frame in view"}, None, None
        prompt = str(args.get("prompt", ""))
        try:
            out = await imagine.reimagine(str(site), str(frame_id), prompt)
        except (ValueError, imagine.ImagineError, cache.OfflineMiss) as exc:
            return _imagine_error(exc), None, None
        remember_reimagine(session.id, str(site), prompt, out)
        return {"ok": True}, _show_reimagined(session), None

    if name in ("refine_reimagine", "undo_reimagine", "start_over_reimagine"):
        rv = session.reimagine
        if rv is None:
            return {"error": "nothing reimagined yet"}, None, None
        steps = rv["steps"]
        result: dict[str, Any] = {"ok": True}
        if name == "undo_reimagine" and steps:
            steps.pop()
        elif name == "start_over_reimagine":
            steps.clear()
        elif name == "refine_reimagine":
            prompt = str(args.get("prompt", "")).strip()[: imagine.MAX_PROMPT_LEN]
            try:
                out = await imagine.refine(
                    rv["site"],
                    rv["frame_id"],
                    [*(s["prompt"] for s in steps), prompt],
                    [s["id"] for s in steps],
                )
            except (ValueError, imagine.ImagineError, cache.OfflineMiss) as exc:
                return _imagine_error(exc), None, None
            steps.append({"prompt": prompt, "id": out["id"], "drift": out["drift"]})
            result = {"drift": out["drift"], "retried": out["retried"]}
            if out["retried"]:
                result["spoken"] = "Changed it, redrawn from the original to keep the rest steady."
        return result, _show_reimagined(session), None

    if name == "find_installer":
        trade = str(args.get("trade") or "").strip()[:MAX_TEXT_LEN]
        if not trade:
            return {"error": "no trade given"}, None, None
        location = (
            args.get("location") or context.get("location") or get_settings().DEFAULT_LOCATION
        )
        try:
            found = await intel.find_installers(trade, str(location)[:MAX_TEXT_LEN])
        except cache.OfflineMiss:
            return {"error": "installer search is offline"}, None, None
        except (RuntimeError, httpx.HTTPError) as exc:  # provider down: say so, keep the turn
            logger.warning("find_installer failed: %s", exc)
            return {"error": "installer search failed"}, None, None
        installers = [i.model_dump() for i in found.installers]
        result = {"installers": [i["name"] for i in installers], "spoken": found.summary}
        action = {
            "name": "show_installers",
            "args": {"installers": installers, "summary": found.summary},
        }
        return result, action, None

    if name == "make_report":
        # Relative URL: the headset prefixes its own server base URL (it knows the laptop IP).
        url = f"/report/{url_quote(session.id, safe='')}"
        return {"url": url}, {"name": "show_report", "args": {"url": url}}, None

    if name == "see_it_installed":
        return await _see_it_installed(session, str(args.get("placement") or ""), context)

    if name == "plan_placement":
        site = context.get("site")
        if not site:
            return {"error": "no scanned scene loaded"}, None, None
        try:
            result = await plan.plan(
                site,
                str(args.get("request", ""))[:MAX_TEXT_LEN],
                {**context, "selected_part_id": session.selected_part_id},
            )
        except plan.PlanError as exc:
            return {"error": exc.spoken, "spoken": exc.spoken}, None, None
        session.plan = result
        keys = ("plan_id", "segments", "points", "label")
        return result, {"name": "show_plan", "args": {k: result[k] for k in keys}}, None

    if name == "place_plan":
        # "place them" after a plan: point plans become a place_array, runs go to the BOM.
        p = session.plan
        if p["count"] and session.selected_part_id:
            session.placed = [{"part_id": session.selected_part_id, "count": p["count"]}]
        if not p["points"]:
            return await _run_tool(session, "what_else", {}, context)
        args = {"spacing_mm": p["spacing_mm"], "plan_id": p["plan_id"]}
        return {"ok": True}, {"name": "place_array", "args": args}, None

    if name == "walk_in_preview":
        site, frame_id = context.get("site"), context.get("frame_id")
        prompt, end_id = str(args.get("prompt", "")), None
        rv = session.reimagine
        if fresh and rv and rv["steps"]:  # right after a reimagine step: walk into that picture
            site, frame_id, end_id = rv["site"], rv["frame_id"], rv["steps"][-1]["id"]
            prompt = imagine.merged_prompt([s["prompt"] for s in rv["steps"]])
        if not site or not frame_id:
            return {"error": "no camera frame in view"}, None, None
        try:
            out = await flythrough.start(str(site), str(frame_id), prompt, end_image_id=end_id)
        except ValueError as exc:  # unknown site/frame, empty prompt
            return {"error": str(exc)}, None, None
        except imagine.ModerationBlocked:
            return {"error": "the image service refused that one"}, None, None
        except (imagine.ImagineError, cache.OfflineMiss):
            return {"error": "no walk-in available right now"}, None, None
        if out["status"] == "done":  # cached clip: play it now
            action = {"name": "show_video", "args": flythrough.video_args(out)}
            return {"spoken": flythrough.READY_LINE}, action, None
        session.flythrough_job_id = out["job_id"]
        action = {"name": "flythrough_started", "args": {"job_id": out["job_id"]}}
        return {"spoken": flythrough.RENDERING_LINE}, action, None

    if name == "ask_manual":
        question = str(args.get("question") or "").strip()[:MAX_TEXT_LEN]
        part_id = str(args.get("part_id") or session.selected_part_id or "")
        # the model supplies part_id: a path segment under data/parts, so ids only
        part = jobs.load_part(part_id) if re.fullmatch(r"[a-z0-9-]+", part_id) else None
        if part is None:
            return {"error": "no part selected"}, None, None
        if not question:
            return {"error": "no question"}, None, None
        if cache.get("manual", part.id) is None and not get_settings().OFFLINE:
            manuals.prefetch(part)  # a first find takes 10-130 s: too long to hold a voice turn
            return {"spoken": "Fetching the manual now; ask me again in a minute."}, None, None
        try:
            answer = await manuals.ask(part, question)
        except cache.OfflineMiss:
            return {"error": "the manual is offline"}, None, None
        except (RuntimeError, httpx.HTTPError) as exc:
            logger.warning("ask_manual failed: %s", exc)
            return {"error": "manual lookup failed"}, None, None
        result = {"answer": answer.answer, "page": answer.page, "spoken": answer.spoken}
        action = {
            "name": "show_manual_answer",
            "args": {
                "part_id": part.id,
                "answer": answer.answer,
                "page": answer.page,
                "quote": answer.quote,
                "pdf_url": answer.pdf_url,
            },
        }
        return result, action, None
    if name == "survey_condition":
        if _asks_to_measure(session.last_text):  # sizes are the tape's, never F10's pins
            return _measure_tool(session, _measure_args(session.last_text), context)
        return await _survey(session, args, context)
    if name == "measure":
        return _measure_tool(session, args, context)

    if name == "fix_pin":
        return await _fix_pin(session, str(args.get("pin_id", "")), context)
    if name == "coach_capture":
        scene_dir = coverage.site_dir(str(context.get("site") or ""))
        if scene_dir is None:
            return {"error": "no scene loaded"}, None, None
        try:
            cov = await coverage.coach(scene_dir)
        except coverage.Incomplete as exc:
            return {"error": f"the scan isn't finished ({exc})"}, None, None
        result = {"seen": cov.get("seen"), "verdict": cov["verdict"], "spoken": cov["spoken"]}
        return result, {"name": "show_coverage", "args": cov}, None
    if name == "check_rules":
        return await _check_rules(session, args, context)
    if name == "run_job":
        return _run_job(session, args, context)
    if name == "replace_component":
        return _replace_job_tool(session, args, context)
    if name == "show_model":
        site = sites.match(str(args.get("site") or ""))
        if site is None:
            return {"error": f"no scanned site called {args.get('site')!r}"}, None, None
        spoken = f"Here's the {sites.name(site)}."
        return (
            {"site": site, "spoken": spoken},
            {"name": "show_model", "args": {"site": site}},
            None,
        )

    if name == "check_quote":
        return await _check_quote(context)
    if name in ("send_packet", "revoke_packet"):
        return await _packet_tool(session, name, args)
    if name in COACH_TOOLS:
        return await _coach_tool(session, name, args, context)
    if name == "share_design":
        return await _share_design(session, args)

    if name == "select_candidate":
        index = int(args.get("index", -1))
        if 0 <= index < len(session.candidate_ids):
            session.selected_part_id = session.candidate_ids[index]
            part = jobs.load_part(session.selected_part_id)
            if part is not None:
                safety.start(part)
                manuals.prefetch(part)  # first manual question is fast
        return {"ok": True}, {"name": name, "args": {"index": index}}, None

    if name == "check_safety":
        part_id = args.get("part_id") or session.selected_part_id
        part = jobs.load_part(str(part_id)) if part_id else None
        if part is None:
            return {"error": "no part selected"}, None, None
        safety_report = await safety.check(part)
        result = {
            "verdict": safety_report.verdict,
            "headline": safety_report.headline,
            "spoken": safety_report.spoken,
        }
        action = {
            "name": "show_safety",
            "args": {
                "part_id": part.id,
                "verdict": safety_report.verdict,
                "headline": safety_report.headline,
            },
        }
        return result, action, None

    if name == "start_checkout":
        # Buying the selected part: a null seller_index is a seller named in the command, else
        # the cheapest/fastest if asked, else recommended_seller; the reply then names it (the
        # model can't know which one the server picked). Honesty over blocking (F6): the panel
        # still opens on a recalled part, but the warning is spoken first, once per part per
        # session. Cache only -- never stall on a live check.
        if not session.selected_part_id:
            return {"error": "no part selected"}, None, None
        part = jobs.load_part(session.selected_part_id)
        if part is None:
            return {"error": "part not found"}, None, None
        index = args.get("seller_index")
        picked = index is None
        if picked:
            index = _checkout_seller(part, str(args.get("hint") or ""))
        try:
            index = int(index)
        except (TypeError, ValueError):
            index = -1
        if not 0 <= index < len(part.sellers):
            return {"error": "no seller to check out with"}, None, None
        seller = part.sellers[index].name
        safety_report = safety.peek(part)
        result: dict[str, Any] = {"ok": True}
        if safety_report and safety_report.verdict == "recalled" and part.id not in session.warned:
            session.warned.add(part.id)
            where = f" for {seller}" if picked else ""
            result["spoken"] = (
                f"{safety_report.spoken} The pay panel's open{where} if you still want it."
            )
        elif picked:
            result["spoken"] = f"Hold-to-pay panel's open for {seller}."
        return result, {"name": name, "args": {"seller_index": index}}, None

    if name == "add_note":
        text = str(args.get("text", ""))[:MAX_TEXT_LEN]
        session.notes.append(text)
        return {"ok": True}, {"name": name, "args": {"text": text}}, None

    if name == "set_finish":
        return await _set_finish(session, args)

    if name in _CLIENT_ONLY_TOOLS:
        return {"ok": True}, {"name": name, "args": args}, None

    logger.warning("agent: unknown tool %r", name)
    return {"error": f"unknown tool {name}"}, None, None


def _site_view(context: dict[str, Any]) -> tuple[str | None, str | None, str | None]:
    """(site, frame_id, frame_jpg_b64) for a postcard: the headset's frame, else a scene thumb."""
    frames = [f for f in context.get("frames") or [] if isinstance(f, dict)]
    b64 = context.get("frame_jpg_b64") or next((f.get("jpg_b64") for f in frames), None)
    return context.get("site"), context.get("frame_id"), b64


async def _see_it_installed(
    session: Session, placement: str, context: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None, str | None]:
    part = jobs.load_part(session.selected_part_id) if session.selected_part_id else None
    if part is None:
        return {"error": "no part selected"}, None, None
    site, frame_id, b64 = _site_view(context)
    if not b64 and not (site and frame_id):
        return {"error": "no camera view to draw on"}, None, None
    try:
        jpg, before_url = postcard.load_site(site, frame_id, b64)
        out = await postcard.make_postcard(
            part,
            jpg,
            placement=placement or str(context.get("placement") or ""),
            box=context.get("placed_box"),
            before_url=before_url,
        )
    except ValueError as exc:  # unknown frame, bad box, no product photo
        return {"error": str(exc)}, None, None
    except imagine.ModerationBlocked:
        return {"error": "the image service refused that one"}, None, None
    except (imagine.ImagineError, cache.OfflineMiss):
        return {"error": "no picture available right now"}, None, None
    args = {k: out[k] for k in ("part_id", "image_url", "before_url", "label")}
    return {"ok": True}, {"name": "show_postcard", "args": args}, None


async def _survey(session: Session, args: dict[str, Any], context: dict[str, Any]):
    """Start the survey job; if it finishes within SURVEY_WAIT_S, also send `show_survey`."""
    site = context.get("site")
    if not site:
        return {"error": "no scene loaded"}, None, None
    focus = args.get("focus") if args.get("focus") in ("roof", "gutters", "facade") else "all"
    try:
        plan = survey.plan(site, careful=bool(args.get("careful")), focus=focus)
    except LookupError:
        return {"error": f"no scene called {site}"}, None, None
    except survey.NotReady as exc:
        return {"error": str(exc)}, None, None
    if get_settings().OFFLINE and not survey.is_cached(plan):
        return {"error": "the survey needs the uplink"}, None, None
    survey_id = jobs.start_task("survey", survey.run(plan)).id
    session.survey_id = survey_id
    actions = [{"name": "survey_started", "args": {"survey_id": survey_id}}]
    job = await jobs.wait(survey_id, SURVEY_WAIT_S)
    if job is not None and job.status == "failed":
        return {"error": "the survey failed"}, actions, None
    if job is None or job.status != "done":
        return {"survey_id": survey_id, "spoken": _CANNED_REPLY["survey_condition"]}, actions, None
    task = job.result or {}
    actions.append(
        {
            "name": "show_survey",
            "args": {"survey_id": survey_id, "pins": task["pins"], "label": task["label"]},
        }
    )
    return {"survey_id": survey_id, "spoken": task["spoken"]}, actions, None


async def _fix_pin(session: Session, pin_id: str, context: dict[str, Any]):
    """ "Find a fix for pin f1": `find_part` with the pin's query, plus a notebook entry."""
    job = jobs.get(session.survey_id or "")
    pins = (job.result or {}).get("pins", []) if job and job.stage == "survey" else []
    pin = next((p for p in pins if p["id"] == pin_id), None)
    if pin is None:
        return {"error": f"no pin {pin_id} in the last survey"}, None, None
    note = {
        "name": "add_note",
        "args": {
            "text": f"Pin {pin_id} ({pin['element']}, {pin['severity']}): {pin['issue']}",
            "pin_id": pin_id,
            "frame_id": pin["frame_id"],
        },
    }
    session.notes.append(note["args"]["text"])
    if not pin["part_query"]:
        return {"spoken": "That one needs a closer look before any part."}, [note], None
    result, action, job_id = await _run_tool(
        session, "find_part", {"query": pin["part_query"]}, context
    )
    return result, [action, note], job_id


def _as_list(action: dict | list | None) -> list[dict]:
    """`_run_tool`'s action slot holds one action, or a list when a tool queues several."""
    return action if isinstance(action, list) else [action] if action else []


async def _set_finish(session: Session, args: dict[str, Any]):
    """Re-texture the selected part (server/finish.py). The action is still `{name}`, plus
    `model_url`/`label` when there's a render, so an old client just tints."""
    fname = str(args.get("name", "")).strip()[:60]
    action = {"name": "set_finish", "args": {"name": fname}}
    part = jobs.load_part(session.selected_part_id) if session.selected_part_id else None
    if part is None or not fname:
        return {"ok": True}, action, None
    try:  # shield: a slow first render still finishes and caches after we stop waiting
        res = await asyncio.wait_for(asyncio.shield(finish.apply(part, fname)), FINISH_TIMEOUT_S)
    except TimeoutError:
        spoken = f"I've tinted it {fname}; the render is still cooking."
        return {"ok": True, "spoken": spoken}, action, None
    action["args"].update({k: res[k] for k in ("model_url", "label") if res[k]})
    return {"ok": True, "source": res["source"], "spoken": res["spoken"]}, action, None


async def _check_rules(
    session: Session, args: dict[str, Any], context: dict[str, Any]
) -> tuple[dict[str, Any], list[dict[str, Any]] | dict[str, Any] | None, None]:
    """Start a rules check for the selected part (plus the rest of the placed BOM, for the
    ENERGY STAR pair) or a named job. `rules_started` goes out at once; `show_rules` follows in
    the same reply if the check lands within RULES_WAIT_S, else on the next command."""
    parts, _ = _placed_parts_and_counts(session)
    selected = jobs.load_part(session.selected_part_id) if session.selected_part_id else None
    if selected is not None:
        parts = [selected, *(p for p in parts if p.id != selected.id)]
    job = args.get("job") if args.get("job") in rules.JOBS else None
    job = job or (rules.job_for(parts[0].name) if parts else None)
    if job is None:
        return {"error": "pick a part or name the job"}, None, None
    address = str(args.get("address") or context.get("address") or "")[:200] or None
    location = context.get("location") or (None if address else get_settings().DEFAULT_LOCATION)
    try:
        juris = await rules.jurisdiction(address, location)
    except cache.OfflineMiss:
        return {"error": "the rules check is offline"}, None, None
    except rules.NoMatch:
        return {"error": "I couldn't place that address"}, None, None
    check = rules.start(juris, job, parts)
    started = {"name": "rules_started", "args": {"check_id": check.id}}
    check = await jobs.wait(check.id, RULES_WAIT_S)
    if check.status == "running":
        session.rules_check_id = check.id
        line = f"Checking {rules.where(juris)}'s rules and the rebates. I'll put it up when ready."
        return {"spoken": line}, started, None
    if check.status != "done":
        return {"error": "the rules check failed"}, started, None
    shown = {"name": "show_rules", "args": rules.body(check)}
    return {"spoken": check.result["spoken"]}, [started, shown], None


def _coach_frame(context: dict[str, Any]) -> str | None:
    """The frame to check a step on: this request's, or the voice relay's if still fresh."""
    at = context.get("frame_at")
    if at is not None and time.time() - float(at) > coach.FRAME_MAX_AGE_S:
        return None
    frames = [f for f in context.get("frames") or [] if isinstance(f, dict)]
    return context.get("frame_jpg_b64") or next((f.get("jpg_b64") for f in frames), None)


async def _coach_tool(
    session: Session, name: str, args: dict[str, Any], context: dict[str, Any]
) -> tuple[dict[str, Any], list[dict[str, Any]] | None, None]:
    """start_coach / coach_step / check_step (server/coach.py). Every reply is `spoken`."""
    if name == "start_coach":
        part = jobs.load_part(session.selected_part_id) if session.selected_part_id else None
        job = args.get("job") if args.get("job") in coach.JOBS else None
        try:
            out = await coach.start(session.id, part, job)
        except coach.CantCoach as exc:
            return {"spoken": exc.spoken, "refused": exc.refused}, None, None
        session.coach_id = out["coach_id"]
    else:
        state = coach.current(session.coach_id, session.id)
        if state is None:
            return {"error": "no install coaching running"}, None, None
        session.coach_id = state["coach_id"]
        if name == "coach_step":
            out = coach.move(state, str(args.get("move") or "next"))
        elif (frame := _coach_frame(context)) is None:
            return {"spoken": coach.HOLD_STILL}, None, None
        else:
            try:
                out = await coach.check(state, frame, context.get("drill_px"))
            except ValueError as exc:  # bad frame or drill point
                return {"error": str(exc)}, None, None
            except coach.CheckFailed as exc:
                logger.warning("check_step failed: %s", exc)
                return {"spoken": coach.FAILED_LINE}, None, None
    result = {"spoken": out["spoken"], "i": out["i"], "status": out["status"]}
    return result | ({"verdict": out["verdict"]} if "verdict" in out else {}), out["actions"], None


async def _check_quote(
    context: dict[str, Any],
) -> tuple[dict[str, Any], dict[str, Any] | None, None]:
    """The quote held up to the camera (`frame_jpg_b64`, else the first of `frames`)."""
    _, _, b64 = _site_view(context)
    if not b64:
        return {"error": "hold the quote up to the camera"}, None, None
    try:
        data = base64.b64decode(str(b64).split(",")[-1], validate=True)
    except ValueError:
        return {"error": "the camera frame didn't come through"}, None, None
    address = str(context.get("address") or "")[:200] or None
    try:
        res = await quote.check(data, address, context.get("location"))
    except cache.OfflineMiss:
        return {"error": "the quote reader is offline"}, None, None
    except quote.BadQuote:
        return {"error": "that frame isn't a picture I can read"}, None, None
    except quote.ReadFailed as exc:
        logger.warning("check_quote failed: %s", exc)
        return {"error": "I couldn't read the quote"}, None, None
    result = {"spoken": res["spoken"], "flags": [f["text"] for f in res["flags"]]}
    return result, {"name": "show_quote_check", "args": res}, None


def _rules_ready(session: Session) -> dict[str, Any] | None:
    """`show_rules` for a rules check that finished since the last command."""
    check = jobs.get(session.rules_check_id) if session.rules_check_id else None
    if check is None or check.status == "running":
        return None
    session.rules_check_id = None
    return {"name": "show_rules", "args": rules.body(check)} if check.status == "done" else None


async def _packet_tool(
    session: Session, name: str, args: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None, None]:
    if name == "revoke_packet":
        record = packet.latest_public(session.id)
        if record is None:
            return {"error": "no public packet for this session"}, None, None
        if get_settings().OFFLINE:
            return {"error": "offline, can't reach the host to take it down"}, None, None
        try:
            await packet.take_down(record)
        except Exception as exc:  # noqa: BLE001 -- spoken, not raised
            logger.warning("revoke_packet failed: %s", exc)
            return {"error": "the host didn't answer, try again"}, None, None
        action = {"name": "packet_revoked", "args": {"packet_id": record["packet_id"]}}
        return {"spoken": "Packet's down. That link is dead now."}, action, None

    days = int(args.get("days") or packet.DEFAULT_DAYS)
    try:
        record = await packet.make(session.id, days)
        spoken = (
            f"Scan the code. The link is public for {days} days, and I can take it down any time."
        )
    except packet.PublishError as exc:
        record, spoken = exc.record, "Couldn't post it publicly; it's on the local network."
    if record is None:
        return {"error": "nothing in the notebook yet"}, None, None
    if not record["public"] and "error" not in record:
        spoken = "Offline, so the packet is on the local network only. No public link."
    action = packet.show_action(record)
    return {"spoken": spoken, "url": action["args"]["url"]}, action, None


def _run_job(session: Session, args: dict[str, Any], context: dict[str, Any]):
    """Start the whole-job chain; `job_started` now, the steps via GET /job/run/{run_id}."""
    goal = str(args.get("goal") or context.get("goal") or "")[:MAX_TEXT_LEN] or None
    try:
        run = runjob.start(session, context.get("site"), goal, context)
    except runjob.Busy:
        return {"error": "a job is already running"}, None, None
    except ValueError:
        return {"error": "tell me what to fix, or load a scan"}, None, None
    names = run.actions[0]["args"]["steps"]
    said = [_STEP_WORDS[n] for n in names if n in _STEP_WORDS]
    spoken = f"On it: {', '.join(said)}. I'll stop at the pay panel."
    return {"run_id": run.id, "spoken": spoken}, run.actions[0], None


_STEP_WORDS = {
    "survey": "survey",
    "part": "part",
    "safety": "safety",
    "rules": "rules",
    "postcard": "preview",
    "packet": "packet",
}


async def _label_view(
    session: Session, focus: str | None, context: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None, None]:
    """Labels for the headset's frame (`frame_jpg_b64`, else the first of `frames`)."""
    frames = context.get("frames") or []
    b64 = context.get("frame_jpg_b64") or (frames[0].get("jpg_b64") if frames else None)
    if not b64:
        return {"error": "no frame to look at"}, None, None
    frame_id = context.get("frame_id") or (frames[0].get("id") if frames else None) or "frame"
    try:
        jpg = base64.b64decode(b64, validate=True)
        labels.take(session.id)
        result = await labels.label_frame(jpg, focus or None)
    except (binascii.Error, ValueError) as exc:
        return {"error": f"bad frame: {exc}"}, None, None
    except labels.RateLimited as exc:
        return {"error": f"too many looks, wait {exc.retry_after_s:.0f} seconds"}, None, None
    except cache.OfflineMiss:
        return {"error": "offline and this view was never labelled"}, None, None
    except openai.APIError:
        return {"error": "the labelling model failed"}, None, None
    names = [label["name"] for label in result["labels"]]
    action = {"name": "show_labels", "args": {"frame_id": frame_id, "labels": result["labels"]}}
    return {"labels": names, "spoken": result["spoken"]}, action, None


async def _share_design(
    session: Session, args: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None, None]:
    """Consent is the user's own phrase; the voice relay calls this without the fast path."""
    if not _SHARE_RE.search(str(args.get("phrase") or "")):
        return {"error": "say 'add it to the wall' to share your design"}, None, None
    result = await booth.share(session.id, args.get("name"), consent=True)
    if result is None:
        return {"error": "nothing in the notebook yet"}, None, None
    x = result["x"]
    spoken = "It's on the booth wall." + (
        " Hold the ring to post it on X." if x["ready"] else " X posting isn't set up."
    )
    action = {
        "name": "show_share_preview",
        "args": {
            "entry_id": result["entry_id"],
            "card_url": result["card_url"],
            "caption": result["caption"],
            "x_ready": x["ready"],
            "preview_text": x.get("preview_text"),
            "confirm_token": x.get("confirm_token"),  # the headset's hold-to-post sends it
        },
    }
    return {"spoken": spoken, "caption": result["caption"]}, action, None


# --- LLM turn plumbing -------------------------------------------------------------------


def _assistant_msg(message: Any) -> dict[str, Any]:
    """Assistant message ready to round-trip, stripped of Groq's extra `reasoning` field."""
    msg: dict[str, Any] = {"role": "assistant", "content": message.content}
    if message.tool_calls:
        msg["tool_calls"] = [
            {
                "id": tc.id,
                "type": "function",
                "function": {"name": tc.function.name, "arguments": tc.function.arguments},
            }
            for tc in message.tool_calls
        ]
    return msg


def _synth_reply(last_tool: str | None, last_result: dict[str, Any] | None) -> str:
    """Fallback reply when the model emits no content after tool calls."""
    if last_result and "error" in last_result:
        return f"Can't do that yet: {last_result['error']}."
    return _CANNED_REPLY.get(last_tool, "Done.")


def _opening(session: Session) -> Opening | None:
    """assetgen: the session's taped opening as a SearchRequest.opening (None when it's missing or
    malformed)."""
    if not isinstance(session.opening, dict):
        return None
    try:
        return Opening.model_validate(session.opening)
    except ValueError:
        return None


def _apply_context(session: Session, context: dict[str, Any]) -> None:
    """Headset-supplied state overrides the session's own memory of it."""
    if context.get("measurement"):
        session.measurement = context["measurement"]
    if context.get("selected_part_id"):
        session.selected_part_id = context["selected_part_id"]
    if context.get("candidate_ids"):
        session.candidate_ids = context["candidate_ids"]
    if context.get("placed"):
        session.placed = context["placed"]
    if context.get("survey_id"):
        session.survey_id = context["survey_id"]
    if context.get("site"):
        session.site = str(context["site"])
    if context.get("scale"):
        try:
            scale = float(context["scale"])
        except (TypeError, ValueError):
            scale = 0.0
        session.scale = scale if scale > 0 else 1.0
    # assetgen: a taped opening lasts while the headset keeps sending it (its tapes are undone,
    # or newer readings made it stale: it stops).
    session.opening = context.get("opening") if isinstance(context.get("opening"), dict) else None
    # e2e: the headset's own scene-parts state (sent whenever its scene has parts).
    if isinstance(context.get("removed"), list):
        session.removed = [str(x) for x in context["removed"] if x]
        session.cavity = context.get("cavity") if isinstance(context.get("cavity"), dict) else None
    if context.get("asset_mode"):  # asset-mode
        session.asset_mode = assets.normalize_mode(context["asset_mode"])
    # context["tool"] (currently-equipped tool) is headset-side state the agent doesn't need.


def _sellers_line(part) -> str:
    """Spoken pick, e.g. "Home Depot, 2 dollars 74 each, arrives Sep 26." """
    if part.recommended_seller is None or not part.sellers:
        return "No sellers found for that one."
    s = part.sellers[part.recommended_seller]
    price = s.unit_price_usd if s.unit_price_usd is not None else s.price_usd
    bits = [s.name]
    if price is not None:
        bits.append(f"${price:.2f} each" if s.pack_qty > 1 else f"${price:.2f}")
    if s.eta:
        bits.append(f"arrives {s.eta}")
    more = len(part.sellers) - 1
    tail = f" {more} more on the board." if more > 0 else ""
    return ", ".join(bits) + "." + tail


def _checkout_seller(part, hint: str) -> int | None:
    """Seller index for "buy it ...": one named in the command ("from Home Depot"), else the
    cheapest/fastest if asked for, else the part's recommended seller (which already follows the
    user's last "sellers, cheapest first")."""
    lowered = hint.lower()
    for i, s in enumerate(part.sellers):
        if s.name and s.name.lower() in lowered:
            return i
    for sort in ("cheapest", "fastest"):
        if sort in lowered:
            rec = sellers.recommend(part.sellers, sort)
            return rec[0] if rec else None
    if part.recommended_seller is None and part.sellers:
        return 0  # no recommendation computed: the first listing, like F15's pay step
    return part.recommended_seller


def _placed_parts_and_counts(session: Session) -> tuple[list, dict[str, int]]:
    """Parts placed so far, for `what_else` -- `session.placed` (see the `placed` context field,
    folded in by `_apply_context`) if the headset sent one, else the single selected part x1."""
    pairs = (
        [(p["part_id"], int(p.get("count", 1))) for p in session.placed]
        if session.placed
        else ([(session.selected_part_id, 1)] if session.selected_part_id else [])
    )
    parts, counts = [], {}
    for part_id, count in pairs:
        part = jobs.load_part(part_id)
        if part is not None:
            parts.append(part)
            counts[part_id] = count
    return parts, counts


def _bom_reply(result_bom) -> str:
    """Spoken pick, e.g. "Also grab sealant, drip edge and 2 more. $24.81 more." The panel
    shows the full list; speech drops pack specs ("(10 oz tube)") and names at most two."""
    names = [re.sub(r"\s*\(.*?\)", "", line.name).strip().lower() for line in result_bom.lines]
    if len(names) == 1:
        joined = names[0]
    elif len(names) == 2:
        joined = f"{names[0]} and {names[1]}"
    else:
        joined = f"{names[0]}, {names[1]} and {len(names) - 2} more"
    cost = f" ${result_bom.total_usd:.2f} more." if result_bom.total_usd else ""
    return f"Also grab {joined}.{cost}"


def _flythrough_ready(session: Session) -> dict[str, Any] | None:
    """`show_video` for a walk-in clip that finished since the last command (a failed one is
    reported by `GET /scene/flythrough/{job_id}`, which the headset polls anyway)."""
    job = jobs.get(session.flythrough_job_id) if session.flythrough_job_id else None
    if job is None or job.status == "running":
        return None
    session.flythrough_job_id = None
    if job.status != "done":
        return None
    return {"name": "show_video", "args": flythrough.video_args(job.result or {})}


async def handle_command(session_id: str, text: str, context: dict[str, Any]) -> dict[str, Any]:
    """Run one command through to a spoken reply + queued client actions; a walk-in clip or a
    rules check that finished since the last command rides along as a leading `show_video` /
    `show_rules`."""
    session = _get_session(session_id)
    ready = [r for r in (_flythrough_ready(session), _rules_ready(session)) if r]
    try:
        result = await _handle_command(session_id, text, context)
    except _PROVIDER_ERRORS as exc:
        logger.warning("agent: %r failed on a provider: %s: %s", text, type(exc).__name__, exc)
        result = {"reply": BUSY_REPLY, "actions": [], "job_id": None}
    if ready:
        result["actions"] = [*ready, *result["actions"]]
    return result


async def _handle_command(session_id: str, text: str, context: dict[str, Any]) -> dict[str, Any]:
    text = replace.normalize((text or "")[:MAX_TEXT_LEN])  # "34 1⁄2 inches", number words
    ctx = context or {}
    session = _get_session(session_id)
    _apply_context(session, ctx)
    session.last_text = text

    limits, rest = _extract_limits(text)
    # Money in a rules or quote command is about rebates or the quote, not a purchase limit.
    if limits and not (_RULES_RE.search(text) or _QUOTE_RE.search(text)):
        # "Find hangers, under $40, arriving by Friday": set the limits deterministically, then
        # route what's left of the command (if anything) as usual.
        result, action, _ = await _run_tool(session, "set_limits", limits, ctx)
        reply = f"Can't do that yet: {result['error']}." if "error" in result else result["spoken"]
        head = {"reply": reply, "actions": [action] if action else [], "job_id": None}
        if not _has_more(rest):
            return head
        tail = await _handle_command(session_id, rest, context)
        return {
            **tail,
            "reply": f"{head['reply']} {tail['reply']}".strip(),
            "actions": head["actions"] + tail["actions"],
        }

    fast = _fast_route(session, text, ctx)
    if fast is not None:
        name, args, canned_reply = fast
        result, action, job_id = await _run_tool(session, name, args, ctx)
        if "error" in result:
            reply = f"Can't do that yet: {result['error']}."
        else:
            reply = result.get("spoken") or canned_reply
        return {"reply": reply, "actions": _as_list(action), "job_id": job_id}

    if get_settings().OFFLINE:
        # No uplink for the LLM tool-calling path; only the fast-path intents above still work,
        # plus a question about the selected part from its cached manual ("what drill bit do I
        # need?" -- checked first, _FIND_RE would take its "need"), and a literal "find a
        # <thing>" served from the warmed search cache.
        selected = session.selected_part_id
        if (
            selected
            and _QUESTION_RE.search(text)
            and not re.search(r"\b(?:find|buy|order)\b", text, re.IGNORECASE)
            and manuals.indexed(selected)
        ):
            result, action, _ = await _run_tool(session, "ask_manual", {"question": text}, ctx)
            reply = result.get("spoken") or _synth_reply("ask_manual", result)
            return {"reply": reply, "actions": [action] if action else [], "job_id": None}
        m = _FIND_RE.search(text)
        if m:
            _, action, job_id = await _run_tool(session, "find_part", {"query": m.group(1)}, ctx)
            return {"reply": "Searching the supply shops.", "actions": [action], "job_id": job_id}
        return {"reply": OFFLINE_REPLY, "actions": [], "job_id": None}

    session.history.append({"role": "user", "content": text})
    session.history = session.history[-MAX_HISTORY:]

    d = _digest(session)  # the scene's structure counts: numbers the model may say
    system = SYSTEM_PROMPT + (" " + d.llm_line() if d is not None else "")
    removable = [c for c in replace.components(session.site) if c.get("removable", True)]
    if removable:  # autonomy: what replace_component can take out
        names = ", ".join(replace.label(c) for c in removable)
        system += (
            f" Scene parts that come out: {names}. To replace one with a new one that fits, "
            "call replace_component (it does the whole job)."
        )
    messages: list[dict[str, Any]] = [{"role": "system", "content": system}, *session.history]
    actions: list[dict[str, Any]] = []
    job_id: str | None = None
    reply = ""
    last_tool: str | None = None
    last_result: dict[str, Any] | None = None

    # Nothing to look at = don't offer the model the view tools (saves tokens too).
    hidden = set()
    if not ctx.get("frames"):
        hidden.add("ask_scene")
    if not ctx.get("site") or not (ctx.get("frame_id") or ctx.get("frames")):
        hidden.add("reimagine_view")
    site, frame_id, b64 = _site_view(ctx)
    if not b64 and not (site and frame_id):
        hidden.add("see_it_installed")
    if not b64:  # no quote in view
        hidden.add("check_quote")
    if not ctx.get("site"):  # no scan to plan on
        hidden.add("plan_placement")
    if session.reimagine is None:
        hidden.add("refine_reimagine")
    walk_from_refine = session.rv_fresh and session.reimagine and session.reimagine["steps"]
    if (not ctx.get("site") or not ctx.get("frame_id")) and not walk_from_refine:
        hidden.add("walk_in_preview")
    if not ctx.get("site"):  # no drone scene to survey or coach
        hidden |= {"survey_condition", "coach_capture"}
    # check_rules stays offered: "a permit for a mini-split?" names its job with no part picked.
    if report._notebook_entries(session.id) is None:  # nothing to put in a packet
        hidden.add("send_packet")
    if packet.latest_public(session.id) is None:  # no live link to take down
        hidden.add("revoke_packet")
    if not (ctx.get("frames") or ctx.get("frame_jpg_b64")):  # no frame to label
        hidden.add("label_view")
    if coach.current(session.coach_id, session.id) is None:  # nothing to step through
        hidden |= {"coach_step", "check_step"}
    if not _SHARE_RE.search(text) or "send_packet" in hidden:  # no consent phrase, or nothing
        hidden.add("share_design")
    if not session.tape_survey:  # no tape survey reported yet: nothing to answer from
        hidden.add("survey_query")
    if not removable:  # autonomy: nothing in this scene comes out
        hidden.add("replace_component")
    tools = [t for t in TOOLS if t["function"]["name"] not in hidden]
    for _turn in range(MAX_TURNS):
        resp = await chat("agent", messages, tools=tools, reasoning_effort="low")
        message = resp.choices[0].message
        content = (message.content or "").strip()
        tool_calls = message.tool_calls or []
        has_find_part = any(tc.function.name == "find_part" for tc in tool_calls)
        # These already hold the spoken answer: no second agent turn to say it, and they run
        # even when the model also returned text (that text becomes the fallback reply).
        ends_turn = any(
            tc.function.name
            in (
                "ask_scene",
                "reimagine_view",
                "refine_reimagine",
                "find_installer",
                "see_it_installed",
                "check_safety",
                "plan_placement",
                "walk_in_preview",
                "ask_manual",
                "survey_condition",
                "set_finish",
                "coach_capture",
                "check_rules",
                "check_quote",
                "send_packet",
                "revoke_packet",
                "run_job",
                "replace_component",
                "show_model",
                "label_view",
                *COACH_TOOLS,
                "share_design",
                *_OWN_LINE_TOOLS,
            )
            for tc in tool_calls
        )

        if content and not (has_find_part or ends_turn):
            reply = content
            break

        if not tool_calls:
            break

        messages.append(_assistant_msg(message))
        spoken: list[str] = []  # _OWN_LINE_TOOLS' lines (and a template survey answer), in order
        answered = False  # a template survey_query answer is final, like ask_scene's
        for tc in tool_calls:
            name = tc.function.name
            # find_part just starts a background job -- nothing else in this turn has
            # results to act on yet, so drop every other tool call except equip_tool/
            # add_note (client-side no-ops that don't depend on search results).
            if has_find_part and name not in ("find_part", "equip_tool", "add_note", "set_limits"):
                continue
            try:
                args = json.loads(tc.function.arguments or "{}")
            except json.JSONDecodeError:
                args = {}
            last_tool = name
            last_result, action, job = await _run_tool(session, last_tool, args, ctx)
            if last_result.get("spoken") and (name in _OWN_LINE_TOOLS or name == "survey_query"):
                spoken.append(last_result["spoken"])
                answered = answered or name == "survey_query"
            actions += _as_list(action)
            if job:
                job_id = job
            messages.append(
                {"role": "tool", "tool_call_id": tc.id, "content": json.dumps(last_result)}
            )

        if has_find_part:
            reply = content or _CANNED_REPLY["find_part"]
            break

        if last_tool in ("check_safety", "start_checkout") and (last_result or {}).get("spoken"):
            reply = last_result["spoken"]  # safety wording goes out verbatim, not paraphrased
            break

        if ends_turn or answered:
            # The tool's own words (a plan's computed numbers, the intel summary) beat the
            # model's content; ask_scene answers in "answer". Every app-lane tape line said in
            # this turn goes first, in call order.
            last = last_result or {}
            theirs = (
                last.get("spoken") if last_tool not in (*_OWN_LINE_TOOLS, "survey_query") else None
            )
            if "error" in last:
                reply = (
                    " ".join(spoken)
                    or last.get("spoken")
                    or content
                    or _synth_reply(last_tool, last)
                )
            else:
                reply = (
                    " ".join([*spoken, *([theirs] if theirs else [])])
                    or content
                    or last.get("answer", "")
                    or _CANNED_REPLY.get(last_tool or "", "Done.")
                )
            break

    if not reply:
        reply = _synth_reply(last_tool, last_result)

    session.history.append({"role": "assistant", "content": reply})
    session.history = session.history[-MAX_HISTORY:]
    return {"reply": reply, "actions": actions, "job_id": job_id}


def observe(obs: tape_survey.Observation) -> dict[str, Any]:
    """`POST /agent/observe`: the headset reports what its tape measured for a `survey` or
    `check_slope` action (app lane; F10's condition survey never comes back here). Everything here is a template (no LLM, works OFFLINE): size groups and
    the spoken summary, or the gutter drainage verdict. The reply joins the session history so
    follow-ups ("which is the widest?") have it."""
    session = _get_session(obs.session_id)
    pending = session.pending.pop(obs.request_id, None) if obs.request_id else None
    pending = pending or {}
    actions: list[dict[str, Any]] = []

    if obs.kind == "survey_result":
        first_label = obs.results[0].label if obs.results else None
        label = obs.label or pending.get("label") or first_label or "any"
        measure = obs.measure or pending.get("measure") or "size"
        summary = tape_survey.survey_summary(obs, label, measure)
        reply = summary["reply"]
        if obs.results:
            session.tape_survey = {
                "request_id": obs.request_id,
                "label": label,
                "measure": measure,
                "query": obs.query or pending.get("query"),
                "items": [r.model_dump() for r in obs.results],
                "groups": summary["groups"],
                "unverified": summary["unverified"],
                "skipped": summary["skipped"],
            }
            actions.append(_show_tape_survey_action(session.tape_survey, []))
    else:
        residual = obs.gravity_residual_deg
        if residual is None:
            d = _digest(session)
            residual = d.gravity_residual_deg if d is not None else None
        unc = obs.uncertainty_mm
        if unc is None:
            unc = tape_survey.slope_uncertainty_mm(obs.run_m, residual)
        verdict = tape_survey.slope_verdict(obs.run_m, obs.fall_mm, unc)
        target = obs.target or pending.get("target") or "gutter"
        reply = tape_survey.slope_reply(verdict)
        note = (
            f"{target.replace('_', ' ').capitalize()} slope: {verdict['verdict']} "
            f"({verdict['fall_mm']:g} ± {verdict['uncertainty_mm']:g} mm of fall over "
            f"{verdict['run_m']:.2f} m; needs {verdict['required_mm']:g} mm)"
        )
        actions.append({"name": "add_note", "args": {"text": note}})
        session.notes.append(note)

    session.history.append({"role": "assistant", "content": reply})
    session.history = session.history[-MAX_HISTORY:]
    return {"reply": reply, "actions": actions, "job_id": None}
