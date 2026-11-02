# OQ4 / FT4 operating-window audit (1.1.6-OQ4, 2026-11-01)

The FT4 operating window is the UI reference. The operator specifically identified
the controls and bottom status strip as glitchy, including when changing TX audio
frequency. This audit compares `Ft4Window`, `Ft4ViewModel`, the relevant modem
operations, and `Oq4Window`; it does not change the waveform or decoder.

## Changes

- Rebuilt OQ4's XAML from FT4's visual tree: same control order, spacing, margins,
  styles, station banner, tooltips, accessibility labels, and decode columns
  (`76,48,48,72,*,52`). Header font sizes now follow the configured decode font.
  OQ4 retains its title and an SNR tooltip describing its data-only estimator.
- Bottom strip now uses FT4's percentage progress and slot-start UTC / elapsed /
  even-or-odd clock format, including the same GPS/PC clock-source messages.
  TX-period highlighting tracks a prepared/current or actually keyed slot rather
  than marking every selected-parity slot as TX, including skipped slots.
- Buttons refresh their enabled/active appearance immediately. Tune can replace
  armed packet transmission as it does in FT4; CQ and standard messages can replace
  Tune. These transitions wait for the previous worker's cleanup before starting
  another transmission. Halt invalidates a pending transition so it cannot re-arm
  after the operator stops it. Radio ownership, power checks and PTT cleanup stay
  enforced.
- TX audio edits update the brackets immediately but coalesce prepared-packet
  updates until 150 ms after the last edit. The waiting worker refreshes its PCM
  in place while keeping the PTT adapter prepared and the original slot. Frequency
  edits do not cancel the worker, release PTT or re-arm TX. Audio updates freeze
  250 ms before keying; later changes apply to the following packet. Already keyed
  packets are not rewritten mid-transmission. A slow rebuild which misses the
  deadline skips the slot without keying late. Message/parity changes still use
  cancellation and retain an existing slot only when enough lead time remains.
  Fresh arming retains its original preparation margin.
- Receive-driven sequencer refreshes respect pending frequency-edit coalescing.
  A stale worker's queued status cannot overwrite a newer transmission's status;
  cancellation and ordinary background decodes no longer replace Halt/CQ/pounce
  messages with generic processing text. Standard actions use FT4's localized
  status vocabulary with the OQ4 mode name.
- Hold TX is applied immediately to waterfall clicks. Clearing Hold TX snaps TX
  to the RX bracket. During a contact the RX bracket follows the partner's newest
  received frequency using the same helper as FT4.
- The operating context menu now matches FT4 (pounce only). The previous extra
  prepare-audio / audio-only test actions are removed from this window.
- Pounce remembers the last target and displays the same armed target label and
  accessibility text as FT4. Context-menu opening validates the selected station.
- Newest rows scroll to the top after layout like FT4. Worked-call/grid highlights
  refresh when another window logs a contact. Manual-log availability follows QSO
  phase, and saved activity uses the shared chronological formatter with SNR,
  frequency and TX/Echo/RX fields, labelled OQ4.
- Station readiness gates initial receiver start as in FT4; later completion of
  station setup starts capture without repeated automatic retries of a failed
  audio device. Tune audio does not start merely because its preparation is active.

## Validation

- App build succeeded.
- 562 FT4/OQ4-related tests passed, including new layout/control parity tests and
  tests for retaining/rejecting an edited transmission's slot safely. Five new
  runner tests cover live frequency refresh without extra PTT cycles, multiple
  edits, late edits affecting only the next packet, Halt during refresh, and a
  rebuild exceeding the slot deadline.
- A separate Avalonia Headless smoke check instantiated the actual OQ4 window
  without opening it. Checks covered numeric TX edits, Hold TX bracket
  behavior, header fonts, button state, frequency updates without cancellation,
  waterfall selections while armed, and an old worker's
  queued status arriving after ownership changed. All passed. Service stubs reject
  unexpected service access; capture, playback and radio connection were not
  started. Temporary harness and results: `/tmp/oq4-window-ui-check/`.
- No physical PTT/RF test, desktop screenshot comparison or new satellite-pass
  test was performed. The operator should still verify feel while changing
  frequency, switching CQ/Tune/Halt and watching the slot strip.

## Deliberate boundaries

The engines remain different: OQ4 has 3.75-second slots, a 0.4-second start offset,
its own waveform, reference acquisition, phase tracking and SNR estimator. FT4's
AP decoder, echo-calibration decisions, recording/reporting features and predicted
receive dechirping are not silently substituted into OQ4. Both still share the FT4
settings dialog/store; some decoder-specific settings apply to FT4. OQ4 retains
its existing requirement to Halt TX before editing settings. These are not claims
of full modem-service feature parity.

Prepared for 1.1.6-OQ4. The operator subsequently authorized pushing these changes
and starting the fork’s release build. The installed binary, radio settings and
audio routing were not changed by this audit.

## Reference tones (confirmed from the production decoder)

`Oq4Search.Find` looks for both opening and closing references across the audio
band. `Oq4Decoder` uses their frequency difference and known time separation to
estimate average drift, then searches linear and curved trajectories and refines
with the preamble/pilots. CRC/FEC validates the recovered message. The tone pair
is an acquisition aid, not proof of a payload decode or satellite propagation.
