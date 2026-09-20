using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Core.UseCases.Editing;

/// <summary>
/// 编辑命令的实现：纯函数、无状态，每条命令收散参、返回一个新的 <see cref="Song"/>。
/// 「改没改」看返回的引用是不是同一个 —— 改完与改前等价时原样返回入参。
/// 撤销由外层的 <c>UndoableSongEditor</c> 装饰器统一负责，这里不碰。
/// </summary>
public sealed class SongEditor : ISongEditor
{
    /// <summary>速度的合法下限（拍/分），与界面共用。</summary>
    public const double MinBpm = 1;

    /// <summary>速度的合法上限（拍/分）。</summary>
    public const double MaxBpm = 1000;

    /// <summary>微秒/四分音符 的上限：缩放结果可能超出 <see cref="long"/>，而 <c>(long)</c> 转换在那种情况下是未定义值，所以夹到边界。</summary>
    private const double MaxMicrosPerQuarter = long.MaxValue / 2.0;

    /// <summary>
    /// 改整曲速度：定住 tick 0 的基准速度、其余变速点整体等比缩放，以保住变速曲子的快慢关系。
    /// 音符与轨原样带过去。
    /// </summary>
    public Song SetBpm(Song song, double beatsPerMinute)
    {
        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute < MinBpm || beatsPerMinute > MaxBpm)
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute), beatsPerMinute,
                $"速度要在 {MinBpm:0} 到 {MaxBpm:0} 拍/分之间（收到 {beatsPerMinute:R}）。");

        long baseMicros = MicrosecondsAtZero(song.TempoMap);
        double factor = 60_000_000.0 / beatsPerMinute / baseMicros;

        var scaled = new List<TempoChange>(song.TempoMap.TempoChanges.Count + 1);
        bool hasZero = false;
        foreach (var change in song.TempoMap.TempoChanges)
        {
            if (change.Tick == 0) hasZero = true;
            scaled.Add(change with { MicrosecondsPerQuarterNote = Scale(change.MicrosecondsPerQuarterNote, factor) });
        }

        // tick 0 上没有速度事件时，缩放动不了开头那一段，补一条事件把它钉下来。
        if (!hasZero) scaled.Add(new TempoChange(0, RoundMicros(60_000_000.0 / beatsPerMinute)));

        var map = new TempoMap(song.TempoMap.Division, scaled, song.TempoMap.TimeSignatureChanges);

        // 改完跟改前一模一样就原样还回去：装饰器拿引用相等当「改没改」的判据。
        if (map.TempoChanges.SequenceEqual(song.TempoMap.TempoChanges)) return song;

        return new Song(song.Tracks, map);
    }

    /// <summary>
    /// 改某条轨的移调，绝对赋值（界面自己算步进后的值）。
    /// 只换目标轨那一个 <see cref="Track"/> 值，音符不动 —— 移调是轨的属性，播放和导出时才叠加到音高上。
    /// </summary>
    public Song SetTranspose(Song song, int trackIndex, int semitones)
    {
        var track = TrackAt(song, trackIndex);
        if (track.Transpose == semitones) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track with { Transpose = semitones };
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 改某条轨的音色（GM 编号），只影响试听，音符不动。
    /// 越界抛而不是夹：音色是从固定的 128 项里挑一个，传入越界是调用方写错了。
    /// </summary>
    public Song SetProgram(Song song, int trackIndex, int program)
    {
        if (program < 0 || program > 127)
            throw new ArgumentOutOfRangeException(
                nameof(program), program, $"音色号要在 0 到 127 之间（收到 {program}）。");

        var track = TrackAt(song, trackIndex);
        if (track.Program == program) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track with { Program = program };
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 一组音同时挪一个量（tick 与音高各一个增量）。
    /// 越界时整组按同一个量一起夹，保住组内的相对形状；时间不越过 0，音高出不了 0..127。
    /// 被选中的音可能越过没被选中的音，重排由 <see cref="Track.WithNotes"/> 负责。
    /// </summary>
    public Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch)
    {
        if (notes.Count == 0) return song;

        // 越界先查，两个增量都是 0 也查：否则同一个坏坐标会「挪 0 格没事、挪 1 格就炸」。
        long minStart = long.MaxValue;
        int minPitch = int.MaxValue;
        int maxPitch = int.MinValue;
        foreach (var reference in notes)
        {
            var note = NoteAt(song, reference, nameof(notes));
            if (note.StartTick < minStart) minStart = note.StartTick;
            if (note.Pitch < minPitch) minPitch = note.Pitch;
            if (note.Pitch > maxPitch) maxPitch = note.Pitch;
        }

        // 夹：整组往左顶到 0 就少挪这么多（minStart 本身为负的坏数据不在这里修）。
        long ticks = deltaTicks;
        if (SaturatingAdd(minStart, deltaTicks) < 0) ticks = -minStart;

        // 音高两个方向都夹；合法音高必在 0..127，两处不会同时生效，先后无所谓。
        int pitch = deltaPitch;
        if ((long)minPitch + pitch < 0) pitch = -minPitch;
        if ((long)maxPitch + pitch > 127) pitch = 127 - maxPitch;

        // 夹完等于没挪就原样返回（装饰器拿引用相等当判据）。
        if (ticks == 0 && pitch == 0) return song;

        // 读的都是原来那份 song 里的音：同一个坐标说两遍写进去的值一样，不需要去重。
        var touched = new Dictionary<int, Note[]>();
        foreach (var reference in notes)
        {
            var original = NoteAt(song, reference, nameof(notes));
            if (!touched.TryGetValue(reference.Track, out var edited))
                touched[reference.Track] = edited = song.Tracks[reference.Track].Notes.ToArray();

            // 位置从原数组现查；身份在一条轨里不重复，同一坐标说两遍查到的是同一格。
            int at = IndexAt(song, reference, nameof(notes));

            // 只点这两个字段：挪位置不换身份，Id 原样带过去。
            edited[at] = original with
            {
                Pitch = AddPitch(original.Pitch, pitch),
                StartTick = SaturatingAdd(original.StartTick, ticks),
            };
        }

        // 没碰过的轨连引用原样带走；碰过的写回去，重排由 WithNotes 做。
        var tracks = song.Tracks.ToArray();
        foreach (var (trackIndex, edited) in touched)
            tracks[trackIndex] = song.Tracks[trackIndex].WithNotes(edited);

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 把一个音的起点和时值设成给定的值（绝对赋值，界面自己算好目标位置）。
    /// 改时值可能让这个音越过邻居，排序由 <see cref="Track.WithNotes"/> 保证。
    /// </summary>
    public Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks)
    {
        var original = NoteAt(song, note, nameof(note));

        if (startTick < 0) startTick = 0;
        if (lengthTicks < 1) lengthTicks = 1;

        // 收的是时值不是起点（起点是用户钉住的那一头）。只有连 1 个 tick 都塞不下时才反过来
        // 把起点退一格 ——「时值至少 1」是不变量，不能让给溢出。
        long room = long.MaxValue - startTick;
        if (lengthTicks > room) lengthTicks = room;
        if (lengthTicks < 1)
        {
            lengthTicks = 1;
            startTick = long.MaxValue - 1;
        }

        if (original.StartTick == startTick && original.LengthTicks == lengthTicks) return song;

        var track = song.Tracks[note.Track];
        // 位置现查，上面已确认它存在；下标只是写回那一格用的中间量。
        int at = IndexAt(song, note, nameof(note));
        var notes = track.Notes.ToArray();
        // 同样只点这两个字段：改时值不换身份，Id 跟着这个音走。
        notes[at] = original with { StartTick = startTick, LengthTicks = lengthTicks };

        var tracks = song.Tracks.ToArray();
        tracks[note.Track] = track.WithNotes(notes);
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 删掉一组音。删空的轨保留，只是 <see cref="Track.NoteCount"/> 变成 0 —— 删轨是另一条命令（<see cref="DeleteTrack"/>）。
    /// </summary>
    public Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes)
    {
        if (notes.Count == 0) return song;

        // 按 (轨, 身份) 去重：要删的音按集合理解，说几遍都是同一个。
        // 按身份而不是下标挑要留下的音，内容相同的两个音才分得开。
        var doomed = new Dictionary<int, HashSet<NoteId>>();
        foreach (var reference in notes)
        {
            NoteAt(song, reference, nameof(notes));
            if (!doomed.TryGetValue(reference.Track, out var ids))
                doomed[reference.Track] = ids = new HashSet<NoteId>();
            ids.Add(reference.Id);
        }

        var tracks = song.Tracks.ToArray();
        foreach (var (trackIndex, ids) in doomed)
        {
            var track = song.Tracks[trackIndex];
            var kept = new List<Note>(track.Notes.Count);
            foreach (var note in track.Notes)
                if (!ids.Contains(note.Id)) kept.Add(note);

            tracks[trackIndex] = track.WithNotes(kept);
        }

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 把这条轨上 <c>[startTick, endTick)</c> 这段连时间一起抽走，后面的音前移补上。分派规则见接口上的说明。
    /// 剪空的轨保留；剪断留下的碎片发新身份，只是前移的音身份不变。
    /// 新号从 <see cref="NoteIdentity.FirstFree"/> 拿剪之前那一轨算（被整个剪掉的音也占着号）。
    /// 顺序由 <see cref="Track.WithNotes"/> 保证，这条命令自己不排。
    /// </summary>
    public Song CutRange(Song song, int trackIndex, long startTick, long endTick)
    {
        var track = TrackAt(song, trackIndex);

        // 起点大于终点就是调用方写错了 —— 抛，不猜。
        if (endTick < startTick)
            throw new ArgumentException(
                $"切口终点 {endTick} 在起点 {startTick} 之前：这段没有长度，不知道该抽掉哪一块。",
                nameof(endTick));

        if (startTick < 0) startTick = 0;
        long span = endTick - startTick;

        // 空区间是抽掉零个 tick，正常输入，不是错误。
        if (span == 0) return song;

        var kept = new List<Note>(track.Notes.Count);
        bool changed = false;

        NoteId nextId = NoteIdentity.FirstFree(track.Notes);

        foreach (var note in track.Notes)
        {
            long start = note.StartTick;
            long end = note.EndTick;

            if (end <= startTick)
            {
                kept.Add(note);
            }
            else if (start >= endTick)
            {
                kept.Add(note with { StartTick = start - span });  // 整个在右切口之后：前移
                changed = true;
            }
            else if (start < startTick)
            {
                // 跨过左切口：在左切口剪断，留下左边那截。盖住整个区间的那种右截就此丢掉，
                // 挪回来的话一个音会变成两个，「剪」就成了「分裂」。
                kept.Add(note with { LengthTicks = startTick - start, Id = nextId });
                nextId = nextId.Next;
                changed = true;
            }
            else if (end > endTick)
            {
                // 从区间里伸出右切口：剪下外面那截，挪到左切口接上。
                kept.Add(note with { StartTick = startTick, LengthTicks = end - endTick, Id = nextId });
                nextId = nextId.Next;
                changed = true;
            }
            else
            {
                changed = true;                                    // 整个在区间里：删掉，不入队
            }
        }

        if (!changed) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track.WithNotes(kept);
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>给某条轨改名，名字两端的空白会被去掉。</summary>
    public Song RenameTrack(Song song, int trackIndex, string name)
    {
        var track = TrackAt(song, trackIndex);

        // 去空白后什么都不剩的名字等于没有名字；null 也走这一条。
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                $"轨名不能是空的（收到「{name}」）：去掉两端空白之后总得剩下点什么。", nameof(name));

        string trimmed = name.Trim();
        if (string.Equals(track.Name, trimmed, StringComparison.Ordinal)) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track with { Name = trimmed };
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 删掉一整条轨。只剩一条时照样删（0 条轨是合法状态，撤销拿得回来）。
    /// <see cref="Track.TrackIndex"/> 不重编号：它是「来自文件里第几个轨块」的出处标记，不是列表排名。
    /// </summary>
    public Song DeleteTrack(Song song, int trackIndex)
    {
        TrackAt(song, trackIndex);   // 越界照抛

        var tracks = new List<Track>(song.Tracks.Count - 1);
        for (int i = 0; i < song.Tracks.Count; i++)
            if (i != trackIndex) tracks.Add(song.Tracks[i]);

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>取出第 <paramref name="trackIndex"/> 条轨，越界就抛。三条按轨下标干活的命令共用这一个检查。</summary>
    private static Track TrackAt(Song song, int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= song.Tracks.Count)
            throw new ArgumentOutOfRangeException(
                nameof(trackIndex), trackIndex,
                $"轨下标 {trackIndex} 越界：这首曲子有 {song.Tracks.Count} 条轨。");

        return song.Tracks[trackIndex];
    }

    /// <summary>
    /// 查一个坐标：这条轨上那个身份的音在数组里的位置，查不到就抛。
    /// 下标只在命令层内部用（写回音符要指向数组里那一格），对外一律是身份。
    /// <paramref name="paramName"/> 由调用方给，报的是调用方签名里的参数名。
    /// </summary>
    private static int IndexAt(Song song, NoteRef reference, string paramName)
    {
        if (reference.Track < 0 || reference.Track >= song.Tracks.Count)
            throw new ArgumentOutOfRangeException(
                paramName, reference,
                $"轨下标 {reference.Track} 越界：这首曲子有 {song.Tracks.Count} 条轨。");

        var track = song.Tracks[reference.Track];
        int at = IndexOfId(track, reference.Id);
        if (at < 0)
            throw new ArgumentOutOfRangeException(
                paramName, reference,
                // 报 .Value 而不是 Id 本身：NoteId.ToString() 会带上类型名，这句消息是给用户看的
                $"{reference.Id.Value} 号音不是第 {reference.Track} 条轨上的音："
                + $"这条轨有 {track.Notes.Count} 个音，没有一个是这个号。");

        return at;
    }

    /// <summary>
    /// 取出一个 <see cref="NoteRef"/> 指着的音，顺手把坐标查一遍。
    /// 先查再索引，顺序不能反：否则轨下标越界时先炸的是 <c>List</c> 的英文错误，中文消息轮不到。
    /// </summary>
    private static Note NoteAt(Song song, NoteRef reference, string paramName)
    {
        int at = IndexAt(song, reference, paramName);
        return song.Tracks[reference.Track].Notes[at];
    }

    /// <summary>
    /// 这条轨上身份是 <paramref name="id"/> 的音在数组里的位置；没有就是 -1（不抛）。
    /// 线性扫一遍，不建索引表：一次编辑只查那几下，而索引表每次改音符都要维护，容易过期。
    /// </summary>
    private static int IndexOfId(Track track, NoteId id)
    {
        for (int i = 0; i < track.Notes.Count; i++)
            if (track.Notes[i].Id == id) return i;

        return -1;
    }

    /// <summary>饱和加法：溢出就往那一头贴边，绝不绕回去（绕回去的 tick 会变成负数，一路没人报错）。</summary>
    private static long SaturatingAdd(long value, long delta)
    {
        if (delta > 0 && value > long.MaxValue - delta) return long.MaxValue;
        if (delta < 0 && value < long.MinValue - delta) return long.MinValue;
        return value + delta;
    }

    /// <summary>音高加上增量，并夹回 0..127。走 <see cref="long"/> 是为了不让 <c>int</c> 加法绕圈。</summary>
    private static int AddPitch(int pitch, int delta) => (int)Math.Clamp((long)pitch + delta, 0, 127);

    /// <summary>tick 0 上生效的微秒数：有速度事件就取它，没有就是 MIDI 的默认 500000（= 120 拍/分）。</summary>
    private static long MicrosecondsAtZero(TempoMap map)
    {
        foreach (var change in map.TempoChanges)
            if (change.Tick == 0) return change.MicrosecondsPerQuarterNote;

        return TempoMap.DefaultMicrosecondsPerQuarterNote;
    }

    /// <summary>按比例缩放一条速度，四舍五入到整数微秒。</summary>
    private static long Scale(long microsecondsPerQuarterNote, double factor)
        => RoundMicros(microsecondsPerQuarterNote * factor);

    /// <summary>
    /// 微秒数四舍五入到整数，下限 1、上限 <see cref="MaxMicrosPerQuarter"/>。
    /// 写成 <c>!(x &gt; 1)</c> 而不是 <c>x &lt;= 1</c>，是为了让 NaN 也走下限那条路。
    /// </summary>
    private static long RoundMicros(double micros)
    {
        if (!(micros > 1)) return 1;
        if (micros > MaxMicrosPerQuarter) return (long)MaxMicrosPerQuarter;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }
}
