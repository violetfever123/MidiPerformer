using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using NUnit.Framework;

namespace MidiPerformer.Tests.Analysis;

/// <summary>
/// 单声部判定 —— 这条轨游戏里的口琴弹不弹得了。
/// 判据是「起音是否同时」，不是「时值是否重叠」；容差的单位是秒，不是 tick。
/// </summary>
public class MonophonyCheckTests
{
    /// <summary>
    /// 1 tick = 0.1ms 的表：PPQ 5000、120 BPM。
    /// 挑这个分辨率是为了让 1 tick 是 0.1ms 的整数倍，边界用例才摆得出 29.9 / 30.0 / 30.1ms。
    /// </summary>
    private static TempoMap Tempo() => new(
        TimeDivision.PulsesPerQuarter(5000),
        new[] { new TempoChange(0, 500_000) });

    private static Track TrackOf(params Note[] notes) =>
        new(0, 0, "旋律", 24, notes);

    /// <summary>连奏：前音还在响、后音才起 —— 时值重叠，但不算多声部。</summary>
    [Test]
    public void 连奏旋律不算多声部()
    {
        var map = Tempo();

        // 每个音 400ms 长，下一个音 40ms 处就起：时值整整三度重叠。
        var track = TrackOf(
            new Note(60, 0, 4000, 100),
            new Note(62, 400, 4000, 100),
            new Note(64, 800, 4000, 100),
            new Note(65, 1200, 4000, 100));

        Assert.Multiple(() =>
        {
            // 前提：这条轨确实时值重叠，音符改短之后下面的断言就不说明问题了。
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

            // 先撞上的是相邻两个，间隔为 0：界面/诊断拿这两个下标说话
            Assert.That(collision!.Value.EarlierNoteIndex, Is.EqualTo(0));
            Assert.That(collision.Value.LaterNoteIndex, Is.EqualTo(1));
            Assert.That(collision.Value.GapSeconds, Is.EqualTo(0.0).Within(1e-12));
        });
    }

    /// <summary>错开一点点起音仍然算撞：谱面上的同刻和弦总是差几毫秒。</summary>
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

    /// <summary>边界两侧：29.9ms 与 30.0ms 算撞，30.1ms 不算（判的是 ≤）。</summary>
    [TestCase(299, 0.0299, false)]     // 29.9ms：撞
    [TestCase(300, 0.0300, false)]     // 30.0ms：撞（含等号）
    [TestCase(301, 0.0301, true)]      // 30.1ms：不撞
    public void 容差两侧的判定(int gapTicks, double gapSeconds, bool expectedMonophonic)
    {
        var map = Tempo();
        var track = TrackOf(new Note(60, 0, 4000, 100), new Note(62, gapTicks, 4000, 100));

        Assert.Multiple(() =>
        {
            // 前提：tick 间隔真的等于那个秒数。
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

    /// <summary>
    /// 变速曲目：同一对 tick 间隔，在快段算撞、在慢段不算 —— 判的是秒，不是 tick。
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

    /// <summary>空轨和单音轨没有第二个起音。</summary>
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
