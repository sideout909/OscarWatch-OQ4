# OQ4 SNR reports and code-assisted phase tracking

Implemented 2026-11-01. This changes the receiver only. The transmitted packet,
message packing, symbol rate, pilots, reference tones and slot timing are unchanged.

## SNR reporting

OQ4 receive rows and automatic/manual QSO reports use `Result.SnrDb`. Successful
BP, OSD and phase-feedback decodes now estimate signal-to-noise ratio from all
174 data components (87 QPSK symbols), using the CRC-validated message as the
reference. Previously the estimate used just the 32-symbol preamble. Text that
cannot be repacked, for example unresolved hashed callsigns, falls back to the
known-preamble estimate instead of discarding the decode.

For observed real components x, with reference signs s in {-1,+1}:

- Fit amplitude a = mean(x*s).
- Estimate residual variance v = sum((x-a*s)^2)/(N-1).
- Remove the amplitude fit's noise bias: signal = max(0, a*a-v/N).
- Report 10*log10((signal/v)*64/2500), bounded to -50 through +49 dB.

The reported quantity is **data SNR in a 2,500 Hz noise bandwidth**. OQ4's stronger
opening/closing reference tones are excluded. It consequently differs from the
Decoder Lab's active-whole-packet RMS SNR. Fading, timing error and residual phase
distortion contribute to the residual variance. Audio gain cancels from the ratio.
An approximate `~` remains in the window, with a tooltip describing the quantity.
This is not a calibrated measurement of the radio's antenna-input SNR.

Feedback decodes use their original, pre-feedback observations for the report.
The tracker therefore cannot produce an optimistic SNR merely by rotating symbols
to agree with its decoder feedback. Unresolved text and alternate message packing
remain reasons to treat the number as approximate.

Validation includes 500 seeded Gaussian trials at each of -5, -12 and -18 dB;
mean estimates are within 0.3 dB of the specified SNR. Changing amplitude by a
factor of 0.003 changes estimates by less than 0.001 dB. Three full-waveform tests
at 500, 1500 and 2300 Hz include modulation, noise, filtering, acquisition and CRC,
with a 2.5 dB per-packet tolerance around -10 dB data SNR. On the existing 144-case
corpus, the 92 successful packets report a median 0.90 dB below its packet-average
SNR; reference-tone power and residual distortion make this a different quantity,
not a universal correction to add to every report.

## Phase feedback

Ordinary BP decoding and bounded OSD run first. If both fail, the receiver tries
feedback on the existing top 12 preamble-qualified hypotheses (quality at least
0.35), for at most three updates per hypothesis.

A new receiver-only native export, `ow_oq4_soft_bits`, runs 12 sum-product LDPC
iterations and returns the check-to-variable messages from the iteration with the
fewest failed parity checks. These are **extrinsic** likelihoods: the bit's own
channel likelihood is excluded. Existing FT4 and OQ4 hard-decode functions retain
their behavior. An older native library without this optional export simply skips
feedback; its regular decoder continues working. The Linux x64 library is rebuilt;
other platforms need a native rebuild to enable the new fallback.

For each QPSK symbol, tanh(extrinsic/2) gives soft estimates of its I/Q signs.
Their correlation with the observed symbol supplies a residual phase estimate.
A triangular smoother spans nearby data symbols within each pilot-delimited block;
it does not cross the known pilots. Coherence below 0.5 produces no update. Each
update is damped by 0.5 and limited to 0.25 radians; accumulated correction is
limited to 0.65 radians from the original pilot-based solution. Every update
restarts from the original observations plus the accumulated correction. Ordinary
LDPC decoding and the existing 14-bit CRC must succeed before output is accepted.
There are no callsign hints or searches over expected messages.

This is a conservative small-phase-error fallback. It does not repair arbitrary
cycle slips, replace acquisition, change the transmitted pilots or establish a
new sensitivity threshold.

## Measured results and reproduction

Machine-readable results: [2026-11-01-snr-phase-results.json](oq4-evaluation/2026-11-01-snr-phase-results.json).

| Check | Before | With feedback |
| --- | ---: | ---: |
| Existing full-waveform weak-signal corpus | 92/144 | 92/144 |
| Existing recorded-pass signals | 19/19 | 19/19 |
| Synthetic noise/interference outputs | 0/160 | 0/160 |
| Symbol-domain, no phase distortion | 300/300 | 300/300 |
| Symbol-domain, +0.5 rad phase excursions | 254/300 | 287/300 |
| Symbol-domain, -0.5 rad phase excursions | 254/300 | 278/300 |

The application builds and all 146 targeted tests pass.
No wrong payloads appeared in these trials. The additional packets are from the
symbol-domain diagnostic, not the RF recording or acquisition corpus. Its phase
excursions are a sinusoid within each 16-data-symbol block, with Gaussian noise
sigma 0.65 per component, calibrated input LLRs and seeds 8000 through 8299. It
assumes synchronization and pilot correction already succeeded. This deliberately
isolates the feedback mechanism; it does not model the complete satellite channel.
Larger independent fading/phase-noise captures and a fair FT4 comparison remain
necessary. The small negative corpus does not measure rare false-decode rates.

Run the reproducible symbol test without audio devices or PTT:

```bash
~/.dotnet/dotnet run --project tools/Oq4Replay -- --phase-benchmark
```

Use the existing replay README and benchmark generator/scorer for full-waveform
and recording regression. Raw audio stays in ignored local `testdata/oq4/`.

## Files changed in this continuation

- `OscarWatch/Ft4/Oq4Decoder.cs`: bounded feedback path, whole-data SNR, finite-input
  validation, fit-bias correction and preamble fallback.
- `OscarWatch/Ft4/Ft8Native.cs`: optional extrinsic-information P/Invoke wrapper.
- `native/ft8_lib/ft8/ldpc.c`, `.h`: separate soft-output BP entry point.
- `native/oscarwatch_ft8/oscarwatch_ft8.c`, `.h`: exported receiver API and validation.
- `OscarWatch/runtimes/linux-x64/native/oscarwatch_ft8.so`: rebuilt native library.
- `OscarWatch/Views/Oq4Window.axaml`: SNR explanation tooltip.
- `OscarWatch.Tests/Ft4/Oq4PhaseTrackingTests.cs`: phase recovery, extrinsic sign,
  unchanged input, invalid input, SNR calibration and full-waveform checks.
- `tools/Oq4Replay/Program.cs`, `PhaseTrackingBenchmark.cs`: reproducible diagnostic.

The fork's first OQ4 commit also includes the previously developed OQ4 waveform,
window, sequencer integration, transmit runner/PTT preparation, acquisition,
bounded OSD, receive buffering, replay tools and their tests/documentation. See
[protocol/design](OQ4_PROTOCOL_AND_DESIGN.md), [window/PTT parity](OQ4_WINDOW_FT4_PARITY.md),
[recording analysis](OQ4_RECORDING_ANALYSIS.md), [weak-signal research](OQ4_WEAK_SIGNAL_RESEARCH.md)
and [handoff](OQ4_HANDOFF.md). Unrelated terminal/AX.25/theme edits are excluded.
No RF transmission, contact logging or upload was performed during this work.
