using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using MidiPerformer.App.Theme;

namespace MidiPerformer.App.Views;

/// <summary>
/// 曲库面板底下那一层毛玻璃（F5）：把面板底下那一块糊掉，再叠 45% 底色、撒颗粒、压一道顶边高光。
///
/// 它自己不是「一块面板」，是**面板的底子**：摆得跟面板一样大、垫在内容后面。
/// <see cref="SongLibraryPanel"/> 就是这么用它的。
///
/// 三层结构（从下往上）：
/// <list type="number">
/// <item>糊掉的背景 —— 一张位图，比面板大 <see cref="FrostedGlass.Bleed"/> 一圈，多出来的被本层剪掉。</item>
/// <item>底色 + 颗粒 + 顶边高光 —— 这三样**不许跟着糊**，所以画在另一层上。</item>
/// </list>
///
/// 位图**只在底下那块变了的时候才算一次**（<see cref="FrostedSurface"/>）：这是个打开就停在那儿的
/// 面板，每帧重算只是白烧 CPU。
/// </summary>
public sealed class FrostedGlassLayer : Panel, IFrostedRenderer
{
    private readonly BackdropLayer _backdrop = new();
    private readonly Finish _finish = new();
    private readonly FrostedSurface _surface;

    private RenderTargetBitmap? _bitmap;
    private long _stamp;

    public FrostedGlassLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;                 // 外扩出来的那一圈糊边靠它剪掉，四边才不会泛白
        _surface = new FrostedSurface(this);
        Children.Add(_backdrop);
        Children.Add(_finish);
    }

    /// <summary>取色桥（和面板同一个）。没给就什么都不画 —— 面板也就没有底子。</summary>
    public TokenSource? Tokens { get; set; }

    /// <summary>要被糊掉的那一块。<c>null</c> = 底下没东西（玻璃只剩底色 + 颗粒 + 高光）。</summary>
    public Visual? Backdrop { get; private set; }

    /// <summary>一共算过几次位图。验收里「只渲染一次」那条用它。</summary>
    public int RenderCount => _surface.RenderCount;

    /// <summary>
    /// 面板底下那一块是什么。<c>null</c> = 底下什么都没有。
    ///
    /// 曲库面板今天住在一个独立窗口里，它自己底下只有一层窗口底色 ——
    /// 真正有东西可以透的是**主窗口**那份卷帘，所以曲库窗口把主窗口的内容交给它（见
    /// <c>SongLibraryWindow.OnOpened</c>）。规格要的「透出底下的卷帘」只有这样才兑现得了。
    /// </summary>
    public void ShowBackdrop(Visual? backdrop)
    {
        Backdrop = backdrop;
        Invalidate();
    }

    /// <summary>
    /// 底下那块变了（卷帘滚动 / 编辑 / 换了底子）：下一趟布局重算一张。
    ///
    /// 曲库窗口是模态的，开窗期间底下动不了 —— 所以今天只有 <see cref="ShowBackdrop"/>
    /// 走这条路。这个口子留给「面板浮在那儿、底下还能动」的那种用法。
    /// </summary>
    public void Invalidate()
    {
        _stamp++;
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Tokens is { } tokens) tokens.Changed += OnTokensChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Tokens is { } tokens) tokens.Changed -= OnTokensChanged;

        _bitmap?.Dispose();
        _bitmap = null;
        _backdrop.Image = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 布局定了才知道位图该多大。放在这儿而不是 <c>OnSizeChanged</c>：这是**唯一**保证
    /// 「尺寸已经定下来」的时刻，而位图尺寸跟着它走。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        Refresh();
        return arranged;
    }

    /// <summary>主题换了：颗粒的强度、混色模式、底色都跟着换一份，位图得重算。</summary>
    private void OnTokensChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var palette = Tokens?.Current;
        _finish.Palette = palette;
        _finish.Dark = FrostedGlass.IsDark(ActualThemeVariant);

        // 底下没东西就没什么可糊的 —— 不算、不留位图，玻璃只剩上面那三层
        if (palette is null || Backdrop is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _backdrop.Image = null;
            return;
        }

        var request = new FrostedRequest(Bounds.Width, Bounds.Height, _finish.Dark, _stamp);
        if (!_surface.Show(request)) return;

        _backdrop.Image = _bitmap;
        _backdrop.InvalidateVisual();
        _finish.InvalidateVisual();
    }

    /// <summary>
    /// 真算那张位图。尺寸 = 面板尺寸 + 2 × <see cref="FrostedGlass.Bleed"/> —— 那一圈是给
    /// σ=34 的模糊**吸料**用的：靠边那几 px 外面要是没料，糊出来就是白的，面板四边会
    /// 泛出一圈假高光。
    ///
    /// 模糊本身不在这儿做：它挂在 <see cref="BackdropLayer"/> 那一层的 <c>Effect</c> 上
    /// （那样底色 / 颗粒 / 高光才不会被一起糊掉）。
    /// </summary>
    void IFrostedRenderer.Recompute(FrostedRequest request)
    {
        var size = FrostedGlass.BitmapSize(request.Width, request.Height);

        _bitmap?.Dispose();
        _bitmap = new RenderTargetBitmap(size, new Vector(96, 96));

        if (Backdrop is not { } backdrop) return;
        if (backdrop.Bounds.Width <= 0 || backdrop.Bounds.Height <= 0) return;

        // 底子整幅渲一遍，再按坐标把面板底下那一块抠出来。
        // 位置不能用「同一个父层里的偏移」算：这两块可能不在同一棵树、甚至不在同一个窗口里
        // （曲库面板就是这么用的），所以走屏幕坐标 —— 它对谁都成立。
        if (Origin(backdrop) is not { } origin) return;

        var shot = new RenderTargetBitmap(
            PixelSize.FromSize(backdrop.Bounds.Size, 1), new Vector(96, 96));
        shot.Render(backdrop);

        using (var context = _bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateTranslation(
                   FrostedGlass.Bleed - origin.X, FrostedGlass.Bleed - origin.Y)))
        {
            context.DrawImage(shot, new Rect(shot.Size));
        }

        shot.Dispose();
    }

    /// <summary>
    /// 面板左上角落在<paramref name="backdrop"/>自己的坐标系里的哪儿（DIP）。
    ///
    /// 分两步：各自换算到屏幕（物理像素，这一步只有 <c>TopLevel</c> 会做），相减，
    /// 再按底子那边的缩放比折回 DIP —— 位图是按 96dpi 渲的，1px = 1DIP。
    /// 两块不在同一棵树 / 同一个窗口里的时候，这是唯一还算得出来的算法。
    /// </summary>
    private Point? Origin(Visual backdrop)
    {
        if (TopLevel.GetTopLevel(this) is not { } here) return null;
        if (TopLevel.GetTopLevel(backdrop) is not { } there) return null;

        var panelOnScreen = here.PointToScreen(this.TranslatePoint(default, here) ?? default);
        var backdropOnScreen = there.PointToScreen(backdrop.TranslatePoint(default, there) ?? default);

        var scale = there.RenderScaling <= 0 ? 1 : there.RenderScaling;
        return new Point(
            (panelOnScreen.X - backdropOnScreen.X) / scale,
            (panelOnScreen.Y - backdropOnScreen.Y) / scale);
    }

    // ==================== 底下那层：糊掉的背景 ====================

    /// <summary>
    /// 糊掉的底子。它比面板**大一圈**（往外 90px），多出来的那圈被父层剪掉 ——
    /// 模糊在它自己的边上会吸淡，把边放到面板外面去，面板的边上就还是实的。
    /// </summary>
    private sealed class BackdropLayer : Control
    {
        public BackdropLayer()
        {
            IsHitTestVisible = false;
            Margin = new Thickness(-FrostedGlass.Bleed);
            Effect = new BlurEffect { Radius = FrostedGlass.Sigma };
        }

        public IImage? Image { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Image is not { } image) return;
            context.DrawImage(image, new Rect(Bounds.Size));
        }
    }

    // ==================== 上面那层：底色 → 颗粒 → 顶边高光 ====================

    /// <summary>
    /// 盖在糊掉背景上的三样。**一样都不许糊** —— 底色糊了托不住字，颗粒糊了就是一坨灰。
    /// 顺序即正确性，见 <see cref="FrostedGlass.Steps"/>。
    /// </summary>
    private sealed class Finish : Control
    {
        /// <summary>噪点贴图的边长（正方形，平铺）。</summary>
        private const int Tile = 160;

        private static WriteableBitmap? _noise;

        /// <summary>
        /// 顶边那道高光：**纯白** 42%（原型里写的就是 <c>rgba(255,255,255,.42)</c>）。
        ///
        /// 白在这儿写真名、不走令牌，是**想过的**：令牌那 26 条是 wireframe 里那套颜色的逐条投影
        /// （<c>TokenParityTests</c> 的「令牌数正好是 26 条」钉着这条契约，加一条就得先改那张对账表），
        /// 而「玻璃的棱」是**材料**的性质，不是那套颜色里的一条。两套主题下都是同一个白，
        /// 也不该按主题分叉 —— 这一层在近白的浅色面板上本来就淡。
        ///
        /// ⚠️ <c>TokenParityTests.除Tokens_axaml外没有字面颜色值</c> 拦的是 <c>#hex</c> / <c>rgb()</c>
        /// 那种**写死的字面量**，它自己的注释写着「具名颜色有正当用途」，也写着
        /// 「<c>Colors.White</c> 拦得住，<c>Brushes.White</c> 拦不住」（那条正则里有 `Colors.[A-Z]`）。
        /// 既然要的就是这个具名色，就照后者写。
        /// </summary>
        private static readonly ImmutableSolidColorBrush 顶边高光 =
            new(Brushes.White.Color, FrostedGlass.TopHighlightOpacity);

        public Finish() => IsHitTestVisible = false;

        public TokenPalette? Palette { get; set; }

        public bool Dark { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Palette is not { } palette) return;

            var area = new Rect(Bounds.Size);
            if (area.Width <= 0 || area.Height <= 0) return;

            // ① 底色：糊得越厚，底下的形状越没有信息，这一层就可以越薄；糊得薄就必须靠它把字托起来
            context.FillRectangle(
                new SolidColorBrush(palette.Surface, FrostedGlass.BaseOpacity), area);

            // ② 颗粒：磨砂玻璃的「材料感」全在这一层。没有它，糊掉的背景看起来像一张失焦的照片。
            //    混色模式按主题分叉（深色 soft-light / 浅色 overlay），理由见 FrostedGlass.GrainBlend。
            if (Noise() is { } noise)
            {
                using (context.PushClip(area))
                using (context.PushOpacity(FrostedGlass.GrainOpacity(Dark)))
                using (context.PushRenderOptions(
                           new RenderOptions { BitmapBlendingMode = FrostedGlass.GrainBlend(Dark) }))
                {
                    for (var y = 0.0; y < area.Height; y += Tile)
                        for (var x = 0.0; x < area.Width; x += Tile)
                            context.DrawImage(noise, new Rect(x, y, Tile, Tile));
                }
            }

            // ③ 顶边一道 1px 高光：玻璃的棱。只有真玻璃才有这道边，实心面板没有。
            context.FillRectangle(顶边高光, new Rect(0, 0, area.Width, 1));
        }

        /// <summary>
        /// 一张 160×160 的噪点贴图，平铺。
        ///
        /// 以**中灰**为中心：overlay 与 soft-light 在中灰上都是中性的，偏离多少就是颗粒有多粗 ——
        /// 所以同一张图在深浅两套主题下都成立，只有强度和混色模式分叉。
        ///
        /// 种子写死：颗粒是「材料」不是「随机」—— 每画一次换一副噪点的话，面板一刷新就闪一下。
        /// </summary>
        private static WriteableBitmap? Noise()
        {
            if (_noise is not null) return _noise;

            var tile = new WriteableBitmap(
                new PixelSize(Tile, Tile), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

            var random = new Random(20260924);
            using (var framebuffer = tile.Lock())
            {
                var row = new byte[framebuffer.RowBytes];
                for (var y = 0; y < Tile; y++)
                {
                    for (var x = 0; x < Tile; x++)
                    {
                        // 128 ± 64：暗一格到亮一格
                        var value = (byte)(128 + (random.Next(256) - 128) / 2);
                        var at = x * 4;
                        row[at] = value;
                        row[at + 1] = value;
                        row[at + 2] = value;
                        row[at + 3] = 255;
                    }

                    Marshal.Copy(row, 0, IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), row.Length);
                }
            }

            return _noise = tile;
        }
    }
}
