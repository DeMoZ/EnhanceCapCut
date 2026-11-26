#!/bin/bash
# Golden parity runner for the Rust core (openspec/changes/archive/2026-11-26-rust-core, design decision 11). Both apps
# run on the core since stages 3 (macOS) and 4 (Windows), and both CLIs print the core's `ecc info`; this checks that
# what they print is byte for byte what the core prints (`ecc-golden`), on each OS it runs on:
#
#   scripts/golden.sh fixtures       every fixture in core/fixtures, written once as draft_info.json and once as
#                                    draft_content.json: `info` of every timeline must be identical in every CLI, and
#                                    each CLI's `info` of all timelines must equal the committed expected/info.txt
#                                    (so a run on Windows compares with what macOS and Linux print)
#   scripts/golden.sh local [root]   every project and timeline of a CapCut library (default: CapCut's drafts folder),
#                                    one timeline at a time: `info`; file names are compared after Unicode NFC
#                                    normalization and project folders through their real path
#                                    (core/fixtures/known-differences.md)
#
# The CLIs are built first (release): the macOS one (Swift; skipped elsewhere and with SKIP_SWIFT=1) and the Windows one
# (C#, any OS; SKIP_CSHARP=1 leaves it out). The core's dumps (`ecc-golden dump-timeline|dump-writers`) are checked
# against expected/ by `cargo test -p ecc-golden`. Differences are saved under build/golden/; the exit status is 1 when
# there are any.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd -P)
MODE=${1:-}
OUT="$REPO/build/golden"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "Building the CLIs…"
(cd core && cargo build --quiet --release -p ecc-golden)
RUST="${CARGO_TARGET_DIR:-$REPO/core/target}/release/ecc-golden"
CLIS=("rust:$RUST")
if [ "${SKIP_SWIFT:-0}" != 1 ] && [ "$(uname)" = Darwin ]; then
    scripts/build-core.sh >/dev/null
    swift build --quiet -c release --product ecc
    CLIS+=("swift:$(swift build -c release --show-bin-path)/ecc")
fi
if [ "${SKIP_CSHARP:-0}" != 1 ]; then
    dotnet build windows/src/ecc -c Release --nologo -v quiet -p:TargetFramework=net10.0 -o "$OUT/csharp-cli" >/dev/null
    CLIS+=("csharp:$OUT/csharp-cli/ecc")
fi

# compare <fixture> <label> <project> <timeline number> <normalize 0|1>: runs `info` in every CLI, diffs each output
# with the Rust core's and appends it to $OUT/<cli>.all; a difference listed as accepted in known-differences.md
# (`accepted: <cli> <fixture> info`) counts as known.
KNOWN_FILE="$REPO/core/fixtures/known-differences.md"
DIFFERENT=0
KNOWN=0
SAME=0
compare() {
    local fixture=$1 label=$2 project=$3 timeline=$4 normalize=$5 command=info
    local real
    real=$(cd "$project" && pwd -P)
    for cli in "${CLIS[@]}"; do
        local name=${cli%%:*} bin=${cli#*:}
        "$bin" "$command" "$project" --timeline "$timeline" > "$OUT/$name.out" 2>&1 || true
        if [ "$normalize" = 1 ]; then
            python3 -I -c '
import sys, unicodedata
path, real, project = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(path, encoding="utf-8", errors="replace").read()
for folder in (real, project):
    text = text.replace(folder + "/", "<project>/")
open(path, "w", encoding="utf-8").write(unicodedata.normalize("NFC", text))
' "$OUT/$name.out" "$real" "$project"
        fi
    done
    for cli in "${CLIS[@]}"; do
        cat "$OUT/${cli%%:*}.out" >> "$OUT/${cli%%:*}.all"
    done
    for cli in "${CLIS[@]:1}"; do
        local name=${cli%%:*}
        if cmp -s "$OUT/rust.out" "$OUT/$name.out"; then
            SAME=$((SAME + 1))
        elif grep -qxF "accepted: $name $fixture $command" "$KNOWN_FILE"; then
            KNOWN=$((KNOWN + 1))
        else
            DIFFERENT=$((DIFFERENT + 1))
            local file="$OUT/diff/$label.$command.$name.diff"
            mkdir -p "$OUT/diff"
            diff "$OUT/$name.out" "$OUT/rust.out" > "$file" || true
            echo "  differs: $label $command ($name vs rust) → ${file#$REPO/}"
        fi
    done
}

timeline_count() {
    "$RUST" timelines "$1" 2>/dev/null | wc -l | tr -d ' '
}

case "$MODE" in
fixtures)
    PROJECTS="$OUT/projects"
    mkdir -p "$PROJECTS"
    PROJECTS=$(cd "$PROJECTS" && pwd -P)
    fixtures=0
    for fixture in core/fixtures/*/; do
        [ -f "$fixture/draft.fixture.json" ] || continue
        fixtures=$((fixtures + 1))
        for flag in "" --content; do
            project=$("$RUST" materialize "$fixture" "$PROJECTS" $flag)
            label="$(basename "$fixture")${flag:+-content}"
            rm -f "$OUT"/*.all
            for i in $(seq 1 "$(timeline_count "$project")"); do
                compare "$(basename "$fixture")" "$label-$i" "$project" "$i" 0
            done
            # Every CLI's report of all timelines against the committed one (the core's, written on any OS).
            for cli in "${CLIS[@]}"; do
                cli_name=${cli%%:*}
                if cmp -s "$fixture/expected/info.txt" "$OUT/$cli_name.all"; then
                    SAME=$((SAME + 1))
                else
                    DIFFERENT=$((DIFFERENT + 1))
                    file="$OUT/diff/$label.expected.$cli_name.diff"
                    mkdir -p "$OUT/diff"
                    diff "$OUT/$cli_name.all" "$fixture/expected/info.txt" > "$file" || true
                    echo "  differs: $label info ($cli_name vs expected/info.txt) → ${file#$REPO/}"
                fi
            done
        done
    done
    echo "$fixtures fixtures × 2 draft names: $SAME identical, $KNOWN known differences, $DIFFERENT different" \
        "(${CLIS[*]%%:*})"
    ;;
local)
    ROOT=${2:-"$HOME/Movies/CapCut/User Data/Projects/com.lveditor.draft"}
    projects=0
    timelines=0
    for dir in "$ROOT"/*/; do
        dir=${dir%/}
        [ -f "$dir/draft_info.json" ] || [ -f "$dir/draft_content.json" ] || continue
        projects=$((projects + 1))
        for i in $(seq 1 "$(timeline_count "$dir")"); do
            timelines=$((timelines + 1))
            compare - "$(basename "$dir")-$i" "$dir" "$i" 1
        done
    done
    echo "$projects projects, $timelines timelines: $SAME identical, $KNOWN known differences, $DIFFERENT different" \
        "(${CLIS[*]%%:*})"
    ;;
*)
    sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
rm -f "$OUT"/*.out "$OUT"/*.all
[ "$DIFFERENT" = 0 ]
