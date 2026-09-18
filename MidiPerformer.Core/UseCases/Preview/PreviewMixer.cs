using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.UseCases.Preview;

/// <summary>
/// 把整首曲子摊成一张**试听事件表**：每个音带上它该用的声道和音色。
///
/// 走的是和演奏同一条换算链（<see cref="RepertoireToSeconds"/> → <see cref="NoteMapper"/>），
/// 唯一的差别是终点：演奏那边继续走 <c>EventBuilder</c> 变成按键，这边变成
/// <see cref="PreviewNote"/> 交给声卡。**移调照样叠加上去** —— 卷帘上看到的音高、
/// 耳朵听到的音高、这份表里的音高必须是同一个。
///
/// <c>InRange == false</c> 的音**照发**：MIDI 出声不挑音域，灰显说的是「游戏里弹不出来」，
/// 不是「不该出声」。试听正是用来听这个的。
///
/// <b>为什么不能直接拿文件里的声道号当试听声道。</b>音色是声道事件，而格式 0/1 的 MIDI
/// 常把好几条轨塞在同一个声道上（每条轨各是一个轨块，声声道号却都是 0）—— 照搬的话，
/// 后一条轨的音色会把前一条轨的顶掉，两条轨一起响的时候谁在响都分不清。
/// 所以这里按**轨**重新分声道：一条轨一个声道，音色才落得到实处。
/// </summary>
public static class PreviewMixer
{
    /// <summary>9 号声道在 MIDI 里固定是打击乐，音色号在那一轨没有意义。</summary>
    public const int PercussionChannel = 9;

    /// <summary>
    /// 试听可用的旋律声道，按分配顺序：0..8 与 10..15，**跳过 9**。
    /// 15 条 —— 再多就得有两条轨共用一个声道（见 <see cref="Mix"/>）。
    /// </summary>
    private static readonly int[] MelodicChannels =
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 };

    /// <summary>
    /// 整曲 → 试听事件表。轨按 <see cref="Song.Tracks"/> 的顺序分配声道，轨内保持音符顺序。
    ///
    /// <b>旋律轨超过 15 条时会绕回第一个声道</b>，于是那两条轨共用一个声道、音色互相顶掉
    /// （先响的那条会被后来那条的换音色顺手改掉）。这是个**说得出来的代价，不是没想到**：
    /// 另一边是「第 16 条轨干脆不发声」，那更糟 —— 试听正是用来听有没有声音的。
    /// 真要每轨一个音色到 16 条以上，得换一个不只 16 个声道的出声方式，
    /// 那是这件乐器之外的事（GS 软波表就 16 个）。
    ///
    /// 时间**不**在这里排序：那是实现方建事件表时的事（它本来就要排，
    /// 还要把同一时刻的抬键排在按键前面），这一层排序只会是白排一遍。
    /// </summary>
    public static IReadOnlyList<PreviewNote> Mix(Song song)
    {
        var result = new List<PreviewNote>();
        int next = 0;

        foreach (var track in song.Tracks)
        {
            // 打击乐轨仍然发到 9 号声道 —— 那里是鼓组，正是它该在的地方；
            // 它不占旋律声道的名额（鼓组不受「一条轨一个音色」这条约束，它只有一套）
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
