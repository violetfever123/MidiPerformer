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
/// 小节导航条：**焦点轨**的整曲缩影 + 当前可见窗口那个框。
///
/// 缩略图跟着**焦点轨**走（Ctrl+↑/↓ 换的那条，也是点音符时跟着换的那条）：
/// 一眼看得出这条轨哪儿密哪儿空。原来是「所有轨加在一起的每小节音符数」画成半透明的密度柱，
/// 那只说得清整首曲子哪儿热闹，说不出你手上这条轨是什么样。
/// 焦点轨永远唯一、永远有定义，所以不会因为按 Esc 清空选择就整条空掉。
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
    /// <summary>每隔几小节画一根分隔线，照 wireframe 的 <c>b += 4</c>。</summary>
    private const int DividerEveryBars = 4;

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

        // 每 4 小节一根淡分隔线：音符块连着看容易糊成一片，靠它分段
        var dividerPen = new Pen(new ImmutableSolidColorBrush(palette.LineSoft), 1);
        for (int bar = 0; bar < scene.BarCount; bar += DividerEveryBars)
        {
            double x = Math.Round(bar * scene.BarWidth) + 0.5;
            context.DrawLine(dividerPen, new Point(x, 0), new Point(x, height));
        }

        // 焦点轨的音符块，**实心 Accent**。
        //
        // 比原来那根半透明 Note 的密度柱饱和得多 —— 那是这条工单要的「明显些」的落点。
        // 但这里**不再画顶边那条 1px 亮线**（RollPreviewStrip 和卷帘的模子是「实心块 + 顶边」）：
        // 那两处一条轨有几十上百像素高，而这儿的整条只有 30px、纵向还要按音域摊成一行一两个像素，
        // 1px 的顶边会把整块吃掉 —— 画出来是边的颜色，就不是 Accent 了。
        // 所以缩略图上的块是纯色的，靠上下那 1px 的缝分行（见 NavNoteGap）。
        var noteBrush = new ImmutableSolidColorBrush(palette.Accent);
        foreach (var note in scene.Notes)
        {
            context.FillRectangle(noteBrush, new Rect(note.X, note.Y, note.Width, note.Height));
        }

        // 当前可见窗口那个框，照 wireframe 的 .navthumb（实底 + 1px 边）。
        // **外观与行为一概不动** —— 需求原话是「这个框不变」，所以哪怕它和音符块同色系、
        // 可能有糊在一起的风险，也不在这一版里顺手改它。
        var thumb = new Rect(scene.ThumbX, 0, scene.ThumbWidth, height);
        context.FillRectangle(new ImmutableSolidColorBrush(palette.AccentSoft), thumb);
        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.AccentLine), 1), thumb);

        // 播放头，和卷帘里那根同色（Stop）。
        //
        // 这一条是**超出原始需求的新增**：整曲缩略图上没有它，就只能靠「跳到」输入框里的数字
        // 反推播到哪儿了。必须画在框**之后** —— 框是实底，先画就被盖住，
        // 而播放头绝大多数时候正好落在可见窗口里，那正好是它最该被看见的时候。
        double playheadX = Math.Round(scene.PlayheadX) + 0.5;
        if (playheadX >= 0 && playheadX <= width)
        {
            context.DrawLine(
                new Pen(new ImmutableSolidColorBrush(palette.Stop), 1),
                new Point(playheadX, 0), new Point(playheadX, height));
        }

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
        if (_scene is not { } scene || scene.BarCount == 0) return;
        SeekRequested?.Invoke(
            this, PianoRollGeometry.NavBarAtX(x, Bounds.Width, scene.BarCount));
    }

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}
