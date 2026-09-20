using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 编辑命令的入口：改整曲速度、改某条轨的移调与音色、挪音符、改时值、删音符、剪掉一段、改轨名、删轨。
///
/// 收散参、返回新的 <see cref="Song"/>：曲子不可变，「改没改」就等于「返回的引用是不是同一个」，
/// 不需要一个 <c>Changed</c> 字段。撤销不在这张嘴上，它是 <c>UndoableSongEditor</c> 装饰器多出来的能力。
/// </summary>
public interface ISongEditor
{
    /// <summary>
    /// 把整曲速度改成 <paramref name="beatsPerMinute"/> 拍/分。改的是 <see cref="TempoMap"/> 里的速度事件
    /// （tick 0 的基准速度按目标值定下来，其余变速点按同一比例缩放），音符数组一个字节都不动。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="beatsPerMinute">目标速度，1..1000 拍/分。越界或不是有限数就抛。</param>
    /// <returns>新的曲子；本来就是这个速度时返回 <paramref name="song"/> 本身。</returns>
    Song SetBpm(Song song, double beatsPerMinute);

    /// <summary>
    /// 把第 <paramref name="trackIndex"/> 条轨的移调设成 <paramref name="semitones"/> 个半音。
    /// 绝对赋值不是增量，界面自己算「当前值 ± 1」再传进来。移调只落在 <see cref="Track.Transpose"/> 上，
    /// 永远不写回 <see cref="Note"/>。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="semitones">移调半音数（可正可负）。</param>
    /// <returns>新的曲子；本来就是这个值时返回 <paramref name="song"/> 本身。</returns>
    Song SetTranspose(Song song, int trackIndex, int semitones);

    /// <summary>
    /// 把第 <paramref name="trackIndex"/> 条轨的音色设成 GM 的 <paramref name="program"/> 号。
    /// 只影响试听 —— 发给游戏时永远是口琴那套键位。和 <see cref="SetTranspose"/> 一样只换那一格。
    ///
    /// 9 号声道（打击乐）不特殊对待：界面不给那一轨画下拉框，但命令不替界面挡这一格。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="program">GM 音色号 0..127。越界抛（换音色是从一张表里挑一个，不是填一个数）。</param>
    /// <returns>新的曲子；本来就是这一号音色时返回 <paramref name="song"/> 本身。</returns>
    Song SetProgram(Song song, int trackIndex, int program);

    /// <summary>
    /// 把一组音同时挪动 <paramref name="deltaTicks"/> 个 tick、<paramref name="deltaPitch"/> 个半音。
    /// 收增量而不是目标位置：一组音要保住彼此的相对关系，唯一说得清的说法就是「都挪这么远」。
    ///
    /// 越界时整组一起夹住而不是逐个夹（逐个夹会把整组压成一摞）：时间不能挪到 0 之前，音高不出 0..127。
    /// 挪完这条轨的音符数组会重排回起点升序，由 <see cref="Track.WithNotes"/> 负责。
    /// 重排不影响调用方手里的 <see cref="NoteRef"/> —— 它按身份寻址。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="notes">要动的音。空集时返回 <paramref name="song"/> 本身。**里面有认不出的坐标就抛**，不当「没这个音」放过。</param>
    /// <param name="deltaTicks">时间上的增量，可正可负。</param>
    /// <param name="deltaPitch">音高上的增量，可正可负。</param>
    /// <returns>新的曲子；一个音都没动时返回 <paramref name="song"/> 本身。</returns>
    Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch);

    /// <summary>
    /// 把一个音的起点和时值设成给定的值（绝对赋值，界面自己算好目标位置）。
    /// 拖左边缘、拖右边缘、方向键微调都归到这一条上。
    ///
    /// 会夹住不变量：起点不小于 0、时值不小于 1 个 tick、两者相加不溢出。
    /// 改时值可能让这个音越过邻居，那时数组重排（同 <see cref="MoveNotes"/>）；被改的音身份不变。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="note">要改的音。</param>
    /// <param name="startTick">目标起点 tick。</param>
    /// <param name="lengthTicks">目标时值（tick），至少 1。</param>
    /// <returns>新的曲子；本来就是这段时值时返回 <paramref name="song"/> 本身。</returns>
    Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks);

    /// <summary>
    /// 删掉一组音。卷帘上横拖删掉区间内所有音也走这一条 ——
    /// 哪几个音落在那段区间里由界面算，命令只管删，不必懂「区间」，也不必懂像素。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="notes">要删的音。空集时返回 <paramref name="song"/> 本身。</param>
    /// <returns>新的曲子；一个音都没删时返回 <paramref name="song"/> 本身。</returns>
    Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes);

    /// <summary>
    /// 把第 <paramref name="trackIndex"/> 条轨上 <c>[startTick, endTick)</c> 这一段连时间一起抽走：
    /// 区间里的音删掉，区间之后的音整体前移，让剩下的接上来。和 <see cref="DeleteNotes"/> 是两件事 ——
    /// 那条只把音拿走、谱面长度一个 tick 都不变，这条是整段抽走，后面的音提前落下来。
    ///
    /// 只动这一条轨，别的轨连引用都不变，于是从这一刀往后这条轨和别的轨永久错位（那是这个功能的定义）。
    /// 两个后果：<see cref="Song.EndTick"/> 是所有轨的最大值，剪一条轨不会让整曲变短；
    /// <see cref="TempoMap"/> 整曲共用、不跟着挪，被前移的那段按它新位置上的速度演奏。
    ///
    /// 区间由界面算：命令只认 tick，「从第 5 小节到第 8 小节」那种对齐由界面拿小节宽度换算好再传进来。
    ///
    /// 剪，不是分裂：跨过切口的音在切口处剪断，绝不一个变两个，跑完音符数只会变少或不变。
    /// 跨过左切口且伸出右切口的音剪下右截挪到左切口接上；整个区间被同一个音盖住时也在左切口剪断、
    /// 右边那截丢掉，所以一个长音会被剪短。剪出来的那两截发新身份（<see cref="Note.Id"/>），
    /// 整体前移的音身份不变。
    ///
    /// 剪完音符数组仍是起点升序，但排序由 <see cref="Track.WithNotes"/> 保证，这条命令自己不管。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="startTick">要抽掉的那一段的起点，含。负数夹到 0。</param>
    /// <param name="endTick">要抽掉的那一段的终点，不含。小于 <paramref name="startTick"/> 时抛。</param>
    /// <returns>新的曲子；这段本来就是空的时返回 <paramref name="song"/> 本身。</returns>
    Song CutRange(Song song, int trackIndex, long startTick, long endTick);

    /// <summary>给某条轨改名，两端的空白会被去掉。名字只是显示用的标签，不影响播放、导出或任何换算。</summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="name">新名字。去掉两端空白后不能是空的，否则抛。</param>
    /// <returns>新的曲子；本来就是这个名字时返回 <paramref name="song"/> 本身。</returns>
    Song RenameTrack(Song song, int trackIndex, string name);

    /// <summary>
    /// 删掉一整条轨。这是唯一一条会改变轨的条数的命令 —— 删完 <see cref="Song.Tracks"/> 的下标整体前移，
    /// 界面上「选中了哪条轨」和存下来的轨下标当场作废。
    /// <see cref="Track.TrackIndex"/> 是「来自文件里第几个轨块」的出处标记，不重编号。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <returns>新的曲子；只剩一条轨时**照样删**（空曲子是合法的，撤销拿得回来）。</returns>
    Song DeleteTrack(Song song, int trackIndex);
}
