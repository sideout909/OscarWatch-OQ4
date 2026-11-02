# OQ4 acquisition improvements — pending 1.1.6-OQ4

The JO-97 capture recorded at 2026-11-02 02:32 UTC contains strong local TX
leakage and substantially weaker delayed copies. The operator's annotations let
us recover eight additional packets with frequency-guided offline acquisition;
the unchanged payload decoder then validated each from unfiltered raw audio.
The receiver should find those candidates without the operator highlighting them.

## Receiver changes

The original four-candidate pass still runs first, with its existing scoring,
duplicate rules and CRC/FEC acceptance. Its successful results are retained.
A bounded additional pass then:

1. Anchors candidate timing to decoded reference pairs, or the strongest original
   pair if none decoded. This is signal-derived timing, not a callsign/message
   hint or a hardcoded capture time. Up to two anchors are used.
2. Looks within 120 ms of those anchors for alternate closing-reference peaks.
   A strong local closing tone must not monopolize the weak opening tone's pairing.
   The fallback penalizes badly unbalanced pairs and reserves candidate space for
   independent frequency regions.
3. Extends reference-pair separation from the original approximately ±205 Hz to
   approximately ±351 Hz. This addresses the observed 212–267 Hz near-TX sweeps,
   including uplink audio precompensation. It is not a measured satellite-only
   drift limit or a change to the waveform's instantaneous bandwidth.
4. Ranks at most 96 candidate pairs against the fixed preamble. Four 100 ms
   coherent sums estimate a residual frequency offset within ±5 Hz. The score
   prioritizes hypotheses; it does not accept a decoded message.
5. Tries at most four additional distinct candidates through the existing payload
   decoder. Candidates close to an already decoded carrier are skipped. For a
   sufficiently coherent preamble, an unsuccessful candidate also gets bounded
   closing-reference offsets of -3 and +3 Hz. Thus there are at most twelve
   fallback payload attempts, not an unlimited deep search.

No payload bits, expected messages or callsign hints enter this acquisition path.
Preamble ranking uses the fixed preamble, not the variable message. CRC, LDPC,
OSD, phase-feedback acceptance, packet format and transmit timing are unchanged.
The lower acquisition boundary remains 200 Hz; extending it is separate work.

## Validation

| Check | Previous receiver | Updated receiver |
|---|---:|---:|
| Latest capture, raw copies in 38 clips | 37 | 57 |
| Copies close to logged TX carrier | 29 | 38 |
| Separate CRC-valid copies | 8 | 19 |
| Previously confirmed separate copies recovered blindly | 8 of 16 | 16 of 16 |
| Independent-waveform synthetic corpus | 92/144 correct | 98/144 correct |
| Previously successful synthetic cases lost | — | 0 |
| Incorrect synthetic payloads | 0 | 0 |
| Noise windows yielding a decode | 0/160 | 0/160 |
| Older RS-44 / JO-97 expected signals | 26 | 26 |

The 54 checked recording expectations cover all 38 near-TX copies and the 16
previously established separate copies. The three additional separate outputs
are CRC-valid copies of the actual CQ, but their propagation paths need further
review. In particular, the extra approximately 1869 Hz copy alongside approximately
2363 Hz and 2857 Hz signals is compatible in frequency with a mixing product;
it must not automatically be counted as an independent satellite return.
Two other additional outputs fall in previously unconfirmed annotated regions.

These are receiver regression results, not an FT4 sensitivity comparison or a
satellite QSO count. Strong local-copy recovery tests acquisition; it does not
establish weak downlink sensitivity. The synthetic gains span stationary and
fast-drift cases and are not a universal sensitivity threshold. Noise tests do
not establish a rare false-alarm rate for real-world interference.

576 FT4/OQ4/update-related tests passed in Release configuration. New synthetic
coverage includes both signs of 113 Hz/s drift, a stronger misleading closing
reference next to another packet, and rejection of reference tones without data.
The app was not launched and no audio device/PTT/RF operation was performed.

## Replay the local recording

Audio remains local and ignored by Git. The checked manifest records the original
WAV checksum and the exact 38 clip offsets. Extraction requires FFmpeg and uses
an anti-aliased 12 kHz conversion; prepared clips were verified byte-for-byte
against the reviewed raw input.

```bash
python3 tools/Oq4Replay/prepare_manifest.py \
  "$HOME/Documents/RS44-OQ4-20261102-023206-UTC.wav" \
  tools/Oq4Replay/jo97-20261102-0232.manifest.json \
  --output testdata/oq4/jo97-20261102-0232

dotnet run --project tools/Oq4Replay -- \
  --verify tools/Oq4Replay/jo97-20261102-0232.expected.json \
  testdata/oq4/jo97-20261102-0232/*.f32
```

The WAV filename retains RS44; the operator identifies this pass as JO-97.
Expectations only score output; they are never supplied to the receiver as hints.
The replay tool's `candidates` field lists the initial search; its `results`
include both the original pass and the bounded fallback.

## Timing and SNR of the eight annotation-guided recoveries

The updated blind receiver recovers all of these without annotation input.
The following estimates are from the earlier raw-audio guided review, retained
for comparison. SNR is approximate, data-only, referenced to 2500 Hz.

| UTC slot | Audio Hz | SNR dB | Delay after local copy ms |
|---|---:|---:|---:|
| 02:37:45 | 1827.6 | -11.7 | 16 |
| 02:38:07.5 | 2275.5 | -9.3 | 12 |
| 02:38:30 | 2363.3 | -3.6 | 12 |
| 02:38:45 | 2330.5 | -9.5 | 16 |
| 02:39:30 | 2117.2 | -11.9 | 16 |
| 02:39:37.5 | 2070.1 | -9.6 | 16 |
| 02:39:45 | 2025.8 | -14.1 | 16 |
| 02:40:07.5 | 1911.0 | -11.8 | 20 |

Repeated delayed copies and separate frequency tracks support satellite returns
rather than undelayed harmonic lines. These decoder timing estimates are not
calibrated time-of-flight measurements and cannot by themselves exclude radio
filter latency or every possible delayed/nonlinear signal path.

## Release scope

This is a receiver update to the still-unpublished 1.1.6-OQ4 build, alongside the
already committed control-window and fork-update changes. A new Publish workflow
run must use the new commit; the earlier build lacks these receiver improvements.
Raw recordings and operator annotations are not uploaded. Unrelated local Terminal
changes remain outside the release commit.
