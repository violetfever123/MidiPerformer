using System.Text.Json;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// S1 缝的 .mproj 半边：<see cref="Song"/> + 文件头 ⇄ 工程文件的 JSON。
///
/// 断言的口径和 MIDI 那半（<see cref="SongProjectWriteTests"/>）完全一样：
/// **逐字段、精确、无容差** —— 用同一个 <see cref="SongAssert"/>。
/// 缝的两半比的是同一个东西，比法也该是同一份实现。
///
/// 这里还多盯一件事：文件里**不许出现算出来的属性**
/// （EndTick / TotalSeconds / NoteCount / BeatsPerMinute / IsSmpte / 那两张表）。
/// 它们是构造器参数的派生视图，写进去就是第二个真相源，读回来当成必填就更糟。
/// </summary>
public class SongProjectFileTests
{
    private static ProjectHeader Header(string name = "测试曲", bool edited = false, string? from = null) =>
        new(SongProjectFile.ProjectVersion, name, edited, from);

    // ==================== 手工拼的曲子：逐字段往返 ====================

    /// <summary>最简的一份：一轨一个音。</summary>
    [Test]
    public void 最简的曲子往返逐字段相等()
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 12, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        SongAssert.Same(song, SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header())).Song, "最简的曲子");
    }

    /// <summary>空曲（0 轨）：速度表和分辨率得有地方待，谱面可以是空的。</summary>
    [Test]
    public void 空曲往返逐字段相等()
    {
        var song = new Song(
            Array.Empty<Track>(),
            new ModelTempoMap(
                ModelTimeDivision.PulsesPerQuarter(96),
                new[] { new TempoChange(0, 400_000), new TempoChange(960, 250_000) },
                new[] { new TimeSignatureChange(0, 3, 4) }));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Is.Empty);
            SongAssert.Same(song, again, "空曲");
            Assert.That(again.TempoMap.TempoChanges, Has.Count.EqualTo(2), "空曲的速度表也要留下");
        });
    }

    /// <summary>变速曲：速度事件表逐条相等。tick → 秒只认这张表，丢一条整曲都跟着歪。</summary>
    [Test]
    public void 变速曲的速度事件表逐条相等()
    {
        var song = new Song(
            new[] { new Track(0, 0, "变速", 0, new[] { new ModelNote(60, 0, 480, 100), new ModelNote(64, 1920, 240, 90) }) },
            new ModelTempoMap(
                ModelTimeDivision.PulsesPerQuarter(480),
                new[]
                {
                    new TempoChange(0, 500_000),
                    new TempoChange(480, 400_000),
                    new TempoChange(2400, 300_000)
                }));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, "变速曲");
            // 500000（120 BPM）是默认速度，构造器会把它从表里去掉 —— 这不是丢数据，
            // 往返之后两边都不该有它
            Assert.That(again.TempoMap.TempoChanges.Select(c => c.MicrosecondsPerQuarterNote),
                Is.EqualTo(new long[] { 400_000, 300_000 }));
        });
    }

    /// <summary>中途变拍：变拍事件只影响显示的小节线，但也得逐条留下。</summary>
    [Test]
    public void 中途变拍的变拍表逐条相等()
    {
        var song = new Song(
            new[] { new Track(0, 0, "变拍", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(
                ModelTimeDivision.PulsesPerQuarter(480),
                timeSignatureChanges: new[]
                {
                    new TimeSignatureChange(0, 4, 4),
                    new TimeSignatureChange(1920, 3, 4),
                    new TimeSignatureChange(3840, 6, 8)
                }));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, "变拍曲");
            Assert.That(again.TempoMap.TimeSignatureChanges, Has.Count.EqualTo(3), "三条变拍一条都不能少");
        });
    }

    /// <summary>
    /// SMPTE 分辨率。它与 PPQ 是两种模式（帧率 × 每帧 tick，和速度表无关），
    /// 读回来**不许退化成 PPQ** —— 那是把整首曲子的时间轴换了一根。
    /// </summary>
    [TestCase(24, 40)]
    [TestCase(25, 40)]
    [TestCase(29, 40)]     // 29.97 drop-frame 按 29 存（模型表达不了小数帧率）
    [TestCase(30, 80)]
    public void SMPTE分辨率往返不变(int framesPerSecond, int ticksPerFrame)
    {
        var song = new Song(
            new[] { new Track(0, 0, "SMPTE", 0, new[] { new ModelNote(60, 960, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.Smpte(framesPerSecond, ticksPerFrame)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, $"SMPTE {framesPerSecond}×{ticksPerFrame}");
            Assert.That(again.TempoMap.Division.IsSmpte, Is.True, "读回来还得是 SMPTE");
            Assert.That(again.TempoMap.Division.SmpteFramesPerSecond, Is.EqualTo(framesPerSecond));
            Assert.That(again.TempoMap.Division.SmpteTicksPerFrame, Is.EqualTo(ticksPerFrame));
        });
    }

    /// <summary>PPQ 分辨率：从 1（每拍一格）到 32767（MIDI 文件的上限）都得原样。</summary>
    [TestCase(1)]
    [TestCase(96)]
    [TestCase(480)]
    [TestCase(960)]
    [TestCase(24576)]     // 语料里最大的那个
    [TestCase(32767)]
    public void PPQ分辨率往返不变(int ticksPerQuarterNote)
    {
        var song = new Song(
            new[] { new Track(0, 0, "PPQ", 0, new[] { new ModelNote(60, 7, 13, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(ticksPerQuarterNote)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, $"PPQ {ticksPerQuarterNote}");
            Assert.That(again.TempoMap.Division.TicksPerQuarterNote, Is.EqualTo(ticksPerQuarterNote));
        });
    }

    /// <summary>
    /// 超长 tick。JSON 里的数字是十进制文本，64 位整数原样写原样读 ——
    /// 中途若走过 double（很多序列化器这么干），最后几位就会飘，而 tick 一飘整首曲子就错位。
    /// </summary>
    [Test]
    public void 超长tick往返一位不差()
    {
        long[] ticks = { 0, 1, 4_611_686_018_427_387_903, 9_000_000_000_000_000 };
        var song = new Song(
            new[]
            {
                new Track(0, 0, "超长", 0,
                    ticks.Select(t => new ModelNote(60, t, t % 977 + 1, 64)).ToArray())
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, "超长 tick");
            Assert.That(again.Tracks.Single().Notes.Select(n => n.StartTick),
                Is.EqualTo(ticks), "每个 tick 都要一位不差");
        });
    }

    /// <summary>移调是轨的属性，不是音符的 —— 存进文件再读回来还是轨的属性，音符一个字节没动。</summary>
    [TestCase(12)]
    [TestCase(-12)]
    [TestCase(3)]
    [TestCase(-127)]
    public void 移调存在轨上不动音符(int transpose)
    {
        var notes = new[] { new ModelNote(60, 0, 480, 100), new ModelNote(64, 480, 480, 90) };
        var song = new Song(
            new[] { new Track(0, 0, "移调", 0, notes, transpose) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Single().Transpose, Is.EqualTo(transpose));
            Assert.That(again.Tracks.Single().Notes, Is.EqualTo(notes), "音符的音高不该被移调改过");
            SongAssert.Same(song, again, $"移调 {transpose}");
        });
    }

    /// <summary>多轨、轨块序号跳号、零时长的音、中文轨名 —— 一份文件里的各种形状一起来。</summary>
    [Test]
    public void 多轨与跳号的轨块序号往返不变()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "第一小提琴·主旋律", 40, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(2, 1, "伴奏", 24, new[] { new ModelNote(48, 0, 960, 80) }),
                new Track(2, 9, "打击乐", 0, new[] { new ModelNote(38, 0, 0, 100) }),   // 零时长
                new Track(7, 5, "空的", 7, Array.Empty<ModelNote>())
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, "多轨");
            Assert.That(again.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 0, 2, 2, 7 }));
            Assert.That(again.Tracks[3].NoteCount, Is.EqualTo(0), "没有音符的轨也要在");
        });
    }

    /// <summary>没有音符的轨往返之后还是「在」，而不是被当成空轨块丢掉 —— 这是 .mproj 与 MIDI 的一处不同。</summary>
    [Test]
    public void 没有音符的轨往返之后还在()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "还有音", 0, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(1, 1, "音被删光了", 24, Array.Empty<ModelNote>())
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Has.Count.EqualTo(2), "工程文件不像 MIDI 那样「没有音符的轨块不成轨」");
            Assert.That(again.Tracks[1].Name, Is.EqualTo("音被删光了"));
            SongAssert.Same(song, again, "带空轨的曲子");
        });
    }

    // ==================== 真实语料整体扫一遍 ====================

    /// <summary>
    /// 每一份真实语料都走一遍「导入 → 存工程 → 读工程 → 逐字段相等」。
    ///
    /// 这一条是 .mproj 那半最值钱的一条：真实 MIDI 里的分辨率、变速、变拍、轨名（含 GBK 的）、
    /// 大 tick 全是实测出来的形状，比手写的边界用例更歪。
    /// </summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 语料存成工程再读回来是同一首曲子(string path)
    {
        var song = MidiReader.Read(path);
        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        SongAssert.Same(song, again, Path.GetFileName(path));
    }

    /// <summary>存在盘上再读回来也一样（<see cref="SongProjectFile.SaveProject"/> / <see cref="SongProjectFile.LoadProject"/> 那条路）。</summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 语料落盘成mproj再读回来是同一首曲子(string path)
    {
        var song = MidiReader.Read(path);
        string file = Path.Combine(Path.GetTempPath(), $"mp-proj-{Guid.NewGuid():N}.mproj");

        try
        {
            SongProjectFile.SaveProject(song, Header(Path.GetFileNameWithoutExtension(path)), file);
            var (header, again) = SongProjectFile.LoadProject(file);

            Assert.Multiple(() =>
            {
                SongAssert.Same(song, again, Path.GetFileName(path));
                Assert.That(header.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
                Assert.That(header.Name, Is.EqualTo(Path.GetFileNameWithoutExtension(path)));
            });
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>
    /// 语料整体上这条往返不是空转 —— 和 MIDI 那半同样的理由：
    /// 逐条比对的测试对空壳文件是空转的（两边都是零条轨）。
    /// </summary>
    [Test]
    public void 语料整体上工程往返不是空转()
    {
        MidiCorpus.AssertCorpusPresent();

        int withNotes = 0, totalNotes = 0, variableTempo = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var song = MidiReader.Read(path);
            var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

            if (again.Tracks.Count > 0) withNotes++;
            totalNotes += again.Tracks.Sum(t => t.NoteCount);
            if (again.TempoMap.TempoChanges.Count > 1) variableTempo++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(withNotes, Is.GreaterThan(50), "往返后还有音符的语料太少");
            Assert.That(totalNotes, Is.GreaterThan(50_000), "往返后剩下的音符总数太少");
            Assert.That(variableTempo, Is.GreaterThan(10), "带变速的语料太少，速度表这条路没走到");
        });
    }

    // ==================== 文件头 ====================

    [Test]
    public void 文件头逐字段往返()
    {
        var song = SingleNoteSong();
        var header = new ProjectHeader(SongProjectFile.ProjectVersion, "起风了", true, @"C:\下载\起风了.mid");

        var (again, _) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, header));

        Assert.Multiple(() =>
        {
            Assert.That(again.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
            Assert.That(again.Name, Is.EqualTo("起风了"));
            Assert.That(again.Edited, Is.True);
            Assert.That(again.ImportedFrom, Is.EqualTo(@"C:\下载\起风了.mid"));
        });
    }

    /// <summary>没导入过的工程（从头新建的）没有来源，读回来还该是没有。</summary>
    [Test]
    public void 没有导入来源时往返还是空()
    {
        var (again, _) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(SingleNoteSong(), Header()));

        Assert.Multiple(() =>
        {
            Assert.That(again.ImportedFrom, Is.Null);
            Assert.That(again.Edited, Is.False, "没动过就是没动过");
        });
    }

    /// <summary>中文曲名与中文路径要原样躺在文件里（转义成 \uXXXX 的话 diff 就没法看了）。</summary>
    [Test]
    public void 中文在文件里是原样的字()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header("夜空中最亮的星", true, @"C:\我的谱子\星.mid"));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("夜空中最亮的星"));
            // 路径里那条反斜杠被 JSON 转义成两个（"\\"）是 JSON 本身的规矩，不是中文的问题
            Assert.That(json, Does.Contain(@"我的谱子\\星.mid"));
            Assert.That(json, Does.Not.Contain("\\u"), "不该有 \\uXXXX 转义");
        });
    }

    /// <summary>
    /// 写文件时**用当前版本**，不照调用方手里那个数写：我们只写得出一种格式，
    /// 照抄一个别的数字等于让文件声称自己是另一种格式。
    /// </summary>
    [Test]
    public void 版本号一律写当前版本()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), new ProjectHeader(99, "来自于未来", false, null));

        Assert.That(json, Does.Contain($"\"Version\": {SongProjectFile.ProjectVersion}"));
    }

    /// <summary>
    /// 工程文件是给人看的：缩进过的，diff 工具一比就是几行改动，而不是整文件重写。
    /// </summary>
    [Test]
    public void 写出来的是缩进过的JSON()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header());

        var lines = json.Split('\n');
        Assert.Multiple(() =>
        {
            Assert.That(lines.Length, Is.GreaterThan(10), "一行到底的 JSON 在 diff 里没法比");
            Assert.That(json, Does.Contain("\n"));
        });
    }

    // ==================== 文件里不许出现算出来的属性 ====================

    /// <summary>
    /// 模型上那些**算出来的**属性一个都不许写进文件。
    ///
    /// 它们全是构造器参数的派生视图（EndTick 是起点加时值算的、BeatsPerMinute 是微秒除出来的、
    /// IsSmpte 是帧率判出来的、NoteCount 就是音符的条数）。写进文件 = 第二个真相源：
    /// 改一个忘改另一个文件就自相矛盾，而读回来当成必填就更糟 —— 缺一个字段整份工程就读不回来。
    ///
    /// 反过来，**构造器的参数一个都不能少**：速度表的两张表就是参数本身
    /// （<c>TempoChanges</c> / <c>TimeSignatureChanges</c>），少了它们整首曲子就变回 120 BPM。
    /// 这一条是那份「不许写」名单的边界，所以下面正反两面都点一遍。
    /// </summary>
    [Test]
    public void 文件里没有算出来的属性()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header());

        var derived = new[] { "EndTick", "TotalSeconds", "NoteCount", "BeatsPerMinute", "IsSmpte" };

        Assert.Multiple(() =>
        {
            foreach (var name in derived)
                Assert.That(json, Does.Not.Contain($"\"{name}\""), $"{name} 是算出来的，不该进文件");

            // 这两样名字看着像派生属性，其实是 TempoMap 的构造器参数 —— 它们是数据本身，必须在
            Assert.That(json, Does.Contain("\"TempoChanges\""), "速度表是数据，得写进文件");
            Assert.That(json, Does.Contain("\"TimeSignatureChanges\""), "变拍表是数据，得写进文件");
        });
    }

    /// <summary>
    /// 反过来：把派生属性**塞进**文件也得读得回来 —— 它们不是必填，也不该被当数据收下。
    ///
    /// 这条模拟「别人手改过 / 别的版本多写了一个字段」的文件：
    /// 读取端得照着构造器参数把谱面重建出来，多出来的字段当没看见。
    /// </summary>
    [Test]
    public void 文件里多出派生字段也能读回来()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header());
        json = json.Replace("\"Tracks\"", "\"EndTick\": 999999, \"TotalSeconds\": 12.5, \"Tracks\"");

        var (_, song) = SongProjectFile.ReadProject(json);

        SongAssert.Same(SingleNoteSong(), song, "多写了派生字段的工程");
    }

    /// <summary>写的是不是模型本身：模型上多一个公开字段就该跟着进文件（不靠名单，靠构造器）。</summary>
    [Test]
    public void 文件里的字段就是构造器的参数()
    {
        var song = SingleNoteSong();
        using var document = JsonDocument.Parse(SongProjectFile.WriteProject(song, Header()));
        var songNode = document.RootElement.GetProperty("Song");

        var trackFields = songNode.GetProperty("Tracks")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
        var noteFields = songNode.GetProperty("Tracks")[0].GetProperty("Notes")[0]
            .EnumerateObject().Select(p => p.Name).ToHashSet();
        var mapFields = songNode.GetProperty("TempoMap").EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(trackFields, Is.EquivalentTo(
                new[] { "TrackIndex", "Channel", "Name", "Program", "Notes", "Transpose" }));
            Assert.That(noteFields, Is.EquivalentTo(
                new[] { "Pitch", "StartTick", "LengthTicks", "Velocity" }));
            // 速度表只留「分辨率 + 两张表」三样，没有第四样
            Assert.That(mapFields, Is.EquivalentTo(
                new[] { "Division", "TempoChanges", "TimeSignatureChanges" }));
            Assert.That(songNode.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(
                new[] { "Tracks", "TempoMap" }));
        });
    }

    /// <summary>顶层的形状：文件头四个字段 + Song，平铺在一层。</summary>
    [Test]
    public void 顶层是文件头加Song()
    {
        using var document = JsonDocument.Parse(SongProjectFile.WriteProject(SingleNoteSong(), Header()));

        Assert.That(document.RootElement.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(
            new[] { "Version", "Name", "Edited", "ImportedFrom", "Song" }));
    }

    // ==================== 坏文件：读不回来时报中文错，不崩 ====================

    [Test]
    public void 空文件报清楚的错不崩()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(""));
        Assert.That(ex!.Message, Does.Contain("空的"), "错误消息得是给人看的中文");
    }

    /// <summary>截断的 JSON：写到一半断掉的文件就长这样。</summary>
    [Test]
    public void 截断的JSON报清楚的错不崩()
    {
        string whole = SongProjectFile.WriteProject(SingleNoteSong(), Header());
        string half = whole[..(whole.Length / 2)];

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(half));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("不是合法的 JSON"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Not.Contain("JsonReaderException"), "漏出了 STJ 的异常类型名");
        });
    }

    [Test]
    public void 不是JSON的文件报清楚的错不崩()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject("PK\u0003\u0004这不是 JSON，是个 zip"));
        Assert.That(ex!.Message, Does.Contain("JSON"));
    }

    [Test]
    public void 顶层不是对象的报清楚的错不崩()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject("[1, 2, 3]"));
        Assert.That(ex!.Message, Does.Contain("不是一个 JSON 对象"));
    }

    /// <summary>版本比当前新：不猜着读。猜出来的谱面比读不出来更坏。</summary>
    [Test]
    public void 版本比当前新时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace($"\"Version\": {SongProjectFile.ProjectVersion}", $"\"Version\": {SongProjectFile.ProjectVersion + 1}");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("更新版本"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Contain($"{SongProjectFile.ProjectVersion + 1}"), "说清读到的是哪个版本");
        });
    }

    [Test]
    public void 没有版本号时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header()).Replace("\"Version\": 1,", "");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("版本号"));
    }

    /// <summary>缺 Song：这不是 .mproj，或者保存时没写完。</summary>
    [Test]
    public void 缺Song字段时报清楚的错不崩()
    {
        // 版本号是好的、就是没有谱面 —— 得说「没有 Song」，而不是笼统地说文件坏了
        string json = $"{{\"Version\": {SongProjectFile.ProjectVersion}, \"Name\": \"只有文件头\"}}";

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("Song"));
    }

    [Test]
    public void Song是null时报清楚的错不崩()
    {
        string json = $"{{\"Version\": {SongProjectFile.ProjectVersion}, \"Song\": null}}";

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("Song"));
    }

    /// <summary>分辨率缺字段 / 值非法 —— 走的是 <c>TimeDivisionConverter</c> 那条路。</summary>
    [Test]
    public void 分辨率缺字段时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"SmpteFramesPerSecond\": 0,", "");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"), "错误消息得是给人看的中文");
    }

    [TestCase(0)]      // 除以零没有意义（MIDI 读取端也拦这一条）
    [TestCase(-480)]
    public void 分辨率是非法值时报清楚的错不崩(int ticksPerQuarterNote)
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"TicksPerQuarterNote\": 480", $"\"TicksPerQuarterNote\": {ticksPerQuarterNote}");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"));
    }

    /// <summary>分辨率不是个整数（被手改成了小数或字符串）也要说清楚，而不是崩在转换里。</summary>
    [TestCase("480.5")]
    [TestCase("\"480\"")]
    public void 分辨率不是整数时报清楚的错不崩(string written)
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"TicksPerQuarterNote\": 480", $"\"TicksPerQuarterNote\": {written}");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"));
    }

    [Test]
    public void 两种分辨率模式同时有值时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"SmpteFramesPerSecond\": 0", "\"SmpteFramesPerSecond\": 25")
            .Replace("\"SmpteTicksPerFrame\": 0", "\"SmpteTicksPerFrame\": 40");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"));
    }

    /// <summary>音符里少一个字段：这是一份被改坏的工程，得说清是谱面读不出来。</summary>
    [Test]
    public void 音符缺字段时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"Velocity\": 100", "\"Strength\": 100");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("谱面读不出来"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Contain("Velocity"), "得说清少的是哪个字段");
        });
    }

    /// <summary>
    /// 音符里写着物理上不可能的值。
    ///
    /// 为什么这类也要拦：STJ 对**缺字段**是悄悄补 0 的（所以 <c>Note</c> 有自己的转换器），
    /// 而 0 力度 / 0 时值 / 负数 tick 都是「读出来了一个错的谱面还告诉用户没问题」。
    /// </summary>
    [TestCase("\"Pitch\": 60", "\"Pitch\": 128", "音高")]
    [TestCase("\"Pitch\": 60", "\"Pitch\": -1", "音高")]
    [TestCase("\"Velocity\": 100", "\"Velocity\": 128", "力度")]
    [TestCase("\"Velocity\": 100", "\"Velocity\": -5", "力度")]
    [TestCase("\"StartTick\": 0", "\"StartTick\": -1", "起始")]
    [TestCase("\"LengthTicks\": 480", "\"LengthTicks\": -480", "时值")]
    public void 音符的值不合法时报清楚的错不崩(string from, string to, string because)
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header()).Replace(from, to);

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("音符"), "错误消息得说清是音符的问题");
            Assert.That(ex.Message, Does.Contain(because));
        });
    }

    /// <summary>文件头缺字段**不算坏文件**：曲名从文件名来，改过没改过缺省就是没动过。</summary>
    [Test]
    public void 文件头缺字段照样读得出来()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"Name\": \"测试曲\",", "")
            .Replace("\"Edited\": false,", "")
            .Replace("\"ImportedFrom\": null,", "");

        var (header, song) = SongProjectFile.ReadProject(json);

        Assert.Multiple(() =>
        {
            Assert.That(header.Name, Is.Empty);
            Assert.That(header.Edited, Is.False);
            Assert.That(header.ImportedFrom, Is.Null);
            SongAssert.Same(SingleNoteSong(), song, "文件头缺字段的工程");
        });
    }

    // ==================== 盘上的几种坏法 ====================

    [Test]
    public void 文件不存在时报清楚的错不崩()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"mp-没有这个文件-{Guid.NewGuid():N}.mproj");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.LoadProject(missing));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("读不出来"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Contain(Path.GetFileName(missing)), "得说清是哪个文件");
        });
    }

    [Test]
    public void 盘上是空文件时报清楚的错不崩()
    {
        string file = Path.Combine(Path.GetTempPath(), $"mp-空-{Guid.NewGuid():N}.mproj");
        File.WriteAllText(file, "");

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.LoadProject(file));
            Assert.That(ex!.Message, Does.Contain("空的"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void 存出去的工程Load回来是一样的()
    {
        var song = SingleNoteSong();
        string file = Path.Combine(Path.GetTempPath(), $"mp-存读-{Guid.NewGuid():N}.mproj");

        try
        {
            SongProjectFile.SaveProject(song, Header("存读", true, "从哪里来"), file);
            var (header, again) = SongProjectFile.LoadProject(file);

            Assert.Multiple(() =>
            {
                SongAssert.Same(song, again, "存了再读");
                Assert.That(header.Name, Is.EqualTo("存读"));
                Assert.That(header.Edited, Is.True);
                Assert.That(header.ImportedFrom, Is.EqualTo("从哪里来"));
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ==================== TryReadProjectHeader：曲库列表用的那一个 ====================

    [Test]
    public void 读文件头能读到曲名和改过没改过()
    {
        string file = Path.Combine(Path.GetTempPath(), $"mp-头-{Guid.NewGuid():N}.mproj");

        try
        {
            SongProjectFile.SaveProject(SingleNoteSong(), Header("夜空中最亮的星", true, "C:\\x.mid"), file);

            var header = SongProjectFile.TryReadProjectHeader(file);

            Assert.That(header, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(header!.Name, Is.EqualTo("夜空中最亮的星"));
                Assert.That(header.Edited, Is.True);
                Assert.That(header.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
                Assert.That(header.ImportedFrom, Is.EqualTo("C:\\x.mid"));
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// 坏文件返回 null，**不抛** —— 曲库列表为每一首读一次头，
    /// 一首读不出来的不能让整个列表消失：用户得有机会把它删掉。
    /// </summary>
    [TestCase("")]
    [TestCase("{")]                                             // 截断
    [TestCase("不是 JSON")]                                       // 压根不是 JSON
    [TestCase("[1, 2, 3]")]                                     // 顶层不是对象
    [TestCase("{\"Name\": \"没有版本号\"}")]                        // 缺 Version
    [TestCase("{\"Version\": 9999, \"Name\": \"来自于未来\"}")]        // 版本比当前新
    [TestCase("{\"Version\": \"一\", \"Name\": \"版本号不是数\"}")]     // 版本号类型不对
    public void 坏文件的文件头问不出来但不崩(string content)
    {
        string file = Path.Combine(Path.GetTempPath(), $"mp-坏头-{Guid.NewGuid():N}.mproj");
        File.WriteAllText(file, content);

        try
        {
            Assert.That(SongProjectFile.TryReadProjectHeader(file), Is.Null);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void 文件不在时问文件头也不崩()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"mp-没有这个-{Guid.NewGuid():N}.mproj");
        Assert.That(SongProjectFile.TryReadProjectHeader(missing), Is.Null);
    }

    /// <summary>
    /// 只读头，不碰谱面：把 Song 换成一坨垃圾，头照样问得出来。
    ///
    /// 这一条盯的是「曲库列表不必为了显示曲名把整首曲子反序列化一遍」——
    /// 列表要为每一首读一次，读整棵树是白花的钱。
    /// </summary>
    [Test]
    public void 问文件头时不碰谱面()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header("只读头"))
            .Replace("\"Song\": {", "\"Song\": \"这不是谱面\", \"扔掉\": {");

        string file = Path.Combine(Path.GetTempPath(), $"mp-只读头-{Guid.NewGuid():N}.mproj");
        File.WriteAllText(file, json);

        try
        {
            var header = SongProjectFile.TryReadProjectHeader(file);

            Assert.That(header, Is.Not.Null);
            Assert.That(header!.Name, Is.EqualTo("只读头"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ==================== 帮手 ====================

    /// <summary>一轨一个音的最小曲子，好几条测试拿它当素材。</summary>
    private static Song SingleNoteSong() => new(
        new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
        new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));
}
