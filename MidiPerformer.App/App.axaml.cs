using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Logging;
using MidiPerformer.App.Startup;
using MidiPerformer.App.Theme;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.App;

public partial class App : Application
{
    /// <summary>
    /// 自绘取色桥。令牌在 <c>Styles/Tokens.axaml</c> 里，XAML 那边自己会找；
    /// 代码画的那一层（卷帘、悬浮层）从这里取。
    /// </summary>
    public TokenSource Tokens { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 要在任何资源查找之前 —— TokenSource 一建就取一份当前主题的令牌
        Tokens = new TokenSource(this);
    }

    /// <summary>
    /// 组装点。全程序唯一允许 new 具体实现的地方：取色桥（自绘层用）、墙上钟（试听与演奏
    /// 共用的时间原点）、出声的出口（winmm → GS 软波表）、演奏器还要的键鼠出口，全在这儿
    /// 建好，构造器注入下去。
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
                // dev-only：把悬浮层单独拉起来，验证置顶与不抢焦点。它没有边框、不进任务栏、
                // 还点击穿透，所以关不掉：验完从外面结束进程。
                var overlay = new PerformerOverlayWindow();
                overlay.Opened += (_, _) => overlay.ShowAllStatesForDemo();
                desktop.MainWindow = overlay;
            }
            else
            {
                var clock = new SystemClock();
                var sink = new WinmmPreview();
                var sender = new InputSender();

                // 日志：清旧的、挂上那个自写的文件 provider、写启动那一行。**只有这一处建它** ——
                // 下面每个窗口拿到的都是这同一个工厂开出来的 logger（62 号要的那个耗时也从这儿走）。
                // 整块不抛：日志起不来只是没日志，不该挡住程序启动。
                var logFactory = LoggingSetup.Start(DateTimeOffset.Now);

                // 曲库就住在 exe 旁边。「在哪儿」只写在这一行 —— SongLibrary 自己不猜自己在哪
                // （它收一个目录），所以测试塞得进临时目录。
                var library = new SongLibrary(Path.Combine(AppContext.BaseDirectory, "songs"));

                var window = new MainWindow(
                    Tokens, clock, sink, PerformerFactory(clock, sender), library,
                    logFactory.CreateLogger("MidiPerformer.App.Views.MainWindow"));
                desktop.MainWindow = window;

                // 启动时问一次提权：没提权就弹一颗「以管理员身份重启」，用户可以先不按。
                // 等窗口开出来再问 —— 那颗框是挂在这一窗上的模态框，没显示就跑 ShowDialog 无处可依。
                window.Opened += async (_, _) => await ElevationPrompt.AskAsync(window, desktop.Args);

                // 退出时收尾。**这里不许再补一次 sender.ReleaseAll()**（98 号票删的就是它）：
                // 那一发是无条件的 —— 不管自己按没按过，它都会发左/右/中三个鼠标**抬键**，
                // 而实测「只发一次右键抬起、没有配对的按下」照样会让资源管理器弹出桌面右键菜单
                // （菜单窗 #32768 从 0 变 1）。「以管理员身份重启」走的正是这一刻：旧进程退出时
                // 新进程刚起来、旧窗口已经没了、光标底下是桌面 —— 用户看到的就是「新实例一开就自己点了右键」。
                // 键盘上多发一个抬键没人管，鼠标上多发一个右键抬起就是一次真右键，
                // 所以「多松一次是幂等的」这句只对键盘成立。
                // 删掉它也**不留缺口**：窗口正常关掉时演奏器自己已经松过键（StopPerformance），
                // 真被强杀（taskkill / 崩溃）时这段根本不执行 —— 它本来就只是一层冗余的兜底。
                desktop.Exit += (_, _) =>
                {
                    sink.Dispose();
                    // 日志最后收一次。每行都是「开→追加→关」，这儿没有要冲的缓冲区；
                    // 收它是为了给以后真加了缓冲的时候留个明确的收尾点。
                    logFactory.Dispose();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 「演奏器…」的入口工厂：同一时刻只允许存在一个演奏器窗口 —— F6 急停靠的是低层键盘钩子，
    /// 两个窗口就是两个钩子，按一下两边同时响应。窗口被关掉之后才允许再开一个。
    ///
    /// 返回 <see cref="PerformerWindow"/> 而不是 <see cref="Window"/>：拿到的窗口是**复用**的，
    /// 主窗口每按一次「演奏」都要把此刻该弹的那份曲子经
    /// <see cref="PerformerWindow.LoadSong"/> 重新递一次（换了曲子再按，窗口里得换成新那首），
    /// 所以它得看得见那个方法在哪个类型上。
    /// </summary>
    private static Func<PerformerWindow> PerformerFactory(IClock clock, InputSender sender)
    {
        PerformerWindow? live = null;

        return () =>
        {
            if (live is not null) return live;

            var window = new PerformerWindow(clock, sender);
            live = window;
            window.Closed += (_, _) => live = null;
            return window;
        };
    }

    /// <summary>样板窗口是 dev-only 的，产品界面里不给入口，只能靠这个开关打开。</summary>
    private static bool Has(string[]? args, string flag)
        => args?.Contains(flag, StringComparer.Ordinal) == true;
}
