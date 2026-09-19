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
    /// <param name="SelectedNotes">
    /// 本轨上选中的音符**身份**（<see cref="NoteId"/>），**按加进选中集的先后**（去重，但不是升序）。
    /// 顺序是**有意义**的：<see cref="LaneScene.SelectedNote"/> 取的是这一串的尾巴，
    /// 也就是「最后点的那一个」= 主选中 —— 排成升序就把这条语义毁了（见那边的说明）。
    /// **跨轨的选中由调用方先筛过一道** ——
    /// 一条轨的卷帘只画自己这条上的音，别条轨的选中在这一屏没有落笔的地方。
    ///
    /// 装身份而不是下标：这一串要活到下一次重画，而中间隔着一次编辑的时候，
    /// 下标已经指到别的音上去了（见 <see cref="NoteRef"/>）。
    /// </param>
    /// <param name="Drag">拖动中「将要落到哪」。没在拖（或者这一帧还没有位移）就是 null。</param>
    /// <param name="Marquee">框选中的那根带子。没在框选就是 null。</param>
    public readonly record struct RollOverlay(
        long PlayheadTick,
        bool PlayheadVisible,
        IReadOnlyList<NoteId> SelectedNotes,
        DragPreview? Drag = null,
        MarqueeRange? Marquee = null)
    {
        /// <summary>
        /// 只选中一个音时的写法（08 起建场景的那些地方写的都是这个形状）。
        ///
        /// 留着它是因为「点一下选中一个」仍然是绝大多数情况，
        /// 让这些调用方继续编得过，比逼它们各拼一个单元素数组干净。
        /// 选中集本身仍然只有一份 —— <see cref="LaneScene.SelectedNote"/>
        /// 只是这一串的尾巴（= 主选中），不是另一个真相源。
        /// </summary>
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
    /// 拖动中「将要落到哪」：被拖的一组音各挪多少。
    ///
    /// 位移是**增量**（和 <c>SongEditor.MoveNotes</c> 收的一样），不是目标位置 ——
    /// 一组音要保住彼此的相对关系，唯一说得清的说法就是「都挪这么远」。
    ///
    /// 改时值也套这个形状，于是三条拖动路径在这儿合流，落笔的地方只认一种数据：
    /// 拖身体是 <see cref="StartDeltaTicks"/> 变；拖尾巴是 <see cref="LengthDeltaTicks"/> 变；
    /// 拖头是两个一起变，而且**互为相反数**（尾巴钉住 = <c>新起点 + 新时值 == 老尾巴</c>）。
    /// </summary>
    /// <param name="NoteIds">
    /// 被拖的音符的**身份**（<see cref="NoteId"/>）—— 这一条轨上要画幽灵的就是这几个。
    /// 是调用方手上的活列表，只在 <see cref="BuildLane"/> 那一次读，场景不留引用。
    /// 装身份不是为了省事：拖动中间隔着一次重画，而重画的时候正好有一次编辑落了地的话，
    /// 下标就已经指到别的音上去了。
    /// </param>
    /// <param name="StartDeltaTicks">起点位移（tick，已吸附到网格）。</param>
    /// <param name="LengthDeltaTicks">时值增量（tick，已吸附到网格）。</param>
    /// <param name="DeltaPitch">音高增量（半音）。</param>
    public readonly record struct DragPreview(
        IReadOnlyList<NoteId> NoteIds, long StartDeltaTicks, long LengthDeltaTicks, int DeltaPitch);

    /// <summary>
    /// 框选那根带子盖住的**时间**区间（tick）。
    ///
    /// **只有时间，没有音高。** 空白处横拖删的是「这段区间里的所有音」，与音高无关
    /// （<c>PianoRollController.NotesInRange</c> 也只吃 tick）。框因此画成整条轨那么高 ——
    /// 矮矮地只盖住两行、结果把三行都删了，那是框在撒谎；wireframe 的 <c>.rangebar</c> 也是整高的。
    ///
    /// 起止不保证有序（往左拖就是反的），谁算像素谁负责归一 —— 见 <see cref="BuildLane"/>。
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

        /// <summary>
        /// 主选中 —— <see cref="SelectedNotes"/> 的最后一个，<see cref="NoteId.None"/> = 本轨没有选中的音。
        ///
        /// 和 <c>PianoRollController.Selection</c> 是同一条规矩（主选中是选中集的尾巴），
        /// 所以跨轨选中时它指的是**本轨上最后选中的那个**，不一定是全局主选中。
        /// 卷帘的选边框不读它（整组一视同仁），它是给「只想知道选中了谁」的调用方留的。
        /// </summary>
        public NoteId SelectedNote { get; init; } = NoteId.None;

        /// <summary>
        /// 被拖的那几个音**松手之后会落在哪**（虚线幽灵）。不在拖动中就是空的。
        ///
        /// 和 <see cref="Notes"/> 是两个独立的列表：原来那个音还在原位照画不误 ——
        /// 拖动期间谱面一个字节都没变，幽灵是唯一的「即将发生什么」。
        /// </summary>
        public IReadOnlyList<PianoRollGeometry.NoteBox> GhostNotes { get; init; }
            = Array.Empty<PianoRollGeometry.NoteBox>();

        /// <summary>框选那根带子。没在框选就是 null。</summary>
        public MarqueeRect? Marquee { get; init; }
    }

    /// <summary>
    /// 导航条上的一块音符（像素）。
    ///
    /// 和卷帘的 <see cref="PianoRollGeometry.NoteBox"/> 不是一回事：那个按**一屏**换算、
    /// 还带着命中判定要的下标；这个按**整曲**换算 —— 一个音在缩略图上多宽，
    /// 只由它在整曲里的位置决定，跟当前视图无关。
    /// </summary>
    public readonly record struct NavNote(double X, double Y, double Width, double Height);

    /// <summary>
    /// 导航条这块地方有多大、铺的是多长的曲子 —— 整曲缩影要换算要知道的全部东西。
    ///
    /// 收成一个值类型，是为了让 <see cref="BuildNav"/> 别收一列同类型的数字：
    /// 宽、高、整曲 tick 数、小节数四个挨着排，串一个位置照样编得过，
    /// 而串了看不出来（<c>TokenPalette</c> 那边为同一件事吃过一次亏）。
    /// 顺带让「一个小节多宽」只有一份出处 —— 画每 4 小节那根分隔线的控件和算音符块这边，
    /// 读的是同一个数。
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

    /// <summary>导航条要画的东西：<b>焦点轨</b>的整曲缩影 + 当前可见窗口那个框。</summary>
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
        var ghosts = new List<PianoRollGeometry.NoteBox>();
        double viewEnd = viewport.ViewStartTick + viewport.TicksVisible;

        for (int i = 0; i < track.Notes.Count; i++)
        {
            var note = track.Notes[i];
            if (note.StartTick >= viewEnd || note.EndTick <= viewport.ViewStartTick) continue;

            int pitch = note.Pitch + track.Transpose;
            // 音域是照这条轨自适应出来的，正常都在范围内；只有音域贴到 MIDI 两端被夹过才会漏出去
            if (pitch < viewport.LowPitch || pitch > viewport.HighPitch) continue;

            // 局部变量不叫 inRange：那个名字被参数（这张标记表）占着，
            // 同名会撞上 CS0136 —— 一个叫「表」一个叫「这个音在不在表里」，本来就该分开叫
            bool playable = InRangeAt(inRange, i);
            notes.Add(PianoRollGeometry.BoxOf(
                viewport, note.Id, note.StartTick, note.LengthTicks, pitch, playable));

            // 幽灵走的是**和真音符同一段算法**（BoxOf），不是「把算好的块平移几像素」。
            // 平移看着更省事，但宽度那个 MinNoteWidth 下限会让极短的音在拉长时对不上，
            // 预览和松手之后的落点就差那么一两个像素 —— 预览的价值全在「一模一样」上。
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
            Marquee = MarqueeOf(viewport, overlay.Marquee)
        };
    }

    /// <summary>
    /// 场景自己留一份选中集。
    ///
    /// 不直接拿传进来的那个列表：它是调用方的，下一帧就会被清掉重填
    /// （卷帘那边复用同一个缓冲），而场景要活到下一次 <c>SetScene</c> ——
    /// 留着引用的话，这一帧刚画到一半，选中集就被下一帧改掉了。
    /// 一屏也就几个身份，拷一份不值一提。
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
    /// 框选那根带子的像素位置。
    ///
    /// 起止统一归一（往左拖时起止是反的），纵向**铺满标尺以下的整条轨** ——
    /// 框的纵向本来就不参与判定，见 <see cref="MarqueeRange"/>。
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

    /// <summary>
    /// 缩略图上最短的一笔（像素）。一行音高摊到 30px 高的条上往往只有一两个像素，
    /// 不给下限的话，音域宽的那几条轨会一个块都看不见 —— 缩略图成了空白。
    /// </summary>
    private const double MinNavNoteHeight = 1;

    /// <summary>
    /// 缩略图上音符块上下让出的总高度（像素）。
    ///
    /// 一行音高只有一两个像素，不让这点缝，上下相邻的两行就糊成一整片，
    /// 「哪儿密哪儿空」恰恰是这条缩略图唯一要回答的问题。
    /// </summary>
    private const double NavNoteGap = 1;

    /// <summary>
    /// 算导航条要画的东西：**焦点轨**的音符铺满整曲。
    ///
    /// 画的是**焦点轨**，不是「含选中音符的那条轨」：焦点轨永远唯一、永远有定义，
    /// 不会因为按 Esc 清空选择就整条空掉，而且它跟 Ctrl+↑/↓ 换的是同一条 ——
    /// 一套心智模型，不是两套。这里只管「给哪条轨就画哪条轨」，
    /// 「焦点轨是谁」由调用方（窗口）每帧现问一次，于是换焦点轨下一帧就跟着换。
    ///
    /// 纵向按**这条轨自己的显示音域**铺开（和卷帘给的是同一个 <c>PitchRangeOf</c>），
    /// 所以缩略图上「高音在上」和卷帘里是同一个朝向，两边高低对得上。
    /// 音域窄的轨会被摊开占满整条高度，这是有意的：缩略图要回答的是「哪儿密哪儿空」，
    /// 把一条只用八个音的轨挤成中间一丝，反而什么都看不出来。
    /// </summary>
    /// <param name="nav">导航条多大、铺多长的曲子。</param>
    /// <param name="track">
    /// 画哪条轨的音符。**null = 没有这条轨**（轨被删光的那一帧，控制器那边也是这么认的，
    /// 见 <c>PianoRollController.PitchRangeOf</c>）—— 那时画出来是一条空的缩略图，不抛。
    /// </param>
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
                // 整曲之后的位置不该有音（tick 跨度按小节向上取整，正常盖得住每个音）。
                // 真碰上了就跳过：自绘控件默认**不裁边界**，画出去会压到隔壁控件上
                if (x >= nav.Width) continue;

                double right = Math.Min(nav.Width, nav.XAtTick(note.StartTick + note.LengthTicks));
                int pitch = note.Pitch + lane.Transpose;

                // 音高夹进音域的**边行**，而不是像卷帘那样把越界的音丢掉：
                // 越界只可能来自 FitPitchRange 在 MIDI 0/127 两端被夹过（音域本来就贴着边），
                // 而这种音恰恰是用户最想看见的那几个。缩略图少画一个音，看着就是「这段没谱」
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
