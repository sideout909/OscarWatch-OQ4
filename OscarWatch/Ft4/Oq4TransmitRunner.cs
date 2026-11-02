using OscarWatch.Core.Ft4;

namespace OscarWatch.Ft4;

internal sealed record Oq4TransmitRequest(string Message, double FrequencyHz, DateTime? SlotStart,
    int LeadMs, int TailMs, bool AudioOnly = false, bool Tune = false, bool KeepPttPortOpen = false);
internal sealed record Oq4PreparedAudio(float[] Samples, int SampleRate);
internal enum Oq4TransmitOutcome { Sent, Skipped }

/// <summary>Hardware seam for exercising the actual TX sequence without opening a port or sending RF.</summary>
internal interface IOq4TransmitHardware
{
    Oq4PreparedAudio Prepare(Oq4TransmitRequest request, CancellationToken cancellation);
    // Rebuild audio without touching the already prepared PTT adapter.
    Oq4PreparedAudio PrepareFrequencyChange(Oq4TransmitRequest request, CancellationToken cancellation) => Prepare(request, cancellation);
    void PreparePtt();
    bool StepDoppler(Oq4TransmitRequest request, TimeSpan budget);
    void KeyNow();
    bool PlayPrepared(Oq4PreparedAudio audio);
    void StartTune(Oq4TransmitRequest request);
    bool IsPlaying { get; }
    void StopAudio();
    void UnkeyNow();
    void ReleasePtt();
    void ReleaseDoppler();
}

/// <summary>
/// FT4-style preparation, dedicated slot wait and immediate PTT, followed by
/// prebuilt device-rate audio with silent lead-in. Never waits on the UI dispatcher.
/// </summary>
internal sealed class Oq4TransmitRunner
{
    private readonly IOq4TransmitHardware _hardware;
    private readonly Func<DateTime> _now;
    private readonly Action<DateTime, CancellationToken> _wait;
    private readonly Action<TimeSpan, CancellationToken> _delay;
    private readonly Action<string> _status;
    private readonly object _keyGate = new();
    private bool _stopped;

    internal Oq4TransmitRunner(IOq4TransmitHardware hardware, Action<string> status,
        Func<DateTime>? now = null, Action<DateTime, CancellationToken>? wait = null,
        Action<TimeSpan, CancellationToken>? delay = null)
    {
        _hardware = hardware; _status = status;
        _now = now ?? (() => Ft4Clock.UtcNow);
        _wait = wait ?? Ft4SlotWait.UntilUtc;
        _delay = delay ?? ((duration, token) =>
        {
            if (token.WaitHandle.WaitOne(duration)) token.ThrowIfCancellationRequested();
        });
    }

    internal static string ArmMessage(Ft4QsoSequencer sequencer, string? operatorText, bool evenSlot, double frequencyHz)
    {
        sequencer.SetTxMessage(operatorText);
        sequencer.PreferEvenSlot = evenSlot;
        sequencer.TxAudioHz = frequencyHz;
        // FT4's EnableTx builds CQ when the box is empty. Validate the resulting
        // message, rather than trying to encode the watermark/empty input first.
        sequencer.EnableTx();
        try
        {
            Ft8Native.EncodeOq4Codeword(sequencer.CurrentTxMessage);
            return sequencer.CurrentTxMessage;
        }
        catch { sequencer.HaltTx(); throw; }
    }

    public Oq4TransmitOutcome Run(Oq4TransmitRequest request, CancellationToken token,
        Func<double>? latestFrequency = null, Action<Oq4TransmitRequest>? committed = null)
    {
        lock (_keyGate) { token.ThrowIfCancellationRequested(); _stopped = false; }
        var keepPort = false;
        try
        {
            _status("Preparing OQ4 audio and PTT…");
            var prepared = _hardware.Prepare(request, token);
            void RefreshAudioUntil(DateTime until)
            {
                if (latestFrequency is null) { _wait(until, token); return; }
                while (_now() < until)
                {
                    token.ThrowIfCancellationRequested();
                    var hz = latestFrequency();
                    if (double.IsFinite(hz) && hz >= 200 && hz <= 3000 && Math.Abs(hz - request.FrequencyHz) > .1)
                    {
                        var updated = request with { FrequencyHz = hz };
                        prepared = _hardware.PrepareFrequencyChange(updated, token);
                        token.ThrowIfCancellationRequested();
                        request = updated;
                    }
                    var next = _now().AddMilliseconds(50);
                    _wait(next < until ? next : until, token);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!request.AudioOnly) WithKeyGate(token, _hardware.PreparePtt);
            token.ThrowIfCancellationRequested();
            if (request.SlotStart is { } slot)
            {
                var burst = slot.AddSeconds(Oq4SlotClock.BurstOffsetSeconds);
                var keyAt = slot < burst.AddMilliseconds(-request.LeadMs) ? slot : burst.AddMilliseconds(-request.LeadMs);
                // Leave the previous RX burst alone; begin the CAT step in the
                // remaining silence, rather than freezing the dial for a whole slot.
                var catLead = Oq4SlotClock.SlotSeconds - Oq4SlotClock.BurstOffsetSeconds
                    - Oq4Waveform.PacketDurationSeconds - .16;
                RefreshAudioUntil(keyAt.AddSeconds(-catLead));
                var budget = keyAt - _now() - TimeSpan.FromMilliseconds(40);
                if (!request.AudioOnly && budget > TimeSpan.FromMilliseconds(80))
                {
                    // FT4 does not make successful CAT tuning a prerequisite for
                    // PTT: an urgent key command can preempt the remaining write.
                    if (!_hardware.StepDoppler(request, budget))
                        _status("Doppler preparation is still running; PTT takes priority.");
                }
                // Freeze the packet before the PTT lead. Late edits apply to
                // the next packet rather than interrupting an on-air waveform.
                if (latestFrequency is not null) RefreshAudioUntil(keyAt.AddMilliseconds(-250));
                _wait(keyAt, token);
                if (_now() > slot.AddMilliseconds(150))
                {
                    _status("Slot missed during preparation; waiting for the next selected slot.");
                    return Oq4TransmitOutcome.Skipped;
                }
                WithKeyGate(token, () => { committed?.Invoke(request); if (!request.AudioOnly) _hardware.KeyNow(); });
                _wait(slot, token);
                if (_now() > slot.AddMilliseconds(150))
                {
                    _status("PTT was late; waiting for the next selected slot.");
                    return Oq4TransmitOutcome.Skipped;
                }
            }
            else
            {
                WithKeyGate(token, () => { committed?.Invoke(request); if (!request.AudioOnly) _hardware.KeyNow(); });
                if (!request.AudioOnly) _delay(TimeSpan.FromMilliseconds(request.LeadMs), token);
            }
            WithKeyGate(token, () =>
            {
                if (request.Tune) _hardware.StartTune(request);
                else if (!_hardware.PlayPrepared(prepared))
                    throw new InvalidOperationException("The prepared audio output is no longer available. Check the output device in Settings.");
            });
            _status(request.Tune ? "Tune active. Select Halt TX to stop."
                : request.AudioOnly ? "Playing OQ4 audio test (PTT not keyed)…" : "Transmitting OQ4…");
            var deadline = _now().AddSeconds(request.Tune ? 180 : prepared.Samples.Length / (double)prepared.SampleRate + 1.5);
            while (_hardware.IsPlaying)
            {
                token.ThrowIfCancellationRequested();
                if (_now() >= deadline)
                    throw new InvalidOperationException(request.Tune ? "Tune stopped after three minutes." : "Audio playback stalled; transmission stopped.");
                _delay(TimeSpan.FromMilliseconds(20), token);
            }
            if (!request.AudioOnly) _delay(TimeSpan.FromMilliseconds(request.TailMs), token);
            keepPort = request.KeepPttPortOpen && !request.AudioOnly && !request.Tune;
            return Oq4TransmitOutcome.Sent;
        }
        finally
        {
            try { StopCore(keepPort && !token.IsCancellationRequested); }
            finally { _hardware.ReleaseDoppler(); }
        }
    }

    private void WithKeyGate(CancellationToken token, Action action)
    {
        lock (_keyGate)
        {
            token.ThrowIfCancellationRequested();
            if (_stopped) throw new OperationCanceledException(token);
            action();
        }
    }

    // Explicit Halt/retarget/close always releases the adapter. Successful
    // repeated bursts may leave it open at idle: USB serial open/close can pulse
    // RTS/DTR even when the application's requested line state is deasserted.
    public void Stop() => StopCore(keepPort: false);

    private void StopCore(bool keepPort)
    {
        lock (_keyGate)
        {
            keepPort &= !_stopped;
            _stopped = true;
            var audioStopped = false;
            try { _hardware.StopAudio(); audioStopped = true; }
            finally
            {
                var unkeyed = false;
                try { _hardware.UnkeyNow(); unkeyed = true; }
                finally { if (!keepPort || !audioStopped || !unkeyed) _hardware.ReleasePtt(); }
            }
        }
    }
}
