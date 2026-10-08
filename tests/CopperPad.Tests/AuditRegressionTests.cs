using CopperPad;
using System.Reflection;
using HidSharp.Reports;

public sealed class CoreAuditTests
{
    static HidDeviceDescriptor Device(byte[]? descriptor = null, bool ids = false, int vendor = 0x1234, int product = 0x5678, string name = "Audit Gamepad", bool usage = true) =>
        new("audit-pad", name, vendor, product, ControllerTransport.Usb, 64, descriptor ?? [], usage, ids, null);
    static RawControllerInput Input(HidDeviceDescriptor device, byte[] report) => new(device, report, report.Length, DateTimeOffset.UtcNow);

    [Fact]
    public void Audit_MicrosoftKeyboardMustNotBeAControllerCandidate()
    {
        var device = Device(vendor: 0x045e, product: 0x07f8, name: "Microsoft Wired Keyboard", usage: false);
        Assert.False(ControllerMapperFactory.IsCandidate(device, ControllerProfileSet.Empty));
    }

    [Fact]
    public void Audit_DiagnosticMappingMustNotAdvertiseGamepadProfiles()
    {
        var device = Device(vendor: 0x9999, product: 0x8888, name: "Unknown HID controller", usage: false);
        var mapper = ControllerMapperFactory.Create(device, ControllerProfileSet.Empty);
        var result = mapper.Map(Input(device, [0]));
        Assert.Equal(ControllerMappingSource.None, result.MappingSource);
        Assert.Null(result.StandardGamepad);
        Assert.Null(result.ExtendedGamepad);
    }

    [Fact]
    public void Audit_SwitchPackedYAxisCenterMustRemainNeutral()
    {
        var device = Device(vendor: 0x057e, product: 0x2009);
        // Full report 0x30: timer, battery, 3 button bytes, then 12-bit X/Y packed as 00 08 80.
        var report = new byte[14]; report[0] = 0x30;
        report[6] = 0x00; report[7] = 0x08; report[8] = 0x80;
        report[9] = 0x00; report[10] = 0x08; report[11] = 0x80;
        var result = new SwitchProControllerMapper().Map(Input(device, report));
        Assert.Equal(0, result.GetAxis(ControllerElement.LeftStickY), 2);
        Assert.Equal(0, result.GetAxis(ControllerElement.RightStickY), 2);
    }

    [Fact]
    public void Audit_ValidHidDescriptorMustBeDecodedInsteadOfGuessed()
    {
        // Report ID 1 with a single unsigned X axis.
        byte[] descriptor = [0x05,0x01,0x09,0x05,0xa1,0x01,0x85,0x01,0x09,0x30,0x15,0x00,0x26,0xff,0x00,0x75,0x08,0x95,0x01,0x81,0x02,0xc0];
        var device = Device(descriptor, ids: true);
        var parsed = new ReportDescriptor(descriptor);
        var item = parsed.DeviceItems.Single();
        var parser = item.CreateDeviceItemInputParser();
        // Control: the installed HidSharp version parses the report correctly when supplied a definition.
        Assert.True(parser.TryParseReport([1,255],0,item.InputReports.Single()));
        Assert.Equal(255, parser.GetValue(0).GetLogicalValue());
        Assert.Throws<ArgumentNullException>(() => parser.TryParseReport([1,255],0,null!));
        var decoded = new SdlHidInputDecoder(device).Decode(Input(device, [1,255]));
        Assert.Null(decoded.Diagnostic);
        Assert.Equal(255, decoded.GetAxis(0)!.Value.Raw);
    }

    [Fact]
    public void Audit_SdlSplitReportsMustRetainAxisAndButtonState()
    {
        // Report 1 contains X, report 2 contains button 1.
        byte[] descriptor = [0x05,0x01,0x09,0x05,0xa1,0x01,0x85,0x01,0x09,0x30,0x15,0x00,0x26,0xff,0x00,0x75,0x08,0x95,0x01,0x81,0x02,0x85,0x02,0x05,0x09,0x09,0x01,0x15,0x00,0x25,0x01,0x75,0x01,0x95,0x01,0x81,0x02,0x75,0x07,0x95,0x01,0x81,0x03,0xc0];
        var device = Device(descriptor, ids: true);
        var mapping = SdlControllerMapping.TryParse("03000000341200007856000000000000,Audit Pad,a:b0,leftx:a0,")!;
        var mapper = new SdlControllerMapper(mapping, device);
        var moved = mapper.Map(Input(device, [1,255]));
        Assert.Equal(1, moved.GetAxis(ControllerElement.LeftStickX), 3);
        var pressed = mapper.Map(Input(device, [2,1]));
        Assert.True(pressed.A);
        Assert.Equal(1, pressed.GetAxis(ControllerElement.LeftStickX), 3);
    }

    [Fact]
    public void Audit_TruncatedInt16SourceMustNotBecomeAByteAxis()
    {
        var device = Device();
        var profile = new ControllerProfile { Name = "Signed", Bindings = [new ControllerBinding { Target = ControllerElement.LeftStickX, Source = new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportInt16LittleEndian, Offset = 0 }, Axis = new AxisCalibration { Minimum = short.MinValue, Maximum = short.MaxValue, Center = 0, Deadzone = 0 }}]};
        var result = new ProfileControllerMapper(profile).Map(Input(device, [255]));
        Assert.Equal(0, result.GetAxis(ControllerElement.LeftStickX), 6);
    }

    [Fact]
    public async Task Audit_DiagnosticsMustSuppressCancelledReaders()
    {
        var device = Device(); var provider = new AuditProvider(device);
        using var host = new ControllerDiagnosticsHost(provider, new HidSharpControllerProviderOptions());
        var snapshots = new List<CopperControllerSnapshot>();
        host.SnapshotChanged += (_, args) => { lock (snapshots) snapshots.Add(args.Snapshot); };
        host.Start(); host.SelectDevice(device.Id);
        await provider.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var oldReader = (Task)typeof(ControllerDiagnosticsHost).GetField("_readerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.UpdateProfiles(new ControllerProfileSet { Profiles = [new ControllerProfile { Name = "New", Bindings = [new ControllerBinding { Target = ControllerElement.South, Source = new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportBit, Offset = 0 } }] }] });
        await provider.Second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // A native read can complete with buffered data concurrently with cancellation.
        provider.First.Complete.TrySetResult(new byte[] {255,128,128,128,0,0,0,8});
        await oldReader.WaitAsync(TimeSpan.FromSeconds(2));
        lock(snapshots) Assert.Empty(snapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_StopFromElementCallbackMustNotResurrectControllers(bool dispose)
    {
        var device = Device(); var provider = new AuditProvider(device);
        using var controllerProvider = new HidSharpControllerProvider(provider, new HidSharpControllerProviderOptions());
        using var host = new CopperControllerHost(controllerProvider);
        host.Start();
        var controller = Assert.Single(host.GetControllers());
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleEvents = new List<CopperElementChangedEventArgs>();
        controller.ElementChanged += (_, args) =>
        {
            if (args.Element != ControllerElement.South || !args.CurrentValue.IsPressed) return;
            if (dispose) host.Dispose(); else host.Stop();
            stopped.TrySetResult();
        };
        controller.ElementChanged += (_, args) => { if (args.CurrentValue.IsPressed) staleEvents.Add(args); };
        await provider.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.First.Complete.TrySetResult(new byte[] {128,128,128,128,0,0,1,8});
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(controllerProvider.GetControllers());
        Assert.Empty(host.GetControllers());
        Assert.False(controller.GetSnapshot().IsConnected);
        Assert.False(controller.Info.IsConnected);
        Assert.Empty(staleEvents);
    }

    sealed class AuditProvider(HidDeviceDescriptor device) : IHidDeviceProvider
    {
        public AuditStream First { get; } = new(); public AuditStream Second { get; } = new();
        int opens;
        public event EventHandler? Changed { add {} remove {} }
        public IReadOnlyList<HidDeviceDescriptor> GetDevices() => [device];
        public IHidInputStream Open(HidDeviceDescriptor device, TimeSpan timeout) => Interlocked.Increment(ref opens) == 1 ? First : Second;
        public void Dispose() { Second.Complete.TrySetCanceled(); }
    }
    sealed class AuditStream : IHidInputStream
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxInputReportLength => 64;
        public async ValueTask<int> ReadAsync(byte[] buffer, CancellationToken token) { Entered.TrySetResult(true); var bytes = await Complete.Task; bytes.CopyTo(buffer,0); return bytes.Length; }
        public void Dispose() => Disposed.TrySetResult(true);
    }
}
