#!/usr/bin/env bash
# load.sh <label> <entry dir> <levels> [rate] [settle] [hold] [NAME=value ...]
#
# One staircase of users against a built entry on this machine: the entry as a transient systemd unit on CPU 0 with
# the scorer's limits (65,535 descriptors, 2 GB, no swap), the load generator on the other CPUs. For each level it
# appends a TSV line to lab/out/load.tsv: latencies, requests a second, and the unit's CPU time per request and
# peak memory over the hold (from its cgroup, as the leaderboard reports them).
#
# NAME=value pairs go into the entry's environment (its test settings).
#
# The entry runs in the network namespace that lab/box.sh makes (run "lab/box.sh up" first): its packets cross a
# device, arrive on its own CPU and meet a connection table of the scorer's size. The line also has the busy time of
# CPU 0 per request (softirq included, which no cgroup is charged for). Still not the scorer's machine: read the
# entries against each other, never as a score.
set -euo pipefail
label=$1 dir=$(cd "$2" && pwd) levels=$3 rate=${4:-4000} settle=${5:-20} hold=${6:-60}
extra=(); for kv in "${@:7}"; do [ -n "$kv" ] && extra+=("--setenv=$kv"); done
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/lab/out"; mkdir -p "$OUT"
LG="$ROOT/lab/loadgen/bin/lg/loadgen"
unit="entry-$label"
data=$(mktemp -d); cp "$ROOT/seed/feed.db" "$data/feed.db"
cg="/sys/fs/cgroup/system.slice/$unit.service"

stop() { sudo systemctl stop "$unit" 2>/dev/null || true; sudo systemctl reset-failed "$unit" 2>/dev/null || true; rm -rf "$data"; }
trap stop EXIT
sudo systemd-run --quiet --unit="$unit" --uid="$(id -u)" --gid="$(id -g)" --working-directory="$dir" \
  -p LimitNOFILE=65535 -p MemoryMax=2G -p MemorySwapMax=0 -p AllowedCPUs=0 -p NetworkNamespacePath=/run/netns/box \
  --setenv=JWT_SECRET=twelve-dollar-challenge --setenv=HOST=0.0.0.0 --setenv=PORT=3000 --setenv=SQLITE_PATH="$data/feed.db" \
  --setenv=HOME="$HOME" --setenv=PATH="$PATH" "${extra[@]}" bash start.sh
for _ in $(seq 120); do curl -sf -m 1 http://10.9.0.1:3000/health >/dev/null && break; sleep 0.5; done
curl -sf -m 1 http://10.9.0.1:3000/health >/dev/null || { echo "$label: no /health after 60 s"; sudo journalctl -u "$unit" -n 30 --no-pager; exit 1; }

usage() { awk '/^usage_usec/ {print $2}' "$cg/cpu.stat"; }
# CPU 0's busy time in USER_HZ ticks: user, nice, system, irq, softirq
busy() { awk '$1 == "cpu0" {print $2 + $3 + $4 + $7 + $8}' /proc/stat; }
hz=$(getconf CLK_TCK)
cpus=$(nproc)
sudo bash -c "ulimit -n 1000000; export DOTNET_ROOT=/usr/local/dotnet; exec taskset -c 1-$((cpus - 1)) '$LG' steps --url http://10.9.0.1:3000 --tokens '$ROOT/seed/tokens.json' \
  --levels $levels --rate $rate --settle $settle --hold $hold --sources 10.9.1.1/32" 2>"$OUT/$label.lg.err" |
while IFS= read -r line; do
  echo "$line" >> "$OUT/$label.lg.txt"
  case "$line" in
    HOLD*) u0=$(usage); b0=$(busy); t0=$(date +%s.%N)
      # PROFILE=1: where CPU 0's time goes for 15 s of the hold, kernel and entry together
      if [ "${PROFILE:-}" = 1 ] && [ ! -e "$OUT/$label.perf.txt" ]; then
        : > "$OUT/$label.perf.txt"
        (sleep 10; pd=/tmp/$label.perf.data
         sudo perf record -q -C 0 -F 1999 -g -o "$pd" -- sleep 15 > /dev/null 2>&1
         sudo perf report -i "$pd" --no-children --sort dso,symbol --stdio -g none 2>/dev/null | awk '/^ +[0-9.]+%/' | sed -n 1,70p > "$OUT/$label.perf.txt"
         sudo perf report -i "$pd" --no-children --sort dso --stdio -g none 2>/dev/null | awk '/^ +[0-9.]+%/' | sed -n 1,12p > "$OUT/$label.perf-dso.txt"
         sudo perf report -i "$pd" --no-children --sort symbol --stdio -g caller,0.5,callee --percent-limit 4 2>/dev/null | awk 'NF' | sed -n 1,220p > "$OUT/$label.perf-callers.txt"
         sudo rm -f "$pd"; touch "$OUT/$label.perf.done") &
      fi ;;
    all*) all=$line ;;
    DONE*)
      u1=$(usage); b1=$(busy); t1=$(date +%s.%N); peak=$(cat "$cg/memory.peak")
      users=$(awk '{print $2}' <<<"$line")
      awk -v label="$label" -v users="$users" -v u=$((u1 - u0)) -v b=$(( (b1 - b0) * 1000000 / hz )) -v s="$(awk -v a="$t0" -v b="$t1" 'BEGIN{print b-a}')" -v peak="$peak" '
        { gsub(",", "", $2); gsub(",", "", $4)
          for (i = 1; i <= NF; i++) { if ($i == "p50") p50 = $(i+1); if ($i == "p95") p95 = $(i+1); if ($i == "p99") p99 = $(i+1); if ($i == "max") mx = $(i+1); if ($i == "errors") e = $(i+2) }
          gsub("[(%)]", "", e); rps = $4 + 0
          printf "%s\t%d\t%d\t%.1f\t%.0f\t%.0f\t%.0f\t%s\t%.0f\t%.1f\t%.0f\t%.1f\t%d\n", label, users, rps, p50, p95, p99, mx, e, u / s / 10000, (rps > 0 ? u / s / rps : 0), b / s / 10000, (rps > 0 ? b / s / rps : 0), peak / 1048576 }' <<<"$all" | tee -a "$OUT/load.tsv"
      ;;
  esac
done
if [ "${PROFILE:-}" = 1 ]; then for _ in $(seq 240); do [ -e "$OUT/$label.perf.done" ] && break; sleep 1; done; fi
sudo journalctl -u "$unit" --no-pager -o cat | tail -80 > "$OUT/$label.server.txt" || true
# What the load left in the database: the new posts and likes, and the three most liked posts. bench/load.js gives a
# new post about 7 likes; thousands on one post mean the users did not read the posts their feeds showed.
sudo systemctl stop "$unit" 2>/dev/null || true
sqlite3 "$data/feed.db" "SELECT 'new posts', count(*) FROM posts WHERE id > 500000;
  SELECT 'new likes', count(*) - 2016005 FROM likes;
  SELECT 'post ' || post_id, count(*) || ' likes' FROM likes GROUP BY post_id ORDER BY count(*) DESC LIMIT 3;" > "$OUT/$label.data.txt" 2>&1 || true
