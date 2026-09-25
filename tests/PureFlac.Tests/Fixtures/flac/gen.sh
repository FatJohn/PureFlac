#!/usr/bin/env bash
# Generates the FLAC fixtures for the PureFlac tests plus libFLAC reference values.
#
# All material is synthetic: ffmpeg aevalsrc sine waves (a different frequency per channel) plus white noise
# (aevalsrc random(), fixed seed), with 0.08-0.20 s of digital silence (so the encoder uses CONSTANT subframes);
# no real music. Each file is <= 0.3 s long.
# Encoders: the flac 1.5.0 CLI (main), ffmpeg's built-in flac encoder (to force a stereo decorrelation mode), and
# the minimal encoder in flacgen.py (for paths that neither the flac CLI nor ffmpeg can produce).
#
# Outputs (all committed):
#   *.flac          fixtures
#   expected.tsv    file name, bps, channels, sample rate, total samples, STREAMINFO MD5, SHA-256 of the PCM
#                   decoded by libFLAC (PCM packing = the FLAC MD5 definition: ceil(bps/8) bytes per sample,
#                   little-endian, signed, right-aligned, interleaved)
#   coverage.tsv    the encoding paths actually used in each file, counted by `flac -a` (libFLAC analysis mode);
#                   this file is the authority for the coverage matrix
#
# Where the SHA-256 references come from:
#   bps 8/16/24/32: the raw output of `flac -d --force-raw-format --endian=little --sign=signed`; the WAV path
#   described next is also run, and the two must match.
#   bps 4/12/20: flac 1.5 raw output only supports 8/16/24/32 (observed: "bits per sample is 12, must be
#   8/16/24/32 for raw format output"), so `flac -d` writes WAV and `flacgen.py pack` shifts the left-aligned WAV
#   samples back to the original bit depth. For every file the MD5 of this packing is also checked against the
#   STREAMINFO MD5, to make sure the packing definition agrees.
#   The files from flacgen.py synth: additionally, the PCM decoded by libFLAC must be byte-identical to the
#   raw PCM written by the generator.
#
# Coverage matrix (coverage.tsv is the authority for the paths actually taken):
#   bps: 4 (not Subset, needs --lax), 8, 12, 16, 20, 24, 32 (the side channel of 32-bit stereo is a 33-bit subframe)
#   channels: 1, 2, 3, 6, 8; sample rates: 8k, 11k (header code 12), 12345 (code 13), 16k, 22.05k, 44.1k, 48k,
#   96k, 110.25k (code 14), 192k
#   block sizes: 16, 37, 100, 192, 256, 576, 1000, 1024, 1152, 2304, 4096, 4608, 8192, 16384 (including
#   non-standard sizes written explicitly as 8/16 bits in the header)
#   compression levels 0 and 8, `-l 32 -e --lax` (flac actually picks order 18; order 32 is covered by
#   ff_b24_c2_midside), `-r 9,12 --lax` (rice partition order 9, above the Subset limit of 8)
#   rice partition orders 10, 12 and 15 (synth_po10/po12/po15; po12 and po15 have one sample per partition, so
#   partition 0 of their order-1 predictor is empty)
#   subframes: CONSTANT, VERBATIM (forced with `--disable-constant-subframes --disable-fixed-subframes -l 0`),
#   FIXED (`-l 0`), LPC; wasted bits (low 8/3 bits of the samples cleared, plus verbatim + wasted)
#   residual: RICE, RICE2 (24/32-bit noise), escaped partitions (including width 0; both the RICE and RICE2 escape codes)
#   stereo: independent, left/side, right/side, mid/side (flac -m/-M choosing on its own, ffmpeg ch_mode forcing
#   each of the three, and synth)
#   metadata: PICTURE (noise PNG) + PADDING + SEEKTABLE + VORBIS_COMMENT
#   variable block size (blocking strategy 1, the header carries the sample number): synth_varblock
# Not covered (listed as is):
#   - Negative LPC shift: not allowed by RFC 9639 and never produced by encoders; the decoder treats it as a format error.
#   - Escaped partitions inside LPC subframes: only synth's FIXED subframes have them (neither the libFLAC nor
#     the ffmpeg encoder produces escapes).
#   - ID3v2 prefix: not a fixture; the tests prepend an ID3v2 tag to a fixture in memory.
#
# Usage: bash gen.sh (needs ffmpeg, flac and metaflac 1.5.0, and python3; override the paths with the
# FFMPEG, FLAC, METAFLAC and PY environment variables)
set -euo pipefail
cd "$(dirname "$0")"
FFMPEG=${FFMPEG:-ffmpeg}
FLAC=${FLAC:-flac}
METAFLAC=${METAFLAC:-metaflac}
PY=${PY:-python3}
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FREQS=(440 660 550 330 880 220 770 990)

rm -f ./*.flac expected.tsv coverage.tsv

# $1=channels -> aevalsrc exprs (a different frequency per channel, and a different random() seed slot)
exprs() {
  local ch=$1 e="" i
  for ((i = 0; i < ch; i++)); do
    [ -n "$e" ] && e="$e|"
    e="${e}if(between(t\,0.08\,0.20)\,0\,0.45*sin(2*PI*${FREQS[$i]}*t)+0.05*(2*random($i)-1))"
  done
  echo "$e"
}

# mk name channels rate seconds bps low_bits_to_clear -- flac arguments...
mk() {
  local name=$1 ch=$2 rate=$3 dur=$4 bps=$5 zero=$6
  shift 7
  "$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i "aevalsrc=exprs=$(exprs "$ch"):s=$rate:d=$dur" \
    -f s32le -c:a pcm_s32le "$WORK/$name.s32"
  "$PY" flacgen.py wav "$WORK/$name.s32" "$WORK/$name.wav" "$ch" "$rate" "$bps" "$zero"
  "$FLAC" -s -f --channel-map=none "$@" -o "$name.flac" "$WORK/$name.wav"
}

# mkff name channels rate seconds -- ffmpeg output arguments... (ffmpeg's built-in flac encoder)
mkff() {
  local name=$1 ch=$2 rate=$3 dur=$4
  shift 5
  "$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i "aevalsrc=exprs=$(exprs "$ch"):s=$rate:d=$dur" \
    -fflags +bitexact -flags:a +bitexact -c:a flac "$@" "$name.flac"
}

# ---- flac CLI
mk b16_c2_44k_l8        2 44100  0.3  16 0 -- -8
mk b16_c2_44k_l0        2 44100  0.3  16 0 -- -0
mk b16_c2_44k_indep     2 44100  0.3  16 0 -- -5 --no-mid-side
mk b16_c2_44k_adaptive  2 44100  0.3  16 0 -- -M -l 8 -b 4096
mk b4_c1_16k            1 16000  0.3  4  0 -- -5 --lax
mk b8_c1_8k             1 8000   0.3  8  0 -- -5 -b 1152
mk b12_c2_22k           2 22050  0.3  12 0 -- -5
mk b20_c2_48k           2 48000  0.3  20 0 -- -8
mk b24_c2_96k_b16384    2 96000  0.3  24 0 -- -8 -b 16384 --lax
mk b24_c2_192k          2 192000 0.15 24 0 -- -8
mk b32_c2_44k           2 44100  0.3  32 0 -- -8
mk b32_c1_192k          1 192000 0.1  32 0 -- -5
mk b16_c3_44k           3 44100  0.2  16 0 -- -5
mk b16_c6_48k           6 48000  0.2  16 0 -- -5
mk b24_c8_48k           8 48000  0.1  24 0 -- -5
mk b16_c2_44k_b192      2 44100  0.3  16 0 -- -5 -b 192
mk b16_c1_44k_b1000     1 44100  0.2  16 0 -- -5 -b 1000
mk b16_c1_44k_b4608     1 44100  0.3  16 0 -- -5 -b 4608
mk b16_c2_44k_lpc32     2 44100  0.3  16 0 -- -8 -l 32 -e --lax
mk b16_c1_48k_r12       1 48000  0.3  16 0 -- -l 0 -b 8192 -r 9,12 --lax
mk b16_c2_44k_nofixed   2 44100  0.3  16 0 -- -8 --disable-constant-subframes --disable-fixed-subframes
mk b16_c2_44k_verbatim  2 44100  0.3  16 0 -- -l 0 --disable-constant-subframes --disable-fixed-subframes
mk b16_c2_44k_fixed     2 44100  0.3  16 0 -- -l 0 -b 4096 -m
mk b24_c2_44k_wasted8   2 44100  0.3  24 8 -- -8
mk b16_c1_44k_wasted3   1 44100  0.3  16 3 -- -5
mk b16_c1_11k           1 11000  0.2  16 0 -- -5
mk b16_c1_12345         1 12345  0.2  16 0 -- -5
mk b16_c1_110k          1 110250 0.1  16 0 -- -5

# Large metadata: noise PNG cover (PICTURE) + PADDING + SEEKTABLE + VORBIS_COMMENT
"$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i "color=c=gray:s=128x128,noise=alls=100:allf=t" -frames:v 1 "$WORK/cover.png"
mk b16_c2_44k_bigmeta   2 44100  0.3  16 0 -- -5 --picture="3||cover||$WORK/cover.png" --padding=16384 -S 0.05s \
  -T "TITLE=synthetic tone" -T "ARTIST=gen.sh" -T "COMMENT=$(printf 'x%.0s' {1..2000})"

# ---- ffmpeg's built-in encoder: force the stereo decorrelation mode (the flac CLI's -m chooses per frame)
mkff ff_b16_c2_leftside  2 44100 0.3 -- -sample_fmt s16 -ch_mode left_side
mkff ff_b16_c2_rightside 2 44100 0.3 -- -sample_fmt s16 -ch_mode right_side
mkff ff_b16_c2_midside   2 44100 0.3 -- -sample_fmt s16 -ch_mode mid_side
mkff ff_b24_c2_midside   2 96000 0.2 -- -sample_fmt s32 -bits_per_raw_sample 24 -ch_mode mid_side \
  -lpc_type cholesky -max_prediction_order 32 -exact_rice_parameters 1 -max_partition_order 8

# ---- flacgen.py synth: escaped partitions, variable block size, every 33-bit side mode of 32-bit stereo,
#      rice partition orders 10/12/15 (the flac CLI accepts `-r` up to 15 but never picked more than 9 here)
for k in escape varblock side33 po10 po12 po15; do
  "$PY" flacgen.py synth "$k" "synth_$k.flac" "$WORK/synth_$k.raw"
  "$FLAC" -s -f -d --force-raw-format --endian=little --sign=signed -o "$WORK/synth_$k.dec" "synth_$k.flac"
  cmp "$WORK/synth_$k.raw" "$WORK/synth_$k.dec" || { echo "synth_$k: the PCM decoded by libFLAC differs from the generator output" >&2; exit 1; }
done

# ---- reference values
printf 'file\tbps\tchannels\trate\tsamples\tstreaminfo_md5\tpcm_sha256\n' > expected.tsv
printf 'file\tframes\tblocksizes\tassignments\tconstant\tverbatim\tfixed\tlpc\tmax_lpc_order\twasted\trice\trice2\tescape\tmax_po\n' > coverage.tsv
for f in *.flac; do
  "$FLAC" -s -t "$f"
  bps=$("$METAFLAC" --show-bps "$f"); ch=$("$METAFLAC" --show-channels "$f")
  rate=$("$METAFLAC" --show-sample-rate "$f"); n=$("$METAFLAC" --show-total-samples "$f")
  md5=$("$METAFLAC" --show-md5sum "$f")
  "$FLAC" -s -f -d -o "$WORK/dec.wav" "$f"
  read -r sha pmd5 _ < <("$PY" flacgen.py pack "$WORK/dec.wav")
  [ "$pmd5" = "$md5" ] || { echo "$f: packed MD5 $pmd5 != STREAMINFO $md5" >&2; exit 1; }
  case $bps in
    8|16|24|32)
      "$FLAC" -s -f -d --force-raw-format --endian=little --sign=signed -o "$WORK/dec.raw" "$f"
      rsha=$(shasum -a 256 "$WORK/dec.raw" | cut -d' ' -f1)
      [ "$rsha" = "$sha" ] || { echo "$f: raw SHA $rsha != packed WAV SHA $sha" >&2; exit 1; } ;;
  esac
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$bps" "$ch" "$rate" "$n" "$md5" "$sha" >> expected.tsv

  "$FLAC" -s -f -a -o "$WORK/a.ana" "$f"
  awk -v f="$f" -F'\t' '
    /^frame=/ { frames++; for (i = 1; i <= NF; i++) { if ($i ~ /^blocksize=/) bs[substr($i, 11)] = 1; if ($i ~ /^channel_assignment=/) ca[substr($i, 20)] = 1 } }
    /^\tsubframe=/ { for (i = 1; i <= NF; i++) {
        if ($i ~ /^type=/) { t = substr($i, 6); ty[t]++ }
        if ($i ~ /^order=/ && t == "LPC") { o = substr($i, 7) + 0; if (o > maxo) maxo = o }
        if ($i ~ /^wasted_bits=/ && substr($i, 13) + 0 > 0) wasted++
        if ($i ~ /^residual_type=/) rt[substr($i, 15)]++
        if ($i ~ /^partition_order=/) { p = substr($i, 17) + 0; if (p > maxpo) maxpo = p } } }
    /ESCAPE/ { esc++ }
    END {
      b = ""; n = 0; for (k in bs) { b = b (n++ ? "," : "") k }
      a = ""; n = 0; for (k in ca) { a = a (n++ ? "," : "") k }
      printf "%s\t%d\t%s\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%d\n", f, frames, b, a, ty["CONSTANT"], ty["VERBATIM"], ty["FIXED"], ty["LPC"], maxo, wasted, rt["RICE"], rt["RICE2"], esc, maxpo
    }' "$WORK/a.ana" >> coverage.tsv
done

echo "fixtures: $(ls ./*.flac | wc -l | tr -d ' ') files, $(cat ./*.flac | wc -c | tr -d ' ') bytes in total"
