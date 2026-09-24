using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace MidiPerformer.Tests.Tools;

/// <summary>
/// <c>tools/uitest/run-all.ps1</c> 的冒烟测试：**别让 runner 自己变成一门不响的门。**
///
/// 那批 verify-*.ps1 的价值全押在「跑出来的结论可不可信」上，而 runner 是它们的入口。
/// 所以这里不测那 20 来份脚本（它们各有自己的工单），只测 runner 的四件事：
///
///   ① **四条状态分得开**：绿 / 红 / 没跑完 / 超时，谁都不许冒充谁 ——
///      尤其是「退出码 0 但一个字节都没说」那种（<c>find-note.ps1</c> 坏了 4 天没人知道，
///      就是因为它返回 0 条之后照样往下跑，产出**看起来像结论的垃圾**）。
///   ② **一条红了不中断**：全跑完再报，总表里看得见「哪几条红」。
///   ③ **没跑完和退出码非零分得开**：<c>-只查日志</c> 按哨兵复核已经存在的 log。
///   ④ **关门判据只认那四类语法形态**：注释与提示文案不许被判成 `.scratch` 引用 ——
///      判据写粗了它自己就制造一片假红，而**一条恒红的判据等于没有判据**。
///   ⑤ **跑前桌上有实例就当场抛，且绝不替用户关**：那是退出码 2（没开始跑），
///      不是红也不是绿；抛完那个实例必须还活着。
///
/// <b>跑的都是临时目录里的假脚本</b>（<c>verify-9x-*.ps1</c>）：这个 fixture 不起窗口、
/// 不碰 MidiPerformer、不动仓库里任何一份真脚本 —— 起真窗口那件事必须由人在锁里做
/// （那 20 来份脚本要抢前台，桌面上一有别人的实例就会得到一张假红表）。
///
/// 断言一律断在 <c>logs/run-all.json</c> 上，**不断控制台那几句中文**：
/// 那是给人读的，措辞一改测试就假红；而且这个仓库的控制台是 936，中文来回转码本就不稳。
/// </summary>
[TestFixture]
public class RunAllTests
{
    // runner 写进 log 与 json 的都是 ASCII 词（控制台那张表才翻译成中文）——
    // 中文在 936 的宿主里会变成乱码，而「匹配不到就默认算过」正是最标准的假绿。
    private const string 绿 = "green";
    private const string 红 = "red";
    private const string 没跑完 = "incomplete";
    private const string 超时 = "timeout";

    private const string 脚本文件名 = "run-all.ps1";

    private readonly List<string> 待删 = new();

    [TearDown]
    public void 收掉临时文件()
    {
        foreach (var 路径 in 待删)
        {
            try
            {
                if (Directory.Exists(路径)) { Directory.Delete(路径, recursive: true); }
                else if (File.Exists(路径)) { File.Delete(路径); }
            }
            catch
            {
                // 临时文件删不掉不该让测试红（被刚杀掉的子进程占着是常事）。
            }
        }
        待删.Clear();
    }

    // ==================== ① ② 四态分得开 + 红了不中断 ====================

    [Test]
    public void 一条命令跑完全套_四态分得开_红的不中断后面的_总退出码非零()
    {
        var 目录 = 造临时目录();
        造一套标准脚本(目录);

        var (码, _) = 跑Runner(目录, "-超时秒", "5", "-跳过实例检查");

        var 条目 = 读条目(目录);

        Assert.Multiple(() =>
        {
            Assert.That(
                条目.Keys,
                Is.EquivalentTo(new[]
                {
                    "verify-90-绿", "verify-91-红", "verify-92-不响", "verify-93-卡死", "verify-94-半路抛",
                }),
                "五条都跑了：红的那条不中断后面的；uitest-lib / find-note / probe-41-dlg 不匹配 verify-*，不该被跑");

            Assert.That(码, Is.EqualTo(3), "有「没跑完」和「超时」时 3 压过 1（红）——3 比 1 严重，因为「有几条红」这个数本身就不完整");

            Assert.That(条目["verify-90-绿"].State, Is.EqualTo(绿), "退出码 0 且说了话 = 绿");
            Assert.That(条目["verify-91-红"].State, Is.EqualTo(红), "退出码非零且说了话 = 红");
            Assert.That(条目["verify-91-红"].ExitCode, Is.EqualTo(1));

            Assert.That(条目["verify-92-不响"].State, Is.EqualTo(没跑完), "退出码 0 但一个字节都没说 ——「不响的门」不是绿门");
            Assert.That(条目["verify-92-不响"].ExitCode, Is.EqualTo(0), "……而它的退出码就是 0：这正是「非零」和「压根没跑完」必须分开的原因");

            Assert.That(条目["verify-93-卡死"].State, Is.EqualTo(超时), "超时和红、和没跑完都是三件事");
            Assert.That(条目["verify-93-卡死"].ExitCode, Is.EqualTo(-1), "超时那条没有退出码（被按 PID 杀了）");

            Assert.That(条目["verify-94-半路抛"].State, Is.EqualTo(没跑完), "脚本自己抛了 = 断点之后那些断言一条都没跑，不是红也不是绿");
        });
    }

    [Test]
    public void 上一次的状态记在上一趟那一列_改之前是红的改之后是绿的才看得见()
    {
        var 目录 = 造临时目录();
        造一套标准脚本(目录);

        // 第一趟：一切照旧。
        跑Runner(目录, "-超时秒", "5", "-跳过实例检查");

        // 把「不响」那条修好、把红那条改成绿的 —— 模拟一次「改之前红、改之后绿」。
        写脚本(目录, "verify-91-红.ps1", "Write-Host '断言 3 条全过'\nexit 0\n");
        写脚本(目录, "verify-92-不响.ps1", "Write-Host '现在会说话了'\nexit 0\n");

        var (码, _) = 跑Runner(目录, "-只跑", "verify-9[12]-*", "-超时秒", "5", "-跳过实例检查");

        var 条目 = 读条目(目录);

        Assert.Multiple(() =>
        {
            Assert.That(码, Is.EqualTo(0), "-只跑 把那三条（红的、不响的、卡死的）滤在外面，这一趟就是全绿");

            // -只跑 之后总表里只有这两条：上一趟那两条的原值要**从上一份 json** 读回来 ——
            // 这正是「改之前必须是红的」在报告上留下的痕迹。
            Assert.That(条目.Keys, Is.EquivalentTo(new[] { "verify-91-红", "verify-92-不响" }), "-只跑 只跑匹配的那两条");
            Assert.That(条目["verify-91-红"].PreviousState, Is.EqualTo(红), "上一趟它是红的");
            Assert.That(条目["verify-91-红"].State, Is.EqualTo(绿), "这一趟绿了 —— 上一趟红、这一趟绿，同一条脚本都看得见");
            Assert.That(条目["verify-92-不响"].PreviousState, Is.EqualTo(没跑完), "上一趟它一个字都没说");
            Assert.That(条目["verify-92-不响"].State, Is.EqualTo(绿), "这一趟会说话了 = 绿（「不响」不是绿）");
        });
    }

    // ==================== ③ 没跑完 ≠ 退出码非零 ====================

    [Test]
    public void 只查日志_把log截断之后分得出没跑完_而且没跑完不算绿()
    {
        var 目录 = 造临时目录();
        // 这四条只提供**名字**：-只查日志 不跑任何脚本，只看 logs 里已经躺着的那几份。
        foreach (var i in new[] { 1, 2, 3, 4 })
        {
            写脚本(目录, $"verify-99-{i}.ps1", "Write-Host '占位'\nexit 0\n");
        }

        var 日志目录 = Path.Combine(目录, "logs");
        Directory.CreateDirectory(日志目录);
        写日志(日志目录, "verify-99-1", "==== run-all 开始 脚本=verify-99-1", "断言 3 条全过",
            "==== run-all 止 退出码=0", "==== run-all 判定 状态=green 退出码=0 耗时=1.0s 脚本=verify-99-1");
        写日志(日志目录, "verify-99-2", "==== run-all 开始 脚本=verify-99-2", "前半验过");
        写日志(日志目录, "verify-99-3",
            "==== run-all 开始 脚本=verify-99-3", "断言 1 条", "==== run-all 止 退出码=0",
            "==== run-all 判定 状态=green 退出码=0 耗时=1.0s 脚本=verify-99-3",
            "后面又写了一行（这一份被截断了）");
        写日志(日志目录, "verify-99-4", "这是手工重定向留下的 log，没有哨兵");

        var (码, _) = 跑Runner(目录, "-只查日志");

        var 表 = 读JSON(Path.Combine(日志目录, "run-all.json"));
        var 条目 = 条目字典(表);

        Assert.Multiple(() =>
        {
            Assert.That(码, Is.EqualTo(3), "「没跑完」既不是红（1）也不是绿（0），它自己一个退出码：3");
            Assert.That(表.GetProperty("mode").GetString(), Is.EqualTo("audit"));

            Assert.That(条目["verify-99-1"].State, Is.EqualTo(绿), "带着完整的判定哨兵 = 这一趟跑完了");

            Assert.That(条目["verify-99-2"].State, Is.EqualTo(没跑完), "有「开始」、没有「判定」—— runner 或脚本被杀在中间");
            Assert.That(条目["verify-99-3"].State, Is.EqualTo(没跑完), "「判定」那行后面还有东西 = 这份 log 被截断过");
            Assert.That(
                条目["verify-99-3"].State,
                Is.Not.EqualTo(绿),
                "⚠️ 重点：它的判定行白纸黑字写着「状态=green」，可那行**不在末尾** —— "
                + "被截断的 log 不许当绿：后面那些断言一条都没跑");

            Assert.That(条目["verify-99-4"].State, Is.EqualTo("not-runner"), "没有哨兵的 log 不是 runner 写的，本工具不判它（也不许当成红）");
        });
    }

    // ==================== ④ 关门判据只认那四类语法形态 ====================

    [Test]
    public void 关门判据只认四类语法形态_注释与提示文案不许被算成引用()
    {
        var 目录 = 造临时目录();

        // 四类真形态 —— 一处都不能漏。
        写脚本(目录, "verify-98-A.ps1", "param([string]$Out = '.scratch/shots/a.png')\nexit 0\n");
        写脚本(目录, "verify-98-B.ps1", ". (Join-Path $PSScriptRoot '..\\..\\.scratch\\load-song.ps1')\nexit 0\n");
        写脚本(目录, "verify-98-C.ps1", ". '..\\..\\.scratch\\uitest-lib.ps1'\nexit 0\n");
        写脚本(目录, "verify-98-D.ps1", "& pwsh -NoProfile -File '.scratch/verify-x.ps1'\nexit 0\n");

        // 不算的四类 —— 一处都不许抓。
        写脚本(目录, "verify-98-E.ps1", "throw '还没载入曲子 —— 先跑 .scratch/load-song.ps1'\n");
        写脚本(目录, "verify-98-F.ps1", "# 这个数是 .scratch/probe-40.log 里量出来的\nexit 0\n");
        // 块注释里那条**长得跟 B 一模一样**：只有「块注释整段抹掉」这条规则能把它挡下来。
        // （抬头的 .EXAMPLE 里写一条示例命令是最正常不过的事，判红就是判据自己制造假红。）
        写脚本(目录, "verify-98-H.ps1",
            "<#\n.EXAMPLE\n    . (Join-Path $PSScriptRoot '..\\..\\.scratch\\load-song.ps1')\n#>\nWrite-Host 'h'\nexit 0\n");
        // 本脚本当初拿这条判据判红过自己：提示文案里出现 `.scratch`，但那不是路径解析。
        写脚本(目录, "verify-98-I.ps1", "$说明 = \"语法级 .scratch 引用 N 处\"\nexit 0\n");

        // 那把「去注释」的刀自己的体检：字符串里有个 `#`，而**同一行 `#` 之后**还有真的路径字面量。
        // 要是那把刀在字符串里的 `#` 上就切了，这一行会**静默变成 0 命中** —— 那正是它的失败形态。
        写脚本(目录, "verify-98-G.ps1", "$s = '带 # 号的一句话'; $Out = '.scratch/y.txt'\nexit 0\n");

        var (码, _) = 跑Runner(目录, "-只查引用");

        var 表 = 读JSON(Path.Combine(目录, "logs", "run-all.json"));
        var 命中 = 命中清单(表, "scratch");

        Assert.Multiple(() =>
        {
            Assert.That(码, Is.EqualTo(1), "判据命中了就红 —— 而且**只**因为这些真形态");

            var 键 = 命中.Select(h => $"{h.File}:{h.Line}").ToHashSet();
            Assert.That(
                键,
                Is.EquivalentTo(new[]
                {
                    "verify-98-A.ps1:1", "verify-98-B.ps1:1", "verify-98-C.ps1:1",
                    "verify-98-D.ps1:1", "verify-98-G.ps1:1",
                }),
                "四类语法形态一处不漏（A=字面量 B=Join-Path C=dot-source D=子进程参数）"
                + "，外加 G —— 字符串里的 `#` 不许把同一行后面的真引用吃掉");

            // 四类「看着像、其实不是」的，必须一个都不在里面。
            foreach (var 不该抓 in new[] { "verify-98-E.ps1", "verify-98-F.ps1", "verify-98-H.ps1", "verify-98-I.ps1" })
            {
                Assert.That(键.Any(k => k.StartsWith(不该抓, StringComparison.Ordinal)), Is.False,
                    $"{不该抓} 是提示字符串 / 注释 / 提示文案，不是路径解析 —— 判据不许抓它（抓了就是判据自己制造假红）");
            }

            Assert.That(命中.Single(h => h.File == "verify-98-A.ps1").Kind, Is.EqualTo("literal"));
            Assert.That(命中.Single(h => h.File == "verify-98-B.ps1").Kind, Is.EqualTo("join-path"));
            Assert.That(命中.Single(h => h.File == "verify-98-C.ps1").Kind, Is.EqualTo("dot-source"));
            Assert.That(命中.Single(h => h.File == "verify-98-D.ps1").Kind, Is.EqualTo("argv"));
        });

        Assert.Multiple(() =>
        {
            // 闭包那一节：点名的成员缺位要报出来（这里一个都没造，所以五样全缺）。
            var 缺 = 命中清单(表, "closure");
            Assert.That(缺.Count(h => h.Kind == "missing-member"), Is.EqualTo(5), "uitest-lib / find-note / probe-41-dlg / probe-41 下两份，缺了就得点名");

            // 像素那节只报不判：判据是 info，不许让它把退出码顶上去。
            Assert.That(
                new[] { "pass", "fail" }.Contains(判据(表, "pixel").Verdict),
                Is.False,
                "像素 API 那一节**只列清单不判红**（硬规矩第 3 条）—— 存量那几处不在本票范围，判红等于挂一条恒红的闸门");
        });
    }

    // ==================== ⑤ 跑前桌上已经有实例 ====================

    /// <summary>
    /// 票面 D 组那两条「**都亲手试过**（留一个实例在跑，跑一次；别只读代码）」的验收，
    /// 在这里落成一条**每次都会重跑**的断言：
    ///
    ///   · 跑前桌面上已经有实例 → **当场抛**（退出码 2），一份 log 都不留；
    ///   · 而且**绝不替用户关**它 —— 两趟跑完那个实例必须还活着。
    ///
    /// 扮「实例」的是一份**改了名的 <c>cmd.exe</c> 副本**，不是真起一个 MidiPerformer：
    /// 起真窗口要抢前台，而这一轮桌上本来就同时有别的 agent 的实例来来去去（§5f），
    /// 凭空多一个假实例等于给别人制造假结论。副本由本测试自己起、自己**按 PID** 收
    /// （硬规矩第 1 条：绝不按进程名杀一片）。
    /// </summary>
    [Test]
    public void 跑前桌上已经有实例_当场抛退出码2_而且绝不替用户把它关掉()
    {
        var 目录 = 造临时目录();
        造一套标准脚本(目录);

        var 实例名 = "MidiPerformerGate" + Guid.NewGuid().ToString("N")[..8];
        using var 假实例 = 起假实例(实例名);
        try
        {
            // 刚 Start 的那一瞬间 Windows 不一定已经把它列得出来 —— 假实例没起来的话，
            // 下面「它没被关掉」这条断言会变成永真，整条测试就白验了。
            var 看见了 = false;
            for (var i = 0; i < 40 && !看见了; i++)
            {
                看见了 = Process.GetProcessesByName(实例名).Length > 0;
                if (!看见了) { Thread.Sleep(100); }
            }
            Assert.That(看见了, Is.True, $"假实例 {实例名} 没在进程表里出现 —— 这条闸门没法验");

            // ---- 第一趟：不跳过检查 → 当场抛，一条脚本都不跑 ----
            var (码, 输出) = 跑Runner核心(目录, 实例名, "-超时秒", "5");

            var 日志目录 = Path.Combine(目录, "logs");
            Assert.Multiple(() =>
            {
                Assert.That(码, Is.EqualTo(2), "跑前有实例 = 压根没开始跑 → 退出码 2（不是红 1、不是没跑完 3）");
                Assert.That(输出, Does.Contain(实例名), "得说清楚是**哪个名字**的实例在跑，不然人会去关错");
                Assert.That(
                    输出,
                    Does.Contain("PID " + 假实例.Id),
                    "……还得点名 PID：光说「有个实例」等于让人自己猜。这句和上面那句都是机械的，不按中文措辞断");
                Assert.That(
                    Directory.Exists(日志目录) ? Directory.GetFiles(日志目录, "*.log").Length : 0,
                    Is.EqualTo(0),
                    "当场抛在跑之前：一份 log 都不该留下 —— 留下了就说明它其实跑了半截");
                Assert.That(
                    File.Exists(Path.Combine(日志目录, "run-all.json")),
                    Is.False,
                    "连总表都不写：这一趟没有任何结论可言，留一份出来只会被人当成「跑过了」");
            });

            Assert.That(假实例.HasExited, Is.False, "⚠️ 重点：runner 不替用户关窗口 —— 抛完之后那个实例必须还活着");

            // ---- 第二趟：-跳过实例检查 → 照跑，并在总表里**如实报出**桌上还有实例 ----
            var (码2, _) = 跑Runner核心(目录, 实例名, "-只跑", "verify-90-*", "-超时秒", "5", "-跳过实例检查");

            var 表 = 读JSON(Path.Combine(日志目录, "run-all.json"));
            Assert.Multiple(() =>
            {
                Assert.That(码2, Is.EqualTo(0), "-跳过实例检查 之后就照跑，那条绿脚本是绿的");
                Assert.That(
                    表.GetProperty("instancesAfterRun").GetArrayLength(),
                    Is.EqualTo(1),
                    "跑完桌上还有几个实例要**在总表里看得见**（是信息，不是内部细节）");
                Assert.That(
                    表.GetProperty("instancesAfterRun")[0].GetProperty("pid").GetInt32(),
                    Is.EqualTo(假实例.Id),
                    "报出来的必须就是它，而且它的名字要是我们给的那个");
                Assert.That(
                    表.GetProperty("instancesAfterRun")[0].GetProperty("name").GetString(),
                    Is.EqualTo(实例名));
            });

            Assert.That(假实例.HasExited, Is.False, "跑完它还得活着 —— 这条不进退出码，但更不许「顺手清场」");
        }
        finally
        {
            按PID收掉(假实例);
        }
    }

    // ==================== 假实例（改了名的 cmd.exe 副本） ====================

    /// <summary>
    /// 造一个**名字由我们定**的进程，用来试 runner 的实例闸门。
    ///
    /// 为什么是「<c>cmd.exe</c> 改个名」：<c>Get-Process -Name X</c> 认的是**映像文件名**，
    /// 所以一份改名的副本就足够扮成 <c>X.exe</c>；反过来，要真弄出几个
    /// <c>MidiPerformer.exe</c>，就等于往桌上扔假实例去骗别人的验收（§5f 明确不许）。
    /// </summary>
    private Process 起假实例(string 实例名)
    {
        var 系统 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var 原版 = Path.Combine(系统, "cmd.exe");
        if (!File.Exists(原版))
        {
            Assert.Ignore($"没跑（不是通过）：找不到 {原版}，造不出一个「改了名的实例」来试这条闸门。");
        }

        var 窝 = Path.Combine(Path.GetTempPath(), $"midiperformer-fakeinst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(窝);
        待删.Add(窝);
        var 副本 = Path.Combine(窝, 实例名 + ".exe");
        File.Copy(原版, 副本);

        var psi = new ProcessStartInfo
        {
            FileName = 副本,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        // 活够久就行；输出丢给 nul，免得往这个测试宿主的控制台上溅东西。
        psi.ArgumentList.Add("ping -n 60 127.0.0.1 > nul");
        return Process.Start(psi)!;
    }

    /// <summary>按 PID 收掉**本测试自己起的**那一个进程（不是「叫这个名字的全杀」）。</summary>
    private static void 按PID收掉(Process? p)
    {
        if (p is null) { return; }
        try
        {
            if (!p.HasExited) { p.Kill(); }
            // 等它真的没了：不等的话那个 .exe 还被占着，临时目录删不掉。
            p.WaitForExit(10_000);
        }
        catch
        {
            // 收不掉不该让测试红 —— 但下一趟的 TearDown 会再试一次删目录。
        }
    }

    // ==================== fixture ====================

    private void 造一套标准脚本(string 目录)
    {
        写脚本(目录, "verify-90-绿.ps1", "Write-Host '断言 3 条全过'\nexit 0\n");
        写脚本(目录, "verify-91-红.ps1", "Write-Host '断言 2 挂了一条'\nexit 1\n");
        // 不响的门：退出码 0，一个字节都不说。
        写脚本(目录, "verify-92-不响.ps1", "exit 0\n");
        // 卡死：永远不退出 —— runner 不设上限的话就会跟着一起挂住。
        写脚本(目录, "verify-93-卡死.ps1", "Write-Host '进去了'\nwhile ($true) { Start-Sleep -Seconds 30 }\n");
        // 跑到一半抛：前半段看着像正常输出，后面的断言一条都没跑。
        写脚本(目录, "verify-94-半路抛.ps1", "Write-Host '前半验过'\nthrow '后面跑不动了'\n");

        // 闭包那节点名的成员：在位。这几份**不匹配 verify-\***，所以 runner 不该把它们当成一条来跑。
        写脚本(目录, "uitest-lib.ps1", "# 占位\n");
        写脚本(目录, "find-note.ps1", "# 占位\n");
        写脚本(目录, "probe-41-dlg.ps1", "# 占位\n");
        写脚本(Path.Combine(目录, "probe-41"), "Program.cs", "// 占位\n");
        写脚本(Path.Combine(目录, "probe-41"), "Probe41.csproj", "<Project />\n");
    }

    private void 写脚本(string 目录, string 名字, string 内容)
    {
        Directory.CreateDirectory(目录);
        File.WriteAllText(Path.Combine(目录, 名字), 内容, 无BOM的UTF8);
    }

    private void 写日志(string 日志目录, string 基名, params string[] 行)
    {
        Directory.CreateDirectory(日志目录);
        File.WriteAllText(Path.Combine(日志目录, 基名 + ".log"),
            string.Join("\n", 行) + "\n", 无BOM的UTF8);
    }

    private string 造临时目录()
    {
        var 路径 = Path.Combine(Path.GetTempPath(), $"midiperformer-runall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(路径);
        待删.Add(路径);
        return 路径;
    }

    private static readonly UTF8Encoding 无BOM的UTF8 = new(encoderShouldEmitUTF8Identifier: false);

    // ==================== 跑 runner ====================

    /// <summary>
    /// 用 <c>pwsh -NoProfile -File</c> 起 runner 本体（跟人跑它的方式一模一样）。
    ///
    /// <b>不传 <c>-ExecutionPolicy Bypass</c></b>：这个仓库的规矩是永远不传它。
    /// <c>-跳过实例检查</c> 只是别让桌面上某个人的实例把测试拦在门外 ——
    /// 这个 fixture 跑的全是临时目录里的假脚本，一条窗口都不起。
    /// </summary>
    private (int 码, string 输出) 跑Runner(string 目录, params string[] 参数)
        => 跑Runner核心(目录, "MidiPerformer", 参数);

    /// <summary>
    /// 同上，但把「它在盯哪个进程名」也交出来 —— <c>-实例名</c> 是**给这条闸门的测试用的口子**
    /// （同 <c>-目录</c>）：不传它就是默认的 <c>MidiPerformer</c>，人跑的时候不用管它。
    /// </summary>
    private (int 码, string 输出) 跑Runner核心(string 目录, string 实例名, params string[] 参数)
    {
        var runner = Path.Combine(仓库根(), "tools", "uitest", 脚本文件名);
        // 哨兵：这个测试的全部价值都押在「真跑到了那份 runner」上。文件没了就得**红**，
        // 不能悄悄退化成一条「什么都没验」的绿。
        Assert.That(File.Exists(runner), Is.True, $"找不到 runner：{runner}");

        var psi = new ProcessStartInfo
        {
            FileName = Pwsh路径(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(runner);
        psi.ArgumentList.Add("-目录");
        psi.ArgumentList.Add(目录);
        psi.ArgumentList.Add("-实例名");
        psi.ArgumentList.Add(实例名);
        foreach (var a in 参数) { psi.ArgumentList.Add(a); }

        using var 进程 = Process.Start(psi)!;
        // 两个流都要**同时**读走：只读一个、另一个的管道缓冲区满了就是死锁。
        var 出 = 进程.StandardOutput.ReadToEndAsync();
        var 错 = 进程.StandardError.ReadToEndAsync();
        Assert.That(进程.WaitForExit(120_000), Is.True, "runner 没在 120 秒内退出 —— 它自己挂住了");
        进程.WaitForExit();

        return (进程.ExitCode, 出.Result + 错.Result);
    }

    private static string Pwsh路径()
    {
        var 路径变量 = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var 目录 in 路径变量.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var 候选 = Path.Combine(目录.Trim('"'), "pwsh.exe");
            if (File.Exists(候选)) { return 候选; }
        }
        Assert.Ignore("没跑（不是通过）：PATH 上找不到 pwsh —— runner 靠它给每一条脚本起子进程。");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// 仓库根 = 从 <see cref="AppContext.BaseDirectory"/> 往上第一个放 <c>MidiPerformer.slnx</c> 的目录。
    /// 找不到就是**没跑**（<c>Assert.Ignore</c>），不是通过 —— 测试宿主换了个摆法的时候，
    /// 一条「假装验过了」的绿比红难查得多。
    /// </summary>
    private static string 仓库根()
    {
        for (var 目录 = new DirectoryInfo(AppContext.BaseDirectory); 目录 is not null; 目录 = 目录.Parent)
        {
            if (File.Exists(Path.Combine(目录.FullName, "MidiPerformer.slnx"))) { return 目录.FullName; }
        }
        Assert.Ignore($"没跑（不是通过）：从 {AppContext.BaseDirectory} 往上找不到 MidiPerformer.slnx，不知道仓库在哪。");
        throw new InvalidOperationException("unreachable");
    }

    // ==================== 读 runner 的产物 ====================

    private static JsonElement 读JSON(string 路径)
    {
        Assert.That(File.Exists(路径), Is.True, $"runner 没留下总表：{路径}");
        return JsonDocument.Parse(File.ReadAllText(路径, Encoding.UTF8)).RootElement.Clone();
    }

    private static Dictionary<string, 条目> 读条目(string 目录)
        => 条目字典(读JSON(Path.Combine(目录, "logs", "run-all.json")));

    private static Dictionary<string, 条目> 条目字典(JsonElement 表)
    {
        var 出 = new Dictionary<string, 条目>(StringComparer.Ordinal);
        foreach (var e in 表.GetProperty("entries").EnumerateArray())
        {
            var 名 = e.GetProperty("script").GetString()!;
            出[名] = new 条目(
                名,
                e.GetProperty("state").GetString()!,
                e.TryGetProperty("exitCode", out var 码) && 码.ValueKind == JsonValueKind.Number ? 码.GetInt32() : (int?)null,
                e.TryGetProperty("previousState", out var 上) ? 上.GetString() ?? string.Empty : string.Empty);
        }
        return 出;
    }

    private static List<命中项> 命中清单(JsonElement 表, string 判据名)
    {
        var 出 = new List<命中项>();
        foreach (var c in 判据(表, 判据名, 允许缺: true).Hits) { 出.Add(c); }
        return 出;
    }

    private static (string Verdict, List<命中项> Hits) 判据(JsonElement 表, string 判据名, bool 允许缺 = false)
    {
        foreach (var c in 表.GetProperty("closure").EnumerateArray())
        {
            if (c.GetProperty("check").GetString() != 判据名) { continue; }
            var 命中 = new List<命中项>();
            foreach (var h in c.GetProperty("hits").EnumerateArray())
            {
                命中.Add(new 命中项(
                    h.GetProperty("file").GetString()!,
                    h.GetProperty("line").GetInt32(),
                    h.GetProperty("kind").GetString()!));
            }
            return (c.GetProperty("verdict").GetString()!, 命中);
        }
        Assert.Fail(允许缺 ? $"总表里没有 [{判据名}] 那一节" : $"总表里没有 [{判据名}] 那一行");
        throw new InvalidOperationException("unreachable");
    }

    private sealed record 条目(string 名, string State, int? ExitCode, string PreviousState);

    private sealed record 命中项(string File, int Line, string Kind);
}
