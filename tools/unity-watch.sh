#!/bin/bash
# Runs a batch Unity command and watches its log: prints progress every 30 s and
# stops Unity when the shader compiler keeps crashing or nothing moves for too long.
#   unity-watch.sh LOGFILE UNITY ARGS...     (env: CRASHES=5 STALL=600 seconds)
log=$1; shift
crashes_max=${CRASHES:-5}; stall_max=${STALL:-600}
: > "$log"
"$@" -logFile "$log" > /dev/null 2>&1 &
pid=$!
last_size=0; still=0
while kill -0 $pid 2>/dev/null; do
  sleep 30
  size=$(stat -c %s "$log" 2>/dev/null || echo 0)
  crashes=$(grep -c "Crashed!" "$log")
  busy=$(ps -eo pcpu,comm | awk '$2=="UnityShaderComp" && $1>50' | wc -l)
  if [ "$size" = "$last_size" ] && [ "$busy" = 0 ]; then still=$((still + 30)); else still=0; fi
  last_size=$size
  echo "[watch] $(date +%T) log=$((size / 1024))K crashes=$crashes compilers_busy=$busy quiet=${still}s last: $(grep -E '^\[|Compiling|Shader error' "$log" | tail -1 | cut -c1-120)"
  if [ "$crashes" -ge "$crashes_max" ]; then echo "[watch] shader compiler crashed $crashes times: stopping"; kill -9 $pid; pkill -9 -x UnityShaderComp; exit 3; fi
  if [ "$still" -ge "$stall_max" ]; then echo "[watch] nothing for ${still}s: stopping"; kill -9 $pid; pkill -9 -x UnityShaderComp; exit 4; fi
done
wait $pid; rc=$?
echo "[watch] unity exited rc=$rc"
exit $rc
