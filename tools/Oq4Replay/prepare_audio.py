#!/usr/bin/env python3
"""Extract anti-aliased 12 kHz float clips from a mono PCM16 pass recording.
Requires numpy. No audio devices or radio controls are opened.
"""
import argparse
import json
from pathlib import Path
import wave
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('wav', type=Path)
parser.add_argument('--starts', nargs='+', type=float, required=True)
parser.add_argument('--duration', type=float, default=4)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if args.duration <= 0 or any(t < 0 for t in args.starts):
    parser.error('Use nonnegative starts and a positive duration.')
args.output.mkdir(parents=True, exist_ok=True)
with wave.open(str(args.wav), 'rb') as wav:
    rate = wav.getframerate()
    if wav.getnchannels() != 1 or wav.getsampwidth() != 2 or rate % 12000:
        parser.error('Expected mono PCM16 WAV at a multiple of 12000 Hz (e.g. 48000).')
    manifest = dict(source=str(args.wav.resolve()), source_rate=rate, output_rate=12000, clips=[])
    for start in args.starts:
        first, count = round(start * rate), round(args.duration * rate)
        if first + count > wav.getnframes():
            parser.error(f'Clip at {start} extends beyond the recording.')
        # One second of context on each side avoids FFT filtering at clip edges.
        left, right = max(0, first - rate), min(wav.getnframes(), first + count + rate)
        wav.setpos(left)
        samples = np.frombuffer(wav.readframes(right - left), '<i2').astype(np.float64) / 32768
        if rate > 12000:
            spectrum = np.fft.rfft(samples)
            hz = np.fft.rfftfreq(len(samples), 1 / rate)
            # Smooth low pass; do not let high audio harmonics fold into OQ4.
            transition = np.clip((hz - 4500) / 1000, 0, 1)
            spectrum *= .5 + .5 * np.cos(np.pi * transition)
            samples = np.fft.irfft(spectrum, n=len(samples))
        audio = samples[first-left:first-left+count:rate//12000].astype('<f4')
        name = f'{args.wav.stem}-{start:.3f}s.f32'
        audio.tofile(args.output / name)
        manifest['clips'].append(dict(file=name, start_seconds=start, sample_count=len(audio)))
    (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))
