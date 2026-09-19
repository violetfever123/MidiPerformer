using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Core.UseCases.Editing;

/// <summary>
/// 撤销 / 重做：横着罩在所有编辑命令外面的**装饰器**。
///
/// 撤销栈就是一摞旧的 <see cref="Song"/> 引用 —— 这就是 <see cref="Song"/> 不可变、
/// 命令返回新对象的直接兑现。它在转发前后记一笔账，除此之外什么都不做：
/// 命令本身一行都不知道有撤销这回事，将来加一条新命令，撤销自动就有。
///
/// <b>「改没改」比引用。</b>命令没改东西时返回的是传进来的那一个，于是这里根本不记账 ——
/// 撤销栈里不会攒下一堆按了没反应的格子。
///
/// 代价是内存，所以栈封顶 <see cref="MaxUndoSteps"/> 步，超了丢最老的。
///
/// **但代价不是「一份快照」**（这里从前写着「一万音符一份快照约 400KB」，是错的，差三个数量级）：
/// 改 BPM 原样带走整摞 <c>Tracks</c>、改移调也只换掉其中一条 —— 音符数组是**共享**的，
/// 从不复制。一步真正留下的只有：移调那一步一个 <c>Track[]</c> 加一个 <c>Track</c> record；
/// 改 BPM 那一步一张新 <c>TempoMap</c>（四个数组，长度是变速事件的条数，
/// 语料里最长的一首 3043 条 ≈ 97KB）。也就是说大头是**变速表**，不是音符数。
/// 封顶本身没问题，但照着「快照」去估这一步要多少内存会估错得很离谱。
/// </summary>
public sealed class UndoableSongEditor : ISongEditor
{
    /// <summary>撤销栈最多记多少步，超了丢最老的。见 spec「撤销：装饰器」。</summary>
    public const int MaxUndoSteps = 100;

    private readonly ISongEditor _inner;
    private readonly List<Song> _undo = new();
    private readonly List<Song> _redo = new();

    /// <summary>
    /// 最后一次交出去的曲子。撤销时要把它推进重做栈（「撤掉的那一步」= 现在这一份），
    /// 重做时反过来 —— 两个栈里装的都是整份 <see cref="Song"/>，不是「怎么改的」。
    /// </summary>
    private Song? _current;

    public UndoableSongEditor(ISongEditor inner) => _inner = inner;

    /// <summary>还有没有可以撤的。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>还有没有可以重做的。</summary>
    public bool CanRedo => _redo.Count > 0;

    public Song SetBpm(Song song, double beatsPerMinute)
        => Record(song, _inner.SetBpm(song, beatsPerMinute));

    public Song SetTranspose(Song song, int trackIndex, int semitones)
        => Record(song, _inner.SetTranspose(song, trackIndex, semitones));

    /// <summary>
    /// 换音色。**一格撤销**：从「大钢琴」挑到「口琴」是一步，撤销回去也是一步 ——
    /// 下拉框选一次就是一次选择，不像拖动那样中途会经过几十个值。
    /// </summary>
    public Song SetProgram(Song song, int trackIndex, int program)
        => Record(song, _inner.SetProgram(song, trackIndex, program));

    /// <summary>
    /// 挪音符这条命令**一次调用就是一格撤销**，哪怕界面上是一次鼠标拖动。
    ///
    /// 拖动过程中不发命令：界面先自己画预览，松手才算一次「挪到这里」交进来 ——
    /// 否则拖一下会攒出几百格撤销，用户按 Ctrl+Z 得按到手酸才能退回拖动之前。
    /// 所以这里不需要「合并连续的同一条命令」那种机制，装饰器一行都不用改。
    /// </summary>
    public Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch)
        => Record(song, _inner.MoveNotes(song, notes, deltaTicks, deltaPitch));

    /// <summary>
    /// 改时值同样是一格。注意它**可能让音符越位**（<see cref="Track.Notes"/> 会重排）——
    /// 那是撤销链上的一步，撤回去的时候整份旧 <see cref="Song"/> 一起回去，位置也就跟着回去了，
    /// 这里没有额外要记账的东西。
    ///
    /// <b>撤销之后界面手里那些坐标照样有效</b>，不用重算：它们是按身份认音的（见 <see cref="NoteRef"/>），
    /// 而撤销把音放回原位、身份一个都没换 —— 撤销链上装的每一份 <see cref="Song"/> 都是
    /// 同一批身份的不同版本（改音符那几条命令只 <c>with</c> 内容字段，剪断发的新号只增不改）。
    /// </summary>
    public Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks)
        => Record(song, _inner.SetNoteSpan(song, note, startTick, lengthTicks));

    public Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes)
        => Record(song, _inner.DeleteNotes(song, notes));

    /// <summary>
    /// 剪掉一段同样是**一格撤销**：一次调用推掉整条轨上一段区间，撤销一步回到剪之前。
    ///
    /// 这条挪动的是「后面所有音的位置」，牵动得比 <see cref="MoveNotes"/> 多得多，
    /// 但装饰器不用为此多记任何账：栈里装的是整份旧 <see cref="Song"/>，
    /// 位置、时值、音符数一起回去，没有「挪了多少要反向挪回来」这种事。
    /// </summary>
    public Song CutRange(Song song, int trackIndex, long startTick, long endTick)
        => Record(song, _inner.CutRange(song, trackIndex, startTick, endTick));

    public Song RenameTrack(Song song, int trackIndex, string name)
        => Record(song, _inner.RenameTrack(song, trackIndex, name));

    public Song DeleteTrack(Song song, int trackIndex)
        => Record(song, _inner.DeleteTrack(song, trackIndex));

    /// <summary>
    /// 撤掉上一步，返回该回去的那份曲子。栈空时返回 <c>null</c> 且什么都不做、不崩 ——
    /// 「没什么可撤的」是个正常状态，不是错误。
    /// </summary>
    public Song? Undo()
    {
        if (_undo.Count == 0) return null;

        var previous = Pop(_undo);
        if (_current is not null) _redo.Add(_current);
        _current = previous;
        return previous;
    }

    /// <summary>把刚撤掉的那一步做回来。重做栈空时返回 <c>null</c> 且什么都不做、不崩。</summary>
    public Song? Redo()
    {
        if (_redo.Count == 0) return null;

        var next = Pop(_redo);
        if (_current is not null) PushUndo(_current);
        _current = next;
        return next;
    }

    /// <summary>
    /// 忘掉一切：两个栈清空，<c>_current</c> 也放掉。
    ///
    /// <b>换一首曲子时必须调它。</b>撤销栈里装的是上一首曲子的 <see cref="Song"/> 引用，
    /// 不换掉的话接着按撤销会把人送到另一首曲子的历史里去。
    /// </summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _current = null;
    }

    /// <summary>
    /// 转发前后记账：命令确实产出了新对象才压栈并清空重做栈。
    ///
    /// 没改的命令（<c>ReferenceEquals</c> 为真）**既不入栈也不清重做栈**：
    /// 它不是一个「新命令」，用户手上那份曲子没有任何变化。
    /// </summary>
    private Song Record(Song before, Song after)
    {
        if (!ReferenceEquals(before, after))
        {
            PushUndo(before);
            // 撤回去之后又改了新的：原来那条重做链已经不在同一条时间线上了，作废。
            // 不丢的话，用户重做会凭空跳到一个他撤销之后又改过的状态上去。
            _redo.Clear();
        }
        _current = after;
        return after;
    }

    /// <summary>压入撤销栈并封顶：超了就从最老的开始丢。</summary>
    private void PushUndo(Song song)
    {
        _undo.Add(song);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
    }

    private static Song Pop(List<Song> stack)
    {
        var top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return top;
    }
}
