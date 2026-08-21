#!/usr/bin/env bash
#
# Renders every model through this port AND through Braids' own C++, and reports
# the largest disagreement. This is what says the port is a port.
#
#   ./tools/compare-reference.sh
#
# Needs gcc, git and the network the first time, to fetch Braids and stmlib into
# a scratch directory. Set BESIEGE_DIR if the install is not auto-detected.
#
# Two patches are applied to Braids' source, both of which remove a dependence on
# uninitialised memory rather than changing what it computes:
#
#   * AnalogOscillator::previous_shape_ is never initialised, so whether the first
#     Render calls Init() -- which resets pitch_ to note 60 -- depends on stack
#     garbage. The harness gives it a value no shape has, which is what this port
#     does with its own -1.
#   * The comb's delay line is likewise uninitialised, so the harness holds the
#     oscillator in static storage, which is zeroed. A module powers up with
#     cleared RAM; the port clears it explicitly.
#
# The harness also swaps in the closed forms this port computes for `wav_sine` and
# the fifteen comb tables. Braids generates those two offline through a
# second-order dither, which the closed form does not reproduce and which puts a
# noise floor 2 counts wide under a comparison. Everything else -- the
# waveshapers, the filter table, the pitch tables -- matches Braids' shipped
# arrays exactly and is left alone.
#
# All sixteen models then agree sample for sample.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/besiege-braids-synth-reference"
SRC_DIR="$REPO_DIR/BraidsSynth/BraidsSynthScripts"

BLOCKS=40
SIZE=24                 # Braids' own block size; its scratch buffers are 24 long
                        # and rendering more than that walks off the end of them.
RATE=96000              # and its own rate, which is what its tables are built for

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

BUILD_DIR="${TMPDIR:-/tmp}/besiege-braids-synth-build"
mkdir -p "$BUILD_DIR" "$WORK"
for tool in besiegecc monohost; do
    if [[ ! -x "$BUILD_DIR/$tool" || "$REPO_DIR/tools/$tool.c" -nt "$BUILD_DIR/$tool" ]]; then
        gcc -O1 -o "$BUILD_DIR/$tool" "$REPO_DIR/tools/$tool.c" -ldl
    fi
done

# ---- Braids' own source, patched for determinism -----------------------------

if [[ ! -d "$WORK/eurorack" ]]; then
    echo "Fetching Braids..."
    git clone -q --depth 1 https://github.com/pichenettes/eurorack.git "$WORK/eurorack"
fi
if [[ ! -d "$WORK/stmlib" ]]; then
    echo "Fetching stmlib..."
    git clone -q --depth 1 https://github.com/pichenettes/stmlib.git "$WORK/stmlib"
fi

rm -rf "$WORK/build"
mkdir -p "$WORK/build"
cp -r "$WORK/eurorack/braids" "$WORK/build/braids"
cp -r "$WORK/stmlib" "$WORK/build/stmlib"

python3 - "$WORK/build" <<'PY'
import re, sys, os
root = sys.argv[1]

p = os.path.join(root, 'braids/analog_oscillator.h')
s = open(p).read()
s = s.replace("  AnalogOscillator() { }",
              "  AnalogOscillator() { previous_shape_ = static_cast<AnalogOscillatorShape>(255); }")
open(p, 'w').write(s)

# The two dithered tables have to be writable for the harness to replace them.
p = os.path.join(root, 'braids/resources.cc')
s = open(p).read()
s = re.sub(r'^const int16_t (wav_sine|wav_bandlimited_comb_\d+)\[\]',
           r'int16_t \1[]', s, flags=re.M)
open(p, 'w').write(s)

p = os.path.join(root, 'braids/resources.h')
s = open(p).read()
s = re.sub(r'^extern const int16_t (wav_sine|wav_bandlimited_comb_\d+)\[\];',
           r'extern int16_t \1[];', s, flags=re.M)
open(p, 'w').write(s)
PY

cat > "$WORK/build/harness.cc" <<'CC'
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include "stmlib/utils/random.h"
#include "braids/macro_oscillator.h"

namespace stmlib { uint32_t Random::rng_state_ = 1; }

// resources.py's `scale`, over a centred curve.
static void Scale(double* v, int n, int16_t* out) {
  double sum = 0.0;
  for (int i = 0; i < n; i++) sum += v[i];
  double mean = sum / n, peak = 0.0;
  for (int i = 0; i < n; i++) {
    v[i] -= mean;
    double a = fabs(v[i]);
    if (a > peak) peak = a;
  }
  for (int i = 0; i < n; i++) out[i] = (int16_t)floor(v[i] / peak * 32766.0 + 0.5);
}

// The port's closed forms for the two tables Braids dithers offline.
static void UseClosedFormTables(double sr) {
  double v[257];
  for (int i = 0; i <= 256; i++) v[i] = -cos(2.0 * M_PI * i / 256.0);
  Scale(v, 257, braids::wav_sine);

  int16_t* combs[15] = {
    braids::wav_bandlimited_comb_0,  braids::wav_bandlimited_comb_1,
    braids::wav_bandlimited_comb_2,  braids::wav_bandlimited_comb_3,
    braids::wav_bandlimited_comb_4,  braids::wav_bandlimited_comb_5,
    braids::wav_bandlimited_comb_6,  braids::wav_bandlimited_comb_7,
    braids::wav_bandlimited_comb_8,  braids::wav_bandlimited_comb_9,
    braids::wav_bandlimited_comb_10, braids::wav_bandlimited_comb_11,
    braids::wav_bandlimited_comb_12, braids::wav_bandlimited_comb_13,
    braids::wav_bandlimited_comb_14 };

  for (int zone = 0; zone < 15; zone++) {
    double f0 = 440.0 * pow(2.0, (18 + 8 * zone - 69) / 12.0);
    double nyquist = sr / 2.0;
    f0 = (zone == 14) ? nyquist - 1.0 : (f0 < nyquist ? f0 : nyquist);
    double m = 2.0 * floor(sr / f0 / 2.0) + 1.0;
    double pulse[256];
    for (int j = 0; j < 256; j++) {
      double t = (j - 128) / 256.0;
      pulse[j] = (j == 128) ? 1.0 : sin(M_PI * t * m) / (m * sin(M_PI * t) + 1e-9);
    }
    for (int i = 0; i <= 256; i++) v[i] = pulse[(i + 64) % 256];
    Scale(v, 257, combs[zone]);
  }
}

int main(int argc, char** argv) {
  UseClosedFormTables(atof(argv[7]));
  // Static, so the comb's delay line starts cleared rather than on stack garbage.
  static braids::MacroOscillator osc;
  osc.Init();
  osc.set_shape(static_cast<braids::MacroOscillatorShape>(atoi(argv[1])));
  osc.set_pitch(atoi(argv[2]));
  osc.set_parameters(atoi(argv[3]), atoi(argv[4]));
  int blocks = atoi(argv[5]), size = atoi(argv[6]);
  int16_t* buffer = new int16_t[size];
  uint8_t* sync = new uint8_t[size];
  memset(sync, 0, size);
  for (int b = 0; b < blocks; b++) {
    osc.Render(sync, buffer, size);
    for (int i = 0; i < size; i++) printf("%d\n", buffer[i]);
  }
  return 0;
}
CC

echo "Building Braids' C++..."
g++ -O0 -w -I"$WORK/build" -o "$WORK/reference" "$WORK/build/harness.cc" \
    "$WORK/build/braids/macro_oscillator.cc" \
    "$WORK/build/braids/analog_oscillator.cc" \
    "$WORK/build/braids/digital_oscillator.cc" \
    "$WORK/build/braids/resources.cc" 2>&1 | grep -v 'warning' || true

# ---- this port ---------------------------------------------------------------

cat > "$WORK/Port.cs" <<'CS'
using System;
using BraidsSynth;

/// <summary>Renders one setting through the port, for tools/compare-reference.sh.</summary>
public class Port
{
    public static void Main(string[] args)
    {
        int rate = int.Parse(args[6]);
        BraidsResources.Prepare(rate);
        MacroOscillator osc = new MacroOscillator(rate);
        osc.SetModel(int.Parse(args[0]));
        osc.SetPitch((short)int.Parse(args[1]));
        osc.SetTimbre((short)int.Parse(args[2]));
        osc.SetColour((short)int.Parse(args[3]));

        int blocks = int.Parse(args[4]);
        int size = int.Parse(args[5]);
        short[] buffer = new short[size];
        System.Text.StringBuilder text = new System.Text.StringBuilder();
        for (int b = 0; b < blocks; b++)
        {
            osc.Render(null, buffer, size);
            for (int i = 0; i < size; i++)
            {
                text.Append(buffer[i]);
                text.Append('\n');
            }
        }
        Console.Write(text.ToString());
    }
}
CS

echo "Building the port..."
"$BUILD_DIR/besiegecc" -target:exe -out:"$WORK/port.exe" -lib:"$MANAGED" -r:System.dll \
    "$WORK/Port.cs" \
    "$SRC_DIR/BraidsResources.cs" "$SRC_DIR/AnalogOscillator.cs" \
    "$SRC_DIR/DigitalOscillator.cs" "$SRC_DIR/MacroOscillator.cs" >/dev/null

# ---- compare -----------------------------------------------------------------

NAMES=(CSAW MORPH "SAW SQUARE" "SINE TRIANGLE" BUZZ "SQUARE SUB" "SAW SUB"
       "SQUARE SYNC" "SAW SYNC" "TRIPLE SAW" "TRIPLE SQUARE" "TRIPLE TRIANGLE"
       "TRIPLE SINE" "TRIPLE RING MOD" "SAW SWARM" "SAW COMB")

echo
worst_overall=0
for model in $(seq 0 15); do
    worst=0
    where=""
    for pitch in 3072 7680 11520 14080; do
        for timbre in 0 1 8000 16384 32767; do
            for colour in 0 16384 32767; do
                "$WORK/reference" "$model" "$pitch" "$timbre" "$colour" \
                    "$BLOCKS" "$SIZE" "$RATE" > "$WORK/a.txt"
                TARGET_ASM="$WORK/port.exe" "$BUILD_DIR/monohost" \
                    "$model" "$pitch" "$timbre" "$colour" "$BLOCKS" "$SIZE" "$RATE" \
                    > "$WORK/b.txt"
                e=$(paste "$WORK/a.txt" "$WORK/b.txt" |
                    awk '{d=$1-$2; if(d<0)d=-d; if(d>m)m=d} END{print m+0}')
                if [[ "$e" -gt "$worst" ]]; then
                    worst=$e
                    where="pitch=$pitch timbre=$timbre color=$colour"
                fi
            done
        done
    done
    [[ "$worst" -gt "$worst_overall" ]] && worst_overall=$worst
    printf '  %-16s %s\n' "${NAMES[$model]}" \
        "$([[ $worst -eq 0 ]] && echo 'identical' || echo "differs by $worst at $where")"
done

echo
if [[ "$worst_overall" -eq 0 ]]; then
    echo "Every model matches Braids sample for sample."
else
    echo "Largest disagreement: $worst_overall counts." >&2
    exit 1
fi
