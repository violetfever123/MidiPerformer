using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MidiPerformer.App.Views;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 毛玻璃 F5 的那套数：位图多大、什么时候重算、颗粒的混色模式按主题怎么分叉。
///
/// 判据跟 <c>Visual/</c> 里别的几份一样：**不起 Avalonia、不做像素断言**。
/// <b>说清楚这些断言证明了什么、没证明什么</b>：它们证明「代码要了这几条」；
/// 糊出来好不好看、面板四边有没有泛出一圈白晕、浅色主题下颗粒是不是真看不见 ——
/// 一概没验，也验不了：那要一块真屏幕、两套主题各截一张图（归人工，见 46 号工单的验收）。
///
/// 那还守它做什么：因为这三条里任何一条**坏掉都不报错** ——
/// 位图忘了外扩，只是四边多一圈淡淡的白；每帧重算，只是 CPU 白烧；混色模式忘了分主题，
/// 只是「这个主题下看不见颗粒」，看起来像没做而不是像做错。
/// </summary>
public class FrostedGlassTests
{
    /// <summary>计数替身：一块像素都不画，只记下「算过几次、每次算的是什么」。</summary>
    private sealed class CountingRenderer : IFrostedRenderer
    {
        public List<FrostedRequest> Requests { get; } = new();

        public void Recompute(FrostedRequest request) => Requests.Add(request);
    }

    private static FrostedRequest 一处(double width = 560, double height = 388,
        bool dark = true, long stamp = 1)
        => new(width, height, dark, stamp);

    // ==================== 位图尺寸 ====================

    /// <summary>
    /// σ=34 会把边缘吸淡约 90px，那一圈必须真的扩出来 —— **这一条最容易在实现时被省掉**
    /// （省了照样跑，只是面板四边泛出一圈假高光），所以给它单开一条断言。
    /// </summary>
    [Test]
    public void 位图比面板四周各大九十像素()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FrostedGlass.Sigma, Is.EqualTo(34), "σ 是这一票的定案数，动了它别的数都得重调");
            Assert.That(FrostedGlass.Bleed, Is.EqualTo(90), "σ=34 吸淡的范围（σ=22 时是 60）");

            var size = FrostedGlass.BitmapSize(560, 388);
            Assert.That(size.Width, Is.EqualTo(560 + 2 * 90), "少了这一圈，四边就是一圈假高光");
            Assert.That(size.Height, Is.EqualTo(388 + 2 * 90));

            // 尺寸跟着 Bleed 走，不是写死的四个数字
            var wide = FrostedGlass.BitmapSize(1000, 700);
            Assert.That(wide.Width, Is.EqualTo(1000 + 2 * FrostedGlass.Bleed));
            Assert.That(wide.Height, Is.EqualTo(700 + 2 * FrostedGlass.Bleed));
        });
    }

    // ==================== 只算一次 ====================

    /// <summary>打开面板：算一次，就一次。</summary>
    [Test]
    public void 打开面板只渲染一次()
    {
        var renderer = new CountingRenderer();
        var surface = new FrostedSurface(renderer);

        Assert.That(surface.Show(一处()), Is.True, "第一次打开必须真算一张");
        Assert.That(renderer.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// 打开之后不做任何事 —— 渲染计数**不再增长**。
    ///
    /// 「每帧重算」这件事就是这么发生的：布局每跑一趟都会问一次「这会儿该长什么样」，
    /// 而这是个**打开就停在那儿的**面板，底下没动，答案就没变。
    /// </summary>
    [Test]
    public void 打开之后不做任何事渲染计数不再增长()
    {
        var renderer = new CountingRenderer();
        var surface = new FrostedSurface(renderer);

        surface.Show(一处());
        for (var i = 0; i < 60; i++)
            Assert.That(surface.Show(一处()), Is.False, $"第 {i + 2} 次问不该重算");

        Assert.That(renderer.Requests, Has.Count.EqualTo(1), "一帧一张，60 帧就是 60 张");
    }

    /// <summary>
    /// 底下那块变了（滚动 / 编辑 / 换了个底子）就得重算 —— 糊出来的必须是**新的**那一份，
    /// 不是缓存的旧图。
    /// </summary>
    [Test]
    public void 底下变了就重算而且算的是新的那一份()
    {
        var renderer = new CountingRenderer();
        var surface = new FrostedSurface(renderer);

        surface.Show(一处(stamp: 1));
        surface.Show(一处(stamp: 1));                       // 停在那儿：不重算
        Assert.That(surface.Show(一处(stamp: 2)), Is.True, "卷帘动过之后重开，得重算");

        Assert.Multiple(() =>
        {
            Assert.That(renderer.Requests.Select(r => r.SourceStamp), Is.EqualTo(new long[] { 1, 2 }),
                "第二次算的必须是新指纹那一份");
            Assert.That(surface.RenderCount, Is.EqualTo(2));
        });
    }

    /// <summary>换了面板尺寸、换了一套主题，都算「底下那一份过期了」。</summary>
    [Test]
    public void 换了尺寸或者换了主题也得重算()
    {
        var renderer = new CountingRenderer();
        var surface = new FrostedSurface(renderer);

        surface.Show(一处(width: 560));
        Assert.That(surface.Show(一处(width: 480)), Is.True, "窗口被拉过，位图得重新裁");
        Assert.That(surface.Show(一处(width: 480, dark: false)), Is.True, "切了主题，混色模式跟着换");

        Assert.That(renderer.Requests, Has.Count.EqualTo(3));
    }

    // ==================== 混色模式 ====================

    /// <summary>
    /// 颗粒的混色模式**按主题分叉**：深色 <c>soft-light</c>、浅色 <c>overlay</c>。
    ///
    /// ⚠️ 这一条跟 46 号票面、<c>spec-界面改版.md:219</c> 的**字面相反**。那两处紧接着给的实测理由
    /// 说的恰恰是「深色底上 overlay 几乎不动、浅色底上 soft-light 也几乎不动」—— 也就是票面列的那
    /// 一对偏偏就是不工作的那一对。权威记录是原型 <c>docs/prototype-主窗口改版.html:253-263</c>，
    /// 它的 CSS 与它的散文两边自洽：深色 soft-light / 浅色 overlay。用户挑的正是原型上那一版，
    /// 所以按原型来，理由与取舍写在 <see cref="FrostedGlass.GrainBlend"/> 上。
    /// </summary>
    [Test]
    public void 颗粒的混色模式按主题分叉()
    {
        var dark = FrostedGlass.GrainBlend(dark: true);
        var light = FrostedGlass.GrainBlend(dark: false);

        Assert.Multiple(() =>
        {
            Assert.That(dark, Is.Not.EqualTo(light),
                "两边一样 = 「分主题」这条等于没做，而它正是实测出来唯一能看见颗粒的摆法");

            // 具体值也要钉住：换错了方向，两个主题下颗粒都会消失，而且不报错
            Assert.That(dark, Is.EqualTo(BitmapBlendingMode.SoftLight), "深色底上 overlay 走乘法那半边，几乎不动");
            Assert.That(light, Is.EqualTo(BitmapBlendingMode.Overlay), "浅色底上 soft-light 走温和那半边，几乎不动");
        });
    }

    /// <summary>颗粒的强度也分主题 —— 浅色那档要弱得多（近白的底上叠噪点，差值本来就小）。</summary>
    [Test]
    public void 颗粒的强度按主题分叉()
    {
        Assert.That(FrostedGlass.GrainOpacity(dark: true),
            Is.GreaterThan(FrostedGlass.GrainOpacity(dark: false)));
        Assert.That(FrostedGlass.GrainOpacity(dark: true), Is.EqualTo(FrostedGlass.GrainOpacityDark));
        Assert.That(FrostedGlass.GrainOpacity(dark: false), Is.EqualTo(FrostedGlass.GrainOpacityLight));
    }

    /// <summary>深色那档只有 <c>ThemeVariant.Dark</c>；浅色与「跟随系统」（Default）都算浅色。</summary>
    [Test]
    public void 主题分叉只认深色那一档()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FrostedGlass.IsDark(ThemeVariant.Dark), Is.True);
            Assert.That(FrostedGlass.IsDark(ThemeVariant.Light), Is.False);
            Assert.That(FrostedGlass.IsDark(ThemeVariant.Default), Is.False);
        });
    }

    // ==================== 画法顺序 ====================

    /// <summary>
    /// 四步的顺序**就是**正确性：颗粒压在底色之前会被底色盖掉，压在最后会把字盖住。
    /// </summary>
    [Test]
    public void 画法顺序是糊完叠底色再撒颗粒最后压顶边高光()
    {
        Assert.That(FrostedGlass.Steps, Is.EqualTo(new[]
        {
            FrostedStep.Blur, FrostedStep.Base, FrostedStep.Grain, FrostedStep.TopHighlight,
        }));
    }
}
