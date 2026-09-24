using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace MidiPerformer.App.Views;

/// <summary>
/// 毛玻璃 F5 的那套数字与画法顺序：外扩 → 模糊 → 叠底色 → 颗粒 → 顶边高光。
///
/// 这里一块位图都不碰、一个像素都不画 —— 全是算术，所以这一层能脱开 Avalonia 测
/// （见 <c>MidiPerformer.Tests/Visual/FrostedGlassTests.cs</c>）。
/// 真把像素画出来的是 <see cref="FrostedGlassLayer"/>。
/// </summary>
public static class FrostedGlass
{
    /// <summary>高斯模糊的 σ。其余每个数字都是围着它定的（外扩、底色、颗粒强度）。</summary>
    public const double Sigma = 34;

    /// <summary>
    /// 位图往外扩多少。σ=34 会把边缘吸淡约 90px（σ=22 时是约 60px），扩出去的那圈正好补上
    /// 被吸掉的料。**这一圈不能省** —— 省了面板四边会泛出一圈假高光（外面没料可吸，糊出来是白的）。
    /// </summary>
    public const double Bleed = 90;

    /// <summary>面板底色。糊得越厚，底下的形状越没有信息，底色就可以越薄（F5 定在 45%）。</summary>
    public const double BaseOpacity = 0.45;

    /// <summary>颗粒的强度。深色要压得住，浅色只需要一点点 —— 两边不是一个数。</summary>
    public const double GrainOpacityDark = 0.50;
    public const double GrainOpacityLight = 0.32;

    /// <summary>顶边那道 1px 高光（玻璃的棱）的强度。只有真玻璃才有这道边，实心面板没有。</summary>
    public const double TopHighlightOpacity = 0.42;

    /// <summary>
    /// 位图尺寸 = 面板尺寸 + 2 × <see cref="Bleed"/>。
    ///
    /// 这一条最容易被实现时省掉（省了也照样跑、照样好看一点，只是四边多一圈白晕），
    /// 所以它有一条单独的断言盯着。
    /// </summary>
    public static PixelSize BitmapSize(double width, double height) =>
        new((int)Math.Ceiling(Math.Max(width, 0) + 2 * Bleed),
            (int)Math.Ceiling(Math.Max(height, 0) + 2 * Bleed));

    /// <summary>这套玻璃在不在深色主题下（混色模式与颗粒强度都按它分叉）。</summary>
    public static bool IsDark(ThemeVariant variant) => variant == ThemeVariant.Dark;

    /// <summary>
    /// 颗粒的混色模式，**按主题分叉**。
    ///
    /// 深色 `soft-light`、浅色 `overlay`。
    ///
    /// ⚠️ 46 号票面与 <c>spec-界面改版.md:219</c> 写的是反的（「深色 overlay、浅色 soft-light」），
    /// 但那两句紧接着给的**实测理由**说的恰恰是这两个组合不能用：深色底上 overlay 走乘法那半边
    /// 几乎不动，浅色底上 soft-light 走温和那半边也几乎不动。原型
    /// （<c>docs/prototype-主窗口改版.html:253-263</c>）是这一条的权威记录，它的 CSS 就是
    /// 深色 soft-light / 浅色 overlay，两边自洽。用户挑的正是原型上那一版，所以照原型来 ——
    /// 照票面字面做会让**两个主题都看不见颗粒**。见收工报告。
    /// </summary>
    public static BitmapBlendingMode GrainBlend(bool dark) =>
        dark ? BitmapBlendingMode.SoftLight : BitmapBlendingMode.Overlay;

    /// <summary>同上，颗粒的不透明度。</summary>
    public static double GrainOpacity(bool dark) => dark ? GrainOpacityDark : GrainOpacityLight;

    /// <summary>
    /// 画法顺序：先糊、再叠底色、再撒颗粒、最后压顶边高光。**顺序即正确性** ——
    /// 颗粒跟底色一起画就分不出「玻璃」和「一坨灰」，颗粒压在最后则会把字盖住。
    /// </summary>
    public static IReadOnlyList<FrostedStep> Steps { get; } = new[]
    {
        FrostedStep.Blur, FrostedStep.Base, FrostedStep.Grain, FrostedStep.TopHighlight,
    };
}

/// <summary>毛玻璃那四步里的一步，顺序见 <see cref="FrostedGlass.Steps"/>。</summary>
public enum FrostedStep
{
    /// <summary>把面板底下那一块糊掉（外扩 <see cref="FrostedGlass.Bleed"/> 之后才算得干净）。</summary>
    Blur,

    /// <summary>叠一层 <see cref="FrostedGlass.BaseOpacity"/> 的底色，把文字托起来。</summary>
    Base,

    /// <summary>撒颗粒 —— 磨砂玻璃的「材料感」全在这一层，没有它糊掉的背景看起来像一张失焦的照片。</summary>
    Grain,

    /// <summary>顶边一道 1px 高光：玻璃的棱。</summary>
    TopHighlight,
}
