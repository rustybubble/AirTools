from server import jobs
from server.config import get_settings
from server.models import Dims, Fit, Job, Part


def _part(name, fit):
    return Part(id="p", name=name, dims_mm=Dims(w=1, d=1, h=1), fit=fit)


def test_summary_uses_query_noun_and_fit():
    job = Job(
        id="j",
        status="done",
        query="gutter hanger",
        candidates=[
            _part(
                "Amerimax 5 in. Hidden Gutter Hanger with Screw", Fit(status="fits", spare_mm=38)
            ),
            _part("Other", Fit()),
            _part("Third", Fit()),
        ],
    )
    assert jobs.summary(job) == "Three hangers. The first fits with 38 millimetres to spare."


def test_summary_exact_and_too_small_and_empty():
    one = Job(id="j", status="done", query="window air conditioner")
    one.candidates = [_part("LG 6000 BTU", Fit(status="fits", spare_mm=0.4))]
    assert jobs.summary(one) == "One conditioner. It is an exact fit."
    one.candidates = [_part("LG", Fit(status="too_small", spare_mm=-30))]
    assert jobs.summary(one).endswith("is too small by 30 millimetres.")
    assert "No parts found" in jobs.summary(Job(id="j", status="done", query="x"))


def test_summary_offline_no_candidates(monkeypatch):
    monkeypatch.setenv("OFFLINE", "true")
    get_settings.cache_clear()
    try:
        empty = Job(id="j", status="done", query="gutter hanger")
        assert jobs.summary(empty) == jobs.OFFLINE_NO_CANDIDATES
    finally:
        get_settings.cache_clear()


# --- unbounded _jobs/_done_events: LRU cap -----------------------------------------------


def test_evict_old_jobs_keeps_running_jobs():
    jobs._jobs.clear()
    jobs._done_events.clear()
    try:
        for i in range(jobs.MAX_JOBS + 5):
            status = "running" if i < 5 else "done"
            jobs._jobs[f"j{i}"] = Job(id=f"j{i}", status=status)
            jobs._done_events[f"j{i}"] = None

        jobs._evict_old_jobs()

        assert len(jobs._jobs) <= jobs.MAX_JOBS
        for i in range(5):  # oldest 5 are "running" -- must survive despite being oldest
            assert f"j{i}" in jobs._jobs
            assert f"j{i}" in jobs._done_events
        assert "j5" not in jobs._jobs  # oldest non-running job is the one evicted
        assert "j5" not in jobs._done_events
    finally:
        jobs._jobs.clear()
        jobs._done_events.clear()


def test_save_part_keeps_ready_asset_on_re_search(monkeypatch, tmp_path):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    get_settings.cache_clear()
    ready = _part("Hanger", Fit())
    ready.asset.status, ready.asset.tier = "ready", "ai_mesh"
    jobs.save_part(ready)
    (jobs.assets.part_dir("p") / "model.glb").write_bytes(b"glb")

    jobs.save_part(_part("Hanger", Fit()))  # fresh search result, asset pending

    assert jobs.load_part("p").asset.tier == "ai_mesh"
    get_settings.cache_clear()


def test_summary_noun_skips_clauses_numbers_and_filler():
    for query, expected in [
        ("joist hanger for a 2x6", "Two hangers."),
        ("shower head replacement", "Two heads."),
        ("window air conditioner", "Two conditioners."),
    ]:
        job = Job(id="j", status="done", query=query, candidates=[_part("a", Fit()), _part("b", Fit())])
        assert jobs.summary(job).startswith(expected), query


def test_summary_length_run_fits_the_run():
    """Integration finding #11: axis "length" -> compute_fit says fits with spare_mm None, and
    the summary used to fall through to "Fit against the measurement is unknown."."""
    from server import search
    from server.models import Measurement

    fit = search.compute_fit(
        Dims(w=127, d=38, h=45),
        Measurement(label="gutter run", value_m=3.66, axis="length"),
        "length",
        None,
    )
    assert (fit.status, fit.spare_mm) == ("fits", None)
    many = Job(
        id="j",
        status="done",
        query="gutter hanger",
        candidates=[_part("a", fit), _part("b", Fit())],
    )
    assert jobs.summary(many) == "Two hangers. The first fits the run."
    one = Job(id="j", status="done", query="gutter hanger", candidates=[_part("a", fit)])
    assert jobs.summary(one) == "One hanger. It fits the run."
    assert "unknown" in jobs.summary(
        Job(id="j", status="done", query="hinge", candidates=[_part("a", Fit())])
    )
