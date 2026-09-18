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
/// 小节导航条：整曲的音符密度缩略图 + 当前可见窗口那个框。
///
/// **按小节吸附**（wireframe 标注 3）：落点永远在整小节上，不会停在半小节 ——
/// 停在半小节等于让人对着谱子数格子。吸附的算法在 <see cref="PianoRollGeometry"/>，
/// 这儿只管把算出来的小节号报出去。
///
/// 拖动期间它会喊 <see cref="DragStarted"/> / <see cref="DragCompleted"/>：
/// **拖动时卷帘上的播放头红线要藏起来** —— 不藏的话它跟着吸附一格格跳，很烦（标注 3）。
/// </summary>
public sealed class RollNavStrip : Control
{
    /// <summary>柱子离底边多远，照 wireframe 的 <c>h - bh - 3</c>。</summary>
    private const double BarBottomMargin = 3;

    /// <summary>柱子最高能长到哪儿，照 wireframe 的 <c>h - 7</c>。</summary>
    private const double BarTopMargin = 7;

    /// <summary>空小节也给这么高：柱子彻底消失的话，那一段谱面看着像不存在。</summary>
    private const double MinBarHeight = 2;

    /// <summary>每隔几小节画一根分隔线，照 wireframe 的 <c>b += 4</c>。</summary>
    private const int DividerEveryBars = 4;

    /// <summary>柱子的透明度。wireframe 是 <c>--note</c> 加 0.5 的 alpha，令牌是实色，这儿补上那一半。</summary>
    private const double BarOpacity = 0.5;

    private TokenSource? _tokens;
    private PianoRollPresenter.NavScene? _scene;
    private bool _dragging;

    public RollNavStrip()
    {
        // 整条都是可点的：Cursor 给手型，别处（卷帘、轨道头）不给
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>用户按下了（还没松手）。拖动期间卷帘要把播放头藏起来。</summary>
    public event EventHandler? DragStarted;

    /// <summary>要跳到第 <paramref name="barZeroBased"/> 小节（0 起，已吸附到小节线）。</summary>
    public event EventHandler<int>? SeekRequested;

    /// <summary>松手了。播放头红线从这一刻起重新出现。</summary>
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

    /// <summary>换一份要画的东西。窗口改宽、滚动、每帧都会给一次。</summary>
    public void SetScene(PianoRollPresenter.NavScene? scene)
    {
        _scene = scene;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Tokens is not { } tokens || _scene is not { } scene) return;

        var palette = tokens.Current;
        double width = Bounds.Width;
        double height = Bounds.Height;

        context.FillRectangle(new ImmutableSolidColorBrush(palette.Surface), new Rect(Bounds.Size));

        // 每 4 小节一根淡分隔线：密度柱子连着看容易糊成一片，靠它分段
        var dividerPen = new Pen(new ImmutableSolidColorBrush(palette.LineSoft), 1);
        for (int bar = 0; bar < scene.Bars.Count; bar += DividerEveryBars)
        {
            double x = Math.Round(bar * scene.BarWidth) + 0.5;
            context.DrawLine(dividerPen, new Point(x, 0), new Point(x, height));
        }

        // 密度柱：高度 = 该小节的音符数（多轨合计）
        var barBrush = new ImmutableSolidColorBrush(WithAlpha(palette.Note, BarOpacity));
        double span = Math.Max(1, height - BarTopMargin - BarBottomMargin);
        for (int bar = 0; bar < scene.Bars.Count; bar++)
        {
            double bh = Math.Max(MinBarHeight, scene.Bars[bar].Fraction * span);
            double x = bar * scene.BarWidth + 1;
            double w = Math.Max(1, scene.BarWidth - 2);
            context.FillRectangle(barBrush, new Rect(x, height - bh - BarBottomMargin, w, bh));
        }

        // 当前可见窗口那个框，照 wireframe 的 .navthumb（实底 + 1px 边）
        var thumb = new Rect(scene.ThumbX, 0, scene.ThumbWidth, height);
        context.FillRectangle(new ImmutableSolidColorBrush(palette.AccentSoft), thumb);
        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.AccentLine), 1), thumb);

        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.Line), 1),
            new Rect(0, 0, width - 1, height - 1));
    }

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
        if (_scene is not { } scene || scene.Bars.Count == 0) return;
        SeekRequested?.Invoke(
            this, PianoRollGeometry.NavBarAtX(x, Bounds.Width, scene.Bars.Count));
    }

    /// <summary>给令牌色配一个透明度。**不是颜色字面值** —— 基色仍然只从令牌来。</summary>
    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}
