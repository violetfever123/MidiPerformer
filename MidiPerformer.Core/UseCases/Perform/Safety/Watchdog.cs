using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform.Safety;

/// <summary>
/// 看门狗：曲子放不停怎么办。**原版 harmonica-auto-player 里没有这块**，是新增的兜底。
///
/// 硬超时 = <b>最后一个事件的音乐时间 + <see cref="GraceSeconds"/> 秒</b>，经
/// <see cref="SongWalker.PhysicalAt"/> 换成物理时刻。到点**无条件**做两件事：
/// 通知派发器停、把所有按住的键松开。
///
/// <b>超时时刻一次算好</b>（<see cref="For"/> 里算，构造完就是个 double），
/// 运行时只剩「现在 >= 截止」这一次比较。每次循环去翻事件表、算时长，
/// 都会在时间最紧的那一段上多花时间，还会把「到底按哪个时刻算」这件事搞得说不清。
///
/// <b>为什么不是「起个线程睡 5 秒」</b>：那样超时是拿真实时间当判据的，测试只能干等五秒，
/// 而且曲子长度一变就测不准。这里时间从注入的 <see cref="IClock"/> 来，
/// 于是注入假时钟就能把「到点」这件事在微秒内验完。线程是有的，但它等的是时钟，不是墙。
///
/// 看门狗是**兜底**，不是常规收尾：自然放完由派发器自己松键（见 <c>Dispatcher.Run</c> 的 finally），
/// 那时应当 <see cref="Cancel"/> 掉看门狗，免得它过五秒再补一枪。
/// </summary>
public sealed class Watchdog : IDisposable
{
    /// <summary>曲长之外还宽限多久。给最后几个事件、系统调度抖动和「差一点点没放完」留的余量。</summary>
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
    /// 「最后一个事件」取的是时间戳最大的那个而不是列表末尾，免得事件表没排序时算出一个偏早的
    /// 截止点 —— 那会把一首正常放着的曲子拦腰砍断。空事件表退回曲首 + 5 秒，空曲子也该收尾。
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
    /// 撤掉看门狗（曲子自然放完时调）。幂等，可以从任何线程调。
    /// 已经在等的线程不会立刻死掉，但它醒来会发现已被取消，于是不再开火。
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

            // 分片等：既等超时也等取消。睡满一片就重新看一次时钟，
            // 取消那一下则靠事件唤醒 —— 两者共用一次等待。
            _cancelled.Wait(TimeSpan.FromSeconds(Math.Min(remaining, SliceSeconds)));
        }
    }

    /// <summary>
    /// 开火。到点之后**无条件**：先叫停派发器，再把所有键松开。
    ///
    /// 顺序是有讲究的：先叫停、后松键。反过来的话，派发线程可能刚好在松键之后又发出一个按下，
    /// 那个键就永久卡住了。叫停在前，派发线程最多再发一个事件，它自己的收尾（finally 里的
    /// ReleaseAll）随后会把这些一起清掉。这里的 ReleaseAll 是给「派发线程本身卡住了」兜底的。
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
