using OscarWatch.Ft4;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4OrderedStatisticsTests
{
    [Theory]
    [InlineData("CQ K8OTY EN73")]
    [InlineData("CQ K1ABC FN42")]
    [InlineData("K8OTY K1ABC -12")]
    public void Managed_parity_generator_agrees_with_native_encoder(string message)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var bits = Ft8Native.EncodeOq4Codeword(message);
        Assert.True(Oq4OrderedStatisticsDecoder.HasValidParity(bits));
        bits[110] ^= 1;
        Assert.False(Oq4OrderedStatisticsDecoder.HasValidParity(bits));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(24)]
    public void Bounded_reprocessing_recovers_crc_valid_words_that_belief_propagation_misses(int seed)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var bits = Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73");
        var random = new Random(seed);
        const double sigma = .85;
        var likelihoods = bits.Select(bit => (float)(2/(sigma*sigma) * ((bit == 0 ? -1 : 1)
            + sigma*Math.Sqrt(-2*Math.Log(1-random.NextDouble()))*Math.Cos(2*Math.PI*random.NextDouble())))).ToArray();
        Assert.Null(Ft8Native.DecodeOq4Codeword(likelihoods));
        Assert.Equal("CQ K8OTY EN73", Oq4OrderedStatisticsDecoder.Decode(likelihoods, .8));
    }

    [Fact]
    public void Full_parity_codeword_without_a_valid_crc_is_not_accepted()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        // A fixed LDPC-valid codeword whose 91 information positions are all
        // zero except CRC bit 77. Its zero payload requires CRC zero, so it is
        // definitely CRC-invalid even though all LDPC parity checks pass.
        var invalid = Convert.FromBase64String("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAEBAQAAAAEBAQABAQABAAABAAEBAAABAAAAAAAAAAAAAQEAAAEBAAEAAAEBAAEBAQAAAQABAQEBAAABAQABAQEBAAAAAAEBAAEBAQEBAQEA");
        Assert.True(Oq4OrderedStatisticsDecoder.HasValidParity(invalid));
        var likelihoods = invalid.Select(x => x == 0 ? -5f : 5f).ToArray();
        Assert.Null(Ft8Native.DecodeOq4Codeword(likelihoods));
        Assert.Null(Oq4OrderedStatisticsDecoder.Decode(likelihoods, .95));
    }

    [Fact]
    public void Missing_preamble_evidence_does_not_invoke_a_message_search()
    {
        var random = new Random(420);
        var noise = Enumerable.Range(0, 174).Select(_ => (float)(random.NextDouble()-.5)).ToArray();
        Assert.Null(Oq4OrderedStatisticsDecoder.Decode(noise, .1));
        Assert.Null(Oq4OrderedStatisticsDecoder.Decode(new float[174], .9));
        noise[4] = float.NaN;
        Assert.Null(Oq4OrderedStatisticsDecoder.Decode(noise, .9));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    public void Strong_reference_and_preamble_with_uncoded_random_data_do_not_decode(int seed)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var random = new Random(seed);
        var randomBits = Enumerable.Range(0, 174).Select(_ => (byte)random.Next(2)).ToArray();
        Assert.False(Oq4OrderedStatisticsDecoder.HasValidParity(randomBits));
        var packet = Oq4Waveform.EncodePacket(randomBits, 1500, -14);
        var samples = new float[48000];
        Array.Copy(packet, 0, samples, 4800, packet.Length);
        Assert.Empty(Oq4Decoder.DecodeWideband(samples));
    }
}
