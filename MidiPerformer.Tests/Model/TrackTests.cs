using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Track"/> 的相等语义。
///
/// 这一条看着琐碎，其实卡着 S1 缝：spec 要求「导入 → 导出 → 再导入，两个 <c>Song</c> 逐字段相等」。
/// <c>Track</c> 是 record，**默认生成的相等会把音符列表当引用比**，
/// 于是两份内容完全一样的轨也不相等 —— 那条 S1 测试即使实现全对也会红。
/// 所以这里把「比内容」这件事钉死。
/// </summary>
public class TrackTests
{
    private static Track T(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    [Test]
    public void 内容相同的两条轨相等()
    {
        // 刻意用不同的列表类型、不同的实例：比的是内容，不是那个列表对象
        var a = new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, 480, 100) });
        var b = new Track(0, 0, "主旋律", 24, new List<Note> { new Note(60, 0, 480, 100) });

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b), "同内容的轨必须相等，否则 S1 的「导出再导入」永远红");
            Assert.That(a == b, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()), "相等的东西哈希必须一样");
        });
    }

    [Test]
    public void 音符顺序不同就不相等()
    {
        var forward = T(new Note(60, 0, 480, 100), new Note(62, 480, 480, 100));
        var backward = T(new Note(62, 480, 480, 100), new Note(60, 0, 480, 100));

        Assert.That(forward, Is.Not.EqualTo(backward), "音符是有序的，换了顺序就是另一条轨");
    }

    [Test]
    public void 音符内容不同就不相等()
    {
        Assert.Multiple(() =>
        {
            Assert.That(T(new Note(60, 0, 480, 100)), Is.Not.EqualTo(T(new Note(61, 0, 480, 100))), "音高");
            Assert.That(T(new Note(60, 0, 480, 100)), Is.Not.EqualTo(T(new Note(60, 1, 480, 100))), "起始 tick");
            Assert.That(T(new Note(60, 0, 480, 100)), Is.Not.EqualTo(T(new Note(60, 0, 481, 100))), "时值");
            Assert.That(T(new Note(60, 0, 480, 100)), Is.Not.EqualTo(T(new Note(60, 0, 480, 101))), "力度");
            Assert.That(T(new Note(60, 0, 480, 100)), Is.Not.EqualTo(T()), "少的那个音也算数");
        });
    }

    [Test]
    public void 逐字段不同就不相等()
    {
        var baseline = new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, 480, 100) });

        Assert.Multiple(() =>
        {
            Assert.That(baseline, Is.Not.EqualTo(baseline with { TrackIndex = 1 }), "轨块序号");
            Assert.That(baseline, Is.Not.EqualTo(baseline with { Channel = 1 }), "声道");
            Assert.That(baseline, Is.Not.EqualTo(baseline with { Name = "伴奏" }), "轨名");
            Assert.That(baseline, Is.Not.EqualTo(baseline with { Program = 25 }), "音色");
            Assert.That(baseline, Is.Not.EqualTo(baseline with { Transpose = 2 }), "移调");
        });
    }

    [Test]
    public void 空轨彼此相等()
    {
        Assert.That(T(), Is.EqualTo(T()));
    }

    [Test]
    public void 换音符之后原轨不动()
    {
        var before = T(new Note(60, 0, 480, 100));
        var after = before.WithNotes(new[] { new Note(62, 0, 480, 100) });

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Not.EqualTo(after), "换过音符就是另一条轨");
            Assert.That(before.Notes.Single().Pitch, Is.EqualTo(60), "原轨必须一个字节都没动");
            Assert.That(before.Name, Is.EqualTo(after.Name), "其余字段保持不变");
        });
    }
}
