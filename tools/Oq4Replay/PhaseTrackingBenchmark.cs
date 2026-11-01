using System.Numerics;
using System.Text.Json;
using OscarWatch.Ft4;

// Matched-filter symbol-domain diagnostic; this is not an over-the-air or
// complete acquisition benchmark. No expected payload enters the receiver.
internal static class PhaseTrackingBenchmark
{
    internal static int Run()
    {
        const string message = "CQ K8OTY EN73";
        var bits = Ft8Native.EncodeOq4Codeword(message);
        var rows = new List<object>();
        var wrongTotal = 0;
        foreach (var phaseAmplitude in new[] { 0.0, .5, -.5 })
        {
            int baseline = 0, after = 0, wrong = 0;
            for (var seed = 8000; seed < 8300; seed++)
            {
                var random = new Random(seed);
                const double sigma = .65;
                double Noise() => sigma*Math.Sqrt(-2*Math.Log(1-random.NextDouble()))*Math.Cos(2*Math.PI*random.NextDouble());
                var channel = new float[174];
                for (var i = 0; i < 87; i++)
                {
                    var phase = phaseAmplitude*Math.Sin(Math.PI*(i%16+.5)/16);
                    var value = new Complex(bits[2*i] == 0 ? 1 : -1, bits[2*i+1] == 0 ? 1 : -1)
                        * Complex.FromPolarCoordinates(1,phase) + new Complex(Noise(),Noise());
                    channel[2*i] = (float)(-2*value.Real/(sigma*sigma));
                    channel[2*i+1] = (float)(-2*value.Imaginary/(sigma*sigma));
                }
                var before = Ft8Native.DecodeOq4Codeword(channel) ?? Oq4OrderedStatisticsDecoder.Decode(channel,.8);
                var result = before ?? Oq4Decoder.DecodeWithPhaseFeedback(channel);
                if (before == message) baseline++;
                if (result == message) after++;
                if (result is not null && result != message) wrong++;
            }
            wrongTotal += wrong;
            rows.Add(new { phaseAmplitudeRadians=phaseAmplitude, sigma=.65, firstSeed=8000,
                trials=300, baselineCorrect=baseline, feedbackCorrect=after, wrongOutputs=wrong });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { domain="matched-filter symbols; AWGN; sinusoidal phase error within pilot intervals", rows },
            new JsonSerializerOptions { WriteIndented=true }));
        return wrongTotal == 0 ? 0 : 1;
    }
}
