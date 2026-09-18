using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 试听出声的出口（winmm → Microsoft GS Wavetable Synth）。
///
/// 它**和演奏器共用同一个 <c>SongWalker</c>**（时间积分、变速、跳转）—— 那几件事
/// 只写一遍，两边就不会各自跑偏，测一次两边都覆盖。
///
/// 出站端口：用例层引用、适配层实现。winmm 出声没法断言，所以缝开在它上面。
/// </summary>
public interface IAudioSink
{
    /// <summary>
    /// 从当前进度开始，把这批音发出去。音符时间是**音乐时间**（秒，与播放倍速无关），
    /// 已含该轨的移调；<c>InRange == false</c> 的音由实现方自行忽略或照发，不影响演奏路径。
    ///
    /// 每个音自己带着声道与音色（见 <see cref="PreviewNote"/>），所以「哪条轨用哪个音色」
    /// 在用例层就定完了，实现方只负责照着发：该在哪个声道换音色是它的事，
    /// **不必**、也**不该**在这里再猜一次哪几个音属于同一条轨。
    /// </summary>
    void Play(IReadOnlyList<PreviewNote> notes);

    /// <summary>停止并释放所有正在响的音。**无循环**：放完就停，不留尾音。</summary>
    void Stop();

    /// <summary>把播放位置挪到指定音乐时间（秒）。播放中跳转、从当前位置起播都走它。</summary>
    void Seek(double musicSeconds);
}
