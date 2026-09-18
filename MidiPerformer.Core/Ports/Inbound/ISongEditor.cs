using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 编辑命令的入口：改整曲速度、改某条轨的移调。
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
}
