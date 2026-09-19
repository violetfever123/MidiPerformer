using System.Reflection;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;   // TrackChunk.GetNotes 住在这儿，它自己也有个 Note
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
// DryWetMidi 也有同名的 MidiReader，不加别名就分不清说的是哪一边
using MidiReader = MidiPerformer.Core.UseCases.Project.MidiReader;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// S1 缝的读半边：MIDI 文件 → <see cref="Song"/>。
///
/// 「tick 精确」不是修辞：这里逐条比的是原始整数 tick，任何一个字段被秒污染过都会现形。
/// </summary>
public class SongProjectReadTests
{
    // ==================== 真实 MIDI ====================

    /// <summary>
    /// 轨的骨架（几条、哪条、叫什么、哪些音）与原版 <c>MidiLoader</c> 逐条对齐。
    ///
    /// 用原版当参照而不是自己拿 DryWetMidi 算一遍：轨名的解码规则（严格 UTF-8 → GBK → Latin1）、
    /// 没轨名时的兜底名、按声道拆轨的顺序，这些都是**从原版逐字搬过来的**，
    /// 拿它当尺子才量得出「搬歪了没有」。自己再写一遍等于把同一份规则抄两遍，抄错了一起错。
    /// </summary>
    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的轨与原版逐条对齐(string path)
    {
        var expected = HarpAutoPlayer.Midi.MidiLoader.Parse(path).Candidates;
        var song = MidiReader.Read(path);

        Assert.That(song.Tracks.Count, Is.EqualTo(expected.Count),
            $"{Path.GetFileName(path)}：轨数不对（一个轨块里的每个声道算一条轨）");

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = song.Tracks[i];

            Assert.That(a.TrackIndex, Is.EqualTo(e.TrackIndex), $"第 {i} 条轨的轨块序号");
            Assert.That(a.Channel, Is.EqualTo(e.Channel), $"第 {i} 条轨的声道");
            Assert.That(a.Name, Is.EqualTo(e.Name), $"第 {i} 条轨的轨名");
            Assert.That(a.NoteCount, Is.EqualTo(e.Notes.Count), $"第 {i} 条轨的音符数");

            // 音高与力度两边是同一个单位（音符号、0-127），可以直接逐条比。
            // 时间不比 —— 原版那边已经是秒了，tick 由下面那条测试单独盯着。
            for (int k = 0; k < e.Notes.Count; k++)
            {
                Assert.That(a.Notes[k].Pitch, Is.EqualTo(e.Notes[k].Pitch), $"第 {i} 条轨第 {k} 个音的音高");
                Assert.That(a.Notes[k].Velocity, Is.EqualTo(e.Notes[k].Velocity), $"第 {i} 条轨第 {k} 个音的力度");
            }
        }
    }

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 真实MIDI的tick与文件里的整数一致(string path)
    {
        // 期望值直接来自文件的整数 tick，中间不过秒。
        // 音符的比对不看文本，所以这里用默认 ReadingSettings 读一遍就够了。
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
    /// 音色取自**整份文件**里该声道的第一次切换，不是「有音符的那个轨块里」的。
    ///
    /// 期望值这里刻意换一种写法算：语料 724 条轨里有 101 条的音色事件不在音符所在的轨块里，
    /// 要是期望值也跟着「只看同一个轨块」，两边会一起错、一起对，这条测试就废了。
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
        // 格式 1 的常见写法：音色、速度都集中在轨块 0，音符在轨块 1。
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

    /// <summary>
    /// 语料整体的「有料」程度。
    ///
    /// 上面逐条比对的测试对空文件是**空转**的（两边都是零条轨，比了个寂寞），
    /// 而 Valid 语料里确实混着几个空壳（14 字节只有文件头的、一个 NoteOn 都没有的）。
    /// 所以「语料不是空的」这件事得单独有一条来盯，否则哪天语料整体退化了也没人知道。
    /// </summary>
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
        // 分辨率 480、120BPM：第 481 tick 只能是 481，不能是「0.501 秒换回来」的 480 或 482。
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
        // .rmi = RIFF 壳子套 MIDI。老曲子在网上常是这种，直接当 .mid 喂进来也得能读。
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
    /// 文件头只读了一半就断了的文件。
    ///
    /// 这种文件走的是**另一条**路：<c>NotEnoughBytesPolicy.Ignore</c> 会让
    /// <c>MidiFile.Read</c> 正常返回，只是 <c>TimeDivision</c> 是 null。
    /// 不自己查这一下的话，错误会变成 DryWetMidi 的
    /// <c>ArgumentNullException("timeDivision")</c> —— 一句英文，不是给用户看的。
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

    /// <summary>分辨率是 0 的文件 DryWetMidi 照收不误，但除以零没有意义，要在门口拦成中文错误。</summary>
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
        // SMPTE 的分辨率字：高字节是负的格式号（0xE8 = −24，即 24 帧/秒），低字节是每帧 tick 数
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

        // 砍掉最后一个轨块的后半截 —— 「网站试听给的残缺文件」就长这样
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
        // 「tick 是唯一的时值表示」这条约束，机器也来盯一眼：
        // 只要有人往 Note 上加一个 double/float/decimal 成员，就是往模型里塞了第二个真相源。
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
        // spec：DryWetMidi 由 MidiReader / MidiWriter 独占，那两个文件之外不许出现它的类型。
        // 它俩本身当然有（它们就是干这个的），所以这里盯的是**模型**。
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

    private static bool IsIntegral(Type t) =>
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
