using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    private static double GraphOutput(MainWindow window) => (double)typeof(CalibrationGraph).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Field<CalibrationGraph>(window, "_calibrationGraph"))!;

    // Compare with the production mapper without exposing library internals as public API.
    private static double RuntimeOutput(ControllerProfile profile, ControllerElement target, byte[] report)
    {
        var assembly = typeof(ControllerDiagnosticsHost).Assembly;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var device = Activator.CreateInstance(assembly.GetType("CopperPad.HidDeviceDescriptor")!, flags, null,
            ["test-pad", "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb, 8, Array.Empty<byte>(), true, false, null], null)!;
        var input = Activator.CreateInstance(assembly.GetType("CopperPad.RawControllerInput")!, flags, null, [device, report, report.Length, DateTimeOffset.UtcNow], null)!;
        var type = assembly.GetType("CopperPad.ProfileControllerMapper")!;
        var mapper = Activator.CreateInstance(type, flags, null, [profile], null)!;
        var snapshot = (CopperControllerSnapshot)type.GetMethod("Map")!.Invoke(mapper, [input])!;
        return snapshot.GetAxis(target);
    }

    [Theory]
    [InlineData(ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte, 192, false)]
    [InlineData(ControllerElement.LeftStickY, ControllerBindingSourceKind.ReportByte, 50, true)]
    [InlineData(ControllerElement.RightStickX, ControllerBindingSourceKind.ReportInt16LittleEndian, -16384, true)]
    [InlineData(ControllerElement.RightStickY, ControllerBindingSourceKind.ReportInt16LittleEndian, 20000, false)]
    [InlineData(ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportBit, 192, true)]
    [InlineData(ControllerElement.LeftStickY, ControllerBindingSourceKind.Hat, 192, true)]
    [InlineData(ControllerElement.LeftTrigger, ControllerBindingSourceKind.ReportByte, 128, true)]
    [InlineData(ControllerElement.RightTrigger, ControllerBindingSourceKind.ReportInt16LittleEndian, 128, false)]
    [InlineData(ControllerElement.LeftTrigger, ControllerBindingSourceKind.ReportBit, 1, true)]
    [InlineData(ControllerElement.RightTrigger, ControllerBindingSourceKind.Hat, 1, false)]
    public async Task CalibrationReadoutMatchesRuntimeAcrossSourcesAndInversion(ControllerElement target, ControllerBindingSourceKind kind, int raw, bool inverted)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            var axis = new AxisCalibration { Minimum = kind == ControllerBindingSourceKind.ReportInt16LittleEndian ? -32768 : 0,
                Maximum = kind == ControllerBindingSourceKind.ReportInt16LittleEndian ? 32767 : 255,
                Center = kind == ControllerBindingSourceKind.ReportInt16LittleEndian ? 0 : 128, Invert = inverted, Deadzone = .2, Saturation = .75 };
            var binding = ProfileEditor.CreateBinding(target, new ControllerBindingSource { Kind = kind, Offset = 0, Bit = 0, HatValue = kind == ControllerBindingSourceKind.Hat ? 0 : null, Invert = inverted }, axis);
            var editor = Field<EditorSession>(window, "_session");
            editor.Edit(ProfileEditor.UpsertBinding(editor.Draft, binding)); Invoke(window, "RestoreSession");
            var selector = Field<ComboBox>(window, "_calibrationTargetBox");
            selector.SelectedItem = selector.Items.OfType<MappingTargetItem>().Single(x => x.Element == target);
            var report = new byte[8]; report[0] = unchecked((byte)raw);
            if (kind == ControllerBindingSourceKind.ReportInt16LittleEndian) report[1] = unchecked((byte)(raw >> 8));
            host.Report(Device(), report); Invoke(window, "ProcessPendingRawReport"); Dispatcher.UIThread.RunJobs();
            var expected = RuntimeOutput(editor.Draft, target, report);
            Assert.Equal(expected, GraphOutput(window), 12);
            Assert.Contains($"Adjusted {expected:0.000}", Field<TextBlock>(window, "_calibrationValues").Text);
            Assert.Equal("Draft output", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(900, 600, 1)]
    [InlineData(900, 600, 1.5)]
    [InlineData(1180, 760, 1)]
    [InlineData(1180, 760, 1.5)]
    public async Task RestAndIncompleteCaptureKeepDraftOutputAndReviewIsExplicitlyTemporary(int width, int height, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte);
            window.Width = width; window.Height = height; window.SetRenderScaling(scale);
            Field<TabControl>(window, "_tabs").SelectedIndex = 2;
            SendReport(window, host, 180);
            var editor = Field<EditorSession>(window, "_session"); var original = editor.Draft;
            var expected = RuntimeOutput(original, ControllerElement.LeftStickX, [180, 0, 0, 0, 0, 0, 0, 0]);
            Assert.NotEqual(0, expected);
            Invoke(window, "AdvanceCalibration");
            Assert.Equal(expected, GraphOutput(window), 12);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-review"));
            Directory.CreateDirectory(output);
            window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"calibration-rest-{width}x{height}-{scale:0.0}.png"));
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            Assert.Equal(expected, GraphOutput(window), 12);
            Assert.Contains("Capture not applied", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
            Assert.Same(original, editor.Draft);
            Invoke(window, "RetryCalibrationRange"); SendReport(window, host, 10); SendReport(window, host, 240); SendReport(window, host, 180);
            Assert.Equal(expected, GraphOutput(window), 12);
            Invoke(window, "AdvanceCalibration");
            Assert.Equal(0, GraphOutput(window), 12);
            Assert.Equal("Capture preview · Not applied", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
            Assert.Contains($"Current draft output {expected:0.000}", Field<TextBlock>(window, "_calibrationValues").Text);
            Assert.Contains("capture preview", Field<TextBlock>(window, "_calibrationLegend").Text);
            Assert.Same(original, editor.Draft);
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_calibrationOutputLabel"));
            AssertVisibleInWindow(window, Field<TextBlock>(window, "_calibrationValues"));
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"calibration-review-{width}x{height}-{scale:0.0}.png"));
            Invoke(window, "CancelCalibration");
            Assert.Equal(expected, GraphOutput(window), 12);
            Assert.Equal("Draft output", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RangeCaptureKeepsThrottledExtremesAndIgnoresOtherDevices()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte); SendReport(window, host, 128);
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            host.Report(Device(), [10, 0, 0, 0, 0, 0, 0, 0]);
            host.Report(Device(), [240, 0, 0, 0, 0, 0, 0, 0]);
            host.Report(Device(), [128, 0, 0, 0, 0, 0, 0, 0]);
            host.Report(Device("unselected"), [0, 0, 0, 0, 0, 0, 0, 0]);
            // Review before the queued UI update: all range samples must still be included.
            Invoke(window, "AdvanceCalibration");
            var capture = Field<AxisCalibrationCapture>(window, "_calibrationCapture");
            Assert.Equal(10, capture.Minimum); Assert.Equal(240, capture.Maximum); Assert.Equal(128, capture.LastRaw);
            Invoke(window, "AcceptCalibrationRange");
            var axis = Field<EditorSession>(window, "_session").Draft.Bindings.Single().Axis!;
            Assert.Equal(10, axis.Minimum); Assert.Equal(240, axis.Maximum);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancelledPendingReportsCannotContaminateANewCapture()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte); SendReport(window, host, 128);
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            host.Report(Device(), [0, 0, 0, 0, 0, 0, 0, 0]);
            Invoke(window, "CancelCalibration");
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration");
            Invoke(window, "ProcessPendingRawReport");
            host.Report(Device(), [100, 0, 0, 0, 0, 0, 0, 0]); host.Report(Device(), [150, 0, 0, 0, 0, 0, 0, 0]);
            Invoke(window, "AdvanceCalibration");
            var capture = Field<AxisCalibrationCapture>(window, "_calibrationCapture");
            Assert.Equal(100, capture.Minimum); Assert.Equal(150, capture.Maximum);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExistingLowSaturationIsDisplayedAndPreservedWhenChangingDeadzone()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte);
            var editor = Field<EditorSession>(window, "_session"); var binding = editor.Draft.Bindings.Single();
            editor.Edit(ProfileEditor.UpsertBinding(editor.Draft, binding with { Axis = new AxisCalibration { Saturation = .01 } })); Invoke(window, "RestoreSession");
            Assert.Equal(.01m, Field<NumericUpDown>(window, "_saturationNumber").Value);
            Field<NumericUpDown>(window, "_deadzoneNumber").Value = .2m;
            Assert.Equal(.01, editor.Draft.Bindings.Single().Axis!.Saturation);
            SendReport(window, host, 200);
            Assert.Equal(RuntimeOutput(editor.Draft, ControllerElement.LeftStickX, [200, 0, 0, 0, 0, 0, 0, 0]), GraphOutput(window), 12);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OneSidedStickCaptureDoesNotPreviewOrCommitAsACompleteRange()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte); SendReport(window, host, 128);
            var editor = Field<EditorSession>(window, "_session"); var original = editor.Draft;
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration"); SendReport(window, host, 240); Invoke(window, "AdvanceCalibration");
            Assert.Contains("Draft output", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
            Invoke(window, "AcceptCalibrationRange");
            Assert.Contains("both sides", Field<TextBlock>(window, "_calibrationStatusText").Text);
            Assert.Same(original, editor.Draft);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisconnectClearsCalibrationReadingsUntilANewLiveReport()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftStickX, ControllerBindingSourceKind.ReportByte); SendReport(window, host, 200);
            Assert.NotEqual(0, GraphOutput(window));
            var snapshot = new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, false, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                new Dictionary<ControllerElement, ControllerElementValue>(), [ControllerProfileKind.RawInput], ControllerMappingSource.None, null, null);
            host.Report(Device(), [240, 0, 0, 0, 0, 0, 0, 0]);
            Call(window, "OnSnapshotChanged", snapshot);
            Assert.Equal(0, GraphOutput(window));
            Assert.Equal("No live input", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
            Assert.DoesNotContain("Raw 200", Field<TextBlock>(window, "_calibrationValues").Text);
            Call(window, "OnSnapshotChanged", snapshot with { IsConnected = true });
            Invoke(window, "ProcessPendingRawReport");
            Assert.Equal("No live input", Field<TextBlock>(window, "_calibrationOutputLabel").Text);
            SendReport(window, host, 200);
            Assert.NotEqual(0, GraphOutput(window));
            Assert.Contains("Raw 200", Field<TextBlock>(window, "_calibrationValues").Text);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 255, true)]
    [InlineData(255, 0, false)]
    public async Task TriggerCaptureRequiresNeutralOutputAtRest(byte rest, byte pressed, bool accepted)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window, ControllerElement.LeftTrigger, ControllerBindingSourceKind.ReportByte);
            Field<ComboBox>(window, "_calibrationTargetBox").SelectedIndex = 4;
            SendReport(window, host, rest);
            var editor = Field<EditorSession>(window, "_session"); var original = editor.Draft;
            Invoke(window, "AdvanceCalibration"); Invoke(window, "AdvanceCalibration"); SendReport(window, host, pressed); Invoke(window, "AdvanceCalibration"); Invoke(window, "AcceptCalibrationRange");
            if (accepted)
            {
                Assert.NotSame(original, editor.Draft);
                Assert.Equal(0, RuntimeOutput(editor.Draft, ControllerElement.LeftTrigger, [rest, 0, 0, 0, 0, 0, 0, 0]));
                Assert.Equal(1, RuntimeOutput(editor.Draft, ControllerElement.LeftTrigger, [pressed, 0, 0, 0, 0, 0, 0, 0]));
            }
            else
            {
                Assert.Same(original, editor.Draft);
                Assert.Contains("active at rest", Field<TextBlock>(window, "_calibrationStatusText").Text);
            }
        }, CancellationToken.None);
    }
}
