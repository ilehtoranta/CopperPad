using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CopperPad;
using CopperPad.Gui;

public sealed partial class WorkspaceTests
{
    private static void ActivateWithKeyboard(MainWindow window, Button button)
    {
        button.Focus(); Assert.True(button.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task AssignedSourceDetailsAndClearRemainKeyboardAccessible()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            Field<TabControl>(window, "_tabs").SelectedIndex = 1; window.UpdateLayout();
            var rows = Field<StackPanel>(window, "_mappingRows");
            var details = rows.GetVisualDescendants().OfType<Expander>().Single(x => AutomationProperties.GetName(x) == "Source details for A / South");
            Assert.False(details.IsExpanded);
            var header = details.GetVisualDescendants().OfType<ToggleButton>().Single();
            ActivateWithKeyboard(window, header);
            Assert.True(details.IsExpanded);
            Assert.True(((TextBlock)details.Content!).IsEffectivelyVisible);
            Assert.Contains("bit 0.0", ((TextBlock)details.Content!).Text);
            var clear = rows.GetVisualDescendants().OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Clear A / South");
            Assert.True(clear.IsEffectivelyVisible);
            ActivateWithKeyboard(window, clear);
            Assert.Empty(Field<EditorSession>(window, "_session").Draft.Bindings);
            Assert.False(Field<bool>(window, "_guidedMappingActive"));
            window.UpdateLayout();
            Assert.DoesNotContain(rows.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Clear A / South" && button.IsEffectivelyVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HelpAndSecondaryMappingActionsCanBeReachedByKeyboard()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            Field<TabControl>(window, "_tabs").SelectedIndex = 1; window.UpdateLayout();
            var help = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Help: Mapping");
            ActivateWithKeyboard(window, help);
            Assert.True(help.Flyout!.IsOpen);
            Assert.Contains("unassigned controls are optional", ((TextBlock)((Flyout)help.Flyout).Content!).Text);
            help.Flyout.Hide();
            ActivateWithKeyboard(window, Field<Button>(window, "_mappingMore"));
            ActivateWithKeyboard(window, Field<Button>(window, "_openAdvancedButton"));
            Assert.True(Field<Expander>(window, "_advancedEditor").IsExpanded);
            Assert.True(Field<NumericUpDown>(window, "_offsetBox").IsKeyboardFocusWithin,
                $"Focused: {window.FocusManager?.GetFocusedElement()?.GetType().Name}");
            Assert.False(Field<Button>(window, "_mappingMore").Flyout!.IsOpen);
            ActivateWithKeyboard(window, Field<Button>(window, "_mappingMore"));
            ActivateWithKeyboard(window, Field<Button>(window, "_clearAllButton"));
            Assert.Empty(Field<EditorSession>(window, "_session").Draft.Bindings);
            Assert.True(Field<Button>(window, "_undoButton").IsEnabled);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(900, 600, 1)]
    [InlineData(900, 600, 1.5)]
    [InlineData(1180, 760, 1)]
    [InlineData(1180, 760, 1.5)]
    public async Task LocalDiagramReadoutsAreAccessibleAndNeutralizedOnDisconnect(int width, int height, double scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(() =>
        {
            var host = new FakeGuiService(); using var window = Open(host);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs();
            window.Width = width; window.Height = height; window.SetRenderScaling(scale);
            host.Snapshot(new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, true, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                new Dictionary<ControllerElement, ControllerElementValue> { [ControllerElement.LeftStickX] = ControllerElementValue.Axis(.33), [ControllerElement.RightTrigger] = ControllerElementValue.Trigger(.75) },
                [ControllerProfileKind.RawInput], ControllerMappingSource.SdlGameControllerDb, "Built-in mapping", null));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var diagram = Field<GamepadView>(window, "_gamepad");
            Assert.Contains($"X {.33:0.00}", diagram.LeftStickReadout.Text);
            Assert.Contains($"RT {.75:0.00}", diagram.RightTriggerReadout.Text);
            Assert.Contains("Left stick", AutomationProperties.GetName(diagram.LeftStickReadout));
            Assert.False(Field<TextBlock>(window, "_liveNumbers").IsVisible);
            foreach (var readout in new[] { diagram.LeftStickReadout, diagram.RightStickReadout, diagram.LeftTriggerReadout, diagram.RightTriggerReadout })
            {
                AssertVisibleInWindow(window, readout);
                Assert.True(readout.FontSize >= 12);
                Assert.True(readout.Bounds.Top >= 0 && readout.Bounds.Bottom <= diagram.Bounds.Height);
            }
            Assert.True(diagram.LeftStickReadout.Bounds.Right < diagram.RightStickReadout.Bounds.Left);
            host.Snapshot(new CopperControllerSnapshot("test-pad", DateTimeOffset.UtcNow, false, "Studio controller", 0x1234, 0x5678, ControllerTransport.Usb,
                [], [ControllerProfileKind.RawInput], ControllerMappingSource.SdlGameControllerDb, "Built-in mapping", null));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("—", diagram.LeftStickReadout.Text);
            Assert.Contains("—", diagram.RightTriggerReadout.Text);
            Assert.True(Field<TextBlock>(window, "_liveNumbers").IsVisible);
            Assert.Contains("Reconnect", Field<TextBlock>(window, "_liveNumbers").Text);
        }, CancellationToken.None);
    }
}
