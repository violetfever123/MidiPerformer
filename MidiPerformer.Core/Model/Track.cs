namespace MidiPerformer.Core.Model;

/// <summary>
/// 一条轨 = MIDI 文件里的一个轨块 × 一个声道；没有音符的轨块不成轨。
/// <see cref="TrackIndex"/> + <see cref="Channel"/> 唯一确定一条轨。不可变。
/// </summary>
/// <param name="TrackIndex">来自文件里第几个轨块（0 起）。同一轨块的不同声道共享这个编号。</param>
/// <param name="Channel">MIDI 声道 0..15（9 = 打击乐）。</param>
/// <param name="Name">轨名。文件里有就用文件里的，没有就用「声道 N」补齐。</param>
/// <param name="Program">该声道的音色号 0..127。只影响编辑器里的试听，发给游戏时永远是口琴那套键位。</param>
/// <param name="Notes">音符，按起始 tick 升序。构造器收的是原始顺序，排序由 <see cref="WithNotes"/> 收口。</param>
/// <param name="Transpose">整轨移调，单位半音。</param>
public sealed record Track(
    int TrackIndex,
    int Channel,
    string Name,
    int Program,
    IReadOnlyList<Note> Notes,
    int Transpose = 0)
{
    public int NoteCount => Notes.Count;

    /// <summary>该轨最后一个音的结束 tick（空轨为 0）。</summary>
    public long EndTick
    {
        get
        {
            long end = 0;
            foreach (var n in Notes)
                if (n.EndTick > end) end = n.EndTick;
            return end;
        }
    }

    /// <summary>
    /// 换音符、保持其余字段不变。这条轨的不变量（音符按起点升序）在这里收口：进来什么顺序都行，出去的一定有序。
    ///
    /// 用 <c>OrderBy</c>（稳定排序）而不是 <c>List.Sort</c>：起点相同的音保持原来的先后，
    /// 这个次序在卷帘定位和导出的事件序列里是看得见的。重排只换位置，<see cref="Note.Id"/> 原样带过。
    /// </summary>
    public Track WithNotes(IReadOnlyList<Note> notes)
        => this with { Notes = notes.OrderBy(n => n.StartTick).ToArray() };

    /// <summary>值相等：音符逐个比，不是比列表引用。（<see cref="Song"/> 则用引用相等。）</summary>
    public bool Equals(Track? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return TrackIndex == other.TrackIndex
            && Channel == other.Channel
            && Program == other.Program
            && Transpose == other.Transpose
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && Notes.SequenceEqual(other.Notes);
    }

    /// <summary>与 <see cref="Equals(Track?)"/> 对齐：音符参与哈希，且与顺序相关。</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TrackIndex);
        hash.Add(Channel);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Program);
        hash.Add(Transpose);
        foreach (var n in Notes) hash.Add(n);
        return hash.ToHashCode();
    }
}
