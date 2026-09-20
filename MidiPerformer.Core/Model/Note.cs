namespace MidiPerformer.Core.Model;

/// <summary>
/// 一个音符。只有 tick，没有秒 —— tick 是唯一的时值表示。值类型。
///
/// <see cref="Id"/> 是身份而不是内容：由读取时发下来（见 <see cref="NoteIdentity"/>），
/// 之后挪动、改时值、改音高都跟着这个音走，<see cref="Equals(Note)"/> 不比它。
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
    /// 值相等：比内容，不比身份。身份不参与，所以两个内容一样的音值相等而身份不同，分开它们靠身份。
    /// </summary>
    public bool Equals(Note other) =>
        Pitch == other.Pitch
        && StartTick == other.StartTick
        && LengthTicks == other.LengthTicks
        && Velocity == other.Velocity;

    /// <summary>与 <see cref="Equals(Note)"/> 对齐：身份不参与哈希。</summary>
    public override int GetHashCode() => HashCode.Combine(Pitch, StartTick, LengthTicks, Velocity);
}
