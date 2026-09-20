using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using NUnit.Framework;

namespace MidiPerformer.Tests.Analysis;

/// <summary>
/// 演奏轨排序 —— 能弹的轨里哪条最像主旋律。
/// 能不能弹与分数无关（打击乐和多声部轨一律不出现）；顺序是确定的，同分时按原曲下标排。
/// </summary>
public class TrackRankingTests
{
    /// <summary>1 tick = 0.1ms 的表。</summary>
    private static TempoMap Tempo() => new(
        TimeDivision.PulsesPerQuarter(5000),
        new[] { new TempoChange(0, 500_000) });

    /// <summary>一条往上爬的单声部旋律：16 个音，每个 400ms。</summary>
    private static Note[] Melody(int count = 16, long stepTicks = 4000) =>
        Enumerable.Range(0, count)
            .Select(i => new Note(60 + i % 12, i * stepTicks, stepTicks, 100))
            .ToArray();

    /// <summary>有和声的轨：每个起音上叠一个三度，同一 tick 两个音。</summary>
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

    /// <summary>四轨两弹：主旋律 + 伴奏能弹，鼓轨（声道 9）和有和声的那条不能。</summary>
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

            // 能弹的一条也不能漏
            Assert.That(ranked.Select(r => r.Track), Does.Contain(melody));
            Assert.That(ranked.Select(r => r.Track), Does.Contain(bass));

            // 下标要指回原曲：界面拿它去 song.Tracks[...] 取轨
            foreach (var r in ranked)
                Assert.That(song.Tracks[r.SongTrackIndex], Is.SameAs(r.Track));
        });
    }

    /// <summary>没有音符的轨不成轨，不该占下拉框的位置。</summary>
    [Test]
    public void 没有音符的轨不出现()
    {
        var song = SongOf(TrackOf(0, 0, "空轨"), TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1 }));
    }

    /// <summary>一首什么都没有的曲子返回空表，不抛。</summary>
    [Test]
    public void 空曲返回空表()
    {
        Assert.That(TrackRanking.Of(SongOf()), Is.Empty);
    }

    /// <summary>轨名权重最高：名字说主旋律的排在名字说伴奏的前面。</summary>
    [Test]
    public void 名字像主旋律的排在像伴奏的前面()
    {
        // 「伴奏」摆在下标更小的位置：排序若失效，返回的就正好是原序
        var song = SongOf(
            TrackOf(0, 0, "伴奏 钢琴", Melody()),
            TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1, 0 }));
    }

    /// <summary>音数压不过轨名：音符更多、盖满全曲的「伴奏」也排在短旋律后面。</summary>
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

    /// <summary>同分时按原曲下标排，反复取几次的结果必须一模一样。</summary>
    [Test]
    public void 同分时按原曲下标决定顺序()
    {
        // 两条同分的旋律在下标 1、2，伴奏在下标 0：这样「结果 = 下标升序」不再等于「原序」。
        var song = SongOf(
            TrackOf(0, 0, "伴奏 吉他", Melody()),
            TrackOf(1, 1, "旋律", Melody()),
            TrackOf(2, 2, "旋律", Melody()));

        var first = TrackRanking.Of(song);

        Assert.Multiple(() =>
        {
            // 同分的两条旋律按原曲下标排在前，伴奏排在后
            Assert.That(first.Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1, 2, 0 }));
            Assert.That(first[0].Score, Is.EqualTo(first[1].Score).Within(1e-12),
                "这两条本来就该同分；同分这个前提不成立的话，下面那句就不说明问题");

            for (int i = 0; i < 8; i++)
                Assert.That(TrackRanking.Of(song).Select(r => r.SongTrackIndex),
                    Is.EqualTo(first.Select(r => r.SongTrackIndex)),
                    "同一个 Song 反复排序必须给出同一个顺序，默认选中的那条不能飘");
        });
    }

    /// <summary>界面拿 [0] 当下拉框的默认项。</summary>
    [Test]
    public void 默认选中排在最前的那条()
    {
        var song = SongOf(
            TrackOf(0, 0, "伴奏 吉他", Melody()),
            TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(TrackRanking.Of(song)[0].SongTrackIndex, Is.EqualTo(1), "默认该选最像主旋律的那条");
    }
}
