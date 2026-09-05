using CopperPad;

namespace CopperPad.Gui;

internal interface IControllerGuiService : IDisposable
{
    event EventHandler<HidDevicesChangedEventArgs>? DevicesChanged;
    event EventHandler<ControllerRawReportReceivedEventArgs>? RawReportReceived;
    event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged;
    void Start();
    void Stop();
    void SelectDevice(string? id);
    void UpdateProfiles(ControllerProfileSet profiles);
    ControllerMappingInfo? GetMappingInfo(string id);
}

internal sealed class ControllerGuiService : IControllerGuiService
{
    private readonly ControllerDiagnosticsHost _host = new(new HidSharpControllerProviderOptions());
    public event EventHandler<HidDevicesChangedEventArgs>? DevicesChanged { add => _host.DevicesChanged += value; remove => _host.DevicesChanged -= value; }
    public event EventHandler<ControllerRawReportReceivedEventArgs>? RawReportReceived { add => _host.RawReportReceived += value; remove => _host.RawReportReceived -= value; }
    public event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged { add => _host.SnapshotChanged += value; remove => _host.SnapshotChanged -= value; }
    public void Start() => _host.Start();
    public void Stop() => _host.Stop();
    public void SelectDevice(string? id) => _host.SelectDevice(id);
    public void UpdateProfiles(ControllerProfileSet profiles) => _host.UpdateProfiles(profiles);
    public ControllerMappingInfo? GetMappingInfo(string id) => _host.GetMappingInfo(id);
    public void Dispose() => _host.Dispose();
}
