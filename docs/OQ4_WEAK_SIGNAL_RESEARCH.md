# Weak-signal QPSK research and OQ4 receiver experiment — 2026-11-01

Update 2026-11-01: the code-assisted phase-tracking follow-up is now implemented.
See [SNR and phase tracking](OQ4_SNR_AND_PHASE_TRACKING.md) for the bounded
extrinsic-feedback design, calibration checks and measured limits. Earlier next-step
references below describe the research state before that implementation.


## Outcome

A bounded ordered-statistics fallback was added after the existing OQ4 belief-
propagation decoder. On 144 paired synthetic packets it recovered **92 instead of
87 correct messages**, retaining every earlier success. The reserved half improved
from **41/72 to 43/72**. No incorrect payloads were returned in those signal trials.
Both receivers returned zero messages in 160 noise/interference windows.

This is a measured improvement on the selected test set. It is **not a measured
sensitivity gain in dB, a demonstrated false-decode rate, or an FT4 comparison**.
The experiment leaves the waveform and the new PTT/window work unchanged.

## What the primary sources support

### Hybrid error correction is a practical next step

Taylor, Franke and Somerville's [FT4/FT8 protocol paper](https://wsjt.sourceforge.io/FT4_FT8_QEX.pdf)
describes combining fast belief propagation with ordered-statistics decoding after
BP fails. Its simulations show gains from receiver processing, not simply from
choosing a modulation. Our native OQ4 FEC entry point previously called BP alone.
This motivated adding a bounded fallback, rather than assuming that inheriting
FT4's code automatically inherited its strongest decoder.

The foundational [Fossorier and Lin OSD paper](https://ieeexplore.ieee.org/document/412683/)
describes reliability ordering and successive reprocessing with a performance/
complexity tradeoff. Our restricted implementation is not a copy of WSJT-X's OSD,
not a full maximum-likelihood search and not expected to match its performance.

### Phase recovery and error correction should eventually cooperate

The JPL/NASA record for [Information-reduced Carrier Synchronization of Iterative
Decoded BPSK and QPSK using Soft Decision Feedback](https://ntrs.nasa.gov/citations/20090026416)
describes feeding extrinsic soft information from an iterative decoder back into
carrier recovery at low SNR. That is directly relevant to OQ4: a weak two-symbol
pilot can give a noisy phase estimate, while LDPC constraints contain additional
information about the transmitted symbols. The NASA record provides an abstract,
not a downloadable full implementation; no quantitative OQ4 gain is inferred.

A future implementation would alternate a bounded number of carrier refinements
and LDPC passes. It should use soft or extrinsic decisions, confidence weighting
and independent pilot checks, so an initially wrong decode does not reinforce its
own phase estimate. That needs posterior/extrinsic outputs from the native decoder;
we did not add that larger interface change during this experiment.

### Pilot location matters, but published bounds are not measured gains

Rice's [pilot-assisted phase/frequency estimation study](https://digital.library.adelaide.edu.au/items/0822b038-d19f-4211-881d-c647e1c44437)
examines estimation bounds for bursts with known symbols and discusses symmetric
pilot placement, including preamble/postamble arrangements. This supports studying
how we use OQ4's distributed pilots and final reference. It does not establish that
adding a particular number of pilots will improve this receiver by a stated dB
amount. OQ4 already has a substantial known-symbol overhead; better use of existing
pilots should precede a format change.

### Soft decisions must describe uncertainty

The [MathWorks QPSK demodulator documentation](https://www.mathworks.com/help/comm/ref/qpskdemodulatorbaseband.html)
explains soft LLR outputs and the noise-variance input used by soft decoding. In
our mapping, positive LLR means bit one. For a real component with means ±A and
noise variance σ², our sign convention gives `LLR = -2*A*received_component/σ²`.
Amplitude uncertainty, residual phase error and interference complicate that simple
AWGN expression. Confidence should decrease during a fade or a bad phase estimate,
rather than turning unreliable samples into confident hard bits.

### Tracking bandwidth and cycle slips remain important

JPL's [DSN telemetry reception handbook](https://deepspace.jpl.nasa.gov/dsndocs/810-005/207/207A.pdf)
discusses carrier-loop losses and phase-slip behavior for suppressed-carrier QPSK.
Its receiver scale and operating conditions differ substantially from a short
amateur-radio burst. The applicable lesson is to budget tracking error and phase
noise explicitly; its numerical loop recommendations should not be transplanted
into OQ4 without a matching model.

## Experiments performed

The prior receiver was frozen locally before edits. Independent Python waveform
construction comes from the existing Decoder Lab; message packing/FEC generation
shares the native encoder. Expectations and known payloads are supplied only to
the scorer after decoding, never to the receiver.

### Two small changes that did not improve this set

1. Estimate preamble amplitude/noise and try a calibrated QPSK LLR scale before
   the existing scale hypotheses.
2. Correlate against a complete known-preamble pulse template instead of the
   current shortened correlation interval.

Both versions still decoded **87/144** with the same successes as the baseline.
They were removed. This does not prove the techniques cannot help under other
conditions; it avoids retaining extra work without evidence of a benefit here.

### The retained bounded fallback

`Oq4OrderedStatisticsDecoder.cs` uses the same (174,91) LDPC generator as the native
encoder. The 91 information positions include the 14 CRC bits. Its packed parity
matrix is attributed to the MIT-licensed `native/ft8_lib/ft8/constants.c`; native-
encoder cross-checks verify the managed matrix.

The existing fast BP path runs first. Only if it cannot recover a valid message:

- Require normalized **symbol-preamble coherence ≥0.35**.
- Retain at most **12** promising soft hypotheses per tone-pair candidate, with
  near-duplicate timing/frequency/LLR hypotheses suppressed.
- Form a most-reliable independent basis by reliability-ordered Gaussian
  elimination of the full (174,91) generator.
- Try order zero, then one- and two-bit flips among the **16 least reliable basis
  positions**: `1 + 16 + 120 = 137` patterns per hypothesis.
- Select the codeword with minimum weighted Hamming distance across the 174
  received bits. Reject it if its distance exceeds **0.10 of total reliability**
  or it differs from the hard decisions in more than **32 bits**.
- Submit **only that closest candidate** to the existing native parity/CRC/unpack
  path. Do not walk a candidate list until a conveniently passing CRC appears.

Those bounds are conservative engineering choices, not universal thresholds from
a paper. The search is restricted and can miss a codeword outside its 137 patterns.
CRC still has finite accidental-accept probability, especially with many searched
hypotheses. Noise tests remain necessary; these gates do not establish a production
false-alarm specification. No QSO or callsign hints are used by this fallback.

Both fast and recovered packets retain the existing approximate preamble-based
SNR estimate. The UI continues to mark it `~`.

## Measured results

Signal trials:

- 144 packets; three payloads; 500/1500/2300 Hz nominal audio frequencies.
- Two packet-average SNR settings: **−12.5 and −14.5 dB**, referred to 2500 Hz.
- Fixed −35 dBFS active packet RMS; white noise is scaled consistently from its
  6000 Hz Nyquist bandwidth. This avoids confusing dBFS with SNR.
- Stationary, +14 Hz/s linear drift, 0→−14 Hz/s changing rate and +68 Hz/s fast
  drift. The last is a stress case, not verified RS-44 Doppler ground truth.
- Random carrier offsets within ±1.8 Hz and packet timing within ±100 ms.
- Six seeded trials per configuration: first three discovery, last three holdout.
  The search bounds were fixed before inspecting OSD holdout results.

| Set | Prior BP receiver | Bounded BP+OSD |
| --- | ---: | ---: |
| Discovery | 46/72 | 49/72 |
| Holdout | 41/72 | 43/72 |
| Combined | 87/144 | 92/144 |
| Prior successes lost | — | 0 |
| Incorrect signal payloads | 0 | 0 |

All five added messages were at −14.5 dB in this sample. Five added cases do not
justify a general sensitivity or statistical-significance claim. Larger independent
sweeps and fading/phase-noise cases remain necessary.

Noise/interference trials consisted of **40 windows each** of white noise, colored
noise, a continuous tone plus white noise, and sparse additive impulses plus white
noise. Each window was four seconds; both receivers returned **0/160** messages.
The impulse model is an unclipped additive stress test, not an ADC saturation model.
Ten minutes and forty seconds of synthetic negative audio is a useful regression
check, not sufficient measurement of a rare false-decode rate.

Targeted validation passed **135 tests**, including direct cases where BP misses
20–30 hard-decision errors and the fallback recovers the original CRC-valid message.
Tests also check rejection of LDPC-valid/CRC-invalid words, malformed random coded
payloads with valid synchronization, weak preamble evidence and nonfinite inputs.
The existing recording replay still passes **19 expected signals in thirteen
clips**. No new satellite-path claim follows from these checks.

[Machine-readable cases/results](oq4-evaluation/2026-11-01-weak-signal-results.json)
preserve all signal-case metadata, paired outputs, split counts, negative cases
and source hashes. Raw audio and the frozen baseline sources are local under
`testdata/oq4/research-20261101/`, which is ignored by Git. Parallel evaluation and
other builds mean the recorded CPU times are not controlled latency comparisons.

## What “better than FT4” should mean

Two targets should be measured separately:

1. **More successful exchanges during a short, drifting satellite pass.** OQ4's
   shorter bursts/slots and a receiver designed for drift may help here. Measure
   completed exchanges including decoder latency and missed slots.
2. **A lower single-packet SNR threshold on a stationary AWGN channel.** That is a
   harder target. At equal average burst power, a 2.523-second OQ4 burst has about
   half the energy of a 5.04-second FT4 burst—roughly 3 dB less. Coding, modulation
   efficiency and receiver processing have to make up that difference.

The current experiment compares OQ4 against its own previous receiver. It does
not compare against OscarWatch FT4, much less full WSJT-X. A credible comparison
must specify equal signal power or equal packet energy, reference noise bandwidth,
messages, frequencies, timing offsets, drift/fading profiles and decoder budgets.
FT4 AP/OSD settings must be stated. Report probability of correct decode and false
outputs over many seeds, rather than comparing two isolated SNR labels.

## Prioritized next research

1. **Code-assisted carrier/phase refinement.** Expose soft/extrinsic LDPC output,
   re-estimate phase using confidence-weighted known/data symbols, and retry a
   bounded number of times. Include cycle-slip and oscillator-phase-noise tests.
2. **Reliability-weighted pilot tracking and fading-aware LLRs.** Downweight bad
   pilot pairs and data during fades. Validate on held-out nonlinear drift,
   dropouts and real recordings; avoid unvalidated hard-decision feedback.
3. **Improve deep FEC using BP posterior reliabilities.** The current fallback
   orders raw channel reliabilities. A hybrid posterior-based basis and principled
   stopping tests may help, but need a larger negative corpus before expanding
   search depth.
4. **Interference handling and repeat combining.** Fit/subtract known strong
   signals only when residuals improve. Combine soft information only after timing,
   carrier and message-identity checks; do not combine changing reports or CQ/QSO
   transitions. Neither approach repairs analog clipping.
5. **Pilot/reference changes only after receiver work is measured.** A versioned
   experimental format could redistribute known symbols or alter reference power,
   but should not invalidate the pass corpus before those comparisons exist.

No wire-format, transmit waveform, PTT configuration or global audio routing was
changed by this research pass. No RF transmission, upload, commit or push occurred.
