using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Preview;

/// <summary>
/// 折叠一条轨时试听那一头的反应（<see cref="PreviewPlayback.SetMutedTracks"/>）。
/// 缝开在 <see cref="FakeAudioSink"/> 上，验两件事：折叠接着放（不打断正在听的那一遍），
/// 以及那一刻声卡收到的表里确实少掉了那一条轨 —— 光换掉内部那个列表是不够的，
/// 出声那头手上还留着上一批音，被静音的那条轨会一直响到它自己结束。
/// 界面那一半（点按钮、那一行小字）在这儿验不了。
/// </summary>
public class PreviewPlaybackMuteTests
{
    private const long Quarter = 480;
    private const int 第一轨 = 0;
    private const int 第二轨 = 1;

    /// <summary>折叠一条**正在响**的轨：接着放，只是给声卡重排了一张少一条轨的表。</summary>
    [Test]
    public void 折叠正在播的轨是接着放不是停下来()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(两条轨(), 静音());
        playback.Play();

        Assert.That(sink.LastPlayed, Has.Count.EqualTo(2), "起播时两条轨都在");

        // 基数在起播之后取：Load 自己也会停一次，那一次不是这一下造成的
        int plays = sink.PlayCount;
        int stops = sink.StopCount;

        playback.SetMutedTracks(静音(第一轨));

        Assert.Multiple(() =>
        {
            Assert.That(playback.IsPlaying, Is.True, "没停下来 —— 折叠不该打断正在听的那一遍");
            Assert.That(sink.PlayCount, Is.EqualTo(plays + 1), "给声卡重排了一遍");
            Assert.That(sink.StopCount, Is.EqualTo(stops + 1), "重排之前先松开所有正在响的音");
            Assert.That(sink.LastPlayed.Select(n => n.Note.Pitch), Is.EqualTo(new[] { 40 }),
                "声卡手上这一批里只剩没被折叠的那条");
        });
    }

    /// <summary>重排是从此刻的音乐时间接下去的，不是从这一遍的开头重来。</summary>
    [Test]
    public void 重排是从此刻接着排不是从头()
    {
        var clock = new FakeClock();
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, clock);
        playback.Load(两条轨(), 静音());
        playback.Play();

        clock.Advance(0.5);
        playback.SetMutedTracks(静音(第一轨));

        Assert.That(sink.LastSeek, Is.EqualTo(0.5).Within(1e-9), "声音那头从当前音乐时间起算");
    }

    /// <summary>展开回来：那一条的音回到表里，同样接着放。</summary>
    [Test]
    public void 展开回来那条音又回到表里()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(两条轨(), 静音(第一轨));
        playback.Play();

        Assert.That(sink.LastPlayed, Has.Count.EqualTo(1), "起播时就已经是折叠的那条不响");

        playback.SetMutedTracks(静音());

        Assert.Multiple(() =>
        {
            Assert.That(playback.IsPlaying, Is.True);
            Assert.That(sink.LastPlayed.Select(n => n.Note.Pitch), Is.EqualTo(new[] { 60, 40 }));
        });
    }

    /// <summary>没在播的时候只换那张表，一个字节都不往声卡发。</summary>
    [Test]
    public void 没在播的时候不碰声卡()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(两条轨(), 静音());
        int plays = sink.PlayCount;
        int stops = sink.StopCount;

        playback.SetMutedTracks(静音(第一轨));

        Assert.Multiple(() =>
        {
            Assert.That(sink.PlayCount, Is.EqualTo(plays), "没在播，不重排");
            Assert.That(sink.StopCount, Is.EqualTo(stops), "也没必要去松开谁");
            Assert.That(playback.IsPlaying, Is.False);
        });

        playback.Play();

        Assert.That(sink.LastPlayed.Select(n => n.Note.Pitch), Is.EqualTo(new[] { 40 }),
            "下一次起播用的是新那张表");
    }

    /// <summary>曲子都还没装就调它：什么都不做，别炸（窗口重建控件那一瞬间真会走到这儿）。</summary>
    [Test]
    public void 还没装曲子时什么都不做()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());

        Assert.DoesNotThrow(() => playback.SetMutedTracks(静音(第一轨)));
        Assert.That(sink.PlayCount, Is.EqualTo(0));
    }

    /// <summary>整首都折叠起来：声卡收到的是一张空表。</summary>
    [Test]
    public void 全都折叠之后声卡收到空表()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(两条轨(), 静音());
        playback.Play();

        playback.SetMutedTracks(静音(第一轨, 第二轨));

        Assert.That(sink.LastPlayed, Is.Empty);
    }

    /// <summary>
    /// 折叠一条比别的都长的轨：整曲时长当场缩短，展开回来长回去。
    /// 长度只按听得见的轨算（见 <c>AudibleLength</c>），不然会出现「主旋律放完还要空转一段」。
    /// </summary>
    [Test]
    public void 折叠最长的轨让整曲时长缩短()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());
        playback.Load(长轨与短轨(), 静音());

        Assert.That(playback.TotalSeconds, Is.EqualTo(6.0).Within(1e-9), "两条都在：3 小节 = 6 秒");

        playback.SetMutedTracks(静音(第二轨));

        Assert.That(playback.TotalSeconds, Is.EqualTo(2.0).Within(1e-9), "长的收起来了：1 小节 = 2 秒");

        playback.SetMutedTracks(静音());

        Assert.That(playback.TotalSeconds, Is.EqualTo(6.0).Within(1e-9), "展开回来长度也回来");
    }

    /// <summary>装曲子时就带着折叠名单走的那条路。</summary>
    [Test]
    public void 装曲子时就按名单算时长()
    {
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, new FakeClock());

        playback.Load(长轨与短轨(), 静音(第二轨));

        Assert.That(playback.TotalSeconds, Is.EqualTo(2.0).Within(1e-9));
    }

    /// <summary>
    /// 正放着的时候把长的那条收起来：位置当场落到曲子外面（「位置 &gt; 总长」）。
    /// 窗口据此把暂停中的播放头拉回曲尾，正在播的话下一帧的 <c>Finished</c> 会把它停掉；
    /// 定时器那一帧在这儿推不动，所以量的是状态不是结果。
    /// </summary>
    [Test]
    public void 折叠之后位置可能当场落到曲子外面()
    {
        var clock = new FakeClock();
        var sink = new FakeAudioSink();
        using var playback = new PreviewPlayback(sink, clock);
        playback.Load(长轨与短轨(), 静音());
        playback.Play();

        clock.Advance(2.5);
        playback.SetMutedTracks(静音(第二轨));

        Assert.Multiple(() =>
        {
            Assert.That(playback.MusicSeconds, Is.EqualTo(2.5).Within(1e-9), "折叠不动播放头");
            Assert.That(playback.TotalSeconds, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(playback.MusicSeconds, Is.GreaterThan(playback.TotalSeconds), "落到曲子外面了");
            Assert.That(playback.IsPlaying, Is.True, "折叠这一下不该打断正在听的那一遍");
        });
    }

    /// <summary>两条轨：轨块 0 的 0 号声道（C4）、轨块 1 的 1 号声道（E2）。</summary>
    private static Song 两条轨() => new(
        new[]
        {
            new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, Quarter, 100) }),
            new Track(1, 1, "贝斯", 33, new[] { new Note(40, 0, Quarter, 100) })
        },
        new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>
    /// 长短不一的两条轨：轨块 0 那条 1 小节（2.0 秒），轨块 1 那条 3 小节（6.0 秒）。
    /// 长度口径的用例都得用它，等长的两条测不出「按哪条算」。
    /// </summary>
    private static Song 长轨与短轨() => new(
        new[]
        {
            new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, 4 * Quarter, 100) }),
            new Track(1, 1, "贝斯", 33, new[] { new Note(40, 0, 3 * 4 * Quarter, 100) })
        },
        new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>按轨块号写一份折叠名单（两条轨刚好是 0 号和 1 号）。</summary>
    private static IReadOnlySet<(int TrackIndex, int Channel)> 静音(params int[] trackIndexes)
        => trackIndexes.Select(i => (i, i)).ToHashSet();
}
