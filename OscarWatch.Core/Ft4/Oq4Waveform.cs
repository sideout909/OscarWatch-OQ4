using System.Numerics;

namespace OscarWatch.Core.Ft4;

/// <summary>Experimental OQ4 v0: 64-baud QPSK with a 0.25-rolloff, 12-symbol RRC pulse.</summary>
public static class Oq4Waveform
{
    public const int SymbolRate = 64;
    public const int SampleRate = 12000;
    public const int ReferenceSamples = 1920; // 160 ms at 12 kHz
    public const int PacketSampleCount = 30278;
    public const double PacketDurationSeconds = (double)PacketSampleCount / SampleRate;
    private const int HalfSpan = 6;
    private const double RollOff = 0.25;

    /// <summary>Build a deterministic burst. The symbol sequence is part of the experimental format.</summary>
    public static float[] Encode(ReadOnlySpan<byte> bits, double audioHz, double audioSlopeHzPerSecond = 0, double carrierTimeOffsetSeconds = 0)
    {
        if (bits.Length != 174)
            throw new ArgumentException("OQ4 needs a 174-bit codeword.", nameof(bits));
        if (!double.IsFinite(audioHz) || audioHz < 200 || audioHz > 3000)
            throw new ArgumentOutOfRangeException(nameof(audioHz));
        if (!double.IsFinite(audioSlopeHzPerSecond))
            throw new ArgumentOutOfRangeException(nameof(audioSlopeHzPerSecond));
        if (!double.IsFinite(carrierTimeOffsetSeconds))
            throw new ArgumentOutOfRangeException(nameof(carrierTimeOffsetSeconds));

        var symbols = new List<Complex>();
        uint state = 0x4f513431;
        Complex KnownSymbol()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return Map((int)(state & 1), (int)((state >> 1) & 1));
        }

        for (var i = 0; i < 32; i++) symbols.Add(KnownSymbol());
        for (var i = 0; i < bits.Length; i += 2)
        {
            if (bits[i] > 1 || bits[i + 1] > 1)
                throw new ArgumentException("Codeword bits must be zero or one.", nameof(bits));
            symbols.Add(Map(bits[i], bits[i + 1]));
            if ((i / 2 + 1) % 16 == 0 && i + 2 < bits.Length)
            {
                symbols.Add(KnownSymbol());
                symbols.Add(KnownSymbol());
            }
        }

        // The pulse has six symbol periods of support on either side. Keeping
        // its full tail makes the burst start and end smoothly.
        var count = (int)Math.Ceiling((symbols.Count + 2.0 * HalfSpan) * SampleRate / SymbolRate);
        var output = new float[count];
        var peak = 0.0;
        for (var n = 0; n < count; n++)
        {
            var t = n * (double)SymbolRate / SampleRate - HalfSpan;
            var first = Math.Max(0, (int)Math.Ceiling(t - HalfSpan));
            var last = Math.Min(symbols.Count - 1, (int)Math.Floor(t + HalfSpan));
            var value = Complex.Zero;
            for (var k = first; k <= last; k++) value += symbols[k] * RootRaisedCosine(t - k);
            var seconds = (double)n / SampleRate + carrierTimeOffsetSeconds;
            var phase = 2 * Math.PI * audioHz * seconds + Math.PI * audioSlopeHzPerSecond * seconds * seconds;
            var sample = value.Real * Math.Cos(phase) - value.Imaginary * Math.Sin(phase);
            output[n] = (float)sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak > 0)
            for (var n = 0; n < count; n++) output[n] = (float)(0.9 * output[n] / peak);
        return output;
    }

    /// <summary>
    /// Experimental OQ4 v1: add 160 ms center-frequency references before and
    /// after the v0 QPSK burst. References have 20 ms cosine tapers and share
    /// the packet's carrier phase and optional frequency slope.
    /// </summary>
    public static float[] EncodePacket(ReadOnlySpan<byte> bits, double audioHz, double audioSlopeHzPerSecond = 0)
    {
        var data = Encode(bits, audioHz, audioSlopeHzPerSecond, (double)ReferenceSamples / SampleRate);
        var packet = new float[data.Length + 2 * ReferenceSamples];
        data.CopyTo(packet, ReferenceSamples);
        WriteReference(packet, 0, audioHz, audioSlopeHzPerSecond);
        WriteReference(packet, ReferenceSamples + data.Length, audioHz, audioSlopeHzPerSecond);
        return packet;
    }

    private static void WriteReference(float[] packet, int offset, double audioHz, double slope)
    {
        const int ramp = 240; // 20 ms
        for (var i = 0; i < ReferenceSamples; i++)
        {
            var edge = Math.Min(1.0, Math.Min(i, ReferenceSamples - 1 - i) / (double)ramp);
            var envelope = 0.5 - 0.5 * Math.Cos(Math.PI * edge);
            var seconds = (double)(offset + i) / SampleRate;
            var phase = 2 * Math.PI * audioHz * seconds + Math.PI * slope * seconds * seconds;
            packet[offset + i] = (float)(0.9 * envelope * Math.Cos(phase));
        }
    }

    private static Complex Map(int i, int q) => new(i == 0 ? 1 : -1, q == 0 ? 1 : -1);

    private static double RootRaisedCosine(double t)
    {
        if (Math.Abs(t) < 1e-9) return 1 + RollOff * (4 / Math.PI - 1);
        if (Math.Abs(Math.Abs(t) - 1 / (4 * RollOff)) < 1e-9)
            return RollOff / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * RollOff))
                + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * RollOff)));
        return (Math.Sin(Math.PI * t * (1 - RollOff)) + 4 * RollOff * t * Math.Cos(Math.PI * t * (1 + RollOff)))
            / (Math.PI * t * (1 - Math.Pow(4 * RollOff * t, 2)));
    }
}
