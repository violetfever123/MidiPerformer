using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.App.Views;

/// <summary>
/// 按键速度读数：这首**弹得动吗** —— 一个平均、一个峰值、一张每秒直方图。
///
/// 这个文件是**纯数据**那一半（<see cref="KeyRateView"/> 才碰控件），和 49 号的
/// <see cref="PitchRangeReadout"/> 是同一套切法：三个数怎么算、哪一秒是峰值、柱子多高，
/// 全在下面这几个静态函数里，一份 Avalonia 都不用。这么切是因为那一半**不测像素也说得清对错**。
///
/// **全部在播放之前算完**（用户 2026-09-20 的原话：「对于按键速度的测量，可以尽量在播放之前
/// 就计算出来吗？我不希望在播放的时候临时看」）—— 这一块载入就看得见，演奏期间一个数都不动。
///
/// **三个数的来源只有一个**：<c>EventBuilder.Build</c> 那张事件表 —— 也就是演奏时真按下去的那一张
/// （见 <c>Core/UseCases/Perform/EventTable.Build</c>）。所以这里不碰 <c>Song</c>、不碰音符：
/// 另走一遍轨数据就成了「同一件事有第二个写它的地方」，两份迟早对不上。
///
/// 判据是「与事件表对拍」：**按了一下** = 事件表里音键**按下**那一下（一个音符正好一下；
/// 口琴那套键位里，升号音多出来的那下鼠标键不算 —— 这一栏答的是「一秒弹几个音」）。
/// </summary>
public static class KeyRateReadout
{
    /// <summary>
    /// 把一张事件表量成一块读数。
    ///
    /// 平均 = 按键数 ÷ 时长，峰值 = 每秒桶里的最大值，桶 = 一秒一个。
    /// </summary>
    /// <param name="events">事件表（<c>EventTable.Build</c> 的产物，按时刻升序）。</param>
    /// <param name="seconds">这张表铺了多久 —— <c>Build</c> 一起吐出来的那个数（最后一个事件的时刻）。</param>
    public static KeyRateState Measure(IReadOnlyList<EventBuilder.PhysicalEvent> events, double seconds)
    {
        int keys = 0;
        foreach (var e in events)
            if (e.Kind == EventBuilder.K_Key && e.Down) keys++;

        // 时长取闭区间那一头：`!(seconds > 0)` 一并挡住 0 / 负数 / NaN，
        // 后面所有除法都在这个门后面（空轨、单音、只有一瞬间的音都从这儿过）。
        double 时长 = seconds > 0 ? seconds : 0;
        if (keys == 0 || 时长 <= 0)
            return new KeyRateState(keys, 时长, Array.Empty<int>(), -1);

        // 一秒一个桶，从 0:00 铺到这张表的末尾：桶数 = 这张表铺了几个整秒（向上取整）。
        // 横轴是一格一秒画出来的，最后一格整格都在 —— 右端那个刻度就是格数（见 KeyRateReadout.AxisEnd）。
        int 桶数 = (int)Math.Ceiling(时长);
        var buckets = new int[桶数];
        foreach (var e in events)
        {
            if (e.Kind != EventBuilder.K_Key || !e.Down) continue;
            int i = (int)e.T;
            buckets[i < 0 ? 0 : i >= 桶数 ? 桶数 - 1 : i]++;
        }

        // 峰值那一秒：最大值所在的下标。**并列时取最左那一秒**（原型里是 indexOf(max)，
        // 第一个最大值的下标）—— 用严格大于去换，先出现的那一秒留任。
        int 峰值桶 = 0;
        for (int i = 1; i < 桶数; i++)
            if (buckets[i] > buckets[峰值桶]) 峰值桶 = i;

        return new KeyRateState(keys, 时长, buckets, 峰值桶);
    }

    /// <summary>横轴中间那句话（规格定死的，界面不另写一份）。</summary>
    public const string AxisNote = "整曲每秒按键数 · 红的那一秒是峰值";

    /// <summary>直方图那一行的高度（px）—— 与原型 <c>.histo{height:26px}</c> 同高。</summary>
    public const double HistogramHeight = 26;

    /// <summary>最矮的一根（px）：那秒一下都没有也留一条底 —— 与原型 <c>.histo i{min-height:2px}</c> 同一个值。</summary>
    public const double MinBarHeight = 2;

    /// <summary>平均那个数：一位小数（规格与原型里写的就是「3.0」）。</summary>
    public static string AverageText(KeyRateState state)
        => state.Average.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>峰值那个数：整数 —— 最密的那一秒里按了几下。</summary>
    public static string PeakText(KeyRateState state)
        => state.Peak.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 横轴左端。走的是界面里**同一个**时钟格式（<see cref="Format.Clock"/>），
    /// 不在 XAML 里写死一份「0:00」：两个真相源迟早会对不上（比如哪天时钟改成 0:00.0）。
    /// </summary>
    public static string AxisStartText() => Format.Clock(0);

    /// <summary>
    /// 横轴右端 = **这根轴铺到哪儿**：一格一秒，一共 <see cref="KeyRateState.Buckets"/> 格，
    /// 所以右端就是那个整秒数（勾指起誓是 154 格 → 2:34，与规格里的示例一致）。
    ///
    /// 不拿最后一个事件的时刻来写：那是 153.6 秒，截成「2:33」会比柱子短一截 ——
    /// 横轴画的是**桶**，右端就该落在最后一格的格沿上。
    /// </summary>
    public static string AxisEndText(KeyRateState state) => Format.Clock(state.Buckets.Count);

    /// <summary>
    /// 一根柱子的高度（px）：按峰值归一，峰值那一秒顶到 <see cref="HistogramHeight"/>，
    /// 一下都没有的那秒也留 <see cref="MinBarHeight"/>（原型里就是 max(4%, 2px) 这一手）。
    /// 没有峰值（没得画）时给 0。
    /// </summary>
    public static double BarHeight(int value, int peak)
    {
        if (peak <= 0) return 0;

        double h = Math.Round(HistogramHeight * value / peak);
        return h < MinBarHeight ? MinBarHeight : h;
    }
}

/// <summary>
/// 一块按键速度读数：按键数、时长（秒）、每秒一个桶、峰值在第几个桶（0 起；没得画时 -1）。
///
/// <see cref="Peak"/> 不另存一份 —— 它就是峰值那个桶的高度，免得「最大值」有两个写它的地方。
/// </summary>
/// <param name="Keys">按了几下（事件表里音键按下的个数 = 发出去几个音）。</param>
/// <param name="Seconds">这张表铺了多久（最后一个事件的时刻）。</param>
/// <param name="Buckets">一秒一个桶，桶 i = 第 i 秒到第 i+1 秒之间按了几下。</param>
/// <param name="PeakBucket">峰值在第几个桶（并列时取最左）；-1 = 没有桶。</param>
public sealed record KeyRateState(
    int Keys,
    double Seconds,
    IReadOnlyList<int> Buckets,
    int PeakBucket)
{
    /// <summary>这一块值不值得出现：一个音都没有、或者一个可画的整秒都没有，就不出现。</summary>
    public bool HasData => Keys > 0 && Buckets.Count > 0;

    /// <summary>平均（键/秒）：按键数 ÷ 时长。时长 ≤ 0 时给 0 —— 不许出现 NaN / Infinity。</summary>
    public double Average => Seconds > 0 ? Keys / Seconds : 0;

    /// <summary>峰值（键/秒）：每秒桶里的最大值。</summary>
    public int Peak => PeakBucket >= 0 ? Buckets[PeakBucket] : 0;
}

/// <summary>
/// 把一块按键速度读数画到界面上：两个数、一秒钟一根柱子（峰值那根红）、横轴那行字。
///
/// 柱子**由这儿摆**（根数随曲子变，XAML 里摆不出来）：一秒一根、等宽铺满、底对齐，
/// 高度由 <see cref="KeyRateReadout.BarHeight"/> 算 —— 颜色只用来标「哪一秒是峰值」这一件事，
/// 高矮归 Height（brief §5s：颜色当不了几何判据）。
/// </summary>
public sealed class KeyRateView
{
    private readonly StackPanel _block;
    private readonly TextBlock _average;
    private readonly TextBlock _peak;
    private readonly UniformGrid _bars;
    private readonly TextBlock _axisStart;
    private readonly TextBlock _axisNote;
    private readonly TextBlock _axisEnd;

    /// <param name="block">整块（一行读数 + 挂在它底下的直方图），没得画时整块不出现。</param>
    /// <param name="average">平均那个数。</param>
    /// <param name="peak">峰值那个数（比平均重一档，见窗口样式）。</param>
    /// <param name="bars">柱子那格：一秒一根，由这儿往里摆。</param>
    /// <param name="axisStart">横轴左端（0:00）。</param>
    /// <param name="axisNote">横轴中间那句话。</param>
    /// <param name="axisEnd">横轴右端（总时长）。</param>
    public KeyRateView(
        StackPanel block, TextBlock average, TextBlock peak, UniformGrid bars,
        TextBlock axisStart, TextBlock axisNote, TextBlock axisEnd)
    {
        _block = block;
        _average = average;
        _peak = peak;
        _bars = bars;
        _axisStart = axisStart;
        _axisNote = axisNote;
        _axisEnd = axisEnd;

        // 这两条是**契约**，不是可达状态：柱子和两个数写错了地方，画出来只是「看着有点不对」，
        // 不会报错，所以在这里当场抛。
        if (ReferenceEquals(average, peak))
            throw new InvalidOperationException("按键速度：平均和峰值是同一个控件，两个数会互相盖掉");

        if (bars.Children.Count != 0)
            throw new InvalidOperationException(
                $"按键速度：柱子那格该由这一块全权摆，XAML 里先摆了 {bars.Children.Count} 根");
    }

    /// <summary>
    /// 画一块读数。传 <c>null</c>（或一份没有数据的读数）时**整块不出现**：
    /// 三个数都是**这首歌**的量，没歌就没得说，不写「0.0 键/秒」这种空话。
    /// </summary>
    public void Show(KeyRateState? state)
    {
        if (state is not { } s || !s.HasData)
        {
            _block.IsVisible = false;
            _bars.Children.Clear();
            return;
        }

        _average.Text = KeyRateReadout.AverageText(s);
        _peak.Text = KeyRateReadout.PeakText(s);
        _axisStart.Text = KeyRateReadout.AxisStartText();
        _axisNote.Text = KeyRateReadout.AxisNote;
        _axisEnd.Text = KeyRateReadout.AxisEndText(s);

        _bars.Children.Clear();
        for (int i = 0; i < s.Buckets.Count; i++)
        {
            var bar = new Border
            {
                Classes = { "kbar" },
                Height = KeyRateReadout.BarHeight(s.Buckets[i], s.Peak),
            };
            if (i == s.PeakBucket) bar.Classes.Add("peak");
            _bars.Children.Add(bar);
        }

        _block.IsVisible = true;
    }
}
