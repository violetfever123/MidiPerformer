using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.UseCases.Analysis;

/// <summary>一条能弹的轨，连同它凭什么排在这个位置。</summary>
/// <param name="SongTrackIndex">它在 <see cref="Song.Tracks"/> 里的下标 —— 界面拿这个下标回指原曲。</param>
/// <param name="Track">轨本身。</param>
/// <param name="Score">排序用的分数，越大越像主旋律。**只用于排序与诊断，不是给用户看的文案。**</param>
public sealed record RankedTrack(int SongTrackIndex, Track Track, double Score);

/// <summary>
/// 挑出能弹的轨，并按「最像主旋律」排序，第一条默认选中。
///
/// <b>能弹 = 有音 + 单声部 + 非打击乐</b>（就是 <see cref="IsPlayable"/>，判据只有那一份）。
/// 单声部见 <see cref="MonophonyCheck"/>（起音是否同时）。
/// 打击乐那一半直接照搬原版：<c>TrackRowVM.IsPlayable => !IsPercussion</c> ——
/// 口琴是按音高吹的，鼓点发过去只是一串没有意义的音，留着它只会把真正想弹的轨挤下去。
///
/// <b>排序依据移植自原版 <c>MainWindow.ScoreCandidate</c></b>（那份是踩过坑调出来的：
/// 光按音数排，会把一条从头铺到尾的伴奏排在旋律前面）。四条线索，权重照抄：
/// <list type="number">
/// <item>轨名像旋律的加分、像伴奏的减分 —— 唯一一条「人写进去的意图」线索，权重最高。</item>
/// <item>音域贴合口琴的程度（能弹的音占比）。</item>
/// <item>音符数：太碎（&lt; 8 个音）的减分，其余轻微加分。</item>
/// <item>覆盖时长：只盖住开头几秒的轨（前奏、过门、演示音）压不过整首主旋律，
///   用「最后一个音的结束时刻 / 全曲时长」的平方，覆盖越全加得越多。</item>
/// </list>
///
/// 分数只用来排序，**不参与任何判定**：一条轨能不能弹是布尔的事，不看分数高低。
/// </summary>
public static class TrackRanking
{
    /// <summary>GM 规定第 10 声道（下标 9，0 起）是打击乐。原版的 <c>IsPercussion</c> 也是这么判的。</summary>
    public const int PercussionChannel = 9;

    /// <summary>
    /// 能弹：有音 + 不是打击乐 + 单声部 —— 游戏口琴弹得了。
    ///
    /// <b>「有音」这一条也算在里面，不是废话。</b> 漏了它，同一个问题就会有两个答案：
    /// 预检拿这一条判「放不放行」，<see cref="Of"/> 拿它筛下拉框 —— 而一条零音符的空轨
    /// （MIDI 里很常见：只有轨头与元事件）单声部恒真，于是预检放行、起跑，最后建出一张空事件表，
    /// 用户看到的是「按了开始什么都没发生，也没有任何解释」。预检存在的全部理由就是给那句解释。
    /// 判据只能有一份，就放在这里。
    /// </summary>
    public static bool IsPlayable(Track track, TempoMap tempoMap)
        => track.Notes.Count > 0
        && track.Channel != PercussionChannel
        && MonophonyCheck.IsMonophonic(track, tempoMap);

    /// <summary>
    /// 一首曲子里所有能弹的轨，最像主旋律的在最前。
    ///
    /// 顺序是**确定**的：分数相同时按原曲里的下标排。默认选中的那条不能因为
    /// 一次 <c>OrderBy</c> 的不稳定性而变来变去 —— 用户按下开始之前，看到的必须是同一条。
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

    /// <summary>轨名里出现这些词，多半就是旋律 —— 全部按子串匹配，不分大小写。</summary>
    private static readonly string[] MelodyHints =
    {
        "旋律", "主旋律", "主唱", "人声", "女声", "男声", "独奏", "主音",
        "lead", "melod", "vocal", "vox", "solo", "sing"
    };

    /// <summary>轨名里出现这些词，多半是伴奏。减分不减到「不能弹」—— 能弹与否是另一个判据。</summary>
    private static readonly string[] AccompanimentHints =
    {
        "伴奏", "和声", "和弦", "低音", "吉他", "钢琴伴", "节奏",
        "bass", "chord", "back", "guitar", "pad", "rhythm", "fx"
    };

    /// <summary>
    /// 打分。权重的来历见类注释，改这几个数之前先去看原版那份是被什么坑调出来的。
    /// </summary>
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

        // 音域贴合度：走的是演奏那条路（tick → 秒 → 键位），拿到的就是发按键时那个判定。
        // 自己再写一遍「这个音够不够得着」等于把 NoteMapper 的规则抄成第二份。
        var mapped = NoteMapper.Map(RepertoireToSeconds.Convert(notes, song.TempoMap), track.Transpose, null);
        s += 30.0 * mapped.InRangeCount / notes.Count;

        if (notes.Count < 8) s -= 20;                      // 太碎不像是能吹的歌
        s += Math.Min(notes.Count / 50.0, 8.0);            // 稍偏好完整曲目轨

        // 覆盖时长：用「最后一个音的结束时刻」而不是跨度，这样后半段才进旋律的轨也能得高分。
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
