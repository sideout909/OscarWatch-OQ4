using System.Diagnostics;
using System.Text.Json;
using OscarWatch.Ft4;

// Exercise the same independent capture services as FT4 and OQ4. Receive only:
// no output streams, routing changes, radio connection, PTT or saved audio.
var input = args.Length == 2 && args[0] == "--input" ? args[1] : "default";
if (args.Length != 0 && !(args.Length == 2 && args[0] == "--input") && !(args.Length == 1 && args[0] == "--list"))
{
    Console.Error.WriteLine("Usage: ModemPairProbe [--list | --input device-id-or-name]");
    return 2;
}
using var ft4 = new Ft4AudioService();
using var oq4 = new Ft4AudioService();
try
{
    if (args.Length == 1 && args[0] == "--list")
    {
        Console.WriteLine(JsonSerializer.Serialize(ft4.GetInputDevices()));
        return 0;
    }
    Console.Error.WriteLine("Opening first capture...");
    ft4.StartCapture(input,input);
    Console.Error.WriteLine("Opening second capture...");
    oq4.StartCapture(input,input);
    var buffer = new float[8192];
    long Drain(Ft4AudioService source)
    {
        long count = 0;
        int n;
        while ((n = source.ReadCaptureSamples(buffer)) > 0) count += n;
        return count;
    }
    Drain(ft4); Drain(oq4);
    long firstCount = 0, secondCount = 0;
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromSeconds(3))
    {
        await Task.Delay(20);
        firstCount += Drain(ft4); secondCount += Drain(oq4);
    }
    var seconds = watch.Elapsed.TotalSeconds;
    ft4.StopCapture();
    Drain(oq4);
    long survivorCount = 0;
    watch.Restart();
    while (watch.Elapsed < TimeSpan.FromSeconds(1))
    {
        await Task.Delay(20);
        survivorCount += Drain(oq4);
    }
    var survivorSeconds = watch.Elapsed.TotalSeconds;
    var passed = firstCount > seconds*ft4.CaptureSampleRate*.8
        && secondCount > seconds*oq4.CaptureSampleRate*.8
        && survivorCount > survivorSeconds*oq4.CaptureSampleRate*.8;
    Console.WriteLine(JsonSerializer.Serialize(new { passed, input, seconds,
        ft4 = new { samples=firstCount, rate=ft4.CaptureSampleRate },
        oq4 = new { samples=secondCount, rate=oq4.CaptureSampleRate },
        afterClosingFt4 = new { samples=survivorCount, seconds=survivorSeconds },
        scope="Capture services only; not a GUI, decode or RF test. No routing changes or audio saved." },
        new JsonSerializerOptions { WriteIndented=true }));
    return passed ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Paired capture failed on '{input}': {ex.Message}");
    return 1;
}
