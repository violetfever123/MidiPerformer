using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.Adapters.Presenters;

namespace MidiPerformer.App.Views;

/// <summary>
/// 悬浮层：演奏时<b>浮在游戏画面上</b>的那块提示（倒计时 / 演奏中 / 已停止）。
///
/// 它为什么必须存在：按下「开始」之后用户就切到游戏去了，演奏器窗口本身看不见 ——
/// 还剩几秒、现在在发哪个音、怎么急停，都得有一块**压在游戏上面**的东西来说。
///
/// <b>绝不能抢焦点</b>，这一条比「看得见」更要紧：抢了焦点，之后发的按键就发到别处去了
/// （游戏一个都收不到，而且不会有任何报错）。三重保险，缺一不可：
/// <list type="number">
/// <item><c>ShowActivated="False"</c>（XAML）—— 显示这一下不激活。</item>
/// <item><c>WS_EX_NOACTIVATE</c> + <c>WS_EX_TRANSPARENT</c>（网关给的拓展样式）——
///   之后鼠标点到它上面也不激活、且这一点会穿透到游戏那边。</item>
/// <item><c>WS_EX_TOOLWINDOW</c> —— 不进任务栏和 Alt+Tab，用户不会在切窗口时顺手把它点出来。</item>
/// </list>
///
/// 它是个**纯视图**：不知道演奏在干什么，也不知道时间从哪儿来。三个 <c>Show*</c> 方法
/// 由 <see cref="PerformerWindow"/> 按节奏调（倒计时数字与进度条都是它每 100ms 来问一次），
/// 这里一个判断都不多做 —— 判在窗口那边，因为那边才知道整场演奏的状态机。
/// </summary>
public partial class PerformerOverlayWindow : Window
{
    /// <summary>卡片离屏幕顶端多远（逻辑像素）。照 wireframe 的 <c>top:74px</c>。</summary>
    private const double TopOffset = 74;

    /// <summary>「已停止」自己收起来之前停留多久。</summary>
    private static readonly TimeSpan StoppedLinger = TimeSpan.FromSeconds(3);

    /// <summary>窗口显示出来了没有。显示之前 <c>Position</c> 设不下去 —— 见 <see cref="PlaceAtTopCenter"/>。</summary>
    private bool _shown;

    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _demoTimer;

    /// <summary>dev 开关拉起来的演示：不自动收起，否则截不到「已停止」那一态。</summary>
    private bool _demoMode;

    /// <summary>
    /// 三个拓展样式有没有真设上。**要读就在 <see cref="OnOpened"/> 之后读**（句柄那一刻才真有）。
    ///
    /// 它公开出来，是因为「没设上」的后果和「不抢焦点」这条一样重：卡片会吃掉点击、
    /// 还会抢走焦点，之后发的按键全发到它身上 —— 表现就是游戏一个音都收不到，且不报错。
    /// 网关只管设、不发议论（<see cref="OverlayWindowStyles.Apply"/> 的说明），
    /// 说一句是窗口的事：演奏器窗口把它写进状态行，跟 F6 装不上走同一条路。
    /// </summary>
    public bool StylesApplied { get; private set; }

    public PerformerOverlayWindow()
    {
        InitializeComponent();

        // 卡片是**水平居中**的，而它的尺寸随状态变（倒计时那块比演奏中窄）：
        // 每次尺寸落定都得重新摆一次，只摆一次的话换到下一态就歪了。
        //
        // 这也是为什么定位不能只在 OnOpened 里做一次：那一刻 SizeToContent 还没量过内容，
        // Bounds 报的是窗口的默认尺寸（实测 1139×630），拿它算出来的居中是 397 而不是 1350。
        // 真尺寸要等第一次布局落定，也就是 SizeChanged 这一下。
        SizeChanged += (_, _) => PlaceAtTopCenter();

        // 先摆成倒计时那一态。不是「默认值」而是**首帧的形状**：
        // 窗口是 SizeToContent 的，一开始没内容的话第一次布局量到的是零尺寸，
        // 定位（见 SizeChanged）就会算歪。
        ShowCountdown(3);
    }

    /// <summary>倒计时态：大数字 + 「准备演奏 · 切到游戏窗口」+ F6 提示。</summary>
    public void ShowCountdown(int seconds)
    {
        CancelHide();

        // 不走当前区域性：阿拉伯语等区域下 int.ToString() 会写成另一套数字字符。
        CountdownNumber.Text = seconds.ToString(CultureInfo.InvariantCulture);
        Switch(CountdownState);
    }

    /// <summary>演奏中态：当前在发的音 + 进度 + 已走时间 / 总时长 + F6 提示。</summary>
    public void ShowPlaying(string note, double musicNow, double totalSeconds)
    {
        CancelHide();

        NoteNow.Text = note.Length > 0 ? $"♪ {note}" : $"♪ {Format.Placeholder}";
        Progress.Value = totalSeconds > 0 ? Math.Clamp(musicNow / totalSeconds, 0, 1) : 0;
        TimeNow.Text = $"{Format.Clock(musicNow)} / {Format.Clock(totalSeconds)}";
        Switch(PlayingState);
    }

    /// <summary>
    /// 已停止态：一句话说清楚键都松了。
    ///
    /// 它停一会儿就自己收起来（急停之后用户还在游戏里，一块永远压在画面上的提示是噪声）。
    /// 这一会儿是留给用户抬头的：他刚按完 F6，得有时间看到「已松开所有按键」这句。
    /// </summary>
    public void ShowStopped()
    {
        Switch(StoppedState);
        if (_demoMode) return;

        _hideTimer ??= NewHideTimer();
        _hideTimer.Start();
    }

    /// <summary>
    /// 显示之后才能做的那件事：<b>要拓展样式</b>（句柄是这一刻才真有的）。
    ///
    /// 定位不在这儿 —— 它跟着尺寸走，见构造器里的 SizeChanged。这一刻布局还没量过。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 三个样式（点击穿透 / 不抢焦点 / 不进任务栏）Avalonia 表达不了，去网关要。
        // 设不上也不抛：悬浮层少一条样式只是不好用，不该把整场演奏带走 —— 但**不许静默**，
        // 记在 StylesApplied 上，由演奏器窗口写进状态行（见那个属性的说明）。
        StylesApplied = TryGetPlatformHandle() is { } handle && OverlayWindowStyles.Apply(handle.Handle);

        _shown = true;
        PlaceAtTopCenter();
    }

    /// <summary>屏幕顶端居中 —— 别放在正中间，那是准星的位置。</summary>
    private void PlaceAtTopCenter()
    {
        // 显示之前设 Position 是空操作（平台窗口还没建出来），而 SizeChanged 在构造期间就会响。
        if (!_shown || Screens.Primary is not { } screen) return;

        var area = screen.WorkingArea;                                  // 物理像素
        int width = (int)Math.Ceiling(Bounds.Width * RenderScaling);    // 逻辑像素 → 物理像素
        int top = (int)Math.Ceiling(TopOffset * RenderScaling);

        var target = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + top);
        if (Position != target) Position = target;                      // 位置没变就别再动一次窗口
    }

    /// <summary>
    /// dev-only：把三态轮流演一遍，给启动参数 <c>--overlay-demo</c> 用。**产品路径不碰它。**
    ///
    /// 它存在的理由很具体：工单里有「已在一个普通窗口上验证悬浮层置顶与不抢焦点」这一条，
    /// 而这条没法在测试里验 —— 它需要一个真窗口、一个真的前台窗口，和一次真的点击。
    /// 于是照 <c>--style-guide</c> 的先例留一个开关，能把悬浮层单独拉起来比一比
    /// 「显示之后前台窗口还是不是原来那个」。见 `.scratch/midi-performer/issues/06-performance-preflight-and-overlay.md`
    /// 的验收记录（原先写的是「见 docs 里 06 的验收记录」，而 docs/ 下只有 spec 与 wireframe，
    /// 这条指针是空的 —— 记录跟着工单走）。
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
