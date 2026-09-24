using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform;

/// <summary>
/// 「选中轨 → 秒 → 键位 → 事件表」这四步的**唯一一处**。
///
/// 顺序就是 <see cref="RepertoireToSeconds"/> 定下的那条链（换单位只发生一次），
/// 而这张表有**两个**用的人：演奏时派发器照着它发键，界面在**播放之前**照着它量按键速度读数
/// （50 号票）。两边各写一遍的话，屏幕上那个「最密的一秒」迟早和耳朵里真挨的那一秒对不上 ——
/// 所以两边都从这儿拿表。读数的判据是「与事件表对拍」，对拍的前提就是同一个真相源。
///
/// 这儿只建表，不下发、不统计、不缓存：调用方各取所需（派发器要 <see cref="SongWalker"/>，
/// 读数只要事件表本身）。
/// </summary>
public static class EventTable
{
    /// <summary>
    /// 建一张事件表。
    /// </summary>
    /// <param name="song">曲子。</param>
    /// <param name="trackIndex">弹哪条轨，<paramref name="song"/> 的 <c>Tracks</c> 里的下标。</param>
    /// <param name="timing">时序档位 —— 它**是这张表的一个输入**（帧宽 / 提前量 / 重触发间隔都从它来），
    /// 换了档位就是另一张表。</param>
    /// <param name="baseOctave">基准八度，<c>null</c> = 自动（让可演奏区容下最多音符）。</param>
    /// <returns>事件表（按时刻升序）、这张表铺了多久（最后一个事件的时刻，秒），
    /// 以及给看门狗用的游标。时长原样转交 <see cref="EventBuilder.Build"/> 算出来的那个数，
    /// 不在这里另算一遍 —— 按键速度读数的分母就是它。</returns>
    public static (List<EventBuilder.PhysicalEvent> Events, double Seconds, SongWalker Walker) Build(
        Song song, int trackIndex, InputTiming timing, int? baseOctave)
    {
        var track = song.Tracks[trackIndex];

        var seconds = RepertoireToSeconds.Convert(track.Notes, song.TempoMap);

        // 基准八度：手动给的就是它，null 走自动（让可演奏区容下最多音符）。
        var mapped = NoteMapper.Map(seconds, track.Transpose, baseOctave);

        // 超出三个八度的音跳过不发，事件表只收 InRange 的那些（卷帘上它们已经标灰了）。
        var builder = new EventBuilder { Timing = timing };
        var (events, 时长) = builder.Build(
            mapped.Notes.Where(n => n.InRange).ToList(), EventBuilder.ModState.None);

        return (events, 时长, new SongWalker(song));
    }
}
