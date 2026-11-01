# OQ4 operating window and PTT review — 2026-11-01

Update 2026-11-01: [paired-operation changes](OQ4_PAIRED_EVALUATION.md) supersede the
older requirement below to close FT4 before OQ4 RF transmission. FT4 may now stay
open receive-only, with a temporary CAT/TX lease and shared PTT ownership guard.
[SNR/phase work](OQ4_SNR_AND_PHASE_TRACKING.md) also supersedes the preamble-only
SNR description below. Historical verification counts refer to that earlier pass.


## Findings

The FT4 window is a view over `Ft4ViewModel`, `Ft4ModemService`, the shared
`PttKeyer` and `Ft4AudioService`. Its reliable timing depends on work that was
missing from the first OQ4 window: preparing the audio before the slot, a dedicated
slot waiter, keying before audio, and treating a late CAT Doppler write as lower
priority than PTT. Copying the controls alone did not carry those behaviors over.

OQ4 previously performed repeated CAT power checks in `ValidateTransmission`,
including immediately before keying, on the UI continuation. A power read, serial
adapter open or dispatcher delay could consume the keying deadline. The compulsory
250 ms Doppler step could also prevent keying altogether. Last session's earlier
preparation/retry patch did not remove all of these dependencies.

A further arming bug encoded an empty message box before the sequencer could
build the default CQ shown by its watermark. Enable TX now uses FT4's ordering:
build the station CQ first, validate the resulting message, then arm. Invalid
messages halt the sequencer instead of leaving a hidden armed state.

The operator's current settings use a **separate serial adapter, RTS, non-inverted,
200 ms lead and 100 ms tail**. At review time its configured by-id path existed,
resolved to `/dev/ttyUSB1`, and allowed read/write access. It was not opened or
keyed during verification. No physical cause or hardware success is claimed from
that permissions check.

## Implemented transmit path

`Oq4TransmitRunner` now runs on a dedicated long-running worker. Its injected
`IOq4TransmitHardware` seam lets tests execute the same ordering and cleanup logic
without serial ports, audio devices or RF:

1. Validate the station/ownership/transponder policy; check RF power once during
   preparation, off the UI thread. Honor the existing auto-lower-power setting.
2. Encode OQ4, prepend the existing 400 ms slot silence, and prepare gain-scaled
   device-rate audio through the shared audio service. Opening/resampling is done
   before the deadline, rather than after keying.
3. Prepare a separate PTT adapter while leaving the selected line idle. Its open
   latency is paid before the key instant. CAT, CAT-port RTS/DTR, VOX and Manual
   use the same existing `PttKeyer` methods as FT4.
4. Wait on the worker until the inter-burst silence before doing the CAT step.
   A step that times out reports its status and lets PTT proceed, like FT4. It is
   not a guarantee that a slow separate-PTT radio finished its tuning write.
5. Key at the slot boundary, or earlier when the configured PTT lead needs it.
   Play the prepared audio at the boundary; the actual OQ4 reference/data begins
   400 ms into the slot. An actually missed slot is skipped while remaining armed.
6. Wait for audio completion and the configured tail, stop audio, unkey, release
   the separate port and release any held Doppler gate. Failures and cancellation
   follow the same cleanup path.

Halt and the worker share a lock around key/play operations. A cancelled or halted
preparation cannot subsequently key after the operator has pressed Halt. Tune uses
the same worker and cleanup path. A failed attempt leaves the controls usable for
another attempt. No radio operation depends on a UI dispatcher continuation.

FT4 and OQ4 retain separate state/audio ownership. OQ4 reports that FT4 must be
closed before OQ4 RF transmission; audio-only tests remain usable while FT4 is
listening. Opening FT4 while OQ4 is active activates OQ4 rather than starting a
competing modem. Monitoring alone does not assert PTT.

## FT4 operating behavior brought over

- Reuse the **actual `Ft4QsoSequencer`**, not a second implementation of CQ/contact
  message logic. Own callsign/grid come from station settings.
- Selecting a valid RX decode answers on the opposite **3.75-second OQ4** parity.
  Own echoes/TX rows do not start contacts.
- CQ, report, 73, Skip RRR, Auto reply, recent-caller pickup and call/grid pounce.
  Report/73/manual-log controls become available for an actual partner.
- Hold TX frequency, tuning selection, the configured TX watchdog and reply-based
  watchdog reset. Halt also clears pounce. Tune, close confirmation and manual
  key/unkey prompts follow the FT4 operating pattern.
- Use `Ft4DecodeRowViewModel`, the FT4 row template, `Ft4DecodeHighlight` and the
  configured calling-me/reply/new-call/new-grid/CQ/TX colors. Worked calls/grids
  load from the existing logbook; completed contacts update highlighting.
- Show RX, TX and own-echo rows, DT, frequency and estimated SNR; newest rows scroll
  into view. Right-clicking a row does not accidentally answer it. Activity save
  and clear remain available.
- Manual and completed-contact logging use the existing logger with mode **OQ4**,
  following the existing app's configured logbook/upload behavior. No contact was
  logged or uploaded during this work.
- Pending TX only retargets when its message, frequency or parity actually changes.
  Receiving unrelated messages does not cancel the schedule. A changed message is
  not credited as sent merely because an earlier queued message finished.
- PTT failures remain visible instead of being immediately overwritten by an RX
  status. OQ4 TX phases and PTT preparation/key requests now appear in Serilog logs.

The symbol waveform, coding, references, 2.523-second burst and 3.75-second slot
are unchanged. Preparing a buffer with silence changes scheduling, not the packet.

## SNR and feature boundaries

OQ4 now estimates SNR from the known, phase-corrected preamble after a CRC-valid
message. It uses a 2500 Hz reference noise bandwidth and is marked `~` in the
window. The estimate supplies the reused sequencer's reports. It has not been
calibrated against real radios, fading or an independently measured noise source;
it must not be used to claim sensitivity improvements over FT4.

FT4 decoder-specific controls in the shared settings dialog (AP/decode depth,
FT4 echo calibration and FT4 slot recording/reporting options) do not become OQ4
algorithms by reusing the window. OQ4 retains its own experimental decoder. These
were deliberately not represented as completed OQ4 features. The shared settings
that OQ4 consumes are station identity, audio devices/level, PTT, RF-power policy,
TX audio Doppler, display/colors, blocked calls and operating preferences.

A confirmed over-the-air QSO and physical RTS/PTT validation are still required.
The user will collect more pass audio and arrange another station. The already
recovered lower-band packets remain regression evidence, not proof of their RF path.

## Verification

```bash
/home/lschaar/.dotnet/dotnet test OscarWatch.Tests/OscarWatch.Tests.csproj \
  --no-restore --filter 'FullyQualifiedName~Oq4|FullyQualifiedName~PttKeyerTests|FullyQualifiedName~RigControllerFt4PttTests|FullyQualifiedName~Ft4QsoSequencerTests|FullyQualifiedName~Ft4DecodeRowHighlightTests|FullyQualifiedName~Ft4TransmitEncodeTests' -v:q

/home/lschaar/.dotnet/dotnet run --project tools/Oq4Replay -- \
  --verify tools/Oq4Replay/rs44-20261031-070511.expected.json \
  testdata/oq4/rs44-070511/*.f32
```

**124 targeted tests passed.** This includes the shared FT4 sequencing/highlight,
PTT and encode checks and new OQ4 cases for adapter preparation latency, long PTT
lead, CAT timeout fallback, missed slots, cancellation before/after key, Halt while
waiting, key/playback failures, recovery on a subsequent attempt, Tune, audio-only
operation, default-CQ arming/invalid-message cleanup and SNR gain invariance. The recording replay passed all **19 expected
signals in thirteen clips**. The build succeeds. Existing unrelated test-project
analyzer warnings remain. GUI/hardware operation was not exercised by these tests.

Restart the locally built app:

```bash
cd /home/lschaar/Projects/OscarWatch/OscarWatch-Tracker
/home/lschaar/.dotnet/dotnet run --no-build --project OscarWatch/OscarWatch.csproj
```

If PTT still fails, retain the exact OQ4 status text and the OQ4 lines from
`~/.config/OscarWatch/logs/oscarwatch-YYYYMMDD.log`; they now distinguish preparation,
key request and audio start. No code was committed or pushed as part of this work.
