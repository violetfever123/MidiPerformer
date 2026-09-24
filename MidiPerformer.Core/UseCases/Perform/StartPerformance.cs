using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Dispatch;
using MidiPerformer.Core.UseCases.Perform.Preflight;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Perform.Safety;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform;

/// <summary>
/// 起一场演奏：预检 → 倒计时 → 建事件表 → 定锚点 → 挂看门狗 → 派发。
/// 它真做编排（Controller 那条链只是「点按钮 → 调命令」的纯管道）。
///
/// 预检在调用线程上同步答完再返回（界面要拿到不放行的原因），倒计时与派发交给后台线程：
/// 占着界面线程就等于窗口不再重绘、急停按钮点不动。
///
/// 进度是算出来的：倍速恒为 1，音乐时间就是距锚点那一刻的物理时长，所以 <see cref="MusicNow"/>
/// 只是一次减法，派发线程不必在每个事件上回头报告。只有取消信号与线程引用两个托管对象，不实现 IDisposable。
/// </summary>
public sealed class StartPerformance
{
    /// <summary>倒计时与等待取消的分片长度（秒）。要短到「按了急停键立刻有反应」，也要短到假时钟下推进得动。</summary>
    private const double SliceSeconds = 0.005;

    private readonly IClock _clock;
    private readonly IEventSink _sink;

    /// <summary>叫停信号。倒计时等待与派发器各看一份：倒计时还没结束就急停时，派发器还没出生。</summary>
    private readonly ManualResetEventSlim _cancel = new(false);

    private Dispatcher? _dispatcher;
    private Watchdog? _watchdog;
    private Thread? _worker;

    private volatile bool _running;
    private volatile string? _error;

    /// <summary>倒计时的截止物理时刻。</summary>
    private double _countdownEndsAt;

    /// <summary>整曲时长（秒），进度条的分母。</summary>
    private double _totalSeconds;

    /// <summary>起跑那一刻的物理时刻（<see cref="double.NaN"/> = 还没起跑）。只写一次，界面线程读。</summary>
    private double _anchorPhysical = double.NaN;

    /// <param name="clock">墙上钟。真跑用 <c>SystemClock</c>，测试用 <c>FakeClock</c>。</param>
    /// <param name="sink">按键出口。</param>
    public StartPerformance(IClock clock, IEventSink sink)
    {
        _clock = clock;
        _sink = sink;
    }

    /// <summary>这一场收尾了（自然放完、急停、看门狗超时、或者中途出错）。在派发线程上触发。</summary>
    public event Action? Finished;

    /// <summary>还在倒计时或还在派发。界面拿它防重复起跑。</summary>
    public bool Running => _running;

    /// <summary>
    /// 出错的原文（没有错就是 <c>null</c>），只有 <see cref="Finished"/> 之后读才有意义。
    /// 存的是异常自己的消息而不是拼好的中文句子：文案归界面，这里只转交一个原因。
    /// </summary>
    public string? Error => _error;

    /// <summary>倒计时还剩几秒（向上取整，给悬浮层的大数字用）。没在倒计时时是 0。</summary>
    public int CountdownSecondsLeft
    {
        get
        {
            if (!_running) return 0;

            double left = _countdownEndsAt - _clock.NowSeconds();
            return left <= 0 ? 0 : (int)Math.Ceiling(left);
        }
    }

    /// <summary>走到哪儿了（音乐时间，秒）。已按整曲时长截断，可以直接喂进度条。</summary>
    public double MusicNow
    {
        get
        {
            double anchor = Volatile.Read(ref _anchorPhysical);
            if (double.IsNaN(anchor)) return 0;

            double now = _clock.NowSeconds() - anchor;
            if (now < 0) return 0;
            return now > _totalSeconds ? _totalSeconds : now;
        }
    }

    /// <summary>整曲时长（秒）。进度条的分母。</summary>
    public double TotalSeconds => _totalSeconds;

    /// <summary>
    /// 预检 + 起跑，同步返回预检的结论：不是 <see cref="PerformanceStartOutcome.Started"/>
    /// 就一个音都没发出去、一条线程都没起，界面照着原因给中文提示即可。
    /// </summary>
    public PerformanceStartOutcome Start(
        StartPerformanceRequest request,
        bool elevated,
        bool imeInChinese)
    {
        // 已经有一场在跑就不重开第二场：两场共用同一块键盘，收尾时会互相松对方的键。
        // 回 Started 而不是抛：抛在按钮回调里就是一次崩溃。
        if (_running) return PerformanceStartOutcome.Started;

        var outcome = PerformancePreflight.Check(request, elevated, imeInChinese);
        if (outcome != PerformanceStartOutcome.Started) return outcome;

        // 起跑前把取消信号清掉：窗口关闭、切歌这些路径会无条件调一次 Stop，那一下不该把下一场也毙掉。
        _cancel.Reset();
        _error = null;
        _anchorPhysical = double.NaN;
        _totalSeconds = request.Song.TotalSeconds;

        // 倒计时终点在按下开始这一刻就算定：等线程被调度起来再算的话，线程启动那几毫秒会算进用户的倒计时里。
        _countdownEndsAt = _clock.NowSeconds() + Math.Max(0, request.CountdownSeconds);

        _running = true;
        _worker = new Thread(() => Run(request))
        {
            IsBackground = true,        // 后台线程：窗口关了不该被它拖着不退出
            Name = "演奏派发"
        };
        _worker.Start();

        return PerformanceStartOutcome.Started;
    }

    /// <summary>
    /// 急停（任意键、急停按钮、关窗都走它）。可以从任何线程调，重复调无害。
    /// 倒计时期间调它就是「取消」；立刻松一次键不等派发线程醒来 —— 它自己的收尾是幂等的。
    /// </summary>
    public void Stop()
    {
        _cancel.Set();
        _dispatcher?.Stop();
        _watchdog?.Cancel();
        _sink.ReleaseAll();
    }

    /// <summary>派发线程跑的全部东西。所有异常都收在这里 —— 这个线程上抛出去就是整个进程躺下，而躺下时按键还按着。</summary>
    private void Run(StartPerformanceRequest request)
    {
        try
        {
            if (!WaitCountdown()) return;                 // 倒计时期间被取消

            var (events, walker) = BuildEventTable(request);

            // 建表不是瞬时的（长曲子上万音符，几十毫秒起步），而这一段的 Stop() 只会置上 _cancel：
            // 派发器还没出生，等它醒来就会把整首一个音不漏地弹完，所以这里必须再看一眼。
            if (_cancel.IsSet) return;

            var dispatcher = new Dispatcher(_clock, _sink);
            _dispatcher = dispatcher;

            // 起跑前清场：上一轮可能在游戏侧留下按着的键。放在锚点这一刻而不是按下开始那一刻 ——
            // 中间隔着几秒倒计时，用户在这几秒里早把状态改过了。
            _sink.ReleaseAll();

            // 锚点在挂看门狗之前定：看门狗的超时时刻从同一条 PhysicalAt 算出来，反过来会让两者错开。
            double anchor = _clock.NowSeconds();
            walker.Start(anchor);
            Volatile.Write(ref _anchorPhysical, anchor);

            var watchdog = Watchdog.For(events, walker, _clock, _sink, dispatcher.Stop);
            _watchdog = watchdog;
            watchdog.Start();

            try
            {
                dispatcher.Run(events, walker, request.Timing);
            }
            finally
            {
                // 放完了（或急停/超时收尾了）就撤掉看门狗：它守的那 5 秒宽限已经没必要了。
                watchdog.Dispose();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _dispatcher = null;
            _watchdog = null;
            _running = false;

            // 这一句落在上面那个 catch 的射程之外：订阅者一抛（界面那边是 Dispatcher.UIThread.Post），
            // 异常就从这条后台线程逃出去，而这个文件第一行写着那意味着整个进程躺下。
            try { Finished?.Invoke(); }
            catch { }
        }
    }

    /// <summary>
    /// 盲目等到倒计时归零，返回 false = 中途被叫停。
    /// 分片等而不是一次睡满：取消要立刻生效，而且假时钟只在被读的时候前进，一次睡满测试会挂死。
    /// </summary>
    private bool WaitCountdown()
    {
        while (!_cancel.IsSet)
        {
            double remaining = _countdownEndsAt - _clock.NowSeconds();
            if (remaining <= 0) return true;

            _cancel.Wait(TimeSpan.FromSeconds(Math.Min(remaining, SliceSeconds)));
        }
        return false;
    }

    /// <summary>
    /// 要弹的那张事件表。四步的链子不在这个文件里 —— 它在 <see cref="EventTable.Build"/>，
    /// 界面量按键速度读数（50 号票）用的是**同一个方法**：读数与演奏对拍的前提就是同一个真相源。
    /// </summary>
    private static (List<EventBuilder.PhysicalEvent> Events, SongWalker Walker) BuildEventTable(
        StartPerformanceRequest request)
    {
        // 中间那个是这张表铺了多久 —— 派发这一路用不上（进度条读的是 Song.TotalSeconds），
        // 它是按键速度读数的分母
        var (events, _, walker) = EventTable.Build(
            request.Song, request.TrackIndex, request.Timing, request.BaseOctave);
        return (events, walker);
    }
}
