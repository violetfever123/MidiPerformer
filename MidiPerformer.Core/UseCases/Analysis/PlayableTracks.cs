using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Analysis;

/// <summary>一条能弹的轨，连同它在原曲里的出处。</summary>
/// <param name="SongTrackIndex">它在 <see cref="Song.Tracks"/> 里的下标 —— 界面拿这个下标回指原曲。</param>
/// <param name="Track">轨本身。</param>
public sealed record PlayableTrack(int SongTrackIndex, Track Track);

/// <summary>
/// 挑出能弹的轨，按原曲下标升序（与卷帘自上而下的顺序一致），第一条默认选中。
///
/// 能弹 = 有音 + 单声部 + 非打击乐（判据只有 <see cref="IsPlayable"/> 那一份）。
/// 顺序就是曲子里本来的顺序，不打分、不重排 —— 「哪条是主旋律」交给用户自己认。
/// </summary>
public static class PlayableTracks
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
    /// 一首曲子里所有能弹的轨，按它们在 <see cref="Song.Tracks"/> 里的下标升序。
    /// </summary>
    public static IReadOnlyList<PlayableTrack> Of(Song song)
    {
        var playable = new List<PlayableTrack>();
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var track = song.Tracks[i];
            if (!IsPlayable(track, song.TempoMap)) continue;   // 空轨也在这条里面，别在外面再判一次

            playable.Add(new PlayableTrack(i, track));
        }

        return playable;
    }
}
