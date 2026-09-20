using MidiPerformer.Core.Model;
using NUnit.Framework;
// DryWetMidi 也有同名的 TempoMap / TimeDivision，必须加别名区分
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Song.TryMeasure"/>：这份谱面装不装得进秒。
/// 试听、演奏、时长读数都要 tick → 秒，<see cref="ModelTempoMap.SecondsAt"/> 对装不下的 tick 会抛，
/// 所以界面得在装谱面之前先问这一句。
/// </summary>
public class SongTests
{
    private static Song With(params Note[] notes) =>
        new(new[] { new Track(0, 0, "主旋律", 24, notes) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

    /// <summary>480 PPQ / 120 BPM 下装得下的正常曲子，秒数就是整曲时长。</summary>
    [Test]
    public void 正常曲子量得出来()
    {
        var song = With(new Note(60, 0, 480, 100), new Note(62, 480, 480, 100));

        Assert.That(song.TryMeasure(out double seconds, out string? reason), Is.True);
        Assert.That(reason, Is.Null, "通过的时候不该有理由");
        Assert.That(seconds, Is.EqualTo(song.TotalSeconds), "给出来的秒数要和 TotalSeconds 一致");
    }

    /// <summary>0 轨、0 音是合法谱面，量得出来，时长 0。</summary>
    [Test]
    public void 空曲量得出来且是零秒()
    {
        var song = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        Assert.That(song.TryMeasure(out double seconds, out _), Is.True);
        Assert.That(seconds, Is.Zero);
    }

    /// <summary>
    /// tick 大到换算出来装不下就量不出来，理由是中文。
    /// 9e15 是本仓库自己认为合法的值（.mproj 存得下、读得回），只在装进窗口那一刻才炸。
    /// </summary>
    [Test]
    public void tick大到装不下时量不出来()
    {
        var song = With(new Note(60, 9_000_000_000_000_000, 480, 100));

        Assert.That(song.TryMeasure(out _, out string? reason), Is.False);
        Assert.That(reason, Does.Contain("太大"), "理由得是给人看的中文");
    }

    /// <summary>
    /// 音符的结束 tick 溢出成负数时也得拦住。
    /// <see cref="Song.TotalSeconds"/> 走 <see cref="Song.EndTick"/>，
    /// 而 <c>StartTick + LengthTicks</c> 溢出成 <c>long.MinValue</c> 后会被 <c>Math.Max</c> 忽略掉，
    /// 所以判据必须把起点和终点都算进去。
    /// </summary>
    [Test]
    public void 音符终点溢出成负数时也量不出来()
    {
        var song = With(new Note(60, long.MaxValue, 1, 100));

        // 前提：这份谱面的 EndTick 确实是错的，所以「读 TotalSeconds 就够」的写法会漏。
        Assert.That(song.EndTick, Is.Zero, "前提：溢出的结束 tick 被 Math.Max 忽略了");

        Assert.That(song.TryMeasure(out _, out string? reason), Is.False);
        Assert.That(reason, Does.Contain("太大"));
    }
}
