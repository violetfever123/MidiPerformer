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
/// 只管布置与转发：换算在 <see cref="PianoRollController"/>，画什么在
/// <see cref="PianoRollPresenter"/>，出声在 <see cref="PreviewPlayback"/>，
/// 改谱面在 <see cref="SongEditor"/>（外面罩着 <see cref="UndoableSongEditor"/> 记账），
/// 演奏器走组装点给的工厂。
///
/// 它持着当前这一份曲子（<c>_song</c>）：换一份曲子要把控制器与所有轨重建、试听换谱、
/// 播放头按新的速度表换回同一个 tick、撤销按钮亮灭，那一整套在 <see cref="ApplySong"/> 里。
/// 谱面的每一处改动都经 <c>_editor</c> 落成一份新的 <see cref="Song"/>，再交给它装上。
///
/// 曲库的读写文件也在这里收口：列表自己不动盘，只把「点了哪一首 / 要删掉」报上来
/// （<see cref="SongLibraryWindow"/> 是工具栏上一颗按钮开出来的模态窗口，见
/// <see cref="OnLibraryClick"/>），因为只有本窗口知道手上正开着什么；改名不在那边，
/// 唯一入口是顶栏那格「歌曲名」框（<see cref="OnSongNameKeyDown"/>）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>播放时把播放头放在屏幕的哪个位置：偏左约三分之一，右边留出前瞻。</summary>
    private const double FollowFraction = 0.32;

    /// <summary>
    /// 缩略条算得动的最小宽度（像素）。窄过它就是**布局还没把它摆开**，不是「一条真的很窄的条」——
    /// 窗口最小宽度 720 撑得住缩略条远不止 20，所以这个数只用来挡「还没排到布局」那一档
    /// （见 <see cref="RefreshView"/>）。
    /// </summary>
    private const double MinNavWidth = 20;

    /// <summary>认得出的文件类型：midi 和 mid 都收，导入和另存为两侧共用同一份。</summary>
    private static readonly FilePickerFileType MidiFileType = new("MIDI 文件")
    {
        Patterns = new[] { "*.mid", "*.midi" }
    };

    private readonly TokenSource _tokens;
    private readonly PreviewPlayback _playback;
    private readonly Func<PerformerWindow>? _performerFactory;
    private readonly SongLibrary? _library;
    private readonly List<TrackLaneView> _lanes = new();

    /// <summary>编辑脊柱。撤销是装饰器加的能力，界面拿到的就是装饰器，命令本身（<see cref="SongEditor"/>）不知道有撤销这回事。</summary>
    private readonly UndoableSongEditor _editor = new(new SongEditor());

    private PianoRollController? _controller;

    /// <summary>当前这一份谱面。每次编辑换一份新的（不可变），换完走 <see cref="ApplySong"/>。</summary>
    private Song? _song;

    /// <summary>
    /// 曲库里存着的那一份 —— 手上这份最后一次**和盘上对得上**的样子。
    ///
    /// 只在「和曲库打交道」的那两处换：从曲库读出来装上（<see cref="TryOpenLibrarySong"/>）、
    /// 写进曲库（<see cref="SaveTo"/>）；换曲子那一处（<see cref="LoadSong"/>）把它清掉 ——
    /// 新装上来的这一份是不是曲库里那份，装上来的这一刻还不知道，知道的那两处自己补。
    ///
    /// <see cref="ApplySong"/> 那条编辑的路**一个字节都不碰它**，这正是它存在的理由：
    /// 点「演奏」弹的是曲库里存的那份、草稿还留在编辑器里（见 <see cref="UnsavedPrompt.Of"/> 里
    /// 「点演奏」那一格），而 <see cref="_song"/> 是跟着编辑走的 —— 直接把它递过去就是递了草稿。
    ///
    /// 还没进过曲库的（导入时取消了命名、或者刚从曲库删掉）是 <c>null</c>：
    /// 那时没有「已存的」这一份，手上这份就是唯一的一份。
    /// </summary>
    private Song? _storedSong;

    /// <summary>手上这份在曲库里叫什么（= 文件名）。<c>null</c> = 还没进曲库，那时「保存」会先问一个名字。</summary>
    private string? _currentName;

    /// <summary>曲名框里那个名字，也是「这首叫什么」的显示来源。和 <see cref="_currentName"/> 分开：有一类曲子只在手上、不在曲库里（导入时取消了命名），它有名字可显示却没有曲库里的位置。</summary>
    private string _title = "";

    /// <summary>
    /// 这份是从哪个 .mid 导入的，写进缓存的文件头。合并成一首、或从曲库打开的都可能是 null。
    /// 它**什么判据都不参与** —— 只是跟着缓存存着（拿它当判据会让每首导入的歌都长出缓存文件）。
    /// </summary>
    private string? _importedFrom;

    /// <summary>
    /// 这份工程「动过没有」，写进缓存的文件头。它是粘的：一旦编辑过就永远是 true，
    /// 撤销回初始状态、存盘都不清（见 <see cref="ProjectHeader.Edited"/>）。
    /// 清它的只有 <see cref="LoadSong"/> 那一处（换曲子才重来），所以 <see cref="SaveTo"/> 里
    /// 填 header 拿到的就是「保存这一刻」的值 —— 曲库列表上「编辑过」三个字唯一来源就是这一格。
    /// </summary>
    private bool _edited;

    /// <summary>
    /// 手上有「还没存下去的东西」—— 工具栏上「保存」那一格穿不穿主色看它。
    /// 和 <see cref="_edited"/> 刻意分开：那个是**粘性**的、写进工程头、给曲库列表那格
    /// 「改过 / 没动过」用；这一格答的是另一个问题（现在这份和盘上那份对不对得上），
    /// 所以载入和存盘都要清零。
    /// </summary>
    private bool _dirty;

    /// <summary>
    /// 「关窗口」那一处已经问过、用户选了继续 —— 这一趟真的关，别再拦第二次。
    /// 和 <see cref="_askingBeforeClose"/> 分开：那个挡的是「问的过程中又来一次关闭请求」，
    /// 这个放的是「问完了、可以关了」。
    /// </summary>
    private bool _closeConfirmed;

    /// <summary>正开着「关窗口」那颗弹窗。问的过程里再来一次关闭请求（任务栏那一处）不再叠第二个框。</summary>
    private bool _askingBeforeClose;

    /// <summary>正在拖导航条 —— 这期间卷帘上的播放头红线要藏起来。</summary>
    private bool _draggingNav;

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public MainWindow() : this(null!, null!, null!, null, null) { }

    /// <param name="tokens">自绘取色桥（卷帘和导航条不在 XAML 里，拿不到 DynamicResource）。</param>
    /// <param name="clock">墙上钟，喂给试听的时间积分。</param>
    /// <param name="sink">出声的出口（winmm）。</param>
    /// <param name="performerFactory">
    /// 工具栏上「演奏」按下时去要那个独立窗口。给工厂不给现成的窗口：演奏器一建出来就装
    /// 低层键盘钩子，必须到用户真要用的那一刻才存在。复用与单例在组装点里管。
    /// 收的是 <see cref="PerformerWindow"/> 而不是 <see cref="Window"/>：窗口是复用的，
    /// 每按一次「演奏」都要把此刻该弹的那份曲子经 <see cref="PerformerWindow.LoadSong"/> 重递一次
    /// （见 <see cref="OnPerformerClick"/>）。
    /// </param>
    /// <param name="library">
    /// 曲库，目录由组装点拼好（默认是 exe 旁边的 .\songs\）。给 null 就整条曲库都不出现
    /// （保存 / 另存为也不亮）。
    /// </param>
    public MainWindow(
        TokenSource tokens, IClock clock, IAudioSink sink,
        Func<PerformerWindow>? performerFactory, SongLibrary? library)
    {
        InitializeComponent();

        _tokens = tokens;
        _performerFactory = performerFactory;
        _library = library;
        _playback = new PreviewPlayback(sink, clock);

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

        // 提示语住在 Format 里，同一句同时喂给显示那一行和 ToolTip（ToolTip 只在代码里设）；
        // 起手这一下只把「还没载曲子、什么都没选中」那个初始状态摆对（哪一层由 RefreshHint 定）。
        HintText.Text = Format.ReadoutHintPerforming;
        ToolTip.SetTip(HintText, Format.ReadoutHintTooltip);

        // 窗口改宽 = 每小节变宽（固定 4 小节，没有缩放），所以要按新的宽度重算场景
        SizeChanged += (_, _) => RefreshView();
        LanesHost.SizeChanged += (_, _) => RefreshView();
        // ★ 缩略条**自己的**尺寸落定也得重算一次。上面那两处都在布局排到缩略条之前就喊了，
        // 那一刻 NavStrip.Bounds 还是上一次布局留下的旧值（首屏是 0 或几百像素）——
        // 上一版就死在这儿：整张缩略图按一个过期的小宽度被压在条的最左边一小截，
        // 而没有任何人在缩略条真的摆到最终宽度之后再算一次，所以它**一直**错着，
        // 缩放一次窗口才归位（缩放会再喊一次 SizeChanged，正好补上那一帧）。
        NavStrip.SizeChanged += (_, _) => RefreshView();

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

    /// <summary>把窗口变成一个能接收拖放的落点。拖进来的不一定是文件，所以 DragOver 要把「收不收」先说清楚：不说的话光标一直是个禁止符号，用户以为这窗口不吃拖放。</summary>
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

    /// <summary>拖进来的东西里，第一个本机认得出的 MIDI 文件路径；没有就是 <c>null</c>。只要一个。云盘 / 网络位置上的文件拿不到本机路径，那种也当作没有。</summary>
    private static string? FirstMidiPath(IDataTransfer data)
    {
        // TryGetFiles 没有文件时给 null 而不是空数组
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

    /// <summary>导入一个 .mid：读 → 命名 → 存进曲库 → 显示。菜单和拖放走的是同一个它。命名那一步取消不等于失败：曲子照样装上，只是没进曲库（<see cref="_currentName"/> 保持 null）。</summary>
    private async Task ImportFile(string path)
    {
        // 拖进来也好、挑一个文件也好，都是「打开另一首」：手上这份改过还没存就先拦一下
        // （拦在这儿而不是挑文件那一步，拖放那条路才一并盖得住）
        if (!await ConfirmUnsavedAsync(UnsavedScene.SwitchSong, this)) return;

        Song song;
        try
        {
            song = MidiReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // MidiReader 抛的就是中文消息，原样报出来
            ShowError(ex.Message);
            return;
        }

        LoadSong(song, Path.GetFileNameWithoutExtension(path));
        // 刚 LoadSong 完，_edited 已经是 false、曲名还是空，补上来路，剩下的交给 SaveTo
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
        // 先验 tick 装不装得下，再动任何状态：TempoMap.SecondsAt 在 tick 换算出的微秒数
        // 超过 long 的十分之一时会抛「时间跨度太大」，而这是每首曲子进窗口的唯一入口。
        if (!song.TryMeasure(out _, out string? reason))
        {
            // 中文原因来自 TempoMap，原样报出来
            ShowError(reason!);
            return;
        }

        HideMessages();

        // 换一首曲子就得把撤销栈清掉：栈里装的是上一首曲子的 Song 引用
        _editor.Reset();

        _song = song;
        // 这一份是不是曲库里那份，装上来的这一刻还不知道 —— 导入那条路要等命名之后才写进曲库。
        // 所以先清掉，知道的调用方在那两处自己补回来（TryOpenLibrarySong 读出来的、SaveTo 写下去的）。
        // 不清的话「导入了另一首」会留着上一首的曲子当「已存的」，点了演奏弹出来的是别人。
        _storedSong = null;
        // 「这份是哪来的、叫什么、动过没有」三件都随曲子一起换掉，调用方在这之后按自己的来路覆盖
        _currentName = null;
        _importedFrom = null;
        _edited = false;
        // 刚装上来的这一份和盘上那份一致到下一次编辑之前
        _dirty = false;
        _title = title;

        // 换曲子一律重建轨控件：轨数碰巧一样时「就地重挂」是拿另一首曲子的轨接着用上一首的控件，
        // 折叠、改名框这些控件上的状态会跨曲子漏过去。编辑那条路才是「同一首曲子的新一份」
        SyncLanes(rebuildAll: true);
        // 换曲子这一路折叠一律是空的，照旧现问一次
        _playback.Load(song, MutedTracks());

        SongNameBox.Text = title;
        EmptyHint.IsVisible = false;
        JumpBox.Text = "1";

        // 走带条跟着新曲子回位，规矩只此一处（RefreshTransport）
        RefreshTransport();
        JumpBox.IsEnabled = true;

        // 换曲子了：悬停那个音说的是上一份谱面，清掉。清完读数自己回落到选中（多半也是空的）
        ShowHover(null);
        // 工具栏那几样亮不亮的判据都在 RefreshEditState 一处算
        RefreshEditState();

        // 这一趟多半算不出场景（控件刚建出来、宽度还是 0），但位置读数、导航条这些要它；
        // 卷帘自己会在尺寸落定那一帧补上（挂在 TrackLaneView 的 Roll.SizeChanged 上）
        RefreshView();
    }

    /// <summary>
    /// 照着当前这份曲子把控制器和所有轨同步一遍。控制器每次都得换新的
    /// （按曲子建出来的一次性对象，音域、灰显标记、每小节音符数都缓存着了）；
    /// 轨数没变就地重挂（<see cref="TrackLaneView.Rebind"/>），免得更重建时滚动位置被夹回顶部、
    /// 新控件当帧算不出场景（症状是「编辑一下，音轨就消失了」）；轨数变了只能重建。
    /// </summary>
    /// <param name="rebuildAll">
    /// 强制全部重建。换一首曲子时用 —— 控件上的状态（折叠、开着没提交的改名框）属于上一首的轨。
    /// </param>
    private void SyncLanes(bool rebuildAll = false)
    {
        if (_song is not { } song) return;

        // 聚焦轨跟那条轨走，不跟下标走：删掉第 0 条之后下标整体前移，按下标带会把高亮挪到别人身上。
        // 必须在换控制器之前抄下来（控制器一换，旧下标当场作废），换一首曲子一律从头发。
        var focused = rebuildAll ? null : FocusedIdentity();

        _controller = new PianoRollController(song, rebuildAll ? null : MutedTracks());

        if (!rebuildAll && _lanes.Count == song.Tracks.Count)
        {
            for (int i = 0; i < _lanes.Count; i++) _lanes[i].Rebind(_controller, i);
            // 轨数一样就是那几条轨、同一个次序，身份换算回来还是同一个下标，放回去不能省（新控制器聚焦是 0）
            _controller.SetFocusedTrack(FocusIndex(focused));
            return;
        }

        // 折叠是用户对某条轨的标记，不该被一次删轨顺手抹掉；按轨的身份记，不按下标，
        // 必须在 Clear 之前抄下来。换一首曲子一律不带：收起来的轨在试听里不出声（见 MutedTracks）。
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

    /// <summary>此刻聚焦的那条轨的身份。还没建控制器、或者下标已经越界时给 null（= 从头发）。必须在 <c>_controller</c> 换成新的之前调：它读的是旧控制器手里那份曲子。</summary>
    private (int Chunk, int Channel)? FocusedIdentity()
    {
        if (_controller is not { } controller) return null;

        int index = controller.FocusedTrack;
        return index >= 0 && index < _lanes.Count ? _lanes[index].Identity : null;
    }

    /// <summary>身份 → 它现在在第几号。找不到（那条轨被删了）就落到第一条没收起来的轨上 —— 高亮总得落在某一条上。必须在轨控件重建之后调：它读的是新的 <c>_lanes</c>。</summary>
    private int FocusIndex((int Chunk, int Channel)? identity)
    {
        if (identity is { } wanted)
            for (int i = 0; i < _lanes.Count; i++)
                if (_lanes[i].Identity == wanted) return i;

        return PianoRollController.FirstExpanded(CollapsedFlags());
    }

    /// <summary>每条轨收没收起，按下标排一张表（控制器要的正是这个形状）。和 <see cref="MutedTracks"/> 一样每次现问一次控件，不在窗口里另存一份折叠状态。</summary>
    private bool[] CollapsedFlags()
    {
        var flags = new bool[_lanes.Count];
        for (int i = 0; i < _lanes.Count; i++) flags[i] = _lanes[i].IsCollapsed;
        return flags;
    }

    /// <summary>此刻哪几条轨在试听里不发声：收起来的那几条。认轨用的是 <see cref="TrackLaneView.Identity"/> 那一对 <c>(轨块, 声道)</c>，不是下标。每次现问一次控件。</summary>
    private IReadOnlySet<(int TrackIndex, int Channel)> MutedTracks()
    {
        var muted = new HashSet<(int, int)>();
        foreach (var lane in _lanes)
            if (lane.IsCollapsed) muted.Add(lane.Identity);
        return muted;
    }

    /// <summary>
    /// 某条轨收 / 放了。试听与卷帘跟着走，用的是同一份名单：那条轨不出声，
    /// 整曲时长与「多少小节」（<c>AudibleLength</c>）只按听得见的轨算
    /// （见 <see cref="PreviewPlayback.SetMutedTracks"/>）。正在播的话是接着放；
    /// 暂停 / 停止中则要在这儿把播放头拉回新的曲尾，否则读数会一直显示「位置 20 / 12」。
    /// </summary>
    private void OnLaneCollapseChanged(object? sender, EventArgs e)
    {
        var muted = MutedTracks();
        _playback.SetMutedTracks(muted);
        _controller?.SetMutedTracks(muted);

        if (!_playback.IsPlaying && _playback.HasSong && _playback.MusicSeconds > _playback.TotalSeconds)
            _playback.SeekSeconds(_playback.TotalSeconds);

        RefreshView();
    }

    // ==================== 曲库 ====================

    /// <summary>
    /// 「歌曲库」那颗按钮：开曲库窗口（模态）。开窗那一刻现列一遍列表就够 ——
    /// 模态期间主窗口动不了，列表不会过期。两件会改盘的事（打开、删除）落在本窗口。
    /// 打开那一支要先关窗再装曲子：<see cref="LoadSong"/> 会重建全部控件、重算场景，
    /// 模态框还压在头上时做这件事，用户看到的是一个卡住的对话框。
    /// </summary>
    private async void OnLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (_library is not { } library) return;

        var dialog = new SongLibraryWindow(library, _tokens, _currentName);

        dialog.OpenRequested += async (s, name) =>
        {
            if (s is not SongLibraryWindow window) return;

            // 打开另一首会把手上的改动丢掉：先问一句。模态挂在曲库窗口上 —— 它压在头上，
            // 弹到主窗口背后用户看不见
            if (!await ConfirmUnsavedAsync(UnsavedScene.SwitchSong, window)) return;

            // 读不出来就写进窗口的页脚、窗口留着（它压在头上，主窗口提示行看不见）；装上了才关窗
            if (TryOpenLibrarySong(name) is { } error) window.ShowMessage(error);
            else window.Close();
        };

        dialog.DeleteRequested += OnLibraryDeleteRequested;

        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// 曲库里某一首确认要删（面板已经问过一句了）。删掉的要是当前正开着的那一首，
    /// 手上这份留着 —— 它还在内存里、可能还有没存过的编辑，按保存会重新问个名字。
    /// 回话走那个曲库窗口的页脚（<paramref name="sender"/>），不是主窗口那条提示行。
    /// </summary>
    private void OnLibraryDeleteRequested(object? sender, string name)
    {
        if (_library is not { } library) return;
        if (sender is not SongLibraryWindow dialog) return;

        try
        {
            library.Delete(name);
        }
        catch (InvalidDataException ex)
        {
            dialog.ShowMessage(ex.Message);
            return;
        }

        if (_currentName == name)
        {
            _currentName = null;
            // 曲库里那一份没了，手上这份成了唯一的一份 —— 再点演奏没有「已存的」可递
            _storedSong = null;
            dialog.ShowMessage($"「{name}」已从曲库删掉。手上这份还在，按「保存」可以再存回去。");
        }
        else
        {
            dialog.ShowMessage($"「{name}」已从曲库删掉。");
        }

        // 那一行得当场消失
        dialog.RefreshLibrary(_currentName);
    }

    /// <summary>
    /// 曲库窗口里双击了某一首：把曲子读出来装上。
    /// 装上了返回 null，没装上返回那句要报的中文 —— 报错写哪儿交给调用方（这一趟是模态框
    /// 底下那次点击引起来的，提示行写了也看不见），也顺手回答了「窗口关不关」。
    /// </summary>
    private string? TryOpenLibrarySong(string name)
    {
        if (_library is not { } library) return null;

        try
        {
            // 曲库里躺着的是一份标准 MIDI（**你的文件**）+ 一份本程序自己的缓存
            //（songs\.work\<名字>.mproj）。**有缓存就用缓存** —— 移调、音符被删光的轨、轨的身份
            // 全都在里面，这一路是无损的；缓存不在、或者读不出来（截断 / JSON 坏了 / 版本不对）
            // 就**静默**读 .mid 继续，不弹窗、不拦路 —— 这就是「缓存」两个字的定义。
            //
            // ⚠️ 代价写在明处：降级那一趟**移调和删光的轨会一起没掉**，程序不会拦你。
            var loaded = SongCache.Load(library, name);
            var song = loaded.Song;

            LoadSong(song, name);
            // LoadSong 把这四个都清空了（它不知道新来的是哪一份），所以在这儿补上 ——
            // **补在它清之后**，顺序反了等于没读缓存
            _currentName = name;
            _title = name;
            _importedFrom = loaded.Header?.ImportedFrom;
            _edited = loaded.Header?.Edited ?? false;
            // 刚才是从曲库里读出来的这一份 —— 它**就是**「曲库里存的那份」
            _storedSong = song;

            // 不用去挪列表的高亮：这一支成功就走到底，调用方紧接着把窗口关了
            return null;
        }
        catch (InvalidDataException ex)
        {
            // 读不出来的曲子：把那句中文交出去，**不动**手上正开着的那一份
            return ex.Message;
        }
    }

    /// <summary>
    /// 「保存」：已经有曲名就写回那一首，还没有（导入时没命名、或者刚从曲库删掉）就先问一个。
    /// 本体在 <see cref="SaveAsync"/>，工具栏上那颗按钮和 <c>Ctrl+S</c> 走的是同一件事。
    /// </summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>
    /// 「保存」和 <c>Ctrl+S</c> 共用的入口。返回 <see cref="Task"/> 而不是 <c>async void</c>：
    /// 键盘那一路（<see cref="OnWindowKeyDown"/>）不是 async 的，只能把它丢掉（<c>_ =</c>）。
    ///
    /// ⚠️ <b>「保存」永远只管曲库</b>：两条路最后都落在 <see cref="SaveTo"/> 上，
    /// **一个文件框都不弹**。要存到任意位置去的是「另存为…」（<see cref="OnSaveAsClick"/>），
    /// 那是另一件事 —— 54 号票之前这两件事共用过一个方法，别再把它们合回去。
    /// </summary>
    private async Task SaveAsync()
    {
        if (_library is not { } library) return;

        // 有曲名：写回那一首（名字一样就是覆盖这一首）
        if (_currentName is { } name)
        {
            SaveTo(library, name);
            return;
        }

        // 还没进曲库（导入时没命名、或者刚从曲库删掉）：先问一个名字，问到了再走同一条写库的路。
        // 取消不是失败 —— 曲子还在手上，就是没进曲库。
        if (await AskNameForSaveAsync(library, "保存", _title) is not { } asked) return;

        SaveTo(library, asked);
    }

    /// <summary>
    /// 「另存为…」—— 把此刻手上的谱面写到你挑的**任意一个路径**上去，写成标准 MIDI。
    ///
    /// <b>和「保存」是两件不同的事</b>：那一个往**曲库**里存（覆盖那一首、带缓存），
    /// 这一个往**外面**存 —— **不入库**：不写曲库成员、不写缓存、也不问曲名
    /// （名字由文件框那一格决定）。所以存到曲库目录里去，那儿会多出一首，那是**对的**：
    /// 它本来就住在 <c>songs\</c>，不是这条路径顺手入了库。
    ///
    /// 写出去的是模型，不是屏幕：<c>Track.Transpose</c>、卷帘视口、播放头都不进文件。
    /// 写完报一句，用户才知道盘上落了文件。
    /// </summary>
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (_song is not { } song) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            SuggestedFileName = SongNameBox.Text ?? "未命名",
            DefaultExtension = "mid",
            FileTypeChoices = new[] { MidiFileType }
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            // 用户取消了，不是失败
            return;
        }

        try
        {
            // 写出去的是此刻手上的那一份（含刚做完、还没撤销的编辑），不是屏幕
            MidiWriter.Write(song, path);
            ShowNotice($"已另存到 {path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // 和导入同一条规矩：MidiWriter 抛的是中文消息，原样报出来
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 问一个要写进曲库的名字，撞名时先问一句 —— <c>SongLibrary.Write</c> 是覆盖语义，
    /// 手滑打出一个已有的名字就会拿手上这份把另一首曲子悄悄换掉，撤不回来。
    /// 放行的两种：名字就是当前这首，或者曲库里没这个名字；用户说不覆盖就带着刚打的名字再问一次。
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
    /// 把手上这份写进曲库的某个名字，**两条路都走、无条件**：
    /// ① <c>songs\&lt;名字&gt;.mid</c>（标准 MIDI，你的文件：能拷给别人、能用别的软件打开）
    /// ② <c>songs\.work\&lt;名字&gt;.mproj</c>（本程序自己的缓存：移调、删光的轨、轨的身份、列表那两个标记）。
    /// 写出去的是此刻手上的那一份（含刚做完、还没撤销的编辑），不是屏幕 —— 卷帘视口、播放头从来不进文件。
    ///
    /// ⚠️ 缓存不判断「需不需要」：它跟着主文件一起更新，**没有条件、没有判据、没有表**。
    ///
    /// ⚠️ 填 header 用的是**此刻**的 <c>_edited</c>（这份东西到现在动过没有）—— 曲库列表上
    /// 「编辑过」三个字的唯一来源。`_edited` 是**粘性**的、存盘不清它（清的只有 <see cref="LoadSong"/>），
    /// 所以这儿读到的就是「保存这一刻」那个值；顺序要是反了（先清再填），列表上永远是「没改过」。
    /// </summary>
    private void SaveTo(SongLibrary library, string name)
    {
        if (_song is not { } song) return;

        try
        {
            SongCache.Save(library, name, song, SongCache.HeaderFor(name, song, _edited, _importedFrom));
        }
        catch (InvalidDataException ex)
        {
            ShowError(ex.Message);
            return;
        }

        _currentName = name;
        _title = name;
        SongNameBox.Text = name;
        // 刚写下去的这一份从此就是「曲库里存的那份」—— 手上这份和它一致到下一次编辑之前
        _storedSong = song;
        // 手上这份和盘上那份又对上了：工具栏上「保存」那一格褪回素的
        _dirty = false;
        RefreshEditState();
        ShowNotice($"「{name}」已存进曲库：{library.Directory}");
    }

    /// <summary>
    /// 改名 —— 就是把文件换个名字，内容一个字节都不碰。唯一的入口是顶栏那格「歌曲名」框
    /// （<see cref="OnSongNameKeyDown"/>）。改的要是当前正开着的那一首，曲名框得跟着换，
    /// 不然界面上会同时存在两个名字。没改成的那一路不用管曲库那一行：盘上什么都没变。
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

        ShowNotice($"「{oldName}」改成了「{newName}」。");
    }

    /// <summary>
    /// 曲名框：回车改名。这一格显示的就是曲名，所以回车 = 「这首叫这个」；
    /// 还没进曲库的（导入时取消了命名）回车就顺势存进去，不然这一格是个死框。
    /// 不合法就报一句中文、把框退回原名，判据和曲库是同一个
    /// <see cref="SongLibrary.IsUsableName"/>。
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
            // 还没进曲库：起了名就等于存进去。名字已经是别人的就拦下 —— 往曲名框里打一个已有的名字，
            // 本意多半是「这首叫这个」而不是「把那一首换掉」；真要覆盖那一首，把它打开再按「保存」
            //（保存是覆盖语义）。「另存为…」不往曲库里写，指它没有用。
            if (library.Contains(name))
            {
                ShowError($"曲库里已经有一首「{name}」了，换个名字。要覆盖那一首，把它打开再按「保存」。");
                SongNameBox.Text = _title;
                return;
            }

            SaveTo(library, name);
            return;
        }

        if (oldName == name)
        {
            HideMessages();               // 名字没变，但别留着一句过期的话
            return;
        }

        RenameTo(library, oldName, name);
    }

    // ==================== 未保存 ====================

    /// <summary>
    /// 「手上这份改过还没存」时先拦一下，返回 true = 可以继续。
    ///
    /// 三处入口走的是同一条（切曲子 / 点演奏 / 关窗口），差别只有那颗弹窗第二行说什么、第二颗叫什么 ——
    /// 那份表在 <see cref="UnsavedPrompt.Of"/> 里，这儿只判「要不要问」和「用户选了哪条出口」。
    ///
    /// 三颗出口：**是** = 先存再继续；**第二颗** = 不存也继续；**✕ / Esc** = 留在原地（返回 false）。
    /// 判据用的是 <c>_dirty</c>（工具栏「保存」那一格穿不穿主色用的是同一个），不另起一套。
    /// </summary>
    /// <param name="owner">那颗弹窗模态挂在谁身上（曲库那一处挂曲库窗口）。</param>
    private async Task<bool> ConfirmUnsavedAsync(UnsavedScene scene, Window owner)
    {
        if (!_dirty) return true;

        var choice = await UnsavedChangesDialog.AskAsync(owner, scene, _title);

        // ✕（和 Esc）：什么都不做，留在原地
        if (choice == UnsavedChoice.Cancel) return false;

        // 「是」= 先存再继续
        if (choice == UnsavedChoice.Save) return await SaveFirstAsync();

        // 第二颗：不存也继续 —— 它只是「继续」的另一种方式
        return true;
    }

    /// <summary>
    /// 「是」那一支：先存再继续。返回 false = 没存下去，那就**不许**往下走。
    ///
    /// 存不下去的两种：这一首还没进曲库、而问名字那一步被取消了；或者压根没配曲库。
    /// 两种都得停在这儿 —— 继续下去正是要把改动丢掉，而用户刚才选的是「存」。
    /// </summary>
    private async Task<bool> SaveFirstAsync()
    {
        await SaveAsync();
        return !_dirty;
    }

    /// <summary>
    /// 「关窗口」那一处问完之后真去关。<see cref="_closeConfirmed"/> 是先放行再关：
    /// 不然 <see cref="OnClosing"/> 会把自己再拦一次。
    /// </summary>
    private async Task ConfirmCloseAsync()
    {
        bool proceed = await ConfirmUnsavedAsync(UnsavedScene.CloseWindow, this);

        _askingBeforeClose = false;
        if (!proceed) return;

        _closeConfirmed = true;
        Close();
    }

    // ==================== 编辑脊柱 ====================

    /// <summary>
    /// 把一份编辑过的曲子换上，并把界面按原样恢复。三件事必须一起做，少一件用户就会看见
    /// 「改一下就被弹走了」：视口与选中按原样放回去（选中集 = 坐标本身，见 <see cref="NoteRef"/>，
    /// 编辑换的是内容不是身份）；播放头用新的速度表把原来那个 tick 换算成秒再 Seek 回去；
    /// 试听换谱（<c>Load</c> 会先松开正在响的音，等于顺手停了播放）。「改没改」比引用。
    /// </summary>
    /// <param name="selectionAfter">
    /// 编辑之后该选中的那组音。不传（null）= 手上这一串原样留着，那是绝大多数命令
    /// （挪、拉、改速度、改移调、撤销、重做…），它们的坐标在新曲子上仍然成立。
    /// 传了就是「换掉」：只有命令自己改了「选中谁」时才传，而且坐标必须是照着新曲子算的。
    /// </param>
    private void ApplySong(Song edited, IReadOnlyList<NoteRef>? selectionAfter = null)
    {
        if (ReferenceEquals(edited, _song)) return;

        long playheadTick = _playback.PlayheadTick;
        long viewStartTick = _controller?.ViewStartTick ?? 0;
        // 必须在 SyncLanes 之前抄：下面换控制器，旧的那个当场作废。
        // 抄下来的是坐标本身，不是它指向的音 —— 这才是不必重新认音的原因
        var selection = selectionAfter ?? _controller?.SelectedNotes.ToArray() ?? Array.Empty<NoteRef>();

        _song = edited;
        // 粘性标记：动过就是动过，撤销回原样、存盘都不清
        _edited = true;
        // 手上这份和盘上那份从此对不上，工具栏上「保存」那一格穿上主色，存盘或换曲子才褪下来
        _dirty = true;
        SyncLanes();

        // 视口照旧有效：小节刻度不受任何一条编辑命令影响，SetViewStart 还会夹一次，曲子变短也不越界
        _controller?.SetViewStart(viewStartTick);
        // 选中集放回新控制器上：坐标是身份，原样交回去就行。
        // 认不出的（音被删了、轨被删了）由 SetSelection 丢掉 —— 它不抛
        _controller?.SetSelection(selection);

        // 收起来的轨照旧不出声，名单从控件现问（见 MutedTracks）
        _playback.Load(edited, MutedTracks());
        _playback.SeekSeconds(edited.TempoMap.SecondsAt(playheadTick));
        // 试听被换谱顺手停了，走带条的亮灭跟着回位 —— 走 RefreshTransport，不在这儿手写两行
        RefreshTransport();

        // 悬停那个音说的可能是刚被这条命令改掉（或者删掉）的音，而读数条只跟着鼠标动才更新 ——
        // 鼠标这会儿多半正压在那个按钮上，指针不动的话它会一直挂着一条已经作废的读数。
        // 清掉之后读数回落到选中
        ShowHover(null);
        RefreshEditState();
        RefreshView();
    }

    /// <summary>
    /// 工具栏那几样跟着「手上有没有曲子、动过没有、存过没有」变的东西，一处算清：
    /// 撤销 / 重做、操作、演奏、速度框、歌曲名格、存盘组、歌曲库。
    /// 换曲子、每次编辑、存盘之后都调它。
    /// </summary>
    private void RefreshEditState()
    {
        // 撤销 / 重做：没得撤就置灰。菜单项置灰一样点不动
        UndoMenuItem.IsEnabled = _editor.CanUndo;
        RedoMenuItem.IsEnabled = _editor.CanRedo;
        // 没曲子可动时「操作」整组灰：「操作」里那些命令动的都是手上这份谱面
        OperationMenu.IsEnabled = _song is not null;

        // 演奏是「弹手上这一首」，没曲子可弹就灰着 —— 开出一个没东西可弹的窗口比灰着更让人困惑
        PerformerButton.IsEnabled = _song is not null;

        BpmBox.IsEnabled = _song is not null;

        // 「歌曲名」那一格整个藏掉，不是置灰：没装曲子时它不是「按不动」，是「这儿现在没有东西」。
        // 载入曲子之后同一个判断当场把它放回来，不用重启
        SongNameCell.IsVisible = _song is not null;
        SongNameBox.IsEnabled = _song is not null;

        // 没有曲库就存不了（组装点没给），灰着比按了没反应诚实
        SaveButton.IsEnabled = _song is not null && _library is not null;
        SaveAsButton.IsEnabled = SaveButton.IsEnabled;

        // 「歌曲库」的判据只有曲库这一半：没曲库时按下去会开出一个空窗口，灰着比那诚实。
        // 它也是空状态里唯一亮着的那颗 —— 第一首得从那儿拿进来
        LibraryButton.IsEnabled = _library is not null;

        // 有没存下去的东西时**只有「保存」那一格**穿主色：丢的是「你改了没存」这件事，
        // 「另存为」没这个意思，组的框和别的入口一个都不动（见 42 号工单）
        SaveButton.Classes.Set("dirty", _dirty);
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
    /// 轨道头上的移调步进器被按了：参数是新的绝对半音数。
    /// 移调只换 <c>Track.Transpose</c>，音符一个字节都不动，所以和改 BPM 共用同一套重绘。
    /// </summary>
    private void OnTransposeRequested(object? sender, int semitones)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.SetTranspose(song, lane.TrackIndex, semitones));
    }

    /// <summary>
    /// 轨道头上的音色下拉挑了新的一号。只影响试听（发给游戏时永远是口琴那套键位），
    /// 谱面一个字节都不动：和移调、改速度共用同一套重绘，要换的只有试听那张表。
    /// </summary>
    private void OnProgramRequested(object? sender, int program)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.SetProgram(song, lane.TrackIndex, program));
    }

    // ==================== 卷帘编辑：事件 → 命令 ====================

    /// <summary>
    /// 卷帘上拖完一组音符（方向键微调也走这一条）。位移是已经夹过的：<c>PianoRollLane</c>
    /// 在发事件之前夹一次，<see cref="NudgeNotes"/> 在调命令之前夹一次。
    /// 选中集不用管：命令换的是内容，坐标指着的那批音一个都没换号。
    /// </summary>
    private void OnNotesMoved(object? sender, NoteMoveRequest request)
    {
        if (_song is not { } song) return;
        ApplySong(_editor.MoveNotes(song, request.Notes, request.DeltaTicks, request.DeltaPitch));
    }

    /// <summary>
    /// 卷帘上拖完某条边。请求里是绝对的起点与时值，不是增量。被拉的那个音身份不变
    /// （<c>SetNoteSpan</c> 只 <c>with</c> 起点和时值），所以选中集照旧不用管。
    /// </summary>
    private void OnNoteResized(object? sender, NoteResizeRequest request)
    {
        if (_song is not { } song) return;
        ApplySong(_editor.SetNoteSpan(song, request.Note, request.StartTick, request.LengthTicks));
    }

    /// <summary>
    /// <c>Delete</c> / <c>Backspace</c>：把当前选中的音整批删掉（一次调用 = 撤销栈上一格）。
    /// 删完选中落到时间上最近的邻居，不清空 —— 连续删谱时手不用重新找位置。
    /// 落的规则：拿被删那组里最靠右的那个音当基准，先找它右边最近的一个
    /// （<c>StartTick</c> 严格大于基准）；右边没有了就落回左边最近的一个；两边都没有就是空选中。
    /// 基准取最靠右而不是最靠左：选中集可以是不挨着的，取最靠左会让落点掉进两次点击中间的缝里；
    /// 落在哪条轨也由它定，不跟聚焦轨走。只有这条路要显式交一份新的选中集：被删的那几个音
    /// 连身份一起没了，而落点是编辑之后才算出来的。
    /// </summary>
    private void DeleteSelection()
    {
        if (_song is not { } song || _controller is not { } controller) return;

        // 先抄下来：下面换曲子之后控制器里的那一串就作废了
        var doomed = controller.SelectedNotes.ToArray();
        if (doomed.Length == 0) return;

        // 基准：最靠右的那个被删音
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
    /// 删完之后选中该落到哪儿：<paramref name="track"/> 上起点严格大于 <paramref name="edge"/>
    /// 的第一个音；没有就退回起点小于等于它的最后一个。那条轨空了就给空表。
    /// 返回的是坐标（身份，<see cref="NoteRef"/>），在那份新曲子上现算现取。
    /// 起点取严格大于而不是大于等于：删掉的那一段里可能还有没被选中的音留在原地。
    /// </summary>
    private static List<NoteRef> NeighbourAfterDelete(Song song, int track, long edge)
    {
        if (track < 0 || track >= song.Tracks.Count) return new List<NoteRef>();

        var notes = song.Tracks[track].Notes;
        for (int i = 0; i < notes.Count; i++)
            if (notes[i].StartTick > edge) return new List<NoteRef> { new(track, notes[i].Id) };

        // 右边没有了：退回左边最近的一个（音符按起点升序，所以是最后一个）
        return notes.Count > 0
            ? new List<NoteRef> { new(track, notes[^1].Id) }
            : new List<NoteRef>();
    }

    /// <summary>
    /// 那份曲子里的这个音；按身份找（见 <see cref="NoteRef"/>），认不出来时给 null。
    /// 只剩删音符那一条路用它（要拿被删那组里最靠右那个的起点当落点基准）；内容一模一样的
    /// 两个音也分得开。扫一遍而不是建索引表：一次删除手势里只走几十遍。
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
    /// 删掉一整条轨（轨头上那个二次确认已经按过了）。删完明确清空选中：<see cref="NoteRef"/>
    /// 里轨那一半是下标，删掉一条之后剩下的整体前移，手上那串坐标会被解读成别的位置；
    /// 而且它不会认不出来 —— 身份从 1 开始按轨连号发（见 <see cref="NoteIdentity"/>），
    /// 换一条轨照样能撞上一个号，<c>SetSelection</c> 那道「认不出就丢掉」拦不住。
    /// </summary>
    private void OnTrackDeleteRequested(object? sender, EventArgs e)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(_editor.DeleteTrack(song, lane.TrackIndex), Array.Empty<NoteRef>());
    }

    /// <summary>
    /// 抽掉这条轨上的一段（轨道头上两个小节号填好了、预览那一行也看过了）。传进来的已经是 tick：
    /// 小节 → tick 的换算在控件里做完了。抽完明确清空选中：被剪断的音会拿到新身份
    /// （见 <c>ISongEditor.CutRange</c>），被前移的保留身份，于是「原样留着选中集」一半对一半错。
    /// </summary>
    private void OnTrackCutRequested(object? sender, CutRangeRequest request)
    {
        if (_song is not { } song || sender is not TrackLaneView lane) return;
        ApplySong(
            _editor.CutRange(song, lane.TrackIndex, request.StartTick, request.EndTick),
            Array.Empty<NoteRef>());
    }

    /// <summary>
    /// 卷帘上的选中变了。选中集是全局的（一个控制器管所有轨），所以别的轨的高亮也得跟着变 ——
    /// 整窗重画一遍。
    /// </summary>
    private void OnLaneSelectionChanged(object? sender, IReadOnlyList<NoteRef> selected)
    {
        RefreshReadout();
        RefreshView();
    }

    /// <summary>
    /// 卷帘上按了一下，焦点轨跟到那一条去了。那一头自己已经重画过，这里管的是别的轨：
    /// 灭掉上一条焦点得整窗推一遍。读数和选中集都不用动 —— 换焦点不改选中集（见 <see cref="MoveFocus"/>）。
    /// 不滚进视野：鼠标点的东西本来就在眼前，再滚一下反而是画面在手下抽搐。
    /// </summary>
    private void OnLaneFocusChanged(object? sender, EventArgs e) => RefreshView();

    // ==================== 改速度 ====================

    /// <summary>
    /// 速度框：回车提交。写进来的必须是具体的拍 / 分（比如 76），不是百分比也不是倍率 ——
    /// 用户要能对着原曲的标记直接填。不合法就报一句中文、把框退回原值；提交之后焦点从框里放开。
    /// </summary>
    private void OnBpmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _song is not { } song) return;
        e.Handled = true;

        // double.TryParse 连 "NaN" / "Infinity" 都收，所以解析成功之后还要自己判一次有限数
        bool parsed = double.TryParse(BpmBox.Text?.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double bpm);

        // 先放开焦点，否则框里写不进字（RefreshView 会跳过有焦点的框）
        ReleaseEditFocus();

        if (!parsed || !double.IsFinite(bpm) || bpm < SongEditor.MinBpm || bpm > SongEditor.MaxBpm)
        {
            ShowError($"速度要填 {SongEditor.MinBpm:0} 到 {SongEditor.MaxBpm:0} 之间的数（拍/分），"
                + $"「{BpmBox.Text}」不算。");
            // 退回原值，不留一个看着生效了的数在框里
            BpmBox.Text = CurrentBpmText(song);
            return;
        }

        HideMessages();
        ApplySong(_editor.SetBpm(song, bpm));

        // 把框写成规范样子。就算是空操作也得写：打的是 ` 76 ` 或者 `076` 时算出来的数和生效的值
        // 一模一样，SetBpm 返回同一个引用、ApplySong 提前返回、跳过 RefreshView，
        // 于是用户回车了，框里还留着他打的那串原文。
        BpmBox.Text = CurrentBpmText(_song!);
    }

    /// <summary>
    /// 速度框此刻该写的数：具体的拍 / 分（四舍五入到整数）。取的是 tick 0 的基准速度，
    /// 不是播放头那一点的速度 —— 这一格是输入框，显示的数必须就是回车之后生效的那个数
    /// （<see cref="SongEditor.SetBpm"/> 写的就是基准值再整体等比缩放，所以 tick 0 是它的逆）。
    /// </summary>
    private string CurrentBpmText(Song song) =>
        Math.Round(song.TempoMap.BeatsPerMinuteAt(0))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>把焦点从输入框里放开，交给卷帘那一块（它可聚焦，见 MainWindow.axaml）。</summary>
    private void ReleaseEditFocus() => LanesHost.Focus();

    // ==================== 演奏器 ====================

    /// <summary>
    /// 工具栏上「演奏」—— 另开一个独立窗口，把选中的轨弹到别的程序里去。
    /// 本窗口不 new 它、也不知道它要什么：要一个过来、把**此刻该弹的那一份**递进去
    /// （<see cref="SongForPerformer"/>，走 <see cref="PerformerWindow.LoadSong"/>）、
    /// 挂到自己名下、Show —— 挂了 owner 之后主窗口一关它就跟着关。
    ///
    /// 改过还没存就先问一句：这一处弹的是**曲库里存的那份**，草稿还在编辑器里 ——
    /// 所以那颗弹窗的第二颗叫「用已存的」而不叫「丢掉」，也不穿警示色（见 <see cref="UnsavedPrompt.Of"/>）。
    ///
    /// 取舍必须在**问完之后**做：上面那句「是」（先存再继续）存完之后，
    /// 「曲库里存的那份」正好变成了手上这一份。
    /// </summary>
    private async void OnPerformerClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedAsync(UnsavedScene.Perform, this)) return;

        if (SongForPerformer(_song, _storedSong, _dirty) is not { } song) return;

        if (_performerFactory?.Invoke() is not { } window) return;

        // 窗口是复用的（同一时刻只允许一个，F6 急停靠的是低层键盘钩子，见组装点那个工厂），
        // 所以每按一次都得重递一次：换了曲子再按，窗口里必须换成新那首。
        // 递完（挑轨要走一遍全曲音符，长曲子几十到几百毫秒）再显 / 唤 —— 窗口出来就是装好的样子，
        // 不用先露一下上一首、或者空状态，再改。
        await window.LoadSong(song);

        // 组装点复用的那个窗口可能已经显示着了，再 Show 一次会抛
        if (!window.IsVisible) window.Show(this);
        else window.Activate();
    }

    /// <summary>
    /// 「点演奏」按下去之后该拿哪一份曲子去递 —— 这个判断本身就是一张三行的表。
    ///
    /// 提成一个纯函数是为了能拿真值喂它：这一处最容易做错的一格是「改过还没存」那条路
    /// （<see cref="_song"/> 会跟着编辑走，顺手把它递过去就是把草稿递进去了），
    /// 而它坏掉**不报错** —— 只是弹出来的东西和弹窗上那句「用已存的」对不上。
    ///
    /// <list type="number">
    /// <item>手上没有曲子 → <c>null</c>：什么都不该发生，不开那个空窗。
    /// 工具栏那颗按钮本来就是灰的（判据是同一个 <c>_song is not null</c>，见 <see cref="RefreshEditState"/>），
    /// 这一条是兜底。</item>
    /// <item>手上这份和盘上那份对得上（<paramref name="dirty"/> 为假）→ 手上这份：
    /// 它**就是**曲库里存的那份，编辑器里没有任何没落盘的东西。</item>
    /// <item>改过还没存 → <paramref name="stored"/>（曲库里存的那份，草稿留着）。
    /// 还没进过曲库的没有这一份，那是 <c>null</c> —— 手上这份是唯一的一份，只能递它。</item>
    /// </list>
    /// </summary>
    /// <param name="current">手上这份（<see cref="_song"/>）。</param>
    /// <param name="stored">曲库里存的那份（<see cref="_storedSong"/>）。</param>
    /// <param name="dirty">手上这份有没有没落盘的改动（<see cref="_dirty"/>）。</param>
    public static Song? SongForPerformer(Song? current, Song? stored, bool dirty)
        => current is null ? null
            : dirty ? stored ?? current
            : current;

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
    /// 鼠标按它和空格键走的是同一条（见 <see cref="OnWindowKeyDown"/> 里的空格那一支）。
    /// </summary>
    private void TogglePlayback()
    {
        if (_controller is null) return;
        if (_playback.IsPlaying) PausePlayback();
        else StartPlayback();
    }

    /// <summary>
    /// 从当前位置开始播。<see cref="PreviewPlayback.Play"/> 那句 <c>Seek(MusicNow)</c>
    /// 一个人管三种情况（从头、暂停之后接着、停止之后再放），这里不必分。
    /// </summary>
    private void StartPlayback()
    {
        if (_controller is null) return;
        _playback.Play();
        RefreshTransport();
        RefreshView();
    }

    /// <summary>
    /// 停在原地。和 <see cref="StopPlayback"/> 的差别只有两样：不动视野、按钮上写「继续」——
    /// <c>SnapViewToBar</c> 是「这段我听完了」的意思，暂停要的恰恰是「就在这，别动」。
    /// </summary>
    private void PausePlayback()
    {
        _playback.Pause();
        RefreshTransport();
        RefreshView();
    }

    private void OnRestartClick(object? sender, RoutedEventArgs e) => RestartPlayback();

    /// <summary>重头播放：播放头回开头、视野回第一小节，然后立刻开始放。</summary>
    private void RestartPlayback() => SeekBarAndPlay(0);

    /// <summary>
    /// 回跳一小节并播放（绑在 Shift + 空格 上）。退的是整整一小节的预备：在<em>当前小节 − 1</em>
    /// 的小节头上起播，已经在第 1 小节就还在第 1 小节（夹住）。
    /// 「退到哪」和 <see cref="RestartPlayback"/> 是同一套算法，只是那一格写 0、这一格写
    /// 当前小节 − 2（<see cref="PianoRollController.BarOfTick"/> 给 1 起的小节号，
    /// <see cref="PianoRollController.TickOfBarClamped"/> 收 0 起的，减 2 不是笔误）。
    /// </summary>
    private void BackOneBarAndPlay()
    {
        if (_controller is null) return;

        int bar = _controller.BarOfTick(_playback.PlayheadTick);   // 1 起：现在在第几小节
        SeekBarAndPlay(bar - 2);                                    // 0 起：上一小节
    }

    /// <summary>
    /// 寻到某一小节的小节头，然后立刻开始放 —— ↻ 和 Shift+空格 共用的那一半。
    /// 不硬写 <c>SeekSeconds(0)</c>：照抄 <see cref="OnNavSeek"/> 的算法，三处对「一小节从哪一秒
    /// 开始」必须是同一个定义。这一步也不做 <c>Stop()</c>：正在播的时候按它，
    /// <see cref="PreviewPlayback.Play"/> 拿到的就是刚寻过去的位置，自然变成「从那儿重放」。
    /// 能不能放以那颗播放键的 <c>IsEnabled</c> 为准 —— Shift+空格 是按键，绕得过按钮的灰。
    /// </summary>
    private void SeekBarAndPlay(int barZeroBased)
    {
        if (_controller is null) return;
        if (!PlayButton.IsEnabled) return;

        _controller.CenterOnBar(barZeroBased);
        _playback.SeekSeconds(_controller.Song.TempoMap.SecondsAt(_controller.TickOfBarClamped(barZeroBased)));
        // 之后的事全归 StartPlayback：它 RefreshTransport（按钮亮灭 / 字）+ RefreshView
        //（红线、「位置」读数、缩略图都在里面），这儿不额外补那两下
        StartPlayback();
    }

    /// <summary>停下：松掉所有正在响的音，并把视图对齐到小节线。</summary>
    private void StopPlayback()
    {
        _playback.Stop();
        _controller?.SnapViewToBar();
        RefreshTransport();
        RefreshView();
    }

    private void OnPlaybackFinished(object? sender, EventArgs e)
    {
        // 放完了：和按停止一样收尾，光标留在原地。状态上走的是 Stop 不是 Pause，
        // 所以按钮回到「▶ 播放」，不会卡在「继续」上
        _playback.Stop();
        _controller?.SnapViewToBar();
        RefreshTransport();
        RefreshView();
    }

    /// <summary>
    /// 走带条那两颗按钮的字和亮灭。播放状态一变就调它。以播放器为准、不以「上一次点了什么」为准：
    /// 暂停、停止、放完自动停、换曲子都能把状态改掉，靠记一个字段迟早会和真身对不上。
    /// </summary>
    private void RefreshTransport()
    {
        PlayButton.Content = _playback.IsPlaying ? "⏸ 暂停"
            : _playback.IsPaused ? "▶ 继续"
            : "▶ 播放";

        // 判据和换曲子那儿一致：有轨才放得响，一条轨都没有的谱面按了也是白按
        PlayButton.IsEnabled = _song is { Tracks.Count: > 0 };
        // ↻ 的判据跟播放键一模一样：它的意义就是「放」，没东西可放时亮着等于承诺一件做不到的事
        RestartButton.IsEnabled = _song is { Tracks.Count: > 0 };
    }

    /// <summary>
    /// 导航条落到了某一小节（0 起，已吸附到小节线）。它搬的是播放头，不只是视图 ——
    /// 松手后红线出现在新位置，而那也正是「从当前位置播放」的起点；视图顺手把小节摆到屏幕中间。
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

    /// <summary>
    /// 「跳到 __ 小节」：回车生效，越界的小节号夹到首尾（输 999 的意思就是「去最后」）。
    /// 回车之后焦点从框里放开，而且放在校验之前（照抄 <see cref="OnBpmKeyDown"/> 的次序）：
    /// 跳过去就是为了听，接着按下去的十有八九是空格，焦点还在框里的话那一下就打不进曲子；
    /// 报了错、框退回原值之后焦点一样要出来。
    /// </summary>
    private void OnJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _controller is null) return;
        e.Handled = true;
        ReleaseEditFocus();

        if (!int.TryParse(JumpBox.Text?.Trim(), out int bar))
        {
            // 不是数：退回当前小节，不留一个看着生效了的错值
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
    /// 窗口级快捷键：空格播放 / 暂停、撤销 / 重做（Ctrl+Z、Ctrl+Y、Ctrl+Shift+Z）、保存（Ctrl+S）、
    /// 方向键微调、Ctrl+←/→ 定位、Ctrl+↑/↓ 换聚焦轨。
    /// 这一段就是屏幕上那两行提示的真身（<see cref="Format.ReadoutHintPerforming"/> /
    /// <see cref="Format.ReadoutHintEditing"/>），这里动的每一个键那边那行字都得跟着动。
    ///
    /// 方向键：<c>←/→</c> 移时间、<c>↑/↓</c> 移音高、<c>Shift+←/→</c> 改时值、
    /// <c>Ctrl+←/→</c> 在焦点轨内前后跳、<c>Ctrl+↑/↓</c> 在轨之间上下走（见 <see cref="MoveFocus"/>）。
    /// <c>Delete</c> / <c>Backspace</c> 删掉选中（见 <see cref="DeleteSelection"/>），<c>Esc</c> 放开选中。
    /// 隧道阶段接进来，先于任何控件拿到按键；焦点在输入框里时整个让开 ——
    /// 那时左右键归光标用、Ctrl+Z 归输入框自己的撤销。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Esc：先收掉「删掉这条轨？」和「抽掉一段」那两问（它们不是弹窗，只是轨道头上换了一排控件），
        // 都没收掉时再把选中的音放开 —— 屏幕上正摆着一问一答时，Esc 的意思是「我不回答」。
        // 只放音，不动焦点轨：焦点轨一直有一条 —— 放开选中之后按 Ctrl+←→ 跳的还是那条轨上的音。
        if (e.Key == Key.Escape)
        {
            bool consumed = false;
            foreach (var lane in _lanes)
            {
                consumed |= lane.CancelPendingDelete();
                consumed |= lane.CancelPendingSplit();
            }

            // 一个音都没选中时不标记 Handled，让它照常往下走（和 Delete 那一支同一条规矩）。
            // 放开选中要重画的那两下：读数和提示行归 RefreshReadout，卷帘上那圈高亮归 RefreshView。
            if (!consumed && _controller is { } controller && controller.SelectedNotes.Count > 0)
            {
                controller.ClearSelection();
                RefreshReadout();
                RefreshView();
                consumed = true;
            }

            e.Handled = consumed;
            return;
        }

        // Enter：把正摆着的「抽掉一段」按下去。那一问里没有输入框，焦点多半不在任何框里
        //（划段那个手势不用打字），于是这一下会落到窗口这一层。
        // 只在真摆着那一问时才算用掉：没摆着就返回 false，回车照常往下走。
        // 灰着的「抽掉」（这一段里没有音）也算用掉 —— 屏幕上正摆着一问一答，不该顺手去触发别的什么。
        if (e.Key == Key.Enter)
        {
            foreach (var lane in _lanes)
            {
                if (!lane.ConfirmPendingSplit()) continue;
                e.Handled = true;
                return;
            }
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            if (e.Key == Key.Z && !shift) { e.Handled = true; Undo(); return; }
            if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { e.Handled = true; Redo(); return; }

            // Ctrl+S = 保存，和工具栏上那颗「保存」等价（走同一个 SaveAsync）。
            // 那颗从「文件」下拉里升上台面之后没地方印键位了（Button 没有 InputGesture），
            // 所以这一句写进了它的悬浮提示 —— 真按键是这儿接的。
            // 焦点在输入框里时这一条不会生效 —— 上面那句是整段让开的。
            if (e.Key == Key.S && !shift) { e.Handled = true; _ = SaveAsync(); return; }
        }

        if (_controller is null) return;

        // Ctrl + ←/→：在焦点轨的音符之间前后跳（只定位，不动音符）。
        // 限定在一条轨里：跨轨那版按着按着会莫名其妙换到别的轨上，换轨本来有自己的手势（Ctrl+↑/↓）
        if (ctrl && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            JumpSelection(e.Key == Key.Left ? -1 : 1);
            return;
        }

        // Ctrl + ↑/↓：换一条轨（聚焦）。和 Ctrl + ←/→ 换一个音是对称的两件事，都不动谱面；
        // 裸 ↑/↓ 是微调音高（见下面那个 switch），所以这一对必须带 Ctrl 才分得开
        if (ctrl && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            MoveFocus(e.Key == Key.Up ? -1 : 1);
            return;
        }

        if (ctrl) return;

        // Delete / Backspace：把当前选中的音整批删掉，两个键都绑。
        // 一个音都没选中时不标记 Handled，让它照常往下走。
        if (e.Key is Key.Delete or Key.Back && _controller.SelectedNotes.Count > 0)
        {
            e.Handled = true;
            DeleteSelection();
            return;
        }

        // Shift + 空格 = 回跳一小节并播放。空格本身还是播放 / 暂停 ——
        // 这一支只认按住 Shift 的那一下，所以必须排在下面那一支前面。
        // 落点、夹法、为什么不做 Stop 都写在 BackOneBarAndPlay 上。
        if (e.Key == Key.Space && shift)
        {
            e.Handled = true;
            ReleaseControlFocus();
            BackOneBarAndPlay();
            return;
        }

        // 空格 = 走带条上那颗「▶ 从当前位置播放」。必须抢在控件前面：焦点停在轨道头上那些按钮、
        // 下拉上时空格本来归它们（按钮是「按一下」，下拉是「展开」），不抢的话空格播放就时灵时不灵。
        // 输入框那一头在上面已经整块让开了。
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            ReleaseControlFocus();

            // 能不能按以那颗按钮为准，不是另算一套：它在没曲子、没音轨时是灰的，
            // 空格一并跟着没反应（见 RefreshTransport）
            if (PlayButton.IsEnabled) TogglePlayback();
            return;
        }

        // 一步一格 = 一个十六分音符，和拖动吸的是同一个格
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
    /// 把键盘焦点从「会被空格按响」的控件上收回来 —— 空格和 Shift+空格 起手都要先做这一下。
    /// 光在隧道处理器里标记 Handled 拦不住：<c>Button</c>（下拉也一样）对空格走的是类处理器，
    /// 不看你标没标，于是会「一个键干了两件事」。收回焦点之后 KeyUp 按抬起那一刻的焦点重新路由，
    /// 按钮「按下 → 抬起 → 触发」那条路就断在中间。
    /// </summary>
    private void ReleaseControlFocus()
    {
        if (FocusManager?.GetFocusedElement() is InputElement { Focusable: true })
            FocusManager.ClearFocus();
    }

    /// <summary>
    /// 在焦点轨的音符之间前后跳一个（Ctrl + ←/→）。只定位，不动音符、不动焦点。
    /// 落点一定在焦点轨上（见 <see cref="PianoRollController.MoveSelection"/>）。
    /// </summary>
    private void JumpSelection(int delta)
    {
        if (_controller is null) return;

        var info = _controller.MoveSelection(delta);
        if (info is not { } note) return;

        RefreshReadout();
        // 横向已经由控制器对齐到那一小节，纵向在这儿滚进视野。走 Reveal 而不是 BringIntoView：
        // 那条轨要是收着的，「滚到它那儿」屏幕上一点变化都没有 —— 跳过去的是那个音，顺手把它展开
        if (note.Track >= 0 && note.Track < _lanes.Count) _lanes[note.Track].Reveal();

        RefreshView();
    }

    /// <summary>
    /// Ctrl + ↑/↓：把聚焦挪到上一条 / 下一条轨，跳过收起来的那些（↑ 是往上，即下标小的那一条）。
    /// 到头、或者这个方向上只剩收起来的轨，就原地不动（不绕回去）。落点滚进视野但不展开 ——
    /// 这里本来就绕开了收起来的轨。也不动选中集、不动试听、不动播放头：换聚焦不改哪条轨。
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
    /// 方向键微调：把选中的一组音整体挪一格（时间）或一个半音（音高）。夹在这儿做一次，
    /// 夹完的增量才是真正会生效的那个，预览与命令两边都得拿它算 ——
    /// 不夹的话屏幕上动的地方和用户按的那一下对不上。
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
    /// Shift + ←/→：改时值，一步一格；缩到头也不小于 1 个 tick（时值不能是 0）。
    /// 只动主选中那一个：<c>SetNoteSpan</c> 只收一个音，选中一组按一下会记 N 格撤销。
    /// 选中集不用管：改时值的那个音身份不变，哪怕它越过邻居在数组里换了位置。
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
    /// 悬停到某个音上。参数是那个音的身份，<see cref="NoteId.None"/> = 没命中（空白处）。
    /// 没命中时不用特判：真曲子里的号是从 1 开始连号发的（见 <see cref="NoteIdentity"/>），
    /// 0 在这条轨上一个音都对不上。
    /// </summary>
    private void OnLaneHover(object? sender, NoteId note)
    {
        if (_controller is null || sender is not TrackLaneView lane) return;
        ShowHover(_controller.Describe(lane.TrackIndex, note));
    }

    /// <summary>
    /// 鼠标此刻悬在哪个音上。<c>null</c> 既是「没悬在任何音上」也是「鼠标刚离开卷帘」，
    /// 两种都回落到主选中。存下「悬停」这件事本身、每次现算「悬停优先、选中兜底」，
    /// 就不用回答「刚才被顶掉的是什么」。编辑之后一律作废，所以换曲子和每次编辑都要清。
    /// </summary>
    private PianoRollController.NoteInfo? _hovered;

    /// <summary>鼠标进 / 出一个音。传 null 是「离开了」——这时读数回落到主选中。</summary>
    private void ShowHover(PianoRollController.NoteInfo? info)
    {
        _hovered = info;
        RefreshReadout();
    }

    /// <summary>
    /// 读数条那一行提示该显示哪一层。判据是选中集的个数，不是「悬停在哪」——
    /// 悬停是一过性的，那行字会跟着鼠标闪。用 &gt; 0 而不是 == 1：<c>← →</c> / <c>↑ ↓</c> /
    /// <c>Delete</c> 动的都是整批选中（见 <see cref="NudgeNotes"/> / <see cref="DeleteSelection"/>）。
    /// 不动 ToolTip：它始终是两行合起来的全文（见 <see cref="Format.ReadoutHintTooltip"/>）。
    /// </summary>
    private void RefreshHint()
    {
        bool hasNote = _controller is { } controller && controller.SelectedNotes.Count > 0;
        HintText.Text = hasNote ? Format.ReadoutHintEditing : Format.ReadoutHintPerforming;
    }

    /// <summary>
    /// 读数条上那一套（轨 / 音高 / 小节 / 拍位 / 时值）唯一的出处。值从哪来：悬停优先，
    /// 没悬停就用主选中的音 —— 鼠标从音上移开时读数回落到选中的音，而不是变空。
    /// 两个都没有时标签和值一起藏：只藏里面那块（ReadoutDetail），外面 <c>Border</c> 的
    /// MinHeight 不动，不然鼠标一移开这一条会塌下去。改选中的每一条路最后都走到这儿，
    /// 所以提示行也在这条上喊（<see cref="RefreshHint"/>）。
    /// </summary>
    private void RefreshReadout()
    {
        RefreshHint();

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

        // ⚠️ 缩略条的宽度只认**布局落定之后**的那个数。NavStrip.Bounds 是**上一次**布局排出来的：
        // 首屏那几趟刷新跑在缩略条被摆开之前，读到的是 0 或几百像素的旧值，而按它算出来的
        // 「整曲铺满这一条」自然全挤在条的左边一小截里 —— 看上去就是「红杠跑到缩略图外面」。
        // 宽度真的定下来之后，NavStrip.SizeChanged 会再喊一次 RefreshView（见构造函数），
        // 到那时才算得对，所以这里只挡住「还没排到布局」那一档，不承担算准的责任。
        double navWidth = NavStrip.Bounds.Width;
        if (navWidth < MinNavWidth)
        {
            // ⚠️ 这里**不能**把整趟刷新丢掉。上一版是「宽度不合法就跳过这一块、也不管后面有没有人来补」，
            // 于是缩略条停在上一张场景上**一直**不更新（这正是「不是闪一下、而是一直错着」的来源）。
            // 现在：把这张过期场景清掉（宁可空一帧，也不要留着一张错的），下面位置读数、速度框
            // 照常更新，补场景那一帧归布局回调管。
            NavStrip.SetScene(null);
        }
        else
        {
            // 缩略图画的是焦点轨：Ctrl+↑/↓ 换了焦点、或者点了别条轨上的音符，下一帧它就跟着换。
            // 焦点轨的轨对象要按下标取，而「轨被删光」那一帧 FocusedTrack 已经越界，
            // 这儿给 null 而不是硬取 —— 重画是每个播放帧都跑的，越界会当场炸
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

        // 位置读数留的是播放头所在小节（不是视口起始）：它右边紧挨着「跳到某小节」的输入框，
        // 「我在哪 / 我要去哪」摆在一起才成对照。
        PositionText.Text = Format.Position(controller.BarOfTick(playhead), controller.BarCount);

        // 速度框报的是 tick 0 的基准速度，也就是回车之后真正生效的那个数（见 CurrentBpmText）。
        // 焦点在框里时跳过：这一帧一帧地重写，会把用户正打进去的字吃掉半个。
        if (!BpmBox.IsFocused) BpmBox.Text = CurrentBpmText(controller.Song);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // 改过还没存就先拦一下（是 / 丢掉 / ✕）。判据是工具栏那一格用的同一个 _dirty，不另起一套。
        // 问到一半又来一次关闭请求（任务栏那一处）不再叠第二个框：_askingBeforeClose 挡着
        if (!_closeConfirmed && !_askingBeforeClose && _dirty)
        {
            e.Cancel = true;
            _askingBeforeClose = true;
            _ = ConfirmCloseAsync();
            return;
        }

        // 关窗口时把音松开：不然合成器上会留一串按着不放的键
        _playback.Stop();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 定时器跟着窗口一起收掉。出声的设备是 App 建的，由 App 收尾
        _playback.Dispose();
        base.OnClosed(e);
    }
}
