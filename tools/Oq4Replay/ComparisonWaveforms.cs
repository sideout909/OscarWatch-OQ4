using System.Text.Json;
using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

internal static class ComparisonWaveforms
{
    internal static int Export(string directory)
    {
        Directory.CreateDirectory(directory);
        const string message = "CQ K8OTY EN73";
        const int rate = 12000;
        var ft4 = Ft8Native.EncodeFt4(message,1468.75f)!; // Center the four-tone group at 1500 Hz.
        var packet = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword(message),1500);
        var oq4 = new float[rate*15/2];
        Array.Copy(packet,0,oq4,4800,packet.Length);
        foreach (var (name,audio) in new[] { ("ft4",ft4), ("oq4",oq4) })
        {
            var bytes = new byte[audio.Length*sizeof(float)];
            Buffer.BlockCopy(audio,0,bytes,0,bytes.Length);
            File.WriteAllBytes(Path.Combine(directory,name+".f32"),bytes);
        }
        File.WriteAllText(Path.Combine(directory,"metadata.json"),JsonSerializer.Serialize(new {
            message, sampleRate=rate, centerHz=1500, ft4StartSeconds=.5, ft4DurationSeconds=5.04,
            oq4StartSeconds=.4, oq4DurationSeconds=Oq4Waveform.PacketDurationSeconds,
            note="Noise-free production encoder output; synthetic, not RF or a sensitivity comparison." },
            new JsonSerializerOptions { WriteIndented=true }));
        return 0;
    }
}
