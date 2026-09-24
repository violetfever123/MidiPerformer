using NUnit.Framework;

namespace MidiPerformer.Tests.Tools;

/// <summary>
/// CRAP 公式自己的单测。
///
/// **三个手算点一个都不能错**：一个算错的 CRAP 报告比没有报告更坏 ——
/// 它会让人去改一行本来没问题的代码。
/// </summary>
[TestFixture]
public class CrapScoreTests
{
    [Test]
    public void 复杂度1零覆盖_CRAP是2_这是简单方法的下限()
    {
        // 1² × (1−0/100)³ + 1 = 1 + 1 = 2
        Assert.That(CrapScore.Of(1, 0), Is.EqualTo(2.0));
    }

    [Test]
    public void 复杂度5零覆盖_CRAP正好是门槛30()
    {
        // 5² × 1³ + 5 = 25 + 5 = 30 —— **正好卡在门槛上**。
        // 「复杂度 ≤ 5 的方法天然免疫这条线」那句结论就靠它，所以这里顺带把方向也钉住：
        // 超线是「大于 30」，正好 30 不算。
        Assert.Multiple(() =>
        {
            Assert.That(CrapScore.Of(5, 0), Is.EqualTo(30.0));
            Assert.That(CrapScore.IsOver(5, 0), Is.False);
        });
    }

    [Test]
    public void 复杂度10零覆盖_CRAP是110_高复杂度零覆盖会飞出去()
    {
        // 10² × 1³ + 10 = 100 + 10 = 110
        Assert.Multiple(() =>
        {
            Assert.That(CrapScore.Of(10, 0), Is.EqualTo(110.0));
            Assert.That(CrapScore.IsOver(10, 0), Is.True);
        });
    }

    [Test]
    public void 百分百覆盖时_CRAP就等于复杂度本身()
    {
        // cov = 100 时 (1 − 100/100)³ = 0，后面那一整项消失。
        Assert.Multiple(() =>
        {
            Assert.That(CrapScore.Of(1, 100), Is.EqualTo(1.0));
            Assert.That(CrapScore.Of(7, 100), Is.EqualTo(7.0));
            Assert.That(CrapScore.Of(62, 100), Is.EqualTo(62.0), "再复杂的方法，测透了也是它自己");
        });
    }

    [Test]
    public void 覆盖率按命中行数算_半覆盖的4复杂度是6分()
    {
        // 4² × (1−50/100)³ + 4 = 16 × 0.125 + 4 = 2 + 4 = 6
        var 方法 = new MethodCrap(
            ClassName: "某类", MethodName: "半覆盖", FileName: "某类.cs", FirstLine: 3,
            Complexity: 4, CoveredLines: 1, TotalLines: 2);

        Assert.Multiple(() =>
        {
            Assert.That(方法.CoveragePercent, Is.EqualTo(50.0));
            Assert.That(方法.Score, Is.EqualTo(6.0));
            Assert.That(方法.IsOver, Is.False);
        });
    }

    [Test]
    public void 参数不合法直接抛_不拿0顶替()
    {
        // 这条防的是「读不到复杂度就当 0」：那样整份报告会静默地全变绿。
        Assert.Multiple(() =>
        {
            Assert.That(() => CrapScore.Of(0, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => CrapScore.Of(3, -1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => CrapScore.Of(3, 101), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => CrapScore.Of(3, double.NaN), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}
