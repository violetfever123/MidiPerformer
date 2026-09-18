using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Theme;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Ports.Outbound;

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
    /// 窗口一要三样东西：取色桥（自绘层用）、墙上钟（试听与演奏共用的时间原点）、
    /// 出声的出口（winmm → GS 软波表）。演奏器还要键鼠出口。全在这儿建好，构造器注入下去 ——
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
            else if (Has(desktop.Args, "--overlay-demo"))
            {
                // dev-only：把悬浮层单独拉起来。工单里「已在一个普通窗口上验证悬浮层置顶与不抢焦点」
                // 这一条没法在测试里验 —— 它需要一个真窗口、一个真前台窗口、一次真点击。
                // 照 --style-guide 的先例留一个开关：起来之后前台窗口还是不是原来那个，一比就知道。
                // 它没有边框、不进任务栏、还点击穿透，所以**关不掉**：验完从外面结束进程。
                var overlay = new PerformerOverlayWindow();
                overlay.Opened += (_, _) => overlay.ShowAllStatesForDemo();
                desktop.MainWindow = overlay;
            }
            else
            {
                var clock = new SystemClock();
                var sink = new WinmmPreview();
                var sender = new InputSender();

                desktop.MainWindow = new MainWindow(Tokens, clock, sink, PerformerFactory(clock, sender));

                // 退出时收尾。窗口自己会松开按着的音，但那要窗口正常关掉才算数 ——
                // 进程被别处带走时，还按着的键就留在游戏里了。多松一次是幂等的，代价为零。
                desktop.Exit += (_, _) =>
                {
                    sender.ReleaseAll();
                    sink.Dispose();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 「演奏器…」的入口工厂：<b>同一个时刻只允许存在一个演奏器窗口</b>。
    ///
    /// 这不是省内存，是安全：F6 急停靠的是低层键盘钩子，两个窗口就是两个钩子，
    /// 按一下两边同时响应，各自去松各自的键 —— 而它们管的是同一块键盘。
    /// 所以窗口复用；窗口被关掉（用户关的，或它自己的 owner 关的）之后才允许再开一个。
    /// </summary>
    private static Func<Window> PerformerFactory(IClock clock, InputSender sender)
    {
        Window? live = null;

        return () =>
        {
            if (live is not null) return live;

            var window = new PerformerWindow(clock, sender);
            live = window;
            window.Closed += (_, _) => live = null;
            return window;
        };
    }

    /// <summary>
    /// 样板窗口是 dev-only 的，产品界面里不给入口 —— 只能靠这个开关打开。
    /// </summary>
    private static bool Has(string[]? args, string flag)
        => args?.Contains(flag, StringComparer.Ordinal) == true;
}
