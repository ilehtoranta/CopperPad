using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private readonly Button _mappingMore = new() { Content = "More…" };
    private readonly Button _reviewMapping = new() { Content = "Test mapping" };
    private readonly Border[] _calibrationSteps = new Border[3];
    private readonly TextBlock _leftStickValues = TextBlock(), _rightStickValues = TextBlock(), _leftTriggerValue = TextBlock(), _rightTriggerValue = TextBlock();
    private bool _mappingNotice;

    private void SetMappingPrompt(string message, bool notice = false)
    {
        _mappingNotice = notice;
        _guidedPromptText.Text = message;
        _guidedPromptText.IsVisible = _guidedMappingActive || notice || _selectedDevice == null || !_deviceConnected;
    }

    private static Button HelpButton(string name, string explanation)
    {
        var button = new Button { Content = "?", Width = 30, MinHeight = 30, Padding = new Thickness(5),
            Flyout = new Flyout { Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 } } };
        button.Classes.Add("quiet");
        ToolTip.SetTip(button, explanation);
        AutomationProperties.SetName(button, $"Help: {name}");
        return button;
    }

    private static void UseIcon(Button button, string name, string geometry)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(geometry), StrokeThickness = 1.8, Width = 20, Height = 20, Stretch = Stretch.Uniform };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
        button.Content = icon;
        button.Width = 36; button.Padding = new Thickness(8);
        button.Classes.Add("quiet");
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
    }

    private static Control LegendItem(IBrush color, Control label, bool ring = false)
    {
        var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(ring ? 5 : 2),
            Background = ring ? Brushes.Transparent : color, BorderBrush = color, BorderThickness = new Thickness(ring ? 2 : 0), VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 12, 0), Children = { swatch, label } };
    }

    private Control BuildCalibrationSteps()
    {
        var strip = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 6 };
        var names = new[] { "Rest", "Range", "Review" };
        for (var index = 0; index < names.Length; index++)
        {
            var step = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = $"{index + 1}  {names[index]}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center } };
            _calibrationSteps[index] = step;
            Grid.SetColumn(step, index); strip.Children.Add(step);
        }
        return strip;
    }

    private void UpdateCalibrationSteps(bool available)
    {
        var current = _calibrationStage switch { CalibrationStage.RestCaptured or CalibrationStage.Capturing => 1, CalibrationStage.Review => 2, _ => 0 };
        var names = new[] { "Rest", "Range", "Review" };
        for (var index = 0; index < _calibrationSteps.Length; index++)
        {
            if (_calibrationSteps[index] is not { } step) continue;
            var active = available && index == current;
            var complete = available && index < current;
            step.Background = active ? CopperTheme.Copper : CopperTheme.Surface;
            step.BorderBrush = complete ? CopperTheme.Success : active ? CopperTheme.Copper : CopperTheme.Line;
            ((TextBlock)step.Child!).Foreground = active ? CopperTheme.Background : complete ? CopperTheme.Success : CopperTheme.Muted;
            AutomationProperties.SetName(step, $"{names[index]}: {(active ? "current step" : complete ? "complete" : "pending")}");
        }
    }
}
