using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace CopperPad.Gui;

internal static class CopperTheme
{
    public static readonly ImmutableSolidColorBrush Background = new(Color.Parse("#171A1E"));
    public static readonly ImmutableSolidColorBrush Surface = new(Color.Parse("#22272D"));
    public static readonly ImmutableSolidColorBrush Copper = new(Color.Parse("#D99A70"));
    public static readonly ImmutableSolidColorBrush Muted = new(Color.Parse("#A9B3BD"));
    public static readonly ImmutableSolidColorBrush Line = new(Color.Parse("#3B444E"));
    public static readonly ImmutableSolidColorBrush Success = new(Color.Parse("#8DD4AE"));
    public static readonly ImmutableSolidColorBrush Warning = new(Color.Parse("#E6BC72"));
    public static readonly ImmutableSolidColorBrush Error = new(Color.Parse("#FF9D8E"));
    public static readonly ImmutableSolidColorBrush ErrorSurface = new(Color.Parse("#2D2222"));
    public static void Install(Application app)
    {
        app.Resources["SystemAccentColor"] = Color.Parse("#D99A70");
        app.Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Window.BackgroundProperty, Background), new Setter(Window.FontSizeProperty, 14d) } });
        app.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.MinHeightProperty, 36d), new Setter(Button.PaddingProperty, new Thickness(14, 8)), new Setter(Button.CornerRadiusProperty, new CornerRadius(6)) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("primary")) { Setters = { new Setter(Button.BackgroundProperty, Copper), new Setter(Button.ForegroundProperty, Background) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("quiet")) { Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderBrushProperty, Line), new Setter(Button.BorderThicknessProperty, new Thickness(1)) } });
        app.Styles.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TabItem.FontSizeProperty, 16d), new Setter(TabItem.PaddingProperty, new Thickness(10, 8)) } });
        app.Styles.Add(new Style(x => x.OfType<ListBox>()) { Setters = { new Setter(ListBox.BackgroundProperty, Surface) } });
        app.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch) } });
        app.Styles.Add(new Style(x => x.OfType<Expander>()) { Setters = { new Setter(Expander.BackgroundProperty, Surface), new Setter(Expander.BorderBrushProperty, Line), new Setter(Expander.BorderThicknessProperty, new Thickness(1)), new Setter(Expander.CornerRadiusProperty, new CornerRadius(6)) } });
        app.Styles.Add(new Style(x => x.OfType<Expander>().Template().OfType<ToggleButton>()) { Setters = { new Setter(ToggleButton.BackgroundProperty, Surface) } });
        app.Styles.Add(new Style(x => x.OfType<Expander>().Class("binding-row").Template().OfType<ToggleButton>()) { Setters = { new Setter(ToggleButton.BackgroundProperty, Brushes.Transparent), new Setter(ToggleButton.BorderThicknessProperty, new Thickness(0)), new Setter(ToggleButton.PaddingProperty, new Thickness(0)), new Setter(ToggleButton.MinHeightProperty, 30d) } });
    }
}
