using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task HeaderKeepsMappingStatesDistinctWithoutRepeatingSaveOrSelectionFeedback()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            var host = new FakeGuiService(); using var window = Open(host, path);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Built-in mapping", Field<TextBlock>(window, "_previewText").Text);
            Assert.Equal("Connected", Field<TextBlock>(window, "_deviceSubtitle").Text);
            Assert.False(Field<TextBlock>(window, "_statusText").IsVisible);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(x => x.IsEffectivelyVisible && x.Text == "Connected"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text?.Contains("Controller studio") == true);
            Assert.Contains("Connection: Usb", Field<TextBlock>(window, "_descriptorText").Text);
            SeedBinding(window);
            Field<TextBox>(window, "_profileName").Text = "My custom mapping";
            await Task.Delay(300);
            Assert.Equal("Draft preview", Field<TextBlock>(window, "_previewText").Text);
            Assert.Equal("Unsaved changes", Field<TextBlock>(window, "_saveState").Text);
            Assert.DoesNotContain("not saved", Field<TextBlock>(window, "_previewText").Text);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.Equal("Custom mapping", Field<TextBlock>(window, "_previewText").Text);
            Assert.Equal("Saved", Field<TextBlock>(window, "_saveState").Text);
            Assert.Contains("My custom mapping", AutomationProperties.GetName(Field<TextBlock>(window, "_previewText")));
            Assert.True(Field<TextBlock>(window, "_statusText").IsVisible);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CompactHeaderRetainsUnverifiedAndReconnectStatesAndAvoidsDuplicateSetupActions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService { Mapping = new("Fallback", "Generic HID convention") }; using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            var tabs = Field<TabControl>(window, "_tabs");
            Assert.Equal("Fallback · Unverified", Field<TextBlock>(window, "_previewText").Text);
            Assert.True(Field<Button>(window, "_setupButton").IsVisible);
            tabs.SelectedIndex = 1;
            Assert.False(Field<Button>(window, "_setupButton").IsVisible);
            Assert.True(Field<Button>(window, "_startGuidedMappingButton").IsVisible);
            tabs.SelectedIndex = 0;
            Assert.True(Field<Button>(window, "_setupButton").IsVisible);
            host.Snapshot(new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, false, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                new Dictionary<ControllerElement, ControllerElementValue>(), [ControllerProfileKind.RawInput], ControllerMappingSource.None, null, null));
            Dispatcher.UIThread.RunJobs(); tabs.SelectedIndex = 3;
            Assert.Equal("Disconnected", Field<TextBlock>(window, "_deviceSubtitle").Text);
            Assert.Contains("Reconnect", Field<TextBlock>(window, "_liveNumbers").Text);
            host.Devices(); Dispatcher.UIThread.RunJobs();
            Assert.False(Field<Border>(window, "_connectionBadge").IsVisible);
            Assert.False(Field<TextBlock>(window, "_previewText").IsVisible);
            Assert.Contains("Connect", Field<TextBlock>(window, "_deviceTitle").Text);
            Assert.False(Field<TextBlock>(window, "_statusText").IsVisible);
        }, CancellationToken.None);
    }
}
