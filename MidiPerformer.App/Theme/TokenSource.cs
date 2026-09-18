using Avalonia;
using Avalonia.Styling;

namespace MidiPerformer.App.Theme;

/// <summary>
/// 自绘取色桥。<b>这是最容易漏的一条。</b>
///
/// 卷帘和悬浮层不在 XAML 里，拿不到 <c>DynamicResource</c>，所以需要一个能从当前主题
/// 取令牌的入口。<see cref="Current"/> 是快照，主题一变就换一份新的并喊一声
/// <see cref="Changed"/>，订阅者重画即可 —— 于是自绘层和 XAML 控件**同一时刻**换过来。
///
/// 只订阅 <c>ActualThemeVariantChanged</c>，不订阅 <c>RequestedThemeVariant</c>：
/// 要和 XAML 控件对齐，就得看**实际生效**的那个，而不是我们请求的那个。
/// RequestedThemeVariant 是 Default 时实际值由系统定，跟着走的才是对的。
///
/// 生命周期跟着 <see cref="Application"/>，不解除订阅 —— 它和程序同生共死。
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
    /// Application 的 ActualThemeVariant 理论上不会是 Default，但真给了 Default，
    /// ThemeDictionaries 就查不到任何一套，只能炸。这里兜一手，退到明亮那套。
    /// </summary>
    private static ThemeVariant ResolveVariant(Application application)
        => application.ActualThemeVariant == ThemeVariant.Default
            ? ThemeVariant.Light
            : application.ActualThemeVariant;
}
