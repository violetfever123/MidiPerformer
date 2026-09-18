using MidiPerformer.Core.Model;

namespace MidiPerformer.Adapters;

/// <summary>
/// 卷帘的坐标换算 —— <b>S4 缝</b>（见 spec「Testing Decisions」）。
///
/// **纯函数，一个字段都没有。** 不碰 Avalonia、不碰 Win32、不认识控件 ——
/// 卷帘编辑器最容易出的 bug 全在这几个乘法里，而只要它是个纯函数，就能拿假视口一遍遍扫。
/// 反过来，只要往里塞一个 <c>Control</c>，这一整块就再也测不了了。
///
/// 两个方向互为逆运算：
/// <list type="bullet">
/// <item><b>Presenter 方向</b>（tick → 像素）：<see cref="XAtTick"/> / <see cref="YAtPitch"/>。</item>
/// <item><b>Controller 方向</b>（像素 → tick / 音高）：<see cref="TickAtX"/> / <see cref="PitchAtY"/>。</item>
/// </list>
/// 放一起测一次往返恒等，两边都覆盖到。
///
/// 「固定 4 小节」这条硬要求就落在 <see cref="BarsVisible"/> 上：它是常量，不是参数 ——
/// 界面上没有缩放入口，窗口变宽是每小节变宽，不是显示更多小节。
/// </summary>
public static class PianoRollGeometry
{
    /// <summary>一屏恒定显示多少小节。**没有缩放级别**，见 spec 的「Out of Scope」。</summary>
    public const int BarsVisible = 4;

    /// <summary>标尺（画小节号那一条）的高度，照 wireframe 的 <c>RULER_H</c>。</summary>
    public const double RulerHeight = 18;

    /// <summary>音符块之间让出的横向缝隙（像素），照 <c>RollPreviewStrip</c> 的 <c>dur * w - 1</c>。</summary>
    public const double NoteGap = 1;

    /// <summary>音符块上下各让出的高度（像素），照 <c>RollPreviewStrip</c> 的 <c>±0.8</c>。</summary>
    public const double NotePad = 0.8;

    /// <summary>音符窄到什么程度就不画了：再窄也是一根能看见的线。</summary>
    public const double MinNoteWidth = 2;

    /// <summary>命中判定里「头 / 尾」的宽度（像素）。</summary>
    public const double EdgeHitPixels = 4;

    /// <summary>
    /// 指针离按下点挪够多少像素才算「在拖」（像素）。
    ///
    /// **这是「点一下」和「拖一下」之间唯一的分界线**，别处一个都没有。
    ///
    /// 没有它的时候，按下期间任何一个 <c>PointerMoved</c> 都算一次真编辑，而位移是
    /// **相对音符自己的起点**吸出来的（<c>SnapToGrid(_anchorStart + moved) - _anchorStart</c>）——
    /// 一个本来就落在格线之间的音（抢拍），哪怕 <c>moved == 0</c> 也能算出非零位移：
    /// 手没动，音符自己跳了半格，撤销栈上还多一格。空白处那一下更凶：抖动让框选区间
    /// 从零长度变成几个 tick，而「这段区间里的所有音」是不分音高的，删掉的是别行上的音。
    ///
    /// 4px 是照 <see cref="EdgeHitPixels"/> 定的，和「头 / 尾」命中带一样宽：
    /// 再小的话触控板上手一抖（约 2px）就触发，再大就开始吃掉真的微调。
    /// </summary>
    public const double DragThresholdPixels = 4;

    /// <summary>
    /// 指针从按下点挪开了没有 —— 够 <see cref="DragThresholdPixels"/> 才算数。
    ///
    /// 纯函数放在这儿，是为了让「多少像素算拖」这条能在 S4 缝上单测，
    /// 不必起窗口造指针事件（那片地没有 UI 自动化，见工单 09）。
    ///
    /// 取平方比、不开根号：这条每个指针事件都跑，而且要的只是「够不够」，
    /// 不是真长度。坐标里有 NaN 时比较为假 —— 那一下当「没在拖」，
    /// 是这里唯一安全的答案（宁可少改一次，不可乱改一次）。
    /// </summary>
    /// <param name="dx">横坐标相对按下点的位移（像素）。</param>
    /// <param name="dy">纵坐标相对按下点的位移（像素）。</param>
    public static bool ExceedsDragThreshold(double dx, double dy)
        => dx * dx + dy * dy >= DragThresholdPixels * DragThresholdPixels;

    /// <summary>音域自适应的最小行数：单音轨也要看得见一行，而且不能一行撑满整条轨。</summary>
    private const int MinPitchRows = 6;

    /// <summary>音域两端留的余量比例 —— 别顶到边。</summary>
    private const double PitchPaddingFraction = 0.15;

    /// <summary>命中部位。</summary>
    public enum RollHit
    {
        /// <summary>没命中。</summary>
        None = 0,

        /// <summary>音符头部（左边缘那条窄带）。</summary>
        Head,

        /// <summary>音符尾部（右边缘那条窄带）。</summary>
        Tail,

        /// <summary>音符身体。</summary>
        Body
    }

    /// <summary>
    /// 一屏的视口：做一次换算要知道的全部东西。
    ///
    /// 做成值类型是**故意的**：它每帧都要按控件尺寸重建，堆上一堆短命对象不值当。
    /// </summary>
    /// <param name="Width">卷帘的像素宽。</param>
    /// <param name="Height">卷帘的像素高（含标尺那一条）。</param>
    /// <param name="ViewStartTick">屏幕左边缘对应的 tick。</param>
    /// <param name="TicksPerBar">一个小节多少 tick。</param>
    /// <param name="LowPitch">该轨自适应后的最低显示音高。</param>
    /// <param name="HighPitch">该轨自适应后的最高显示音高。</param>
    public readonly record struct Viewport(
        double Width,
        double Height,
        long ViewStartTick,
        long TicksPerBar,
        int LowPitch,
        int HighPitch)
    {
        /// <summary>一屏跨多少 tick。固定 4 小节。</summary>
        public double TicksVisible => (double)Math.Max(1, TicksPerBar) * BarsVisible;

        /// <summary>一个 tick 占几个像素。窗口变宽它就变大 —— 这是「没有缩放」的另一种说法。</summary>
        public double TickWidth => Width / TicksVisible;

        /// <summary>标尺以下的绘图区高度。</summary>
        public double PlotHeight => Math.Max(0, Height - RulerHeight);

        /// <summary>音域占多少行（含两端余量）。</summary>
        public int PitchRows => Math.Max(1, HighPitch - LowPitch + 1);

        /// <summary>一行音高占多高。**每条轨行高恒定**，音域窄的轨是把行画高，不是把轨变矮。</summary>
        public double RowHeight => PlotHeight / PitchRows;
    }

    // ==================== tick ⇄ 像素 ====================

    /// <summary>tick → 横坐标（Presenter 方向）。</summary>
    public static double XAtTick(in Viewport viewport, double tick)
        => (tick - viewport.ViewStartTick) * viewport.TickWidth;

    /// <summary>
    /// 横坐标 → tick（Controller 方向，<see cref="XAtTick"/> 的逆运算）。
    ///
    /// 卷帘左边缘以左一律夹到 0：负 tick 在谱面上不存在，放它出去会一路传进模型。
    /// 右边缘**不夹** —— 曲子末尾之后是空谱面，落在那里就是「没有音符」，不必编一个位置出来。
    /// </summary>
    public static double TickAtX(in Viewport viewport, double x)
    {
        if (!(viewport.TickWidth > 0)) return viewport.ViewStartTick;
        return Math.Max(0, viewport.ViewStartTick + x / viewport.TickWidth);
    }

    /// <summary>音高 → 该行顶边的纵坐标（Presenter 方向）。**高音在上**：最高音那行贴着标尺。</summary>
    public static double YAtPitch(in Viewport viewport, int pitch)
        => RulerHeight + (viewport.HighPitch - pitch) * viewport.RowHeight;

    /// <summary>
    /// 纵坐标 → 音高（Controller 方向）。
    /// 上下边缘之外夹到视口音域的两端，不越界 —— 鼠标滑出卷帘不该报出一个这条轨根本没有的音。
    /// </summary>
    public static int PitchAtY(in Viewport viewport, double y)
    {
        if (!(viewport.RowHeight > 0)) return viewport.LowPitch;
        double row = Math.Floor((y - RulerHeight) / viewport.RowHeight);
        // 写成 `!(row > 0)` 而不是 `row <= 0`：NaN 两种情况都判 false，会一路走到
        // `(int)NaN` 上 —— 那是个未定义值（x64 上实测是 int.MinValue），
        // 减出来是个荒唐的音高，还会顺着命中判定传到读数条上。
        if (!(row > 0)) return viewport.HighPitch;
        if (row >= viewport.PitchRows) return viewport.LowPitch;
        return viewport.HighPitch - (int)row;
    }

    // ==================== 音符块 ====================

    /// <summary>卷帘上的一个音符块。**画出来的那一块**就是它 —— 命中判定直接拿它比，所见即所点。</summary>
    /// <param name="Index">在所属轨的音符数组里的下标。命中之后要用它回到模型。</param>
    /// <param name="StartTick">起始 tick。</param>
    /// <param name="LengthTicks">时值（tick）。</param>
    /// <param name="Pitch">音高（**移调之后**的，也就是听到的那个）。</param>
    /// <param name="InRange">是否在口琴可演奏范围内。false → 标灰。</param>
    /// <param name="X">左边缘（像素）。</param>
    /// <param name="Y">顶边（像素）。</param>
    /// <param name="Width">宽度（像素）。</param>
    /// <param name="Height">高度（像素）。</param>
    public readonly record struct NoteBox(
        int Index,
        long StartTick,
        long LengthTicks,
        int Pitch,
        bool InRange,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;
    }

    /// <summary>
    /// 算出某个音符画出来的那一块。
    ///
    /// 横向减 1px、上下各让 0.8px，和 <c>RollPreviewStrip</c> 的模子一致：
    /// 不留这点缝，相邻的两个音看着就是一整块，分不出是几个音。
    /// </summary>
    public static NoteBox BoxOf(
        in Viewport viewport, int index, long startTick, long lengthTicks, int pitch, bool inRange)
    {
        double x = XAtTick(viewport, startTick);
        double right = XAtTick(viewport, startTick + lengthTicks);
        double width = Math.Max(MinNoteWidth, right - x - NoteGap);

        return new NoteBox(
            index, startTick, lengthTicks, pitch, inRange,
            x,
            YAtPitch(viewport, pitch) + NotePad,
            width,
            Math.Max(1, viewport.RowHeight - NotePad * 2));
    }

    /// <summary>
    /// 命中判定：这个点落在音符块的哪一段上。
    ///
    /// 头尾窄带的宽度**不能超过音符的三分之一** —— 否则一个很短的音上，
    /// 「身体」永远够不着，将来 09 的拖动会变成只能拉时值、拖不动位置。
    /// </summary>
    public static RollHit HitTest(in NoteBox box, double x, double y)
    {
        if (x < box.X || x >= box.Right || y < box.Y || y >= box.Bottom) return RollHit.None;

        double edge = Math.Min(EdgeHitPixels, box.Width / 3);
        if (x <= box.X + edge) return RollHit.Head;
        if (x >= box.Right - edge) return RollHit.Tail;
        return RollHit.Body;
    }

    // ==================== 音域自适应 ====================

    /// <summary>
    /// 一条轨实际用到的音高 → 卷帘的显示音域（含余量）。
    ///
    /// 只显示这条轨**真正用到的**音，再加一点余量：一个只用 8 个音的贝斯轨不该白占 3 个八度的高度
    /// （wireframe 标注 5）。太窄的音域（含单音轨）撑到 <see cref="MinPitchRows"/> 行，
    /// 免得一个音占满整条轨；撑完仍然夹在 0..127 里，极宽音域也不会越界。
    /// </summary>
    public static (int Low, int High) FitPitchRange(int minPitch, int maxPitch)
    {
        // 脏音高先夹回 MIDI 音域：语料里什么都有，别让一个越界的数把整条轨的高度算飞
        minPitch = Math.Clamp(minPitch, 0, 127);
        maxPitch = Math.Clamp(maxPitch, 0, 127);
        if (maxPitch < minPitch) (minPitch, maxPitch) = (maxPitch, minPitch);

        int pad = Math.Max(1, (int)Math.Round((maxPitch - minPitch) * PitchPaddingFraction));
        int low = minPitch - pad;
        int high = maxPitch + pad;

        int rows = high - low + 1;
        if (rows < MinPitchRows)
        {
            int extra = MinPitchRows - rows;
            low -= extra / 2;
            high += extra - extra / 2;
        }

        // 夹完跨度可能又不够了 —— 那是音域本来就贴着 MIDI 的边，认了，但不能越界
        low = Math.Clamp(low, 0, 127);
        high = Math.Clamp(high, 0, 127);
        if (high < low) high = low;

        return (low, high);
    }

    // ==================== 刻度：小节与网格 ====================

    /// <summary>
    /// 一个小节多少 tick —— 卷帘横向刻度的定义。
    ///
    /// 放这儿是因为「一小节多宽」和「一个 tick 多宽」是同一件事，两边各算一遍迟早会不一致。
    /// 拍号取**第一个**：中途变拍只影响后面小节线的位置，而这一张是只读的、不做分段刻度。
    ///
    /// 分段刻度曾经记在 08 名下，08 做完**没有做它，是特意不做的**：卷帘是 tick 轴，
    /// 一小节多宽只由分辨率和拍号决定，和速度表无关。哪天真要做分段，那是新的一条，
    /// 而且要先想清楚「改 BPM 卷帘不动」这条还成不成立 —— 08 把它当成了规矩钉死了。
    /// 没有拍号就按 4/4。
    /// </summary>
    public static long BarTicks(TempoMap tempoMap)
    {
        var (numerator, denominator) = tempoMap.TimeSignatureChanges.Count > 0
            ? (tempoMap.TimeSignatureChanges[0].Numerator, tempoMap.TimeSignatureChanges[0].Denominator)
            : (4, 4);

        int pulsesPerQuarter = Math.Max(1, tempoMap.Division.TicksPerQuarterNote);
        long ticks = (long)pulsesPerQuarter * Math.Max(1, numerator) * 4 / Math.Max(1, denominator);
        return Math.Max(1, ticks);
    }

    /// <summary>
    /// 网格 —— 拖动、微调之后落到哪条线上的最小刻度，**一个十六分音符**。
    ///
    /// 十六分是用户在样机上手选出来的，不是推出来的。格子定成整拍只能修「整段挪了一拍」那种错，
    /// 而**抢拍**（比整拍早/晚一个十六分）恰恰是这个编辑器最常要修的一类：格子比它粗，
    /// 这种音就吸不上任何一条线，只能靠手拖到大概齐 —— 那等于没有吸附。
    /// 代价是拖动精度要求变成一拍网格的 4 倍：一屏恒定 4 小节，一个小节占屏幕四分之一，
    /// 于是十六分音符在 800px 宽的卷帘上只有 12.5px，鼠标得抖得比这细才吸得准。
    /// 再细一档（三十二分）就开始「吸不动」了，比手抖还难受。
    ///
    /// **和拍号无关**：十六分音符本来就是四分音符的四分之一，4/4 和 6/8 里一样大。
    /// 所以这里只看分辨率，不看 <see cref="TempoMap.TimeSignatureChanges"/>，
    /// 也不经过 <see cref="BarTicks"/> —— 网格不是「小节的几分之一」。
    ///
    /// 下限 1 是给分辨率极低的曲子兜底：每四分音符 2 tick 的话 <c>/4</c> 算出 0，
    /// 而 <see cref="SnapToGrid"/> 把 0 格夹成 1（见那边的说明）—— 那就是「每个 tick 都是一条线」，
    /// 等于没有吸附：拖动会停在鼠标落到哪个 tick 就是哪个 tick，一格 0.1 像素地乱跑。
    /// </summary>
    public static long GridTicks(TempoMap tempoMap)
    {
        int pulsesPerQuarter = Math.Max(1, tempoMap.Division.TicksPerQuarterNote);
        return Math.Max(1, pulsesPerQuarter / 4);
    }

    /// <summary>整曲多少个小节，向上取整（空曲也算 1 小节，导航条上总得有个格子）。</summary>
    public static int BarCount(long totalTicks, long ticksPerBar)
        => (int)Math.Max(1, (totalTicks + Math.Max(1, ticksPerBar) - 1) / Math.Max(1, ticksPerBar));

    /// <summary>某个 tick 落在第几小节（0 起）。</summary>
    public static int BarAtTick(double tick, long ticksPerBar)
        => (int)Math.Max(0, Math.Floor(tick / Math.Max(1, ticksPerBar)));

    /// <summary>第几小节（0 起）的起始 tick。</summary>
    public static long TickOfBar(int bar, long ticksPerBar) => Math.Max(0, bar) * Math.Max(1, ticksPerBar);

    /// <summary>
    /// 吸附到**最近的格线**。<paramref name="gridTicks"/> 就是一格多少 tick ——
    /// 拖音符时给 <see cref="GridTicks"/>，拖导航条时给 <see cref="BarTicks"/>。
    ///
    /// 舍入取 <c>AwayFromZero</c>，不是 .NET 默认的「银行家舍入」：正好落在两格正中的 tick
    /// 是常事（半拍、半拍的半拍都是整数 tick），而银行家舍入按「末位是不是偶数」决定往哪边跳，
    /// 于是同一个位置往左拖和往右拖可能吸到不同的线上，用户看到的是「吸附有时不听话」。
    /// 往远处取至少是**可预期的**：正中就是往后（往大的方向）一格。
    ///
    /// 非有限数返回 0：<c>(long)NaN</c> 是个未定义值（x64 上实测 <c>long.MinValue</c>），
    /// 放它出去会一路传进模型。结果夹到 0 以上：负 tick 在谱面上不存在。
    ///
    /// <see cref="SnapToBar"/> 转发到这里，于是舍入方式、NaN、负数夹取这三件事只有一份出处 ——
    /// 各写一遍的话，日后改了其中一处，另一处会悄悄走偏，而那种偏差只有等到用户抱怨
    /// 「拖导航条吸得准、拖音符吸不准」的时候才会被发现。
    /// </summary>
    public static long SnapToGrid(double tick, long gridTicks)
    {
        // 0 或负数按 1 算，不夹的话 `tick / 0` 是 Infinity，
        // `(long)Math.Round(Infinity)` 又是个未定义值，会算出荒唐的吸附结果
        gridTicks = Math.Max(1, gridTicks);
        if (!double.IsFinite(tick)) return 0;
        return Math.Max(0, (long)Math.Round(tick / gridTicks, MidpointRounding.AwayFromZero) * gridTicks);
    }

    /// <summary>
    /// 吸附到**最近的小节线**。拖动导航条时用它 —— 停在半小节上，对着谱子找不着北
    /// （wireframe 标注 3）。
    ///
    /// 就是拿「一个小节」当格的 <see cref="SnapToGrid"/>，转发过去而不是另写一份：
    /// 小节和网格的吸附手感必须是同一套，不能一个吸得准一个吸不准。
    /// </summary>
    public static long SnapToBar(double tick, long ticksPerBar) => SnapToGrid(tick, ticksPerBar);

    /// <summary>
    /// 视图左边缘的合法范围。
    ///
    /// 上界是「最后 4 小节正好铺满一屏」—— 到底了就是到底了，再往后拖只会让谱面缩在左边、
    /// 右边空一片。整曲比 4 小节还短时上界为 0。
    /// </summary>
    public static long ClampViewStart(double tick, long totalTicks, long ticksPerBar)
    {
        if (!double.IsFinite(tick)) return 0;
        double max = (double)totalTicks - (double)Math.Max(1, ticksPerBar) * BarsVisible;
        return (long)Math.Round(Math.Clamp(tick, 0, Math.Max(0, max)));
    }

    // ==================== 导航条 ====================

    /// <summary>导航条上某个 tick 的横坐标（整曲铺满整条导航条）。</summary>
    public static double NavXAtTick(double tick, double width, double totalTicks)
        => totalTicks <= 0 ? 0 : tick / totalTicks * width;

    /// <summary>导航条横坐标 → tick（不吸附；要吸附用 <see cref="NavBarAtX"/>）。</summary>
    public static double NavTickAtX(double x, double width, double totalTicks)
    {
        if (!(width > 0) || totalTicks <= 0) return 0;
        return Math.Clamp(x / width, 0, 1) * totalTicks;
    }

    /// <summary>
    /// 导航条横坐标 → **最近的整小节**（0 起）。拖动时按小节吸附，不会停在半小节上。
    /// 拖出导航条两端就夹到首尾两小节，不会越界。
    /// </summary>
    public static int NavBarAtX(double x, double width, int barCount)
    {
        if (barCount <= 0 || !(width > 0)) return 0;
        double bar = x / width * barCount;
        if (double.IsNaN(bar)) return 0;
        return (int)Math.Clamp(Math.Round(bar, MidpointRounding.AwayFromZero), 0, barCount - 1);
    }
}
