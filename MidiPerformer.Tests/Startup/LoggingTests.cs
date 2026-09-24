using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MidiPerformer.App.Logging;
using NUnit.Framework;

namespace MidiPerformer.Tests.Startup;

/// <summary>
/// 61 号票：日志。分四块 —— **A 红线**（最重要的那块）、B 轮转、C 路径、D 不引第三方、E 菜单。
///
/// <b>判据为什么长这样：</b>日志这一块「坏掉」有很多种坏法，而它们的严重性差着量级。
/// 少写一条日志 = 出事之后少一条线索，用户顶多没得查；**多写一类东西是另一种性质的事** ——
/// 这个程序是往游戏里发按键的，按键 + 窗口标题 + 进程名凑起来就是一份
/// 「用户在玩什么、什么时候玩、手速多快」的记录。所以 A 那一块比「日志写成功了」重得多，
/// 参 <see cref="红线_正常日志落盘之后不含按键进程名窗口标题"/>。
///
/// 起 Avalonia 吗？不起。这一块全是「文件里/盘上是什么样」，跟像素与焦点无关 ——
/// 「点了它资源管理器真开了」是上机那一步（<c>tools/uitest/</c>），这儿不冒充。
/// </summary>
public class LoggingTests
{
    private string _目录 = "";

    /// <summary>固定不动的「现在」。轮转那几条要拿它算年龄，取真的当下会让边界断言飘。</summary>
    private static readonly DateTimeOffset 现在 = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void 造一个临时目录()
        => _目录 = Directory.CreateTempSubdirectory("mp61-").FullName;

    [TearDown]
    public void 收掉临时目录()
    {
        try { Directory.Delete(_目录, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ==================== A. 🔴 红线 ====================

    /// <summary>
    /// <b>这一票最重要的一条。</b> 给 logger 灌一批「正常」日志（就是产品真会写的那几类：
    /// 启动 / 版本号 / 耗时 / 出事了 + 异常），落盘，然后断言那个文件里
    /// **不含按键、进程名、窗口标题这三类东西**。
    ///
    /// 先钉住「有用的东西真的在里面」，再钉「不许有的东西不在」——
    /// 否则一个永远写空文件的实现也能让「不含」全绿，那这几条断言等于没写。
    ///
    /// 顺带钉住一个**允许**：异常里的文件路径（路径里带歌名）是票面明说可以接受的，
    /// 所以这条反过来拦住「以后谁顺手把它一起清了」—— 清了就没线索了。
    /// </summary>
    [Test]
    public void 红线_正常日志落盘之后不含按键进程名窗口标题()
    {
        string 本进程名 = Process.GetCurrentProcess().ProcessName;
        string 本进程路径 = Environment.ProcessPath ?? "";

        var provider = new FileLoggerProvider(_目录, () => 现在);
        var log = provider.CreateLogger("MidiPerformer.App.Views.MainWindow");

        // 「正常」的一批：产品里真会写的那几类
        log.LogInformation("启动 MidiPerformer {版本}", LoggingSetup.Version);
        log.LogInformation("打开曲子耗时 {毫秒} 毫秒", 137);
        log.LogWarning("读不出这个文件：{路径}", @"C:\曲库\（三角洲适配）勾指起誓.mid");
        log.LogError(带栈的异常(), "打开失败了");

        string? 落下的文件 = provider.CurrentPath;
        Assert.That(落下的文件, Is.Not.Null, "一行都没落盘 —— 下面那些「不含」就全是空过的");
        string 落盘 = File.ReadAllText(落下的文件!);

        Assert.Multiple(() =>
        {
            // ① 先证明这一批真的写进去了，而且是有用的东西
            Assert.That(落盘, Does.Contain(LoggingSetup.Version), "版本号没落盘（上机那条要看的就有它）");
            Assert.That(落盘, Does.Contain("137 毫秒"), "耗时没落盘（62 号要写的就是这一类）");
            Assert.That(落盘, Does.Contain("System.InvalidOperationException"), "异常没落盘");
            Assert.That(落盘, Does.Contain("勾指起誓"),
                "路径里的歌名是票面明说**可以接受**的 —— 这条反过来钉住「不许顺手把它一起清了」");

            // ② 🔴 三类不许出现
            Assert.That(本进程名, Is.Not.Empty);
            Assert.That(落盘, Does.Not.Contain(本进程名), $"进程名进了日志：{本进程名}");
            if (本进程路径.Length > 0)
                Assert.That(落盘, Does.Not.Contain(本进程路径), "进程的完整路径进了日志");

            foreach (string 词 in 按键类)
                Assert.That(落盘, Does.Not.Contain(词), $"按键这一类的东西进了日志：{词}");

            foreach (string 词 in 进程类)
                Assert.That(落盘, Does.Not.Contain(词), $"进程这一类的东西进了日志：{词}");

            foreach (string 词 in 窗口标题类)
                Assert.That(落盘, Does.Not.Contain(词), $"窗口标题这一类的东西进了日志：{词}");
        });
    }

    /// <summary>
    /// 上一条只能拦住「已知的那几个词」。这一条拦的是**结构**：
    /// <b>日志文件里每一行，要么是「时间戳 [级别] 类别: 正文」这个头，要么是缩进四格的异常栈续行。</b>
    ///
    /// 行格式里只有四个字段位 —— 时间、级别、类别、正文。于是「顺手带上进程号 / 线程号 / 机器名 /
    /// 用户名」这类**环境事实**根本没有地方落笔：加一个字段这行就不匹配了。
    /// 这是红线唯一一条不靠黑名单的钉法（黑名单永远漏），所以它得单独在。
    /// </summary>
    [Test]
    public void 红线_每一行只有时间级别类别正文四个字段位()
    {
        var provider = new FileLoggerProvider(_目录, () => 现在);
        var log = provider.CreateLogger("MidiPerformer.App.Views.MainWindow");

        log.LogInformation("启动 MidiPerformer {版本}", LoggingSetup.Version);
        log.LogError(带栈的异常(), "打开失败了");

        string[] 行 = File.ReadAllLines(provider.CurrentPath!);

        int 头行数 = 0;
        Assert.Multiple(() =>
        {
            foreach (string 行 in 行)
            {
                if (行.Length == 0) continue;          // 文件末尾那个换行
                bool 是头 = 日志头().IsMatch(行);
                bool 是异常栈 = 行.StartsWith("    ", StringComparison.Ordinal);
                if (是头) 头行数++;

                Assert.That(是头 || 是异常栈, Is.True,
                    $"这一行既不是日志头、也不是缩进四格的异常栈续行 —— 行里多半多了个字段位：{行}");
            }

            Assert.That(头行数, Is.EqualTo(2), "两条日志就该正好两个头行");
            Assert.That(行.Any(l => l.StartsWith("    ", StringComparison.Ordinal)), Is.True,
                "异常栈一条续行都没有 —— 那上面那条断言其实没被异常那一路走到");
        });
    }

    /// <summary>
    /// 上两条跑的都是**产品现在写的那几句**，拦不住「以后有人加一句新的、把按键塞进去」。
    /// 这一条就拦那个：把产品源码里**每一处写日志的调用**整条语句抠出来，
    /// 断言里面不含按键 / 进程 / 窗口标题这三类来源。
    ///
    /// 抠的是「语句」不是「行」：起手写了一半、下一行才把参数写完的也算进去，不然漏得掉。
    /// 和 <c>ElevationTests</c> 那条「提权这条路上不许出现 Assembly.Location」是同一个套路 ——
    /// 行为测不到的地方，用源码把它钉住。
    /// </summary>
    [Test]
    public void 红线_产品里每一处写日志的语句都不含那三类来源()
    {
        var 语句们 = 写日志的语句().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(语句们, Is.Not.Empty,
                "一处写日志的地方都没找到 —— 这条守卫的抠法跟源码对不上了，它现在是空过的");

            foreach (var (文件, 行号, 语句) in 语句们)
            {
                string 哪儿 = $"{Path.GetFileName(文件)}:{行号}";
                foreach (string 词 in 按键类.Concat(进程类).Concat(窗口标题类))
                    Assert.That(语句, Does.Not.Contain(词), $"{哪儿} 这处日志里出现了「{词}」：{语句.Trim()}");
            }
        });
    }

    /// <summary>
    /// 上一类拦的是「喂进去的值」，这一条拦的是**日志那一层自己去问**：
    /// <c>App\Logging\</c> 下面一个文件都不许碰进程名 / 机器名 / 用户名 / 前台窗口。
    ///
    /// 为什么要单独一条：行格式那个四条断言拦得住「加个字段」，拦不住「有人把机器名拼进正文里」——
    /// 那种改法行格式一点没变。这一条堵的就是那个口子。
    /// </summary>
    [Test]
    public void 红线_日志那一层自己不去问环境事实()
    {
        var 文件 = Directory.GetFiles(日志层目录, "*.cs", SearchOption.AllDirectories);
        Assert.That(文件, Is.Not.Empty, $"{日志层目录} 里一个 .cs 都没有 —— 目录被搬了，这条守卫得跟着挪");

        Assert.Multiple(() =>
        {
            foreach (string f in 文件)
            {
                string 码 = 去注释(File.ReadAllText(f));
                foreach (string 词 in 环境事实类)
                    Assert.That(码, Does.Not.Contain(词),
                        $"{Path.GetFileName(f)} 里出现了「{词}」—— 日志那一层不许去问环境事实");
            }
        });
    }

    // ==================== B. 轮转 ====================

    /// <summary>
    /// 25 份、其中 3 份是 8 天前 → 跑清理 → <b>剩不超过 20 份，且没有一份超过 7 天</b>。
    /// 两条规矩都要真的出力：只按年龄清会剩 22 份，只按份数清会留下那 3 份 8 天前的。
    /// </summary>
    [Test]
    public void 轮转_二十五份里八天前的那几份要走_总数也要压到二十()
    {
        for (int i = 0; i < 3; i++) 写一份($"MidiPerformer-20260916-{i:00}.log", 现在.AddDays(-8));
        for (int i = 0; i < 22; i++) 写一份($"MidiPerformer-20260923-{i:00}.log", 现在.AddHours(-1));

        var 删了 = LogFolder.Prune(_目录, 现在);

        var 剩下 = 自己那批();
        Assert.Multiple(() =>
        {
            Assert.That(剩下.Length, Is.LessThanOrEqualTo(LogFolder.MaxFiles), "份数没压到 20 以内");
            Assert.That(剩下.Length, Is.EqualTo(20), "22 份新的 + 3 份老的清完，正好该剩 20");
            Assert.That(删了, Has.Count.EqualTo(5), "8 天前的 3 份 + 多出来的 2 份，一共该删 5 份");

            foreach (string f in 剩下)
                Assert.That(现在.UtcDateTime - File.GetLastWriteTimeUtc(f), Is.LessThanOrEqualTo(TimeSpan.FromDays(7)),
                    $"{Path.GetFileName(f)} 超过 7 天了还留着");
        });
    }

    /// <summary>
    /// <b>边界：正好 7 天。</b> 「留 7 天」是下限式的承诺 —— 正好第 7 天还在「留 7 天」里，留着。
    /// 多删一份用户就少一份线索，而少一份是看不出来的。反侧同一趟量掉：多一秒就该走。
    /// </summary>
    [Test]
    public void 轮转_正好七天留着_多一秒就走()
    {
        string 正好七天 = 写一份("MidiPerformer-20260917-150000.log", 现在.AddDays(-7));
        string 多一秒 = 写一份("MidiPerformer-20260917-145959.log", 现在.AddDays(-7).AddSeconds(-1));

        LogFolder.Prune(_目录, 现在);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(正好七天), Is.True, "正好 7 天的被删了 —— 「留 7 天」不该在第 7 天就动手");
            Assert.That(File.Exists(多一秒), Is.False, "超过 7 秒那一侧的没删掉 —— 那这条边界只测了一半");
        });
    }

    /// <summary>
    /// <b>边界：正好 20 份。</b> 「最多 20 份」同样在下限那一侧 —— 正好 20 份一份都不许删。
    /// 和多一份那侧同趟量掉。
    /// </summary>
    [Test]
    public void 轮转_正好二十份一份都不删_二十一份才删()
    {
        for (int i = 0; i < 20; i++) 写一份($"MidiPerformer-20260924-{i:00}0000.log", 现在.AddMinutes(-i));

        var 删了 = LogFolder.Prune(_目录, 现在);
        Assert.Multiple(() =>
        {
            Assert.That(删了, Is.Empty, "正好 20 份却动了手 —— 「最多 20 份」不该在第 20 份就删");
            Assert.That(自己那批().Length, Is.EqualTo(20));
        });

        // 第 21 份进来，走的该是最旧的那一份
        string 最新一支 = 写一份("MidiPerformer-20260924-235900.log", 现在);
        var 又删了 = LogFolder.Prune(_目录, 现在);

        Assert.Multiple(() =>
        {
            Assert.That(又删了, Has.Count.EqualTo(1), "21 份了，就该只走 1 份");
            Assert.That(File.Exists(最新一支), Is.True, "刚写的那份被删了 —— 删的方向反了");
            Assert.That(自己那批().Length, Is.EqualTo(20));
        });
    }

    /// <summary>
    /// 清理只动**自己写的**那些文件。日志目录是用户的 <c>%LOCALAPPDATA%</c>，
    /// 里面放别的东西（别的版本、用户自己丢进来的）不是我们该删的。
    /// </summary>
    [Test]
    public void 轮转_别人的文件一个都不碰()
    {
        string 别人的 = Path.Combine(_目录, "别的程序的东西.log");
        File.WriteAllText(别人的, "x");
        File.SetLastWriteTimeUtc(别人的, 现在.UtcDateTime.AddDays(-400));
        string 别的后缀 = 写一份("MidiPerformer-旧.txt", 现在.AddDays(-400));

        LogFolder.Prune(_目录, 现在);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(别人的), Is.True, "把同一个目录里别的程序的日志删了");
            Assert.That(File.Exists(别的后缀), Is.True, "只认 .log 结尾的那批，别的后缀不碰");
        });
    }

    // ==================== C. 路径 ====================

    /// <summary>
    /// 落在 <c>%LOCALAPPDATA%\MidiPerformer\logs\</c>。
    /// 并且钉住「**全程序只有这一处拼这个路径**」—— 票面原话是「这条是新引入的依赖，别在别处再猜路径」。
    /// 猜的人一多，两处迟早对不上，而对不上是看不出来的（日志静默落去了另一个文件夹）。
    /// </summary>
    [Test]
    public void 路径_落在LOCALAPPDATA下_而且只在这一处拼()
    {
        string 本机 = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Multiple(() =>
        {
            Assert.That(本机, Is.Not.Empty);
            Assert.That(LogFolder.DefaultDirectory, Is.EqualTo(Path.Combine(本机, "MidiPerformer", "logs")),
                "日志目录不是 %LOCALAPPDATA%\\MidiPerformer\\logs\\");
            Assert.That(Path.IsPathRooted(LogFolder.DefaultDirectory), Is.True,
                "拼出来的是相对路径 —— 那日志会跟着当前工作目录跑");

            var 拼过的 = 产品源码文件()
                .Where(f => 去注释(File.ReadAllText(f)).Contains("SpecialFolder.LocalApplicationData"))
                .Select(f => Path.GetFileName(f)!)
                .ToList();

            Assert.That(拼过的, Is.EqualTo(new[] { "LogFolder.cs" }),
                "除了 Logging\\LogFolder.cs，别处也去拼 %LOCALAPPDATA% 了 —— 路径有两个出处迟早会对不上");
        });
    }

    /// <summary>
    /// <b>目录不存在时自建，不抛。</b> 嵌套好几层都没有过（第一次跑就是这个样子）。
    /// 顺带把「目录不在时清理也不抛」量掉 —— 清理跑在启动路径上，抛一次就是程序起不来。
    /// </summary>
    [Test]
    public void 路径_目录不存在时自建且不抛()
    {
        string 深的 = Path.Combine(_目录, "一级", "二级", "logs");
        Assert.That(Directory.Exists(深的), Is.False, "前提：这个目录本来不该在");

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => LogFolder.EnsureExists(深的));
            Assert.That(Directory.Exists(深的), Is.True, "自建没建出来");
            Assert.That(LogFolder.EnsureExists(深的), Is.EqualTo(深的), "已经在的目录也要原样返回");

            Assert.DoesNotThrow(() => LogFolder.Prune(Path.Combine(_目录, "压根没有这个目录"), 现在),
                "目录不在时清理抛了 —— 这一抛就是程序起不来");
            Assert.That(LogFolder.Prune(Path.Combine(_目录, "压根没有这个目录"), 现在), Is.Empty);
        });
    }

    /// <summary>
    /// 写日志这件事**绝不许把程序带崩**：目录是个文件、盘写不进去、路径非法 —— 一律咽掉。
    /// 「写失败只是没日志」，不能是「写失败程序就没了」。
    /// </summary>
    [Test]
    public void 写不进去也不抛()
    {
        // 拿一个「文件」当目录用 —— CreateDirectory 必然失败
        string 当目录用的文件 = Path.Combine(_目录, "我是个文件");
        File.WriteAllText(当目录用的文件, "x");

        var provider = new FileLoggerProvider(当目录用的文件, () => 现在);
        var log = provider.CreateLogger("MidiPerformer.App");

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => log.LogInformation("写不进去的一句"));
            Assert.That(provider.CurrentPath, Is.Null, "一个字节都没该落下去");
        });
    }

    // ==================== D. 不引第三方 ====================

    /// <summary>
    /// <b>csproj 里不许有 Serilog / NLog</b>（用户明确要求「不引第三方日志库」）。
    /// 这条是防以后有人顺手加的：加一个第三方日志库不会让任何一条别的测试变红。
    /// </summary>
    [Test]
    public void 不引第三方日志库()
    {
        var 项目 = 全部csproj();
        Assert.That(项目, Has.Count.EqualTo(4), "工程数变了 —— 这条守卫要跟着挪");

        Assert.Multiple(() =>
        {
            foreach (string f in 项目)
            {
                // 先摘 XML 注释（见 去掉xml注释）：只有注释外那些节点才进得了还原图
                string 文本 = 去掉xml注释(File.ReadAllText(f)).ToLowerInvariant();
                // 小写比：PackageReference 的写法不止一种（Include / Update / Directory.Packages.props 挪出去）
                foreach (string 词 in new[] { "serilog", "nlog", "log4net" })
                    Assert.That(文本, Does.Not.Contain(词), $"{Path.GetFileName(f)} 里出现了第三方日志库「{词}」");
            }
        });
    }

    // ==================== E. 菜单 ====================

    /// <summary>
    /// 「操作 ▾」里：撤销 → 重做 → <b>一道分隔</b> → 「打开日志文件夹」。
    /// 位置和那道分隔都在这儿钉死 —— 少了分隔它看起来就是第三个编辑动作，
    /// 而「看起来像编辑动作」不会让任何东西报错。
    /// </summary>
    [Test]
    public void 菜单_打开日志文件夹在撤销重做之后且前面有一道分隔()
    {
        var 项 = 操作菜单().Elements().ToList();
        var 名字们 = 项.Select(e => e.Name.LocalName).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(名字们, Is.EqualTo(new[] { "MenuItem", "MenuItem", "Separator", "MenuItem" }),
                "「操作」里的排法变了 —— 要的是 撤销 / 重做 / 分隔 / 打开日志文件夹");

            Assert.That((string?)项[0].Attribute("Header"), Is.EqualTo("撤销"));
            Assert.That((string?)项[1].Attribute("Header"), Is.EqualTo("重做"));
            Assert.That((string?)项[3].Attribute("Header"), Is.EqualTo("打开日志文件夹"));

            Assert.That(名字(项[3]), Is.EqualTo("OpenLogFolderMenuItem"), "有测试按这个名字找它");
            Assert.That((string?)项[3].Attribute("Click"), Is.EqualTo("OnOpenLogFolderClick"),
                "那一项没接上处理函数");
        });
    }

    /// <summary>
    /// 点它要真的去开那个文件夹，而且**日志还没写出来时也得开得出来**：
    /// <c>LogFolder.Open</c> 的第一件事就是把目录建出来，处理函数**不许**先问「文件在不在」
    /// —— 那正是「点了弹一句错」的写法。
    ///
    /// 真去调 <c>Process.Start</c> 会把资源管理器开在跑测试的人脸上，所以这两条看的是源码；
    /// 「资源管理器真开了」在上机那一步。
    /// </summary>
    [Test]
    public void 菜单_那一项先建目录再打开_不拿有没有日志当条件()
    {
        var 打开 = 函数体(日志层代码("LogFolder.cs"), "public static void Open(");
        var 处理 = 函数体(主窗口代码(), "private void OnOpenLogFolderClick(");

        Assert.Multiple(() =>
        {
            Assert.That(打开, Is.Not.Empty, "LogFolder.Open 没找到");
            Assert.That(打开, Does.Contain("EnsureExists"), "Open 没先建目录 —— 日志还没写出来时点它就是一条死路");
            Assert.That(打开, Does.Contain("Process.Start"), "Open 没真去开");
            Assert.That(打开.IndexOf("EnsureExists", StringComparison.Ordinal),
                Is.LessThan(打开.IndexOf("Process.Start", StringComparison.Ordinal)),
                "得先建目录、再打开");
            Assert.That(打开, Does.Contain("UseShellExecute"), "不开 ShellExecute 就打不开一个文件夹");

            Assert.That(处理, Is.Not.Empty, "OnOpenLogFolderClick 没找到");
            Assert.That(处理, Does.Contain("LogFolder.Open"), "那一项没真去开文件夹");
            Assert.That(处理, Does.Contain("LogFolder.DefaultDirectory"),
                "路径该从 LogFolder 拿 —— 别在这儿再拼一遍 %LOCALAPPDATA%");
            Assert.That(处理, Does.Not.Contain("File.Exists"),
                "处理函数拿「文件在不在」当条件了 —— 日志还没写出来时点它就该把目录建出来，不是弹错");
        });
    }

    /// <summary>
    /// 组装点真的把日志接上了：建了工厂、写启动那一行、并且**递给了主窗口**。
    /// 少了任何一半，「跑了程序却没有日志」都不会让别的测试变红。
    /// </summary>
    [Test]
    public void 组装点建了日志并且递给了主窗口()
    {
        string app = 去注释(File.ReadAllText(Path.Combine(App目录, "App.axaml.cs")));
        // 签名挑这一段：`public MainWindow(` 有两处（可视化设计器那个空构造也长这样），
        // 从这一串参数往后找花括号才落在真那个构造上
        string 主窗口构造 = 花括号段(主窗口代码(), "Func<PerformerWindow>? performerFactory, SongLibrary? library,");

        Assert.Multiple(() =>
        {
            Assert.That(app, Does.Contain("LoggingSetup.Start("), "组装点没起日志");
            Assert.That(app, Does.Contain("CreateLogger("), "组装点没开 logger");
            Assert.That(app, Does.Contain("logFactory"), "组装点起的那个工厂没往下递");
            Assert.That(主窗口构造, Does.Contain("ILogger? logger"),
                "主窗口收不到 logger —— 62 号那个耗时就没地方拿");
            Assert.That(主窗口构造, Does.Contain("NullLogger.Instance"),
                "没给 logger 时得退回空转的，别让日志成了「窗口起不起得来」的条件");
        });
    }

    // ==================== 共用 ====================

    /// <summary>
    /// 带真栈的异常：日志格式里异常栈是许多行，红线那条「每一行只有四个字段位」的断言
    /// 要量到多行那一档才作数。
    /// </summary>
    private static Exception 带栈的异常()
    {
        try
        {
            throw new InvalidOperationException("盘上写不下去");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>写一份「本程序的日志」，时间戳按给的时刻摆好。返回全路径。</summary>
    private string 写一份(string 文件名, DateTimeOffset 时刻)
    {
        string path = Path.Combine(_目录, 文件名);
        File.WriteAllText(path, "一行日志\n");
        File.SetLastWriteTimeUtc(path, 时刻.UtcDateTime);
        return path;
    }

    /// <summary>这个目录里**本程序自己写的**那些日志。判据跟 <see cref="LogFolder.Prune"/> 同一条。</summary>
    private string[] 自己那批()
        => Directory.GetFiles(_目录, LogFolder.FilePrefix + "*" + LogFolder.FileSuffix);

    /// <summary>日志头的形状：时间戳 · 级别 · 类别 · 正文。就这四个字段位。</summary>
    private static Regex 日志头() => new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} "
        + @"\[(Trace|Debug|Information|Warning|Error|Critical)\] [\w.+]+: ");

    // ---- 三类不许进日志的来源 ----

    /// <summary>按键：哪个键、什么时候按的。</summary>
    private static readonly string[] 按键类 =
    {
        "VirtualKey", "ScanCode", "PhysicalKey", "KeyDown", "KeyUp", "KeyEvent",
    };

    /// <summary>进程：这台机器上开着什么。</summary>
    private static readonly string[] 进程类 =
    {
        "ProcessName", "ProcessId", "GetProcesses", "GetCurrentProcess", "MainModule",
    };

    /// <summary>窗口标题：开着的是哪个游戏。</summary>
    private static readonly string[] 窗口标题类 =
    {
        "WindowTitle", "MainWindowTitle", "GetForegroundWindow", "GetWindowText", "ActiveWindow",
    };

    /// <summary>日志那一层不许去问的环境事实。</summary>
    private static readonly string[] 环境事实类 =
    {
        "Environment.MachineName", "Environment.UserName", "Environment.UserDomainName",
        "Environment.ProcessId", "Environment.ProcessPath", "Environment.CommandLine",
        "Environment.StackTrace", "Assembly.Location",
        "Process.GetCurrentProcess", "Process.GetProcesses", "ProcessName",
        "GetForegroundWindow", "GetWindowText", "MainWindowTitle",
    };

    // ---- 源码位置与读法 ----

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string App目录 => Path.Combine(RepoRoot, "MidiPerformer.App");

    private static string 日志层目录 => Path.Combine(App目录, "Logging");

    private static string 日志层代码(string 文件) => File.ReadAllText(Path.Combine(日志层目录, 文件));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(App目录, "Views", "MainWindow.axaml.cs"));

    private static string[] 产品源码文件()
        => new[] { App目录, Path.Combine(RepoRoot, "MidiPerformer.Core"), Path.Combine(RepoRoot, "MidiPerformer.Adapters") }
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

    private static List<string> 全部csproj() => new[]
    {
        Path.Combine(App目录, "MidiPerformer.App.csproj"),
        Path.Combine(RepoRoot, "MidiPerformer.Core", "MidiPerformer.Core.csproj"),
        Path.Combine(RepoRoot, "MidiPerformer.Adapters", "MidiPerformer.Adapters.csproj"),
        Path.Combine(RepoRoot, "MidiPerformer.Tests", "MidiPerformer.Tests.csproj"),
    }.Where(File.Exists).ToList();

    /// <summary>
    /// 把 <c>//</c> 之后切掉。下面几条守卫看的是**代码**不是注释里的散文 ——
    /// 那几条注释正要点名这几个词才说得清为什么不许用它们。
    /// </summary>
    private static string 去注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");

    /// <summary>
    /// 同上，切的是 XML 注释：守卫要盯的是**会被还原器读到的那些节点**。
    /// <c>&lt;!-- 本工程没引 Serilog --&gt;</c> 是**写明**这条禁令，不是违反它 ——
    /// csproj 里那条注释正是这个写法，不摘掉的话守卫会红在自己那份说明上。
    /// </summary>
    private static string 去掉xml注释(string 文本) =>
        Regex.Replace(文本, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>谁是写日志的调用。只看调用点，不看方法定义（provider 自己那个 <c>Log</c> 不是）。</summary>
    private static readonly Regex 写日志的调用 = new(
        @"\.\s*Log(Trace|Debug|Information|Warning|Error|Critical)?\s*(<[^>()]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// 抠出产品源码里每一处写日志的**整条语句**（跨行的也算一条），
    /// 连是哪个文件哪一行一起带出来，红了能直接指过去。
    /// </summary>
    private static IEnumerable<(string 文件, int 行号, string 语句)> 写日志的语句()
    {
        foreach (string 文件 in 产品源码文件())
        {
            string[] 行 = 去注释(File.ReadAllText(文件)).Split('\n');

            for (int i = 0; i < 行.Length; i++)
            {
                if (!写日志的调用.IsMatch(行[i])) continue;

                var 语句 = new StringBuilder(行[i]);
                int j = i;
                while (!语句.ToString().TrimEnd().EndsWith(";", StringComparison.Ordinal) && j + 1 < 行.Length)
                    语句.Append(' ').Append(行[++j]);

                yield return (文件, i + 1, 语句.ToString());
            }
        }
    }

    private static XDocument 主窗口() => XDocument.Load(Path.Combine(App目录, "Views", "MainWindow.axaml"));

    private static XElement 操作菜单()
    {
        var 菜单 = 主窗口().Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "MenuItem" && (string?)e.Attribute("Header") == "操作");

        Assert.That(菜单, Is.Not.Null, "「操作 ▾」那个菜单没找到");
        return 菜单!;
    }

    private static string? 名字(XElement e)
        => e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")?.Value;

    /// <summary>
    /// 从一句签名开始，一直抠到它那对花括号合上（按括号配对，不管里面有几个块）。
    /// <b>签名本身也算在里面</b> —— 参数表里也是会出事的（主窗口那个 <c>ILogger? logger</c> 就住在参数表里）。
    /// </summary>
    private static string 花括号段(string 源码, string 签名)
    {
        int 起 = 源码.IndexOf(签名, StringComparison.Ordinal);
        if (起 < 0) return "";
        int 开 = 源码.IndexOf('{', 起);
        if (开 < 0) return "";

        int 层 = 0;
        for (int i = 开; i < 源码.Length; i++)
        {
            if (源码[i] == '{') 层++;
            else if (源码[i] == '}')
            {
                层--;
                if (层 == 0) return 源码[起..(i + 1)];
            }
        }

        return "";
    }

    /// <summary>同上，但签名后面直接跟花括号的那种（构造器、方法都算）。</summary>
    private static string 函数体(string 源码, string 签名) => 花括号段(源码, 签名);
}
