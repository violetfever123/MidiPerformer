using MidiPerformer.Core.Model;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 卷帘的 <b>tick → 像素</b> 方向：一屏要画的东西全部在这儿算好，控件只管落笔。
///
/// 和 <c>Controllers.PianoRollController</c> 的**像素 → tick** 互为逆运算 ——
/// 两边都只调 <see cref="PianoRollGeometry"/>，所以不可能各算各的。
/// 这里一行 <c>Control</c> 都没有，<c>Adapters</c> 里也不该有：视图层薄，就是为了让缝开在上面。
///
/// 产出的是**纯数据**（<see cref="LaneScene"/> / <see cref="NavScene"/>），
/// 于是「画什么」能单独看、单独测，不必先起一个窗口。
/// </summary>
public static class PianoRollPresenter
{
    /// <summary>标尺上小节号离小节线多远（照 wireframe 的 <c>lx = x + 4</c>）。</summary>
    private const double BarLabelInset = 4;

    /// <summary>此刻的播放头与选中状态 —— 画一屏卷帘需要知道的那点「现在」。</summary>
    /// <param name="PlayheadTick">播放头位置（tick）。</param>
    /// <param name="PlayheadVisible">
    /// 是否画播放头红线。**拖导航条时是 false**：红线跟着吸附一格格跳很烦（wireframe 标注 3），
    /// 所以拖动期间整个卷帘都不画它，松手后再出现。
    /// </param>
    /// <param name="SelectedNote">选中音符的下标，-1 = 没选中。</param>
    public readonly record struct RollOverlay(long PlayheadTick, bool PlayheadVisible, int SelectedNote);

    /// <summary>标尺上一个小节号。</summary>
    public readonly record struct BarLabel(double X, string Text);

    /// <summary>一条轨一屏要画的东西。</summary>
    public sealed class LaneScene
    {
        public required PianoRollGeometry.Viewport Viewport { get; init; }

        /// <summary>这一屏里出现的音符块，按 tick 升序。</summary>
        public required IReadOnlyList<PianoRollGeometry.NoteBox> Notes { get; init; }

        /// <summary>小节线横坐标。</summary>
        public required IReadOnlyList<double> BarLines { get; init; }

        /// <summary>拍线横坐标（不含小节线）。</summary>
        public required IReadOnlyList<double> BeatLines { get; init; }

        /// <summary>标尺上的小节号。出了视口的不给，免得画到屏幕外。</summary>
        public required IReadOnlyList<BarLabel> BarLabels { get; init; }

        /// <summary>播放头的横坐标。不可见时是 <see cref="double.NaN"/>。</summary>
        public double PlayheadX { get; init; } = double.NaN;

        /// <summary>选中音符的下标，-1 = 没选中。</summary>
        public int SelectedNote { get; init; } = -1;
    }

    /// <summary>导航条上的一根柱子：这个小节有多少个音。</summary>
    /// <param name="NoteCount">该小节的音符数（多轨合计）。</param>
    /// <param name="Fraction">相对最高的那一根的比例 0..1。空小节也给一个最小高度，柱子不能消失。</param>
    public readonly record struct NavBar(int NoteCount, double Fraction);

    /// <summary>导航条要画的东西：整曲密度缩略图 + 当前可见窗口那个框。</summary>
    public sealed class NavScene
    {
        public required IReadOnlyList<NavBar> Bars { get; init; }

        /// <summary>一个小节的宽度（像素）。柱子按它铺开。</summary>
        public required double BarWidth { get; init; }

        /// <summary>可见窗口那个框的左边缘与宽度（像素）。</summary>
        public required double ThumbX { get; init; }

        public required double ThumbWidth { get; init; }
    }

    /// <summary>
    /// 算一条轨这一屏要画的东西。
    /// <paramref name="inRange"/> 与 <paramref name="track"/> 的音符逐个对应 ——
    /// 它来自 <c>PianoRollController</c>，是**标灰**的判据（见那边的说明），这里只负责用。
    /// </summary>
    /// <param name="barCount">整曲多少小节 —— 谱面之外不画小节线。</param>
    /// <param name="ticksPerQuarter">四分音符多少 tick，用来把一个小节切成几拍。</param>
    public static LaneScene BuildLane(
        Track track,
        in PianoRollGeometry.Viewport viewport,
        int barCount,
        int ticksPerQuarter,
        IReadOnlyList<bool> inRange,
        in RollOverlay overlay)
    {
        var notes = new List<PianoRollGeometry.NoteBox>();
        double viewEnd = viewport.ViewStartTick + viewport.TicksVisible;

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];
            if (note.StartTick >= viewEnd || note.EndTick <= viewport.ViewStartTick) continue;

            int pitch = note.Pitch + track.Transpose;
            // 音域是照这条轨自适应出来的，正常都在范围内；只有音域贴到 MIDI 两端被夹过才会漏出去
            if (pitch < viewport.LowPitch || pitch > viewport.HighPitch) continue;

            notes.Add(PianoRollGeometry.BoxOf(
                viewport, i, note.StartTick, note.LengthTicks, pitch,
                InRangeAt(inRange, i)));
        }

        BuildGrid(viewport, barCount, ticksPerQuarter, out var bars, out var beats, out var labels);

        double playheadX = overlay.PlayheadVisible
            ? PianoRollGeometry.XAtTick(viewport, overlay.PlayheadTick)
            : double.NaN;

        return new LaneScene
        {
            Viewport = viewport,
            Notes = notes,
            BarLines = bars,
            BeatLines = beats,
            BarLabels = labels,
            PlayheadX = playheadX,
            SelectedNote = overlay.SelectedNote
        };
    }

    /// <summary>算导航条要画的东西。</summary>
    /// <param name="barNoteCounts">每小节的音符数，由 <c>PianoRollController</c> 数好。</param>
    /// <param name="width">导航条的像素宽。</param>
    /// <param name="totalTicks">整曲的 tick 跨度（按小节对齐）。</param>
    /// <param name="viewStartTick">可见窗口的左边缘。</param>
    /// <param name="ticksVisible">可见窗口跨多少 tick（固定 4 小节）。</param>
    public static NavScene BuildNav(
        IReadOnlyList<int> barNoteCounts,
        double width,
        long totalTicks,
        long viewStartTick,
        long ticksVisible)
    {
        int max = 0;
        foreach (int count in barNoteCounts)
            if (count > max) max = count;

        var bars = new List<NavBar>(barNoteCounts.Count);
        foreach (int count in barNoteCounts)
        {
            // 空小节也给一点点高度：柱子彻底消失的话，那段谱面看着像不存在
            double fraction = max <= 0 ? 0 : count / (double)max;
            bars.Add(new NavBar(count, fraction));
        }

        double barWidth = barNoteCounts.Count > 0 ? width / barNoteCounts.Count : width;

        return new NavScene
        {
            Bars = bars,
            BarWidth = barWidth,
            ThumbX = PianoRollGeometry.NavXAtTick(viewStartTick, width, totalTicks),
            ThumbWidth = totalTicks <= 0
                ? width
                : Math.Min(width, ticksVisible / (double)totalTicks * width)
        };
    }

    /// <summary>
    /// 一屏里的小节线 / 拍线 / 小节号。
    ///
    /// 多画一格：右边缘那条小节线正落在屏幕边上，不画的话卷帘右侧看着像断了。
    /// 小节号则相反 —— 出了视口就丢掉，画在屏幕外没有任何意义。
    /// </summary>
    private static void BuildGrid(
        in PianoRollGeometry.Viewport viewport, int barCount, int ticksPerQuarter,
        out List<double> barLines, out List<double> beatLines, out List<BarLabel> labels)
    {
        barLines = new List<double>();
        beatLines = new List<double>();
        labels = new List<BarLabel>();

        ticksPerQuarter = Math.Max(1, ticksPerQuarter);
        int beatsPerBar = Math.Max(1, (int)(viewport.TicksPerBar / ticksPerQuarter));

        int firstBar = PianoRollGeometry.BarAtTick(viewport.ViewStartTick, viewport.TicksPerBar);
        int lastBar = PianoRollGeometry.BarAtTick(
            viewport.ViewStartTick + viewport.TicksVisible, viewport.TicksPerBar);

        for (int bar = firstBar; bar <= lastBar + 1; bar++)
        {
            double x = PianoRollGeometry.XAtTick(
                viewport, PianoRollGeometry.TickOfBar(bar, viewport.TicksPerBar));

            if (bar <= barCount - 1)
            {
                barLines.Add(x);
                if (x + BarLabelInset >= 0 && x + 60 <= viewport.Width)
                    labels.Add(new BarLabel(x + BarLabelInset, Format.BarNumber(bar + 1)));
            }

            if (bar > barCount - 1) continue;

            for (int beat = 1; beat < beatsPerBar; beat++)
            {
                beatLines.Add(x + beat * viewport.TickWidth * ticksPerQuarter);
            }
        }
    }

    /// <summary>取标灰标记。数组短了就当「在范围内」—— 这一格只是画法，不该让画不出去。</summary>
    private static bool InRangeAt(IReadOnlyList<bool> inRange, int index)
        => index < 0 || index >= inRange.Count || inRange[index];
}
