#!/usr/bin/env python3
"""Plot actual encoder exports. Requires numpy/matplotlib; never opens audio or PTT.
Export first: dotnet run --project tools/Oq4Replay -- --export-waveforms /tmp/oq4-waveforms
Plot: python3 tools/Oq4Replay/plot_comparison.py /tmp/oq4-waveforms screenshots/oq4-vs-ft4.png
"""
import argparse
import json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from numpy.lib.stride_tricks import sliding_window_view

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('exports',type=Path)
parser.add_argument('output',type=Path)
args=parser.parse_args()
meta=json.loads((args.exports/'metadata.json').read_text())
fs=meta['sampleRate']
audio={name:np.fromfile(args.exports/(name+'.f32'),dtype='<f4').astype(float) for name in ('ft4','oq4')}
# Equalize average power across each complete active packet, including references.
for name,x in audio.items():
    start=meta[name+'StartSeconds']; duration=meta[name+'DurationSeconds']
    active=x[round(start*fs):round((start+duration)*fs)]
    x*=.2/np.sqrt(np.mean(active**2))

plt.rcParams.update({'font.family':'DejaVu Sans','font.size':11,'text.color':'#e8edf5',
    'axes.labelcolor':'#dce4ef','xtick.color':'#b9c7d9','ytick.color':'#b9c7d9',
    'axes.edgecolor':'#41506a','axes.facecolor':'#101b2e','figure.facecolor':'#0b1322'})
fig,axes=plt.subplots(3,1,figsize=(14,9),gridspec_kw={'height_ratios':[1.1,1,1]},sharex=True)
fig.subplots_adjust(left=.095,right=.88,top=.82,bottom=.13,hspace=.23)
fig.text(.095,.945,'FT4 vs OQ4',fontsize=27,weight='bold')
fig.text(.095,.902,'Same message. Shorter packet. A different waveform.',fontsize=15,color='#b9c7d9')
fig.text(.095,.86,f"{meta['message']}  |  12 kHz encoder output  |  equal average active-packet power  |  no noise or drift",fontsize=10,color='#b9c7d9')
colors={'ft4':'#ba9cff','oq4':'#55e0b6'}
ax=axes[0]
for name,offset in [('ft4',1.4),('oq4',0)]:
    x=audio[name]; hop=120
    blocks=x[:len(x)//hop*hop].reshape(-1,hop)
    peak=np.max(np.abs(blocks),axis=1)
    times=(np.arange(len(peak))+.5)*hop/fs
    ax.fill_between(times,offset-peak,offset+peak,color=colors[name],alpha=.9,linewidth=0)
    start=meta[name+'StartSeconds']; end=start+meta[name+'DurationSeconds']
    ax.text(5.85,offset+.03,name.upper(),color=colors[name],fontsize=14,weight='bold')
    ax.text(5.85,offset-.27,f"{meta[name+'DurationSeconds']:.3f} s packet",color='#b9c7d9',fontsize=10)
    ax.annotate('',xy=(end,offset+.62),xytext=(start,offset+.62),arrowprops={'arrowstyle':'|-|','color':colors[name]})
ax.set_ylim(-.75,2.3); ax.set_yticks([0,1.4],['OQ4','FT4']); ax.set_ylabel('Audio envelope')
ax.set_title('Packet timing on the same 7.5-second timeline',loc='left',fontsize=11,pad=10)
ax.axvline(3.75,color='#55e0b6',linestyle='--',alpha=.6)
ax.text(3.81,.48,'OQ4 slot boundary',fontsize=9,color='#55e0b6')
for ax,name in zip(axes[1:],('ft4','oq4')):
    x=audio[name]; window=np.hanning(2048); hop=64
    frames=sliding_window_view(x,len(window))[::hop]
    spec=np.abs(np.fft.rfft(frames*window,axis=1)/window.sum())**2
    hz=np.fft.rfftfreq(len(window),1/fs)-meta['centerHz']
    keep=np.abs(hz)<=150
    time=(np.arange(len(frames))*hop+len(window)/2)/fs
    db=10*np.log10(np.maximum(spec[:,keep],1e-14))
    im=ax.pcolormesh(time,hz[keep],db.T,cmap='magma',vmin=-65,vmax=-15,shading='auto',rasterized=True)
    ax.set_ylim(-150,150); ax.set_yticks([-100,0,100]); ax.set_ylabel('Offset (Hz)')
    ax.set_title(('FT4 — four-tone GFSK' if name=='ft4' else 'OQ4 — shaped QPSK with opening/closing reference tones'),loc='left',fontsize=11,color=colors[name],pad=9)
    if name=='oq4':
        for t in (.48,.4+meta['oq4DurationSeconds']-.08):
            ax.annotate('reference',xy=(t,0),xytext=(t+.12,100),fontsize=9,color='#dce4ef',arrowprops={'arrowstyle':'->','color':'#dce4ef','lw':.8})
for ax in axes:
    ax.set_xlim(0,7.5); ax.set_xticks(np.arange(0,7.51,.75)); ax.grid(axis='x',alpha=.12)
axes[-1].set_xlabel('Time from slot boundary (seconds)')
cax=fig.add_axes([.90,.13,.015,.43])
cb=fig.colorbar(im,cax=cax);cb.set_label('Spectral level (dB; common scale)');cb.outline.set_edgecolor('#41506a')
fig.text(.095,.047,'Synthetic illustration from the production encoders. Matched axes and spectral color scale; active-packet RMS normalized equally.',fontsize=9,color='#b9c7d9')
fig.text(.095,.024,'Shows timing and signal structure, not measured RF occupied bandwidth, decoding sensitivity, or a satellite recording.',fontsize=9,color='#b9c7d9')
args.output.parent.mkdir(parents=True,exist_ok=True)
fig.savefig(args.output,dpi=170,facecolor=fig.get_facecolor())
print(args.output)
