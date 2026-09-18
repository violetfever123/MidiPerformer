using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;
using MidiPerformer.App.Views;

namespace MidiPerformer.App;

public partial class App : Application
{
    /// <summary>
    /// 自绘取色桥。令牌在 <c>Styles/Tokens.axaml</c> 里，XAML 那边自己会找；
    /// 代码画的那一层（卷帘、悬浮层）从这里取 —— 组装点建好它，一路构造器注入往下传。
    /// </summary>
    public TokenSource Tokens { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 要在任何资源查找之前 —— TokenSource 一建就取一份当前主题的令牌
        Tokens = new TokenSource(this);
    }

    /// <summary>
    /// 组装点。全程序唯一允许 new 具体实现的地方。
    ///
    /// 窗口一要三样东西：取色桥（自绘层用）、墙上钟（试听的时间积分）、
    /// 出声的出口（winmm → GS 软波表）。三样都在这儿建好，构造器注入下去 ——
    /// 别处谁也不 new 它们，不然「换一个实现」就得改一片。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Has(desktop.Args, "--style-guide"))
            {
                desktop.MainWindow = new StyleGuideWindow(Tokens);
            }
            else
            {
                var sink = new WinmmPreview();
                desktop.MainWindow = new MainWindow(Tokens, new SystemClock(), sink);

                // 退出时关掉 MIDI 设备。窗口只负责松开按着的音，设备的开关是组装点的活
                desktop.Exit += (_, _) => sink.Dispose();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 样板窗口是 dev-only 的，产品界面里不给入口 —— 只能靠这个开关打开。
    /// </summary>
    private static bool Has(string[]? args, string flag)
        => args?.Contains(flag, StringComparer.Ordinal) == true;
}
