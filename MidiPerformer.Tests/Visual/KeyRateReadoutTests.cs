using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 50 号：按键速度读数（平均 + 峰值 + 每秒直方图）里**不靠眼睛看**的那一半。
///
/// 判据分两层：
/// - 三个数怎么算、哪一秒是峰值、柱子多高 —— 全是 <see cref="KeyRateReadout"/> 里的纯函数
///   （一份 Avalonia 都不碰），喂的**不是音符而是事件表**：<c>EventTable.Build</c> 那条链，
///   演奏时真按下去的就是它。示例曲子的期望值是**手算的**（按 tick 换算，注释里写着），
///   不是照着实现抄的 —— 抄一遍就成了自己和自己对拍。
/// - 「块贴在演奏轨底下、直方图缩进 84px、峰值那根红的、峰值那个数重一档、播放期间一个数都不动」
///   —— 读 XAML / 源码文本断言（和 49 号的 <c>PitchRangeReadoutTests</c> 同一套办法）。
///
/// <b>证明不了什么</b>：屏幕上真画成什么样（那一根真的红、那个数真的大一号、一百多根柱子
/// 在一格一秒的宽度下还看得清）归上机；「勾指起誓 轨1 在真机上量出 3.0 平均 / 9 峰值 / 2:34」
/// 也只有上机那条路能证 —— 库里那首歌（mproj）不在测试里，这里的曲子是手搭的。
/// </summary>
public class KeyRateReadoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>480 tick 的四分音符，默认速度（500000 µs/四分音符 = 120 BPM）下是 0.5 秒。</summary>
    private const long 四分 = 480;

    /// <summary>一秒 = 960 tick（480 tick 的四分音符 × 2 拍）。</summary>
    private const long 一秒Tick = 960;

    // ==================== 手搭的曲子 ====================

    /// <summary>
    /// 一首单声部曲子：全是 C4，起点按 tick 给，时值一样。
    ///
    /// tick 换算：<c>tick × 500000 / 480 / 1e6</c> 秒 —— 960 tick = 1 秒。
    /// 起点**故意落在 .25 / .75 这种非整秒上**：整秒正好是桶的边界，浮点误差一抖就会从这一桶
    /// 掉进上一桶，那把尺子就自己先坏了（不是产品坏了）。
    /// </summary>
    private static Song 曲子(long 时值, params long[] 起点Tick)
        => 建曲(0, 时值, 起点Tick.Select(t => (音高: 60, 起点: t)).ToArray());

    /// <summary>同上，音高与起点成对给，还能整轨移调。</summary>
    private static Song 建曲(int transpose, long 时值, params (int 音高, long 起点)[] 音符)
        => new(
            new[]
            {
                new Track(0, 0, "主旋律", 24,
                    音符.Select(n => new Note(n.音高, n.起点, 时值, 100)).ToArray(),
                    transpose)
            },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>把这首曲子建成事件表 —— 走的就是演奏与读数共用的那一条链。</summary>
    private static (List<EventBuilder.PhysicalEvent> Events, double Seconds) 事件表(Song song)
    {
        var (events, seconds, _) = EventTable.Build(song, 0, InputTiming.FromIndex(1), null);
        return (events, seconds);
    }

    /// <summary>一条链走到底：曲子 → 事件表 → 读数。</summary>
    private static KeyRateState 量(Song song)
    {
        var (events, seconds) = 事件表(song);
        return KeyRateReadout.Measure(events, seconds);
    }

    /// <summary>事件表里按下了几个音键（读数认的就是这个数）。</summary>
    private static int 按下几下(IReadOnlyList<EventBuilder.PhysicalEvent> events)
        => events.Count(e => e.Kind == EventBuilder.K_Key && e.Down);

    // ==================== 三个数与事件表对拍 ====================

    /// <summary>
    /// **这一票的判据**：平均 / 峰值 / 每秒桶全部与 <c>EventBuilder.Build</c> 那张事件表对拍。
    ///
    /// 手算（起点 0 / 1.25 / 2.25 / 2.75 / 3.25 / 3.75 / 4.25 / 4.75 秒，每个 0.5 秒）：
    /// 第 0 秒 1 下、第 1 秒 1 下、第 2 秒 2 下（2.25 / 2.75）、第 3 秒 2 下、第 4 秒 2 下，
    /// 最后一个音 4.75 + 0.5 = 5.25 秒收尾 ⇒ 桶数 ⌈5.25⌉ = 6（第 5 秒空着，留一根 2px 的底），
    /// 按键 8 下、峰值 2（第 2 / 3 / 4 秒并列 ⇒ 取最左的第 2 秒）、平均 8 ÷ 5.25 = 1.52…→「1.5」。
    /// </summary>
    [Test]
    public void 平均峰值和每秒桶与事件表对拍()
    {
        // 240 tick = 0.25 秒、720 tick = 0.75 秒（tick = 960 × 秒）
        var 曲 = 曲子(四分,
            0, 一秒Tick + 240, 2 * 一秒Tick + 240, 2 * 一秒Tick + 720,
            3 * 一秒Tick + 240, 3 * 一秒Tick + 720, 4 * 一秒Tick + 240, 4 * 一秒Tick + 720);

        var (events, seconds) = 事件表(曲);
        var state = KeyRateReadout.Measure(events, seconds);

        Assert.Multiple(() =>
        {
            Assert.That(按下几下(events), Is.EqualTo(8), "事件表里按下了 8 下");
            Assert.That(seconds, Is.EqualTo(5.25).Within(1e-9), "这张表铺了 5.25 秒（最后一个事件抬键）");

            Assert.That(state.Keys, Is.EqualTo(8), "按键数 = 事件表里按下的音键数");
            Assert.That(state.Seconds, Is.EqualTo(seconds), "时长就是 Build 一起吐出来的那个数");
            Assert.That(state.Buckets, Is.EqualTo(new[] { 1, 1, 2, 2, 2, 0 }), "一秒一个桶，逐桶对");
            Assert.That(state.PeakBucket, Is.EqualTo(2), "并列时取最左那一秒（原型里是 indexOf(max)）");
            Assert.That(state.Peak, Is.EqualTo(2), "峰值 = 那一桶的高度");
            Assert.That(state.Average, Is.EqualTo(8 / 5.25).Within(1e-12), "平均 = 按键数 ÷ 时长");
            Assert.That(KeyRateReadout.AverageText(state), Is.EqualTo("1.5"));
            Assert.That(KeyRateReadout.PeakText(state), Is.EqualTo("2"));
            Assert.That(state.HasData, Is.True);
        });
    }

    /// <summary>
    /// 桶数就是**这张表铺了几个整秒**（向上取整），一根不多一根不少；桶里的数加起来正好等于
    /// 按键数（一下都没漏，也没多算）。两个数是同一张表的两面。
    /// </summary>
    [Test]
    public void 桶数正好是几个整秒加起来不多不少()
    {
        foreach (var 曲 in new[]
                 {
                     曲子(四分, 0, 240, 1200),                  // 3.25 秒收尾 → 4 桶
                     曲子(96, 240, 336, 2160, 3120, 3216),      // 3.45 秒收尾 → 4 桶
                     曲子(96, 120, 240, 360, 480, 600, 720),    // 0.85 秒收尾 → 1 桶
                 })
        {
            var (events, seconds) = 事件表(曲);
            var state = KeyRateReadout.Measure(events, seconds);

            Assert.Multiple(() =>
            {
                Assert.That(state.Buckets, Has.Count.EqualTo((int)Math.Ceiling(seconds)),
                    $"桶数该是 ⌈{seconds}⌉");
                Assert.That(state.Buckets.Sum(), Is.EqualTo(state.Keys), "桶里的数加起来 = 按键数");
                Assert.That(state.Buckets, Has.All.GreaterThanOrEqualTo(0));
                Assert.That(KeyRateReadout.AxisEndText(state),
                    Is.EqualTo($"0:{(int)Math.Ceiling(seconds):D2}"), "右端刻度就是桶数那个整秒数");
            });
        }
    }

    /// <summary>
    /// 读数认的是**事件表**，不是这首曲子的音符 —— 「同一个真相源」最硬的一条。
    ///
    /// 多给的那个音整轨移调 +40 之后是 140，越出 MIDI 音域：映射那一步就跳过它（<c>InRange=false</c>），
    /// 事件表里根本没有它，按键数**不动**。反过来，按音符数去数就会多出这一下 ——
    /// 屏幕上就会比耳朵里多一下。
    ///
    /// （移调而不是直接给个高音高：越界音压根不进 <c>AutoBaseOctave</c> 的候选，基准八度不会跟着挪，
    /// 两首曲子的主旋律落在同一个档位、同一个键上，比出来的差就只有「那一个音」。）
    /// </summary>
    [Test]
    public void 读数认的是事件表不是音符()
    {
        const int 移调 = 40;   // 主旋律的三个音抬到 100（还在表里），多给的那个抬到 140（表里没有）
        var 原件 = 建曲(移调, 四分, (60, 0), (60, 1200), (60, 2400));
        var 多一个音 = 建曲(移调, 四分, (60, 0), (100, 480), (60, 1200), (60, 2400));

        var 原件读数 = 量(原件);
        var 多一个音读数 = 量(多一个音);
        var (多一个音事件, _) = 事件表(多一个音);

        Assert.Multiple(() =>
        {
            Assert.That(多一个音.Tracks[0].NoteCount, Is.EqualTo(4), "曲子里是 4 个音");
            Assert.That(按下几下(多一个音事件), Is.EqualTo(3),
                "事件表里只有 3 下 —— 超出 MIDI 音域的那个音压根没进表");
            Assert.That(多一个音读数.Keys, Is.EqualTo(原件读数.Keys), "越界音不该让按键数多一下");
            Assert.That(多一个音读数.Buckets, Is.EqualTo(原件读数.Buckets), "桶也逐桶一样");
            Assert.That(多一个音读数.Seconds, Is.EqualTo(原件读数.Seconds).Within(1e-9));
        });
    }

    // ==================== 四个边界 ====================

    /// <summary>
    /// 空轨（0 音符）：没有按键、没有时长、没有桶 —— 而且**不许出现 NaN / Infinity**（分母是 0）。
    /// 这一块整块不出现：没歌就没得说，不写「0.0 键/秒」这种空话。
    /// </summary>
    [Test]
    public void 空轨没有读数也不许除出个无穷()
    {
        var (events, seconds) = 事件表(曲子(四分));      // 一个音都不给
        var state = KeyRateReadout.Measure(events, seconds);

        Assert.Multiple(() =>
        {
            Assert.That(events, Is.Empty, "空轨建出来就是一张空表");
            Assert.That(seconds, Is.EqualTo(0));
            Assert.That(state.Keys, Is.EqualTo(0));
            Assert.That(state.Seconds, Is.EqualTo(0));
            Assert.That(state.Buckets, Is.Empty);
            Assert.That(state.PeakBucket, Is.EqualTo(-1), "没有桶就没有峰值那一秒");
            Assert.That(state.Peak, Is.EqualTo(0));
            Assert.That(state.Average, Is.EqualTo(0));
            Assert.That(double.IsFinite(state.Average), Is.True, "0 ÷ 0 不许漏出来");
            Assert.That(KeyRateReadout.AverageText(state), Is.EqualTo("0.0"));
            Assert.That(state.HasData, Is.False, "整块不出现");
        });
    }

    /// <summary>
    /// 单音：整首就一个音，时长就是这个音自己的时值（0.5 秒）—— 桶只有一个，那一下就在桶 0，
    /// 平均 1 ÷ 0.5 = 2.0。**不是**「时长 0」：表里那个音有按下也有抬起（至少跨一帧）。
    /// </summary>
    [Test]
    public void 单音就一个桶那一下就在桶零()
    {
        var state = 量(曲子(四分, 0));

        Assert.Multiple(() =>
        {
            Assert.That(state.Keys, Is.EqualTo(1));
            Assert.That(state.Seconds, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(state.Buckets, Is.EqualTo(new[] { 1 }));
            Assert.That(state.PeakBucket, Is.EqualTo(0));
            Assert.That(state.Peak, Is.EqualTo(1));
            Assert.That(state.Average, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(state.HasData, Is.True, "一个音也是读数，得画出来");
        });
    }

    /// <summary>
    /// 时长真的是 0（退化到「一瞬间」的那张表）：平均给 0，不给无穷也不给 NaN。
    ///
    /// 走的是**除零那一格**，所以这份事件表是手写的 —— 真链子建不出 0 秒的表
    /// （音键的抬起至少跨一帧，见上一条）。
    /// </summary>
    [Test]
    public void 时长为零时平均给零不许是NaN也不许是无穷()
    {
        var 一瞬间 = new List<EventBuilder.PhysicalEvent>
        {
            new(0, EventBuilder.K_Key, 'v', true, ""),
        };

        foreach (double 时长 in new[] { 0.0, -1.0, double.NaN })
        {
            var state = KeyRateReadout.Measure(一瞬间, 时长);

            Assert.Multiple(() =>
            {
                Assert.That(state.Keys, Is.EqualTo(1), "按下的那一下还是数出来的");
                Assert.That(state.Seconds, Is.EqualTo(0), "时长退化成 0");
                Assert.That(state.Buckets, Is.Empty, "没有可画的横轴就不给桶");
                Assert.That(state.PeakBucket, Is.EqualTo(-1));
                Assert.That(state.Average, Is.EqualTo(0));
                Assert.That(double.IsFinite(state.Average), Is.True, $"时长 {时长} 时不能漏出 NaN / 无穷");
                Assert.That(state.HasData, Is.False);
            });
        }
    }

    /// <summary>峰值出现在第一秒：第一秒 4 下、后面都稀疏 —— 红的是**最左边**那一根。</summary>
    [Test]
    public void 峰值在第一秒()
    {
        // 0.25 / 0.35 / 0.45 / 0.55 秒各一下（0.1 秒一个音，互不重叠），后面 2.25 / 3.25 各一下
        var state = 量(曲子(96, 240, 336, 432, 528, 2160, 3120));

        Assert.Multiple(() =>
        {
            Assert.That(state.Buckets, Is.EqualTo(new[] { 4, 0, 1, 1 }));
            Assert.That(state.PeakBucket, Is.EqualTo(0), "最密的是第 0 秒");
            Assert.That(state.Peak, Is.EqualTo(4));
        });
    }

    /// <summary>峰值出现在最后一秒：红的是**最右边**那一根（下标 = 桶数 − 1）。</summary>
    [Test]
    public void 峰值在最后一秒()
    {
        // 0.25 / 1.25 / 2.25 秒各一下，3.25 起 0.1 秒一个音连按四下
        var state = 量(曲子(96, 240, 1200, 2160, 3120, 3216, 3312, 3408));

        Assert.Multiple(() =>
        {
            Assert.That(state.Buckets, Is.EqualTo(new[] { 1, 1, 1, 4 }));
            Assert.That(state.PeakBucket, Is.EqualTo(state.Buckets.Count - 1), "最密的是最后一秒");
            Assert.That(state.Peak, Is.EqualTo(4));
        });
    }

    /// <summary>并列时红的是**先出现**的那一秒（原型里 renderHisto 用的是 indexOf(max)）。</summary>
    [Test]
    public void 并列时红的是最左那一秒()
    {
        // 0.25 / 0.35（第 0 秒 2 下）与 2.25 / 2.35（第 2 秒 2 下）—— 一样密
        var state = 量(曲子(96, 240, 336, 2160, 2256));

        Assert.Multiple(() =>
        {
            Assert.That(state.Buckets, Is.EqualTo(new[] { 2, 0, 2 }));
            Assert.That(state.Peak, Is.EqualTo(2));
            Assert.That(state.PeakBucket, Is.EqualTo(0), "两边一样密时红的是左边那一秒");
        });
    }

    // ==================== 柱子的高矮 ====================

    /// <summary>
    /// 柱子按峰值归一：峰值那一秒顶到 26px（原型 <c>.histo{height:26px}</c>），
    /// 一下都没有的那秒也留 2px 的底（原型 <c>min-height:2px</c>）—— 空的那一秒是「0」，
    /// 不是「没有这一秒」。高矮只有一个来源，颜色不管高矮。
    /// </summary>
    [Test]
    public void 柱子的高矮按峰值归一空的也留一条底()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KeyRateReadout.BarHeight(9, 9), Is.EqualTo(KeyRateReadout.HistogramHeight));
            Assert.That(KeyRateReadout.BarHeight(0, 9), Is.EqualTo(KeyRateReadout.MinBarHeight));
            Assert.That(KeyRateReadout.BarHeight(9, 0), Is.EqualTo(0), "没有峰值就没什么可画的");
            Assert.That(KeyRateReadout.HistogramHeight, Is.EqualTo(26));
            Assert.That(KeyRateReadout.MinBarHeight, Is.EqualTo(2));

            // 单调不减、且谁都不许顶出那一行的高度
            double 前 = -1;
            for (int v = 0; v <= 9; v++)
            {
                double 高 = KeyRateReadout.BarHeight(v, 9);
                Assert.That(高, Is.GreaterThanOrEqualTo(前), $"v={v} 比上一根矮了");
                Assert.That(高, Is.InRange(KeyRateReadout.MinBarHeight, KeyRateReadout.HistogramHeight));
                前 = 高;
            }
        });
    }

    // ==================== 三个数的字 ====================

    /// <summary>
    /// 横轴那行字：左端 <c>0:00</c>、右端总时长、中间那句「整曲每秒按键数 · 红的那一秒是峰值」
    /// —— 中间那句是**规格里点名的原句**，在这儿钉死（界面上那句字只有这一个来源）。
    /// </summary>
    [Test]
    public void 横轴那行字()
    {
        var state = 量(曲子(四分, 0, 1200));

        Assert.Multiple(() =>
        {
            Assert.That(KeyRateReadout.AxisStartText(), Is.EqualTo("0:00"));
            Assert.That(KeyRateReadout.AxisEndText(state), Is.EqualTo("0:02"), "一格一秒，两个整秒");
            Assert.That(KeyRateReadout.AxisNote, Is.EqualTo("整曲每秒按键数 · 红的那一秒是峰值"));
        });
    }

    // ==================== XAML：块摆在哪、长什么样 ====================

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string 仓库根 => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string AppDir => Path.Combine(仓库根, "MidiPerformer.App");

    private static XElement 窗() => XDocument.Load(Path.Combine(AppDir, "Views", "PerformerWindow.axaml")).Root!;

    private static string? 属性(XElement e, string 名) => e.Attribute(名)?.Value;

    private static string? 名字(XElement e) => e.Attribute(Xaml + "Name")?.Value;

    private static XElement 控件(XElement 根, string 名) =>
        根.Descendants().FirstOrDefault(e => 名字(e) == 名)
        ?? throw new AssertionException($"演奏器窗口里找不到 x:Name=\"{名}\" 的控件");

    /// <summary>窗口的样式表里那一条规则（选择器 + 它的 setter）。</summary>
    private static Dictionary<string, string> 样式(XElement 根, string 选择器)
    {
        var 规则 = 根.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Style" && 属性(e, "Selector") == 选择器)
            ?? throw new AssertionException($"样式表里没有 \"{选择器}\" 这条规则");

        return 规则.Elements()
            .Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(e => 属性(e, "Property")!, e => 属性(e, "Value") ?? "");
    }

    /// <summary>样式表里所有规则的先后（同优先级谁后写谁赢，所以次序本身是判据）。</summary>
    private static List<string> 规则次序(XElement 根) =>
        根.Descendants().Where(e => e.Name.LocalName == "Style")
            .Select(e => 属性(e, "Selector") ?? "").ToList();

    /// <summary>
    /// 块的位置：**紧贴「演奏轨」那一行、压在 38 根细条上面** —— 不另起一行、不单独成块：
    /// 选完轨第一个想知道的就是「这首弹得动吗」，这条直方图是它的细节。
    /// </summary>
    [Test]
    public void 按键速度块紧贴在演奏轨下面()
    {
        var 顺序 = 窗().Descendants().ToList();
        int 位(string 名) => 顺序.FindIndex(e => 名字(e) == 名);

        Assert.Multiple(() =>
        {
            Assert.That(位("KeyRate"), Is.GreaterThan(位("TrackCombo")), "块该在演奏轨那一行下面");
            Assert.That(位("KeyRate"), Is.LessThan(位("PitchRange")), "块该压在 38 根细条上面");
            Assert.That(位("KeyRate"), Is.LessThan(位("TimingCombo")), "整块都在时序上面");

            // 上面那一行：74 的标签列 + 值那一栏（和别的行同一套格子），标签写「按键速度」
            var 行 = 控件(窗(), "KeyRate").Elements().First();
            Assert.That(行.Name.LocalName, Is.EqualTo("Grid"));
            Assert.That(属性(行, "ColumnDefinitions"), Is.EqualTo("74,*"), "和别的行同一套格子");
            Assert.That(行.Elements().First().Attribute("Text")?.Value, Is.EqualTo("按键速度"));
        });
    }

    /// <summary>
    /// 直方图缩进 **84px** 挂在那一行底下（74 的标签列 + 10 的列距 = 值那一栏的左端）。
    /// 那一格是这一块里的第二样东西，里面只有柱子那一格和横轴那行 —— 不另起一行、不单独成块。
    /// </summary>
    [Test]
    public void 直方图缩进八十四挂在那一行底下()
    {
        var 子 = 控件(窗(), "KeyRate").Elements().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(子, Has.Count.EqualTo(2), "这一块就是「一行读数 + 挂在它底下的直方图」两样");
            Assert.That(子[0].Name.LocalName, Is.EqualTo("Grid"), "第一样是读数那一行");
            Assert.That(子[1].Name.LocalName, Is.EqualTo("StackPanel"));
            Assert.That(属性(子[1], "Margin"), Is.EqualTo("84,0,0,0"),
                "缩进 84px 才与上面那一行的「值」左端齐");

            var 缩进里的 = 子[1].Elements().ToList();
            Assert.That(缩进里的, Has.Count.EqualTo(2), "柱子 + 横轴，两样，没有第三样");
            Assert.That(名字(缩进里的[0]), Is.EqualTo("KeyRateBars"), "先是柱子那一格");
            Assert.That(缩进里的[1].Name.LocalName, Is.EqualTo("Grid"), "再是横轴那行");
            Assert.That(缩进里的[1].Elements().Select(名字),
                Is.EqualTo(new[] { "KeyRateAxisStart", "KeyRateAxisNote", "KeyRateAxisEnd" }),
                "横轴那行就是三格字：左端 / 中间 / 右端");
        });
    }

    /// <summary>
    /// 柱子那一格：一行、等宽平分（一秒一格各占一样宽）、26px 高，**XAML 里一根柱子都不摆**
    /// —— 根数随曲子变，全由 <c>KeyRateView</c> 摆（摆了它会在构造函数里当场抛）。
    /// </summary>
    [Test]
    public void 柱子那格是空的等宽铺满的一行()
    {
        var 格 = 控件(窗(), "KeyRateBars");

        Assert.Multiple(() =>
        {
            Assert.That(格.Name.LocalName, Is.EqualTo("UniformGrid"), "等宽平分：一秒一格，谁也不多占");
            Assert.That(属性(格, "Rows"), Is.EqualTo("1"));
            Assert.That(属性(格, "Columns"), Is.Null, "列数由柱子数说了算，不写死");
            Assert.That(属性(格, "Height"), Is.EqualTo("26"), "与原型 .histo 同高");
            Assert.That(格.Elements(), Is.Empty, "XAML 里先摆一根柱子 = 多出来一根假的");
        });
    }

    /// <summary>
    /// 峰值那一根是 <c>TokenStop</c>（红）、其余 <c>TokenAccent</c>；
    /// 底色差**只用来标「哪一秒是峰值」这一件事** —— 柱子的高矮归 Height（brief §5s）。
    /// 次序：红的排在底色那条之后（同优先级谁后写谁赢）。
    /// </summary>
    [Test]
    public void 峰值那根红的其余主色()
    {
        var 根 = 窗();

        Assert.Multiple(() =>
        {
            Assert.That(样式(根, "Border.kbar")["Background"],
                Is.EqualTo("{DynamicResource TokenAccent}"), "其余的是主色");
            Assert.That(样式(根, "Border.kbar")["VerticalAlignment"], Is.EqualTo("Bottom"), "柱子一律贴底");

            Assert.That(样式(根, "Border.kbar.peak")["Background"],
                Is.EqualTo("{DynamicResource TokenStop}"), "峰值那一秒是红的");
            Assert.That(样式(根, "Border.kbar.peak").Keys, Has.None.EqualTo("Height"),
                "红的那一根不许在高度上另搞一套");

            var 次序 = 规则次序(根);
            Assert.That(次序.IndexOf("Border.kbar"), Is.LessThan(次序.IndexOf("Border.kbar.peak")),
                "红的排在底色那条后面才盖得住它");
        });
    }

    /// <summary>
    /// **峰值那个数比平均那个数明显重一档**：大一号 + 加粗 + warn 色，平均是陪衬。
    ///
    /// 重一档不许只靠颜色（同一块色在浅底 / 暗底上轻重会翻个个儿，brief §5s）——
    /// 所以字号与字重也各差一档，这两样到哪儿都是那个差。
    /// </summary>
    [Test]
    public void 峰值那个数比平均重一档()
    {
        var 根 = 窗();
        var 平均 = 样式(根, "TextBlock.kps");
        var 峰值 = 样式(根, "TextBlock.kps.peak");

        Assert.Multiple(() =>
        {
            Assert.That(double.Parse(峰值["FontSize"]), Is.GreaterThan(double.Parse(平均["FontSize"])),
                "峰值那个数要大一号");
            Assert.That(平均["FontWeight"], Is.EqualTo("Normal"));
            Assert.That(峰值["FontWeight"], Is.EqualTo("Bold"), "峰值那个数要重一档字重");
            Assert.That(峰值["Foreground"], Is.EqualTo("{DynamicResource TokenWarn}"));
            Assert.That(平均["Foreground"], Is.EqualTo("{DynamicResource TokenInkMuted}"), "平均是陪衬");
            Assert.That(平均["FontFamily"], Is.EqualTo("{DynamicResource TokenFontMono}"), "两个数都是等宽的");

            Assert.That(属性(控件(根, "KeyRatePeak"), "Classes"), Is.EqualTo("kps peak"));
            Assert.That(属性(控件(根, "KeyRateAverage"), "Classes"), Is.EqualTo("kps"),
                "平均那个数不许挂着 peak");
        });
    }

    /// <summary>
    /// 横轴那行字**不在这里写样例**：三个格子一开始都是空的，字全由 <c>KeyRateReadout</c> 写
    /// （连 <c>0:00</c> 也走同一个时钟格式）—— 写一份样例就多一个真相源。
    /// 三格一样淡，中间那句居在两端之间。
    /// </summary>
    [Test]
    public void 横轴那行字由代码写不在这里写样例()
    {
        var 根 = 窗();
        var 三格 = 控件(根, "KeyRateAxisStart").Parent!.Elements().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(三格.Select(e => 属性(e, "Text")), Is.All.EqualTo(""), "样例一个字都不许写");
            Assert.That(三格.Select(e => 属性(e, "Classes")), Is.All.EqualTo("kps-axis"), "三格一样淡");
            Assert.That(属性(控件(根, "KeyRateAxisNote"), "HorizontalAlignment"), Is.EqualTo("Center"),
                "中间那句居在两端之间");
            Assert.That(属性(控件(根, "KeyRateAxisEnd"), "HorizontalAlignment"), Is.Null,
                "右端那格贴着右边，不额外居中");
        });
    }

    /// <summary>
    /// 还没曲子时整块不出现：读数是**这首歌**的量，没歌就没得说（不写「0.0 键/秒」这种空话）。
    /// </summary>
    [Test]
    public void 还没曲子时整块不出现()
    {
        Assert.That(属性(控件(窗(), "KeyRate"), "IsVisible"), Is.EqualTo("False"));
    }

    // ==================== 源码：算一次摆在那儿，播放期间不动 ====================

    private static string 读(params string[] 段) => File.ReadAllText(Path.Combine(段.Prepend(仓库根).ToArray()));

    private static string 演奏器代码 => 读("MidiPerformer.App", "Views", "PerformerWindow.axaml.cs");

    private static string 用例代码 => 读("MidiPerformer.Core", "UseCases", "Perform", "StartPerformance.cs");

    private static string 链子代码 => 读("MidiPerformer.Core", "UseCases", "Perform", "EventTable.cs");

    private static string 读数代码 => 读("MidiPerformer.App", "Views", "KeyRateReadout.cs");

    /// <summary>
    /// **这一票的魂**：读数全部在播放之前算完 —— 装曲子、换一条轨、换一次时序各算一遍，
    /// 播放那条路上**一次都不算**（没有计时器、没有节流，演奏期间一个数都不动）。
    ///
    /// 用户的原话：「对于按键速度的测量，可以尽量在播放之前就计算出来吗？我不希望在播放的时候
    /// 临时看」。
    ///
    /// 74 号票把算的地方从三处加到**四处**：动一次微调也算一遍。理由是同一个「真相源」——
    /// 微调会进事件表（<c>StartPerformanceRequest.TransposeOffset</c> → <c>EventTable.Build</c>），
    /// 表变了读数不跟着变，屏幕上那两个数说的就是另一张表了。原型那句写的正是
    /// 「这两个数在**选好轨 / 动微调**的时候就算出来」，所以第四处不是多出来的。
    /// 「播放那条路上一次都不算」这一半一个字都没动（下面那四条还在）。
    /// </summary>
    [Test]
    public void 读数在播放之前算完播放期间一下都不算()
    {
        var 原文 = 演奏器代码;
        string 构造函数 = 只读代码(一段(原文, "public PerformerWindow(IClock clock, InputSender sender)",
            "protected override void OnOpened"));

        Assert.Multiple(() =>
        {
            Assert.That(数一数(只读代码(原文), "_ = RefreshKeyRate();"), Is.EqualTo(4),
                "算的地方该有四处：装曲子 / 换一条轨 / 换一次时序 / 动一次微调，一个不多");
            Assert.That(数一数(构造函数, "_ = RefreshKeyRate();"), Is.EqualTo(2),
                "构造期挂的两处：换时序、换一条轨");
            Assert.That(构造函数, Does.Contain("new KeyRateView("), "控件在这儿认领");
            Assert.That(一段(原文, "private void ApplySong(", "private static string Describe"),
                Does.Contain("_ = RefreshKeyRate();"), "装上曲子就算出来 —— 载入就看得见，不用先按播放");

            string 算的那一处 = 只读代码(一段(原文, "private async Task RefreshKeyRate()",
                "private StartPerformanceRequest? CurrentRequest()"));
            Assert.That(算的那一处, Does.Contain("await Task.Run("), "建表不占界面线程");
            Assert.That(算的那一处, Does.Contain("EventTable.Build("), "表从链子那一个方法拿");

            // 播放那条路：一个数都不重算（没有计时器、没有节流）
            foreach (var (起, 止) in new[]
                     {
                         ("private void OnStart(", "private void OnStop("),
                         ("private void OnProgressTick(", "private PerformerOverlayWindow ShowOverlay("),
                         ("private void OnPerformanceFinished(", "private void OnProgressTick("),
                         ("private void OnNoteSent(", "private void SetStatus("),
                     })
            {
                Assert.That(只读代码(一段(原文, 起, 止)), Does.Not.Contain("RefreshKeyRate"),
                    $"{起} 是播放那条路：读数不许在这儿重算");
                Assert.That(只读代码(一段(原文, 起, 止)), Does.Not.Contain("EventTable.Build"),
                    $"{起} 是播放那条路：表也不许在这儿重建");
            }
        });
    }

    /// <summary>
    /// **同一个真相源**：链子（选中轨 → 秒 → 键位 → 事件表）只有一处 —— <c>EventTable.Build</c>，
    /// 演奏与读数都从那儿拿表。另写一遍就是「同一件事有第二个写它的地方」，
    /// 屏幕上那个「最密的一秒」迟早和耳朵里真挨的那一秒对不上。
    /// </summary>
    [Test]
    public void 链子只有一处两个用的人共用它()
    {
        var 用例 = 只读代码(用例代码);
        var 链 = 只读代码(链子代码);
        var 读数 = 只读代码(读数代码);

        Assert.Multiple(() =>
        {
            // 四步都在链子那一个文件里
            Assert.That(链, Does.Contain("RepertoireToSeconds.Convert(track.Notes, song.TempoMap)"));
            Assert.That(链, Does.Contain("NoteMapper.Map("));
            Assert.That(链, Does.Contain("new EventBuilder { Timing = timing }"));
            Assert.That(链, Does.Contain("new SongWalker(song)"));

            // 演奏那一路不再自己走一遍，它调的是同一个方法
            //（找的是**声明**那一处：「BuildEventTable(」在 Run 里还有一次调用）
            Assert.That(一段(用例代码, "static (List<EventBuilder.PhysicalEvent> Events, SongWalker Walker) BuildEventTable(", "}"),
                Does.Contain("EventTable.Build("), "演奏走的是链子那一个方法");
            foreach (string 一步 in new[] { "RepertoireToSeconds", "NoteMapper", "new EventBuilder" })
                Assert.That(用例, Does.Not.Contain(一步), $"{一步} 在演奏那一路又写了一遍");

            // 读数那一路也只认事件表：不碰音符、不碰曲子
            foreach (string 别的来源 in new[] { "RepertoireToSeconds", "NoteMapper", "Song" })
                Assert.That(读数, Does.Not.Contain(别的来源), $"读数那一半不许碰 {别的来源} —— 它只认事件表");
        });
    }

    // ==================== 源码的两个取材函数 ====================

    /// <summary>取两处标记之间的原文（两头的标记都得在，第二个在第一个之后）。</summary>
    private static string 一段(string source, string 起, string 止)
    {
        int a = source.IndexOf(起, StringComparison.Ordinal);
        Assert.That(a, Is.GreaterThanOrEqualTo(0), $"源码里找不到「{起}」");

        int b = source.IndexOf(止, a + 起.Length, StringComparison.Ordinal);
        Assert.That(b, Is.GreaterThan(a), $"「{起}」之后找不到「{止}」");

        return source[a..b];
    }

    /// <summary>
    /// 摘掉 <c>//</c>、<c>///</c>、<c>/* */</c> 注释，只留代码：注释里提一句
    /// <c>EventTable.Build</c> 也能把断言喂饱，而断言要看的自始至终是代码。
    /// </summary>
    private static string 只读代码(string source)
    {
        var kept = new System.Text.StringBuilder(source.Length);
        bool 在串里 = false, 在字符里 = false, 行注释 = false, 块注释 = false;

        for (int i = 0; i < source.Length; i++)
        {
            char 本 = source[i];
            char 下 = i + 1 < source.Length ? source[i + 1] : '\0';

            if (行注释)
            {
                if (本 != '\n') continue;
                行注释 = false;
            }
            else if (块注释)
            {
                if (本 == '*' && 下 == '/') { 块注释 = false; i++; continue; }
                if (本 != '\n') continue;
            }
            else if (在串里 || 在字符里)
            {
                if (本 == '\\' && 下 != '\0') { kept.Append(本).Append(下); i++; continue; }
                if (本 == (在串里 ? '"' : '\'')) { 在串里 = false; 在字符里 = false; }
            }
            else if (本 == '"') 在串里 = true;
            else if (本 == '\'') 在字符里 = true;
            else if (本 == '/' && 下 == '/') { 行注释 = true; i++; continue; }
            else if (本 == '/' && 下 == '*') { 块注释 = true; i++; continue; }

            kept.Append(本);
        }

        return kept.ToString();
    }

    /// <summary>数一段代码里某串字出现了几次（**先去过注释**再用）。</summary>
    private static int 数一数(string source, string 找)
    {
        int 数 = 0;
        for (int i = source.IndexOf(找, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(找, i + 找.Length, StringComparison.Ordinal))
        {
            数++;
        }
        return 数;
    }
}
