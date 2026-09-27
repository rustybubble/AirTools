#!/bin/bash
# idle_check.sh NAME : log box state before an idle-box timed job (load, GPU processes, top CPU users,
# and this shell's affinity) to /workspace/bench/logs/idle.txt.
{
  echo "=== $1 $(date -Is)"
  uptime
  taskset -cp $$
  echo "gpu busy: $(cat /sys/class/drm/card*/device/gpu_busy_percent 2>/dev/null | tr '\n' ' ')%"  # no rocm-smi on hfbox
  for d in /proc/[0-9]*; do  # processes holding the GPU (/dev/kfd or a render node)
    ls -l $d/fd 2>/dev/null | grep -q "kfd\|renderD" && echo "gpu pid ${d#/proc/}: $(tr '\0' ' ' < $d/cmdline | cut -c1-120)"
  done
  ps -eo pid,pcpu,etime,args --sort=-pcpu | head -6 | cut -c1-160
} >> /workspace/bench/logs/idle.txt 2>&1
