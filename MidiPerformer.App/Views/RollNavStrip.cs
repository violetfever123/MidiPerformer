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
/// </summary>
public sealed class RollNavStrip : Control
{
    /// <summary>每隔几小节画一根分隔线。</summary>
    private const int DividerEveryBars = 4;

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

        var palette = tokens.Current;
        double width = Bounds.Width;
        double height = Bounds.Height;

        context.FillRectangle(new ImmutableSolidColorBrush(palette.Surface), new Rect(Bounds.Size));

        // 每 4 小节一根淡分隔线，给音符块分段
        var dividerPen = new Pen(new ImmutableSolidColorBrush(palette.LineSoft), 1);
        for (int bar = 0; bar < scene.BarCount; bar += DividerEveryBars)
        {
            double x = Math.Round(bar * scene.BarWidth) + 0.5;
            context.DrawLine(dividerPen, new Point(x, 0), new Point(x, height));
        }

        // 焦点轨的音符块画成实心 Accent，且不画顶边 1px 亮线：
        // 整条只有 30px 高、纵向还要按音域摊成一行一两个像素，顶边会把整块吃掉。
        // 块是纯色的，靠上下 1px 的缝分行（见 NavNoteGap）。
        var noteBrush = new ImmutableSolidColorBrush(palette.Accent);
        foreach (var note in scene.Notes)
        {
            context.FillRectangle(noteBrush, new Rect(note.X, note.Y, note.Width, note.Height));
        }

        // 当前可见窗口那个框：实底 + 1px 边
        var thumb = new Rect(scene.ThumbX, 0, scene.ThumbWidth, height);
        context.FillRectangle(new ImmutableSolidColorBrush(palette.AccentSoft), thumb);
        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.AccentLine), 1), thumb);

        // 播放头，和卷帘里那根同色（Stop）。必须画在框之后：
        // 框是实底，先画就被盖住，而播放头绝大多数时候正好落在框里。
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
