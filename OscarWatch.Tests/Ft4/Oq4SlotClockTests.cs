using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4SlotClockTests
{
    [Fact]
    public void Three_and_three_quarter_second_slots_align_to_utc_and_alternate()
    {
        var t = new DateTime(2026, 10, 31, 12, 0, 11, 249, DateTimeKind.Utc);
        var n = Oq4SlotClock.SlotNumber(t);
        Assert.Equal(new DateTime(2026, 10, 31, 12, 0, 7, 500, DateTimeKind.Utc), Oq4SlotClock.SlotStart(n));
        Assert.Equal(Oq4SlotClock.IsEven(n), !Oq4SlotClock.IsEven(n + 1));
        Assert.Equal(Oq4SlotClock.SlotStart(n).AddMilliseconds(400), Oq4SlotClock.BurstStart(n));
    }

    [Fact]
    public void Sixteen_slots_fit_in_a_utc_minute()
    {
        var minute = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var first = Oq4SlotClock.SlotNumber(minute);
        Assert.True(Oq4SlotClock.IsEven(first));
        Assert.Equal(minute.AddMinutes(1), Oq4SlotClock.SlotStart(first + 16));
        Assert.False(Oq4SlotClock.IsEven(first + 15));
    }
    [Fact]
    public void Preparation_starts_in_the_previous_slot_and_late_arming_waits_for_next()
    {
        var minute = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var even = Oq4SlotClock.SlotNumber(minute);
        Assert.Equal(even, Oq4SlotClock.NextTransmitSlot(minute.AddSeconds(-.8), true));
        Assert.Equal(even, Oq4SlotClock.NextTransmitSlot(minute, true));
        Assert.Equal(even + 2, Oq4SlotClock.NextTransmitSlot(minute.AddSeconds(.2), true));
        Assert.Equal(even + 1, Oq4SlotClock.NextTransmitSlot(minute, false));
        Assert.Equal(even + 2, Oq4SlotClock.NextTransmitSlot(minute, true, 1));
    }
}
