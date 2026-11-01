#!/usr/bin/env python3
"""Score blind Oq4Replay outputs against a generated manifest (expectations never enter decoding)."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('manifest', type=Path)
parser.add_argument('results', type=Path)
parser.add_argument('--baseline', type=Path)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
manifest = json.loads(args.manifest.read_text())
metadata = {case['file']: case for case in manifest['cases']}

def read(path):
    rows = [json.loads(line) for line in path.read_text().splitlines() if line.startswith('{')]
    result = {row['file']: row for row in rows}
    if len(result) != len(rows): raise ValueError(f'Duplicate case names in {path}')
    if result.keys() - metadata.keys(): raise ValueError('Results contain unknown cases')
    return result

def correct(case, row):
    expected = case.get('expected_message')
    return expected is not None and any(r['Message'] == expected and abs(r['FrequencyHz']-case['frequency_hz']) <= 8 for r in row['results'])

def score(rows):
    groups = {}
    unexpected = []
    for name, row in rows.items():
        case = metadata[name]
        if case.get('expected_message') is None:
            key = 'noise/'+case['noise_model']
        else:
            key = f"{case['split']}/{case['profile']}/{case['snr_2500_db']}dB"
        group = groups.setdefault(key, dict(tested=0, correct=0, valid_outputs=0))
        group['tested'] += 1
        group['correct'] += int(correct(case,row))
        group['valid_outputs'] += len(row['results'])
        for r in row['results']:
            if r['Message'] != case.get('expected_message') or ('frequency_hz' in case and abs(r['FrequencyHz']-case['frequency_hz']) > 8):
                unexpected.append(dict(file=name, result=r))
    return dict(completed=len(rows), correct=sum(correct(metadata[n],r) for n,r in rows.items()),
        valid_outputs=sum(len(r['results']) for r in rows.values()), groups=groups, unexpected=unexpected)

rows = read(args.results)
report = dict(manifest=str(args.manifest), results=score(rows))
if args.baseline:
    baseline = read(args.baseline)
    if baseline.keys() != rows.keys(): raise ValueError('Before/after results must contain identical cases')
    gained = [n for n in rows if correct(metadata[n],rows[n]) and not correct(metadata[n],baseline[n])]
    lost = [n for n in rows if not correct(metadata[n],rows[n]) and correct(metadata[n],baseline[n])]
    report['baseline'] = score(baseline)
    report['gained'] = [metadata[n] for n in gained]
    report['lost'] = [metadata[n] for n in lost]
text = json.dumps(report,indent=2)+'\n'
if args.output: args.output.write_text(text)
else: print(text)
