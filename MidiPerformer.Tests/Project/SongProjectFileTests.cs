using System.Text.Json;
using System.Text.RegularExpressions;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 工程文件（.mproj）的 <see cref="Song"/> + 文件头与 JSON 之间的往返，逐字段精确比对
/// （用同一个 <see cref="SongAssert"/>）；并盯住文件里不许出现算出来的派生属性。
/// </summary>
public class SongProjectFileTests
{
    private static ProjectHeader Header(string name = "测试曲", bool edited = false, string? from = null, int playable = 0) =>
        new(SongProjectFile.ProjectVersion, name, edited, from, playable);

    // ==================== 手工拼的曲子：逐字段往返 ====================

    /// <summary>一轨一个音。</summary>
    [Test]
    public void 最简的曲子往返逐字段相等()
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 12, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        SongAssert.Same(song, SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header())).Song, "最简的曲子");
    }

    /// <summary>0 轨的空曲：速度表和分辨率仍在，谱面是空的。</summary>
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

    /// <summary>变速曲的速度事件表逐条相等。</summary>
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
            // 500000（120 BPM）是默认速度，构造器会把它从表里去掉，两边都不该有它
            Assert.That(again.TempoMap.TempoChanges.Select(c => c.MicrosecondsPerQuarterNote),
                Is.EqualTo(new long[] { 400_000, 300_000 }));
        });
    }

    /// <summary>变拍事件逐条相等，一条都不能少。</summary>
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

    /// <summary>SMPTE 分辨率（帧率 × 每帧 tick）往返不变，读回来不许退化成 PPQ。</summary>
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

    /// <summary>PPQ 分辨率从 1 到 32767（MIDI 文件的上限）都原样保留。</summary>
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

    /// <summary>超长 tick 一位不差：JSON 数字是十进制文本，64 位整数不经过 double。</summary>
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

    /// <summary>移调存在轨上，音符一个字节没动。</summary>
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

    /// <summary>多轨、跳号的轨块序号、零时长的音、中文轨名一起往返。</summary>
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

    /// <summary>没有音符的轨往返之后还在，不被当成空轨块丢掉（.mproj 与 MIDI 的一处不同）。</summary>
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

    /// <summary>每一份真实语料都走一遍「导入 → 存工程 → 读工程 → 逐字段相等」。</summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 语料存成工程再读回来是同一首曲子(string path)
    {
        var song = MidiReader.Read(path);
        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, Path.GetFileName(path));
            AssertSameIds(song, again, Path.GetFileName(path));
        });
    }

    /// <summary>经 <see cref="SongProjectFile.SaveProject"/> / <see cref="SongProjectFile.LoadProject"/> 落盘再读回来也一样。</summary>
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
                AssertSameIds(song, again, Path.GetFileName(path));
                Assert.That(header.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
                Assert.That(header.Name, Is.EqualTo(Path.GetFileNameWithoutExtension(path)));
            });
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>语料整体上往返不是空转：逐条比对对空壳文件是空转的。</summary>
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
        var header = new ProjectHeader(SongProjectFile.ProjectVersion, "起风了", true, @"C:\下载\起风了.mid", 3);

        var (again, _) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, header));

        Assert.Multiple(() =>
        {
            Assert.That(again.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
            Assert.That(again.Name, Is.EqualTo("起风了"));
            Assert.That(again.Edited, Is.True);
            Assert.That(again.ImportedFrom, Is.EqualTo(@"C:\下载\起风了.mid"));
            Assert.That(again.PlayableTrackCount, Is.EqualTo(3));
        });
    }

    /// <summary>没导入来源的工程读回来仍是空。</summary>
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

    /// <summary>中文曲名与路径原样写在文件里，不转义成 \uXXXX。</summary>
    [Test]
    public void 中文在文件里是原样的字()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header("夜空中最亮的星", true, @"C:\我的谱子\星.mid"));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("夜空中最亮的星"));
            // 路径里的反斜杠被 JSON 转义成两个（"\\"）是 JSON 的规矩，与中文无关
            Assert.That(json, Does.Contain(@"我的谱子\\星.mid"));
            Assert.That(json, Does.Not.Contain("\\u"), "不该有 \\uXXXX 转义");
        });
    }

    /// <summary>写文件一律用当前版本，不照调用方手里那个数写。</summary>
    [Test]
    public void 版本号一律写当前版本()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), new ProjectHeader(99, "来自于未来", false, null, 0));

        Assert.That(json, Does.Contain($"\"Version\": {SongProjectFile.ProjectVersion}"));
    }

    /// <summary>
    /// 可弹轨数**两端**都存得住：<c>0</c>（一条都弹不了）与一个大数。
    /// 存的是**条数**而不是「能不能弹」—— 少了的那个信息再也拿不回来。
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(38)]
    [TestCase(9999)]
    public void 可弹轨数两端都存得住(int playable)
    {
        var (again, _) = SongProjectFile.ReadProject(
            SongProjectFile.WriteProject(SingleNoteSong(), Header(playable: playable)));

        Assert.That(again.PlayableTrackCount, Is.EqualTo(playable));
    }

    /// <summary>写出来的是缩进过的 JSON。</summary>
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
    /// 派生属性（EndTick / TotalSeconds / NoteCount / BeatsPerMinute / IsSmpte）一个都不写进文件；
    /// 而速度表的两张表（<c>TempoChanges</c> / <c>TimeSignatureChanges</c>）是构造器参数，必须在。
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

            // 这两个名字看着像派生属性，其实是 TempoMap 的构造器参数，是数据本身
            Assert.That(json, Does.Contain("\"TempoChanges\""), "速度表是数据，得写进文件");
            Assert.That(json, Does.Contain("\"TimeSignatureChanges\""), "变拍表是数据，得写进文件");
        });
    }

    /// <summary>文件里多出派生字段也读得回来，多出来的字段当没看见。</summary>
    [Test]
    public void 文件里多出派生字段也能读回来()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header());
        json = json.Replace("\"Tracks\"", "\"EndTick\": 999999, \"TotalSeconds\": 12.5, \"Tracks\"");

        var (_, song) = SongProjectFile.ReadProject(json);

        SongAssert.Same(SingleNoteSong(), song, "多写了派生字段的工程");
    }

    /// <summary>文件里的字段就是构造器的参数：轨、音符、速度表各写出的名字逐一对上。</summary>
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
                new[] { "Pitch", "StartTick", "LengthTicks", "Velocity", "Id" }));
            // 速度表只有「分辨率 + 两张表」三样
            Assert.That(mapFields, Is.EquivalentTo(
                new[] { "Division", "TempoChanges", "TimeSignatureChanges" }));
            Assert.That(songNode.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(
                new[] { "Tracks", "TempoMap" }));
        });
    }

    /// <summary>文件头五个字段 + Song，平铺在顶层。</summary>
    [Test]
    public void 顶层是文件头加Song()
    {
        using var document = JsonDocument.Parse(SongProjectFile.WriteProject(SingleNoteSong(), Header()));

        Assert.That(document.RootElement.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(
            new[] { "Version", "Name", "Edited", "ImportedFrom", "PlayableTrackCount", "Song" }));
    }

    // ==================== 身份 ====================

    /// <summary>存盘再打开身份一个都不换：号取 7、9、11 而非 1..N，以证明合规的身份原样留着、不重发。</summary>
    [Test]
    public void 存盘再打开身份不变()
    {
        var song = SongOf(
            new ModelNote(60, 0, 480, 100, new NoteId(7)),
            new ModelNote(62, 480, 480, 100, new NoteId(9)),
            new ModelNote(64, 960, 480, 100, new NoteId(11)));

        var (_, again) = SongProjectFile.ReadProject(SongProjectFile.WriteProject(song, Header()));

        Assert.That(again.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 7, 9, 11 }),
            "身份写出去、读回来还是原来那三个号");
    }

    /// <summary>老工程没有 Id 字段时，按文件里的顺序整轨重发，号从 1 开始而不是留 0 号。</summary>
    [Test]
    public void 老工程里没有身份时按位置重发()
    {
        string json = SongProjectFile.WriteProject(SongOf(
            new ModelNote(60, 0, 480, 100, new NoteId(7)),
            new ModelNote(62, 480, 480, 100, new NoteId(9)),
            new ModelNote(64, 960, 480, 100, new NoteId(11))), Header());

        // 模拟老文件：连前面的逗号一起删，否则 JSON 里留下悬空逗号，文件连解析都过不去
        json = Regex.Replace(json, @",\s*""Id"": \d+", "");

        var (_, song) = SongProjectFile.ReadProject(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("\"Id\""), "前提：文件里真的没有 Id 字段");
            Assert.That(song.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1, 2, 3 }),
                "缺身份：按文件里的顺序整轨重发，号从 1 开始");
        });
    }

    /// <summary>文件里的身份重号时整轨重发，不硬着头皮往下传。</summary>
    [Test]
    public void 文件里的身份重号时整轨重发()
    {
        string json = SongProjectFile.WriteProject(SongOf(
            new ModelNote(60, 0, 480, 100, new NoteId(7)),
            new ModelNote(62, 480, 480, 100, new NoteId(7)),
            new ModelNote(64, 960, 480, 100, new NoteId(11))), Header());

        var (_, song) = SongProjectFile.ReadProject(json);

        Assert.That(song.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1, 2, 3 }),
            "重号的文件：整轨重发，号从 1 开始");
    }

    // ==================== 坏文件：读不回来时报中文错，不崩 ====================

    [Test]
    public void 空文件报清楚的错不崩()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(""));
        Assert.That(ex!.Message, Does.Contain("空的"), "错误消息得是给人看的中文");
    }

    /// <summary>截断的 JSON（写到一半断掉的文件）。</summary>
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

    /// <summary>版本比当前新时报错，不猜着读。</summary>
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
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace($"\"Version\": {SongProjectFile.ProjectVersion},", "");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("版本号"));
    }

    /// <summary>缺 Song 字段（不是 .mproj，或保存时没写完）。</summary>
    [Test]
    public void 缺Song字段时报清楚的错不崩()
    {
        // 版本号是好的、只是没有谱面
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

    /// <summary>分辨率缺字段，走的是 <c>TimeDivisionConverter</c> 那条路。</summary>
    [Test]
    public void 分辨率缺字段时报清楚的错不崩()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"SmpteFramesPerSecond\": 0,", "");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"), "错误消息得是给人看的中文");
    }

    [TestCase(0)]      // 除以零没有意义
    [TestCase(-480)]
    public void 分辨率是非法值时报清楚的错不崩(int ticksPerQuarterNote)
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header())
            .Replace("\"TicksPerQuarterNote\": 480", $"\"TicksPerQuarterNote\": {ticksPerQuarterNote}");

        var ex = Assert.Throws<InvalidDataException>(() => SongProjectFile.ReadProject(json));
        Assert.That(ex!.Message, Does.Contain("分辨率"));
    }

    /// <summary>分辨率不是整数（小数或字符串）也要报清楚的错。</summary>
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

    /// <summary>音符里少一个字段时报清楚的错，并说清少的是哪个。</summary>
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
    /// 音符里写着不可能的值（越界音高、0 力度、负数 tick、负数或非数字的身份）时报清楚的错；
    /// 身份缺失不算坏值，走整轨重发。
    /// </summary>
    [TestCase("\"Pitch\": 60", "\"Pitch\": 128", "音高")]
    [TestCase("\"Pitch\": 60", "\"Pitch\": -1", "音高")]
    [TestCase("\"Velocity\": 100", "\"Velocity\": 128", "力度")]
    [TestCase("\"Velocity\": 100", "\"Velocity\": -5", "力度")]
    [TestCase("\"StartTick\": 0", "\"StartTick\": -1", "起始")]
    [TestCase("\"LengthTicks\": 480", "\"LengthTicks\": -480", "时值")]
    [TestCase("\"Id\": 0", "\"Id\": -1", "身份")]
    [TestCase("\"Id\": 0", "\"Id\": \"一\"", "身份")]
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

    /// <summary>文件头缺字段不算坏文件，用缺省值补齐。</summary>
    [Test]
    public void 文件头缺字段照样读得出来()
    {
        string json = SongProjectFile.WriteProject(SingleNoteSong(), Header(playable: 7))
            .Replace("\"Name\": \"测试曲\",", "")
            .Replace("\"Edited\": false,", "")
            .Replace("\"ImportedFrom\": null,", "")
            .Replace("\"PlayableTrackCount\": 7,", "");

        var (header, song) = SongProjectFile.ReadProject(json);

        Assert.Multiple(() =>
        {
            Assert.That(header.Name, Is.Empty);
            Assert.That(header.Edited, Is.False);
            Assert.That(header.ImportedFrom, Is.Null);
            Assert.That(header.PlayableTrackCount, Is.EqualTo(0), "缺了就当没有 —— 这也是 v1 要整个判成读不出来的原因");
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
            SongProjectFile.SaveProject(SingleNoteSong(), Header("夜空中最亮的星", true, "C:\\x.mid", playable: 4), file);

            var header = SongProjectFile.TryReadProjectHeader(file);

            Assert.That(header, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(header!.Name, Is.EqualTo("夜空中最亮的星"));
                Assert.That(header.Edited, Is.True);
                Assert.That(header.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
                Assert.That(header.ImportedFrom, Is.EqualTo("C:\\x.mid"));
                Assert.That(header.PlayableTrackCount, Is.EqualTo(4), "列表那一行靠这个数说「可播放 / 不可播放」");
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// **版本 1 的工程整个读不出来** —— 这一票最重要的一条，它防的不是崩溃，是**安静地答错**。
    ///
    /// 手工造一份 v1：有 <c>Version: 1</c>、**没有** <c>PlayableTrackCount</c>（其余字段都在）。
    /// 两处版本闸门都只卡上界（<c>1 &gt; 2</c> 是假），v1 照过；而 <c>ReadPlayableTrackCount</c>
    /// 按房子规矩「缺了或类型不对都当没有」会返回 <b>0</b> ⇒ 曲库那一行显示「不可播放」，
    /// **而那首歌可能弹得了**。所以 <see cref="SongProjectFile.TryReadProjectHeader"/> 只认当前版本：
    /// v1 返回 <c>null</c>（= 读不出来 → 降级读 <c>.mid</c>），**不是**返回一个
    /// <c>PlayableTrackCount == 0</c> 的 header。
    /// </summary>
    [Test]
    public void 版本1的工程头读不出来()
    {
        string 当前版本 = SongProjectFile.WriteProject(
            SingleNoteSong(), Header("老缓存", edited: true, playable: 5));

        // 手工降级成 v1：版本号改 1、把 v2 才有的那个字段整行删掉
        string v1 = 当前版本
            .Replace($"\"Version\": {SongProjectFile.ProjectVersion},", "\"Version\": 1,")
            .Replace("\"PlayableTrackCount\": 5,", "");

        Assert.That(v1, Does.Not.Contain("PlayableTrackCount"), "前提：这份 v1 里真的没有那个字段");

        string file = Path.Combine(Path.GetTempPath(), $"mp-v1头-{Guid.NewGuid():N}.mproj");

        try
        {
            File.WriteAllText(file, v1);
            var v1头 = SongProjectFile.TryReadProjectHeader(file);

            // 反面：同一份 JSON 只是版本号是当前版本，就读得出来 ——
            // 证明上面那个 null 来自版本闸门，不是「这份 JSON 本来就坏了」
            File.WriteAllText(file, 当前版本);
            var 当前头 = SongProjectFile.TryReadProjectHeader(file);

            Assert.Multiple(() =>
            {
                Assert.That(v1头, Is.Null,
                    "v1 → 读不出来（降级读 .mid）；返回一个 PlayableTrackCount == 0 的 header 是安静地答错");
                Assert.That(当前头, Is.Not.Null, "同一份工程的当前版本照读 —— 上面那个 null 不是假绿");
                Assert.That(当前头!.PlayableTrackCount, Is.EqualTo(5));
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>坏文件返回 null 不抛：曲库列表为每首读一次头，一首读不出来不该让整个列表消失。</summary>
    [TestCase("")]
    [TestCase("{")]                                             // 截断
    [TestCase("不是 JSON")]                                       // 压根不是 JSON
    [TestCase("[1, 2, 3]")]                                     // 顶层不是对象
    [TestCase("{\"Name\": \"没有版本号\"}")]                        // 缺 Version
    [TestCase("{\"Version\": 9999, \"Name\": \"来自于未来\"}")]        // 版本比当前新
    [TestCase("{\"Version\": 1, \"Name\": \"老版本\"}")]             // 版本比当前老（没有 PlayableTrackCount）
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

    /// <summary>只读头不碰谱面：把 Song 换成一坨垃圾，头照样问得出来。</summary>
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

    /// <summary>一轨一个音的最小曲子。</summary>
    private static Song SingleNoteSong() => new(
        new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
        new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

    /// <summary>一轨多音的小曲子，音符（含身份）由调用方给。</summary>
    private static Song SongOf(params ModelNote[] notes) => new(
        new[] { new Track(0, 0, "主旋律", 0, notes) },
        new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

    /// <summary>逐条比身份：<see cref="SongAssert.Same"/> 比的是内容，不含身份。</summary>
    private static void AssertSameIds(Song expected, Song actual, string because)
    {
        Assert.That(actual.Tracks, Has.Count.EqualTo(expected.Tracks.Count), $"{because}：轨数");

        for (int t = 0; t < expected.Tracks.Count; t++)
            Assert.That(actual.Tracks[t].Notes.Select(n => n.Id.Value),
                Is.EqualTo(expected.Tracks[t].Notes.Select(n => n.Id.Value)),
                $"{because}：第 {t} 条轨的身份");
    }
}
