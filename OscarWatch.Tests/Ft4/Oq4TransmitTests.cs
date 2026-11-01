using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4TransmitTests
{
    [Fact]
    public void Encoded_burst_contains_the_message_codeword_as_qpsk()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var bits = Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42");
        Assert.Equal(174, bits.Length);
        Assert.Contains((byte)0, bits);
        Assert.Contains((byte)1, bits);
        var packed = new byte[22];
        for (var bit = 0; bit < bits.Length; bit++)
            packed[bit / 8] |= (byte)(bits[bit] << (7 - bit % 8));
        Assert.Equal("4A5E8994FD6563DFA7A74C9DC3A2D77B8E01C43AB5A0", Convert.ToHexString(packed));
        var pcm = Oq4Waveform.Encode(bits, 1500);
        Assert.InRange(pcm.Length / 12000.0, 2.1, 2.3);
        Assert.All(pcm, sample => Assert.InRange(sample, -0.901f, 0.901f));

        // Independently integrate the central half of each symbol against both
        // carriers. Skip the 32 known acquisition symbols and check the first
        // 16 data symbols before the first inserted pilot pair.
        const double samplesPerSymbol = 12000.0 / 64;
        for (var k = 0; k < 16; k++)
        {
            var center = (6 + 32 + k) * samplesPerSymbol;
            double i = 0, q = 0;
            for (var n = (int)(center - samplesPerSymbol / 4); n < center + samplesPerSymbol / 4; n++)
            {
                var phase = 2 * Math.PI * 1500 * n / 12000;
                i += pcm[n] * Math.Cos(phase);
                q -= pcm[n] * Math.Sin(phase);
            }
            Assert.Equal(bits[2 * k] == 0, i > 0);
            Assert.Equal(bits[2 * k + 1] == 0, q > 0);
        }
    }

    [Fact]
    public void Different_messages_produce_different_bursts()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var first = Oq4Waveform.Encode(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 1500);
        var second = Oq4Waveform.Encode(Ft8Native.EncodeOq4Codeword("CQ W9XYZ EN37"), 1500);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Packet_has_center_frequency_references_and_fits_the_slot()
    {
        var bits = new byte[174];
        var samples = Oq4Waveform.EncodePacket(bits, 1500);
        Assert.Equal(Oq4Waveform.PacketSampleCount, samples.Length);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[Oq4Waveform.ReferenceSamples - 1]);
        Assert.Equal(0f, samples[^1]);
        Assert.InRange(samples[240], 0.899f, 0.901f);
        var end = samples.Length - Oq4Waveform.ReferenceSamples + 240;
        var expected = 0.9 * Math.Cos(2 * Math.PI * 1500 * end / Oq4Waveform.SampleRate);
        Assert.InRange(Math.Abs(samples[end] - expected), 0, 0.0001);
        Assert.True(Oq4SlotClock.BurstOffsetSeconds + Oq4Waveform.PacketDurationSeconds < Oq4SlotClock.SlotSeconds);
    }
}
