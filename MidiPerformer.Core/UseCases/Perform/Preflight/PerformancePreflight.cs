using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Analysis;

namespace MidiPerformer.Core.UseCases.Perform.Preflight;

/// <summary>
/// 预检：这场演奏放不放行。**纯函数**，一个外部系统都不碰。
///
/// <b>为什么它不自己去问系统</b>：端口总数在 spec 里定死在四个，不为预检新开一个。
/// 于是「是不是管理员」「输入法是不是中文」这两个**事实**由界面经网关取到之后传进来，
/// 这里只做判定与排序 —— 也因此这一整块能在 NUnit 里用两个 bool 跑遍，
/// 不需要提权、不需要装输入法、不需要一个人坐在电脑前手动切输入法。
///
/// <b>检查顺序是定死的：权限 → 输入法 → 有没有能弹的轨。</b> 顺序有意义：
/// 权限不够是**环境**不对（怎么改谱子都没用），输入法中文是**当前状态**不对（切一下就好），
/// 两者都轮不到看谱子。同一时刻坏了两样时，用户该先去解决那个更根本的 ——
/// 报一个「这条轨弹不了」而实际原因是没提权，等于把人支到错的方向上去。
/// </summary>
public static class PerformancePreflight
{
    /// <summary>
    /// 放不放行。<paramref name="elevated"/> 与 <paramref name="imeInChinese"/> 是**事实**，
    /// 由调用方（界面）从网关取；这里不判断它们取得对不对。
    ///
    /// 返回 <see cref="PerformanceStartOutcome.Started"/> 之外的值都表示**一个音都不该发**。
    /// </summary>
    public static PerformanceStartOutcome Check(
        StartPerformanceRequest request,
        bool elevated,
        bool imeInChinese)
    {
        if (!elevated) return PerformanceStartOutcome.NotElevated;
        if (imeInChinese) return PerformanceStartOutcome.ImeActive;

        // 能弹与否判在 TrackRanking.IsPlayable 上（单声部 + 非打击乐），这里不重写一遍 ——
        // 判据有两份，就迟早有一份是过时的。界面那边用它筛下拉框，这里用它守最后一道。
        var tracks = request.Song.Tracks;
        if (request.TrackIndex < 0 || request.TrackIndex >= tracks.Count)
            return PerformanceStartOutcome.NoPlayableTrack;

        return TrackRanking.IsPlayable(tracks[request.TrackIndex], request.Song.TempoMap)
            ? PerformanceStartOutcome.Started
            : PerformanceStartOutcome.NoPlayableTrack;
    }
}
