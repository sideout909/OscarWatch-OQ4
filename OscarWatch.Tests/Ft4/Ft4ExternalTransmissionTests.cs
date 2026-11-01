using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Ft4;
using OscarWatch.Localization;
using OscarWatch.Tests.Hardware;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4ExternalTransmissionTests
{
    [Fact]
    public void External_owner_blocks_ft4_arming_and_releases_idempotently()
    {
        var settings = new Settings();
        settings.Current.Ft4.PskReporterEnabled = false;
        var rig = new RecordingRigController();
        var frequencies = new FrequencyOverlayViewModel(settings,null!,new Localization());
        // These collaborators are not used by the ownership path. No devices,
        // network services, UI, CAT connection or radio transmission are started.
        using var ft4 = new Ft4ModemService(settings,null!,frequencies,rig,null!,null!,null!,
            new Localization(),null!,null!,null!,null!);
        var lease = ft4.BeginExternalTransmission();
        Assert.Throws<InvalidOperationException>(() => ft4.BeginExternalTransmission());
        ft4.EnableTx();
        Assert.Contains("OQ4 owns",ft4.Status);
        Assert.False(ft4.IsTransmissionActive);
        ft4.StartCq(true);
        Assert.Contains("OQ4 owns",ft4.Status);
        Assert.Empty(rig.Calls);
        lease.Dispose(); lease.Dispose();
        using var next = ft4.BeginExternalTransmission();
        Assert.Empty(rig.Calls);
    }

    private sealed class Localization : ILocalizationService
    {
        public string Get(string key) => key;
        public string Get(string key, params object[] args) => key;
    }
    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "unused";
        public string? LoadError => null;
        public bool CanPersist => false;
        public string SerializeCurrent() => "{}";
        public Task ReplaceAndSaveAsync(AppSettings imported,CancellationToken cancellationToken=default) => Task.CompletedTask;
        public void Load() { }
        public Task LoadAsync(CancellationToken cancellationToken=default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken=default) => Task.CompletedTask;
        public void RequestSave() { }
        public Task FlushAsync(CancellationToken cancellationToken=default) => Task.CompletedTask;
        public void SyncGridFromLatLon() { }
        public void SyncLatLonFromGrid() { }
        public void EnsureSavedStations() { }
        public void ApplyActiveStation() { }
        public void SyncActiveStationFromGroundStation() { }
    }
}
