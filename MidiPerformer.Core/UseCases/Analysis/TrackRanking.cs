using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.UseCases.Analysis;

/// <summary>一条能弹的轨，连同它凭什么排在这个位置。</summary>
/// <param name="SongTrackIndex">它在 <see cref="Song.Tracks"/> 里的下标 —— 界面拿这个下标回指原曲。</param>
/// <param name="Track">轨本身。</param>
/// <param name="Score">排序用的分数，越大越像主旋律。只用于排序与诊断，不是给用户看的文案。</param>
public sealed record RankedTrack(int SongTrackIndex, Track Track, double Score);

/// <summary>
/// 挑出能弹的轨，并按「最像主旋律」排序，第一条默认选中。
///
/// 能弹 = 有音 + 单声部 + 非打击乐（判据只有 <see cref="IsPlayable"/> 那一份）。
/// 打分四条线索：轨名像不像旋律、音域贴合口琴的程度、音符数、覆盖时长；
/// 分数只用来排序，不参与任何判定。
/// </summary>
public static class TrackRanking
{
    /// <summary>GM 规定第 10 声道（下标 9，0 起）是打击乐。</summary>
    public const int PercussionChannel = 9;

    /// <summary>
    /// 能弹：有音 + 不是打击乐 + 单声部。
    ///
    /// 「有音」也在里面：零音符的空轨单声部恒真，漏了它预检会放行，
    /// 最后建出一张空事件表，用户看到「按了开始什么都没发生」。
    /// </summary>
    public static bool IsPlayable(Track track, TempoMap tempoMap)
        => track.Notes.Count > 0
        && track.Channel != PercussionChannel
        && MonophonyCheck.IsMonophonic(track, tempoMap);

    /// <summary>
    /// 一首曲子里所有能弹的轨，最像主旋律的在最前。
    /// 分数相同时按原曲里的下标排，顺序是确定的。
    /// </summary>
    public static IReadOnlyList<RankedTrack> Of(Song song)
    {
        var ranked = new List<RankedTrack>();
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var track = song.Tracks[i];
            if (!IsPlayable(track, song.TempoMap)) continue;   // 空轨也在这条里面，别在外面再判一次

            ranked.Add(new RankedTrack(i, track, Score(track, song)));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.SongTrackIndex)
            .ToList();
    }

    /// <summary>轨名里出现这些词，多半就是旋律 —— 按子串匹配，不分大小写。</summary>
    private static readonly string[] MelodyHints =
    {
        "旋律", "主旋律", "主唱", "人声", "女声", "男声", "独奏", "主音",
        "lead", "melod", "vocal", "vox", "solo", "sing"
    };

    /// <summary>轨名里出现这些词，多半是伴奏。只减分，不影响能不能弹。</summary>
    private static readonly string[] AccompanimentHints =
    {
        "伴奏", "和声", "和弦", "低音", "吉他", "钢琴伴", "节奏",
        "bass", "chord", "back", "guitar", "pad", "rhythm", "fx"
    };

    /// <summary>打分。权重移植自原版 <c>MainWindow.ScoreCandidate</c>。</summary>
    private static double Score(Track track, Song song)
    {
        double s = 0;

        foreach (var kw in MelodyHints)
        {
            if (track.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s += 45;
                break;
            }
        }
        foreach (var kw in AccompanimentHints)
        {
            if (track.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s -= 35;
                break;
            }
        }

        var notes = track.Notes;

        // 音域贴合度：走演奏那条路（tick → 秒 → 键位），拿到的就是发按键时那个判定。
        var mapped = NoteMapper.Map(RepertoireToSeconds.Convert(notes, song.TempoMap), track.Transpose, null);
        s += 30.0 * mapped.InRangeCount / notes.Count;

        if (notes.Count < 8) s -= 20;                      // 太碎不像是能吹的歌
        s += Math.Min(notes.Count / 50.0, 8.0);            // 稍偏好完整曲目轨

        // 覆盖时长：用「最后一个音的结束时刻」而不是跨度，后半段才进旋律的轨也能得高分。
        double total = song.TotalSeconds;
        if (total > 1)
        {
            double cover = Math.Clamp(song.TempoMap.SecondsAt(track.EndTick) / total, 0, 1);
            s += 60.0 * cover * cover;
            if (cover < 0.25) s -= 25;                     // 只盖住开头几秒的，基本是前奏或演示音
        }

        return s;
    }
}
