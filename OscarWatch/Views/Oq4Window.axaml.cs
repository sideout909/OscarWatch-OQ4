using System.ComponentModel;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Input;
using System.Collections.ObjectModel;
using OscarWatch.Services;
using OscarWatch.Localization;
using Serilog;
using OscarWatch.Core.Terminal.Modem;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Controls;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using OscarWatch.Ft4;
using OscarWatch.Hardware;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

/// <summary>OQ4 operating window using shared FT4 QSO, PTT and display components.</summary>
public partial class Oq4Window : Window
{
    private readonly Ft4AudioService _audio = new();
    private readonly ObservableCollection<Ft4DecodeRowViewModel> _rows = new();
    private readonly Ft4QsoSequencer _sequencer;
    private readonly Oq4TransmitRunner _transmitter;
    private readonly IQsoLogbookRepository _logbook;
    private readonly LiveQsoLogger _qsoLogger;
    private IReadOnlySet<string> _workedCalls = new HashSet<string>();
    private IReadOnlySet<string> _workedGrids = new HashSet<string>();
    private readonly HashSet<string> _finishedPartners = new(StringComparer.OrdinalIgnoreCase);
    private Ft4PounceTarget? _pounceTarget;
    private string? _lastLoggedKey;
    private bool _logging;
    private string _lastPounceInput = "";
    private ScrollViewer? _decodeScroll;
    private bool _changingOperation;
    private long? _retargetSlot;
    private long _lastKeyedSlot = long.MinValue;
    private int _operatorRevision;
    private DateTime? _frequencyEditAt;
    private double _nextTransmitFrequency = 1500;
    private string L(string key, params object[] args) => LocalizationService.Instance.Get(key, args).Replace("FT4", "OQ4", StringComparison.Ordinal);
    private bool _closeConfirmed;
    private Ft4DecodeRowViewModel? _contextRow;
    private bool _syncingMessage;
    private bool _lastTxFailed;
    private volatile Oq4TransmitRequest? _activeRequest;
    public bool IsTransmissionActive => _armed || _tx is not null || _tuning || _changingOperation;

    private readonly ISettingsService _settings;
    private readonly PttKeyer _ptt;
    private readonly IRigController _rig;
    private readonly MainViewModel? _main;
    private readonly IGpsService _gps;
    private readonly IOrbitPropagator _propagator;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _tx;
    private bool _closed;
    private bool _armed;
    private bool _captureRunning;
    private bool _waitingForStation;
    private bool _tuning;
    private volatile bool _tonePlaying;
    private long _lastSlot = long.MinValue;
    private DateTime _armedAt;
    private int _waterfallTicks;
    private int _clockTicks;
    private readonly List<float> _resampled = new();
    private readonly Oq4ReceiveWindowBuffer _receiveBuffer = new();
    private int _reportedDroppedWindows;
    private readonly List<(long Sample, double Frequency)> _recentDecodes = new();
    private PolyphaseResampler? _receiveResampler;
    private int _receiveGeneration;
    private bool _decoding;

    public Oq4Window()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _rig = App.Services.GetRequiredService<IRigController>();
        _main = App.MainWindow?.DataContext as MainViewModel;
        _gps = App.Services.GetRequiredService<IGpsService>();
        _propagator = App.Services.GetRequiredService<IOrbitPropagator>();
        _ptt = new PttKeyer(_rig, () => _settings.Current.Ft4.ToPttOptions(), "OQ4",
            port => SerialPortConflictHelper.TryDescribeFt4SeparatePttConflict(_settings.Current, port, out var conflict) ? conflict : null);
        _logbook = App.Services.GetRequiredService<IQsoLogbookRepository>();
        _qsoLogger = App.Services.GetRequiredService<LiveQsoLogger>();
        _sequencer = new Ft4QsoSequencer(
            () => Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? ""),
            () => (_settings.Current.GroundStation.GridSquare ?? "")[..Math.Min(4, (_settings.Current.GroundStation.GridSquare ?? "").Length)],
            () => _settings.Current.Ft4.SkipRrr,
            () => _settings.Current.Ft4.HoldTxFrequency,
            () => _settings.Current.Ft4.AutoReply,
            call => Ft4BlockedCallsigns.Matches(_settings.Current.Ft4.BlockedCallsigns, call));
        _transmitter = new Oq4TransmitRunner(new TransmitHardware(this), status =>
        {
            Log.Information("OQ4 TX: {Status}", status);
            var owner = _tx;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || owner is null || !ReferenceEquals(_tx, owner) || owner.IsCancellationRequested) return;
                if (status == "Preparing OQ4 audio and PTT…") return;
                StatusText.Text = status == "Tune active. Select Halt TX to stop."
                    ? L("Ft4.Status.Tuning", ((double)(TxFrequency.Value ?? 1500m)).ToString("0"))
                    : status;
            });
        });
        _ptt.SetManualPromptHandler(prompt => Dispatcher.UIThread.Post(() =>
        {
            if (!_closed) ManualPttPrompt.Text = L(prompt == "key" ? "Ft4.ManualKey" : "Ft4.ManualUnkey");
        }));
        ActivityList.ItemsSource = _rows;
        _logbook.QsosChanged += OnLogbookChanged;
        AutoReply.IsChecked = _settings.Current.Ft4.AutoReply;
        SkipRrr.IsChecked = _settings.Current.Ft4.SkipRrr;
        AutoReply.IsCheckedChanged += (_, _) =>
        {
            _settings.Current.Ft4.AutoReply = AutoReply.IsChecked == true;
            _sequencer.SetAutoReply(AutoReply.IsChecked == true);
            _settings.RequestSave();
            if (AutoReply.IsChecked == true) TryAcceptRecentCaller();
        };
        SkipRrr.IsCheckedChanged += (_, _) => { _settings.Current.Ft4.SkipRrr = SkipRrr.IsChecked == true; _settings.RequestSave(); };
        MessageBox.TextChanged += (_, _) =>
        {
            if (_syncingMessage) return;
            _sequencer.SetTxMessage(MessageBox.Text);
            RetargetPendingTransmit();
        };
        EvenSlot.IsCheckedChanged += (_, _) => { _sequencer.PreferEvenSlot = EvenSlot.IsChecked == true; RetargetPendingTransmit(); };
        ActivityList.SelectionChanged += OnDecodeSelected;
        TxFrequency.Value = (decimal)Math.Clamp(_settings.Current.Ft4.TxAudioHz, 200, 3000);
        _sequencer.TxAudioHz = (double)TxFrequency.Value.Value;
        Volatile.Write(ref _nextTransmitFrequency, _sequencer.TxAudioHz);
        _sequencer.PreferEvenSlot = EvenSlot.IsChecked == true;
        HoldTx.IsChecked = _settings.Current.Ft4.HoldTxFrequency;
        TxFrequency.ValueChanged += (_, _) =>
        {
            if (TxFrequency.Value is not { } value) return;
            _settings.Current.Ft4.TxAudioHz = (double)value;
            _settings.RequestSave();
            _sequencer.TxAudioHz = (double)value;
            Waterfall.TxAudioHz = (double)value;
            if (HoldTx.IsChecked != true) Waterfall.RxAudioHz = (double)value;
            _frequencyEditAt = Ft4Clock.UtcNow;
            if (_tonePlaying)
            {
                var s = _settings.Current.Ft4;
                try { _audio.StartContinuousTone((double)value, s.TxLevel, s.OutputDeviceId, s.OutputDeviceDisplayName); StatusText.Text = L("Ft4.Status.Tuning", ((double)value).ToString("0")); }
                catch (Exception ex) { Disarm(); StatusText.Text = "Tune failed: " + ex.Message; }
            }
        };
        HoldTx.IsCheckedChanged += (_, _) =>
        {
            _settings.Current.Ft4.HoldTxFrequency = HoldTx.IsChecked == true;
            _settings.RequestSave();
            Waterfall.HoldTxFrequency = HoldTx.IsChecked == true;
            if (HoldTx.IsChecked != true) TxFrequency.Value = (decimal)Waterfall.RxAudioHz;
        };
        Waterfall.HoldTxFrequency = HoldTx.IsChecked == true;
        Waterfall.RxAudioHz = Waterfall.TxAudioHz = (double)TxFrequency.Value.Value;
        Waterfall.FrequencySelected += (_, _) => TxFrequency.Value = (decimal)Waterfall.TxAudioHz;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
        Opened += (_, _) => { RefreshStation(); if (StationReady()) StartMonitor(); RefreshHighlights(); UpdateButtons(); _timer.Start(); _ = LoadWorkedAsync(); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _logbook.QsosChanged -= OnLogbookChanged;
            _armed = false;
            _timer.Stop();
            _tx?.Cancel();
            _transmitter.Stop();
            if (_tx is null) DisposeHardware();
        };
    }

    private void StartMonitor()
    {
        if (!StationReady()) { RefreshStation(); return; }
        try
        {
            var settings = _settings.Current.Ft4;
            _audio.StartCapture(settings.InputDeviceId, settings.InputDeviceDisplayName);
            _resampled.Clear();
            _receiveBuffer.Clear();
            _reportedDroppedWindows = 0;
            _recentDecodes.Clear();
            _receiveResampler = new PolyphaseResampler(_audio.CaptureSampleRate, 12000);
            _receiveGeneration++;
            _captureRunning = true;
            Waterfall.StatusText = null;
            StatusText.Text = L("Ft4.Status.Listening");
        }
        catch (Exception ex)
        {
            _captureRunning = false;
            Waterfall.StatusText = "Audio input unavailable — check Settings";
            StatusText.Text = "Audio input unavailable: " + ex.Message;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_waitingForStation && StationReady()) { RefreshStation(); StartMonitor(); UpdateButtons(); }
        if (++_clockTicks >= 20)
        {
            _clockTicks = 0;
            Ft4Clock.Update(_gps.GetFt4ClockOffset());
        }
        var now = Ft4Clock.UtcNow;
        var slot = Oq4SlotClock.SlotNumber(now);
        var seconds = (now - Oq4SlotClock.SlotStart(slot)).TotalSeconds;
        if (_frequencyEditAt is { } edited && now - edited >= TimeSpan.FromMilliseconds(150))
        {
            _frequencyEditAt = null;
            Volatile.Write(ref _nextTransmitFrequency, (double)(TxFrequency.Value ?? 1500m));
        }
        var isOurSlot = Oq4SlotClock.IsEven(slot) == (EvenSlot.IsChecked == true)
            && (Volatile.Read(ref _lastKeyedSlot) == slot
                || _activeRequest?.SlotStart == Oq4SlotClock.SlotStart(slot) && seconds < .15);
        SlotLabel.Text = isOurSlot && _armed ? "TX" : "RX";
        SlotLabel.Classes.Set("ft4-slot-tx", isOurSlot && _armed);
        SlotLabel.Classes.Set("ft4-slot-rx", !isOurSlot || !_armed);
        SlotProgress.Classes.Set("tx", isOurSlot && _armed);
        StopButton.Classes.Set("accent", _armed);
        TuneButton.Classes.Set("accent", _tuning);
        SlotProgress.Value = Math.Clamp(seconds * 100 / Oq4SlotClock.SlotSeconds, 0, 100);
        SlotTime.Text = $"{SlotProgress.Value:0}%";
        ClockText.Text = $"{Oq4SlotClock.SlotStart(slot):HH:mm:ss.f} UTC  +{seconds:0.0}s  {(Oq4SlotClock.IsEven(slot) ? "even" : "odd")}";
        ClockSource.Text = Ft4Clock.MeasuredOffset is not { } measured
            ? L("Ft4.ClockSource.Pc")
            : Ft4Clock.UsingGps ? L("Ft4.ClockSource.Gps", -measured.TotalSeconds) : L("Ft4.ClockSource.PcGpsAgrees");
        if (_main is { } main)
        {
            UplinkText.Text = string.IsNullOrWhiteSpace(main.Frequencies.RadioTransmitText) ? "-" : main.Frequencies.RadioTransmitText;
            DownlinkText.Text = string.IsNullOrWhiteSpace(main.Frequencies.RadioReceiveText) ? "-" : main.Frequencies.RadioReceiveText;
        }
        Waterfall.HoldTxFrequency = HoldTx.IsChecked == true;
        Waterfall.TxAudioHz = (double)(TxFrequency.Value ?? 1500m);
        if (!string.IsNullOrWhiteSpace(_sequencer.TheirCall)
            && Ft4ViewModel.TryPartnerRxHz(_rows.Select(r => r.Message), _sequencer.TheirCall, out var partnerHz))
            Waterfall.RxAudioHz = partnerHz;
        else if (!Waterfall.HoldTxFrequency && string.IsNullOrWhiteSpace(_sequencer.TheirCall))
            Waterfall.RxAudioHz = Waterfall.TxAudioHz;

        if (_captureRunning && ++_waterfallTicks >= 2)
        {
            _waterfallTicks = 0;
            var scratch = new float[4096];
            var n = _audio.CopyMonitorSamples(scratch);
            var bins = new float[Ft4WaterfallControl.SpectrumColumns];
            if (n >= 512 && Ft4SpectrumAnalyzer.TryComputePassband(scratch.AsSpan(0, n), _audio.CaptureSampleRate, bins))
                Waterfall.Spectrum = bins;
            int count;
            while ((count = _audio.ReadCaptureSamples(scratch)) > 0)
                AppendReceiveSamples(scratch.AsSpan(0, count), _audio.CaptureSampleRate);
            StartReceiveDecode(Ft4Clock.UtcNow);
        }

        if (!_armed || _tx is not null || _changingOperation) return;
        var watchdog = Ft4TxWatchdog.ClampMinutes(_settings.Current.Ft4.TxWatchdogMinutes);
        if (Ft4TxWatchdog.ShouldHalt(_armed, _tuning, watchdog, now, _armedAt))
        {
            Disarm();
            StatusText.Text = $"TX stopped after the {watchdog}-minute watchdog.";
            return;
        }
        var leadSeconds = _settings.Current.Ft4.ToPttOptions().ClampedLeadMs / 1000.0;
        // Prepare as soon as TX is armed, on a dedicated worker, as FT4 does.
        // The 400 ms silent lead-in is played from the UTC slot boundary.
        if (_frequencyEditAt is not null) return;
        var nextSlot = Oq4SlotClock.NextTransmitSlotAfterEdit(now, EvenSlot.IsChecked == true, _retargetSlot, leadSeconds);
        _retargetSlot = null;
        if (nextSlot == _lastSlot) return;
        _lastSlot = nextSlot;
        _ = SendAsync(audioOnly: false, Oq4SlotClock.BurstStart(nextSlot));
    }

    private void AppendReceiveSamples(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate < 12000) return;
        if (_receiveResampler is null || _receiveResampler.InputRate != sampleRate)
            _receiveResampler = new PolyphaseResampler(sampleRate, 12000);
        _resampled.Clear();
        _receiveResampler.Process(samples, _resampled);
        _receiveBuffer.Append(_resampled.ToArray());
    }

    private void StartReceiveDecode(DateTime now)
    {
        // Snapshot on the slot clock even while the worker is busy. Decoder
        // runtime must not determine which parts of the next packet survive.
        _receiveBuffer.CaptureCompletedWindows(now);
        if (_receiveBuffer.DroppedWindows != _reportedDroppedWindows)
        {
            _reportedDroppedWindows = _receiveBuffer.DroppedWindows;
            if (!_armed && _tx is null && !_lastTxFailed) StatusText.Text = "RX processing is behind; an old slot was skipped.";
        }
        if (_decoding || !_receiveBuffer.TryTake(out var window) || window is null) return;
        var audio = window.Audio;
        var firstSample = window.FirstSample;
        var generation = _receiveGeneration;
        _decoding = true;
        _ = Task.Run(() => Oq4Decoder.DecodeWideband(audio)).ContinueWith(task =>
            Dispatcher.UIThread.Post(() =>
            {
                _decoding = false;
                if (_closed || generation != _receiveGeneration) return;
                if (task.IsFaulted)
                {
                    StatusText.Text = "OQ4 receive failed: " + task.Exception?.GetBaseException().Message;
                    return;
                }
                foreach (var result in task.Result)
                {
                    var sample = firstSample + (long)(result.TimeSeconds * 12000);
                    _recentDecodes.RemoveAll(x => x.Sample < _receiveBuffer.TotalSamples - 10 * 12000);
                    if (_recentDecodes.Any(x => Math.Abs(x.Sample - sample) < 12000
                                               && Math.Abs(x.Frequency - result.FrequencyHz) < 30)) continue;
                    _recentDecodes.Add((sample, result.FrequencyHz));
                    var when = Ft4Clock.UtcNow.AddSeconds((sample - _receiveBuffer.TotalSamples) / 12000.0);
                    Ft4MessageCodec.TryParse(result.Message, out var callTo, out var callDe, out var extra);
                    var slotStart = Oq4SlotClock.SlotStart(Oq4SlotClock.SlotNumber(when));
                    var own = string.Equals(Ft4MessageCodec.NormalizeCall(callDe ?? ""),
                        Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? ""), StringComparison.OrdinalIgnoreCase);
                    var message = new Ft4DecodedMessage(slotStart, result.Message, (float)result.FrequencyHz,
                        (float)((when-slotStart).TotalSeconds - Oq4SlotClock.BurstOffsetSeconds), result.SnrDb,
                        callTo, callDe, extra, own);
                    AddActivity(message);
                    if (!own)
                    {
                        if (Ft4TxWatchdog.IsReply(callTo, callDe, _settings.Current.GroundStation.Callsign)) _armedAt = Ft4Clock.UtcNow;
                        if (_pounceTarget?.Matches(message, _settings.Current.GroundStation.Callsign) == true)
                        {
                            _pounceTarget = null; Pounce.IsChecked = false;
                            AnswerDecode(message);
                        }
                        else if (_sequencer.OnDecoded(message)) _ = LogContactAsync(manual: false);
                        SyncSequencer();
                    }
                    // A background decode must not replace the operator's Halt/CQ/pounce status.
                }
            }));
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_armed || _tuning || _changingOperation) return;
        _changingOperation = true;
        try
        {
            var revision = _operatorRevision;
            await WaitForTransmitCleanupAsync();
            if (_closed || revision != _operatorRevision) return;
            ValidateTransmission(audioOnly: false);
            Oq4TransmitRunner.ArmMessage(_sequencer, MessageBox.Text, EvenSlot.IsChecked == true,
                (double)(TxFrequency.Value ?? 1500m));
            _lastTxFailed = false;
            _armed = _sequencer.TransmitEnabled;
            _armedAt = Ft4Clock.UtcNow;
            _lastSlot = long.MinValue;
            SyncSequencer();
            StatusText.Text = L("Ft4.Status.TxEnabled");
            TryAcceptRecentCaller();
        }
        catch (Exception ex) { ShowTransmitError("Cannot arm OQ4", ex); }
        finally { _changingOperation = false; UpdateButtons(); }
    }

    private void ValidateTransmission(bool audioOnly)
    {
        if (!audioOnly && !StationReady())
            throw new InvalidOperationException("Enter your callsign and grid in station settings.");
        var ft4 = App.Services.GetRequiredService<Ft4ModemService>();
        if (ft4.IsTransmissionActive)
            throw new InvalidOperationException("Halt FT4 transmission first. FT4 can remain open for receive.");
        if (!audioOnly && _main is { } main
            && !Ft4SatelliteEligibility.IsAllowed(main.Frequencies.SatelliteName, main.FocusedNoradId, main.Frequencies.SelectedMode))
            throw new InvalidOperationException("The selected satellite or FM transponder blocks digital transmission.");

    }

    private async Task SendAsync(bool audioOnly, DateTime? burstStart, bool tune = false)
    {
        if (_tx is not null || (audioOnly && _armed)) return;
        var options = _settings.Current.Ft4.ToPttOptions();
        var request = new Oq4TransmitRequest((MessageBox.Text ?? "").Trim().ToUpperInvariant(),
            (double)(TxFrequency.Value ?? 1500m), burstStart?.AddSeconds(-Oq4SlotClock.BurstOffsetSeconds),
            options.ClampedLeadMs, options.ClampedTailMs, audioOnly, tune,
            KeepPttPortOpen: _armed && !audioOnly && !tune && burstStart.HasValue);
        Volatile.Write(ref _nextTransmitFrequency, request.FrequencyHz);
        var sentRequest = request;
        var operationRevision = _operatorRevision;
        var cancellation = new CancellationTokenSource();
        _tx = cancellation;
        _activeRequest = request;
        _tuning = tune;
        _tonePlaying = false;
        _lastTxFailed = false;
        UpdateButtons();
        IDisposable? radioSession = null;
        try
        {
            ValidateTransmission(audioOnly);
            if (!audioOnly)
                radioSession = App.Services.GetRequiredService<Ft4ModemService>().BeginExternalTransmission();
            var outcome = await Task.Factory.StartNew(() => _transmitter.Run(request, cancellation.Token,
                latestFrequency: request.SlotStart.HasValue && !tune ? () => Volatile.Read(ref _nextTransmitFrequency) : null,
                committed: effective =>
                {
                    sentRequest = effective; _activeRequest = effective;
                    Log.Information("OQ4 TX buffer selected: {Message} at {Hz:0.0} Hz for {SlotUtc}",
                        effective.Message, effective.FrequencyHz, effective.SlotStart);
                }),
                CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            if (!_closed && !tune && outcome == Oq4TransmitOutcome.Sent)
            {
                request = sentRequest;
                var at = request.SlotStart ?? Ft4Clock.UtcNow;
                Ft4MessageCodec.TryParse(request.Message, out var to, out var de, out var extra);
                AddActivity(new Ft4DecodedMessage(at, request.Message, (float)request.FrequencyHz, 0, 0, to, de, extra,
                    false, IsTransmitted: true));
                if (!audioOnly && string.Equals(request.Message, _sequencer.CurrentTxMessage, StringComparison.Ordinal)
                    && _sequencer.OnTxCompleted()) await LogContactAsync(manual: false);
                if (!audioOnly) SyncSequencer();
                if (operationRevision == _operatorRevision && !cancellation.IsCancellationRequested)
                    StatusText.Text = L("Ft4.Status.TxDone", request.Message);
            }
        }
        catch (OperationCanceledException) { /* Preserve the status set by the newer operator action. */ }
        catch (Exception ex) when (cancellation.IsCancellationRequested)
        {
            Log.Debug(ex, "OQ4 cancelled TX preparation finished with an error");

        }
        catch (Exception ex)
        {
            _armed = false;
            _sequencer.HaltTx();
            if (!_closed) ShowTransmitError("OQ4 transmit failed", ex);
        }
        finally
        {
            try { radioSession?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Could not restore FT4 CAT timing after OQ4 transmission"); }
            _tx = null; _activeRequest = null; _tuning = false; _tonePlaying = false;
            cancellation.Dispose();
            if (_closed) DisposeHardware();
            else { if (_settings.Current.Ft4.PttMethod != PttMethod.Manual) ManualPttPrompt.Text = ""; UpdateButtons(); }
        }
    }

    private void UpdateButtons()
    {
        SendButton.IsEnabled = !_armed && !_tuning && !_changingOperation;
        TuneButton.IsEnabled = _captureRunning && !_changingOperation;
        StopButton.IsEnabled = _armed || _tx is not null || _changingOperation;
        ReportButton.IsEnabled = Send73Button.IsEnabled = !string.IsNullOrWhiteSpace(_sequencer.TheirCall);
        ManualLogButton.IsEnabled = !string.IsNullOrWhiteSpace(_sequencer.TheirCall)
            && _sequencer.Phase is Ft4QsoPhase.InQso or Ft4QsoPhase.Finished;
        StopButton.Classes.Set("accent", _armed);
        TuneButton.Classes.Set("accent", _tuning);
    }

    private void RetargetPendingTransmit()
    {
        if (_armed && _tx is not null && _activeRequest is { } pending && !_ptt.IsKeyed && !_audio.IsPlaying
            && (!string.Equals(pending.Message, (MessageBox.Text ?? "").Trim().ToUpperInvariant(), StringComparison.Ordinal)
                || pending.SlotStart is { } slot && Oq4SlotClock.IsEven(Oq4SlotClock.SlotNumber(slot)) != (EvenSlot.IsChecked == true)))
        {
            _retargetSlot = pending.SlotStart is { } scheduled ? Oq4SlotClock.SlotNumber(scheduled) : null;
            _tx.Cancel();
            _lastSlot = long.MinValue;
        }
    }

    private void ShowTransmitError(string prefix, Exception error)
    {
        _lastTxFailed = true;
        Log.Error(error, "OQ4 {Operation}", prefix);
        StatusText.Text = prefix + ": " + PttFailureLocalizer.Localize(error.Message, LocalizationService.Instance);
        UpdateButtons();
    }

    private sealed class TransmitHardware(Oq4Window window) : IOq4TransmitHardware
    {
        private bool _heldDoppler;
        private bool _stepDoppler;
        public Oq4PreparedAudio Prepare(Oq4TransmitRequest request, CancellationToken cancellation)
        {
            window.ValidateTransmission(request.AudioOnly);
            var settings = window._settings.Current.Ft4;
            // Like FT4, do the CAT power query at preparation time, off the UI
            // thread; never query it again between the PTT lead and the tone.
            if (!request.AudioOnly && window._rig.TryGetUplinkRfPowerWatts(out var watts) && Ft4RfPowerLimit.ExceedsLimit(watts))
            {
                if (!settings.AutoLowerRfPower || !window._rig.TrySetUplinkRfPowerWatts(Ft4RfPowerLimit.MaxWatts)
                    || !window._rig.TryGetUplinkRfPowerWatts(out watts) || Ft4RfPowerLimit.ExceedsLimit(watts))
                    throw new InvalidOperationException($"Reduce radio power to {Ft4RfPowerLimit.MaxWatts} W or less.");
            }
            return PrepareFrequencyChange(request, cancellation);
        }
        public Oq4PreparedAudio PrepareFrequencyChange(Oq4TransmitRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var settings = window._settings.Current.Ft4;
            var slope = 0.0;
            _stepDoppler = !request.AudioOnly && !request.Tune && settings.AudioDopplerTx
                && window._rig.GetStatus() is { IsConnected: true, IsTracking: true }
                && window._main is { Frequencies.SelectedMode: not null };
            if (_stepDoppler && request.SlotStart is { } slot && window._main is { } main
                && main.Frequencies.SelectedMode is { } mode && !string.IsNullOrWhiteSpace(main.FocusedNoradId))
            {
                var anchor = slot.AddSeconds(Oq4SlotClock.BurstOffsetSeconds);
                const double duration = Oq4Waveform.PacketDurationSeconds;
                var rr0 = window._propagator.GetLookAngles(main.FocusedNoradId, window._settings.Current.GroundStation, anchor).RangeRateKmPerSec;
                var rr1 = window._propagator.GetLookAngles(main.FocusedNoradId, window._settings.Current.GroundStation, anchor.AddSeconds(duration)).RangeRateKmPerSec;
                var txOffset = main.Frequencies.TransmitOffsetKHz + settings.GetUplinkCalibrationKHz(main.Frequencies.SatelliteName);
                var ul0 = Ft4DopplerShift.ComputeShiftsHz(mode, rr0, main.Frequencies.ReceiveOffsetKHz, txOffset).UplinkHz;
                var ul1 = Ft4DopplerShift.ComputeShiftsHz(mode, rr1, main.Frequencies.ReceiveOffsetKHz, txOffset).UplinkHz;
                slope = Ft4AudioDoppler.TxPrecompAudioSlope(Ft4DopplerShift.SlopeHzPerSec(ul0, ul1, duration),
                    TransponderOperatingModes.GetEffectiveUplinkMode(mode, main.Frequencies.IsCwUplink));
            }
            var bits = request.Tune ? new byte[174] : Ft8Native.EncodeOq4Codeword(request.Message);
            var packet = request.Tune ? new float[1] : Oq4Waveform.EncodePacket(bits, request.FrequencyHz, slope);
            var prefix = request.SlotStart.HasValue ? (int)(12000 * Oq4SlotClock.BurstOffsetSeconds) : 0;
            var pcm = new float[prefix + packet.Length];
            packet.CopyTo(pcm, prefix);
            var prepared = window._audio.PreparePcm(pcm, settings.TxLevel, settings.OutputDeviceId, settings.OutputDeviceDisplayName);
            return new Oq4PreparedAudio(prepared.Samples, prepared.SampleRate);
        }
        public void PreparePtt()
        {
            window._ptt.Prepare();
            var options = window._settings.Current.Ft4.ToPttOptions();
            Log.Information("OQ4 PTT prepared: {Method}, {Line}, lead {LeadMs} ms, tail {TailMs} ms",
                options.Method, options.Line, options.ClampedLeadMs, options.ClampedTailMs);
        }
        public bool StepDoppler(Oq4TransmitRequest request, TimeSpan budget)
        {
            if (!_stepDoppler || request.SlotStart is not { } slot) return true;
            window._rig.SetFt4SlotGatedDoppler(true); _heldDoppler = true;
            return window._rig.TryForceFt4DopplerStep(slot.AddSeconds(Oq4SlotClock.BurstOffsetSeconds), budget);
        }
        public void KeyNow()
        {
            window._ptt.KeyNow();
            if (window._activeRequest?.SlotStart is { } slot)
                Volatile.Write(ref window._lastKeyedSlot, Oq4SlotClock.SlotNumber(slot));
            Log.Information("OQ4 PTT requested via {Method}; keyed state {Keyed}", window._ptt.Method, window._ptt.IsKeyed);
        }
        public bool PlayPrepared(Oq4PreparedAudio audio) => window._audio.TryPlayPrepared(audio.Samples, audio.SampleRate);
        public void StartTune(Oq4TransmitRequest request)
        {
            var settings = window._settings.Current.Ft4;
            window._audio.StartContinuousTone(settings.TxAudioHz, settings.TxLevel, settings.OutputDeviceId, settings.OutputDeviceDisplayName);
            window._tonePlaying = true;
        }
        public bool IsPlaying => window._audio.IsPlaying;
        public void StopAudio() => window._audio.StopPlayback();
        public void UnkeyNow() => window._ptt.UnkeyNow();
        public void ReleasePtt() => window._ptt.ReleasePort();
        public void ReleaseDoppler() { if (_heldDoppler) window._rig.SetFt4SlotGatedDoppler(false); _heldDoppler = false; }
    }

    private void Disarm()
    {
        _operatorRevision++;
        _armed = false;
        _retargetSlot = null;
        _frequencyEditAt = null;
        _sequencer.HaltTx();
        _pounceTarget = null; Pounce.IsChecked = false;
        _tonePlaying = false;
        _tx?.Cancel();
        _transmitter.Stop();
        SyncPounceDisplay();
        UpdateButtons();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e) { Disarm(); StatusText.Text = L("Ft4.Status.TxHalted"); }
    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _rows.Clear();
        Waterfall.RxAudioHz = Waterfall.TxAudioHz;
        StatusText.Text = L("Ft4.Status.DecodesCleared");
    }

    private async void OnLogbookClick(object? sender, RoutedEventArgs e) => await LogContactAsync(manual: true);

    private async void OnSaveActivityClick(object? sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { StatusText.Text = "There is no activity to save."; return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save OQ4 activity",
                SuggestedFileName = $"OscarWatch-OQ4-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt"
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(Ft4ActivityLog.FormatAll(_rows.Select(r => r.Message)).Replace("# OscarWatch FT4 activity", "# OscarWatch OQ4 activity", StringComparison.Ordinal));
            if (!_closed) StatusText.Text = $"Activity saved to {file.Name}.";
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = "Could not save activity: " + ex.Message; }
    }

    private async void OnTuneClick(object? sender, RoutedEventArgs e)
    {
        if (_tuning) { Disarm(); StatusText.Text = L("Ft4.Status.TuneStopped"); return; }
        if (_changingOperation || !_captureRunning) return;
        _changingOperation = true;
        try
        {
            Disarm();
            var revision = _operatorRevision;
            await WaitForTransmitCleanupAsync();
            if (_closed || revision != _operatorRevision) return;
        }
        finally { _changingOperation = false; UpdateButtons(); }
        await SendAsync(audioOnly: false, null, tune: true);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_tx is not null || _armed) { StatusText.Text = "Halt TX before changing settings."; return; }
        var dialog = new Ft4SettingsWindow { DataContext = App.Services.GetRequiredService<Ft4ViewModel>() };
        dialog.Title = L("Window.Ft4Settings.Title");
        await dialog.ShowDialog(this);
        if (!_closed)
        {
            TxFrequency.Value = (decimal)Math.Clamp(_settings.Current.Ft4.TxAudioHz, 200, 3000);
            HoldTx.IsChecked = _settings.Current.Ft4.HoldTxFrequency;
            AutoReply.IsChecked = _settings.Current.Ft4.AutoReply;
            SkipRrr.IsChecked = _settings.Current.Ft4.SkipRrr;
            RefreshStation();
            StartMonitor();
            RefreshHighlights(); UpdateButtons();
        }
    }

    private async void OnCqClick(object? sender, RoutedEventArgs e)
    {
        if (_changingOperation) return;
        _changingOperation = true;
        try
        {
            ValidateTransmission(audioOnly: false);
            Disarm();
            var revision = _operatorRevision;
            await WaitForTransmitCleanupAsync();
            if (_closed || revision != _operatorRevision) return;
            _sequencer.StartCq(EvenSlot.IsChecked == true);
            _lastLoggedKey = null;
            _armed = true; _armedAt = Ft4Clock.UtcNow; _lastSlot = long.MinValue;
            SyncSequencer();
            TryAcceptRecentCaller();
            StatusText.Text = _sequencer.Phase == Ft4QsoPhase.InQso
                ? L("Ft4.Status.Answering", _sequencer.TheirCall ?? "") : L("Ft4.Status.CallingCq");
        }
        catch (Exception ex) { ShowTransmitError("Cannot call CQ", ex); }
        finally { _changingOperation = false; UpdateButtons(); }
    }

    private async Task WaitForTransmitCleanupAsync()
    {
        // Wait for the old worker's finally block before starting another operation.
        // Otherwise it can clear the new operation's cancellation/PTT state.
        while (_tx is not null && !_closed) await Task.Delay(10);
    }

    private void AddActivity(Ft4DecodedMessage message)
    {
        var row = new Ft4DecodeRowViewModel(message, dtWsjtxConvention: false);
        _rows.Insert(0, row);
        while (_rows.Count > 200) _rows.RemoveAt(_rows.Count - 1);
        RefreshHighlights();
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || ActivityList.ItemCount == 0) return;
            ActivityList.ScrollIntoView(0);
            _decodeScroll ??= ActivityList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_decodeScroll is not null) _decodeScroll.Offset = new Vector(_decodeScroll.Offset.X, 0);
        }, DispatcherPriority.Background);
    }

    private void OnLogbookChanged(long logbookId) => Dispatcher.UIThread.Post(() => { if (!_closed) _ = LoadWorkedAsync(); });

    private async Task LoadWorkedAsync()
    {
        try
        {
            var (calls, grids) = await _logbook.LoadWorkedCallAndGridFieldsAsync();
            if (_closed) return;
            _workedCalls = calls; _workedGrids = grids; RefreshHighlights();
        }
        catch (Exception ex) { Log.Warning(ex, "OQ4 could not read worked calls/grids"); }
    }

    private void RefreshHighlights()
    {
        var settings = _settings.Current.Ft4;
        ActivityList.FontSize = settings.DecodeFontSize;
        foreach (var header in new[] { HeaderTime, HeaderKind, HeaderDt, HeaderSnr, HeaderMessage, HeaderHz })
            header.FontSize = settings.DecodeFontSize;
        SyncPounceDisplay();
        Waterfall.DisplayRangeDb = settings.WaterfallRangeDb;
        foreach (var row in _rows)
            row.RefreshHighlight(_settings.Current.GroundStation.Callsign, _sequencer.TheirCall,
                settings.CallingMeColour, settings.ReplyingColour, settings.NewCallColour,
                settings.NewGridColour, settings.CqColour, _workedCalls, _workedGrids,
                _finishedPartners, settings.CallingMeTextColour, settings.ReplyingTextColour,
                settings.NewCallTextColour, settings.NewGridTextColour, settings.CqTextColour,
                settings.TxTextColour, _pounceTarget);
    }

    private void SyncSequencer()
    {
        var message = _sequencer.CurrentTxMessage;
        _syncingMessage = true;
        try
        {
            MessageBox.Text = message;
            EvenSlot.IsChecked = _sequencer.PreferEvenSlot;
            TxFrequency.Value = (decimal)_sequencer.TxAudioHz;
        }
        finally { _syncingMessage = false; }
        var wasArmed = _armed;
        _armed = _sequencer.TransmitEnabled;
        if (_armed && !wasArmed) { _armedAt = Ft4Clock.UtcNow; _lastSlot = long.MinValue; }
        if (!_armed && _tx is not null) { _tx.Cancel(); _transmitter.Stop(); }
        RetargetPendingTransmit();
        UpdateButtons(); RefreshHighlights();
    }

    private void TryAcceptRecentCaller()
    {
        if (!_sequencer.PrepareAutoReply()) return;
        var cutoff = Ft4Clock.UtcNow.AddSeconds(-3 * Oq4SlotClock.SlotSeconds);
        foreach (var row in _rows)
        {
            var message = row.Message;
            if (message.SlotUtc < cutoff) break;
            if (!message.IsReceiveActivity || _sequencer.IsHistoricDecode(message.SlotUtc)) continue;
            _sequencer.OnDecoded(message);
            if (_sequencer.Phase != Ft4QsoPhase.InQso) continue;
            _armedAt = Ft4Clock.UtcNow; SyncSequencer(); return;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !IsTransmissionActive) return;
        e.Cancel = true;
        var stop = await SimpleConfirmDialog.ShowAsync(this, "Close OQ4 while transmitting?",
            "Stop OQ4 transmission and close this window?");
        if (!stop || _closed) return;
        Disarm(); _closeConfirmed = true; Close();
    }

    private void OnDecodeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        _contextRow = (sender as Control)?.DataContext as Ft4DecodeRowViewModel;
        e.Handled = true;
    }

    private void OnDecodeContextOpening(object? sender, CancelEventArgs e)
    {
        _contextRow ??= ActivityList.SelectedItem as Ft4DecodeRowViewModel;
        if (_contextRow?.Message is not { IsReceiveActivity: true } message || string.IsNullOrWhiteSpace(message.CallDe))
        {
            _contextRow = null;
            e.Cancel = true;
        }
    }

    private void OnDecodeContextClosed(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _contextRow = null);

    private void OnPounceStationClick(object? sender, RoutedEventArgs e)
    {
        if (_contextRow?.Message is not { IsReceiveActivity: true, CallDe: { } call }) return;
        if (!Ft4PounceTarget.TryParse(call, out var target)) return;
        ArmPounce(target);
    }

    private void OnDecodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ActivityList.SelectedItem is Ft4DecodeRowViewModel row) AnswerDecode(row.Message);
    }

    private async void AnswerDecode(Ft4DecodedMessage message)
    {
        if (!message.IsReceiveActivity || string.IsNullOrWhiteSpace(message.CallDe) || _changingOperation) return;
        try
        {
            ValidateTransmission(audioOnly: false);
            if (_tuning)
            {
                _changingOperation = true;
                Disarm();
                var revision = _operatorRevision;
                await WaitForTransmitCleanupAsync();
                if (_closed || revision != _operatorRevision) return;
            }
            _pounceTarget = null; Pounce.IsChecked = false;
            _sequencer.StartAnswer(message, !Oq4SlotClock.IsEven(Oq4SlotClock.SlotNumber(message.SlotUtc)));
            _lastLoggedKey = null; _armedAt = Ft4Clock.UtcNow;
            SyncSequencer();
            Waterfall.RxAudioHz = Math.Clamp(message.FreqHz, 200, 3000);
            StatusText.Text = L("Ft4.Status.Answering", message.Text);
        }
        catch (Exception ex) { ShowTransmitError("Cannot answer station", ex); }
        finally { _changingOperation = false; UpdateButtons(); }
    }

    private async void OnReportClick(object? sender, RoutedEventArgs e)
    {
        if (!await PrepareStandardMessageAsync()) return;
        float? snr = _rows.FirstOrDefault(r => r.Message.IsReceiveActivity &&
            string.Equals(r.Message.CallDe, _sequencer.TheirCall, StringComparison.OrdinalIgnoreCase))?.Message.SnrDb;
        if (!_sequencer.ForceReport(snr)) return;
        _armedAt = Ft4Clock.UtcNow; SyncSequencer();
        StatusText.Text = L("Ft4.Status.SendingReport", _sequencer.TheirCall ?? "");
    }

    private async void On73Click(object? sender, RoutedEventArgs e)
    {
        if (!await PrepareStandardMessageAsync() || !_sequencer.Force73()) return;
        _armedAt = Ft4Clock.UtcNow; SyncSequencer();
        StatusText.Text = L("Ft4.Status.Sending73", _sequencer.TheirCall ?? "");
    }

    private async Task<bool> PrepareStandardMessageAsync()
    {
        if (_changingOperation) return false;
        try
        {
            ValidateTransmission(audioOnly: false);
            if (_tuning)
            {
                _changingOperation = true;
                Disarm();
                var revision = _operatorRevision;
                await WaitForTransmitCleanupAsync();
                if (revision != _operatorRevision) return false;
            }
            return !_closed;
        }
        catch (Exception ex) { ShowTransmitError("Cannot send message", ex); return false; }
        finally { _changingOperation = false; UpdateButtons(); }
    }

    private async void OnPounceClick(object? sender, RoutedEventArgs e)
    {
        if (_pounceTarget is not null)
        {
            _pounceTarget = null; Pounce.IsChecked = false; RefreshHighlights(); StatusText.Text = L("Ft4.Status.PounceCancelled"); return;
        }
        var dialog = new Ft4PounceWindow(_lastPounceInput);
        var target = await dialog.ShowDialog<Ft4PounceTarget?>(this);
        if (_closed) return;
        if (target is not null)
        {
            ArmPounce(target);
        }
        Pounce.IsChecked = _pounceTarget is not null; RefreshHighlights();
    }

    private void ArmPounce(Ft4PounceTarget target)
    {
        _lastPounceInput = target.Value;
        _pounceTarget = target;
        SyncPounceDisplay(); RefreshHighlights();
        StatusText.Text = L("Ft4.Status.PounceWaiting", target.Value);
    }

    private void SyncPounceDisplay()
    {
        Pounce.IsChecked = _pounceTarget is not null;
        var label = _pounceTarget is null ? L("Ft4.Pounce") : L("Ft4.Pounce.Armed", _pounceTarget.Value);
        Pounce.Content = label;
        Avalonia.Automation.AutomationProperties.SetName(Pounce, label);
    }

    private async Task LogContactAsync(bool manual)
    {
        if (_logging) return;
        var call = _sequencer.TheirCall;
        if (call is null || (!manual && !_sequencer.CanLog())) return;
        var key = $"{call}|{_sequencer.ReportSent}|{_sequencer.ReportReceived}|{_sequencer.TheirGrid}";
        if (!manual && key == _lastLoggedKey) return;
        _logging = true;
        try
        {
            await _qsoLogger.LogAsync(new LiveQsoEntry(Ft4Clock.UtcNow, call,
                Ft4MessageCodec.NormalizeSnrReport(_sequencer.ReportSent),
                Ft4MessageCodec.NormalizeSnrReport(_sequencer.ReportReceived), _sequencer.TheirGrid ?? "",
                "OQ4", "OQ4", manual ? "OQ4 manual log" : ""));
            _lastLoggedKey = key; _finishedPartners.Add(call);
            if (!_closed) { StatusText.Text = "OQ4 QSO logged: " + call; await LoadWorkedAsync(); }
        }
        catch (Exception ex) { Log.Warning(ex, "OQ4 logbook save failed"); if (!_closed) StatusText.Text = "Could not save OQ4 contact: " + ex.Message; }
        finally { _logging = false; }
    }

    private bool StationReady() => Ft4StationIdentity.IsReady(
        _settings.Current.GroundStation.Callsign, _settings.Current.GroundStation.GridSquare);

    private void RefreshStation()
    {
        StationBanner.IsVisible = !StationReady();
        _waitingForStation = StationBanner.IsVisible;
        Waterfall.ShowTuningBrackets = !StationBanner.IsVisible;
        var gap = Ft4StationIdentity.Gap(_settings.Current.GroundStation.Callsign, _settings.Current.GroundStation.GridSquare);
        StationMessage.Text = L(gap switch
        {
            Ft4StationGap.Callsign => "Ft4.Station.NeedCallsign",
            Ft4StationGap.Locator => "Ft4.Station.NeedLocator",
            _ => "Ft4.Station.NeedBoth"
        });
        Avalonia.Automation.AutomationProperties.SetName(StationBanner, StationMessage.Text);
        if (StationBanner.IsVisible) StatusText.Text = StationMessage.Text;
        var call = Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? "");
        var grid = (_settings.Current.GroundStation.GridSquare ?? "").Trim().ToUpperInvariant();
        MessageBox.Watermark = StationReady() ? Ft4MessageCodec.BuildCq(call, grid[..4]) : L("Ft4.TxMessage.Watermark");
    }

    private async void OnEnterStationDetailsClick(object? sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetRequiredService<Ft4ViewModel>();
        var dialog = new Ft4StationSetupWindow { DataContext = vm.CreateStationSetup() };
        if (await dialog.ShowDialog<bool>(this) && !_closed)
        {
            if (_main is { } main) main.RefreshStationFromSettings();
            RefreshStation();
            StartMonitor();
        }
    }

    private void DisposeHardware() { _audio.Dispose(); _ptt.Dispose(); }
}
