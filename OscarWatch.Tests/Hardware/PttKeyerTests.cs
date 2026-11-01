using OscarWatch.Core.Hardware;
using OscarWatch.Hardware;

namespace OscarWatch.Tests.Hardware;

public sealed class PttKeyerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-ptt-" + Guid.NewGuid().ToString("N"));

    public PttKeyerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PttSessionMarker Marker() => PttSessionMarker.ForTerminal(Path.Combine(_dir, "settings.json"));

    private static PttOptions Options(PttMethod method, PttLine line = PttLine.Rts, bool invert = false) =>
        new(method, line, invert, "", 0, 0);

    [Theory]
    [InlineData(PttMethod.Cat)]
    [InlineData(PttMethod.CatPortHandshake)]
    [InlineData(PttMethod.Vox)]
    [InlineData(PttMethod.Manual)]
    public void Preparing_ptt_does_not_assert_cat_or_handshake(PttMethod method)
    {
        var rig = new RecordingRigController();
        using var keyer = new PttKeyer(rig, () => Options(method), "OQ4");
        keyer.Prepare();
        Assert.False(keyer.IsKeyed);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public void Separate_adapter_conflict_is_reported_during_preparation_before_keying()
    {
        var rig = new RecordingRigController();
        var options = Options(PttMethod.SeparateComPort) with { SeparatePort = "test-port" };
        using var keyer = new PttKeyer(rig, () => options, "OQ4", _ => "in use");
        Assert.Equal("in use", Assert.Throws<InvalidOperationException>(keyer.Prepare).Message);
        Assert.False(keyer.IsKeyed);
    }

    [Theory]
    [InlineData(PttMethod.Cat)]
    [InlineData(PttMethod.CatPortHandshake)]
    [InlineData(PttMethod.Vox)]
    [InlineData(PttMethod.Manual)]
    public void Passive_ft4_allows_oq4_but_cannot_key_or_unkey_its_burst(PttMethod method)
    {
        var rig = new RecordingRigController();
        using var ft4 = new PttKeyer(rig, () => Options(method), "FT4");
        using var oq4 = new PttKeyer(rig, () => Options(method), "OQ4");
        oq4.Prepare();
        Assert.Throws<InvalidOperationException>(ft4.Prepare);
        oq4.KeyNow();
        var keyedCalls = rig.Calls.ToArray();
        Assert.Throws<InvalidOperationException>(ft4.KeyNow);
        ft4.UnkeyNow(); ft4.ForceOff(); ft4.ReleasePort();
        Assert.Equal(keyedCalls,rig.Calls);
        Assert.True(oq4.IsKeyed);
        oq4.UnkeyNow();
        ft4.KeyNow();
        Assert.True(ft4.IsKeyed);
    }

    [Fact]
    public void Preparation_error_does_not_release_an_already_keyed_owner()
    {
        var rig = new RecordingRigController();
        var options = Options(PttMethod.Cat);
        using var oq4 = new PttKeyer(rig, () => options, "OQ4");
        using var ft4 = new PttKeyer(rig, () => Options(PttMethod.Cat), "FT4");
        oq4.KeyNow();
        options = Options(PttMethod.SeparateComPort);
        Assert.Throws<InvalidOperationException>(oq4.Prepare);
        Assert.Throws<InvalidOperationException>(ft4.KeyNow);
        options = Options(PttMethod.Cat);
        oq4.UnkeyNow();
        ft4.KeyNow();
        Assert.True(ft4.IsKeyed);
    }

    [Fact]
    public void Failed_preparation_releases_radio_for_the_other_mode()
    {
        var rig = new RecordingRigController();
        using var broken = new PttKeyer(rig, () => Options(PttMethod.SeparateComPort), "OQ4");
        using var ft4 = new PttKeyer(rig, () => Options(PttMethod.Cat), "FT4");
        Assert.Throws<InvalidOperationException>(broken.Prepare);
        ft4.KeyNow();
        Assert.True(ft4.IsKeyed);
    }

    [Fact]
    public async Task Cat_keying_writes_the_marker_until_unkeyed()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Cat), "Terminal", marker: marker);

        await keyer.KeyAsync();
        Assert.True(keyer.IsKeyed);
        Assert.True(marker.Exists);
        Assert.Equal(PttMethod.Cat, marker.RecordedMethod);

        await keyer.UnkeyAsync();
        Assert.False(keyer.IsKeyed);
        Assert.False(marker.Exists);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
    }

    [Fact]
    public async Task Vox_sends_nothing_and_leaves_no_marker()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Vox), "Terminal", marker: marker);

        await keyer.KeyAsync();
        Assert.False(marker.Exists);
        await keyer.UnkeyAsync();
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task Inverted_handshake_line_asserts_low()
    {
        var rig = new RecordingRigController();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.CatPortHandshake, PttLine.Dtr, invert: true), "FT4");

        await keyer.KeyAsync();
        keyer.UnkeyNow();
        Assert.Equal(["dtr:off", "dtr:on"], rig.Calls);
    }

    [Fact]
    public void Force_off_unkeys_cat_even_when_not_keyed()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.Cat);
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Cat), "Terminal", marker: marker);

        keyer.ForceOff();

        Assert.Equal(["ptt:off"], rig.Calls);
        Assert.False(marker.Exists);
    }

    [Fact]
    public void Separate_port_conflict_stops_keying()
    {
        var rig = new RecordingRigController();
        var options = new PttOptions(PttMethod.SeparateComPort, PttLine.Rts, false, "COM3", 0, 0);
        using var keyer = new PttKeyer(rig, () => options, "Terminal", _ => "in use");

        var ex = Assert.Throws<InvalidOperationException>(keyer.KeyNow);
        Assert.Equal("in use", ex.Message);
        Assert.False(keyer.IsKeyed);
    }

    [Fact]
    public async Task Crash_recovery_unkeys_cat_once_the_rig_connects()
    {
        var rig = new RecordingRigController { Connected = false };
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.Cat);
        using var recovery = new PttCrashRecovery(rig, marker);
        var recovered = new TaskCompletionSource();
        recovery.Recovered += (_, _) => recovered.TrySetResult();

        recovery.Start();
        await Task.Delay(300);
        Assert.Empty(rig.Calls);

        rig.Connected = true;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["ptt:off"], rig.Calls);
        Assert.False(marker.Exists);
        Assert.True(recovery.HasNotice);
    }

    [Fact]
    public void Crash_recovery_ignores_methods_that_drop_on_their_own()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.SeparateComPort);
        using var recovery = new PttCrashRecovery(rig, marker);

        recovery.Start();

        Assert.Empty(rig.Calls);
        Assert.False(marker.Exists);
        Assert.False(recovery.HasNotice);
    }
}
