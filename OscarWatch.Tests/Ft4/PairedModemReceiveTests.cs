using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class PairedModemReceiveTests
{
    [Theory]
    [InlineData(800,2200)]
    [InlineData(2200,800)]
    public async Task Both_receivers_decode_the_same_audio_concurrently_without_changing_it(int ft4Hz, int oq4Hz)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(Ft8Native.IsAvailable, Ft8Native.LoadError);
        const string ft4Message = "CQ K8OTY EN73";
        const string oq4Message = "CQ K1ABC FN42";
        var ft4 = Ft8Native.EncodeFt4(ft4Message,ft4Hz)!;
        Assert.NotNull(ft4);
        var oq4 = Oq4Waveform.EncodePacket(Ft8Native.EncodeOq4Codeword(oq4Message),oq4Hz);
        var random = new Random(9510);
        var audio = ft4.Select(x => .2f*x+(float)((random.NextDouble()-.5)*.01)).ToArray();
        for (var i = 0; i < oq4.Length; i++) audio[4800+i] += .2f*oq4[i];
        var original = (float[])audio.Clone();
        var ft4Work = Task.Run(() => Ft8Native.DecodeFt4(audio,12000,200,3000));
        var oq4Work = Task.Run(() => Oq4Decoder.DecodeWideband(audio));
        await Task.WhenAll(ft4Work,oq4Work);
        Assert.Contains(await ft4Work, x => x.text == ft4Message);
        Assert.Contains(await oq4Work, x => x.Message == oq4Message);
        Assert.DoesNotContain(await ft4Work, x => x.text == oq4Message);
        Assert.DoesNotContain(await oq4Work, x => x.Message == ft4Message);
        Assert.Equal(original,audio);
    }
}
