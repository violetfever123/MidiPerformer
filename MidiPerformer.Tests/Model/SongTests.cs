using MidiPerformer.Core.Model;
using NUnit.Framework;
// DryWetMidi 也有同名的 TempoMap / TimeDivision，不加别名就分不清说的是哪一边
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Song.TryMeasure"/> —— 「这份谱面装不装得进秒」这一问。
///
/// 它存在的理由是**卷帘画得出来不等于放得出来**：卷帘是 tick 轴，多大的 tick 都画得出来；
/// 而试听、演奏、时长读数都要 tick → 秒，<see cref="ModelTempoMap.SecondsAt"/> 对装不下的 tick 会抛。
/// 界面必须在**装谱面之前**问这一句，不然会装到一半炸掉（卷帘已经是新的、试听还是旧的）。
///
/// 这些用例钉的是那份「先问再装」的判据，不是 <c>SecondsAt</c> 本身 —— 后者另一处已经有测试了。
/// </summary>
public class SongTests
{
    private static Song With(params Note[] notes) =>
        new(new[] { new Track(0, 0, "主旋律", 24, notes) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

    /// <summary>480 PPQ / 120 BPM 下装得下的正常曲子：照常通过，秒数就是整曲时长。</summary>
    [Test]
    public void 正常曲子量得出来()
    {
        var song = With(new Note(60, 0, 480, 100), new Note(62, 480, 480, 100));

        Assert.That(song.TryMeasure(out double seconds, out string? reason), Is.True);
        Assert.That(reason, Is.Null, "通过的时候不该有理由");
        Assert.That(seconds, Is.EqualTo(song.TotalSeconds), "给出来的秒数要和 TotalSeconds 一致");
    }

    /// <summary>空曲不算「量不出来」：0 轨、0 音都是合法的谱面，时长就是 0。</summary>
    [Test]
    public void 空曲量得出来且是零秒()
    {
        var song = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        Assert.That(song.TryMeasure(out double seconds, out _), Is.True);
        Assert.That(seconds, Is.Zero);
    }

    /// <summary>
    /// tick 大到换算出来装不下 → 量不出来，且理由是中文。
    ///
    /// 9e15 这个数是**本仓库自己认为合法**的：`SongProjectFileTests.超长tick往返一位不差`
    /// 拿它往返过 .mproj，`WriteProject` 也不校验（JSON 里没有「装不下」的值）。
    /// 也就是说这样一份工程存得下、读得回、列在曲库里，只在**装进窗口**那一刻才炸 ——
    /// 所以拦的必须是这一步。
    /// </summary>
    [Test]
    public void tick大到装不下时量不出来()
    {
        var song = With(new Note(60, 9_000_000_000_000_000, 480, 100));

        Assert.That(song.TryMeasure(out _, out string? reason), Is.False);
        Assert.That(reason, Does.Contain("太大"), "理由得是给人看的中文");
    }

    /// <summary>
    /// 音符的**结束 tick 溢出成负数**时也得拦住。
    ///
    /// 这一条是这份判据为什么不直接读 <see cref="Song.TotalSeconds"/> 的原因，也是它唯一的坑：
    /// <c>TotalSeconds</c> 走 <see cref="Song.EndTick"/>，而 EndTick 是各音
    /// <c>StartTick + LengthTicks</c> 的最大值 —— <c>long.MaxValue + 1</c> 一溢出就成了
    /// <c>long.MinValue</c>，于是这个音被 <c>Math.Max</c> 当成「比 0 还小」忽略掉，
    /// 判据看着一切正常，试听那边却会拿一个天文数字的 StartTick 去换算，照样抛。
    ///
    /// 所以判据必须把**起点和终点都**算进去 —— 就是这一条在钉它。
    /// </summary>
    [Test]
    public void 音符终点溢出成负数时也量不出来()
    {
        var song = With(new Note(60, long.MaxValue, 1, 100));

        // 先说清楚前提：这份谱面的 EndTick 确实是错的（溢出被忽略了），
        // 所以「读 TotalSeconds 就够」那种写法在这一条上必然是漏的。
        Assert.That(song.EndTick, Is.Zero, "前提：溢出的结束 tick 被 Math.Max 忽略了");

        Assert.That(song.TryMeasure(out _, out string? reason), Is.False);
        Assert.That(reason, Does.Contain("太大"));
    }
}
