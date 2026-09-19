using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Preview;

/// <summary>
/// 暂停（<see cref="PreviewPlayback.Pause"/>）这一态的行为。
///
/// 和折叠那条缝一样，出声断言不了、「这一下之后声卡收到了什么」断言得了，
/// 而暂停真正容易错的地方**不在声卡，在位置**：
/// 「接着放是从暂停那一刻接下去」这句话，做错了（比如从暂停前的某一帧接上、
/// 或者从这一遍的开头重来）都**不会报错**，只会听着不对。所以这里量的是
/// <c>Seek</c> 收到的那个秒数。
///
/// 「暂停时视野不动」和「按钮上的字」测不到 —— 那是窗口那一半，见
/// <c>.scratch/midi-performer/issues/20-play-pause-toggle.md</c>。
/// </summary>
public class PreviewPlaybackPauseTests
{
    private const long Quarter = 480;

    /// <summary>暂停会松开正在响的音，并停在「没在放但停在半路」这一态上。</summary>
    [Test]
    public void 暂停是停下来但停在半路()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();
        int stops = sink.StopCount;

        playback.Pause();

        Assert.Multiple(() =>
        {
            Assert.That(playback.IsPlaying, Is.False, "没在放了");
            Assert.That(playback.IsPaused, Is.True, "但停在半路，不是「这段听完了」");
            Assert.That(sink.StopCount, Is.EqualTo(stops + 1), "正在响的音要松开 —— 否则合成器上留一串按着不放的键");
        });
    }

    /// <summary>
    /// **暂停那一刻把积分推到此刻。**
    ///
    /// 积分是每帧喂一次墙上钟的，最后一次喂是上一帧的事（最多 33ms 之前）。
    /// 不补这一下的话，位置会停在上一帧 —— 这一条量的是「停了半秒之后暂停，
    /// 位置是 0.5 秒而不是 0」。
    /// </summary>
    [Test]
    public void 暂停的那一刻把位置推到此刻()
    {
        var clock = new FakeClock();
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, clock);
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();

        clock.Advance(0.5);
        playback.Pause();

        Assert.That(playback.MusicSeconds, Is.EqualTo(0.5).Within(1e-9),
            "位置在暂停的那一刻，不是上一帧");
    }

    /// <summary>
    /// 接着放是从暂停那一刻接下去 —— 不是从头，也不是把暂停期间流逝的**墙上时间**补进去。
    ///
    /// 暂停了就十分钟没管它，再按「继续」必须从刚才停的地方接上；
    /// 补墙上时间的话会一下跳到十分钟之后（早就过了曲尾）。
    /// </summary>
    [Test]
    public void 接着放是从暂停那一刻接下去()
    {
        var clock = new FakeClock();
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, clock);
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();

        clock.Advance(0.5);
        playback.Pause();

        clock.Advance(10);          // 暂停期间墙上时间照走 —— 但音乐时间不许跟着走
        playback.Play();

        Assert.Multiple(() =>
        {
            Assert.That(sink.LastSeek, Is.EqualTo(0.5).Within(1e-9),
                "声音那头从暂停那一刻起算，不是从 10.5 秒");
            Assert.That(playback.MusicSeconds, Is.EqualTo(0.5).Within(1e-9), "播放头的音乐时间也没动");
            Assert.That(playback.IsPaused, Is.False, "接着放就不再是暂停态了");
            Assert.That(playback.IsPlaying, Is.True);
        });
    }

    /// <summary>
    /// **停止会把暂停态一起清掉。**
    ///
    /// 这条是「放完自动停止之后，空格得能重新开一段」的底子：
    /// 自动停止走的就是 <c>Stop</c>。要是它不清暂停态，按钮上会一直写着「▶ 继续」，
    /// 而那个位置的语义（「从刚才停的地方接着听」）已经没有了 ——
    /// 按下去人是等着听「刚才那段」，结果从别处开始。
    /// </summary>
    [Test]
    public void 停止会把暂停态清掉()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();
        playback.Pause();

        playback.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(playback.IsPaused, Is.False);
            Assert.That(playback.IsPlaying, Is.False);
        });
    }

    /// <summary>换曲子（<c>Load</c>）也走 <c>Stop</c>：上一首的暂停态不许漏到下一首上。</summary>
    [Test]
    public void 换曲子会把暂停态清掉()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();
        playback.Pause();

        playback.Load(一条长轨(), 没有折叠());

        Assert.That(playback.IsPaused, Is.False);
    }

    /// <summary>没在放的时候按暂停：什么都不做，别往声卡发东西（按钮是灰的，但代码这条路要自己挡住）。</summary>
    [Test]
    public void 没在放的时候按暂停什么都不做()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(一条长轨(), 没有折叠());
        int stops = sink.StopCount;

        playback.Pause();

        Assert.Multiple(() =>
        {
            Assert.That(sink.StopCount, Is.EqualTo(stops), "没响的东西，没什么可松的");
            Assert.That(playback.IsPaused, Is.False, "没在放，谈不上「停在半路」");
        });
    }

    /// <summary>曲子都还没装就按暂停：不炸。</summary>
    [Test]
    public void 还没装曲子时按暂停不炸()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());

        Assert.DoesNotThrow(() => playback.Pause());
        Assert.That(playback.IsPaused, Is.False);
    }

    /// <summary>连按两下暂停：第二下无害，位置不许被推第二次。</summary>
    [Test]
    public void 连按两下暂停是无害的()
    {
        var clock = new FakeClock();
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, clock);
        playback.Load(一条长轨(), 没有折叠());
        playback.Play();

        clock.Advance(0.5);
        playback.Pause();
        clock.Advance(1);           // 暂停着的时候墙上时间又走了 1 秒
        playback.Pause();

        Assert.That(playback.MusicSeconds, Is.EqualTo(0.5).Within(1e-9),
            "第二下不许把位置推到 1.5 秒 —— 没在放的时候 AdvanceTo 就不该跑");
    }

    // ==================== 帮手 ====================

    /// <summary>
    /// 8 个四分音符、4 秒长的单轨。**要够长**：暂停那几条会把时钟推到 0.5 秒，
    /// 而 <c>Play</c> 有一句「已经到曲尾就从头再来」—— 曲子短于暂停点的话，
    /// 「接着放」会被那条分支接走，量的就不是暂停了。
    /// </summary>
    private static Song 一条长轨()
    {
        var notes = Enumerable.Range(0, 8)
            .Select(i => new Note(60, i * Quarter, Quarter, 100))
            .ToArray();
        return new Song(
            new[] { new Track(0, 0, "主旋律", 24, notes) },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));
    }

    private static IReadOnlySet<(int TrackIndex, int Channel)> 没有折叠() => new HashSet<(int, int)>();
}
