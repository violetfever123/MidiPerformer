using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 一条轨的卷帘。<b>画的全是 <see cref="PianoRollPresenter.LaneScene"/> 里算好的东西</b>，
/// 这里一个乘法都不做 —— 「画什么」和「画在哪」分开，前者才能不起窗口就测。
///
/// 它是 <b>只读</b>的：不响应点击、不拖任何东西，连鼠标指针都是默认箭头
/// （「看着像能拖」的手型光标在这儿等于一句谎话）。唯一接的输入是**悬停**，
/// 只为了把音高报到读数条上 —— 而命中判定同样交给
/// <see cref="PianoRollController"/>，画的和点的才是同一份几何。
///
/// 画法照 <c>RollPreviewStrip</c>：实心块 + 顶边 1px 亮线，不倒圆角、不描边。
/// </summary>
public sealed class PianoRollLane : Control
{
    /// <summary>刻度数字的字号，照 wireframe 的 <c>10px</c>。</summary>
    private const double RulerFontSize = 10;

    /// <summary>播放头红线多宽，照 wireframe 的 <c>fillRect(px-1, 0, 2, h)</c>。</summary>
    private const double PlayheadWidth = 2;

    private TokenSource? _tokens;
    private PianoRollPresenter.LaneScene? _scene;
    private int _hoveredNote = -1;

    public PianoRollLane()
    {
        // 音符块允许画到视口外一点点（尾巴），画到控件外面就该被切掉
        ClipToBounds = true;
    }

    /// <summary>做命中判定用的控制器。**只用于悬停** —— 画法一个字都不从这儿取。</summary>
    public PianoRollController? Controller { get; set; }

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex { get; set; }

    /// <summary>悬停到的音符变了。参数是音符下标，-1 = 移开了或没命中。</summary>
    public event EventHandler<int>? HoverChanged;

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

    /// <summary>换一屏要画的东西。视口一变（滚动、窗口改宽）就要重给一次。</summary>
    public void SetScene(PianoRollPresenter.LaneScene? scene)
    {
        _scene = scene;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Tokens is not { } tokens || _scene is not { } scene) return;

        var palette = tokens.Current;
        var viewport = scene.Viewport;

        // 轨道底色铺满整条，顺带给鼠标一个能命中的面 —— 悬停要落在空白处也算「移开了」。
        // 深浅按轨号奇偶交替，照 wireframe 的 .lane.a / .lane.b：堆在一起时能看清一条轨在哪儿结束。
        var background = TrackIndex % 2 == 0 ? palette.LaneA : palette.LaneB;
        context.FillRectangle(new ImmutableSolidColorBrush(background), new Rect(Bounds.Size));

        // 音高行的黑键底纹**刻意没画**：wireframe 用的是半透明灰（rgba(128,140,155,.075)），
        // 而这一层不许出现颜色字面值，也不为它新造一个令牌。少一层底纹不影响读谱。
        // 音高行之间的分隔线同理 —— 音符块自己就说明了行在哪。

        DrawGrid(context, palette, scene);

        // 音符块：压在网格上面，播放头再压在音符上面
        foreach (var box in scene.Notes)
        {
            DrawNote(context, palette, box, box.Index == scene.SelectedNote);
        }

        // 标尺下沿那条横线：刻度区到此为止，照 wireframe 的 `moveTo(0, RULER_H + .5)`
        context.DrawLine(
            new Pen(new ImmutableSolidColorBrush(palette.Line), 1),
            new Point(0, Math.Round(PianoRollGeometry.RulerHeight) + 0.5),
            new Point(viewport.Width, Math.Round(PianoRollGeometry.RulerHeight) + 0.5));

        DrawRulerLabels(context, palette, scene);
        DrawPlayhead(context, palette, scene, viewport);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        int note = -1;
        if (_scene is { } scene && Controller is { } controller)
        {
            var point = e.GetPosition(this);
            controller.HitTest(TrackIndex, scene.Viewport, point.X, point.Y, out note);
        }

        SetHover(note);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int note)
    {
        if (note == _hoveredNote) return;
        _hoveredNote = note;
        HoverChanged?.Invoke(this, note);
    }

    // ==================== 落笔 ====================

    private static void DrawGrid(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene)
    {
        var barPen = new Pen(new ImmutableSolidColorBrush(palette.GridBar), 1);
        var beatPen = new Pen(new ImmutableSolidColorBrush(palette.GridBeat), 1);
        double bottom = scene.Viewport.Height;

        // +0.5 让 1px 的线落在像素中心，不糊成两像素 —— 照 RollPreviewStrip
        foreach (double x in scene.BarLines)
        {
            double cx = Math.Round(x) + 0.5;
            context.DrawLine(barPen, new Point(cx, 0), new Point(cx, bottom));
        }

        // 拍线从标尺下沿起，标尺那一格留给小节号
        foreach (double x in scene.BeatLines)
        {
            double cx = Math.Round(x) + 0.5;
            context.DrawLine(beatPen, new Point(cx, PianoRollGeometry.RulerHeight), new Point(cx, bottom));
        }
    }

    private static void DrawNote(
        DrawingContext context, TokenPalette palette, PianoRollGeometry.NoteBox box, bool selected)
    {
        // 超出可演奏范围的音标灰（判据来自 NoteMapper，见 PianoRollController）：
        // 填 ink-faint —— 它和 note 一样是「实心块」的材质，但比四周的网格线还淡，
        // 一眼就是「这块点不动」。顶边挑 line 而不是 note-edge：后者是正常音符的
        // 亮边颜色，压在灰块上会让灰音重新长出高光，两种音就看不出区别了。
        var fill = box.InRange ? palette.Note : palette.InkFaint;
        var edge = box.InRange ? palette.NoteEdge : palette.Line;

        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        context.FillRectangle(new ImmutableSolidColorBrush(fill), rect);
        context.FillRectangle(new ImmutableSolidColorBrush(edge), new Rect(box.X, box.Y, box.Width, 1));

        // 选中的那个：一圈 accent 细边。键盘定位（← →）全靠它告诉你「现在在哪」，
        // 没有它，方向键按下去屏幕上什么也没变。
        if (selected)
        {
            context.DrawRectangle(
                null, new Pen(new ImmutableSolidColorBrush(palette.Accent), 1), rect);
        }
    }

    private static void DrawRulerLabels(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene)
    {
        var brush = new ImmutableSolidColorBrush(palette.InkFaint);
        // 刻度数字不追字体令牌：自绘这层的取色桥只送颜色，字体归 XAML 那一层。
        // 一两位数字的等宽与否，看不出差别。
        var typeface = Typeface.Default;

        foreach (var label in scene.BarLabels)
        {
            var text = new FormattedText(
                label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, RulerFontSize, brush);
            context.DrawText(text, new Point(label.X, (PianoRollGeometry.RulerHeight - RulerFontSize) / 2));
        }
    }

    private static void DrawPlayhead(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene,
        in PianoRollGeometry.Viewport viewport)
    {
        double x = scene.PlayheadX;
        if (double.IsNaN(x) || x < 0 || x > viewport.Width) return;

        var pen = new Pen(new ImmutableSolidColorBrush(palette.Stop), PlayheadWidth);
        context.DrawLine(pen, new Point(x, 0), new Point(x, viewport.Height));
    }

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}
