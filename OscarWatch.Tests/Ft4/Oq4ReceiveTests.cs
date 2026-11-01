using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4ReceiveTests
{
    [Theory]
    [InlineData("CQ K8OTY EN73", 1500, 0)]
    [InlineData("K1ABC K8OTY -10", 1760, -3.5)]
    public void Decodes_packet_with_frequency_offset_and_drift(string message, double frequency, double slope)
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var bits = Ft8Native.EncodeOq4Codeword(message);
        var packet = Oq4Waveform.EncodePacket(bits, frequency, slope);
        var audio = new float[48000];
        Array.Copy(packet, 0, audio, 5000, packet.Length);
        var result = Oq4Decoder.Decode(audio, frequency + 8);
        Assert.NotNull(result);
        Assert.Equal(message, result.Message);
        Assert.InRange(result.TimeSeconds, .40, .44);
    }

    [Fact]
    public void Silence_does_not_decode()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        Assert.Null(Oq4Decoder.Decode(new float[48000], 1500));
    }

    [Fact]
    public void Noise_does_not_produce_a_crc_valid_message()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var random = new Random(42);
        var samples = new float[48000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)((random.NextDouble() - .5) * .1);
        Assert.Null(Oq4Decoder.Decode(samples, 1500));
    }

    [Fact]
    public void Wideband_search_does_not_decode_noise()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var random = new Random(42);
        var samples = new float[48000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)((random.NextDouble() - .5) * .1);
        Assert.Empty(Oq4Decoder.DecodeWideband(samples));
    }

    [Theory]
    [InlineData(500, -14)]
    [InlineData(2300, -14)]
    public void Wideband_search_decodes_strong_drifting_packets_without_a_selected_frequency(double frequency, double slope)
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var bits = Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73");
        var packet = Oq4Waveform.EncodePacket(bits, frequency, slope);
        var audio = new float[48000];
        Array.Copy(packet, 0, audio, 4800, packet.Length);
        var found = Oq4Decoder.DecodeWideband(audio);
        Assert.Contains(found, r => r.Message == "CQ K8OTY EN73" && Math.Abs(r.FrequencyHz-frequency) < 6);
    }

    [Fact]
    public void Wideband_search_finds_two_simultaneous_stations()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var audio = new float[48000];
        var a = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73"), 700, -4);
        var b = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 2200, 3);
        for (int i = 0; i < a.Length; i++) audio[4800+i] = .3f * (a[i] + b[i]);
        var found = Oq4Decoder.DecodeWideband(audio);
        Assert.Contains(found, r => r.Message == "CQ K8OTY EN73");
        Assert.Contains(found, r => r.Message == "CQ K1ABC FN42");
    }

    [Theory]
    [InlineData(68)]
    [InlineData(-68)]
    public void Wideband_search_recovers_fast_drift_next_to_a_much_stronger_signal(double slope)
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var audio = new float[48000];
        var direct = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 2550, -17);
        var echo = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73"), 1760, slope);
        for (int i = 0; i < direct.Length; i++) audio[6300+i] = .4f * direct[i] + .025f * echo[i];
        var found = Oq4Decoder.DecodeWideband(audio);
        Assert.Contains(found, r => r.Message == "CQ K1ABC FN42");
        Assert.Contains(found, r => r.Message == "CQ K8OTY EN73");
    }

    [Fact]
    public void Candidate_search_retains_a_weaker_fast_drifting_tone_pair()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        var audio = new float[48000];
        var direct = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 2550, -17);
        var echo = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73"), 1760, 68);
        for (int i = 0; i < direct.Length; i++) audio[6300+i] = .4f * direct[i] + .005f * echo[i];
        var candidates = Oq4Search.Find(audio, 2);
        Assert.Contains(candidates, c => Math.Abs(c.StartHz - 1765) < 8 && c.EndHz-c.StartHz > 145);
    }

    [Theory]
    [InlineData(397, -13)]
    [InlineData(540, 9)]
    public void Wideband_search_recovers_low_audio_beside_strong_bleed(double frequency, double slope)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var direct = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 2600, -4);
        var weak = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73"), frequency, slope);
        var audio = new float[48000];
        var random = new Random(18);
        for (var i = 0; i < audio.Length; i++) audio[i] = (float)((random.NextDouble() - .5) * .00006);
        for (var i = 0; i < direct.Length; i++)
        {
            audio[6300+i] += .4f * direct[i];
            audio[6540+i] += .002f * weak[i]; // delayed, 46 dB below the direct signal
        }
        var decoded = Oq4Decoder.DecodeWideband(audio);
        Assert.Contains(decoded, r => r.Message == "CQ K1ABC FN42");
        Assert.Contains(decoded, r => r.Message == "CQ K8OTY EN73" && Math.Abs(r.FrequencyHz-frequency) < 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Wideband_search_rejects_additional_noise_realizations(int seed)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var random = new Random(seed);
        var audio = new float[48000];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(Math.Sqrt(-2 * Math.Log(1-random.NextDouble()))
                * Math.Cos(2 * Math.PI * random.NextDouble()) * .07);
        Assert.Empty(Oq4Decoder.DecodeWideband(audio));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void Strong_reference_tones_do_not_override_qpsk_timing(double referenceGain)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        var packet = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 1760, 68);
        var audio = new float[48000];
        var random = new Random(31);
        for (var i = 0; i < audio.Length; i++) audio[i] = (float)((random.NextDouble() - .5) * .03);
        for (var i = 0; i < packet.Length; i++)
        {
            var isReference = i < Oq4Waveform.ReferenceSamples || i >= packet.Length - Oq4Waveform.ReferenceSamples;
            audio[6300+i] += .04f * packet[i] * (float)(isReference ? referenceGain : 1);
        }
        var candidate = new Oq4Search.Candidate(.52, 1760 + 68 * .08,
            1760 + 68 * (Oq4Waveform.PacketDurationSeconds - .08), 40);
        var decoded = Oq4Decoder.DecodeCandidate(audio, candidate);
        Assert.NotNull(decoded);
        Assert.Equal("CQ K1ABC FN42", decoded.Message);
        Assert.InRange(decoded.TimeSeconds, .519, .531);
    }

    [Fact]
    public void Wideband_search_decodes_spectrally_inverted_qpsk()
    {
        if (!OperatingSystem.IsLinux() || !Ft8Native.IsAvailable) return;
        const double center = 1760, sampleRate = 12000;
        var source = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K8OTY EN73"), center, -68);
        var direct = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword("CQ K1ABC FN42"), 2550, -17);
        var audio = new float[48000];
        // Independent windowed-Hilbert approximation. Reflect the analytic
        // spectrum around the carrier, as an inverting SSB path can do.
        for (var n = 0; n < source.Length; n++)
        {
            double quadrature = 0;
            for (var k = -127; k <= 127; k += 2)
            {
                var j = n - k;
                if ((uint)j >= (uint)source.Length) continue;
                var h = 2.0 / (Math.PI * k) * (.5 + .5 * Math.Cos(Math.PI * k / 128));
                quadrature += h * source[j];
            }
            var phase = 4 * Math.PI * center * n / sampleRate;
            audio[4800+n] = .025f * (float)(source[n] * Math.Cos(phase) + quadrature * Math.Sin(phase))
                            + .4f * direct[n];
        }
        var decoded = Oq4Decoder.DecodeWideband(audio);
        Assert.Contains(decoded, r => r.Message == "CQ K8OTY EN73");
        Assert.Contains(decoded, r => r.Message == "CQ K1ABC FN42");
    }
}
