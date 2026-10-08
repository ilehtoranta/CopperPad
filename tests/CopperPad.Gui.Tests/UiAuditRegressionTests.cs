using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    private static void Call(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static void SendReport(MainWindow window, FakeGuiService host, byte value)
    {
        host.Report(Field<HidDeviceInfo>(window, "_selectedDevice"), [value, 0, 0, 0, 0, 0, 0, 0]);
        Invoke(window, "ProcessPendingRawReport");
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertVisibleInWindow(MainWindow window, Control control)
    {
        window.UpdateLayout();
        Assert.True(control.IsEffectivelyVisible);
        var bounds = control.Bounds.Translate(control.TranslatePoint(new Point(), window)!.Value - control.Bounds.Position);
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.Bounds.Height && bounds.Left >= 0 && bounds.Right <= window.Bounds.Width, $"{control.GetType().Name}: {bounds} outside {window.Bounds}");
    }

    [Fact]
    public async Task CalibrationUsesTheSameRawAxisValueAsRuntimeForLegacyBindings()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportBit);
            SendReport(window, host, 129);
            Assert.Contains("Raw 129", Field<TextBlock>(window, "_calibrationValues").Text);
            Invoke(window, "AdvanceCalibration");
            Assert.Equal(129, Field<AxisCalibrationCapture>(window, "_calibrationCapture").Center);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(180, 1)]
    [InlineData(330, 1)]
    [InlineData(180, 1.5)]
    [InlineData(330, 1.5)]
    public async Task LongNamesAndMultipleDraftsFitResizedSidebar(int sidebarWidth, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device() with { ProductName = "Wireless precision controller with a long manufacturer name" }); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            host.Devices(Device("second") with { ProductId = 0xABCD, ProductName = "Second wireless precision controller with a long manufacturer name" }); Dispatcher.UIThread.RunJobs();
            Field<ListBox>(window, "_deviceList").SelectedIndex = 0; Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            window.Width = 900; window.Height = 600; window.SetRenderScaling(scale);
            ((Grid)window.Content!).Children.OfType<Grid>().Single(x => x.ColumnDefinitions.Count == 3).ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
            var tabs = Field<TabControl>(window, "_tabs");
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-review"));
            Directory.CreateDirectory(output);
            for (var page = 0; page < 4; page++)
            {
                tabs.SelectedIndex = page; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                AssertVisibleInWindow(window, Field<Button>(window, "_saveAllButton"));
                AssertVisibleInWindow(window, Field<Button>(window, "_saveProfileButton"));
                AssertVisibleInWindow(window, Field<TextBlock>(window, "_deviceTitle"));
                AssertVisibleInWindow(window, Field<Border>(window, "_connectionBadge"));
                Assert.True(Field<TextBlock>(window, "_deviceTitle").Bounds.Right < Field<Border>(window, "_connectionBadge").Bounds.Left);
                if (page == 1) AssertVisibleInWindow(window, Field<Button>(window, "_startGuidedMappingButton"));
                if (page == 2) AssertVisibleInWindow(window, Field<Button>(window, "_mapCalibrationAxis"));
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"stress-{page}-sidebar-{sidebarWidth}-{scale:0.0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                PageScroll(tabs).ScrollToEnd(); window.UpdateLayout();
                using var lower = window.CaptureRenderedFrame();
                lower!.Save(Path.Combine(output, $"stress-{page}-lower-sidebar-{sidebarWidth}-{scale:0.0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RenamingBuiltInControllerCannotReplaceItWithEmptyMapping()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TextBox>(window, "_profileName").Text = "Renamed built-in";
            await Task.Delay(250);
            Assert.Empty(host.Profiles.Profiles);
            Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Assert.Contains("Assign at least one", Field<TextBlock>(window, "_validationText").Text);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.False(File.Exists(path));
            Assert.True(Field<EditorSession>(window, "_session").IsDirty);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NameAndNoReportErrorsAreVisibleWithoutOpeningAdvanced()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            window.Width = 900; window.Height = 600;
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            Field<TextBox>(window, "_profileName").Text = "";
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "StartGuidedMapping");
            Assert.False(Field<Expander>(window, "_advancedEditor").IsExpanded);
            Assert.Contains("required", Field<TextBlock>(window, "_profileNameError").Text);
            Assert.Contains("Waiting for reports", Field<TextBlock>(window, "_guidedPromptText").Text);
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_profileNameError"));
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_guidedPromptText"));
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_validationText"));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidDraftRetainsLastValidPreviewAndOfflineRevertRemainsAvailable()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            await Task.Delay(250);
            var preview = host.Profiles;
            Field<TextBox>(window, "_profileName").Text = "";
            await Task.Delay(250);
            Assert.Same(preview, host.Profiles);
            Assert.Contains("Preview unchanged", Field<TextBlock>(window, "_previewText").Text);
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.True(Field<Button>(window, "_revertButton").IsEnabled);
            Click(Field<Button>(window, "_revertButton"));
            Assert.Empty(Field<ListBox>(window, "_deviceList").Items);
            await Task.Delay(250);
            Assert.Empty(host.Profiles.Profiles);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RemapCapturesOneTargetAndRetryReplacesItsAssignment()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            SendReport(window, host, 0);
            Call(window, "RemapControl", ControllerElement.East);
            Assert.True(Field<bool>(window, "_guidedSingleTarget"));
            Assert.False(Field<Button>(window, "_backGuided").IsVisible);
            Call(window, "ApplyGuidedBinding", new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportBit, Offset = 0, Bit = 0 });
            Click(Field<Button>(window, "_retryGuided"));
            Call(window, "ApplyGuidedBinding", new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportBit, Offset = 0, Bit = 1 });
            Invoke(window, "AdvanceGuidedTarget");
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            var binding = Assert.Single(Field<EditorSession>(window, "_session").Draft.Bindings);
            Assert.Equal(ControllerElement.East, binding.Target);
            Assert.Equal(1, binding.Source.Bit);
            Assert.Contains("assigned", Field<TextBlock>(window, "_guidedPromptText").Text);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisconnectedDirtyDraftRemainsSelectableAndCanBeSaved()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var editor = Field<EditorSession>(window, "_session");
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.Same(editor, Field<EditorSession>(window, "_session"));
            Assert.Single(Field<ListBox>(window, "_deviceList").Items);
            Assert.False(editor.Connected);
            Assert.Contains("Disconnected", Field<TextBlock>(window, "_deviceSubtitle").Text);
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Invoke(window, "StartGuidedMapping");
            Assert.Contains("Reconnect", Field<TextBlock>(window, "_guidedPromptText").Text);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.False(editor.IsDirty);
            Assert.Empty(Field<ListBox>(window, "_deviceList").Items);
            Assert.Single((await new FileControllerProfileStore(path).LoadAsync()).Profiles);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SaveAllReachesOtherOfflineControllers()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var first = Field<EditorSession>(window, "_session");
            host.Devices(Device("second") with { ProductId = 0xABCD }); Dispatcher.UIThread.RunJobs();
            var list = Field<ListBox>(window, "_deviceList"); list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            SeedBinding(window);
            Assert.True(Field<Button>(window, "_saveAllButton").IsVisible);
            await CallAsync(window, "SaveAllDraftsAsync");
            Assert.False(first.IsDirty);
            Assert.False(Field<EditorSession>(window, "_session").IsDirty);
            Assert.Equal(2, (await new FileControllerProfileStore(path).LoadAsync()).Profiles.Count);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CalibrationCannotAdvanceWithoutMappedLiveAnalogInput()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Field<TabControl>(window, "_tabs").SelectedIndex = 2;
            Invoke(window, "AdvanceCalibration");
            Assert.False(Field<bool>(window, "_rangeReview"));
            Assert.Contains("Assign a raw source", Field<TextBlock>(window, "_calibrationStatusText").Text);
            SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte);
            Invoke(window, "AdvanceCalibration");
            Assert.False(Field<bool>(window, "_rangeReview"));
            Assert.False(Field<bool>(window, "_calibrationActive"));
            Assert.Equal("Capture rest", Field<Button>(window, "_calibrationAction").Content);
            Assert.Contains("Waiting for a live report", Field<TextBlock>(window, "_calibrationStatusText").Text);
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_calibrationStatusText"));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CalibrationSequenceRejectsZeroRangeAndCanRetryAcceptAndCancel()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte);
            SendReport(window, host, 128);
            var editor = Field<EditorSession>(window, "_session"); var original = editor.Draft;
            Invoke(window, "AdvanceCalibration");
            Assert.Equal("Start range capture", Field<Button>(window, "_calibrationAction").Content);
            Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
            Invoke(window, "AdvanceCalibration");
            Assert.Equal("Review range", Field<Button>(window, "_calibrationAction").Content);
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            Assert.Same(original, editor.Draft);
            Assert.Contains("non-zero range", Field<TextBlock>(window, "_calibrationStatusText").Text);
            Assert.Equal("Accept range", Field<Button>(window, "_calibrationAction").Content);
            Click(Field<Button>(window, "_retryCalibration"));
            SendReport(window, host, 10); SendReport(window, host, 240);
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            Assert.Equal(10, editor.Draft.Bindings[0].Axis!.Minimum);
            Assert.Equal(240, editor.Draft.Bindings[0].Axis!.Maximum);
            var accepted = editor.Draft;
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            SendReport(window, host, 0); SendReport(window, host, 255);
            Click(Field<Button>(window, "_cancelCalibration"));
            Assert.Same(accepted, editor.Draft);
            Assert.False(Field<bool>(window, "_rangeReview"));
            Invoke(window, "ResetCalibration");
            Assert.Equal(new AxisCalibration(), editor.Draft.Bindings[0].Axis);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(ControllerBindingSourceKind.ReportBit, 1, false, 1)]
    [InlineData(ControllerBindingSourceKind.ReportBit, 1, true, 0)]
    [InlineData(ControllerBindingSourceKind.Hat, 1, false, 1)]
    public async Task DigitalTriggerShowsRuntimeOutputAndDisablesAnalogCalibration(ControllerBindingSourceKind kind, byte value, bool inverted, int output)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            var editor = Field<EditorSession>(window, "_session");
            editor.Edit(ProfileEditor.UpsertBinding(editor.Draft, ProfileEditor.CreateBinding(ControllerElement.LeftTrigger, new ControllerBindingSource { Kind = kind, Offset = 0, Bit = 0, HatValue = kind == ControllerBindingSourceKind.Hat ? 0 : null, Invert = inverted }, null)));
            Invoke(window, "RestoreSession");
            Field<ComboBox>(window, "_calibrationTargetBox").SelectedIndex = 4;
            SendReport(window, host, value);
            Assert.Contains($"Adjusted {output:0.000}", Field<TextBlock>(window, "_calibrationValues").Text);
            Assert.False(Field<Button>(window, "_calibrationAction").IsEnabled);
            Assert.False(Field<Slider>(window, "_deadzoneSlider").IsEnabled);
            Assert.Contains("Digital input", Field<TextBlock>(window, "_calibrationStep").Text);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LabeledGridHidesEntireGamepadCardAndSidebarOptionsFitAtMinimumWidth()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            window.Width = 900; window.Height = 600;
            var root = (Grid)window.Content!;
            root.Children.OfType<Grid>().Single(x => x.ColumnDefinitions.Count == 3).ColumnDefinitions[0].Width = new GridLength(180);
            var toggle = window.GetVisualDescendants().OfType<CheckBox>().Single(x => Equals(x.Content, "Control grid"));
            toggle.IsChecked = true;
            Assert.False(Field<Border>(window, "_gamepadCard").IsVisible);
            window.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "Options")).IsExpanded = true;
            window.UpdateLayout();
            var export = window.GetVisualDescendants().OfType<Button>().Single(x => x.Content is TextBlock text && text.Text == "Export saved profiles…");
            AssertVisibleInWindow(window, export);
            Assert.True(((TextBlock)export.Content!).Bounds.Width <= export.Bounds.Width);
        }, CancellationToken.None);
    }
}
