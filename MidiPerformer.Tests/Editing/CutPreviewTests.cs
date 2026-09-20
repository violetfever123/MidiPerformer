using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Editing;
using NUnit.Framework;

namespace MidiPerformer.Tests.Editing;

/// <summary>
/// 「抽掉这一段会怎么样」那份预测（轨道头上那行预览）和真跑一遍 <see cref="ISongEditor.CutRange"/>
/// 对不对得上。预测是照着命令那张分支表再走一遍写的（数数、不动音符），
/// 所以要紧的不是「数得对不对」，而是「和命令是不是还一致」。
/// </summary>
public class CutPreviewTests
{
    private readonly SongEditor _editor = new();

    /// <summary>一小节 1920 tick（480 分辨率、4/4）。</summary>
    private const long Bar = 1920;

    // ==================== 一个音一个音的算法 ====================

    /// <summary>整个在左切口之前：一个字节不动，也不算进任何一档。</summary>
    [Test]
    public void 切口之前的音什么都不算()
    {
        var notes = new[] { new Note(60, 0, 480, 100), new Note(62, 960, 960, 100) };

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Deleted, Is.EqualTo(0));
            Assert.That(preview.Trimmed, Is.EqualTo(0));
            Assert.That(preview.Shifted, Is.EqualTo(0));
            Assert.That(preview.BarsBefore, Is.EqualTo(1), "末尾落在 1920，正好一小节");
            Assert.That(preview.BarsAfter, Is.EqualTo(1), "这一刀谁都没碰到");
        });
    }

    /// <summary>紧贴切口的那一下不算越界：终点正好落在切口起点上的音是「左边那批」，不受影响。</summary>
    [Test]
    public void 终点正好在切口起点上的音不算被剪()
    {
        var notes = new[] { new Note(60, 0, Bar, 100) };

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.That(preview.Changes, Is.False);
    }

    /// <summary>整个落在区间里：删掉。</summary>
    [Test]
    public void 切口里的音算删掉()
    {
        var notes = new[]
        {
            new Note(60, 0, 480, 100),
            new Note(64, Bar, 480, 100),
            new Note(67, Bar + 960, 480, 100),
            new Note(72, Bar * 3, 480, 100)
        };

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Deleted, Is.EqualTo(2));
            Assert.That(preview.Trimmed, Is.EqualTo(0));
            Assert.That(preview.Shifted, Is.EqualTo(1), "第 3 小节后面那个要提前一小节");
            Assert.That(preview.BarsBefore, Is.EqualTo(4), "末尾在 5760+480，第 4 小节");
            Assert.That(preview.BarsAfter, Is.EqualTo(3));
        });
    }

    /// <summary>跨过左切口：在切口上剪断，留下左边那截（右边丢掉），末尾落在切口起点。</summary>
    [Test]
    public void 跨过左切口的音剪断在切口上()
    {
        var notes = new[] { new Note(60, 480, Bar * 2, 100) };   // 480 .. 4320，盖住整个 [1920, 3840)

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Deleted, Is.EqualTo(0), "「整个区间被它盖住」那种也是剪断，不是删掉");
            Assert.That(preview.Trimmed, Is.EqualTo(1));
            Assert.That(preview.Shifted, Is.EqualTo(0));
            Assert.That(preview.BarsBefore, Is.EqualTo(3), "末尾 4320");
            Assert.That(preview.BarsAfter, Is.EqualTo(1), "剪完只剩 480..1920");
        });
    }

    /// <summary>
    /// 伸出右切口：剪下外面那截、挪到左切口接上，末尾是 <c>startTick + (e - endTick)</c>。
    /// </summary>
    [Test]
    public void 伸出右切口的音挪到左切口接上()
    {
        var notes = new[] { new Note(60, Bar + 480, Bar * 2, 100) };   // 2400 .. 6240

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Deleted, Is.EqualTo(0));
            Assert.That(preview.Trimmed, Is.EqualTo(1));
            Assert.That(preview.Shifted, Is.EqualTo(0));
            Assert.That(preview.BarsAfter, Is.EqualTo(3), "1920 + (6240 - 3840) = 4320，落在第 3 小节里");
        });
    }

    /// <summary>整个在右切口之后：整体前移。</summary>
    [Test]
    public void 右切口之后的音算前移()
    {
        var notes = new[] { new Note(60, Bar * 2, 480, 100), new Note(64, Bar * 3, 480, 100) };

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Shifted, Is.EqualTo(2));
            Assert.That(preview.BarsBefore, Is.EqualTo(4), "末尾 5760+480 = 6240");
            Assert.That(preview.BarsAfter, Is.EqualTo(3));
        });
    }

    /// <summary>切口里一个音都没有、后面也没有音要挪：这一刀什么都没动（「抽掉」该灰着）。</summary>
    [Test]
    public void 谁都没碰到的切口什么都不改()
    {
        var notes = new[] { new Note(60, 0, 480, 100), new Note(64, 480, 480, 100) };

        var preview = CutPreview.Of(notes, Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Changes, Is.False);
            Assert.That(preview.BarsBefore, Is.EqualTo(1));
            Assert.That(preview.BarsAfter, Is.EqualTo(1));
        });
    }

    /// <summary>空轨：抽哪儿都一样，末尾按 0 算，也就是 1 小节（和导航条同一个口径）。</summary>
    [Test]
    public void 空轨上抽一段什么都不改()
    {
        var preview = CutPreview.Of(Array.Empty<Note>(), Bar, Bar * 2, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Changes, Is.False);
            Assert.That(preview.BarsBefore, Is.EqualTo(1));
            Assert.That(preview.BarsAfter, Is.EqualTo(1));
        });
    }

    /// <summary>负数起点夹到 0（命令那边也夹）。</summary>
    [Test]
    public void 起点是负数时按零算()
    {
        var notes = new[] { new Note(60, 0, 480, 100), new Note(64, Bar * 2, 480, 100) };

        var preview = CutPreview.Of(notes, -999, Bar, Bar);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Deleted, Is.EqualTo(1));
            Assert.That(preview.Shifted, Is.EqualTo(1));
        });
    }

    // ==================== 和命令对得上 ====================

    /// <summary>
    /// 同一份谱子先预测、再真跑一遍命令，逐条对：有没有变化、少掉几个音、这条轨剩几小节
    /// （三样都是命令自己报得出来的，对不上就当场红）。
    /// </summary>
    [TestCaseSource(nameof(切口样本))]
    public void 预测和命令逐条对得上(string name, Note[] notes, long startTick, long endTick)
    {
        var song = new Song(new[] { new Track(0, 0, "主旋律", 24, notes) },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));
        long ticksPerBar = PianoRollGeometry.BarTicks(song.TempoMap);

        var preview = CutPreview.Of(notes, startTick, endTick, ticksPerBar);
        var edited = _editor.CutRange(song, 0, startTick, endTick);

        var before = song.Tracks[0].Notes;
        var after = edited.Tracks[0].Notes;

        Assert.Multiple(() =>
        {
            Assert.That(
                ReferenceEquals(edited, song), Is.EqualTo(!preview.Changes),
                $"[{name}] 「有没有变化」说反了");

            Assert.That(
                after.Count, Is.EqualTo(before.Count - preview.Deleted),
                $"[{name}] 「删掉几个音」对不上");

            Assert.That(
                preview.BarsBefore, Is.EqualTo(PianoRollGeometry.BarCount(song.Tracks[0].EndTick, ticksPerBar)),
                $"[{name}] 「抽之前几小节」对不上");

            Assert.That(
                preview.BarsAfter, Is.EqualTo(PianoRollGeometry.BarCount(edited.Tracks[0].EndTick, ticksPerBar)),
                $"[{name}] 「抽之后几小节」对不上");

            // 每个音都得落进且只落进一档：没被碰的 + 剪短 + 删掉 + 前移 = 全部
            //（空区间是特判，本来就不走这张分支表）
            if (endTick > startTick)
            {
                int untouched = before.Count(n => n.EndTick <= Math.Max(0, startTick));
                Assert.That(
                    untouched + preview.Trimmed + preview.Deleted + preview.Shifted,
                    Is.EqualTo(before.Count),
                    $"[{name}] 有几档没数到（或者数重了）");
            }
        });
    }

    /// <summary>
    /// 拿来对账的那些切口：正中间剪、剪尾巴、长音盖住整段、空区间、一个音都不碰、起终点压在音上。
    /// </summary>
    private static IEnumerable<TestCaseData> 切口样本()
    {
        var 中间剪 = new[]
        {
            new Note(60, 0, 480, 100), new Note(62, 480, 480, 100),
            new Note(64, Bar, 480, 100), new Note(65, Bar + 960, 480, 100),
            new Note(67, Bar * 2, 480, 100), new Note(72, Bar * 3, 480, 100)
        };
        var 长音盖住 = new[] { new Note(60, 480, Bar * 2, 100), new Note(72, Bar * 3, 480, 100) };
        var 尾巴上剪 = new[]
        {
            new Note(60, 0, 480, 100), new Note(64, Bar, 480, 100), new Note(67, Bar * 2, 480, 100)
        };
        var 一个都不碰 = new[] { new Note(60, 0, 480, 100), new Note(64, 480, 480, 100) };
        var 空区间 = new[] { new Note(60, 0, 480, 100), new Note(64, Bar * 2, 480, 100) };
        var 压在音上 = new[] { new Note(60, 480, Bar, 100), new Note(67, Bar * 2 + 240, Bar, 100) };
        var 空轨 = Array.Empty<Note>();

        return new[]
        {
            new TestCaseData("正中间剪一小节", 中间剪, Bar, Bar * 2),
            new TestCaseData("正中间剪两小节", 中间剪, Bar, Bar * 3),
            new TestCaseData("从曲子头剪到末尾", 中间剪, 0, Bar * 4),
            new TestCaseData("长音盖住整个切口", 长音盖住, Bar, Bar * 2),
            new TestCaseData("剪尾巴上的两小节", 尾巴上剪, Bar, Bar * 3),
            new TestCaseData("切口里一个音都没有", 一个都不碰, Bar, Bar * 2),
            new TestCaseData("空区间（起终点相等）", 空区间, Bar, Bar),
            new TestCaseData("切口压在音的中间", 压在音上, Bar + 240, Bar * 2 + 480),
            new TestCaseData("起点负数", 压在音上, -999, Bar),
            new TestCaseData("空轨", 空轨, Bar, Bar * 2)
        };
    }
}
