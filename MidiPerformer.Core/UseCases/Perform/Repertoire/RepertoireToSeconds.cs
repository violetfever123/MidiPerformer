using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>
/// **演奏路径上唯一换单位的地方。**
///
/// <code>
/// Song（tick）── RepertoireToSeconds ──► RawNote[]（秒）── NoteMapper ──► MappedNote[] ── EventBuilder ──► 事件表
/// </code>
///
/// 用 <see cref="TempoMap"/> 把选中轨的音符 tick → 秒，**只算这一次**。之后一路到事件表，
/// 走的都是移植来的那套数学，拿到的全是秒。编辑器那边保持 tick，谁也不将就谁。
///
/// 这条界线是整份 spec 里两个世界的接缝：内层（模型）只认 tick，外层（移植来的演奏逻辑）
/// 只认秒。接缝只有这一处，换算错了只会在这一处错。
/// </summary>
public static class RepertoireToSeconds
{
    /// <summary>
    /// 极短音的下限（秒）。和原版 <c>MidiLoader</c> 里的 <c>if (e - s &lt; 0.02) e = s + 0.02;</c> 是同一件事。
    ///
    /// 为什么这条垫底放在这儿而不是模型里：它是**可演奏性**的修正，不是谱面事实。
    /// 谱面说这个音 1ms，模型就老老实实存 1ms；到了发按键这一步，1ms 的按键游戏一帧都采样不到，
    /// 才会被垫到 20ms。放进模型就等于让编辑器显示一个假的时值。
    /// </summary>
    public const double MinNoteSeconds = 0.02;

    /// <summary>选中轨的音符 tick → 秒。空轨返回空表。</summary>
    public static List<RawNote> Convert(IReadOnlyList<Note> notes, TempoMap tempoMap)
    {
        var result = new List<RawNote>(notes.Count);
        foreach (var n in notes)
        {
            double start = tempoMap.SecondsAt(n.StartTick);
            double end = tempoMap.SecondsAt(n.EndTick);
            if (end - start < MinNoteSeconds) end = start + MinNoteSeconds;

            result.Add(new RawNote
            {
                Pitch = n.Pitch,
                Start = start,
                End = end,
                Velocity = n.Velocity
            });
        }
        return result;
    }
}
