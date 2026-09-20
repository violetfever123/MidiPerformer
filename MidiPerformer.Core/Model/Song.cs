namespace MidiPerformer.Core.Model;

/// <summary>
/// 一首曲子：一摞轨 + 一张速度表。不可变 —— 每条编辑命令接收一个 <see cref="Song"/>，返回一个新的。
/// 不定义值相等：引用相等是「改没改」的判据。
/// </summary>
public sealed class Song
{
    public Song(IReadOnlyList<Track> tracks, TempoMap tempoMap)
    {
        Tracks = tracks;
        TempoMap = tempoMap;
    }

    /// <summary>所有轨，按导入顺序（也就是卷帘自上而下的显示顺序）。</summary>
    public IReadOnlyList<Track> Tracks { get; }

    public TempoMap TempoMap { get; }

    /// <summary>整曲最后一个音的结束 tick（空曲为 0）。看门狗的超时时刻从它算起。</summary>
    public long EndTick
    {
        get
        {
            long end = 0;
            foreach (var t in Tracks)
            {
                long e = t.EndTick;
                if (e > end) end = e;
            }
            return end;
        }
    }

    /// <summary>整曲时长（秒）。</summary>
    public double TotalSeconds => TempoMap.SecondsAt(EndTick);

    /// <summary>
    /// 整曲能不能换算成秒 —— 不能时 <paramref name="reason"/> 是中文原因，可直接报给用户。
    /// <paramref name="seconds"/> 在返回 <c>true</c> 时等于 <see cref="TotalSeconds"/>。
    ///
    /// 不用 <see cref="TotalSeconds"/> 当判据：它走 <see cref="EndTick"/>，而音符的
    /// <see cref="Note.EndTick"/> 是起点加时值，两个大数相加会溢出成负数被 <c>Math.Max</c> 忽略掉。
    /// 这里自己走一遍音符，起点和终点都取。
    /// </summary>
    public bool TryMeasure(out double seconds, out string? reason)
    {
        long worst = 0;
        foreach (var track in Tracks)
            foreach (var note in track.Notes)
                worst = Math.Max(worst, Math.Max(note.StartTick, note.EndTick));

        try
        {
            seconds = TempoMap.SecondsAt(worst);
            reason = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            seconds = 0;
            reason = ex.Message;
            return false;
        }
    }
}
