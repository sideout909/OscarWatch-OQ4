# OQ4 continuation handoff — 2026-10-31

## Latest continuation — receive-slot PTT blip, 2026-11-01

The operator reports a short PTT reassertion after OQ4 TX, near the start of RX.
The 16:40 UTC log has one scheduled OQ4 key event at :22.500, then adapter close
at :25.541 and another adapter preparation at :26.007. There is no second logged
OQ4 key command during that interval. A USB-serial open/close line transient is a
plausible cause, not yet electrically measured.

Scheduled repeated OQ4 requests now keep the separate PTT adapter **open at idle**
after successful bursts. PTT still deasserts at each burst end. Halt, close,
retarget, cancelled/failed/missed transmissions and nonrepeating operations release
the adapter. Serial preparation is protected by the same stop/key gate; cleanup
always releases Doppler, even when stopping audio/unkeying throws. A serial unkey
failure forces the port closed rather than retaining a potentially asserted line.
No RF timing, waveform, decoder or global audio settings changed.

Tests cover consecutive bursts sharing one adapter open with exactly one key each,
Halt release, next-burst failure, cancellation and cleanup failure. The app builds and **153 targeted tests pass**. Hardware RTS/DTR
behavior still needs an operator check after restarting this build. Do not describe
the physical glitch as proven eliminated until that check.

The prior JO-97 capture review is saved locally in Documents as
`JO97-OQ4-20261101-1604-review.md`, with graphic and analysis JSON. Three CRC-valid
lower copies near 980 Hz were independently reproduced, 16 ms after upper copies,
with downlink-like drift; the source filename misleadingly starts RS44. Replay
fixtures remain ignored under `testdata/oq4/jo97-20261101-1604/`. The separate FT4
live RX/TX report is not conclusively diagnosed; native replay recovers W9SV and
logs show two FT4 playback starts. Keep that issue distinct from this adapter fix.

## Latest continuation — fork presentation and paired operation, 2026-11-01

The fork is renamed **OscarWatch-OQ4** (`sideout909/OscarWatch-OQ4`); origin now
points there and upstream push remains disabled. The root README focuses on OQ4,
links the approved extended abstract and includes an actual-encoder waveform/
spectrogram comparison. O means OSCAR, Q means QPSK, 4 means four phase states
(two bits per symbol). The original local review draft in Documents is untouched.
The original upstream README is preserved at `docs/UPSTREAM_README.md`.

Read [paired evaluation](OQ4_PAIRED_EVALUATION.md). Both receive components were
verified live through a shareable `pulse` source and both production decoders
passed concurrent mixed-audio tests. The audio probe exposed a missing saved
Decoder Lab default source; a per-process explicit source worked. No global audio
routing/default changes, loopback creation, RF transmission or captured audio save.

OQ4 can now RF-transmit with FT4 left open **receive-only**. FT4 TX/Tune must be off;
a temporary external-radio lease suppresses its CAT steps/hold and TX arming during
OQ4 transmission, leaving capture and decode running. Its idle PTT adapter is freed,
then CAT timing resumes after OQ4 cleanup. A shared per-radio `PttKeyer` ownership
guard prevents cross-key/unkey races. MainViewModel now permits opening FT4 while
OQ4 is active. The application builds and all **155 targeted tests pass**. Fake-radio tests cover ownership, cancellation/failure and passive
window cleanup; real PTT/GUI operation still needs operator verification.

Publishing source requires commit/push, not a GitHub build. Existing CI targets
`main`; no release packages are created here. The experiment branch is intended as
the fork landing page, leaving main as the upstream baseline. Unrelated terminal,
AX.25 and theme changes remain local and excluded from commits.

## Latest continuation — SNR and phase feedback, 2026-11-01

Read [OQ4_SNR_AND_PHASE_TRACKING.md](OQ4_SNR_AND_PHASE_TRACKING.md) for implementation,
calibration limits, file inventory and reproduction. Code-assisted phase tracking
now runs after ordinary BP and OSD, using native LDPC extrinsic information and
bounded corrections. The optional native API is rebuilt for Linux x64; older
native libraries skip feedback without breaking regular decoding.

Reports now use all 87 data symbols after CRC and original observations before
feedback, with a finite-sample bias correction. The window still marks SNR `~`:
it is approximate data SNR in 2500 Hz, excluding the louder reference tones.
The app builds and all **146 targeted tests pass**, including waveform and Gaussian/gain calibration. No extra full-waveform or
recording packets were found: 92/144 and 19/19 remain intact; 160 negative windows
remain silent. Controlled symbol-domain phase tests improve 254/300 to 287/300
(+0.5 rad excursions) and 254/300 to 278/300 (-0.5 rad), with no wrong payloads.
These gains do not establish a satellite or FT4 performance advantage.

The user authorized committing/pushing OQ4 to their own origin fork on the
`oq4-experiment` branch. Upstream push is disabled. Unrelated terminal/AX.25/theme
edits stay local. Raw recordings and generated trial audio remain ignored.
Earlier "next priority: code-assisted phase tracking" entries are now historical.

## Latest continuation — weak-signal research, 2026-11-01

Read [OQ4_WEAK_SIGNAL_RESEARCH.md](OQ4_WEAK_SIGNAL_RESEARCH.md). Online primary-source
research led to a bounded OSD fallback after the unchanged fast BP path. The new
`Oq4OrderedStatisticsDecoder` uses the same full (174,91) LDPC code, a conservative
preamble/metric gate, 16 reprocessed basis bits and at most 12 soft hypotheses.
Only the closest codeword is CRC checked; no message hints are supplied.

Paired independent-waveform simulation: **87/144 → 92/144**, no lost successes or
wrong payloads. Holdout **41/72 → 43/72**. Both before/after gave **0/160** synthetic
noise/interference windows. These are small regression results, not an FT4 win or
proven rare false-alarm rate. Calibrated-LLR and longer-preamble prototypes produced
no extra packets and were removed. **135 targeted tests pass**, and recording
replay retains **19/19 expected signals**. No TX/window behavior or waveform changed
in this research pass.

The generator/scorer and machine-readable results are saved in the repo; raw
trial audio and frozen baseline sources are under ignored `testdata/oq4/`.
Next priority is code-assisted phase tracking with proper soft/extrinsic decoder
feedback, larger held-out fading/phase-noise tests and a fair FT4 comparison.
Do not claim general superiority from the current five added synthetic packets.

## Latest continuation — 2026-11-01: FT4 window/PTT deep review

The operator still had intermittent failure to key PTT and requested that we
understand FT4's complete operating path and carry it into OQ4. Read
[OQ4_WINDOW_FT4_PARITY.md](OQ4_WINDOW_FT4_PARITY.md) for the findings and exact scope.

OQ4 now has a dedicated `Oq4TransmitRunner`, shared `PttKeyer` adapter preparation,
prebuilt device-rate audio with the existing 400 ms silent lead-in, nonfatal CAT
Doppler timeout handling, and cancellation/cleanup ordering covered by fake-hardware
tests. Power reads no longer happen on the UI keying continuation. The configured
separate RTS adapter exists and is permitted, but was not opened/keyed here.

The window now reuses FT4's actual QSO sequencer and decode-row highlighting/template.
CQ, reply selection, reports/73, auto reply, Skip RRR, pounce, manual prompts,
watchdog, logging and close/Halt behavior are connected. Colors requested earlier
are implemented. Approximate preamble-based SNR is marked `~`; calibration remains
future work. FT4-specific AP/depth/echo/recording settings are not OQ4 algorithms.

Build and **124 targeted tests pass**; all **19 recorded signals** still pass replay.
No hardware/RF transmission, contact log/upload, commit or push was performed.
Restart the source build for operator validation. If keying still fails, collect
exact status and new `OQ4` log lines; preparation, PTT request and playback are now
separately reported. Continue pass/QSO data collection without changing the waveform.

Earlier entries below are historical and superseded where noted.

## Latest continuation: recorded low-band packets recovered

Read [OQ4_RECORDING_ANALYSIS.md](OQ4_RECORDING_ANALYSIS.md) first. The later recording
now yields eight CRC-valid low-band packets (390–910 Hz); six are new compared with
the previous decoder. Thirteen identical clips give 13 → 19 decoded signals.
Strong-signal acquisition tapering and preamble-only timing improved recovery.
Live OQ4 capture now uses the existing anti-alias polyphase resampler.

The high marked traces follow `12000 - 4*strong_frequency` in the original WAV;
do not keep interpreting their +67/+41/+17 Hz/s as verified satellite Doppler.
The low-band decoded packets are the stronger downlink candidates, consistent with
the user's listening observations, but CRC alone does not establish the RF path.

The user also reported TX arming and then turning off at the last moment. Slot
preparation now starts earlier, and timing overruns skip/retry rather than disarm.
This needs operator confirmation on hardware; no RF/PTT was exercised here.
45 targeted OQ4/resampler tests passed. The replay tool verifies 19 recorded outputs.

The live receiver now queues UTC-slot snapshots while a decode is busy (four
pending slots maximum; sustained overload is reported).

Next: preserve these recording regressions, validate live receive/TX behavior on
the operator's setup, collect the user's further pass/QSO audio,
and address the previously requested FT4-style message colors. The full format is
unchanged. Do not push or overwrite unrelated work. The original marked image is
untouched; a separate low-band zoom is in Documents.

The earlier handoff below records prior context and is superseded where noted.

## Where we stopped

The operator is finished for the night. Their latest observation: using Decoder Lab at **signal −40 dBFS RMS, broadband noise −23 dBFS RMS, starting drift 0 Hz/s, ending drift −6 Hz/s**, OQ4 decoded approximately **60–70%** of the time and FT4 approximately **10%**. These are informal operator estimates; no counted trials or seeds were recorded. Treat this as a promising isolated observation, not a claim of general superiority. The operator wants to continue improving and evaluating OQ4, especially for short satellite passes, while retaining terrestrial usefulness.

The detailed format, implementation, test conditions, and limitations are in [OQ4_PROTOCOL_AND_DESIGN.md](OQ4_PROTOCOL_AND_DESIGN.md).

## Workspace and repository safety

- Repo: `/home/lschaar/Projects/OscarWatch/OscarWatch-Tracker`; branch: `oq4-experiment`.
- `origin` is the operator's fork, `https://github.com/sideout909/OscarWatch-Tracker.git`. `upstream` is `https://github.com/magicbug/OscarWatch-Tracker`, and upstream pushes are disabled. The operator was concerned about pushing experiments to the original author's repo. Do not push to upstream.
- Work remains **uncommitted and unpushed**; inspect `git status` before changing anything. The working tree also contains unrelated existing terminal, AX.25, theme and other changes; preserve them. Untracked OQ4 source and docs are part of the current work. Do not assume a clean checkout or reset it.
- The operator has authorized using their fork for experiments, but no new push was requested tonight.

## Run and route

```bash
cd /home/lschaar/Projects/OscarWatch/OscarWatch-Tracker
/home/lschaar/.dotnet/dotnet run --no-build --project OscarWatch/OscarWatch.csproj
```

Build when needed with `/home/lschaar/.dotnet/dotnet build OscarWatch/OscarWatch.csproj`. The last build and targeted tests completed before this handoff. Restart the app after rebuilding.

Local test app: `/home/lschaar/Documents/OscarWatch Decoder Lab/Start Decoder Lab.sh`; launcher: `/home/lschaar/Documents/OscarWatch Decoder Lab.desktop`; its `README.txt` explains routing. It supports FT4 and OQ4, signal/noise RMS dBFS, frequency, even/odd/both UTC slots, one-shot or repeat, and linearly changing drift rate (a quadratic frequency path). It creates its own PipeWire/PulseAudio source while running and removes it on normal exit. It does not deliberately change system defaults. OscarWatch's PortAudio list on this machine exposes `pulse`, not the virtual source by name: select `pulse` in OscarWatch, then use the lab's **Audio routing** button and route the active OscarWatch Recording stream to **OW Decoder Lab Input**. Recheck routing when the stream restarts. Do not leave loopback routing applied to the user's radio setup after testing; the operator has had routing interference before.

The lab contains a snapshot of `oscarwatch_ft8.so` plus its own Python implementation of OQ4 modulation. Keep the copy and Python waveform synchronized if the wire format or native encoding changes. No RF or PTT is involved in the lab.

## Current OQ4 receiver

- `OscarWatch/Ft4/Oq4Search.cs` searches 200–3000 Hz for pairs of 160 ms reference tones, ranking up to four candidates. The waterfall selection is not required for reception. Candidate pairing now allows approximately ±205 Hz over the packet, prioritizes separated frequency regions, and uses a heuristic threshold; crowded bands, stronger interference and wider shifts still need work.
- `OscarWatch/Ft4/Oq4Decoder.cs` correlates the known preamble, tries straight and curved drift models, filters/demodulates QPSK, uses pilot and smoothed/constant phase hypotheses, and submits soft bits to FT4-derived LDPC and CRC decoding. `Oq4Window.axaml.cs` processes overlapping four-second audio windows off the UI thread and deduplicates detections by time/frequency.
- At present the receiver is a prototype: search is candidate-limited; weak-signal results vary with noise realization; multipath, multiple stations, clock error, oscillator error and false positives need systematic testing; normal and spectrally inverted QPSK are both attempted. OQ4 uses 3.75-second UTC slots and an approximately 2.523-second packet, so account for radio turnaround and capture/processing latency.
- Focused tests: `/home/lschaar/.dotnet/dotnet test OscarWatch.Tests/OscarWatch.Tests.csproj --no-restore --filter FullyQualifiedName~Oq4 -v:q`. The last run passed **17 tests**, including simultaneous strong and weak stations, ±68 Hz/s drift, inverted QPSK, and noise rejection. Other repo tests emitted existing compiler/analyzer warnings during the build.
- A scripted single noisy realization at −40/−22 dBFS decoded with end drift −4 and −14 Hz/s (starting at zero), but a small sweep at 500/1500/2300 Hz and three seeds per condition produced uneven results. Do not infer reliable weak-signal performance from a single decode.

## Real pass reference

The recording is `/home/lschaar/Documents/RS44-OQ4-20261031-052447-UTC.wav`. User made an FT4 QSO with N8HRZ; the antenna path was obstructed by trees and a barn. A preliminary fit to the **received station's** two known FT4 messages gave about **−14.5 and −13.5 Hz/s in recorded audio**, respectively, near the pass peak. This is approximately −70 Hz over a five-second FT4 burst. OscarWatch's log reported **−4 Hz/s residual beyond predicted downlink correction**; this is different from the raw recorded-audio slope. Do not interpret these as universal RS-44 drift, or as proof of curvature within the short bursts.

A recorded OQ4 burst decoded as `CQ K8OTY EN73` with CRC passing, including with the latest wideband search. The user did not see a confirmed satellite echo; TX bleed is a plausible source, so do not claim that OQ4 was decoded via RS-44.

## Useful next work

1. Ask what the operator wants to prioritize, unless they give a new specific task. For a meaningful comparison, automate a **counted, seeded** sweep for both FT4 and OQ4 with equal active signal RMS, explicit noise bandwidth, multiple frequencies/messages/drift profiles, and report number sent, CRC-valid decodes and uncertainty. Ensure both modes have fair timing and search budgets; they have different burst lengths and decoders. Include drift near the measured −14 Hz/s and scenarios with changing rate.
2. Investigate weak-signal phase tracking: short pilot pairs are noisy and the current heuristics sometimes miss packets even with known timing/frequency. Evaluate calibrated soft bit likelihoods, improved pilot/data-aided tracking, acquisition thresholds and CRC false-accept rate before changing the wire format.
3. Measure live decoder runtime, overlapping-window behavior and candidate starvation with weak and strong simultaneous signals. Improve acquisition bandwidth and slope bounds if measurements justify it. Keep changes and tests independently reviewable.
4. Eventually validate occupied RF bandwidth, linearity, real satellite return, and interoperability. There is no existing external OQ4 implementation; FT4 software will not decode OQ4.

## UI follow-up requested

Match the OQ4 activity/decode window’s color scheme to the existing FT4 window, including CQ calls, signal reports, and other message types. Inspect and reuse the existing FT4 color and message-classification behavior so the two windows remain visually consistent. This is a note for a future session; no UI changes were requested tonight.


## Marked later RS-44 recording

The operator marked `/home/lschaar/Documents/RS44-OQ4-20261031-070511-waterfall-notes.png` and identified the weaker traces as signals they heard on the downlink. The associated audio is `/home/lschaar/Documents/RS44-OQ4-20261031-070511-UTC.wav`. At 10:56 elapsed its initial and final references occur near 1764 and 1923 Hz, roughly +67 Hz/s; later marked bursts show roughly +41 and +17 Hz/s. A much stronger simultaneous trace can fill the detector’s candidate list.

The OQ4 search was widened to about ±205 Hz, prioritizes separated candidates, adjusts frequency for the reference-tone midpoint, and attempts spectrally inverted as well as normal QPSK. Generated tests exercise ±68 Hz/s and inverted fast-drifting packets alongside stronger signals. In offline checks the new search found marked weak tone pairs, but **no CRC-valid message was recovered from those marked weaker traces**. `CQ K8OTY EN73` was decoded on the strong trace; do not conflate it with a confirmed downlink decode. More weak-signal and distorted-channel work is needed.
