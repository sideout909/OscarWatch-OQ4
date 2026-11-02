using System.Numerics;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Ft4;

/// <summary>Experimental single-signal OQ4 receiver. Audio is mono at 12 kHz.</summary>
internal static class Oq4Decoder
{
    internal sealed record Result(string Message, double FrequencyHz, double TimeSeconds, float SnrDb = 0);
    private sealed record SoftTrial(float[] Likelihoods, double Quality, double Frequency, int Start, float SnrDb);
    private const int Rate = 1000;
    private const int Decimation = 12;
    private const int TemplateLength = 650;
    private const int FallbackCandidatePool = 96;
    private const int MaximumFallbackAttempts = 4;
    private const double MinimumFallbackPreambleScore = .08;
    private static readonly double[] LowPass = MakeLowPass();
    private static readonly Complex[] Template = MakeTemplate();
    private static readonly Complex[] Known = MakeKnown();

    private static Complex[] MakeKnown()
    {
        uint state = 0x4f513431;
        var result = new Complex[42];
        for (var i = 0; i < result.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            result[i] = new Complex((state & 1) == 0 ? 1 : -1, (state & 2) == 0 ? 1 : -1);
        }
        return result;
    }

    private static Complex[] MakeTemplate()
    {
        var wave = Oq4Waveform.EncodePacket(new byte[174], 1500);
        return Mix(wave, 1500)[..TemplateLength];
    }

    private static double[] MakeLowPass()
    {
        const int half = 96;
        const double cutoff = 220.0 / 12000;
        var taps = new double[2 * half + 1];
        double sum = 0;
        for (var i = -half; i <= half; i++)
        {
            var x = i == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * i) / (Math.PI * i);
            var window = .5 + .5 * Math.Cos(Math.PI * i / half);
            taps[i + half] = x * window;
            sum += taps[i + half];
        }
        for (var i = 0; i < taps.Length; i++) taps[i] /= sum;
        return taps;
    }

    private static Complex[] Mix(float[] samples, double frequency) => MixChirp(samples, frequency, 0, 0);

    private static double Score(Complex[] signal, int start)
    {
        Complex sum = 0;
        double power = 0;
        for (var i = 0; i < TemplateLength; i++)
        {
            var v = signal[start + i];
            sum += v * Complex.Conjugate(Template[i]);
            power += v.Magnitude * v.Magnitude;
        }
        return power > 1e-15 ? sum.Magnitude : 0;
    }

    private static double SyncScore(Complex[] signal, int start)
    {
        // Use only known QPSK: the louder unmodulated reference can otherwise
        // select a timing peak that is several symbols away from the data.
        Complex sum = 0;
        double power = 0;
        for (var i = 250; i < TemplateLength; i++)
        {
            var v = signal[start + i];
            sum += v * Complex.Conjugate(Template[i]);
            power += v.Real * v.Real + v.Imaginary * v.Imaginary;
        }
        return power > 1e-15 ? sum.Magnitude * sum.Magnitude / power : 0;
    }

    /// <summary>Search the audio passband for several tone-pair candidates.</summary>
    internal static Result[] DecodeWideband(float[] samples)
    {
        var results = new List<Result>();
        var decodedCandidates = new List<Oq4Search.Candidate>();
        var initial = Oq4Search.Find(samples, 4);
        bool AlreadyDecoded(Oq4Search.Candidate candidate) => decodedCandidates.Any(x =>
            Math.Abs(x.StartSeconds - candidate.StartSeconds) < .25
            && Math.Abs(x.StartHz - candidate.StartHz) < 35
            && Math.Abs(x.EndHz - candidate.EndHz) < 35);
        void TryCandidate(Oq4Search.Candidate candidate)
        {
            if (AlreadyDecoded(candidate)) return;
            var result = DecodeCandidate(samples, candidate);
            if (result is null) return;
            decodedCandidates.Add(candidate);
            if (results.Any(r => Math.Abs(r.TimeSeconds - result.TimeSeconds) < .3
                              && Math.Abs(r.FrequencyHz - result.FrequencyHz) < 30)) return;
            results.Add(result);
        }
        // Keep the original four-candidate pass and its successful decodes.
        foreach (var candidate in initial)
        {
            if (candidate.Score < 16.5) continue;
            if (decodedCandidates.Any(x => Math.Abs(x.StartSeconds - candidate.StartSeconds) < .25
                && Math.Abs(x.StartHz - candidate.StartHz) < 350
                && Math.Abs(x.EndHz - candidate.EndHz) < 45)) continue;
            TryCandidate(candidate);
        }
        if (initial.Length == 0) return results.ToArray();
        // A decoded packet is a timing anchor for weaker simultaneous returns.
        // Without a decode, use the strongest reference pair, never a callsign
        // or a highlighted frequency. No new payload assumptions are introduced.
        var anchors = decodedCandidates.Count > 0
            ? decodedCandidates.Select(c => c.StartSeconds).Distinct().Take(2).ToArray()
            : new[] { initial[0].StartSeconds };
        bool KnownCarrier(Oq4Search.Candidate candidate) => decodedCandidates.Any(c =>
            Math.Abs(c.StartSeconds - candidate.StartSeconds) < .25
            && Math.Abs(c.StartHz - candidate.StartHz) < 50);
        var ranked = Oq4Search.Find(samples, FallbackCandidatePool, expanded: true, nearTimes: anchors)
            .Where(c => !KnownCarrier(c))
            .Select(c => RankPreamble(samples, c))
            .OrderByDescending(c => c.Score).ToArray();
        var attempted = new List<Oq4Search.Candidate>();
        foreach (var trial in ranked)
        {
            if (trial.Score < MinimumFallbackPreambleScore) continue;
            var candidate = trial.Candidate;
            if (KnownCarrier(candidate) || attempted.Any(c =>
                Math.Abs(c.StartSeconds - candidate.StartSeconds) < .12
                && Math.Abs(c.StartHz - candidate.StartHz) < 12
                && Math.Abs(c.EndHz - candidate.EndHz) < 15)) continue;
            if (attempted.Count == MaximumFallbackAttempts) break;
            attempted.Add(candidate);
            TryCandidate(candidate);
            // Short/noisy references can bias the measured end frequency by a
            // few Hz. Explore bounded endpoint uncertainty only for a coherent
            // preamble, preserving the same CRC/FEC acceptance rule.
            if (!AlreadyDecoded(candidate) && trial.Score >= .15)
            {
                foreach (var delta in new[] { -3.0, 3.0 })
                {
                    TryCandidate(candidate with { EndHz = candidate.EndHz + delta });
                    if (AlreadyDecoded(candidate)) break;
                }
            }
        }
        return results.ToArray();
    }

    /// <summary>
    /// Rank reference pairs using only the fixed preamble. Four 100 ms coherent
    /// sums estimate a residual carrier within ±5 Hz without searching messages.
    /// This score prioritizes work; only the ordinary CRC/FEC path accepts data.
    /// </summary>
    internal static (double Score, Oq4Search.Candidate Candidate) RankPreamble(float[] samples, Oq4Search.Candidate candidate)
    {
        const double separation = (Oq4Waveform.PacketSampleCount - Oq4Waveform.ReferenceSamples) / 12000.0;
        var slope = (candidate.EndHz - candidate.StartHz) / separation;
        var count = Math.Min(samples.Length, (int)((candidate.StartSeconds + .85) * 12000));
        if (count < 1200) return (0, candidate);
        var mixed = MixChirp(samples[..count], candidate.StartHz - slope * .08, slope, candidate.StartSeconds);
        var initial = (int)Math.Round(candidate.StartSeconds * Rate);
        double best = 0, bestOffset = 0;
        var templatePower = Template.Skip(250).Take(400).Sum(v => v.Magnitude * v.Magnitude);
        foreach (var inverted in new[] { false, true })
        for (var t = Math.Max(0, initial - 80); t <= Math.Min(mixed.Length - TemplateLength, initial + 80); t += 4)
        {
            var sums = new Complex[4];
            double power = 0;
            for (var k = 0; k < 4; k++)
            for (var j = 0; j < 100; j++)
            {
                var i = 250 + 100 * k + j;
                var value = mixed[t + i];
                if (inverted) value = Complex.Conjugate(value);
                sums[k] += value * Complex.Conjugate(Template[i]);
                power += value.Magnitude * value.Magnitude;
            }
            Complex advance = 0;
            for (var k = 0; k < 3; k++) advance += sums[k + 1] * Complex.Conjugate(sums[k]);
            var phase = advance.Phase;
            Complex coherent = 0;
            for (var k = 0; k < 4; k++) coherent += sums[k] * Complex.FromPolarCoordinates(1, -k * phase);
            var score = coherent.Magnitude * coherent.Magnitude / Math.Max(1e-20, power * templatePower);
            if (score <= best) continue;
            best = score;
            bestOffset = (inverted ? -1 : 1) * phase / (2 * Math.PI * .1);
        }
        return (best, candidate with { StartHz = candidate.StartHz + bestOffset, EndHz = candidate.EndHz + bestOffset });
    }

    internal static Result? Decode(float[] samples, double centerHz, int searchHz = 30)
    {
        if (samples.Length < Oq4Waveform.PacketSampleCount || centerHz < 200 || centerHz > 3000) return null;
        var limit = samples.Length / Decimation - (int)Math.Ceiling(Oq4Waveform.PacketDurationSeconds * Rate) - 1;
        if (limit < 0) return null;
        double best = 0, bestFreq = 0;
        int bestStart = 0;
        // The waterfall-selected receive frequency is the center of this modest search.
        for (var f = Math.Max(200, centerHz - searchHz); f <= Math.Min(3000, centerHz + searchHz); f += 2)
        {
            var z = Mix(samples, f);
            for (var t = 0; t <= limit; t += 4)
            {
                var score = Score(z, t);
                if (score > best) { best = score; bestFreq = f; bestStart = t; }
            }
        }
        if (best < 0.01) return null;
        var coarse = bestStart;
        var coarseFreq = bestFreq;
        for (double f = coarseFreq - 2; f <= coarseFreq + 2; f += .5)
        {
            var z = Mix(samples, f);
            for (var t = Math.Max(0, coarse - 5); t <= Math.Min(limit, coarse + 5); t++)
            {
                var score = Score(z, t);
                if (score > best) { best = score; bestFreq = f; bestStart = t; }
            }
        }
        // Measure the end reference relative to the preamble frequency. A short
        // tone gives a coarse slope; pilot interpolation removes residual phase.
        double endFreq = bestFreq, toneBest = 0;
        for (double f = bestFreq - 20; f <= bestFreq + 20; f += .5)
        {
            Complex sum = 0;
            var start = (int)((bestStart / 1000.0 + 2.39) * 12000);
            for (var i = 0; i < 1100 && start + i < samples.Length; i++)
                sum += samples[start + i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * i / 12000);
            if (sum.Magnitude > toneBest) { toneBest = sum.Magnitude; endFreq = f; }
        }
        var slope = (endFreq - bestFreq) / 2.1;
        var mixed = MixChirp(samples, bestFreq, slope, bestStart / 1000.0);
        // The reference tone can dominate the coarse correlation. Refine the
        // start using the known QPSK preamble itself.
        var timingCenter = bestStart;
        double timingScore = 0;
        for (var t = Math.Max(0, timingCenter - 100); t <= Math.Min(limit, timingCenter + 100); t += 2)
        {
            Complex sync = 0;
            for (var k = 0; k < 32; k++)
            {
                var c = t + (0.16 + (6.0 + k) / 64) * Rate;
                Complex symbol = 0;
                var lo = Math.Max(0, (int)Math.Ceiling(c - 6.0 * Rate / 64));
                var hi = Math.Min(mixed.Length - 1, (int)Math.Floor(c + 6.0 * Rate / 64));
                for (var i = lo; i <= hi; i++) symbol += mixed[i] * Rrc((i - c) * 64 / Rate);
                sync += Complex.Conjugate(Known[k]) * symbol;
            }
            if (sync.Magnitude > timingScore) { timingScore = sync.Magnitude; bestStart = t; }
        }
        foreach (var frequencyOffset in new[] { 0.0, -2.0, -1.0, 1.0, 2.0 })
        foreach (var slopeOffset in new[] { 0.0, 1.0, -1.0, 2.0, -2.0, 1.5, -1.5, 0.5, -0.5 })
        {
            var trial = frequencyOffset == 0 && slopeOffset == 0 ? mixed
                : MixChirp(samples, bestFreq + frequencyOffset, slope + slopeOffset, bestStart / 1000.0);
            foreach (var delta in new[] { 0, 2, -2, 4, -4, 6, -6 })
            {
                var start = bestStart + delta;
                if (start < 0 || start > limit) continue;
                var decoded = DecodeAt(trial, start, bestFreq + frequencyOffset);
                if (decoded is not null) return decoded;
            }
        }
        return null;
    }

    internal static Result? DecodeCandidate(float[] samples, Oq4Search.Candidate candidate)
    {
        const double separation = (Oq4Waveform.PacketSampleCount - Oq4Waveform.ReferenceSamples) / 12000.0;
        var averageSlope = (candidate.EndHz - candidate.StartHz) / separation;
        var initialTime = (int)Math.Round(candidate.StartSeconds * Rate);
        var limit = samples.Length / Decimation - (int)Math.Ceiling(Oq4Waveform.PacketDurationSeconds * Rate) - 1;
        if (initialTime < 0 || initialTime > limit) return null;
        var trials = new List<(double Score, double Frequency, double Slope, double Acceleration, int Start, Complex[] Mixed)>();
        foreach (var frequencyOffset in new[] { 0.0, -1.0, 1.0, -.5, .5 })
        foreach (var curve in new[] { 0.0, 1.0, -1.0 })
        {
            // Preserve the measured total frequency change while exploring
            // linear drift and two smoothly curved trajectories.
            var acceleration = curve * 2 * averageSlope / separation;
            // The spectral estimates are centered 80 ms into each reference
            // tone, not at packet start. Refer both slope and carrier to the
            // packet start before removing fast Doppler drift.
            const double referenceCenter = .08;
            var slope = averageSlope - acceleration * (separation / 2 + referenceCenter);
            var frequency = candidate.StartHz - slope * referenceCenter
                            - .5 * acceleration * referenceCenter * referenceCenter + frequencyOffset;
            // Both orientations share the same mixer and low-pass filter.
            var originalMixed = MixTrajectory(samples, frequency, slope, acceleration, candidate.StartSeconds);
            foreach (var inverted in new[] { false, true })
            {
                var mixed = inverted ? originalMixed.Select(Complex.Conjugate).ToArray() : originalMixed;
                double best = 0; var bestStart = initialTime;
                for (var t = Math.Max(0, initialTime - 80); t <= Math.Min(limit, initialTime + 80); t += 4)
                {
                    var score = SyncScore(mixed, t);
                    if (score > best) { best = score; bestStart = t; }
                }
                trials.Add((best, frequency, slope, acceleration, bestStart, mixed));
            }
        }
        var softTrials = new List<SoftTrial>();
        // The preamble identifies plausible timing and carrier; CRC validates
        // each trial. Keep several trajectories because the short preamble
        // cannot reliably determine curvature at weak signal levels.
        foreach (var trial in trials.OrderByDescending(x => x.Score))
        {
            foreach (var delta in new[] { 0, -2, 2, -4, 4, -6, 6 })
            {
                var t = trial.Start + delta;
                if (t < 0 || t > limit) continue;
                var decoded = DecodeAt(trial.Mixed, t, trial.Frequency, softTrials);
                if (decoded is not null) return decoded;
            }
        }
        foreach (var trial in softTrials.OrderByDescending(x => x.Quality))
        {
            var message = Oq4OrderedStatisticsDecoder.Decode(trial.Likelihoods, trial.Quality);
            if (message is not null) return new Result(message, trial.Frequency, trial.Start / 1000.0, MessageSnr(trial.Likelihoods, message, trial.SnrDb));
        }
        // Preserve the ordinary BP/OSD path. Feedback is a bounded last resort.
        foreach (var trial in softTrials.OrderByDescending(x => x.Quality))
        {
            var message = DecodeWithPhaseFeedback(trial.Likelihoods);
            if (message is not null)
                return new Result(message, trial.Frequency, trial.Start / 1000.0,
                    MessageSnr(trial.Likelihoods, message, trial.SnrDb));
        }
        return null;
    }

    internal static string? DecodeWithPhaseFeedback(float[] channel)
    {
        if (channel.Length != 174 || channel.Any(x => !float.IsFinite(x))) return null;
        var current = (float[])channel.Clone();
        var accumulated = new double[87];
        // Each update uses parity-check extrinsic information, never a guessed
        // message. Excluding the symbol's own channel evidence avoids a trivial
        // self-confirming hard-decision phase detector.
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var extrinsic = Ft8Native.Oq4Extrinsic(current);
            if (extrinsic is null) return null;
            var correlation = new Complex[87];
            for (var i = 0; i < 87; i++)
            {
                var expected = new Complex(-Math.Tanh(extrinsic[2*i]/2), -Math.Tanh(extrinsic[2*i+1]/2));
                var observed = new Complex(-current[2*i], -current[2*i+1]);
                correlation[i] = observed * Complex.Conjugate(expected);
            }
            for (var i = 0; i < 87; i++)
            {
                Complex sum = 0;
                // Stay within pilot-delimited intervals; zero phase is the
                // pilot-derived prior. Triangular smoothing suppresses noise.
                var first = i/16*16;
                var last = Math.Min(first+15, 86);
                double evidence = 0;
                for (var j = first; j <= last; j++)
                {
                    var weight = Math.Max(0, 1-Math.Abs(i-j)/12.0);
                    sum += weight*correlation[j]; evidence += weight*correlation[j].Magnitude;
                }
                var coherence = evidence > 1e-12 ? sum.Magnitude/evidence : 0;
                var update = coherence < .5 ? 0 : .5*Math.Clamp(sum.Phase, -.5, .5);
                accumulated[i] = Math.Clamp(accumulated[i]+update, -.65, .65);
                var value = new Complex(-channel[2*i], -channel[2*i+1])
                    * Complex.FromPolarCoordinates(1, -accumulated[i]);
                current[2*i] = (float)-value.Real; current[2*i+1] = (float)-value.Imaginary;
            }
            var message = Ft8Native.DecodeOq4Codeword(current);
            if (message is not null) return message;
        }
        return null;
    }

    private static float MessageSnr(float[] channel, string message, float fallback)
    {
        // Use all data symbols after CRC, with the original observations:
        // feedback must not manufacture a higher signal report.
        try
        {
            return EstimateSnr(channel.Select(x => (double)x).ToArray(), Ft8Native.EncodeOq4Codeword(message));
        }
        catch (ArgumentException)
        {
            // An unresolved hashed callsign may not round-trip through the text
            // packer. Preserve the independent known-preamble estimate.
            return fallback;
        }
    }

    private static Result? DecodeAt(Complex[] mixed, int bestStart, double bestFreq, List<SoftTrial>? softTrials = null)
    {
        var symbols = ReadSymbols(mixed, bestStart);
        Complex preamble = 0;
        for (var i = 0; i < 32; i++) preamble += Complex.Conjugate(Known[i]) * symbols[i];
        if (preamble.Magnitude < 1e-8) return null;
        var preambleEnergy = symbols.Take(32).Sum(z => z.Real*z.Real+z.Imaginary*z.Imaginary);
        var quality = preamble.Magnitude * preamble.Magnitude / Math.Max(64 * preambleEnergy, 1e-15);
        var rotation = Complex.FromPolarCoordinates(1, -preamble.Phase);
        for (var i = 0; i < symbols.Length; i++) symbols[i] *= rotation;
        var positions = new List<double> { 15.5 };
        var phases = new List<double> { 0 };
        int p = 32, known = 32;
        for (var i = 0; i < 87; i++)
        {
            p++;
            if ((i + 1) % 16 != 0 || i == 86) continue;
            var v = Complex.Conjugate(Known[known]) * symbols[p] + Complex.Conjugate(Known[known + 1]) * symbols[p + 1];
            var phase = v.Phase;
            while (phase - phases[^1] > Math.PI) phase -= 2 * Math.PI;
            while (phase - phases[^1] < -Math.PI) phase += 2 * Math.PI;
            positions.Add(p + .5); phases.Add(phase); p += 2; known += 2;
        }
        // Try pilot interpolation and a smoothed low-order phase trajectory.
        var smoothPhases = SmoothPilotPhases(positions, phases);
        foreach (var phaseMode in new[] { 0, 1, 2 })
        {
            var track = phaseMode == 0 ? phases : phaseMode == 1 ? smoothPhases : new List<double> { 0, 0, 0, 0, 0, 0 };
            var raw = new double[174]; p = 32; int interval = 0;
            for (var i = 0; i < 87; i++)
            {
                while (interval + 1 < positions.Count - 1 && p > positions[interval + 1]) interval++;
                var a = Math.Clamp((p - positions[interval]) / (positions[interval + 1] - positions[interval]), 0, 1);
                var phase = track[interval] * (1 - a) + track[interval + 1] * a;
                var value = symbols[p] * Complex.FromPolarCoordinates(1, -phase);
                raw[2 * i] = -value.Real; raw[2 * i + 1] = -value.Imaginary;
                p++;
                if ((i + 1) % 16 == 0 && i != 86) p += 2;
            }
            if (softTrials is not null && quality >= Oq4OrderedStatisticsDecoder.MinimumPreambleQuality)
            {
                var soft = raw.Select(x => (float)(x * 100 / preamble.Magnitude)).ToArray();
                // Limit decoder work and near-duplicate timing hypotheses.
                var duplicate = softTrials.Any(old => Math.Abs(old.Start-bestStart) <= 2
                    && Math.Abs(old.Frequency-bestFreq) < .6
                    && old.Likelihoods.Zip(soft).Sum(pair => (double)(pair.First-pair.Second)*(pair.First-pair.Second))
                        < .002 * soft.Sum(x => (double)x*x));
                if (!duplicate)
                {
                    softTrials.Add(new SoftTrial(soft, quality, bestFreq, bestStart, EstimatePreambleSnr(symbols)));
                    softTrials.Sort((a,b) => b.Quality.CompareTo(a.Quality));
                    if (softTrials.Count > 12) softTrials.RemoveAt(12);
                }
            }
            foreach (var strength in new[] { 100.0, 200.0, 40.0 })
            {
                var llr = new float[174];
                for (var i = 0; i < llr.Length; i++) llr[i] = (float)(raw[i] * strength / preamble.Magnitude);
                var message = Ft8Native.DecodeOq4Codeword(llr);
                if (message is not null)
                {
                    return new Result(message, bestFreq, bestStart / 1000.0, MessageSnr(llr, message, EstimatePreambleSnr(symbols)));
                }
            }
        }
        return null;
    }

    private static float EstimatePreambleSnr(Complex[] symbols)
    {
        var soft = symbols.Take(32).SelectMany(z => new[] { -z.Real, -z.Imaginary }).ToArray();
        var bits = Known.Take(32).SelectMany(z => new[] { (byte)(z.Real < 0 ? 1 : 0), (byte)(z.Imaginary < 0 ? 1 : 0) }).ToArray();
        return EstimateSnr(soft, bits);
    }

    // Data-symbol SNR referred to 2500 Hz, as in FT4 reports. This excludes
    // the louder reference tones. Fading and phase distortion count as noise;
    // this remains an approximate modem estimate, not receiver calibration.
    internal static float EstimateSnr(double[] softSymbols, byte[] bits)
    {
        if (bits.Length < 4 || softSymbols.Length != bits.Length
            || bits.Any(x => x > 1) || softSymbols.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("SNR requires equal finite observations and binary reference bits.");
        double amplitude = 0, noise = 0;
        for (var i = 0; i < bits.Length; i++) amplitude += softSymbols[i] * (bits[i] == 0 ? -1 : 1);
        amplitude /= bits.Length;
        for (var i = 0; i < bits.Length; i++)
        {
            var error = softSymbols[i] - amplitude * (bits[i] == 0 ? -1 : 1);
            noise += error * error;
        }
        noise /= bits.Length - 1; // One fitted amplitude; remove finite-sample power bias.
        var signal = Math.Max(0, amplitude * amplitude - noise / bits.Length);
        return (float)Math.Clamp(10 * Math.Log10(Math.Max(signal, 1e-20)
            / Math.Max(noise, 1e-20) * Oq4Waveform.SymbolRate / 2500.0), -50, 49);
    }

    // Symbol centers shift by whole samples with timing, so their filter
    // offsets and weights can be reused for every timing hypothesis.
    private static readonly (int Offset, double[] Weights)[] SymbolFilters = MakeSymbolFilters();

    private static (int Offset, double[] Weights)[] MakeSymbolFilters()
    {
        var filters = new (int Offset, double[] Weights)[129];
        for (var k = 0; k < filters.Length; k++)
        {
            var c = (0.16 + (6.0 + k) / 64) * Rate;
            var lo = (int)Math.Ceiling(c - 6.0 * Rate / 64);
            var hi = (int)Math.Floor(c + 6.0 * Rate / 64);
            var weights = new double[hi - lo + 1];
            for (var i = lo; i <= hi; i++) weights[i - lo] = Rrc((i - c) * 64 / Rate);
            filters[k] = (lo, weights);
        }
        return filters;
    }

    private static Complex[] ReadSymbols(Complex[] mixed, int bestStart)
    {
        var symbols = new Complex[129];
        for (var k = 0; k < symbols.Length; k++)
        {
            var (offset, weights) = SymbolFilters[k];
            for (var j = 0; j < weights.Length; j++)
            {
                var i = bestStart + offset + j;
                if ((uint)i < (uint)mixed.Length) symbols[k] += mixed[i] * weights[j];
            }
        }
        return symbols;
    }

    private static List<double> SmoothPilotPhases(List<double> positions, List<double> phases)
    {
        // Fit phase = a + b*t + c*t^2, weighting the 32-symbol preamble.
        var m = new double[3,4];
        for (var i = 0; i < positions.Count; i++)
        {
            var t = (positions[i] - 15.5) / 100.0;
            double[] v = { 1, t, t*t };
            var w = i == 0 ? 8.0 : 1.0;
            for (var j = 0; j < 3; j++)
            {
                for (var k = 0; k < 3; k++) m[j,k] += w*v[j]*v[k];
                m[j,3] += w*v[j]*phases[i];
            }
        }
        for (var j = 0; j < 3; j++)
        {
            var d = m[j,j];
            if (Math.Abs(d) < 1e-12) return phases;
            for (var k = j; k < 4; k++) m[j,k] /= d;
            for (var i = 0; i < 3; i++)
            {
                if (i == j) continue;
                var c = m[i,j];
                for (var k = j; k < 4; k++) m[i,k] -= c*m[j,k];
            }
        }
        return positions.Select(x => { var t = (x - 15.5)/100; return m[0,3] + m[1,3]*t + m[2,3]*t*t; }).ToList();
    }

    private static Complex[] MixChirp(float[] samples, double frequency, double slope, double start)
    {
        return MixTrajectory(samples, frequency, slope, 0, start);
    }

    private static Complex[] MixTrajectory(float[] samples, double frequency, double slope, double acceleration, double start)
    {
        var baseband = new Complex[samples.Length];
        for (var n = 0; n < samples.Length; n++)
        {
            var t = n / 12000.0;
            var relative = t - start;
            baseband[n] = samples[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * (frequency * t + .5 * slope * relative * relative + acceleration * relative * relative * relative / 6));
        }
        var output = new Complex[samples.Length / Decimation];
        const int half = 96;
        for (var i = 0; i < output.Length; i++)
        {
            var center = i * Decimation + 5;
            Complex sum = 0;
            for (var j = -half; j <= half; j++)
            {
                var n = center + j;
                if ((uint)n < (uint)baseband.Length) sum += baseband[n] * LowPass[j + half];
            }
            output[i] = sum;
        }
        return output;
    }

    private static double Rrc(double t)
    {
        if (Math.Abs(t) < 1e-9) return 1 + .25 * (4 / Math.PI - 1);
        if (Math.Abs(Math.Abs(t) - 1) < 1e-9) return .25 / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI) + (1 - 2 / Math.PI) * Math.Cos(Math.PI));
        return (Math.Sin(Math.PI * t * .75) + t * Math.Cos(Math.PI * t * 1.25)) / (Math.PI * t * (1 - t * t));
    }
}
