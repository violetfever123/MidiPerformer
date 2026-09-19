using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Core.UseCases.Editing;

/// <summary>
/// 编辑命令的真身：<b>纯函数、无状态</b>，一行撤销代码都没有。
///
/// 每条命令收散参、返回一个新的 <see cref="Song"/>；「改没改」就是「返回的引用是不是同一个」。
/// 所以改完与改前等价时返回的<b>就是传进来的那一个</b>（<c>ReferenceEquals</c> 为真），
/// 装饰器靠这一条决定要不要记一笔 —— 不需要额外的 <c>Changed</c> 字段。
///
/// 撤销不在这儿，也不在将来任何一条命令里：它由 <c>UndoableSongEditor</c> 横着罩在所有命令外面，
/// 于是加一条新命令，撤销自动就有（见 spec「撤销：装饰器」）。
/// </summary>
public sealed class SongEditor : ISongEditor
{
    /// <summary>速度的合法下限（拍/分）。界面与用例共用这一对边界，免得两边各写一个数、各错一处。</summary>
    public const double MinBpm = 1;

    /// <summary>速度的合法上限（拍/分）。</summary>
    public const double MaxBpm = 1000;

    /// <summary>
    /// 微秒/四分音符 的上限。缩放算出来的数可能超出 <see cref="long"/>，
    /// 而 <c>(long)</c> 转换在那种情况下是未定义值（和 <c>TempoMap</c> 拦 NaN 是同一类坑：
    /// 一个垃圾值会静静混进新速度表，再一路传到秒数上），所以宁可夹到边界。
    /// </summary>
    private const double MaxMicrosPerQuarter = long.MaxValue / 2.0;

    /// <summary>
    /// 改整曲速度。
    ///
    /// 做法是「定住 tick 0 的基准速度，其余变速点整体等比缩放」：
    /// 先把 tick 0 上生效的微秒数拿来做基准（有事件就取它，没有就是 MIDI 默认的 500000），
    /// 算出 <c>factor = 目标微秒 / 基准微秒</c>，再把表里**每一条**速度乘上它。
    ///
    /// 为什么不是把每条速度都设成目标值：**变速曲子的快慢关系要保住**。
    /// 全抹成同一个数等于把一首变速曲改成匀速的 —— 音符还在，曲子没了。
    /// 而为什么先定基准再整体缩放，而不是只改 tick 0 那一条：只改开头的话，后面那些变速段
    /// 相对开头的快慢也会跟着变（开头变慢了，中间那段的「两倍速」就变成「四倍速」了）。
    ///
    /// 音符一个字节都不动，不是「值相等」，是同一份：<see cref="Song.Tracks"/> 原样带过去，
    /// 连每条 <see cref="Track"/> 的引用都复用。
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

        // 原来 tick 0 上没有速度事件的话，缩放**动不了开头那一段** ——
        // 基础速度住在 TempoMap 的默认值里，只能补一条事件把它钉下来。
        // 补出来的值正好等于默认的 500000（目标本来就是 120）时，TempoMap 的构造器会把它丢掉，
        // 那是对的：丢掉之后 BeatsPerMinuteAt(0) 仍然是 120，结果一样，别去绕过它。
        if (!hasZero) scaled.Add(new TempoChange(0, RoundMicros(60_000_000.0 / beatsPerMinute)));

        var map = new TempoMap(song.TempoMap.Division, scaled, song.TempoMap.TimeSignatureChanges);

        // 改完跟改前一模一样（比如本来就是 120、速度表是空的）：原样还回去。
        // 装饰器拿引用相等当「这条命令改没改」的判据 —— 比完仍然返回新对象的话，
        // 撤销栈里就会多出一格什么都撤不动的记录，用户按一下撤销看着像没反应。
        if (map.TempoChanges.SequenceEqual(song.TempoMap.TempoChanges)) return song;

        // 轨整摞照旧（同一个列表、同一批 Track 对象）：音符不是「值相等」，是同一个
        return new Song(song.Tracks, map);
    }

    /// <summary>
    /// 改某条轨的移调。<b>绝对赋值</b>，界面自己算步进后的值。
    ///
    /// 只换目标轨那一个 <see cref="Track"/> 值，别的轨连引用都不变（数组是新的，元素是旧的）。
    /// 音符一个字节都不碰 —— 移调是轨的属性，播放和导出时才叠加到音高上。
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
    /// 改某条轨的音色（GM 编号）。**只影响试听**，音符一个字节都不动。
    ///
    /// 越界**抛**而不是夹：换音色是从一张 128 项的固定表里挑一个，不是一个可以填任意数的格子。
    /// 夹一下的话，界面传错了 200 会静静地变成 127（一个听着完全不一样的音色），
    /// 而抛出来至少是个能被发现的 bug —— 和 <see cref="SetNoteSpan"/> 那种「用户拖过头了」
    /// 的夹是两回事，那是用户意图，这是调用方写错了。
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
    ///
    /// <b>越界整组一起夹，不是逐个夹。</b>逐个夹的话，拖到最左边时那一组会被压成一摞 ——
    /// 和弦的形状、两个声部之间的错位当场就没了；整组按「最小的那个可挪量」缩一下，
    /// 至少还保持着原来的形状（代价是拖到边上之后整组少挪一点，手感上是「拖不动了」）。
    /// 两头都要夹：时间不能挪到 0 之前，音高不能出 0..127。
    ///
    /// <b>这条命令会破掉「按起点升序」。</b>「整组挪的是同一个量，先后顺序就不会变」这句话
    /// **只对选中的那几个音之间成立**。一个被选中的音照样能越过一个**没被选中**的音：
    /// 轨上是 <c>A@0</c> 和 <c>B@100</c>，只选中 A 往右挪 200，轨就成了 <c>[A@200, B@100]</c> ——
    /// <see cref="Track.Notes"/> 的升序承诺当场破掉，而且是**一声不吭**地破。
    ///
    /// 破了之后挨打的是导出那一侧：它会写出「后一个音先响」的事件序列，
    /// 同一个音高上的 note-off / note-on 一乱，发出去就是漏音或卡音。
    /// 对一个演奏器来说这不是排版问题。
    ///（从前还有第二条罪状：「按下标认音的东西全部错位」—— 界面存的是下标，重排一次就指错音。
    /// 那一条已经不存在了：坐标按身份寻址，重排改不掉谁是谁，见 <see cref="NoteRef"/>。）
    ///
    /// 修它的是 <see cref="Track.WithNotes"/>：下面每个改过的音都从那一扇门写回去，
    /// 于是「重排」就发生在写回去这一句里 —— 这条命令自己不排。
    /// 为什么这件事收在 <see cref="Track"/> 里，理由写在那条方法上。
    /// </summary>
    public Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch)
    {
        if (notes.Count == 0) return song;

        // 越界先查，哪怕两个增量都是 0 也查。放到后面查的话，同一个坏坐标会变成
        // 「挪 0 格没事、挪 1 格就炸」—— 调用方到底错没错，读代码的人没法一眼说清。
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

        // 夹：拖着整组往左顶到 0 就少挪这么多。minStart 本身为负（坏文件）时这个数是正的，
        // 于是「往左挪」反而把音推到 0 之后 —— 那是坏数据该有的样子，不该在这里修。
        long ticks = deltaTicks;
        if (SaturatingAdd(minStart, deltaTicks) < 0) ticks = -minStart;

        // 音高两个方向都夹。两处不能同时生效（合法的音高一定在 0..127 里，
        // 组内跨度不会超过 127），所以先后顺序无所谓，只是先抬后压读起来顺一点。
        int pitch = deltaPitch;
        if ((long)minPitch + pitch < 0) pitch = -minPitch;
        if ((long)maxPitch + pitch > 127) pitch = 127 - maxPitch;

        // 夹完等于没挪（整组本来就贴着边）：原样还回去。装饰器拿引用相等当判据，
        // 这里返回一个新对象的话，撤销栈里会攒下一格按了没反应的记录。
        if (ticks == 0 && pitch == 0) return song;

        // 读的都是原来那份 song 里的音，不是刚写进去的：同一个坐标在 notes 里说两遍
        // 也只是说同一个音，两次写进去的值一模一样，于是这里不需要去重。
        var touched = new Dictionary<int, Note[]>();
        foreach (var reference in notes)
        {
            var original = NoteAt(song, reference, nameof(notes));
            if (!touched.TryGetValue(reference.Track, out var edited))
                touched[reference.Track] = edited = song.Tracks[reference.Track].Notes.ToArray();

            // 位置从**原数组**现查：身份在一条轨里不重复（见 NoteIdentity），
            // 于是同一个坐标说两遍查到的是同一格，两次写进去的值一样。
            // 这也是这里唯一用到下标的地方 —— 它是个中间量，出了这一句就没用了。
            int at = IndexAt(song, reference, nameof(notes));

            // with 只点这两个字段：身份（Id）原样带过去 —— 挪一个音不换身份，它是同一个音换了个位置
            //（和 CutRange 里剪断留下的碎片正相反，那条命令的判据写在它的方法注释上）。
            edited[at] = original with
            {
                Pitch = AddPitch(original.Pitch, pitch),
                StartTick = SaturatingAdd(original.StartTick, ticks),
            };
        }

        // 没碰过的轨连引用都原样带走（数组是新的，元素是旧的）。
        // 碰过的轨写回去 —— 重排由 WithNotes 做（见方法注释：被选中的音会越过没被选中的音）。
        var tracks = song.Tracks.ToArray();
        foreach (var (trackIndex, edited) in touched)
            tracks[trackIndex] = song.Tracks[trackIndex].WithNotes(edited);

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 把一个音的起点和时值设成给定的值（绝对赋值，界面自己算好目标位置）。
    ///
    /// <b>改时值可能让这个音越过邻居。</b><see cref="Track.Notes"/> 承诺按起点升序，
    /// 而把起点右移、或者把时值拉长，都可能让这个音排到后面的音后面去 —— 破了这个承诺，
    /// 导出会写出「后一个音先响」的事件序列（同 <see cref="MoveNotes"/>，那里说了代价）。
    /// 界面那一头**不再受它影响**：坐标按身份寻址，这个音排到哪儿去了都还是它
    /// （理由写在 <see cref="NoteRef"/> 上）。
    ///
    /// 重排本身不在这里：改完的音符从 <see cref="Track.WithNotes"/> 写回去，那一扇门保证有序
    /// （为什么收在 <see cref="Track"/> 里、为什么必须是稳定排序，理由都写在那条方法上）。
    /// </summary>
    public Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks)
    {
        var original = NoteAt(song, note, nameof(note));

        if (startTick < 0) startTick = 0;
        if (lengthTicks < 1) lengthTicks = 1;

        // startTick + lengthTicks 不能溢出。收的是**时值**，不是起点：起点是用户钉住的那一头
        // （拖左边缘时尾巴不动、方向键微调时看的是这一段），收时值更接近他的意图。
        // 只有连 1 个 tick 都塞不下（起点已经贴在 long.MaxValue 上）才反过来把起点退一格，
        // 「时值至少 1」是不变量，不能让给溢出。
        long room = long.MaxValue - startTick;
        if (lengthTicks > room) lengthTicks = room;
        if (lengthTicks < 1)
        {
            lengthTicks = 1;
            startTick = long.MaxValue - 1;
        }

        if (original.StartTick == startTick && original.LengthTicks == lengthTicks) return song;

        var track = song.Tracks[note.Track];
        // 位置现查（上面那句已经确认过它存在，所以这里查得到）—— 下标只是写回那一格用的中间量
        int at = IndexAt(song, note, nameof(note));
        var notes = track.Notes.ToArray();
        // 同样只点这两个字段：身份跟着这个音走（改时值不换身份，见 Note.Id）
        notes[at] = original with { StartTick = startTick, LengthTicks = lengthTicks };

        var tracks = song.Tracks.ToArray();
        tracks[note.Track] = track.WithNotes(notes);
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 删掉一组音。
    ///
    /// <b>删空的轨保留。</b>轨还在，只是 <see cref="Track.NoteCount"/> 变成 0 ——
    /// 删轨是另一条命令（<see cref="DeleteTrack"/>），两者不能混：
    /// 「把这个音删掉」和「把这条声部整条拿掉」在用户心里是两件事，
    /// 悄悄替他把空轨也顺手删了，他再想往那条轨上放音就没地方放了。
    /// </summary>
    public Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes)
    {
        if (notes.Count == 0) return song;

        // 去重：同一份 Song 上同一个 (轨, 身份) 说的是同一个音，说三遍还是删那一个。
        // 「要删的音」本来就该按集合理解 —— 界面横拖出来的选中集是顺手并起来的，重叠是常态，
        // 而「把 7 号音删两次」这句话本身没有意义。用 HashSet 而不是 List，
        // 下面那句「这个音要不要留」才是一次判断，也不用担心重复扣。
        //
        // 按**身份**去重、按身份挑要留下的音：内容一模一样的两个音（同刻同音高同力度）
        // 现在是分得开的两个音了 —— 从前按下标删也能分开，但前提是界面手上有对的下标；
        // 内容相同而下标错位那一下，删掉的是另一个音，而且不报错。
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
    /// 把这条轨上 <c>[startTick, endTick)</c> 这段连时间一起抽走，后面的音前移补上。
    ///
    /// 一边扫一边分派，分派规则见接口上的那张表。这里只补两件实现上的事：
    ///
    /// <b>顺序不是这条命令的事。</b>音符从下面那一句 <see cref="Track.WithNotes"/> 写回去，
    /// 而「按起点升序」是那一扇门保证的 —— 这里一个字节都不用管它。
    /// 从前这里挂着一整段论证（「新的起点是旧起点的单调不减函数，所以有序数组过一遍出来还有序，
    /// 这条命令从不需要重排」）：那段话没错，但它要读的人**先信一个证明、再信这段代码**，
    /// 而且任何一次分派顺序的改动都能把它悄悄推翻。现在这条性质由 <see cref="Track.WithNotes"/> 给 ——
    /// 读的人不用再信任何证明，整条链上也没有第二处需要证明它。这正是这条命令
    /// （以及 <see cref="MoveNotes"/>、<see cref="SetNoteSpan"/>）不再各自 <c>OrderBy</c> 的意义。
    ///
    /// <b><see cref="Track.NoteCount"/> 变成 0 的轨保留</b>，和 <see cref="DeleteNotes"/> 一个道理：
    /// 剪空了是「这条声部这段没东西」，不是「这条声部不要了」。
    ///
    /// <b>剪断留下的碎片是新音，发新身份；只是前移的那些音身份不变。</b>
    /// 判据是「这个音还是不是原来那个音」：前移的音一个字节没缺，只是换了个位置，
    /// 它当然还是它（和 <see cref="MoveNotes"/>、<see cref="SetNoteSpan"/> 一个道理，那两条命令
    /// 改完的音身份都不换）；而被剪断的音**已经不在了** —— 留下的是它的碎片，
    /// 左边那截和右边那截都不是原来的那个音（右边那截甚至被挪到了别处接上）。
    /// 碎片沿用原音的身份是错的：那个身份代表的是「原来那个音」，
    /// 界面手里可能还攥着它（选中集、拖动的锚点），继续用就等于告诉界面「那个音还在，只是变短了」。
    ///
    /// 新号从 <see cref="NoteIdentity.FirstFree"/> 来（这一轨现有身份的最大值 + 1，不是「数一数有几个音」），
    /// 而且是**拿原来那一轨算的**，不是拿剪完的那一轨：被整个剪掉的音也占着号，
    /// 从剪完的那一轨往上发就有可能发出一个「这一趟刚被剪掉、界面上还看得见旧曲子里的它」的号。
    /// </summary>
    public Song CutRange(Song song, int trackIndex, long startTick, long endTick)
    {
        var track = TrackAt(song, trackIndex);

        // 界面在算完小节边界之后就该把「起点大于终点」换过来（用户把两个框填反了是常事，
        // 那是要照顾的输入，不是错误）。换过来还反着，就是调用方自己写错了 —— 抛，不猜。
        if (endTick < startTick)
            throw new ArgumentException(
                $"切口终点 {endTick} 在起点 {startTick} 之前：这段没有长度，不知道该抽掉哪一块。",
                nameof(endTick));

        if (startTick < 0) startTick = 0;
        long span = endTick - startTick;

        // 空区间是「抽掉零个 tick」，和 DeleteNotes 收到空集一样，是正常输入，不是错误。
        if (span == 0) return song;

        var kept = new List<Note>(track.Notes.Count);
        bool changed = false;

        // 剪断留下的碎片从这儿取号。为什么取号的范围是**剪之前**那一轨：见方法注释最后一节。
        NoteId nextId = NoteIdentity.FirstFree(track.Notes);

        foreach (var note in track.Notes)
        {
            long start = note.StartTick;
            long end = note.EndTick;

            if (end <= startTick)
            {
                kept.Add(note);                                   // 整个在左切口之前
            }
            else if (start >= endTick)
            {
                kept.Add(note with { StartTick = start - span }); // 整个在右切口之后：前移
                changed = true;
            }
            else if (start < startTick)
            {
                // 跨过左切口（含「整个区间都被它盖住」那种）：在左切口剪断，留下左边那截。
                // 盖住整个区间的那种，右边那截就此丢掉 —— 挪回来的话它紧贴着左截，
                // 一个音变成两个，「剪」就成了「分裂」（见接口上的说明）。
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
                changed = true;                                   // 整个在区间里：删掉，不入队
            }
        }

        if (!changed) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track.WithNotes(kept);
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 给某条轨改名。名字两端的空白会被去掉（用户从别处粘过来的名字常带一个尾空格，
    /// 留着它会让「贝斯」和「贝斯 」在列表里看着一模一样、排序和查找却分成两个）。
    /// </summary>
    public Song RenameTrack(Song song, int trackIndex, string name)
    {
        var track = TrackAt(song, trackIndex);

        // 去掉两端空白就什么都不剩的名字等于没有名字，那是导入时给「声道 N」填的活儿，
        // 不该由用户手动改成一个空白标签 —— 列表上会是一行空白，看着像丢了东西。
        // null 也走这一条（可空注解说了 name 不该是 null，真收到也别让它变成 NullReferenceException）。
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
    /// 删掉一整条轨。
    ///
    /// <b>只剩一条时照样删</b>：空曲子（0 条轨）是合法状态，撤销拿得回来 ——
    /// 在这里挡一道「至少留一条」，用户删最后一条声部时会撞上一句莫名其妙的错误，
    /// 而他本来就只是想把这条不要的删掉。
    ///
    /// <b><see cref="Track.TrackIndex"/> 不重编号。</b>那是「来自文件里第几个轨块」的出处标记，
    /// 是这条轨的身份，不是它在列表里的排名 —— 删掉第 0 条之后，剩下那条仍然带着原来的编号。
    /// 它们本来就是同一个轨块切出来的两个声道，重编号会把这条亲缘关系抹掉。
    /// 代价是删除之后 <see cref="Song.Tracks"/> 的下标整体前移，界面存的旧下标当场作废。
    /// </summary>
    public Song DeleteTrack(Song song, int trackIndex)
    {
        TrackAt(song, trackIndex);   // 越界照抛。返回的那条轨这里用不上 —— 删的正是它

        var tracks = new List<Track>(song.Tracks.Count - 1);
        for (int i = 0; i < song.Tracks.Count; i++)
            if (i != trackIndex) tracks.Add(song.Tracks[i]);

        return new Song(tracks, song.TempoMap);
    }

    /// <summary>
    /// 取出第 <paramref name="trackIndex"/> 条轨，越界就抛。
    ///
    /// 三条按轨下标干活的命令（移调 / 改名 / 删轨）共用一个检查、一句中文错：各写各的话，
    /// 改了一处的措辞或边界、另一处忘了跟，用户会拿到两句说法不一样的话。
    /// </summary>
    private static Track TrackAt(Song song, int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= song.Tracks.Count)
            throw new ArgumentOutOfRangeException(
                nameof(trackIndex), trackIndex,
                $"轨下标 {trackIndex} 越界：这首曲子有 {song.Tracks.Count} 条轨。");

        return song.Tracks[trackIndex];
    }

    /// <summary>
    /// 查一个坐标：这条轨上那个身份的音**在数组里的位置**，查不到就抛。
    ///
    /// <b>位置只在命令层内部用</b>（写回一个音符要指向它在数组里的那一格），对外一律是身份。
    /// 这就是「按身份寻址」落地的样子：签名上收的是 <see cref="NoteRef"/>（轨 + 身份），
    /// 下标是这里现算出来的一个中间量 —— 命令跑完它就没了，不留到下一份曲子上。
    ///
    /// <b>查不到就抛</b>，而不是当成「没这个音」悄悄跳过：坐标只该指着**同一份曲子**里
    /// 确实存在的那个音，拿别处算出来的号来用是调用方的 bug，不是正常状态 ——
    /// 悄悄跳过的话，用户看到的是「拖了五个音，只有一个动了」，而没有任何地方报错。
    /// 从前这里判的是「下标越界」；换成身份之后判的是「这条轨上没有这个号」，
    /// 触发的情形其实变多了（不只是算错，还可能是那个音刚被删掉），所以更要抛。
    ///
    /// 找不到时用的字眼是「不是这条轨上的音」而不是「越界」：号是发的不是排的
    /// （见 <see cref="NoteIdentity"/>），一个号不在这条轨上未必说明它大得离谱 ——
    /// 它可能只是**另一条轨**上的音，说「越界」会把人往「号太大」那个方向带。
    ///
    /// <paramref name="paramName"/> 由调用方给：报的该是调用方签名里的那个参数名
    /// （<c>note</c> / <c>notes</c>），不是这里这个内部参数名，否则报错指着的地方在调用栈上找不到。
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
                // 报的是 reference.Id.Value 而不是 reference.Id：NoteId.ToString() 会带上类型名
                // （「NoteId { 7 }」），那是给写代码的人看的，这句话是给用户看的
                $"{reference.Id.Value} 号音不是第 {reference.Track} 条轨上的音："
                + $"这条轨有 {track.Notes.Count} 个音，没有一个是这个号。");

        return at;
    }

    /// <summary>
    /// 取出一个 <see cref="NoteRef"/> 指着的音，顺手把坐标查一遍。
    /// 只是 <see cref="IndexAt"/> 外面套一层取值 —— 两处查的是同一件事，规矩只有一份。
    ///
    /// <b>先查再索引，顺序不能反。</b>写成 <c>song.Tracks[reference.Track].Notes[IndexAt(…)]</c>
    /// 看着更短，但 C# 从左往右求值，轨下标越界时先炸的是 <c>List</c> 那句英文错误
    /// （<c>Index was out of range…</c>），下面那句中文消息根本轮不到 —— 测试逮到过这一下。
    /// </summary>
    private static Note NoteAt(Song song, NoteRef reference, string paramName)
    {
        int at = IndexAt(song, reference, paramName);
        return song.Tracks[reference.Track].Notes[at];
    }

    /// <summary>
    /// 这条轨上身份是 <paramref name="id"/> 的音在数组里的位置；没有就是 -1（<b>不抛</b>）。
    ///
    /// 线性扫一遍，不建索引表：一条轨最多几千个音，而一次编辑只查那几下
    /// （用户敲一下才跑一次，不在这条链的热路上）。建表的代价是**每次改音符都得维护它**，
    /// 而维护它的地方正是「音符数组换了一份」的那扇门 —— 一张会过期的表比一次线性扫危险得多。
    /// </summary>
    private static int IndexOfId(Track track, NoteId id)
    {
        for (int i = 0; i < track.Notes.Count; i++)
            if (track.Notes[i].Id == id) return i;

        return -1;
    }

    /// <summary>
    /// 饱和加法：溢出就往那一头贴边，绝不绕回去。
    ///
    /// 一个绕回去的 tick 会变成**负数**，于是那个音跑到了曲子开头之前 ——
    /// 画不出来、导不出去，而且一路没人报错（和 <see cref="RoundMicros"/> 拦 NaN 是同一类谨慎：
    /// 输入来自文件与用户操作，两边都不可信）。贴着 long.MaxValue 虽然荒唐，至少还是个「很靠后」。
    /// </summary>
    private static long SaturatingAdd(long value, long delta)
    {
        if (delta > 0 && value > long.MaxValue - delta) return long.MaxValue;
        if (delta < 0 && value < long.MinValue - delta) return long.MinValue;
        return value + delta;
    }

    /// <summary>
    /// 音高加上增量，并夹回 0..127。
    ///
    /// 走 <see cref="long"/> 是为了不让 <c>int</c> 加法绕圈（进到负数的音高会一路走到
    /// 发给游戏的按键映射上）；夹回范围则是替坏文件兜底 —— 导入时本该夹过，这里是第二道。
    /// </summary>
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
    ///
    /// 写成 <c>!(x &gt; 1)</c> 而不是 <c>x &lt;= 1</c>：NaN 在两种比较下都是 false，
    /// 一个 NaN 会一路走到 <c>(long)</c> 转换上（那是未定义值），
    /// 而这里的输入来自「文件里的速度 × 用户输入的速度」，两边都不可信。
    /// </summary>
    private static long RoundMicros(double micros)
    {
        if (!(micros > 1)) return 1;
        if (micros > MaxMicrosPerQuarter) return (long)MaxMicrosPerQuarter;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }
}
