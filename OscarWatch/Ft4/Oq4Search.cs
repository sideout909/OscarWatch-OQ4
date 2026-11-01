using OscarWatch.Core.Ft4;

namespace OscarWatch.Ft4;

/// <summary>Find candidate OQ4 reference-tone pairs across the received audio passband.</summary>
internal static class Oq4Search
{
    internal sealed record Candidate(double StartSeconds, double StartHz, double EndHz, double Score);
    private const int FftSize = 4096;
    private const int ToneSamples = 1440;
    private const int Bins = 957;
    private const double LowHz = 200;
    private const double HighHz = 3000;
    private const double StepHz = (HighHz - LowHz) / (Bins - 1);
    private const double EndOffset = (Oq4Waveform.PacketSampleCount - Oq4Waveform.ReferenceSamples) / 12000.0;

    private static float[] Spectrum(float[] audio, double time)
    {
        var input = new float[FftSize];
        var source = (int)Math.Round((time + .02) * 12000);
        if (source < 0 || source + ToneSamples > audio.Length) return [];
        Array.Copy(audio, source, input, (FftSize - ToneSamples) / 2, ToneSamples);
        var bins = new float[Bins];
        Ft4SpectrumAnalyzer.TryComputePassband(input, 12000, bins, LowHz, HighHz);
        // Preserve the longer effective integration in noise-limited windows.
        // With a dominant tone, taper the actual 120 ms observation before
        // zero-padding: the analyzer's full-FFT window leaves sharp edges on
        // this shorter segment and its sidelobes can hide a weaker station.
        if (bins.Max() - Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(bins, .5f) > 35)
        {
            for (var i = 0; i < ToneSamples; i++)
                input[(FftSize - ToneSamples) / 2 + i] *=
                    (float)(.5 - .5 * Math.Cos(2 * Math.PI * i / (ToneSamples - 1)));
            Ft4SpectrumAnalyzer.TryComputePassband(input, 12000, bins, LowHz, HighHz);
        }
        return bins;
    }

    internal static Candidate[] Find(float[] audio, int maximum = 8)
    {
        var maxTime = audio.Length / 12000.0 - Oq4Waveform.PacketDurationSeconds;
        if (maxTime < 0) return [];
        var candidates = new List<Candidate>();
        for (double t = 0; t <= maxTime; t += .04)
        {
            var start = Spectrum(audio, t);
            var end = Spectrum(audio, t + EndOffset);
            if (start.Length == 0 || end.Length == 0) continue;
            var floorA = Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(start, .5f);
            var floorB = Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(end, .5f);
            for (var i = 1; i < Bins - 1; i++)
            {
                if (start[i] < start[i-1] || start[i] < start[i+1]) continue;
                var a = start[i] - floorA;
                if (a < 5) continue;
                var lo = Math.Max(1, i - 70);
                var hi = Math.Min(Bins - 2, i + 70);
                var best = lo;
                for (var j = lo + 1; j <= hi; j++) if (end[j] > end[best]) best = j;
                var b = end[best] - floorB;
                if (b < 5) continue;
                double Interpolate(float[] values, int k)
                {
                    var den = values[k - 1] - 2 * values[k] + values[k + 1];
                    var offset = Math.Abs(den) < 1e-6 ? 0 : .5 * (values[k - 1] - values[k + 1]) / den;
                    return LowHz + (k + Math.Clamp(offset, -.5, .5)) * StepHz;
                }
                candidates.Add(new Candidate(t, Interpolate(start, i), Interpolate(end, best), Math.Min(a,b) + .25 * (a+b)));
            }
        }
        candidates.Sort((a,b) => b.Score.CompareTo(a.Score));
        var selected = new List<Candidate>();
        // Prioritize independent frequency regions before filling nearby ones.
        // This prevents sidelobes of a strong local signal from crowding a
        // weaker simultaneous satellite return out of the candidate budget.
        foreach (var separation in new[] { 200.0, 40.0 })
        foreach (var c in candidates)
        {
            if (selected.Any(x => Math.Abs(x.StartSeconds-c.StartSeconds) < 1.0 &&
                                  (Math.Abs(x.StartHz-c.StartHz) < separation ||
                                   Math.Abs(x.EndHz-c.EndHz) < 15))) continue;
            selected.Add(c);
            if (selected.Count == maximum) return selected.ToArray();
        }
        return selected.ToArray();
    }
}
