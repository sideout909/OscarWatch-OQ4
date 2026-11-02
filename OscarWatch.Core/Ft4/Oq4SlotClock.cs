namespace OscarWatch.Core.Ft4;

/// <summary>Experimental UTC-aligned OQ4 slots: 3.75 seconds each, alternating even/odd.</summary>
public static class Oq4SlotClock
{
    public const double SlotSeconds = 3.75;
    public const double BurstOffsetSeconds = 0.4;
    public static long SlotNumber(DateTime utc) =>
        (long)Math.Floor((utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds / SlotSeconds);
    public static DateTime SlotStart(long slot) => DateTime.UnixEpoch.AddSeconds(slot * SlotSeconds);
    public static bool IsEven(long slot) => (slot & 1) == 0;
    public static DateTime BurstStart(long slot) => SlotStart(slot).AddSeconds(BurstOffsetSeconds);
    /// <summary>Next selected burst with enough lead time for audio/PTT preparation.</summary>
    public static long NextTransmitSlot(DateTime utc, bool even, double minimumLeadSeconds = .25)
    {
        var slot = SlotNumber(utc);
        if (IsEven(slot) != even) slot++;
        if ((BurstStart(slot) - utc.ToUniversalTime()).TotalSeconds < minimumLeadSeconds) slot += 2;
        return slot;
    }
    /// <summary>Keep an edited preparation in its original slot when it can still key on time.</summary>
    public static long NextTransmitSlotAfterEdit(DateTime utc, bool even, long? previousSlot, double pttLeadSeconds)
    {
        var now = utc.ToUniversalTime();
        if (previousSlot is { } previous && IsEven(previous) == even)
        {
            var slotStart = SlotStart(previous);
            var leadStart = BurstStart(previous).AddSeconds(-Math.Max(0, pttLeadSeconds));
            var keyAt = slotStart < leadStart ? slotStart : leadStart;
            if (keyAt - now >= TimeSpan.FromMilliseconds(250)) return previous;
        }
        return NextTransmitSlot(now, even, Math.Max(1.5, pttLeadSeconds + 1));
    }
}
