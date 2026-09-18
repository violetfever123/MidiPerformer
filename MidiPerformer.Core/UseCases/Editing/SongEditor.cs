using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Core.UseCases.Editing;

/// <summary>
/// 编辑命令的真身：<b>纯函数、无状态</b>，一行撤销代码都没有。
///
/// 每条命令收散参、返回一个新的 <see cref="Song"/>；「改没改」就是「返回的引用是不是同一个」。
/// 所以改完与改前等价时返回的<b>就是传进来的那一个</b>（<c>ReferenceEquals</c> 为真），
/// 装饰器靠这一条决定要不要记一笔 —— 不需要额外的 <c>Changed</c> 字段。
///
/// 撤销不在这儿，也不在将来任何一条命令里：它由 <c>UndoableSongEditor</c> 横着罩在所有命令外面，
/// 于是加一条新命令，撤销自动就有（见 spec「撤销：装饰器」）。
/// </summary>
public sealed class SongEditor : ISongEditor
{
    /// <summary>速度的合法下限（拍/分）。界面与用例共用这一对边界，免得两边各写一个数、各错一处。</summary>
    public const double MinBpm = 1;

    /// <summary>速度的合法上限（拍/分）。</summary>
    public const double MaxBpm = 1000;

    /// <summary>
    /// 微秒/四分音符 的上限。缩放算出来的数可能超出 <see cref="long"/>，
    /// 而 <c>(long)</c> 转换在那种情况下是未定义值（和 <c>TempoMap</c> 拦 NaN 是同一类坑：
    /// 一个垃圾值会静静混进新速度表，再一路传到秒数上），所以宁可夹到边界。
    /// </summary>
    private const double MaxMicrosPerQuarter = long.MaxValue / 2.0;

    /// <summary>
    /// 改整曲速度。
    ///
    /// 做法是「定住 tick 0 的基准速度，其余变速点整体等比缩放」：
    /// 先把 tick 0 上生效的微秒数拿来做基准（有事件就取它，没有就是 MIDI 默认的 500000），
    /// 算出 <c>factor = 目标微秒 / 基准微秒</c>，再把表里**每一条**速度乘上它。
    ///
    /// 为什么不是把每条速度都设成目标值：**变速曲子的快慢关系要保住**。
    /// 全抹成同一个数等于把一首变速曲改成匀速的 —— 音符还在，曲子没了。
    /// 而为什么先定基准再整体缩放，而不是只改 tick 0 那一条：只改开头的话，后面那些变速段
    /// 相对开头的快慢也会跟着变（开头变慢了，中间那段的「两倍速」就变成「四倍速」了）。
    ///
    /// 音符一个字节都不动，不是「值相等」，是同一份：<see cref="Song.Tracks"/> 原样带过去，
    /// 连每条 <see cref="Track"/> 的引用都复用。
    /// </summary>
    public Song SetBpm(Song song, double beatsPerMinute)
    {
        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute < MinBpm || beatsPerMinute > MaxBpm)
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute), beatsPerMinute,
                $"速度要在 {MinBpm:0} 到 {MaxBpm:0} 拍/分之间（收到 {beatsPerMinute:R}）。");

        long baseMicros = MicrosecondsAtZero(song.TempoMap);
        double factor = 60_000_000.0 / beatsPerMinute / baseMicros;

        var scaled = new List<TempoChange>(song.TempoMap.TempoChanges.Count + 1);
        bool hasZero = false;
        foreach (var change in song.TempoMap.TempoChanges)
        {
            if (change.Tick == 0) hasZero = true;
            scaled.Add(change with { MicrosecondsPerQuarterNote = Scale(change.MicrosecondsPerQuarterNote, factor) });
        }

        // 原来 tick 0 上没有速度事件的话，缩放**动不了开头那一段** ——
        // 基础速度住在 TempoMap 的默认值里，只能补一条事件把它钉下来。
        // 补出来的值正好等于默认的 500000（目标本来就是 120）时，TempoMap 的构造器会把它丢掉，
        // 那是对的：丢掉之后 BeatsPerMinuteAt(0) 仍然是 120，结果一样，别去绕过它。
        if (!hasZero) scaled.Add(new TempoChange(0, RoundMicros(60_000_000.0 / beatsPerMinute)));

        var map = new TempoMap(song.TempoMap.Division, scaled, song.TempoMap.TimeSignatureChanges);

        // 改完跟改前一模一样（比如本来就是 120、速度表是空的）：原样还回去。
        // 装饰器拿引用相等当「这条命令改没改」的判据 —— 比完仍然返回新对象的话，
        // 撤销栈里就会多出一格什么都撤不动的记录，用户按一下撤销看着像没反应。
        if (map.TempoChanges.SequenceEqual(song.TempoMap.TempoChanges)) return song;

        // 轨整摞照旧（同一个列表、同一批 Track 对象）：音符不是「值相等」，是同一个
        return new Song(song.Tracks, map);
    }

    /// <summary>
    /// 改某条轨的移调。<b>绝对赋值</b>，界面自己算步进后的值。
    ///
    /// 只换目标轨那一个 <see cref="Track"/> 值，别的轨连引用都不变（数组是新的，元素是旧的）。
    /// 音符一个字节都不碰 —— 移调是轨的属性，播放和导出时才叠加到音高上。
    /// </summary>
    public Song SetTranspose(Song song, int trackIndex, int semitones)
    {
        if (trackIndex < 0 || trackIndex >= song.Tracks.Count)
            throw new ArgumentOutOfRangeException(
                nameof(trackIndex), trackIndex,
                $"轨下标 {trackIndex} 越界：这首曲子有 {song.Tracks.Count} 条轨。");

        var track = song.Tracks[trackIndex];
        if (track.Transpose == semitones) return song;

        var tracks = song.Tracks.ToArray();
        tracks[trackIndex] = track with { Transpose = semitones };
        return new Song(tracks, song.TempoMap);
    }

    /// <summary>tick 0 上生效的微秒数：有速度事件就取它，没有就是 MIDI 的默认 500000（= 120 拍/分）。</summary>
    private static long MicrosecondsAtZero(TempoMap map)
    {
        foreach (var change in map.TempoChanges)
            if (change.Tick == 0) return change.MicrosecondsPerQuarterNote;

        return TempoMap.DefaultMicrosecondsPerQuarterNote;
    }

    /// <summary>按比例缩放一条速度，四舍五入到整数微秒。</summary>
    private static long Scale(long microsecondsPerQuarterNote, double factor)
        => RoundMicros(microsecondsPerQuarterNote * factor);

    /// <summary>
    /// 微秒数四舍五入到整数，下限 1、上限 <see cref="MaxMicrosPerQuarter"/>。
    ///
    /// 写成 <c>!(x &gt; 1)</c> 而不是 <c>x &lt;= 1</c>：NaN 在两种比较下都是 false，
    /// 一个 NaN 会一路走到 <c>(long)</c> 转换上（那是未定义值），
    /// 而这里的输入来自「文件里的速度 × 用户输入的速度」，两边都不可信。
    /// </summary>
    private static long RoundMicros(double micros)
    {
        if (!(micros > 1)) return 1;
        if (micros > MaxMicrosPerQuarter) return (long)MaxMicrosPerQuarter;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }
}
