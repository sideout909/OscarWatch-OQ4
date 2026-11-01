# OQ4: experimental protocol and design

**Status:** experimental, not an established or interoperable amateur-radio mode. This document describes the OQ4 v1 transmitter implemented in this fork, identifies the parts inherited from FT4, and separates existing behavior from ideas still to be developed. The format may change as we test it. A basic OQ4 decoder is implemented; automatic QSO sequencing and satellite echo calibration have not been implemented. Transmit-side Doppler compensation is experimental and has not been tested through a satellite.

## Why OQ4 exists

The original goal is a digital mode for short amateur-radio contacts through existing linear satellites such as RS-44, with terrestrial use as well. A satellite pass can be brief, so short messages and short transmit/receive cycles matter. The mode should share a transponder with other operators, occupy far less than 1 kHz, automatically detect corrupted messages, and cope with frequency offset and changing Doppler.

FT4 on RS-44, using OscarWatch, is our practical reference. FT4 already supports narrow signals, structured contacts, FEC and useful satellite Doppler tools. A first 500 Hz proposal would have used about six FT4 channels per station and was rejected. The design direction is closer to FT4's approximately 83 Hz nominal signal width while targeting shorter bursts. A shorter burst does not by itself guarantee a shorter or more reliable completed contact; turnaround, decoding, retransmissions, and signal strength also matter.

Our first waveform uses coherent quadrature phase-shift keying (QPSK). QPSK and LDPC codes are established techniques; OQ4's particular synchronization sequence, frame and timing are our experiment. We have not established a weak-signal advantage over FT4 or QPSK63. In particular, reducing the time available to send a message reduces its received energy at constant average signal power.

## Comparison with FT4

| Element | FT4 | OQ4 v1 |
| --- | --- | --- |
| Intended messages | Compact structured amateur contacts | Initially the same supported message formats |
| Message packing | 77-bit FT4/FT8 packed message | Uses the same `ft8_lib` message packer |
| Error detection | 14-bit CRC | Same FT4 CRC, applied after FT4 payload scrambling |
| Error correction | LDPC (174, 91) | Same FT4 LDPC code and bit order |
| RF/audio modulation | Four-tone Gaussian FSK | Single-carrier coherent QPSK with root-raised-cosine shaping |
| Symbol rate | Approximately 20.83 baud | 64 baud |
| Synchronization | FT4 Costas arrays | 32 known QPSK symbols followed by known pilot pairs |
| Burst | Approximately 5.04 s | Approximately 2.5232 s including reference tones at the internal 12 kHz rate |
| Slot | 7.5 s | Experimental 3.75 s UTC slots |
| Decoder | Established | Not yet implemented |

FT4 information comes from the [WSJT-X user guide](https://wsjtx.github.io/wsjtx/guide-full.html). Although OQ4 uses FT4's message and coding building blocks, **it is not FT4**: the over-the-air modulation, framing, pilot symbols, symbol rate, and timing differ. An FT4 decoder cannot decode OQ4; an OQ4 receiver must first recover the QPSK symbols and reconstruct the codeword in the expected bit order.

## Current transmitter processing

The implementation is in `OscarWatch.Core/Ft4/Oq4Waveform.cs`, `OscarWatch/Ft4/Ft8Native.cs`, and `native/oscarwatch_ft8`. It is the authoritative reference if this document and code disagree.

1. The operator supplies a message supported by the existing FT4 message packer, for example `CQ K1ABC FN42` or `K1ABC W9XYZ -10`. The software normalizes case and common slash variants. Callsign hashes and nonstandard calls inherit ft8_lib's handling and limitations; a station receiving a hashed call may need prior context to resolve it.
2. `ftx_message_encode` packs the message into the existing 77-bit representation. The 77 bits include message-type fields; they are not 77 bits of arbitrary free text. Invalid input is rejected rather than sent as another mode.
3. `ftx_codeword_bits(1, ...)` applies the FT4 payload XOR sequence, calculates the 14-bit CRC, and encodes the 91-bit message-plus-CRC into a 174-bit LDPC codeword. The returned bytes each contain one bit, 0 or 1, in the underlying decoder's LLR/codeword order. No extra interleaver is currently applied.
4. Adjacent bits are mapped to the I and Q signs of one QPSK symbol: `0 -> +1`, `1 -> -1`. Thus each data symbol carries two coded bits. The absolute constellation amplitude is subsequently scaled during waveform generation; receivers should not depend on a fixed level.
5. Known acquisition and pilot symbols are inserted as specified below. The entire symbol sequence is shaped using a finite root-raised-cosine pulse, with a center-frequency reference burst added before and after it.
6. The complex baseband waveform is translated to real audio at the chosen USB audio frequency, peak-normalized, and handed to the existing OscarWatch audio output path. The operator's FT4 TX audio level is then applied. Device-rate conversion is performed by that shared audio service.

The FT4 encoder is supplied by `native/ft8_lib` and the native wrapper. The new wrapper entry point `ow_oq4_encode_bits` is required on builds whose native library hides internal ft8_lib symbols. The Linux development library in this checkout was rebuilt. Other platform packages need their own rebuilt native library; existing released DLLs do not automatically gain OQ4 support.

## Exact v1 symbol framing

The QPSK portion of the v1 packet is unchanged from v0. It has **129 symbols** before filter tails:

- 32 known acquisition symbols.
- 87 QPSK data symbols containing the 174 coded bits in order.
- 10 known pilot symbols: a pair after data symbols 16, 32, 48, 64, and 80. There are no pilots after the final seven data symbols.

Known symbols are generated from a 32-bit unsigned xorshift state. Initialize `state = 0x4f513431`. For each acquisition or pilot symbol, advance the state once using unsigned 32-bit wraparound:

```
state ^= state << 13
state ^= state >> 17
state ^= state << 5
I_bit = state & 1
Q_bit = (state >> 1) & 1
I = I_bit == 0 ? +1 : -1
Q = Q_bit == 0 ? +1 : -1
```

The PRNG continues across the entire burst; data symbols do not advance it. The acquisition sequence uses the first 32 states, then the five pilot pairs use the next ten states. This exact sequence is experimental: it has not yet been optimized or measured for false acquisition, frequency-offset ambiguity, or weak-signal performance.

For interoperability testing, the 32 preamble symbols written as `IQ` bit pairs are:

```
01 01 11 11 00 00 01 11 01 11 01 11 00 01 11 01
10 10 10 10 10 11 11 01 01 01 00 11 10 11 10 11
```

The subsequent ten pilot symbols are `10 10 00 10 01 10 11 01 01 00`. As a codeword test vector, `CQ K1ABC FN42` produces the following 174 bits packed MSB-first into 22 bytes; the final two low bits are zero padding and are not transmitted:

```
4A5E8994FD6563DFA7A74C9DC3A2D77B8E01C43AB5A0
```

A prospective receiver must account for the fourfold QPSK carrier-phase ambiguity, use the known symbols to estimate phase, frequency offset and timing, and output soft information in codeword bit order. The pilot placement is intended to help track residual phase and frequency changes, but its adequacy under real Doppler and fading is not established.

## Start and end reference tones

The v1 packet adds a 160 ms center-frequency reference tone at each end of the existing QPSK burst. Each reference uses a 20 ms raised-cosine ramp at both edges, with a 120 ms constant-amplitude interior. At 12 kHz each reference contains 1,920 samples; the first and last samples are zero. The reference is a cosine at the selected audio center frequency, with peak amplitude 0.9 before the operator's audio gain is applied. Its carrier phase is based on elapsed time from the start of the complete packet, including any configured transmit Doppler slope. The QPSK segment uses the same absolute carrier time, but its amplitude falls to near zero at its edges, so a future decoder must not assume that phase can be recovered unambiguously between segments.

A beginning reference may help initial frequency acquisition; the end reference may improve an offline estimate of average frequency drift across the burst. The 32-symbol preamble and five internal pilot pairs remain in place for identifying the packet, symbol timing, phase recovery and tracking. This is an experimental change to the v0 wire format. A basic OQ4 receiver now exists; a satellite-relayed OQ4 decode has not been confirmed.

Both references experience Doppler, and received frequency also includes transmitter and receiver oscillator errors, tuning offsets, and transponder effects. The receiver must search in a sufficiently wide captured passband. Short tones can be weak or confused with unrelated carriers; pairing the references and detecting the known preamble are essential. Compare the two-reference approach with known-symbol-only acquisition under noise, Doppler, fading, and overlapping transmissions.

## Pulse shaping, bandwidth, and duration

The QPSK modulation rate is **64 symbols/s**, so one symbol lasts **15.625 ms**. The pulse is root-raised-cosine (RRC), roll-off `alpha = 0.25`, truncated at six symbol periods on either side of each symbol. For the QPSK portion, the ideal full RF bandwidth of an RRC signal is approximately `(1 + alpha) × symbol rate`, or **80 Hz**. The finite filter, burst edges, soundcard resampling, and transmitter nonlinearity produce energy outside that ideal limit.

Let `t` be time in symbol periods relative to a symbol center. For non-singular values, the pulse is:

```
h(t) = [sin(pi t (1-alpha)) + 4 alpha t cos(pi t (1+alpha))]
       / [pi t (1 - (4 alpha t)^2)]
```

The implementation evaluates the usual limits at `t = 0` and `|t| = 1/(4 alpha)` and sums the contributing symbols within ±6 symbols. The real audio sample is `I*cos(phase) - Q*sin(phase)`, where `phase = 2*pi*audio_hz*n/sample_rate` and the nominal internal audio rate is 12,000 Hz. The carrier phase starts at zero for each burst. When transmit Doppler precompensation is enabled, `pi*slope_hz_per_second*(n/sample_rate)^2` is added to the carrier phase. Samples are globally peak-normalized to 0.9 before the operator's audio-level setting is applied. This normalization means absolute amplitude may vary with the symbol pattern.

The 129 QPSK symbols plus 12 symbol periods allocated for the filter tails yield `ceil((129 + 12) × 12000 / 64) = 26,438` samples, approximately **2.2032 s**. The two 1,920-sample reference tones bring v1 to **30,278 samples**, approximately **2.5232 s** in total. One generated v1 test message had approximately 69 Hz containing 99% of its audio energy, including both reference tones. This is one offline sample, not a certified RF bandwidth or a guarantee across messages. Measure the actual transmitter's occupied bandwidth and unwanted emissions separately.

## Slot and operating model

The current experiment uses **3.75-second UTC-aligned slots**, numbered by `floor(seconds_since_Unix_epoch / 3.75)`. Sixteen slots fit in one UTC minute. Even and odd numbered slots alternate. The operator chooses which parity to transmit. The target audio burst start is 0.4 s after the selected slot boundary, leaving roughly 0.827 s after the 2.523 s burst for turnaround and future decoding work.

The operating window can enable transmission of the current message on successive slots of the selected parity until halted. Repetition stops after a three-minute watchdog. It prepares the audio and opens the output before PTT, applies the configured PTT lead and tail, and skips a transmission if preparation or keying makes the slot too late. Timing is controlled by the PC clock, with the existing FT4 GPS clock-offset mechanism where active. The actual clock error, scheduler delay, radio keying delay, and audio-device latency still require measurement; this is not yet a validated over-the-air timing specification.

Call CQ builds a message from the station callsign and grid in Settings and enables repeated transmission. A tune control transmits a continuous audio tone until halted or the three-minute watchdog expires. The other operator is normally identified from a decoded message; the decoder-dependent Report, 73, Auto Reply, Pounce and Skip RRR controls remain disabled. There is no receive-driven auto-reply or automatic advancement. Right-click the activity list to prepare or send an audio-only loopback test. TX activity records the nominal UTC slot start (including fractional seconds), is local display history, and can be saved to a text file; it is not proof another station received a message. The Logbook button opens the existing OscarWatch logbook for manual entry; OQ4 does not automatically log contacts. The live waterfall displays incoming audio, and the receive path searches the passband independently of the selected transmit frequency. The audio-only test plays immediately and does not assert PTT, but audio routed to a radio can still trigger VOX. Existing FT4 audio and PTT configuration is shared by this prototype.

When satellite tracking is active, the radio is connected, and FT4 transmit audio Doppler is enabled, OQ4 estimates the uplink Doppler slope over the burst from two orbit propagations, temporarily holds CAT Doppler updates, commands a slot-time tuning step, and applies the predicted audio-frequency slope to the QPSK carrier. The CAT hold is released after the burst or on failure. A late or failed tuning step skips the burst. This transmit-side path is experimental and has not been tested on an actual satellite; radio behavior and timing remain to be measured. When those conditions do not apply, normal OscarWatch tracking remains in control and no OQ4 audio precompensation is applied. OQ4 has no decoded-echo uplink calibration yet. The decoder must estimate each received station's residual offset and frequency drift; orbit prediction alone will not perfectly align all stations. Terrestrial paths can also have frequency offsets, fading, and multipath.

## Basic receive implementation

The experimental window collects four seconds of input audio and searches the 200–3000 Hz passband for pairs of reference tones. It tries up to four candidates, correlates the known preamble, estimates drift from both references, tests linear and curved drift trajectories, applies a matched RRC filter, follows residual phase using pilot and smoothed-phase hypotheses, and passes soft bits through the FT4 LDPC and CRC checks. A message is displayed only after the CRC succeeds. The windows overlap and repeated detections of the same burst are suppressed. The receiver tries both normal and spectrally inverted QPSK. This is a basic, candidate-limited receiver; weak-signal sensitivity, severe interference, clock mismatch, false positives from additional hypotheses, and satellite operation need more work and measurement. The Linux native library must be rebuilt with the new OQ4 decode entry point; other platforms need their native packages rebuilt as well.

For a more capable receiver, the design should:

1. Locate the known acquisition symbols across candidate arrival times and residual frequency offsets; consider frequency-slope hypotheses where necessary. A wide initial search and a narrow decoded signal are compatible.
2. Estimate symbol timing, carrier phase and frequency; correct the four possible QPSK phase rotations using the known acquisition symbols.
3. Apply a matched RRC filter and use the pilot pairs and/or data-aided tracking to follow remaining phase and frequency drift.
4. Produce calibrated soft bit likelihoods in the original 174-bit order. Deinterleave only if a future format revision adds interleaving.
5. Run the FT4 LDPC decoder and validate the FT4 CRC before displaying a message as decoded. Reverse FT4 payload scrambling and unpack the 77-bit message. A CRC reduces false acceptance but is not authentication.
6. For multiple stations in a slot, find and test multiple candidates, manage adjacent and overlapping signals, and avoid falsely labeling one station's signal as another's echo.
7. Once decoding works, add receive-driven sequencing and echo calibration. Neither the exact search ranges nor the acceptable decode deadline have been established.

A receiver is a substantial part of the design. Matched filtering alone is not sufficient to handle weak signals, phase ambiguity, drift, clocks, and simultaneous signals. A clean loopback test is the beginning, not evidence of robust satellite reception.

## Validation plan

- Preserve known message-to-codeword and codeword-to-waveform test vectors, including calls, grids, reports, nonstandard calls, invalid inputs and timing boundaries.
- Test loopback recovery and CRC checking without a radio; exercise both slot parities, message changes, repeat/halt, PTT failure and audio-device failure.
- Measure occupied bandwidth at several levels, audio frequencies, sample rates, and messages; check transmitter linearity and spectrum with real hardware.
- Characterize packet success versus SNR, frequency offset, clock error, frequency drift, phase noise, fading and adjacent signals. Compare against FT4 and QPSK63 using clearly stated signal power, noise bandwidth, bandwidth, and message length.
- Test transponder power, tuning, timing, and Doppler on appropriate linear satellites with other operators, then test terrestrial paths, including HF multipath if desired.
- Measure completed, correctly confirmed contacts per pass, not only burst length or ideal symbol rate.

## Implementation and source boundaries

The OQ4 code lives in this fork of OscarWatch. Message packing and error-correcting-code primitives are carried over from the project's existing ft8_lib integration. The pulse shaping, frame and operating controls are experimental OQ4 additions. New software can implement the protocol only after the format and test vectors have been documented consistently; before broader use, review the repository's GPL license and the licenses and attribution for the upstream components.

## RS-44 FT4 drift reference

In the 2026-10-31 pass recording, the received FT4 messages `CQ N8HRZ EN91` and `K8OTY N8HRZ -14` provide a useful real-world drift reference. A preliminary time-frequency fit to their known FT4 symbol sequences gave approximately -14.5 Hz/s and -13.5 Hz/s respectively in the recorded audio, or roughly -70 Hz over a five-second FT4 burst. These are approximate fits to this receiver recording, not a universal RS-44 drift specification. OscarWatch's pass log reported a further -4 Hz/s residual correction *after* its predicted downlink Doppler correction; it is a different quantity and should not be substituted for the raw audio slope. The data does not establish the amount of curvature within a short burst.


## Initial local comparison, 2026-10-31

The operator reported that, in Decoder Lab, OQ4 decoded roughly 60–70% of the time and FT4 roughly 10% at signal **−40 dBFS RMS**, broadband noise **−23 dBFS RMS**, and drift rate changing from **0 to −6 Hz/s**. These are the operator’s approximate observations, not a counted or statistically controlled benchmark. Record the number of transmitted and correctly decoded messages, message contents, UTC slots, mode, frequency, repetitions, random seeds and confidence intervals before drawing a general sensitivity comparison. Control for differences in burst duration, slot timing, amplitude normalization, decoder search and latency, noise bandwidth, and how each mode handles drift.

In the lab, the drift-rate control changes linearly through a burst, so frequency follows a quadratic curve. Signal level is active-burst RMS dBFS and noise level is broadband RMS dBFS across the 0–6 kHz band; these are not direct WSJT-X reported SNR values. Under ideal white-noise assumptions, −40/−23 dBFS corresponds to approximately −13.2 dB signal-to-noise power in a 2.5 kHz reference bandwidth; actual decoder-reported SNR depends on its estimator. At 0 to −6 Hz/s over a 2.523 s OQ4 burst, the approximate total frequency change is −7.6 Hz.

A separate small scripted sweep at −40/−22 dBFS found variable OQ4 success across noise realizations and 500, 1500 and 2300 Hz, so the local test is encouraging but not evidence of universal superiority or verified satellite reception. One noisy, curved-drift sample and the recorded `CQ K8OTY EN73` burst both decoded with the full-passband prototype. FT4’s received RS-44 drift reference above is a separate measurement from this generated lab scenario.


## Marked RS-44 downlink reference

In `RS44-OQ4-20261031-070511-waterfall-notes.png`, the operator marked weaker traces that they heard on the satellite downlink. The marked signals occur alongside much stronger, separate transmissions. In the accompanying recording at about 10:56 elapsed, the first and final reference tones are approximately 1764 Hz and 1923 Hz, respectively: about **+159 Hz over 2.363 seconds**, or **+67 Hz/s on average**. A later marked burst around 12:34 measures approximately +41 Hz/s, and another around 14:26 approximately +17 Hz/s. These are approximate received audio-frequency slopes for this pass and receiver configuration, not a specification of the satellite or necessarily a measurement of nonlinear curvature.

The earlier receiver could pair tones only within about 60 Hz and allowed a strong signal’s sidelobes to occupy its limited candidate list. The search now pairs tones within approximately 205 Hz, prioritizes different frequency regions, and suppresses some sidelobe candidates after a stronger candidate is decoded. It also accounts for the reference tones being measured near their centers, and tries normal and spectrally inverted QPSK. Tests cover simultaneous strong and weak generated packets at ±68 Hz/s and a spectrally inverted, fast-drifting weak packet next to a stronger signal. These improvements do not establish that the marked downlink is spectrally inverted.

Offline evaluation against three marked windows found the weaker tone-pair candidates while also decoding `CQ K8OTY EN73` on the much stronger trace. **The weaker marked traces did not produce a CRC-valid message.** The stronger trace is not proof of a satellite-relayed decode. Continue investigating weak-signal demodulation and real-pass distortion; avoid presenting detected tones or the stronger copy as a successful decode of the marked downlink.


## Recording-driven update, 2026-10-31 evening

See [OQ4_RECORDING_ANALYSIS.md](OQ4_RECORDING_ANALYSIS.md) for the reproducible
13 → 19 signal comparison, eight low-band CRC-valid packets, acquisition and live
resampling changes, the intermittent TX timing fix, and remaining limitations.
The previously measured high positive drifts on the marked traces now have strong
evidence of a harmonic/alias relationship; they are not verified satellite Doppler.
No on-air format, coding, packet length or slot-length change was made.


## Operating window/PTT update — 2026-11-01

See [OQ4_WINDOW_FT4_PARITY.md](OQ4_WINDOW_FT4_PARITY.md). The operating window now
uses FT4's QSO sequencer/highlighting and a dedicated, testable PTT/audio timing
worker. Adapter open, audio preparation and power queries happen before the keying
deadline. CAT preparation timeout no longer prevents PTT. The actual packet format
and OQ4 slot duration are unchanged. Displayed reports use an approximate
preamble-based SNR estimate, marked `~`; real-radio calibration is outstanding.


## Weak-signal receiver research — 2026-11-01

[OQ4_WEAK_SIGNAL_RESEARCH.md](OQ4_WEAK_SIGNAL_RESEARCH.md) records the primary-source
review and bounded OSD fallback. After fast BP fails, up to 12 gated soft hypotheses
per candidate undergo restricted order-2 reprocessing with the existing CRC check.
A seeded 144-packet comparison improved 87 → 92 correct messages without losing
previous successes; a 72-case holdout improved 41 → 43. Negative tests and the
recording regressions passed. These results do not establish a lower threshold
than FT4. The on-air format and PTT/window operation are unchanged.


## SNR and phase-tracking continuation (2026-11-01)

The current receiver uses whole-data SNR after CRC and bounded LDPC-extrinsic
phase feedback after ordinary BP/OSD. These supersede earlier descriptions of
preamble-only reports and planned feedback. See [SNR and phase tracking](OQ4_SNR_AND_PHASE_TRACKING.md)
for equations, native compatibility, test results and limitations. The wire format
and transmit timing are unchanged.
