# Backend hand-off: cancel a job run (`POST /job/run/{run_id}/cancel`)

*Sun 2026-09-27, app switchclean lane → P4. One patch on top of `catalog-on-asset-mode` (`42c0899`, the demo server's
branch on `:8004`). Made and tested in a scratch worktree (`backend-jobcancel`, branch `job-cancel`, commit `9ef4731`).
Nothing changed on `:8004`, and nothing was pushed.*

## Why

The headset now stops following a job run when the user switches to another scanned model mid-run. This covers "do the
whole job" and the autonomous replace ("replace the dishwasher"). A job must never act on a model it wasn't started
for. Without this route the server's run carried on for minutes: a replace's search can take up to 150 s and its model
up to 60 s more. During that time every new job in the session was refused with "Can't do that yet: a job is already
running; give it a moment". This route stops the run at once and frees the session.

## How to apply

```sh
cd airtools-drone-backend            # or ~/airtools-backend-demo
git switch catalog-on-asset-mode     # 42c0899
git am /path/to/AirTools/docs/handoff/p4-jobcancel/*.patch
uv run pytest -q tests/test_job_cancel.py tests/test_runjob.py tests/test_replace_job.py tests/test_jobs.py
```

- The patch applies with `git am` on `42c0899`. The result is byte-identical to the tested tree (`8d6285f`).
- It adds no new dependencies and no new env vars. Restart uvicorn afterwards.
- Tests:
  - `tests/test_job_cancel.py` (new) has 5 tests. With the other job tests that makes 71 passed.
  - The full suite gives 1534 passed, 1 skipped and 3 failed. The 3 failures are `tests/pipeline/test_structure_*`
    (`os.sched_getaffinity` doesn't exist on macOS; nothing this patch touches).
  - No test calls an LLM or the network.
- `ruff check` and `ruff format --check` are clean on the changed files.

## The contract

`POST /job/run/{run_id}/cancel` with an optional JSON body `{session_id?, reason?}`:

| Case | Answer |
|---|---|
| unknown run | 404 `unknown run` |
| `session_id` given and not the run's | 403 `not this session's run` |
| running | 200 `{run_id, status: "cancelled", cancelled: true, next}` |
| already ended (done / stopped / cancelled) | 200 `{run_id, status, cancelled: false, next}`, with nothing changed |

A cancelled run:
- `GET /job/run/{run_id}` reports `status: "cancelled"`. Every step not finished yet is `cancelled`.
- Its actions end with one `job_done {run_id, status: "cancelled", kind, reason?}`. Nothing is written after it: no
  `job_step`, no `place_part`, no second `job_done`.
- Its chain task is cancelled at the await it is in (a search wait, a model build or an LLM call). A replace run's
  next-model builds are cancelled too.
- Background jobs a step started (a search in the jobs table) finish on their own, because other runs and the cache may
  use them.
- The session can start a new job at once.

## Patch

| Patch | What | Tests |
|---|---|---|
| `0001-job-cancel-POST-job-run-run_id-cancel-…` | `runjob.Run.tasks` holds the chain task and, for a replace run, its next-model builds. New `runjob.cancel(run, reason)` and `runjob.cancelled(run)`. Both chains (`runjob._chain` / `_step`, `replace_job._chain` / `_progress`) write nothing after a cancel. `replace_job` cancels `st.others` when its chain is cancelled. `app.py` adds the route (`async def`, so `task.cancel()` runs on the loop). | `tests/test_job_cancel.py`: a replace waiting on its search (the same session starts a new job at once, there is no `place_part`, and one `job_done`), its next-model builds, idempotence and 404, 403, and a whole-job run |

## Live check (port 8013, scratch `DATA_DIR` seeded from `data/cache`, `SCENE_DIR` = the drone backend's scenes)

Ports 8008–8012 were all taken by other lanes' servers, so the check ran on the free port 8013. `:8004` wasn't touched.

1. "replace the range with a 30-inch induction one" on the kitchen. At 0.7 s the run was in its search step.
2. A second "replace the fridge…" in the same session got "a job is already running". This is the old behaviour while a
   run is still going.
3. `POST …/cancel {session_id, reason: "switched to zabel-gymnasium"}` returned `cancelled: true`. The search and every
   later step were `cancelled`, and the last action was `job_done {status: "cancelled", kind: "replace", reason}`.
4. "replace the fridge…" right after the cancel started a new run at once.
5. 4 s later the cancelled run still had 9 actions, 1 `job_done` and no `place_part`. The server log showed
   `run 573eab0a578f (replace) cancelled after 0.7 s: switched to zabel-gymnasium`.
6. Another session's cancel returned 403 and an unknown run returned 404. A dishwasher run that had already finished
   from the cache answered `cancelled: false` and was left as it was.

## The app side (`feat/switch-closes-ui`)

- `GrokRails.CancelForSwitch` stops the run on the headset: no more polls, the strip ends "✗ Cancelled · you switched
  to the Zabel gym", and a quiet toast "Stopped the dishwasher job: you switched to the Zabel gym" shows. It then calls
  `GrokRails.CancelOnServer` = `JobCancelClient.Send`.
- `JobCancelClient.Send` is fire and forget: `PartsClient.PostJson` (`HttpDeadline`, 5 s) sends
  `POST /job/run/{id}/cancel {session_id, reason: "switched to <site>"}`.
- An older server without the route (404) or no answer changes nothing on the headset; the run just finishes there on
  its own, as before.
- A switch the job asked for itself (its own `show_model` / `next_model` / `load_site`) doesn't cancel it.
