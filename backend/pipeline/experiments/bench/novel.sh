#!/bin/bash
# novel.sh NAME SCENE : E3's novel-view coverage (back 0.6 m / right 0.9 m) -> results/novel.jsonl
source /workspace/env.sh; cd /workspace/airtools || exit 1
taskset -c ${CORES:-0-15} uv run --extra pipeline python -m pipeline.experiments.bench.novel_coverage \
  --ref /workspace/bench/ref "$2" | sed "s/^{/{\"name\": \"$1\", /" >> /workspace/bench/results/novel.jsonl
