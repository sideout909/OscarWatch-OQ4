using System.Diagnostics;
using System.Text.Json;
using OscarWatch.Ft4;

if (args.Length == 1 && args[0] == "--phase-benchmark") return PhaseTrackingBenchmark.Run();

// Offline only: compile the production receiver sources, with no UI/audio/PTT.
var expected = new List<ExpectedDecode>();
if (args.Length >= 2 && args[0] == "--verify")
{
    expected = JsonSerializer.Deserialize<List<ExpectedDecode>>(File.ReadAllText(args[1]))
        ?? throw new InvalidDataException("Empty expectations file.");
    args = args.Skip(2).ToArray();
}
var observed = new Dictionary<string, Oq4Decoder.Result[]>();
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: Oq4Replay [--verify expectations.json] clip.f32 [clip.f32 ...] (mono, 12 kHz, little-endian float32)");
    return 2;
}
if (!Ft8Native.IsAvailable)
{
    Console.Error.WriteLine(Ft8Native.LoadError);
    return 3;
}
foreach (var file in args)
{
    var bytes = File.ReadAllBytes(file);
    if (bytes.Length % 4 != 0) throw new InvalidDataException("Expected complete float32 samples.");
    var samples = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    if (samples.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Nonfinite audio sample.");
    var watch = Stopwatch.StartNew();
    var results = Oq4Decoder.DecodeWideband(samples);
    watch.Stop();
    observed.Add(Path.GetFileName(file), results);
    Console.WriteLine(JsonSerializer.Serialize(new {
        file = Path.GetFileName(file), durationSeconds = samples.Length / 12000.0,
        elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
        candidates = Oq4Search.Find(samples, 4), results
    }));
}
var failures = expected.Where(e => !observed.TryGetValue(e.File, out var results) ||
    !results.Any(r => r.Message == e.Message && r.FrequencyHz >= e.MinFrequencyHz && r.FrequencyHz <= e.MaxFrequencyHz)).ToArray();
foreach (var failure in failures) Console.Error.WriteLine($"Missing expected decode: {failure}");
return failures.Length == 0 ? 0 : 1;

internal sealed record ExpectedDecode(string File, string Message, double MinFrequencyHz, double MaxFrequencyHz);
