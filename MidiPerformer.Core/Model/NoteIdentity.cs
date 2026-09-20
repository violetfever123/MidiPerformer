namespace MidiPerformer.Core.Model;

/// <summary>
/// 发身份的地方 —— 整条链上只有这里写出新的 <see cref="NoteId"/>。
///
/// 身份是发的不是算的：算出来的号碰上两个一模一样的音就分不开。发号还必须可重现 ——
/// 只由这一次读取的音符顺序决定，一轨一轨地从头数，第几个音就是几号，
/// 这样 MIDI 导入和读工程文件两条路口给出的号才一致。
/// </summary>
internal static class NoteIdentity
{
    /// <summary>给一轨音符按给定顺序发号：1、2、3……（<paramref name="notes"/> 的顺序就是号的顺序，这里不排序）。</summary>
    internal static Note[] AssignInOrder(IReadOnlyList<Note> notes)
    {
        var numbered = new Note[notes.Count];
        for (int i = 0; i < notes.Count; i++)
            numbered[i] = notes[i] with { Id = new NoteId(i + 1) };
        return numbered;
    }

    /// <summary>
    /// 这一轨下一个能用的号：现有身份的最大值 + 1（一个音都没有时是 1）。剪切用它给新音发号。
    ///
    /// 从最大值往上发而不是数音符个数，否则删掉一个音之后再剪一刀会把刚删掉的号又发一次。
    /// </summary>
    internal static NoteId FirstFree(IReadOnlyList<Note> notes)
    {
        int max = 0;
        foreach (var note in notes)
            if (note.Id.Value > max) max = note.Id.Value;

        return new NoteId(max + 1);
    }

    /// <summary>
    /// 把一轨音符的身份盘一遍：互不相同且都不是 0 号就原样还回去，不合规就整轨重发一遍。
    /// 读工程文件之后用它 —— 盘上的号可能是旧版本存的或被人改过的。
    /// </summary>
    internal static Note[] Normalized(IReadOnlyList<Note> notes)
    {
        var seen = new HashSet<int>();
        foreach (var note in notes)
        {
            int id = note.Id.Value;
            if (id == 0 || !seen.Add(id)) return AssignInOrder(notes);
        }

        return notes as Note[] ?? notes.ToArray();
    }
}
