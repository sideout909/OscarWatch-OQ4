namespace OscarWatch.Core.Ft4;

/// <summary>Keep complete UTC-slot snapshots while a previous decode is running.
/// Feed mono 12 kHz samples and call CaptureCompletedWindows after draining capture.
/// Access from one thread; immutable snapshots can be decoded on a worker.</summary>
public sealed class Oq4ReceiveWindowBuffer
{
    public const int Rate = 12000;
    public const int WindowSamples = 4 * Rate;
    public const double SnapshotOffsetSeconds = 3.25;
    private const int RetainedSamples = 16 * Rate;
    private const int MaximumPending = 4;
    private readonly List<float> _samples = new();
    private readonly Queue<Window> _pending = new();
    private long? _nextSlot;

    public sealed record Window(long Slot, long FirstSample, float[] Audio);
    public long TotalSamples { get; private set; }
    public int DroppedWindows { get; private set; }
    public int PendingCount => _pending.Count;

    public void Clear()
    {
        _samples.Clear(); _pending.Clear(); _nextSlot = null;
        TotalSamples = 0; DroppedWindows = 0;
    }

    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples) _samples.Add(sample);
        TotalSamples += samples.Length;
        if (_samples.Count > RetainedSamples)
            _samples.RemoveRange(0, _samples.Count - RetainedSamples);
    }

    public void CaptureCompletedWindows(DateTime latestSampleUtc)
    {
        var completed = Oq4SlotClock.SlotNumber(latestSampleUtc.AddSeconds(-SnapshotOffsetSeconds));
        _nextSlot ??= completed;
        // Bound catch-up after suspend/large forward clock adjustments. An old
        // sample timeline cannot reconstruct slots missed during a suspension.
        if (completed - _nextSlot.Value > MaximumPending)
        {
            _nextSlot = completed - MaximumPending;
            DroppedWindows++;
        }
        if (_nextSlot.Value > completed + 1) _nextSlot = completed; // clock moved backwards
        while (_nextSlot.Value <= completed)
        {
            var slot = _nextSlot.Value;
            var endUtc = Oq4SlotClock.SlotStart(slot).AddSeconds(SnapshotOffsetSeconds);
            var trailing = (long)Math.Round((latestSampleUtc.ToUniversalTime() - endUtc).TotalSeconds * Rate);
            var first = TotalSamples - trailing - WindowSamples;
            var offset = first - (TotalSamples - _samples.Count);
            _nextSlot++;
            // Opening monitoring partway through a slot cannot recover audio
            // from before capture began. Only enqueue complete windows.
            if (offset < 0 || offset + WindowSamples > _samples.Count) continue;
            if (_pending.Count == MaximumPending)
            {
                _pending.Dequeue();
                DroppedWindows++;
            }
            _pending.Enqueue(new Window(slot, first, _samples.GetRange((int)offset, WindowSamples).ToArray()));
        }
    }

    public bool TryTake(out Window? window) => _pending.TryDequeue(out window);
}
