using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using NUnit.Framework;

namespace MidiPerformer.Tests.Analysis;

/// <summary>
/// 演奏器下拉框那份「能弹的轨」：哪些进得来、按什么顺序。
/// 顺序就是原曲下标升序 —— 不打分、不重排，「哪条是主旋律」交给用户自己认。
/// </summary>
public class PlayableTracksTests
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

    /// <summary>四轨两弹：主旋律 + 低音线能弹，鼓轨（声道 9）和有和声的那条不能。</summary>
    [Test]
    public void 只列能弹的轨_打击乐与多声部都不在()
    {
        var melody = TrackOf(0, 0, "主旋律", Melody());
        var bass = TrackOf(1, 1, "低音线", Melody(12));
        var drums = TrackOf(2, PlayableTracks.PercussionChannel, "鼓", Melody(24));
        var chords = TrackOf(3, 2, "和弦铺底", Chords());

        var song = SongOf(melody, bass, drums, chords);
        var playable = PlayableTracks.Of(song);

        Assert.Multiple(() =>
        {
            Assert.That(playable.Select(r => r.SongTrackIndex), Is.EquivalentTo(new[] { 0, 1 }),
                "能弹的只有主旋律和低音线：鼓（声道 9）和有和声的那条都不该出现");

            Assert.That(playable.Select(r => r.Track), Does.Not.Contain(drums), "打击乐轨不该出现在下拉框里");
            Assert.That(playable.Select(r => r.Track), Does.Not.Contain(chords), "多声部轨不该出现在下拉框里");

            // 能弹的一条也不能漏
            Assert.That(playable.Select(r => r.Track), Does.Contain(melody));
            Assert.That(playable.Select(r => r.Track), Does.Contain(bass));

            // 下标要指回原曲：界面拿它去 song.Tracks[...] 取轨
            foreach (var r in playable)
                Assert.That(song.Tracks[r.SongTrackIndex], Is.SameAs(r.Track));
        });
    }

    /// <summary>没有音符的轨不成轨，不该占下拉框的位置。</summary>
    [Test]
    public void 没有音符的轨不出现()
    {
        var song = SongOf(TrackOf(0, 0, "空轨"), TrackOf(1, 1, "主旋律", Melody()));

        Assert.That(PlayableTracks.Of(song).Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 1 }));
    }

    /// <summary>一首什么都没有的曲子返回空表，不抛。</summary>
    [Test]
    public void 空曲返回空表()
    {
        Assert.That(PlayableTracks.Of(SongOf()), Is.Empty);
    }

    /// <summary>
    /// 顺序就是原曲下标升序，不按任何别的名目重排。默认选中的因此是曲子里**第一条能弹的**，
    /// 哪怕它叫「伴奏」、哪怕后面那条叫「主旋律」。
    /// </summary>
    [Test]
    public void 按原曲下标升序_按名字打分的那一套不在了()
    {
        // 名字会「赢」的那条摆在下标最大的位置：要是还留着按名字/音域/音数打分排序，
        // 返回的就是 [2, 0, 1]（主旋律 +45 在前，「伴奏」「低音」各 -35 并列在后）。
        var song = SongOf(
            TrackOf(0, 0, "伴奏 吉他", Melody()),
            TrackOf(1, 1, "低音线", Melody()),
            TrackOf(2, 2, "主旋律", Melody()));

        var playable = PlayableTracks.Of(song);

        Assert.Multiple(() =>
        {
            Assert.That(playable.Select(r => r.SongTrackIndex), Is.EqualTo(new[] { 0, 1, 2 }),
                "顺序该是原曲下标升序，不受轨名影响");
            Assert.That(playable[0].SongTrackIndex, Is.EqualTo(0),
                "默认选中的是第一条能弹的，不是最像主旋律的那条");
        });
    }
}
