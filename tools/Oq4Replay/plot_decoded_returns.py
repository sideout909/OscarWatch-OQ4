#!/usr/bin/env python3
"""Plot six recorded OQ4 decodes; never opens audio or PTT.

Requires numpy, scipy and matplotlib. Prepare the local recording with
prepare_manifest.py, then run:
  python3 tools/Oq4Replay/plot_decoded_returns.py CLIP_DIRECTORY OUTPUT.png
Metadata records production-receiver results; this script does not decode audio.
"""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
from scipy.signal import spectrogram


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('clips', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    meta = json.loads(Path(__file__).with_name('decoded_returns_figure.json').read_text())
    fs = meta['sample_rate']
    duration = meta['packet_duration_seconds']
    plt.rcParams.update({
        'font.family': 'DejaVu Sans', 'font.size': 10,
        'text.color': '#e8edf5', 'axes.labelcolor': '#c8d4e5',
        'xtick.color': '#b9c7d9', 'ytick.color': '#b9c7d9',
        'axes.edgecolor': '#41506a', 'axes.facecolor': '#101b2e',
        'figure.facecolor': '#0b1322',
    })
    fig, axes = plt.subplots(2, 3, figsize=(16, 10.8))
    fig.subplots_adjust(left=.07, right=.91, top=.755, bottom=.19,
                        wspace=.25, hspace=.68)
    fig.text(.07, .94, 'OQ4 • decoded downlink returns', fontsize=26, weight='bold')
    fig.text(.07, .90, 'Real recorded audio. Short packets. Weaker and stronger examples.',
             fontsize=14, color='#b9c7d9')
    fig.text(.07, .86, 'JO-97  |  02 November 2026 UTC  |  CQ K8OTY EN73  |  CRC-valid production-decoder outputs',
             fontsize=11, color='#b9c7d9')
    fig.text(.07, .825, 'WEAKER RETURNS', fontsize=12, weight='bold', color='#ba9cff')
    fig.text(.07, .485, 'STRONGER RETURNS', fontsize=12, weight='bold', color='#55e0b6')
    for i, (row, ax) in enumerate(zip(meta['examples'], axes.flat)):
        audio = np.fromfile(args.clips / row['file'], dtype='<f4')
        assert len(audio) == round(4.5 * fs), f'Unexpected clip length: {row["file"]}'
        frequency, time, power = spectrogram(
            audio, fs, window='hann', nperseg=2048, noverlap=1988,
            detrend=False, scaling='density')
        offset = frequency - row['frequency_hz']
        keep = abs(offset) <= 130
        image = ax.pcolormesh(
            time - row['packet_start_seconds'], offset[keep],
            10 * np.log10(np.maximum(power[keep], 1e-16)),
            shading='auto', cmap='magma', vmin=-100, vmax=-45, rasterized=True)
        for start, stop in ((0, .16), (duration - .16, duration)):
            ax.axvspan(start, stop, facecolor='#55e0b6', alpha=.08,
                       edgecolor='#55e0b6', linewidth=.8)
            ax.text((start+stop)/2, 1.025, 'REF', ha='center', fontsize=8,
                    color='#55e0b6', transform=ax.get_xaxis_transform())
        ax.set(xlim=(-.15, 2.8), ylim=(-120, 120),
               xticks=[0, .5, 1, 1.5, 2, 2.5], yticks=[-100, 0, 100],
               xlabel='Seconds from packet start', ylabel='Carrier offset (Hz)')
        color = '#ba9cff' if i < 3 else '#55e0b6'
        utc = row['utc_slot'][11:].rstrip('0').rstrip('.')
        if len(utc) < 8:
            utc = row['utc_slot'][11:19]
        ax.set_title(f"SNR {row['snr_db']:.1f} dB  •  {row['frequency_hz']:.1f} Hz",
                     loc='left', fontsize=12, weight='bold', color=color, pad=32)
        ax.text(0, 1.095, f"{utc} UTC  |  +{row['delay_ms']} ms after local TX copy",
                transform=ax.transAxes, fontsize=9, color='#c8d4e5')
    cax = fig.add_axes([.935, .19, .012, .565])
    cb = fig.colorbar(image, cax=cax)
    cb.set_label('Recorded spectral density (dB re full-scale²/Hz)', fontsize=9)
    fig.text(.07, .117, '2.523 s packets  •  Shaded REF regions: opening / closing center-frequency tones',
             fontsize=11, color='#55e0b6')
    fig.text(.07, .08, 'Unfiltered recorded audio; identical time, frequency-span and color scales in every panel. No per-panel gain adjustment.',
             fontsize=10, color='#b9c7d9')
    fig.text(.07, .047, 'SNR: approximate decoder estimate in 2500 Hz. Delays are relative decoder timings, not calibrated satellite range.',
             fontsize=10, color='#b9c7d9')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(args.output, dpi=170, facecolor=fig.get_facecolor())
    plt.close(fig)


if __name__ == '__main__':
    main()
