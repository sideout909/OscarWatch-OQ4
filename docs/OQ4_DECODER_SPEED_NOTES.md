# OQ4 decoder speed and signal search

FT4 searches its waterfall for known FT4 synchronization patterns, scores
candidates and supports frequency-drift tracks. OQ4 uses its own acquisition:
it pairs the packet's opening and closing reference tones to estimate timing and
drift, attempts strong candidates, then ranks additional candidates using the
known preamble. The different waveforms require different signal searches.

Ideas to investigate, not established improvements:

- Profile the live receive path, including native error correction, on a slower
  Windows PC and on representative quiet, weak-signal and busy recordings.
- Investigate whether FT4's efficient candidate scoring and duplicate handling
  suggest cheaper OQ4 ranking or pruning without losing weak or drifting signals.
- Look for calculations shared across candidates or frequency tracks, and
  optimize hot routines while preserving the existing search and acceptance rules.
- Consider a quick first pass followed by deeper work on promising candidates;
  measure both time to the first useful decode and total work per slot.
- Compare correct, missed and false decodes as well as CPU time and elapsed time;
  changing thresholds or search budgets can trade sensitivity for speed.

OQ4 receives a new slot every 3.75 seconds. A single receive worker processes
queued windows, so sustained processing longer than that can lead to skipped
slots; a shorter decode may still arrive too late for an immediate response.
Earlier 4.5-second offline clips on an i7-8700K had a 1.95 s median and 5.55 s
maximum after the repeated-computation improvement. These are not live Windows
measurements or a minimum specification. A virtual audio input with playback
aligned to the UTC slot clock can exercise the actual app; monitor the skipped
slot status and time to displayed results, with transmit and Auto Reply off.
