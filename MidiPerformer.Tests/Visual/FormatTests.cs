using MidiPerformer.Adapters.Presenters;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 读数条与走带条上的**文案**。
///
/// 单独测一遍的理由：这些字符串是用户唯一能读到数的地方 ——
/// 卷帘上画错了还能靠眼睛发现，读数条上写错一个音名或拍位，用户会当成谱子错了。
/// 顺便把「空值不留白、不抛异常」这一类边界钉住。
/// </summary>
public class FormatTests
{
    [Test]
    public void 音高写成音名加简谱()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Pitch(60), Is.EqualTo("C4（1）"));
            Assert.That(Format.Pitch(61), Is.EqualTo("C#4（#1）"), "升号音两边都带 #");
            Assert.That(Format.Pitch(0), Is.EqualTo("C-1（1）"));
        });
    }

    [Test]
    public void 拍位和时值都写两位小数()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Beat(1), Is.EqualTo("1.00 拍"));
            Assert.That(Format.Beat(2.5), Is.EqualTo("2.50 拍"));
            Assert.That(Format.Length(0.25), Is.EqualTo("0.25 拍"));
        });
    }

    [Test]
    public void 小数点不跟着系统语言走()
    {
        // F2 的格式符会跟着当前区域性变 —— 德语环境下会写成 "1,00"，
        // 而这一条是给人对着谱子核对的，读数必须是稳定的
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.That(Format.Beat(1), Is.EqualTo("1.00 拍"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public void 小节号从一起数()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.BarNumber(1), Is.EqualTo("1"));
            Assert.That(Format.Position(3, 12), Is.EqualTo("3 / 12 小节"));
        });
    }

    [Test]
    public void 轨序号补零对齐()
    {
        Assert.That(Format.TrackNumber(1), Is.EqualTo("01"), "wireframe 里是两位的 01");
    }

    [Test]
    public void 打击乐轨直接说鼓组()
    {
        Assert.Multiple(() =>
        {
            // 9 号声道整条都是鼓组，音色号在那一轨没有意义
            Assert.That(Format.Timbre(0, 9), Is.EqualTo("标准鼓组 · 通道 10"));
            Assert.That(Format.Timbre(0, 0), Is.EqualTo("大钢琴 · GM 1"), "GM 编号从 1 起，程序里从 0 起");
            Assert.That(Format.Timbre(22, 3), Is.EqualTo("口琴 · GM 23"));
        });
    }

    [Test]
    public void 音色号越界就退回编号()
    {
        // 编不出来就别编：报一个假名字比报编号更误导人
        Assert.Multiple(() =>
        {
            Assert.That(Format.ProgramName(200), Is.EqualTo("音色 201"));
            Assert.That(Format.ProgramName(-1), Is.EqualTo("音色 0"));
        });
    }

    /// <summary>
    /// 移调那格的读数：正负号只在真有方向时才出现。
    ///
    /// 零写成 <c>0</c> 而不是 <c>+0</c> 是有意的 —— 零同时也是「没移调」这个默认状态的样子，
    /// 「+0 半音」看着像动过一手。这条单独钉住它，因为轨头上那一格是**每时每刻**都挂着的，
    /// 一个「+0」会在每一首没移调的曲子上出现。
    /// </summary>
    [Test]
    public void 移调读数只在有方向时才带符号()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Transpose(0), Is.EqualTo("0 半音"), "零就是零，不带符号");
            Assert.That(Format.Transpose(12), Is.EqualTo("+12 半音"));
            Assert.That(Format.Transpose(-12), Is.EqualTo("-12 半音"));
            Assert.That(Format.Transpose(1), Is.EqualTo("+1 半音"));
            Assert.That(Format.Transpose(-1), Is.EqualTo("-1 半音"));
            // 移调步进器一直能按，按到 int 的边上也得写得出字来，不能抛
            Assert.That(Format.Transpose(int.MinValue), Is.EqualTo("-2147483648 半音"));
            Assert.That(Format.Transpose(int.MaxValue), Is.EqualTo("+2147483647 半音"));
        });
    }

    [Test]
    public void 时长写成m比ss()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Clock(0), Is.EqualTo("0:00"));
            Assert.That(Format.Clock(65), Is.EqualTo("1:05"));
            Assert.That(Format.Clock(3599), Is.EqualTo("59:59"));
        });
    }

    [Test]
    public void 没时长的时候不写负数也不崩()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Clock(-5), Is.EqualTo("0:00"));
            Assert.That(Format.Clock(double.NaN), Is.EqualTo("0:00"));
        });
    }

    [Test]
    public void 刻度为零时不除零()
    {
        Assert.That(Format.Beats(960, 0), Is.EqualTo(0));
    }

    // ==================== 抽掉一段的预览 ====================

    /// <summary>
    /// 那一行预览是「按下抽掉之前，屏幕上唯一说得清会发生什么的地方」，所以逐字钉住。
    /// 三样都得在：哪几小节、动几个音、这条轨短几小节。
    /// </summary>
    [Test]
    public void 抽掉一段的预览把三样都说出来()
    {
        var preview = new CutPreview.Result(Deleted: 12, Trimmed: 2, Shifted: 30, BarsBefore: 96, BarsAfter: 92);

        Assert.That(
            Format.CutSummary(5, 8, 3, preview),
            Is.EqualTo("第 5–8 小节（共 4 小节）：删掉 12 个音、在切口上剪短 2 个、后面 30 个提前 4 小节"
                       + " · 第 03 轨 96 → 92 小节"));
    }

    /// <summary>
    /// 只有删、没有前移是常事（剪的是尾巴上那一段）—— 那时不该出现「后面 0 个提前 4 小节」。
    /// </summary>
    [Test]
    public void 某一档是零就不提它()
    {
        var preview = new CutPreview.Result(Deleted: 4, Trimmed: 0, Shifted: 0, BarsBefore: 8, BarsAfter: 7);

        Assert.That(
            Format.CutSummary(3, 3, 1, preview),
            Is.EqualTo("第 3–3 小节（共 1 小节）：删掉 4 个音 · 第 01 轨 8 → 7 小节"));
    }

    /// <summary>一个音都不会动的时候直说 —— 那时「抽掉」是灰的，这一行得说清为什么。</summary>
    [Test]
    public void 什么都不改的时候直说()
    {
        var preview = new CutPreview.Result(0, 0, 0, BarsBefore: 8, BarsAfter: 8);

        Assert.That(
            Format.CutSummary(5, 8, 3, preview),
            Is.EqualTo("第 5–8 小节（共 4 小节）：这一段里没有音，抽了和没抽一样"));
    }
}
