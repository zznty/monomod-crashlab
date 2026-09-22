#!/bin/bash
# Summarize one or more capture-on-hang output directories:
#   - the hang marker and the supervised command's output
#   - the busiest native stack from `sample`
#   - managed frames that mention MonoMod's detour synchronization
#
# usage: summarize-capture.sh <capture-dir> [capture-dir...]

set -u

for d in "$@"; do
    echo "########## $d"
    if [ -f "$d/HANG" ]; then
        cat "$d/HANG"
    else
        echo "(no HANG marker - run did not hang)"
    fi

    echo "--- command.log (first 15 lines) ---"
    head -15 "$d/command.log" 2>/dev/null

    for s in "$d"/sample.*.txt; do
        [ -f "$s" ] || continue
        echo "--- $s: process line + busiest stacks ---"
        grep -m1 'Code Type:' "$s"
        awk '/^Call graph:/{f=1} f{print; n++} n>30{exit}' "$s"
    done

    for m in "$d"/managed.*.txt; do
        [ -f "$m" ] || continue
        echo "--- $m: managed totals ---"
        grep -c '^Thread (' "$m" | sed 's/^/threads: /'
        echo "--- $m: MonoMod detour sync frames ---"
        grep -nE 'DetourManager|WaitForNoActiveCalls|DetermineThreadCallDepth|SyncProxy|OnMethodCompiled|UpdateChain|CreateSimpleDetour|PlatformTriple' "$m" | head -20
    done
done
