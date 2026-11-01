# Paired modem capture probe

Uses two independent production `Ft4AudioService` instances to check whether a
selected input can be opened twice and continues supplying one receiver after the
other closes. Receive only: no output streams, radio control, PTT, routing changes
or saved audio. It captures briefly and reports sample counts, not audio content.

```bash
dotnet run --project tools/ModemPairProbe -- --list
dotnet run --project tools/ModemPairProbe -- --input pulse
```

On Linux, an explicit **existing** source can be selected for this process:

```bash
PULSE_SOURCE=your_existing_source dotnet run --project tools/ModemPairProbe -- --input pulse
```

If using a user-local .NET installation whose app host cannot locate the runtime,
set `DOTNET_ROOT` to that SDK directory for the command. The build copies the
application's separate PortAudio initialization probe next to this tool.

A pass verifies capture components/backend sharing, not GUI behavior, decoder
performance, RF/PTT hardware or identical stream timing. The two receivers have
independent capture start times. See [paired evaluation](../../docs/OQ4_PAIRED_EVALUATION.md)
for actual local results, decoder tests and operating instructions.
