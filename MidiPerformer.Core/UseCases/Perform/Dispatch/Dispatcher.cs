using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform.Dispatch;

/// <summary>
/// 派发器：把 <see cref="EventBuilder"/> 建好的事件表按时间戳发出去。
/// 时间从 <see cref="IClock"/> 来、按键往 <see cref="IEventSink"/> 去，两个端口都是注入的。
///
/// 三个时间别混（见 <see cref="SongWalker"/>）：事件表里 <c>e.T</c> 是音乐时间，而 sleep 只能睡物理时间，
/// 两者之间隔着播放倍速，所以每个事件的物理目标时刻是 <c>walker.PhysicalAt(e.T) - LeadMs/1000</c>。
/// LeadMs 是给游戏按帧采样留的提前量（见 <see cref="InputTiming.LeadMs"/>），不乘倍速。
///
/// 等法：先按时间戳分片睡，最后 <see cref="SpinSeconds"/> 改成自旋；睡觉用可中断的等待，
/// 急停那一下不必等当前这一觉睡完（见 <see cref="Stop"/>）。
///
/// 收尾只有一条路：无论自然放完、急停还是看门狗超时，<see cref="Run"/> 退出时一律
/// <see cref="IEventSink.ReleaseAll"/>。
/// </summary>
public sealed class Dispatcher
{
    /// <summary>
    /// 最后这段改成自旋等待（秒）。比一次线程调度的抖动小一个量级，又短到烧不掉多少 CPU。
    /// </summary>
    public const double SpinSeconds = 0.0015;

    /// <summary>
    /// 一次最多睡多久（秒）。不一次睡到目标时刻：假时钟只在被读的时候前进，
    /// 一次睡到目标会让等待循环变成 O(n²) 次真实睡眠。分片之后，推进由读时钟驱动。
    /// </summary>
    private const double MaxSleepSliceSeconds = 0.005;

    private readonly IClock _clock;
    private readonly IEventSink _sink;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stopped;

    /// <summary>两个端口都由外部注入，假实现能替进来是这一层可测的前提。</summary>
    public Dispatcher(IClock clock, IEventSink sink)
    {
        _clock = clock;
        _sink = sink;
    }

    /// <summary>是否已经收到停止信号。</summary>
    public bool Stopped => _stopped;

    /// <summary>
    /// 叫停。可以从任何线程调（看门狗在自己的线程上、急停按钮在界面上）。
    /// 置标志和唤醒等待缺一不可：只置标志，睡觉的派发线程不会知道；只唤醒，醒来会再睡回去。
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        _wake.Set();
    }

    /// <summary>
    /// 把一整张事件表放完，然后收尾。
    ///
    /// 调用方要先把 <see cref="SongWalker"/> 的锚点定下来（<c>walker.Start(clock.NowSeconds())</c>），
    /// 这里不重设：看门狗的超时时刻按同一个锚点算出，再 Start 一次会让两者错开。
    /// 事件表按时间戳升序（<see cref="EventBuilder.Build"/> 就是），这里不重排、不去重。
    /// </summary>
    public void Run(
        IReadOnlyList<EventBuilder.PhysicalEvent> events,
        SongWalker walker,
        InputTiming timing)
    {
        try
        {
            double lead = timing.LeadMs / 1000.0;

            foreach (var e in events)
            {
                // 每个事件发出前都查一次：急停之后一个键都不该再发出去。
                if (_stopped) return;

                WaitUntil(walker.PhysicalAt(e.T) - lead);

                // 等待期间被叫停：那个键已经不该发了。
                if (_stopped) return;

                _sink.Send(e);
            }
        }
        finally
        {
            // 三条路都从这里出去，所以松键只写这一处。多松一次不要紧，少松一次会把键卡在按下状态。
            _sink.ReleaseAll();
        }
    }

    /// <summary>等到物理时刻 <paramref name="targetPhysical"/>。</summary>
    private void WaitUntil(double targetPhysical)
    {
        while (!_stopped)
        {
            double remaining = targetPhysical - _clock.NowSeconds();

            // 迟到（或目标本来就在过去）就直接发：等到的是负数没有意义。
            if (remaining <= 0) return;

            if (remaining <= SpinSeconds)
            {
                SpinUntil(targetPhysical);
                return;
            }

            // 两种醒法：睡满一片，或被 Stop 叫醒。急停靠后者，所以不能用裸 Thread.Sleep。
            _wake.Wait(TimeSpan.FromSeconds(Math.Min(remaining - SpinSeconds, MaxSleepSliceSeconds)));

            // 假时钟务必开 AutoStepSeconds：手动模式下时钟不往前走，自旋那一段会死转。
        }
    }

    /// <summary>自旋到最后 1.5ms —— 剩下这段时间比一次调度抖动还短。</summary>
    private void SpinUntil(double targetPhysical)
    {
        // 一样要查停止信号：自旋期间也得能立刻停。
        while (!_stopped && _clock.NowSeconds() < targetPhysical)
            Thread.SpinWait(20);
    }
}
