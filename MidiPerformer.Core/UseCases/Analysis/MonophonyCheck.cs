using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Analysis;

/// <summary>
/// 单声部判定：这条轨游戏里的口琴弹不弹得了。
///
/// 判据是「起音是否同时」而不是「时值是否重叠」：同一瞬间（±
/// <see cref="SimultaneityToleranceSeconds"/>）出现两个及以上起音才算多声部，
/// 前音还在响、后音才起的连奏不算（演奏时由槽位排开，见 <c>EventBuilder.Build</c>）。
/// 用「时值重叠」当判据会把连奏的旋律误判成多声部，而那正是用户最想弹的轨。
/// </summary>
public static class MonophonyCheck
{
    /// <summary>
    /// 两个起音算作「同一瞬间」的容差（秒，音乐时间）。
    /// 比秒而不是比 tick：tick 的长度随分辨率和 BPM 变，而「听起来同不同时」是耳朵的事。
    /// </summary>
    public const double SimultaneityToleranceSeconds = 0.030;

    /// <summary>一对撞在一起的起音。<paramref name="GapSeconds"/> 为它们的间隔（≥ 0）。</summary>
    public readonly record struct OnsetCollision(int EarlierNoteIndex, int LaterNoteIndex, double GapSeconds);

    /// <summary>
    /// 找出第一处撞在一起的起音，没有就是 <c>null</c>。
    /// 音符按起始时间升序（<see cref="Track"/> 的约定），所以只看相邻两个就够，一趟线性扫描。
    /// </summary>
    public static OnsetCollision? FindCollision(Track track, TempoMap tempoMap)
    {
        var notes = track.Notes;
        for (int i = 1; i < notes.Count; i++)
        {
            double gap = tempoMap.SecondsAt(notes[i].StartTick) - tempoMap.SecondsAt(notes[i - 1].StartTick);

            // 「≤ 容差」算撞上，边界往严的一侧倒：刚好 30ms 的两个音，游戏那一帧多半只读得到一个。
            if (gap <= SimultaneityToleranceSeconds) return new OnsetCollision(i - 1, i, gap);
        }
        return null;
    }

    /// <summary>这条轨是不是单声部（起音两两不同时）。空轨和单音轨当然是。</summary>
    public static bool IsMonophonic(Track track, TempoMap tempoMap)
        => FindCollision(track, tempoMap) is null;
}
