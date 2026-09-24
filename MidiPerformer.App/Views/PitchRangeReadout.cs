using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.App.Views;

/// <summary>
/// 音域读数：游戏里那 38 个半音排成一根长条，一根细条一个半音。
///
/// 这个文件是**纯数据**那一半：窗口怎么切、这首歌压在窗口的哪几格、读数行上写什么字，
/// 全是这里的几个静态函数算出来的，一份 Avalonia 都不用（<see cref="PitchRangeView"/> 才碰控件）。
/// 这么切是因为那一半**不测像素也说得清对错**：38 格逐格对不对、越界几个音、亮的几根。
///
/// 窗口的算法来自 <see cref="NoteMapper"/>，不是估的：可演奏区是 `[12B, 12B+37]` ——
/// B−1 八度全十二个音 + B 八度全十二个 + B+1 八度全十二个 + B+2 八度的 do 和 #do，
/// **38 个半音，中间一个洞都没有**。B=C4 时就是 C3 到 C#6。
/// 基准八度不在这里另算一遍：<see cref="NoteMapper.AutoBaseOctave"/> 就是执行时
/// <c>Map</c> 选的那一个，两个真相源迟早会对不上。
/// </summary>
public static class PitchRangeReadout
{
    /// <summary>条子里一共几根细条。</summary>
    public const int Bars = 38;

    /// <summary>
    /// 四段各几根：低八度 12 + 基准八度 12 + 高八度 12 + 高高音的 do/#do 2。
    /// 四段的宽度比就是这几个数（12:12:12:2）—— 靠布局算法保证，不靠眼睛。
    /// </summary>
    public static readonly int[] SegmentLengths = { 12, 12, 12, 2 };

    /// <summary>读数行分几段写（槽的位置是契约，见 <see cref="SummarySlots"/>）。</summary>
    public const int SummarySlotCount = 10;

    /// <summary>升号音（黑键）的音级。这几根**只降不透明度**，不换底色、不描边。</summary>
    private static readonly int[] SharpPitchClasses = { 1, 3, 6, 8, 10 };

    /// <summary>`C#5(...) → 按[…]` 这种音名开头的串，取它前面那个音名。</summary>
    private static readonly Regex NoteNamePattern = new(@"^([A-G])(#?)(-?\d+)\(", RegexOptions.Compiled);

    // ==================== 窗口 ====================

    /// <summary>窗口最低那个音（八度是 <c>pitch / 12 - 1</c>，所以基准八度 B 的最低音就是 12B）。</summary>
    public static int WindowLow(int baseOctave) => baseOctave * 12;

    /// <summary>窗口里那 38 个半音，从低到高，中间一个洞都没有。</summary>
    public static int[] Window(int baseOctave)
        => Enumerable.Range(WindowLow(baseOctave), Bars).ToArray();

    /// <summary>四段各自的**最低那个音**（段头写的音名就是它）。</summary>
    public static int[] SegmentTops(int baseOctave)
    {
        var tops = new int[SegmentLengths.Length];
        int at = WindowLow(baseOctave);
        for (int i = 0; i < tops.Length; i++)
        {
            tops[i] = at;
            at += SegmentLengths[i];
        }
        return tops;
    }

    /// <summary>这个音高在不在窗口里。</summary>
    public static bool InWindow(int pitch, int baseOctave)
        => pitch >= WindowLow(baseOctave) && pitch < WindowLow(baseOctave) + Bars;

    /// <summary>这个音高落在第几根细条上（0 起）。不在窗口里给 <c>null</c>。</summary>
    public static int? BarOf(int pitch, int baseOctave)
        => InWindow(pitch, baseOctave) ? pitch - WindowLow(baseOctave) : null;

    /// <summary>升号音（黑键）。这一档只影响不透明度。</summary>
    public static bool IsSharp(int pitch) => SharpPitchClasses.Contains(Music.Mod(pitch, 12));

    /// <summary>音名。和执行时事件表上那串字是同一个来源（<see cref="Music.NoteName"/>）。</summary>
    public static string NoteName(int pitch) => Music.NoteName(pitch);

    // ==================== 一帧读数 ====================

    /// <summary>
    /// 把一条轨的音高（原谱，未平移）加上微调，量成一帧读数。
    ///
    /// 基准八度只看**平移后仍在 MIDI 音域内**的那些音 —— 和 <see cref="NoteMapper.Map"/>
    /// 喂给 <see cref="NoteMapper.AutoBaseOctave"/> 的是同一份。
    /// 越界的统计则按原型：每个音都算数，重复的音各算一个（「漏了多少个音符」才是用户关心的事），
    /// 而列出来的音名去重。
    /// </summary>
    public static PitchRangeState Measure(IReadOnlyList<int> pitches, int transpose)
    {
        var all = new List<int>(pitches.Count);
        var valid = new List<int>(pitches.Count);
        foreach (var p in pitches)
        {
            int shifted = p + transpose;
            all.Add(shifted);
            if (shifted is >= 0 and <= 127) valid.Add(shifted);
        }

        int baseOctave = NoteMapper.AutoBaseOctave(valid);

        var used = new SortedSet<int>();
        var outside = new SortedSet<int>();
        int outsideNotes = 0;
        foreach (var pitch in all)
        {
            if (InWindow(pitch, baseOctave)) used.Add(pitch);
            else
            {
                outside.Add(pitch);
                outsideNotes++;
            }
        }

        return new PitchRangeState(
            baseOctave, transpose, used.ToList(), outside.ToList(), outsideNotes, all.Count);
    }

    // ==================== 一帧里 38 根细条 ====================

    /// <summary>
    /// 一帧里 38 根细条各自的样子，从低到高、一根一个半音。**纯数据** ——
    /// <see cref="PitchRangeView"/> 只是照着它刷样式，所以「正在响的恰好一根、而且那根亮着」
    /// 这条要求不用靠眼睛看。
    ///
    /// 「正在响的那根一定亮着」是**数据**保证的：响的那个音是从这首歌的事件表里发出去的，
    /// 它一定在这首歌用到的音里。代码不替它补亮（补了的话，亮的那片就不再等于「这首歌用到的音」了，
    /// 而那块读数正是拿来量这个的）。测试拿真曲子逐个音钉住这条。
    ///
    /// 音高落在窗口外时一根都不套边（<see cref="BarOf"/> 给 null，没有那根条子可套）——
    /// 那种情况下读数行本来就已经换成红字那一声了。
    /// </summary>
    public static BarState[] BarsOf(PitchRangeState state, int? nowPitch)
    {
        var bars = new BarState[Bars];
        int low = state.WindowLow;
        var lit = new HashSet<int>(state.Used);

        for (int i = 0; i < Bars; i++)
        {
            int pitch = low + i;
            bars[i] = new BarState(pitch, IsSharp(pitch), lit.Contains(pitch), nowPitch == pitch);
        }
        return bars;
    }

    // ==================== 读数行 ====================

    /// <summary>
    /// 常态读数行切成的那几个槽，槽 i 对应窗口里第 i 个 <c>Run</c>。
    /// 空串 = 这一段不出现（空 <c>Run</c> 画出来什么都没有，所以条件句不需要另外藏控件）。
    ///
    /// 加粗的是 1 / 4 / 8 三个槽（用到的音数、平移了几个半音、「正在响的 X」）——
    /// 位置是**写死的**，理由在原型里：一句话里值得重一档的只有这三个数，
    /// 而槽和 <c>Run</c> 一一对应之后，界面那边不需要再判一次。
    /// </summary>
    public static string[] SummarySlots(PitchRangeState state, int? nowPitch)
    {
        var slots = new string[SummarySlotCount];

        slots[0] = "亮着的是这首歌用到的 ";
        slots[1] = state.Used.Count.ToString(CultureInfo.InvariantCulture);
        slots[2] = $" 个音（{NoteName(state.Used[0])} – {NoteName(state.Used[^1])}）";

        if (state.Transpose != 0)
        {
            slots[3] = "，整首已平移 ";
            slots[4] = state.Transpose > 0
                ? "+" + state.Transpose.ToString(CultureInfo.InvariantCulture)
                : state.Transpose.ToString(CultureInfo.InvariantCulture);
            slots[5] = " 个半音";
        }

        slots[6] = "，全在条子里面。";

        if (nowPitch is { } now && InWindow(now, state.BaseOctave))
        {
            slots[7] = " 套着细边的那根是";
            slots[8] = $"正在响的 {NoteName(now)}";
            slots[9] = "。";
        }

        return slots;
    }

    /// <summary>越界那一行的正行（居中，红字）。</summary>
    public const string AlertTitle = "当前已超出可演奏音域";

    /// <summary>
    /// 越界那一行的副行：越界的音名**去重升序**列出来（顿号分隔），后面跟音符个数与占比。
    /// 个数按音符算、占比按音符算 —— 与常态那行的「几个音」是两个不同的量（那儿是音高数）。
    /// </summary>
    public static string AlertSub(PitchRangeState state)
        => $"{string.Join("、", state.Outside.Select(NoteName))} 落到这 38 个音外面了 —— 共 "
         + $"{state.OutsideNotes.ToString(CultureInfo.InvariantCulture)} 个音符，占全曲 {state.OutsidePercent}%";

    // ==================== 正在响的那一根 ====================

    /// <summary>
    /// 事件表上那串音名（<c>NoteMapper.Describe</c> 产的，形如 `F4(4) → 按[v] 基准八度`）
    /// 里的音高。派发线程只把这串字递到界面（<c>NotifyingSink</c>），音高是它带上来的
    /// 唯一一样东西，所以只能从这儿取。
    ///
    /// 取不出来给 <c>null</c>（那就不画细边）——**不许猜一个音**：猜错就是把细边套在
    /// 另一个音上，而这个读数的全部意义就是「现在响的是哪一格」。
    /// </summary>
    public static int? PitchOfLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return null;

        var match = NoteNamePattern.Match(label);
        if (!match.Success) return null;

        int pc = match.Groups[1].Value[0] switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1
        };
        if (pc < 0) return null;
        if (match.Groups[2].Value.Length > 0) pc++;

        if (!int.TryParse(match.Groups[3].Value, NumberStyles.AllowLeadingSign,
                          CultureInfo.InvariantCulture, out int octave))
            return null;

        int pitch = 12 * (octave + 1) + pc;
        return pitch is >= 0 and <= 127 ? pitch : null;
    }
}

/// <summary>
/// 一帧读数：基准八度、平移量、窗口里用到的音（去重升序）、越界的音（去重升序），
/// 越界的音符个数与分母。
/// </summary>
public sealed record PitchRangeState(
    int BaseOctave,
    int Transpose,
    IReadOnlyList<int> Used,
    IReadOnlyList<int> Outside,
    int OutsideNotes,
    int TotalNotes)
{
    /// <summary>窗口最低那个音。</summary>
    public int WindowLow => BaseOctave * 12;

    /// <summary>有音掉到窗口外面了（亮的那片要一律变红，读数行换成红字那一组）。</summary>
    public bool OutOfRange => OutsideNotes > 0;

    /// <summary>越界占全曲的百分比，一位小数（原型里就是 <c>toFixed(1)</c>）。</summary>
    public string OutsidePercent => TotalNotes == 0
        ? "0.0"
        : (OutsideNotes * 100.0 / TotalNotes).ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// 一根细条这一帧的样子。状态**只有这三样**：暗 / 亮 / 套着细边，
/// 高度不在这三样里 —— 高度由样式那条 24px 统一给。
/// </summary>
/// <param name="Pitch">这根条子代表的音高。</param>
/// <param name="Sharp">升号音（黑键）：只降不透明度，不换底色、不描边。</param>
/// <param name="Lit">这首歌用到了这个音。</param>
/// <param name="Now">这一刻正在响的就是这个音（套一圈细边，**高度不变**）。</param>
public readonly record struct BarState(int Pitch, bool Sharp, bool Lit, bool Now);

/// <summary>
/// 把一帧读数画到界面上：38 根细条的状态、段头那四个音名、读数行 / 越界那一行。
///
/// **全部同高是硬要求**：高度只有 `.s38` 那一条样式给（24px），状态只换底色 / 不透明度 /
/// 描边，一根都不用高度分档 —— 一段里只要有一根比别人高，那一段整体会被撑起来
/// （嵌套布局默认 stretch，病根在这儿），四段就宽窄不一了。
/// 段容器一律显式 <c>VerticalAlignment="Bottom"</c> + <c>Height="24"</c>，细条也一律 `Bottom`。
/// </summary>
public sealed class PitchRangeView
{
    private readonly Grid _bar;
    private readonly Grid _keys;
    private readonly TextBlock _summary;
    private readonly StackPanel _alert;
    private readonly TextBlock _alertTitle;
    private readonly TextBlock _alertSub;

    /// <summary>四段的容器，按条的先后（12 / 12 / 12 / 2 根）。</summary>
    private readonly Panel[] _groups;

    /// <summary>读数行那 10 个槽，按写进去的顺序。</summary>
    private readonly Run[] _runs;

    /// <param name="bar">细条那一行：外层是 12*,8,12*,8,12*,8,2* 的格子，四段各占一格。</param>
    /// <param name="keys">段头音名那一行：格子定义和 <paramref name="bar"/> 一模一样，音名才落在各段左端。</param>
    public PitchRangeView(
        Grid bar, Grid keys, TextBlock summary, StackPanel alert, TextBlock alertTitle, TextBlock alertSub)
    {
        _bar = bar;
        _keys = keys;
        _summary = summary;
        _alert = alert;
        _alertTitle = alertTitle;
        _alertSub = alertSub;

        _groups = bar.Children.OfType<Panel>().ToArray();
        _runs = summary.Inlines?.OfType<Run>().ToArray() ?? Array.Empty<Run>();

        // 这四条是**契约**，不是可达状态：格子被改过、少写一根细条、读数行的槽少一个，
        // 画出来只是「看着有点不对」而不会报错，所以在这里当场抛。
        if (_groups.Length != PitchRangeReadout.SegmentLengths.Length)
            throw new InvalidOperationException(
                $"音域读数：细条那一行该有 {PitchRangeReadout.SegmentLengths.Length} 段，实到 {_groups.Length} 段");

        for (int i = 0; i < _groups.Length; i++)
        {
            if (_groups[i].Children.Count != PitchRangeReadout.SegmentLengths[i])
                throw new InvalidOperationException(
                    $"音域读数：第 {i + 1} 段该有 {PitchRangeReadout.SegmentLengths[i]} 根细条，实到 {_groups[i].Children.Count} 根");
        }

        if (_keys.Children.Count != PitchRangeReadout.SegmentLengths.Length)
            throw new InvalidOperationException(
                $"音域读数：段头那行该有 {PitchRangeReadout.SegmentLengths.Length} 个音名格，实到 {_keys.Children.Count} 个");

        if (_runs.Length != PitchRangeReadout.SummarySlotCount)
            throw new InvalidOperationException(
                $"音域读数：读数行该有 {PitchRangeReadout.SummarySlotCount} 个槽，实到 {_runs.Length} 个");
    }

    /// <summary>
    /// 画一帧。<paramref name="pitches"/> 是这条轨的**原谱**音高，<paramref name="transpose"/>
    /// 是它的整轨移调加上微调；<paramref name="nowPitch"/> 是这一刻正在响的那个音（没有就给 null）。
    /// 没有曲子時传 <c>null</c>：细条全暗、读数行整行不出现 —— 读数是**这首歌**的量，没歌就没得说。
    /// </summary>
    public void Show(IReadOnlyList<int>? pitches, int transpose, int? nowPitch)
    {
        var state = PitchRangeReadout.Measure(pitches ?? Array.Empty<int>(), transpose);

        // 一根一根照着这一帧的样子刷。顺序**就是**条的先后（四段依次、每段从左到右），
        // 所以这里的下标必须一格不错 —— 错了就是「亮的那片整体挪了一根」，看着还挺像回事。
        var bars = PitchRangeReadout.BarsOf(state, nowPitch);
        int index = 0;
        foreach (var group in _groups)
        {
            foreach (var child in group.Children)
            {
                if (child is not Border bar || index >= bars.Length) continue;

                var state38 = bars[index++];
                bar.Classes.Set("sharp", state38.Sharp);
                bar.Classes.Set("lit", state38.Lit);
                bar.Classes.Set("now", state38.Now);
            }
        }

        // 越界那一个类是给**整条**打的：亮着的一律换红（`.bad` 在后代选择器上，见窗口的样式）
        _bar.Classes.Set("bad", state.OutOfRange);

        var tops = PitchRangeReadout.SegmentTops(state.BaseOctave);
        int label = 0;
        foreach (var child in _keys.Children)
        {
            // 段头只写那个音名（C3 / C4 / C5 / C6），手位不写
            if (child is TextBlock text && label < tops.Length)
                text.Text = PitchRangeReadout.NoteName(tops[label++]);
        }

        // 常态读数行与越界那一声是互斥的（原型里就是一个 if/else）：越界时换掉，不是并列
        if (state.OutOfRange)
        {
            _summary.IsVisible = false;
            _alertTitle.Text = PitchRangeReadout.AlertTitle;
            _alertSub.Text = PitchRangeReadout.AlertSub(state);
            _alert.IsVisible = true;
            return;
        }

        _alert.IsVisible = false;

        // 一个用到的音都没有（空轨 / 还没曲子）：读数行整行不出现，别写「0 个音」这种空话
        if (state.Used.Count == 0)
        {
            _summary.IsVisible = false;
            return;
        }

        var slots = PitchRangeReadout.SummarySlots(state, nowPitch);
        for (int i = 0; i < _runs.Length; i++) _runs[i].Text = slots[i];
        _summary.IsVisible = true;
    }
}
