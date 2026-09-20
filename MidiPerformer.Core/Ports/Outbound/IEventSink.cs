using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 物理按键的出口。端口不是「网关的接口」，是用例需要的那一小块 ——
/// 网关 <c>InputSender</c> 上其余的 <c>CheckElevation()</c> 之类由界面直接调，不经过端口。
/// 出站端口：用例层引用、适配层实现，记录型假实现让 <c>Dispatcher</c> 与 <c>Watchdog</c> 不碰真键盘也能测。
/// </summary>
public interface IEventSink
{
    /// <summary>发出一个物理输入事件（按下或松开，键盘或鼠标）。</summary>
    void Send(EventBuilder.PhysicalEvent e);

    /// <summary>
    /// 松开所有可能还按着的键：<c>Z X C V B N M</c>、逗号，以及鼠标左中右三键。
    /// 急停、看门狗超时、曲子放完三条收尾路径都走它。实现方不要「记住按过哪些」——
    /// 上一轮中断可能留下残留，这里要的是无条件清干净。
    /// </summary>
    void ReleaseAll();
}
