using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MidiPerformer.App.Startup;
using NUnit.Framework;

namespace MidiPerformer.Tests.Startup;

/// <summary>
/// 启动时那次提权 —— 该不该弹、用户拒绝之后怎么办、真去 <c>runas</c> 有没有把命令行带上。
/// 判据全在 <see cref="ElevationStartup.Decide"/> 那张纯函数表里，所以整块脱开进程与 UAC 测；
/// 真碰 Win32 的那两半（<c>CheckElevation</c>、<c>Process.Start</c>）不在这儿测，只钉住它们的用法。
/// </summary>
public class ElevationTests
{
    /// <summary>判定函数的速记，让每条断言只剩要验的那几个字。</summary>
    private static ElevationDecision 判(bool 已提权, bool 自检, ElevationChoice 选择, ElevationUac uac)
        => ElevationStartup.Decide(已提权, 自检, 选择, uac);

    private static readonly string[][] 参数组 =
    {
        new[] { "--style-guide" },
        new[] { "--overlay-demo" },
        new[] { "--style-guide", "--overlay-demo" },
        Array.Empty<string>(),
    };

    // ==================== A. 判据（纯函数）====================

    [Test]
    public void 已提权就不弹()
    {
        var d = 判(已提权: true, 自检: false, ElevationChoice.Unasked, ElevationUac.NotAttempted);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), "已经是管理员了还弹一颗要提权的框");
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.AlreadyElevated));
        });
    }

    [Test]
    public void 未提权又没问过就弹一颗()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.Unasked, ElevationUac.NotAttempted);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Ask));
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.AwaitingUser));
        });
    }

    /// <summary>
    /// 这一票最重要的一条：拒绝 = 以普通权限继续，<b>不是</b>退出，<b>不是</b>再弹一次。
    /// 编辑谱面本来就用不着管理员，拒绝之后程序必须和加这个功能之前一模一样。
    /// </summary>
    [Test]
    public void 未提权_用户拒绝_继续以普通权限进()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.KeepCurrent, ElevationUac.NotAttempted);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), "用户选了「先不用」却走了别的路 —— 不是退出、也不是再弹一次");
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.UserDeclined));
            Assert.That(d.Route, Is.Not.EqualTo(ElevationRoute.Ask), "拒绝之后不能再弹第二次");
            Assert.That(d.Route, Is.Not.EqualTo(ElevationRoute.ExitForRestart), "拒绝之后不能把进程带走");
        });
    }

    [Test]
    public void 未提权_点了重启_那就去重启()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.Restart, ElevationUac.NotAttempted);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.TryRestart));
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.UacPending));
        });
    }

    [Test]
    public void 未提权_点了重启且UAC通过_新进程接手原进程收工()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.Restart, ElevationUac.Approved);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.ExitForRestart));
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.UacApproved));
        });
    }

    /// <summary>用户在 UAC 那个安全桌面框上按了「否」——和「在框上选了先不用」同一结局。</summary>
    [Test]
    public void 未提权_UAC被拒_继续以普通权限进()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.Restart, ElevationUac.Cancelled);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), "UAC 被拒之后程序必须照常能用");
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.UacCancelled));
            Assert.That(d.Route, Is.Not.EqualTo(ElevationRoute.Ask), "UAC 被拒之后不能再弹第二次");
        });
    }

    /// <summary>
    /// UAC 抛的是别的异常（策略拦了、路径不对……）：行为必须<b>明确</b>——照常以普通权限进，
    /// 只是原因记得和「被拒」分开，日志里才分得清是用户不要还是根本没起来。
    /// </summary>
    [Test]
    public void 未提权_UAC因为别的原因没起来_也继续以普通权限进()
    {
        var d = 判(已提权: false, 自检: false, ElevationChoice.Restart, ElevationUac.Failed);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), "重启没起来就把程序拦在门外了");
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.UacFailed));
            Assert.That(d.Reason, Is.Not.EqualTo(ElevationReason.UacCancelled), "两种失败得能分开认");
        });
    }

    /// <summary>只有 <c>ERROR_CANCELLED</c> 是「用户按了否」，别的错误码都是「没起来」。</summary>
    [Test]
    public void 只有1223算被拒()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ElevationStartup.ErrorCancelled, Is.EqualTo(1223));
            Assert.That(ElevationStartup.ClassifyRestartFailure(1223), Is.EqualTo(ElevationUac.Cancelled));
            Assert.That(ElevationStartup.ClassifyRestartFailure(5), Is.EqualTo(ElevationUac.Failed), "拒绝访问不是「用户按了否」");
            Assert.That(ElevationStartup.ClassifyRestartFailure(2), Is.EqualTo(ElevationUac.Failed), "找不到文件不是「用户按了否」");
            Assert.That(ElevationStartup.ClassifyRestartFailure(0), Is.EqualTo(ElevationUac.Failed));
        });
    }

    /// <summary>
    /// 穷举四路输入的所有组合，把两条安全性质钉死（单看某一条断言是验不出「未定义」的）：
    /// ① <b>只有</b>「点了重启 且 UAC 通过」才收工换进程 —— 别的一律照常进；
    /// ② 已提权 / 自检时不弹也不重启。
    /// </summary>
    [Test]
    public void 穷举所有组合_没有哪一组会把用户拦在门外()
    {
        Assert.Multiple(() =>
        {
            foreach (bool 已提权 in new[] { true, false })
            {
                foreach (bool 自检 in new[] { true, false })
                {
                    foreach (var 选择 in Enum.GetValues<ElevationChoice>())
                    {
                        foreach (var uac in Enum.GetValues<ElevationUac>())
                        {
                            string 这一组 = $"已提权={已提权} 自检={自检} 选择={选择} UAC={uac}";
                            var d = 判(已提权, 自检, 选择, uac);

                            Assert.That(Enum.IsDefined(d.Route), Is.True, $"{这一组} 没有明确的路可走");
                            Assert.That(Enum.IsDefined(d.Reason), Is.True, $"{这一组} 没有明确的原因");

                            if (d.Route == ElevationRoute.ExitForRestart)
                            {
                                Assert.That(选择 == ElevationChoice.Restart && uac == ElevationUac.Approved, Is.True,
                                    $"{这一组} 把原进程带走了 —— 只有「点了重启且 UAC 通过」才允许");
                            }

                            if (选择 != ElevationChoice.Restart)
                            {
                                Assert.That(d.Route, Is.AnyOf(ElevationRoute.Ask, ElevationRoute.Proceed),
                                    $"{这一组} 用户没点那颗按钮，却去 runas 了");
                            }

                            if (已提权 || 自检)
                            {
                                Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), $"{这一组} 不该弹也不该重启");
                            }
                        }
                    }
                }
            }
        });
    }

    [Test]
    public void 每条路都说得清为什么()
    {
        Assert.Multiple(() =>
        {
            Assert.That(判(true, false, ElevationChoice.Unasked, ElevationUac.NotAttempted),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Proceed, ElevationReason.AlreadyElevated)));
            Assert.That(判(false, true, ElevationChoice.Unasked, ElevationUac.NotAttempted),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Proceed, ElevationReason.SelfTest)));
            Assert.That(判(false, false, ElevationChoice.Unasked, ElevationUac.NotAttempted),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Ask, ElevationReason.AwaitingUser)));
            Assert.That(判(false, false, ElevationChoice.KeepCurrent, ElevationUac.NotAttempted),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Proceed, ElevationReason.UserDeclined)));
            Assert.That(判(false, false, ElevationChoice.Restart, ElevationUac.NotAttempted),
                Is.EqualTo(new ElevationDecision(ElevationRoute.TryRestart, ElevationReason.UacPending)));
            Assert.That(判(false, false, ElevationChoice.Restart, ElevationUac.Approved),
                Is.EqualTo(new ElevationDecision(ElevationRoute.ExitForRestart, ElevationReason.UacApproved)));
            Assert.That(判(false, false, ElevationChoice.Restart, ElevationUac.Cancelled),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Proceed, ElevationReason.UacCancelled)));
            Assert.That(判(false, false, ElevationChoice.Restart, ElevationUac.Failed),
                Is.EqualTo(new ElevationDecision(ElevationRoute.Proceed, ElevationReason.UacFailed)));
        });
    }

    /// <summary>弹框那三句是实机验收（D 组）逐字要看的东西，钉在这儿免得改没了不知道。</summary>
    [Test]
    public void 弹框正文说清了为什么值得重启()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ElevationStartup.PromptRestartText, Is.EqualTo("以管理员身份重启"), "那颗按钮上写的得是个动词");
            Assert.That(ElevationStartup.PromptBody, Does.Contain("往游戏里发按键"), "正文得说清这个程序是干什么的");
            Assert.That(ElevationStartup.PromptBody, Does.Contain("普通权限"));
            Assert.That(ElevationStartup.PromptBody, Does.Contain("挡"), "正文得说清不提权会怎样");
            Assert.That(ElevationStartup.PromptBody, Does.Contain("不重启也行"),
                "正文得让用户知道不重启不是走投无路 —— 拒绝之后他只是不能弹");
        });
    }

    // ==================== B. 命令行与 exe 路径 ====================

    [Test]
    public void 重启用的命令行原样带上参数()
    {
        const string exe = @"C:\某个目录\MidiPerformer.exe";

        Assert.Multiple(() =>
        {
            foreach (string[] args in 参数组)
            {
                var info = ElevationRestart.BuildStartInfo(exe, args);
                string 这一组 = args.Length == 0 ? "（空）" : string.Join(' ', args);

                Assert.That(info.ArgumentList, Is.EqualTo(args), $"{这一组} 没被原样带过去");
                Assert.That(info.FileName, Is.EqualTo(exe), $"{这一组}：重启得是同一个 exe");
                Assert.That(info.Verb, Is.EqualTo("runas"), $"{这一组}：提权靠的就是这个词");
                Assert.That(info.UseShellExecute, Is.True,
                    $"{这一组}：runas 是 ShellExecute 的词，不开它递不出提权请求");
            }

            // 组装点给的是 desktop.Args，它可能是 null
            Assert.That(ElevationRestart.BuildStartInfo(exe, null).ArgumentList, Is.Empty,
                "压根没有命令行时不该凭空造出参数");
        });
    }

    /// <summary>
    /// 拿 exe 路径只能用 <see cref="Environment.ProcessPath"/>。<c>Assembly.Location</c> 给的是
    /// App 程序集那个 .dll 的位置，而单文件裁剪下干脆是空串 —— 空串喂给 <c>ProcessStartInfo.FileName</c>
    /// 不报错也不启动，是个在开发机上永远看不见的静默失败。
    /// </summary>
    [Test]
    public void 重启拿的是进程可执行文件路径_不是程序集位置()
    {
        string 路径 = ElevationRestart.CurrentExecutablePath;
        string 程序集位置 = typeof(ElevationRestart).Assembly.Location;

        Assert.Multiple(() =>
        {
            Assert.That(路径, Is.Not.Empty, "空串喂给 ProcessStartInfo.FileName 是静默失败");
            Assert.That(路径, Is.EqualTo(Environment.ProcessPath), "这条路必须是 Environment.ProcessPath");
            Assert.That(路径, Is.Not.EqualTo(程序集位置),
                "拿到的是 App 程序集的 .dll 位置 —— 那正是单文件下会变成空串的那个值");
            Assert.That(路径, Does.EndWith(".exe").IgnoreCase, "要重启的是一个可执行文件，不是一个 .dll");
        });
    }

    /// <summary>
    /// 上面那条断言在开发机上只能证明「现在是对的」；这条把源码本身钉住，
    /// 免得以后有人顺手把 <c>Environment.ProcessPath</c> 换成 <c>Assembly.Location</c>。
    /// </summary>
    [Test]
    public void 提权这条路上不许出现Assembly_Location()
    {
        var 文件 = Directory.GetFiles(AppStartupDir, "*.cs");
        Assert.That(文件, Is.Not.Empty, $"{AppStartupDir} 里一个 .cs 都没有 —— 目录被搬了，这条守卫得跟着挪");

        Assert.Multiple(() =>
        {
            foreach (string f in 文件)
            {
                Assert.That(去注释(File.ReadAllText(f)).Contains("Assembly.Location"), Is.False,
                    $"{Path.GetFileName(f)} 的代码里用了 Assembly.Location —— 单文件裁剪下它是空串");
            }

            Assert.That(去注释(File.ReadAllText(Path.Combine(AppStartupDir, "ElevationRestart.cs"))),
                Does.Contain("Environment.ProcessPath"), "取 exe 路径的那个属性不见了");
        });
    }

    // ==================== C. 自检不弹 ====================

    /// <summary>
    /// 自检是自动化在跑，弹一颗 UAC 会把脚本挂死。两半都要钉：判据（自检时压根不问），
    /// 以及接线（流程真去读那个开关；而 <c>Program.Main</c> 在自检那条路上压根不建窗口）。
    /// </summary>
    [Test]
    public void 自检模式走不到弹窗那一步()
    {
        var d = 判(已提权: false, 自检: true, ElevationChoice.Unasked, ElevationUac.NotAttempted);
        string 流程 = 去注释(File.ReadAllText(Path.Combine(AppStartupDir, "ElevationPrompt.cs")));
        string main = 去注释(File.ReadAllText(Path.Combine(RepoRoot, "MidiPerformer.App", "Program.cs")));

        int 自检退出 = main.IndexOf("Environment.Exit(PerformerSelfTest.Run())", StringComparison.Ordinal);
        int 起窗口 = main.IndexOf("StartWithClassicDesktopLifetime", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(d.Route, Is.EqualTo(ElevationRoute.Proceed), "自检模式下还去弹一颗 UAC");
            Assert.That(d.Reason, Is.EqualTo(ElevationReason.SelfTest));
            Assert.That(流程, Does.Contain("PerformerSelfTest.Requested"), "自检那个开关没接到提权这条路上");
            Assert.That(自检退出, Is.GreaterThanOrEqualTo(0), "Program.cs 里没找到自检那条退出");
            Assert.That(起窗口, Is.GreaterThan(自检退出), "自检得在建窗口之前退出，否则一样会走到弹窗那一步");
        });
    }

    // ==================== 源码位置 ====================

    /// <summary>
    /// 把 <c>//</c> 之后切掉。下面两条守卫看的是<b>代码</b>，不是注释里的散文：
    /// <c>ElevationRestart</c> 的注释正要点名 <c>Assembly.Location</c> 才说得清那个坑，
    /// <c>Program.cs</c> 的注释也正要点名 <c>StartWithClassicDesktopLifetime</c> 才说得清
    /// 自检为什么必须抢在它前面 —— 拿整份源码去 Contains，红的会是注释，不是代码。
    /// </summary>
    private static string 去注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string AppStartupDir => Path.Combine(RepoRoot, "MidiPerformer.App", "Startup");
}
