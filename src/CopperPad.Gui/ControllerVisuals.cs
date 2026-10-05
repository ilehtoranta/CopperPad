using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CopperPad;
namespace CopperPad.Gui;
internal sealed class GamepadView : Control
{
    private CopperControllerSnapshot? _state;
    private ControllerElement? _highlight;
    public ControllerElement? Highlight { get => _highlight; set { _highlight = value; InvalidateVisual(); } }
    internal TextBlock LeftStickReadout { get; } = Readout();
    internal TextBlock RightStickReadout { get; } = Readout();
    internal TextBlock LeftTriggerReadout { get; } = Readout();
    internal TextBlock RightTriggerReadout { get; } = Readout();
    public GamepadView()
    {
        Height = 300; MinWidth = 300;
        Avalonia.Automation.AutomationProperties.SetName(this, "Live controller diagram");
        foreach (var readout in Readouts)
        {
            VisualChildren.Add(readout);
            LogicalChildren.Add(readout);
        }
        SetState(null);
    }
    private static TextBlock Readout() => new() { TextAlignment = TextAlignment.Center, Foreground = CopperTheme.Muted,
        FontFamily = new FontFamily("Consolas, monospace") };
    private TextBlock[] Readouts => [LeftStickReadout, RightStickReadout, LeftTriggerReadout, RightTriggerReadout];
    public void SetState(CopperControllerSnapshot? state)
    {
        _state = state;
        LeftStickReadout.Text = state == null ? "X —\nY —" : $"X {state.GetAxis(ControllerElement.LeftStickX):0.00}\nY {state.GetAxis(ControllerElement.LeftStickY):0.00}";
        RightStickReadout.Text = state == null ? "X —\nY —" : $"X {state.GetAxis(ControllerElement.RightStickX):0.00}\nY {state.GetAxis(ControllerElement.RightStickY):0.00}";
        LeftTriggerReadout.Text = state == null ? "LT —" : $"LT {state.GetAxis(ControllerElement.LeftTrigger):0.00}";
        RightTriggerReadout.Text = state == null ? "RT —" : $"RT {state.GetAxis(ControllerElement.RightTrigger):0.00}";
        foreach (var (readout, label) in new[] { (LeftStickReadout, "Left stick"), (RightStickReadout, "Right stick"), (LeftTriggerReadout, "Left trigger"), (RightTriggerReadout, "Right trigger") })
            Avalonia.Automation.AutomationProperties.SetName(readout, label + ": " + readout.Text);
        InvalidateVisual();
    }
    private static double DiagramScale(Size size) => Math.Min(size.Width / 600, size.Height / 320);
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 600, 300);
        var scale = DiagramScale(size);
        foreach (var readout in Readouts)
        {
            readout.FontSize = Math.Max(12, 14 * scale);
            readout.Measure(new Size(130 * scale, 40));
        }
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var scale = DiagramScale(finalSize);
        var origin = new Point((finalSize.Width - 600 * scale) / 2, (finalSize.Height - 320 * scale) / 2);
        void Place(TextBlock readout, double x, double y) => readout.Arrange(new Rect(origin.X + x * scale - readout.DesiredSize.Width / 2, origin.Y + y * scale, readout.DesiredSize.Width, readout.DesiredSize.Height));
        Place(LeftTriggerReadout, 155, 0); Place(RightTriggerReadout, 445, 0);
        Place(LeftStickReadout, 230, 240); Place(RightStickReadout, 370, 240);
        return finalSize;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = DiagramScale(Bounds.Size);
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 600 * scale) / 2, (Bounds.Height - 320 * scale) / 2));
        var outline = StreamGeometry.Parse("M 130,70 C 75,65 55,145 35,245 C 22,310 80,320 125,255 L 165,220 L 435,220 L 475,255 C 520,320 578,310 565,245 C 545,145 525,65 470,70 Z");
        context.DrawGeometry(CopperTheme.Background, new Pen(CopperTheme.Line, 3), outline);
        DrawButton(context, ControllerElement.LeftShoulder, 150, 55, "LB", 25);
        DrawButton(context, ControllerElement.RightShoulder, 450, 55, "RB", 25);
        DrawButton(context, ControllerElement.DPadUp, 150, 116, "↑");
        DrawButton(context, ControllerElement.DPadLeft, 118, 148, "←");
        DrawButton(context, ControllerElement.DPadRight, 182, 148, "→");
        DrawButton(context, ControllerElement.DPadDown, 150, 180, "↓");
        DrawButton(context, ControllerElement.North, 450, 112, "Y");
        DrawButton(context, ControllerElement.West, 418, 144, "X");
        DrawButton(context, ControllerElement.East, 482, 144, "B");
        DrawButton(context, ControllerElement.South, 450, 176, "A");
        DrawButton(context, ControllerElement.Select, 264, 119, "‹", 15);
        DrawButton(context, ControllerElement.Menu, 300, 100, "○", 15);
        DrawButton(context, ControllerElement.Start, 336, 119, "›", 15);
        DrawStick(context, 230, 193, ControllerElement.LeftStickX, ControllerElement.LeftStickY, ControllerElement.LeftStickButton);
        DrawStick(context, 370, 193, ControllerElement.RightStickX, ControllerElement.RightStickY, ControllerElement.RightStickButton);
        DrawTrigger(context, 95, ControllerElement.LeftTrigger, "LT");
        DrawTrigger(context, 385, ControllerElement.RightTrigger, "RT");
    }
    private void DrawButton(DrawingContext c, ControllerElement element, double x, double y, string label, double radius = 18)
    {
        var active = _state?.IsPressed(element) == true;
        c.DrawEllipse(active ? CopperTheme.Copper : CopperTheme.Surface, new Pen(active || Highlight == element ? CopperTheme.Copper : CopperTheme.Line, Highlight == element ? 4 : 2), new Point(x, y), radius, radius);
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, active ? CopperTheme.Background : CopperTheme.Muted);
        c.DrawText(text, new Point(x - text.Width / 2, y - text.Height / 2));
    }
    private void DrawStick(DrawingContext c, double x, double y, ControllerElement axisX, ControllerElement axisY, ControllerElement button)
    {
        c.DrawEllipse(null, new Pen(Highlight == axisX || Highlight == axisY || Highlight == button ? CopperTheme.Copper : CopperTheme.Line, 2), new Point(x, y), 35, 35);
        c.DrawLine(new Pen(CopperTheme.Line), new Point(x - 35, y), new Point(x + 35, y));
        c.DrawLine(new Pen(CopperTheme.Line), new Point(x, y - 35), new Point(x, y + 35));
        c.DrawEllipse(_state?.IsPressed(button) == true ? CopperTheme.Copper : CopperTheme.Success, null, new Point(x + (_state?.GetAxis(axisX) ?? 0) * 26, y - (_state?.GetAxis(axisY) ?? 0) * 26), 10, 10);
    }
    private void DrawTrigger(DrawingContext c, double x, ControllerElement element, string label)
    {
        c.DrawRectangle(Highlight == element ? CopperTheme.Copper : CopperTheme.Line, null, new Rect(x, 19, 120, 8), 4, 4);
        c.DrawRectangle(CopperTheme.Copper, null, new Rect(x, 19, Math.Clamp(_state?.GetAxis(element) ?? 0, 0, 1) * 120, 8), 4, 4);
    }
}
internal sealed class CalibrationGraph : Control
{
    private double _input, _output, _deadzone;
    private bool _trigger, _vertical;
    public CalibrationGraph() { Height = 120; }
    public void Update(double input, double output, double deadzone, bool trigger, bool vertical = false) { _input = input; _output = output; _deadzone = deadzone; _trigger = trigger; _vertical = vertical; InvalidateVisual(); }
    public override void Render(DrawingContext c)
    {
        base.Render(c);
        using var transform = c.PushTransform(Matrix.CreateScale(1, .8));
        var left = _trigger ? 16 : 130;
        var width = Math.Max(1, Bounds.Width - left - 16);
        double Position(double value) => left + (_trigger ? Math.Clamp(value, 0, 1) : (Math.Clamp(value, -1, 1) + 1) / 2) * width;
        if (!_trigger)
        {
            var center = new Point(62, 75);
            c.DrawEllipse(CopperTheme.Background, new Pen(CopperTheme.Line, 2), center, 48, 48);
            c.DrawEllipse(CopperTheme.Line, null, center, 48 * _deadzone, 48 * _deadzone);
            Point Dot(double value) => _vertical ? new Point(62, 75 - value * 44) : new Point(62 + value * 44, 75);
            c.DrawEllipse(CopperTheme.Muted, null, Dot(_input), 6, 6);
            c.DrawEllipse(null, new Pen(CopperTheme.Copper, 3), Dot(_output), 9, 9);
        }
        c.DrawRectangle(CopperTheme.Background, null, new Rect(left, 20, width, 110), 6, 6);
        var start = Position(_trigger ? 0 : -_deadzone);
        c.DrawRectangle(CopperTheme.Line, null, new Rect(start, 20, Position(_deadzone) - start, 110));
        c.DrawLine(new Pen(CopperTheme.Muted, 3), new Point(Position(_input), 30), new Point(Position(_input), 70));
        c.DrawLine(new Pen(CopperTheme.Copper, 4), new Point(Position(_output), 80), new Point(Position(_output), 120));
    }
}

internal sealed class StickView : Control
{
	private double _x;
	private double _y;

	public StickView()
	{
		Width = 220;
		Height = 220;
		MinWidth = 180;
		MinHeight = 180;
	}

	public void SetPosition(double x, double y)
	{
		_x = Math.Clamp(x, -1, 1);
		_y = Math.Clamp(y, -1, 1);
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		var size = Math.Min(Bounds.Width, Bounds.Height);
		var radius = Math.Max(10, (size / 2) - 12);
		var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
		context.DrawEllipse(Brushes.Transparent, new Pen(CopperTheme.Line, 1), center, radius, radius);
		context.DrawLine(new Pen(CopperTheme.Line, 1), new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
		context.DrawLine(new Pen(CopperTheme.Line, 1), new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
		var dot = new Point(center.X + (_x * radius), center.Y - (_y * radius));
		context.DrawEllipse(CopperTheme.Success, new Pen(CopperTheme.Success, 2), dot, 8, 8);
	}
}
