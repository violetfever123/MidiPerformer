using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 试听要发出去的一个音：**映射好的音符 + 它该在哪个声道上、用哪个音色响**。
///
/// 为什么不直接发 <see cref="MappedNote"/>：音色（Program）在 MIDI 里是**声道事件**，
/// 而 <see cref="MappedNote"/> 里只有音高和时间 —— 它是演奏路径的词汇，那个文件要和原版
/// 逐行对拍，一个字段都加不得（加了就再也不是原版那一行的子序列了）。
/// 于是试听这一侧另开一个词：三个字段合起来才说清「这个音怎么响」。
///
/// <b>声道和音色都挂在每个音上，不是端口上的一份状态。</b>端口于是不必记住
/// 「现在用的是几号音色、下一个音要不要先换」—— 那是实现方自己按需去比的事，
/// 用例层只管说清每一个音要什么。跳转、重排、重发整批的时候，这句话都还是完整的。
/// </summary>
/// <param name="Note">音高与时间（音乐时间，秒）。</param>
/// <param name="Channel">发到哪个 MIDI 声道 0..15（9 = 打击乐）。</param>
/// <param name="Program">GM 音色号 0..127。只影响试听，发给游戏时永远是口琴那套键位。</param>
public sealed record PreviewNote(MappedNote Note, int Channel, int Program);
