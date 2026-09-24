using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Adapters.Presenters;

namespace MidiPerformer.App.Views;

/// <summary>
/// 悬浮层：演奏时浮在游戏画面上的那块提示（倒计时 / 演奏中 / 已停止）。按下「开始」后用户
/// 切到游戏，演奏器窗口看不见，得有一块压在游戏上面的东西说还剩几秒、在发哪个音、怎么急停。
/// 绝不能抢焦点：抢了之后发的按键会发到别处，游戏一个都收不到且不报错。三重保险：
/// <c>ShowActivated="False"</c>（XAML）、<c>WS_EX_NOACTIVATE</c> + <c>WS_EX_TRANSPARENT</c>、
/// <c>WS_EX_TOOLWINDOW</c>（后三条是网关给的拓展样式，最后一条让它不进任务栏和 Alt+Tab）。
/// 纯视图：三个 <c>Show*</c> 方法由 <see cref="PerformerWindow"/> 按节奏调，这里不做判断，
/// 因为只有那边知道整场演奏的状态机。
/// </summary>
public partial class PerformerOverlayWindow : Window
{
    /// <summary>卡片离屏幕顶端多远（逻辑像素）。</summary>
    private const double TopOffset = 74;

    /// <summary>「已停止」自己收起来之前停留多久。</summary>
    private static readonly TimeSpan StoppedLinger = TimeSpan.FromSeconds(3);

    /// <summary>窗口显示出来了没有；显示之前 <c>Position</c> 设不下去。</summary>
    private bool _shown;

    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _demoTimer;

    /// <summary>dev 演示模式：不自动收起，否则截不到「已停止」那一态。</summary>
    private bool _demoMode;

    /// <summary>
    /// 三个拓展样式有没有真设上（要读就在 <see cref="OnOpened"/> 之后读，句柄那一刻才真有）。
    /// 没设上的后果和抢焦点一样重：卡片会吃掉点击、抢走焦点，之后发的按键全发到它身上且不报错，
    /// 所以公开出来由演奏器窗口写进状态行。
    /// </summary>
    public bool StylesApplied { get; private set; }

    public PerformerOverlayWindow()
    {
        InitializeComponent();

        // 卡片水平居中而尺寸随状态变，所以每次尺寸落定都重新摆一次。
        // 定位不能只在 OnOpened 里做一次：那一刻 SizeToContent 还没量过内容，
        // Bounds 报的是窗口默认尺寸，算出来的居中是错的；真尺寸要等第一次布局落定。
        SizeChanged += (_, _) => PlaceAtTopCenter();

        // 先摆成倒计时那一态：窗口是 SizeToContent 的，一开始没内容的话
        // 第一次布局量到的是零尺寸，定位就会算歪。
        ShowCountdown(3);
    }

    /// <summary>倒计时态：大数字 + 「准备演奏 · 切到游戏窗口」+ 取消 / 急停提示。</summary>
    public void ShowCountdown(int seconds)
    {
        CancelHide();

        // 固定区域：阿拉伯语等区域下 int.ToString() 会写成另一套数字字符。
        CountdownNumber.Text = seconds.ToString(CultureInfo.InvariantCulture);
        Switch(CountdownState);
    }

    /// <summary>演奏中态：当前在发的音 + 进度 + 已走时间 / 总时长 + 急停提示。</summary>
    public void ShowPlaying(string note, double musicNow, double totalSeconds)
    {
        CancelHide();

        NoteNow.Text = note.Length > 0 ? $"♪ {note}" : $"♪ {Format.Placeholder}";
        Progress.Value = totalSeconds > 0 ? Math.Clamp(musicNow / totalSeconds, 0, 1) : 0;
        TimeNow.Text = $"{Format.Clock(musicNow)} / {Format.Clock(totalSeconds)}";
        Switch(PlayingState);
    }

    /// <summary>
    /// 已停止态：一句话说清楚键都松了。停一会儿自己收起来（急停后用户还在游戏里，
    /// 常年压在画面上的提示是噪声），这一会儿留给他看到「已松开所有按键」这句。
    /// </summary>
    public void ShowStopped()
    {
        Switch(StoppedState);
        if (_demoMode) return;

        _hideTimer ??= NewHideTimer();
        _hideTimer.Start();
    }

    /// <summary>
    /// 显示之后才能做的那件事：要拓展样式（句柄这一刻才真有）。
    /// 定位不在这儿，它跟着尺寸走（见构造器里的 SizeChanged），这一刻布局还没量过。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 三个样式 Avalonia 表达不了，得去网关要。设不上也不抛：
        // 悬浮层少一条样式只是不好用，不该把整场演奏带走，但要记在 StylesApplied 上，
        // 由演奏器窗口写进状态行报出来。
        StylesApplied = TryGetPlatformHandle() is { } handle && OverlayWindowStyles.Apply(handle.Handle);

        _shown = true;
        PlaceAtTopCenter();
    }

    /// <summary>屏幕顶端居中 —— 别放在正中间，那是准星的位置。</summary>
    private void PlaceAtTopCenter()
    {
        // 显示之前设 Position 是空操作（平台窗口还没建出来），而 SizeChanged 在构造期间就会响
        if (!_shown || Screens.Primary is not { } screen) return;

        var area = screen.WorkingArea;                                  // 物理像素
        int width = (int)Math.Ceiling(Bounds.Width * RenderScaling);    // 逻辑像素 → 物理像素
        int top = (int)Math.Ceiling(TopOffset * RenderScaling);

        var target = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + top);
        if (Position != target) Position = target;                      // 位置没变就别再动一次窗口
    }

    /// <summary>
    /// dev-only：把三态轮流演一遍，给启动参数 <c>--overlay-demo</c> 用，产品路径不碰它。
    /// 用来手动比一比「显示之后前台窗口还是不是原来那个」。
    /// </summary>
    public void ShowAllStatesForDemo()
    {
        _demoMode = true;

        int tick = 0;
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _demoTimer.Tick += (_, _) =>
        {
            switch (++tick)
            {
                case 1: ShowCountdown(2); break;
                case 2: ShowCountdown(1); break;
                case 3:
                case 4:
                case 5: ShowPlaying("G4", tick - 3, 8); break;      // 0/8 → 2/8，进度条走两格
                default:
                    ShowStopped();
                    _demoTimer!.Stop();
                    break;
            }
        };
        _demoTimer.Start();
    }

    private void Switch(Control state)
    {
        CountdownState.IsVisible = ReferenceEquals(state, CountdownState);
        PlayingState.IsVisible = ReferenceEquals(state, PlayingState);
        StoppedState.IsVisible = ReferenceEquals(state, StoppedState);
    }

    private void CancelHide() => _hideTimer?.Stop();

    private DispatcherTimer NewHideTimer()
    {
        var timer = new DispatcherTimer { Interval = StoppedLinger };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Hide();
        };
        return timer;
    }
}
