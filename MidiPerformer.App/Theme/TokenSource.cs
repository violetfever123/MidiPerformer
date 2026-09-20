using Avalonia;
using Avalonia.Styling;

namespace MidiPerformer.App.Theme;

/// <summary>
/// 自绘取色桥。卷帘和悬浮层不在 XAML 里，拿不到 <c>DynamicResource</c>，得从这里取当前主题
/// 的令牌。<see cref="Current"/> 是快照，主题一变就换一份新的并喊一声 <see cref="Changed"/>，
/// 订阅者重画即可。
///
/// 只订阅 <c>ActualThemeVariantChanged</c>（实际生效的那个），不订阅
/// <c>RequestedThemeVariant</c> —— 那个是 Default 时实际值由系统定。
/// </summary>
public sealed class TokenSource
{
    private readonly Application _application;

    public TokenSource(Application application)
    {
        _application = application;
        Current = TokenPalette.Resolve(application, ResolveVariant(application));
        application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    /// <summary>当前主题下的令牌快照。主题切换后换成新的一份。</summary>
    public TokenPalette Current { get; private set; }

    /// <summary>主题变了。自绘层订阅它 <c>InvalidateVisual()</c>。</summary>
    public event EventHandler? Changed;

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        Current = TokenPalette.Resolve(_application, ResolveVariant(_application));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// ActualThemeVariant 真给了 Default 的话，ThemeDictionaries 一套都查不到，只能炸；
    /// 这里兜一手，退到明亮那套。
    /// </summary>
    private static ThemeVariant ResolveVariant(Application application)
        => application.ActualThemeVariant == ThemeVariant.Default
            ? ThemeVariant.Light
            : application.ActualThemeVariant;
}
