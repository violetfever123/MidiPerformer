using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MidiPerformer.Tests.Safety;

/// <summary>
/// 89 号票：<b>安全边界</b>（不枚举进程 / 不查前台窗口 / 不碰游戏内存）的仓库级守卫。
///
/// <b>为什么要有它：</b>这条边界是<b>用户点名要的</b>，规格里写着两处 ——
/// <c>docs/spec-演奏器.md</c> 的 User Stories 第 70 条，和 Implementation Decisions「安全边界」第 1 条。
/// 理由不是洁癖，是<b>防反作弊</b>：程序只往系统里发按键，那就不会被当成外挂；
/// 代价是没有「失焦自动急停」这条兜底，这是用户自己认下的取舍。
///
/// <b>今天它是绿的</b>（产品源码里确实一处都没有）。所以这一票<b>不是修 bug</b>，
/// 价值全在以后：这条边界此前<b>没有任何仓库级的东西阻止下一张票顺手加一个
/// <c>GetForegroundWindow</c></b>。全仓唯一沾边的是
/// <c>Startup/LoggingTests.cs</c> 的「日志那一层自己不去问环境事实」，但它只扫
/// <c>App\Logging\</c> 一个目录，理由是<b>日志隐私</b> —— 跟反作弊不是一回事。
///
/// <b>只扫产品三个工程</b>（<c>MidiPerformer.App</c> / <c>.Core</c> / <c>.Adapters</c>）。
/// 两个必须排除的：
/// <list type="bullet">
///   <item><c>tools/uitest/</c> —— 那套 harness 整套活就是驱动窗口，<b>合法地</b>大量用
///   <c>GetForegroundWindow</c> / 枚举窗口 / <c>GetWindowThreadProcessId</c>。
///   把它扫进来 = 门红在自己合法的工具上。</item>
///   <item><c>MidiPerformer.Tests</c>（含本文件）—— 这份守卫的词表里<b>就写着这些名字</b>，
///   扫自己必然第一天就红。而且测试工程里<b>合法地</b>就有这种调用：
///   <c>Tools/RunAllTests.cs</c> 拿 <c>Process.GetProcessesByName</c> 去验
///   「跑之前已经有实例在跑就拦住」那道闸门。两条排除都由
///   <see cref="范围_只扫产品三个工程_uitest那些调用一个都没被扫成违反"/> 钉住。</item>
/// </list>
///
/// <b>断言之前先剥注释</b>（手法照抄 61 号，见 <see cref="去注释"/>）：这条边界会在自己的说明文字里
/// 反复点到这些名字，而<b>写明</b>一条禁令不是<b>违反</b>它。61 号就是先撞了这条红才知道要剥的。
///
/// <b>词表宁窄勿宽</b>：宽词表 + 例外名单必然越长越烂，而长名单就是下一个假红源。
/// 所以这里只收「观察别的进程/别的窗口/别的进程的内存」这三类里的<b>具体 API 名</b>，
/// 并且用<b>词边界</b>匹配 —— 于是 <c>OpenProcessToken</c>（对<b>自己</b>的进程做提权自检，
/// <c>Adapters\Gateways\InputSender.cs</c> 在用）不会被 <c>OpenProcess</c> 这条误伤。
/// <b>边界内的</b>一律放过、也一条都没进词表：<c>SendInput</c>（发按键）、
/// <c>SetWindowsHookEx</c>/<c>WH_KEYBOARD_LL</c>（低层钩子）、<c>PostMessage</c>/<c>SendMessage</c>、
/// <c>GetWindowRect</c>/<c>GetClientRect</c>、以及 <c>GetActiveWindow</c>
/// （取的是<b>本线程</b>的活动窗口，不是系统的前台窗口 —— <c>InputMethod.cs</c> 有注释写明这一点）。
///
/// <b>这条守卫抓什么：</b>产品源码里出现词表里任何一个 API 名 —— <b>包括写在字符串字面量里的</b>
/// （于是 <c>GetProcAddress(库, "GetForegroundWindow")</c> 这种绕法也在射程内）。
/// <b>不抓什么：</b>把名字拆开拼出来的（<c>"GetForeground" + "Window"</c>）、
/// 托管反射按方法名字符串取的（词表里带点的 <c>Process.GetProcesses</c> 拦不到裸 <c>"GetProcesses"</c>）、
/// 以及非 <c>.cs</c> 文件与 <c>obj</c>/<c>bin</c> 生成物。它是<b>文本</b>守卫，不是运行时行为守卫。
///
/// 起 Avalonia 吗？不起。这一块看的是<b>源码里有什么</b>，跟像素与焦点无关 ——
/// 「跑起来之后有没有偷偷去读进程」是别的手段的事，这儿不冒充。
/// </summary>
public class SafetyBoundaryTests
{
    // ==================== 🔴 红线 ====================

    /// <summary>
    /// <b>这一票真正的门。</b> 产品三个工程的每一个 <c>.cs</c> 里，都不许出现词表里的任何调用。
    ///
    /// 「今天本来就是绿的」这一点也必须钉住「文件真的读到了」——
    /// 否则「一处违反都没有」跟「一个文件都没读」在读法上一模一样（<see cref="产品源码文件"/> 换个路径、
    /// 或者三个目录被搬走，这条断言都会照样绿）。
    /// </summary>
    [Test]
    public void 红线_产品源码里不枚举进程不查前台窗口不读别人内存()
    {
        string[] 文件们 = 产品源码文件();
        var 违反们 = 扫(文件们).ToList();

        TestContext.Out.WriteLine($"89 号守卫扫了 {文件们.Length} 个 .cs：");
        foreach (string 工程 in 三个工程)
            TestContext.Out.WriteLine(
                $"  {工程}: {文件们.Count(f => 在工程里(f, 工程))} 个");

        Assert.Multiple(() =>
        {
            // ① 先钉住「真的读到了东西」——不然下面那条「一处都没有」是空过的
            foreach (string 工程 in 三个工程)
                Assert.That(文件们.Any(f => 在工程里(f, 工程)), Is.True,
                    $"{工程} 里一个 .cs 都没扫到 —— 目录被搬了，这条守卫现在是空过的");

            // ② 🔴 边界：一处都不许有
            Assert.That(违反们, Is.Empty, 违反明细(违反们));
        });
    }

    // ==================== 对照：证明这条门真的会响 ====================

    /// <summary>
    /// <b>本票最要紧的一条对照。</b> 一个从没红过的门，「它不响」和「它坏了」在读法上分不出来。
    /// 所以这里现造一份<b>故意违规</b>的样本（只活在 <c>%TEMP%</c>，不进仓库），
    /// 一条禁令一行喂给守卫，然后断言：
    /// <list type="number">
    ///   <item>它<b>确实红了</b>（不是一声不吭）；</item>
    ///   <item>词表里<b>每一条</b>都抓住了自己那份样例 —— 少一条就说明那条禁令是个<b>哑弹</b>
    ///   （名字写在表里、正则却打不中），而哑弹是看不出来的。</item>
    /// </list>
    /// </summary>
    [Test]
    public void 对照_把违规样本喂进来每一条禁令都真的响()
    {
        string 样本目录 = Directory.CreateTempSubdirectory("mp89-违规-").FullName;
        try
        {
            Assert.That(样本目录.StartsWith(RepoRoot, StringComparison.OrdinalIgnoreCase), Is.False,
                "临时样本落到仓库里了 —— 它只许活在 %TEMP% 下");

            string 样本文件 = Path.Combine(样本目录, "违规样本.cs");
            File.WriteAllLines(样本文件,
                new[] { "internal static class 违规样本", "{" }
                    .Concat(禁令表.Select(条 => "    static void _() { " + 条.样例 + " }"))
                    .Concat(new[] { "}" }));

            var 违反们 = 扫(new[] { 样本文件 }).ToList();
            var 响过的 = 违反们.Select(v => v.词).ToHashSet(StringComparer.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(违反们, Is.Not.Empty,
                    "整份违规样本喂进去，一条都没响 —— 这条守卫是坏的，它挡不住任何东西");

                foreach (var 条 in 禁令表)
                    Assert.That(响过的, Does.Contain(条.词),
                        $"「{条.词}」在词表里，却抓不住自己那份样例 —— 这条禁令是个哑弹：{条.样例}");
            });
        }
        finally
        {
            try { Directory.Delete(样本目录, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 上一条的反面：同样一批名字，<b>只出现在注释里</b>时守卫必须是绿的。
    ///
    /// 这条同时钉住两件事：
    /// <list type="bullet">
    ///   <item>注释真的被剥掉了（<b>前置断言</b>：这几个词在原文里在、剥完就不在 ——
    ///   少了这一步，万一样本根本没写这些词，这条对照就是在测空气）；</item>
    ///   <item>剥完之后，<b>边界内的</b>那几个（<c>SetWindowsHookEx</c> / <c>SendInput</c> /
    ///   <c>GetActiveWindow</c> / <c>OpenProcessToken</c>）一个都不算违反。</item>
    /// </list>
    /// 后一半是给词表的<b>误伤</b>上的一道闸：<c>OpenProcessToken</c> 里含着 <c>OpenProcess</c> 这个子串，
    /// 靠词边界才放得过去 —— 这条断言就是词边界那一处的现场证据。
    /// </summary>
    [Test]
    public void 对照_这些词只出现在注释里不算违反()
    {
        string 样本目录 = Directory.CreateTempSubdirectory("mp89-合规-").FullName;
        try
        {
            string 样本文件 = Path.Combine(样本目录, "合规样本.cs");
            File.WriteAllText(样本文件, """
                // 本程序不枚举进程：不许出现 Process.GetProcesses / Process.GetProcessesByName
                /// <summary>不查前台窗口，所以这里不调 GetForegroundWindow / GetWindowThreadProcessId。</summary>
                /* 也不读别人的内存：ReadProcessMemory / WriteProcessMemory / OpenProcess 一个都不用 */
                using System.Runtime.InteropServices;

                internal static class 合规样本
                {
                    // WH_KEYBOARD_LL 是边界内的：钩子只看得到本进程自己的按键事件流
                    private static extern IntPtr SetWindowsHookEx(int 哪, IntPtr 钩, IntPtr 模块, uint 线程);
                    private static extern void SendInput(uint 几条, IntPtr 们, int 每条多大);
                    private static extern IntPtr GetActiveWindow();  // 本线程的活动窗口，不是系统的前台窗口
                    private static extern bool OpenProcessToken(IntPtr 进程, uint 要什么, out IntPtr 令符);
                }
                """);

            string 原文 = File.ReadAllText(样本文件);
            string 剥过 = 去注释(原文);

            Assert.Multiple(() =>
            {
                // 前置：这几个词（三种注释写法各一个）确实写在样本里
                Assert.That(原文, Does.Contain("GetForegroundWindow")
                    .And.Contain("Process.GetProcesses")
                    .And.Contain("ReadProcessMemory"),
                    "样本里根本没写这些词 —— 这条对照是空过的");

                // 前置：剥完就不再有了（注释那一步真的干了活）
                Assert.That(剥过, Does.Not.Contain("GetForegroundWindow")
                    .And.Not.Contain("Process.GetProcesses")
                    .And.Not.Contain("ReadProcessMemory"),
                    "注释没被剥干净 —— 这条守卫会红在自己的说明文字上");

                // 正题：注释里写着这些词、代码里全是边界内的调用 ⇒ 绿
                Assert.That(扫(new[] { 样本文件 }), Is.Empty,
                    "代码里只有边界内的调用，守卫却红了 —— 词表误伤了");
            });
        }
        finally
        {
            try { Directory.Delete(样本目录, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ==================== 范围 ====================

    /// <summary>
    /// 扫描范围<b>就是产品三个工程</b>，一个不多一个不少。
    ///
    /// 前三条把范围钉死（<c>tools/uitest/</c> 与测试工程都不在里面）；第四条是这条排除的
    /// <b>旁证</b>：uitest 里确实满是这些调用 —— 不然「把它排除在外」就没在挡任何事，
    /// 那条排除是不是真的生效也就无从看起。
    /// </summary>
    [Test]
    public void 范围_只扫产品三个工程_uitest那些调用一个都没被扫成违反()
    {
        string uitest目录 = Path.Combine(RepoRoot, "tools", "uitest");
        string[] 文件们 = 产品源码文件();

        Assert.Multiple(() =>
        {
            // ① 扫描根目录：就这三个
            Assert.That(扫描根目录().Select(目录 => Path.GetFileName(目录)),
                Is.EqualTo(三个工程),
                "扫描范围变了 —— 这条守卫要跟着挪");

            // ② 一个 uitest 文件都没有（它合法地大量用这些 API，扫它必然红在自己合法的工具上）
            foreach (string f in 文件们)
                Assert.That(f.Contains($"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}"),
                    Is.False, $"{f} 混进扫描范围了");

            // ③ 也没有一个测试工程自己的文件（这份守卫的词表里就写着这些名字，扫自己必然红）
            foreach (string f in 文件们)
                Assert.That(f.Contains($"{Path.DirectorySeparatorChar}MidiPerformer.Tests{Path.DirectorySeparatorChar}"),
                    Is.False, $"{f} 混进扫描范围了 —— 守卫扫到自己的词表上了");
        });

        // ④ 旁证：uitest 里确实满是这些调用
        if (!Directory.Exists(uitest目录))
        {
            TestContext.Out.WriteLine("tools/uitest 不在，这条旁证这趟没有 —— 范围本身仍由 ① ② ③ 钉住。");
            return;
        }

        var uitest里的 = 扫(Directory.GetFiles(uitest目录, "*.ps1", SearchOption.AllDirectories)).ToList();
        TestContext.Out.WriteLine($"（旁证）tools/uitest 里有 {uitest里的.Count} 处这些调用，一处都没被扫成违反。");

        Assert.That(uitest里的, Is.Not.Empty,
            "tools/uitest 里一处这些调用都没有 —— 那「把它排除在外」就没在挡任何事。"
            + "（若哪天 uitest 真被清干净了，把这条断言连同 ④ 一起删掉即可，① ② ③ 仍钉住范围。）");
    }

    // ==================== 词表 ====================

    /// <summary>
    /// 一条禁令：<paramref name="词"/> 是要拦的 API 名，<paramref name="样例"/> 是一句
    /// <b>真会用到它</b>的代码 —— 对照那一条拿它现造违规样本，所以<b>每一条都必须能被自己的样例打红</b>。
    ///
    /// 只收三类，且只收<b>具体 API 名</b>（不收 <c>Process</c> 这种宽词 —— <c>Process.Start</c>
    /// 是合法在用的：开日志文件夹、提权重启都靠它）。
    /// </summary>
    private static readonly 禁令[] 禁令表 =
    {
        // —— 一、枚举进程（「这台机器上开着什么」）——
        new("Process.GetProcesses",       "var 别人 = Process.GetProcesses();"),
        new("Process.GetProcessesByName", "var 游戏 = Process.GetProcessesByName(\"三角洲行动\");"),
        new("Process.GetProcessById",     "var 它 = Process.GetProcessById(1234);"),
        new("MainWindowTitle",            "string 标题 = 某个.MainWindowTitle;"),
        new("EnumProcesses",              "EnumProcesses(缓冲, 大小, out uint 用掉多少);"),
        new("CreateToolhelp32Snapshot",   "IntPtr 快照 = CreateToolhelp32Snapshot(0x2, 0);"),
        new("Process32First",             "Process32First(快照, ref 条目);"),
        new("Process32Next",               "Process32Next(快照, ref 条目);"),

        // —— 二、查前台窗口 / 扫别人的窗口（「用户现在在玩什么、切没切出去」）——
        new("GetForegroundWindow",        "IntPtr 前台 = GetForegroundWindow();"),
        new("GetWindowThreadProcessId",   "GetWindowThreadProcessId(前台, out uint 进程号);"),
        new("EnumWindows",                "EnumWindows(回调, IntPtr.Zero);"),
        new("EnumChildWindows",           "EnumChildWindows(父, 回调, IntPtr.Zero);"),
        new("FindWindow",                 "IntPtr 它 = FindWindow(null, \"三角洲行动\");"),
        new("FindWindowEx",               "IntPtr 子 = FindWindowEx(父, IntPtr.Zero, null, null);"),
        new("GetWindowText",              "GetWindowText(句柄, 缓冲, 缓冲.Length);"),
        new("GetWindowTextLength",        "int 多长 = GetWindowTextLength(句柄);"),
        new("SetWinEventHook",            "SetWinEventHook(3, 3, IntPtr.Zero, 回调, 0, 0, 0);"),

        // —— 三、碰别人的内存（这三条最重）——
        new("OpenProcess",                "IntPtr 它 = OpenProcess(0x10, false, 进程号);"),
        new("ReadProcessMemory",          "ReadProcessMemory(句柄, 地址, 缓冲, 大小, out IntPtr 读了);"),
        new("WriteProcessMemory",         "WriteProcessMemory(句柄, 地址, 缓冲, 大小, out IntPtr 写了);"),
        new("VirtualQueryEx",             "VirtualQueryEx(句柄, 地址, out 信息, 大小);"),
    };

    /// <summary>
    /// 词表的正则：<b>词边界</b>包住每一个名字，任选其一。
    /// 词边界是这儿唯一一处不能省的：<c>OpenProcess</c> 是 <c>OpenProcessToken</c> 的前缀，
    /// 而后者是<b>合法</b>的（对自己进程的提权自检，见 <c>Adapters\Gateways\InputSender.cs</c>）——
    /// 不加词边界，这条守卫第一天就红在产品自己身上。
    /// </summary>
    private static Regex? _禁令正则;

    private static Regex 禁令正则() => _禁令正则 ??= new Regex(
        @"\b(?<词>" + string.Join("|", 禁令表.Select(条 => Regex.Escape(条.词))) + @")\b",
        RegexOptions.Compiled);

    // ==================== 扫法与读法 ====================

    /// <summary>一条禁令。</summary>
    private sealed record 禁令(string 词, string 样例);

    /// <summary>
    /// 扫一批文件，把命中的地方连<b>文件 / 行号 / 哪个词 / 那一行</b>一起交出来。
    /// <b>先剥注释再比</b>（见 <see cref="去注释"/>）。
    /// </summary>
    private static IEnumerable<(string 文件, int 行号, string 词, string 行)> 扫(IEnumerable<string> 文件们)
    {
        foreach (string 文件 in 文件们)
        {
            string[] 行们 = 去注释(File.ReadAllText(文件)).Split('\n');
            for (int i = 0; i < 行们.Length; i++)
            {
                var 命中 = 禁令正则().Match(行们[i]);
                if (命中.Success)
                    yield return (文件, i + 1, 命中.Groups["词"].Value, 行们[i].Trim());
            }
        }
    }

    /// <summary>红了直接指过去：哪一份、第几行、哪个词、那一行长什么样。</summary>
    private static string 违反明细(IReadOnlyCollection<(string 文件, int 行号, string 词, string 行)> 违反们)
        => 违反们.Count == 0
            ? "（没有违反）"
            : "产品源码里出现了安全边界外的调用（不枚举进程 / 不查前台窗口 / 不碰游戏内存）：\n"
              + string.Join("\n", 违反们.Select(v => $"  {v.文件}:{v.行号}  「{v.词}」  {v.行}"));

    /// <summary>
    /// 把 <c>//</c> 之后切掉。<b>照抄 61 号</b>：下面几条守卫看的是<b>代码</b>不是注释里的散文 ——
    /// 那些注释正要点名这几个词才说得清为什么不许用它们。
    /// </summary>
    private static string 去行注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");

    /// <summary>
    /// 再把 <c>/* … */</c> 这一种也切掉。61 号那份只切了 <c>//</c>，本票补上这一种：
    /// 「写明一条禁令」和「违反它」在两种注释里长得一样，漏掉一种就是留一个假红源。
    ///
    /// ⚠️ 已知代价（和 61 号那条 <c>//</c> 规则同一个性质）：它是<b>正则剥</b>不是<b>词法剥</b> ——
    /// 字符串字面量里出现 <c>/*</c> 时可能连带吞掉后面的真代码。那个方向的错是<b>漏报</b>不是误报，
    /// 对一条安全边界守卫来说是可以接受的一侧。
    /// </summary>
    private static string 去块注释(string 文本) => Regex.Replace(文本, @"/\*.*?\*/", "", RegexOptions.Singleline);

    /// <summary>先剥块注释、再剥行注释，剩下的才是「代码」。</summary>
    private static string 去注释(string 源码) => 去行注释(去块注释(源码));

    /// <summary>被扫的三个工程。产品源码就是这三个 —— 别的一律不在范围内。</summary>
    private static readonly string[] 三个工程 = { "MidiPerformer.App", "MidiPerformer.Core", "MidiPerformer.Adapters" };

    private static string[] 扫描根目录() => 三个工程.Select(名 => Path.Combine(RepoRoot, 名)).ToArray();

    /// <summary>三个工程里的全部 <c>.cs</c>，<c>obj</c>/<c>bin</c> 那两个生成物目录不算。</summary>
    private static string[] 产品源码文件() => 扫描根目录()
        .SelectMany(目录 => Directory.GetFiles(目录, "*.cs", SearchOption.AllDirectories))
        .Where(不是编译产物)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();

    private static bool 不是编译产物(string 路径)
        => !路径.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
           && !路径.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static bool 在工程里(string 文件, string 工程)
        => 文件.StartsWith(Path.Combine(RepoRoot, 工程), StringComparison.OrdinalIgnoreCase);

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));
}
