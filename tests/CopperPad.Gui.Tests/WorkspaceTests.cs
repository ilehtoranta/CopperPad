using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CopperPad;
using CopperPad.Gui;
using System.Reflection;

[assembly: AvaloniaTestApplication(typeof(GuiTestApplication))]

public sealed class GuiTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<GuiTestApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize() { Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; CopperTheme.Install(this); }
}

public sealed class WorkspaceTests
{
    internal static HidDeviceInfo Device(string id = "test-pad") => new(id, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb, 8, new byte[] { 1, 2 }, true, false, null);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static MainWindow Open(FakeGuiService host, string? path = null)
    {
        var window = new MainWindow(host, new FileControllerProfileStore(path ?? Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json")));
        window.Show(); Dispatcher.UIThread.RunJobs(); return window;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Task CallAsync(MainWindow window, string method, params object[] args) => (Task)typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args)!;

    [Fact]
    public async Task KeyboardNavigationCanActivateFocusedButton()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            Field<TextBox>(window, "_profileName").Text = "Temporary name"; Dispatcher.UIThread.RunJobs();
            var revert = Field<Button>(window, "_revertButton");
            revert.Focus();
            Assert.True(revert.IsFocused);
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
            Assert.False(Field<EditorSession>(window, "_session").IsDirty);
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
            Assert.NotNull(window.FocusManager?.GetFocusedElement());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ImportCancellationPreservesSavedDocumentAndDirtySession()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TextBox>(window, "_profileName").Text = "Keep"; Dispatcher.UIThread.RunJobs();
            window.DialogHandler = (_, _, _) => Task.FromResult<string?>("Cancel");
            await CallAsync(window, "ImportDocumentAsync", ControllerProfileSet.Empty);
            Assert.True(Field<EditorSession>(window, "_session").IsDirty);
            var invalid = new ControllerProfileSet { Profiles = [new ControllerProfile { Name = "" }] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(window, "ImportDocumentAsync", invalid));
            Assert.True(Field<EditorSession>(window, "_session").IsDirty);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SaveFailureKeepsDraftAndImportFailureDoesNotDiscardIt()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var host = new FakeGuiService();
            // An existing directory cannot be replaced by the profile document.
            var blockedPath = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(blockedPath);
            using var window = Open(host, blockedPath);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TextBox>(window, "_profileName").Text = "Keep this draft"; Dispatcher.UIThread.RunJobs();
            Assert.True(Field<EditorSession>(window, "_session").IsDirty, "Draft must be dirty before saving");
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.True(Field<EditorSession>(window, "_session").IsDirty, Field<TextBlock>(window, "_statusText").Text);
            Assert.Contains("Could not save", Field<TextBlock>(window, "_statusText").Text);
            window.DialogHandler = (title, _, _) => Task.FromResult<string?>(title.StartsWith("Replace") ? "Replace" : "Discard");
            await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(window, "ImportDocumentAsync", ControllerProfileSet.Empty));
            Assert.Equal("Keep this draft", Field<EditorSession>(window, "_session").Draft.Name);
            Assert.True(Field<EditorSession>(window, "_session").IsDirty);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CloseCancelRetainsWindowAndSaveAllPersistsDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TextBox>(window, "_profileName").Text = "Saved on close"; Dispatcher.UIThread.RunJobs();
            window.DialogHandler = (_, _, _) => Task.FromResult<string?>("Cancel");
            window.Close(); Assert.True(window.IsVisible);
            window.DialogHandler = (_, _, _) => Task.FromResult<string?>("Save all");
            window.Close(); await Task.Delay(200);
            Assert.False(window.IsVisible);
            var saved = await new FileControllerProfileStore(path).LoadAsync();
            Assert.Equal("Saved on close", Assert.Single(saved.Profiles).Name);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GuidedNavigationRetainsAssignmentsAndDefersPreview()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            host.Report(Device(), [0,0,0,0,0,0,0,0]); Dispatcher.UIThread.RunJobs();
            Invoke(window, "StartGuidedMapping");
            Assert.True(Field<bool>(window, "_guidedMappingActive"));
            Click(Field<Button>(window, "_skipGuidedMappingButton"));
            Assert.Equal(1, Field<int>(window, "_guidedTargetIndex"));
            Click(Field<Button>(window, "_backGuided"));
            Assert.Equal(0, Field<int>(window, "_guidedTargetIndex"));
            typeof(MainWindow).GetMethod("ApplyGuidedBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportBit, Offset = 0, Bit = 0 }]);
            Click(Field<Button>(window, "_retryGuided"));
            await Task.Delay(250);
            Assert.Empty(host.Profiles.Profiles);
            Click(Field<Button>(window, "_stopGuidedMappingButton"));
            Assert.Single(Field<EditorSession>(window, "_session").Draft.Bindings);
            await Task.Delay(250);
            Assert.Single(host.Profiles.Profiles);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CalibrationReviewAndCancelKeepDraftUntouchedUntilAcceptance()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            var edit = Field<EditorSession>(window, "_session");
            edit.Edit(ProfileEditor.UpsertBinding(edit.Draft, ProfileEditor.CreateBinding(ControllerElement.LeftStickX, new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportByte, Offset = 0 }, new AxisCalibration())));
            Invoke(window, "RestoreSession");
            var before = edit.Draft;
            Invoke(window, "StartCalibrationCapture");
            var capture = Field<AxisCalibrationCapture>(window, "_calibrationCapture");
            capture.CaptureCenter(128); capture.Observe(10); capture.Observe(240);
            Invoke(window, "StopCalibrationCapture");
            Assert.Same(before, edit.Draft);
            typeof(MainWindow).GetField("_rangeReview", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            Invoke(window, "AcceptCalibrationRange");
            Assert.Equal(10, edit.Draft.Bindings[0].Axis!.Minimum);
            Assert.Equal(240, edit.Draft.Bindings[0].Axis!.Maximum);
            Invoke(window, "ResetCalibration");
            Assert.Equal(new AxisCalibration(), edit.Draft.Bindings[0].Axis);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BuiltInControllerOpensTestWithoutCustomProfile()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
        var host = new FakeGuiService(); using var window = Open(host);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Assert.True(Field<TabControl>(window, "_tabs").IsVisible);
        Assert.Equal(0, Field<TabControl>(window, "_tabs").SelectedIndex);
        Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
        Assert.Empty(Field<ControllerProfileSet>(window, "_profiles").Profiles);

        }, CancellationToken.None);
    }

    [Fact]
    public async Task RefreshDisconnectAndReconnectRetainDraftAndPage()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
        var host = new FakeGuiService(); using var window = Open(host);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Field<TabControl>(window, "_tabs").SelectedIndex = 1;
        Field<TextBox>(window, "_profileName").Text = "My edited controller";
        Dispatcher.UIThread.RunJobs();
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Assert.Equal("My edited controller", Field<TextBox>(window, "_profileName").Text);
        host.Devices(); Dispatcher.UIThread.RunJobs();
        Assert.Contains("Waiting", Field<TextBlock>(window, "_liveNumbers").Text);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Assert.Equal("My edited controller", Field<TextBox>(window, "_profileName").Text);
        Assert.Equal(1, Field<TabControl>(window, "_tabs").SelectedIndex);
        Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
        Click(Field<Button>(window, "_revertButton"));
        Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);

        }, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidNameBlocksSaveAndUndoRestoresValidDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
        var host = new FakeGuiService(); using var window = Open(host);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Field<TextBox>(window, "_profileName").Text = ""; Dispatcher.UIThread.RunJobs();
        Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
        Assert.Contains("required", Field<TextBlock>(window, "_validationText").Text);
        Click(Field<Button>(window, "_undoButton"));
        Assert.False(Field<EditorSession>(window, "_session").IsDirty);

        }, CancellationToken.None);
    }

    [Fact]
    public async Task DraftPreviewIsTemporaryAndRevertRestoresProfiles()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
        var host = new FakeGuiService(); using var window = Open(host);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        Field<TextBox>(window, "_profileName").Text = "Draft"; Dispatcher.UIThread.RunJobs();
        await Task.Delay(300);
        Assert.Equal("test-pad", host.Profiles.Profiles[0].DeviceId);
        Click(Field<Button>(window, "_revertButton"));
        await Task.Delay(300);
        Assert.Empty(host.Profiles.Profiles);

        return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(900, 600, 1)]
    [InlineData(1180, 760, 1)]
    [InlineData(900, 600, 1.5)]
    [InlineData(1180, 760, 1.5)]
    public async Task PagesRenderAtSupportedSizes(int width, int height, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
        var host = new FakeGuiService(); using var window = Open(host);
        host.Devices(Device()); Dispatcher.UIThread.RunJobs();
        window.Width = width; window.Height = height; window.SetRenderScaling(scale);
        var editing = Field<EditorSession>(window, "_session");
        editing.Edit(ProfileEditor.UpsertBinding(editing.Draft, ProfileEditor.CreateBinding(ControllerElement.LeftStickX, new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportByte, Offset = 0 }, new AxisCalibration { Minimum = 0, Maximum = 255, Center = 128, Deadzone = .15 })));
        Invoke(window, "RestoreSession");
        host.Report(Device(), [170,0,0,0,0,0,0,0]); Dispatcher.UIThread.RunJobs();
        var tabs = Field<TabControl>(window, "_tabs");
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-review"));
        Directory.CreateDirectory(output);
        for (var page = 0; page < 4; page++)
        {
            tabs.SelectedIndex = page; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output, $"page-{page}-{width}x{height}-{scale:0.0}.png"));
            Assert.True(Field<Button>(window, "_saveProfileButton").Bounds.Width > 0);
            if (page == 1) Field<Expander>(window, "_advancedEditor").IsExpanded = true;
            var scroll = (ScrollViewer)((TabItem)tabs.SelectedItem!).Content!;
            window.UpdateLayout(); scroll.ScrollToEnd(); window.UpdateLayout();
            using var lower = window.CaptureRenderedFrame();
            lower!.Save(Path.Combine(output, $"page-{page}-lower-{width}x{height}-{scale:0.0}.png"));
        }
        host.Devices(); Dispatcher.UIThread.RunJobs();
        using var empty = window.CaptureRenderedFrame();
        empty!.Save(Path.Combine(output, $"empty-{width}x{height}-{scale:0.0}.png"));

        }, CancellationToken.None);
    }
}

internal sealed class FakeGuiService : IControllerGuiService
{
    public event EventHandler<HidDevicesChangedEventArgs>? DevicesChanged;
    public event EventHandler<ControllerRawReportReceivedEventArgs>? RawReportReceived;
    public event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged;
    public ControllerProfileSet Profiles { get; private set; } = ControllerProfileSet.Empty;
    public void Devices(params HidDeviceInfo[] devices) => DevicesChanged?.Invoke(this, new HidDevicesChangedEventArgs(devices));
    public void Report(HidDeviceInfo device, byte[] bytes) => RawReportReceived?.Invoke(this, new ControllerRawReportReceivedEventArgs(device, bytes, bytes.Length, DateTimeOffset.UtcNow));
    public void Snapshot(CopperControllerSnapshot snapshot) => SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(snapshot));
    public void Start() { }
    public void Stop() { }
    public void SelectDevice(string? id) { }
    public void UpdateProfiles(ControllerProfileSet profiles) => Profiles = profiles;
    public ControllerMappingInfo? GetMappingInfo(string id) => new("SDL", "Built-in mapping");
    public void Dispose() { }
}
