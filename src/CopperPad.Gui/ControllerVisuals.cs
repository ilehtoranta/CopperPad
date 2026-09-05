using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CopperPad;
namespace CopperPad.Gui;
internal sealed class GamepadView : Control
{
    private CopperControllerSnapshot? _state;
    public ControllerElement? Highlight { get; set; }
    public GamepadView() { Height = 240; MinWidth = 300; Avalonia.Automation.AutomationProperties.SetName(this, "Live controller diagram. Numeric readings and labeled controls are available below."); }
    public void SetState(CopperControllerSnapshot? state) { _state = state; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width / 600, Bounds.Height / 320);
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 600 * scale) / 2, 0));
        var outline = StreamGeometry.Parse("M 130,70 C 75,65 55,145 35,245 C 22,310 80,320 125,255 L 165,220 L 435,220 L 475,255 C 520,320 578,310 565,245 C 545,145 525,65 470,70 Z");
        context.DrawGeometry(CopperTheme.Background, new Pen(CopperTheme.Line, 3), outline);
        DrawButton(context, ControllerElement.LeftShoulder, 150, 50, "LB", 30);
        DrawButton(context, ControllerElement.RightShoulder, 450, 50, "RB", 30);
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
        c.DrawEllipse(active ? CopperTheme.Copper : CopperTheme.Surface, new Pen(active ? CopperTheme.Copper : CopperTheme.Line, 2), new Point(x, y), radius, radius);
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, active ? CopperTheme.Background : CopperTheme.Muted);
        c.DrawText(text, new Point(x - text.Width / 2, y - text.Height / 2));
    }
    private void DrawStick(DrawingContext c, double x, double y, ControllerElement axisX, ControllerElement axisY, ControllerElement button)
    {
        c.DrawEllipse(null, new Pen(CopperTheme.Line, 2), new Point(x, y), 35, 35);
        c.DrawLine(new Pen(CopperTheme.Line), new Point(x - 35, y), new Point(x + 35, y));
        c.DrawLine(new Pen(CopperTheme.Line), new Point(x, y - 35), new Point(x, y + 35));
        c.DrawEllipse(_state?.IsPressed(button) == true ? CopperTheme.Copper : CopperTheme.Success, null, new Point(x + (_state?.GetAxis(axisX) ?? 0) * 26, y - (_state?.GetAxis(axisY) ?? 0) * 26), 10, 10);
    }
    private void DrawTrigger(DrawingContext c, double x, ControllerElement element, string label)
    {
        c.DrawRectangle(CopperTheme.Line, null, new Rect(x, 15, 120, 8), 4, 4);
        c.DrawRectangle(CopperTheme.Copper, null, new Rect(x, 15, Math.Clamp(_state?.GetAxis(element) ?? 0, 0, 1) * 120, 8), 4, 4);
        c.DrawText(new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, CopperTheme.Muted), new Point(x - 25, 10));
    }
}
internal sealed class CalibrationGraph : Control
{
    private double _input, _output, _deadzone;
    private bool _trigger, _vertical;
    public CalibrationGraph() { Height = 160; }
    public void Update(double input, double output, double deadzone, bool trigger, bool vertical = false) { _input = input; _output = output; _deadzone = deadzone; _trigger = trigger; _vertical = vertical; InvalidateVisual(); }
    public override void Render(DrawingContext c)
    {
        base.Render(c);
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
        c.DrawText(new FormattedText("Raw input (top)   /   Adjusted output (bottom)   /   Shaded deadzone", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, CopperTheme.Muted), new Point(16, 139));
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
