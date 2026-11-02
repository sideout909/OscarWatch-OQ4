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
    [Fact]
    public void Frequency_edit_keeps_prepared_slot_when_rebuild_can_finish_before_keying()
    {
        var slotStart = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var slot = Oq4SlotClock.SlotNumber(slotStart);
        // A fresh arm would skip this slot with the normal 1.5-second preparation margin.
        var now = slotStart.AddMilliseconds(-600);
        Assert.Equal(slot + 2, Oq4SlotClock.NextTransmitSlot(now, true, 1.5));
        Assert.Equal(slot, Oq4SlotClock.NextTransmitSlotAfterEdit(now, true, slot, .2));
    }

    [Fact]
    public void Late_frequency_edit_never_reuses_an_unsafe_or_wrong_parity_slot()
    {
        var start = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var slot = Oq4SlotClock.SlotNumber(start);
        Assert.Equal(slot + 2, Oq4SlotClock.NextTransmitSlotAfterEdit(start.AddMilliseconds(-100), true, slot, .2));
        Assert.Equal(slot + 1, Oq4SlotClock.NextTransmitSlotAfterEdit(start.AddMilliseconds(-600), false, slot, .2));
        // A long PTT lead starts before the UTC slot boundary and must also fit.
        Assert.Equal(slot + 2, Oq4SlotClock.NextTransmitSlotAfterEdit(start.AddMilliseconds(-600), true, slot, 1.0));
    }

    [Fact]
    public void New_arming_keeps_the_existing_preparation_margin()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Oq4SlotClock.NextTransmitSlot(now, true, 1.5),
            Oq4SlotClock.NextTransmitSlotAfterEdit(now, true, null, .2));
    }
}
