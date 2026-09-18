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
}
