#!/bin/bash
# Golden parity runner for the Rust core (openspec/changes/rust-core, design decision 11). It compares what the Swift
# CLI, the Windows (C#) CLI and the Rust core print for the same projects:
#
#   scripts/golden.sh fixtures       every fixture in core/fixtures, written once as draft_info.json and once as
#                                    draft_content.json: `info`, `dump-timeline` and `dump-writers` of every timeline
#                                    must be byte-identical in all three
#   scripts/golden.sh local [root]   every project and timeline of a CapCut library (default: CapCut's drafts folder),
#                                    one timeline at a time: `info` and `dump-timeline`; file names are compared after
#                                    Unicode NFC normalization and project folders through their real path, the two
#                                    accepted differences of core/fixtures/known-differences.md
#
# The three CLIs are built first (release). SKIP_SWIFT=1 or SKIP_CSHARP=1 leaves one out (the Rust core is always
# compared). Differences are saved under build/golden/; the exit status is 1 when there are any.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd -P)
MODE=${1:-}
OUT="$REPO/build/golden"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "Building the CLIs…"
(cd core && cargo build --quiet --release -p ecc-golden)
RUST="$REPO/core/target/release/ecc-golden"
CLIS=("rust:$RUST")
if [ "${SKIP_SWIFT:-0}" != 1 ]; then
    swift build --quiet -c release --product ecc
    CLIS+=("swift:$(swift build -c release --show-bin-path)/ecc")
fi
if [ "${SKIP_CSHARP:-0}" != 1 ]; then
    dotnet build windows/src/ecc -c Release --nologo -v quiet -p:TargetFramework=net10.0 -o "$OUT/csharp-cli" >/dev/null
    CLIS+=("csharp:$OUT/csharp-cli/ecc")
fi

# compare <fixture> <label> <project> <command> <timeline number> <normalize 0|1>: runs the command in every CLI and
# diffs each output with the Rust core's; a difference listed as accepted in known-differences.md counts as known.
KNOWN_FILE="$REPO/core/fixtures/known-differences.md"
DIFFERENT=0
KNOWN=0
SAME=0
compare() {
    local fixture=$1 label=$2 project=$3 command=$4 timeline=$5 normalize=$6
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
            for i in $(seq 1 "$(timeline_count "$project")"); do
                for command in info dump-timeline dump-writers; do
                    compare "$(basename "$fixture")" "$label-$i" "$project" "$command" "$i" 0
                done
            done
        done
    done
    echo "$fixtures fixtures × 2 draft names: $SAME identical, $KNOWN known differences, $DIFFERENT different (${#CLIS[@]} CLIs)"
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
            for command in info dump-timeline; do
                compare - "$(basename "$dir")-$i" "$dir" "$command" "$i" 1
            done
        done
    done
    echo "$projects projects, $timelines timelines: $SAME identical, $KNOWN known differences, $DIFFERENT different (${#CLIS[@]} CLIs)"
    ;;
*)
    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
rm -f "$OUT"/*.out
[ "$DIFFERENT" = 0 ]
