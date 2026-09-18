using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Dispatch;
using MidiPerformer.Core.UseCases.Perform.Preflight;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Perform.Safety;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.Core.UseCases.Perform;

/// <summary>
/// 起一场演奏：<b>预检 → 倒计时 → 建事件表 → 定锚点 → 挂看门狗 → 派发</b>。
///
/// 它是**用例**，不是 Controller：Controller 那条链是「点按钮 → 调命令」的纯管道，
/// 而这里真做编排。窗口那边只剩「把界面上的选择读出来、把结果放回界面」。
///
/// <b>线程由这里起，不由窗口起。</b> 按「开始」的是界面线程，而倒计时最长 10 秒、
/// 派发可能几分钟 —— 占着界面线程就等于窗口不再重绘、急停按钮点不动、悬浮层的大数字也不动。
/// 但预检必须**同步**答完再返回：界面要知道「不放行」的原因才给得出那句中文提示，
/// 也得在真起跑之前决定禁不禁用按钮、亮不亮悬浮层。于是分工是：
/// 预检在调用线程上当场答，预检之后的全部（倒计时、建表、派发、收尾）交给这条后台线程。
///
/// <b>进度是算出来的，不是问出来的。</b> 倍速恒为 1，音乐时间就是「距锚点那一刻的物理时长」，
/// 所以 <see cref="MusicNow"/> 只是一次减法 —— 悬浮层的进度条因此不需要派发线程在每个事件上
/// 回头报告一次（那会把每次按键都变成一次跨线程投递）。
///
/// 生命周期跟着窗口走：不实现 IDisposable，因为这里只有两个托管对象（取消信号、线程引用），
/// 线程是后台线程、信号是纯托管内存，窗口一关就再没人碰它们。
/// 给一个 Dispose 只是多一条「谁先调」的规矩，换不来任何实际回收。
/// </summary>
public sealed class StartPerformance
{
    /// <summary>
    /// 倒计时与等待取消的分片长度（秒）。取 5ms 的理由和 <see cref="Dispatcher"/> 那一片一样：
    /// 要短到「按了 F6 立刻有反应」，也要短到假时钟下推进得动 —— 见 <see cref="WaitCountdown"/>。
    /// </summary>
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

    /// <summary>倒计时的截止物理时刻。在 <see cref="Start"/> 里定死 —— 见那里的说明。</summary>
    private double _countdownEndsAt;

    /// <summary>整曲时长（秒），进度条的分母。</summary>
    private double _totalSeconds;

    /// <summary>起跑那一刻的物理时刻（<see cref="double.NaN"/> = 还没起跑）。只写一次，界面线程读。</summary>
    private double _anchorPhysical = double.NaN;

    /// <param name="clock">墙上钟。真跑用 <c>SystemClock</c>，测试用 <c>FakeClock</c>。</param>
    /// <param name="sink">按键出口。界面上要显示「当前在发哪个音」时，套一层记录型实现递进来即可。</param>
    public StartPerformance(IClock clock, IEventSink sink)
    {
        _clock = clock;
        _sink = sink;
    }

    /// <summary>这一场收尾了（自然放完、急停、看门狗超时、或者中途出错）。**在派发线程上触发。**</summary>
    public event Action? Finished;

    /// <summary>还在倒计时或还在派发。界面拿它防重复起跑。</summary>
    public bool Running => _running;

    /// <summary>
    /// 出错的原文（没有错就是 <c>null</c>）。只有 <see cref="Finished"/> 之后读才有意义。
    ///
    /// 存的是**异常自己的消息**而不是拼好的中文句子：文案归界面，这里只转交一个原因 ——
    /// 和 <see cref="PerformanceStartOutcome"/> 是同一条规矩。
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
    /// 预检 + 起跑。<b>同步返回预检的结论</b>：不是 <see cref="PerformanceStartOutcome.Started"/>
    /// 就一个音都没发出去、一条线程都没起，界面照着原因给中文提示即可。
    /// </summary>
    public PerformanceStartOutcome Start(
        StartPerformanceRequest request,
        bool elevated,
        bool imeInChinese)
    {
        // 已经有一场在跑：不重开第二场。两场共用同一块键盘（同一份按键、同一个 F6），
        // 收尾时互相松对方的键，是真正的安全事故。界面上「开始」在演奏期间本来就按不动，
        // 这一条是兜底。回 Started 而不是抛：抛在按钮回调里就是一次崩溃。
        if (_running) return PerformanceStartOutcome.Started;

        var outcome = PerformancePreflight.Check(request, elevated, imeInChinese);
        if (outcome != PerformanceStartOutcome.Started) return outcome;

        // 起跑前把取消信号清掉：窗口关闭、切歌这些路径会**无条件**调一次 Stop，
        // 那一下不该把下一场也一起毙掉（ManualResetEventSlim 一旦置上就一直置着）。
        _cancel.Reset();
        _error = null;
        _anchorPhysical = double.NaN;
        _totalSeconds = request.Song.TotalSeconds;

        // 倒计时的终点在**按下开始这一刻**就算定：等线程被调度起来再算的话，
        // 线程启动那几毫秒会算进用户的倒计时里，而且悬浮层第一次读剩余秒数时
        // 这个值可能还没写下去。
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
    /// 急停（F6、急停按钮、关窗都走它）。<b>可以从任何线程调</b>，重复调无害。
    ///
    /// 倒计时期间调它就是「取消」—— 用户还在数秒，取消之后一个音都不该发出去。
    /// 立刻松一次键是在派发线程之外做的：派发线程醒来还会再走一遍自己的收尾（幂等），
    /// 但用户按了急停，键就得在这一瞬间松开，不能等那一觉睡完。
    /// </summary>
    public void Stop()
    {
        _cancel.Set();
        _dispatcher?.Stop();
        _watchdog?.Cancel();
        _sink.ReleaseAll();
    }

    /// <summary>派发线程跑的全部东西。**所有异常都收在这里** —— 这个线程上抛出去就是整个进程躺下，而躺下时按键还按着。</summary>
    private void Run(StartPerformanceRequest request)
    {
        try
        {
            if (!WaitCountdown()) return;                 // 倒计时期间被取消

            var (events, walker) = BuildEventTable(request);

            // 建表这段不是瞬时的：长曲子上万音符，tick→秒→键位要走一遍，几十毫秒起步。
            // 而这一段的 Stop() 只会置上 _cancel —— 派发器还没出生，`_dispatcher?.Stop()` 打空，
            // 等它醒来就会把整首一个音不漏地弹完。**急停失灵**，正是 WaitCountdown 那段
            // 说明里要防的事，所以这里必须再看一眼。
            if (_cancel.IsSet) return;

            var dispatcher = new Dispatcher(_clock, _sink);
            _dispatcher = dispatcher;

            // 起跑前清场：上一轮（急停、看门狗超时、程序崩过）可能在游戏侧留下按着的键。
            // 放在**锚点这一刻**而不是按下开始那一刻 —— 中间隔着几秒倒计时，用户在这几秒里
            // 切窗口、按键盘，早清的那一次已经不作数了。清场的含义就是「起点状态 = 什么都没按」，
            // 所以事件表也照 EventBuilder.ModState.None 建（见 BuildEventTable）。
            _sink.ReleaseAll();

            // 锚点在挂看门狗**之前**定。看门狗的超时时刻是从同一条 PhysicalAt 算出来的，
            // 反过来会让两者错开一个「起跑到挂看门狗」的间隔 —— Dispatcher.Run 因此刻意不重设锚点。
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
                // Dispose 只是取消，不去动那个等待句柄 —— 见 Watchdog 的说明。
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

            // 这一句在 finally 里，落在上面那个 catch 的射程**之外** —— 订阅者一抛
            // （界面那边是 Dispatcher.UIThread.Post，程序正在关的时候它会抛），
            // 异常就从这条后台线程逃出去，而这个文件第一行就写着那意味着整个进程躺下。
            // 收尾这一刻除了把界面恢复过来没有别的事可做，恢复不了也不该把进程带走。
            try { Finished?.Invoke(); }
            catch { }
        }
    }

    /// <summary>
    /// 盲目等到倒计时归零。返回 false = 中途被叫停。
    ///
    /// <b>分片等，不是一次睡满。</b> 两个理由：一是取消要立刻生效（最长 10 秒的倒计时，
    /// 按了 F6 却要等这一觉睡完才反应，那叫急停失灵）；二是假时钟只在被**读**的时候前进，
    /// 一次睡满的话测试里时钟永远不动，等待循环原地打转、测试挂死 ——
    /// 所以「推进」这件事必须由读时钟驱动，见 <c>FakeClock.AutoStepSeconds</c>。
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
    /// 选中轨 → 秒 → 键位 → 事件表。四步的顺序就是 <c>RepertoireToSeconds</c> 定下的那条链，
    /// 一步都不多：换单位只发生一次，之后全是移植来的那套数学。
    /// </summary>
    private static (List<EventBuilder.PhysicalEvent> Events, SongWalker Walker) BuildEventTable(
        StartPerformanceRequest request)
    {
        var track = request.Song.Tracks[request.TrackIndex];

        var seconds = RepertoireToSeconds.Convert(track.Notes, request.Song.TempoMap);

        // 基准八度：手动给的就是它，null 走自动（让可演奏区容下最多音符）。
        var mapped = NoteMapper.Map(seconds, track.Transpose, request.BaseOctave);

        // 超出三个八度的音跳过不发 —— 事件表只收 InRange 的那些。卷帘上它们已经标灰了，
        // 用户看到的就是「这些音弹不出来」，发一串没意义的按键反而更糟。
        var builder = new EventBuilder { Timing = request.Timing };
        var (events, _) = builder.Build(
            mapped.Notes.Where(n => n.InRange).ToList(), EventBuilder.ModState.None);

        return (events, new SongWalker(request.Song));
    }
}
