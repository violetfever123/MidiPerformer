using System.Reflection;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;   // GetNotes 在这里，DryWetMidi 自己也有个 Note
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
// 与 DryWetMidi 的同名类型区分
using MidiReader = MidiPerformer.Core.UseCases.Project.MidiReader;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 读半边：MIDI 文件 → <see cref="Song"/>，逐条比的是原始整数 tick。
/// </summary>
public partial class SongProjectReadTests
{
    // ==================== 真实 MIDI ====================

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的tick与文件里的整数一致(string path)
    {
        // 期望值直接来自文件的整数 tick；比的是音符、不看文本，默认 ReadingSettings 就够。
        using var stream = File.OpenRead(path);
        var file = MidiFile.Read(stream);
        var song = MidiReader.Read(path);

        int trackIndex = 0;
        foreach (var chunk in file.GetTrackChunks())
        {
            foreach (var group in chunk.GetNotes().GroupBy(n => (int)n.Channel))
            {
                var raw = group.OrderBy(n => n.Time).ToList();
                if (raw.Count == 0) continue;

                var ours = song.Tracks.First(t => t.TrackIndex == trackIndex && t.Channel == group.Key);
                for (int k = 0; k < raw.Count; k++)
                {
                    Assert.That(ours.Notes[k].StartTick, Is.EqualTo(raw[k].Time),
                        $"{Path.GetFileName(path)}：第 {k} 个音的起始 tick");
                    Assert.That(ours.Notes[k].LengthTicks, Is.EqualTo(raw[k].Length),
                        $"{Path.GetFileName(path)}：第 {k} 个音的时值 tick");
                    Assert.That(ours.Notes[k].Pitch, Is.EqualTo((int)raw[k].NoteNumber),
                        $"{Path.GetFileName(path)}：第 {k} 个音的音高");
                }
            }
            trackIndex++;
        }
    }

    /// <summary>tick 是唯一的时值真相源：转成秒再转回来必须原样回来。</summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的tick没有被秒污染(string path)
    {
        var song = MidiReader.Read(path);
        var map = song.TempoMap;

        foreach (var track in song.Tracks)
        {
            foreach (var n in track.Notes)
            {
                Assert.That(map.TickAt(map.SecondsAt(n.StartTick)), Is.EqualTo(n.StartTick),
                    $"{Path.GetFileName(path)}：起始 tick 被污染");
                Assert.That(map.TickAt(map.SecondsAt(n.EndTick)), Is.EqualTo(n.EndTick),
                    $"{Path.GetFileName(path)}：结束 tick 被污染");
            }
        }
    }

    /// <summary>
    /// 音色取自整份文件里该声道的第一次切换，不是「有音符的那个轨块里」的；
    /// 语料里有 101 条轨的音色事件不在音符所在的轨块里。
    /// </summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的音色与文件一致(string path)
    {
        using var stream = File.OpenRead(path);
        var file = MidiFile.Read(stream);
        var song = MidiReader.Read(path);

        var firstProgram = new Dictionary<int, int>();
        foreach (var chunk in file.GetTrackChunks())
            foreach (var e in chunk.Events.OfType<ProgramChangeEvent>())
                if (!firstProgram.ContainsKey((int)e.Channel))
                    firstProgram[(int)e.Channel] = (int)e.ProgramNumber;

        foreach (var track in song.Tracks)
        {
            int expected = firstProgram.TryGetValue(track.Channel, out int p) ? p : 0;
            Assert.That(track.Program, Is.EqualTo(expected),
                $"{Path.GetFileName(path)}：{track.Name}（轨{track.TrackIndex} 声道{track.Channel}）的音色");
        }
    }

    [Test]
    public void 音色放在指挥轨里也能认出来()
    {
        // 格式 1 常见写法：音色、速度集中在轨块 0，音符在轨块 1。
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("指挥轨").Tempo(0, 400_000).Program(0, 3, 42),
            SmfTrack.Named("主旋律").Note(0, 480, 3, 60));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(1));
            Assert.That(song.Tracks[0].Program, Is.EqualTo(42), "音色在另一个轨块里，不能丢");
            Assert.That(song.Tracks[0].TrackIndex, Is.EqualTo(1), "有音符的是轨块 1");
        });
    }

    [Test]
    public void 中途换音色时记第一个()
    {
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Program(0, 0, 24).Note(0, 480, 0, 60)
                .Program(960, 0, 42).Note(960, 480, 0, 62));

        Assert.That(MidiReader.ReadBytes(bytes).Tracks.Single().Program, Is.EqualTo(24));
    }

    /// <summary>语料整体上有料 —— 逐条比对的测试对空壳文件是空转的（Valid 语料里混着几个空壳）。</summary>
    [Test]
    public void 语料整体上有料()
    {
        MidiCorpus.AssertCorpusPresent();

        int withNotes = 0, totalNotes = 0, totalTracks = 0;
        foreach (var path in MidiCorpus.Files)
        {
            var song = MidiReader.Read(path);
            totalTracks += song.Tracks.Count;
            totalNotes += song.Tracks.Sum(t => t.NoteCount);
            if (song.Tracks.Count > 0) withNotes++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(withNotes, Is.GreaterThan(50), "有音符的语料太少，逐条比对基本在空转");
            Assert.That(totalTracks, Is.GreaterThan(100));
            Assert.That(totalNotes, Is.GreaterThan(50_000), "语料里的音符总数太少");
        });
    }

    /// <summary>语料里三种 SMF 格式都有，逐条读一遍就是在覆盖「格式 0 / 1 / 2 都能读」。</summary>
    [Test]
    public void 语料覆盖了SMF格式0和1和2()
    {
        MidiCorpus.AssertCorpusPresent();

        var formats = new HashSet<int>();
        foreach (var path in MidiCorpus.Files)
        {
            using var stream = File.OpenRead(path);
            var file = MidiFile.Read(stream, new ReadingSettings { NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore });
            formats.Add((int)file.OriginalFormat);
            Assert.That(() => MidiReader.Read(path), Throws.Nothing, $"{Path.GetFileName(path)} 读不进来");
        }

        Assert.That(formats, Is.SupersetOf(new[] { 0, 1, 2 }), "语料没有覆盖全三种 SMF 格式");
    }

    // ==================== 身份 ====================

    /// <summary>
    /// 导入时按顺序发身份：一轨一数，第几个音就是几号（见 <c>NoteIdentity</c>）。
    /// 身份是发的、不是按内容算的 —— 文件里两个同 tick 同音高同力度的音值相等但身份不同。
    /// </summary>
    [Test]
    public void 导入时按顺序发身份_一轨一数()
    {
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律")
                .Note(0, 480, 0, 60).Note(0, 480, 0, 60).Note(960, 480, 0, 64),
            SmfTrack.Named("伴奏").Note(0, 960, 1, 40));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks[0].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1, 2, 3 }),
                "第几个音就是几号：0 号留给「没有身份」");
            Assert.That(song.Tracks[1].Notes.Select(n => n.Id.Value), Is.EqualTo(new[] { 1 }),
                "一轨一数：另一条轨从 1 号重新开始，不是接着上一条往下数");
            Assert.That(song.Tracks[0].Notes[0], Is.EqualTo(song.Tracks[0].Notes[1]),
                "同 tick 同音高同力度的两个音，值相等");
            Assert.That(song.Tracks[0].Notes[0].Id, Is.Not.EqualTo(song.Tracks[0].Notes[1].Id),
                "值相等但身份不同 —— 分开这两者靠的正是身份");
        });
    }

    /// <summary>同一次导入可重现：同一份字节读两遍，身份一模一样（掺了随机数或全局计数器就会红）。</summary>
    [Test]
    public void 同一次导入读两遍身份一模一样()
    {
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律")
                .Note(0, 480, 0, 64).Note(480, 480, 0, 62).Note(960, 480, 0, 60),
            SmfTrack.Named("伴奏").Note(0, 960, 1, 40).Note(960, 960, 1, 43));

        var first = MidiReader.ReadBytes(bytes);
        var second = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(first.Tracks, Has.Count.EqualTo(second.Tracks.Count));
            for (int t = 0; t < first.Tracks.Count; t++)
            {
                Assert.That(second.Tracks[t].Notes.Select(n => n.Id.Value),
                    Is.EqualTo(first.Tracks[t].Notes.Select(n => n.Id.Value)),
                    $"第 {t} 条轨第二遍读出来的身份和第一遍不一样");
            }
        });
    }

    /// <summary>语料全量：每条轨上的身份正好是 1..N，不重不漏（手工造的曲子盯不住重号这种坏法）。</summary>
    [Test]
    public void 语料里每条轨的身份都是1到N()
    {
        MidiCorpus.AssertCorpusPresent();

        int tracksSeen = 0;
        foreach (var path in MidiCorpus.Files)
        {
            foreach (var track in MidiReader.Read(path).Tracks)
            {
                tracksSeen++;
                var expected = Enumerable.Range(1, track.NoteCount);
                Assert.That(track.Notes.Select(n => n.Id.Value), Is.EqualTo(expected),
                    $"{Path.GetFileName(path)}：{track.Name}（轨{track.TrackIndex} 声道{track.Channel}）的身份不是 1..N");
            }
        }

        Assert.That(tracksSeen, Is.GreaterThan(100), "一条轨都没查到，这条测试在空转");
    }

    // ==================== 手工构造的文件：格式 / 坏文件 ====================

    [Test]
    public void 格式0_单个轨块多个声道拆成多条轨()
    {
        var bytes = SmfWriter.Build(0, 480,
            SmfTrack.Named("整首")
                .Program(0, 0, 0).Note(0, 480, 0, 60)
                .Program(0, 1, 40).Note(0, 480, 1, 67)
                .Note(480, 480, 1, 69));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(2), "一个轨块里的两个声道要拆成两条轨");
            Assert.That(song.Tracks[0].Channel, Is.EqualTo(0));
            Assert.That(song.Tracks[0].Program, Is.EqualTo(0));
            Assert.That(song.Tracks[0].NoteCount, Is.EqualTo(1));
            Assert.That(song.Tracks[1].Channel, Is.EqualTo(1));
            Assert.That(song.Tracks[1].Program, Is.EqualTo(40));
            Assert.That(song.Tracks[1].NoteCount, Is.EqualTo(2));
            Assert.That(song.Tracks[0].Name, Is.EqualTo("整首"), "同一个轨块的两个声道共用轨名");
        });
    }

    [Test]
    public void 格式1_多轨块共用一个时间轴()
    {
        var bytes = SmfWriter.Build(1, 96,
            SmfTrack.Named("主旋律").Note(0, 96, 0, 72).Note(96, 96, 0, 74),
            SmfTrack.Named("伴奏").Note(48, 192, 1, 48));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(2));
            Assert.That(song.Tracks[0].TrackIndex, Is.EqualTo(0));
            Assert.That(song.Tracks[1].TrackIndex, Is.EqualTo(1));
            Assert.That(song.Tracks[0].Notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 0, 96 }));
            Assert.That(song.Tracks[1].Notes.Single().StartTick, Is.EqualTo(48),
                "轨块 1 的 tick 是相对整个文件的时间轴，不是相对它自己");
        });
    }

    [Test]
    public void 格式2_每个轨块各自独立的时序()
    {
        var bytes = SmfWriter.Build(2, 480,
            SmfTrack.Named("第一段").Note(0, 480, 0, 60),
            SmfTrack.Named("第二段").Note(960, 480, 0, 62));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(2));
            Assert.That(song.Tracks[0].Notes.Single().StartTick, Is.EqualTo(0));
            Assert.That(song.Tracks[1].Notes.Single().StartTick, Is.EqualTo(960));
        });
    }

    [Test]
    public void tick是原样的整数不经过秒()
    {
        // 分辨率 480、120BPM：第 481 tick 只能是 481，不能是秒换回来的 480 或 482。
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("t").Note(481, 7, 0, 60).Note(1_234_567, 13, 0, 62));

        var notes = MidiReader.ReadBytes(bytes).Tracks.Single().Notes;

        Assert.Multiple(() =>
        {
            Assert.That(notes[0].StartTick, Is.EqualTo(481));
            Assert.That(notes[0].LengthTicks, Is.EqualTo(7));
            Assert.That(notes[0].EndTick, Is.EqualTo(488));
            Assert.That(notes[1].StartTick, Is.EqualTo(1_234_567), "大 tick 不能因为浮点就飘");
            Assert.That(notes[1].LengthTicks, Is.EqualTo(13));
        });
    }

    [Test]
    public void 空文件报清楚的错不崩()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(Array.Empty<byte>()));
        Assert.That(ex!.Message, Does.Contain("0 字节"));
    }

    [Test]
    public void 不是MIDI的文件报清楚的错不崩()
    {
        byte[] notMidi = System.Text.Encoding.ASCII.GetBytes("PK这不是MIDI这是一个zip");
        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(notMidi));
        Assert.That(ex!.Message, Does.Contain("不是标准 MIDI 文件"));
    }

    [Test]
    public void RIFF包装的rmi能读出来()
    {
        // .rmi = RIFF 壳子套 MIDI，直接当 .mid 喂进来也得能读。
        byte[] midi = SmfWriter.Build(1, 480, SmfTrack.Named("壳里的曲子").Note(0, 480, 0, 60));

        var riff = new List<byte>();
        riff.AddRange(SmfWriter.Ascii("RIFF"));
        riff.AddRange(SmfWriter.BE32(4 + 8 + midi.Length));   // RMID + data 块头 + 数据
        riff.AddRange(SmfWriter.Ascii("RMID"));
        riff.AddRange(SmfWriter.Ascii("data"));
        riff.AddRange(SmfWriter.BE32(midi.Length));
        riff.AddRange(midi);

        var song = MidiReader.ReadBytes(riff.ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(1));
            Assert.That(song.Tracks[0].Name, Is.EqualTo("壳里的曲子"));
            Assert.That(song.Tracks[0].Notes.Single().StartTick, Is.EqualTo(0), "壳子里的 tick 要原样读出来");
        });
    }

    [Test]
    public void RIFF里没有MIDI的报清楚的错()
    {
        var riffWithoutMidi = new List<byte>();
        riffWithoutMidi.AddRange(SmfWriter.Ascii("RIFF"));
        riffWithoutMidi.AddRange(SmfWriter.BE32(4));
        riffWithoutMidi.AddRange(SmfWriter.Ascii("WAVE"));

        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(riffWithoutMidi.ToArray()));
        Assert.That(ex!.Message, Does.Contain("找不到 MIDI"));
    }

    [Test]
    public void 只有文件头没有轨道的文件不崩()
    {
        byte[] headerOnly = SmfWriter.HeaderOnly(1, 480, declaredTracks: 0);
        var song = MidiReader.ReadBytes(headerOnly);
        Assert.That(song.Tracks, Is.Empty);
        Assert.That(song.EndTick, Is.EqualTo(0));
    }

    /// <summary>
    /// 文件头只读了一半就断：<c>NotEnoughBytesPolicy.Ignore</c> 会让 <c>MidiFile.Read</c>
    /// 正常返回但 <c>TimeDivision</c> 是 null，不自己查就会漏出 DryWetMidi 的英文异常。
    /// </summary>
    [TestCase(4)]     // 只有 "MThd" 四个字节
    [TestCase(10)]    // "MThd" + 长度 + 只给了 2 字节的 body
    [TestCase(13)]    // 差一个字节的完整头
    public void 文件头残缺时报清楚的错不崩(int length)
    {
        byte[] whole = SmfWriter.HeaderOnly(1, 480, declaredTracks: 0);
        byte[] partial = whole[..length];

        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(partial));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("不是完整可用"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Not.Contain("timeDivision"), "漏出了 DryWetMidi 的英文异常");
        });
    }

    /// <summary>分辨率为 0 时拦成中文错误（DryWetMidi 照收不误，但除以零没有意义）。</summary>
    [Test]
    public void 分辨率是零时报清楚的错不崩()
    {
        byte[] zeroDivision = SmfWriter.HeaderOnly(1, division: 0, declaredTracks: 0);

        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(zeroDivision));
        Assert.That(ex!.Message, Does.Contain("分辨率"), "错误消息得是给人看的中文");
    }

    /// <summary>SMPTE 的每帧 tick 数是 0 —— 同样是除以零，同样要拦。</summary>
    [Test]
    public void SMPTE每帧零tick时报清楚的错不崩()
    {
        // SMPTE 分辨率字：高字节是负的格式号（0xE8 = −24，即 24 帧/秒），低字节是每帧 tick 数
        byte[] zeroTicksPerFrame = SmfWriter.HeaderOnly(1, division: 0xE800, declaredTracks: 0);

        var ex = Assert.Throws<InvalidDataException>(() => MidiReader.ReadBytes(zeroTicksPerFrame));
        Assert.That(ex!.Message, Does.Contain("分辨率"));
    }

    /// <summary>SMPTE 文件极少，但读到了不该崩。</summary>
    [Test]
    public void SMPTE的文件能读出来()
    {
        // 24 帧/秒、每帧 40 tick → 960 tick/秒
        byte[] bytes = SmfWriter.Build(1, 0xE828, SmfTrack.Named("SMPTE").Note(960, 960, 0, 60));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks.Single().Notes.Single().StartTick, Is.EqualTo(960), "SMPTE 的 tick 也是原样的整数");
            Assert.That(song.TempoMap.Division.IsSmpte, Is.True);
            Assert.That(song.TempoMap.SecondsAt(960), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(song.TempoMap.TickAt(1.0), Is.EqualTo(960), "SMPTE 下 tick ⇄ 秒 也得自洽");
        });
    }

    [Test]
    public void 零音符的文件读成空曲不崩()
    {
        // 有轨块、有轨名、有速度，就是没有音符（纯信息轨）
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("指挥轨").Tempo(0, 400_000).TimeSignature(0, 3, 2));

        var song = MidiReader.ReadBytes(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Is.Empty, "没有音符的轨块不成轨");
            Assert.That(song.EndTick, Is.EqualTo(0));
            Assert.That(song.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(150.0).Within(1e-9), "速度事件不该被丢掉");
            Assert.That(song.TempoMap.TimeSignatureChanges.Single().Numerator, Is.EqualTo(3));
        });
    }

    [Test]
    public void 被截断的下载能救回来()
    {
        byte[] whole = SmfWriter.Build(1, 480,
            SmfTrack.Named("主旋律").Note(0, 480, 0, 60),
            SmfTrack.Named("伴奏").Note(0, 480, 1, 48));

        // 砍掉最后一个轨块的后半截
        byte[] truncated = whole[..(whole.Length - 6)];

        var song = MidiReader.ReadBytes(truncated);

        Assert.Multiple(() =>
        {
            Assert.That(song.Tracks, Has.Count.EqualTo(1), "残缺的第二个轨块要被丢掉，完整的第一个要留下");
            Assert.That(song.Tracks[0].Name, Is.EqualTo("主旋律"));
            Assert.That(song.Tracks[0].Notes.Single().Pitch, Is.EqualTo(60));
        });
    }

    [Test]
    public void 有按下没抬起的音不崩()
    {
        var bytes = SmfWriter.Build(1, 480,
            SmfTrack.Named("断头音").Note(0, 480, 0, 60).DanglingNoteOn(960, 0, 64));

        Assert.That(() => MidiReader.ReadBytes(bytes), Throws.Nothing);
    }

    // ==================== 模型自身的约束 ====================

    [Test]
    public void Note上没有秒字段()
    {
        // 往 Note 上加 double/float/decimal 成员，就是塞进第二个时值真相源
        var offenders = new List<string>();

        foreach (var p in typeof(ModelNote).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (!IsIntegral(p.PropertyType)) offenders.Add($"属性 {p.Name}: {p.PropertyType.Name}");

        foreach (var f in typeof(ModelNote).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!IsIntegral(f.FieldType)) offenders.Add($"字段 {f.Name}: {f.FieldType.Name}");

        Assert.That(offenders, Is.Empty,
            "Note 上出现了非整数成员，tick 不再是唯一的时值表示：\n  " + string.Join("\n  ", offenders));
    }

    [Test]
    public void 模型的公开签名里没有DryWetMidi()
    {
        // DryWetMidi 由 MidiReader / MidiWriter 独占，这里盯的是模型里不出现它的类型
        Type[] model =
        {
            typeof(Song), typeof(Track), typeof(ModelNote),
            typeof(ModelTempoMap), typeof(ModelTimeDivision), typeof(TempoChange), typeof(TimeSignatureChange)
        };

        var offenders = new List<string>();
        foreach (var type in model)
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                foreach (var referenced in ReferencedTypes(member))
                    if (referenced.Namespace?.StartsWith("Melanchall", StringComparison.Ordinal) == true)
                        offenders.Add($"{type.Name}.{member.Name} → {referenced.FullName}");

        Assert.That(offenders, Is.Empty,
            "模型里漏进了 DryWetMidi 的类型：\n  " + string.Join("\n  ", offenders));
    }

    // ==================== 帮手 ====================

    /// <summary>
    /// 允许出现在 <see cref="ModelNote"/> 上的整数成员类型。
    /// <see cref="NoteId"/> 在里面：它就是一个 <c>int</c>（身份号），不表示时间。
    /// </summary>
    private static bool IsIntegral(Type t) =>
        t == typeof(NoteId) ||
        t == typeof(byte) || t == typeof(sbyte) ||
        t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) ||
        t == typeof(long) || t == typeof(ulong);

    private static IEnumerable<Type> ReferencedTypes(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo p:
                yield return p.PropertyType;
                break;
            case FieldInfo f:
                yield return f.FieldType;
                break;
            case MethodInfo m:
                yield return m.ReturnType;
                foreach (var a in m.GetParameters()) yield return a.ParameterType;
                break;
            case ConstructorInfo c:
                foreach (var a in c.GetParameters()) yield return a.ParameterType;
                break;
        }
    }

}
