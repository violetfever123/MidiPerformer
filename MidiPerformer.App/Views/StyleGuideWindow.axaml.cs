using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// dev-only 样板窗口。令牌、圆角、字体、控件平铺出来，和 <c>docs/wireframe.html</c> 逐块对照。
///
/// 主题切换按钮是**唯一的**主题入口，产品界面里没有 —— 它在这儿是为了能当场看见
/// 「XAML 控件和自绘层同时变」这件事。
/// </summary>
public partial class StyleGuideWindow : Window
{
    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public StyleGuideWindow() : this(null!) { }

    public StyleGuideWindow(TokenSource tokens)
    {
        InitializeComponent();

        SwatchBoard.Tokens = tokens;
        RollStrip.Tokens = tokens;

        // 应用级的 RequestedThemeVariant 可能已经被别处定过，按实际值把按钮点亮
        Highlight(Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default);
    }

    private void OnThemeSystem(object? sender, RoutedEventArgs e) => Apply(ThemeVariant.Default);

    private void OnThemeLight(object? sender, RoutedEventArgs e) => Apply(ThemeVariant.Light);

    private void OnThemeDark(object? sender, RoutedEventArgs e) => Apply(ThemeVariant.Dark);

    private void Apply(ThemeVariant variant)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = variant;
        Highlight(variant);
    }

    /// <summary>
    /// 当前那个用「主」样式顶出来。
    /// 这里只改 Classes，颜色还是走 Controls.axaml —— 样板窗口自己也不许写死颜色。
    /// </summary>
    private void Highlight(ThemeVariant variant)
    {
        Mark(ThemeSystemButton, variant == ThemeVariant.Default);
        Mark(ThemeLightButton, variant == ThemeVariant.Light);
        Mark(ThemeDarkButton, variant == ThemeVariant.Dark);
    }

    private static void Mark(Button button, bool active) => button.Classes.Set("primary", active);
}
