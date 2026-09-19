using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 基准八度怎么选：**手动给的就是它，不给就走自动**，而自动那条规则是
/// 「让可演奏区容下最多音符，同分时取平均八度最近的」。
///
/// 为什么单独测这一条，而不是只靠 <c>FullChainParityTests</c> 的对拍：
/// 对拍管的是「和原版算得一样」，它覆盖面很广、却是**黑盒**的 —— 它没法构造出
/// 「两个八度打平、只有平均八度分得出胜负」那种样本，也就守不住工单里写死的那半句
/// 「同分时取平均八度最近的」。而这两半是会各自坏掉的：只按平均八度选，听起来只是
/// 「整首曲子偏高偏低、零星几个音弹不出来」—— 不难听，只是不对，没人会去查。
///
/// 口琴只有「基准八度 ±1 个八度，外加最高两个音（高高音 do / #do）」，
/// 所以「能不能弹」不是一条区间，下面每个样本都是照这条算的。
/// </summary>
public class BaseOctaveTests
{
    /// <summary>
    /// 自动选的是**能弹最多**的那个八度，不是平均八度最近的那个。
    ///
    /// 样本：两个 C3 + 十个 D5。平均八度是 4.67（更靠近 5），但选 5 只能弹 10 个
    /// （两个 C3 掉到范围外），选 4 能弹满 12 个。一个「按平均八度选」的实现
    /// 在这条上给出 5 —— 听起来只是低音那两个音没了，别的一概正常。
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
    /// 打平时取**平均八度最近**的那个。
    ///
    /// 样本：C4 + 两个 C6。选 4 和选 5 都能弹满 3 个（C4 在基准 5 下低一个八度，
    /// 两个 C6 在基准 4 下正好落在「最高两个音」那个宽限上），而平均八度是 5.33 ——
    /// 该选 5。一个「谁先扫到算谁的」实现会给出 4：四个音一样弹得出来，
    /// 错的只是整条线的位置，听不出毛病。
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

    /// <summary>
    /// 手动指定的就是它，**哪怕它比自动差**。
    ///
    /// 「手动指定就按你选的那个八度算」是工单的原话：用户知道自己在干什么
    /// （手上那把口琴、或者跟别人合奏要对齐），程序不该偷偷替他改回自动。
    /// 所以这里故意用同一份样本、手动挑一个更差的八度，断言它照样听手动的。
    /// </summary>
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

    /// <summary>
    /// 一个音都没有的时候给一个常量，不抛。
    ///
    /// 空轨在预检那一关就被拦下了（见 <c>PreflightTests.空轨也不放行</c>），但
    /// <c>AutoBaseOctave</c> 是公开的纯函数，别处（编辑器算音域）也会走它 ——
    /// 空列表上抛异常，等于把一次界面上的空状态变成一次崩溃。
    /// </summary>
    [Test]
    public void 空样本给一个常量不抛()
    {
        Assert.That(NoteMapper.AutoBaseOctave(Array.Empty<int>()), Is.EqualTo(4));
    }

    /// <summary>
    /// 移调算进「能不能弹」里：判断用的是**移调之后**的音高。
    /// 同一份样本不移调要 4、整体移高一个八度之后就该跟着变成 5，
    /// 否则移调一按，整条线的位置就错了。
    /// </summary>
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

    // ==================== 夹具 ====================

    /// <summary><paramref name="count"/> 个同一个音高的音，时间上前后排开。</summary>
    private static IEnumerable<RawNote> 音符(int pitch, int count)
    {
        for (int i = 0; i < count; i++)
            yield return new RawNote { Pitch = pitch, Start = i * 0.5, End = i * 0.5 + 0.25 };
    }
}
