namespace MidiPerformer.Core.Model;

/// <summary>
/// 一个音符。**只有 tick，没有秒** —— tick 是唯一的时值表示。
///
/// 这条约束是整份 spec 的地基：改了 BPM 只需要缩放 <see cref="TempoMap"/> 里的速度事件，
/// 音符数组一个字节都不用碰。一旦这里多出一个「秒」字段，两个真相源就会开始互相打架。
///
/// 值类型。编辑命令改一个音就是换一个 <see cref="Note"/> 值，不含引用共享。
///
/// <b><see cref="Id"/> 是身份，不是内容。</b>它由读取时发下来（见 <see cref="NoteIdentity"/>），
/// 之后不管是挪动、改时值还是改音高都跟着这个音走 —— 编辑命令写回的是
/// <c>note with { ... }</c>，身份自然原样带过。也正因为它不是内容，
/// <see cref="Equals(Note)"/> 刻意不比它，理由写在那条方法上。
/// </summary>
/// <param name="Pitch">MIDI 音高 0..127，60 = C4。</param>
/// <param name="StartTick">起始 tick（相对曲子开头）。</param>
/// <param name="LengthTicks">时值（tick）。</param>
/// <param name="Velocity">力度 0..127。</param>
/// <param name="Id">稳定身份。默认 0 号 = 没有身份（见 <see cref="NoteId.None"/>）。</param>
public readonly record struct Note(
    int Pitch,
    long StartTick,
    long LengthTicks,
    int Velocity,
    NoteId Id = default)
{
    /// <summary>结束 tick（不含）。</summary>
    public long EndTick => StartTick + LengthTicks;

    /// <summary>
    /// 值相等：比**内容**，不比身份。
    ///
    /// 为什么把 <see cref="Id"/> 排除在外：它是**一把把手，不是这个音本身**。
    /// 让身份参与相等，S1 缝上那几条测试会当场红：MIDI 里没有地方放身份，
    /// 「导入 → 导出 → 再导入」回来的是同一份谱面，但身份是重新发的
    /// （导出时丢掉，导入时按位置重发，见 <c>MidiWriter</c> 与 <c>MidiReader</c>）；
    /// 手工拼出来的谱面和从文件读进来的谱面，同一段音乐也可能拿着两套身份。
    /// 身份参与相等的话，「这两份谱面是不是同一份」就变成了「它们的身份发得一样不一样」——
    /// 那不是这条缝要问的问题，而它一旦红了，会红得让人以为读取逻辑坏了。
    ///
    /// 代价要写清楚：**两个内容完全一样的音，值相等而身份不同**（同一个 tick 上两个同音高同力度的音，
    /// 真实语料里常见）。分开它们靠的是身份，不是这个 <c>Equals</c> —— 这正是这条工单要的东西：
    /// 从「拿内容去认音」换成「拿身份去认音」。
    /// </summary>
    public bool Equals(Note other) =>
        Pitch == other.Pitch
        && StartTick == other.StartTick
        && LengthTicks == other.LengthTicks
        && Velocity == other.Velocity;

    /// <summary>
    /// 与 <see cref="Equals(Note)"/> 对齐：身份不参与哈希。
    ///
    /// 必须一起改：相等的两个音哈希不相等的话，它们放进 <c>HashSet</c> / <c>Dictionary</c>
    /// 会被当成两个键，或者查不出来 —— 而 <c>Track</c> 的相等是拿音符逐个比出来的，
    /// 底下用的就是这个。
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Pitch, StartTick, LengthTicks, Velocity);
}
