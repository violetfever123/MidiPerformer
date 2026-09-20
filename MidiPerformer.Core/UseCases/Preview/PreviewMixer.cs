using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.UseCases.Preview;

/// <summary>
/// 把整首曲子摊成一张试听事件表：每个音带上它该用的声道和音色。
///
/// 走的是和演奏同一条换算链（<see cref="RepertoireToSeconds"/> → <see cref="NoteMapper"/>），
/// 差别只在终点：这边变成 <see cref="PreviewNote"/> 交给声卡。移调照样叠加上去。
/// <c>InRange == false</c> 的音照发：灰显说的是「游戏里弹不出来」，试听正是用来听这个的。
///
/// 不拿文件里的声道号当试听声道（好几条轨常塞在同一个声道上，音色会互相顶掉），
/// 而是按轨重新分：一条轨一个声道。
/// </summary>
public static class PreviewMixer
{
    /// <summary>9 号声道在 MIDI 里固定是打击乐，音色号在那一轨没有意义。</summary>
    public const int PercussionChannel = 9;

    /// <summary>试听可用的旋律声道，按分配顺序：0..8 与 10..15，跳过 9，共 15 条。</summary>
    private static readonly int[] MelodicChannels =
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 };

    /// <summary>
    /// 整曲 → 试听事件表。轨按 <see cref="Song.Tracks"/> 的顺序分配声道，轨内保持音符顺序。
    ///
    /// <paramref name="mutedTracks"/> 里的轨一个音都不进这张表，也不占声道名额。
    /// 认轨用模型那对唯一键 <c>(TrackIndex, Channel)</c> 而不是下标 —— 下标会随删轨整体前移。
    /// 旋律轨超过 15 条时会绕回第一个声道，那两条轨的音色互相顶掉。
    ///
    /// 时间不在这里排序：那是实现方建事件表时的事。
    /// </summary>
    /// <param name="mutedTracks">
    /// 不发声的轨，按 <c>(轨块号, 声道)</c> 给（= <c>Track.TrackIndex</c> 与 <c>Track.Channel</c>）。
    /// null 或不给 = 一条都不静音。
    /// </param>
    public static IReadOnlyList<PreviewNote> Mix(
        Song song,
        IReadOnlySet<(int TrackIndex, int Channel)>? mutedTracks = null)
    {
        var result = new List<PreviewNote>();
        int next = 0;

        foreach (var track in song.Tracks)
        {
            // 收起来的轨整个跳过：音不发，声道名额也不占，换算也不做
            if (mutedTracks?.Contains((track.TrackIndex, track.Channel)) == true) continue;

            // 打击乐轨仍然发到 9 号声道（那里是鼓组），也不占旋律声道的名额
            int channel = track.Channel == PercussionChannel
                ? PercussionChannel
                : MelodicChannels[next++ % MelodicChannels.Length];

            var raws = RepertoireToSeconds.Convert(track.Notes, song.TempoMap);
            // 基准八度给 null（自动）：试听不按键，基准八度只影响 InRange 那个标记
            foreach (var note in NoteMapper.Map(raws, track.Transpose, manualBaseOctave: null).Notes)
                result.Add(new PreviewNote(note, channel, track.Program));
        }

        return result;
    }
}
