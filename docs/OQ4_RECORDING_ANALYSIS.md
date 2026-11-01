# OQ4 recording analysis and receiver update — 2026-10-31

## Result

The revised receiver recovered **eight CRC-valid low-band packets** in the later
RS-44 recording, all `CQ K8OTY EN73`. Six were missed by the previous receiver.
A direct before/after comparison of thirteen identical four-second clips improved
from **13 to 19 decoded signals**: the earlier signals were retained and six lower
signals were added. The operator reports hearing their downlink low in the audio
band, and these are our strongest return candidates. CRC establishes decoded
content, not the RF propagation path; a second station's independent recording
remains valuable confirmation.

Source: `~/Documents/RS44-OQ4-20261031-070511-UTC.wav`, mono PCM16, 48 kHz.
The original marked-up waterfall was inspected and left unchanged. A new zoom is
saved separately as `~/Documents/RS44-OQ4-low-band-decodes.png`.

| Elapsed packet start | Start audio frequency | Previous receiver |
| --- | ---: | --- |
| 12:03.560 | 910.1 Hz | Decoded |
| 12:18.556 | 430.0 Hz | Decoded |
| 13:48.532 | 595.2 Hz | Missed |
| 13:56.056 | 588.8 Hz | Missed |
| 14:03.560 | 582.2 Hz | Missed |
| 14:26.068 | 397.0 Hz | Missed |
| 14:33.516 | 394.7 Hz | Missed |
| 14:41.016 | 389.6 Hz | Missed |

The scan covered 61 four-second windows spaced 7.5 seconds apart from 7:25.5 to
14:55.5 elapsed. It recovered 38 signals total, including the eight above. This is
a sampled scan of the pass, not a complete enumeration of all possible signals.
Neither message contents nor the expected frequencies were supplied to the decoder.

## What changed

- **Tone acquisition under strong bleedover:** the 120 ms observation was previously
  abruptly truncated before padding to a 4096-point FFT. The FFT analyzer's own Hann
  window spans the full padded buffer, leaving substantial edges on the shorter
  observation. Those sidelobes displaced real weaker candidates. When the peak is
  more than 35 dB above the median spectral floor, the observation now receives its
  own Hann taper. Noise-limited observations retain the previous integration window;
  always tapering hurt some weak synthetic cases. This threshold is empirical.
- **Packet timing:** candidate timing and trajectory ranking now use normalized
  correlation against the known QPSK preamble, excluding the louder opening reference.
  This avoids letting the tone select a timing peak away from the data symbols.
- **Live audio conversion:** OQ4's old capture path simply discarded input samples to
  reach 12 kHz. It now uses the existing streaming polyphase low-pass resampler,
  including at 44.1/48 kHz. This prevents additional high-frequency audio harmonics
  folding into the decoder band. It cannot remove aliases already in the recording.
- **No waveform change:** the 64-baud QPSK format, references, pilots, coding, CRC,
  2.523-second burst and 3.75-second slots remain unchanged.

An experimental joint pilot/carrier fit produced no extra decodes on the sample
set and was removed. The earlier ±205 Hz pairing range and inversion support remain.

## A misleading set of traces

The marked traces near 1764, 1882 and 1601 Hz track `12000 - 4 × strong_frequency`.
This was checked directly with FFTs on the **original 48 kHz samples**, not merely
on resampled clips. Opening/closing measurements of three bursts satisfy that
relationship within about 0.6 Hz:

| Elapsed start | Strong opening tone | Marked opening tone | Marked + 4 × strong |
| --- | ---: | ---: | ---: |
| 10:56.020 | 2558.993 Hz | 1763.943 Hz | 11999.916 Hz |
| 12:33.520 | 2529.529 Hz | 1882.118 Hz | 12000.233 Hz |
| 14:26.020 | 2599.948 Hz | 1600.648 Hz | 12000.438 Hz |

Fourth harmonics around 10.1–10.4 kHz are also present in the original recording.
This is strong evidence of a harmonic/alias relationship for these particular
traces. It does **not** identify where the artifact originated, or negate the
operator's audible downlink observations. Earlier estimates of +67/+41/+17 Hz/s
on these traces should not be treated as verified satellite Doppler measurements.
The low-band decoded packets are a separate observation.

## Reproduction and checks

See [the replay tool](../tools/Oq4Replay/README.md) for the exact extraction and
verification commands. Its committed expectations check all 19 signals in thirteen
clips, with separate frequency ranges for simultaneous copies. Audio is local in
ignored `testdata/oq4/`; only measurements and expectations belong in the repository.
[Machine-readable results](oq4-evaluation/2026-10-31-replay-results.json) preserve
before/after outputs, tone measurements and the low-band scan results.

Targeted validation passed **45 tests**: 32 OQ4 tests and 13 existing polyphase
resampler tests. Coverage includes a delayed signal 46 dB below a stronger station,
low-band tuning offsets, fast drift/inversion, dominant references, additional
seeded Gaussian noise, TX slot scheduling, and preservation of receive windows while a worker is busy. This small noise suite is not a
measured false-positive rate.

A retained 27-file noisy synthetic set decoded 7/27 before and 8/27 after. That is
only a small regression check, not a sensitivity claim or FT4 comparison. The
recorded examples show a clearer acquisition improvement than the noisy simulation.

In the thirteen-clip comparison and sampled pass scan, some failed candidates
consume substantial time. The 61-window scan's slowest decode took approximately
3.23 seconds; in the initial eight-clip comparison median time rose from about
1.05 to 2.51 seconds. These are offline measurements, not a live throughput guarantee.
The live receiver now captures one four-second snapshot per UTC slot, ending at
slot +3.25 seconds, independently of whether a decode is busy. Up to four snapshots
can wait for the worker; only sustained overload discards the oldest pending slot
and reports that condition. Sixteen seconds of input history allow catch-up after
brief UI delays. Tests verify that every queued window contains the full scheduled
burst and that overload/reset behavior is bounded. This assumes OQ4 UTC timing;
large station clock errors or bursts outside the receive guard still need work.

## Intermittent TX abort

The operator described TX looking armed, then switching off at the last moment.
The old timer only began preparation in the first 250 ms of the selected slot,
leaving little time before the +400 ms burst. A slow Doppler tune, waveform/audio
preparation or PTT operation could throw a timing error and disarm transmission.

Preparation now starts up to 1.2 seconds before the burst (longer for a configured
PTT lead). Arming too late selects the next matching slot. A timing overrun skips
that slot while preserving the armed state for the next one; actual configuration
and audio failures still stop TX and report their error. Stop/Halt and the existing
three-minute watchdog remain in place. Slot-selection tests pass; no RF transmission
was initiated to test the change, so the operator's exact hardware symptom still
needs confirmation on the next run.

## Mode ideas to evaluate next

1. Track successfully decoded carriers between bursts, while keeping wideband
   acquisition available. Use previous drift as a search hint, not a requirement
   for an exact split or tuning frequency on an older radio.
2. Investigate subtraction of a decoded/known local TX waveform to expose weaker
   returns. Fit amplitude, delay and phase to the actual recording; subtraction
   can make things worse if the model is wrong and cannot undo analog clipping.
3. Compare pilot placement and power under fading and drift before changing the
   on-air format. Extra or redistributed pilots trade payload/packet time and
   synchronization energy for tracking information; keep versioned captures if
   a new format is attempted.
4. Later, consider QSO-context-assisted decoding and soft combining of verified
   repeated-message candidates. Both require explicit false-decode testing and
   safeguards against combining different payloads. WSJT-X's official
   [decoder documentation](https://wsjt.sourceforge.io/wsjtx-main_en.html)
   describes the established use of QSO context in its AP decoding; this is an
   idea for OQ4, not a feature implemented here.

For the next pass/QSO, retain an unprocessed receive WAV, UTC timing, selected
satellite, transmitted messages/carrier changes, and the other operator's receive
recording if available. Exact manual split calibration is not required by this
receiver. Keep the current waveform fixed while collecting those comparisons.
