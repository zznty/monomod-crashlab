#!/bin/bash
# Supervise a command. If it outlives its budget, treat it as a hang and capture whatever
# can still be read from the live process tree:
#   - ps snapshot of the tree
#   - native `sample` stacks (works under Rosetta, unlike createdump)
#   - managed stacks via dotnet-stack (EventPipe; no dump needed)
#   - the runtime's own createdump attempt (SIGABRT + DOTNET_DbgEnableMiniDump), if enabled
# then kill the tree.
#
# Every capture command is bounded: macOS ships no timeout(1), and a wedged sampler must not
# be able to hang the capture itself.
#
# usage: capture-on-hang.sh <budget-seconds> <outdir> <command> [args...]
# exit:  the command's exit code, or 100 when a hang was captured

set -u
budget=$1
outdir=$2
shift 2
mkdir -p "$outdir"
log="$outdir/command.log"
: >"$log"

# run a command with a hard bound, killing it if it overruns
run_bounded() {
    local secs=$1
    shift
    "$@" &
    local cmdpid=$!
    ( sleep "$secs"; kill -9 "$cmdpid" 2>/dev/null ) &
    local watchdog=$!
    wait "$cmdpid" 2>/dev/null
    local rc=$?
    kill -9 "$watchdog" 2>/dev/null
    wait "$watchdog" 2>/dev/null
    return $rc
}

# append mode on the child too: a child fd opened with '>' keeps its own file offset, and any
# write it makes after our own appends would otherwise overwrite them (child offset < file size)
"$@" >>"$log" 2>&1 &
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

hang_msg="HANG: pid $root alive after ${budget}s -- capturing"
echo "$hang_msg" >>"$outdir/HANG"
echo "$hang_msg" >>"$log"
echo "capture-on-hang: capturing pid $root" >&2

# process tree: driver, its children (testhost), and their children
collect_tree() {
    local tree="$root"
    for p in $(pgrep -P "$root" 2>/dev/null); do tree="$tree $p"; done
    for p in $tree; do
        for c in $(pgrep -P "$p" 2>/dev/null); do tree="$tree $c"; done
    done
    echo $tree | tr ' ' '\n' | sort -u | tr '\n' ' '
}

pids=$(collect_tree)
echo "process tree: $pids" >>"$log"

for p in $pids; do
    {
        echo "===== pid $p ====="
        run_bounded 20 ps -o pid=,ppid=,stat=,etime=,rss=,command= -p "$p" 2>&1
    } >>"$log" 2>&1

    if command -v sample >/dev/null 2>&1; then
        echo "sampling pid $p (native, /usr/bin/sample)" >>"$log"
        run_bounded 45 sample "$p" 3 -file "$outdir/sample.$p.txt" >>"$log" 2>&1
    fi

    if command -v dotnet-stack >/dev/null 2>&1; then
        echo "sampling pid $p (managed, dotnet-stack)" >>"$log"
        run_bounded 60 dotnet-stack report --process-id "$p" >"$outdir/managed.$p.txt" 2>>"$log"
    fi
done

# let the runtime try to write its own dump, then grab whatever landed
for p in $pids; do kill -ABRT "$p" 2>/dev/null; done
sleep 15
if [ -d /tmp/dumps ]; then
    mkdir -p "$outdir/runtime-dumps"
    run_bounded 60 cp -R /tmp/dumps/. "$outdir/runtime-dumps/" 2>>"$log"
fi

# kill the tree, including anything spawned while we were sampling
for p in $(collect_tree); do kill -9 "$p" 2>/dev/null; done
wait "$root" 2>/dev/null

echo "capture-on-hang: hang captured, artifacts in $outdir" >&2
exit 100
