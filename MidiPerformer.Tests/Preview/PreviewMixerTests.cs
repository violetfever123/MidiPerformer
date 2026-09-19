using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Preview;
using NUnit.Framework;

namespace MidiPerformer.Tests.Preview;

/// <summary>
/// 试听事件表的摊法（<see cref="PreviewMixer"/>）。
///
/// 这一层是 16 新增的「每轨一个音色」真正落地的地方，而且它是**纯函数** ——
/// 出声验不了，但「哪个音发到哪个声道、带哪个音色」验得了，所以这里验的就是这一件事。
///
/// 最要紧的一条是<b>同一个文件声道上的两条轨必须分开</b>：音色是声道事件，
/// 挤在一个声道上的话，后一条轨的音色会把前一条的顶掉 —— 而这正是格式 0/1 的 MIDI
/// 最常见的形状（每条轨一个轨块，声声道号却都是 0）。
/// </summary>
public class PreviewMixerTests
{
    private const long Quarter = 480;

    [Test]
    public void 每条轨各占一个声道并带上自己的音色()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 1, "贝斯", 33, 0, new Note(40, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song);

        Assert.Multiple(() =>
        {
            Assert.That(mixed, Has.Count.EqualTo(2));
            Assert.That(mixed[0].Channel, Is.EqualTo(0));
            Assert.That(mixed[0].Program, Is.EqualTo(24));
            Assert.That(mixed[1].Channel, Is.EqualTo(1));
            Assert.That(mixed[1].Program, Is.EqualTo(33));
        });
    }

    /// <summary>
    /// 两条轨在文件里都是 0 号声道（格式 1 的 MIDI 常这样），试听时必须分开 ——
    /// 挤在一起的话，第二条轨那条 ProgramChange 会把第一条轨也换成它的音色。
    /// </summary>
    [Test]
    public void 文件里同一声道的两条轨在试听里也分开()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 0, "副旋律", 40, 0, new Note(67, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song);

        Assert.Multiple(() =>
        {
            Assert.That(mixed[0].Channel, Is.Not.EqualTo(mixed[1].Channel), "同一声道上两个音色会互相顶掉");
            Assert.That(mixed[0].Program, Is.EqualTo(24));
            Assert.That(mixed[1].Program, Is.EqualTo(40));
        });
    }

    /// <summary>9 号声道在 MIDI 里固定是鼓组，打击乐轨发到那儿；它也不占旋律声道的名额。</summary>
    [Test]
    public void 打击乐轨发到9号声道而且不占旋律的名额()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 9, "鼓点", 0, 0, new Note(36, 0, Quarter, 100)),
            Trk(2, 0, "副旋律", 40, 0, new Note(67, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song);

        Assert.Multiple(() =>
        {
            Assert.That(mixed[1].Channel, Is.EqualTo(PreviewMixer.PercussionChannel));
            Assert.That(mixed[2].Channel, Is.EqualTo(1), "鼓轨不占旋律声道的名额，第三条轨照旧拿 1 号");
        });
    }

    /// <summary>
    /// 旋律轨用不到 9 号声道（那是鼓组），15 条是这个池子的全部。
    /// 第 16 条绕回来和第一条共用 —— 这是写出来的代价，不是碰巧：
    /// 另一边是「第 16 条干脆不出声」，那更糟。
    /// </summary>
    [Test]
    public void 旋律轨跳过9号声道超过十五条就绕回来()
    {
        var tracks = Enumerable.Range(0, 16)
            .Select(i => Trk(i, 0, $"轨{i}", i, 0, new Note(60, 0, Quarter, 100)))
            .ToArray();

        var mixed = PreviewMixer.Mix(SongOf(Map(), tracks));
        var channels = mixed.Select(n => n.Channel).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(channels, Has.None.EqualTo(PreviewMixer.PercussionChannel),
                "9 号是鼓组，旋律落在那儿会被当成打击乐");
            Assert.That(channels.Take(15), Is.Unique, "前 15 条一条一个声道");
            Assert.That(channels[15], Is.EqualTo(channels[0]), "第 16 条绕回来和第一条共用");
        });
    }

    /// <summary>
    /// 走的是和演奏同一条换算链：**移调叠上去**（卷帘上看到的音高、耳朵听到的音高、
    /// 这份表里的音高必须是同一个），时间是**音乐时间的秒**，不是 tick。
    /// </summary>
    [Test]
    public void 移调照叠时间换成秒()
    {
        // 400000 微秒/四分音符 = 150 拍/分，一个四分音符 0.4 秒
        var song = SongOf(
            Map(480, new TempoChange(0, 400_000)),
            Trk(0, 0, "主旋律", 24, 12, new Note(60, 0, Quarter, 100)));

        var note = PreviewMixer.Mix(song).Single();

        Assert.Multiple(() =>
        {
            Assert.That(note.Note.Pitch, Is.EqualTo(72), "移调照样叠上去");
            Assert.That(note.Note.Start, Is.EqualTo(0).Within(1e-9));
            Assert.That(note.Note.End, Is.EqualTo(0.4).Within(1e-9), "秒 —— 试听这一侧只认秒");
        });
    }

    /// <summary>
    /// 超出三个八度的音**照发**：MIDI 出声不挑音域，灰显说的是「游戏里弹不出来」，
    /// 不是「不该出声」。试听正是用来听这个的。
    /// </summary>
    [Test]
    public void 超出可演奏音域的音也照发()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100), new Note(120, Quarter, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song);

        Assert.Multiple(() =>
        {
            Assert.That(mixed, Has.Count.EqualTo(2), "一个音都没被丢掉");
            Assert.That(mixed.Count(n => !n.Note.InRange), Is.EqualTo(1), "其中确实有一个超出音域");
        });
    }

    [Test]
    public void 空曲子摊出空表()
    {
        Assert.That(PreviewMixer.Mix(SongOf(Map())), Is.Empty);
    }

    // ==================== 静音（折叠起来的轨） ====================

    /// <summary>
    /// 折叠起来的轨一个音都不发，其余照旧 —— 音高、时间、音色一样不少。
    /// </summary>
    [Test]
    public void 静音的轨一个音都不发其余的照旧()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 1, "贝斯", 33, 0, new Note(40, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song, Mute((1, 1)));

        Assert.Multiple(() =>
        {
            Assert.That(mixed, Has.Count.EqualTo(1));
            Assert.That(mixed[0].Note.Pitch, Is.EqualTo(60), "留下的是没被静音的那条");
            Assert.That(mixed[0].Program, Is.EqualTo(24));
        });
    }

    /// <summary>
    /// 认轨用的是**模型那对唯一键**，不是它在 <c>Song.Tracks</c> 里的下标。
    ///
    /// 这一条就是按下标写会错的地方：第一条轨的轨块号是 5，静音 <c>(5, 2)</c> 得静到它头上；
    /// 要是按「第 1 条」算，静音的会是贝斯 —— 屏幕上演的是同一件事，耳朵听到的是另一件。
    /// </summary>
    [Test]
    public void 按轨块与声道认轨不是按列表下标()
    {
        var song = SongOf(
            Map(),
            Trk(5, 2, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(0, 0, "贝斯", 33, 0, new Note(40, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song, Mute((5, 2)));

        Assert.That(mixed.Select(n => n.Note.Pitch), Is.EqualTo(new[] { 40 }), "静音的是 (5,2) 那条，不是列表里的第二条");
    }

    /// <summary>
    /// 静音的轨**连声道名额一起让出来**：后面的轨照旧从 0 号声道排起，
    /// 不会因为前面有一条听不见的轨而白占一格、把别人往「共用声道」那一步推。
    /// </summary>
    [Test]
    public void 静音的轨不占声道名额()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 0, "副旋律", 40, 0, new Note(67, 0, Quarter, 100)));

        var mixed = PreviewMixer.Mix(song, Mute((0, 0)));

        Assert.Multiple(() =>
        {
            Assert.That(mixed, Has.Count.EqualTo(1));
            Assert.That(mixed[0].Channel, Is.EqualTo(0), "剩下这条是第一个要声道的，拿 0 号");
            Assert.That(mixed[0].Program, Is.EqualTo(40));
        });
    }

    /// <summary>整首都收起来了：一张空表。出声那头收到空表就是不出声（见 <c>IAudioSink.Play</c>）。</summary>
    [Test]
    public void 全都静音就摊出空表()
    {
        var song = SongOf(
            Map(),
            Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)),
            Trk(1, 9, "鼓点", 0, 0, new Note(36, 0, Quarter, 100)));

        Assert.That(PreviewMixer.Mix(song, Mute((0, 0), (1, 9))), Is.Empty);
    }

    /// <summary>名单里写了一条**根本不存在**的轨：什么都不该被静音掉（名单对不上就是没静音，不是全静音）。</summary>
    [Test]
    public void 名单里没有这条轨时它照响()
    {
        var song = SongOf(Map(), Trk(0, 0, "主旋律", 24, 0, new Note(60, 0, Quarter, 100)));

        Assert.That(PreviewMixer.Mix(song, Mute((7, 3))), Has.Count.EqualTo(1));
    }

    // ==================== 帮手 ====================

    /// <summary>一份静音名单，按 <c>(轨块号, 声道)</c> 写 —— 界面上那对唯一键。</summary>
    private static IReadOnlySet<(int TrackIndex, int Channel)> Mute(params (int, int)[] tracks)
        => tracks.ToHashSet();

    private static Song SongOf(TempoMap map, params Track[] tracks) => new(tracks, map);

    private static TempoMap Map(int pulsesPerQuarterNote = 480, params TempoChange[] changes)
        => new(TimeDivision.PulsesPerQuarter(pulsesPerQuarterNote), changes);

    private static Track Trk(int index, int channel, string name, int program, int transpose, params Note[] notes)
        => new(index, channel, name, program, notes, transpose);
}
