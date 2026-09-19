using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 编辑命令的入口：改整曲速度、改某条轨的移调与音色、挪音符、改时值、删音符、剪掉一段、
/// 改轨名、删轨。
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
    /// 把第 <paramref name="trackIndex"/> 条轨的音色设成 GM 的 <paramref name="program"/> 号。
    ///
    /// <b>只影响试听。</b>发给游戏时永远是口琴那套键位 —— 音色是「我想听成什么样」，
    /// 不是「弹出来是什么」。所以这条命令和 <see cref="SetTranspose"/> 一样，
    /// 只换掉那条轨上的一格，音符一个字节都不动。
    ///
    /// 音色在 MIDI 里是**声道事件**，这正是模型按 (轨块, 声道) 切轨的理由之一
    /// （见 <see cref="Track"/>）：一条轨一个音色，才落得到实处。
    ///
    /// <b>9 号声道（打击乐）不特殊对待。</b>MIDI 规定那一整个声道就是鼓组，
    /// 音色号在它上面本来没有意义 —— 界面因此不给那一轨画下拉框，
    /// 但命令不替界面把这一格挡掉：「这一格是什么」和「要不要让人改这一格」是两件事，
    /// 挡在命令里的话，一个导入时带着音色的鼓轨就再也回不到原来的值了。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="program">GM 音色号 0..127。越界抛（换音色是从一张表里挑一个，不是填一个数）。</param>
    /// <returns>新的曲子；本来就是这一号音色时返回 <paramref name="song"/> 本身。</returns>
    Song SetProgram(Song song, int trackIndex, int program);

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
    /// 这一句现在仍然成立：模型已经给了音符稳定身份（<see cref="Note.Id"/>），但
    /// <see cref="NoteRef"/> 还按**下标**寻址，所以下标跟着挪这件事没变 —— 换成按身份寻址
    /// 和界面不再自己认音是同一件事，得一起来（理由写在 <see cref="NoteRef"/> 上）。
    /// 这件事本身由 <see cref="Track.WithNotes"/> 负责（改音符只此一扇门，不变量在它那儿收口），
    /// 命令这一层不再各自排一遍。
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
    /// 下标跟着变（<see cref="NoteRef"/> 的注释里说了这件事，界面得重新算选中集；
    /// 这一句仍然成立的理由同 <see cref="MoveNotes"/>，那里说了为什么现在还删不掉）。
    /// 被改的这个音**身份不变**：它还是原来那个音，只是变长变短了。
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
    /// 把第 <paramref name="trackIndex"/> 条轨上 <c>[startTick, endTick)</c> 这一段
    /// <b>连时间一起</b>抽走：区间里的音删掉，区间之后（含跨过终点那一截）的音整体前移，
    /// 让剩下的接上来。
    ///
    /// <b>和 <see cref="DeleteNotes"/> 是两件事。</b>那条只把音拿走，谱面长度一个 tick 都不变 ——
    /// 删掉第 5 小节里的那几个音，第 6 小节还在第 6 小节，留下一段空的。这条是<b>把第 5 小节整个抽走</b>：
    /// 后面的音提前一小节落下来。用户说的「不是清除音符，是自动拼接」就是这一条。
    ///
    /// <b>只动这一条轨。</b>别的轨连引用都不变，于是从这一刀往后这条轨和别的轨<b>永久错位</b> ——
    /// 那是这个功能的定义，不是副作用：它要的就是「把这声部里多余的那一段剪掉，剩下的接上」。
    /// 两个后果得说在前面：<see cref="Song.EndTick"/> 是<b>所有轨</b>的最大值，
    /// 所以剪一条轨不会让整曲变短（导航条和总时长都不缩）；
    /// 而 <see cref="TempoMap"/> 是整曲共用的、不跟着挪，变速曲子里被前移的那段
    /// 会按<b>它新位置上的速度</b>演奏。
    ///
    /// <b>区间由界面算，命令只认 tick。</b>跟 <see cref="DeleteNotes"/> 同一条规矩：
    /// 「从第 5 小节到第 8 小节」那种对齐由界面拿小节宽度换算成 tick 再传进来。
    /// 命令层连「小节」这个概念都没有 —— 小节线是显示层画的东西，不参与时序换算。
    ///
    /// <b>剪，不是分裂：跑完这条命令，音符数只会变少或者不变。</b>
    /// 跨过切口的音就在切口处剪断，绝不一个变两个 —— 一个音变两个的话，
    /// 左截的尾巴和右截的头会紧紧贴着，看着像一件没剪干净的事。
    /// 逐个音是这么分的（<c>s</c> = 起点，<c>e</c> = 终点，<c>e</c> 不含）：
    /// <list type="bullet">
    /// <item>整个在左切口之前（<c>e &lt;= startTick</c>）—— 一个字节不动。</item>
    /// <item>整个在右切口之后（<c>s &gt;= endTick</c>）—— 起点减去这段长度，整体前移。</item>
    /// <item>跨过左切口（<c>s &lt; startTick &lt; e</c>）—— 在左切口剪断，留下左边那截。</item>
    /// <item>从区间里伸出右切口（<c>s &gt;= startTick</c> 且 <c>e &gt; endTick</c>）——
    /// 剪下右切口之外那截，<b>挪到左切口接上</b>（起点变成 <c>startTick</c>）。</item>
    /// <item>整个落在区间里 —— 删掉。</item>
    /// </list>
    /// 「跨过左切口」那一条同时管住了<b>整个区间都被同一个音盖住</b>的情形：
    /// 那种音也只在左切口剪断，右边那截<b>丢掉</b>、不挪回来。它挪回来的话会和左截紧贴成两个音，
    /// 正好破了上面那条「绝不分裂」。代价是一个长音会被剪短，换来的是这条命令好推理。
    ///
    /// <b>剪出来的那两截是「新音」，发新身份</b>（<see cref="Note.Id"/>）——
    /// 被剪的那个音已经不在谱面上了，留下的是它的碎片，界面手里那个身份不该跟到碎片上去；
    /// 而<b>整体前移</b>的音身份不变（它只是换了个位置）。判据写在
    /// <c>SongEditor.CutRange</c> 的注释上。
    ///
    /// 剪完音符数组仍然是<b>起点升序</b>的，但这条命令自己不管排序：音符从
    /// <see cref="Track.WithNotes"/> 写回去，那条不变量是它保证的（见那边的说明）。
    /// 从前这里跟着一句「新的起点是旧起点的单调不减函数，所以不必重排」的证明 ——
    /// 这类「先信一个证明再信这段代码」的推理，现在整条链上只剩那一扇门一处。
    /// </summary>
    /// <param name="song">改之前的曲子。</param>
    /// <param name="trackIndex"><see cref="Song.Tracks"/> 里的下标。越界抛。</param>
    /// <param name="startTick">要抽掉的那一段的起点，**含**。负数夹到 0。</param>
    /// <param name="endTick">要抽掉的那一段的终点，**不含**。小于 <paramref name="startTick"/> 时抛 ——
    /// 界面在传进来之前就该把「起点大于终点」换过来，换过来还反着就是调用方写错了。</param>
    /// <returns>新的曲子；这段区间里没有音、也没有音要前移时（这段本来就是空的）
    /// 返回 <paramref name="song"/> 本身。</returns>
    Song CutRange(Song song, int trackIndex, long startTick, long endTick);

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
