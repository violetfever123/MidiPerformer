using Melanchall.DryWetMidi.Common;       // SevenBitNumber —— 只在断言音色时点名，不是拿来造文件
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;   // 这里用它来**检查**写出去的文件，不是拿来写
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// S1 缝的写半边：<see cref="Song"/> → MIDI 文件。
///
/// 「别的 MIDI 软件读得出来」这条没法自动化，代理是**再导入一次能读回同一首曲子** ——
/// 走的是我们自己的读取端，而读取端的能力已经由 <see cref="SongProjectReadTests"/> 对着原版
/// <c>MidiLoader</c> 和文件里的整数 tick 逐条钉过了。所以「读得回来」等价于「写出去的是标准形态的 MIDI」。
///
/// 比较一律**逐字段、精确**（tick 是整数，不用容差），这是 S1 缝的原话。
/// 比较帮手是 <see cref="SongAssert"/>，<c>Song</c> 刻意没有值相等（撤销装饰器要的是引用相等），只能自己比。
/// </summary>
public class SongProjectWriteTests
{
    // ==================== 语料往返 ====================

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 语料导入导出再导入是同一首曲子(string path)
    {
        var song = SongProject.Read(path);
        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        SongAssert.Same(song, again, Path.GetFileName(path));
    }

    /// <summary>
    /// 变速语料单独再跑一遍，把速度事件表**逐条**比掉。
    ///
    /// <see cref="AssertSameSong"/> 里本来也比了速度表，但那条测试红的时候得先分清是音符错了还是速度错了；
    /// 这一条把「变速曲目导出后别的软件读出来速度正确」单独拎出来盯着。
    /// </summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.VariableTempoFiles))]
    public void 变速语料的速度事件逐条相等(string path)
    {
        var song = SongProject.Read(path);
        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

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

    /// <summary>
    /// 语料整体上有料 —— 和读测试里那条同样的理由：逐条比对的测试对空壳文件是**空转**的，
    /// 「导出这条路真的被走过了」得单独有一条来盯。
    /// </summary>
    [Test]
    public void 语料整体上导出不是空转()
    {
        MidiCorpus.AssertCorpusPresent();

        int withNotes = 0, totalNotes = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var again = SongProject.ReadBytes(SongProject.WriteBytes(SongProject.Read(path)));
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
    /// 导出会踩到的那几种形状，语料里真的都有。
    ///
    /// 为什么要有这一条：往返测试对「语料没覆盖到的形状」是**静默正确**的。
    /// 尤其是轨块序号跳号 —— 那条路（中间补空轨块）要是没有语料走到，写错了也永远是绿的。
    /// 格式 2 那份是**故意写不回去**的（格式没进模型），走一遍只为证明它不会读不回来。
    /// </summary>
    [Test]
    public void 语料覆盖了导出会踩到的形状()
    {
        // 门槛故意压得比实测低（实测 63 份里有 18 / 32 / 30 / 23 份），
        // 只是不让语料哪天整体退化到这几条路没人走。
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

    /// <summary>
    /// 移调**在导出时才叠加**：写出去的音高 = <c>Note.Pitch + Track.Transpose</c>，
    /// 而源 <see cref="Song"/> 的音符一个字节都不动。
    ///
    /// 后一半同样是验收条目：「移调是轨的属性，永远不落进音符」这条约束靠它盯着。
    /// </summary>
    [TestCase(12)]      // 往上一个八度
    [TestCase(-12)]     // 往下一个八度
    [TestCase(3)]
    public void 移调在导出时叠加且源音符不动(int transpose)
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60).Note(480, 480, 0, 64)));

        var shifted = song.Tracks.Single() with { Transpose = transpose };
        var source = new Song(new[] { shifted }, song.TempoMap);

        // 导出前先抄一份源音符，导出后逐条比回来
        var before = source.Tracks.Single().Notes.ToArray();

        var again = SongProject.ReadBytes(SongProject.WriteBytes(source));

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

    /// <summary>
    /// 移调把音高推出 0..127 时**夹到边界，不跳过**。
    ///
    /// 取舍写在 <c>SongProject.ClampPitch</c> 的注释里：导出物要被人编辑、被别的软件读，
    /// 夹住至少保住音数、时值和节奏，跳过则是静默丢音、用户在导出结果里找不到少了哪儿。
    /// 这里把「不丢音」这件事钉死。
    /// </summary>
    [Test]
    public void 移调后音高越界时夹住不丢音()
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("顶到天花板的").Note(0, 480, 0, 125).Note(480, 480, 0, 120)));

        var shifted = new Song(
            new[]
            {
                song.Tracks.Single() with { Transpose = 10 },   // 125+10 → 夹到 127，120+10 → 130 也夹到 127
                song.Tracks.Single() with { Channel = 1, Transpose = -10 }  // 往下：125-10=115，120-10=110
            },
            song.TempoMap);

        var again = SongProject.ReadBytes(SongProject.WriteBytes(shifted));

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
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("贴地的").Note(0, 480, 0, 3)));

        var shifted = new Song(new[] { song.Tracks.Single() with { Transpose = -10 } }, song.TempoMap);
        var again = SongProject.ReadBytes(SongProject.WriteBytes(shifted));

        Assert.That(again.Tracks.Single().Notes.Single().Pitch, Is.EqualTo(0));
    }

    // ==================== 轨名与音色 ====================

    /// <summary>
    /// 轨名与音色一起写出去。这里换一个角度验：不信我们自己的读取端，直接看写出去的字节里
    /// 有没有那两个事件 —— 「别的 MIDI 软件读得出来轨名和音色」靠的就是它们。
    /// </summary>
    [Test]
    public void 写出去的文件里有轨名和音色事件()
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Program(0, 0, 42).Note(0, 480, 0, 60),
            SmfTrack.Named("伴奏").Program(0, 1, 24).Note(0, 960, 1, 48)));

        byte[] bytes = SongProject.WriteBytes(song);
        // 读的时候要自己指 UTF-8：DryWetMidi 的**默认**读写编码都是 ASCII，
        // 这里不指名的话中文轨名会读成一串问号，那是读数的人错了，不是写的人错了。
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

    /// <summary>中文轨名要能原样过去原样回来。默认写 ASCII 的话这里会变成一串问号。</summary>
    [Test]
    public void 中文轨名往返不变()
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("第一小提琴·主旋律").Note(0, 480, 0, 60)));

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        Assert.That(again.Tracks.Single().Name, Is.EqualTo("第一小提琴·主旋律"));
    }

    /// <summary>
    /// 格式 0 的曲子整首塞在一个轨块里靠声道分声部，同一个轨块里的几个声道**共用轨名**。
    /// 导出时只写一个轨名（取该组第一条），再导入时它发给组里每个声道，名字就还原了。
    /// </summary>
    [Test]
    public void 格式0的多声道轨名音色都还在()
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(0, 480,
            SmfTrack.Named("整首")
                .Program(0, 0, 0).Note(0, 480, 0, 60)
                .Program(0, 1, 40).Note(0, 480, 1, 67).Note(480, 480, 1, 69)));

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

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
    /// <see cref="Track.TrackIndex"/> 是「文件里第几个轨块」，导出必须原样占住那个位置。
    ///
    /// 中间空掉的序号要补一个空轨块：不补的话再导入时后面所有轨的序号会整体前移
    /// （导入端「没有音符的轨块不产生 Track，但序号照样往前走」）。
    /// </summary>
    [Test]
    public void 只有一条轨但轨块序号不为零()
    {
        var song = SongProject.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("指挥轨").Tempo(0, 400_000),
            SmfTrack.Named("第二块").Note(0, 480, 0, 60),
            SmfTrack.Named("第三块").Note(480, 480, 0, 62)));

        // 导入后第 0 块（纯速度轨）不成轨，剩下两条的序号是 1 和 2
        Assert.That(song.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 1, 2 }), "前提：序号不从 0 起");

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        SongAssert.Same(song, again, "序号不从 0 起的曲子");
    }

    [Test]
    public void 轨块序号中间空掉的要补空轨块()
    {
        // 手工拼一份：序号 0 和 2 有轨，序号 1 空着 —— 正是「第 0 轨块是纯速度轨」那类曲子的形状
        var map = new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480));
        var song = new Song(
            new[]
            {
                new Track(0, 0, "第一条", 0, new[] { new ModelNote(60, 0, 480, 100) }),
                new Track(2, 1, "第三条", 24, new[] { new ModelNote(62, 960, 480, 90) })
            },
            map);

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 0, 2 }),
                "空掉的序号 1 要补一个空轨块，否则第三条会前移成 1");
            SongAssert.Same(song, again, "序号中间空着的曲子");
        });
    }

    // ==================== 分辨率 ====================

    /// <summary>分辨率照 <see cref="ModelTempoMap.Division"/> 写，PPQ 与 SMPTE 都要能往返。</summary>
    [TestCase(480)]
    [TestCase(96)]
    [TestCase(1)]
    [TestCase(32767)]
    public void PPQ分辨率往返不变(int ticksPerQuarterNote)
    {
        var song = new Song(
            new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 7, 13, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(ticksPerQuarterNote)));

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

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

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        Assert.Multiple(() =>
        {
            SongAssert.Same(song, again, $"SMPTE {framesPerSecond}×{ticksPerFrame}");
            Assert.That(again.TempoMap.Division.IsSmpte, Is.True, "写回去还得是 SMPTE，不能退化成 PPQ");
        });
    }

    // ==================== 边界 ====================

    /// <summary>
    /// 空曲（0 轨）。**不是**写一个没有轨块的文件：速度表和分辨率得有地方待，
    /// 所以照样写一个（空的）轨块，再导入时它不产生 Track，但速度表原样回来。
    /// </summary>
    [Test]
    public void 空曲能写出来也能读回去()
    {
        var song = new Song(
            Array.Empty<Track>(),
            new ModelTempoMap(
                ModelTimeDivision.PulsesPerQuarter(96),
                new[] { new TempoChange(0, 400_000), new TempoChange(960, 250_000) },
                new[] { new TimeSignatureChange(0, 3, 4) }));

        byte[] bytes = SongProject.WriteBytes(song);

        Assert.That(bytes, Is.Not.Empty, "空曲也得写出一份合法文件，不能是 0 字节");

        var again = SongProject.ReadBytes(bytes);

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

        SongAssert.Same(song, SongProject.ReadBytes(SongProject.WriteBytes(song)), "单轨单音");
    }

    /// <summary>
    /// 零时长的音（按下和抬起在同一 tick）也要能往返。
    ///
    /// 这一条单拎出来是因为它踩的是**事件顺序**：MIDI 里没有「时长」这个东西，
    /// 抬键若排在按键之前，配对的会是别人的按下，两个音一起坏。
    ///
    /// 语料里没有这种曲子，也造不出来：<see cref="SmfTrack"/> 的约定就是「同一 tick 上抬键排在按键之前」，
    /// 那个约定本身就会把零时长的音吃掉。所以这里手拼一份 Song 走导出
    /// —— 而「零时长音的抬键要排到它自己按下之后」正是导出端要**故意破例**的地方。
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

        var again = SongProject.ReadBytes(SongProject.WriteBytes(song));

        Assert.Multiple(() =>
        {
            Assert.That(again.Tracks.Single().Notes.Select(n => n.Pitch),
                Is.EqualTo(new[] { 60, 62, 64 }), "三个音一个都不能少");
            Assert.That(again.Tracks.Single().Notes.Select(n => n.LengthTicks),
                Is.EqualTo(new long[] { 480, 0, 480 }), "零时长的音读回来还是零时长");
        });
    }

    /// <summary>
    /// 写不出来的东西要**炸出中文错误**，不能是截断出来的垃圾值。
    /// 与读取端「分辨率 0 / 每帧 0 tick 报中文错」是对称的两条。
    /// </summary>
    [Test]
    public void 分辨率写不出去时报清楚的错不崩()
    {
        // 帧率 26 不是 MIDI 规定的四种之一；每帧 300 tick 装不进一个字节。
        // （每帧 0 tick 那种在这里造不出来 —— 模型的分辨率工厂本身就只收 ≥ 1。）
        var badFrameRate = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.Smpte(26, 40)));
        var badTicksPerFrame = new Song(Array.Empty<Track>(),
            new ModelTempoMap(ModelTimeDivision.Smpte(25, 300)));

        // PPQ 的合法上限是 short 的正半区（最高位用来区分 SMPTE）
        var tooManyTicks = EmptySong(32768);

        var ex1 = Assert.Throws<InvalidDataException>(() => SongProject.WriteBytes(badFrameRate));
        var ex2 = Assert.Throws<InvalidDataException>(() => SongProject.WriteBytes(badTicksPerFrame));
        var ex3 = Assert.Throws<InvalidDataException>(() => SongProject.WriteBytes(tooManyTicks));

        Assert.Multiple(() =>
        {
            Assert.That(ex1!.Message, Does.Contain("SMPTE"), "错误消息得是给人看的中文");
            Assert.That(ex2!.Message, Does.Contain("SMPTE"));
            Assert.That(ex3!.Message, Does.Contain("分辨率"));
        });

        static Song EmptySong(int ticksPerQuarterNote) => new(
            Array.Empty<Track>(), new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(ticksPerQuarterNote)));
    }

    /// <summary>轨块序号是负数的 Song 写出去会悄悄少轨，所以宁可报错。</summary>
    [Test]
    public void 轨块序号是负数时报清楚的错不崩()
    {
        var song = new Song(
            new[] { new Track(-1, 0, "无中生有", 0, new[] { new ModelNote(60, 0, 480, 100) }) },
            new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480)));

        var ex = Assert.Throws<InvalidDataException>(() => SongProject.WriteBytes(song));
        Assert.That(ex!.Message, Does.Contain("轨块序号"), "错误消息得是给人看的中文");
    }

    // ==================== 帮手 ====================

    private static (int Gap, int MultiChannel, int Drums, int MultiChunk, int Format2) CorpusShapes()
    {
        MidiCorpus.AssertCorpusPresent();

        int gap = 0, multiChannel = 0, drums = 0, multiChunk = 0, format2 = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var song = SongProject.Read(path);
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

    // 逐字段比较的帮手在 SongAssert 里 —— .mproj 那半（SongProjectFileTests）用的是同一份：
    // 缝的两半要比的是同一个东西，比法也该是同一份实现，不然两边会各松各的。
}
