using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using NUnit.Framework;

namespace MidiPerformer.Tests.Analysis;

/// <summary>
/// 演奏轨排序 —— 「能弹的轨里，哪条最像主旋律」。
///
/// 这一条测试守两件事：
/// <list type="number">
/// <item><b>能不能弹是布尔的事，与分数无关。</b> 打击乐和多声部轨一律不出现，
///   哪怕它们音符更多、覆盖更全 —— 分数只用来排顺序，不能把一条弹不了的轨捞回来。</item>
/// <item><b>顺序是确定的。</b> 同分时按原曲下标排。默认选中的那条不能因为一次排序的
///   不稳定性而变来变去 —— 用户按下开始之前看到的必须是同一条。</item>
/// </list>
/// </summary>
public class TrackRankingTests
{
    /// <summary>1 tick = 0.1ms，见 MonophonyCheckTests 里同一个表的说明。</summary>
    private static TempoMap Tempo() => new(
        TimeDivision.PulsesPerQuarter(5000),
        new[] { new TempoChange(0, 500_000) });

    /// <summary>一条往上爬的单声部旋律。16 个音、每个 400ms，够长到不被「太碎」减分。</summary>
    private static Note[] Melody(int count = 16, long stepTicks = 4000) =>
        Enumerable.Range(0, count)
            .Select(i => new Note(60 + i % 12, i * stepTicks, stepTicks, 100))
            .ToArray();

    /// <summary>有和声的轨：每个起音上都叠一个三度。同一 tick 两个音 = 多声部。</summary>
    private static Note[] Chords(int count = 16, long stepTicks = 4000) =>
        Enumerable.Range(0, count)
            .SelectMany(i => new[]
            {
                new Note(60 + i % 12, i * stepTicks, stepTicks, 100),
                new Note(64 + i % 12, i * stepTicks, stepTicks, 100),
            })
            .ToArray();

    private static Track TrackOf(int index, int channel, string name, params Note[] notes) =>
        new(index, channel, name, 24, notes);

    private static Song SongOf(params Track[] tracks) => new(tracks, Tempo());

    // ==================== 只列能弹的 ====================

    /// <summary>
    /// 一首「四轨两弹」的曲子：主旋律 + 伴奏能弹，鼓轨（声道 9）和一条有和声的轨不能。
    ///
    /// 这也正是界面上那句提示的来源：<c>N 条轨里 M 条可演奏</c> —— 两个数一个取自全表
    /// （<c>song.Tracks.Count</c>）、一个取自这个结果，不另算一遍。
    /// </summary>
    [Test]
    public void 只列能弹的轨_打击乐与多声部都不在()
    {
        var melody = TrackOf(0, 0, "主旋律", Melody());
        var bass = TrackOf(1, 1, "低音线", Melody(12));
        var drums = TrackOf(2, TrackRanking.PercussionChannel, "鼓", Melody(24));
        var chords = TrackOf(3, 2, "和弦铺底", Chords());

        var song = SongOf(melody, bass, drums, chords);
        var ranked = TrackRanking.Of(song);

        Assert.Multiple(() =>
        {
            Assert.That(ranked.Select(r => r.SongTrackIndex), Is.EquivalentTo(new[] { 0, 1 }),
                "能弹的只有主旋律和低音线：鼓（声道 9）和有和声的那条都不该出现");

            Assert.That(ranked.Select(r => r.Track), Does.Not.Contain(drums), "打击乐轨不该出现在下拉框里");
            Assert.That(ranked.Select(r => r.Track), Does.Not.Contain(chords), "多声部轨不该出现在下拉框里");

            // 反过来说：能弹的一条都不能漏。漏掉才是真正的坏结果（用户想弹的轨不见了）
            Assert.That(ranked.Select(r => r.Track), Does.Contain(melody));
            Assert.That(ranked.Select(r => r.Track), Does.Contain(bass));

            // 下标要指回原曲：界面拿它去 song.Tracks[...] 取轨，错一位就弹错轨
            foreach (var r in ranked)
                Assert.That(song.Tracks[r.SongTrackIndex], Is.SameAs(r.Track));
        });
    }

    /// <summary>一条音的轨都不成轨（见 <see cref="Track"/> 的说明），不该占着下拉框的一个位置。</summary>
    [Test]
    public void 没有音符的轨不出现()
    {
        var song = SongOf(TrackOf(0, 0, "空轨"), TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1 }));
    }

    /// <summary>一首什么都没有的曲子：结果空，不抛。界面靠这个走「一条都弹不了」那条路。</summary>
    [Test]
    public void 空曲返回空表()
    {
        Assert.That(TrackRanking.Of(SongOf()), Is.Empty);
    }

    // ==================== 排序 ====================

    /// <summary>轨名是唯一一条「人写进去的意图」线索，权重最高：名字说主旋律的排在名字说伴奏的前面。</summary>
    [Test]
    public void 名字像主旋律的排在像伴奏的前面()
    {
        // 刻意把「伴奏」摆在前面（下标小）：排序若失效，返回的就正好是原序，这条才会红
        var song = SongOf(
            TrackOf(0, 0, "伴奏 钢琴", Melody()),
            TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1, 0 }));
    }

    /// <summary>
    /// 音数压不过轨名 —— 这条是原版那份打分被坑出来的原因。
    ///
    /// 「伴奏」轨音符数是「主旋律」的两倍、还盖满全曲，纯按音数或覆盖排它都会跑到前面去。
    /// 用户要弹的偏偏是那条短的旋律。
    /// </summary>
    [Test]
    public void 音数多的伴奏压不过音数少的主旋律()
    {
        // 主旋律只盖前半首（8 个音），伴奏盖满全曲（16 个音）
        var song = SongOf(
            TrackOf(0, 0, "主旋律", Melody(8)),
            TrackOf(1, 1, "伴奏 吉他", Melody(16)));

        var ranked = TrackRanking.Of(song);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks[1].NoteCount, Is.GreaterThan(song.Tracks[0].NoteCount),
                "前提：伴奏那轨音符更多，不然这条用例测不到「音数压不过名字」");
            Assert.That(ranked.Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 0, 1 }));
        });
    }

    /// <summary>
    /// 同分时按原曲下标排 —— 顺序必须是确定的。
    ///
    /// 两条名字与音符都一样的轨必然同分（分数只看名字、音域、音数、覆盖），
    /// 所以这里能稳定地钉住「同分怎么办」。反复取几次结果也必须一模一样：
    /// 默认选中的那条不能这次是甲、下次是乙。
    /// </summary>
    [Test]
    public void 同分时按原曲下标决定顺序()
    {
        // 两条同分的旋律摆在下标 1、2（不是最前），一条低分的伴奏摆在下标 0。
        // 这样「结果 = 下标升序」就不再等于「原序」，排序真失效的话这里会红。
        var song = SongOf(
            TrackOf(0, 0, "伴奏 吉他", Melody()),
            TrackOf(1, 1, "旋律", Melody()),
            TrackOf(2, 2, "旋律", Melody()));

        var first = TrackRanking.Of(song);

        Assert.Multiple(() =>
        {
            // 两条同分的旋律按原曲下标 1、2 排在前，伴奏排在后
            Assert.That(first.Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1, 2, 0 }));
            Assert.That(first[0].Score, Is.EqualTo(first[1].Score).Within(1e-12),
                "这两条本来就该同分；同分这个前提不成立的话，下面那句就不说明问题");

            for (int i = 0; i < 8; i++)
                Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex),
                    Is.EqualTo(first.Select(r => r.SongTrackIndex)),
                    "同一个 Song 反复排序必须给出同一个顺序，默认选中的那条不能飘");
        });
    }

    /// <summary>默认选中的是排在最前的那条 —— 界面就是拿 [0] 当下拉框的默认项。</summary>
    [Test]
    public void 默认选中排在最前的那条()
    {
        var song = SongOf(
            TrackOf(0, 0, "伴奏 吉他", Melody()),
            TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song)[0].SongTrackIndex, Is.EqualTo(1), "默认该选最像主旋律的那条");
    }
}
