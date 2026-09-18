using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Project;

namespace MidiPerformer.App.Views;

/// <summary>
/// 窗口一：编辑器外壳（卷帘 + 导航条 + 读数条 + 走带条）。
///
/// **只管布置与转发**：换算在 <see cref="PianoRollController"/>，画什么在
/// <see cref="PianoRollPresenter"/>，出声在 <see cref="PreviewPlayback"/>。
/// 所以这里没有 ViewModel 类 —— 那会是个只做转发的空壳（见 spec 的「Controller」那节）。
///
/// 这一张是**只读**的：卷帘不响应点击、不改任何音符，键盘也只定位不移动。
/// 工具栏四条命令里，导入和导出这台窗口自己就做得了（都是 <see cref="SongProject"/> 的一层薄转发），
/// 保存 / 另存为要的是曲库，还留给 10；演奏器那条走组装点给的工厂，本窗口不 new 那个窗。
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
    private readonly List<TrackLaneView> _lanes = new();

    private PianoRollController? _controller;

    /// <summary>正在拖导航条 —— 这期间卷帘上的播放头红线要藏起来（wireframe 标注 3）。</summary>
    private bool _draggingNav;

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public MainWindow() : this(null!, null!, null!, null) { }

    /// <param name="tokens">自绘取色桥（卷帘和导航条不在 XAML 里，拿不到 DynamicResource）。</param>
    /// <param name="clock">墙上钟，喂给试听的时间积分。</param>
    /// <param name="sink">出声的出口（winmm）。</param>
    /// <param name="performerFactory">
    /// 「演奏器…」按下时去要那个独立窗口。给的是工厂不是现成的窗口：
    /// 演奏器一建出来就装低层键盘钩子，所以它必须到用户真要用的那一刻才存在。
    /// 复用与单例都在组装点里管，本窗口只管要、然后 Show。
    /// </param>
    public MainWindow(TokenSource tokens, IClock clock, IAudioSink sink, Func<Window>? performerFactory)
    {
        InitializeComponent();

        _tokens = tokens;
        _performerFactory = performerFactory;
        _playback = new PreviewPlayback(sink, clock);

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

        // 提示语只写这一张真做得到的（定位），编辑那些归 09 —— 文案住在 Format 里，一处改处处改
        HintText.Text = Format.ReadoutHint;

        // 窗口改宽 = 每小节变宽（固定 4 小节，没有缩放），所以要按新的宽度重算场景
        SizeChanged += (_, _) => RefreshView();
        LanesHost.SizeChanged += (_, _) => RefreshView();

        // 隧道阶段接方向键：先于任何控件拿到它。焦点在输入框里时让开（见 OnWindowKeyDown）
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        RefreshView();
    }

    // ==================== 导入 ====================

    /// <summary>
    /// 「导入 MIDI…」—— 选文件 → 读 → 显示。
    ///
    /// 只做这三件事：曲库、命名、导入历史归 10。
    /// </summary>
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

        try
        {
            LoadSong(SongProject.Read(path), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // SongProject 抛的就是中文消息，原样报出来 —— 编一句更笼统的话只会把线索弄丢
            ShowError(ex.Message);
        }
    }

    /// <summary>装一首曲子：重建卷帘、把事件表交给试听、把界面复位。</summary>
    private void LoadSong(Song song, string title)
    {
        HideMessages();

        _controller = new PianoRollController(song);
        _playback.Load(song);

        LanesHost.Children.Clear();
        _lanes.Clear();
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            var lane = new TrackLaneView(_controller, i, _tokens);
            lane.HoverChanged += OnLaneHover;
            _lanes.Add(lane);
            LanesHost.Children.Add(lane);
        }

        SongNameBox.Text = title;
        EmptyHint.IsVisible = false;
        JumpBox.Text = "1";

        PlayButton.IsEnabled = song.Tracks.Count > 0;
        JumpBox.IsEnabled = true;
        // 有谱面就写得出，哪怕一个音都没有 —— 速度表和分辨率也值得留下来，
        // 所以这条的判据是「装上了曲子」，不是「有轨」
        ExportButton.IsEnabled = true;

        ShowHover(null);
        ShowSelection();

        // 布局还没跑，卷帘的宽度是 0 —— 场景要等 LanesHost.SizeChanged 那一趟才算得出来
        RefreshView();
    }

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
        if (_controller is not { } controller) return;

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
            SongProject.Write(controller.Song, path);
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

    // ==================== 键盘定位 ====================

    /// <summary>
    /// ← → 在所有轨的音符之间前后跳。
    ///
    /// **只定位，不移动**：这一张改不了任何东西（编辑是 09）。
    /// 焦点在输入框里时整个让开 —— 那时左右键归光标用，抢过来会让人没法改自己输的数。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_controller is null || e.Handled) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        int delta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
        if (delta == 0) return;

        e.Handled = true;
        var info = _controller.MoveSelection(delta);
        if (info is not { } note) return;

        ShowSelection();
        // 横向已经由控制器对齐到那一小节，纵向（哪条轨）在这儿滚进视野
        if (note.Track >= 0 && note.Track < _lanes.Count) _lanes[note.Track].BringIntoView();

        RefreshView();
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
        // 速度框只报当前值：改 BPM 是改谱面（08），做成只读的免得看着像能改
        BpmBox.Text = Math.Round(controller.Song.TempoMap.BeatsPerMinuteAt(playhead))
            .ToString(CultureInfo.InvariantCulture);
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
