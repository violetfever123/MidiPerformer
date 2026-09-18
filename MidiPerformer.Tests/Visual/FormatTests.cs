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
            Assert.That(Format.BarRange(2, 5, 12), Is.EqualTo("第 2–5 小节 / 共 12"));
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

    [Test]
    public void 读数条的选中格()
    {
        Assert.That(Format.Selection(1, 60, 1.0), Is.EqualTo("轨 01 · C4 · 1.00 拍"));
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
}
