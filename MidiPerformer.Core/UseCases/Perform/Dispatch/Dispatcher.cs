using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform.Dispatch;

/// <summary>
/// 派发器：把 <see cref="EventBuilder"/> 建好的事件表按时间戳发出去。
///
/// <b>为什么它在 Core 而不是在网关里</b>：演奏器接管键盘、把按键发进游戏，跑起来只能盯着看，
/// 没有任何断点可下。这种代码测试价值最高，所以派发逻辑留在内层，时间从
/// <see cref="IClock"/> 来、按键往 <see cref="IEventSink"/> 去，两个端口都是注入的，
/// 于是假时钟 + 记录型 sink 就能把整条时间轴跑一遍。
///
/// <b>三个时间别混</b>（见 <see cref="SongWalker"/>）：事件表里 <c>e.T</c> 是**音乐时间**，
/// 而 sleep 只能睡物理时间，两者之间隔着播放倍速。所以每个事件的物理目标时刻是
/// <c>walker.PhysicalAt(e.T) - LeadMs/1000</c>。
///
/// <b>LeadMs 为什么不乘倍速</b>：它是给游戏**按帧采样**留的提前量（见
/// <see cref="InputTiming.LeadMs"/>），游戏那边一帧还是 16.7ms，跟你放多快没有关系。
/// 乘上倍速就等于「放得快时提前量变小」，越快漏得越多 —— 这正是它单独存在一个字段的理由。
///
/// <b>等法</b>：先按时间戳睡，最后 <see cref="SpinSeconds"/> 改成自旋。
/// 睡醒这一刻带着系统调度抖动（可能迟到十几毫秒），自旋那一段没有；1.5ms 是把
/// 「自旋烧 CPU」压到可以忽略的上限。睡觉用的是可中断的等待（不是裸 Thread.Sleep），
/// 于是急停那一下不用等当前这一觉睡完 —— 见 <see cref="Stop"/>。
///
/// <b>收尾只有一条路</b>：无论自然放完、急停还是看门狗超时，<see cref="Run"/> 退出时
/// 一律 <see cref="IEventSink.ReleaseAll"/>。三条路径各写一遍迟早会漏一条，
/// 漏掉的那条就是把某个键永久卡在按下状态。
/// </summary>
public sealed class Dispatcher
{
    /// <summary>
    /// 最后这段改成自旋等待（秒）。取 1.5ms 是因为它比一次线程调度的抖动小一个量级，
    /// 又短到自旋烧不掉多少 CPU —— 曲子里最长的那段等待仍然是在睡觉。
    /// </summary>
    public const double SpinSeconds = 0.0015;

    /// <summary>
    /// 一次最多睡多久（秒）。
    ///
    /// 不一次睡到目标时刻，是因为「睡多久」这件事在假时钟下完全不成立：假时钟只在被**读**的时候
    /// 才前进，睡一个按假时钟算出来的时长，醒来会发现时间几乎没动，于是又睡一遍 ——
    /// 等待循环会变成 O(n²) 次真实睡眠，测试跑到天荒地老。
    /// 分片之后，等待循环的推进由「读时钟」驱动，假时钟一个自动步进就推进一步。
    ///
    /// 代价是长时间休止符上会多醒几次（每秒 200 次），对一首曲子可以忽略。
    /// </summary>
    private const double MaxSleepSliceSeconds = 0.005;

    private readonly IClock _clock;
    private readonly IEventSink _sink;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stopped;

    /// <summary>两个端口都由外部注入，这里不 new 任何东西 —— 假实现能替进来是这一层可测的全部前提。</summary>
    public Dispatcher(IClock clock, IEventSink sink)
    {
        _clock = clock;
        _sink = sink;
    }

    /// <summary>是否已经收到停止信号。</summary>
    public bool Stopped => _stopped;

    /// <summary>
    /// 叫停。<b>可以从任何线程调</b>（看门狗在自己的线程上、急停按钮在界面上）。
    ///
    /// 置标志和唤醒等待是两件事，缺一不可：只置标志的话，正在睡觉的派发线程不会知道；
    /// 只唤醒的话，醒来重新算一次等待又会睡回去。
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        _wake.Set();
    }

    /// <summary>
    /// 把一整张事件表放完，然后收尾。
    ///
    /// <b>调用方要先把 <see cref="SongWalker"/> 的锚点定下来</b>（<c>walker.Start(clock.NowSeconds())</c>），
    /// 这里刻意不重设：看门狗的超时时刻是按同一个锚点从同一条 <see cref="SongWalker.PhysicalAt"/>
    /// 算出来的，这里再 Start 一次会让两者错开一个「起跑到挂看门狗」的间隔。
    ///
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

                // 等待期间被叫停：这一觉虽然醒得早，但那个键已经不该发了。
                if (_stopped) return;

                _sink.Send(e);
            }
        }
        finally
        {
            // 自然放完、急停、看门狗超时 —— 三条路都从这里出去，所以松键只写这一处。
            // 多松一次没有代价（ReleaseAll 是幂等的无条件全松），少松一次会把键卡在按下状态。
            _sink.ReleaseAll();
        }
    }

    /// <summary>等到物理时刻 <paramref name="targetPhysical"/>。</summary>
    private void WaitUntil(double targetPhysical)
    {
        while (!_stopped)
        {
            double remaining = targetPhysical - _clock.NowSeconds();

            // 迟到（或目标本来就在过去）就直接发：时间不会倒流，等到的是负数是没意义的。
            if (remaining <= 0) return;

            if (remaining <= SpinSeconds)
            {
                SpinUntil(targetPhysical);
                return;
            }

            // 这一觉的两种醒法：睡满一片，或被 Stop 叫醒。急停靠的是后者，
            // 所以它不能是 Thread.Sleep —— 那样最长要等一整片才反应得过来。
            _wake.Wait(TimeSpan.FromSeconds(Math.Min(remaining - SpinSeconds, MaxSleepSliceSeconds)));

            // 假时钟务必开 AutoStepSeconds：手动模式下时钟不往前走，自旋那一段会死转。
            // 见 Tests/Fakes/FakeClock 的说明。
        }
    }

    /// <summary>自旋到最后 1.5ms 里，睡已经不是好主意了 —— 调度抖动的量级比剩下的时间还大。</summary>
    private void SpinUntil(double targetPhysical)
    {
        // 一样要查停止信号：自旋期间按 F6 也得立刻停。
        while (!_stopped && _clock.NowSeconds() < targetPhysical)
            Thread.SpinWait(20);
    }
}
