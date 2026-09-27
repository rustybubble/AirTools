#!/bin/bash
# Sequential job queue: runs /workspace/bench/queue/*.sh oldest-first, one at a time (timing fairness).
Q=/workspace/bench/queue; mkdir -p $Q /workspace/bench/done /workspace/bench/logs
while true; do
  job=$(ls -1tr $Q/*.sh 2>/dev/null | head -1)
  if [ -z "$job" ]; then sleep 10; continue; fi
  name=$(basename $job .sh)
  mv $job /workspace/bench/done/$name.sh
  t0=$(date +%s)
  bash /workspace/bench/done/$name.sh > /workspace/bench/logs/$name.log 2>&1
  echo "$name exit=$? wall=$(( $(date +%s)-t0 )) end=$(date +%H:%M:%S)" >> /workspace/bench/status.txt
done
