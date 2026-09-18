using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Perform.Safety;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Core.UseCases.Timeline;
// 名字和 Avalonia 的 Dispatcher 撞了。别名写在这里，别在本文件里再引入那个命名空间。
using CoreDispatcher = MidiPerformer.Core.UseCases.Perform.Dispatch.Dispatcher;

namespace MidiPerformer.App.Views;

/// <summary>
/// 演奏器窗口 —— 最小的那条路径：打开 MIDI → 选轨 → 选时序档位 → 开始 / 急停。
///
/// 这是**唯一**碰外部世界的地方（窗口、文件选择器、真时钟、真键鼠网关），
/// 中间那一段全是 Core 的纯逻辑。所以这个文件里只有编排，没有算法：
/// 音符怎么映射、事件表怎么排、什么时候发、超时怎么算，全在 <c>Core/UseCases/Perform</c> 里。
///
/// <b>这一张的编排还写在 code-behind 里，是有意的</b>：spec 说「开始演奏」那条链
/// （预检 → 倒计时 → 起跑 → 挂看门狗）是一个用例 <c>StartPerformance</c>，不是 Controller。
/// 06 会把它提到用例层，那时这个文件只剩「把界面上的选择读出来、把结果放回界面」，
/// 现在写在 <see cref="RunPerformance"/> 里的那串调用会整段搬走。
///
/// 这一张**不做**的（归 05 / 06）：倒计时与悬浮层、权限与输入法预检、
/// 只列出单声部轨或只列出能弹的轨、目标窗口选择。
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

    private readonly IClock _clock;
    private readonly InputSender _sender;
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly List<string> _trackNames = new();

    private Song? _song;
    private CoreDispatcher? _dispatcher;
    private Thread? _worker;
    private bool _running;
    private string _hotkeyNote = "";

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个 —— 网关只由组装点建。</summary>
    public PerformerWindow() : this(null!, null!) { }

    /// <param name="clock">墙上钟。真跑用 <see cref="SystemClock"/>，注入是为了不把测试逼到真时间上。</param>
    /// <param name="sender">键鼠出口。同一个对象既是 <see cref="IEventSink"/>，也直接提供 <c>ReleaseAll</c>。</param>
    public PerformerWindow(IClock clock, InputSender sender)
    {
        _clock = clock;
        _sender = sender;

        InitializeComponent();

        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;              // 标准档：InputTiming.FromIndex(1) = Standard
        TrackCombo.SelectionChanged += (_, _) => ShowReady();
        _hotkeys.Panic += OnPanicHotkey;

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
            // 读盘 + 解析放到线程池上：一首长曲子要几百毫秒，界面不该卡那一下。
            var song = await Task.Run(() => SongProject.Read(path));

            _song = song;
            _trackNames.Clear();
            for (int i = 0; i < song.Tracks.Count; i++)
                _trackNames.Add($"{i + 1:D2} {song.Tracks[i].Name} · {song.Tracks[i].NoteCount} 个音");

            SongValue.Text = Path.GetFileNameWithoutExtension(path);

            // 先设列表再设选中项：SelectionChanged 会去读 _song，那之前 _song 必须已经就位。
            TrackCombo.ItemsSource = _trackNames;
            TrackCombo.IsEnabled = _trackNames.Count > 0;
            TrackCombo.SelectedIndex = 0;              // 默认第一条（挑「最像主旋律」的是 06 的事）
            StartButton.IsEnabled = _trackNames.Count > 0;

            ShowReady();
        }
        catch (Exception ex)
        {
            // 畸形 MIDI 抛的是 InvalidDataException（消息是给人看的中文），读盘还可能抛 IO 异常。
            // 这里一律收住：打不开一个文件不该让程序躺下。
            _song = null;
            _trackNames.Clear();
            TrackCombo.ItemsSource = null;
            TrackCombo.IsEnabled = false;
            StartButton.IsEnabled = false;
            SongValue.Text = "还没有选曲子";
            SetStatus($"这个文件打不开：{ex.Message}", Status.Idle);
        }
    }

    // ==================== 开始 / 急停 ====================

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_song is not { } song) return;

        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= song.Tracks.Count) return;

        var track = song.Tracks[index];
        var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);

        // 起点状态取自游戏那边的**真实**状态：上一轮急停、看门狗超时、甚至上次程序崩掉，
        // 都可能在游戏侧留下按着的键。先把它们无条件松开，再按「什么都没按」起跑
        // （这一张的起点固定是 ModState.None；等 06 把预检接上，这里会改成读真实状态）。
        _sender.ReleaseAll();

        var sink = new NotifyingSink(_sender, OnNoteSent, OnReleased);
        var dispatcher = new CoreDispatcher(_clock, sink);
        _dispatcher = dispatcher;
        _running = true;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenButton.IsEnabled = false;
        TrackCombo.IsEnabled = false;
        TimingCombo.IsEnabled = false;
        SetStatus("演奏中 · 准备…", Status.Running);

        _worker = new Thread(() => RunPerformance(song, track, timing, sink, dispatcher))
        {
            IsBackground = true,       // 后台线程：窗口关了不该被它拖着不退出
            Name = "演奏派发"
        };
        _worker.Start();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => StopPerformance();

    /// <summary>
    /// 急停。**立刻**松键，不等派发线程自己醒过来 —— 派发线程醒来还会再走一遍收尾，
    /// 但那是它的事；用户按了急停，键就得在这一瞬间松开。
    /// </summary>
    private void StopPerformance()
    {
        _dispatcher?.Stop();
        _sender.ReleaseAll();
        StopButton.IsEnabled = false;
    }

    /// <summary>F6 的回调。<see cref="GlobalHotkeys"/> 已经投递到界面线程了，这里再兜一层断言。</summary>
    private void OnPanicHotkey()
    {
        if (Dispatcher.UIThread.CheckAccess()) StopPerformance();
        else Dispatcher.UIThread.Post(StopPerformance);
    }

    /// <summary>
    /// 真正干活的那个线程。
    ///
    /// <b>下面这一串就是 06 要提到用例层（<c>StartPerformance</c>）的编排</b>：
    /// 选中轨的音符 tick → 秒 → 键位 → 事件表 → 派发，外加挂上老看门狗。
    /// 搬走之后这个方法只剩一行调用。
    /// </summary>
    private void RunPerformance(Song song, Track track, InputTiming timing, IEventSink sink, CoreDispatcher dispatcher)
    {
        try
        {
            // RepertoireToSeconds 是整条链上唯一换单位的地方：过了它，全是秒。
            var seconds = RepertoireToSeconds.Convert(track.Notes, song.TempoMap);

            // 基准八度这里传 null = 自动选（spec 里「自己指定基准八度」那条归 06）。
            var mapped = NoteMapper.Map(seconds, track.Transpose, null);

            // 超出三个八度的音跳过不发 —— 事件表只收 InRange 的那些。
            var builder = new EventBuilder { Timing = timing };
            var (events, _) = builder.Build(
                mapped.Notes.Where(n => n.InRange).ToList(), EventBuilder.ModState.None);

            // 锚点定在此刻：派发器算每个事件的物理目标时刻、看门狗算超时时刻，
            // 用的必须是同一个原点，否则两者会错开一个「起跑到挂看门狗」的间隔。
            var walker = new SongWalker(song);
            walker.Start(_clock.NowSeconds());

            var watchdog = Watchdog.For(events, walker, _clock, sink, dispatcher.Stop);
            watchdog.Start();
            try
            {
                dispatcher.Run(events, walker, timing);
            }
            finally
            {
                // 自然放完就撤掉看门狗：它守着的那 5 秒宽限已经没必要了。
                // （Dispose 只是取消，不会去动那个等待句柄 —— 见 Watchdog 的说明。）
                watchdog.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 这个线程上抛出去就是整个进程躺下 —— 而躺下的时候按键还按着。
            // 收住，报给界面，由界面告诉用户。
            Dispatcher.UIThread.Post(() => SetStatus($"演奏中断：{ex.Message}", Status.Idle));
        }
        finally
        {
            Dispatcher.UIThread.Post(Finish);
        }
    }

    /// <summary>演奏线程收工时把界面恢复成可再按一次的样子。跑在界面线程上。</summary>
    private void Finish()
    {
        _running = false;
        _worker = null;
        _dispatcher = null;

        StartButton.IsEnabled = _trackNames.Count > 0;
        StopButton.IsEnabled = false;
        OpenButton.IsEnabled = true;
        TrackCombo.IsEnabled = _trackNames.Count > 0;
        TimingCombo.IsEnabled = true;

        // 只有还停在「演奏中」才改成「已停止」：中断那条路已经报过具体原因了，别覆盖掉。
        if (_status == Status.Running)
            SetStatus("已停止 · 已松开所有按键", Status.Idle);
    }

    // ==================== 状态行 ====================

    private Status _status;

    private void ShowReady()
    {
        if (_song is not { } song) return;

        int index = TrackCombo.SelectedIndex;
        if (index < 0 || index >= song.Tracks.Count) return;

        SetStatus($"就绪 · {song.Tracks[index].Name} · 共 {song.Tracks[index].NoteCount} 个音", Status.Ready);
    }

    /// <summary>当前在发的是哪个音。由 <see cref="NotifyingSink"/> 在派发线程上叫过来。</summary>
    private void OnNoteSent(string note)
    {
        // Post 而不是直接改：这个方法是从派发线程上调进来的。
        // 而且只在「演奏中」才写 —— 派发线程的最后一批事件和收尾是挨着的，别让它把「已停止」盖掉。
        Dispatcher.UIThread.Post(() =>
        {
            if (_running) SetStatus($"演奏中 · {note}", Status.Running);
        });
    }

    /// <summary>键已经全部松开（急停、看门狗、自然放完，三条路都会到这儿）。</summary>
    private void OnReleased()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_running) SetStatus("已停止 · 已松开所有按键", Status.Idle);
        });
    }

    private void SetStatus(string text, Status status)
    {
        _status = status;
        StatusText.Text = text + _hotkeyNote;
        StatusDot.Classes.Set("ready", status == Status.Ready);
        StatusDot.Classes.Set("run", status == Status.Running);
    }

    /// <summary>
    /// 给真 sink 套一层，好把「现在发的是哪个音」报到状态行上。
    ///
    /// 界面不能从派发线程直接改控件，所以这里只做一次转发 + 一次投递，一个判断都不多。
    /// 状态行显示的是**音键按下**那一个事件（抬键和修饰键的 Label 是空的）。
    /// </summary>
    private sealed class NotifyingSink : IEventSink
    {
        private readonly IEventSink _inner;
        private readonly Action<string> _onNote;
        private readonly Action _onReleased;

        public NotifyingSink(IEventSink inner, Action<string> onNote, Action onReleased)
        {
            _inner = inner;
            _onNote = onNote;
            _onReleased = onReleased;
        }

        public void Send(EventBuilder.PhysicalEvent e)
        {
            // 先把键真发出去：状态行晚几毫秒显示一个音，比那个音迟到几毫秒便宜得多。
            _inner.Send(e);

            if (e.Kind == EventBuilder.K_Key && e.Down && e.Label.Length > 0)
                _onNote(e.Label);
        }

        public void ReleaseAll()
        {
            _inner.ReleaseAll();
            _onReleased();
        }
    }
}
