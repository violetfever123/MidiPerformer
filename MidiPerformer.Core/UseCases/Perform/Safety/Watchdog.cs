using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform.Safety;

/// <summary>
/// 看门狗：曲子放不停怎么办。原版 harmonica-auto-player 里没有这块，是新增的兜底。
///
/// 硬超时 = 最后一个事件的音乐时间 + <see cref="GraceSeconds"/> 秒，经 <see cref="SongWalker.PhysicalAt"/>
/// 换成物理时刻。到点无条件做两件事：通知派发器停、把所有按住的键松开。
/// 超时时刻在 <see cref="For"/> 里一次算好，运行时只剩一次比较。
///
/// 时间从注入的 <see cref="IClock"/> 来，注入假时钟就能把「到点」在微秒内验完。
/// 它是兜底而不是常规收尾：自然放完时应当 <see cref="Cancel"/> 掉，免得过五秒再补一枪。
/// </summary>
public sealed class Watchdog : IDisposable
{
    /// <summary>曲长之外还宽限多久。给最后几个事件和系统调度抖动留的余量。</summary>
    public const double GraceSeconds = 5.0;

    /// <summary>一次最多等多久（秒）。纯粹为了「取消」这件事能及时被看见，跟超时精度无关。</summary>
    private const double SliceSeconds = 0.005;

    private readonly IClock _clock;
    private readonly IEventSink _sink;
    private readonly Action _stop;
    private readonly ManualResetEventSlim _cancelled = new(false);
    private Thread? _thread;
    private int _fired;

    /// <param name="clock">时间判据。真跑用 <c>SystemClock</c>，测试用 <c>FakeClock</c>。</param>
    /// <param name="sink">到点时无条件全松的出口。</param>
    /// <param name="stop">通知派发器停的回调（真跑就是 <c>Dispatcher.Stop</c>）。</param>
    /// <param name="deadlinePhysicalSeconds">物理时刻的截止点。一般由 <see cref="For"/> 算出来。</param>
    public Watchdog(IClock clock, IEventSink sink, Action stop, double deadlinePhysicalSeconds)
    {
        _clock = clock;
        _sink = sink;
        _stop = stop;
        DeadlineSeconds = deadlinePhysicalSeconds;
    }

    /// <summary>
    /// 按事件表算出让看门狗出场的时刻：最后一个事件的音乐时间 + <see cref="GraceSeconds"/>，
    /// 再经 <paramref name="walker"/> 换成物理时刻。
    ///
    /// 「最后一个事件」取时间戳最大的那个而不是列表末尾，免得事件表没排序时截止点偏早。
    /// 空事件表退回曲首 + 5 秒。
    /// </summary>
    public static Watchdog For(
        IReadOnlyList<EventBuilder.PhysicalEvent> events,
        SongWalker walker,
        IClock clock,
        IEventSink sink,
        Action stop)
    {
        double lastMusicT = 0;
        foreach (var e in events)
            if (e.T > lastMusicT) lastMusicT = e.T;

        return new Watchdog(clock, sink, stop, walker.PhysicalAt(lastMusicT) + GraceSeconds);
    }

    /// <summary>截止的物理时刻（秒）。</summary>
    public double DeadlineSeconds { get; }

    /// <summary>是否已经响过。响过之后 <see cref="Start"/> 不会再响第二次。</summary>
    public bool Fired => Volatile.Read(ref _fired) != 0;

    /// <summary>起一条后台线程守到点。重复调只有第一次生效。</summary>
    public void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(Loop)
        {
            IsBackground = true,        // 后台线程：主窗口关了不该被它拖着不退出
            Name = "演奏看门狗"
        };
        _thread.Start();
    }

    /// <summary>
    /// 撤掉看门狗（曲子自然放完时调）。幂等，可以从任何线程调；已在等的线程醒来会发现已被取消，不再开火。
    /// </summary>
    public void Cancel() => _cancelled.Set();

    public void Dispose()
    {
        Cancel();
        _cancelled.Dispose();
    }

    private void Loop()
    {
        while (!_cancelled.IsSet)
        {
            double remaining = DeadlineSeconds - _clock.NowSeconds();
            if (remaining <= 0)
            {
                Fire();
                return;
            }

            // 分片等：既等超时也等取消 —— 睡满一片重看一次时钟，取消靠事件唤醒。
            _cancelled.Wait(TimeSpan.FromSeconds(Math.Min(remaining, SliceSeconds)));
        }
    }

    /// <summary>
    /// 开火。到点之后无条件：先叫停派发器，再把所有键松开。
    ///
    /// 顺序不能反 —— 反了的话派发线程可能刚在松键之后又发出一个按下，那个键就永久卡住。
    /// 这里的 ReleaseAll 是给「派发线程本身卡住了」兜底的。
    /// </summary>
    private void Fire()
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        try
        {
            _stop();
        }
        finally
        {
            // 叫停那条路万一抛了，键还是得松 —— 这是这一整块唯一的职责。
            _sink.ReleaseAll();
        }
    }
}
