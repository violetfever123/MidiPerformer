using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 卷帘的缩影，纯代码画：轨道底色、小节/拍网格线、音符块、播放头红线。
/// dev-only 样板窗口的一部分，用的是卷帘那几个取色令牌（lane-a/b、grid-bar/beat、note/note-edge、stop）。
/// </summary>
public sealed class RollPreviewStrip : Control
{
    private const int Bars = 4;
    private const int BeatsPerBar = 4;
    private const int Lanes = 2;

    /// <summary>一个轨道放几个音高格。</summary>
    private const int PitchesPerLane = 10;

    /// <summary>音高 0 在轨道高度的这个比例处，其余高度留给更低的音。</summary>
    private const double BaselineFraction = 0.75;

    /// <summary>样板窗口的示例音符，不是真实曲目。</summary>
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

        // 音高格只铺在轨道顶到基线之间，所以格高按 3/4 轨道高算；
        // 按整条轨道高算的话，负音高会探出轨道底被裁掉半截。
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
            // 音符 = 实心块（上下各让 0.8px）+ 顶边 1px 亮线，不描边、不倒角。
            var x = (beat * BeatsPerBar + step) * beatWidth;
            var width = Math.Max(2, length * beatWidth - 1);
            var top = lane * laneHeight + baseline - (pitch + 1) * pitchHeight + 0.8;
            var height = pitchHeight - 1.6;

            context.FillRectangle(
                new ImmutableSolidColorBrush(palette.Note), new Rect(x, top, width, height));
            context.FillRectangle(
                new ImmutableSolidColorBrush(palette.NoteEdge), new Rect(x, top, width, 1));
        }

        // 播放头，画在音符之上
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
