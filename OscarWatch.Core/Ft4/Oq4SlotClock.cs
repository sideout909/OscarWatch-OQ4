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
}
