using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiPerformer.Core.Model;
// DryWetMidi 自己也有 Note / TempoMap / TimeDivision。这几个别名是**故意**留着刺眼的：
// 它们每出现一次，都在提醒读代码的人「这里正是内外两个世界握手的地方」。
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// 工程进出：标准 MIDI 文件 ⇄ <see cref="Song"/>，以及 .mproj 工程文件 ⇄ <see cref="Song"/>。
/// **两条都是 S1 缝**（见 spec「Testing Decisions」）—— 缝的两半在同一个文件里。
///
/// **全程序唯一允许出现 DryWetMidi 的文件。** 这个文件之外，任何地方都不许出现它的类型 ——
/// <c>MidiPerformer.Tests</c> 里有一条反射测试盯着这件事。
///
/// 为什么 MIDI 解析不做成网关（即：为什么 DryWetMidi 在 Core 里而不是 Adapters 里）：
/// 解析最容易错的地方正是 PPQ 换算和变速处理，把它放进网关等于把最该测的东西放进「明确不测」的筐里。
/// DryWetMidi 是纯托管库，不碰 Win32、不碰 Avalonia，住进内层不破任何约束。
/// </summary>
/// <remarks>
/// 读取的容错策略是照 <c>harmonica-auto-player@a14335c</c> 的 <c>Midi/MidiLoader.cs</c> 来的 ——
/// 它面对的是「用户从各种网站下载来的 MIDI」，那些文件的脏法是实测出来的：
/// 0 字节的网盘占位文件、RIFF 包装的 .rmi、GBK 轨名、参数值越界的元事件、被截断的下载。
/// 这些都不是假想，照搬比重新踩一遍便宜。
/// </remarks>
public static class SongProject
{
    static SongProject()
    {
        // 支持 GBK/GB2312 等旧编码（国内老 MIDI 的轨名常用）
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* 注册失败不影响读取，轨名会退化成 Latin1 */ }
    }

    /// <summary>读一个标准 MIDI 文件（格式 0 / 1 / 2）。文件不可用时抛 <see cref="InvalidDataException"/>，消息是给人看的中文。</summary>
    public static Song Read(string path)
    {
        // 先整体读进内存再解析：网盘「占位文件」、文件被占用时，边读边解析容易互相干扰
        byte[] data = File.ReadAllBytes(path);
        return ReadBytes(data);
    }

    /// <summary>读一份已经在内存里的 MIDI 数据。测试与「拖进来一个文件」都走这里。</summary>
    public static Song ReadBytes(byte[] data)
    {
        MidiFile file = ParseFile(data);
        return ToSong(file);
    }

    // ==================== 字节 → MidiFile ====================

    private static MidiFile ParseFile(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("文件是 0 字节：多半是下载失败或网盘“占位文件”尚未同步完成。");

        string sig = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length));
        if (sig == "RIFF")
        {
            // .rmi：RIFF 包装的 MIDI，取出内部 MThd 再解析
            int p = IndexOf(data, new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' }, 12);
            if (p < 0)
                throw new InvalidDataException("是 RIFF(.rmi) 格式但内部找不到 MIDI 数据，文件可能损坏。");
            data = data[p..];
            sig = "MThd";
        }
        if (sig != "MThd")
            throw new InvalidDataException($"不是标准 MIDI 文件：开头是“{sig}”。可能文件已损坏、被改名，或根本不是 MIDI（如其实是其它格式）。");

        using var stream = new MemoryStream(data);
        MidiFile? file = null;
        try
        {
            file = MidiFile.Read(stream, ReadingSettings);
        }
        catch (Exception ex) when (ex is NotEnoughBytesException || ex is InvalidChunkSizeException)
        {
            // 落到下面的兜底
        }

        // 两个信号都表示「这份文件没有完整可用的文件头」：
        //   · 上面抛了异常
        //   · 文件头只读了一半就断了 —— NotEnoughBytesPolicy.Ignore 会让 Read 正常返回，
        //     只是 TimeDivision 是 null。这个必须自己查：放过去的话，后面 GetTempoMap()
        //     会抛一句英文的 ArgumentNullException('timeDivision')，那不是给用户看的话。
        if (file?.TimeDivision is null)
        {
            // 兜底：裁掉「最后一个完整轨道之后」的残缺字节，再重新解析
            byte[]? trimmed = TryTrimToCompleteChunks(data);
            if (trimmed == null) throw Truncated();
            try
            {
                using var second = new MemoryStream(trimmed);
                file = MidiFile.Read(second, ReadingSettings);
            }
            catch
            {
                throw Truncated();
            }
            if (file.TimeDivision is null) throw Truncated();
        }

        return file;
    }

    private static InvalidDataException Truncated() => new(
        "文件不是完整可用的 MIDI（数据损坏或被截断）。常见原因：" +
        "网站“免积分/试听”给的是残缺或非 MIDI 内容，请重新完整下载。");

    /// <summary>
    /// 网上 MIDI 常带脏数据（调号、通道事件的参数值越界等），一律就近纠正而不中断。
    /// 这些元事件对演奏没有任何影响，但会让整个文件读不进来。
    /// </summary>
    private static readonly ReadingSettings ReadingSettings = new()
    {
        TextEncoding = System.Text.Encoding.UTF8,
        DecodeTextCallback = DecodeTextSmart,
        NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
        InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
        InvalidSystemCommonEventParameterValuePolicy = InvalidSystemCommonEventParameterValuePolicy.SnapToLimits
    };

    /// <summary>轨名解码：优先严格 UTF-8，解不出再退 GBK，最后拉丁一。</summary>
    private static string DecodeTextSmart(byte[] raw, ReadingSettings settings)
    {
        if (raw == null || raw.Length == 0) return "";
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            string s = utf8.GetString(raw);
            if (!s.Contains('�')) return s;
        }
        catch { /* 不是合法 UTF-8，往下退 */ }
        try
        {
            return System.Text.Encoding.GetEncoding(936).GetString(raw);
        }
        catch { /* 拿不到 GBK 代码页，继续退 */ }
        return System.Text.Encoding.Latin1.GetString(raw);
    }

    // ==================== MidiFile → Song ====================

    private static Song ToSong(MidiFile file)
    {
        ModelTempoMap tempoMap = ReadTempoMap(file);
        var firstProgram = FirstProgramPerChannel(file);

        var tracks = new List<Track>();
        int trackIndex = 0;
        foreach (var chunk in file.GetTrackChunks())
        {
            string trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text ?? "";

            // 一个轨块可能装着多个声道（格式 0 的 MIDI 就是整首曲子一个轨块）。
            // 按声道拆开 —— 每个声道才是一条声部、才有一个明确的音色。
            // 分组用 GroupBy 的天然顺序（该声道第一次出音的先后），不按声道号排序：
            // 原版就是这么排的，顺序一致，两边的轨列表可以直接逐条对着看。
            foreach (var group in chunk.GetNotes().GroupBy(n => (int)n.Channel))
            {
                // 稳定排序：同一 tick 上的音保持 DryWetMidi 给出的相对顺序，
                // 原版的合并逻辑依赖这个顺序，换了顺序对拍就红。
                var notes = group
                    .OrderBy(n => n.Time)
                    .Select(n => new ModelNote(
                        (int)n.NoteNumber,
                        n.Time,
                        n.Length,
                        (int)n.Velocity))
                    .ToArray();

                if (notes.Length == 0) continue;

                int channel = group.Key;
                tracks.Add(new Track(
                    trackIndex,
                    channel,
                    BuildTrackName(trackName, channel),
                    firstProgram.TryGetValue(channel, out int program) ? program : 0,
                    notes));
            }

            trackIndex++;
        }

        return new Song(tracks, tempoMap);
    }

    /// <summary>没有轨名时给个能认的名字。9 号声道按 MIDI 规定是打击乐。</summary>
    private static string BuildTrackName(string trackName, int channel) =>
        string.IsNullOrWhiteSpace(trackName) ? DefaultTrackName(channel) : trackName.Trim();

    /// <summary>
    /// 「文件里根本没写轨名」时那条轨该叫什么。
    ///
    /// 单独抽出来是为了导出端能用它**反推**：名字等于本位兜底名的轨，说明当初文件里就没有轨名，
    /// 导出时就不该把兜底名当轨名写出去（见 <c>BuildChunk</c>）。
    /// </summary>
    private static string DefaultTrackName(int channel) => channel == 9 ? "打击乐" : $"声道 {channel + 1}";

    /// <summary>
    /// 每个声道在**整份文件**里第一次切换到的音色；没有出现过的声道不在表里（调用方按 0 号大钢琴处理）。
    ///
    /// 为什么是全文件扫、不是只看有音符的那个轨块：音色切换是**声道事件**，它和音符不必住在同一个轨块。
    /// 实测语料 724 条轨里有 **101 条（14%）** 就是这个样子 —— 格式 1 常见把音色、速度这些
    /// 集中放在第 0 个「指挥轨」里，音符在后面的轨块里。只看同一个轨块的话，
    /// 这 14% 的音色全都会退化成 0 号大钢琴。
    ///
    /// 一条轨只有一个 <see cref="Track.Program"/>，所以中途换音色的曲子只能记第一个 ——
    /// 这个字段只用来给编辑器试听定音色，不值得为它引入「音色随 tick 变化」的模型。
    /// </summary>
    private static Dictionary<int, int> FirstProgramPerChannel(MidiFile file)
    {
        var result = new Dictionary<int, int>();
        foreach (var chunk in file.GetTrackChunks())
        {
            foreach (var e in chunk.Events)
            {
                if (e is ProgramChangeEvent program && !result.ContainsKey((int)program.Channel))
                    result[(int)program.Channel] = (int)program.ProgramNumber;
            }
        }
        return result;
    }

    private static ModelTempoMap ReadTempoMap(MidiFile file)
    {
        var tempoMap = file.GetTempoMap();

        // 分辨率是 0 的文件 DryWetMidi 照收不误，但我们模型的构造器会拒绝（除以零没有意义）。
        // 在这里拦下来换成中文错误，而不是让一个英文的 ArgumentOutOfRangeException 冒到界面上。
        ModelTimeDivision division;
        switch (file.TimeDivision)
        {
            case TicksPerQuarterNoteTimeDivision ppq when ppq.TicksPerQuarterNote >= 1:
                division = ModelTimeDivision.PulsesPerQuarter(ppq.TicksPerQuarterNote);
                break;

            case SmpteTimeDivision smpte when smpte.Resolution >= 1:
                // SMPTE 的格式号 29 按标准是 29.97（drop-frame）。模型存的是整数帧率，表达不了 29.97，
                // 这里就当 29 用，drop-frame 文件算出来的时间会偏长约 3.3%。
                // 不去修的原因：SMPTE 的 MIDI 实际已经绝迹，而且 DryWetMidi 的换算器对 SMPTE 直接抛异常
                // （原版根本打不开这种文件），所以既没有对拍参照、也没有语料，
                // 能保证的只是「读到不崩、tick ⇄ 秒自洽」。
                division = ModelTimeDivision.Smpte((int)smpte.Format, smpte.Resolution);
                break;

            default:
                throw new InvalidDataException(
                    "这份 MIDI 的时间分辨率不合法（每四分音符 tick 数是 0），文件多半损坏了。");
        }

        var tempos = tempoMap.GetTempoChanges()
            .Select(c => new TempoChange(c.Time, c.Value.MicrosecondsPerQuarterNote));

        var signatures = tempoMap.GetTimeSignatureChanges()
            .Select(c => new TimeSignatureChange(c.Time, c.Value.Numerator, c.Value.Denominator));

        return new ModelTempoMap(division, tempos, signatures);
    }

    // ==================== 截断修复 ====================

    /// <summary>
    /// 裁掉「最后一个完整 MTrk 轨道」之后的残缺字节，并把文件头的轨道数改成实际个数。
    /// 修不了返回 null。
    /// </summary>
    private static byte[]? TryTrimToCompleteChunks(byte[] data)
    {
        try
        {
            if (data.Length < 14) return null;
            int headerLen = BE32(data, 4);
            if (headerLen < 6) return null;
            int headerTotal = 8 + headerLen;
            if (headerTotal > data.Length) return null;

            var chunks = new List<byte[]>();
            int i = headerTotal;
            while (i + 8 <= data.Length)
            {
                if (data[i] != (byte)'M' || data[i + 1] != (byte)'T' ||
                    data[i + 2] != (byte)'r' || data[i + 3] != (byte)'k') break;
                long size = BE32(data, i + 4);
                long end = (long)i + 8 + size;
                if (end > data.Length) break;          // 最后一块不完整 → 丢弃它及其后
                chunks.Add(data[i..(int)end]);
                i = (int)end;
            }
            if (chunks.Count == 0) return null;

            byte[] head = data[..headerTotal];
            head[10] = (byte)(chunks.Count >> 8);      // 修正 ntrks
            head[11] = (byte)(chunks.Count & 0xFF);

            using var ms = new MemoryStream();
            ms.Write(head, 0, head.Length);
            foreach (var c in chunks) ms.Write(c, 0, c.Length);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int BE32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    // ==================== Song → 字节 ====================

    /// <summary>把一份 <see cref="Song"/> 写回标准 MIDI 文件。写不出来时抛 <see cref="InvalidDataException"/>，消息是给人看的中文。</summary>
    public static void Write(Song song, string path)
    {
        // 先整体序列化进内存再落盘，和读取端的「先整体读进内存再解析」对称：
        // 序列化中途出错不会在磁盘上留下半截文件（那半截会被当成本地文件损坏，更难查）。
        File.WriteAllBytes(path, WriteBytes(song));
    }

    /// <summary>把一份 <see cref="Song"/> 写成内存里的 MIDI 数据。测试与「导出成文件」都走这里。</summary>
    public static byte[] WriteBytes(Song song)
    {
        MidiFile file = ToMidiFile(song);

        // 一个轨块 → 格式 0（最通用的形态，什么都读得了）；多个 → 格式 1（各轨块共用一个时间轴）。
        // 格式 2 表达不出来：那三种格式的差别不在数据里，而在「各轨块的时间轴算不算同一根」，
        // 而 Song 已经把每一轨的 tick 都摊平到同一根轴上了 —— 格式没进模型，这里就补不回来。
        // 补不回来也不丢东西：格式 2 的每个音都原样写进格式 1，只是「各段各自计时」变成了「同时开始」。
        MidiFileFormat format = file.Chunks.Count > 1 ? MidiFileFormat.MultiTrack : MidiFileFormat.SingleTrack;

        using var stream = new MemoryStream();
        file.Write(stream, format, WritingSettings);
        return stream.ToArray();
    }

    /// <summary>
    /// 轨名一律按 UTF-8 写出。
    ///
    /// DryWetMidi 的默认值是 <c>SmfConstants.DefaultTextEncoding</c> = **ASCII**，中文轨名会被问号吃掉；
    /// 而读取端（<see cref="DecodeTextSmart"/>）本来就先试严格 UTF-8，两边正好对上。
    /// </summary>
    private static readonly WritingSettings WritingSettings = new()
    {
        TextEncoding = System.Text.Encoding.UTF8
    };

    // 同一 tick 上的先后：元事件 → 抬键 → 按键 → 零时长音的抬键。四档的顺序是有理由的：
    //
    // 「抬键排在按键之前」既是 MIDI 的常规写法，也是 DryWetMidi 配对音符的**前提**。
    // 它按「同一个音高同一个声道里最后那次还没配对的按下」来配抬键（NoteStartDetectionPolicy.LastNoteOn），
    // 所以「前一个音正好在这一 tick 结束、后一个音在同一 tick 开始」若写成按键在前，
    // 新按下会被前一个音的抬键配走 —— 前一个音永远等不到抬键、后一个音变成零时长，两个音一起坏。
    //
    // 零时长音（LengthTicks == 0）自己那一对按下/抬起单开一档、排在按键之后，
    // 否则它自己的抬键会跨过别的音，配对又串。
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

        // 按 TrackIndex 分组：TrackIndex 是「文件里第几个轨块」，导出时必须原样占住那个位置。
        // 同一个轨块下的多个声道共用一个轨块（格式 0 的曲子整首就在一个轨块里），也共用轨名。
        var byIndex = song.Tracks
            .GroupBy(t => t.TrackIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToArray());

        // 中间空掉的序号要补一个空轨块。**不补的话再导入时序号会整体前移**：
        // 导入端是「没有音符的轨块不产生 Track，但序号照样往前走」，格式 1 的第 0 个轨块
        // 常常就是纯速度/版权信息轨（语料里随处可见）。补上空轨块，序号才对得上。
        int chunkCount = song.Tracks.Count == 0 ? 1 : song.Tracks.Max(t => t.TrackIndex) + 1;

        for (int index = 0; index < chunkCount; index++)
        {
            byIndex.TryGetValue(index, out var group);
            // 空曲（0 轨）也要写一个轨块：速度表和分辨率得有地方待，不然空曲导出后连速度都丢了
            file.Chunks.Add(BuildChunk(group, song.TempoMap, withTempoMap: index == 0));
        }

        return file;
    }

    /// <summary>
    /// 拼一个轨块。<paramref name="group"/> 为 null 表示「补序号用的空轨块」——
    /// 里面连轨名都不写：写了就凭空多出一个名字，而导入端读轨名是「有就用、没有才补」。
    /// </summary>
    /// <remarks>
    /// 一处**修不掉**的不对称，记在这儿免得下次有人当 bug 查：没有音符的轨在文件里只是一条空轨块，
    /// 而导入端的规矩是「没有音符的轨块不成轨」，所以「轨还在、音被删光了」这种 Song
    /// 导出再导入之后就少一条轨（轨名与音色还留在文件里，别的软件看得到）。
    /// 要修得动模型里「轨块不成轨」那条规矩，那是另一个决定，不在导出这一半。
    /// </remarks>
    private static TrackChunk BuildChunk(IReadOnlyList<Track>? group, ModelTempoMap tempoMap, bool withTempoMap)
    {
        var events = new List<(long Tick, int Phase, int Seq, MidiEvent Event)>();
        int seq = 0;
        void Add(long tick, int phase, MidiEvent e) => events.Add((tick, phase, seq++, e));

        if (withTempoMap)
        {
            // 速度与变拍统一放在第 0 个轨块里 —— 格式 1 的惯例就是「第一个轨块留给速度/拍号/版权」。
            // 放在哪个轨块对导入端没差别（它是整份文件一起扫速度表的），但别的软件按惯例去第 0 个轨块找。
            // 变速曲目导出后速度要正确，靠的就是这几行。
            foreach (var c in tempoMap.TempoChanges)
                Add(c.Tick, PhaseMeta, new SetTempoEvent(c.MicrosecondsPerQuarterNote));

            foreach (var s in tempoMap.TimeSignatureChanges)
                Add(s.Tick, PhaseMeta, new TimeSignatureEvent((byte)s.Numerator, (byte)s.Denominator));
        }

        if (group != null)
        {
            // 一个轨块一个轨名。**但名字是不是写出去得看它从哪来**，这是导入规则的反函数：
            //
            // 文件里有轨名 → 导入时同一个名字发给了该轨块的每个声道 → 几条轨名字相同 → 写一条就还原得了。
            // 文件里**没有**轨名 → 每个声道各挂一个按声道算出来的兜底名（「声道 3」「打击乐」），
            // 它们**各不相同**。这时要是把兜底名当轨名写出去，再导入时整块都会挂上第一个声道的那个名字
            // —— 语料里真会这样：一条无名轨块的几个声道回来全叫「声道 3」。
            //
            // 所以「每条轨的名字都等于它自己的兜底名」时一个轨名都不写，让导入端再算一遍。
            // 反过来，真有文件把轨名起成「声道 3」时，同组的其余声道名字对不上自己的兜底名，
            // 走的是写出去那条路，也还原得回来。
            if (!group.All(t => t.Name == DefaultTrackName(t.Channel)))
                Add(0, PhaseMeta, new SequenceTrackNameEvent(group[0].Name));

            foreach (var track in group)
            {
                var channel = (FourBitNumber)track.Channel;

                // 音色（ProgramChange 是声道事件）必须和轨名一起写出去，
                // 否则别的软件打开这条轨只会是 0 号大钢琴，编辑器里选的音色等于白选。
                //
                // 同一个声道**跨轨块**出现时（格式 1 里不常见但合法）两处都写各轨自己的音色，
                // 而这个文件再导入时两条轨会得同一个值（导入端按声道取全文件第一次切换，
                // 见 FirstProgramPerChannel）—— 这是导入端的规矩，导出端如实写、不迁就它。
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

        // 排序定死后按顺序补增量时间。EndOfTrack 不用自己加：DryWetMidi 写轨块时自动补，
        // 而且它的 Events 集合根本不收这个事件。
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
    /// 移调**只在这里**叠加到音高上：写出去的音高 = <c>Note.Pitch + Track.Transpose</c>。
    /// 音符一个字节都不动（<see cref="Track.Transpose"/> 是轨的属性，永远不落进 <see cref="Note"/>），
    /// 所以同一份 Song 想导多少次、想换成别的移调再导，都是无损的。
    ///
    /// 越界的音高**夹到边界，不跳过**。取舍：导出物是给人拿去别的软件用的，
    /// 夹住至少保住音数、时值和节奏（听得出不对，也看得见）；跳过则是静默丢音，
    /// 用户对着导出结果找不到少了哪儿。演奏那条路正好相反 —— 游戏弹不出来的音是**跳过不发**
    /// （见 <c>UseCases/Perform/Repertoire</c>），因为发一个错的音比少发一个音难听得多。
    /// 两处不一样是有意的：导出的产物还要被人编辑、被别的软件读，丢音不可逆。
    /// </summary>
    private static int ClampPitch(int pitch) => Math.Clamp(pitch, 0, 127);

    /// <summary>
    /// 力度夹到 1..127。
    ///
    /// 0 不能写：MIDI 里**力度为 0 的按下就是抬键**（DryWetMidi 读到会转成 NoteOff，见 SilentNoteOnPolicy），
    /// 写出去等于把这个音删了。导入来的音符本来不会带 0（读到 0 就变成抬键了），
    /// 拦的是手工拼出来的 Song。127 是上限。
    /// </summary>
    private static int ClampVelocity(int velocity) => Math.Clamp(velocity, 1, 127);

    // 返回类型写全名：DryWetMidi 也有个 TimeDivision，而别名那一套是给**模型**那边留的刺
    // （见文件头），这里只有一个地方要指名道姓，不值得为它再添一个别名。
    private static Melanchall.DryWetMidi.Core.TimeDivision ToTimeDivision(ModelTimeDivision division)
    {
        // SMPTE 的格式号就是这个帧率本身（24 / 25 / 29 = 29.97 drop-frame / 30），
        // 模型里存的就是它，直接转回去 —— 读取端也是这么转过来的。
        if (division.IsSmpte)
            return new SmpteTimeDivision((SmpteFormat)division.SmpteFramesPerSecond, (byte)division.SmpteTicksPerFrame);

        return new TicksPerQuarterNoteTimeDivision((short)division.TicksPerQuarterNote);
    }

    /// <summary>
    /// 「模型里装得下、文件里装不下」的值，在门口拦成中文错误。
    ///
    /// 与读取端对称：读那边拦的是「文件里的值装不进模型」（分辨率 0），
    /// 这边拦的是「模型里的值装不进文件」。不拦的话冒出去的是
    /// <c>(short)</c> / <c>(byte)</c> 悄悄截断的垃圾，或者一句英文的 <c>ArgumentOutOfRangeException</c>。
    /// </summary>
    private static void Validate(Song song)
    {
        // 轨块序号是「文件里第几个轨块」，负数是没意义的。不拦的话这些轨会被悄悄丢掉
        // （序号对不上任何一个轨块），而「悄悄少一条轨」比报错难查得多。
        if (song.Tracks.Any(t => t.TrackIndex < 0))
            throw new InvalidDataException("有轨的轨块序号是负数，这份工程写不成 MIDI 文件。");

        var division = song.TempoMap.Division;

        if (division.IsSmpte)
        {
            // 帧率只能是 MIDI 规定的那四种（枚举值就是帧率本身）。每帧 tick 数占一个字节，
            // 上限 255 —— 模型那个工厂只保证 ≥ 1，上不封顶，所以这一头得自己拦。
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

    // ==================== .mproj：工程文件 ====================
    //
    // S1 缝的另一半：Song ⇄ .mproj。要的也是逐字段精确：
    //   SaveProject → LoadProject 之后，轨数、每轨的轨块序号/声道/名字/音色/移调、
    //   每个音的四个字段、速度表的分辨率与两张事件表，一个都不能变。
    //
    // **文件格式**：JSON，平铺成一个对象 —— 文件头那几个字段就是文件最上面那几行：
    //     {
    //       "Version": 1,
    //       "Name": "起风了",
    //       "Edited": true,
    //       "ImportedFrom": "C:\\下载\\起风了.mid",
    //       "Song": { "Tracks": [ … ], "TempoMap": { … } }
    //     }
    // 头和信息平铺在一层，是因为它们确实是「这份文件的头」；于是「缺 Song」也就成了
    // 读取端要单独认的一种坏文件。
    //
    // **实体直接序列化，没有 DTO 层**（spec「文件与存储」）：只有一个消费者的文件格式，
    // 多一层映射是纯仪式。代价是模型上那几个「算出来的属性」得挡住不写 —— 见 DropDerivedProperties。

    /// <summary>当前 .mproj 的版本。读到比它大的版本就报错，不猜着读 —— 猜出来的谱面比读不出来更坏。</summary>
    public const int ProjectVersion = 1;

    /// <summary>
    /// <see cref="Song"/> + 文件头 → .mproj 的 JSON 文本。
    ///
    /// 这里**不校验谱面**：JSON 里没有「装不下」的值，MIDI 导出那边的越界检查（分辨率上限之类）
    /// 在这儿一条都不适用。什么 <see cref="Song"/> 都写得出来，空曲（0 轨）也一样。
    /// </summary>
    public static string WriteProject(Song song, ProjectHeader header)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(header);

        // 先整体序列化进内存再交给调用方，和 MIDI 的 Write 是对称的：序列化中途出错
        // 不会在盘上留下半截文件（那半截会被当成本地文件损坏，更难查）。
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            // 字段名一律从 record 上取（nameof），改了属性名这里跟着改，不会两边对不上。
            // 版本号**不取 header 里的那个值**：写出去的只有当前这一种格式，
            // 照着调用方手里那个数写，等于让文件声称自己是另一种格式。
            writer.WriteNumber(nameof(ProjectHeader.Version), ProjectVersion);
            writer.WriteString(nameof(ProjectHeader.Name), header.Name);
            writer.WriteBoolean(nameof(ProjectHeader.Edited), header.Edited);
            if (header.ImportedFrom is null)
                writer.WriteNull(nameof(ProjectHeader.ImportedFrom));
            else
                writer.WriteString(nameof(ProjectHeader.ImportedFrom), header.ImportedFrom);

            writer.WritePropertyName(SongFieldName);
            JsonSerializer.Serialize(writer, song, ProjectJson);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// .mproj 的 JSON 文本 → <see cref="Song"/> + 文件头。
    ///
    /// 读不回来时抛 <see cref="InvalidDataException"/>，消息是给人看的中文 ——
    /// 和这个文件的读取端（<see cref="Read"/>）同一条规矩：宁可说清楚哪儿坏了，不给英文异常。
    /// </summary>
    public static (ProjectHeader Header, Song Song) ReadProject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("工程文件是空的：多半是保存没写完，或者复制/下载时丢了内容。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"工程文件不是合法的 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("工程文件的内容不是一个 JSON 对象，多半不是 .mproj 文件。");

            int version = ReadVersion(root);
            // 比当前新：不猜着读。将来真加了版本 2，迁移就写在下面这一行之后
            //（「版本 1 → 2 要补什么」是那个版本的事，现在没有）
            if (version > ProjectVersion)
            {
                throw new InvalidDataException(
                    $"这份工程是更新版本的 MIDI 演奏器存的（版本 {version}，本程序只认到 {ProjectVersion}）。" +
                    "请换用新版本的程序打开，或者用导出的 MIDI 文件。");
            }
            if (version < 1)
                throw new InvalidDataException($"工程文件的版本号不合法（{version}）。");

            Song song = ReadSong(root);
            var header = new ProjectHeader(version, ReadName(root), ReadEdited(root), ReadImportedFrom(root));
            return (header, song);
        }
    }

    /// <summary>
    /// <see cref="Song"/> + 文件头 → 盘上的 .mproj。
    ///
    /// 先整体序列化进内存再一次落盘，和 <see cref="Write"/> 同一个理由：不落半截文件。
    /// 文件是 **UTF-8 无 BOM**（<see cref="File.WriteAllText(string, string)"/> 的默认），
    /// 中文因此原样在里面，diff 工具和编辑器都读得懂。
    /// </summary>
    public static void SaveProject(Song song, ProjectHeader header, string path) =>
        File.WriteAllText(path, WriteProject(song, header));

    /// <summary>
    /// 盘上的 .mproj → <see cref="Song"/> + 文件头。
    ///
    /// 文件不在 / 读不动（被别的程序占着、路径不允许）也抛 <see cref="InvalidDataException"/>：
    /// 这个特性里「读不回来」只有一种异常，调用方 catch 一处就够，
    /// 提示语里带着路径和系统给的原因，照样查得出是什么事。
    /// </summary>
    public static (ProjectHeader Header, Song Song) LoadProject(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"工程文件读不出来（{path}）：{ex.Message}", ex);
        }

        return ReadProject(json);
    }

    /// <summary>
    /// 只问文件头，不碰谱面 —— 曲库列表为每一首读一次的就是它。
    ///
    /// 所以它**不抛**：坏了、不是 JSON、读不动，一律返回 null。
    /// 一首读不出来的曲子不能让整个曲库列表消失 —— 用户得有机会把那首从列表里删掉。
    /// </summary>
    public static ProjectHeader? TryReadProjectHeader(string path)
    {
        try
        {
            // 只解析、不建对象：谱面那棵树（每个音符一个对象）一个都不造。
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int version = ReadVersion(root);
            if (version < 1 || version > ProjectVersion) return null;

            return new ProjectHeader(version, ReadName(root), ReadEdited(root), ReadImportedFrom(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    // ==================== .mproj 的内部件 ====================

    /// <summary>谱面挂在 JSON 的这个字段下。缺了它就是一份坏工程。</summary>
    private const string SongFieldName = "Song";

    /// <summary>
    /// 工程文件的写法：缩进 + 中文不转义。
    ///
    /// 缩进是给 diff 工具的（工程文件会跟着 git 走，一行到底的 JSON 一比就是整文件重写）；
    /// 中文不转义是给人看的 —— 默认转义会把曲名和导入路径写成一片 <c>\u8D77\u98CE</c>。
    /// 用 <see cref="UnicodeRanges.All"/> 而不是那个名字很吓人的 Relaxed：
    /// 中文照样原样写出去，而 <c>&lt;</c>、<c>&amp;</c> 该转义还是转义。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>读写共用的序列化设置。写法见 <see cref="WriterOptions"/>，读这边只用到转换器与类型信息。</summary>
    private static readonly JsonSerializerOptions ProjectJson = CreateProjectJson();

    private static JsonSerializerOptions CreateProjectJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropDerivedProperties } },
            // 工程文件里不写 null 之外的东西 —— 默认行为就够，这里不额外开任何开关
        };
        options.Converters.Add(new TimeDivisionConverter());
        options.Converters.Add(new TempoMapConverter());
        options.Converters.Add(new NoteConverter());
        return options;
    }

    /// <summary>
    /// **只留构造器收得到的那些属性**，公开属性里其余的（= 算出来的派生视图）直接从类型信息里摘掉。
    ///
    /// 为什么要摘掉：模型上那些算出来的东西 —— <c>Song.EndTick</c> / <c>TotalSeconds</c>、
    /// <c>Track.NoteCount</c> / <c>EndTick</c> —— 写进文件就是给同一个事实开了第二个真相源：
    /// 改一个字段忘改另一个，文件里就自相矛盾；而**读**回来时它们本该由构造器重新算出来，
    /// 一旦被当成「必填」，字段缺一个就整份工程读不回来。
    ///
    /// 为什么是「摘掉」而不是把 <c>ShouldSerialize</c> 设成 false —— 那是这个坑踩出来的：
    /// STJ 写一个成员时是**先取值、再问要不要写**（<c>GetMemberAndWriteJson</c> 里
    /// <c>Get(obj)</c> 在 <c>ShouldSerialize</c> 之前），所以设 false 只挡住了写出去，
    /// 挡不住取值这个动作本身。而派生属性的 getter 是**会抛的**：<c>Song.TotalSeconds</c> 对
    /// 一个大到不现实的 tick 会抛「时间跨度太大」—— 于是「存一份 tick 很大的工程」会当场炸，
    /// 而它本该只是一个数字。摘掉之后取值这一步根本不存在。
    ///
    /// 用「构造器参数以外的一律不留」这条笼统的规矩，而不是逐个点名：
    /// 以后模型上再加派生属性（或者加一个真字段）不用回来补名单，规矩自己就成立。
    /// 这些类型上**没有加任何序列化特性** —— 模型不该知道文件格式这回事。
    /// （<c>TimeDivision</c> / <c>TempoMap</c> / <c>Note</c> 走各自的转换器，不经过这里。）
    /// </summary>
    private static void DropDerivedProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;

        var fromConstructor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constructor in info.Type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            foreach (var parameter in constructor.GetParameters())
                if (parameter.Name is { } name) fromConstructor.Add(name);

        // 先抄一份再删：不能一边遍历一边改这个集合
        foreach (var property in info.Properties.Where(p => !fromConstructor.Contains(p.Name)).ToArray())
            info.Properties.Remove(property);
    }

    /// <summary>
    /// <see cref="ModelTimeDivision"/> 的读写。**必须自己写**：它的构造器是私有的
    /// （只有 <c>PulsesPerQuarter</c> / <c>Smpte</c> 两个工厂），STJ 自己建不出来，
    /// 不管就是一句英文的 <c>NotSupportedException</c>。
    ///
    /// 写成三个整数（两种模式互斥，另一种的字段是 0），读回来按「SMPTE 帧率是不是 &gt; 0」
    /// 分流 —— 和模型的 <c>IsSmpte</c> 是同一个判据，不另立一套。
    /// </summary>
    private sealed class TimeDivisionConverter : JsonConverter<ModelTimeDivision>
    {
        public override ModelTimeDivision Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("时间分辨率应该是一个对象。");

            int ticksPerQuarter = Field(root, "TicksPerQuarterNote");
            int framesPerSecond = Field(root, "SmpteFramesPerSecond");
            int ticksPerFrame = Field(root, "SmpteTicksPerFrame");

            if (framesPerSecond > 0)
            {
                if (ticksPerFrame < 1)
                    throw new JsonException($"SMPTE 分辨率每帧至少要 1 tick（现在是 {ticksPerFrame}）。");
                if (ticksPerQuarter != 0)
                    throw new JsonException("时间分辨率同时写着 PPQ 和 SMPTE 两种模式，只能有一种。");
                return ModelTimeDivision.Smpte(framesPerSecond, ticksPerFrame);
            }

            if (ticksPerFrame != 0)
                throw new JsonException("时间分辨率里没有 SMPTE 帧率，却有「每帧 tick 数」。");
            if (ticksPerQuarter < 1)
                throw new JsonException($"时间分辨率里的每四分音符 tick 数至少要 1（现在是 {ticksPerQuarter}）。");
            return ModelTimeDivision.PulsesPerQuarter(ticksPerQuarter);
        }

        public override void Write(
            Utf8JsonWriter writer, ModelTimeDivision value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("TicksPerQuarterNote", value.TicksPerQuarterNote);
            writer.WriteNumber("SmpteFramesPerSecond", value.SmpteFramesPerSecond);
            writer.WriteNumber("SmpteTicksPerFrame", value.SmpteTicksPerFrame);
            writer.WriteEndObject();
        }

        /// <summary>
        /// 取一个整数字段。缺了、或者不是数字（写成字符串、小数、null），都当场说清楚。
        ///
        /// 为什么要自己判 <see cref="JsonValueKind.Number"/>：<c>JsonElement.TryGetInt32</c> 对一个
        /// **字符串**元素不是返回 false，是**抛** <c>InvalidOperationException</c>（实测），
        /// 而那是个英文异常；而且它会从转换器里冒出去，被 STJ 换成一句
        /// 「The JSON value could not be converted to …」，中文原因就全没了。
        /// </summary>
        private static int Field(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
                throw new JsonException($"时间分辨率里缺 {name} 字段（或者它不是个数字）。");
            if (!element.TryGetInt32(out int value))
                throw new JsonException($"时间分辨率里的 {name} 不是一个整数。");
            return value;
        }
    }

    /// <summary>
    /// <see cref="ModelNote"/> 的读写。模型的四个字段就是文件的四个字段，一个不多一个不少。
    ///
    /// 为什么要自己写，而不是靠 STJ 按构造器参数配：**STJ 对缺字段是悄悄补默认值的**
    /// （.NET 8 的默认行为：构造器参数没配到属性时，值类型就填 0，不报错）。
    /// 一份被改坏 / 手改漏了一行的工程会静默地读成一堆 velocity = 0 或者 length = 0 的音 ——
    /// 那不是「读出来了」，是「读出了一个错的谱面还告诉用户没问题」。宁可在这儿报中文错。
    ///
    /// 顺带把值的范围也拦下：音高与力度是七位整数（0..127，模型和 MIDI 都是这个约定），
    /// tick 不能是负数。这些都是「文件里写着但物理上不可能」的值。
    /// </summary>
    private sealed class NoteConverter : JsonConverter<ModelNote>
    {
        public override ModelNote Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("音符应该是一个对象。");

            int pitch = Int(root, "Pitch");
            long startTick = Long(root, "StartTick");
            long lengthTicks = Long(root, "LengthTicks");
            int velocity = Int(root, "Velocity");

            if (pitch is < 0 or > 127)
                throw new JsonException($"音符的音高是 {pitch}，不在 0..127 里。");
            if (velocity is < 0 or > 127)
                throw new JsonException($"音符的力度是 {velocity}，不在 0..127 里。");
            if (startTick < 0)
                throw new JsonException($"音符的起始 tick 是负数（{startTick}）。");
            if (lengthTicks < 0)
                throw new JsonException($"音符的时值是负数（{lengthTicks}）。");

            return new ModelNote(pitch, startTick, lengthTicks, velocity);
        }

        public override void Write(Utf8JsonWriter writer, ModelNote value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ModelNote.Pitch), value.Pitch);
            writer.WriteNumber(nameof(ModelNote.StartTick), value.StartTick);
            writer.WriteNumber(nameof(ModelNote.LengthTicks), value.LengthTicks);
            writer.WriteNumber(nameof(ModelNote.Velocity), value.Velocity);
            writer.WriteEndObject();
        }

        private static int Int(JsonElement root, string name)
        {
            long value = Long(root, name);
            if (value is < int.MinValue or > int.MaxValue)
                throw new JsonException($"音符的 {name} 是 {value}，超出了整数的范围。");
            return (int)value;
        }

        private static long Long(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
                throw new JsonException($"音符里缺 {name} 字段（或者它不是个数字）。这份工程多半被改坏了。");
            if (!element.TryGetInt64(out long value))
                throw new JsonException($"音符的 {name} 不是一个整数。");
            return value;
        }
    }

    /// <summary>
    /// <see cref="ModelTempoMap"/> 的读写。**必须自己写**，理由不是「不方便」，是 STJ **拒绝**：
    /// 它的构造器收的是 <c>IEnumerable&lt;TempoChange&gt;</c>，而属性是 <c>IReadOnlyList&lt;TempoChange&gt;</c>，
    /// STJ 要求构造器参数与属性**同名且同类型**才能配对，类型对不上就一句
    /// <c>InvalidOperationException: Each parameter ... must bind to an object property or field</c>，
    /// 而且是在**读第一份文件时**才炸（实测：写出去一切正常，读回来才报错）。
    ///
    /// 为什么不改模型的构造器签名：模型是为了「调用方能传数组、能传 LINQ」才收
    /// <c>IEnumerable</c> 的，**文件格式不该反过来规定模型的签名**。写个转换器就都保住了。
    ///
    /// 表缺了当空表（构造器本来就有默认值），但**类型不对要报错** ——
    /// 一个不是列表的东西假装成速度表，读出来的曲子会静默地变回 120 BPM。
    /// </summary>
    private sealed class TempoMapConverter : JsonConverter<ModelTempoMap>
    {
        public override ModelTempoMap Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("速度表应该是一个对象。");

            if (!root.TryGetProperty(nameof(ModelTempoMap.Division), out var divisionElement))
                throw new JsonException("速度表里没有时间分辨率（Division 字段），这份工程读不出曲子的时间轴。" +
                    "多半是保存时没写完。");

            ModelTimeDivision division = divisionElement.Deserialize<ModelTimeDivision>(options)
                ?? throw new JsonException("速度表里的时间分辨率是空的。");

            return new ModelTempoMap(
                division,
                Table<TempoChange>(root, nameof(ModelTempoMap.TempoChanges), options),
                Table<TimeSignatureChange>(root, nameof(ModelTempoMap.TimeSignatureChanges), options));
        }

        public override void Write(Utf8JsonWriter writer, ModelTempoMap value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName(nameof(ModelTempoMap.Division));
            JsonSerializer.Serialize(writer, value.Division, options);

            // 写的是构造器收下的那两张表（= TempoMap 自己归一化过的视图），
            // 不是从哪儿读来的原始事件流 —— 工程文件里只留一份算得出来的东西。
            writer.WritePropertyName(nameof(ModelTempoMap.TempoChanges));
            JsonSerializer.Serialize(writer, value.TempoChanges, options);

            writer.WritePropertyName(nameof(ModelTempoMap.TimeSignatureChanges));
            JsonSerializer.Serialize(writer, value.TimeSignatureChanges, options);

            writer.WriteEndObject();
        }

        /// <summary>读一张事件表。缺 = 空表，但不是列表就是坏文件（见类型上的注释）。</summary>
        private static IReadOnlyList<T> Table<T>(JsonElement root, string name, JsonSerializerOptions options)
        {
            if (!root.TryGetProperty(name, out var element)) return Array.Empty<T>();
            if (element.ValueKind == JsonValueKind.Null) return Array.Empty<T>();
            if (element.ValueKind != JsonValueKind.Array)
                throw new JsonException($"速度表里的 {name} 应该是一个列表。");

            return element.Deserialize<T[]>(options) ?? Array.Empty<T>();
        }
    }

    private static Song ReadSong(JsonElement root)
    {
        if (!root.TryGetProperty(SongFieldName, out var element) || element.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("工程文件里没有 Song 字段：这份文件不是 .mproj，或者保存时没写完。");

        try
        {
            return element.Deserialize<Song>(ProjectJson)
                ?? throw new InvalidDataException("工程文件里的 Song 字段是空的。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"工程文件里的谱面读不出来：{ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException($"工程文件里的谱面读不出来：{ex.Message}");
        }
    }

    /// <summary>
    /// 读版本号。**只认数字**：<c>TryGetInt32</c> 对字符串元素是抛异常而不是返回 false
    /// （见 <c>TimeDivisionConverter.Field</c> 的注释），不先判一下，
    /// 「版本号写成字符串」这种坏文件冒出去的就是一句英文的 InvalidOperationException。
    /// </summary>
    private static int ReadVersion(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(ProjectHeader.Version), out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int version))
        {
            throw new InvalidDataException("工程文件里没有版本号（Version 字段）：这份文件不是 .mproj。");
        }
        return version;
    }

    /// <summary>
    /// 曲名 / 是否改过 / 从哪导入的，缺了或类型不对**都当没有**，不算坏文件。
    ///
    /// 它们只是给人看的信息：曲名本来就从文件名来（见 <c>SongLibrary</c>），
    /// 「改过没改过」缺省就是没动过，导入来源丢了顶多看不到出处。
    /// 为这仨字段把一份读得出来的谱面拦在门外，是拿用户的时间换格式的洁癖。
    /// </summary>
    private static string ReadName(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.Name), out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static bool ReadEdited(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.Edited), out var element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        element.GetBoolean();

    private static string? ReadImportedFrom(JsonElement root) =>
        root.TryGetProperty(nameof(ProjectHeader.ImportedFrom), out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

/// <summary>
/// 工程文件的文件头 —— 一个 .mproj 的身份，也是曲库列表要显示的三个东西。
/// </summary>
/// <param name="Version">格式版本，见 <see cref="SongProject.ProjectVersion"/>。</param>
/// <param name="Name">曲名。也就是它存进曲库后的文件名（去扩展名）。</param>
/// <param name="Edited">
/// **粘性**标记：这首曲子被编辑过并且存过盘。
///
/// 它是**进度指示**（「这首我动过」），**不是**和原始导入的逐字节比较 ——
/// 撤销回初始状态也不会把它变回 <c>false</c>，再存一版照样是 <c>true</c>。
/// 这是有意的，别当 bug 查：要「和刚导入时一模一样」就得留一份原始 MIDI 逐字节比对，
/// 既费盘又答非所问 —— 用户要看的是「我记得这首还没弄完」，不是文件的哈希。
/// </param>
/// <param name="ImportedFrom">当初从哪个文件导入的（原始 MIDI 的全路径）。没导入过的工程是 null。</param>
public sealed record ProjectHeader(int Version, string Name, bool Edited, string? ImportedFrom);
