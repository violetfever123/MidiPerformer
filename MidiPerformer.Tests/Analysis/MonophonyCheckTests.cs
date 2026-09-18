using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using NUnit.Framework;

namespace MidiPerformer.Tests.Analysis;

/// <summary>
/// 单声部判定 —— 「这条轨游戏里的口琴弹不弹得了」。
///
/// 这一条测试真正守的是一个**方向**：判据必须是「起音是否同时」，不是「时值是否重叠」。
/// 两者在这批用例里被刻意摆成相反的答案（连奏那条：时值重叠、起音不同时），
/// 所以哪一天有人把判据换成时值重叠，这里立刻红 —— 而不是等到用户发现主旋律不见了。
///
/// 第二件事是**单位**：容差是 30ms 的**秒**，不是某个 tick 数。最后一条用例把同一对
/// tick 间隔摆在快慢两段上，得到一个撞、一个不撞 —— 换成 tick 判就两边一样了。
/// </summary>
public class MonophonyCheckTests
{
    /// <summary>
    /// 1 tick = 0.1ms 的表：PPQ 5000、120 BPM（500000 微秒/四分音符 → 500µs / 5000 = 100µs）。
    ///
    /// 为什么挑这么个不常见的分辨率：容差是 30ms，边界用例要正好落在 29.9 / 30.0 / 30.1 上，
    /// 那就得让 1 tick 是 0.1ms 的整数倍 —— PPQ 480 的 1 tick 是 1.04ms，根本摆不出 30.1。
    /// 分辨率不影响判定本身（判的是秒），这里只是把边界摆得干净。
    /// </summary>
    private static TempoMap Tempo() => new(
        TimeDivision.PulsesPerQuarter(5000),
        new[] { new TempoChange(0, 500_000) });

    private static Track TrackOf(params Note[] notes) =>
        new(0, 0, "旋律", 24, notes);

    // ==================== 判据是「起音是否同时」 ====================

    /// <summary>
    /// 连奏：前音还在响、后音才起。**时值重叠，但不算多声部。**
    ///
    /// 这是整块代码存在的理由。判错的代价是把用户真正想弹的那条旋律藏起来，
    /// 而且他不知道为什么 —— 所以这条最要紧。
    /// </summary>
    [Test]
    public void 连奏旋律不算多声部()
    {
        var map = Tempo();

        // 每个音 400ms 长，下一个音在 40ms 处就起 —— 整整三度重叠。
        var track = TrackOf(
            new Note(60, 0, 4000, 100),
            new Note(62, 400, 4000, 100),
            new Note(64, 800, 4000, 100),
            new Note(65, 1200, 4000, 100));

        Assert.Multiple(() =>
        {
            // 前提：这条轨**确实**是时值重叠的。不先钉这一条，哪天音符改成不重叠了，
            // 下面那个断言就变成在测一件不相干的事。
            Assert.That(track.Notes[0].EndTick, Is.GreaterThan(track.Notes[1].StartTick),
                "这条用例的前提是「时值重叠」，音符改短之后就不说明问题了");

            Assert.That(MonophonyCheck.FindCollision(track, map), Is.Null,
                "连奏被判成多声部 —— 这正是「用时值重叠当判据」会犯的错");
            Assert.That(MonophonyCheck.IsMonophonic(track, map), Is.True);
        });
    }

    /// <summary>同一瞬间两个音 = 和声。口琴只能响一个，发出去必然漏掉一个。</summary>
    [Test]
    public void 同刻双音算多声部()
    {
        var map = Tempo();
        var track = TrackOf(
            new Note(60, 480, 480, 100),
            new Note(64, 480, 480, 100),
            new Note(67, 480, 480, 100));

        var collision = MonophonyCheck.FindCollision(track, map);

        Assert.Multiple(() =>
        {
            Assert.That(MonophonyCheck.IsMonophonic(track, map), Is.False);
            Assert.That(collision, Is.Not.Null, "三个音全在同一 tick 上，一处撞都没有说明扫描漏了");

            // 先撞上的那一对是相邻的两个，且间隔为 0 —— 界面/诊断拿这两个下标说话
            Assert.That(collision!.Value.EarlierNoteIndex, Is.EqualTo(0));
            Assert.That(collision.Value.LaterNoteIndex, Is.EqualTo(1));
            Assert.That(collision.Value.GapSeconds, Is.EqualTo(0.0).Within(1e-12));
        });
    }

    /// <summary>错开一点点起音仍然是撞：人手弹不出「同时」，同刻和弦在谱面上总是差几毫秒。</summary>
    [Test]
    public void 相隔一毫秒的两个起音也算多声部()
    {
        var map = Tempo();
        var track = TrackOf(new Note(60, 0, 4000, 100), new Note(64, 10, 4000, 100));

        Assert.Multiple(() =>
        {
            Assert.That(map.SecondsAt(10) - map.SecondsAt(0), Is.EqualTo(0.001).Within(1e-12));
            Assert.That(MonophonyCheck.IsMonophonic(track, map), Is.False);
        });
    }

    // ==================== ±30ms 边界 ====================

    /// <summary>
    /// 边界两侧：29.9ms 与 30.0ms 算撞，30.1ms 不算。
    ///
    /// 30.0 含在容差里（判的是 <c>≤</c>）：刚好卡在 30ms 上的两个音，游戏那一帧多半也只读得到
    /// 一个 —— 当成能弹就是把漏音放出去。边界往严的一侧倒。
    /// </summary>
    [TestCase(299, 0.0299, false)]     // 29.9ms：撞
    [TestCase(300, 0.0300, false)]     // 30.0ms：撞（含等号）
    [TestCase(301, 0.0301, true)]      // 30.1ms：不撞
    public void 容差两侧的判定(int gapTicks, double gapSeconds, bool expectedMonophonic)
    {
        var map = Tempo();
        var track = TrackOf(new Note(60, 0, 4000, 100), new Note(62, gapTicks, 4000, 100));

        Assert.Multiple(() =>
        {
            // 前提：tick 间隔真的等于那个秒数。表被改过的话，这条用例就不再说明边界了。
            Assert.That(map.SecondsAt(gapTicks) - map.SecondsAt(0),
                Is.EqualTo(gapSeconds).Within(1e-12), "1 tick = 0.1ms 这个前提不成立了");

            Assert.That(MonophonyCheck.IsMonophonic(track, map), Is.EqualTo(expectedMonophonic),
                $"间隔 {gapSeconds * 1000:0.0}ms 的判定不符（容差 {MonophonyCheck.SimultaneityToleranceSeconds * 1000:0.#}ms）");

            if (!expectedMonophonic)
            {
                var collision = MonophonyCheck.FindCollision(track, map);
                Assert.That(collision!.Value.GapSeconds, Is.EqualTo(gapSeconds).Within(1e-12),
                    "报出来的间隔要和实际间隔一致，诊断信息才可信");
            }
        });
    }

    // ==================== 阈值是秒，不是 tick ====================

    /// <summary>
    /// 变速曲目：**同一对 tick 间隔，在快段算撞、在慢段不算。**
    ///
    /// 这就是「阈值是秒」的可观察后果 —— 判的是耳朵听到的间隔。换成按 tick 判，
    /// 一首变速曲子里同一个 tick 距离会得到同一个答案，慢的那段漏音、快的那段误杀。
    ///
    /// （工单里写的是「慢速段算撞、快速段不算」，方向反了：慢段每个 tick 占的秒数更多，
    /// 同样的 tick 间隔只会**更**不容易撞。这里按实现的真实语义钉。）
    /// </summary>
    [Test]
    public void 变速曲目上同一对tick间隔的判定随段落翻转()
    {
        // 前 8000 tick 是 120BPM（1 tick = 0.5ms），之后换成 30BPM（1 tick = 2ms）。
        var map = new TempoMap(
            TimeDivision.PulsesPerQuarter(1000),
            new[]
            {
                new TempoChange(0, 500_000),
                new TempoChange(8000, 2_000_000),
            });

        Assert.That(map.TempoChanges, Has.Count.EqualTo(1),
            "只有一条变速事件（tick 0 上那条默认速度会被去掉）—— 表不是变速表，这条用例就没意义了");

        // 同一对 tick 间隔：相邻两个起音差 20 tick。
        var fast = TrackOf(new Note(60, 0, 500, 100), new Note(62, 20, 500, 100));
        var slow = TrackOf(new Note(60, 8000, 500, 100), new Note(62, 8020, 500, 100));

        Assert.Multiple(() =>
        {
            Assert.That(map.SecondsAt(20) - map.SecondsAt(0), Is.EqualTo(0.010).Within(1e-12),
                "快段 20 tick 应该是 10ms");
            Assert.That(map.SecondsAt(8020) - map.SecondsAt(8000), Is.EqualTo(0.040).Within(1e-12),
                "慢段同样 20 tick 应该是 40ms");

            Assert.That(MonophonyCheck.IsMonophonic(fast, map), Is.False,
                "快段 10ms 的两个起音是撞的");
            Assert.That(MonophonyCheck.IsMonophonic(slow, map), Is.True,
                "慢段 40ms 的两个起音不撞 —— 这一条按 tick 判就会错");
        });
    }

    // ==================== 退化的轨 ====================

    /// <summary>空轨和单音轨没有「第二个起音」，谈不上撞。</summary>
    [TestCase(0)]
    [TestCase(1)]
    public void 音数不足两个的轨是单声部(int noteCount)
    {
        var map = Tempo();
        var notes = Enumerable.Range(0, noteCount).Select(i => new Note(60 + i, i * 480, 480, 100));
        var track = TrackOf(notes.ToArray());

        Assert.That(MonophonyCheck.IsMonophonic(track, map), Is.True);
    }
}
