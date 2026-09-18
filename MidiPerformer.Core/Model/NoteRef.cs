namespace MidiPerformer.Core.Model;

/// <summary>
/// 指向某一份 <see cref="Song"/> 里某个音符的坐标：第几条轨 + 那条轨音符数组里的下标。
///
/// <b>它不是身份，是一张当时的地址。</b><see cref="Track.Notes"/> 承诺按起始 tick 升序，
/// 于是任何一次改动都可能把音符挪到邻居后面、让整条数组重排 —— 同一个音的下标下一次就变了。
/// 所以 <see cref="NoteRef"/> <b>只在它被算出来的那一份 <see cref="Song"/> 上有效</b>：
/// 命令用它精确地指出「动哪几个音」（下标在同一份曲子里没有歧义，两个一模一样的音也分得开），
/// 而界面在每次改动之后必须拿新的 <see cref="Song"/> 重新算一遍选中集，不能把旧下标留着用。
///
/// 换成「按音高 + tick 认音」是另一条路，但那种认法碰上两个完全一样的音就没法区分 ——
/// 命令这一层要的是精确，认不认得住是界面那一层的事。
/// </summary>
/// <param name="Track"><see cref="Song.Tracks"/> 里的下标。</param>
/// <param name="Index">该轨 <see cref="Track.Notes"/> 里的下标。</param>
public readonly record struct NoteRef(int Track, int Index);
