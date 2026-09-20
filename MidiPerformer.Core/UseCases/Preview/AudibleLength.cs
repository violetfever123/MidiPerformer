using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Preview;

/// <summary>
/// 「这首曲子到哪儿算完」—— 按还听得见的那几条轨算，而不是按整份谱面：
/// 收起来的轨出声那头早就不发它们的音了（见 <see cref="PreviewMixer.Mix"/>），
/// 长度再按整份谱面算，多出来的那几小节就是一段什么都不出声的空转。
///
/// 不写回 <see cref="Song"/> / <see cref="Track"/>：它是派生视图，同一份曲子折叠不同的轨就有不同长度，
/// 而 <c>.mproj</c> 的写入规则是「构造函数参数即文件内容」，加个真字段就会存进文件、多一个真相源。
///
/// 静音名单用 <c>(TrackIndex, Channel)</c> 而不是下标 —— 下标会随删轨整体前移。
/// </summary>
public static class AudibleLength
{
    /// <summary>
    /// 还听得见的轨里最后一个音的结束 tick。
    ///
    /// 一条未静音的轨都没有、或者它们全都空着时，退回所有轨的最大末尾（= <see cref="Song.EndTick"/>）——
    /// 折叠是「这条我不想听」而不是「这首曲子没了」，让长度变成 0 会把卷帘缩成一小节、进度条分母变 0。
    /// 留下的轨本来就有音、只是短，那给的短数才是这一层要的。
    /// </summary>
    /// <param name="mutedTracks">
    /// 不发声的轨，按 <c>(轨块号, 声道)</c> 给（= <c>Track.TrackIndex</c> 与 <c>Track.Channel</c>）。
    /// null 或不给 = 一条都不静音，出来的就是 <see cref="Song.EndTick"/>。
    /// </param>
    public static long EndTick(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks)
    {
        long end = 0;
        foreach (var track in song.Tracks)
        {
            if (mutedTracks?.Contains((track.TrackIndex, track.Channel)) == true) continue;

            long e = track.EndTick;
            if (e > end) end = e;
        }

        return end > 0 ? end : song.EndTick;
    }

    /// <summary>同上的秒数：走和 <see cref="Song.TotalSeconds"/> 同一张速度表，只是终点换了。</summary>
    public static double Seconds(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks)
        => song.TempoMap.SecondsAt(EndTick(song, mutedTracks));
}
