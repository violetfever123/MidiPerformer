using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Preview;

/// <summary>
/// 「这首曲子到哪儿算完」—— 按**还听得见的那几条轨**算，而不是按整份谱面。
///
/// 一件乐器上最常发生的事是「这一遍我只想听主旋律」：把伴奏那几条收起来（折叠），
/// 出声那头早就不发它们的音了（见 <see cref="PreviewMixer.Mix"/>），可**长度**还在按
/// 整份谱面算 —— 于是伴奏比主旋律多出来的那几小节，会变成一段什么都不出声的空转：
/// 播放头照走、进度条照爬、卷帘照画，听感上就是「曲子放完了还在放」。
/// 这一层就是把这个数改对：**收起来的轨不算长度**。
///
/// <b>为什么不把这个数写回 <see cref="Song"/> / <see cref="Track"/>。</b>
/// 它是**派生视图**，不是谱面的一部分：同一份曲子折叠不同的轨就有不同的长度，
/// 而谱面只有一份。而且 <c>.mproj</c> 的写入规则是「构造函数参数即文件内容」
/// （见 <c>SongProjectFile</c>）—— 往 Song 上加一个真字段，它会当场跟着存进文件，
/// 从此「长度」就有了第二个真相源，撤销、重开、换曲子各说各话。
///
/// 静音名单用的是模型自己那对唯一键 <c>(TrackIndex, Channel)</c>，**不是下标** ——
/// 和 <see cref="PreviewMixer.Mix"/> 同一条理由：下标会随删轨整体前移，按下标算会算到别人头上。
/// </summary>
public static class AudibleLength
{
    /// <summary>
    /// 还听得见的轨里最后一个音的结束 tick。
    ///
    /// **一条未静音的轨都没有、或者它们全都空着的时候，退回所有轨的最大末尾**
    /// （= <see cref="Song.EndTick"/>，也就是从前的算法）。这条兜底不是「顺手写一下」：
    /// 折叠是用户对某一条轨的标记，把最后一条轨也收起来，他的意思是
    /// 「这条我也不想听」，**不是**「这首曲子没了」。让整曲长度当场变成 0 的话，
    /// 卷帘会缩成一小节、播放头被拉回开头、进度条分母变 0 —— 那是把一句「静音」
    /// 翻译成了「曲子是空的」，两回事。
    ///
    /// 反过来，留下的轨**本来就有音**、只是比被收起来的那几条短，那就是货真价实的
    /// 「后面没有声音了」—— 这时候给的短数才是这一层要的那个数。
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

    /// <summary>
    /// 同上的秒数。走的是和 <see cref="Song.TotalSeconds"/> 同一张速度表，
    /// 只是把终点从一个 tick 换成另一个 tick。
    /// </summary>
    public static double Seconds(Song song, IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks)
        => song.TempoMap.SecondsAt(EndTick(song, mutedTracks));
}
