"""In-memory index of every part known locally, for the catalog and its lazy search.

Sources (`Index.load`): `<DATA_DIR>/parts/*/part.json` (resolved parts; `asset.status`) and the
search caches `<DATA_DIR>/cache/search*/` (each cached result says which query found which part,
in rank order). Built at start in a background thread, rescanned incrementally (by file mtime) at
most every `REFRESH_S` when the catalog is used, and updated at once when a search finishes in
this process (`add_parts`, called by jobs._run_search and the catalog's own searches).

`search` never touches the disk or the network: tokens -> an inverted index with exact, prefix
(the word being typed) and bounded-edit-distance fuzzy matches, AND across the query's words
(OR when nothing has them all). Each document carries its name, brand, model number, category
words and the queries that found it, weighted by field.

Readers take `self._state` once; writers build a new state under a lock and swap it in, so a
request never sees a half-built index.
"""

import bisect
import json
import logging
import os
import re
import threading
import time
from dataclasses import dataclass, field, replace
from pathlib import Path

from server import catalog_tables as tables
from server.config import get_settings

logger = logging.getLogger(__name__)

REFRESH_S = 10.0
FIELD_WEIGHTS = {
    "name": 1.0,
    "model": 1.0,
    "brand": 0.9,
    "category": 0.8,
    "query": 0.7,
    "other": 0.4,
}
STOP = {"a", "an", "the", "for", "with", "and", "of", "to", "by", "in", "on", "x"}
_TOKEN_RE = re.compile(r"[a-z0-9]+")
_PREFIX_CAP = 400  # vocabulary words one typed prefix may expand to


def tokens(text: str | None) -> list[str]:
    return _TOKEN_RE.findall((text or "").lower().replace("&", " "))


def _with_singulars(words) -> set[str]:
    """The words (stop words dropped) plus their singulars."""
    out = set()
    for w in words:
        if w in STOP:
            continue
        out.add(w)
        if len(w) > 3 and w.endswith("ies"):
            out.add(w[:-3] + "y")
        elif len(w) > 3 and w.endswith("s") and not w.endswith("ss"):
            out.add(w[:-1])
    return out


def bounded_edit(a: str, b: str, k: int) -> tuple[int, int]:
    """(edit distance a->b, edit distance a -> the closest prefix of b), each capped at k+1.
    Only the first len(a)+k characters of b can matter for either within k."""
    big = k + 1
    full_possible = len(b) <= len(a) + k
    b = b[: len(a) + k]
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i] + [0] * len(b)
        row_min = i
        for j, cb in enumerate(b, 1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb))
            row_min = min(row_min, cur[j])
        if row_min > k:
            return big, big
        prev = cur
    full = prev[-1] if full_possible else big
    return min(full, big), min(min(prev), big)


@dataclass(frozen=True)
class Doc:
    part_id: str
    name: str
    brand: str | None
    model_no: str | None
    category: str | None  # the curated category this part belongs to (catalog_tables.classify)
    queries: dict  # normalized search query -> the part's rank in that result
    price_usd: float | None
    seller: str | None
    dims_mm: dict
    image_remote: str | None
    ready_hint: bool  # part.json says asset.status == "ready" (the response re-checks the disk)
    has_part_json: bool
    order: int  # first-seen order, for stable sorts
    other: str = ""  # finish / material / colour words
    part: dict = field(default_factory=dict, compare=False, repr=False)


def _price_and_seller(part: dict) -> tuple[float | None, str | None]:
    sellers = part.get("sellers") or []
    rec = part.get("recommended_seller")
    seller = sellers[rec] if isinstance(rec, int) and 0 <= rec < len(sellers) else None
    seller = seller or next((s for s in sellers if s.get("price_usd") is not None), None)
    seller = seller or (sellers[0] if sellers else None)
    if not seller:
        return None, None
    price = seller.get("price_usd")
    if price is None:
        price = seller.get("total_usd")
    return (round(float(price), 2) if price is not None else None), seller.get("name")


def _categorize(name: str, queries: dict) -> str | None:
    """An exact curated query that found the part, else its name, else any query's words."""
    if tables.is_junk(name):
        return None
    ranked = sorted(queries, key=lambda q: queries[q])
    for q in ranked:
        if tables.is_curated_query(q):
            return tables.classify(q)
    by_name = tables.classify(name)
    if by_name is not None:
        return by_name
    return next((c for q in ranked if (c := tables.classify(q)) is not None), None)


def make_doc(
    part: dict,
    old: Doc | None,
    *,
    order: int,
    from_part_json: bool,
    query: str | None = None,
    rank: int = 0,
) -> Doc | None:
    pid = part.get("id")
    name = part.get("name")
    if not pid or not name or not isinstance(part.get("dims_mm"), dict):
        return None
    queries = dict(old.queries) if old else {}
    if query:
        q = tables.normalize_query(query)
        queries[q] = min(rank, queries.get(q, rank))
    if old is not None and old.has_part_json and not from_part_json:
        # part.json is the durable copy: a cached search dump only adds the query that found it
        return replace(old, queries=queries, category=_categorize(old.name, queries))
    price, seller = _price_and_seller(part)
    dims = part["dims_mm"]
    other = " ".join(str(part.get(k) or "") for k in ("finish", "material", "manufacturer"))
    return Doc(
        part_id=pid,
        name=name,
        brand=part.get("manufacturer"),
        model_no=part.get("model_no"),
        category=_categorize(name, queries),
        queries=queries,
        price_usd=price,
        seller=seller,
        dims_mm={k: dims.get(k) for k in ("w", "h", "d")},
        image_remote=part.get("image_url"),
        ready_hint=((part.get("asset") or {}).get("status") == "ready"),
        has_part_json=from_part_json or bool(old and old.has_part_json),
        order=old.order if old else order,
        other=other,
        part=part,
    )


@dataclass
class _State:
    docs: dict  # part_id -> Doc
    inv: dict  # token -> {part_id: field weight}
    vocab: list  # sorted tokens
    buckets: dict  # first letter -> tokens


def _field_tokens(doc: Doc) -> dict[str, set[str]]:
    cat = tables.CATEGORIES.get(doc.category or "")
    cat_words = set()
    if cat is not None:
        cat_words = set(tokens(cat.title)) | {t for k in cat.keywords for t in tokens(k)}
    model = set(tokens(doc.model_no))
    if doc.model_no:
        model.add(re.sub(r"[^a-z0-9]", "", doc.model_no.lower()))
    return {
        "name": _with_singulars(tokens(doc.name)),
        "model": model,
        "brand": _with_singulars(tokens(doc.brand)),
        "category": _with_singulars(cat_words),
        "query": _with_singulars(t for q in doc.queries for t in tokens(q)),
        "other": _with_singulars(tokens(doc.other)),
    }


def _build_state(docs: dict) -> _State:
    inv: dict[str, dict[str, float]] = {}
    for doc in docs.values():
        for fname, words in _field_tokens(doc).items():
            w = FIELD_WEIGHTS[fname]
            for t in words:
                if not t:
                    continue
                postings = inv.setdefault(t, {})
                if w > postings.get(doc.part_id, 0.0):
                    postings[doc.part_id] = w
    vocab = sorted(inv)
    buckets: dict[str, list[str]] = {}
    for t in vocab:
        buckets.setdefault(t[0], []).append(t)
    return _State(docs=docs, inv=inv, vocab=vocab, buckets=buckets)


class Index:
    def __init__(self) -> None:
        self._state = _State({}, {}, [], {})
        self._write = threading.Lock()
        self._mtimes: dict[str, float] = {}
        self._order = 0
        self._loading = False
        self.loaded_at = 0.0  # monotonic time of the last disk scan (0 = never)
        self.load_ms = 0.0
        self.data_dir: str | None = None

    # --- writers ----------------------------------------------------------------------------

    def _next_order(self) -> int:
        self._order += 1
        return self._order

    def add_parts(
        self, parts: list[dict], query: str | None = None, from_part_json: bool = False
    ) -> None:
        """Parts (Part.model_dump(mode="json") dicts) a search just found, in rank order."""
        if not parts:
            return
        with self._write:
            docs = dict(self._state.docs)
            for rank, part in enumerate(parts):
                pid = part.get("id")
                doc = make_doc(
                    part,
                    docs.get(pid),
                    order=self._next_order(),
                    from_part_json=from_part_json,
                    query=query,
                    rank=rank,
                )
                if doc is not None:
                    docs[doc.part_id] = doc
            self._state = _build_state(docs)

    def load(self, data_dir: str | None = None) -> int:
        """Scan the parts dir and the search caches; (re)read only files whose mtime changed.
        Returns how many files were read."""
        data = Path(data_dir or get_settings().DATA_DIR)
        start = time.monotonic()
        with self._write:
            if self.data_dir != str(data):  # a new DATA_DIR (tests): start over
                self._state, self._mtimes, self.data_dir = _State({}, {}, [], {}), {}, str(data)
            docs = dict(self._state.docs)
            read = 0
            for path, query in self._changed_files(data):
                try:
                    body = json.loads(path.read_text())
                except (OSError, ValueError):
                    continue
                read += 1
                if query is None:  # a part.json
                    doc = make_doc(
                        body,
                        docs.get(body.get("id")),
                        order=self._next_order(),
                        from_part_json=True,
                    )
                    if doc is not None:
                        docs[doc.part_id] = doc
                    continue
                key, value = body.get("key"), body.get("value")
                q = key.get("q") if isinstance(key, dict) else key
                if not isinstance(q, str) or not isinstance(value, list):
                    continue
                for rank, part in enumerate(value):
                    if isinstance(part, dict):
                        doc = make_doc(
                            part,
                            docs.get(part.get("id")),
                            order=self._next_order(),
                            from_part_json=False,
                            query=q,
                            rank=rank,
                        )
                        if doc is not None:
                            docs[doc.part_id] = doc
            if read:
                self._state = _build_state(docs)
            self.loaded_at = time.monotonic()
            self.load_ms = (self.loaded_at - start) * 1000
        return read

    def _changed_files(self, data: Path):
        """(path, None) for each changed part.json; (path, "q") for each changed search cache
        file. part.json first, so a part's durable copy wins over a cached dump."""
        found = []
        parts_dir = data / "parts"
        if parts_dir.is_dir():
            with os.scandir(parts_dir) as it:
                for entry in it:
                    path = Path(entry.path) / "part.json"
                    found.append((path, None))
        for ns in ("search_by_query", "search"):
            ns_dir = data / "cache" / ns
            if ns_dir.is_dir():
                with os.scandir(ns_dir) as it:
                    for entry in it:
                        if entry.name.endswith(".json"):
                            found.append((Path(entry.path), "q"))
        for path, kind in found:
            try:
                mtime = path.stat().st_mtime
            except OSError:
                continue
            if self._mtimes.get(str(path)) != mtime:
                self._mtimes[str(path)] = mtime
                yield path, kind

    def start(self) -> None:
        """Build the index in a background thread (server start); requests that come first
        wait for it on the write lock."""
        if self.loaded_at == 0.0 and not self._loading:
            self._loading = True

            def run() -> None:
                try:
                    self.load()
                except Exception:
                    logger.exception("catalog index build failed")
                finally:
                    self._loading = False
                logger.info("catalog index: %d parts in %.0f ms", len(self), self.load_ms)

            threading.Thread(target=run, name="catalog-index", daemon=True).start()

    def ensure_loaded(self) -> None:
        """Load synchronously the first time (or after DATA_DIR changed)."""
        if self.loaded_at == 0.0 or self.data_dir != str(Path(get_settings().DATA_DIR)):
            self.load()

    def maybe_refresh(self) -> None:
        """A background rescan when the last one is older than REFRESH_S (never blocks)."""
        self.ensure_loaded()
        if self._loading or time.monotonic() - self.loaded_at < REFRESH_S:
            return
        self._loading = True
        data_dir = self.data_dir

        def run() -> None:
            try:
                self.load(data_dir)
            except Exception:
                logger.exception("catalog index refresh failed")
            finally:
                self._loading = False

        threading.Thread(target=run, name="catalog-index", daemon=True).start()

    # --- readers ----------------------------------------------------------------------------

    def __len__(self) -> int:
        return len(self._state.docs)

    def get(self, part_id: str) -> Doc | None:
        return self._state.docs.get(part_id)

    def docs(self) -> list[Doc]:
        return list(self._state.docs.values())

    def in_category(self, category: tables.Category) -> list[Doc]:
        """The category's parts, best first: found by its own query, then by an alias, then by
        name; ready models first within each, then the search's own rank."""
        own = tables.normalize_query(category.query)
        aliases = {tables.normalize_query(a) for a in category.aliases}
        out = []
        for doc in self._state.docs.values():
            if category.extra:
                member = own in doc.queries or (
                    doc.category is None and tables.match_strength(category, doc.name) > 0
                )
            else:
                member = doc.category == category.id
            if not member:
                continue
            if own in doc.queries:
                src, rank = 0, doc.queries[own]
            elif aliases & doc.queries.keys():
                src, rank = 1, min(doc.queries[a] for a in aliases & doc.queries.keys())
            else:
                src, rank = 2, 99
            out.append(((src, not doc.ready_hint, rank, doc.order), doc))
        out.sort(key=lambda pair: pair[0])
        return [doc for _, doc in out]

    def search(
        self, query: str, limit: int = 20, boost: frozenset | set = frozenset()
    ) -> list[tuple[Doc, float]]:
        """Best matches for a partly typed query. `boost`: category ids to lift (the site's)."""
        st = self._state
        words = tokens(query)
        qwords = [w for w in words if w not in STOP] or words
        if not qwords or not st.docs:
            return []
        per_word: list[dict[str, float]] = []
        for i, w in enumerate(qwords):  # the last word is the one being typed: a prefix
            per_word.append(self._match_word(st, w, typing=i == len(qwords) - 1))
        pool = set.intersection(*(set(s) for s in per_word))
        partial = not pool
        if partial:
            # No part has every word: the parts with some of them, whole words only -- the
            # prefixes and typos of a query nothing matches are noise ("light" ~ "lightweight").
            per_word = [self._match_word(st, w, typing=False, exact=True) for w in qwords]
            pool = set.union(*(set(s) for s in per_word))
        scored = []
        for pid in pool:
            hits = [s[pid] for s in per_word if pid in s]
            score = sum(hits)
            if partial:
                score *= len(hits) / len(per_word)
            doc = st.docs[pid]
            if doc.category in boost:
                score += 0.3
            scored.append((doc, round(score, 4)))
        scored.sort(key=lambda p: (-p[1], not p[0].ready_hint, len(p[0].name), p[0].order))
        return scored[: max(1, limit)]

    @staticmethod
    def _match_word(st: _State, w: str, typing: bool, exact: bool = False) -> dict[str, float]:
        """part_id -> best (match quality x field weight) for one query word. `exact`: the
        word itself only."""
        quality: dict[str, float] = {}
        if w in st.inv:
            quality[w] = 1.0
        # the word being typed is a prefix; so is a clipped word
        if not exact and (typing or len(w) >= 3):
            lo = bisect.bisect_left(st.vocab, w)
            for v in st.vocab[lo : lo + _PREFIX_CAP]:
                if not v.startswith(w):
                    break
                if v != w:
                    quality[v] = max(quality.get(v, 0.0), 0.9 if typing else 0.8)
        if not exact and len(w) >= 4:  # typos: edit distance 1 (<= 6 letters) or 2
            k = 1 if len(w) <= 6 else 2
            for v in st.buckets.get(w[0], ()):
                if v in quality or (abs(len(v) - len(w)) > k and not typing):
                    continue
                full, prefix = bounded_edit(w, v, k)
                if full <= k:
                    quality[v] = 0.75 - 0.1 * full
                elif typing and prefix <= k and len(v) > len(w):
                    quality[v] = 0.6 - 0.1 * prefix
        scores: dict[str, float] = {}
        for v, q in quality.items():
            for pid, weight in st.inv[v].items():
                s = q * weight
                if s > scores.get(pid, 0.0):
                    scores[pid] = s
        return scores


INDEX = Index()
