using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>
/// 演奏路径上唯一换单位的地方。
///
/// <code>
/// Song（tick）── RepertoireToSeconds ──► RawNote[]（秒）── NoteMapper ──► MappedNote[] ── EventBuilder ──► 事件表
/// </code>
///
/// 用 <see cref="TempoMap"/> 把选中轨的音符 tick → 秒，只算这一次；之后一路到事件表拿的都是秒。
/// </summary>
public static class RepertoireToSeconds
{
    /// <summary>
    /// 极短音的下限（秒），与原版 <c>MidiLoader</c> 的 <c>if (e - s &lt; 0.02) e = s + 0.02;</c> 同一件事。
    /// 这是可演奏性的修正而不是谱面事实，所以不放进模型，免得编辑器显示假时值。
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
