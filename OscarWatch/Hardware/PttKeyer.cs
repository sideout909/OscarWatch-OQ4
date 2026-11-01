using System.IO.Ports;
using System.Runtime.CompilerServices;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>
/// Keys the uplink using CAT, handshake lines, VOX or a manual prompt. FT4 and the Terminal built-in
/// modem each own one, with their own <see cref="PttOptions"/>.
/// </summary>
public sealed class PttKeyer : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PttKeyer>();

    private sealed class RadioOwner { public PttKeyer? Keyer; }
    private static readonly ConditionalWeakTable<IRigController, RadioOwner> Owners = new();
    private readonly RadioOwner _owner;
    private readonly IRigController _rig;
    private readonly Func<PttOptions> _options;
    private readonly string _label;
    private readonly Func<string, string?>? _describePortConflict;
    private readonly PttSessionMarker? _marker;
    private readonly object _portGate = new();
    private SerialPort? _separatePort;
    private volatile bool _keyed;
    private Action<string>? _manualPrompt;

    /// <param name="label">Feature name for logs, for example <c>FT4</c> or <c>Terminal</c>.</param>
    /// <param name="describePortConflict">Returns a message when the separate PTT port is in use elsewhere.</param>
    /// <param name="marker">Records that PTT is up, so a crash while keyed is noticed at the next start.</param>
    public PttKeyer(
        IRigController rig,
        Func<PttOptions> options,
        string label,
        Func<string, string?>? describePortConflict = null,
        PttSessionMarker? marker = null)
    {
        _rig = rig;
        _owner = Owners.GetValue(rig, _ => new RadioOwner());
        _options = options;
        _label = label;
        _describePortConflict = describePortConflict;
        _marker = marker;
    }

    public void SetManualPromptHandler(Action<string>? handler) => _manualPrompt = handler;

    public PttMethod Method => _options().Method;

    public bool IsKeyed => _keyed;

    /// <summary>
    /// Assert PTT without the lead-in wait. CAT and the CAT-port handshake line share the
    /// rig thread with Doppler, so the command has to be queued at the slot boundary,
    /// before that write, or the radio stays in receive for the start of the burst.
    /// </summary>
    public void KeyNow() => TryAssert();

    /// <summary>Open a separate PTT adapter while preparing, leaving its line idle.
    /// This moves USB/serial open latency out of the timed key operation.</summary>
    public void Prepare()
    {
        lock (_owner)
        {
            ReserveRadio();
            try { PrepareCore(); }
            catch { if (!_keyed) _owner.Keyer = null; throw; }
        }
    }

    private void ReserveRadio()
    {
        if (_owner.Keyer is { } other && other != this)
            throw new InvalidOperationException($"{other._label} owns PTT. Stop its transmission before enabling {_label}.");
        _owner.Keyer = this;
    }

    private void PrepareCore()
    {
        var options = _options();
        if (options.Method != PttMethod.SeparateComPort) return;
        lock (_portGate)
        {
            EnsureSeparatePort(options);
            if (!_keyed) SetLine(_separatePort!, options.Line, options.Invert);
        }
    }

    public async Task KeyAsync(CancellationToken cancellationToken = default)
    {
        if (!TryAssert())
            return;

        // VOX keys from the audio itself. The lead wait is for CAT and hardware lines,
        // and TryAssert already returned false for VOX on a fresh key (see below).
        var lead = _options().ClampedLeadMs;
        if (lead > 0)
            await Task.Delay(lead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns false when PTT was already asserted, or when VOX needs no lead wait.</summary>
    private bool TryAssert()
    {
        lock (_owner)
        {
            if (_keyed) return false;
            ReserveRadio();
            try { return TryAssertCore(); }
            catch { _owner.Keyer = null; throw; }
        }
    }

    private bool TryAssertCore()
    {
        if (_keyed)
            return false;

        var options = _options();
        if (options.Method != PttMethod.Vox)
            _marker?.MarkKeyed(_label, options.Method);

        switch (options.Method)
        {
            case PttMethod.Vox:
                _keyed = true;
                return false;

            case PttMethod.Cat:
                _rig.SetPtt(true);
                break;

            case PttMethod.CatPortHandshake:
                _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: !options.Invert);
                break;

            case PttMethod.SeparateComPort:
                lock (_portGate)
                {
                    EnsureSeparatePort(options);
                    SetLine(_separatePort!, options.Line, assert: !options.Invert);
                }
                break;

            case PttMethod.Manual:
                _manualPrompt?.Invoke("key");
                break;
        }

        _keyed = true;
        return true;
    }

    public async Task UnkeyAsync(CancellationToken cancellationToken = default)
    {
        if (!_keyed)
        {
            DropPtt(); // Also release a preparation reservation cancelled before keying.
            return;
        }

        var tail = _options().ClampedTailMs;
        if (tail > 0)
        {
            try
            {
                await Task.Delay(tail, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Halt still has to drop PTT. The tail must not leave the radio keyed.
            }
        }

        DropPtt();
    }

    /// <summary>Drop PTT at once, without the tail delay. Used when the operator stops a transmission.</summary>
    public void UnkeyNow() => DropPtt();

    /// <summary>
    /// Send an unkey whatever this keyer thinks the state is. Covers a radio left transmitting by a
    /// crash: called when the Terminal modem starts and whenever the rig reconnects. VOX and Manual
    /// have nothing to send.
    /// </summary>
    public void ForceOff()
    {
        lock (_owner)
        {
            // A passive window must not unkey the other modem's active burst.
            if (_owner.Keyer is { } other && other != this) return;
            try { ForceOffCore(); }
            finally { _owner.Keyer = null; }
        }
    }

    private void ForceOffCore()
    {
        var options = _options();
        try
        {
            switch (options.Method)
            {
                case PttMethod.Cat:
                    _rig.SetPtt(false);
                    break;
                case PttMethod.CatPortHandshake:
                    _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: options.Invert);
                    break;
                case PttMethod.SeparateComPort:
                    lock (_portGate)
                    {
                        if (_separatePort is { IsOpen: true })
                            SetLine(_separatePort, options.Line, assert: options.Invert);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label} PTT force off failed", _label);
        }
        finally
        {
            _keyed = false;
            _marker?.Clear();
        }
    }

    private void DropPtt()
    {
        lock (_owner)
        {
            if (_owner.Keyer is { } other && other != this) return;
            try { DropPttCore(); }
            finally { if (_owner.Keyer == this) _owner.Keyer = null; }
        }
    }

    private void DropPttCore()
    {
        if (!_keyed)
            return;

        var options = _options();
        try
        {
            switch (options.Method)
            {
                case PttMethod.Vox:
                    break;
                case PttMethod.Cat:
                    _rig.SetPtt(false);
                    break;
                case PttMethod.CatPortHandshake:
                    _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: options.Invert);
                    break;
                case PttMethod.SeparateComPort:
                    lock (_portGate)
                    {
                        if (_separatePort is { IsOpen: true })
                            SetLine(_separatePort, options.Line, assert: options.Invert);
                    }
                    break;
                case PttMethod.Manual:
                    _manualPrompt?.Invoke("unkey");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label} PTT unkey failed", _label);
            // An idle-port reuse policy must not retain a failed asserted line.
            if (options.Method == PttMethod.SeparateComPort)
                lock (_portGate) ClosePort();
        }
        finally
        {
            _keyed = false;
            _marker?.Clear();
        }
    }

    private void EnsureSeparatePort(PttOptions options)
    {
        var portName = options.SeparatePort?.Trim() ?? "";
        if (portName.Length == 0)
            throw new InvalidOperationException("Separate PTT COM port is not configured.");

        if (_describePortConflict?.Invoke(portName) is { Length: > 0 } conflict)
            throw new InvalidOperationException(conflict);

        if (_separatePort is { IsOpen: true }
            && string.Equals(_separatePort.PortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        _separatePort?.Dispose();
        _separatePort = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = options.Line == PttLine.Dtr && options.Invert,
            RtsEnable = options.Line == PttLine.Rts && options.Invert
        };
        try
        {
            _separatePort.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var failed = _separatePort;
            _separatePort = null;
            try { failed.Dispose(); } catch { /* the open already failed */ }
            throw new InvalidOperationException(DescribeOpenFailure(portName, ex), ex);
        }
    }

    private static string DescribeOpenFailure(string portName, Exception ex) =>
        SerialPortConnectErrorHelper.Classify(ex) switch
        {
            SerialPortConnectErrorKind.PortBusy =>
                $"Serial port in use ({portName}). Close the other program using it, or choose a different port.",
            SerialPortConnectErrorKind.PortNotFound =>
                $"Serial port not found ({portName}). Check the USB cable and refresh the port list.",
            _ => ex.Message
        };

    private static void SetLine(SerialPort port, PttLine line, bool assert)
    {
        if (line == PttLine.Rts)
            port.RtsEnable = assert;
        else
            port.DtrEnable = assert;
    }

    /// <summary>
    /// Drop PTT and close the separate PTT COM port so another application (MSHV, WSJT-X)
    /// can open it. The next transmission opens it again.
    /// </summary>
    public void ReleasePort()
    {
        DropPtt();
        lock (_portGate)
            ClosePort();
    }

    /// <summary>
    /// Close the separate PTT port when the settings no longer use it (another method,
    /// or a different port), so the old port is free at once. Closing it also drops its lines.
    /// </summary>
    public void ReleaseUnusedPort()
    {
        var options = _options();
        lock (_portGate)
        {
            if (_separatePort is null)
                return;
            if (options.Method == PttMethod.SeparateComPort
                && string.Equals(_separatePort.PortName, options.SeparatePort?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            ClosePort();
        }
    }

    private void ClosePort()
    {
        var port = _separatePort;
        _separatePort = null;
        if (port is null)
            return;

        try
        {
            port.Dispose();
            Log.Information("{Label} released PTT port {Port}", _label, port.PortName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label} PTT port close failed", _label);
        }
    }

    public void Dispose() => ReleasePort();
}
