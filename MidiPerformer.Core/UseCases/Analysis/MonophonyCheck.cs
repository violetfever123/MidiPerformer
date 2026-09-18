using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Analysis;

/// <summary>
/// 单声部判定：这条轨游戏里的口琴弹不弹得了。
///
/// <b>判据是「起音是否同时」，不是「时值是否重叠」。</b> 同一瞬间（±
/// <see cref="SimultaneityToleranceSeconds"/>）出现两个及以上的起音 → 多声部；
/// 前音还在响、后音才起（连奏）→ <b>不算</b>多声部，演奏时由槽位排开处理
/// （见 <c>EventBuilder.Build</c> 的说明）。
///
/// 这个区分是这一整块存在的理由：用「时值重叠」当判据，连奏的旋律会被误判成多声部，
/// 而那恰恰是用户最想弹的那条轨 —— 判错的代价是把主旋律藏起来，用户还不知道为什么。
///
/// 阈值取 30ms 而不是原版 <c>MergeVoicesByPriority</c> 里的 <c>eps = 0.025</c>：
/// 那个量级是它验证过的，我们留一点余量。**注意两者判法并不等价** —— 原版是把
/// 「与组内首个音相隔 ≤ eps」的音并成一组（链式，25ms 一档可以一路漂下去），
/// 这里判的是「存在一对相邻起音相隔 ≤ 容差」。同一个用途（判别而不是合并），
/// 这里要的是「有没有两个音几乎同时」，链式分组会把它放大成「有没有一串音挨得近」。
/// </summary>
public static class MonophonyCheck
{
    /// <summary>
    /// 两个起音算作「同一瞬间」的容差（秒，音乐时间）。
    ///
    /// 比的是**秒**而不是 tick：tick 的长度随分辨率和 BPM 变，同一份谱换个 PPQ
    /// 就是另一个数 —— 而「听起来同不同时」是耳朵的事，耳朵听的是秒。
    /// </summary>
    public const double SimultaneityToleranceSeconds = 0.030;

    /// <summary>一对撞在一起的起音。<paramref name="GapSeconds"/> 为它们的间隔（≥ 0）。</summary>
    public readonly record struct OnsetCollision(int EarlierNoteIndex, int LaterNoteIndex, double GapSeconds);

    /// <summary>
    /// 找出第一处撞在一起的起音，没有就是 <c>null</c>。
    ///
    /// 音符按起始时间升序（<see cref="Track"/> 的约定），所以只消看**相邻**两个：
    /// 排序之后，若相邻两个的间隔都大于容差，那任意一对的间隔只会更大。
    /// 于是判定是一趟线性扫描，不用拿每个音去跟后面所有音比。
    /// </summary>
    public static OnsetCollision? FindCollision(Track track, TempoMap tempoMap)
    {
        var notes = track.Notes;
        for (int i = 1; i < notes.Count; i++)
        {
            double gap = tempoMap.SecondsAt(notes[i].StartTick) - tempoMap.SecondsAt(notes[i - 1].StartTick);

            // 「≤ 容差」算撞上：刚好 30.000ms 的两个音，游戏那一帧多半也只读得到一个，
            // 当成能弹就是把漏音放出去。边界往严的一侧倒。
            if (gap <= SimultaneityToleranceSeconds) return new OnsetCollision(i - 1, i, gap);
        }
        return null;
    }

    /// <summary>这条轨是不是单声部（起音两两不同时）。空轨和单音轨当然是。</summary>
    public static bool IsMonophonic(Track track, TempoMap tempoMap)
        => FindCollision(track, tempoMap) is null;
}
