namespace MidiPerformer.Tests.Tools;

/// <summary>
/// CRAP 分数：<c>CRAP(m) = comp(m)² × (1 − cov(m)/100)³ + comp(m)</c>。
///
/// <b>这是个纯公式，不碰文件系统、也不读 XML</b> —— 读覆盖率报告是调用方的事
/// （见 <see cref="CoberturaCrapReader"/>）。它得能被手算的三个点钉住：
/// <b>一个算错的 CRAP 报告比没有报告更坏</b> —— 它会让人去改一行本来没问题的代码。
/// </summary>
public static class CrapScore
{
    /// <summary>
    /// 门槛 30（crap4j 论文里的原线）。**全仓只有这一处**，不给第二个人机会写别的数。
    ///
    /// 一个推论：复杂度 ≤ 5 的方法，CRAP 上限正好是 30（5²×1+5）—— 也就是说
    /// <b>低复杂度的方法天然免疫这条线</b>。真正被抓住的只有「又复杂又没测」那一类，
    /// 而那正是该抓的。
    /// </summary>
    public const double Threshold = 30.0;

    /// <summary>
    /// 按圈复杂度与覆盖率算 CRAP。覆盖率是百分数（0~100）。
    /// 覆盖率 100% 时后面那一项为 0，CRAP 退化成复杂度本身。
    /// </summary>
    public static double Of(int complexity, double coveragePercent)
    {
        if (complexity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(complexity), complexity,
                "圈复杂度至少是 1。报告里读不到 complexity 时不要拿 0 顶替 —— 那会把「算不出来」悄悄算成 0 分。");
        }
        if (double.IsNaN(coveragePercent) || coveragePercent < 0 || coveragePercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coveragePercent), coveragePercent, "覆盖率是百分数，落在 0~100。");
        }

        var uncovered = 1.0 - (coveragePercent / 100.0);
        return (complexity * complexity * uncovered * uncovered * uncovered) + complexity;
    }

    /// <summary>超线判据：**严格大于**门槛才算超线（正好 30 不算）。</summary>
    public static bool IsOver(int complexity, double coveragePercent)
        => Of(complexity, coveragePercent) > Threshold;
}

/// <summary>
/// 一个方法的 CRAP 读数：从报告里读出来的几个数，加上算出来的分数。
/// </summary>
/// <param name="ClassName">形如 <c>MidiPerformer.Core.UseCases.Timeline.SongWalker</c>。</param>
/// <param name="MethodName">方法名。</param>
/// <param name="FileName">报告里那个 <c>filename</c>，相对 Core 工程根，形如 <c>UseCases\Timeline\SongWalker.cs</c>。</param>
/// <param name="FirstLine">这个方法在文件里的第一行 —— 清单上要能一步点过去，光有方法名不够。</param>
/// <param name="Complexity">报告里 &lt;method&gt; 节点自带的 complexity 属性（圈复杂度）。</param>
/// <param name="CoveredLines">&lt;line&gt; 里 hits ≥ 1 的行数。</param>
/// <param name="TotalLines">报告为这个方法列出的行数。</param>
public sealed record MethodCrap(
    string ClassName,
    string MethodName,
    string FileName,
    int FirstLine,
    int Complexity,
    int CoveredLines,
    int TotalLines)
{
    /// <summary>行覆盖率，百分数。报告没给行的方法按 0% 算（保守取值）。</summary>
    public double CoveragePercent => TotalLines == 0 ? 0.0 : 100.0 * CoveredLines / TotalLines;

    /// <summary>这个方法的 CRAP 分数。</summary>
    public double Score => CrapScore.Of(Complexity, CoveragePercent);

    /// <summary>超线了没有。</summary>
    public bool IsOver => Score > CrapScore.Threshold;

    /// <summary>
    /// 一句话把一个方法说全：**哪个方法、在哪一行、多少分、这个分是怎么来的**。
    /// 闸门红的时候，红的那条消息里就是它 —— 不点名的方法清单等于没清单。
    /// </summary>
    public string Describe()
        => $"{ClassName}.{MethodName}（{FileName}:{FirstLine}）"
           + $" —— 复杂度 {Complexity}、覆盖 {CoveragePercent:0.#}%（{CoveredLines}/{TotalLines} 行）⇒ CRAP {Score:0.##}";
}
