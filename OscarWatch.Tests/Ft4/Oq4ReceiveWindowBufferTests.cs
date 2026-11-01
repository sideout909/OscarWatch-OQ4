using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4ReceiveWindowBufferTests
{
    [Fact]
    public void Busy_decoder_keeps_four_complete_slot_snapshots()
    {
        var buffer = new Oq4ReceiveWindowBuffer();
        var epoch = DateTime.UnixEpoch;
        // No dequeue for 18.5 seconds: simulate a busy decoder. Samples encode
        // their absolute index so window alignment and continuity are checked.
        for (var chunk = 0; chunk < 185; chunk++)
        {
            var samples = Enumerable.Range(chunk * 1200, 1200).Select(i => (float)i).ToArray();
            buffer.Append(samples);
            buffer.CaptureCompletedWindows(epoch.AddSeconds((chunk + 1) * .1));
        }
        Assert.Equal(4, buffer.PendingCount);
        Assert.Equal(0, buffer.DroppedWindows);
        for (long slot = 1; slot <= 4; slot++)
        {
            Assert.True(buffer.TryTake(out var window));
            Assert.NotNull(window);
            Assert.Equal(slot, window.Slot);
            Assert.Equal(window.FirstSample, (long)window.Audio[0]);
            Assert.Equal(window.FirstSample + window.Audio.Length - 1, (long)window.Audio[^1]);
            var burst = (Oq4SlotClock.BurstStart(slot) - epoch).TotalSeconds * 12000;
            Assert.True(window.FirstSample <= burst);
            Assert.True(window.FirstSample + window.Audio.Length >= burst + Oq4Waveform.PacketSampleCount);
        }
    }

    [Fact]
    public void Overload_is_bounded_and_capture_reset_discards_stale_windows()
    {
        var buffer = new Oq4ReceiveWindowBuffer();
        for (var second = 0; second < 30; second++)
        {
            buffer.Append(new float[12000]);
            buffer.CaptureCompletedWindows(DateTime.UnixEpoch.AddSeconds(second + 1));
        }
        Assert.Equal(4, buffer.PendingCount);
        Assert.True(buffer.DroppedWindows > 0);
        buffer.Clear();
        Assert.Equal(0, buffer.PendingCount);
        Assert.Equal(0, buffer.TotalSamples);
        Assert.Equal(0, buffer.DroppedWindows);
    }
}
