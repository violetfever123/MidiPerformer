using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Preview;
using NUnit.Framework;

namespace MidiPerformer.Tests.Preview;

/// <summary>
/// 「这首曲子到哪儿算完」—— 按还听得见的那几条轨算（<see cref="AudibleLength"/>）：
/// 伴奏比主旋律长的时候，把它折叠起来听，主旋律放完之后不该还空转一段
/// （播放头照走、进度条照爬、卷帘照画，就是不出声）。
/// 用例里的曲子都是手搭的，三条轨末尾分别在 1 / 3 / 2 小节、故意不等长；
/// 480 tick/四分音符、默认 120BPM，所以一小节 = 2.0 秒。
/// </summary>
public class AudibleLengthTests
{
    private const long Quarter = 480;
    private const long Bar = 4 * Quarter;

    /// <summary>一条都不静音：整份谱面的末尾，和 <see cref="Song.EndTick"/> 一模一样。</summary>
    [Test]
    public void 不静音时就是整份谱面的末尾()
    {
        var song = 三条不等长();

        Assert.Multiple(() =>
        {
            Assert.That(AudibleLength.EndTick(song, null), Is.EqualTo(song.EndTick));
            Assert.That(AudibleLength.EndTick(song, 静音()), Is.EqualTo(song.EndTick), "空名单 = 一条都不静音");
            Assert.That(song.EndTick, Is.EqualTo(3 * Bar), "最长的那条在 3 小节末");
        });
    }

    /// <summary>收起最长的那条：末尾退回剩下的里面最长的那一条，不是「第一条」也不是 0。</summary>
    [Test]
    public void 收起最长的那条末尾退回剩下最长的()
    {
        var song = 三条不等长();

        Assert.That(AudibleLength.EndTick(song, 静音((1, 1))), Is.EqualTo(2 * Bar),
            "3 小节那条收起来了，剩下的是 2 小节那条");
    }

    /// <summary>收起短的那条：长度一点不变。</summary>
    [Test]
    public void 收起短的那条长度不变()
    {
        var song = 三条不等长();

        Assert.Multiple(() =>
        {
            Assert.That(AudibleLength.EndTick(song, 静音((0, 0))), Is.EqualTo(3 * Bar), "收起 1 小节那条");
            Assert.That(AudibleLength.EndTick(song, 静音((2, 2))), Is.EqualTo(3 * Bar), "收起 2 小节那条");
        });
    }

    /// <summary>
    /// 全收起来 = 「这几条我都不想听」，不是「这首曲子是空的」，所以退回整份谱面：
    /// 让长度当场变 0 的话，卷帘会缩成一小节、播放头被拉回开头、进度条分母变 0。
    /// </summary>
    [Test]
    public void 全收起来退回整份谱面而不是零()
    {
        var song = 三条不等长();

        long end = AudibleLength.EndTick(song, 静音((0, 0), (1, 1), (2, 2)));

        Assert.That(end, Is.EqualTo(song.EndTick), "退回整份谱面");
        Assert.That(end, Is.GreaterThan(0), "绝不能是 0");
    }

    /// <summary>
    /// 留下的那条轨本来就是空的：和「全收起来」是同一件没有声音的事，走同一条兜底。
    /// </summary>
    [Test]
    public void 留下的那条是空轨时也退回整份谱面()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "主旋律", 24, 音(0, 3 * Bar)),
                new Track(1, 1, "空轨", 33, Array.Empty<Note>())
            },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));

        Assert.That(AudibleLength.EndTick(song, 静音((0, 0))), Is.EqualTo(3 * Bar));
    }

    /// <summary>一首轨都没有：0。空曲本来就画成一小节，长度是 0 才对得上。</summary>
    [Test]
    public void 没有轨时是零()
    {
        var song = new Song(Array.Empty<Track>(), new TempoMap(TimeDivision.PulsesPerQuarter(480)));

        Assert.Multiple(() =>
        {
            Assert.That(AudibleLength.EndTick(song, null), Is.EqualTo(0));
            Assert.That(AudibleLength.Seconds(song, null), Is.EqualTo(0));
        });
    }

    /// <summary>
    /// 名单里给的是一对 <c>(轨块号, 声道)</c>，不是下标。
    /// 这两条轨挤在同一个轨块 0 上（格式 0/1 的 MIDI 常这样），只报轨块号分不开它们；
    /// 0 号声道那条长、1 号声道那条短，静音谁长度就得跟着那一条走。
    /// </summary>
    [Test]
    public void 同一个轨块上的两条轨按声道分开()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "长", 24, 音(0, 3 * Bar)),
                new Track(0, 1, "短", 24, 音(0, Bar))
            },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));

        Assert.Multiple(() =>
        {
            Assert.That(AudibleLength.EndTick(song, 静音((0, 0))), Is.EqualTo(Bar), "收起来的是长的那条");
            Assert.That(AudibleLength.EndTick(song, 静音((0, 1))), Is.EqualTo(3 * Bar), "收起来的是短的那条");
        });
    }

    /// <summary>名单里有一对谁都对不上的身份：当它不存在，长度一点不变（别拿它去凑下标）。</summary>
    [Test]
    public void 名单里对不上的身份不影响任何东西()
    {
        var song = 三条不等长();

        Assert.That(AudibleLength.EndTick(song, 静音((9, 9))), Is.EqualTo(song.EndTick));
    }

    /// <summary>秒数走的是曲子自己那张速度表：一小节 2.0 秒，两小节 4.0 秒。</summary>
    [Test]
    public void 秒数按同一张速度表换算()
    {
        var song = 三条不等长();

        Assert.Multiple(() =>
        {
            Assert.That(AudibleLength.Seconds(song, null), Is.EqualTo(6.0).Within(1e-9), "3 小节 = 6 秒");
            Assert.That(AudibleLength.Seconds(song, 静音((1, 1))), Is.EqualTo(4.0).Within(1e-9), "2 小节 = 4 秒");
            Assert.That(AudibleLength.Seconds(song, 静音((1, 1), (2, 2))), Is.EqualTo(2.0).Within(1e-9),
                "只剩 1 小节那条 = 2 秒");
        });
    }

    /// <summary>三条不等长的轨：末尾分别在 1 / 3 / 2 小节，轨块/声道都是各自的号。</summary>
    private static Song 三条不等长() => new(
        new[]
        {
            new Track(0, 0, "主旋律", 24, 音(0, Bar)),
            new Track(1, 1, "贝斯", 33, 音(0, 3 * Bar)),
            new Track(2, 2, "和声", 24, 音(0, 2 * Bar))
        },
        new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>一个从 0 tick 起、到 <paramref name="endTick"/> 止的音（长度就是它的末尾）。</summary>
    private static Note[] 音(long startTick, long endTick) =>
        new[] { new Note(60, startTick, endTick - startTick, 100) };

    /// <summary>按 <c>(轨块号, 声道)</c> 写一份静音名单。</summary>
    private static IReadOnlySet<(int TrackIndex, int Channel)> 静音(
        params (int TrackIndex, int Channel)[] pairs) => pairs.ToHashSet();
}
