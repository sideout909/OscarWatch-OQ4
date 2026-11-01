# Offline OQ4 recording replay

This Linux x64 developer tool compiles the **production** decoder/search sources,
loads the repo's native codec, and prints JSON lines. It opens no audio devices,
UI, or radio controls. Requires .NET 10; the WAV extraction helper requires Python
and NumPy. No message is supplied to the decoder; expectations only check outputs.

From the repository root, prepare the regression clips:

```bash
python tools/Oq4Replay/prepare_audio.py \
  "$HOME/Documents/RS44-OQ4-20261031-070511-UTC.wav" \
  --starts 655.5 663 678 723 738 753 760.5 828 835.5 843 865.5 873 880.5 \
  --output testdata/oq4/rs44-070511

dotnet run --project tools/Oq4Replay -- \
  --verify tools/Oq4Replay/rs44-20261031-070511.expected.json \
  testdata/oq4/rs44-070511/*.f32
```

The checked expectations include eleven strong-signal decodes and eight low-band
CRC-valid decodes. Frequency bounds distinguish the two even though they contain
the same message. These are recording regression checks, **not proof of the RF
propagation path**. WAV and extracted audio remain in ignored `testdata/`; the
original marked image is not modified. The helper preserves absolute clip times
in a manifest and applies an anti-alias low-pass before downsampling.

For other mono PCM16 recordings at 12/24/48 kHz, select four-second clip starts
that contain complete packets and run without `--verify`. Inputs to the executable
are little-endian float32 samples, mono, 12 kHz. Do not simply discard samples
from high-rate WAV files: high-frequency harmonics can alias into the search band.
The executable reports candidate frequencies, decoded messages and CPU wall time.
It exits nonzero when the codec cannot load or a requested expectation is absent.

The tone-pair scores are heuristic detection scores, not calibrated SNR. Output
`FrequencyHz` is the fitted carrier near packet start. No change to the OQ4 wire
format is needed to use this receiver improvement.

## Seeded weak-signal evaluation

The local Decoder Lab supplies an independent Python waveform implementation.
Its module and adjacent native library must exist; the default is the previously
created app in Documents. No audio routing or device is opened when generating:

```bash
python tools/Oq4Replay/generate_benchmark.py \
  --output testdata/oq4/research-20261101

dotnet run --project tools/Oq4Replay -- \
  testdata/oq4/research-20261101/signal-*.f32 > /tmp/oq4-signal-results.jsonl

dotnet run --no-build --project tools/Oq4Replay -- \
  testdata/oq4/research-20261101/noise-*.f32 > /tmp/oq4-noise-results.jsonl

python tools/Oq4Replay/score_benchmark.py \
  testdata/oq4/research-20261101/manifest.json /tmp/oq4-signal-results.jsonl \
  --output testdata/oq4/research-20261101/signal-summary.json
```

Use `--baseline before.jsonl` to score paired, identical case sets. Expected
messages only enter the scorer. The generated manifest records 2500 Hz SNR,
signal RMS, frequencies, drift, independent noise seeds and discovery/holdout split.
It also hashes the local waveform source and records Python/NumPy versions.
This tests the OQ4 receiver, not relative FT4 sensitivity. See
[the research notes](../../docs/OQ4_WEAK_SIGNAL_RESEARCH.md) for results and limits.


Run the deterministic symbol-domain phase-feedback diagnostic with
`~/.dotnet/dotnet run --project tools/Oq4Replay -- --phase-benchmark`.
It isolates LDPC-assisted tracking after synchronization, without opening audio or
PTT devices. It is not a full-waveform or satellite-channel benchmark. See
[the SNR/phase report](../../docs/OQ4_SNR_AND_PHASE_TRACKING.md) for assumptions.
