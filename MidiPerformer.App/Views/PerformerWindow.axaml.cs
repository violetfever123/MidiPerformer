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

    /// <summary>
    /// 音域读数那块（38 根细条）。窗口只喂数据 —— 窗口怎么切、哪几格亮、读数行写什么字
    /// 全在 <see cref="PitchRangeReadout"/> 里（纯函数，另有测试盯着）。
    /// </summary>
    private readonly PitchRangeView _range;

    /// <summary>
    /// 按键速度那块（平均 / 峰值 / 每秒直方图）。窗口只喂数据 —— 三个数怎么算、哪一秒是峰值
    /// 全在 <see cref="KeyRateReadout"/> 里（同样是纯函数，另有测试盯着）。
    /// </summary>
    private readonly KeyRateView _keyRate;

    /// <summary>
    /// 这一份按键速度读数的代号。算表是丢线程池的（长曲子几十毫秒起步），算完回来时用户可能
    /// 已经换了轨 / 换了时序 / 换了曲子 —— 晚到的那一份不许盖掉新算的那一份。
    /// </summary>
    private int _keyRateStamp;

    /// <summary>上一次画到条子上的那个音。<see cref="OnProgressTick"/> 100ms 一次，同一个音不必重画。</summary>
    private string _rangeNote = "";

    /// <summary>
    /// 微调：整首歌相对窗口平移几个半音（−1 / 0 / +1），默认 0。
    ///
    /// **它不写回 <c>track.Transpose</c>** —— 演奏器是「放」的，不是「改」的：写回就是用户没保存
    /// 的情况下动了他的曲子，而这一屏根本没有保存这个概念。所以它只跟着**这一次**走：
    /// 连同一个基准八度交给 <see cref="StartPerformanceRequest.TransposeOffset"/> 进事件表，
    /// 同时喂给音域读数 —— 条子上亮的那几格和真按下去的那几格因此还是同一张表。
    /// </summary>
    private int _fineTune;

    private Song? _song;
    private bool _running;

    /// <summary>
    /// 演奏中递进来、还没上的那一首（见 <see cref="LoadSong"/>）。
    /// 同一时刻最多一份：后递的顶掉先递的 —— 用户最后点的那次「演奏」说了算。
    /// 挑好的轨一起记着，收尾时不用再挑一遍。
    /// </summary>
    private (Song Song, IReadOnlyList<PlayableTrack> Playable)? _pending;

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

        // 微调默认 0（三颗里选中的是中间那颗）。XAML 里三颗都不带 .on —— 选中态是**代码**刷的
        // （见 MarkFineTune），所以「默认选谁」只有 _fineTune 这一个真相源。
        MarkFineTune();

        // 时序是事件表的一个输入（帧宽 / 提前量 / 重触发间隔都在它里面）：换了档位就是另一张表，
        // 按键速度读数得跟着重量一遍 —— 摆着旧数就是「读数」和「真按下去那张表」两个真相源。
        // 挂在这个下标设完之后，免得构造期白算一次。
        TimingCombo.SelectionChanged += (_, _) => _ = RefreshKeyRate();

        TrackCombo.SelectionChanged += (_, _) =>
        {
            ShowReady();
            RefreshRange();      // 换一条轨，条子跟着换（亮的那片跟着这首曲子的音域走）
            _ = RefreshKeyRate();   // 换一条轨，按键速度那两个数和直方图一起换
        };
        _hotkeys.Panic += OnPanicHotkey;

        // 判定要的那一段（倒计时 / 演奏中）由这里现问现答：钩子那边不缓存阶段，
        // 不然倒计时一结束还得多等一次 tick 才认键。
        _hotkeys.StageSource = CurrentStage;

        // sink 外面套一层 NotifyingSink，好让状态行显示当前在发哪个音；用例层不必知道
        _performance = new StartPerformance(clock, new NotifyingSink(sender, OnNoteSent));
        _performance.Finished += OnPerformanceFinished;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProgressIntervalMs) };
        _progressTimer.Tick += OnProgressTick;

        // 读数那块在这儿认领：控件是 XAML 摆好的，这里只把「哪几个控件拼成一块读数」告诉它。
        // 数目对不上（少写一根细条、读数行少一个槽）它当场抛 —— 那种毛病画出来只是「有点不对」，
        // 不抛就一直没人发现。
        _range = new PitchRangeView(RangeBar, RangeKeys, RangeSummary, RangeAlert, RangeAlertTitle, RangeAlertSub);
        RefreshRange();                             // 还没曲子：38 根全暗、读数行不出现

        // 按键速度那块同理：控件是 XAML 摆好的，这里只把「哪几个控件拼成一块读数」告诉它。
        // 还没曲子：整块不出现（XAML 里就是 IsVisible="False"），装上曲子时 ApplySong 才去算。
        _keyRate = new KeyRateView(KeyRate, KeyRateAverage, KeyRatePeak, KeyRateBars,
                                   KeyRateAxisStart, KeyRateAxisNote, KeyRateAxisEnd);

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
    ///
    /// **演奏进行中递进来的先记着，画面一个字都不动**（73 号票）。正在响的还是上一首
    /// （<see cref="StartPerformance"/> 手上那份是它自己拿着的，跟这里换不换无关），
    /// 这时候把下拉、状态行、空状态换成新那首，屏幕上说的曲子就跟耳朵里听的不是同一首了 ——
    /// 那 38 根条子还会按新那首的轨重画，正在响的那个音跟着跑到别人的音域里。
    /// 这一场收尾时再上（见 <see cref="OnPerformanceFinished"/>）：递进来的曲子不丢，只是晚一步。
    /// </remarks>
    public async Task LoadSong(Song song)
    {
        // 挑轨要把每条轨的音符全走一遍判单声部，长曲子几十到几百毫秒，留在界面线程上会卡，
        // 所以丢线程池；await 回来还在原来的线程上（界面线程调的就还是界面线程）。
        var playable = await Task.Run(() => PlayableTracks.Of(song));

        if (_running)
        {
            _pending = (song, playable);
            return;
        }

        ApplySong(song, playable);
    }

    /// <summary>
    /// 把手上的曲子换成这一份并画出来：下拉框、提示行、空状态、选中第一条、状态行。
    ///
    /// **只在空闲那条路上调**（演奏中递进来的走 <see cref="LoadSong"/> 里那个 <c>_pending</c>）——
    /// 这里写的全是「空闲那副样子」，演奏期间写上去就等于把正在响的那一首从画面上抹掉。
    /// </summary>
    private void ApplySong(Song song, IReadOnlyList<PlayableTrack> playable)
    {
        // 这一份上了，之前记着的那份作废（后递的那一次说了算）
        _pending = null;

        _song = song;

        // 能弹的轨已经挑好，按原曲下标升序；界面后面只用 _playable，不再回头问 song
        _playable.Clear();
        _playable.AddRange(playable);

        TrackHint.Text = $"只列出单声部轨 · {song.Tracks.Count} 条轨里 {_playable.Count} 条可演奏";

        bool any = _playable.Count > 0;
        // 空状态那块说明顶掉提示行，两行说的是同一件事
        EmptyBox.IsVisible = !any;
        TrackHint.IsVisible = any;

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

        // 「开始 / 轨下拉」开不开，判据只有 SetRunning 一处（73 号票：从前这里自己另写了一份，
        // 漏了「没在演奏」，于是演奏中递一手曲子就把这两颗按钮重新点亮了）。
        // 空闲时它给出的正是「有得弹就开」。
        SetRunning(_running);

        // 两条路都要走一遍：换上新曲子（或换上一首一条可弹的都没有的）之后，
        // 条子上不该还留着上一首的亮片
        RefreshRange();

        // 按键速度同理：读数也是**这首歌**的量 —— 换曲子（或换上一首一条可弹的都没有的）
        // 之后不该还摆着上一首的那两个数。它是丢线程池算的，算完自己会画上去。
        _ = RefreshKeyRate();
    }

    /// <summary>下拉框里的一行：序号 + 轨名 + 音数。</summary>
    /// <remarks>
    /// 序号是它在原曲里的位置（<see cref="PlayableTrack.SongTrackIndex"/> + 1），不是它在下拉框里的位置：
    /// 用户拿着这个号回编辑器里找那条轨，两个号对不上就找不到。
    /// </remarks>
    private static string Describe(PlayableTrack r)
        => $"{r.SongTrackIndex + 1:D2} {r.Track.Name} · {r.Track.NoteCount} 个音";

    // ==================== 微调（±1 半音） ====================

    /// <summary>
    /// 三颗微调按钮（−1 / 0 / +1）。按钮只说「要哪一档」，值在这儿落地：选中的那档刷上 <c>.on</c>，
    /// 条子跟着重画，按键速度也重量一遍。
    ///
    /// **为什么读数要重量一遍**：微调改的是「要发出去的那张表」（它进 <c>EventTable.Build</c>），
    /// 表变了，靠这张表量出来的两个数（平均 / 峰值）和直方图就跟着那一份 —— 摆着旧数就是
    /// 「读数」和「真按下去那张表」两个真相源。原型写的就是「这两个数在选好轨 / 动微调的时候就算出来」。
    ///
    /// 窗口（基准八度）**不跟着挪**：它载入时算定（见 <see cref="WindowBaseOctave"/>），
    /// 所以条子整片平移一根，而不是「挪一格、顺手换个窗口再挪一格」。
    ///
    /// 演奏期间这三颗是灰的（<see cref="SetRunning"/>）：这一场的事件表按下开始那一刻就建好了，
    /// 中途再挪半音只挪得动读数 —— 屏幕上说挪了，耳朵里那张旧表照发，那正是要防的那种「对不上」。
    /// </summary>
    private void OnFineTune(object? sender, RoutedEventArgs e)
    {
        // Tag 里是 ASCII 的 -1 / 0 / 1（按钮上写的是排版用的减号 U+2212，两个字符不一样是有意的）。
        // 取不出档位就什么都不做 —— 三颗按钮的字与档位对不上是写错了，不该顺手当成 0。
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int value)) return;

        _fineTune = value;
        MarkFineTune();
        RefreshRange();
        _ = RefreshKeyRate();
    }

    /// <summary>三颗按钮里挂着 <c>.on</c> 的只有选中的那一档（默认 0，见构造器）。</summary>
    private void MarkFineTune()
    {
        FineMinus.Classes.Set("on", _fineTune == -1);
        FineZero.Classes.Set("on", _fineTune == 0);
        FinePlus.Classes.Set("on", _fineTune == 1);
    }

    /// <summary>下拉框选中的那条轨（<c>null</c> = 还没曲子 / 一条能弹的都没有）。</summary>
    private PlayableTrack? CurrentTrack()
    {
        // 和 OnStart / RefreshRange 同一个下标含义：这张表里的第几条，不是原曲里的第几条
        int index = TrackCombo.SelectedIndex;
        return index >= 0 && index < _playable.Count ? _playable[index] : null;
    }

    /// <summary>
    /// 这 38 格窗口压在哪儿（基准八度）。**算一次就钉住** —— 它从这条轨的音高与它自己的移调来，
    /// **不含微调**：一档一个半音，挪的是整首歌、窗口不动（原型那句）。所以 −1 / 0 / +1 来回按，
    /// 窗口始终是同一个，条子整片平移。
    ///
    /// 算法和执行时那个窗口是同一个：<see cref="PitchRangeReadout.Measure"/> 挑的就是
    /// <c>NoteMapper.AutoBaseOctave</c>，而微调这边又把它当 <c>Map</c> 的 manualBaseOctave 递回去 ——
    /// 屏幕上的窗口和发出去的那些音用的是同一个八度，不各挑各的。
    /// </summary>
    private static int? WindowBaseOctave(IReadOnlyList<int>? pitches, int transpose)
        => pitches is null ? null : PitchRangeReadout.Measure(pitches, transpose).BaseOctave;

    // ==================== 音域读数（38 根细条） ====================

    /// <summary>
    /// 把当前这条轨的音域画到那 38 格上。
    ///
    /// 喂进去的是**原谱**音高加这条轨的移调**加微调** —— 和执行时 <c>StartPerformance</c> 递给
    /// <c>NoteMapper.Map</c> 的是同一对；基准八度由这条轨算定、微调不含在里面
    /// （见 <see cref="WindowBaseOctave"/>）。所以条子上亮的那几格，就是真按下去的那几格。
    ///
    /// 「正在响的那个音」从事件表那串字（<c>NoteMapper.Describe</c>）反解出来：
    /// 派发线程递到界面的只有那串字，音高是它带过来的唯一一样东西。
    /// </summary>
    private void RefreshRange()
    {
        _rangeNote = _currentNote;

        var track = CurrentTrack();
        var pitches = track?.Track.Notes.Select(n => n.Pitch).ToList();
        int transpose = track?.Track.Transpose ?? 0;

        _range.Show(
            pitches,
            transpose + _fineTune,
            PitchRangeReadout.PitchOfLabel(_currentNote),
            WindowBaseOctave(pitches, transpose));
    }

    // ==================== 按键速度（平均 + 峰值 + 每秒直方图） ====================

    /// <summary>
    /// 把当前这条轨量成一块按键速度读数，画到窗口上。
    ///
    /// **在播放之前算**（用户的原话：「可以尽量在播放之前就计算出来吗？我不希望在播放的时候
    /// 临时看」）—— 载入、换一条演奏轨、换一次时序、动一次微调就重算一遍，演奏期间一个数都不动，
    /// 没有计时器、没有节流，也没有一边弹一边更新这回事。微调也算在里面，是因为它改的是那张表
    /// （<c>TransposeOffset</c> 进了 <c>EventTable.Build</c>）：表变了，读数不跟着变就是两个真相源。
    ///
    /// 三个数都从 <see cref="EventTable.Build"/> 那张**事件表**来：就是真按下去时
    /// <see cref="StartPerformance"/> 递给派发器的那一张（它自己也走同一个方法，连时序都是
    /// 同一个下拉给的）。**不另走一遍 Song 的轨数据** —— 那就成了「同一件事有第二个写它的地方」，
    /// 屏幕上那个「最密的一秒」迟早和耳朵里真挨的那一秒对不上。
    /// </summary>
    private async Task RefreshKeyRate()
    {
        var request = CurrentRequest();

        // 这一份的代号。算完回来时对一下：换过轨 / 换过时序 / 换过曲子的话，晚到的那份作废
        int stamp = ++_keyRateStamp;

        if (request is null)
        {
            _keyRate.Show(null);            // 没曲子 / 没有能弹的轨：整块不出现
            return;
        }

        // 建表不是瞬时的（长曲子上万音符，几十毫秒起步 —— 同 StartPerformance 里那句），
        // 别占着界面线程算；await 回来还在界面线程上。
        var state = await Task.Run(() =>
        {
            var (events, seconds, _) = EventTable.Build(
                request.Song, request.TrackIndex, request.Timing, request.BaseOctave,
                request.TransposeOffset);
            return KeyRateReadout.Measure(events, seconds);
        });

        if (stamp != _keyRateStamp) return;
        _keyRate.Show(state);
    }

    /// <summary>
    /// 手上这份选择拼出来的那一次「演奏」：选中的轨 + 时序 + 倒计时 + 微调。
    ///
    /// 按下开始、以及算按键速度读数，要的是**同一份**参数 —— 各拼一遍的话，
    /// 读数会和真按下去的那张事件表错开（比如时序换了读数没换）。给不出就是 <c>null</c>
    /// （还没曲子 / 一条能弹的轨都没有）。
    /// </summary>
    private StartPerformanceRequest? CurrentRequest()
    {
        if (_song is not { } song) return null;

        // 下拉框的选中项是「能弹的轨」那张表里的下标，不是 song.Tracks 的下标（中间筛掉过几条），
        // 所以递给用例的是原曲里的下标 _playable[index].SongTrackIndex。
        if (CurrentTrack() is not { } track) return null;

        var pitches = track.Track.Notes.Select(n => n.Pitch).ToList();

        return new StartPerformanceRequest(
            song,
            track.SongTrackIndex,
            // 基准八度：载入这条轨时算出来、算完钉住的那一个。从前这儿是 null（每次让 Map 自己再挑
            // 一遍），而微调要的是「挪的是整首歌，窗口不动」—— 每次都重挑的话，挪到边界上会突然
            // 跳一整段，用户看到的就不是整片平移一根了。人工选八度仍然是程序算得比人准的事，
            // 「基准八度」那一行连同它的下拉框早就按界面改版删掉了。
            BaseOctave: WindowBaseOctave(pitches, track.Track.Transpose),
            InputTiming.FromIndex(TimingCombo.SelectedIndex),
            CountdownOptions[Math.Clamp(CountdownCombo.SelectedIndex, 0, CountdownOptions.Length - 1)],
            // 微调：这一次演奏的偏移（−1 / 0 / +1）。它进事件表 —— 条子上亮的那几格和真按下去的
            // 那几格因此还是同一张表。**不写回 track.Transpose**：演奏器是「放」的，不是「改」的。
            TransposeOffset: _fineTune);
    }

    // ==================== 开始 / 急停 ====================

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_running) return;

        // 要弹的是哪一首、哪条轨、哪个档位，全由 CurrentRequest 一处拼出来 ——
        // 和上面那块按键速度读数是同一份参数（读数就是在播放之前按它算的）。
        if (CurrentRequest() is not { } request) return;

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

        // 按下开始这一刻就把窗口收下去（51 号票）：倒计时那几秒存在的唯一目的就是给用户切窗口
        // （见 48 的阶段门），所以收下去必须落在按下这一下、赶在倒计时**画出来之前** ——
        // 等倒计时走完再收，用户还得自己再切一次，那几秒就白留了。
        //
        // 位置在预检放行**之后**：被拒的那一次一个音都没发出去、一条线程都没起，
        // 窗口不该跟着下去 —— 用户正要看那句原因。
        // 倒计时本身在 Core 里走（StartPerformance 的终点在按下那一刻就算定），跟窗口在不在屏幕上无关；
        // 收下去这一下排在读它、画它、起那条 100ms 节奏之前，所以那几秒一秒都不会少。
        //
        // ⚠️ 三条收场路径（正常结束 / 急停 / 出错）**一律不碰 WindowState**，窗口就留在最小化，
        // 用户自己切回来。**别在这儿加「结束还原」**：急停那一下一个**置顶**窗弹回来，
        // 盖住的正是用户刚想接着玩的游戏画面；而且急停认任意键，他手还在键盘上，
        // 弹回来会让下一个按键直接打到窗口上（51 号票 §②）。
        WindowState = WindowState.Minimized;

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
        // 倒计时读的是用例层那个剩余秒数，和悬浮层画的是同一个数 —— 不另存一份阶段记号，
        // 两份记号就有对不上的时候（对不上的那一下正好落在「按了没反应」上）。
        return GlobalHotkeys.StageOf(_running, _performance.CountdownSecondsLeft);
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

            // 细边收掉：没在响了，条子上不该还留着一根套边的
            _currentNote = "";
            RefreshRange();

            // 出错原文由用例层转交，中文措辞在界面这一层
            SetStatus(
                _performance.Error is { } error ? $"演奏中断：{error}" : "已停止 · 已松开所有按键",
                Status.Idle);

            // 悬浮层停在「已停止」上几秒再自己收起来，留时间让用户看到那句话
            _overlay?.ShowStopped();

            // 演奏期间递进来的那一首：**这一场收干净了才上**（见 LoadSong）——
            // 半路换画面会让屏幕上的曲子和正在响的那首对不上。
            // 放在最后：它会把状态行写成新那首的「就绪 · …」，而那才是用户接下来要按的那一份
            // （「已停止」那句话悬浮层还在接着显示）。
            if (_pending is { } pending) ApplySong(pending.Song, pending.Playable);
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

        // 正在响的那一根：换音了才重画。100ms 一次，同一个音不必把那 38 格重算一遍
        // （而且细边本来也不会动）。
        if (_currentNote != _rangeNote) RefreshRange();

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
    /// <remarks>
    /// <b>「能不能按」的判据只有这一处</b>（73 号票）。从前装曲子那条路（<c>LoadSong</c>）自己
    /// 另写了一份 <c>IsEnabled = any</c>，漏了「没在演奏」—— 于是演奏中递一手曲子，
    /// 开始按钮和轨下拉会被重新点亮，而演奏还在响。装完曲子要开关这两颗控件，**调这个方法**。
    /// </remarks>
    private void SetRunning(bool running)
    {
        // 「有没有能弹的轨」只有一个判据：_playable 空不空；再叠一个「这会儿没在演奏」。
        // 两颗控件共用它 —— 抄第二份就是 73 号那个病。
        bool canPick = !running && _playable.Count > 0;

        StartButton.IsEnabled = canPick;
        StopButton.IsEnabled = running;
        TrackCombo.IsEnabled = canPick;
        TimingCombo.IsEnabled = !running;
        CountdownCombo.IsEnabled = !running;

        // 微调三颗：演奏期间锁死。这一场的事件表在按下开始那一刻就建好了，中途再挪半音
        // 只挪得动读数 —— 屏幕上说挪了，耳朵里那张旧表照发（见 OnFineTune 那段的理由）。
        FineMinus.IsEnabled = !running;
        FineZero.IsEnabled = !running;
        FinePlus.IsEnabled = !running;
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
