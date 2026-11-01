using System.Numerics;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4PhaseTrackingTests
{
    [Theory]
    [InlineData(8039)]
    [InlineData(8061)]
    [InlineData(8124)]
    public void Extrinsic_phase_feedback_recovers_phase_distorted_codewords(int seed)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string message = "CQ K8OTY EN73";
        var bits = Ft8Native.EncodeOq4Codeword(message);
        var random = new Random(seed);
        const double sigma = .65;
        double Noise() => sigma*Math.Sqrt(-2*Math.Log(1-random.NextDouble()))*Math.Cos(2*Math.PI*random.NextDouble());
        var channel = new float[174];
        for (var i = 0; i < 87; i++)
        {
            var phase = .5*Math.Sin(Math.PI*(i%16+.5)/16);
            var value = new Complex(bits[2*i] == 0 ? 1 : -1, bits[2*i+1] == 0 ? 1 : -1)
                * Complex.FromPolarCoordinates(1, phase) + new Complex(Noise(), Noise());
            channel[2*i] = (float)(-2*value.Real/(sigma*sigma));
            channel[2*i+1] = (float)(-2*value.Imaginary/(sigma*sigma));
        }
        var original = (float[])channel.Clone();
        Assert.Null(Ft8Native.DecodeOq4Codeword(channel));
        Assert.Null(Oq4OrderedStatisticsDecoder.Decode(channel, .8));
        Assert.Equal(message, Oq4Decoder.DecodeWithPhaseFeedback(channel));
        Assert.Equal(original, channel);
    }

    [Fact]
    public void Extrinsic_information_excludes_the_input_channel_and_has_correct_sign()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bits = Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73");
        // Weaken one bit: parity messages should restore it without copying
        // its channel likelihood directly into the returned information.
        var channel = bits.Select(x => x == 0 ? -3f : 3f).ToArray();
        channel[50] = bits[50] == 0 ? .01f : -.01f;
        var extra = Ft8Native.Oq4Extrinsic(channel);
        Assert.NotNull(extra);
        Assert.True(extra[50]*(bits[50] == 0 ? -1 : 1) > 0);
        Assert.Null(Ft8Native.Oq4Extrinsic(new float[1]));
        channel[1] = float.NaN;
        Assert.Null(Ft8Native.Oq4Extrinsic(channel));
        Assert.Null(Oq4Decoder.DecodeWithPhaseFeedback(new float[174]));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-12)]
    [InlineData(-18)]
    public void Snr_estimate_matches_known_gaussian_noise_in_2500_hz(int expectedDb)
    {
        var random = new Random(9300+expectedDb);
        var sigma = Math.Sqrt(64.0/2500/Math.Pow(10, expectedDb/10.0));
        var estimates = new List<double>();
        for (var trial = 0; trial < 500; trial++)
        {
            var bits = Enumerable.Range(0,174).Select(_ => (byte)random.Next(2)).ToArray();
            var channel = bits.Select(bit => (bit == 0 ? -1.0 : 1.0)
                + sigma*Math.Sqrt(-2*Math.Log(1-random.NextDouble()))*Math.Cos(2*Math.PI*random.NextDouble())).ToArray();
            estimates.Add(Oq4Decoder.EstimateSnr(channel, bits));
            Assert.InRange(Math.Abs(Oq4Decoder.EstimateSnr(channel.Select(x => x*.003).ToArray(),bits)-estimates[^1]),0,.001);
        }
        Assert.InRange(estimates.Average(), expectedDb-.3, expectedDb+.3);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1500)]
    [InlineData(2300)]
    public void Waveform_snr_survives_mixing_filtering_and_decode(int carrier)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string message = "CQ K8OTY EN73";
        const double expected = -10;
        var packet = OscarWatch.Core.Ft4.Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword(message),carrier);
        // Active QPSK section, excluding the two louder carrier references and
        // filter tails. Reference-band noise power is sigma^2 * 2500 / 6000.
        var dataPower = packet.Skip(3045).Take(24187).Average(x => (double)x*x);
        var sigma = Math.Sqrt(dataPower * 6000/2500 / Math.Pow(10,expected/10));
        var random = new Random(9400+carrier);
        var audio = Enumerable.Range(0,48000).Select(_ => (float)(sigma*Math.Sqrt(-2*Math.Log(1-random.NextDouble()))
            * Math.Cos(2*Math.PI*random.NextDouble()))).ToArray();
        for (var i = 0; i < packet.Length; i++) audio[4800+i] += packet[i];
        var decoded = Assert.Single(Oq4Decoder.DecodeWideband(audio));
        Assert.Equal(message, decoded.Message);
        Assert.InRange(decoded.SnrDb, expected-2.5, expected+2.5);
    }

    [Fact]
    public void Invalid_snr_observations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Oq4Decoder.EstimateSnr([], []));
        Assert.Throws<ArgumentException>(() => Oq4Decoder.EstimateSnr([1,2,3,double.NaN], [0,0,0,0]));
    }
}
