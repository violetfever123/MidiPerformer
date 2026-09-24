using System.Xml.Linq;

namespace MidiPerformer.Tests.Tools;

/// <summary>
/// 从 cobertura 报告里读圈复杂度与行覆盖率，凑成 <see cref="MethodCrap"/>。
///
/// <b>关键：不用再引第二个工具。</b> cobertura 的每个 &lt;method&gt; 节点自带 complexity 属性，
/// 行上有 hits —— **复杂度和覆盖率在同一份报告里**，所以这一层只是「读 XML、算数」，
/// 不需要再挂一个圈复杂度计算器。（这也是 63 号票要求「亲眼在文件里看到 complexity 属性」的原因。）
/// </summary>
public static class CoberturaCrapReader
{
    /// <summary>
    /// CRAP 只算功能层。跟覆盖率采集的范围是同一个（<c>coverlet.runsettings</c> 的
    /// <c>Include=[MidiPerformer.Core]*</c>），也跟 63 / 65 同一范围。
    ///
    /// <b>明确不测 App / Adapters</b>：Avalonia 视图与 Gateway（SendInput、钩子、winmm、权限自检）
    /// 是 <c>docs/spec-演奏器.md</c> 的「各模块测什么」里写明「明确不测」的那两层 ——
    /// 缝开在它们上面，就是为了让这两层尽可能薄。在那两层上要覆盖率，只会逼出一堆
    /// 「测了等于没测」的断言，然后把报告淹掉，然后没人看报告。
    /// </summary>
    public const string ScopePrefix = "MidiPerformer.Core.";

    /// <summary>告诉闸门「报告在哪」的环境变量，由 <c>tools/crap.ps1</c> 设置。</summary>
    public const string ReportPathVariable = "MIDIPERFORMER_CRAP_REPORT";

    /// <summary>报告里所有方法（不筛范围 —— 筛范围是调用方的口径，见 <see cref="ScopePrefix"/>）。</summary>
    public static IReadOnlyList<MethodCrap> Parse(XDocument report)
    {
        var result = new List<MethodCrap>();
        foreach (var cls in report.Descendants("class"))
        {
            var className = (string?)cls.Attribute("name") ?? "";
            var fileName = (string?)cls.Attribute("filename") ?? "";
            foreach (var m in cls.Element("methods")?.Elements("method") ?? Enumerable.Empty<XElement>())
            {
                if (m.Attribute("complexity") is not { } complexity)
                {
                    // 少这一样就没得算。宁可炸在这儿，也不要拿 0 顶替 —— 那会让整份报告静默地全变绿。
                    throw new InvalidDataException(
                        $"{className}.{(string?)m.Attribute("name")} 上没有 complexity 属性：" +
                        "这份报告不是 coverlet 出的 cobertura，算不了 CRAP。");
                }

                var lines = m.Element("lines")?.Elements("line").ToList() ?? new List<XElement>();
                result.Add(new MethodCrap(
                    className,
                    (string?)m.Attribute("name") ?? "",
                    fileName,
                    lines.Count == 0 ? 0 : lines.Min(l => (int?)l.Attribute("number") ?? 0),
                    (int)complexity,
                    lines.Count(l => (int?)l.Attribute("hits") >= 1),
                    lines.Count));
            }
        }
        return result;
    }

    /// <summary>读一份 cobertura 报告文件。</summary>
    public static IReadOnlyList<MethodCrap> ReadFile(string path) => Parse(XDocument.Load(path));

    /// <summary>
    /// 一个目录（含子目录）里最新的一份 <c>coverage.cobertura.xml</c>；没有就是 null。
    /// 报告目录里同一次跑会留多份（VSTest 那层 &lt;guid&gt; 不是我们选的），所以要挑最新的。
    /// </summary>
    public static string? NewestReportUnder(string directory)
    {
        if (!Directory.Exists(directory)) { return null; }

        return Directory.EnumerateFiles(directory, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// 把「在哪读报告」这件事从 <paramref name="hint"/>（就是那个环境变量）解出来：
    /// 给的是文件就用它，给的是目录就取里面最新的一份，什么都没给（或给了个空位置）就是 null。
    ///
    /// <b>注意这里没有「回退」</b>：给了位置却读不到报告，不会转头去翻仓库里上一轮跑剩的报告。
    /// 那正是最坏的一种假绿 —— 拿一份旧报告给这次改动盖章。读不到就是读不到，闸门按「没跑」处理。
    ///
    /// <b>也不自动去 <c>MidiPerformer.Tests\TestResults</c> 里找。</b> 那个目录会把历次跑过的报告都留着，
    /// 随手挑一份最新的，等于让「又一次普通的 dotnet test」在**别人的**报告上出红出绿。
    /// 所以：这次跑的是哪份报告，得有人明确告诉闸门 —— 说这件事的就是 <c>tools/crap.ps1</c>。
    /// </summary>
    public static string? ResolveReportPath(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) { return null; }
        if (File.Exists(hint)) { return hint; }
        return NewestReportUnder(hint);
    }
}
