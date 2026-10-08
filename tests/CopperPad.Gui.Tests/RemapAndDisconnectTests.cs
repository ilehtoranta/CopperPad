using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    private static Button RemapButton(MainWindow window, string label) =>
        Field<StackPanel>(window, "_mappingRows").GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == $"Remap {label}");

    private static void SendTimedReport(MainWindow window, FakeGuiService host, byte value, DateTimeOffset timestamp)
    {
        host.Report(Field<HidDeviceInfo>(window, "_selectedDevice"), [value, 0, 0, 0, 0, 0, 0, 0], timestamp);
        Invoke(window, "ProcessPendingRawReport");
        Dispatcher.UIThread.RunJobs();
    }

    // Drive the same neutral, ready, held-input and release stages as real reports,
    // using report timestamps instead of sleeping through the capture delays.
    private static DateTimeOffset ArmCapture(MainWindow window, FakeGuiService host)
    {
        var armed = Field<DateTimeOffset>(window, "_guidedArmUntil");
        SendTimedReport(window, host, 0, armed);
        var ready = Field<DateTimeOffset>(window, "_guidedAcceptInputAt");
        SendTimedReport(window, host, 0, ready);
        return ready;
    }

    private static void CaptureButton(MainWindow window, FakeGuiService host, byte value, DateTimeOffset ready)
    {
        SendTimedReport(window, host, value, ready.AddMilliseconds(10));
        SendTimedReport(window, host, value, ready.AddMilliseconds(1010));
        Assert.True(Field<bool>(window, "_guidedWaitingForNeutral"));
    }

    [Fact]
    public async Task RemapUsesSingleControlProgressAndStopsAfterHeldInputIsReleased()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            window.UpdateLayout();
            SendReport(window, host, 0);
            var original = Field<EditorSession>(window, "_session").Draft.Bindings[0];
            var remap = RemapButton(window, "B / East");
            remap.Focus(); Assert.True(remap.IsFocused);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.StartsWith("Remapping B / East", Field<TextBlock>(window, "_guidedProgress").Text);
            Assert.False(Field<Button>(window, "_backGuided").IsVisible);
            var ready = ArmCapture(window, host);
            Assert.StartsWith("Remapping B / East", Field<TextBlock>(window, "_guidedProgress").Text);
            CaptureButton(window, host, 2, ready);
            SendTimedReport(window, host, 0, ready.AddMilliseconds(1020));
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            Assert.True(Field<Button>(window, "_startGuidedMappingButton").IsVisible);
            var bindings = Field<EditorSession>(window, "_session").Draft.Bindings;
            Assert.Equal(2, bindings.Count);
            Assert.Contains(original, bindings);
            Assert.Equal(1, bindings.Single(x => x.Target == ControllerElement.East).Source.Bit);
            Assert.Contains("assigned", Field<TextBlock>(window, "_guidedPromptText").Text);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RemapRetryReplacesOnlyItsTargetAndContinueEndsCapture()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            SendReport(window, host, 0);
            Call(window, "RemapControl", ControllerElement.East);
            CaptureButton(window, host, 2, ArmCapture(window, host));
            Click(Field<Button>(window, "_retryGuided"));
            CaptureButton(window, host, 4, ArmCapture(window, host));
            Click(Field<Button>(window, "_skipGuidedMappingButton"));
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            var bindings = Field<EditorSession>(window, "_session").Draft.Bindings;
            Assert.Equal(2, bindings.Count);
            Assert.Equal(2, bindings.Single(x => x.Target == ControllerElement.East).Source.Bit);
            Assert.Equal(0, bindings.Single(x => x.Target == ControllerElement.South).Source.Bit);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemapSkipDoesNotClaimAnAssignmentOrLaunchAnotherControl(bool alreadyAssigned)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            if (alreadyAssigned) SeedBinding(window, ControllerElement.East);
            var editor = Field<EditorSession>(window, "_session"); var original = editor.Draft;
            SendReport(window, host, 0);
            Call(window, "RemapControl", ControllerElement.East);
            Click(Field<Button>(window, "_skipGuidedMappingButton"));
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            Assert.Same(original, editor.Draft);
            Assert.Contains("skipped", Field<TextBlock>(window, "_guidedPromptText").Text);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReaderDisconnectKeepsDraftOfflineAcrossSwitchingAndFiltering()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device(), Device("other")); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            host.Snapshot(new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, false, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                [], [ControllerProfileKind.RawInput], ControllerMappingSource.SdlGameControllerDb, "Built-in mapping", null));
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.Connected);
            Assert.Contains("Reconnect", Field<TextBlock>(window, "_guidedPromptText").Text);
            Assert.Contains("offline", Field<TextBlock>(window, "_deviceFilterText").Text);
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Field<ListBox>(window, "_deviceList").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Field<CheckBox>(window, "_showAllDevicesCheck").IsChecked = true;
            Field<CheckBox>(window, "_showAllDevicesCheck").IsChecked = false;
            Field<ListBox>(window, "_deviceList").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Assert.False(Field<bool>(window, "_deviceConnected"));
            Assert.Null(host.SelectedDeviceId);
            Assert.Equal(1, Field<TabControl>(window, "_tabs").SelectedIndex);
            host.Devices(Device(), Device("other")); Dispatcher.UIThread.RunJobs();
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Assert.True(editor.Connected);
            Assert.Equal("test-pad", host.SelectedDeviceId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DeviceFilterCannotHideAnEditedUnrecognizedDevice()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            var device = Device() with { ProductName = "Unknown HID device", IsGameControllerUsage = false };
            Field<CheckBox>(window, "_showAllDevicesCheck").IsChecked = true;
            host.Devices(device); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            Field<CheckBox>(window, "_showAllDevicesCheck").IsChecked = false;
            Assert.Single(Field<ListBox>(window, "_deviceList").Items);
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.Single(Field<ListBox>(window, "_deviceList").Items);
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PreviewFailureDuringDisconnectCannotPreventDraftAccess()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            host.FailPreview = true;
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.Single(Field<ListBox>(window, "_deviceList").Items);
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Assert.True(Field<Button>(window, "_revertButton").IsEnabled);
            Assert.True(Field<Border>(window, "_errorBanner").IsVisible);
            host.FailPreview = false;
            Click(Field<Button>(window, "_revertButton"));
            Assert.False(editor.IsDirty);
            Assert.Empty(Field<ListBox>(window, "_deviceList").Items);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OfflineUndoCanBeRedoneAfterRefresh()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Click(Field<Button>(window, "_undoButton"));
            Assert.False(editor.IsDirty); Assert.True(editor.CanRedo);
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.Single(Field<ListBox>(window, "_deviceList").Items);
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Click(Field<Button>(window, "_redoButton"));
            Assert.True(editor.IsDirty);
            Assert.Single(editor.Draft.Bindings);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StaleConnectedSnapshotCannotReviveAnOfflineDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            host.Devices(); Dispatcher.UIThread.RunJobs();
            host.Snapshot(new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, true, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                new Dictionary<ControllerElement, ControllerElementValue> { [ControllerElement.South] = ControllerElementValue.Button(true) },
                [ControllerProfileKind.RawInput], ControllerMappingSource.SdlGameControllerDb, "Built-in mapping", null));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Assert.False(editor.Connected); Assert.False(Field<bool>(window, "_deviceConnected"));
            Assert.Contains("Disconnected", Field<TextBlock>(window, "_deviceSubtitle").Text);
            Assert.Null(host.SelectedDeviceId);
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FullSetupStillAdvancesThroughRemainingControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            SendReport(window, host, 0);
            Click(Field<Button>(window, "_startGuidedMappingButton"));
            Assert.False(Field<bool>(window, "_guidedSingleTarget"));
            Assert.StartsWith("Control 2 of", Field<TextBlock>(window, "_guidedProgress").Text);
            var ready = ArmCapture(window, host);
            CaptureButton(window, host, 2, ready);
            SendTimedReport(window, host, 0, ready.AddMilliseconds(1020));
            Assert.True(Field<bool>(window, "_guidedMappingActive"));
            Assert.Equal(2, Field<int>(window, "_guidedTargetIndex"));
            Click(Field<Button>(window, "_skipGuidedMappingButton"));
            Assert.Equal(3, Field<int>(window, "_guidedTargetIndex"));
            Click(Field<Button>(window, "_stopGuidedMappingButton"));
            Assert.Equal(2, Field<EditorSession>(window, "_session").Draft.Bindings.Count);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisconnectDuringRemapPreservesAssignmentsAndMultipleDraftPagesThroughSaveFailure()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid().ToString());
            var host = new FakeGuiService(); using var window = Open(host, path);
            Directory.CreateDirectory(path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var first = Field<EditorSession>(window, "_session");
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            SendReport(window, host, 0);
            Call(window, "RemapControl", ControllerElement.East);
            CaptureButton(window, host, 2, ArmCapture(window, host));
            host.Devices(Device("other") with { ProductId = 2 }); Dispatcher.UIThread.RunJobs();
            Assert.Same(first, Field<EditorSession>(window, "_session"));
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            Assert.Equal(2, first.Draft.Bindings.Count);
            Field<ListBox>(window, "_deviceList").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            SeedBinding(window, ControllerElement.North);
            var other = Field<EditorSession>(window, "_session");
            Field<TabControl>(window, "_tabs").SelectedIndex = 3;
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Field<ListBox>(window, "_deviceList").Items.Count);
            Field<ListBox>(window, "_deviceList").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Same(first, Field<EditorSession>(window, "_session"));
            Assert.Equal(1, Field<TabControl>(window, "_tabs").SelectedIndex);
            Click(Field<Button>(window, "_undoButton")); Assert.Single(first.Draft.Bindings);
            Click(Field<Button>(window, "_redoButton")); Assert.Equal(2, first.Draft.Bindings.Count);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.True(first.IsDirty);
            Assert.Same(first, Field<EditorSession>(window, "_session"));
            Directory.Delete(path);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.False(first.IsDirty);
            Assert.Same(other, Field<EditorSession>(window, "_session"));
            Assert.Equal(3, Field<TabControl>(window, "_tabs").SelectedIndex);
            host.Devices(Device(), Device("other") with { ProductId = 2 }); Dispatcher.UIThread.RunJobs();
            Field<ListBox>(window, "_deviceList").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Same(first, Field<EditorSession>(window, "_session"));
            Assert.True(first.Connected); Assert.True(first.HasSavedProfile);
            Assert.Equal(1, Field<TabControl>(window, "_tabs").SelectedIndex);
            Assert.Equal(2, Assert.Single((await new FileControllerProfileStore(path).LoadAsync()).Profiles).Bindings.Count);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(900, 600, 1)]
    [InlineData(900, 600, 1.5)]
    [InlineData(1180, 760, 1)]
    [InlineData(1180, 760, 1.5)]
    public async Task RemapAndOfflineEditingFitSupportedSizes(int width, int height, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            window.Width = width; window.Height = height; window.SetRenderScaling(scale);
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            SendReport(window, host, 0);
            Call(window, "RemapControl", ControllerElement.East);
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_guidedProgress"));
            AssertVisibleInWindow(window, Field<Button>(window, "_retryGuided"));
            AssertVisibleInWindow(window, Field<Button>(window, "_stopGuidedMappingButton"));
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-review"));
            Directory.CreateDirectory(output);
            using (var remap = window.CaptureRenderedFrame())
                remap!.Save(Path.Combine(output, $"remap-{width}x{height}-{scale:0.0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            CaptureButton(window, host, 2, ArmCapture(window, host));
            host.Devices(); Dispatcher.UIThread.RunJobs();
            AssertVisibleInWindow(window, Field<ListBox>(window, "_deviceList"));
            AssertVisibleInWindow(window, Field<Button>(window, "_saveProfileButton"));
            AssertVisibleInWindow(window, Field<Button>(window, "_revertButton"));
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_guidedPromptText"));
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
            using (var offline = window.CaptureRenderedFrame())
                offline!.Save(Path.Combine(output, $"offline-mapping-{width}x{height}-{scale:0.0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }, CancellationToken.None);
    }
}
