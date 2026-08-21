#!/usr/bin/env bash
#
# Runs the mod's tests, compiled and executed by Besiege's own Mono rather than
# an installed toolchain -- so what they exercise is the arithmetic the game
# will actually run, on the runtime it will run it on.
#
#   ./tools/run-tests.sh
#
# tools/tests/OscillatorCheck.cs renders every model and checks the result is a
# signal. tools/tests/BlacklistCheck.cs is not run from here; tools/build.sh
# runs it over every build.
#
# Set BESIEGE_DIR if the install is not auto-detected.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="$REPO_DIR/BraidsSynth/BraidsSynthScripts"
BUILD_DIR="${TMPDIR:-/tmp}/besiege-braids-synth-build"

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/Besiege"
        "$HOME/.local/share/Steam/steamapps/common/Besiege"
    )
    local vdf
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [[ -f "$vdf" ]] || continue
        while read -r lib; do candidates+=("$lib/steamapps/common/Besiege"); done \
            < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" | sed -E 's/.*"([^"]+)"$/\1/')
    done
    local dir
    for dir in "${candidates[@]}"; do
        [[ -f "$dir/Besiege_Data/Managed/mcs.dll" ]] && { echo "$dir"; return; }
    done
    return 1
}

if ! BESIEGE="$(find_besiege)"; then
    echo "Could not find Besiege. Set BESIEGE_DIR to your install directory." >&2
    exit 1
fi

DATA="$BESIEGE/Besiege_Data"
export LIBMONO="$DATA/Mono/x86_64/libmono.so"
export MANAGED="$DATA/Managed"
export MONOETC="$DATA/Mono/etc"

mkdir -p "$BUILD_DIR"
for tool in besiegecc monohost; do
    if [[ ! -x "$BUILD_DIR/$tool" || "$REPO_DIR/tools/$tool.c" -nt "$BUILD_DIR/$tool" ]]; then
        gcc -O1 -o "$BUILD_DIR/$tool" "$REPO_DIR/tools/$tool.c" -ldl
    fi
done

# The DSP is deliberately free of anything Unity: the oscillators, the tables and
# the model layer reference nothing but System, which is what lets them be run
# and checked here at all. Only the files that talk to the game are left out.
DSP=(
    "$SRC_DIR/BraidsResources.cs"
    "$SRC_DIR/AnalogOscillator.cs"
    "$SRC_DIR/DigitalOscillator.cs"
    "$SRC_DIR/MacroOscillator.cs"
    "$SRC_DIR/DcBlocker.cs"
)

OUT="$BUILD_DIR/oscillator-check.exe"
echo "Compiling the oscillator tests..."
"$BUILD_DIR/besiegecc" -target:exe -out:"$OUT" -lib:"$MANAGED" -r:System.dll \
    "$REPO_DIR/tools/tests/OscillatorCheck.cs" "${DSP[@]}"

echo "Running them..."
TARGET_ASM="$OUT" "$BUILD_DIR/monohost"
