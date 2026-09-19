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

    /// <summary>
    /// <b>「按起点升序」这条不变量收在 <see cref="Track.WithNotes"/> 这里</b>：进来什么顺序都行，
    /// 出去的一定有序 —— 而它是编辑命令写音符的**唯一**一扇门
    /// （<c>MoveNotes</c> / <c>SetNoteSpan</c> / <c>DeleteNotes</c> / <c>CutRange</c> 四条全走它）。
    ///
    /// 从前这条不变量散在调用点那一侧：挪音符和改时值各写一遍 <c>OrderBy</c>，
    /// 而剪一段靠一段「新的起点是旧起点的单调不减函数」的论证才敢不排。
    /// 同一条不变量三处各管各的，漏掉一处的后果（导出写成「后一个音先响」、
    /// 从前还有「按下标认音的地方全部错位」—— 那一条随 31 号工单没了，见 <see cref="NoteRef"/>）
    /// 又一声不吭 —— 所以这里把它钉死在新家里。
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
    /// 排序是**稳定**排序：起点相同的音保持传进来时的先后。
    ///
    /// 换成不稳定的排法这一条就红，而后果是：分不出先后的那几个音之间「谁在前」是**可见的** ——
    /// 卷帘的键盘定位顺序（定位表最后一级就是数组次序）和导出的事件序列都读它。
    /// 不稳定的话，「按两次方向键走到的地方不一样」这种回归只在某些数组长度上偶发。
    ///
    /// （这条性质**从前**是给界面那套「按值重新认音」留的余地：同起点的音一洗，
    /// 按下标重新算出来的选中集就凭空丢掉几个。那套东西 31 号工单整个删了 ——
    /// 界面现在按 <see cref="Note.Id"/> 认音，重排一个下标都不影响它。
    /// 理由换过一次，留着这段是说清楚「别再以为没用了」。）
    ///
    /// 同起点的音**给到 20 个**，不是随口凑的数：分不出先后的三两个音镇不住这件事 ——
    /// 随手换成不稳定的排法（比如 <c>List.Sort</c>）在小数组上照样是对的，
    /// 因为 .NET 的内省排序在 16 个以下走插入排序，而插入排序恰好是稳定的，
    /// 那样这条测试就成了摆设。20 个正好越过那条线，而且**刻意按音高降序**给：
    /// 稳定排序保的是「传进来的先后」，不是音高、也不是任何别的次序。
    ///
    /// 前后各塞一个起点不同的音（0 和 960），把「必须真的重排」这件事也钉住 ——
    /// 不然一个干脆不排的实现也能让这 20 个原样通过。
    /// </summary>
    [Test]
    public void 换音符是稳定排序同起点的音保持原来的先后()
    {
        var sameStart = new List<Note>();
        for (int i = 0; i < 20; i++) sameStart.Add(new Note(50 - i, 480, 240, 100));   // 50, 49, ... 31

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
