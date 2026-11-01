#!/usr/bin/env python3
"""Create reproducible local OQ4 trials using the independently implemented Decoder Lab waveform.
Requires numpy and the existing local Decoder Lab. No audio devices/PTT are opened.
"""
import argparse
import importlib.util
import hashlib
import platform
import json
from pathlib import Path
import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--lab', type=Path, default=Path.home()/'Documents/OscarWatch Decoder Lab/decoder_lab.py')
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--seeds', type=int, default=6)
parser.add_argument('--noise-trials', type=int, default=160)
args = parser.parse_args()
if args.seeds < 2 or args.noise_trials < 0:
    parser.error('Use at least two seeds and a nonnegative noise count.')
spec = importlib.util.spec_from_file_location('oq4_lab', args.lab)
lab = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lab)
encoder = lab.Encoder()
args.output.mkdir(parents=True, exist_ok=True)
fs, signal_dbfs = 12000, -35.0
cases = []
messages = ['CQ K8OTY EN73', 'CQ K1ABC FN42', 'K8OTY K1ABC -12']
profiles = [('stationary', 0, 0), ('curved', 0, -14), ('linear', 14, 14), ('fast', 68, 68)]
for frequency in (500, 1500, 2300):
    for profile, r0, r1 in profiles:
        for snr in (-12.5, -14.5):
            for seed in range(args.seeds):
                case_id = len(cases)
                rng = np.random.default_rng(1031000 + case_id)
                actual_frequency = frequency + rng.uniform(-1.8, 1.8)
                message = messages[seed % len(messages)]
                slot, duration = encoder.build('OQ4', message, actual_frequency, signal_dbfs, r0, r1)
                jitter_samples = int(rng.integers(-1200, 1201))
                slot = np.roll(slot, jitter_samples)  # only surrounding silence wraps
                sigma = 10**(signal_dbfs/20) * np.sqrt((fs/2)/2500) * 10**(-snr/20)
                audio = rng.normal(0, sigma, 48000)
                audio[:len(slot)] += slot
                name = f'signal-{case_id:04d}.f32'
                audio.astype('<f4').tofile(args.output/name)
                cases.append(dict(file=name, expected_message=message, frequency_hz=actual_frequency,
                    profile=profile, start_rate_hz_s=r0, end_rate_hz_s=r1, snr_2500_db=snr,
                    active_signal_dbfs=signal_dbfs, noise_sigma=float(sigma), duration_seconds=duration,
                    seed=seed, noise_seed=1031000+case_id, jitter_samples=jitter_samples,
                    split='discovery' if seed < args.seeds//2 else 'holdout'))
for index in range(args.noise_trials):
    rng = np.random.default_rng(2031000+index)
    audio = rng.normal(0, .07, 48000)
    model = ['white', 'colored', 'tone', 'impulsive'][index % 4]
    if model == 'colored':
        spectrum = np.fft.rfft(audio)
        hz = np.fft.rfftfreq(len(audio), 1/fs)
        spectrum *= 1/np.sqrt(np.maximum(hz, 100))
        audio = np.fft.irfft(spectrum, n=len(audio))
        audio *= .07/np.sqrt(np.mean(audio**2))
    elif model == 'tone':
        t = np.arange(len(audio))/fs
        audio += .08*np.cos(2*np.pi*rng.uniform(250, 2700)*t+rng.uniform(-np.pi,np.pi))
    elif model == 'impulsive':
        locations = rng.choice(len(audio), 40, replace=False)
        audio[locations] += rng.normal(0, .7, len(locations))
    name = f'noise-{index:04d}.f32'
    audio.astype('<f4').tofile(args.output/name)
    cases.append(dict(file=name, expected_message=None, noise_model=model, noise_seed=2031000+index, split='noise'))
manifest = dict(sample_rate=fs, noise_reference_bandwidth_hz=2500,
    waveform_source=str(args.lab.resolve()), waveform_sha256=hashlib.sha256(args.lab.read_bytes()).hexdigest(),
    python_version=platform.python_version(), numpy_version=np.__version__, notes='Native message encoder is shared; waveform/modulation is independent Python. Expected payloads are only used to score output.',
    cases=cases)
(args.output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(f'Wrote {len(cases)} cases ({len(cases)-args.noise_trials} signal, {args.noise_trials} noise).')
