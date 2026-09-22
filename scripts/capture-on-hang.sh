#!/bin/bash
# Supervise a command. If it outlives its budget, treat it as a hang and capture whatever
# can still be read from the live process tree:
#   - ps snapshot of the tree
#   - native `sample` stacks (works under Rosetta, unlike createdump)
#   - managed stacks via dotnet-stack (EventPipe; no dump needed)
#   - the runtime's own createdump attempt (SIGABRT + DOTNET_DbgEnableMiniDump), if enabled
# then kill the tree.
#
# usage: capture-on-hang.sh <budget-seconds> <outdir> <command> [args...]
# exit:  the command's exit code, or 100 when a hang was captured

set -u
budget=$1
outdir=$2
shift 2
mkdir -p "$outdir"
log="$outdir/command.log"

"$@" >"$log" 2>&1 &
root=$!

hang=0
deadline=$(( $(date +%s) + budget ))
while kill -0 "$root" 2>/dev/null; do
    if [ "$(date +%s)" -ge "$deadline" ]; then
        hang=1
        break
    fi
    sleep 2
done

if [ "$hang" -eq 0 ]; then
    wait "$root"
    rc=$?
    echo "capture-on-hang: finished on its own (rc=$rc)"
    exit $rc
fi

echo "HANG: pid $root alive after ${budget}s -- capturing" | tee "$outdir/HANG" >>"$log"

# process tree: driver, its children (testhost), and their children
pids="$root"
for p in $(pgrep -P "$root" 2>/dev/null); do pids="$pids $p"; done
for p in $pids; do
    for c in $(pgrep -P "$p" 2>/dev/null); do pids="$pids $c"; done
done
pids=$(echo $pids | tr ' ' '\n' | sort -u | tr '\n' ' ')

echo "process tree: $pids" >>"$log"

for p in $pids; do
    {
        echo "===== pid $p ====="
        ps -o pid=,ppid=,stat=,etime=,rss=,command= -p "$p"
    } >>"$log" 2>&1

    if command -v sample >/dev/null 2>&1; then
        sample "$p" 3 -file "$outdir/sample.$p.txt" >>"$log" 2>&1
    fi

    if command -v dotnet-stack >/dev/null 2>&1; then
        dotnet-stack report --process-id "$p" >"$outdir/managed.$p.txt" 2>>"$log"
    fi
done

# let the runtime try to write its own dump, then grab whatever landed
for p in $pids; do kill -ABRT "$p" 2>/dev/null; done
sleep 15
if [ -d /tmp/dumps ]; then
    mkdir -p "$outdir/runtime-dumps"
    cp -R /tmp/dumps/. "$outdir/runtime-dumps/" 2>/dev/null
fi

for p in $pids; do kill -9 "$p" 2>/dev/null; done
wait "$root" 2>/dev/null

echo "capture-on-hang: hang captured, artifacts in $outdir"
exit 100
