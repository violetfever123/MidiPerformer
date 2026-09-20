using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 基准八度怎么选：手动给的就是它，不给就走自动
/// （让可演奏区容下最多音符，同分时取平均八度最近的）。
/// 口琴只有「基准八度 ±1 个八度，外加最高两个音」，所以「能不能弹」不是一条区间。
/// </summary>
public class BaseOctaveTests
{
    /// <summary>
    /// 自动选的是能弹最多的那个八度，不是平均八度最近的那个。
    /// 样本：两个 C3 + 十个 D5；平均八度 4.67（更靠近 5），但选 5 只能弹 10 个，选 4 能弹满 12 个。
    /// </summary>
    [Test]
    public void 自动选的是能弹最多的那个八度()
    {
        var notes = 音符(48, 2).Concat(音符(74, 10)).ToList();

        var mapped = NoteMapper.Map(notes, transpose: 0, manualBaseOctave: null);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.BaseOctave, Is.EqualTo(4),
                "平均八度是 4.67，按平均选会得到 5 —— 那就有两个音弹不出来");
            Assert.That(mapped.InRangeCount, Is.EqualTo(12), "选 4 时这 12 个音全都该弹得出来");
            Assert.That(mapped.SkipCount, Is.Zero);
        });
    }

    /// <summary>
    /// 打平时取平均八度最近的那个。
    /// 样本：C4 + 两个 C6，选 4 和选 5 都能弹满 3 个，平均八度 5.33 所以该选 5。
    /// </summary>
    [Test]
    public void 打平的时候取平均八度最近的()
    {
        var notes = 音符(60, 1).Concat(音符(84, 2)).ToList();

        var mapped = NoteMapper.Map(notes, transpose: 0, manualBaseOctave: null);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.InRangeCount, Is.EqualTo(3), "这个样本的前提就是 4 和 5 打平");
            Assert.That(mapped.BaseOctave, Is.EqualTo(5),
                "平均八度 5.33 更近 5；给出 4 说明同分时没拿平均八度比");
        });
    }

    /// <summary>手动指定的就是它，哪怕它比自动差。</summary>
    [Test]
    public void 手动指定的八度说了算哪怕它更差()
    {
        var notes = 音符(48, 2).Concat(音符(74, 10)).ToList();

        var mapped = NoteMapper.Map(notes, transpose: 0, manualBaseOctave: 5);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.BaseOctave, Is.EqualTo(5), "手动指定被自动那条规则盖回去了");
            Assert.That(mapped.InRangeCount, Is.EqualTo(10), "按 5 算就该只有那 10 个 D5 弹得出来");
            Assert.That(mapped.SkipCount, Is.EqualTo(2), "两个 C3 超出去、该被标成跳过");
        });
    }

    /// <summary>一个音都没有的时候给一个常量，不抛（编辑器算音域也会走这个纯函数）。</summary>
    [Test]
    public void 空样本给一个常量不抛()
    {
        Assert.That(NoteMapper.AutoBaseOctave(Array.Empty<int>()), Is.EqualTo(4));
    }

    /// <summary>移调算进「能不能弹」里：判断用的是移调之后的音高。</summary>
    [Test]
    public void 自动八度算的是移调之后的音高()
    {
        var notes = 音符(48, 2).Concat(音符(74, 10)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(NoteMapper.Map(notes, 0, null).BaseOctave, Is.EqualTo(4));
            Assert.That(NoteMapper.Map(notes, 12, null).BaseOctave, Is.EqualTo(5),
                "整体升高一个八度，选的基准八度得跟着走 —— 移调没算进去");
        });
    }

    /// <summary><paramref name="count"/> 个同一个音高的音，时间上前后排开。</summary>
    private static IEnumerable<RawNote> 音符(int pitch, int count)
    {
        for (int i = 0; i < count; i++)
            yield return new RawNote { Pitch = pitch, Start = i * 0.5, End = i * 0.5 + 0.25 };
    }
}
