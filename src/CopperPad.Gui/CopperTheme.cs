using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace CopperPad.Gui;

internal static class CopperTheme
{
    public static readonly SolidColorBrush Background = new(Color.Parse("#171A1E"));
    public static readonly SolidColorBrush Surface = new(Color.Parse("#22272D"));
    public static readonly SolidColorBrush Copper = new(Color.Parse("#D99A70"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#A9B3BD"));
    public static readonly SolidColorBrush Line = new(Color.Parse("#3B444E"));
    public static readonly SolidColorBrush Success = new(Color.Parse("#8DD4AE"));
    public static readonly SolidColorBrush Warning = new(Color.Parse("#E6BC72"));
    public static void Install(Application app)
    {
        app.Resources["SystemAccentColor"] = Color.Parse("#D99A70");
        app.Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Window.BackgroundProperty, Background), new Setter(Window.FontSizeProperty, 14d) } });
        app.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.MinHeightProperty, 36d), new Setter(Button.PaddingProperty, new Thickness(14, 8)), new Setter(Button.CornerRadiusProperty, new CornerRadius(6)) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("primary")) { Setters = { new Setter(Button.BackgroundProperty, Copper), new Setter(Button.ForegroundProperty, Background) } });
        app.Styles.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TabItem.FontSizeProperty, 16d), new Setter(TabItem.PaddingProperty, new Thickness(10, 8)) } });
        app.Styles.Add(new Style(x => x.OfType<ListBox>()) { Setters = { new Setter(ListBox.BackgroundProperty, Surface) } });
    }
}
