using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 小节导航条：焦点轨的整曲缩影 + 当前可见窗口那个框。缩略图跟着焦点轨走
/// （Ctrl+↑/↓ 换的那条），一眼看得出这条轨哪儿密哪儿空。
/// 点击和拖动都按小节吸附，落点永远在整小节上；吸附算法在 <see cref="PianoRollGeometry"/>，
/// 这儿只管把算出来的小节号报出去。
/// 拖动期间它会喊 <see cref="DragStarted"/> / <see cref="DragCompleted"/>，卷帘据此把播放头红线藏起来。
/// 「画什么、按什么顺序画」全在 <see cref="NavStripDraw"/> 里 —— 那儿产出的是一份纯数据，<see cref="Render"/> 只是照着画。
/// </summary>
public sealed class RollNavStrip : Control
{
    private TokenSource? _tokens;
    private PianoRollPresenter.NavScene? _scene;
    private bool _dragging;

    public RollNavStrip()
    {
        // 整条都可点，所以给手型光标
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>用户按下了（还没松手），拖动期间卷帘要把播放头藏起来。</summary>
    public event EventHandler? DragStarted;

    /// <summary>要跳到第 <paramref name="barZeroBased"/> 小节（0 起，已吸附到小节线）。</summary>
    public event EventHandler<int>? SeekRequested;

    /// <summary>松手了，播放头红线从这一刻起重新出现。</summary>
    public event EventHandler? DragCompleted;

    public TokenSource? Tokens
    {
        get => _tokens;
        set
        {
            if (_tokens is not null) _tokens.Changed -= OnTokensChanged;
            _tokens = value;
            if (_tokens is not null) _tokens.Changed += OnTokensChanged;
            InvalidateVisual();
        }
    }

    /// <summary>换一份要画的东西，窗口改宽、滚动、每帧都会调一次。</summary>
    public void SetScene(PianoRollPresenter.NavScene? scene)
    {
        _scene = scene;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Tokens is not { } tokens || _scene is not { } scene) return;

        foreach (var stroke in NavStripDraw.Strokes(
                     Bounds.Width, Bounds.Height, scene, NavColors.From(tokens.Current)))
        {
            var brush = new ImmutableSolidColorBrush(WithAlpha(stroke.Color, stroke.Opacity));

            // 画法按**三类**分，不是按层一列一列写：填一块 / 画一条竖线 / 画一圈 1px 的线。
            // 分类在 NavStripDraw.ShapeOf 里，没归类的层在那儿就抛 —— 不会悄悄落到「填」上。
            switch (NavStripDraw.ShapeOf(stroke.Kind))
            {
                // 竖线：X 是横坐标，Y..Bottom 是跨度
                case NavStrokeShape.Line:
                    context.DrawLine(
                        new Pen(brush, 1),
                        new Point(stroke.Area.X, stroke.Area.Y),
                        new Point(stroke.Area.X, stroke.Area.Bottom));
                    break;

                // 只有一圈线、没有底色：视口框的底色是垫在音符之前的那层淡填充，
                // 外边框是整条的边
                case NavStrokeShape.Outline:
                    context.DrawRectangle(null, new Pen(brush, 1), stroke.Area);
                    break;

                default:
                    context.FillRectangle(brush, stroke.Area);
                    break;
            }
        }
    }

    /// <summary>给令牌色配一个透明度：令牌里只有实色，浓淡是画的时候配上去的（同 <c>PianoRollLane.WithAlpha</c>）。</summary>
    private static Color WithAlpha(Color color, double opacity)
        => Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_scene is null) return;

        _dragging = true;
        e.Pointer.Capture(this);
        DragStarted?.Invoke(this, EventArgs.Empty);
        RaiseSeek(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        RaiseSeek(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>横坐标 → 最近的小节线 → 报出去。</summary>
    private void RaiseSeek(double x)
    {
        if (_scene is not { } scene || scene.BarCount == 0) return;
        SeekRequested?.Invoke(
            this, PianoRollGeometry.NavBarAtX(x, Bounds.Width, scene.BarCount));
    }

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}

/// <summary>缩略条上的一笔是哪一层 —— 决定 <c>Render</c> 用填还是用画线画它，也是测试认「这一笔属于哪一层」的凭据。</summary>
public enum NavStrokeKind
{
    /// <summary>整条的底色。</summary>
    Background,

    /// <summary>视口框那层淡填充。**垫在音符之前** —— 这一条的先后就是「填充盖不住框里的音符」的保证。</summary>
    ViewFill,

    /// <summary>每 4 小节一根的淡分隔线。</summary>
    Divider,

    /// <summary>焦点轨的一个音符块，或它在框边被切开后的一截。</summary>
    Note,

    /// <summary>视口框那圈 1px 的线。自己不带底色。</summary>
    ViewLine,

    /// <summary>播放头。画在框之后，压在框线上。</summary>
    Playhead,

    /// <summary>整条的外边框。</summary>
    Border
}

/// <summary>
/// 缩略条上的一笔。**列表里的先后就是画的先后** —— 这一版的正确性有一半长在顺序上，
/// 所以「画什么」被提成了一份纯数据，测试不碰 Avalonia 也能断言顺序。
/// </summary>
/// <param name="Kind">这一笔属于哪一层。</param>
/// <param name="Color">令牌表里取来的实色，不在这儿配浓淡。</param>
/// <param name="Opacity">
/// 这一笔的浓度：视口框那层填充是 <see cref="NavStripDraw.ViewFillOpacity"/>、
/// 露在框外的音符是 <see cref="NavStripDraw.OutsideNoteOpacity"/>、框内的音符是满色。
/// </param>
/// <param name="Area">
/// 这一笔占的矩形。<see cref="NavStrokeKind.Divider"/> 与 <see cref="NavStrokeKind.Playhead"/> 是竖线：
/// 看 <c>Area.X</c> 与 <c>Area.Y</c>..<c>Area.Bottom</c>，宽度不用。
/// </param>
public readonly record struct NavStroke(NavStrokeKind Kind, Color Color, double Opacity, Rect Area);

/// <summary>
/// 这一层用什么画：<see cref="Fill"/> = 填一块，<see cref="Line"/> = 一条竖线，
/// <see cref="Outline"/> = 画一圈 1px 的线（中间是空的）。
///
/// **为什么要有这张表**：<c>Render</c> 里那个 switch 上一版是「这几个 Kind 画线，其余走 default（填）」，
/// 而外边框排在**最后一个**画 —— 它一落到 default 上，整条（连音符带播放头）就被抹成一块实色，
/// 上机截图是一整条 <c>TokenLine</c> 色的空带子，什么都不剩。层是按 <see cref="NavStrokeKind"/> 分的，
/// 画法却只有这三类，中间少一张表，结果是「多一种层 = 多一次静默的填」。
/// 现在没归类的层在 <see cref="NavStripDraw.ShapeOf"/> 那儿直接抛，测试也能枚举着断言。
/// </summary>
public enum NavStrokeShape
{
    /// <summary>填一块（底色、视口框的淡填充、音符）。</summary>
    Fill,

    /// <summary>一条竖线：看 <c>Area.X</c> 与 <c>Area.Y</c>..<c>Area.Bottom</c>，宽度不用。</summary>
    Line,

    /// <summary>画一圈 1px 的线，中间是空的（视口框、外边框）。</summary>
    Outline,
}

/// <summary>
/// 缩略条要用的几种颜色，从 <see cref="TokenPalette"/> 取来的一小把 —— 绘制那边因此不认识令牌表，
/// 换主题只是换这一把颜色。**一条都不新增**：视口框那层填充用的是 <c>TokenAccent</c> 本人，只是配了 10% 的浓度。
/// </summary>
public readonly record struct NavColors(
    Color Surface,
    Color Divider,
    Color Accent,
    Color ViewLine,
    Color Playhead,
    Color Border)
{
    /// <summary>令牌表 → 缩略条要的那一小把。取哪条令牌只有这一个出处。</summary>
    public static NavColors From(TokenPalette palette) => new(
        Surface: palette.Surface,
        Divider: palette.LineSoft,
        Accent: palette.Accent,
        ViewLine: palette.AccentLine,
        Playhead: palette.Stop,
        Border: palette.Line);
}

/// <summary>
/// 缩略条这一帧要画的东西，按绘制顺序排成一列。纯函数：不碰控件、不认识令牌、不画。
///
/// 顺序是这一版的要害：**先填淡色 → 再画音符 → 最后框线与播放头**。填充垫在音符前面，
/// 两层在屏幕上根本不相交 —— 「淡填充盖不住框里的音符」因此是画法保证的，不是调不透明度调出来的。
/// （上一版正好反着：先画音符，再用**实色** <c>AccentSoft</c> 把视口框填一遍，框里的音符被整块抹平。）
/// </summary>
public static class NavStripDraw
{
    /// <summary>每隔几小节画一根分隔线。</summary>
    public const int DividerEveryBars = 4;

    /// <summary>视口框那层淡填充的强度 —— <c>TokenAccent</c> 的 10%。6% 太弱（深色下几乎只剩那圈线）、16% 会自己变成一块色块跟音符抢眼睛。</summary>
    public const double ViewFillOpacity = 0.10;

    /// <summary>露在框外的音符退到这个浓度；框内的是满色。跨过框边的音符在框边上换一次浓度。</summary>
    public const double OutsideNoteOpacity = 0.55;

    /// <summary>
    /// 切开之后每一截的最小宽度，占整条宽的比例（0.35%）。窄过它的那一截**并进更宽的那个邻居**：
    /// 画出来是一根看不见的发丝，但也不丢它的面积 —— 切开与不切开的总面积因此永远一致。
    /// </summary>
    public const double MinSegmentFraction = 0.0035;

    /// <summary>框内音符的浓度（满色）。</summary>
    private const double InsideNoteOpacity = 1;

    /// <summary>
    /// 这一层归哪一类画法。<c>Render</c> 的 switch 只认这三类 —— 所以**每一种层都必须在这儿有一句**，
    /// 漏了就在这儿抛（而不是在 <c>Render</c> 里悄悄走 <c>default</c> 被当成「填一块」）。
    /// 上一版就是漏了这句：外边框排在最后画、又落进 <c>default</c>，把整条抹成一整块实色。
    /// </summary>
    /// <param name="kind">这一笔属于哪一层。</param>
    /// <exception cref="ArgumentOutOfRangeException">这种层没归类 —— 新增层时先想清楚它用哪一类画法。</exception>
    public static NavStrokeShape ShapeOf(NavStrokeKind kind) => kind switch
    {
        NavStrokeKind.Background or NavStrokeKind.ViewFill or NavStrokeKind.Note => NavStrokeShape.Fill,
        NavStrokeKind.Divider or NavStrokeKind.Playhead => NavStrokeShape.Line,
        NavStrokeKind.ViewLine or NavStrokeKind.Border => NavStrokeShape.Outline,

        // 到这儿说明有人加了新层却没归类。**不能**给它一个 default：默认成「填一块」正是上一版那个 bug，
        // 而且外边框这种排在最后的层一被填，整条就全没了。
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "这一层没归类 —— 别让它悄悄落到「填」上去"),
    };

    /// <summary>
    /// 这一帧要画的一列笔画，按绘制顺序排。整条有多宽多高、铺的是哪首曲子由入参给，
    /// 函数本身不读控件也不读令牌 —— 顺序和几何因此都能不碰 Avalonia 单独断言。
    /// </summary>
    /// <param name="width">整条的像素宽。</param>
    /// <param name="height">整条的像素高。</param>
    /// <param name="scene">这一帧要画的东西：焦点轨的音符 + 视口框 + 播放头。</param>
    /// <param name="colors">从令牌表取来的那一小把颜色。</param>
    public static IReadOnlyList<NavStroke> Strokes(
        double width, double height, PianoRollPresenter.NavScene scene, NavColors colors)
    {
        var strokes = new List<NavStroke>();

        strokes.Add(new NavStroke(
            NavStrokeKind.Background, colors.Surface, 1, new Rect(0, 0, width, height)));

        // 视口框那层淡填充：垫在最下，音符后画，框里的音符一点都不会被糊到
        var thumb = new Rect(scene.ThumbX, 0, scene.ThumbWidth, height);
        strokes.Add(new NavStroke(NavStrokeKind.ViewFill, colors.Accent, ViewFillOpacity, thumb));

        // 每 4 小节一根淡分隔线，给音符块分段。压在淡填充之上，框里的刻度照样读得出
        for (int bar = 0; bar < scene.BarCount; bar += DividerEveryBars)
        {
            double x = Math.Round(bar * scene.BarWidth) + 0.5;
            strokes.Add(new NavStroke(NavStrokeKind.Divider, colors.Divider, 1, new Rect(x, 0, 0, height)));
        }

        // 焦点轨的音符块画成实心 Accent，且不画顶边 1px 亮线：整条只有 30px 高、
        // 纵向还要按音域摊成一行一两个像素，顶边会把整块吃掉。块是纯色的，靠上下 1px 的缝分行。
        double minSegment = MinSegmentFraction * width;
        foreach (var note in scene.Notes)
        {
            foreach (var segment in Segments(
                         note.X, note.X + note.Width, thumb.X, thumb.Right, minSegment))
            {
                strokes.Add(new NavStroke(
                    NavStrokeKind.Note,
                    colors.Accent,
                    segment.Lit ? InsideNoteOpacity : OutsideNoteOpacity,
                    new Rect(segment.Left, note.Y, segment.Width, note.Height)));
            }
        }

        strokes.Add(new NavStroke(NavStrokeKind.ViewLine, colors.ViewLine, 1, thumb));

        // 播放头，和卷帘里那根同色（Stop）。排在框之后：框是操作件里最底的一层，播放头压它。
        double playheadX = Math.Round(scene.PlayheadX) + 0.5;
        if (playheadX >= 0 && playheadX <= width)
        {
            strokes.Add(new NavStroke(NavStrokeKind.Playhead, colors.Playhead, 1, new Rect(playheadX, 0, 0, height)));
        }

        strokes.Add(new NavStroke(NavStrokeKind.Border, colors.Border, 1, new Rect(0, 0, width - 1, height - 1)));

        return strokes;
    }

    /// <summary>切开后的一截。<c>Lit</c> = 落在视口框里，也就是满色那一截。</summary>
    private readonly record struct Segment(double Left, double Right, bool Lit)
    {
        public double Width => Right - Left;
    }

    /// <summary>
    /// 把一个音符按视口框的左右边切开 —— **同一个音符自己在框边上换一次浓度**，不是整条决定亮不亮：
    /// 框里那一截满色，露在框外的两截退到 55%。切出来的几截首尾相接、各占各的面积，
    /// 加起来正好是原来的宽（切开与不切开的总面积一致）。
    /// 窄过 <paramref name="minWidth"/> 的那一截并进更宽的那个邻居，不做成看不见的发丝。
    /// </summary>
    /// <param name="x0">音符左边缘（像素）。</param>
    /// <param name="x1">音符右边缘（像素）。</param>
    /// <param name="boxLeft">视口框左边缘（像素）。</param>
    /// <param name="boxRight">视口框右边缘（像素）。</param>
    /// <param name="minWidth">一截的最小宽度（像素）。</param>
    private static List<Segment> Segments(
        double x0, double x1, double boxLeft, double boxRight, double minWidth)
    {
        var segments = new List<Segment>(3);

        double inLeft = Math.Max(x0, boxLeft);
        double inRight = Math.Min(x1, boxRight);

        // 框宽为 0（或整条都在框外）时 inLeft >= inRight：整个音符退到 55%，一截都不用切
        if (inLeft >= inRight)
        {
            Add(segments, x0, x1, lit: false);
        }
        else
        {
            Add(segments, x0, inLeft, lit: false);      // 左边露在框外的那一截
            Add(segments, inLeft, inRight, lit: true);  // 框里那一截
            Add(segments, inRight, x1, lit: false);     // 右边露在框外的那一截
        }

        // 从右往左把过窄的那一截并进邻居。并进更宽的那个：少数派并进多数派，画面的主调不变。
        // 最多三段、合一次少一段，所以走一遍就收敛。
        for (int i = segments.Count - 1; i >= 0 && segments.Count > 1; i--)
        {
            if (segments[i].Width >= minWidth) continue;

            int left = i - 1;
            int right = i + 1;
            bool intoLeft = left >= 0
                && (right >= segments.Count || segments[left].Width >= segments[right].Width);

            if (intoLeft)
            {
                segments[left] = segments[left] with { Right = segments[i].Right };
                segments.RemoveAt(i);
            }
            else
            {
                segments[right] = segments[right] with { Left = segments[i].Left };
                segments.RemoveAt(i);
            }
        }

        return segments;
    }

    /// <summary>空的一截不收（框宽为 0 时会算出宽度为 0 的那一截）—— 收了也只是白画一笔。</summary>
    private static void Add(List<Segment> segments, double left, double right, bool lit)
    {
        if (right - left > 0) segments.Add(new Segment(left, right, lit));
    }
}
