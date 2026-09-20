using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Core.UseCases.Editing;

/// <summary>
/// 撤销 / 重做：横着罩在所有编辑命令外面的装饰器。撤销栈就是一摞旧的 <see cref="Song"/> 引用，
/// 命令本身不知道有撤销这回事，将来加一条新命令撤销自动就有。
///
/// 「改没改」比引用：命令没改东西时返回的是传进来的那一个，于是不记账。栈封顶
/// <see cref="MaxUndoSteps"/> 步，超了丢最老的。
/// </summary>
public sealed class UndoableSongEditor : ISongEditor
{
    /// <summary>撤销栈最多记多少步，超了丢最老的。</summary>
    public const int MaxUndoSteps = 100;

    private readonly ISongEditor _inner;
    private readonly List<Song> _undo = new();
    private readonly List<Song> _redo = new();

    /// <summary>
    /// 最后一次交出去的曲子。撤销时把它推进重做栈，重做时反过来 ——
    /// 两个栈里装的都是整份 <see cref="Song"/>，不是「怎么改的」。
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

    /// <summary>换音色。一格撤销：下拉框选一次就是一次选择，不像拖动那样中途经过几十个值。</summary>
    public Song SetProgram(Song song, int trackIndex, int program)
        => Record(song, _inner.SetProgram(song, trackIndex, program));

    /// <summary>
    /// 挪音符：一次调用就是一格撤销，哪怕界面上是一次鼠标拖动 ——
    /// 界面先画预览，松手才交一次命令，所以不需要「合并连续的同一条命令」那种机制。
    /// </summary>
    public Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch)
        => Record(song, _inner.MoveNotes(song, notes, deltaTicks, deltaPitch));

    /// <summary>
    /// 改时值同样是一格。它可能让音符越位（<see cref="Track.Notes"/> 会重排），
    /// 但撤回去时整份旧 <see cref="Song"/> 一起回去，这里没有额外要记账的。
    ///
    /// 撤销之后界面手里的坐标照样有效，不用重算：它们按身份认音（见 <see cref="NoteRef"/>），
    /// 而撤销不换身份。
    /// </summary>
    public Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks)
        => Record(song, _inner.SetNoteSpan(song, note, startTick, lengthTicks));

    public Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes)
        => Record(song, _inner.DeleteNotes(song, notes));

    /// <summary>
    /// 剪掉一段同样是一格撤销：一次调用推掉整条轨上一段区间。
    /// 它挪动的是后面所有音的位置，但栈里装的是整份旧 <see cref="Song"/>，不用反向挪回来。
    /// </summary>
    public Song CutRange(Song song, int trackIndex, long startTick, long endTick)
        => Record(song, _inner.CutRange(song, trackIndex, startTick, endTick));

    public Song RenameTrack(Song song, int trackIndex, string name)
        => Record(song, _inner.RenameTrack(song, trackIndex, name));

    public Song DeleteTrack(Song song, int trackIndex)
        => Record(song, _inner.DeleteTrack(song, trackIndex));

    /// <summary>撤掉上一步，返回该回去的那份曲子。栈空时返回 <c>null</c>，不崩。</summary>
    public Song? Undo()
    {
        if (_undo.Count == 0) return null;

        var previous = Pop(_undo);
        if (_current is not null) _redo.Add(_current);
        _current = previous;
        return previous;
    }

    /// <summary>把刚撤掉的那一步做回来。重做栈空时返回 <c>null</c>，不崩。</summary>
    public Song? Redo()
    {
        if (_redo.Count == 0) return null;

        var next = Pop(_redo);
        if (_current is not null) PushUndo(_current);
        _current = next;
        return next;
    }

    /// <summary>
    /// 忘掉一切：两个栈清空，<c>_current</c> 也放掉。换一首曲子时必须调它，
    /// 否则接着按撤销会跳到上一首曲子的历史里去。
    /// </summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _current = null;
    }

    /// <summary>转发前后记账：命令确实产出了新对象才压栈并清空重做栈（没改的命令两者都不做）。</summary>
    private Song Record(Song before, Song after)
    {
        if (!ReferenceEquals(before, after))
        {
            PushUndo(before);
            // 撤回去之后又改了新的：原来那条重做链已经不在同一条时间线上，作废。
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
