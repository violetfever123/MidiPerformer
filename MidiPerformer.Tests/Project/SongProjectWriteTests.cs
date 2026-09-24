using Melanchall.DryWetMidi.Common;       // SevenBitNumber —— 只在断言音色时用到
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;   // 只用来检查写出去的文件
using MidiPerformer.Adapters.Gateways;     // SongLibrary —— 曲库那条路（存成 .mid）
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Editing;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
// 与 DryWetMidi 里的同名类型区分
using MidiReader = MidiPerformer.Core.UseCases.Project.MidiReader;
using MidiWriter = MidiPerformer.Core.UseCases.Project.MidiWriter;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 写半边：<see cref="Song"/> → MIDI 文件。
/// 代理是「再导入一次能读回同一首曲子」——走的是我们自己的读取端，而读取端的能力已由
/// <see cref="SongProjectReadTests"/> 对着原版 <c>MidiLoader</c> 钉过。
/// 比较一律逐字段、精确（tick 是整数，不用容差），帮手是 <see cref="SongAssert"/>
/// （<c>Song</c> 没有值相等，撤销装饰器要的是引用相等）。
/// </summary>
public class SongProjectWriteTests
{
    // ==================== 语料往返 ====================

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 语料导入导出再导入是同一首曲子(string path)
    {
        var song = MidiReader.Read(path);
        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        SongAssert.Same(song, again, Path.GetFileName(path));
    }

    /// <summary>变速语料的速度事件表逐条相等（红了能一眼分清是速度错了还是音符错了）。</summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.VariableTempoFiles))]
    public void 变速语料的速度事件逐条相等(string path)
    {
        var song = MidiReader.Read(path);
        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        Assert.Multiple(() =>
        {
            Assert.That(song.TempoMap.TempoChanges, Is.Not.Empty, "变速语料的速度表不该是空的");
            Assert.That(again.TempoMap.TempoChanges, Is.EqualTo(song.TempoMap.TempoChanges),
                $"{Path.GetFileName(path)}：速度事件表（tick 与微秒都要逐条相等）");
            Assert.That(again.TempoMap.TimeSignatureChanges, Is.EqualTo(song.TempoMap.TimeSignatureChanges),
                $"{Path.GetFileName(path)}：变拍事件表");
            // 速度事件写丢了的话秒数会整体飘，这条从结果那一侧再兜一次
            Assert.That(again.TempoMap.SecondsAt(song.EndTick), Is.EqualTo(song.TotalSeconds),
                $"{Path.GetFileName(path)}：整曲秒数");
        });
    }

    /// <summary>语料整体上有料，保证导出这条路真被走过，逐条比对的测试不是对空壳文件空转。</summary>
    [Test]
    public void 语料整体上导出不是空转()
    {
        MidiCorpus.AssertCorpusPresent();

        int withNotes = 0, totalNotes = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(MidiReader.Read(path)));
            if (again.Tracks.Count > 0) withNotes++;
            totalNotes += again.Tracks.Sum(t => t.NoteCount);
        }

        Assert.Multiple(() =>
        {
            Assert.That(withNotes, Is.GreaterThan(50), "导出后还有音符的语料太少，往返基本在空转");
            Assert.That(totalNotes, Is.GreaterThan(50_000), "导出后剩下的音符总数太少");
        });
    }

    /// <summary>
    /// 语料覆盖了导出会踩到的形状（序号跳号、一个轨块多声道、鼓轨、多轨块、格式 2）；
    /// 没覆盖到的形状往返测试会静默正确。格式 2 没进模型、写不回去，只验它读得回来。
    /// </summary>
    [Test]
    public void 语料覆盖了导出会踩到的形状()
    {
        // 门槛比实测低（实测 18 / 32 / 30 / 23 份），只防语料整体退化到这几条路没人走。
        var (gap, multiChannel, drums, multiChunk, format2) = CorpusShapes();

        Assert.Multiple(() =>
        {
            Assert.That(gap, Is.GreaterThan(5),
                $"只有 {gap} 份语料是「有轨块的序号空着」的（格式 1 的第 0 块常是纯速度轨），补空轨块那条路快没人走了");
            Assert.That(multiChannel, Is.GreaterThan(5),
                $"只有 {multiChannel} 份语料的一个轨块里装着多个声道");
            Assert.That(drums, Is.GreaterThan(5),
                $"只有 {drums} 份语料带 9 号声道的鼓轨（没轨名时它叫「打击乐」）");
            Assert.That(multiChunk, Is.GreaterThan(5),
                $"只有 {multiChunk} 份语料是多轨块的");
            Assert.That(format2, Is.GreaterThan(0), "语料里一份格式 2 的都没有");
        });
    }

    // ==================== 移调 ====================

    /// <summary>移调只在导出时叠加：写出去的音高 = <c>Note.Pitch + Track.Transpose</c>，源 <see cref="Song"/> 的音符一个都不动。</summary>
    [TestCase(12)]
    [TestCase(-12)]
    [TestCase(3)]
    public void 移调在导出时叠加且源音符不动(int transpose)
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60).Note(480, 480, 0, 64)));

        var shifted = song.Tracks.Single() with { Transpose = transpose };
        var source = new Song(new[] { shifted }, song.TempoMap);

        // 导出前先抄一份源音符，导出后比回来
        var before = source.Tracks.Single().Notes.ToArray();

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(source));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Single().Notes.Select(n => n.Pitch),
                Is.EqualTo(new[] { 60 + transpose, 64 + transpose }), "写出去的音高是叠加过的");
            Assert.That(again.Tracks.Single().Transpose, Is.EqualTo(0),
                "移调已经落到音高上了，文件里没有「移调」这东西，读回来当然是 0");
            Assert.That(source.Tracks.Single().Notes, Is.EqualTo(before), "源 Song 的音符一个都不许动");
            Assert.That(source.Tracks.Single().Transpose, Is.EqualTo(transpose), "源 Song 的移调还在");
        });
    }

    /// <summary>移调把音高推出 0..127 时夹到边界、不跳过，音数与时值都不丢。</summary>
    [Test]
    public void 移调后音高越界时夹住不丢音()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("顶到天花板的").Note(0, 480, 0, 125).Note(480, 480, 0, 120)));

        var shifted = new Song(
            new[]
            {
                song.Tracks.Single() with { Transpose = 10 },
                song.Tracks.Single() with { Channel = 1, Transpose = -10 }
            },
            song.TempoMap);

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(shifted));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Has.Count.EqualTo(2), "夹住的意思是音还在，不是被丢掉");
            Assert.That(again.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 127, 127 }),
                "越界的音高夹到 127");
            Assert.That(again.Tracks[1].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 115, 110 }),
                "负数方向同理");
            Assert.That(again.Tracks[0].Notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 0, 480 }),
                "夹住音高不动时值");
        });
    }

    [Test]
    public void 移调到最低端也是夹住()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("贴地的").Note(0, 480, 0, 3)));

        var shifted = new Song(new[] { song.Tracks.Single() with { Transpose = -10 } }, song.TempoMap);
        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(shifted));

        Assert.That(again.Tracks.Single().Notes.Single().Pitch, Is.EqualTo(0));
    }

    // ==================== 轨名与音色 ====================

    /// <summary>写出去的字节里直接有轨名与音色事件（不经过我们自己的读取端）。</summary>
    [Test]
    public void 写出去的文件里有轨名和音色事件()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Program(0, 0, 42).Note(0, 480, 0, 60),
            SmfTrack.Named("伴奏").Program(0, 1, 24).Note(0, 960, 1, 48)));

        byte[] bytes = MidiWriter.WriteBytes(song);
        // DryWetMidi 的默认读写编码是 ASCII，不指 UTF-8 中文轨名会读成一串问号
        var file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            TextEncoding = System.Text.Encoding.UTF8
        });
        var chunks = file.GetTrackChunks().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Has.Count.EqualTo(2), "两条轨（两个轨块）");
            Assert.That(chunks[0].Events.OfType<SequenceTrackNameEvent>().Single().Text, Is.EqualTo("主旋律"));
            Assert.That(chunks[0].Events.OfType<ProgramChangeEvent>().Single().ProgramNumber,
                Is.EqualTo((SevenBitNumber)42), "音色要写出去，不然别的软件里全是 0 号大钢琴");
            Assert.That(chunks[1].Events.OfType<SequenceTrackNameEvent>().Single().Text, Is.EqualTo("伴奏"));
            Assert.That(chunks[1].Events.OfType<ProgramChangeEvent>().Single().ProgramNumber,
                Is.EqualTo((SevenBitNumber)24));
        });
    }

    [Test]
    public void 中文轨名往返不变()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("第一小提琴·主旋律").Note(0, 480, 0, 60)));

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        Assert.That(again.Tracks.Single().Name, Is.EqualTo("第一小提琴·主旋律"));
    }

    /// <summary>格式 0 整首一个轨块、按声道分声部，同轨块的声道共用轨名；导出只写一个轨名，再导入时发给组里每个声道。</summary>
    [Test]
    public void 格式0的多声道轨名音色都还在()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(0, 480,
            SmfTrack.Named("整首")
                .Program(0, 0, 0).Note(0, 480, 0, 60)
                .Program(0, 1, 40).Note(0, 480, 1, 67).Note(480, 480, 1, 69)));

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        SongAssert.Same(song, again, "格式 0 一个轨块两个声道");

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Has.Count.EqualTo(2));
            Assert.That(again.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "整首", "整首" }),
                "同一轨块里的声道共用轨名");
            Assert.That(again.Tracks.Select(t => t.Program), Is.EqualTo(new[] { 0, 40 }), "每个声道自己的音色");
        });
    }

    // ==================== 轨块序号 ====================

    /// <summary>
    /// <see cref="Track.TrackIndex"/> 是「文件里第几个轨块」，导出要原样占住那个位置；
    /// 中间空掉的序号得补一个空轨块，否则再导入时后面所有轨的序号整体前移。
    /// </summary>
    [Test]
    public void 只有一条轨但轨块序号不为零()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("指挥轨").Tempo(0, 400_000),
            SmfTrack.Named("第二块").Note(0, 480, 0, 60),
            SmfTrack.Named("第三块").Note(480, 480, 0, 62)));

        // 第 0 块是纯速度轨不成轨，剩下两条的序号是 1 和 2
        Assert.That(song.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 1, 2 }), "前提：序号不从 0 起");

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        SongAssert.Same(song, again, "序号不从 0 起的曲子");
    }

    [Test]
    public void 轨块序号中间空掉的要补空轨块()
    {
        // 序号 1 空着
        var map = new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480));
        var song = new Song(
            new[]
            {
                new Track(0, 0, "第一条", 0, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(2, 1, "第三条", 24, new[] { new ModelNote(62, 960, 480, 90) })
            },
            map);

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 0, 2 }),
                "空掉的序号 1 要补一个空轨块，否则第三条会前移成 1");
            SongAssert.Same(song, again, "序号中间空着的曲子");
        });
    }

    // ==================== 分辨率 ====================

    /// <summary>分辨率照 <see cref="ModelTempoMap.Division"/> 写。</summary>
    [TestCase(480)]
    [TestCase(96)]
    [TestCase(1)]
    [TestCase(32767)]
    public void PPQ分辨率往返不变(int ticksPerQuarterNote)
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 7, 13, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(ticksPerQuarterNote)));

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        SongAssert.Same(song, again, $"PPQ {ticksPerQuarterNote}");
    }

    [TestCase(24, 40)]
    [TestCase(25, 40)]
    [TestCase(29, 40)]     // 29.97 drop-frame 按 29 存（模型表达不了小数帧率）
    [TestCase(30, 80)]
    public void SMPTE分辨率往返不变(int framesPerSecond, int ticksPerFrame)
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 960, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.Smpte(framesPerSecond, ticksPerFrame)));

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, $"SMPTE {framesPerSecond}×{ticksPerFrame}");
            Assert.That(again.TempoMap.Division.IsSmpte, Is.True, "写回去还得是 SMPTE，不能退化成 PPQ");
        });
    }

    // ==================== 边界 ====================

    /// <summary>空曲（0 轨）照样写一个空轨块给速度表和分辨率待，再导入时不产生 Track。</summary>
    [Test]
    public void 空曲能写出来也能读回去()
    {
        var song = new Song(
            Array.Empty<Track>(),
            new ModelTempoMap(
                ModelTimeDivision.PulsesPerQuarter(96),
                new[] { new TempoChange(0, 400_000), new TempoChange(960, 250_000) },
                new[] { new TimeSignatureChange(0, 3, 4) }));

        byte[] bytes = MidiWriter.WriteBytes(song);

        Assert.That(bytes, Is.Not.Empty, "空曲也得写出一份合法文件，不能是 0 字节");

        var again = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Is.Empty);
            SongAssert.Same(song, again, "空曲");
            Assert.That(again.TempoMap.TempoChanges, Has.Count.EqualTo(2), "空曲的速度表也要写出去");
            Assert.That(again.EndTick, Is.EqualTo(0));
        });
    }

    [Test]
    public void 单轨单音能写出来能读回去()
    {
        var song = new Song(
            new[] { new Track(0, 3, "单音", 7, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        SongAssert.Same(song, MidiReader.ReadBytes(MidiWriter.WriteBytes(song)), "单轨单音");
    }

    /// <summary>
    /// 零时长的音（按下与抬起在同一 tick）往返不变。
    /// MIDI 里没有「时长」，同 tick 上抬键若排在按键之前会配错对；
    /// <see cref="SmfTrack"/> 的约定正是抬键在前，所以这里手拼一份 Song 走导出。
    /// </summary>
    [Test]
    public void 零时长的音往返不变()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "零时长", 0, new[]
                {
                    new ModelNote(60, 0, 480, 100),
                    new ModelNote(62, 480, 0, 100),      // 按下和抬起都在 480
                    new ModelNote(64, 480, 480, 100)
                })
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(song));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Single().Notes.Select(n => n.Pitch),
                Is.EqualTo(new[] { 60, 62, 64 }), "三个音一个都不能少");
            Assert.That(again.Tracks.Single().Notes.Select(n => n.LengthTicks),
                Is.EqualTo(new long[] { 480, 0, 480 }), "零时长的音读回来还是零时长");
        });
    }

    /// <summary>分辨率写不出去时抛 InvalidDataException，消息是中文，不是截断出来的垃圾值。</summary>
    [Test]
    public void 分辨率写不出去时报清楚的错不崩()
    {
        // 帧率 26 不是 MIDI 规定的四种之一；每帧 300 tick 装不进一个字节。
        var badFrameRate = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.Smpte(26, 40)));
        var badTicksPerFrame = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.Smpte(25, 300)));

        // PPQ 的合法上限是 short 的正半区（最高位用来区分 SMPTE）
        var tooManyTicks = EmptySong(32768);

        var ex1 = Assert.Throws<InvalidDataException>(() => MidiWriter.WriteBytes(badFrameRate));
        var ex2 = Assert.Throws<InvalidDataException>(() => MidiWriter.WriteBytes(badTicksPerFrame));
        var ex3 = Assert.Throws<InvalidDataException>(() => MidiWriter.WriteBytes(tooManyTicks));

        Assert.Multiple(() =>
        {
            Assert.That(ex1!.Message, Does.Contain("SMPTE"), "错误消息得是给人看的中文");
            Assert.That(ex2!.Message, Does.Contain("SMPTE"));
            Assert.That(ex3!.Message, Does.Contain("分辨率"));
        });

        static Song EmptySong(int ticksPerQuarterNote) => new(
            Array.Empty<Track>(), new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(ticksPerQuarterNote)));
    }

    /// <summary>轨块序号为负时抛错，否则写出去会悄悄少轨。</summary>
    [Test]
    public void 轨块序号是负数时报清楚的错不崩()
    {
        var song = new Song(
            new[] { new Track(-1, 0, "无中生有", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var ex = Assert.Throws<InvalidDataException>(() => MidiWriter.WriteBytes(song));
        Assert.That(ex!.Message, Does.Contain("轨块序号"), "错误消息得是给人看的中文");
    }

    // ==================== 身份 ====================

    /// <summary>
    /// 导出不带身份，重新导入时按位置重发（标准 MIDI 里没有地方放它）；
    /// 身份只在 .mproj 那条路上存下来（见 <c>SongProjectFileTests.存盘再打开身份不变</c>）。
    /// </summary>
    [Test]
    public void 导出不带身份再导入时按位置重发()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "主旋律", 0, new[]
                {
                    new ModelNote(60, 0, 480, 100, new NoteId(1)),
                    new ModelNote(62, 3360, 720, 100, new NoteId(3))   // 伸出右切口，剪完发新号
                })
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var cut = new SongEditor().CutRange(song, 0, 1920, 3840);
        Assert.That(cut.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1, 4 }),
            "前提：剪出来的那一截拿的是新发的 4 号（3 号跟着被剪掉的那个音一起没了）");

        var again = MidiReader.ReadBytes(MidiWriter.WriteBytes(cut));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1, 2 }),
                "导出再导入：号按位置重发，文件里没有一个地方记着身份");
            SongAssert.Same(cut, again, "剪过的曲子导出再导入");
        });
    }

    // ==================== 帮手 ====================

    private static (int Gap, int MultiChannel, int Drums, int MultiChunk, int Format2) CorpusShapes()
    {
        MidiCorpus.AssertCorpusPresent();

        int gap = 0, multiChannel = 0, drums = 0, multiChunk = 0, format2 = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var song = MidiReader.Read(path);
            if (song.Tracks.Count == 0) continue;

            var indices = song.Tracks.Select(t => t.TrackIndex).Distinct().OrderBy(i => i).ToArray();
            if (indices[^1] + 1 != indices.Length) gap++;      // 序号不从 0 起，或中间空着
            if (indices[^1] > 0) multiChunk++;
            if (song.Tracks.GroupBy(t => t.TrackIndex).Any(g => g.Count() > 1)) multiChannel++;
            if (song.Tracks.Any(t => t.Channel == 9)) drums++;

            using var stream = File.OpenRead(path);
            if (MidiFile.Read(stream, new ReadingSettings { NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore })
                    .OriginalFormat == MidiFileFormat.MultiSequence)
                format2++;
        }

        return (gap, multiChannel, drums, multiChunk, format2);
    }

    // ==================== 曲库那条路：存成 .mid ====================

    /// <summary>
    /// 保存落到曲库里的是一个**真正的 MIDI 文件**：文件名是 <c>songs\&lt;名字&gt;.mid</c>，
    /// 开头是 <c>MThd</c>，DryWetMidi 认得出它 —— 不是 JSON 换了个后缀。
    ///
    /// 走的调用和 <c>MainWindow.SaveTo</c> 是同一组（<c>SongLibrary.WriteBytes</c> +
    /// <c>MidiWriter.WriteBytes</c>）：真正的保存那个方法在 <c>MainWindow</c> 里、是私有的，
    /// 单元测试碰不到它，所以这一条钉的是**落盘那一半**。
    /// </summary>
    [Test]
    public void 存进曲库的是标准MIDI文件不是JSON()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60).Note(480, 480, 0, 64)));

        using var sandbox = new LibrarySandbox();
        sandbox.Library.WriteBytes("勾指起誓", MidiWriter.WriteBytes(song));

        string path = sandbox.Library.PathOf("勾指起誓");
        byte[] bytes = File.ReadAllBytes(path);

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.EqualTo(Path.Combine(sandbox.Root, "勾指起誓.mid")),
                "落下来的名字就是曲名加 .mid（曲名 = 文件名）");
            Assert.That(File.Exists(path), Is.True);
            Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("MThd"),
                "开头得是 MIDI 的文件头，不是 '{'（JSON）");
        });

        // DryWetMidi 直接读得回来 —— 用别人的读取端确认它是标准 MIDI，不是「只有我们自己读得懂」
        var file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            TextEncoding = System.Text.Encoding.UTF8
        });

        Assert.Multiple(() =>
        {
            Assert.That(file.GetTrackChunks().Count(), Is.EqualTo(1));
            Assert.That(file.GetTrackChunks().Single().Events.OfType<SequenceTrackNameEvent>().Single().Text,
                Is.EqualTo("主旋律"));
        });
    }

    /// <summary>
    /// 打开列表里的那一首，读的是 <c>.mid</c> 那一路：曲库给出来的字节直接喂给
    /// <c>MidiReader</c> 就是那首曲子（和 <c>MainWindow.TryOpenLibrarySong</c> 同一组调用）。
    /// </summary>
    [Test]
    public void 从曲库读出来的字节就是那首曲子()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60),
            SmfTrack.Named("伴奏").Program(0, 1, 24).Note(0, 960, 1, 48)));

        using var sandbox = new LibrarySandbox();
        sandbox.Library.WriteBytes("两轨的", MidiWriter.WriteBytes(song));

        var again = MidiReader.ReadBytes(sandbox.Library.ReadBytes("两轨的"));

        SongAssert.Same(song, again, "从曲库读出来的字节");
        Assert.That(again.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "主旋律", "伴奏" }));
    }

    /// <summary>
    /// 曲库里没有这一首时，读的是曲库那句中文错误（不是 MidiReader 的「不是标准 MIDI 文件」）——
    /// 界面把它原样报出来，得能看出是「没有这首」而不是「文件坏了」。
    /// </summary>
    [Test]
    public void 曲库里没有这首时报的是曲库的话()
    {
        using var sandbox = new LibrarySandbox();

        var ex = Assert.Throws<InvalidDataException>(() => sandbox.Library.ReadBytes("没这首"));

        Assert.That(ex!.Message, Does.Contain("曲库里没有").And.Contain("没这首"));
    }

    // ==================== ⚠️ 「.mid 单独走一趟会丢什么」—— 故意断言丢 ====================
    //
    // 这一组**不是**「保证不丢」，是**把损失固定成事实**，用来证明缓存不是多余的：
    // 哪天有人想说「缓存没用，删了吧」或者「打开时干脆别读它」，这一组就指着他。
    //
    // 所以红了**不要**去「修好」它 —— 下面每一条红的都是**故意的**，
    // 对应的那件东西由下一票（`songs\.work\` 里那份 .mproj 缓存）带回来。
    // 四条路各一条：移调被烧进音高、删光的轨会消失、力度 0 被夹成 1、
    // 轨名与音色这两样在文件里的写法本身就装不下「同一声道的第二段」。

    /// <summary>
    /// 移调**拿不回来**：写出去时 <c>Note.Pitch + Track.Transpose</c> 已经烧进音高，
    /// 文件里没有「移调」这东西，读回来只能是 0。音高确实加过了 —— 所以丢的是「还能改回来」这个能力。
    /// </summary>
    [Test]
    public void 走一趟mid移调就没了但音高已经加过()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60).Note(480, 480, 0, 64)));
        var shifted = new Song(new[] { song.Tracks.Single() with { Transpose = 1 } }, song.TempoMap);

        var again = 存进曲库再读回来(shifted);

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Single().Transpose, Is.EqualTo(0),
                "**故意丢**：文件里没有「移调」，读回来是 0，再想整体降回去已经不知道原先移了多少");
            Assert.That(again.Tracks.Single().Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 61, 65 }),
                "音高确实加过了：丢的是「能改回来」，不是「没生效」");
        });
    }

    /// <summary>
    /// 音符被删光的那条轨**整条消失**：文件里它只剩一个空轨块，而导入端「没有音符的轨块不成轨」。
    /// 镜像 <c>SongProjectFileTests</c> 里那条「.mproj 会把空轨留着」。
    /// </summary>
    [Test]
    public void 走一趟mid音符删光的轨就没了()
    {
        var song = new Song(
            new[]
            {
                new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(1, 1, "被删光的那条", 24, Array.Empty<ModelNote>())
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var again = 存进曲库再读回来(song);

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks, Has.Count.EqualTo(1),
                "**故意丢**：空轨在 .mid 里活不下来，读回来只剩主旋律那条");
            Assert.That(again.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "主旋律" }));
        });
    }

    /// <summary>
    /// 力度 0 的音符读回来是 1：MIDI 里力度为 0 的按下就是抬键，写出去等于把这个音删了，
    /// 所以写出端把它夹到 1..127 的下界。
    /// </summary>
    [Test]
    public void 走一趟mid力度0变成1()
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, 480, 0) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var again = 存进曲库再读回来(song);

        Assert.That(again.Tracks.Single().Notes.Single().Velocity, Is.EqualTo(1),
            "**故意丢**：0 变成 1（夹的是下界，因为 0 在文件里是抬键、不是「很轻的一个音」）");
    }

    /// <summary>
    /// 轨名**整体等于兜底名**时，写出的文件里一条 <c>SequenceTrackNameEvent</c> 都没有 ——
    /// 因为「文件里没有轨名」正是兜底名这条规则的来源，写出去再导入时让导入端重算一遍。
    /// 所以「用户真把轨名改成了『声道 1』」和「文件里根本没写轨名」这两件事，
    /// 在 `.mid` 里**长得一模一样**。
    /// </summary>
    [Test]
    public void 走一趟mid兜底名的轨名不会被写出去()
    {
        // 0 号声道的兜底名就是「声道 1」（9 号是「打击乐」）—— 这里不用 MidiReader.DefaultTrackName，
        // 它在 Core 里是 internal，测试工程看得见它反而会把「兜底名到底是什么」这件事一起改了都不知道
        var song = new Song(
            new[] { new Track(0, 0, "声道 1", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        byte[] bytes = MidiWriter.WriteBytes(song);
        var file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            TextEncoding = System.Text.Encoding.UTF8
        });

        Assert.Multiple(() =>
        {
            Assert.That(file.GetTrackChunks().SelectMany(c => c.Events).OfType<SequenceTrackNameEvent>(),
                Is.Empty, "**故意丢**：文件里没有轨名这条事件");
            Assert.That(MidiReader.ReadBytes(bytes).Tracks.Single().Name, Is.EqualTo("声道 1"),
                "读回来还是那个名字 —— 但它是导入端算出来的，不是从文件里读出来的");
        });
    }

    /// <summary>
    /// 同一个轨块里、同一个声道的**第二段**：一个轨块只写一条轨名和一个声道的音色，
    /// 写的是 <c>group[0]</c> 那一份 —— 第二段的名字（和音色）就此丢掉，
    /// 它那些音符还会并进第一段那条轨里。
    /// </summary>
    /// <remarks>
    /// ⚠️ 工单把这条的出处记成 <c>MidiWriter.cs:132</c>（那句是「同一个声道跨轨块出现时两处各写各的、
    /// 再导入时两条轨会得同一个值」，说的是**音色**）。**轨名**丢在这条路上对应的其实是
    /// <c>MidiWriter.cs</c> 里那句「一个轨块一个轨名」+ <c>group[0].Name</c>。
    /// 两种形状都在这条测试里：同一个轨块的第二段，名字与音色一起丢。
    /// </remarks>
    [Test]
    public void 走一趟mid同一声道第二段的轨名留不住()
    {
        var song = new Song(
            new[]
            {
                // 同一轨块（TrackIndex 都是 0）、同一声道（都是 0），在两段里各叫各的名字
                new Track(0, 0, "第一段", 24, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(0, 0, "第二段", 42, new[] { new ModelNote(64, 480, 480, 100) })
            },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var again = 存进曲库再读回来(song);

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "第一段" }),
                "**故意丢**：「第二段」这个名字在文件里没地方待（一个轨块只写一条轨名）");
            Assert.That(again.Tracks.Single().Program, Is.EqualTo(24),
                "音色同理：只留住第一段那个（同一个声道的音色导入端只认第一次切换）");
            Assert.That(again.Tracks.Single().Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 64 }),
                "两段的音符都还在，只是并成了一条轨");
        });
    }

    // ==================== 帮手 ====================

    /// <summary>
    /// 一次「`.mid` 单独走一趟」：把一份曲子写进曲库再读回来。走的是**落盘那一路**
    /// （<c>SongLibrary.WriteBytes</c> / <c>ReadBytes</c> + <c>MidiWriter</c> / <c>MidiReader</c>），
    /// 和 <c>MainWindow</c> 的 <c>SaveTo</c> / <c>TryOpenLibrarySong</c> 是同一组调用。
    /// 内存里对穿一次（<c>WriteBytes</c> ⇄ <c>ReadBytes</c>）盖不住「盘上到底落了什么」。
    /// </summary>
    private static Song 存进曲库再读回来(Song song, string name = "走一趟")
    {
        using var sandbox = new LibrarySandbox();
        sandbox.Library.WriteBytes(name, MidiWriter.WriteBytes(song));

        Assert.That(File.Exists(sandbox.Library.PathOf(name)), Is.True,
            "前提：保存真的在曲库里落下一个文件，否则下面断言的是内存里那份");

        return MidiReader.ReadBytes(sandbox.Library.ReadBytes(name));
    }

    /// <summary>一份临时的曲库目录，用完就删（<c>SongLibraryTests</c> 那套 SetUp / TearDown 的 <c>using</c> 版）。</summary>
    private sealed class LibrarySandbox : IDisposable
    {
        public LibrarySandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "mp-midlib-" + Guid.NewGuid().ToString("N"));
            Library = new SongLibrary(Root);
        }

        public string Root { get; }

        public SongLibrary Library { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // 清理失败不该让测试红
            }
        }
    }

    // 逐字段比较的帮手在 SongAssert，与 .mproj 那半（SongProjectFileTests）共用同一份。
}
