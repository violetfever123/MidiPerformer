namespace MidiPerformer.Core.Model;

/// <summary>
/// 一个音符。**只有 tick，没有秒** —— tick 是唯一的时值表示。
///
/// 这条约束是整份 spec 的地基：改了 BPM 只需要缩放 <see cref="TempoMap"/> 里的速度事件，
/// 音符数组一个字节都不用碰。一旦这里多出一个「秒」字段，两个真相源就会开始互相打架。
///
/// 值类型。编辑命令改一个音就是换一个 <see cref="Note"/> 值，不含引用共享。
/// </summary>
/// <param name="Pitch">MIDI 音高 0..127，60 = C4。</param>
/// <param name="StartTick">起始 tick（相对曲子开头）。</param>
/// <param name="LengthTicks">时值（tick）。</param>
/// <param name="Velocity">力度 0..127。</param>
public readonly record struct Note(int Pitch, long StartTick, long LengthTicks, int Velocity)
{
    /// <summary>结束 tick（不含）。</summary>
    public long EndTick => StartTick + LengthTicks;
}
