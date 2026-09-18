namespace MidiPerformer.Core.Model;

/// <summary>
/// 一首曲子：一摞轨 + 一张速度表。
///
/// **不可变。** 每条编辑命令接收一个 <see cref="Song"/>，返回一个新的 <see cref="Song"/>。
/// 撤销栈因此天然就是「一摞旧的 Song 引用」，装饰器只要比引用就够判断改没改
/// —— 见 <c>UseCases/Editing/UndoableSongEditor</c>。
///
/// 刻意**不**定义值相等：引用相等才是「改没改」的判据，一个「看起来像值语义」的 <c>==</c>
/// 会让人误用它去做编辑判断。要比两首曲子是不是同一份谱，用测试里的逐字段比较。
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
    ///
    /// 为什么这是个**装谱面之前**该问的问题：卷帘是 tick 轴，多长的 tick 都画得出来，
    /// 所以一份 tick 大到荒唐的谱面照样能显示；但试听、演奏、时长读数都要 tick → 秒，
    /// 而 <see cref="TempoMap.SecondsAt"/> 对换算后装不进 <c>long</c> 的 tick 会抛
    /// <see cref="InvalidOperationException"/>。也就是说「画得出来」和「放得出来」不是一回事，
    /// 中间隔着的正是这一问。先问再装，调用方就不会装到一半炸掉。
    ///
    /// <paramref name="seconds"/> 在返回 <c>true</c> 时等于 <see cref="TotalSeconds"/>，
    /// 只是顺手给出来，省得调用方再算一遍。
    ///
    /// 判据为什么不直接用 <see cref="TotalSeconds"/>（那最简单）：它走 <see cref="EndTick"/>，
    /// 而 EndTick 是各轨 <c>EndTick</c> 的最大值，<c>Track.EndTick</c> 又是各音
    /// <see cref="Note.EndTick"/> 的最大值 —— 可 <c>Note.EndTick</c> 是
    /// <c>StartTick + LengthTicks</c>，两个大数一加会**溢出成负数**，于是那个音被
    /// <c>Math.Max</c> 当成「比 0 还小」忽略掉，整曲的判据就漏掉了最大的一刻：
    /// 这一问放行，试听那边照样抛。所以这里自己走一遍音符，**起点和终点都取**，
    /// 溢出的那个也躲不掉。
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
