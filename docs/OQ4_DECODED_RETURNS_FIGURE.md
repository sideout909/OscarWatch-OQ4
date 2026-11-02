# Recorded OQ4 return waterfalls

The README figure shows six separate, delayed CRC-valid copies from the JO-97
pass recorded on 2026-11-02 at 02:32 UTC. The upper row contains the three lowest
SNR estimates among the 16 previously established separate copies; the lower row
contains the three highest. Strong local TX leakage and the three additional
copies with unresolved paths are excluded from selection.

All six examples decoded `CQ K8OTY EN73` without frequency or annotation hints
using the released 1.1.6-OQ4 receiver, commit
`cd50549aca046f43446089daf9104ba61fe0a195`.

| UTC slot | Audio Hz | Estimated SNR dB | Delay after local copy ms |
|---|---:|---:|---:|
| 02:39:45 | 2024.8 | -14.2 | 16 |
| 02:34:15 | 856.1 | -14.0 | 22 |
| 02:35:52.5 | 1240.6 | -13.6 | 16 |
| 02:38:22.5 | 2339.5 | -8.9 | 16 |
| 02:39:52.5 | 1986.2 | -7.1 | 16 |
| 02:38:30 | 2363.2 | -3.8 | 12 |

These values are the blind receiver outputs, so they differ slightly from the
earlier annotation-guided estimates in the acquisition report. The horizontal
axis starts at the decoded packet start, not the UTC slot boundary. Frequency
is displayed relative to the decoded opening carrier; the signal's drift remains
visible. Shaded REF regions mark the nominal 160 ms opening and closing tones.

Each panel uses the same 2048-sample Hann window at 12 kHz, 60-sample hop,
240 Hz frequency span, time span and spectral-density color scale. Audio is
not filtered, drift-corrected or normalized for this figure. The clip preparation
only resamples the original recording to 12 kHz with anti-alias filtering.
Different background levels reflect the recording, not display gain changes.
SNR is the decoder's approximate data-only estimate referenced to 2500 Hz;
the color bar measures recorded spectral density and is not an SNR scale.

The repeated delayed copies and separate frequency tracks support their
identification as downlink returns. Timing is relative to the decoded local
copy, not a calibrated propagation-delay measurement. This figure illustrates
recorded OQ4 decodes; it is not a paired FT4 sensitivity measurement.

## Reproduce locally

The original WAV stays local. Its SHA-256 and clip offsets are in
[`jo97-20261102-0232.manifest.json`](../tools/Oq4Replay/jo97-20261102-0232.manifest.json).
The filename says RS44; the operator identified the satellite as JO-97.
The six exact decoder outputs and timing measurements are recorded in
[`decoded_returns_figure.json`](../tools/Oq4Replay/decoded_returns_figure.json).

With FFmpeg, NumPy, SciPy and Matplotlib installed, run from the repository root:

```bash
python3 tools/Oq4Replay/prepare_manifest.py \
  "$HOME/Documents/RS44-OQ4-20261102-023206-UTC.wav" \
  tools/Oq4Replay/jo97-20261102-0232.manifest.json \
  --output testdata/oq4/jo97-20261102-0232
python3 tools/Oq4Replay/plot_decoded_returns.py \
  testdata/oq4/jo97-20261102-0232 screenshots/oq4-decoded-returns.png
```

See the [acquisition study](OQ4_ACQUISITION_20261102.md) for receiver validation
and recording limitations.
