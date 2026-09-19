namespace MidiPerformer.Core.Model;

/// <summary>
/// 一条轨 = MIDI 文件里的**一个轨块 × 一个声道**。
///
/// 为什么按 (轨块, 声道) 而不是按轨块切：游戏里的口琴是单声部乐器，用户要的是「看清有几条声部」。
/// 格式 0 的 MIDI 把整首曲子塞在一个轨块里、靠声道分声部——按轨块切的话整首歌就是一条轨，
/// 既没法删声部也没法挑一条来弹。而且**音色（Program）本来就是声道事件**，
/// 只有切到声道这一层，「每轨一个音色」才说得通。
///
/// 没有任何音符的轨块（纯速度/版权信息轨）不成轨，直接不出现。
///
/// <see cref="TrackIndex"/> + <see cref="Channel"/> 唯一确定一条轨，也唯一确定原版
/// harmonica-auto-player 里的一个「候选」（它的 <c>MidiCandidate</c> 就是这两个字段）。
/// 全链对拍靠这一对做两边的 1:1 配对。
///
/// 不可变。<see cref="Transpose"/> 是轨的属性，**永远不落进 <see cref="Note"/>**——想改回来随时改，无损。
/// </summary>
/// <param name="TrackIndex">来自文件里第几个轨块（0 起）。同一轨块的不同声道共享这个编号。</param>
/// <param name="Channel">MIDI 声道 0..15（9 = 打击乐）。</param>
/// <param name="Name">轨名。文件里有就用文件里的，没有就用「声道 N」补齐。</param>
/// <param name="Program">该声道的音色号 0..127。只影响编辑器里的试听，发给游戏时永远是口琴那套键位。</param>
/// <param name="Notes">音符，按起始 tick 升序。构造器收的是**原始顺序**（MIDI 导入时排好的、工程文件里
/// 写好的那一份），它自己不做这件事 —— 不变量真正的维护点是 <see cref="WithNotes"/>，改音符一律走那一扇门。</param>
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
    /// 换音符、保持其余字段不变。
    /// <b>这条轨的不变量（音符按起点升序）在这里收口</b>：进来什么顺序都行，出去的一定有序。
    ///
    /// 为什么是这扇门自己管，而不是另开一个 <c>WithNotesSorted()</c> 让调用方挑：
    /// 那样就有两扇门，挑错的那一扇**不会报错** —— 它只是静静地交出一条破序的轨，
    /// 后面的错（按下标认音的地方全部错位、导出写成「后一个音先响」的事件序列）
    /// 也一声不吭地跑到底。这个仓库已经为「同一条不变量散在几处各管各的」付过一次代价：
    /// <c>SongEditor</c> 的 <c>MoveNotes</c> 和 <c>SetNoteSpan</c> 各写了一遍同样的
    /// <c>OrderBy(n =&gt; n.StartTick)</c>，而 <c>CutRange</c> 得靠一段
    /// 「新的起点是旧起点的单调不减函数」的论证才敢不排 —— 同一条不变量在三个地方各推理一次，
    /// 两次是实现、一次是反证。只剩一扇门，就不存在「挑哪一扇」这件事，
    /// 也没有哪一处再需要论证自己为什么不必排。
    ///
    /// 代价是本来就有序的调用点（删音符、剪一段）也要白排一次。这笔账划得来：
    /// 那些都是用户敲一下才跑一次的编辑命令，不在这条链的热路上，而 <c>OrderBy</c>
    /// 在已经有序的输入上本来就快；反过来，「这条命令从不重排」这种性质只写在注释里，
    /// 下一个人调一下分派顺序就能把它破掉，而没有任何东西会响。
    ///
    /// 排序用 <c>OrderBy</c>（LINQ 的稳定排序），**不是** <c>List.Sort</c>：
    /// 起点相同的音保持原来的先后。这不是花边 —— 它让「一个音都没越过邻居」这种最常见的改动
    /// 一个下标都不动，界面那套「改完重新算选中集」（<see cref="NoteRef"/> 里说的那件事）
    /// 就不必面对无谓的洗牌。
    ///
    /// 重排只换**位置**，不换**身份**：进来的音符带着 <see cref="Note.Id"/> 原样出去
    /// （这里从头到尾只动顺序）。身份的意义正在这儿 —— 一个音挪到数组别处去了，
    /// 指着它的那个号还是指着它。
    /// </summary>
    public Track WithNotes(IReadOnlyList<Note> notes)
        => this with { Notes = notes.OrderBy(n => n.StartTick).ToArray() };

    /// <summary>
    /// 值相等：音符**逐个**比，不是比列表引用。
    ///
    /// 为什么必须自己写：record 自动生成的相等会把 <see cref="Notes"/> 当引用比，
    /// 于是两份内容完全一样的轨也不相等 —— 而 S1 缝要的正是「导入 → 导出 → 再导入，
    /// 两个 <see cref="Song"/> 逐字段相等」。留着默认实现的话，那条测试即使实现全对也会红，
    /// 而且红得让人摸不着头脑。
    ///
    /// 与 <see cref="Song"/> 刻意不一致：<c>Song</c> 是**引用**相等，因为撤销装饰器拿
    /// 引用相等当「这条命令改没改」的判据（见 spec）。两处不一样是有意的：
    /// <c>Track</c> 是个值（内容），<c>Song</c> 是份文档（身份）。
    /// </summary>
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
