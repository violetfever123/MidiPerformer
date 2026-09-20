using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MidiPerformer.App.Theme;

/// <summary>
/// 某一份主题下的 26 个令牌，取成 Avalonia 的类型。卷帘和悬浮层是代码画的，拿不到
/// <c>Styles/Tokens.axaml</c> 那套 <c>DynamicResource</c>，必须有个入口把令牌取出来。
///
/// 属性名和令牌键是一回事：<c>NoteEdge</c> 就是 <c>TokenNoteEdge</c>，
/// <see cref="Resolve"/> 靠这条机械映射按名字取，没有第二张对照表。加令牌只要在这里
/// 加一个属性，别处自动跟上。
///
/// 一次取一份快照：画一帧画到一半主题变了不会花屏。
/// </summary>
public sealed record TokenPalette(
    Color Ground,
    Color Surface,
    Color Surface2,
    Color Surface3,
    Color Ink,
    Color InkMuted,
    Color InkFaint,
    Color Line,
    Color LineSoft,
    Color Accent,
    Color AccentSoft,
    Color AccentLine,
    Color Warn,
    Color WarnSoft,
    Color WarnLine,
    Color Stop,
    Color StopSoft,
    Color StopLine,
    Color Note,
    Color NoteEdge,
    Color GridBar,
    Color GridBeat,
    Color LaneA,
    Color LaneB,
    Color LaneFocus,
    BoxShadows Shadow)
{
    /// <summary>令牌键的前缀。属性名加上它就是 <c>Tokens.axaml</c> 里的键。</summary>
    public const string KeyPrefix = "Token";

    /// <summary>
    /// 属性按构造函数参数的名字排，不是按 <c>GetProperties()</c> 的返回顺序 —— 那个顺序 CLR
    /// 不保证，而 26 个令牌里 24 个是同一个类型，顺序一变
    /// <see cref="Activator.CreateInstance(Type, object?[])"/> 会静默装错位置。按参数名取属性，
    /// 位置由语言保证对得上，名单仍然只有这一张。
    /// </summary>
    private static readonly PropertyInfo[] Properties = typeof(TokenPalette)
        .GetConstructors()
        .Single()
        .GetParameters()
        .Select(parameter => typeof(TokenPalette).GetProperty(parameter.Name!)!)
        .ToArray();

    /// <summary>按当前主题，从资源里取一份快照。</summary>
    public static TokenPalette Resolve(IResourceHost host, ThemeVariant variant)
        => Resolve((key, theme) => host.TryGetResource(key, theme, out var value) ? value : null, variant);

    /// <summary>
    /// 同上，只是把「取」这件事交出去 —— 只要一个「键 + 主题 → 值」的函数。开这个口子是为了
    /// 让取色桥可测：Avalonia 的 <c>IResourceHost</c> 不许用户代码实现。
    /// </summary>
    public static TokenPalette Resolve(Func<string, ThemeVariant, object?> lookup, ThemeVariant variant)
    {
        var arguments = new object?[Properties.Length];
        for (var i = 0; i < Properties.Length; i++)
            arguments[i] = Read(lookup, variant, Properties[i]);

        return (TokenPalette)Activator.CreateInstance(typeof(TokenPalette), arguments)!;
    }

    /// <summary>
    /// 取不到就直接炸，不给默认值：令牌名字写错应该是启动就崩，而不是界面上悄悄少一块颜色。
    /// </summary>
    private static object Read(Func<string, ThemeVariant, object?> lookup, ThemeVariant variant, PropertyInfo property)
    {
        var key = KeyPrefix + property.Name;
        var value = lookup(key, variant);

        // 颜色在资源里通常是画刷，画笔要用的是它的 Color；写成 Color 也认
        if (property.PropertyType == typeof(Color))
        {
            if (value is ISolidColorBrush brush) return brush.Color;
            if (value is Color color) return color;
        }
        if (property.PropertyType != typeof(Color) && value is not null && property.PropertyType.IsInstanceOfType(value))
            return value;

        throw new InvalidOperationException(
            $"取不到令牌 {key}（主题 {variant}）。它只该在 Styles/Tokens.axaml 里定义一次，" +
            "别处不许写死颜色 —— 缺了就是那边漏了一条。");
    }
}
