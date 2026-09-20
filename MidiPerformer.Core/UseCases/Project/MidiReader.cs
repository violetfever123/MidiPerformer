using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiPerformer.Core.Model;
// DryWetMidi 也有同名的 Note / TempoMap / TimeDivision，用别名区分内外两个世界。
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Core.UseCases.Project;

/// <summary>
/// 标准 MIDI 文件 → <see cref="Song"/>。写半边在 <see cref="MidiWriter"/>，工程文件那两条（存 / 读）在 <see cref="SongProjectFile"/>。
///
/// DryWetMidi 只许出现在这个文件和 <see cref="MidiWriter"/> 里，别处出现它的类型会被反射测试逮到
/// （<c>MidiPerformer.Tests</c>）。解析不做成网关：最容易错的 PPQ 换算和变速处理正该被测到。
/// </summary>
/// <remarks>
/// 读取容错：用户从各种网站下载来的 MIDI 常见脏数据（0 字节占位文件、RIFF 包装的 .rmi、GBK 轨名、
/// 参数值越界的事件、被截断的下载）一律就近纠正而不中断。
/// </remarks>
public static class MidiReader
{
    static MidiReader()
    {
        // 支持 GBK/GB2312 等旧编码（国内老 MIDI 的轨名常用）；写出那边一律 UTF-8，用不着这个 provider。
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

        // 两种信号都表示「这份文件没有完整可用的文件头」：上面抛了异常，或者文件头只读了一半就断了
        //（NotEnoughBytesPolicy.Ignore 会让 Read 正常返回、TimeDivision 为 null，必须自己查，否则后面抛英文异常）。
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
    /// 网上 MIDI 常带脏数据（调号、通道事件的参数值越界等），一律就近纠正而不中断：这些元事件对演奏没有影响，但会让整个文件读不进来。
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

            // 一个轨块可能装着多个声道（格式 0 的 MIDI 整首曲子就是一个轨块），按声道拆开 ——
            // 每个声道才是一条声部、才有一个明确的音色。分组用该声道第一次出音的先后，不按声道号排序。
            foreach (var group in chunk.GetNotes().GroupBy(n => (int)n.Channel))
            {
                // 稳定排序：同一 tick 上的音保持 DryWetMidi 给出的相对顺序。
                // 排好之后就地在数组顺序上发身份（见 NoteIdentity）—— 不掺随机数，保证同一次导入可重现。
                var notes = NoteIdentity.AssignInOrder(group
                    .OrderBy(n => n.Time)
                    .Select(n => new ModelNote(
                        (int)n.NoteNumber,
                        n.Time,
                        n.Length,
                        (int)n.Velocity))
                    .ToArray());

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
    /// 「文件里根本没写轨名」时那条轨该叫什么。单独抽出来（<c>internal</c>，导出端在另一个文件里）
    /// 是为了导出端能反推：名字等于本位兜底名的轨，说明当初文件里就没有轨名，不该把它当轨名写出去。
    /// </summary>
    internal static string DefaultTrackName(int channel) => channel == 9 ? "打击乐" : $"声道 {channel + 1}";

    /// <summary>
    /// 每个声道在整份文件里第一次切换到的音色；没有出现过的声道不在表里（调用方按 0 号大钢琴处理）。
    /// 全文件扫而不是只看有音符的轨块：音色切换是声道事件，格式 1 常见把它集中放在第 0 个轨块里。
    /// 一条轨只有一个 <see cref="Track.Program"/>，中途换音色的曲子只能记第一个。
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

        // 分辨率是 0 的文件 DryWetMidi 照收不误，但模型的构造器会拒绝（除以零没有意义），
        // 在这里拦下来换成中文错误，而不是让英文的 ArgumentOutOfRangeException 冒到界面上。
        ModelTimeDivision division;
        switch (file.TimeDivision)
        {
            case TicksPerQuarterNoteTimeDivision ppq when ppq.TicksPerQuarterNote >= 1:
                division = ModelTimeDivision.PulsesPerQuarter(ppq.TicksPerQuarterNote);
                break;

            case SmpteTimeDivision smpte when smpte.Resolution >= 1:
                // SMPTE 的格式号 29 按标准是 29.97（drop-frame），模型存整数帧率，这里当 29 用（时间约偏长 3.3%）。
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
}
