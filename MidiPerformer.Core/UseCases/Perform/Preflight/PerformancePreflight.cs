using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Analysis;

namespace MidiPerformer.Core.UseCases.Perform.Preflight;

/// <summary>
/// 预检：这场演奏放不放行。纯函数，一个外部系统都不碰。
///
/// 「是不是管理员」「输入法是不是中文」这两个事实由界面经网关取到之后传进来，这里只做判定。
/// 检查顺序定死：权限 → 输入法 → 有没有能弹的轨 —— 前两项是环境与当前状态的问题，都轮不到看谱子。
/// </summary>
public static class PerformancePreflight
{
    /// <summary>
    /// 放不放行。<paramref name="elevated"/> 与 <paramref name="imeInChinese"/> 是事实，由调用方从网关取。
    /// 返回 <see cref="PerformanceStartOutcome.Started"/> 之外的值都表示一个音都不该发。
    /// </summary>
    public static PerformanceStartOutcome Check(
        StartPerformanceRequest request,
        bool elevated,
        bool imeInChinese)
    {
        if (!elevated) return PerformanceStartOutcome.NotElevated;
        if (imeInChinese) return PerformanceStartOutcome.ImeActive;

        // 能弹与否判在 TrackRanking.IsPlayable 上（单声部 + 非打击乐），判据不写两份。
        var tracks = request.Song.Tracks;
        if (request.TrackIndex < 0 || request.TrackIndex >= tracks.Count)
            return PerformanceStartOutcome.NoPlayableTrack;

        return TrackRanking.IsPlayable(tracks[request.TrackIndex], request.Song.TempoMap)
            ? PerformanceStartOutcome.Started
            : PerformanceStartOutcome.NoPlayableTrack;
    }
}
