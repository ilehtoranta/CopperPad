using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task SaveFailurePersistsAcrossReportsAndDevicesAndKeyboardRetryClearsIt()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device(), Device("other") with { ProductId = 2 }); Dispatcher.UIThread.RunJobs();
            SeedBinding(window); await CallAsync(window, "SaveDraftProfileAsync");
            var draft = Field<EditorSession>(window, "_session");
            Assert.Contains("Save failed", Field<TextBlock>(window, "_errorTitle").Text);
            Field<ListBox>(window, "_deviceList").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Field<TabControl>(window, "_tabs").SelectedIndex = 3;
            SendReport(window, host, 1); Call(window, "SetStatus", "Ordinary live status");
            Assert.Contains("Save failed", Field<TextBlock>(window, "_errorTitle").Text);
            string? details = null;
            window.DialogHandler = (_, message, _) => { details = message; return Task.FromResult<string?>("Close"); };
            Click(Field<Button>(window, "_errorDetails"));
            Assert.Contains(path, details);
            Directory.Delete(path);
            var retry = Field<Button>(window, "_errorAction");
            retry.Focus(); Assert.True(retry.IsFocused);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            for (var attempt = 0; attempt < 40 && draft.IsDirty; attempt++) await Task.Delay(50);
            Assert.False(draft.IsDirty);
            Assert.Equal("test-pad", Field<HidDeviceInfo>(window, "_selectedDevice").Id);
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
            Assert.Single((await new FileControllerProfileStore(path).LoadAsync()).Profiles);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ValidationIsVisibleOnTestAndFixActionOpensAndFocusesName()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            Field<TextBox>(window, "_profileName").Text = "";
            Field<TabControl>(window, "_tabs").SelectedIndex = 0;
            Assert.Contains("cannot be saved", Field<TextBlock>(window, "_errorTitle").Text);
            Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Click(Field<Button>(window, "_errorAction")); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, Field<TabControl>(window, "_tabs").SelectedIndex);
            Assert.True(Field<TextBox>(window, "_profileName").IsFocused);
            Field<TextBox>(window, "_profileName").Text = "Valid";
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RevertClearsSaveFailureAndValidationForThatDevice()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            await CallAsync(window, "SaveDraftProfileAsync");
            Field<TextBox>(window, "_profileName").Text = "";
            Assert.True(Field<Border>(window, "_errorBanner").IsVisible);
            Click(Field<Button>(window, "_revertButton"));
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
            Directory.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssignmentFailureSurvivesScrollingAndNavigationAndFixRestoresFields()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Call(window, "SetSelectedTarget", ControllerElement.East);
            Field<NumericUpDown>(window, "_offsetBox").Value = 99;
            Invoke(window, "AddOrUpdateBinding");
            var tabs = Field<TabControl>(window, "_tabs"); tabs.SelectedIndex = 3;
            Assert.Contains("Input was not assigned", Field<TextBlock>(window, "_errorTitle").Text);
            Call(window, "SetSelectedTarget", ControllerElement.North);
            Field<NumericUpDown>(window, "_offsetBox").Value = 1;
            Click(Field<Button>(window, "_errorAction")); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(ControllerElement.East, ((MappingTargetItem)Field<ComboBox>(window, "_targetBox").SelectedItem!).Element);
            Assert.Equal(99, Field<NumericUpDown>(window, "_offsetBox").Value);
            Assert.True(Field<Expander>(window, "_advancedEditor").IsExpanded);
            PageScroll(tabs).ScrollToEnd(); window.UpdateLayout();
            AssertVisibleInWindow(window, Field<Border>(window, "_errorBanner"));
            Field<NumericUpDown>(window, "_offsetBox").Value = 0;
            Invoke(window, "AddOrUpdateBinding");
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
            Assert.Single(Field<EditorSession>(window, "_session").Draft.Bindings);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PreviewFailureRemainsUntilValidPreviewSucceeds()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            host.FailPreview = true;
            await Task.Delay(300);
            Assert.Contains("Preview failed", Field<TextBlock>(window, "_errorTitle").Text);
            Field<TextBox>(window, "_profileName").Text = "";
            await Task.Delay(300);
            Assert.Contains("Preview failed", Field<TextBlock>(window, "_errorTitle").Text);
            host.FailPreview = false;
            Field<TextBox>(window, "_profileName").Text = "Valid preview";
            Click(Field<Button>(window, "_errorAction"));
            await Task.Delay(300);
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
            Assert.Equal("Valid preview", Assert.Single(host.Profiles.Profiles).Name);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FailedImportKeepsSavedDocumentAndDraftAndExposesFullDetails()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            await CallAsync(window, "SaveDraftProfileAsync");
            var before = await File.ReadAllTextAsync(path);
            Field<TextBox>(window, "_profileName").Text = "Keep my edit";
            var invalid = new ControllerProfileSet { Profiles = [new ControllerProfile { Name = "" }] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(window, "ImportDocumentAsync", invalid));
            Field<TabControl>(window, "_tabs").SelectedIndex = 0;
            Assert.Contains("Import failed", Field<TextBlock>(window, "_errorTitle").Text);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.Equal("Keep my edit", Field<EditorSession>(window, "_session").Draft.Name);
            string? details = null;
            window.DialogHandler = (_, message, _) => { details = message; return Task.FromResult<string?>("Close"); };
            Click(Field<Button>(window, "_errorDetails"));
            Assert.Contains("invalid profiles", details);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FailedDeviceScanOffersRefreshAndClearsOnSuccessfulRetry()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService { FailStart = true }; using var window = Open(host);
            Assert.Contains("scan failed", Field<TextBlock>(window, "_errorTitle").Text);
            Call(window, "SetStatus", "Unrelated status");
            Assert.True(Field<Border>(window, "_errorBanner").IsVisible);
            host.FailStart = false;
            Click(Field<Button>(window, "_errorAction"));
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReaderFailureOnDisconnectStaysVisibleUntilHealthyInputReturns()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            var snapshot = new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, false, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                new Dictionary<ControllerElement, ControllerElementValue>(), [ControllerProfileKind.RawInput], ControllerMappingSource.SdlGameControllerDb, "Built-in mapping", "HID read failed: access denied");
            host.Snapshot(snapshot); Dispatcher.UIThread.RunJobs();
            Assert.Contains("access denied", Field<TextBlock>(window, "_errorMessage").Text);
            Assert.Equal("Disconnected", Field<TextBlock>(window, "_deviceSubtitle").Text);
            Assert.Contains("Reconnect", Field<TextBlock>(window, "_liveNumbers").Text);
            Field<TabControl>(window, "_tabs").SelectedIndex = 3;
            Assert.True(Field<Border>(window, "_errorBanner").IsVisible);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Assert.True(Field<Border>(window, "_errorBanner").IsVisible);
            host.Snapshot(snapshot with { IsConnected = true, Diagnostic = null }); Dispatcher.UIThread.RunJobs();
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadFailureWithoutControllerExposesDocumentLocation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "invalid JSON");
            using var window = Open(new FakeGuiService(), path);
            for (var attempt = 0; attempt < 40 && !Field<Border>(window, "_errorBanner").IsVisible; attempt++) await Task.Delay(25);
            Assert.Contains("could not be loaded", Field<TextBlock>(window, "_errorTitle").Text);
            Click(Field<Button>(window, "_errorAction")); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, Field<TabControl>(window, "_tabs").SelectedIndex);
            Assert.Contains(path, Field<TextBlock>(window, "_descriptorText").Text);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(900, 600, 1)]
    [InlineData(900, 600, 1.5)]
    [InlineData(1180, 760, 1)]
    [InlineData(1180, 760, 1.5)]
    public async Task CalibrationErrorPanelAndRecoveryControlsFitEveryPage(int width, int height, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte);
            window.Width = width; window.Height = height; window.SetRenderScaling(scale);
            var tabs = Field<TabControl>(window, "_tabs"); tabs.SelectedIndex = 2;
            SendReport(window, host, 128);
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            PageScroll(tabs).ScrollToEnd(); window.UpdateLayout(); Invoke(window, "AcceptCalibrationRange");
            Assert.Contains("non-zero range", Field<TextBlock>(window, "_errorMessage").Text);
            AssertVisibleInWindow(window, Field<Border>(window, "_errorBanner"));
            AssertVisibleInWindow(window, Field<Button>(window, "_errorAction"));
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-review"));
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"error-range-{width}x{height}-{scale:0.0}.png"));
            Invoke(window, "CancelCalibration");
            // Idle capture errors persist even when the scrolling page is hidden.
            Call(window, "SetCalibrationStatus", new string('x', 4000), true);
            for (var page = 0; page < 4; page++)
            {
                tabs.SelectedIndex = page; window.UpdateLayout(); PageScroll(tabs).ScrollToEnd(); window.UpdateLayout();
                AssertVisibleInWindow(window, Field<Border>(window, "_errorBanner"));
                AssertVisibleInWindow(window, Field<Button>(window, "_errorAction"));
                AssertVisibleInWindow(window, Field<Button>(window, "_errorDetails"));
                AssertVisibleInWindow(window, Field<Button>(window, "_saveProfileButton"));
                Assert.True(Field<Border>(window, "_errorBanner").Bounds.Height < 110);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"error-{page}-{width}x{height}-{scale:0.0}.png"));
            }
            string? details = null;
            window.DialogHandler = (_, message, _) => { details = message; return Task.FromResult<string?>("Close"); };
            Click(Field<Button>(window, "_errorDetails"));
            Assert.Contains(new string('x', 4000), details);
            Click(Field<Button>(window, "_errorAction")); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, tabs.SelectedIndex);
            Invoke(window, "AdvanceCalibration");
            Assert.False(Field<Border>(window, "_errorBanner").IsVisible);
        }, CancellationToken.None);
    }
}
