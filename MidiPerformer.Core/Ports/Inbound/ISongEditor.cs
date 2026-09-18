using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 编辑命令的入口：改整曲速度、改某条轨的移调、挪音符、改时值、删音符、改轨名、删轨。
///
/// <b>收散参、返回新的 <see cref="Song"/>。</b>没有 Request、没有 Result ——
/// <see cref="Song"/> 不可变，「改没改」就等于「返回的引用是不是同一个」，
/// 装饰器用 <c>ReferenceEquals</c> 判断就够，不需要一个 <c>Changed</c> 字段
/// （见 spec 的「Request / Result」一节）。
///
/// <b>撤销不在这张嘴上。</b>它是 <c>UndoableSongEditor</c> 装饰器的能力：装饰器实现了同一个接口，
/// 于是它多出来的那几个方法（<c>Undo</c> / <c>Redo</c> / <c>Reset</c> / <c>CanUndo</c> / <c>CanRedo</c>）
/// 只长在装饰器上，界面拿到的就是装饰器 —— 这不是「接口不全」，正是装饰器存在的意思：
/// 撤销横切在所有命令外面，命令本身一行都不知道有它。
///
/// 这是全程序<b>唯一</b>一个入站端口，开它的唯一理由就是装饰器给了它第二个实现
/// （<c>SongEditor</c> 真干活，<c>UndoableSongEditor</c> 记账）。
/// </summary>
public interface ISongEditor
{
    /// <summary>
    /// 把整曲速度改成 <paramref name="beatsPerMinute"/> 拍/分。
    ///
    /// 音符数组<b>一个字节都不动</b>：改的是 <see cref="TempoMap"/> 里的速度事件
    /// （tick 0 的基准速度按目标值定下来，其余变速点按同一比例缩放，段与段之间的快慢关系保住）。
    /// 于是卷帘上的音符位置一动不动 —— 卷帘是 tick 轴，跟着缩放的是「一个 tick 有多长」，
    /// 也就是总时长与播放快慢。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="beatsPerMinute">目标速度，1..1000 拍/分。越界或不是有限数就抛。</param>
    /// <returns>新的曲子；本来就是这个速度时返回 <paramref name="song"/> 本身。</returns>
    Song SetBpm(Song song, double beatsPerMinute);

    /// <summary>
    /// 把第 <paramref name="trackIndex"/> 条轨的移调设成 <paramref name="semitones"/> 个半音。
    ///
    /// <b>绝对赋值，不是增量</b>：界面自己算 <c>当前值 ± 1</c> / <c>± 12</c> 再传进来。
    /// 移调始终只是 <see cref="Track.Transpose"/>，<b>永远不写回 <see cref="Note"/></b> ——
    /// 改回来是无损的。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="semitones">移调半音数（可正可负）。</param>
    /// <returns>新的曲子；本来就是这个值时返回 <paramref name="song"/> 本身。</returns>
    Song SetTranspose(Song song, int trackIndex, int semitones);

    /// <summary>
    /// 把一组音同时挪动 <paramref name="deltaTicks"/> 个 tick、<paramref name="deltaPitch"/> 个半音。
    ///
    /// <b>收增量，不收目标位置</b>（和 <see cref="SetTranspose"/> 的绝对赋值相反，那是故意的）：
    /// 一组音要保住彼此的相对关系，唯一说得清的说法就是「都挪这么远」。
    /// 界面拖一个音也好、方向键微调也好，都归到这一条上。
    ///
    /// <b>越界时整组一起夹住，不是逐个夹。</b>把每个音各自夹回合法范围，
    /// 拖到边界上整组会被压成一摞（相对位置没了）；整组按最小可挪量缩一下，
    /// 至少还保持着原来的形状。夹的两头是：时间不能挪到 0 之前，音高不能出 0..127。
    ///
    /// 挪完这条轨的音符数组会**重排回起点升序** —— 一组音挪的是同一个量只保证它们**彼此之间**
    /// 的先后不变，一个被选中的音照样能越过一个没被选中的音。重排是**稳定**的：
    /// 没越过邻居的改动一个下标都不变，而越过了的那些，下标会跟着挪
    /// （<see cref="NoteRef"/> 的注释里说了这件事，界面得重新算选中集）。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="notes">要动的音。空集时返回 <paramref name="song"/> 本身。**里面有越界的 ref 就抛**，不当「没这个音」放过。</param>
    /// <param name="deltaTicks">时间上的增量，可正可负。</param>
    /// <param name="deltaPitch">音高上的增量，可正可负。</param>
    /// <returns>新的曲子；一个音都没动时返回 <paramref name="song"/> 本身。</returns>
    Song MoveNotes(Song song, IReadOnlyList<NoteRef> notes, long deltaTicks, int deltaPitch);

    /// <summary>
    /// 把一个音的起点和时值设成给定的值。<b>绝对赋值</b> —— 界面自己算好目标位置再传进来。
    ///
    /// 拖音符左边缘（起点和时值一起变、尾巴钉住）和拖右边缘（只变时值）都是它，
    /// 加 Shift 的方向键微调也是它。
    ///
    /// 会夹住不变量：起点不小于 0、时值不小于 1 个 tick、两者相加不溢出。
    /// **改时值可能让这个音越过邻居** —— 那时这条轨的音符数组会重排，
    /// 下标跟着变（<see cref="NoteRef"/> 的注释里说了这件事，界面得重新算选中集）。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="note">要改的音。</param>
    /// <param name="startTick">目标起点 tick。</param>
    /// <param name="lengthTicks">目标时值（tick），至少 1。</param>
    /// <returns>新的曲子；本来就是这段时值时返回 <paramref name="song"/> 本身。</returns>
    Song SetNoteSpan(Song song, NoteRef note, long startTick, long lengthTicks);

    /// <summary>
    /// 删掉一组音。
    ///
    /// 「在卷帘空白处横拖删掉区间内所有音」也走这一条：**哪几个音落在那段区间里由界面算**
    /// （它手上有视口和音符数组，一笔就算完了），命令只管删。这样命令不必懂「区间」，
    /// 也不必懂像素。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="notes">要删的音。空集时返回 <paramref name="song"/> 本身。</param>
    /// <returns>新的曲子；一个音都没删时返回 <paramref name="song"/> 本身。</returns>
    Song DeleteNotes(Song song, IReadOnlyList<NoteRef> notes);

    /// <summary>
    /// 给某条轨改名。名字两端的空白会被去掉。
    ///
    /// 名字只是显示用的标签，不影响播放、导出或任何换算 —— 所以改它不会动到别的东西。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="name">新名字。去掉两端空白后不能是空的，否则抛。</param>
    /// <returns>新的曲子；本来就是这个名字时返回 <paramref name="song"/> 本身。</returns>
    Song RenameTrack(Song song, int trackIndex, string name);

    /// <summary>
    /// 删掉一整条轨。
    ///
    /// <b>这是这套命令里唯一一条会改变轨的条数的。</b>删完 <see cref="Song.Tracks"/> 的下标整体前移，
    /// 于是界面上「选中了哪条轨」和任何存下来的轨下标当场作废 —— 删轨之后必须拿新的曲子重新算。
    /// <see cref="Track.TrackIndex"/> 是「来自文件里第几个轨块」的出处标记，**不重编号**：
    /// 删掉第 0 条之后剩下的轨仍然带着各自的原始编号，那是它们的身份，不是排序。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <returns>新的曲子；只剩一条轨时**照样删**（空曲子是合法的，撤销拿得回来）。</returns>
    Song DeleteTrack(Song song, int trackIndex);
}
