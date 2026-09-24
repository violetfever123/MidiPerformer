using System.Text;
using System.Xml.Linq;
using NUnit.Framework;

namespace MidiPerformer.Tests.Tools;

/// <summary>
/// CRAP 闸门：**从覆盖率报告算出每个方法的 CRAP，超过 30 就红**。
///
/// 它是一条 NUnit 测试（<c>Category=Crap</c>）而不是一个报表脚本 —— 因为这个仓库的闸门
/// 就是 `dotnet test`，放进来的它才跟其他那些断言是同一个东西。
///
/// <b>但门槛不设成全局分数闸门，闸门下沉到工单。</b> 「覆盖率 ≥ 80% 否则失败」这种线，
/// 在一个零变异基线的仓库里第一次跑必然是红的 —— 然后所有人学会无视它，
/// <b>而一个红着的闸门比没有闸门更坏：它把「红」训练成了正常状态</b>。
/// 所以这里提供的是**口径与工具**（30 这条线、CRAP 怎么算、报告怎么读），
/// 由每张工单自己扛「杀掉它改动的那些」的义务；编排交给 <c>tools/crap.ps1</c>。
///
/// <b>没有覆盖率报告时它 <c>Assert.Ignore</c>，不是「假装通过」</b> ——
/// 「没跑」和「过了」必须看得出区别。而「没跑被当成过了」是看不见的，
/// 所以那条分界单独有一个测试盯着（喂一个不存在的报告路径）。
/// </summary>
[TestFixture]
public class CrapGateTests
{
    /// <summary>
    /// 闸门本体。报告从 <c>tools/crap.ps1</c> 设的环境变量里拿 ——
    /// 这次跑的是哪份报告得有人明确告诉它，见 <see cref="CoberturaCrapReader.ResolveReportPath"/>。
    /// </summary>
    [Test, Category("Crap")]
    public void 功能层每个方法的CRAP都不超过门槛()
    {
        var 提示 = Environment.GetEnvironmentVariable(CoberturaCrapReader.ReportPathVariable);
        var 报告 = CoberturaCrapReader.ResolveReportPath(提示);

        if (报告 is null)
        {
            // 先把原因打在控制台上：跳过的测试在汇总里只留一个数字，看不出为什么。
            TestContext.Progress.WriteLine(没跑的原因(提示));
            Assert.Ignore(没跑的原因(提示));
            // Assert.Ignore 抛 IgnoreException，下面那行不会执行。
        }

        RunGate(报告);
    }

    /// <summary>
    /// 闸门的全部逻辑。
    ///
    /// 它是 public static 只为一件事：**「没跑被当成过了」那一条测试要走同一段代码**。
    /// 要是测试另测一份实现，那它就测不到真正的闸门 —— 而那条分界恰恰是看不见的那种错。
    /// </summary>
    public static void RunGate(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            Assert.Ignore($"没跑（不是通过）：这份覆盖率报告不存在：{reportPath}。" + 跑法());
        }

        var 全部 = CoberturaCrapReader.ReadFile(reportPath);
        var 功能层 = 全部
            .Where(m => m.ClassName.StartsWith(CoberturaCrapReader.ScopePrefix, StringComparison.Ordinal))
            .ToList();

        if (功能层.Count == 0)
        {
            // 报告在、里面一个功能层方法都没有 —— 那是报告不对，不是「全绿」。
            Assert.Fail(
                $"报告里一个 {CoberturaCrapReader.ScopePrefix}* 的方法都没有：{reportPath}。"
                + "报告不对就别当它通过 —— 换一份再跑。");
        }

        // 断言在**清单文本**上，不在记录对象上 —— 这样红的那条消息（以及断言框架自己补的
        // "But was"）读起来都是同一份人话，不会甩出一串带泛型签名的记录转储。
        var 超线 = 功能层
            .Where(m => m.IsOver)
            .OrderByDescending(m => m.Score)
            .Select(m => m.Describe())
            .ToList();

        TestContext.Progress.WriteLine(
            $"CRAP：算了 {功能层.Count} 个方法（范围 {CoberturaCrapReader.ScopePrefix}*），超线 {超线.Count} 个，门槛 {CrapScore.Threshold:0}。");

        Assert.That(超线, Is.Empty, 超线清单(超线, 功能层.Count));
    }

    private static string 超线清单(IReadOnlyList<string> 超线, int 总数)
    {
        var 文本 = new StringBuilder();
        文本.Append($"CRAP 超过 {CrapScore.Threshold:0} 的方法有 {超线.Count} 个（算了 {总数} 个，"
                    + "又复杂又没测的分数最高，得补测试或者拆函数）：");
        foreach (var m in 超线) { 文本.Append(Environment.NewLine).Append("  ").Append(m); }
        return 文本.ToString();
    }

    private static string 没跑的原因(string? 提示)
        => string.IsNullOrWhiteSpace(提示)
            ? $"没跑（不是通过）：环境变量 {CoberturaCrapReader.ReportPathVariable} 没给，"
              + "闸门不猜该读哪份报告。" + 跑法()
            : $"没跑（不是通过）：环境变量 {CoberturaCrapReader.ReportPathVariable} 指的位置读不到覆盖率报告（{提示}）。"
              + 跑法();

    private static string 跑法()
        => " 跑 pwsh -File tools/crap.ps1 —— 它先 --collect 出一份报告，再回来跑这条闸门。";

    // ==================== 「没跑」和「过了」的分界 ====================

    [Test]
    public void 没有覆盖率报告时是Ignore而不是通过()
    {
        var 不存在 = Path.Combine(Path.GetTempPath(), $"midiperformer-crap-没有这份-{Guid.NewGuid():N}.xml");

        var 异常 = Assert.Throws<IgnoreException>(() => RunGate(不存在));

        // Assert.Ignore 抛的是 IgnoreException：NUnit 只会把这条记成「跳过」，
        // **永远不会记成「通过」**。这就是「没跑」和「过了」分得开的地方。
        // 这条测试盯着的正是那个看不见的分界：跑起来全绿、其实什么都没验。
        Assert.That(异常!.Message, Does.Contain(不存在), "没跑的原因里得写清是哪份报告没找到");
    }

    [Test]
    public void 环境变量指到哪就读哪_指了却没有就是没跑_不去翻上一轮剩下的报告()
    {
        var 空目录 = 造临时目录();

        Assert.Multiple(() =>
        {
            Assert.That(CoberturaCrapReader.ResolveReportPath(null), Is.Null, "没给就是没跑");
            Assert.That(CoberturaCrapReader.ResolveReportPath("   "), Is.Null);
            Assert.That(CoberturaCrapReader.ResolveReportPath(空目录), Is.Null, "给的是空目录也是没跑");
            Assert.That(
                CoberturaCrapReader.ResolveReportPath(Path.Combine(空目录, "没有这份.xml")),
                Is.Null,
                "给了位置却读不到，不许转头拿一份旧报告来盖章");
        });
    }

    [Test]
    public void 环境变量给目录时_取里面最新的一份报告()
    {
        var 目录 = 造临时目录();
        var 旧 = 造报告文件(Path.Combine(目录, "旧"), "旧");
        var 新 = 造报告文件(Path.Combine(目录, "新"), "新");
        File.SetLastWriteTimeUtc(旧, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(新, DateTime.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(CoberturaCrapReader.ResolveReportPath(旧), Is.EqualTo(旧), "给的是文件就直接用它");
            Assert.That(CoberturaCrapReader.ResolveReportPath(目录), Is.EqualTo(新), "给的是目录就取最新的");
        });
    }

    // ==================== 红起来长什么样 ====================

    [Test]
    public void 报告里有超线方法时闸门红_并点名是哪个方法多少分()
    {
        var 报告 = 写临时报告(造报告(造类("MidiPerformer.Core.某类",
            ("又复杂又没测", 10, new[] { 0, 0 }),   // 10² × 1³ + 10 = 110
            ("正好卡线", 5, new[] { 0 }),           //  5² × 1³ +  5 =  30 —— 正好卡线，不该被点
            ("没事", 2, new[] { 1 }))));            //  全覆盖，2

        var 异常 = Assert.Throws<AssertionException>(() => RunGate(报告));

        Assert.Multiple(() =>
        {
            Assert.That(异常!.Message, Does.Contain("某类.又复杂又没测"), "红的那条要说得出是哪个方法");
            Assert.That(异常.Message, Does.Contain("110"), "……还要说得出它多少分");
            Assert.That(异常.Message, Does.Not.Contain("正好卡线"), "正好 30 分不算超线，不该被点出来");
        });
    }

    [Test]
    public void 范围只算功能层_别层的超线方法不让闸门红()
    {
        var 报告 = 写临时报告(造报告(
            造类("MidiPerformer.Adapters.网关", ("发个键", 20, new[] { 0 })),   // 420 分，可它在 Core 之外
            造类("MidiPerformer.Core.某类", ("正常", 3, new[] { 1 }))));      // Core 里，全覆盖

        Assert.That(() => RunGate(报告), Throws.Nothing);
    }

    [Test]
    public void 报告里读不到复杂度就说算不了_不拿0顶替()
    {
        // 一份形状对、但 <method> 上没有 complexity 属性的报告 —— 那不是 coverlet 出的 cobertura。
        var 报告 = 写临时报告(string.Join(Environment.NewLine, new[]
        {
            """<?xml version="1.0" encoding="utf-8"?>""",
            """<coverage line-rate="0" branch-rate="0" version="1.9" timestamp="0">""",
            """  <packages><package name="MidiPerformer.Core" line-rate="0" branch-rate="0">""",
            """    <classes><class name="MidiPerformer.Core.某类" filename="某类.cs" line-rate="0" branch-rate="0">""",
            """      <methods><method name="没复杂度" signature="()">""",
            """        <lines><line number="1" hits="1" branch="False" /></lines>""",
            """      </method></methods>""",
            """    </class></classes>""",
            """  </package></packages>""",
            """</coverage>""",
        }));

        Assert.That(() => CoberturaCrapReader.ReadFile(报告), Throws.TypeOf<InvalidDataException>());
    }

    // ==================== 报告读得对不对 ====================

    [Test]
    public void 报告里的复杂度和命中行被读到对的位置上()
    {
        var 报告 = 写临时报告(造报告(造类("MidiPerformer.Core.某类",
            ("又复杂又没测", 10, new[] { 0, 0 }),
            ("半覆盖", 4, new[] { 1, 0 }))));

        var 方法 = CoberturaCrapReader.ReadFile(报告);

        Assert.Multiple(() =>
        {
            var 复杂 = 方法.Single(m => m.MethodName == "又复杂又没测");
            Assert.That(复杂.Complexity, Is.EqualTo(10), "复杂度取自 <method> 的 complexity 属性");
            Assert.That(复杂.TotalLines, Is.EqualTo(2));
            Assert.That(复杂.CoveredLines, Is.EqualTo(0));
            Assert.That(复杂.FirstLine, Is.EqualTo(1), "行号取自 <line> 的 number，清单上要能点过去");
            Assert.That(复杂.FileName, Is.EqualTo("MidiPerformer.Core.某类.cs"), "文件名取自 <class> 的 filename");
            Assert.That(复杂.Score, Is.EqualTo(110.0));
            Assert.That(复杂.Describe(), Does.Contain(":1"), "清单里得有行号");

            var 半 = 方法.Single(m => m.MethodName == "半覆盖");
            Assert.That(半.CoveragePercent, Is.EqualTo(50.0), "覆盖率按 hits ≥ 1 的行数算");
            Assert.That(半.Score, Is.EqualTo(6.0));
        });
    }

    // ==================== 造报告 / 收拾临时文件 ====================

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
                // 临时文件删不掉不该让测试红。
            }
        }
        待删.Clear();
    }

    /// <summary>造一份最小可用的 cobertura 报告：只留 CRAP 要用的那两样（complexity 属性、line 上的 hits）。</summary>
    private static string 造报告(params string[] 类块)
        => string.Join(Environment.NewLine, new[]
        {
            """<?xml version="1.0" encoding="utf-8"?>""",
            """<coverage line-rate="0" branch-rate="0" version="1.9" timestamp="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0">""",
            """  <sources><source>C:\某处\MidiPerformer.Core\</source></sources>""",
            """  <packages><package name="MidiPerformer.Core" line-rate="0" branch-rate="0" complexity="0"><classes>""",
            string.Join(Environment.NewLine, 类块),
            """  </classes></package></packages>""",
            """</coverage>""",
        });

    /// <summary><paramref name="方法"/> 里每一项是「方法名、复杂度、每一行的 hits」，行号按顺序生成。</summary>
    private static string 造类(string 类名, params (string 名字, int 复杂度, int[] 命中)[] 方法)
    {
        var 文本 = new StringBuilder();
        文本.Append($"""    <class name="{类名}" filename="{类名}.cs" line-rate="0" branch-rate="0" complexity="0">""").AppendLine();
        文本.AppendLine("      <methods>");
        var 行号 = 1;
        foreach (var (名字, 复杂度, 命中) in 方法)
        {
            文本.Append($"""        <method name="{名字}" signature="()" line-rate="0" branch-rate="0" complexity="{复杂度}">""").AppendLine();
            文本.AppendLine("          <lines>");
            foreach (var h in 命中)
            {
                文本.Append($"""            <line number="{行号}" hits="{h}" branch="False" />""").AppendLine();
                行号++;
            }
            文本.AppendLine("          </lines>");
            文本.AppendLine("        </method>");
        }
        文本.AppendLine("      </methods>");
        return 文本.AppendLine("    </class>").ToString().TrimEnd();
    }

    private string 写临时报告(string 内容)
    {
        var 路径 = Path.Combine(Path.GetTempPath(), $"midiperformer-crap-{Guid.NewGuid():N}.xml");
        File.WriteAllText(路径, 内容, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        待删.Add(路径);
        return 路径;
    }

    private string 造临时目录()
    {
        var 路径 = Path.Combine(Path.GetTempPath(), $"midiperformer-crap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(路径);
        待删.Add(路径);
        return 路径;
    }

    private string 造报告文件(string 目录, string 内容)
    {
        Directory.CreateDirectory(目录);
        var 路径 = Path.Combine(目录, "coverage.cobertura.xml");
        File.WriteAllText(路径, 内容);
        待删.Add(目录);
        return 路径;
    }
}
