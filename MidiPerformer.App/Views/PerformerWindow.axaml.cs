using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Perform;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.App.Views;

/// <summary>
/// 演奏器窗口 —— 选轨与档位 → 开始 / 急停，演奏期间另有一块悬浮层。
/// 这是唯一碰外部世界的地方（窗口、真时钟、真键鼠网关），中间全是 Core 的纯逻辑，
/// 所以这里只有编排、没有算法：音符映射、事件表、发送时机、超时、倒计时都在 <c>Core/UseCases/Perform</c>。
/// 按下开始后，预检要的两个事实（是不是管理员、输入法是不是中文）从这里问网关取，
/// 然后整条链交给 <see cref="StartPerformance"/>；窗口只把结论翻成中文提示，并按 100ms 把进度画到悬浮层。
/// 下拉框里列的是「能弹的轨」而不是所有轨（口琴同时只能响一个音），判定
/// （<c>PlayableTracks</c>）全在 Core 里，顺序就是原曲下标顺序 —— 界面不判、不算、也不排。
/// 曲子不由这个窗口挑：它只管弹手上这首，「曲目」行连同那颗文件选择器已经按界面改版删掉了
/// （见 docs/spec-界面改版.md 的「演奏器 —— 行级决定」），曲子由外面经 <see cref="LoadSong"/> 递进来。
/// </summary>
public partial class PerformerWindow : Window
{
    /// <summary>状态行那枚圆点的三种颜色。</summary>
    private enum Status
    {
        /// <summary>灰：还没选曲子 / 已经停了。</summary>
        Idle,

        /// <summary>蓝：准备好了，可以开始。</summary>
        Ready,

        /// <summary>红：正在演奏。</summary>
        Running
    }

    /// <summary>倒计时档位（秒）。默认选中哪一项见构造器里的 <c>SelectedIndex</c>。</summary>
    private static readonly double[] CountdownOptions = { 3, 5, 10 };

    private static readonly string[] CountdownNames = { "3 秒", "5 秒", "10 秒" };

    /// <summary>进度刷新的节奏（毫秒）。</summary>
    private const int ProgressIntervalMs = 100;

    private readonly GlobalHotkeys _hotkeys = new();

    /// <summary>
    /// 下拉框里的项，顺序就是下拉框的顺序，同时是「选中项 ⇄ 原曲轨下标」的映射：
    /// <see cref="ComboBox.SelectedIndex"/> 是这张表里的第几条，不能直接当 <c>song.Tracks</c> 的下标
    /// （筛掉了几条）。原曲下标在 <see cref="PlayableTrack.SongTrackIndex"/> 里。
    /// </summary>
    private readonly List<PlayableTrack> _playable = new();

    /// <summary>整条演奏链；窗口只递参数、只收结论。</summary>
    private readonly StartPerformance _performance;

    /// <summary>悬浮层，按需创建（空构造不该顺手多开一个窗口）。</summary>
    private PerformerOverlayWindow? _overlay;

    private readonly DispatcherTimer _progressTimer;
    private int _shownCountdown = -1;

    private Song? _song;
    private bool _running;

    /// <summary>当前在发的音。派发线程写、界面线程读（见 <see cref="OnNoteSent"/>）。</summary>
    private string _currentNote = "";

    /// <summary>急停热键装不上时缀在状态行后面的一句，写一次就一直在。</summary>
    private string _hotkeyNote = "";

    /// <summary>悬浮层拓展样式没设上时缀在状态行后面的一句。</summary>
    private string _overlayNote = "";

    /// <summary>给可视化设计器用的空构造；真跑起来走下面那个。</summary>
    public PerformerWindow() : this(null!, null!) { }

    /// <param name="clock">墙上钟。真跑用 <see cref="SystemClock"/>，注入是为了不把测试逼到真时间上。</param>
    /// <param name="sender">键鼠出口，既是 <see cref="IEventSink"/> 也直接提供 <c>ReleaseAll</c>。</param>
    public PerformerWindow(IClock clock, InputSender sender)
    {
        InitializeComponent();

        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;              // 标准档（InputTiming.FromIndex(1)）
        CountdownCombo.ItemsSource = CountdownNames;
        CountdownCombo.SelectedIndex = 1;           // 5 秒
        TrackCombo.SelectionChanged += (_, _) => ShowReady();
        _hotkeys.Panic += OnPanicHotkey;

        // 判定要的那一段（倒计时 / 演奏中）由这里现问现答：钩子那边不缓存阶段，
        // 不然倒计时一结束还得多等一次 tick 才认键。
        _hotkeys.StageSource = CurrentStage;

        // sink 外面套一层 NotifyingSink，好让状态行显示当前在发哪个音；用例层不必知道
        _performance = new StartPerformance(clock, new NotifyingSink(sender, OnNoteSent));
        _performance.Finished += OnPerformanceFinished;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProgressIntervalMs) };
        _progressTimer.Tick += OnProgressTick;

        SetStatus("就绪 · 先打开一首 MIDI", Status.Idle);
    }

    /// <summary>
    /// 低层钩子要装在有消息循环的线程上，所以装在这里而不是构造器里（构造器未必在界面线程上跑）。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _hotkeys.Install();
        if (!_hotkeys.Installed)
        {
            // 装不上多半是被安全软件拦了；急停按钮和看门狗还在，但用户必须知道这个键不灵
            _hotkeyNote = " · 急停热键没装上，只能用这个窗口上的按钮";
            SetStatus(StatusText.Text ?? "", _status);
        }
    }

    /// <summary>关窗时把还在放的曲子收掉，并摘钩子（摘和装在同一个线程上）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        StopPerformance();
        _progressTimer.Stop();

        // 悬浮层是独立窗口而非这边的子窗口，不会自己跟着关，必须显式收掉，
        // 不然主窗口关了程序还留着一块透明窗口不肯退出。
        _overlay?.Close();
        _overlay = null;

        _hotkeys.Dispose();
        base.OnClosed(e);
    }

    // ==================== 曲子 ====================

    /// <summary>
    /// 把一首曲子接进这个窗口：挑出能弹的轨、列进下拉框、默认选第一条。
    /// 曲子从哪儿来不归这个窗口管 —— 「曲目」行连同那颗文件选择器已经按界面改版删掉了
    /// （见 docs/spec-界面改版.md 的「演奏器 —— 行级决定」：曲子由曲库窗口决定，演奏器只弹现在这首），
    /// 由外面把读好的 <see cref="Song"/> 递进来。
    /// </summary>
    /// <remarks>
    /// 挑轨（有音 + 单声部 + 非打击乐）与排序都在 Core 的 <c>PlayableTracks</c> 里，
    /// 这里只把结果摆到界面上：界面不判、不算、也不排。
    /// </remarks>
    public async Task LoadSong(Song song)
    {
        // 挑轨要把每条轨的音符全走一遍判单声部，长曲子几十到几百毫秒，留在界面线程上会卡，
        // 所以丢线程池；await 回来还在原来的线程上（界面线程调的就还是界面线程）。
        var playable = await Task.Run(() => PlayableTracks.Of(song));

        _song = song;

        // 能弹的轨已经挑好，按原曲下标升序；界面后面只用 _playable，不再回头问 song
        _playable.Clear();
        _playable.AddRange(playable);

        TrackHint.Text = $"只列出单声部轨 · {song.Tracks.Count} 条轨里 {_playable.Count} 条可演奏";

        bool any = _playable.Count > 0;
        // 空状态那块说明顶掉提示行，两行说的是同一件事
        EmptyBox.IsVisible = !any;
        TrackHint.IsVisible = any;
        TrackCombo.IsEnabled = any;
        StartButton.IsEnabled = any;

        // 先设列表再设选中项：SelectionChanged 会读 _playable
        TrackCombo.ItemsSource = any ? _playable.Select(Describe).ToList() : null;
        if (any)
        {
            TrackCombo.PlaceholderText = "选一条轨";   // 占位只在没选中项时露头，但别留着上一轮那句
            TrackCombo.SelectedIndex = 0;          // 默认第一条能弹的轨
            ShowReady();
        }
        else
        {
            TrackCombo.PlaceholderText = "— 无可演奏的轨 —";
            TrackCombo.SelectedIndex = -1;
            SetStatus($"就绪 · {song.Tracks.Count} 条轨里一条都弹不了，去编辑器里处理一下", Status.Idle);
        }
    }

    /// <summary>下拉框里的一行：序号 + 轨名 + 音数。</summary>
    /// <remarks>
    /// 序号是它在原曲里的位置（<see cref="PlayableTrack.SongTrackIndex"/> + 1），不是它在下拉框里的位置：
    /// 用户拿着这个号回编辑器里找那条轨，两个号对不上就找不到。
    /// </remarks>
    private static string Describe(PlayableTrack r)
        => $"{r.SongTrackIndex + 1:D2} {r.Track.Name} · {r.Track.NoteCount} 个音";

    // ==================== 开始 / 急停 ====================

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_song is not { } song) return;

        // 下拉框的选中项是「能弹的轨」那张表里的下标，不是 song.Tracks 的下标（中间筛掉过几条），
        // 所以递给用例的是原曲里的下标 _playable[index].SongTrackIndex。
        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= _playable.Count) return;

        var request = new StartPerformanceRequest(
            song,
            _playable[index].SongTrackIndex,
            // 基准八度永远自动（null）：按音域挑那个能容下最多音符的八度。人工选八度是程序算得比人准的事，
            // 「基准八度」那一行连同它的下拉框已经按界面改版删掉了。
            BaseOctave: null,
            InputTiming.FromIndex(TimingCombo.SelectedIndex),
            CountdownOptions[Math.Clamp(CountdownCombo.SelectedIndex, 0, CountdownOptions.Length - 1)]);

        // 预检要的两个事实在这里问网关（Core 引不到 Win32）。两次问都是同步一次性的，
        // 只在按下开始时各问一次 —— 输入法那次放进循环里就是白烧 CPU。
        var outcome = _performance.Start(request, InputSender.CheckElevation(), InputMethod.IsChineseActive());
        if (outcome != PerformanceStartOutcome.Started)
        {
            // 文案在展现层（Format）拼，不在这儿
            SetStatus(Format.PreflightRefusal(outcome), Status.Idle);
            return;
        }

        _running = true;
        _currentNote = "";
        _shownCountdown = -1;

        SetRunning(true);

        // 悬浮层这时候才拉起来，不在构造器里：不按开始就不该在桌面上多一块置顶窗口
        var overlay = ShowOverlay();
        overlay.ShowCountdown(_performance.CountdownSecondsLeft);
        _shownCountdown = _performance.CountdownSecondsLeft;

        // 拓展样式没设上就必须说一句：卡片会吃点击、抢焦点，之后发的键全发到它身上且不报错。
        // 样式要等窗口显示之后才设得上，所以这句只能跟在 ShowOverlay() 后面 ——
        // 状态行也因此挪到这儿才写，免得把已经拼上去的那句再拼一遍。
        if (!overlay.StylesApplied)
            _overlayNote = " · 悬浮层没设上点击穿透，别点到它上面";

        SetStatus("演奏中 · 准备…", Status.Running);
        _progressTimer.Start();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => StopPerformance();

    /// <summary>
    /// 空状态里的「去编辑器…」：把主窗口叫到前面来，然后关掉自己 —— 这个窗口置顶，
    /// 而它现在一条能弹的轨都没有，留着只是块挡路的死窗口；用户去删完声部再点一次「演奏器…」
    /// 就是新的一份选择。owner 是主窗口（<c>MainWindow.OnPerformerClick</c> 里 <c>Show(this)</c> 挂的），
    /// 设计器里（空构造）没有 owner，那就只关自己。
    /// </summary>
    private void OnGoEditor(object? sender, RoutedEventArgs e)
    {
        // Owner 的类型是 WindowBase，WindowState 在 Window 上，必须收窄到 Window
        if (Owner is Window owner)
        {
            // 最小化时 Activate 不一定能把它翻上来，先还原再叫
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
        Close();
    }

    /// <summary>
    /// 急停。立刻松键，不等派发线程自己醒过来（它醒来还会再收尾一遍，幂等）。
    /// 倒计时期间按它就是取消：那时派发器还没出生，叫停的只有那个取消信号。
    /// </summary>
    private void StopPerformance()
    {
        _performance.Stop();
        StopButton.IsEnabled = false;
    }

    /// <summary>
    /// 现在走到哪一段了 —— 给 <see cref="GlobalHotkeys"/> 阶段门用，按下那一刻由它现问。
    /// 倒计时期间按任意键都不停（那几秒正是用户切窗口的时间），取消只有这颗「急停」按钮；
    /// 进入演奏后才认键。
    /// </summary>
    private PerformanceStage CurrentStage()
    {
        if (!_running) return PerformanceStage.Idle;

        // 倒计时读的是用例层那个剩余秒数，和悬浮层画的是同一个数 —— 不另存一份阶段记号，
        // 两份记号就有对不上的时候（对不上的那一下正好落在「按了没反应」上）。
        return _performance.CountdownSecondsLeft > 0 ? PerformanceStage.Countdown : PerformanceStage.Playing;
    }

    /// <summary>急停的回调。弹键能走到这儿，说明判定已经放行（演奏中、非修饰键、非注入键）。</summary>
    private void OnPanicHotkey()
    {
        if (Dispatcher.UIThread.CheckAccess()) StopPerformance();
        else Dispatcher.UIThread.Post(StopPerformance);
    }

    /// <summary>
    /// 一场演奏收尾了（自然放完、急停、看门狗超时、中途出错）。这个回调在派发线程上触发，
    /// 所以整段投回界面线程；到这儿为止按键、线程、看门狗都已在 <see cref="StartPerformance"/>
    /// 里收干净，这里只剩把界面恢复成可以再按一次的样子。
    /// </summary>
    private void OnPerformanceFinished()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _running = false;
            _progressTimer.Stop();
            SetRunning(false);

            // 出错原文由用例层转交，中文措辞在界面这一层
            SetStatus(
                _performance.Error is { } error ? $"演奏中断：{error}" : "已停止 · 已松开所有按键",
                Status.Idle);

            // 悬浮层停在「已停止」上几秒再自己收起来，留时间让用户看到那句话
            _overlay?.ShowStopped();
        });
    }

    /// <summary>
    /// 100ms 一次的节奏，两个状态都从这里推：倒计时读剩余秒数，演奏中读「距锚点过了多久」。
    /// 不让派发线程在每个事件上回调界面（一首曲子几百次跨线程投递，只换来状态行早几十毫秒变一次）；
    /// 进度是算出来的，读它免费。
    /// </summary>
    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (!_running || _overlay is not { } overlay) return;

        int left = _performance.CountdownSecondsLeft;
        if (left > 0)
        {
            // 变了才改控件：数字没变的那些 tick 不该去动视觉树
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

    /// <summary>
    /// 拉起悬浮层（已经开着就只把它显出来）。上一场结束后它会自己收起来，所以每次「开始」
    /// 都得重新显示，不然后面那些 <c>Show*</c> 全打在一块看不见的窗口上。
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
        // 「有没有能弹的轨」只有一个判据：_playable 空不空
        StartButton.IsEnabled = !running && _playable.Count > 0;
        StopButton.IsEnabled = running;
        TrackCombo.IsEnabled = !running && _playable.Count > 0;
        TimingCombo.IsEnabled = !running;
        CountdownCombo.IsEnabled = !running;
    }

    // ==================== 状态行 ====================

    private Status _status;

    private void ShowReady()
    {
        if (_song is null) return;

        // 和 OnStart 同一个下标含义：这张表里的第几条，不是原曲里的第几条
        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= _playable.Count) return;

        var track = _playable[index].Track;
        SetStatus($"就绪 · {track.Name} · 共 {track.NoteCount} 个音", Status.Ready);
    }

    /// <summary>当前在发的是哪个音。由 <see cref="NotifyingSink"/> 在派发线程上叫过来。</summary>
    private void OnNoteSent(string note)
    {
        // 只记一个字，不投递：状态行和悬浮层都由 OnProgressTick 按 100ms 统一去读。
        // 引用赋值本身是原子的，最坏晚一个 tick 才显示出来。
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
    /// 给真 sink 套一层，好让界面知道现在发的是哪个音。界面不能从派发线程直接改控件，
    /// 所以这里只做一次转发；状态行显示的是音键按下那一个事件（抬键和修饰键的 Label 是空的）。
    /// 它只转告「发了哪个音」，不转告 <c>ReleaseAll</c>：松键有三个来路，起跑清场那一下也会松一次，
    /// 当成「停了」的话倒计时结束时状态行会先闪一句「已停止」；「停了」由用例层的收尾回调统一说。
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
            // 先把键真发出去：状态行晚几毫秒显示一个音，比那个音迟到几毫秒便宜得多
            _inner.Send(e);

            if (e.Kind == EventBuilder.K_Key && e.Down && e.Label.Length > 0)
                _onNote(e.Label);
        }

        public void ReleaseAll() => _inner.ReleaseAll();
    }
}
