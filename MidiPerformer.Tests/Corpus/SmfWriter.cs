using System.Text;

namespace MidiPerformer.Tests.Corpus;

/// <summary>
/// 一个最小标准 MIDI 文件写手，**只为造测试语料而生**。
///
/// 刻意不用 DryWetMidi 来造：被测的是读取端，写手如果和被读的那一方同源，
/// 两边会一起错、一起对，等于没测。这里直接吐字节，读错了立刻现形。
/// </summary>
internal static class SmfWriter
{
    /// <summary>MThd + 各 MTrk。format 0 = 单轨多声道；1 = 多轨共用一个时间轴；2 = 多轨各自独立。</summary>
    public static byte[] Build(int format, int division, params SmfTrack[] tracks)
    {
        var body = new List<byte>();
        foreach (var t in tracks)
        {
            byte[] chunk = t.Build();
            body.AddRange(Ascii("MTrk"));
            body.AddRange(BE32(chunk.Length));
            body.AddRange(chunk);
        }

        var head = new List<byte>();
        head.AddRange(Ascii("MThd"));
        head.AddRange(BE32(6));
        head.AddRange(BE16(format));
        head.AddRange(BE16(tracks.Length));
        head.AddRange(BE16(division));

        return head.Concat(body).ToArray();
    }

    /// <summary>只有一个轨块、里面什么都没写（连 EndOfTrack 都没有）的空壳。</summary>
    public static byte[] HeaderOnly(int format, int division, int declaredTracks) =>
        Ascii("MThd").Concat(BE32(6)).Concat(BE16(format)).Concat(BE16(declaredTracks)).Concat(BE16(division)).ToArray();

    internal static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    internal static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };

    internal static byte[] BE32(int v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>MIDI 的可变长数值。</summary>
    internal static byte[] VarLen(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0)
        {
            bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        return bytes.ToArray();
    }

    // 同一 tick 上的先后：元事件/音色 → 抬键 → 按键。
    // 抬键排在按键之前是 MIDI 的常规写法，也保证「前一个音刚好在这一点结束、后一个音在同一点开始」时配对不串。
    private const int OrderMeta = 0;
    private const int OrderNoteOff = 1;
    private const int OrderNoteOn = 2;
}

/// <summary>一个待写入的轨块。</summary>
internal sealed class SmfTrack
{
    private readonly List<(long Tick, int Order, byte[] Bytes)> _events = new();
    private int _sequence;

    public static SmfTrack Named(string name)
    {
        var t = new SmfTrack();
        t.Meta(0, 0x03, Encoding.UTF8.GetBytes(name));
        return t;
    }

    public static SmfTrack New() => new();

    private void Add(long tick, int order, params byte[] bytes) =>
        _events.Add((tick, order * 1_000_000 + _sequence++, bytes));

    /// <summary>轨名（元事件 FF 03）。</summary>
    public SmfTrack TrackName(string name, long tick = 0)
    {
        Meta(tick, 0x03, Encoding.UTF8.GetBytes(name));
        return this;
    }

    /// <summary>变速（元事件 FF 51）。</summary>
    public SmfTrack Tempo(long tick, int microsecondsPerQuarterNote)
    {
        Meta(tick, 0x51, new[]
        {
            (byte)(microsecondsPerQuarterNote >> 16),
            (byte)(microsecondsPerQuarterNote >> 8),
            (byte)microsecondsPerQuarterNote
        });
        return this;
    }

    /// <summary>拍号（元事件 FF 58）。</summary>
    public SmfTrack TimeSignature(long tick, int numerator, int denominatorPowerOfTwo)
    {
        Meta(tick, 0x58, new byte[] { (byte)numerator, (byte)denominatorPowerOfTwo, 24, 8 });
        return this;
    }

    /// <summary>音色切换。</summary>
    public SmfTrack Program(long tick, int channel, int program)
    {
        Add(tick, 0, (byte)(0xC0 | channel), (byte)program);
        return this;
    }

    public SmfTrack NoteOn(long tick, int channel, int pitch, int velocity = 100)
    {
        Add(tick, 2, (byte)(0x90 | channel), (byte)pitch, (byte)velocity);
        return this;
    }

    public SmfTrack NoteOff(long tick, int channel, int pitch)
    {
        Add(tick, 1, (byte)(0x80 | channel), (byte)pitch, 64);
        return this;
    }

    /// <summary>一个完整的音：在 <paramref name="tick"/> 按下、<paramref name="lengthTicks"/> 之后抬起。</summary>
    public SmfTrack Note(long tick, long lengthTicks, int channel, int pitch, int velocity = 100)
    {
        NoteOn(tick, channel, pitch, velocity);
        NoteOff(tick + lengthTicks, channel, pitch);
        return this;
    }

    /// <summary>只有按下没有抬起 —— 坏文件语料。</summary>
    public SmfTrack DanglingNoteOn(long tick, int channel, int pitch) => NoteOn(tick, channel, pitch);

    private void Meta(long tick, byte type, byte[] payload)
    {
        var bytes = new List<byte> { 0xFF, type };
        bytes.AddRange(SmfWriter.VarLen(payload.Length));
        bytes.AddRange(payload);
        Add(tick, 0, bytes.ToArray());
    }

    public byte[] Build()
    {
        var body = new List<byte>();
        long last = 0;
        foreach (var e in _events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            body.AddRange(SmfWriter.VarLen(e.Tick - last));
            body.AddRange(e.Bytes);
            last = e.Tick;
        }
        body.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });   // EndOfTrack
        return body.ToArray();
    }
}
