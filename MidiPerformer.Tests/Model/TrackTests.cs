using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Track"/> 的相等语义：比内容，不比音符列表的引用。
/// </summary>
public class TrackTests
{
    private static Track T(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    [Test]
    public void 内容相同的两条轨相等()
    {
        // 用不同的列表类型、不同的实例：比的是内容，不是那个列表对象
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

    /// <summary>
    /// <see cref="Track.WithNotes"/> 进来什么顺序都行，出去的一定按起点升序 ——
    /// 它是编辑命令写音符的唯一一扇门。
    /// </summary>
    [Test]
    public void 换音符之后音符按起点升序()
    {
        var shuffled = new[]
        {
            new Note(60, 960, 240, 100),
            new Note(64, 0, 240, 100),
            new Note(67, 480, 240, 100),
        };

        var after = T().WithNotes(shuffled);

        Assert.Multiple(() =>
        {
            Assert.That(after.Notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 0, 480, 960 }));
            Assert.That(after.Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64, 67, 60 }),
                "只排了序：三个音还是那三个，内容一个都没变");
            Assert.That(after.Notes, Has.Count.EqualTo(3), "一个音都没丢");
            Assert.That(shuffled.Select(n => n.StartTick), Is.EqualTo(new long[] { 960, 0, 480 }),
                "传进去的那个数组一个字节都不动（外面可能还拿着它）");
        });
    }

    /// <summary>
    /// 排序是稳定排序：起点相同的音保持传进来时的先后，而它们的先后是可见的
    /// （卷帘的键盘定位顺序和导出的事件序列都读数组次序）。
    /// </summary>
    [Test]
    public void 换音符是稳定排序同起点的音保持原来的先后()
    {
        // 同起点给到 20 个：.NET 的内省排序在 16 个以下走（恰好稳定的）插入排序，
        // 数组太小的话换成不稳定排法照样过。按音高降序给，稳定排序保的是传进来的先后。
        var sameStart = new List<Note>();
        for (int i = 0; i < 20; i++) sameStart.Add(new Note(50 - i, 480, 240, 100));   // 50, 49, ... 31

        // 前后各一个起点不同的音，钉住「必须真的重排」
        var notes = new List<Note> { sameStart[0], new Note(100, 0, 240, 100) };
        notes.AddRange(sameStart.Skip(1));
        notes.Add(new Note(110, 960, 240, 100));   // 起点最大的那个，重排之后必须落到最后

        var after = T().WithNotes(notes);

        Assert.Multiple(() =>
        {
            Assert.That(after.Notes.Select(n => n.StartTick),
                Is.EqualTo(new long[] { 0 }.Concat(Enumerable.Repeat(480L, 20)).Append(960)),
                "0 起点那个挪到了最前，960 那个落到了最后：真的排过");
            Assert.That(after.Notes.Select(n => n.Pitch).Skip(1).Take(20),
                Is.EqualTo(Enumerable.Range(31, 20).Reverse()),
                "同起点的 20 个音保持传进来的先后（50, 49, ... 31）");
        });
    }
}
