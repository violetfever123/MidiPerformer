using MidiPerformer.Core.Model;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 卷帘的 tick → 像素 方向：一屏要画的东西全部在这儿算好，控件只管落笔。
/// 和 <c>Controllers.PianoRollController</c> 的像素 → tick 互为逆运算，两边都只调 <see cref="PianoRollGeometry"/>。
/// 产出的是纯数据（<see cref="LaneScene"/> / <see cref="NavScene"/>），于是「画什么」能单独看、单独测。
/// </summary>
public static class PianoRollPresenter
{
    /// <summary>标尺上小节号离小节线多远（照 wireframe 的 <c>lx = x + 4</c>）。</summary>
    private const double BarLabelInset = 4;

    /// <summary>此刻的播放头与选中状态 —— 画一屏卷帘需要知道的那点「现在」。</summary>
    /// <param name="PlayheadTick">播放头位置（tick）。</param>
    /// <param name="PlayheadVisible">是否画播放头红线。拖导航条时 false —— 红线跟着吸附一格格跳很烦，拖动期间整个卷帘都不画它。</param>
    /// <param name="SelectedNotes">本轨上选中的音符身份（<see cref="NoteId"/>），按加进选中集的先后（去重，不是升序）。顺序有意义：<see cref="LaneScene.SelectedNote"/> 取的是这一串的尾巴 = 主选中。跨轨的选中由调用方先筛过一道。</param>
    /// <param name="Drag">拖动中「将要落到哪」。没在拖（或者这一帧还没有位移）就是 null。</param>
    /// <param name="Marquee">框选中的那根带子。没在框选就是 null。</param>
    /// <param name="Cut">「抽掉一段」正划着的那一段，没在装备状态就是 null。和 <paramref name="Marquee"/> 是同一套几何但画法不同（框选那根是蓝的，这一根是红的）；分成两个字段是因为拖出来的那一段在松手之后要留着。</param>
    public readonly record struct RollOverlay(
        long PlayheadTick,
        bool PlayheadVisible,
        IReadOnlyList<NoteId> SelectedNotes,
        DragPreview? Drag = null,
        MarqueeRange? Marquee = null,
        MarqueeRange? Cut = null)
    {
        /// <summary>只选中一个音时的写法，内部仍然拼成单元素列表 —— 选中集本身只有一份。</summary>
        /// <param name="selectedNote">选中音符的身份，<see cref="NoteId.None"/> = 没选中。</param>
        public RollOverlay(long playheadTick, bool playheadVisible, NoteId selectedNote)
            : this(playheadTick, playheadVisible,
                selectedNote == NoteId.None
                    ? Array.Empty<NoteId>()
                    : new[] { selectedNote })
        {
        }
    }

    /// <summary>
    /// 拖动中「将要落到哪」：被拖的一组音各挪多少。位移是增量（和 <c>SongEditor.MoveNotes</c> 收的一样），
    /// 不是目标位置。改时值也套这个形状，于是三条拖动路径在这儿合流：拖身体是 <see cref="StartDeltaTicks"/> 变，
    /// 拖尾巴是 <see cref="LengthDeltaTicks"/> 变，拖头是两个一起变而且互为相反数（尾巴钉住）。
    /// </summary>
    /// <param name="NoteIds">被拖的音符的身份（<see cref="NoteId"/>）—— 这一条轨上要画幽灵的就是这几个。是调用方手上的活列表，只在 <see cref="BuildLane"/> 那一次读。装身份是因为拖动中间隔着一次重画，下标在那时已经指到别的音上了。</param>
    /// <param name="StartDeltaTicks">起点位移（tick，已吸附到网格）。</param>
    /// <param name="LengthDeltaTicks">时值增量（tick，已吸附到网格）。</param>
    /// <param name="DeltaPitch">音高增量（半音）。</param>
    public readonly record struct DragPreview(
        IReadOnlyList<NoteId> NoteIds, long StartDeltaTicks, long LengthDeltaTicks, int DeltaPitch);

    /// <summary>
    /// 框选那根带子盖住的时间区间（tick）。只有时间，没有音高 —— 空白处横拖删的是「这段区间里的所有音」，
    /// 所以框画成整条轨那么高。起止不保证有序（往左拖就是反的），谁算像素谁负责归一 —— 见 <see cref="BuildLane"/>。
    /// </summary>
    public readonly record struct MarqueeRange(long StartTick, long EndTick);

    /// <summary>框选那根带子画出来的样子（像素）。</summary>
    public readonly record struct MarqueeRect(double X, double Y, double Width, double Height);

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

        /// <summary>本轨上选中的音符身份。卷帘给每一个都描一圈 accent 边。</summary>
        public IReadOnlyList<NoteId> SelectedNotes { get; init; } = Array.Empty<NoteId>();

        /// <summary>主选中 —— <see cref="SelectedNotes"/> 的最后一个，<see cref="NoteId.None"/> = 本轨没有选中的音。
        /// 跨轨选中时它指的是本轨上最后选中的那个，不一定是全局主选中；卷帘的选边框不读它。</summary>
        public NoteId SelectedNote { get; init; } = NoteId.None;

        /// <summary>被拖的那几个音松手之后会落在哪（虚线幽灵）。不在拖动中就是空的。
        /// 和 <see cref="Notes"/> 是两个独立的列表：原来那个音还在原位照画不误 —— 拖动期间谱面一个字节都没变。</summary>
        public IReadOnlyList<PianoRollGeometry.NoteBox> GhostNotes { get; init; }
            = Array.Empty<PianoRollGeometry.NoteBox>();

        /// <summary>框选那根带子。没在框选就是 null。</summary>
        public MarqueeRect? Marquee { get; init; }

        /// <summary>「抽掉一段」正划着的那一段（红色带子）。没在装备状态就是 null。和 <see cref="Marquee"/> 分开是因为两者会同时出现在屏幕上，意思相反。</summary>
        public MarqueeRect? CutBand { get; init; }
    }

    /// <summary>
    /// 导航条上的一块音符（像素）。
    /// 和卷帘的 <see cref="PianoRollGeometry.NoteBox"/> 不是一回事：那个按一屏换算、还带命中判定要的下标；
    /// 这个按整曲换算 —— 一个音在缩略图上多宽只由它在整曲里的位置决定，跟当前视图无关。
    /// </summary>
    public readonly record struct NavNote(double X, double Y, double Width, double Height);

    /// <summary>
    /// 导航条这块地方有多大、铺的是多长的曲子 —— 整曲缩影要换算要知道的全部东西。
    /// 收成一个值类型，是为了让 <see cref="BuildNav"/> 别收一列同类型的数字（串了位看不出来），
    /// 顺带让「一个小节多宽」只有一份出处。
    /// </summary>
    /// <param name="Width">导航条的像素宽。</param>
    /// <param name="Height">导航条的像素高。</param>
    /// <param name="TotalTicks">整曲的 tick 跨度（按小节对齐）。</param>
    /// <param name="BarCount">整曲多少小节。点击时按小节吸附要用它（见 <c>RollNavStrip.RaiseSeek</c>）。</param>
    public readonly record struct NavViewport(double Width, double Height, long TotalTicks, int BarCount)
    {
        /// <summary>一个小节占的像素宽。畸形数据（0 小节）按整条宽算，不除零。</summary>
        public double BarWidth => BarCount > 0 ? Width / BarCount : Width;

        /// <summary>整曲里某个 tick 落在这条上的横坐标。</summary>
        public double XAtTick(double tick) => PianoRollGeometry.NavXAtTick(tick, Width, TotalTicks);
    }

    /// <summary>导航条要画的东西：焦点轨的整曲缩影 + 当前可见窗口那个框。</summary>
    public sealed class NavScene
    {
        /// <summary>整曲多少小节。点击时要按它吸到整小节上，所以场景必须带着这个数。</summary>
        public required int BarCount { get; init; }

        /// <summary>一个小节的宽度（像素）。每 4 小节那根分隔线按它铺开。</summary>
        public required double BarWidth { get; init; }

        /// <summary>焦点轨的音符块，顺序跟着 <c>Track.Notes</c>（按起点升序）。</summary>
        public required IReadOnlyList<NavNote> Notes { get; init; }

        /// <summary>可见窗口那个框的左边缘与宽度（像素）。</summary>
        public required double ThumbX { get; init; }

        public required double ThumbWidth { get; init; }

        /// <summary>播放头的横坐标（像素）。整曲缩略图上就靠它看播到哪儿了。</summary>
        public required double PlayheadX { get; init; }
    }

    /// <summary>
    /// 算一条轨这一屏要画的东西。<paramref name="inRange"/> 与 <paramref name="track"/> 的音符逐个对应 ——
    /// 它来自 <c>PianoRollController</c>，是标灰的判据，这里只负责用。
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
        var ghosts = new List<PianoRollGeometry.NoteBox>();
        double viewEnd = viewport.ViewStartTick + viewport.TicksVisible;

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];
            if (note.StartTick >= viewEnd || note.EndTick <= viewport.ViewStartTick) continue;

            int pitch = note.Pitch + track.Transpose;
            // 音域是照这条轨自适应出来的，正常都在范围内；只有音域贴到 MIDI 两端被夹过才会漏出去
            if (pitch < viewport.LowPitch || pitch > viewport.HighPitch) continue;

            // 不叫 inRange：那个名字被参数（这张标记表）占着，同名会撞上 CS0136
            bool playable = InRangeAt(inRange, i);
            notes.Add(PianoRollGeometry.BoxOf(
                viewport, note.Id, note.StartTick, note.LengthTicks, pitch, playable));

            // 幽灵走的是和真音符同一段算法（BoxOf），不是把算好的块平移几像素 ——
            // MinNoteWidth 那个下限会让极短的音在拉长时对不上，预览和落点就差一两个像素
            if (overlay.Drag is { } drag && Contains(drag.NoteIds, note.Id))
            {
                ghosts.Add(PianoRollGeometry.BoxOf(
                    viewport, note.Id,
                    note.StartTick + drag.StartDeltaTicks,
                    note.LengthTicks + drag.LengthDeltaTicks,
                    pitch + drag.DeltaPitch,
                    playable));
            }
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
            SelectedNotes = CopyOf(overlay.SelectedNotes),
            SelectedNote = overlay.SelectedNotes.Count > 0 ? overlay.SelectedNotes[^1] : NoteId.None,
            GhostNotes = ghosts,
            Marquee = MarqueeOf(viewport, overlay.Marquee),
            CutBand = MarqueeOf(viewport, overlay.Cut)
        };
    }

    /// <summary>
    /// 场景自己留一份选中集，不直接拿传进来的那个列表：它是调用方的，下一帧就会被清掉重填（卷帘那边复用同一个缓冲），
    /// 而场景要活到下一次 <c>SetScene</c>。
    /// </summary>
    private static IReadOnlyList<NoteId> CopyOf(IReadOnlyList<NoteId> ids)
        => ids.Count == 0 ? Array.Empty<NoteId>() : new List<NoteId>(ids);

    /// <summary>这一串里有没有 <paramref name="id"/>。线性扫一遍 —— 一次拖动也就几个到几十个音。</summary>
    private static bool Contains(IReadOnlyList<NoteId> ids, NoteId id)
    {
        for (int i = 0; i < ids.Count; i++)
            if (ids[i] == id) return true;
        return false;
    }

    /// <summary>
    /// 带子的像素位置。框选那根（蓝）和「抽掉一段」那根（红）走的是同一个算法，只是喂进去的区间不同。
    /// 起止统一归一（往左拖时起止是反的），纵向铺满标尺以下的整条轨 —— 带子的纵向不参与判定。
    /// </summary>
    private static MarqueeRect? MarqueeOf(in PianoRollGeometry.Viewport viewport, MarqueeRange? range)
    {
        if (range is not { } marquee) return null;

        double left = PianoRollGeometry.XAtTick(viewport, Math.Min(marquee.StartTick, marquee.EndTick));
        double right = PianoRollGeometry.XAtTick(viewport, Math.Max(marquee.StartTick, marquee.EndTick));

        return new MarqueeRect(
            left, PianoRollGeometry.RulerHeight, right - left,
            Math.Max(0, viewport.Height - PianoRollGeometry.RulerHeight));
    }

    /// <summary>缩略图上最短的一笔（像素）。不给下限的话，音域宽的那几条轨会一个块都看不见。</summary>
    private const double MinNavNoteHeight = 1;

    /// <summary>缩略图上音符块上下让出的总高度（像素）。不让这点缝，上下相邻的两行就糊成一整片。</summary>
    private const double NavNoteGap = 1;

    /// <summary>
    /// 算导航条要画的东西：焦点轨的音符铺满整曲。
    /// 画的是焦点轨，不是「含选中音符的那条轨」：焦点轨永远唯一、永远有定义，而且跟 Ctrl+↑/↓ 换的是同一条。
    /// 纵向按这条轨自己的显示音域铺开（和卷帘给的是同一个 <c>PitchRangeOf</c>），音域窄的轨会被摊开占满整条高度
    /// —— 缩略图要回答的是「哪儿密哪儿空」。
    /// </summary>
    /// <param name="nav">导航条多大、铺多长的曲子。</param>
    /// <param name="track">画哪条轨的音符。null = 没有这条轨（轨被删光的那一帧）—— 那时画出来是一条空的缩略图，不抛。</param>
    /// <param name="pitchRange">这条轨的显示音域（含余量）。</param>
    /// <param name="playheadTick">播放头位置（tick）。</param>
    /// <param name="viewStartTick">可见窗口的左边缘。</param>
    /// <param name="ticksVisible">可见窗口跨多少 tick（固定 4 小节）。</param>
    public static NavScene BuildNav(
        NavViewport nav,
        Track? track,
        (int Low, int High) pitchRange,
        long playheadTick,
        long viewStartTick,
        long ticksVisible)
    {
        var notes = new List<NavNote>();

        if (track is { } lane)
        {
            // 行数是这条轨的音域，不是 MIDI 那 128 行：缩略图纵向的分辨率全给这条轨真正用到的音
            int rows = Math.Max(1, pitchRange.High - pitchRange.Low + 1);
            double rowHeight = nav.Height / rows;
            double blockHeight = Math.Max(MinNavNoteHeight, rowHeight - NavNoteGap);

            foreach (var note in lane.Notes)
            {
                double x = nav.XAtTick(note.StartTick);
                // 整曲之后的位置不该有音。真碰上了就跳过：自绘控件默认不裁边界，画出去会压到隔壁控件上
                if (x >= nav.Width) continue;

                double right = Math.Min(nav.Width, nav.XAtTick(note.StartTick + note.LengthTicks));
                int pitch = note.Pitch + lane.Transpose;

                // 音高夹进音域的边行，而不是像卷帘那样丢掉越界的音 ——
                // 越界只可能来自 FitPitchRange 在 MIDI 0/127 两端被夹过，而那种音恰恰是用户最想看见的
                int row = Math.Clamp(pitchRange.High - pitch, 0, rows - 1);

                notes.Add(new NavNote(
                    x,
                    row * rowHeight,
                    Math.Max(PianoRollGeometry.MinNoteWidth, right - x - PianoRollGeometry.NoteGap),
                    blockHeight));
            }
        }

        return new NavScene
        {
            BarCount = nav.BarCount,
            BarWidth = nav.BarWidth,
            Notes = notes,
            ThumbX = PianoRollGeometry.NavXAtTick(viewStartTick, nav.Width, nav.TotalTicks),
            ThumbWidth = nav.TotalTicks <= 0
                ? nav.Width
                : Math.Min(nav.Width, ticksVisible / (double)nav.TotalTicks * nav.Width),
            PlayheadX = nav.XAtTick(playheadTick)
        };
    }

    /// <summary>
    /// 一屏里的小节线 / 拍线 / 小节号。
    /// 多画一格：右边缘那条小节线正落在屏幕边上，不画的话卷帘右侧看着像断了。小节号相反 —— 出了视口就丢掉。
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
