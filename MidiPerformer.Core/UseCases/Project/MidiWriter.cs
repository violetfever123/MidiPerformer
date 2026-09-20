using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiPerformer.Core.Model;
// DryWetMidi 也有同名的 Note / TempoMap / TimeDivision，用别名区分内外两个世界。
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// <see cref="Song"/> → 字节：标准 MIDI 文件的写出那一半。读半边在 <see cref="MidiReader"/>，
/// 工程文件那两条（存 / 读）在 <see cref="SongProjectFile"/>。
///
/// DryWetMidi 只许出现在这个文件和 <see cref="MidiReader"/> 里，别处出现它的类型会被反射测试逮到
/// （<c>MidiPerformer.Tests</c>）。
/// </summary>
public static class MidiWriter
{
    /// <summary>把一份 <see cref="Song"/> 写回标准 MIDI 文件。写不出来时抛 <see cref="InvalidDataException"/>，消息是给人看的中文。</summary>
    public static void Write(Song song, string path)
    {
        // 先整体序列化进内存再落盘：中途出错不会在磁盘上留下半截文件。
        File.WriteAllBytes(path, WriteBytes(song));
    }

    /// <summary>把一份 <see cref="Song"/> 写成内存里的 MIDI 数据。测试与「导出成文件」都走这里。</summary>
    public static byte[] WriteBytes(Song song)
    {
        MidiFile file = ToMidiFile(song);

        // 一个轨块 → 格式 0（最通用的形态）；多个 → 格式 1（各轨块共用一个时间轴）。
        // 格式 2 表达不出来：Song 已经把每轨的 tick 摊平到同一根轴上了，每个音照样写进去，
        // 只是「各段各自计时」变成了「同时开始」。
        MidiFileFormat format = file.Chunks.Count > 1 ? MidiFileFormat.MultiTrack : MidiFileFormat.SingleTrack;

        using var stream = new MemoryStream();
        file.Write(stream, format, WritingSettings);
        return stream.ToArray();
    }

    /// <summary>
    /// 轨名一律按 UTF-8 写出：DryWetMidi 的默认值是 ASCII，中文轨名会被问号吃掉，
    /// 而读取端本来就先试严格 UTF-8。
    /// </summary>
    private static readonly WritingSettings WritingSettings = new()
    {
        TextEncoding = System.Text.Encoding.UTF8
    };

    // 同一 tick 上的先后：元事件 → 抬键 → 按键 → 零时长音的抬键。
    //
    // 「抬键排在按键之前」是 DryWetMidi 配对音符的前提（LastNoteOn 按「同音高同声道里最后那次
    // 还没配对的按下」来配）：写成按键在前，「前一个音在这一 tick 结束、后一个音在同一 tick 开始」
    // 会把新按下配走 —— 前一个音等不到抬键、后一个音变成零时长。
    // 零时长音（LengthTicks == 0）自己那一对单开一档、排在按键之后，否则它的抬键会跨过别的音。
    private const int PhaseMeta = 0;
    private const int PhaseNoteOff = 1;
    private const int PhaseNoteOn = 2;
    private const int PhaseZeroLengthNoteOff = 3;

    /// <summary>抬键的力度。模型里没有这个字段（怎么抬的不影响谱子），填 MIDI 的惯例值。</summary>
    private const byte NoteOffVelocity = 64;

    private static MidiFile ToMidiFile(Song song)
    {
        Validate(song);

        var file = new MidiFile { TimeDivision = ToTimeDivision(song.TempoMap.Division) };

        // 按 TrackIndex 分组：TrackIndex 是「文件里第几个轨块」，导出时必须原样占住那个位置；
        // 同一个轨块下的多个声道共用一个轨块、也共用轨名。
        var byIndex = song.Tracks
            .GroupBy(t => t.TrackIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToArray());

        // 中间空掉的序号要补一个空轨块：导入端是「没有音符的轨块不产生 Track，但序号照样往前走」，
        // 不补的话再导入时序号会整体前移（格式 1 的第 0 个轨块常常就是纯速度/版权信息轨）。
        int chunkCount = song.Tracks.Count == 0 ? 1 : song.Tracks.Max(t => t.TrackIndex) + 1;

        for (int index = 0; index < chunkCount; index++)
        {
            byIndex.TryGetValue(index, out var group);
            // 空曲（0 轨）也要写一个轨块：速度表和分辨率得有地方待
            file.Chunks.Add(BuildChunk(group, song.TempoMap, withTempoMap: index == 0));
        }

        return file;
    }

    /// <summary>
    /// 拼一个轨块。<paramref name="group"/> 为 null 表示「补序号用的空轨块」，里面连轨名都不写 ——
    /// 写了就凭空多出一个名字，而导入端读轨名是「有就用、没有才补」。
    /// </summary>
    /// <remarks>
    /// 一处修不掉的不对称：没有音符的轨在文件里只是一条空轨块，而导入端的规矩是「没有音符的轨块不成轨」，
    /// 所以「轨还在、音被删光了」的 Song 导出再导入会少一条轨（轨名与音色还留在文件里）。
    /// </remarks>
    private static TrackChunk BuildChunk(IReadOnlyList<Track>? group, ModelTempoMap tempoMap, bool withTempoMap)
    {
        var events = new List<(long Tick, int Phase, int Seq, MidiEvent Event)>();
        int seq = 0;
        void Add(long tick, int phase, MidiEvent e) => events.Add((tick, phase, seq++, e));

        if (withTempoMap)
        {
            // 速度与变拍统一放在第 0 个轨块里（格式 1 的惯例：第一个轨块留给速度/拍号/版权）。
            // 放在哪个轨块对导入端没差别（它整份文件一起扫速度表），但别的软件按惯例去第 0 个找。
            foreach (var c in tempoMap.TempoChanges)
                Add(c.Tick, PhaseMeta, new SetTempoEvent(c.MicrosecondsPerQuarterNote));

            foreach (var s in tempoMap.TimeSignatureChanges)
                Add(s.Tick, PhaseMeta, new TimeSignatureEvent((byte)s.Numerator, (byte)s.Denominator));
        }

        if (group != null)
        {
            // 一个轨块一个轨名，但写不写得看名字从哪来，这是导入规则的反函数：
            // 文件里有轨名 → 该轨块各声道名字相同 → 写一条就还原得了；文件里没有轨名 → 各声道挂的是
            // 按声道算的兜底名（「声道 3」「打击乐」），各不相同，写出去再导入时整块都会用第一个声道的名字。
            // 所以「每条轨的名字都等于它自己的兜底名」时一个轨名都不写，让导入端再算一遍。
            if (!group.All(t => t.Name == MidiReader.DefaultTrackName(t.Channel)))
                Add(0, PhaseMeta, new SequenceTrackNameEvent(group[0].Name));

            foreach (var track in group)
            {
                var channel = (FourBitNumber)track.Channel;

                // 音色（ProgramChange 是声道事件）必须写出去，否则别的软件打开这条轨只会是 0 号大钢琴。
                // 同一个声道跨轨块出现时两处各写各的；再导入时两条轨会得同一个值（导入端按声道取
                // 全文件第一次切换），导出端如实写、不迁就它。
                Add(0, PhaseMeta, new ProgramChangeEvent((SevenBitNumber)track.Program) { Channel = channel });

                foreach (var note in track.Notes)
                {
                    int pitch = ClampPitch(note.Pitch + track.Transpose);

                    Add(note.StartTick, PhaseNoteOn,
                        new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)ClampVelocity(note.Velocity))
                        {
                            Channel = channel
                        });

                    Add(note.EndTick, note.LengthTicks == 0 ? PhaseZeroLengthNoteOff : PhaseNoteOff,
                        new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)NoteOffVelocity)
                        {
                            Channel = channel
                        });
                }
            }
        }

        // 排序定死后按顺序补增量时间。EndOfTrack 不用自己加：DryWetMidi 写轨块时自动补。
        var chunk = new TrackChunk();
        long lastTick = 0;
        foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Phase).ThenBy(e => e.Seq))
        {
            e.Event.DeltaTime = e.Tick - lastTick;
            lastTick = e.Tick;
            chunk.Events.Add(e.Event);
        }

        return chunk;
    }

    /// <summary>
    /// 移调只在这里叠加到音高上：写出去的音高 = <c>Note.Pitch + Track.Transpose</c>，
    /// <see cref="Note"/> 本身不动，所以同一份 Song 想导多少次都无损。
    /// 越界的音高夹到边界而不是跳过：导出物还要给人编辑、给别的软件读，丢音不可逆（演奏那条路相反）。
    /// </summary>
    private static int ClampPitch(int pitch) => Math.Clamp(pitch, 0, 127);

    /// <summary>
    /// 力度夹到 1..127：MIDI 里力度为 0 的按下就是抬键（DryWetMidi 读到会转成 NoteOff），
    /// 写出去等于把这个音删了。拦的是手工拼出来的 Song，导入来的音符本来不会带 0。
    /// </summary>
    private static int ClampVelocity(int velocity) => Math.Clamp(velocity, 1, 127);

    // 返回类型写全名：DryWetMidi 也有个 TimeDivision，这里只有一处要指名道姓，不值得再添一个别名。
    private static Melanchall.DryWetMidi.Core.TimeDivision ToTimeDivision(ModelTimeDivision division)
    {
        // SMPTE 的格式号就是这个帧率本身（24 / 25 / 29 = 29.97 drop-frame / 30），直接转回去。
        if (division.IsSmpte)
            return new SmpteTimeDivision((SmpteFormat)division.SmpteFramesPerSecond, (byte)division.SmpteTicksPerFrame);

        return new TicksPerQuarterNoteTimeDivision((short)division.TicksPerQuarterNote);
    }

    /// <summary>
    /// 「模型里装得下、文件里装不下」的值，在门口拦成中文错误（读那边拦的是反过来的情形）。
    /// 不拦的话冒出去的是悄悄截断的垃圾值，或者一句英文的 <c>ArgumentOutOfRangeException</c>。
    /// </summary>
    private static void Validate(Song song)
    {
        // 轨块序号是「文件里第几个轨块」，负数是没意义的：不拦的话这些轨会被悄悄丢掉。
        if (song.Tracks.Any(t => t.TrackIndex < 0))
            throw new InvalidDataException("有轨的轨块序号是负数，这份工程写不成 MIDI 文件。");

        var division = song.TempoMap.Division;

        if (division.IsSmpte)
        {
            // 帧率只能是 MIDI 规定的那四种（枚举值就是帧率本身）；每帧 tick 数占一个字节、上限 255，
            // 而模型那个工厂只保证 ≥ 1，所以这一头得自己拦。
            if (!Enum.IsDefined(typeof(SmpteFormat), (byte)division.SmpteFramesPerSecond) ||
                division.SmpteTicksPerFrame > byte.MaxValue)
            {
                throw new InvalidDataException(
                    $"SMPTE 分辨率 {division.SmpteFramesPerSecond} 帧/秒 × {division.SmpteTicksPerFrame} tick/帧 " +
                    $"不是合法的 MIDI 分辨率（帧率只能是 24 / 25 / 29 / 30，每帧 1..{byte.MaxValue} tick）。");
            }
            return;
        }

        // PPQ 占两个字节且最高位用来区分 SMPTE，所以上限是 short 的正半区。
        if (division.TicksPerQuarterNote is < 1 or > short.MaxValue)
        {
            throw new InvalidDataException(
                $"每四分音符 {division.TicksPerQuarterNote} tick 不是合法的 MIDI 分辨率（只能是 1..{short.MaxValue}）。");
        }
    }
}
