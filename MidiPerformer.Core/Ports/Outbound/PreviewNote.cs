using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 试听要发出去的一个音：映射好的音符 + 它该在哪个声道上、用哪个音色响。
///
/// 不直接发 <see cref="MappedNote"/>：音色在 MIDI 里是声道事件，而那个类型里只有音高和时间，
/// 它又是演奏路径的词汇、要和原版逐行对拍，一个字段都加不得。
///
/// 声道和音色挂在每个音上，不是端口上的一份状态 —— 端口不必记住「现在用的是几号音色」，
/// 跳转、重排、重发整批时这句话都还是完整的。
/// </summary>
/// <param name="Note">音高与时间（音乐时间，秒）。</param>
/// <param name="Channel">发到哪个 MIDI 声道 0..15（9 = 打击乐）。</param>
/// <param name="Program">GM 音色号 0..127。只影响试听，发给游戏时永远是口琴那套键位。</param>
public sealed record PreviewNote(MappedNote Note, int Channel, int Program);
