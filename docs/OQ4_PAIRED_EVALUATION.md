# Paired FT4/OQ4 evaluation and concurrent-window operation

Updated 2026-11-01. The fork is now [OscarWatch-OQ4](https://github.com/sideout909/OscarWatch-OQ4).

## What works together

Both windows can be open, capture audio and run their respective decoders at the
same time, provided the input device/backend permits multiple capture streams.
They use separate `Ft4AudioService` instances, queues and receive buffers. Reading
one does not drain the other. They share the saved FT4 audio configuration, station
settings, native message/FEC foundations and radio controller.

**OQ4 may now transmit while FT4 stays open for receive.** Before arming OQ4,
Halt FT4 TX and leave FT4 Tune off. OQ4 rejects an FT4 transmitter that is armed,
playing, keyed or still cleaning up a TX/tune task. Opening FT4 while OQ4 is active
is now permitted; the menu no longer redirects back to OQ4.

For each OQ4 RF transmission, an external-transmission lease in `Ft4ModemService`:

1. Checks that FT4 is not transmitting or finishing a transmission.
2. Releases an idle FT4 separate-PTT port so OQ4 can open it.
3. Temporarily yields FT4's CAT slot hold and suppresses FT4's periodic CAT steps,
   TX arming and decode-driven automatic replies while OQ4 owns the radio.
4. Leaves FT4 capture, waterfall and decoding running. FT4 receive audio-Doppler
   correction is disabled while its own dial-hold assumption is suspended.
5. Restores FT4's normal CAT timing after the OQ4 runner has stopped audio,
   unkeyed, released its PTT adapter and released its own Doppler hold.

The existing OQ4 slot and transmit waveform are unchanged. A process-wide,
per-radio PTT ownership guard also prevents a second keyer from reserving/keying
that radio, or accidentally unkeying the active modem from a passive window's
Halt, close or force-off path. Ownership is released on unkey, cancelled
preparation, preparation failure and disposal. It applies to CAT, handshake,
separate-port, VOX and manual PTT paths. Its radio identity is the shared
`IRigController` instance; separate OscarWatch processes cannot share this guard.

This permits monitoring OQ4's waveform in the FT4 waterfall while transmitting
OQ4. **FT4 does not decode OQ4.** For paired decode measurements, provide both valid
waveforms rather than expecting one receiver to recognize the other's modulation.
No simultaneous RF transmission by both modems is supported.

## Live capture verification on this computer

A receive-only tool used the actual production audio service, not a substituted
capture implementation. It opened two streams on `pulse`, received 132,608 samples
in each over 3.009 seconds at 44,100 samples/s, stopped the first, and received
45,056 more samples in the second over approximately 1.016 seconds. It opened no
playback stream, asserted no PTT and saved no microphone/radio audio.

The initial `default`, `pipewire` and unspecified-source `pulse` attempts failed
before the first capture stream could open. At inspection, WirePlumber's saved
configured source named the absent `ow_decoder_lab_input`; explicitly choosing
an existing physical source for the probe process allowed the two captures.
No global default source, default sink, loopback device, saved audio setting or
existing recording process was changed or stopped.

Successful command on this machine:

```bash
PULSE_SOURCE=alsa_input.pci-0000_00_1f.3.analog-stereo \
DOTNET_ROOT="$HOME/.dotnet" \
~/.dotnet/dotnet run --project tools/ModemPairProbe -- --input pulse
```

`PULSE_SOURCE` applies only to that command. Choose the appropriate existing Lab,
monitor or radio source for your own test. It must exist when the process opens
capture. Do not substitute this physical-input example when you intend to receive
from Decoder Lab. The probe also supports `--list`.

Direct hardware devices may be exclusive. Windows/macOS sharing behavior and
USB radio device behavior were not verified here. A successful `pulse` check does
not establish that every listed device can be opened twice.

## Decoder and ownership regression tests

Two Linux tests concurrently call the production FT4 and OQ4 receivers on one
7.5-second noisy buffer containing different simultaneous FT4/OQ4 messages. One
uses FT4 at 800 Hz and OQ4 at 2200 Hz; the other swaps the frequencies. Both decode
their own message, neither reports the other mode's payload, and the buffer is
unchanged afterward. This checks concurrent native/managed decode paths and
separate mode recognition, not relative weak-signal sensitivity.

Fake-radio tests cover external ownership blocking FT4 arming, idempotent release,
PTT reservation conflicts, passive-window unkey/close behavior and failed
preparation releasing ownership. The existing transmit-runner tests still check
cancellation, scheduling, preparation and cleanup. The application builds and all **155 targeted tests pass**. No hardware PTT or GUI-driven
RF test was performed. Production window paths were inspected in source.

Machine-readable capture/decode evidence:
[2026-11-01-paired-receive-results.json](oq4-evaluation/2026-11-01-paired-receive-results.json).

## Suggested receive comparison

1. Open FT4 and OQ4, keep both TX states off and disable Auto Reply. Use a
   disconnected rig for a decoder-only test.
2. Select a shareable input in their common audio settings. Restart/reopen capture
   in both after a settings change; changing one dialog does not guarantee the
   other already-open stream has switched devices.
3. Route both streams to the same existing Decoder Lab/recording source. Confirm
   both waterfalls receive it and neither reports an audio-open failure.
4. Use the same structured message and stated power/noise definitions. Preserve
   noise seeds and channel/drift profiles. FT4 and OQ4 have different packets and
   slot grids, so align playback appropriately for each; simultaneous unrelated
   tones are an interference test, not a clean sensitivity comparison.
5. Count generated packets, correct messages, missed packets and wrong outputs.
   Save activity from each window separately; retain trial metadata and the
   waveform/audio if needed for blind offline scoring.
6. State FT4 AP/depth, drift correction, decoder budgets and clock settings. With
   extra CPU load from both receivers, note queue overruns and decode latency.
7. Separate packet decodability from completed-QSO speed. Report both if the aim
   is more successful exchanges during a short satellite pass.

An OQ4 SNR label is approximate data SNR excluding its louder reference tones.
Do not treat the two modes' displayed SNR values as a calibrated head-to-head
measurement. No sensitivity advantage or disadvantage is established by the
concurrency checks.

## Publishing this fork

Committing and pushing source publishes it to GitHub; a GitHub Actions build is
not required. The fork uses `oq4-experiment` as its development/landing branch,
leaving `main` as the upstream baseline. Its existing CI workflow only targets
`main`, so an experiment-branch push alone does not automatically run that CI.
Local build/test evidence is recorded separately. Downloadable OQ4 release
packages require a deliberate publish workflow/tag and native libraries for each
supported platform; no release packages are created as part of this update.


## Adapter lifecycle update — 2026-11-01

Repeated scheduled OQ4 transmissions now retain the serial PTT adapter open at its
idle line level between successful bursts. This removes routine close/reopen cycles
near the RX slot, a plausible trigger for brief USB RTS/DTR pulses. It does not hold
PTT asserted. Halt, close, cancellation/failure and nonrepeating operations release
the adapter; Halt OQ4 before switching to FT4 TX. Unkey failure forces the adapter
closed. This supersedes the earlier per-burst adapter-release description above.
Physical behavior must be checked on the operator's adapter; tests use fake hardware.
