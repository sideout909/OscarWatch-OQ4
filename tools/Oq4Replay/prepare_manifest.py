#!/usr/bin/env python3
"""Prepare exact local recording clips from a checked manifest. Requires FFmpeg.

Only audio files are read/written; no audio devices or radio controls are opened.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import wave

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('wav', type=Path)
parser.add_argument('manifest', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
manifest = json.loads(args.manifest.read_text())
with args.wav.open('rb') as source:
    digest = hashlib.file_digest(source, 'sha256').hexdigest()
if digest != manifest['source_sha256']:
    parser.error('WAV checksum differs from the recording used for this manifest.')
with wave.open(str(args.wav), 'rb') as wav:
    duration = wav.getnframes() / wav.getframerate()
    if wav.getnchannels() != 1 or wav.getsampwidth() != 2:
        parser.error('Expected mono PCM16 WAV.')
for clip in manifest['clips']:
    if Path(clip['file']).name != clip['file'] or not clip['file'].endswith('.f32'):
        parser.error('Clip filenames must be plain .f32 filenames.')
    if clip['start_seconds'] < 0 or clip['duration_seconds'] <= 0 or clip['start_seconds'] + clip['duration_seconds'] > duration:
        parser.error('A clip extends outside the recording.')
args.output.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='oq4-replay-') as temporary:
    raw = Path(temporary) / 'audio.f32'
    subprocess.run(['ffmpeg', '-v', 'error', '-i', str(args.wav), '-ar', '12000',
                    '-ac', '1', '-f', 'f32le', '-y', str(raw)], check=True)
    with raw.open('rb') as audio:
        for clip in manifest['clips']:
            audio.seek(round(clip['start_seconds'] * 12000) * 4)
            size = round(clip['duration_seconds'] * 12000) * 4
            samples = audio.read(size)
            if len(samples) != size:
                raise ValueError('Resampled recording is shorter than a requested clip.')
            (args.output / clip['file']).write_bytes(samples)
(args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(f"Prepared {len(manifest['clips'])} mono 12 kHz float32 clips in {args.output}")
