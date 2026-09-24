using Avalonia.Media;
using Avalonia.Styling;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.App.Views;
using NUnit.Framework;
using static MidiPerformer.Adapters.Presenters.PianoRollPresenter;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 缩略条画什么、按什么顺序画。这一版缩略条的正确性有一半长在**顺序**上
/// （淡填充得垫在音符之前，否则调多淡都是糊的），另一半长在**切开**上
/// （跨过视口框的音符在框边上换一次浓度，而不是整条退到 55%）。
/// 绘制已经提成纯函数 <see cref="NavStripDraw.Strokes"/>，所以这一整块不碰 Avalonia 也能断言 ——
/// 不起控件、不做像素断言。
/// </summary>
public class RollNavStripDrawTests
{
    private const double 条宽 = 1000;
    private const double 条高 = 30;

    /// <summary>测试里的视口框：左边 340、右边 560。</summary>
    private const double 框左 = 340;
    private const double 框右 = 560;

    /// <summary>播放头落在框里，这样「播放头压在框上」才有得看。</summary>
    private const double 播放头X = 450;

    /// <summary>
    /// 每种令牌给一个互不相同的假颜色 —— 断言「这一笔画的是哪条令牌」靠它，不依赖 Tokens.axaml 的真实色值
    /// （那是 TokenParityTests 的活）。拿的是软件自己那条取色桥，所以「取串了令牌」在这儿也会露出来。
    /// </summary>
    private static readonly TokenPalette 假令牌表 = 造假令牌表();

    private static readonly NavColors 颜色 = NavColors.From(假令牌表);

    private static TokenPalette 造假令牌表()
    {
        int 序号 = 0;
        return TokenPalette.Resolve(
            (键, _) => 键 == TokenPalette.KeyPrefix + nameof(TokenPalette.Shadow)
                ? default(BoxShadows)
                : new SolidColorBrush(Color.FromRgb((byte)++序号, 0, 0)),
            ThemeVariant.Light);
    }

    /// <summary>画一遍，参数是场景里的音符（左边缘与宽度）。</summary>
    private static IReadOnlyList<NavStroke> 画(params (double X, double Width)[] 音符)
        => NavStripDraw.Strokes(条宽, 条高, 场景(音符), 颜色);

    private static NavScene 场景(
        (double X, double Width)[] 音符, double 视口宽 = 框右 - 框左, double 头X = 播放头X)
        => new()
        {
            BarCount = 40,
            BarWidth = 条宽 / 40,
            Notes = 音符.Select(n => new NavNote(n.X, 2, n.Width, 3)).ToArray(),
            ThumbX = 框左,
            ThumbWidth = 视口宽,
            PlayheadX = 头X
        };

    private static int 第几笔(NavStrokeKind 层, IReadOnlyList<NavStroke> 笔画)
    {
        for (int i = 0; i < 笔画.Count; i++)
            if (笔画[i].Kind == 层) return i;
        return -1;
    }

    private static List<NavStroke> 这一层(NavStrokeKind 层, IReadOnlyList<NavStroke> 笔画)
        => 笔画.Where(s => s.Kind == 层).ToList();

    // ==================== 顺序：这一版的要害 ====================

    [Test]
    public void 淡填充画在第一个音符之前()
    {
        var 笔画 = 画((400.0, 100.0), (600.0, 50.0));

        int 填充 = 第几笔(NavStrokeKind.ViewFill, 笔画);
        int 第一个音符 = 第几笔(NavStrokeKind.Note, 笔画);

        Assert.That(填充, Is.GreaterThanOrEqualTo(0), "视口框那层淡填充得画出来");
        Assert.That(第一个音符, Is.GreaterThanOrEqualTo(0), "音符得画出来");
        Assert.That(填充, Is.LessThan(第一个音符),
            "填充必须垫在音符之前 —— 「盖不住框里的音符」是画法保证的，不是把不透明度调低调出来的");
    }

    [Test]
    public void 框线和播放头画在所有音符之后()
    {
        var 笔画 = 画((400.0, 100.0), (600.0, 50.0));

        int 最后一个音符 = 笔画.Select((s, i) => (s, i)).Where(t => t.s.Kind == NavStrokeKind.Note).Max(t => t.i);
        int 框线 = 第几笔(NavStrokeKind.ViewLine, 笔画);
        int 头 = 第几笔(NavStrokeKind.Playhead, 笔画);

        Assert.That(框线, Is.GreaterThan(最后一个音符), "框是操作件里最底的一层，但它那圈线画在音符之上");
        Assert.That(头, Is.GreaterThan(框线), "播放头压框线 —— 它绝大多数时候正好落在框里");
        Assert.That(第几笔(NavStrokeKind.Border, 笔画), Is.EqualTo(笔画.Count - 1), "外边框收尾");
        Assert.That(第几笔(NavStrokeKind.Background, 笔画), Is.EqualTo(0), "底色在最下");
    }

    // ==================== 颜色：淡，而且是 Accent 的淡 ====================

    [Test]
    public void 视口框的淡填充是Accent的10趴而不是AccentSoft()
    {
        var 笔画 = 画((400.0, 100.0));

        var 填充 = 这一层(NavStrokeKind.ViewFill, 笔画).Single();

        Assert.That(填充.Color, Is.EqualTo(假令牌表.Accent), "淡填充就是 TokenAccent 本人，不另造色值");
        Assert.That(填充.Opacity, Is.EqualTo(NavStripDraw.ViewFillOpacity));
        Assert.That(NavStripDraw.ViewFillOpacity, Is.EqualTo(0.10),
            "6% 太弱、16% 会自己变成一块色块，三档上机看过的定的是 10%");

        // 防回归：把实色改回淡色的同时，顺序必须一起翻过来 —— 这里盯的是「AccentSoft 一次都不许出现」
        Assert.That(笔画.Where(s => s.Color == 假令牌表.AccentSoft).ToList(), Is.Empty,
            "视口框不再用实色 AccentSoft 铺底，那层实色会把框里的音符整块抹平");
    }

    [Test]
    public void 视口框自己不带底色只有一圈线()
    {
        var 笔画 = 画((400.0, 100.0));

        Assert.That(这一层(NavStrokeKind.ViewLine, 笔画).Single().Opacity, Is.EqualTo(1), "框线是满色的一条");
        Assert.That(这一层(NavStrokeKind.ViewFill, 笔画).Single().Opacity, Is.LessThan(0.5),
            "框里那层填充只能是淡的 —— 四边都不许有实色");
    }

    [Test]
    public void 每笔的颜色都从令牌表里来()
    {
        var 笔画 = 画((300.0, 300.0), (400.0, 100.0), (600.0, 100.0), (900.0, 50.0));
        var 令牌色 = new[]
        {
            颜色.Surface, 颜色.Divider, 颜色.Accent, 颜色.ViewLine, 颜色.Playhead, 颜色.Border
        };

        foreach (var 一笔 in 笔画)
            Assert.That(令牌色, Does.Contain(一笔.Color), "缩略条不写死颜色，每笔都得是令牌里取来的");
    }

    // ==================== 切开：框边就是浓度的分界 ====================

    [Test]
    public void 框里的音符满色框外的退到55趴()
    {
        var 笔画 = 画((400.0, 100.0), (100.0, 100.0));
        var 音符 = 这一层(NavStrokeKind.Note, 笔画);

        Assert.That(音符.Count, Is.EqualTo(2), "两条都不跨框边，各画一段");
        Assert.That(音符[0].Opacity, Is.EqualTo(1), "框里的满色");
        Assert.That(音符[1].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity), "框外的退到 55%");
        Assert.That(NavStripDraw.OutsideNoteOpacity, Is.EqualTo(0.55));
    }

    [Test]
    public void 跨过框边的音符在框边切成三截()
    {
        // 200..700 一条长音，横跨整个视口框（340..560）
        var 笔画 = 画((200.0, 500.0));
        var 音符 = 这一层(NavStrokeKind.Note, 笔画);

        Assert.That(音符.Count, Is.EqualTo(3), "框外一截 + 框内一截 + 框外一截");

        Assert.That(音符[0].Area.X, Is.EqualTo(200));
        Assert.That(音符[0].Area.Right, Is.EqualTo(框左), "左边那截到框边为止");
        Assert.That(音符[0].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity));

        Assert.That(音符[1].Area.X, Is.EqualTo(框左));
        Assert.That(音符[1].Area.Right, Is.EqualTo(框右), "框里那截正好是框这一段");
        Assert.That(音符[1].Opacity, Is.EqualTo(1), "框里满色 —— 同一个音符自己在框边上换一次浓度");

        Assert.That(音符[2].Area.X, Is.EqualTo(框右));
        Assert.That(音符[2].Area.Right, Is.EqualTo(700));
        Assert.That(音符[2].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity));
    }

    [Test]
    public void 只跨一边的音符切两截()
    {
        var 笔画 = 画((300.0, 100.0)); // 300..400，右边压在框左边缘上
        var 音符 = 这一层(NavStrokeKind.Note, 笔画);

        Assert.That(音符.Count, Is.EqualTo(2));
        Assert.That(音符[0].Area.Right, Is.EqualTo(框左));
        Assert.That(音符[0].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity));
        Assert.That(音符[1].Area.X, Is.EqualTo(框左));
        Assert.That(音符[1].Area.Right, Is.EqualTo(400));
        Assert.That(音符[1].Opacity, Is.EqualTo(1));
    }

    [Test]
    public void 切开不丢面积()
    {
        var 用例 = new (double X, double W)[]
        {
            (400.0, 100.0),   // 整个在框里
            (100.0, 100.0),   // 整个在框外
            (300.0, 100.0),   // 只压左边缘
            (200.0, 500.0),   // 横跨整框
            (558.0, 100.0),   // 只压右边缘，框里只剩 2px
            (300.0, 261.0),   // 右边露出去 1px
            (339.5, 100.0),   // 左边缘正好压在框左边缘上
            (560.0, 100.0),   // 左边缘正好压在框右边缘上
        };

        foreach (var (x, w) in 用例)
        {
            var 音符 = 这一层(NavStrokeKind.Note, 画((x, w)));

            Assert.That(音符, Is.Not.Empty, $"{x}+{w} 至少得画出一截来");
            Assert.That(音符.Sum(s => s.Area.Width), Is.EqualTo(w).Within(1e-9),
                $"{x}+{w} 切开与不切开的总面积得一致");
            Assert.That(音符[0].Area.X, Is.EqualTo(x).Within(1e-9), $"{x}+{w} 起点不挪");

            // 首尾相接：前一截的右边缘就是后一截的左边缘（不留缝也不叠）
            for (int i = 1; i < 音符.Count; i++)
                Assert.That(音符[i].Area.X, Is.EqualTo(音符[i - 1].Area.Right).Within(1e-9),
                    $"{x}+{w} 第 {i} 截得接在第 {i - 1} 截后面");
        }
    }

    [Test]
    public void 窄过下限的一截并进更宽的邻居()
    {
        // 300..561：右边只露出去 1px（下限是条宽的 0.35% = 3.5px）
        var 音符 = 这一层(NavStrokeKind.Note, 画((300.0, 261.0)));

        Assert.That(音符.Count, Is.EqualTo(2), "那 1px 不做成单独一截");
        Assert.That(音符[0].Area.X, Is.EqualTo(300));
        Assert.That(音符[0].Area.Right, Is.EqualTo(框左));
        Assert.That(音符[0].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity));
        Assert.That(音符[1].Area.X, Is.EqualTo(框左));
        Assert.That(音符[1].Area.Right, Is.EqualTo(561), "并进更宽的那个邻居 —— 面积一分不少");
        Assert.That(音符[1].Opacity, Is.EqualTo(1), "并进去之后仍然按框内算：框里那一大截还是满色");
        Assert.That(音符.Sum(s => s.Area.Width), Is.EqualTo(261).Within(1e-9));
    }

    [Test]
    public void 框宽为零时整条退到55趴()
    {
        var 笔画 = NavStripDraw.Strokes(条宽, 条高, 场景([(300.0, 100.0)], 视口宽: 0), 颜色);
        var 音符 = 这一层(NavStrokeKind.Note, 笔画);

        Assert.That(音符.Count, Is.EqualTo(1), "框收成一条线时切不出框里那一截");
        Assert.That(音符[0].Opacity, Is.EqualTo(NavStripDraw.OutsideNoteOpacity));
        Assert.That(音符[0].Area.Width, Is.EqualTo(100).Within(1e-9));
    }

    // ==================== 别的几层：原样的规矩 ====================

    [Test]
    public void 播放头落在条外就不画()
    {
        var 笔画 = NavStripDraw.Strokes(条宽, 条高, 场景([(400.0, 100.0)], 头X: 条宽 + 50), 颜色);

        Assert.That(第几笔(NavStrokeKind.Playhead, 笔画), Is.EqualTo(-1),
            "播放头出了条就不该画出去压到隔壁控件上");
    }

    [Test]
    public void 每四小节一根分隔线()
    {
        var 笔画 = 画((400.0, 100.0));
        var 分隔线 = 这一层(NavStrokeKind.Divider, 笔画);

        Assert.That(分隔线.Count, Is.EqualTo(10), "40 小节、每 4 小节一根，含第 0 小节那根");

        // 线是竖线：X 是横坐标，Y..Bottom 是跨度（宽度不用）
        foreach (var 线 in 分隔线)
        {
            Assert.That(线.Area.Y, Is.EqualTo(0));
            Assert.That(线.Area.Bottom, Is.EqualTo(条高));
            Assert.That(线.Color, Is.EqualTo(颜色.Divider), "分隔线用现成的 LineSoft");
        }

        Assert.That(分隔线[1].Area.X, Is.EqualTo(100.5), "落在整像素 +0.5 上才不糊");
    }

    [Test]
    public void 底色和外边框铺满整条()
    {
        var 笔画 = 画((400.0, 100.0));

        var 底 = 笔画[0];
        Assert.That(底.Color, Is.EqualTo(颜色.Surface));
        Assert.That(底.Area.Width, Is.EqualTo(条宽));
        Assert.That(底.Area.Height, Is.EqualTo(条高));

        var 外框 = 笔画[^1];
        Assert.That(外框.Kind, Is.EqualTo(NavStrokeKind.Border));
        Assert.That(外框.Area.Width, Is.EqualTo(条宽 - 1), "外边框往里让 1px，右边缘不贴出去");
        Assert.That(外框.Area.Height, Is.EqualTo(条高 - 1));
    }

    // ==================== 层 → 画法：上机才看得见的那个 bug ====================

    [Test]
    public void 每一种层都归了类()
    {
        foreach (var 层 in Enum.GetValues<NavStrokeKind>())
            Assert.That(() => NavStripDraw.ShapeOf(层), Throws.Nothing,
                $"{层} 没归类 —— 新加层时得给它挑一类画法，不能让它落到 default 上");
    }

    [Test]
    public void 外边框和视口框画成一圈线音符画成一块()
    {
        // 回归：上一版 Render 的 switch 是「这几个 Kind 画线，其余 default 填」，
        // 外边框排在**最后**画又落进 default —— 它把整条（连音符带播放头）填成一整块实色，
        // 上机截图是一整条 Line 色的空带子。这条断言盯的就是那个归类。
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.Border), Is.EqualTo(NavStrokeShape.Outline),
            "外边框画一圈线 —— 归成「填」会把整条抹掉");
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.ViewLine), Is.EqualTo(NavStrokeShape.Outline),
            "视口框自己不带底色，只有一圈线");
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.Playhead), Is.EqualTo(NavStrokeShape.Line),
            "播放头是一条竖线，不是一块（归成填会糊掉它压着的那截框线）");
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.Note), Is.EqualTo(NavStrokeShape.Fill));
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.Background), Is.EqualTo(NavStrokeShape.Fill));
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.ViewFill), Is.EqualTo(NavStrokeShape.Fill));
        Assert.That(NavStripDraw.ShapeOf(NavStrokeKind.Divider), Is.EqualTo(NavStrokeShape.Line));
    }

    [Test]
    public void 画出来的每一笔都认得出画法()
    {
        var 笔画 = 画((300.0, 300.0), (600.0, 50.0));

        Assert.That(笔画, Is.Not.Empty);
        foreach (var 一笔 in 笔画)
            Assert.That(NavStripDraw.ShapeOf(一笔.Kind), Is.AnyOf(
                    NavStrokeShape.Fill, NavStrokeShape.Line, NavStrokeShape.Outline),
                $"{一笔.Kind} 得归到这三类里的一类");
    }
}
