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
            song = SongProject.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // SongProject 抛的就是中文消息，原样报出来 —— 编一句更笼统的话只会把线索弄丢
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
        _playback.Load(song);

        SongNameBox.Text = title;
        EmptyHint.IsVisible = false;
        JumpBox.Text = "1";

        PlayButton.IsEnabled = song.Tracks.Count > 0;
        StopButton.IsEnabled = false;
        JumpBox.IsEnabled = true;
        // 有谱面就写得出，哪怕一个音都没有 —— 速度表和分辨率也值得留下来，
        // 所以这条的判据是「装上了曲子」，不是「有轨」
        ExportButton.IsEnabled = true;

        ShowHover(null);
        ShowSelection();
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

        _controller = new PianoRollController(song);

        if (!rebuildAll && _lanes.Count == song.Tracks.Count)
        {
            for (int i = 0; i < _lanes.Count; i++) _lanes[i].Rebind(_controller, i);
            return;
        }

        // 轨数变了只能重建控件，但**折叠是用户对某条轨的标记，不该被一次删轨顺手抹掉**
        // （撤销把那条轨拿回来时尤其明显：收起来的那几条自己全弹开了）。
        // 按轨的身份记，不按下标 —— 删掉第 0 条之后下标整体前移，按下标带会把折叠挪到别人身上。
        // 必须在 Clear 之前抄下来：下面那一刻 _lanes 就空了
        var collapsed = new HashSet<(int, int)>();
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
            lane.NotesDeleted += OnNotesDeleted;
            lane.SelectionChanged += OnLaneSelectionChanged;
            lane.RenameRequested += OnTrackRenameRequested;
            lane.DeleteRequested += OnTrackDeleteRequested;
            _lanes.Add(lane);
            LanesHost.Children.Add(lane);
        }
    }

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
            var (header, song) = SongProject.LoadProject(library.PathOf(name));

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
            library.Write(name, SongProject.WriteProject(song, new ProjectHeader(
                SongProject.ProjectVersion, name, _edited, _importedFrom)));
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
    /// <item>视口与选中按原样放回去 —— 改的是速度或移调，不该把人弹回第 1 小节、也不该丢掉选中。</item>
    /// <item>播放头用**新的**速度表把原来那个 tick 换算成秒再 Seek 回去。tick 是同一处，
    /// 秒数变了（这正是改速度的意思）；用旧秒数的话改完播放头会跳。</item>
    /// <item>试听换谱 —— <c>Load</c> 会先把正在响的音全松掉，所以编辑顺手停掉播放，
    /// 这比「边放边改」安全（改到一半的谱子不该继续发出去）。</item>
    /// </list>
    ///
    /// <b>「改没改」比引用</b>：跟装饰器同一条判据。一次「改成和现在一样」不该把上面这些全部重置一遍。
    /// </summary>
    /// <param name="selectionAfter">
    /// 编辑**之后**该选中的那组音，按**值**给。null（默认）表示「这次的编辑没动音符的值」——
    /// 改速度、改移调、撤销、重做都是这种，照当前的选中集原样留一份下来就行。
    ///
    /// 为什么要按值：<see cref="NoteRef"/> 是下标，而挪音符和改时值都会把音符数组重排
    /// （见它自己的注释），编辑之前算的下标编辑之后可能指着另一个音。
    /// 值在一份曲子里是对得上的；两个一模一样的音对不上号，那是已知的短处，仍然比指错音强。
    ///
    /// **注意增量那一侧要是夹过的**：按值算新位置等于假设「新值 = 旧值 + 增量」，
    /// 命令在边界上会把这个增量缩一截，增量没夹过就对不上了。
    /// （拖动那一路在 <c>PianoRollLane</c> 里夹过，方向键那一路在 <see cref="NudgeNotes"/> 里夹，
    /// 共用 <see cref="PianoRollController.ClampMoveDelta"/>。）
    /// </param>
    private void ApplySong(Song edited, IReadOnlyList<SelectedNote>? selectionAfter = null)
    {
        if (ReferenceEquals(edited, _song)) return;

        long playheadTick = _playback.PlayheadTick;
        long viewStartTick = _controller?.ViewStartTick ?? 0;
        // 必须在 SyncLanes 之前取：下面换掉控制器，旧下标当场作废
        var selection = selectionAfter ?? CaptureSelectionValues();

        _song = edited;
        // 粘性标记：动过就是动过。撤销回原样也不清它（见 _edited 的说明），存盘也不清
        _edited = true;
        SyncLanes();

        // 视口照旧有效：小节刻度不受任何一条编辑命令影响（改速度只动速度表，其余只动音符），
        // 控制器自己的 SetViewStart 还会夹一次，曲子变短也不会越界
        _controller?.SetViewStart(viewStartTick);
        RestoreSelection(selection);

        _playback.Load(edited);
        _playback.SeekSeconds(edited.TempoMap.SecondsAt(playheadTick));
        // 试听被换谱顺手停了（Load 会先松开所有正在响的音），走带条的亮灭跟着回位
        PlayButton.IsEnabled = edited.Tracks.Count > 0;
        StopButton.IsEnabled = false;

        // 悬停那个音说的是**上一份**曲子里的下标，编辑之后一概作废
        //（鼠标这会儿也多半正压在刚点的那个按钮上，本来就不在卷帘里）
        ShowHover(null);
        ShowSelection();
        RefreshEditState();
        RefreshView();
    }

    /// <summary>撤销 / 重做、保存 / 另存为、速度框、曲名框、时长这一组读数。换曲子和每次编辑之后调它。</summary>
    private void RefreshEditState()
    {
        UndoButton.IsEnabled = _editor.CanUndo;
        RedoButton.IsEnabled = _editor.CanRedo;
        BpmBox.IsEnabled = _song is not null;
        SongNameBox.IsEnabled = _song is not null;
        // 没有曲库就存不了（组装点没给），灰着比按了没反应诚实
        SaveButton.IsEnabled = _song is not null && _library is not null;
        SaveAsButton.IsEnabled = SaveButton.IsEnabled;
        DurationText.Text = _song is { } song ? Format.Clock(song.TotalSeconds) : Format.Placeholder;
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
    /// <see cref="NudgeNotes"/> 在调命令之前夹一次。所以下面按「旧值 + 位移」算新选中集是精确的。
    /// </summary>
    private void OnNotesMoved(object? sender, NoteMoveRequest request)
    {
        if (_song is not { } song) return;

        ApplySong(
            _editor.MoveNotes(song, request.Notes, request.DeltaTicks, request.DeltaPitch),
            SelectionAfterMove(request.Notes, request.DeltaTicks, request.DeltaPitch));
    }

    /// <summary>卷帘上拖完某条边。请求里是**绝对**的起点与时值，不是增量。</summary>
    private void OnNoteResized(object? sender, NoteResizeRequest request)
    {
        if (_song is not { } song) return;

        ApplySong(
            _editor.SetNoteSpan(song, request.Note, request.StartTick, request.LengthTicks),
            SelectionAfterResize(request.Note, request.StartTick, request.LengthTicks));
    }

    /// <summary>
    /// 卷帘空白处横拖划掉一段（落在区间里的是哪几个音，<c>PianoRollLane</c> 已经算好了）。
    ///
    /// 删完**明确清空选中**，不靠按值去找：被删的音不该还选着，
    /// 而同一个值在别处可能还有一份，按值找会把那个不相干的音捡回来选上。
    /// </summary>
    private void OnNotesDeleted(object? sender, IReadOnlyList<NoteRef> notes)
    {
        if (_song is not { } song || notes.Count == 0) return;
        ApplySong(_editor.DeleteNotes(song, notes), Array.Empty<SelectedNote>());
    }

    private void OnTrackRenameRequested(object? sender, string name)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.RenameTrack(song, lane.TrackIndex, name));
    }

    /// <summary>
    /// 删掉一整条轨（轨头上那个二次确认已经按过了）。
    ///
    /// 删完**明确清空选中**：<see cref="Song.Tracks"/> 的下标整体前移，
    /// 存下来的选中集（里面带着轨下标）当场作废，按值找也会整片错位。
    /// </summary>
    private void OnTrackDeleteRequested(object? sender, EventArgs e)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.DeleteTrack(song, lane.TrackIndex), Array.Empty<SelectedNote>());
    }

    /// <summary>
    /// 卷帘上的选中变了。
    ///
    /// 选中集是**全局**的（一个控制器管所有轨），所以别的轨的高亮也得跟着变 —— 整窗重画一遍。
    /// </summary>
    private void OnLaneSelectionChanged(object? sender, IReadOnlyList<NoteRef> selected)
    {
        ShowSelection();
        RefreshView();
    }

    // ==================== 编辑之后的选中集 ====================

    /// <summary>「编辑之后该选中哪个音」记的是**值**：哪条轨 + 音符本身（见 <see cref="ApplySong"/>）。</summary>
    private readonly record struct SelectedNote(int Track, Note Note);

    /// <summary>
    /// 此刻选中的那组音，连**下标带值**一起抄下来。
    ///
    /// 必须在换曲子（<see cref="SyncLanes"/>）之前调 —— 下标只对它算出来的那份曲子有效。
    /// 顺手丢掉越界的：ref 过期不是错误，轨刚被删掉那一下就会碰上。
    /// </summary>
    private List<(NoteRef Ref, Note Note)> CaptureSelection()
    {
        var captured = new List<(NoteRef, Note)>();
        if (_song is not { } song || _controller is null) return captured;

        foreach (var reference in _controller.SelectedNotes)
        {
            if (NoteAt(song, reference) is { } note) captured.Add((reference, note));
        }
        return captured;
    }

    /// <summary>同上，只要值那一半 —— 「这次编辑没动音符」时直接扔给 <see cref="ApplySong"/>。</summary>
    private List<SelectedNote> CaptureSelectionValues()
    {
        var values = new List<SelectedNote>();
        foreach (var (reference, note) in CaptureSelection()) values.Add(new SelectedNote(reference.Track, note));
        return values;
    }

    /// <summary>选中集挪过 (deltaTicks, deltaPitch) 之后的样子 —— 挪完拿它放回选中。</summary>
    private List<SelectedNote> SelectionAfterMove(IReadOnlyList<NoteRef> moved, long deltaTicks, int deltaPitch)
    {
        var movedSet = new HashSet<NoteRef>(moved);
        var wanted = new List<SelectedNote>();

        foreach (var (reference, note) in CaptureSelection())
        {
            // 没被挪的那些原样留着：一组音挪的是同一个量，但「选中的」未必就是「被挪的那几个」
            wanted.Add(movedSet.Contains(reference)
                ? new SelectedNote(reference.Track,
                    note with { Pitch = note.Pitch + deltaPitch, StartTick = note.StartTick + deltaTicks })
                : new SelectedNote(reference.Track, note));
        }
        return wanted;
    }

    /// <summary>改完时值之后的样子：只把那一个音换成新值，选中集里其余的原样。</summary>
    private List<SelectedNote> SelectionAfterResize(NoteRef target, long startTick, long lengthTicks)
    {
        var wanted = new List<SelectedNote>();
        foreach (var (reference, note) in CaptureSelection())
        {
            wanted.Add(reference == target
                ? new SelectedNote(reference.Track, note with { StartTick = startTick, LengthTicks = lengthTicks })
                : new SelectedNote(reference.Track, note));
        }
        return wanted;
    }

    /// <summary>
    /// 把「编辑后该选中的那组音」放回选中集：拿**值**在新曲子里找下标。
    ///
    /// 按值找，是因为下标会被重排打乱（<see cref="ApplySong"/> 的说明里讲了为什么）。
    /// 找不到的（音被删了、撤销把它挪回原位了）跳过就行 —— 选中集缩水好过指错音。
    /// 扫一遍而不是建一张索引表：一屏之内音就那么多，而且**顺序得留着**
    /// （主选中 = 最后加进去的那个），哈希表正好把顺序丢了。
    /// </summary>
    private void RestoreSelection(IReadOnlyList<SelectedNote> wanted)
    {
        if (_controller is not { } controller || _song is not { } song) return;

        var references = new List<NoteRef>(wanted.Count);
        foreach (var target in wanted)
        {
            if (target.Track < 0 || target.Track >= song.Tracks.Count) continue;

            var notes = song.Tracks[target.Track].Notes;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i] != target.Note) continue;
                references.Add(new NoteRef(target.Track, i));
                break;
            }
        }

        controller.SetSelection(references);
    }

    /// <summary>那份曲子里的这个音；ref 过期（下标越界、曲子已经换过）时给 null。</summary>
    private static Note? NoteAt(Song song, NoteRef reference)
    {
        if (reference.Track < 0 || reference.Track >= song.Tracks.Count) return null;

        var notes = song.Tracks[reference.Track].Notes;
        return reference.Index >= 0 && reference.Index < notes.Count ? notes[reference.Index] : null;
    }

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
            SongProject.Write(song, path);
            ShowNotice($"已导出到 {path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // 和导入同一条规矩：SongProject 抛的是中文消息，原样报出来
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

    private void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        _playback.Play();
        PlayButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        RefreshView();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e) => StopPlayback();

    /// <summary>停下：松掉所有正在响的音，并把视图对齐到小节线（标注 4）。</summary>
    private void StopPlayback()
    {
        _playback.Stop();
        _controller?.SnapViewToBar();
        PlayButton.IsEnabled = _controller is not null;
        StopButton.IsEnabled = false;
        RefreshView();
    }

    private void OnPlaybackFinished(object? sender, EventArgs e)
    {
        // 放完了：和按停止一样收尾。**光标留在原地**，别自己跳回开头
        _controller?.SnapViewToBar();
        PlayButton.IsEnabled = _controller is not null;
        StopButton.IsEnabled = false;
        RefreshView();
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

        ShowSelection();
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

        ShowSelection();
        RefreshView();
    }

    // ==================== 键盘 ====================

    /// <summary>
    /// 窗口级快捷键：撤销 / 重做（Ctrl+Z、Ctrl+Y、Ctrl+Shift+Z）、方向键微调、Ctrl+←/→ 定位。
    ///
    /// 方向键按**方案 A**（工单 09）：<c>←/→</c> 移时间、<c>↑/↓</c> 移音高、
    /// <c>Shift+←/→</c> 改时值、<c>Ctrl+←/→</c> 在所有轨的音符之间前后跳。
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

        // Esc：收掉「删掉这条轨？」那一问。它不是弹窗（只是轨道头上换了一排按钮），
        // 收不掉的话键盘用户除了再点一次「取消」没有别的退路。
        // 焦点在改名框里时上面那一句已经让开了 —— 那时 Esc 归输入框自己用（取消改名）。
        if (e.Key == Key.Escape)
        {
            bool dismissed = false;
            foreach (var lane in _lanes) dismissed |= lane.CancelPendingDelete();
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

        // Ctrl + ←/→ ：在音符之间前后跳（只定位，不动音符）
        if (ctrl && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            JumpSelection(e.Key == Key.Left ? -1 : 1);
            return;
        }

        if (ctrl) return;

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

    /// <summary>在音符之间前后跳一个（Ctrl + ←/→）。只定位，不动音符。</summary>
    private void JumpSelection(int delta)
    {
        if (_controller is null) return;

        var info = _controller.MoveSelection(delta);
        if (info is not { } note) return;

        ShowSelection();
        // 横向已经由控制器对齐到那一小节，纵向（哪条轨）在这儿滚进视野。
        // 走 Reveal 而不是 BringIntoView：那条轨要是收着的，「滚到它那儿」在屏幕上
        // 一个像素的变化都没有 —— 跳过去的是那个音，所以顺手把它展开
        if (note.Track >= 0 && note.Track < _lanes.Count) _lanes[note.Track].Reveal();

        RefreshView();
    }

    /// <summary>
    /// 方向键微调：把选中的一组音整体挪一格（时间）或一个半音（音高）。
    ///
    /// 夹在这儿做一次，夹完的增量才是真正会生效的那个 —— 下面按「旧值 + 增量」算新选中集，
    /// 拿没夹过的增量算出来的位置在边界上根本不存在，选中集那一下就丢了。
    /// 命令那边还会再夹一次，夹的是已经合法的值，等于没夹。
    /// </summary>
    private void NudgeNotes(long deltaTicks, int deltaPitch)
    {
        if (_song is not { } song || _controller is not { } controller) return;

        var selected = controller.SelectedNotes.ToArray();
        if (selected.Length == 0) return;

        (deltaTicks, deltaPitch) = controller.ClampMoveDelta(selected, deltaTicks, deltaPitch);
        if (deltaTicks == 0 && deltaPitch == 0) return;

        ApplySong(
            _editor.MoveNotes(song, selected, deltaTicks, deltaPitch),
            SelectionAfterMove(selected, deltaTicks, deltaPitch));
    }

    /// <summary>
    /// Shift + ←/→ ：改时值，一步一格；缩到头也不小于 1 个 tick（时值不能是 0）。
    ///
    /// 只动**主选中**那一个。一组音一起改时值本来该是一条命令，而 <c>SetNoteSpan</c> 只收一个音：
    /// 选中一组按一下就会记 N 格撤销，得按 N 次才回到原样，那是坑不是功能。
    /// 主选中就是用户最后点的那个（读数条报的也是它），按一下只改它一个说得通。
    /// </summary>
    private void NudgeLength(long deltaLength)
    {
        if (_song is not { } song || _controller is not { } controller) return;
        if (controller.Selection is not { } primary) return;

        var target = new NoteRef(primary.Track, primary.Note);
        if (NoteAt(song, target) is not { } note) return;

        long length = Math.Max(1, note.LengthTicks + deltaLength);
        if (length == note.LengthTicks) return;

        ApplySong(
            _editor.SetNoteSpan(song, target, note.StartTick, length),
            SelectionAfterResize(target, note.StartTick, length));
    }

    // ==================== 读数条 ====================

    private void OnLaneHover(object? sender, int noteIndex)
    {
        if (_controller is null || sender is not TrackLaneView lane) return;
        ShowHover(_controller.Describe(lane.TrackIndex, noteIndex));
    }

    private void ShowHover(PianoRollController.NoteInfo? info)
    {
        if (info is not { } note)
        {
            HoverPitchText.Text = Format.Placeholder;
            HoverBarText.Text = Format.Placeholder;
            HoverBeatText.Text = Format.Placeholder;
            HoverLengthText.Text = Format.Placeholder;
            return;
        }

        HoverPitchText.Text = Format.Pitch(note.Pitch);
        HoverBarText.Text = Format.BarNumber(note.Bar);
        HoverBeatText.Text = Format.Beat(note.BeatInBar);
        HoverLengthText.Text = Format.Length(note.LengthBeats);
    }

    private void ShowSelection()
    {
        var info = _controller?.DescribeSelection();
        SelectionText.Text = info is { } note
            ? Format.Selection(note.Track + 1, note.Pitch, note.LengthBeats)
            : Format.Placeholder;
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
            NavStrip.SetScene(PianoRollPresenter.BuildNav(
                controller.BarNoteCounts, navWidth,
                controller.TotalTicks, controller.ViewStartTick, controller.TicksVisible));
        }

        int firstBar = controller.ViewStartBar + 1;
        int lastBar = Math.Min(firstBar + PianoRollGeometry.BarsVisible - 1, controller.BarCount);
        NavRangeText.Text = Format.BarRange(firstBar, lastBar, controller.BarCount);

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
