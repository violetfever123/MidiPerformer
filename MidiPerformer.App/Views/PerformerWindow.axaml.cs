using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Perform;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>
/// 演奏器窗口 —— 打开 MIDI → 选轨与档位 → 开始 / 急停，演奏期间另有一块悬浮层。
///
/// 这是**唯一**碰外部世界的地方（窗口、文件选择器、真时钟、真键鼠网关），
/// 中间那一段全是 Core 的纯逻辑。所以这个文件里只有编排，没有算法：
/// 音符怎么映射、事件表怎么排、什么时候发、超时怎么算、倒计时怎么等，
/// 全在 <c>Core/UseCases/Perform</c> 里。
///
/// <b>这一张只剩「递参数进去、把结果放回界面」。</b> 按下开始之后：
/// 预检要的两个事实（是不是管理员、输入法是不是中文）从这里问网关取 ——
/// Core 引不到 Win32，也不该知道系统长什么样；然后整条链交给
/// <see cref="StartPerformance"/>，它同步回一个结论，之后线程、倒计时、看门狗都归它管。
/// 窗口这边只做两件事：把结论翻成中文提示，以及按 100ms 的节奏把进度画到悬浮层上。
///
/// 这一张**不做**的只剩目标窗口选择（归 13 的实机验证）。
///
/// <b>下拉框里列的是「能弹的轨」，不是「所有轨」。</b> 口琴同时只能响一个音，
/// 多声部轨发出去会糊成一片，所以能弹不能弹的判定（<see cref="TrackRanking"/>）直接
/// 决定了下拉框里有什么，而不是列出来之后再拦一道。判定与排序全在 Core 里，
/// 这里只负责把结果铺到界面上 —— 界面不判、不算、也不排。
/// </summary>
public partial class PerformerWindow : Window
{
    /// <summary>状态行那枚圆点的三种颜色（对应 wireframe 的 data-state）。</summary>
    private enum Status
    {
        /// <summary>灰：还没选曲子 / 已经停了。</summary>
        Idle,

        /// <summary>蓝：准备好了，可以开始。</summary>
        Ready,

        /// <summary>红：正在演奏。</summary>
        Running
    }

    /// <summary>基准八度下拉框：第 0 项是自动，其余按 MIDI 八度编号（C4 = 第 4 八度）。</summary>
    private static readonly string[] BaseOctaveNames =
    {
        "自动（按音域选）",
        "第 2 八度 C2", "第 3 八度 C3", "第 4 八度 C4",
        "第 5 八度 C5", "第 6 八度 C6", "第 7 八度 C7"
    };

    /// <summary>倒计时档位（秒）。顺序照 wireframe，第一项是默认。</summary>
    private static readonly double[] CountdownOptions = { 3, 5, 10 };

    private static readonly string[] CountdownNames = { "3 秒", "5 秒", "10 秒" };

    /// <summary>进度刷新的节奏（毫秒）。见 <see cref="OnProgressTick"/>。</summary>
    private const int ProgressIntervalMs = 100;

    private readonly GlobalHotkeys _hotkeys = new();

    /// <summary>
    /// 下拉框里的项，**顺序就是下拉框的顺序**。
    ///
    /// 它同时是「选中项 ⇄ 原曲轨下标」的映射：<see cref="ComboBox.SelectedIndex"/> 指的是这张表里的
    /// 第几条，不再能直接当 <c>song.Tracks</c> 的下标用（筛掉了几条，两个下标就对不上了）。
    /// 原曲下标在 <see cref="RankedTrack.SongTrackIndex"/> 里，要回指原曲一律从这个字段取。
    /// </summary>
    private readonly List<RankedTrack> _playable = new();

    /// <summary>整条演奏链。窗口只递参数、只收结论，别的都归它 —— 见 <see cref="StartPerformance"/>。</summary>
    private readonly StartPerformance _performance;

    /// <summary>悬浮层。**按需创建**：给可视化设计器用的那个空构造不该顺手多开一个窗口。</summary>
    private PerformerOverlayWindow? _overlay;

    private readonly DispatcherTimer _progressTimer;
    private int _shownCountdown = -1;

    private Song? _song;
    private bool _running;

    /// <summary>当前在发的音。派发线程写、界面线程读（见 <see cref="OnNoteSent"/>）。</summary>
    private string _currentNote = "";

    /// <summary>F6 装不上时缀在状态行后面的一句。写一次就一直在 —— 那是这一整场的状态。</summary>
    private string _hotkeyNote = "";

    /// <summary>悬浮层的拓展样式没设上时缀在状态行后面的一句。理由同上。</summary>
    private string _overlayNote = "";

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个 —— 网关只由组装点建。</summary>
    public PerformerWindow() : this(null!, null!) { }

    /// <param name="clock">墙上钟。真跑用 <see cref="SystemClock"/>，注入是为了不把测试逼到真时间上。</param>
    /// <param name="sender">键鼠出口。同一个对象既是 <see cref="IEventSink"/>，也直接提供 <c>ReleaseAll</c>。</param>
    public PerformerWindow(IClock clock, InputSender sender)
    {
        InitializeComponent();

        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;              // 标准档：InputTiming.FromIndex(1) = Standard
        BaseOctaveCombo.ItemsSource = BaseOctaveNames;
        BaseOctaveCombo.SelectedIndex = 0;          // 自动
        CountdownCombo.ItemsSource = CountdownNames;
        CountdownCombo.SelectedIndex = 0;           // 3 秒
        TrackCombo.SelectionChanged += (_, _) => ShowReady();
        _hotkeys.Panic += OnPanicHotkey;

        // sink 外面套一层 NotifyingSink：状态行要显示「当前在发哪个音」。
        // 那是界面自己的诉求，用例层不必知道有人想看它 —— 传一个装饰过的出口进去就够了。
        _performance = new StartPerformance(clock, new NotifyingSink(sender, OnNoteSent));
        _performance.Finished += OnPerformanceFinished;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProgressIntervalMs) };
        _progressTimer.Tick += OnProgressTick;

        SetStatus("就绪 · 先打开一首 MIDI", Status.Idle);
    }

    /// <summary>
    /// 低层钩子要装在有消息循环的线程上，界面线程正好有 —— 所以装在这里，
    /// 而不是构造器里（构造器未必在界面线程上跑，比如可视化设计器）。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _hotkeys.Install();
        if (!_hotkeys.Installed)
        {
            // 装不上多半是被安全软件拦了。急停按钮和看门狗都还在，但用户必须知道
            // F6 不灵 —— 这跟安全有关，不能只在状态行里一闪而过。
            _hotkeyNote = " · F6 热键没装上，急停只能用这个窗口上的按钮";
            SetStatus(StatusText.Text ?? "", _status);
        }
    }

    /// <summary>关窗时把还在放的曲子收掉，并摘钩子（摘和装在同一个线程上）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        StopPerformance();
        _progressTimer.Stop();

        // 悬浮层是**独立窗口**，不是这个窗口的子窗口 —— 子窗口会跟着它一起被最小化收走，
        // 而演奏时用户恰恰要把这边收起来、只留悬浮层压着游戏画面。代价是它不会自己跟着关，
        // 必须在这儿显式收掉，不然主窗口关了程序还留着一块透明窗口不肯退出。
        _overlay?.Close();
        _overlay = null;

        _hotkeys.Dispose();
        base.OnClosed(e);
    }

    // ==================== 曲目 ====================

    private async void OnOpenMidi(object? sender, RoutedEventArgs e)
    {
        // App 的 TFM 是 net8.0，没有 WinForms，所以只能用 Avalonia 自己的 StorageProvider。
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 MIDI 文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MIDI 文件") { Patterns = new[] { "*.mid", "*.midi", "*.rmi" } }
            }
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path)
        {
            SetStatus("这个文件读不到本地路径（是不是在网盘或压缩包里？）", Status.Idle);
            return;
        }

        try
        {
            // 读盘 + 解析 + 挑轨**全**放到线程池上。挑轨不能留在界面线程上：
            // TrackRanking.Of 对每条轨把整首音符走一遍（tick→秒→键位）再打分，
            // 一首长曲子就是几十到几百毫秒，界面照样卡那一下 —— 那就白挪了。
            // 两个都是纯 Core，回来的只是 Track 记录，不含任何界面对象。
            var (song, playable) = await Task.Run(() =>
            {
                var read = MidiReader.Read(path);
                return (read, TrackRanking.Of(read));
            });

            _song = song;

            // 能弹的轨（有音 + 单声部 + 非打击乐）已经在上面挑好排好了：最像主旋律的在最前。
            // 界面后面只用 _playable，不再回头问 song。
            _playable.Clear();
            _playable.AddRange(playable);

            SongValue.Text = Path.GetFileNameWithoutExtension(path);
            TrackHint.Text = $"只列出单声部轨 · {song.Tracks.Count} 条轨里 {_playable.Count} 条可演奏";

            bool any = _playable.Count > 0;
            // 空状态那块说明顶掉提示行 —— 两行说的是同一件事，摆一块儿只是吵。
            EmptyBox.IsVisible = !any;
            TrackHint.IsVisible = any;
            TrackCombo.IsEnabled = any;
            StartButton.IsEnabled = any;

            // 先设列表再设选中项：SelectionChanged 会去读 _playable，那之前它必须已经就位。
            TrackCombo.ItemsSource = any ? _playable.Select(Describe).ToList() : null;
            if (any)
            {
                TrackCombo.PlaceholderText = "选一条轨";   // 占位只在没选中项时露头，但别留着上一轮那句
                TrackCombo.SelectedIndex = 0;          // 默认第一条 = 最像主旋律的那条
                ShowReady();
            }
            else
            {
                TrackCombo.PlaceholderText = "— 无可演奏的轨 —";
                TrackCombo.SelectedIndex = -1;
                SetStatus($"就绪 · {song.Tracks.Count} 条轨里一条都弹不了，去编辑器里处理一下", Status.Idle);
            }
        }
        catch (Exception ex)
        {
            // 畸形 MIDI 抛的是 InvalidDataException（消息是给人看的中文），读盘还可能抛 IO 异常。
            // 这里一律收住：打不开一个文件不该让程序躺下。
            _song = null;
            ClearTrackChoices();
            SongValue.Text = "还没有选曲子";
            SetStatus($"这个文件打不开：{ex.Message}", Status.Idle);
        }
    }

    /// <summary>下拉框里的一行：wireframe 的 <c>01 主旋律 · 128 个音</c>。</summary>
    /// <remarks>
    /// 序号是它在**原曲**里的位置（<see cref="RankedTrack.SongTrackIndex"/> + 1），
    /// 不是它在下拉框里的位置：用户拿着这个号回编辑器里找那条轨，两个号对不上就找不到。
    /// 4 条轨里可弹的第 2 条是该显示成 <c>03</c> 的。
    /// </remarks>
    private static string Describe(RankedTrack r)
        => $"{r.SongTrackIndex + 1:D2} {r.Track.Name} · {r.Track.NoteCount} 个音";

    /// <summary>
    /// 把轨的下拉框收回「还没选曲子」的样子。打不开文件时走这条 ——
    /// 上一首曲子的轨不能留在框里：按钮点下去弹的会是上一首的歌。
    /// </summary>
    private void ClearTrackChoices()
    {
        _playable.Clear();
        TrackCombo.ItemsSource = null;
        TrackCombo.SelectedIndex = -1;
        TrackCombo.PlaceholderText = "先打开一首 MIDI";
        TrackCombo.IsEnabled = false;
        TrackHint.Text = "只列出单声部轨。";
        TrackHint.IsVisible = true;
        EmptyBox.IsVisible = false;
        StartButton.IsEnabled = false;
    }

    // ==================== 开始 / 急停 ====================

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_song is not { } song) return;

        // 下拉框的选中项是「能弹的轨」那张表里的下标，不是 song.Tracks 的下标 —— 中间筛掉过几条。
        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= _playable.Count) return;

        // 递给用例的是**原曲里的下标**：request.TrackIndex 指的是 song.Tracks，
        // 而这里选中的那条在 _playable 里 —— 中间筛掉过轨，两个下标不是一回事。
        var request = new StartPerformanceRequest(
            song,
            _playable[index].SongTrackIndex,
            SelectedBaseOctave(),
            InputTiming.FromIndex(TimingCombo.SelectedIndex),
            CountdownOptions[Math.Clamp(CountdownCombo.SelectedIndex, 0, CountdownOptions.Length - 1)]);

        // 预检要的两个事实在这里问网关：Core 引不到 Win32，也不该知道系统此刻长什么样。
        // 两次问都是**同步、一次性**的（一个开进程令牌、一个跨进程问输入法），
        // 按一次开始各问一次 —— 尤其输入法那次，放进每秒若干次的循环里就是白烧 CPU。
        var outcome = _performance.Start(request, InputSender.CheckElevation(), InputMethod.IsChineseActive());
        if (outcome != PerformanceStartOutcome.Started)
        {
            SetStatus(Explain(outcome), Status.Idle);
            return;
        }

        _running = true;
        _currentNote = "";
        _shownCountdown = -1;

        SetRunning(true);

        // 悬浮层这时候才拉起来，不在构造器里：一整场都不按开始的话，
        // 桌面上不该多出一块看不见又摸不着的置顶窗口。倒计时的第一个数字就由它显示。
        var overlay = ShowOverlay();
        overlay.ShowCountdown(_performance.CountdownSecondsLeft);
        _shownCountdown = _performance.CountdownSecondsLeft;

        // 拓展样式没设上就必须说一句：那块卡片会吃点击、抢焦点，之后发的键全发到它身上，
        // 表现和「游戏一个音都收不到」一样，而且不报错。F6 装不上也是这么说的（见 OnOpened）。
        // 样式要等窗口显示之后才设得上，所以这句只能跟在 ShowOverlay() 后面 ——
        // 也因此状态行挪到这儿才写：先写一次再补一句，会把已经拼上去的那句再拼一遍。
        if (!overlay.StylesApplied)
            _overlayNote = " · 悬浮层没设上点击穿透，别点到它上面";

        SetStatus("演奏中 · 准备…", Status.Running);
        _progressTimer.Start();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => StopPerformance();

    /// <summary>
    /// 空状态里的「去编辑器…」：把主窗口叫到前面来，然后关掉自己。
    ///
    /// 关掉自己是有意的：这个窗口**置顶**，留着就会一直压在编辑器上面；而它现在
    /// 一条能弹的轨都没有，什么也干不了，只是块挡路的死窗口。关掉之后用户去删声部，
    /// 回来点一次「演奏器…」就是新的一份选择（组装点那边窗口是复用 + 关了才许再开）。
    ///
    /// owner 是主窗口（<c>MainWindow.OnPerformerClick</c> 里 <c>Show(this)</c> 挂上去的）。
    /// 设计器里（空构造）没有 owner，那就只关自己。
    /// </summary>
    private void OnGoEditor(object? sender, RoutedEventArgs e)
    {
        // Owner 的类型是 WindowBase，WindowState 在 Window 上 —— 必须收窄到 Window。
        if (Owner is Window owner)
        {
            // 主窗口最小化着的时候 Activate 不一定把它翻上来，先还原再叫。
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
        Close();
    }

    /// <summary>
    /// 急停。**立刻**松键，不等派发线程自己醒过来 —— 派发线程醒来还会再走一遍收尾（幂等），
    /// 但那是它的事；用户按了急停，键就得在这一瞬间松开。
    ///
    /// 倒计时期间按它就是取消：那时派发器还没出生，叫停的只有那个取消信号。
    /// </summary>
    private void StopPerformance()
    {
        _performance.Stop();
        StopButton.IsEnabled = false;
    }

    /// <summary>F6 的回调。<see cref="GlobalHotkeys"/> 已经投递到界面线程了，这里再兜一层断言。</summary>
    private void OnPanicHotkey()
    {
        if (Dispatcher.UIThread.CheckAccess()) StopPerformance();
        else Dispatcher.UIThread.Post(StopPerformance);
    }

    /// <summary>
    /// 一场演奏收尾了（自然放完、急停、看门狗超时、中途出错）。
    ///
    /// 这个回调**在派发线程上触发**，所以整段投回界面线程 —— 到这儿为止，
    /// 按键、线程、看门狗都已经在 <see cref="StartPerformance"/> 里收干净了，
    /// 这里只剩把界面恢复成「可以再按一次」的样子。
    /// </summary>
    private void OnPerformanceFinished()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _running = false;
            _progressTimer.Stop();
            SetRunning(false);

            // 出错原文由用例层转交（它不拼中文），中文措辞在界面这一层。
            SetStatus(
                _performance.Error is { } error ? $"演奏中断：{error}" : "已停止 · 已松开所有按键",
                Status.Idle);

            // 悬浮层停在「已停止」上几秒再自己收起来：急停之后用户还在游戏里，
            // 得给他一点时间看到那句「已松开所有按键」。
            _overlay?.ShowStopped();
        });
    }

    /// <summary>
    /// 100ms 一次的节奏，两个状态都从这里推：倒计时读剩余秒数，演奏中读「距锚点过了多久」。
    ///
    /// 为什么不让派发线程在每个事件上回调界面：那一首曲子就是几百次跨线程投递，
    /// 换来的只是状态行早几十毫秒变一次 —— 不划算。进度是**算**出来的（倍速恒为 1，
    /// 就是一次减法），读它免费。见 <see cref="StartPerformance.MusicNow"/>。
    /// </summary>
    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (!_running || _overlay is not { } overlay) return;

        int left = _performance.CountdownSecondsLeft;
        if (left > 0)
        {
            // 变了才改控件：数字没变的那些 tick 不该去动视觉树。
            if (left != _shownCountdown)
            {
                _shownCountdown = left;
                overlay.ShowCountdown(left);
            }
            return;
        }

        overlay.ShowPlaying(_currentNote, _performance.MusicNow, _performance.TotalSeconds);

        if (_currentNote.Length > 0)
            SetStatus($"演奏中 · {_currentNote}", Status.Running);
    }

    /// <summary>基准八度：第 0 项是「自动」（null），其余按下拉框里的 C2…C7 折算成 MIDI 八度编号。</summary>
    private int? SelectedBaseOctave()
    {
        int index = BaseOctaveCombo.SelectedIndex;
        return index <= 0 ? null : index + 1;
    }

    /// <summary>
    /// 预检结论翻成中文。<b>文案全在这一层</b>：Core 只回一个原因，
    /// 见 <see cref="PerformanceStartOutcome"/> 的说明。
    ///
    /// 三种失败各说各的下一步动作，不写成三句「不能开始」：用户看完得知道去改什么。
    /// </summary>
    private static string Explain(PerformanceStartOutcome outcome) => outcome switch
    {
        PerformanceStartOutcome.NotElevated =>
            "没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。",
        PerformanceStartOutcome.ImeActive =>
            "没开始：输入法现在是中文。中文态下按键会被输入法截走，弹出来就是整段整段地漏音。切成英文再按一次。",
        PerformanceStartOutcome.NoPlayableTrack =>
            "没开始：这条轨弹不了。口琴一次只响一个音，所以只能弹单声部、不带打击乐的轨。换一条试试。",
        _ => "没开始。"
    };

    /// <summary>
    /// 拉起悬浮层（已经开着就只把它显出来）。
    ///
    /// 上一场结束后它会自己收起来，所以第二次「开始」得重新显示 —— 不然后面那些
    /// <c>Show*</c> 全打在一块看不见的窗口上。
    /// </summary>
    private PerformerOverlayWindow ShowOverlay()
    {
        _overlay ??= new PerformerOverlayWindow();
        if (!_overlay.IsVisible) _overlay.Show();
        return _overlay;
    }

    /// <summary>演奏中 / 空闲，界面控件的两副样子。演奏期间全部锁死，除了急停。</summary>
    private void SetRunning(bool running)
    {
        // 「有没有能弹的轨」从头到尾只有一个判据：_playable 空不空。曲子放完一轮回来，
        // 一条能弹的轨都没有的那首也不该把开始按钮重新点亮。
        StartButton.IsEnabled = !running && _playable.Count > 0;
        StopButton.IsEnabled = running;
        OpenButton.IsEnabled = !running;
        TrackCombo.IsEnabled = !running && _playable.Count > 0;
        TimingCombo.IsEnabled = !running;
        BaseOctaveCombo.IsEnabled = !running;
        CountdownCombo.IsEnabled = !running;
    }

    // ==================== 状态行 ====================

    private Status _status;

    private void ShowReady()
    {
        if (_song is null) return;

        // 与 OnStart 同一个下标含义：这张表里的第几条，不是原曲里的第几条。
        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= _playable.Count) return;

        var track = _playable[index].Track;
        SetStatus($"就绪 · {track.Name} · 共 {track.NoteCount} 个音", Status.Ready);
    }

    /// <summary>当前在发的是哪个音。由 <see cref="NotifyingSink"/> 在派发线程上叫过来。</summary>
    private void OnNoteSent(string note)
    {
        // 只记一个字，不投递：状态行和悬浮层都由 OnProgressTick 按 100ms 的节奏统一去读。
        // 每个音投递一次的话，一首曲子就是几百次跨线程投递，而肉眼看到的效果一模一样。
        // 引用赋值本身是原子的（派发线程写、界面线程读，不会读到半个字符串），
        // 最坏情况是晚一个 tick 才显示出来。
        _currentNote = note;
    }

    private void SetStatus(string text, Status status)
    {
        _status = status;
        StatusText.Text = text + _hotkeyNote + _overlayNote;
        StatusDot.Classes.Set("ready", status == Status.Ready);
        StatusDot.Classes.Set("run", status == Status.Running);
    }

    /// <summary>
    /// 给真 sink 套一层，好让界面知道「现在发的是哪个音」。
    ///
    /// 界面不能从派发线程直接改控件，所以这里只做一次转发，一个判断都不多。
    /// 状态行显示的是**音键按下**那一个事件（抬键和修饰键的 Label 是空的）。
    ///
    /// 它只转告「发了哪个音」，不转告 <c>ReleaseAll</c>：松键有三个来路
    /// （急停、看门狗、自然放完），而且起跑清场那一下也会松一次 ——
    /// 那一下要是被当成「停了」，倒计时结束的瞬间状态行会先闪一句「已停止」。
    /// 「停了」这件事由用例层的收尾回调统一说，那才是唯一确定的时刻。
    /// </summary>
    private sealed class NotifyingSink : IEventSink
    {
        private readonly IEventSink _inner;
        private readonly Action<string> _onNote;

        public NotifyingSink(IEventSink inner, Action<string> onNote)
        {
            _inner = inner;
            _onNote = onNote;
        }

        public void Send(EventBuilder.PhysicalEvent e)
        {
            // 先把键真发出去：状态行晚几毫秒显示一个音，比那个音迟到几毫秒便宜得多。
            _inner.Send(e);

            if (e.Kind == EventBuilder.K_Key && e.Down && e.Label.Length > 0)
                _onNote(e.Label);
        }

        public void ReleaseAll() => _inner.ReleaseAll();
    }
}
