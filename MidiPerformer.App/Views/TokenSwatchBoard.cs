using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 自绘的令牌板：26 个令牌各占一格，标上名字和当前主题下的色值。dev-only 样板窗口的一部分。
///
/// 它是<a href="../../docs/wireframe.html">取色桥</a>的可见产物 —— 这块画布拿不到
/// <c>DynamicResource</c>，颜色**全部**来自 <see cref="TokenSource"/>。
/// 旁边就是 XAML 控件，切主题时两边必须一起变；哪边没动，就是桥断了。
///
/// 格子的内容靠反射遍历 <see cref="TokenPalette"/> 的属性得来，不另抄一份名单 ——
/// 抄一份就迟早会和令牌表对不上。顺序是属性声明顺序。
/// </summary>
public sealed class TokenSwatchBoard : Control
{
    /// <summary>字体也走令牌，自绘代码里同样不写死字体名。</summary>
    public static readonly StyledProperty<FontFamily?> UiFontProperty =
        AvaloniaProperty.Register<TokenSwatchBoard, FontFamily?>(nameof(UiFont));

    public static readonly StyledProperty<FontFamily?> MonoFontProperty =
        AvaloniaProperty.Register<TokenSwatchBoard, FontFamily?>(nameof(MonoFont));

    private static readonly PropertyInfo[] Cells = typeof(TokenPalette).GetProperties();

    private const int Columns = 5;

    private TokenSource? _tokens;

    static TokenSwatchBoard()
    {
        AffectsRender<TokenSwatchBoard>(UiFontProperty, MonoFontProperty);
    }

    public FontFamily? UiFont
    {
        get => GetValue(UiFontProperty);
        set => SetValue(UiFontProperty, value);
    }

    public FontFamily? MonoFont
    {
        get => GetValue(MonoFontProperty);
        set => SetValue(MonoFontProperty, value);
    }

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
        var rows = (Cells.Length + Columns - 1) / Columns;
        var cellWidth = Bounds.Width / Columns;
        var cellHeight = Bounds.Height / rows;

        var ui = new Typeface(UiFont ?? FontFamily.Default);
        var mono = new Typeface(MonoFont ?? FontFamily.Default);
        var ink = new ImmutableSolidColorBrush(palette.Ink);
        var muted = new ImmutableSolidColorBrush(palette.InkMuted);
        var line = new ImmutableSolidColorBrush(palette.Line);

        context.FillRectangle(new ImmutableSolidColorBrush(palette.Surface2), new Rect(Bounds.Size));

        for (var i = 0; i < Cells.Length; i++)
        {
            var name = Cells[i].Name;
            var value = Cells[i].GetValue(palette);
            var cell = new Rect(
                i % Columns * cellWidth,
                i / Columns * cellHeight,
                cellWidth,
                cellHeight).Inflate(-3);

            DrawSwatch(context, cell, value, palette, line);

            context.DrawText(Text(name, ui, 11.5, ink), new Point(cell.X, cell.Bottom - 25));
            context.DrawText(
                Text(Describe(value), mono, 10.5, muted),
                new Point(cell.X, cell.Bottom - 12));
        }
    }

    /// <summary>
    /// 颜色画成实心块；阴影没有"颜色"可画，就在同一块地方把阴影本身画出来。
    /// 令牌只有这两种类型，各画各的，不硬凑成一样。
    /// </summary>
    private static void DrawSwatch(
        DrawingContext context, Rect cell, object? value, TokenPalette palette, IBrush line)
    {
        var area = new Rect(cell.X, cell.Y, cell.Width, cell.Height - 28);

        switch (value)
        {
            case Color color:
                context.FillRectangle(new ImmutableSolidColorBrush(color), area, 4);
                context.DrawRectangle(null, new Pen(line, 1), area, 4, 4);
                break;

            case BoxShadows shadow:
                var inner = area.Deflate(new Thickness(14, 10));
                context.DrawRectangle(new ImmutableSolidColorBrush(palette.Surface), null, inner, 4, 4);
                context.DrawRectangle(new ImmutableSolidColorBrush(palette.Surface), new Pen(line, 1),
                    inner, 4, 4, shadow);
                break;
        }
    }

    private static string Describe(object? value) => value switch
    {
        Color color => $"#{color.R:X2}{color.G:X2}{color.B:X2}",
        BoxShadows => "shadow",
        _ => "?",
    };

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}
