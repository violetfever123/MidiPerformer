namespace MidiPerformer.App.Views;

/// <summary>
/// 「糊哪一块」这件事的完整描述：面板多大、哪套主题、底下那块的指纹。
///
/// 三个都是**结构相等**的（record struct），所以「同一个请求」能直接拿来比 ——
/// <see cref="FrostedSurface"/> 就是靠这个判断「还用不用重算」的。
/// </summary>
/// <param name="Width">面板宽（DIP）。</param>
/// <param name="Height">面板高（DIP）。</param>
/// <param name="Dark">当前主题是不是深色（混色模式与颗粒强度按它分叉）。</param>
/// <param name="SourceStamp">
/// 面板底下那一块的指纹。变了 = 底下真的换了东西（卷帘滚动 / 编辑 / 播放），得重算。
/// </param>
public readonly record struct FrostedRequest(double Width, double Height, bool Dark, long SourceStamp);

/// <summary>
/// 把面板底下那一块糊成一张位图。真实现走 RenderTargetBitmap（见 <see cref="FrostedGlassLayer"/>），
/// 测试里换成计数替身 —— 「只渲染一次」那条断言要数的就是这里的调用次数。
/// </summary>
public interface IFrostedRenderer
{
    /// <summary>算一张毛玻璃位图。尺寸由 <see cref="FrostedGlass.BitmapSize"/> 定，实现不许自己另算一份。</summary>
    void Recompute(FrostedRequest request);
}

/// <summary>
/// 「算一次、存着用」：打开面板算一次，之后同一个请求再来多少次都不再算。
///
/// 为什么必须是这个语义而不是每帧算：这是个**打开就停在那儿的选取器** ——
/// 底下那层卷帘不动，糊出来的位图就不变，每帧重算一遍只是白烧 CPU。
/// 反过来说，底下真变了（换了尺寸、切了主题、卷帘滚了）就必须重算，
/// 否则面板上糊的是**上一次的旧图**。
/// </summary>
public sealed class FrostedSurface
{
    private readonly IFrostedRenderer _renderer;

    /// <param name="renderer">真正干活的那个。测试里是计数替身。</param>
    public FrostedSurface(IFrostedRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    /// <summary>一共算过几次。断言「只渲染一次」数的就是它。</summary>
    public int RenderCount { get; private set; }

    /// <summary>上一次算的是哪个请求。<c>null</c> = 一次都还没算过。</summary>
    public FrostedRequest? Last { get; private set; }

    /// <summary>
    /// 面板这会儿该长什么样。请求跟上次一样就什么都不做。
    /// </summary>
    /// <returns>真算了返回 <c>true</c>（调用方据此重画）；原样返回 <c>false</c>。</returns>
    public bool Show(FrostedRequest request)
    {
        if (Last == request) return false;

        _renderer.Recompute(request);
        Last = request;
        RenderCount++;
        return true;
    }
}
