using System.Numerics;

namespace OscarWatch.Ft4;

/// <summary>
/// Bounded order-2 reprocessing of a reliability-selected (174,91) LDPC basis.
/// Only the closest codeword is CRC checked; CRC is not used to cherry-pick a
/// plausible payload from a long list. No message or callsign hints are supplied.
/// </summary>
internal static class Oq4OrderedStatisticsDecoder
{
    private const int N = 174, K = 91;
    private const int ReprocessedBits = 16;
    internal const double MinimumPreambleQuality = .35;
    private const double MaximumRelativeDistance = .10;
    private const int MaximumHardErrors = 32;

    // Packed MSB-first parity generator from native/ft8_lib/ft8/constants.c,
    // kFTX_LDPC_generator (MIT ft8_lib). Same code as the native encoder;
    // the 91 information bits include CRC, which remains an independent check.
    private const string PackedParity = "gynOEb8x6vUJ8n/AdhwmTiXCWTNUkxMg3CZZAvsnfGQQob3AGz9BeFjNLdM+x/YgCf2k/uBBlf0DR4OgB3zMwRuIc+1cPUigKbYq/jygNvT+Gp2gYFT69fNdltOwyMPg4geY5DEO7SeISukAd1ycCOgOJt2uVjGAsLgRAowr+ZchNIfAGKDJIx/GCt9cXqMgdkcegwKgch4BsSuA/7zLgMqDQfr7R7LgZqcqFY+TJaK/ZxcAxCQ2if6FscUTY6GADf9zlBTRobNLHCcAFbSIMGNsi5mJSXLgKaicDT3oHWZUibDgTxJvN/pRy+Yb1rlAmcRyOdDZfTyE4JQAGRm3URl2ViG7Tx6ACdsS1zH67guG32uASI/DPfQ/ve6k6vtAgnQj7kC2dfdW61/gq+GXxITLdHVxRKmgK1AOS8DsWm0r290AxHSqU9cCGHYWaTYAjroaE9szkL1nGM7AdThEZzoneCzEIBLgBv+DoUXDcDWlwSaAOzdBeFjMLdM+w/YgmkpaKO4XypwySELAvCn0ZTCcl36JYQpAJmOubd+LXOK7KUiARvIx7+RXA0wYFEGAP7LOhavpsMcuBvvg3odIHygsFTlxoKLg/NfM8jxp+pm7oUEg8CYUR+lJDKjkdM7ARBARWBgZb5XN1wEgCI/DHfS/veKk6vtAuP7xtjB3KfsKB4wAWv6nrMy3e7ydmakASacBasZT9l7NyQdgGUTQhb5OfajWzH0AJR9ircQDLw7nFAAgVkcfhwKgch4AsSuAK45JI/LdUeLVN/oAa1UKQKZvR1XelcJgoYrSjU4n/pKk9shAEMLlhjiMuCo9gHWA7zSkGBfuAhM9susAfpwMVDJanBWDbgAANpPlctH95M3weehgv7LOxavhsMcuB/vgfuGCMMWDzMxX1LCAoGbLL+2vyfUmZBJguyNyWrxHzF9MxM0g3tnbo77kDFm1YJtA2acBasZT5t7NyQNgmtRq7V9wfygKtfxA5ZIcd4IlhzFtfTwgTxTagkKouG3KczUgi4tQetRn1EQd93DgIoMcnPEWlGetBLaAITuDj+KuVMOO5xgAXZJrbdcfCFGBpOEgZqt51LKe5uaVCeVglYFIaC10ijjdaLqguM4CDPBpwypyOrFA9DMdbUYWB+lXUnRgbaI7pCS5WWEzz5yApja8vHswxfvq5n/gXLDYagffZUqQiaIA8R8QaEh4D8ns3YCgH7tTZPuNLJ1zDVug/LhrxwpQydAqXQNApTRDMCnqwV8yLjTAyYnZx8PTuMVddRMAe7OLLwGG1GZDrpYgJkTrretEuUZ9H0LAYIzIV1lL+7VdaWAA";
    private static readonly Word[] Generator = MakeGenerator();

    private readonly record struct Word(ulong A, ulong B, ulong C)
    {
        public bool Bit(int n) => ((n < 64 ? A : n < 128 ? B : C) & (1UL << (n % 64))) != 0;
        public Word Set(int n) => n < 64 ? this with { A = A | (1UL << n) }
            : n < 128 ? this with { B = B | (1UL << (n-64)) }
            : this with { C = C | (1UL << (n-128)) };
        public static Word operator ^(Word a, Word b) => new(a.A^b.A, a.B^b.B, a.C^b.C);
        public int Weight => BitOperations.PopCount(A)+BitOperations.PopCount(B)+BitOperations.PopCount(C);
    }

    private static Word[] MakeGenerator()
    {
        var parity = Convert.FromBase64String(PackedParity);
        var rows = new Word[K];
        for (var i = 0; i < K; i++)
        {
            var row = new Word().Set(i);
            for (var p = 0; p < N-K; p++)
                if ((parity[p*12+i/8] & (0x80 >> (i%8))) != 0) row = row.Set(K+p);
            rows[i] = row;
        }
        return rows;
    }

    internal static bool HasValidParity(byte[] bits)
    {
        if (bits.Length != N || bits.Any(b => b > 1)) return false;
        var expected = new Word();
        for (var i = 0; i < K; i++) if (bits[i] != 0) expected ^= Generator[i];
        for (var i = 0; i < N; i++) if (expected.Bit(i) != (bits[i] != 0)) return false;
        return true;
    }

    internal static string? Decode(float[] likelihoods, double preambleQuality)
    {
        if (preambleQuality < MinimumPreambleQuality || likelihoods.Length != N
            || likelihoods.Any(x => !float.IsFinite(x))) return null;
        var totalReliability = likelihoods.Sum(x => (double)Math.Abs(x));
        if (totalReliability < 1e-9) return null;
        var order = Enumerable.Range(0, N).OrderByDescending(i => Math.Abs(likelihoods[i])).ToArray();
        var rows = (Word[])Generator.Clone();
        var pivots = new int[K];
        var rank = 0;
        // Most reliable independent positions form an information basis.
        foreach (var column in order)
        {
            var pivot = rank;
            while (pivot < K && !rows[pivot].Bit(column)) pivot++;
            if (pivot == K) continue;
            (rows[rank], rows[pivot]) = (rows[pivot], rows[rank]);
            for (var i = 0; i < K; i++) if (i != rank && rows[i].Bit(column)) rows[i] ^= rows[rank];
            pivots[rank++] = column;
            if (rank == K) break;
        }
        if (rank != K) return null;
        var hard = new Word();
        for (var i = 0; i < N; i++) if (likelihoods[i] > 0) hard = hard.Set(i);
        var initial = new Word();
        for (var i = 0; i < K; i++) if (likelihoods[pivots[i]] > 0) initial ^= rows[i];
        var best = initial;
        double bestDistance = double.PositiveInfinity;
        void Consider(Word candidate)
        {
            var mismatch = candidate ^ hard;
            double distance = 0;
            for (var block = 0; block < 3; block++)
            {
                var mask = block == 0 ? mismatch.A : block == 1 ? mismatch.B : mismatch.C;
                while (mask != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(mask);
                    distance += Math.Abs(likelihoods[64*block+bit]);
                    if (distance >= bestDistance) return;
                    mask &= mask-1;
                }
            }
            bestDistance = distance; best = candidate;
        }
        Consider(initial);
        // 1 + 16 + choose(16,2) = 137 patterns, fixed work per hypothesis.
        for (var i = K-ReprocessedBits; i < K; i++)
        {
            Consider(initial ^ rows[i]);
            for (var j = i+1; j < K; j++) Consider(initial ^ rows[i] ^ rows[j]);
        }
        if (bestDistance / totalReliability > MaximumRelativeDistance
            || (best ^ hard).Weight > MaximumHardErrors) return null;
        var checkedBits = new float[N];
        for (var i = 0; i < N; i++) checkedBits[i] = best.Bit(i) ? 8 : -8;
        // Existing native path checks parity, FT4 scrambling, CRC and unpacking.
        return Ft8Native.DecodeOq4Codeword(checkedBits);
    }
}
