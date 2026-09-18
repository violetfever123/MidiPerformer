using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 卷帘的缩影，纯代码画的 —— 轨道底色、小节/拍网格线、音符块、播放头红线。
/// dev-only 样板窗口的一部分。
///
/// 画的是**卷帘真正要用的那几个令牌**（lane-a/b、grid-bar/beat、note/note-edge、stop），
/// 所以它同时证明两件事：取色桥对这几个键取得到，以及这几个键在明暗两套下都看得清。
/// 07 的卷帘是它的放大版，不是另起一套。
/// </summary>
public sealed class RollPreviewStrip : Control
{
    private const int Bars = 4;
    private const int BeatsPerBar = 4;
    private const int Lanes = 2;

    /// <summary>一个轨道放几个音高格。</summary>
    private const int PitchesPerLane = 10;

    /// <summary>音高 0 落在轨道高度的这个位置上，剩下的高度留给更低的音。</summary>
    private const double BaselineFraction = 0.75;

    /// <summary>每格放几个音，只是摆个样子 —— 样板窗口里的示例数据，不是真实曲目。</summary>
    private static readonly (int Lane, int Beat, int Step, int Length, int Pitch)[] Notes =
    {
        (0, 0, 0, 2, 0), (0, 0, 2, 2, 2), (0, 1, 0, 3, 4), (0, 1, 3, 1, 3),
        (0, 2, 0, 2, 5), (0, 2, 2, 1, 4), (0, 3, 0, 4, 2),
        (1, 0, 1, 2, -3), (1, 1, 1, 2, -1), (1, 2, 1, 2, 0), (1, 3, 1, 2, -3),
    };

    private TokenSource? _tokens;

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

    public override void Render(DrawingContext context)
    {
        if (Tokens is not { } tokens) return;

        var palette = tokens.Current;
        var laneHeight = Bounds.Height / Lanes;
        var beatWidth = Bounds.Width / (Bars * BeatsPerBar);

        // 音高格铺在轨道顶到基线之间那一段，所以是 3/4 轨道高除以格数，
        // 不是整条轨道高除以格数 —— 按后者算，负音高会从轨道底下探出去被裁掉半截。
        var pitchHeight = laneHeight * BaselineFraction / PitchesPerLane;
        var baseline = laneHeight * BaselineFraction;

        context.FillRectangle(new ImmutableSolidColorBrush(palette.Surface), new Rect(Bounds.Size));

        for (var lane = 0; lane < Lanes; lane++)
        {
            var top = lane * laneHeight;
            var background = lane % 2 == 0 ? palette.LaneA : palette.LaneB;
            context.FillRectangle(new ImmutableSolidColorBrush(background),
                new Rect(0, top, Bounds.Width, laneHeight));

            DrawGrid(context, palette, top, laneHeight, beatWidth);
        }

        foreach (var (lane, beat, step, length, pitch) in Notes)
        {
            // 照 wireframe 的画法来：一块实心方块 + 顶边一条 1px 的亮线。
            // 没有描边，也不倒圆角 —— wireframe 是
            //   fillRect(nx, ny + .8, nw, ROW_H - 1.6)  实心块，上下各让 0.8
            //   fillRect(nx, ny + .8, nw, 1)            顶边
            // 之前画成四边带描边的圆角药丸，和它对不上，而 07 的卷帘是拿这个当模子的。
            var x = (beat * BeatsPerBar + step) * beatWidth;
            var width = Math.Max(2, length * beatWidth - 1);
            var top = lane * laneHeight + baseline - (pitch + 1) * pitchHeight + 0.8;
            var height = pitchHeight - 1.6;

            context.FillRectangle(
                new ImmutableSolidColorBrush(palette.Note), new Rect(x, top, width, height));
            context.FillRectangle(
                new ImmutableSolidColorBrush(palette.NoteEdge), new Rect(x, top, width, 1));
        }

        // 播放头：wireframe 里那根红线，压在音符上面
        context.DrawLine(
            new Pen(new ImmutableSolidColorBrush(palette.Stop), 1.5),
            new Point(beatWidth * 6, 0),
            new Point(beatWidth * 6, Bounds.Height));
    }

    private static void DrawGrid(
        DrawingContext context, TokenPalette palette, double top, double height, double beatWidth)
    {
        var beatPen = new Pen(new ImmutableSolidColorBrush(palette.GridBeat), 1);
        var barPen = new Pen(new ImmutableSolidColorBrush(palette.GridBar), 1);

        for (var beat = 0; beat <= Bars * BeatsPerBar; beat++)
        {
            var x = Math.Round(beat * beatWidth) + 0.5;
            var isBar = beat % BeatsPerBar == 0;
            context.DrawLine(isBar ? barPen : beatPen, new Point(x, top), new Point(x, top + height));
        }
    }

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}
