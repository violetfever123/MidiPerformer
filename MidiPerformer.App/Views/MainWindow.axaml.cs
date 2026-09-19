using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Editing;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>
/// 窗口一：编辑器外壳（卷帘 + 导航条 + 读数条 + 走带条）。
///
/// **只管布置与转发**：换算在 <see cref="PianoRollController"/>，画什么在
/// <see cref="PianoRollPresenter"/>，出声在 <see cref="PreviewPlayback"/>，
/// 改谱面在 <see cref="SongEditor"/>（外面罩着 <see cref="UndoableSongEditor"/> 记账）。
/// 所以这里没有 ViewModel 类 —— 那会是个只做转发的空壳（见 spec 的「Controller」那节）。
///
/// 它持着**当前这一份曲子**（<c>_song</c>），因为「换一份曲子」要把好几处一起翻新：
/// 控制器与所有轨重建、试听换谱、播放头按新的速度表换回同一个 tick、撤销按钮亮灭。
/// 那一整套在 <see cref="ApplySong"/> 里，是本切片的脊柱。
///
/// 能改谱面的入口只有三处，都从这里出去：速度框（回车）、移调步进器、撤销 / 重做。
///
/// **曲库（10）的活也在这儿收口。** <see cref="SongLibraryPanel"/> 自己**不动盘**，
/// 只把「点了哪一首 / 要改名 / 要删掉」喊上来，读写文件的是本窗口 —— 因为那几件事
/// 都会反过来影响窗口手上的状态（删掉的正好是当前这首怎么办？改完名曲名框要不要跟着变？），
/// 而面板不知道窗口手上有什么。
///
/// 演奏器那条走组装点给的工厂，本窗口不 new 那个窗。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>播放时把播放头放在屏幕的哪个位置：偏左约三分之一，右边留出前瞻（wireframe 标注 4）。</summary>
    private const double FollowFraction = 0.32;

    /// <summary>认得出的文件类型。midi 和 mid 都收 —— 导入导出两侧共用同一份，免得只改一边。</summary>
    private static readonly FilePickerFileType MidiFileType = new("MIDI 文件")
    {
        Patterns = new[] { "*.mid", "*.midi" }
    };

    private readonly TokenSource _tokens;
    private readonly PreviewPlayback _playback;
    private readonly Func<Window>? _performerFactory;
    private readonly SongLibrary? _library;
    private readonly List<TrackLaneView> _lanes = new();

    /// <summary>左边那条曲库。组装点没给曲库时（设计器、或将来某个不要曲库的壳）就是 null。</summary>
    private SongLibraryPanel? _libraryPanel;

    /// <summary>
    /// 编辑脊柱。撤销是装饰器加的能力，界面拿到的就是装饰器 ——
    /// 命令本身（<see cref="SongEditor"/>）一行都不知道有撤销这回事。
    ///
    /// 为什么在这儿 new：08 立脊柱的时候组装点还没把编辑命令递下来（那要动 App.axaml.cs，
    /// 是主协调者的合并点），而它俩都没有外部依赖、建起来是纯的。
    /// 它和 <see cref="PreviewPlayback"/> 一样是这一层的内部件，不是网关。
    /// </summary>
    private readonly UndoableSongEditor _editor = new(new SongEditor());

    private PianoRollController? _controller;

    /// <summary>当前这一份谱面。每次编辑换一份新的（不可变），换完走 <see cref="ApplySong"/>。</summary>
    private Song? _song;

    /// <summary>
    /// 手上这份在曲库里叫什么（= 文件名）。<c>null</c> = 还没进曲库
    /// （导入时取消了命名，或者从没存过）—— 那时候「保存」会先问一个名字。
    /// </summary>
    private string? _currentName;

    /// <summary>
    /// 曲名框里那个名字，也是「这首叫什么」的**显示**来源。
    ///
    /// 和 <see cref="_currentName"/> 分开，是因为有一类曲子只在手上、不在曲库里
    /// （导入时取消了命名）：它有名字可显示（就是导入的那个文件名），却没有曲库里的位置。
    /// 把两者合成一个的话，「名字框该显示什么」和「保存该写哪儿」就分不开了。
    /// </summary>
    private string _title = "";

    /// <summary>这份是从哪个 .mid 导入的，写进工程文件头。合并成一首、或从曲库打开的都可能是 null。</summary>
    private string? _importedFrom;

    /// <summary>
    /// 这份工程「动过没有」，跟着文件头走。
    ///
    /// <b>它是粘的</b>：一旦编辑过就永远是 true，撤销回初始状态也不会变回 false，存盘也不清。
    /// 它回答的是「这首我动过」，不是「有没有没保存的改动」—— 后者要靠 diff 才说得清，
    /// 而这个标记只要一个 bool（见 <see cref="ProjectHeader.Edited"/>）。
    /// </summary>
    private bool _edited;

    /// <summary>正在拖导航条 —— 这期间卷帘上的播放头红线要藏起来（wireframe 标注 3）。</summary>
    private bool _draggingNav;

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public MainWindow() : this(null!, null!, null!, null, null) { }

    /// <param name="tokens">自绘取色桥（卷帘和导航条不在 XAML 里，拿不到 DynamicResource）。</param>
    /// <param name="clock">墙上钟，喂给试听的时间积分。</param>
    /// <param name="sink">出声的出口（winmm）。</param>
    /// <param name="performerFactory">
    /// 「演奏器…」按下时去要那个独立窗口。给的是工厂不是现成的窗口：
    /// 演奏器一建出来就装低层键盘钩子，所以它必须到用户真要用的那一刻才存在。
    /// 复用与单例都在组装点里管，本窗口只管要、然后 Show。
    /// </param>
    /// <param name="library">
    /// 曲库。**目录由组装点拼好**（默认是 exe 旁边的 .\songs\）—— 曲库自己不猜自己在哪。
    /// 给 null 就整条曲库都不出现（保存 / 另存为也不亮）。
    /// </param>
    public MainWindow(
        TokenSource tokens, IClock clock, IAudioSink sink,
        Func<Window>? performerFactory, SongLibrary? library)
    {
        InitializeComponent();

        _tokens = tokens;
        _performerFactory = performerFactory;
        _library = library;
        _playback = new PreviewPlayback(sink, clock);

        BuildLibraryPanel();
        AllowImportByDrop();

        NavStrip.Tokens = _tokens;
        NavStrip.DragStarted += (_, _) =>
        {
            _draggingNav = true;
            RefreshView();
        };
        NavStrip.DragCompleted += (_, _) =>
        {
            _draggingNav = false;
            RefreshView();
        };
        NavStrip.SeekRequested += OnNavSeek;

        _playback.Frame += (_, _) => RefreshView();
        _playback.Finished += OnPlaybackFinished;

        // 提示语只写这一张真做得到的（定位、撤销），编辑那些归 09 —— 文案住在 Format 里，一处改处处改
        HintText.Text = Format.ReadoutHint;

        // 窗口改宽 = 每小节变宽（固定 4 小节，没有缩放），所以要按新的宽度重算场景
        SizeChanged += (_, _) => RefreshView();
        LanesHost.SizeChanged += (_, _) => RefreshView();

        // 隧道阶段接方向键与撤销 / 重做：先于任何控件拿到它。焦点在输入框里时让开（见 OnWindowKeyDown）
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        RefreshEditState();
        RefreshView();
    }

    // ==================== 导入 ====================

    /// <summary>「导入 MIDI…」—— 选文件，剩下的交给 <see cref="ImportFile"/>。</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 MIDI…",
            AllowMultiple = false,
            FileTypeFilter = new[] { MidiFileType }
        });

        if (files.Count == 0) return;

        string? path = files[0].TryGetLocalPath();
        if (path is null)
        {
            ShowError("这个位置读不到本机路径（云盘 / 网络位置），先把它存到本地再导入。");
            return;
        }

        await ImportFile(path);
    }

    /// <summary>
    /// 把窗口变成一个能接收拖放的落点。
    ///
    /// 拖进来的东西**不一定是文件**（可能是选中的一段文字），所以 DragOver 要把
    /// 「收不收」先说清楚：不说的话光标一直是个禁止符号，用户以为这窗口不吃拖放。
    /// </summary>
    private void AllowImportByDrop()
    {
        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = FirstMidiPath(e.DataTransfer) is null
                ? DragDropEffects.None
                : DragDropEffects.Copy);

        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (FirstMidiPath(e.DataTransfer) is not { } path) return;
            await ImportFile(path);
        });
    }

    /// <summary>
    /// 拖进来的东西里，第一个本机认得出的 MIDI 文件路径；没有就是 <c>null</c>。
    ///
    /// 只要一个：一次拖一整个文件夹那种不在这一条的射程里。
    /// 云盘 / 网络位置上的文件拿不到本机路径（<c>TryGetLocalPath</c> 返回 null），
    /// 那种也当作没有 —— 一律拒收，比收下来再报一句「读不到」干净。
    /// </summary>
    private static string? FirstMidiPath(IDataTransfer data)
    {
        // TryGetFiles 在没有文件时给 null（不是空数组），所以要自己兜一下
        foreach (var item in data.TryGetFiles() ?? Array.Empty<IStorageItem>())
        {
            if (item is not IStorageFile file) continue;

            string name = file.Name;
            if (!name.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".midi", StringComparison.OrdinalIgnoreCase)) continue;

            if (file.TryGetLocalPath() is { } path) return path;
        }

        return null;
    }

    /// <summary>
    /// 导入一个 .mid：读 → 命名 → 存进曲库 → 显示。
    ///
    /// 菜单和拖放走的是同一个它 —— 两条路各写一遍的话，早晚出现「拖进来的没进曲库」
    /// 这种只有一条路才有的毛病。
    ///
    /// 命名那一步**取消不等于失败**：曲子照样装上、照样能编辑导出，只是没进曲库
    /// （<see cref="_currentName"/> 保持 null，之后按保存会再问一次）。用户已经挑好文件了，
    /// 为一个可选的步骤把整件事丢掉不合理。
    /// </summary>
    private async Task ImportFile(string path)
    {
        Song song;
        try
        {
            song = MidiReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // MidiReader 抛的就是中文消息，原样报出来 —— 编一句更笼统的话只会把线索弄丢
            ShowError(ex.Message);
            return;
        }

        LoadSong(song, Path.GetFileNameWithoutExtension(path));
        // 刚 LoadSong 完，_edited 已经是 false、曲名还是空 —— 补上来路，剩下的交给 SaveTo。
        // 从前这里自己写了一遍文件、自己摆了一遍状态，和 SaveTo 是两份几乎一样的代码：
        // 结果就是这份少写了 _title（导入时起的名没同步过去，之后曲名框一回车会拿
        // MIDI 文件名去改曲库里的名字）。写两遍的东西早晚会不一样，所以直接走同一条路。
        _importedFrom = path;

        if (_library is not { } library) return;

        string? name = await AskNameForSaveAsync(
            library, "导入 MIDI", Path.GetFileNameWithoutExtension(path));

        if (name is null) return;

        SaveTo(library, name);
    }

    /// <summary>装一首曲子：重建卷帘、把事件表交给试听、把界面复位。</summary>
    private void LoadSong(Song song, string title)
    {
        // **先验 tick 装不装得下，再动任何状态。**
        //
        // 卷帘是 tick 轴，多长的 tick 都画得出来；但试听要把它换成秒，而 TempoMap.SecondsAt
        // 在 tick 换算出的微秒数超过 long 的十分之一时会抛「时间跨度太大」。tick 一旦到这个量级
        // （480 PPQ 下约 8.85e14，一个几十 MB 的 .mid 就够），_playback.Load 会当场抛。
        //
        // 为什么卡在这儿而不是各调用方各 catch 一遍：这是每首曲子进窗口的**唯一**入口
        // （打开文件 / 拖放 / 从曲库点开）。从前导入那条路把 LoadSong 包在 try 里挡着，
        // 拆出 ImportFile 之后那层就没了 —— 而「从曲库点开」这条路从来没挡过。
        // 一处挡下，三条路一起安全，也不会**装到一半**：卷帘已经换成新的、试听还是旧的，
        // 那种半截状态比压根不装更糟。
        //
        // 判据是 Song.TryMeasure（见那边的注释：为什么不直接读 TotalSeconds）。
        if (!song.TryMeasure(out _, out string? reason))
        {
            // 中文原因来自 TempoMap，原样报出来
            ShowError(reason!);
            return;
        }

        HideMessages();

        // 换一首曲子就得把撤销栈清掉：栈里装的是上一首曲子的 Song 引用，
        // 不清的话接着按撤销会把人送到另一首曲子的历史里去
        _editor.Reset();

        _song = song;
        // 「这份是哪来的、叫什么、动过没有」三件都随曲子一起换掉。调用方在这之后
        // 按自己的来路覆盖：导入的会补 _importedFrom 与 _edited=false，从曲库打开的会补三者。
        _currentName = null;
        _importedFrom = null;
        _edited = false;
        _title = title;

        // 换曲子一律重建轨控件：轨数碰巧一样时「就地重挂」看着也能用，但那是**另一首曲子的轨**
        // 接着用上一首的控件 —— 折叠、改名框这些控件上的状态会跨曲子漏过去。
        // 编辑那条路才是「同一首曲子的新一份」，两者不是一回事
        SyncLanes(rebuildAll: true);
        // 换曲子这一路折叠一律是空的（SyncLanes 那条 rebuildAll 的分支刚说过为什么），
        // 照旧现问一次：哪天那条规矩变了，这里不会悄悄漏掉
        _playback.Load(song, MutedTracks());

        SongNameBox.Text = title;
        EmptyHint.IsVisible = false;
        JumpBox.Text = "1";

        // 走带条跟着新曲子回位。**就这一处规矩**（RefreshTransport），
        // 手写 PlayButton/StopButton 那两行的地方从前有三处，改一处漏两处
        RefreshTransport();
        JumpBox.IsEnabled = true;
        // 有谱面就写得出，哪怕一个音都没有 —— 速度表和分辨率也值得留下来，
        // 所以这条的判据是「装上了曲子」，不是「有轨」
        ExportButton.IsEnabled = true;

        // 换曲子了：悬停那个音说的是上一份谱面，清掉。清完读数自己回落到选中（多半也是空的）
        ShowHover(null);
        RefreshEditState();
        RefreshLibrary(null);

        // 这一趟多半算不出场景（控件刚建出来，宽度还是 0），但位置读数、导航条这些要它。
        // 卷帘自己会在尺寸落定那一帧补上 —— 那条线挂在 TrackLaneView 的 Roll.SizeChanged 上
        RefreshView();
    }

    /// <summary>
    /// 照着当前这份曲子把控制器和所有轨**同步**一遍。
    ///
    /// <b>控制器每次都得换新的</b>，不能就地改：它是按曲子建出来的一次性对象
    /// （音域、灰显标记、每小节音符数都算好缓存着了），移调会同时改掉音域和灰显，
    /// 没有哪一处能「顺手更新一下」。
    ///
    /// <b>但控件树不必跟着拆。</b>轨数没变就地重挂（<see cref="TrackLaneView.Rebind"/>）——
    /// 从前的做法是无条件 <c>Children.Clear()</c> + 全部新建，代价是用户直接看得见的：
    /// 内容高度掉到 0 的那一瞬间 <c>ScrollViewer</c> 把滚动位置夹回顶部（编辑一下就被弹回第 1 小节），
    /// 新控件的卷帘当帧量不出宽度、算不出场景，要等一次谁也不知道什么时候会来的
    /// <c>LanesHost.SizeChanged</c> 才画得出来 —— 轨数不变时那一趟根本不会来，
    /// 于是「编辑一下，音轨就消失了」。就地重挂这两样都没有：控件还是那些控件，
    /// 滚动位置、焦点、改名框、删轨那一问全都还在。
    ///
    /// 轨数变了（删了一条、或者撤销把它拿回来）只能重建 —— 控件和数据是一对一的，
    /// 多一条少一条没有「就地」可言。那一路由 <see cref="TrackLaneView"/> 自己挂在
    /// 卷帘尺寸上的重画兜住：布局一落定就补一帧，不会再空着。
    /// </summary>
    /// <param name="rebuildAll">
    /// 强制全部重建。换一首曲子时用 —— 轨数碰巧一样时「就地重挂」是拿**另一首曲子的轨**
    /// 接着用上一首的控件，控件上的那些状态（折叠、开着没提交的改名框）会跨曲子漏过去。
    /// 「同一首曲子的新一份」（编辑、撤销）才走就地重挂那一支。
    /// </param>
    private void SyncLanes(bool rebuildAll = false)
    {
        if (_song is not { } song) return;

        // 聚焦轨跟**那条轨**走，不跟下标走 —— 和折叠是同一条理由，而且是同一个坑：
        // 删掉第 0 条之后下标整体前移，按下标带会把高亮挪到别人身上。
        // 必须在换控制器之前抄下来：控制器一换，旧下标当场作废。
        // 换一首曲子（rebuildAll）一律从头发 —— 控件上的状态说的是**这一首**里的那一条轨。
        var focused = rebuildAll ? null : FocusedIdentity();

        _controller = new PianoRollController(song);

        if (!rebuildAll && _lanes.Count == song.Tracks.Count)
        {
            for (int i = 0; i < _lanes.Count; i++) _lanes[i].Rebind(_controller, i);
            // 轨数一样就是那几条轨、同一个次序，身份换算回来还是同一个下标；
            // 放回去这一步不能省 —— 新控制器自己的聚焦是 0
            _controller.SetFocusedTrack(FocusIndex(focused));
            return;
        }

        // 轨数变了只能重建控件，但**折叠是用户对某条轨的标记，不该被一次删轨顺手抹掉**
        // （撤销把那条轨拿回来时尤其明显：收起来的那几条自己全弹开了）。
        // 按轨的身份记，不按下标 —— 删掉第 0 条之后下标整体前移，按下标带会把折叠挪到别人身上。
        // 必须在 Clear 之前抄下来：下面那一刻 _lanes 就空了。
        //
        // **换一首曲子（rebuildAll）一律不带。** 折叠现在不只是「先不看它」，收起来的轨
        // 在试听里是不出声的（见 MutedTracks）。两首曲子的轨撞上同一个 (轨块, 声道) 是常事，
        // 带过去就成了「打开一首新曲子，某条轨莫名其妙是哑的」—— 这正是 rebuildAll 存在的理由：
        // 控件上的状态说的是**这一首**里的那一条轨，换一首就该从头开始。
        var collapsed = new HashSet<(int, int)>();
        if (!rebuildAll)
            foreach (var lane in _lanes)
                if (lane.IsCollapsed) collapsed.Add(lane.Identity);

        LanesHost.Children.Clear();
        _lanes.Clear();
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var lane = new TrackLaneView(_controller, i, _tokens);
            lane.SetCollapsed(collapsed.Contains(lane.Identity));
            lane.HoverChanged += OnLaneHover;
            lane.TransposeRequested += OnTransposeRequested;
            lane.ProgramRequested += OnProgramRequested;
            lane.NotesMoved += OnNotesMoved;
            lane.NoteResized += OnNoteResized;
            lane.SelectionChanged += OnLaneSelectionChanged;
            lane.FocusChanged += OnLaneFocusChanged;
            lane.CollapseChanged += OnLaneCollapseChanged;
            lane.RenameRequested += OnTrackRenameRequested;
            lane.DeleteRequested += OnTrackDeleteRequested;
            lane.CutRangeRequested += OnTrackCutRequested;
            _lanes.Add(lane);
            LanesHost.Children.Add(lane);
        }

        _controller.SetFocusedTrack(FocusIndex(focused));
    }

    /// <summary>
    /// 此刻聚焦的那条轨的**身份**。还没建控制器、或者下标已经越界时给 null（= 从头发）。
    ///
    /// 必须在 <c>_controller</c> 换成新的之前调：它读的是**旧**控制器手里那份曲子
    /// （所以删轨之后旧下标仍然读得通，读出来的是删之前那条轨的身份）。
    /// </summary>
    private (int Chunk, int Channel)? FocusedIdentity()
    {
        if (_controller is not { } controller) return null;

        int index = controller.FocusedTrack;
        return index >= 0 && index < _lanes.Count ? _lanes[index].Identity : null;
    }

    /// <summary>
    /// 身份 → 它**现在**在第几号。找不到（那条轨被删了、撤销还没把它拿回来）就落到
    /// 第一条没收起来的轨上 —— 高亮总得落在某一条上。
    ///
    /// 必须在轨控件重建**之后**调：它读的是新的 <c>_lanes</c>。
    /// </summary>
    private int FocusIndex((int Chunk, int Channel)? identity)
    {
        if (identity is { } wanted)
            for (int i = 0; i < _lanes.Count; i++)
                if (_lanes[i].Identity == wanted) return i;

        return PianoRollController.FirstExpanded(CollapsedFlags());
    }

    /// <summary>
    /// 每条轨收没收起，按**下标**排一张表（控制器要的正是这个形状）。
    /// 和 <see cref="MutedTracks"/> 一样**每次现问一次控件**，不在窗口里另存一份折叠状态。
    /// </summary>
    private bool[] CollapsedFlags()
    {
        var flags = new bool[_lanes.Count];
        for (int i = 0; i < _lanes.Count; i++) flags[i] = _lanes[i].IsCollapsed;
        return flags;
    }

    /// <summary>
    /// 此刻哪几条轨在试听里不发声：**收起来的那几条**。
    ///
    /// 认轨用的是 <see cref="TrackLaneView.Identity"/> 那一对 <c>(轨块, 声道)</c>，不是下标 ——
    /// 和重建时把折叠带过去取的是同一套：删掉第 0 条之后下标整体前移，按下标算会静音到别人头上。
    ///
    /// 每次**现问一次**控件（不在窗口里另存一份折叠状态）：状态只有控件那一处，
    /// 抄一份出来就有两处要跟着一起改，而它们迟早会不一致。问一趟是十来条轨的循环，不心疼。
    /// </summary>
    private IReadOnlySet<(int TrackIndex, int Channel)> MutedTracks()
    {
        var muted = new HashSet<(int, int)>();
        foreach (var lane in _lanes)
            if (lane.IsCollapsed) muted.Add(lane.Identity);
        return muted;
    }

    /// <summary>
    /// 某条轨收 / 放了：试听那张表跟着重排一遍。
    ///
    /// 正在播的话是**接着放**，只有那一条不响（见 <see cref="PreviewPlayback.SetMutedTracks"/>）——
    /// 折叠一条正在听的轨不该把整遍听下来打断。
    /// </summary>
    private void OnLaneCollapseChanged(object? sender, EventArgs e)
        => _playback.SetMutedTracks(MutedTracks());

    // ==================== 曲库 ====================

    /// <summary>
    /// 把曲库那条装进左边那一格。
    ///
    /// 面板是**代码建**的（它要曲库和取色桥两样构造参数，XAML 只能调无参构造）。
    /// 这里只挂事件、不碰盘 —— 点开、改名、删除的落地全在本窗口，理由见类注释。
    /// </summary>
    private void BuildLibraryPanel()
    {
        if (_library is not { } library) return;

        _libraryPanel = new SongLibraryPanel(library, _tokens);
        _libraryPanel.OpenRequested += OnOpenLibrarySong;
        _libraryPanel.RenameRequested += OnRenameRequested;
        _libraryPanel.DeleteRequested += OnDeleteRequested;

        LibraryHost.Content = _libraryPanel;
    }

    /// <summary>
    /// 重新列一遍曲库，并把某首标成「正开着」。
    ///
    /// 曲库本身**不会喊「我变了」**（它就是个目录），所以谁动了盘谁负责喊这一声：
    /// 导入完、存完、改完名、删完，四处。
    /// </summary>
    private void RefreshLibrary(string? current)
    {
        if (_libraryPanel is null) return;

        _libraryPanel.Refresh();
        _libraryPanel.MarkCurrent(current);
    }

    /// <summary>曲库列表里点开了某一首：把工程读出来装上。</summary>
    private void OnOpenLibrarySong(object? sender, string name)
    {
        if (_library is not { } library) return;

        try
        {
            var (header, song) = SongProjectFile.LoadProject(library.PathOf(name));

            LoadSong(song, name);
            // LoadSong 把这三个都清空了（它不知道新来的是哪一份），所以在这儿补上
            _currentName = name;
            _title = name;
            _importedFrom = header.ImportedFrom;
            _edited = header.Edited;

            // 只挪高亮，**不再列一遍**：LoadSong 里已经 RefreshLibrary(null) 过一趟了，
            // 而列表的内容这一路根本没变（我们只是读了一个文件）。再列一遍等于把曲库里
            // 每一首的工程文件重新读出来解析一次 —— 200 首的曲库上就是白白卡一下。
            _libraryPanel?.MarkCurrent(name);
        }
        catch (InvalidDataException ex)
        {
            // 读不出来的工程：报一句中文，**不动**手上正开着的那一份
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 「保存」：已经有曲名就写回那一首，还没有（导入时没命名、或者刚从曲库删掉）就先问一个。
    /// </summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_library is not { } library) return;

        if (_currentName is not { } name)
        {
            await SaveAsAsync(library);
            return;
        }

        SaveTo(library, name);
    }

    /// <summary>「另存为…」：问一个新名字存进去。<b>不动原来那一首</b> —— 那正是「另存为一首」的意思。</summary>
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (_library is not { } library) return;
        await SaveAsAsync(library);
    }

    private async Task SaveAsAsync(SongLibrary library)
    {
        string? name = await AskNameForSaveAsync(library, "另存为", _currentName ?? _title);
        if (name is null) return;

        SaveTo(library, name);
    }

    /// <summary>
    /// 问一个要写进曲库的名字，**撞名时先问一句**。
    ///
    /// <c>SongLibrary.Write</c> 是覆盖语义 —— 那正是「保存」的意思。但「保存」和「另存为 / 导入时起名」
    /// 不是一回事：后两者里手一滑打出一个已有的名字，就会拿手上这份把**另一首**曲子悄悄换掉，
    /// 而且是覆盖写，撤不回来。「改名」那条路是拦着的（<c>Rename</c> 撞名直接报错），
    /// 没道理「另存为」反而更松。
    ///
    /// 放行的两种：名字就是**当前这首**（那是保存自己，用户按「保存」要的就是它），
    /// 或者曲库里没这个名字。
    ///
    /// 用户说「不覆盖」就带着刚打的名字再问一次，而不是把他退回工具栏 ——
    /// 他本来就在做「起个名字」这件事，让他接着改一个字就行。
    /// </summary>
    /// <returns>可以写的名字；用户取消 = <c>null</c>。</returns>
    private async Task<string?> AskNameForSaveAsync(SongLibrary library, string title, string initial)
    {
        while (true)
        {
            string? name = await Dialogs.AskNameAsync(this, title, initial);
            if (name is null) return null;

            if (name == _currentName || !library.Contains(name)) return name;

            bool overwrite = await Dialogs.ConfirmAsync(
                this,
                "覆盖这首曲子",
                $"曲库里已经有一首「{name}」了。继续的话它会被手上这份替掉，撤不回来。",
                "覆盖");

            if (overwrite) return name;

            initial = name;   // 名字留在框里，改一个字再试
        }
    }

    /// <summary>
    /// 把手上这份写进曲库的某个名字。
    ///
    /// 写出去的是**此刻手上的那一份**（含刚做完、还没撤销的编辑），不是屏幕：
    /// <c>Track.Transpose</c>、卷帘视口、播放头都不进文件。
    /// </summary>
    private void SaveTo(SongLibrary library, string name)
    {
        if (_song is not { } song) return;

        try
        {
            library.Write(name, SongProjectFile.WriteProject(song, new ProjectHeader(
                SongProjectFile.ProjectVersion, name, _edited, _importedFrom)));
        }
        catch (InvalidDataException ex)
        {
            ShowError(ex.Message);
            return;
        }

        _currentName = name;
        _title = name;
        SongNameBox.Text = name;
        RefreshLibrary(name);
        ShowNotice($"「{name}」已存进曲库：{library.Directory}");
    }

    /// <summary>曲库那条上要改名（点「改名」或按 F2）：问一个新名字，落到盘上。</summary>
    private async void OnRenameRequested(object? sender, string oldName)
    {
        if (_library is not { } library) return;

        string? name = await Dialogs.AskNameAsync(this, "改曲名", oldName);
        if (name is null) return;

        RenameTo(library, oldName, name);
    }

    /// <summary>
    /// 改名 —— 就是把文件换个名字，内容一个字节都不碰。
    ///
    /// 改的要是**当前正开着的那一首**，曲名框得跟着换：它显示的就是这个名字，
    /// 不改的话界面上会同时存在两个名字（列表里新的、框里旧的），按保存还会存回一个已经不存在的名字。
    /// </summary>
    private void RenameTo(SongLibrary library, string oldName, string newName)
    {
        try
        {
            library.Rename(oldName, newName);
        }
        catch (InvalidDataException ex)
        {
            ShowError(ex.Message);
            SongNameBox.Text = _title;   // 把框里那个没落地的名字退回原名
            return;
        }

        if (_currentName == oldName)
        {
            _currentName = newName;
            _title = newName;
            SongNameBox.Text = newName;
        }

        RefreshLibrary(_currentName);
        ShowNotice($"「{oldName}」改成了「{newName}」。");
    }

    /// <summary>
    /// 曲库那条上确认要删（判据：面板已经问过一句了，这里收到就是真的要删）。
    ///
    /// 删掉的要是**当前正开着的那一首**，手上这份**留着**：它还在内存里、可能还有没存过的编辑。
    /// 把它一起清掉，等于让「删掉列表里那一行」顺手动用户正在编辑的东西 ——
    /// 那比留着更让人措手不及。留着的代价只是跟曲库脱了钩，按保存会重新问个名字。
    /// </summary>
    private void OnDeleteRequested(object? sender, string name)
    {
        if (_library is not { } library) return;

        try
        {
            library.Delete(name);
        }
        catch (InvalidDataException ex)
        {
            ShowError(ex.Message);
            return;
        }

        if (_currentName == name)
        {
            _currentName = null;
            ShowNotice($"「{name}」已从曲库删掉。手上这份还在，按「保存」可以再存回去。");
        }
        else
        {
            ShowNotice($"「{name}」已从曲库删掉。");
        }

        RefreshLibrary(_currentName);
    }

    /// <summary>
    /// 曲名框：回车改名。
    ///
    /// 这一格显示的就是曲名，所以回车 = 「这首叫这个」。还没进曲库的（导入时取消了命名）
    /// 回车就顺势存进去 —— 不然这一格是个打完回车什么都没发生的死框。
    ///
    /// 不合法（空的、全是文件名里不能用的字符）就报一句中文、把框退回原名：
    /// 判据和曲库是同一个 <see cref="SongLibrary.IsUsableName"/>，两处各写一套的话，
    /// 早晚出现「界面让过、曲库不让存」。
    /// </summary>
    private void OnSongNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _library is not { } library || _song is null) return;
        e.Handled = true;

        string typed = SongNameBox.Text?.Trim() ?? "";
        ReleaseEditFocus();

        if (!SongLibrary.IsUsableName(typed))
        {
            ShowError($"「{typed}」不能当曲名：曲名就是文件名，不能是空的，也不能只有文件名里用不了的字符。");
            SongNameBox.Text = _title;
            return;
        }

        string name = SongLibrary.Sanitize(typed);

        if (_currentName is not { } oldName)
        {
            // 还没进曲库：起了名就等于存进去。名字已经是别人的就拦下 ——
            // 往曲名框里打一个已有的名字，本意多半是「这首叫这个」，不是「把那一首换掉」。
            // 这一格是回车就走的，不适合弹一个确认框拦一下；报一句让他改更顺。
            // 真要覆盖，走「另存为…」，那儿会问一句。
            if (library.Contains(name))
            {
                ShowError($"曲库里已经有一首「{name}」了，换个名字。要覆盖它请用「另存为…」，那里会问一句。");
                SongNameBox.Text = _title;
                return;
            }

            SaveTo(library, name);
            return;
        }

        if (oldName == name)
        {
            HideMessages();               // 名字没变，什么都不用做，但别留着一句过期的话
            return;
        }

        RenameTo(library, oldName, name);
    }

    // ==================== 编辑脊柱 ====================

    /// <summary>
    /// 把一份编辑过的曲子换上，并把界面**按原样**恢复。整个编辑脊柱就落在这一个方法里。
    ///
    /// 三件事必须一起做，少一件用户就会看见「改一下就被弹走了」：
    /// <list type="number">
    /// <item>视口与选中按原样放回去 —— 改的是速度或移调，不该把人弹回第 1 小节、也不该丢掉选中。
    /// 选中集**抄的是坐标本身**：坐标按身份寻址（见 <see cref="NoteRef"/>），
    /// 编辑换的是内容不是身份，于是同一串坐标在新控制器上照样指着同一批音。</item>
    /// <item>播放头用**新的**速度表把原来那个 tick 换算成秒再 Seek 回去。tick 是同一处，
    /// 秒数变了（这正是改速度的意思）；用旧秒数的话改完播放头会跳。</item>
    /// <item>试听换谱 —— <c>Load</c> 会先把正在响的音全松掉，所以编辑顺手停掉播放，
    /// 这比「边放边改」安全（改到一半的谱子不该继续发出去）。</item>
    /// </list>
    ///
    /// <b>「改没改」比引用</b>：跟装饰器同一条判据。一次「改成和现在一样」不该把上面这些全部重置一遍。
    ///
    /// <b>从前这里还有第二张嘴</b>：一条 <c>selectionAfter</c> 参数，收的是「编辑之后该选中哪几个音」
    /// 的**值**（轨 + 音符内容），由 <c>CaptureSelection</c> / <c>SelectionAfter*</c> 那一套算出来，
    /// 再由 <c>RestoreSelection</c> 拿值去新曲子里重新找下标 —— 因为 <see cref="NoteRef"/> 从前是
    /// 下标，而挪音符 / 改时值都会重排数组（见 <c>Track.WithNotes</c>）。31 号工单把那套按值的镜像
    /// 整个删了（坐标改成按身份寻址之后就没有「重新认一遍」这件事了），只剩下面这一种情形：
    /// <b>命令自己改了「选中谁」</b>（删音符要落到邻居上），那时当然得把新的那一组交回来。
    /// </summary>
    /// <param name="selectionAfter">
    /// 编辑**之后**该选中的那组音。不传（null）= 手上这一串原样留着 —— 那是绝大多数命令
    /// （挪、拉、改速度、改移调、撤销、重做…），它们的坐标在新曲子上仍然成立。
    ///
    /// 传了就是「换掉」：只有删音符（落点换成邻居）和删轨 / 剪一段（坐标的轨那一半当场作废）
    /// 这几种会传。**传进来的坐标必须是照着新曲子算的**（删音符那一路在编辑之后才拿邻居）。
    /// </param>
    private void ApplySong(Song edited, IReadOnlyList<NoteRef>? selectionAfter = null)
    {
        if (ReferenceEquals(edited, _song)) return;

        long playheadTick = _playback.PlayheadTick;
        long viewStartTick = _controller?.ViewStartTick ?? 0;
        // 必须在 SyncLanes 之前抄：下面换控制器，旧的那个当场作废。
        // 抄下来的**不是它指向的音，是坐标本身** —— 这才是不必重新认音的原因
        var selection = selectionAfter ?? _controller?.SelectedNotes.ToArray() ?? Array.Empty<NoteRef>();

        _song = edited;
        // 粘性标记：动过就是动过。撤销回原样也不清它（见 _edited 的说明），存盘也不清
        _edited = true;
        SyncLanes();

        // 视口照旧有效：小节刻度不受任何一条编辑命令影响（改速度只动速度表，其余只动音符），
        // 控制器自己的 SetViewStart 还会夹一次，曲子变短也不会越界
        _controller?.SetViewStart(viewStartTick);
        // 选中集放回**新**控制器上：坐标是身份，原样交回去就行。
        // 认不出的（音被删了、轨被删了）由 SetSelection 丢掉 —— 它不抛，那不是错误，是「它不在了」
        _controller?.SetSelection(selection);

        // 收起来的轨照旧不出声 —— 重摊这张表的名单从控件现问（见 MutedTracks）
        _playback.Load(edited, MutedTracks());
        _playback.SeekSeconds(edited.TempoMap.SecondsAt(playheadTick));
        // 试听被换谱顺手停了（Load 会先松开所有正在响的音），走带条的亮灭跟着回位。
        // **走 RefreshTransport 而不是在这儿手写两行** —— 从前就是手写的，
        // 于是「■ 该不该灰」这条规矩散在三个地方（换曲子、编辑、播放），
        // 改速度这一路（也走这里）就把 ■ 弄灰了，而屏幕上没有任何东西解释为什么。
        RefreshTransport();

        // 悬停那个音，说的可能是**刚被这条命令改掉（或者删掉）的那个音**，
        // 而读数条只跟着鼠标动才更新 —— 鼠标这会儿多半正压在那个按钮上，
        // 指针不动的话它会一直挂着一条已经作废的读数。清掉，比留一个错的强。
        // 清掉之后读数**回落到选中**：编辑走的多半是「动着选中那个音」的路，
        // 于是这一格正好接着显示它，而不是变空
        ShowHover(null);
        RefreshEditState();
        RefreshView();
    }

    /// <summary>撤销 / 重做、保存 / 另存为、速度框、曲名框这一组。换曲子和每次编辑之后调它。</summary>
    private void RefreshEditState()
    {
        UndoButton.IsEnabled = _editor.CanUndo;
        RedoButton.IsEnabled = _editor.CanRedo;
        BpmBox.IsEnabled = _song is not null;
        SongNameBox.IsEnabled = _song is not null;
        // 没有曲库就存不了（组装点没给），灰着比按了没反应诚实
        SaveButton.IsEnabled = _song is not null && _library is not null;
        SaveAsButton.IsEnabled = SaveButton.IsEnabled;
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object? sender, RoutedEventArgs e) => Redo();

    /// <summary>撤上一步。没得撤就什么都不做（按钮本来就是灰的，快捷键那条路要自己挡住）。</summary>
    private void Undo()
    {
        if (_song is null || _editor.Undo() is not { } song) return;
        ApplySong(song);
    }

    private void Redo()
    {
        if (_song is null || _editor.Redo() is not { } song) return;
        ApplySong(song);
    }

    /// <summary>
    /// 轨道头上的移调步进器被按了：参数是新的**绝对半音数**。
    ///
    /// 移调只换 <c>Track.Transpose</c>，音符一个字节都不动 —— 所以这条路和改 BPM 共用同一套重绘。
    /// </summary>
    private void OnTransposeRequested(object? sender, int semitones)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.SetTranspose(song, lane.TrackIndex, semitones));
    }

    /// <summary>
    /// 轨道头上的音色下拉挑了新的一号。
    ///
    /// **只影响试听**：音色是「我想听成什么样」，发给游戏时永远是口琴那套键位。
    /// 所以它和移调、改速度共用同一套重绘 —— 谱面一个字节都不动，
    /// 走 <see cref="ApplySong"/> 要换的只有试听那张表（<c>PreviewPlayback.Load</c>）。
    /// </summary>
    private void OnProgramRequested(object? sender, int program)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.SetProgram(song, lane.TrackIndex, program));
    }

    // ==================== 卷帘编辑：事件 → 命令 ====================

    /// <summary>
    /// 卷帘上拖完一组音符（方向键微调也走这一条）。
    ///
    /// 位移是**已经夹过**的：<c>PianoRollLane</c> 在发事件之前夹一次（预览不能画到命令去不了的地方），
    /// <see cref="NudgeNotes"/> 在调命令之前夹一次。夹取这一步照旧要（预览和微调算出来的位置得真能落下去），
    /// 但从前那个「不夹就会把选中集弄丢」的理由没有了 —— 那是按值认音那套镜像的毛病（31 号工单）。
    ///
    /// <b>选中集不用管</b>：命令换的是内容，坐标指着的那批音一个都没换号，原样留着就是对的。
    /// </summary>
    private void OnNotesMoved(object? sender, NoteMoveRequest request)
    {
        if (_song is not { } song) return;
        ApplySong(_editor.MoveNotes(song, request.Notes, request.DeltaTicks, request.DeltaPitch));
    }

    /// <summary>
    /// 卷帘上拖完某条边。请求里是**绝对**的起点与时值，不是增量。
    ///
    /// 被拉的那个音**身份不变**（<c>SetNoteSpan</c> 只 <c>with</c> 起点和时值），
    /// 所以选中集照旧不用管：哪怕它被拉得越过了邻居、在数组里换了位置，坐标还是指着它。
    /// </summary>
    private void OnNoteResized(object? sender, NoteResizeRequest request)
    {
        if (_song is not { } song) return;
        ApplySong(_editor.SetNoteSpan(song, request.Note, request.StartTick, request.LengthTicks));
    }

    /// <summary>
    /// <c>Delete</c> / <c>Backspace</c>：把当前选中的音整批删掉（一次调用 = 撤销栈上一格）。
    ///
    /// <b>删完选中落到时间上最近的邻居，不清空</b> —— 连续删谱时手不用重新找位置。
    /// 落的规则：拿被删那组里**最靠右**的那个音当基准（「从删掉的那一段末尾接着往下」），
    /// 先找它右边最近的一个（<c>StartTick</c> 严格大于基准）；右边没有了就落回左边最近的一个。
    /// 两边都没有（这条轨被删空了）就是空选中 —— 没地方可落，空着比指一个别的轨的音诚实。
    ///
    /// 基准取**最靠右**那个而不是最靠左：选中集可以是不挨着的（Shift + 点能点上相隔很远的两个），
    /// 取最靠左的话，落点会掉进两次点击中间的缝里 —— 用户按着 Delete 想「接着往后删」，
    /// 手却退回去了。取最靠右才是「继续往下走」。
    ///
    /// 基准落在**哪条轨**也由它定（那个音在哪条轨就落回哪条轨），不跟聚焦轨走：
    /// 选中集可以横跨两条轨（Shift + 点），而「刚删掉的东西在哪儿」比「焦点在哪儿」
    /// 更贴近用户此刻在看的地方。多轨同时删时只管一条 —— 落点只有一个。
    ///
    /// <b>只有这条路要显式交一份新的选中集</b>：被删的那几个音连身份一起没了，
    /// 「原样留着」留住的是几个指向空处的坐标（<c>SetSelection</c> 会把它们丢掉，
    /// 于是选中集空掉）。落点是**编辑之后**才算出来的——在新曲子上找邻居。
    /// </summary>
    private void DeleteSelection()
    {
        if (_song is not { } song || _controller is not { } controller) return;

        // 先抄下来：下面换曲子之后控制器里的那一串就作废了
        var doomed = controller.SelectedNotes.ToArray();
        if (doomed.Length == 0) return;

        // 基准：最靠右的那个被删音（它所在的轨 + 它的起点）
        int track = doomed[0].Track;
        long edge = long.MinValue;
        foreach (var reference in doomed)
        {
            if (NoteAt(song, reference) is not { } note) continue;
            if (note.StartTick >= edge) { edge = note.StartTick; track = reference.Track; }
        }
        if (edge == long.MinValue) return;   // 一个都认不出来（坐标全过期），那就不删

        var edited = _editor.DeleteNotes(song, doomed);
        ApplySong(edited, NeighbourAfterDelete(edited, track, edge));
    }

    /// <summary>
    /// 删完之后选中该落到哪儿：<paramref name="track"/> 上起点**严格大于** <paramref name="edge"/>
    /// 的第一个音；没有就退回起点**小于等于**它的最后一个（也就是它左边最近的那个）。
    /// 那条轨空了就给空表。
    ///
    /// 返回的是**坐标**（身份，<see cref="NoteRef"/>）：调用方要把它交给
    /// <see cref="ApplySong"/> 当「编辑之后该选中什么」，而在那份**新**曲子上，
    /// 邻居的位置是现算的（音符按起点升序，<c>Track.WithNotes</c> 保证），身份是现取的。
    ///
    /// 起点**严格大于**而不是大于等于：删掉的那一段里可能还有没被选中的音留在原地，
    /// 用「大于等于」会把其中一个当成右邻居 —— 那是往后删的时候手突然不动了。
    /// </summary>
    private static List<NoteRef> NeighbourAfterDelete(Song song, int track, long edge)
    {
        if (track < 0 || track >= song.Tracks.Count) return new List<NoteRef>();

        var notes = song.Tracks[track].Notes;
        for (int i = 0; i < notes.Count; i++)
            if (notes[i].StartTick > edge) return new List<NoteRef> { new(track, notes[i].Id) };

        // 右边没有了：退回左边最近的一个。音符按起点升序（Track.WithNotes 保证），所以是最后一个
        return notes.Count > 0
            ? new List<NoteRef> { new(track, notes[^1].Id) }
            : new List<NoteRef>();
    }

    /// <summary>
    /// 那份曲子里的这个音；按**身份**找（见 <see cref="NoteRef"/>），
    /// 认不出来（轨下标越界、这条轨上没有这个号）时给 null。
    ///
    /// 只剩删音符那一条路用它（要拿被删那组里最靠右那个的起点当落点基准）。
    /// 从前它是整套按值镜像的一个零件 —— <c>CaptureSelection</c>、<c>SelectionAfterMove</c>、
    /// <c>SelectionAfterResize</c>、<c>RestoreSelection</c> 和那个 <c>SelectedNote</c> 记录
    /// 都跟着那套一起删了（31 号工单）。删掉的是「拿内容去重新认音」这件事；
    /// 「按一个坐标去取那个音」还得留着，而且现在是**精确**的：内容一模一样的两个音也分得开。
    /// 扫一遍而不是建索引表：一次删除手势里只走几十遍，建表更贵（而且表会过期）。
    /// </summary>
    private static Note? NoteAt(Song song, NoteRef reference)
    {
        if (reference.Track < 0 || reference.Track >= song.Tracks.Count) return null;

        foreach (var note in song.Tracks[reference.Track].Notes)
            if (note.Id == reference.Id) return note;

        return null;
    }

    private void OnTrackRenameRequested(object? sender, string name)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.RenameTrack(song, lane.TrackIndex, name));
    }

    /// <summary>
    /// 删掉一整条轨（轨头上那个二次确认已经按过了）。
    ///
    /// 删完**明确清空选中**，而且这一次清空是**必须的**，不是随手：<see cref="NoteRef"/> 里
    /// 轨那一半是**下标**，<see cref="Song.Tracks"/> 删掉一条之后剩下的整体前移 ——
    /// 手上那串坐标会被解读成「挪了一条轨之后的那个位置」。更坏的是它**不会**认不出来：
    /// 身份是从 1 开始按轨连号发的（见 <see cref="NoteIdentity"/>），换一条轨照样能撞上一个号，
    /// 于是 <c>SetSelection</c> 那道「认不出就丢掉」根本拦不住 —— 用户会看到选中莫名其妙
    /// 落在别条轨的某个音上。（这一条是身份寻址剩下的代价，写在 <see cref="NoteRef"/> 上。）
    /// </summary>
    private void OnTrackDeleteRequested(object? sender, EventArgs e)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.DeleteTrack(song, lane.TrackIndex), Array.Empty<NoteRef>());
    }

    /// <summary>
    /// 抽掉这条轨上的一段（轨道头上两个小节号填好了、预览那一行也看过了）。
    ///
    /// 传进来的已经是 tick：小节 → tick 的换算在控件里做完了，
    /// 靠的是控制器手里那份速度表算出来的小节宽（命令层没有「小节」这个概念）。
    ///
    /// 抽完**明确清空选中**，理由和删轨不一样，说清楚：被剪断的音会拿到**新身份**
    /// （见 <c>ISongEditor.CutRange</c>：剪出来的两截是新音），被前移的音则**保留身份**——
    /// 于是「原样留着选中集」的结果是**一半对一半错**：被前移的那个还选着，
    /// 被剪掉的那一截已经认不出来了（会被丢掉）。选中集忽然缩水一半比清空更难解释，
    /// 而且这一刀本来就是把这一段整个拿走，清掉是更干脆的答复。
    /// </summary>
    private void OnTrackCutRequested(object? sender, CutRangeRequest request)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(
            _editor.CutRange(song, lane.TrackIndex, request.StartTick, request.EndTick),
            Array.Empty<NoteRef>());
    }

    /// <summary>
    /// 卷帘上的选中变了。
    ///
    /// 选中集是**全局**的（一个控制器管所有轨），所以别的轨的高亮也得跟着变 —— 整窗重画一遍。
    /// </summary>
    private void OnLaneSelectionChanged(object? sender, IReadOnlyList<NoteRef> selected)
    {
        RefreshReadout();
        RefreshView();
    }

    /// <summary>
    /// 卷帘上按了一下，焦点轨跟到那一条去了。
    ///
    /// 那一头自己已经重画过（<see cref="TrackLaneView"/> 收到卷帘那一声就 Refresh 了），
    /// 这里管的是**别的轨**：`Refresh` 里只管把「聚焦」那三笔点亮，灭掉上一条得整窗推一遍。
    /// 读数和选中集都不用动 —— 换焦点不改选中集（那是刻意的，见 <see cref="MoveFocus"/>）。
    ///
    /// <b>不滚进视野。</b>Ctrl+↑/↓ 换聚焦轨会滚（你可能看不见落点在哪），
    /// 而这里不会：鼠标点的东西本来就在眼前，这时候再滚一下反而是画面在手下抽搐 ——
    /// 尤其这一按往往还接着一次拖动。
    /// </summary>
    private void OnLaneFocusChanged(object? sender, EventArgs e) => RefreshView();

    // ==================== 改速度 ====================

    /// <summary>
    /// 速度框：回车提交。
    ///
    /// 写进来的必须是**具体的拍/分**（比如 76），不是百分比也不是倍率 —— 用户要能对着原曲的标记直接填。
    /// 不合法就报一句中文、把框退回原值：留一个看着生效了的错值比报错坏得多。
    ///
    /// 提交之后焦点从框里放开：不放的话接着打字会继续改这个框，
    /// 而用户以为自己已经在按方向键看谱子了。
    /// </summary>
    private void OnBpmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _song is not { } song) return;
        e.Handled = true;

        // double.TryParse 连 "NaN" / "Infinity" 都收（和 TempoMap 拦 NaN 那条是同一类坑），
        // 所以解析成功之后还要自己判一次有限数
        bool parsed = double.TryParse(BpmBox.Text?.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double bpm);

        // 先放开焦点，否则框里写不进字（RefreshView 那一帧会跳过有焦点的框）
        ReleaseEditFocus();

        if (!parsed || !double.IsFinite(bpm) || bpm < SongEditor.MinBpm || bpm > SongEditor.MaxBpm)
        {
            ShowError($"速度要填 {SongEditor.MinBpm:0} 到 {SongEditor.MaxBpm:0} 之间的数（拍/分），"
                + $"「{BpmBox.Text}」不算。");
            // 退回原值：报错之后留一个看着生效了的数在框里，比报错本身难查得多
            BpmBox.Text = CurrentBpmText(song);
            return;
        }

        HideMessages();
        ApplySong(_editor.SetBpm(song, bpm));

        // 把框写成规范样子。**就算是空操作也得写**：框里打的是 ` 76 ` 或者 `076` 时，
        // 算出来的数和生效的值一模一样，SetBpm 返回同一个引用，ApplySong 提前返回、
        // 跳过 RefreshView —— 于是用户回车了、什么都没发生、框里还留着他打的那串原文。
        // 那正是这条要消掉的那种「回车了，框里怎么还是这个」的困惑。
        BpmBox.Text = CurrentBpmText(_song!);
    }

    /// <summary>
    /// 速度框此刻该写的数：**具体的拍/分**（四舍五入到整数），不是百分比也不是倍率。
    ///
    /// 取的是 **tick 0 的基准速度**，不是播放头那一点的速度 —— 这一格是**输入框**，
    /// 显示的数必须就是回车之后生效的那个数。改速度写进去的是基准值再整体等比缩放
    /// （<see cref="SongEditor.SetBpm"/>），所以 tick 0 就是它的逆。
    ///
    /// 从前这里写的是 <c>BeatsPerMinuteAt(播放头)</c>，注释里也认了「变速曲子上两者分得开」，
    /// 当成小事放过了。它不是小事：语料库 63 首里有 22 首不止一个速度值。
    /// 拿 `Carulli_Duetto_No2_Op4.mid` 说 —— tick 0 是 50，61920 那一段是 150；
    /// 播到那一段停下，框里写的是 150，用户**一个字都没打**直接回车，
    /// `SetBpm(150)` 就把 tick 0 改成 150、整首按 3 倍缩放：347.8 秒变 115.9 秒，
    /// 撤销按钮亮了，曲子快了三倍。用户以为那一回车是「确认一下现在的值」。
    ///
    /// 一个显示的数和一个写入的数说的是两件事，是这一格最不该有的毛病。
    /// </summary>
    private string CurrentBpmText(Song song) =>
        Math.Round(song.TempoMap.BeatsPerMinuteAt(0))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>把焦点从输入框里放开，交给卷帘那一块（它可聚焦，见 MainWindow.axaml）。</summary>
    private void ReleaseEditFocus() => LanesHost.Focus();

    // ==================== 导出 / 演奏器 ====================

    /// <summary>
    /// 「导出」—— 把此刻手上的谱面写回一个标准 MIDI 文件。
    ///
    /// 写出去的是**模型**，不是屏幕：<c>Track.Transpose</c>、卷帘视口、播放头都不进文件
    /// （spec 里那条「Transpose 是轨的属性，不写进音符」的同一个道理）。
    /// 一句「导出成功」也不说就太安静了 —— 用户没法知道盘上到底有没有落下一个文件。
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_song is not { } song) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 MIDI",
            SuggestedFileName = SongNameBox.Text ?? "未命名",
            DefaultExtension = "mid",
            FileTypeChoices = new[] { MidiFileType }
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            // 用户取消了。取消不是失败，什么都不用说
            return;
        }

        try
        {
            // 写出去的是此刻手上的那一份（含刚做完、还没撤销的编辑），不是屏幕
            MidiWriter.Write(song, path);
            ShowNotice($"已导出到 {path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // 和导入同一条规矩：MidiWriter 抛的是中文消息，原样报出来
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 「演奏器…」—— 另开一个独立窗口，把选中的轨弹到别的程序里去。
    ///
    /// 本窗口不 new 它、也不知道它要什么（钟和键鼠出口都在组装点手里）：
    /// 要一个过来、挂到自己名下、Show。挂了 owner 之后主窗口一关它就跟着关 ——
    /// 一个还在发按键的窗口不该在主窗口没了以后留在屏幕上。
    /// </summary>
    private void OnPerformerClick(object? sender, RoutedEventArgs e)
    {
        if (_performerFactory?.Invoke() is not { } window) return;

        // 组装点复用的那个窗口可能已经显示着了，再 Show 一次会抛
        if (!window.IsVisible) window.Show(this);
        else window.Activate();
    }

    // ==================== 提示行 ====================

    private void ShowError(string message)
    {
        NoticeBox.IsVisible = false;
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }

    private void ShowNotice(string message)
    {
        ErrorBox.IsVisible = false;
        NoticeText.Text = message;
        NoticeBox.IsVisible = true;
    }

    private void HideMessages()
    {
        ErrorBox.IsVisible = false;
        NoticeBox.IsVisible = false;
    }

    // ==================== 播放 ====================

    private void OnPlayClick(object? sender, RoutedEventArgs e) => TogglePlayback();

    /// <summary>
    /// 走带条上那颗按钮：没在放就开始，正在放就暂停，暂停着就接着放。
    /// 鼠标按它和空格键**走的是同一条**（见 <see cref="OnWindowKeyDown"/> 里的空格那一支）——
    /// 两条各写一遍的话，置灰、按钮上的字、刷新这些收尾迟早只有一条会被改到，
    /// 于是「空格」和「点按钮」在某个角落上开始不一样。
    /// </summary>
    private void TogglePlayback()
    {
        if (_controller is null) return;
        if (_playback.IsPlaying) PausePlayback();
        else StartPlayback();
    }

    /// <summary>
    /// 从当前位置开始播。<see cref="PreviewPlayback.Play"/> 那句 <c>Seek(MusicNow)</c> 一个人管三种情况
    /// —— 从头、暂停之后接着、停止之后再放，这里不必分。
    /// </summary>
    private void StartPlayback()
    {
        if (_controller is null) return;
        _playback.Play();
        RefreshTransport();
        RefreshView();
    }

    /// <summary>
    /// 停在原地。**和 <see cref="StopPlayback"/> 的差别只有两样：不动视野、按钮上写「继续」。**
    ///
    /// 那一下 <c>SnapViewToBar</c> 是「这段我听完了」的意思，暂停里做它就等于把视野从人正看着的
    /// 地方拽走 —— 而暂停要的恰恰是「就在这，别动」。
    /// </summary>
    private void PausePlayback()
    {
        _playback.Pause();
        RefreshTransport();
        RefreshView();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e) => StopPlayback();

    /// <summary>停下：松掉所有正在响的音，并把视图对齐到小节线（标注 4）。</summary>
    private void StopPlayback()
    {
        _playback.Stop();
        _controller?.SnapViewToBar();
        RefreshTransport();
        RefreshView();
    }

    private void OnPlaybackFinished(object? sender, EventArgs e)
    {
        // 放完了：和按停止一样收尾。**光标留在原地**，别自己跳回开头。
        // 状态上走的是 Stop（不是 Pause），所以按钮回到「▶ 播放」—— 空格能重新开一段，
        // 不会卡在「继续」上（那条路的语义是「从刚才停的地方接着听」，这回没有那个地方）
        _playback.Stop();
        _controller?.SnapViewToBar();
        RefreshTransport();
        RefreshView();
    }

    /// <summary>
    /// 走带条那两颗按钮的字和亮灭。播放状态一变就调它。
    ///
    /// **以播放器为准，不以「上一次点了什么」为准**：暂停、停止、放完自动停、
    /// 换曲子（<c>Load</c> 里会 <c>Stop</c>）都能把状态改掉，靠记一个「上次是放还是停」
    /// 的字段迟早会和真身对不上 —— 那时按钮上写着「暂停」而没东西在响。
    /// 问 <see cref="PreviewPlayback"/> 自己要，没有第二个真相源。
    /// </summary>
    private void RefreshTransport()
    {
        PlayButton.Content = _playback.IsPlaying ? "⏸ 暂停"
            : _playback.IsPaused ? "▶ 继续"
            : "▶ 播放";

        // 判据和换曲子那儿一致：**有轨才放得响**，一条轨都没有的谱面按了也是白按
        PlayButton.IsEnabled = _song is { Tracks.Count: > 0 };
        // ■ 始终可用（有谱面就能按）：它是「这段我听完了，视野回小节」，
        // 没在放的时候按一下也有意义 —— 而灰着会让人以为「停了就不能再停」
        StopButton.IsEnabled = _song is not null;
    }

    /// <summary>
    /// 导航条落到了某一小节（0 起，已吸附到小节线）。
    ///
    /// 它**搬的是播放头**，不只是视图 —— 松手后红线出现在新位置（标注 3），
    /// 而那也正是「从当前位置播放」的起点。视图顺手把小节摆到屏幕中间，
    /// 免得红线一松手就贴在左边缘上。
    /// </summary>
    private void OnNavSeek(object? sender, int barZeroBased)
    {
        if (_controller is null) return;

        long tick = _controller.TickOfBarClamped(barZeroBased);
        _controller.CenterOnBar(barZeroBased);
        _playback.SeekSeconds(_controller.Song.TempoMap.SecondsAt(tick));

        RefreshReadout();
        RefreshView();
    }

    /// <summary>「跳到 __ 小节」：回车生效，越界的小节号夹到首尾（输 999 的意思就是「去最后」）。</summary>
    private void OnJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _controller is null) return;
        e.Handled = true;

        if (!int.TryParse(JumpBox.Text?.Trim(), out int bar))
        {
            // 输了个不是数的东西：退回当前小节，不留一个看着生效了的错值
            JumpBox.Text = Format.BarNumber(_controller.BarOfTick(_playback.PlayheadTick));
            return;
        }

        _controller.SeekBar(bar);
        long tick = _controller.TickOfBarClamped(bar - 1);
        _playback.SeekSeconds(_controller.Song.TempoMap.SecondsAt(tick));
        JumpBox.Text = Format.BarNumber(_controller.BarOfTick(tick));

        RefreshReadout();
        RefreshView();
    }

    // ==================== 键盘 ====================

    /// <summary>
    /// 窗口级快捷键：空格开始试听、撤销 / 重做（Ctrl+Z、Ctrl+Y、Ctrl+Shift+Z）、方向键微调、
    /// Ctrl+←/→ 定位、Ctrl+↑/↓ 换聚焦轨。
    ///
    /// 方向键按**方案 A**（工单 09）：<c>←/→</c> 移时间、<c>↑/↓</c> 移音高、
    /// <c>Shift+←/→</c> 改时值、<c>Ctrl+←/→</c> 在**焦点轨内**前后跳、
    /// <c>Ctrl+↑/↓</c> 在轨之间上下走（聚焦，见 <see cref="MoveFocus"/>）。
    /// <c>Delete</c> / <c>Backspace</c> 删掉选中（见 <see cref="DeleteSelection"/>）。
    /// 07 原本把裸 <c>←/→</c> 绑成「前后跳」，09 把裸键让给了微调 ——
    /// <b>能力没砍，挪到 Ctrl 上了</b>：07 那两条测试测的是控制器上的 <c>MoveSelection</c>，
    /// 那条路一个字节都没动，所以不会红，变的只是这里把哪个键绑到它上面。
    ///
    /// 隧道阶段接进来，先于任何控件拿到按键。
    /// <b>焦点在输入框里时整个让开</b> —— 那时左右键归光标用、Ctrl+Z 归输入框自己的撤销，
    /// 抢过来会让人没法改自己输的数。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Esc：收掉「删掉这条轨？」和「抽掉一段」那两问。它们都不是弹窗（只是轨道头上换了一排控件），
        // 收不掉的话键盘用户除了再点一次「取消」没有别的退路。
        // 焦点在改名框 / 小节号框里时上面那一句已经让开了 —— 那时 Esc 归输入框自己用。
        if (e.Key == Key.Escape)
        {
            bool dismissed = false;
            foreach (var lane in _lanes)
            {
                dismissed |= lane.CancelPendingDelete();
                dismissed |= lane.CancelPendingSplit();
            }
            e.Handled = dismissed;
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            if (e.Key == Key.Z && !shift) { e.Handled = true; Undo(); return; }
            if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { e.Handled = true; Redo(); return; }
        }

        if (_controller is null) return;

        // Ctrl + ←/→ ：在**焦点轨**的音符之间前后跳（只定位，不动音符）。
        // 限定在一条轨里是 18 改的：跨轨那版按着按着会莫名其妙换到别的轨上，
        // 而换轨本来就有自己的手势（Ctrl+↑/↓，紧挨着下面那一段）
        if (ctrl && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            JumpSelection(e.Key == Key.Left ? -1 : 1);
            return;
        }

        // Ctrl + ↑/↓ ：换一条轨（聚焦）。和 Ctrl + ←/→ 换一个音是对称的两件事：
        // 一个在时间上走，一个在轨之间走，都不动谱面。
        // 裸 ↑/↓ 是微调音高（见下面那个 switch），所以这一对必须带 Ctrl 才分得开
        if (ctrl && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            MoveFocus(e.Key == Key.Up ? -1 : 1);
            return;
        }

        if (ctrl) return;

        // Delete / Backspace：把当前选中的音整批删掉。两个键都绑（两个键原本都空着），
        // 因为「删掉」这件事在键盘上有两个同样顺手的落点，选哪个是肌肉记忆，不是配置项。
        // 一个音都没选中时**不标记 Handled**：这一下不该被吃掉，让它照常往下走。
        if (e.Key is Key.Delete or Key.Back && _controller.SelectedNotes.Count > 0)
        {
            e.Handled = true;
            DeleteSelection();
            return;
        }

        // 空格 = 走带条上那颗「▶ 从当前位置播放」。**必须抢在控件前面**：
        // 焦点停在轨道头上那些按钮、下拉上的时候，空格本来归它们
        //（按钮是「按一下」，下拉是「展开」），不抢的话「空格播放」就是时灵时不灵 ——
        // 而屏幕上没有任何东西说得清为什么，人只会以为自己按歪了。
        // 输入框那一头在上面已经整块让开了（改名、速度、小节号、曲名），所以改名字时打空格还是打空格。
        if (e.Key == Key.Space)
        {
            e.Handled = true;

            // **光标记 Handled 是拦不住的。** 实测：焦点停在轨头那颗「折叠」上按空格，
            // 那条轨收起来了**而且**开始播放了 —— 一个键干了两件事。
            // 原因是 <c>Button</c>（下拉也一样）对空格走的是**类处理器**，
            // 它不看你在这个隧道处理器里标没标 Handled，照按不误。
            //
            // 所以顺手把键盘焦点收回窗口：KeyUp 的路由是按**抬起那一刻**的焦点重新算的，
            // 焦点已经不在那颗按钮上了，它「按下 → 抬起 → 触发」这条路就断在中间
            //（Avalonia 的按钮是**抬起**才触发的，ClickMode.Release）。
            // 收回来的副作用只有一样：那颗按钮不再带着焦点框 —— 而它本来也不该有，
            // 空格是走带键，不是「按按钮」。
            if (FocusManager?.GetFocusedElement() is InputElement { Focusable: true })
                FocusManager.ClearFocus();

            // 能不能按以**那颗按钮**为准，不是另算一套：它在没曲子、没音轨时是灰的，
            // 空格一并跟着没反应（见 RefreshTransport）。
            //
            // **一个键管两头（放 / 暂停）是 20 号工单推翻的一处旧决定。** 从前空格只负责「开始」，
            // 理由是「一个键管两头的话，连按两下手就不知道自己站在哪一头了」——
            // 那条理由缺的正是**暂停**：当时两头是「放」和「停」，按第二下等于把刚放的东西丢掉，
            // 确实让人迷失。现在的两头是「放」和「停在这，等下接着听」，
            // 按第二下的结果就写在按钮上（▶ 暂停 → ▶ 继续），迷失不了。
            // 文档照实改在 27 号工单，这里先把行为改过来。
            if (PlayButton.IsEnabled) TogglePlayback();
            return;
        }

        // 一步一格 = 一个十六分音符，和拖动吸的是同一个格（控制器算好放在那儿）
        long grid = _controller.GridTicks;
        switch (e.Key)
        {
            case Key.Left when shift:
            case Key.Right when shift:
                e.Handled = true;
                NudgeLength(e.Key == Key.Right ? grid : -grid);
                break;

            case Key.Left:
                e.Handled = true;
                NudgeNotes(-grid, 0);
                break;
            case Key.Right:
                e.Handled = true;
                NudgeNotes(grid, 0);
                break;
            case Key.Up:
                e.Handled = true;
                NudgeNotes(0, 1);
                break;
            case Key.Down:
                e.Handled = true;
                NudgeNotes(0, -1);
                break;
        }
    }

    /// <summary>
    /// 在**焦点轨**的音符之间前后跳一个（Ctrl + ←/→）。只定位，不动音符、不动焦点。
    ///
    /// 落点一定在焦点轨上（见 <see cref="PianoRollController.MoveSelection"/>），
    /// 所以下面那个 <c>Reveal</c> 展开的就是焦点轨自己。
    /// </summary>
    private void JumpSelection(int delta)
    {
        if (_controller is null) return;

        var info = _controller.MoveSelection(delta);
        if (info is not { } note) return;

        RefreshReadout();
        // 横向已经由控制器对齐到那一小节，纵向（哪条轨）在这儿滚进视野。
        // 走 Reveal 而不是 BringIntoView：那条轨要是收着的，「滚到它那儿」在屏幕上
        // 一个像素的变化都没有 —— 跳过去的是那个音，所以顺手把它展开
        if (note.Track >= 0 && note.Track < _lanes.Count) _lanes[note.Track].Reveal();

        RefreshView();
    }

    /// <summary>
    /// Ctrl + ↑/↓ ：把聚焦挪到上一条 / 下一条轨，**跳过收起来的那些**。
    ///
    /// 上下和屏幕上的上下一致：↑ 是往上（下标小的那一条）。
    /// 到头、或者这个方向上只剩收起来的轨，就原地不动（不绕回去）。
    ///
    /// 落点滚进视野，但**不展开**：展开是 Ctrl+←/→ 定位到一个音上时的做法
    /// （那条路非展开不可，否则跳过去屏幕上什么变化都没有），
    /// 而这里本来就绕开了收起来的轨，绕过去比掰开它合适。
    ///
    /// 也不动选中集、不动试听、不动播放头 —— 换聚焦是「手挪到哪条轨上」，
    /// 不是「改哪条轨」。
    /// </summary>
    private void MoveFocus(int delta)
    {
        if (_controller is not { } controller) return;

        int before = controller.FocusedTrack;
        if (controller.MoveFocusedTrack(delta, CollapsedFlags()) == before) return;

        if (controller.FocusedTrack < _lanes.Count) _lanes[controller.FocusedTrack].ScrollIntoView();
        RefreshView();
    }

    /// <summary>
    /// 方向键微调：把选中的一组音整体挪一格（时间）或一个半音（音高）。
    ///
    /// 夹在这儿做一次，夹完的增量才是真正会生效的那个 —— 预览与命令两边都得拿它算
    /// （命令那边还会再夹一次，夹的是已经合法的值，等于没夹）。
    ///
    /// 从前这里跟着一句「拿没夹过的增量算出来的位置在边界上根本不存在，选中集那一下就丢了」——
    /// 那是按值认音那套镜像的毛病（31 号工单删了那套，坐标按身份寻址之后没有「算新位置」这件事）。
    /// 夹取本身照旧要：不夹的话按一下方向键会发一条被命令缩掉一截的位移，
    /// 屏幕上动的地方和用户按的那一下对不上。
    /// </summary>
    private void NudgeNotes(long deltaTicks, int deltaPitch)
    {
        if (_song is not { } song || _controller is not { } controller) return;

        var selected = controller.SelectedNotes.ToArray();
        if (selected.Length == 0) return;

        (deltaTicks, deltaPitch) = controller.ClampMoveDelta(selected, deltaTicks, deltaPitch);
        if (deltaTicks == 0 && deltaPitch == 0) return;

        ApplySong(_editor.MoveNotes(song, selected, deltaTicks, deltaPitch));
    }

    /// <summary>
    /// Shift + ←/→ ：改时值，一步一格；缩到头也不小于 1 个 tick（时值不能是 0）。
    ///
    /// 只动**主选中**那一个。一组音一起改时值本来该是一条命令，而 <c>SetNoteSpan</c> 只收一个音：
    /// 选中一组按一下就会记 N 格撤销，得按 N 次才回到原样，那是坑不是功能。
    /// 主选中就是用户最后点的那个（读数条报的也是它），按一下只改它一个说得通。
    ///
    /// 选中集不用管：改时值的那个音**身份不变**（只是变长变短，见 <c>SetNoteSpan</c>），
    /// 哪怕它越过邻居在数组里换了位置，坐标还是指着它。
    /// </summary>
    private void NudgeLength(long deltaLength)
    {
        if (_song is not { } song || _controller is not { } controller) return;
        if (controller.Selection is not { } primary) return;

        var target = new NoteRef(primary.Track, primary.Note);
        if (NoteAt(song, target) is not { } note) return;

        long length = Math.Max(1, note.LengthTicks + deltaLength);
        if (length == note.LengthTicks) return;

        ApplySong(_editor.SetNoteSpan(song, target, note.StartTick, length));
    }

    // ==================== 读数条 ====================

    /// <summary>
    /// 悬停到某个音上。参数是那个音的**身份**，<see cref="NoteId.None"/> = 没命中（空白处）。
    ///
    /// 没命中时不用特判：<c>NoteId.None</c> 是 0，而真曲子里的号是从 1 开始连号发的
    /// （见 <see cref="NoteIdentity"/>），所以它在这条轨上一个音都对不上，Describe 给 null，
    /// 读数条照旧是占位符 —— 「认不出来就是没这个音」这一条两边是同一个判断。
    /// </summary>
    private void OnLaneHover(object? sender, NoteId note)
    {
        if (_controller is null || sender is not TrackLaneView lane) return;
        ShowHover(_controller.Describe(lane.TrackIndex, note));
    }

    /// <summary>
    /// 鼠标此刻悬在哪个音上。**null 有两种意思**：「没悬在任何音上」和「鼠标刚离开卷帘」——
    /// 两种都落到「回落到主选中」，所以不用分开。
    ///
    /// 为什么存下来、而不是悬停时直接把读数改写掉：悬停是**一过性**的，鼠标一移开就得把
    /// 选中那个音的读数重新摆回去，于是必须回答「刚才被顶掉的是什么」—— 那正是从前那两套
    /// 读数的病根（悬停那套变空、选中那套还留着，同一个音在两处各显示一半）。
    /// 存下「悬停」这件事本身，每次现算「悬停优先、选中兜底」，就没有「要还原什么」这回事了。
    ///
    /// **它是个下标，编辑之后一律作废** —— 所以换曲子和每次编辑都要清（见 ApplySong / LoadSong）。
    /// </summary>
    private PianoRollController.NoteInfo? _hovered;

    /// <summary>鼠标进/出一个音。传 null 是「离开了」——这时读数不是变空，是回落到主选中。</summary>
    private void ShowHover(PianoRollController.NoteInfo? info)
    {
        _hovered = info;
        RefreshReadout();
    }

    /// <summary>
    /// 读数条上那一套（轨 / 音高 / 小节 / 拍位 / 时值）**唯一的出处**。
    ///
    /// 值从哪来：**悬停优先，没悬停就用主选中的音**。这就是「合并成一套」的兑现 ——
    /// 鼠标从音上移开时读数回落到选中的音（而不是变空），是这条规矩的直接结果，不是副作用。
    ///
    /// 两个都没有时，**标签和值一起藏**：只藏里面那块（ReadoutDetail），外面
    /// `Border.readoutbar` 上的 MinHeight 一个像素不动 —— 不然鼠标一移开这一条会塌下去，
    /// 整窗跟着跳一下，比留着几个占位符还难受。
    ///
    /// 轨号留着：合起来之后它是「这个音在哪条轨」的唯一线索（从前那句「选中」里也有它）。
    /// </summary>
    private void RefreshReadout()
    {
        if ((_hovered ?? _controller?.DescribeSelection()) is not { } note)
        {
            ReadoutDetail.IsVisible = false;
            return;
        }

        ReadoutDetail.IsVisible = true;
        ReadoutTrackText.Text = Format.TrackNumber(note.Track + 1);
        ReadoutPitchText.Text = Format.Pitch(note.Pitch);
        ReadoutBarText.Text = Format.BarNumber(note.Bar);
        ReadoutBeatText.Text = Format.Beat(note.BeatInBar);
        ReadoutLengthText.Text = Format.Length(note.LengthBeats);
    }

    // ==================== 重画 ====================

    /// <summary>
    /// 按「此刻」重算所有卷帘和导航条。窗口改宽、滚动、点导航条、播放的每一帧都走它。
    /// </summary>
    private void RefreshView()
    {
        if (_controller is not { } controller) return;

        long playhead = _playback.PlayheadTick;

        // 播放中：先跟着播放头滚，再拿新的视图位置算这一屏 —— 反过来的话画面会慢一帧
        if (_playback.IsPlaying) controller.Follow(playhead, FollowFraction);

        foreach (var lane in _lanes) lane.Refresh(playhead, !_draggingNav);

        double navWidth = NavStrip.Bounds.Width;
        if (navWidth >= 20)
        {
            // 缩略图画的是**焦点轨**：Ctrl+↑/↓ 换了焦点、或者点了别条轨上的音符，下一帧它就跟着换。
            //
            // 焦点轨的轨对象要按下标取，而「轨被删光」那一帧 FocusedTrack 已经越界了
            // （控制器那边把这个下标当「没这条轨」，见 PitchRangeOf 的说明，它照答不误）。
            // 这儿同样给 null 而不是硬取 —— 索引越界会当场炸在重画里，而重画是每个播放帧都跑的
            int focused = controller.FocusedTrack;
            var navTrack = focused >= 0 && focused < controller.Song.Tracks.Count
                ? controller.Song.Tracks[focused]
                : null;

            NavStrip.SetScene(PianoRollPresenter.BuildNav(
                new PianoRollPresenter.NavViewport(
                    navWidth, NavStrip.Bounds.Height, controller.TotalTicks, controller.BarCount),
                navTrack, controller.PitchRangeOf(focused), playhead,
                controller.ViewStartTick, controller.TicksVisible));
        }

        // 视图范围从前在这儿写成「第 1–4 小节 / 共 96」—— 那句话没了：
        // 上面缩略图上那个视口框已经在视觉上说明「我在看哪一段」，文字是同一件事说第二遍。
        // 位置读数留的是**播放头**所在小节（不是视口起始）：它右边紧挨着「跳到某小节」的输入框，
        // 「我在哪 / 我要去哪」摆在一起才成对照。
        PositionText.Text = Format.Position(controller.BarOfTick(playhead), controller.BarCount);

        // 速度框报的是 **tick 0 的基准速度** —— 也就是回车之后真正生效的那个数
        // （判据为什么不取播放头那一点，见 CurrentBpmText 的注释）。
        //
        // **焦点在框里时跳过**：这一帧一帧地重写，会把用户正打进去的字吃掉半个。
        if (!BpmBox.IsFocused) BpmBox.Text = CurrentBpmText(controller.Song);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 关窗口时把音松开：不然合成器上会留一串按着不放的键
        _playback.Stop();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 定时器跟着窗口一起收掉。出声的设备不在这儿关 —— 它是 App 建的，由 App 收尾
        _playback.Dispose();
        base.OnClosed(e);
    }
}
