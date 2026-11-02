using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4TransmitRunnerTests
{
    private static Oq4TransmitRequest Request(bool audioOnly = false, bool tune = false) =>
        new("CQ K1ABC FN42", 1500, tune || audioOnly ? null : DateTime.UnixEpoch.AddSeconds(10), 200, 100, audioOnly, tune);

    [Fact]
    public void Timed_transmit_prepares_adapter_and_audio_before_keying_then_plays_at_slot_start()
    {
        var hardware = new Hardware();
        var runner = Runner(hardware);
        Assert.Equal(Oq4TransmitOutcome.Sent, runner.Run(Request(), default));
        Assert.Equal(new[] { "prepare", "prepare-ptt", "doppler", "key", "play", "stop", "unkey", "release-ptt", "release-doppler" }, hardware.Events);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.KeyAt);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.PlayAt);
    }

    [Fact]
    public void Doppler_timeout_does_not_prevent_ptt_or_audio()
    {
        var hardware = new Hardware { StepSucceeded = false };
        var statuses = new List<string>();
        var runner = Runner(hardware, statuses.Add);
        Assert.Equal(Oq4TransmitOutcome.Sent, runner.Run(Request(), default));
        Assert.Contains("key", hardware.Events);
        Assert.Contains("play", hardware.Events);
        Assert.Contains(statuses, s => s.Contains("PTT takes priority"));
    }

    [Fact]
    public void Long_configured_ptt_lead_is_respected_without_delaying_audio()
    {
        var hardware = new Hardware();
        Runner(hardware).Run(Request() with { LeadMs = 1000 }, default);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(9.4), hardware.KeyAt);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.PlayAt);
    }

    [Fact]
    public void Adapter_open_latency_is_absorbed_before_the_key_time()
    {
        var hardware = new Hardware { PreparePttDelay = TimeSpan.FromSeconds(1) };
        Runner(hardware).Run(Request(), default);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.KeyAt);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.PlayAt);
    }

    [Fact]
    public void Missed_slot_does_not_key_or_play_and_releases_resources()
    {
        var hardware = new Hardware { PrepareDelay = TimeSpan.FromSeconds(11) };
        Assert.Equal(Oq4TransmitOutcome.Skipped, Runner(hardware).Run(Request(), default));
        Assert.DoesNotContain("key", hardware.Events);
        Assert.DoesNotContain("play", hardware.Events);
        Assert.Contains("release-ptt", hardware.Events);
        Assert.Contains("release-doppler", hardware.Events);
    }

    [Fact]
    public void Cancellation_during_preparation_never_keys()
    {
        using var cancel = new CancellationTokenSource();
        var hardware = new Hardware { DuringPrepare = cancel.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => Runner(hardware).Run(Request(), cancel.Token));
        Assert.DoesNotContain("key", hardware.Events);
        Assert.Contains("release-ptt", hardware.Events);
    }

    [Fact]
    public void Halt_while_waiting_cannot_be_followed_by_a_late_key()
    {
        var hardware = new Hardware();
        Oq4TransmitRunner? runner = null;
        runner = Runner(hardware, wait: (_, _) => runner!.Stop());
        Assert.ThrowsAny<OperationCanceledException>(() => runner.Run(Request(), default));
        Assert.DoesNotContain("key", hardware.Events);
        Assert.DoesNotContain("play", hardware.Events);
    }

    [Fact]
    public void Playback_failure_unkeys_and_closes_adapter()
    {
        var hardware = new Hardware { PlaySucceeded = false };
        Assert.Throws<InvalidOperationException>(() => Runner(hardware).Run(Request(), default));
        Assert.Contains("key", hardware.Events);
        Assert.True(hardware.Events.IndexOf("unkey") > hardware.Events.IndexOf("key"));
        Assert.Contains("release-ptt", hardware.Events);
    }

    [Fact]
    public void Ptt_failure_does_not_play_and_closes_adapter()
    {
        var hardware = new Hardware { KeyError = new IOException("adapter unavailable") };
        Assert.Throws<IOException>(() => Runner(hardware).Run(Request(), default));
        Assert.DoesNotContain("play", hardware.Events);
        Assert.Contains("release-ptt", hardware.Events);
    }

    [Fact]
    public void Audio_only_test_never_prepares_or_keys_the_radio()
    {
        var hardware = new Hardware();
        Runner(hardware).Run(Request(audioOnly: true), default);
        Assert.DoesNotContain("prepare-ptt", hardware.Events);
        Assert.DoesNotContain("doppler", hardware.Events);
        Assert.DoesNotContain("key", hardware.Events);
        Assert.Contains("play", hardware.Events);
    }

    [Fact]
    public void Tune_uses_same_ptt_path_and_cancellation_unkeys()
    {
        using var cancel = new CancellationTokenSource();
        var hardware = new Hardware { DuringTune = cancel.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => Runner(hardware).Run(Request(tune: true), cancel.Token));
        Assert.Contains("key", hardware.Events);
        Assert.Contains("tune", hardware.Events);
        Assert.Contains("unkey", hardware.Events);
    }

    [Fact]
    public void Cancellation_after_keying_stops_audio_and_drops_ptt()
    {
        using var cancel = new CancellationTokenSource();
        var hardware = new Hardware { DuringKey = cancel.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => Runner(hardware).Run(Request(), cancel.Token));
        Assert.Contains("key", hardware.Events);
        Assert.DoesNotContain("play", hardware.Events);
        Assert.Contains("unkey", hardware.Events);
    }

    [Fact]
    public void Slot_skipped_after_slow_key_does_not_send_a_late_packet()
    {
        var hardware = new Hardware { KeyDelay = TimeSpan.FromMilliseconds(300) };
        Assert.Equal(Oq4TransmitOutcome.Skipped, Runner(hardware).Run(Request(), default));
        Assert.Contains("key", hardware.Events);
        Assert.DoesNotContain("play", hardware.Events);
        Assert.Contains("unkey", hardware.Events);
    }

    [Fact]
    public void Failed_first_attempt_can_be_followed_by_a_successful_transmission()
    {
        var hardware = new Hardware { PlaySucceeded = false };
        var runner = Runner(hardware);
        Assert.Throws<InvalidOperationException>(() => runner.Run(Request(), default));
        hardware.PlaySucceeded = true;
        Assert.Equal(Oq4TransmitOutcome.Sent, runner.Run(Request() with { SlotStart = DateTime.UnixEpoch.AddSeconds(20) }, default));
    }

    [Fact]
    public void Repeated_bursts_keep_idle_adapter_open_until_halt_without_extra_keys()
    {
        var hardware = new Hardware();
        var runner = Runner(hardware);
        var request = Request() with { KeepPttPortOpen = true };
        Assert.Equal(Oq4TransmitOutcome.Sent, runner.Run(request, default));
        Assert.True(hardware.PortOpen);
        Assert.Equal(Oq4TransmitOutcome.Sent, runner.Run(request with { SlotStart = request.SlotStart!.Value.AddSeconds(7.5) }, default));
        Assert.Equal(1,hardware.PortOpenCount);
        Assert.Equal(2,hardware.Events.Count(x => x == "key"));
        Assert.DoesNotContain("release-ptt",hardware.Events);
        runner.Stop();
        Assert.False(hardware.PortOpen);
        Assert.Contains("release-ptt",hardware.Events);
    }

    [Fact]
    public void Failed_next_burst_closes_a_retained_adapter()
    {
        var hardware = new Hardware();
        var runner = Runner(hardware);
        var request = Request() with { KeepPttPortOpen = true };
        runner.Run(request,default);
        hardware.PlaySucceeded = false;
        Assert.Throws<InvalidOperationException>(() => runner.Run(request with { SlotStart=request.SlotStart!.Value.AddSeconds(7.5) },default));
        Assert.False(hardware.PortOpen);
        Assert.Contains("release-doppler",hardware.Events);
    }

    [Fact]
    public void Cancellation_closes_adapter_even_when_repeat_reuse_was_requested()
    {
        using var cancel = new CancellationTokenSource();
        var hardware = new Hardware { DuringKey = cancel.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => Runner(hardware).Run(Request() with { KeepPttPortOpen=true },cancel.Token));
        Assert.False(hardware.PortOpen);
        Assert.DoesNotContain("play",hardware.Events);
        Assert.Contains("release-ptt",hardware.Events);
    }

    [Fact]
    public void Cleanup_failure_never_retains_adapter_or_doppler_hold()
    {
        var hardware = new Hardware { UnkeyFails = true };
        Assert.Throws<IOException>(() => Runner(hardware).Run(Request() with { KeepPttPortOpen=true },default));
        Assert.False(hardware.PortOpen);
        Assert.Contains("release-ptt",hardware.Events);
        Assert.Contains("release-doppler",hardware.Events);
    }

    [Fact]
    public void Estimated_snr_uses_signal_to_noise_ratio_and_not_absolute_audio_gain()
    {
        var bits = Enumerable.Range(0, 174).Select(i => (byte)(i % 2)).ToArray();
        var random = new Random(123);
        var signal = bits.Select(b => (b == 0 ? -1.0 : 1.0) + (random.NextDouble()-.5)*.5).ToArray();
        var snr = Oq4Decoder.EstimateSnr(signal, bits);
        Assert.InRange(snr, -3, 5);
        Assert.InRange(Math.Abs(snr - Oq4Decoder.EstimateSnr(signal.Select(x => x*.001).ToArray(), bits)), 0, .001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Enable_tx_builds_cq_from_station_settings_when_the_message_box_is_empty(string? text)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var sequence = new OscarWatch.Core.Ft4.Ft4QsoSequencer(() => "K8OTY", () => "EN73", () => true);
        Assert.Equal("CQ K8OTY EN73", Oq4TransmitRunner.ArmMessage(sequence, text, true, 2570));
        Assert.True(sequence.TransmitEnabled);
        Assert.Equal(2570, sequence.TxAudioHz);
    }

    [Fact]
    public void Unencodable_message_does_not_leave_the_sequencer_armed()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var sequence = new OscarWatch.Core.Ft4.Ft4QsoSequencer(() => "K8OTY", () => "EN73", () => true);
        Assert.Throws<ArgumentException>(() => Oq4TransmitRunner.ArmMessage(sequence,
            "THIS MESSAGE IS FAR TOO LONG FOR THE FREE TEXT PAYLOAD", true, 1500));
        Assert.False(sequence.TransmitEnabled);
    }

    [Fact]
    public void Waterfall_frequency_change_refreshes_audio_without_releasing_or_reopening_ptt()
    {
        var hardware = new Hardware();
        Oq4TransmitRequest? committed = null;
        var result = Runner(hardware).Run(Request() with { KeepPttPortOpen = true }, default,
            latestFrequency: () => hardware.Now >= DateTime.UnixEpoch.AddSeconds(5) ? 900 : 1500,
            committed: request => committed = request);
        Assert.Equal(Oq4TransmitOutcome.Sent, result);
        Assert.Equal(new double[] { 1500, 900 }, hardware.PreparedFrequencies);
        Assert.Equal(900, committed!.FrequencyHz);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.KeyAt);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.PlayAt);
        Assert.Equal(1, hardware.PortOpenCount);
        Assert.Single(hardware.Events, e => e == "prepare-ptt");
        Assert.Single(hardware.Events, e => e == "key");
        Assert.DoesNotContain("release-ptt", hardware.Events);
    }

    [Fact]
    public void Multiple_waiting_frequency_changes_keep_one_ptt_preparation_and_the_original_slot()
    {
        var hardware = new Hardware();
        double Frequency() => hardware.Now < DateTime.UnixEpoch.AddSeconds(3) ? 1500
            : hardware.Now < DateTime.UnixEpoch.AddSeconds(6) ? 1700 : 1900;
        Runner(hardware).Run(Request() with { KeepPttPortOpen = true }, default, Frequency);
        Assert.Equal(new double[] { 1500, 1700, 1900 }, hardware.PreparedFrequencies);
        Assert.Equal(1, hardware.PortOpenCount);
        Assert.Single(hardware.Events, e => e == "key");
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), hardware.PlayAt);
    }

    [Fact]
    public void Frequency_change_after_keying_does_not_rewrite_the_on_air_packet()
    {
        var hardware = new Hardware();
        var desired = 1500d;
        hardware.DuringKey = () => desired = 900;
        Oq4TransmitRequest? committed = null;
        Runner(hardware).Run(Request(), default, () => desired, request => committed = request);
        Assert.Equal(new double[] { 1500 }, hardware.PreparedFrequencies);
        Assert.Equal(1500, committed!.FrequencyHz);
        Runner(hardware).Run(Request() with { FrequencyHz = desired, SlotStart = DateTime.UnixEpoch.AddSeconds(17.5) }, default,
            () => desired, request => committed = request);
        Assert.Equal(new double[] { 1500, 900 }, hardware.PreparedFrequencies);
        Assert.Equal(900, committed!.FrequencyHz);
    }

    [Fact]
    public void Halt_during_frequency_refresh_never_keys_and_releases_the_reserved_port()
    {
        using var cancellation = new CancellationTokenSource();
        var hardware = new Hardware();
        hardware.DuringPrepare = () => { if (hardware.PreparedFrequencies.Count == 2) cancellation.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => Runner(hardware).Run(
            Request() with { KeepPttPortOpen = true }, cancellation.Token, () => 900));
        Assert.DoesNotContain("key", hardware.Events);
        Assert.Contains("release-ptt", hardware.Events);
        Assert.False(hardware.PortOpen);
    }

    [Fact]
    public void Slow_frequency_rebuild_skips_a_missed_slot_without_keying_late()
    {
        var hardware = new Hardware();
        hardware.DuringPrepare = () => { if (hardware.PreparedFrequencies.Count == 2) hardware.Now += TimeSpan.FromSeconds(2); };
        var result = Runner(hardware).Run(Request(), default,
            () => hardware.Now >= DateTime.UnixEpoch.AddSeconds(9) ? 900 : 1500);
        Assert.Equal(Oq4TransmitOutcome.Skipped, result);
        Assert.DoesNotContain("key", hardware.Events);
        Assert.Contains("release-ptt", hardware.Events);
    }

    private static Oq4TransmitRunner Runner(Hardware h, Action<string>? status = null,
        Action<DateTime, CancellationToken>? wait = null) => new(h, status ?? (_ => { }), () => h.Now,
            wait ?? ((at, token) => { token.ThrowIfCancellationRequested(); if (at > h.Now) h.Now = at; }),
            (delay, token) => { token.ThrowIfCancellationRequested(); h.Now += delay; });

    private sealed class Hardware : IOq4TransmitHardware
    {
        public readonly List<string> Events = [];
        public readonly List<double> PreparedFrequencies = [];
        public DateTime Now = DateTime.UnixEpoch;
        public DateTime KeyAt, PlayAt;
        public TimeSpan PrepareDelay, PreparePttDelay, KeyDelay;
        public Action? DuringPrepare, DuringTune, DuringKey;
        public Exception? KeyError;
        public bool StepSucceeded = true, PlaySucceeded = true;
        public bool PortOpen, UnkeyFails;
        public int PortOpenCount;
        private DateTime _playingUntil;
        private bool _tuning;
        public Oq4PreparedAudio Prepare(Oq4TransmitRequest request, CancellationToken cancellation)
        {
            PreparedFrequencies.Add(request.FrequencyHz);
            Events.Add("prepare"); Now += PrepareDelay; DuringPrepare?.Invoke();
            return new(new float[35078], 12000);
        }
        public void PreparePtt() { Events.Add("prepare-ptt"); if (!PortOpen) { PortOpen = true; PortOpenCount++; Now += PreparePttDelay; } }
        public bool StepDoppler(Oq4TransmitRequest request, TimeSpan budget) { Events.Add("doppler"); return StepSucceeded; }
        public void KeyNow() { Events.Add("key"); KeyAt = Now; Now += KeyDelay; DuringKey?.Invoke(); if (KeyError is not null) throw KeyError; }
        public bool PlayPrepared(Oq4PreparedAudio audio) { Events.Add("play"); PlayAt = Now; _playingUntil = Now.AddSeconds(2.93); return PlaySucceeded; }
        public void StartTune(Oq4TransmitRequest request) { Events.Add("tune"); _tuning = true; DuringTune?.Invoke(); }
        public bool IsPlaying => _tuning || Now < _playingUntil;
        public void StopAudio() { Events.Add("stop"); _tuning = false; _playingUntil = DateTime.MinValue; }
        public void UnkeyNow() { Events.Add("unkey"); if (UnkeyFails) throw new IOException("unkey failed"); }
        public void ReleasePtt() { Events.Add("release-ptt"); PortOpen = false; }
        public void ReleaseDoppler() => Events.Add("release-doppler");
    }
}
