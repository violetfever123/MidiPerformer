using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;

namespace MidiPerformer.App.Views;

/// <summary>
/// 一条轨的卷帘。<b>画的全是 <see cref="PianoRollPresenter.LaneScene"/> 里算好的东西</b>，
/// 这里一个乘法都不做 —— 「画什么」和「画在哪」分开，前者才能不起窗口就测。
///
/// <b>它自己不改谱面。</b>按下 / 拖动 / 抬起只产出三种**意图**
/// （<see cref="NotesMoved"/> / <see cref="NoteResized"/> / <see cref="NotesDeleted"/>），
/// 调命令的是窗口：编辑脊柱只有一条，撤销的记账在装饰器里，谁调命令都自动有撤销，
/// 但调命令的地方只该有一处。选中集也不存在这儿 —— 它住在
/// <see cref="PianoRollController"/> 里（全程序一个），这里只负责调它、再喊一声
/// <see cref="SelectionChanged"/>。多选只有一条路：在音符上 <c>Shift</c> 点
/// （空白处横拖虽然也框住一段，但那一下的结局是直接删掉，不留选中）。
///
/// <b>拖动期间谱面一个字节都不改。</b>一边拖一边调命令，撤销栈会被灌满上百条微步，
/// 用户按 Ctrl+Z 得按到手酸。所以每一帧只画**预览**：被拖的音用虚线幽灵画在它们将要去的位置上
/// （见 <see cref="DragPreview"/> / <see cref="Marquee"/>），松手才结算成一条命令。
/// 连「即将发生什么」也不在这儿算 —— 这里给的是纯数据（挪多少 tick、框住哪段），
/// 方块、虚线幽灵和框选那根带子都由 <see cref="PianoRollPresenter"/> 算出来。
///
/// 命中判定同样交给 <see cref="PianoRollController"/>，画的和点的才是同一份几何。
///
/// 画法照 <c>RollPreviewStrip</c>：实心块 + 顶边 1px 亮线，不倒圆角、不描边。
/// </summary>
public sealed class PianoRollLane : Control
{
    /// <summary>刻度数字的字号，照 wireframe 的 <c>10px</c>。</summary>
    private const double RulerFontSize = 10;

    /// <summary>播放头红线多宽，照 wireframe 的 <c>fillRect(px-1, 0, 2, h)</c>。</summary>
    private const double PlayheadWidth = 2;

    /// <summary>
    /// 幽灵的虚线节奏（画 3px、空 2px）。
    ///
    /// 虚线是**这一层唯一能用的「还不作数」记号**：实心块 = 谱面上真有的音，
    /// 虚线 = 松手之后会变成的样子。整块换成一个新颜色做不到这一点 ——
    /// 不新增颜色值（令牌就那 25 条），而且实心/虚线的分别比两种蓝的分别好认。
    ///
    /// 框选那根带子**不用**它：带子是实底 + 左右两条实边（见 <c>DrawMarquee</c>，
    /// 照 wireframe 的 <c>.rangebar</c>）。两者说的不是一件事，见那边的说明。
    /// </summary>
    private static readonly ImmutableDashStyle GhostDash = new(new double[] { 3, 2 }, 0);

    /// <summary>
    /// 框选那根带子底色补的透明度，照 wireframe 的 <c>.rangebar { opacity: .7 }</c>。
    ///
    /// 令牌里只有实色，透明是**画的时候**配上去的（同 <c>RollNavStrip.WithAlpha</c>）——
    /// 本切片不新增颜色值，基色仍然只从令牌来。实心铺满会把框里的音盖住，
    /// 而那几个音正是用户盯着要看的东西。
    /// </summary>
    private const double MarqueeOpacity = 0.7;

    /// <summary>
    /// 三种光标，建一次就够了。
    ///
    /// 每次 <c>OnPointerMoved</c> 都 <c>new Cursor(...)</c> 的话，鼠标一动就造一个
    /// 平台光标对象（<see cref="Cursor"/> 是 IDisposable，还带一个句柄），
    /// 一次拖动就是几百个。和 <c>RollNavStrip</c> 一样，光标是控件级的东西，建一次用到底。
    /// </summary>
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor GrabCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>这一次拖动在改什么。没有拖动时是 <see cref="None"/>。</summary>
    private enum DragKind
    {
        None = 0,

        /// <summary>拖身体：整组一起挪（时间和音高）。</summary>
        Move,

        /// <summary>拖左边缘：起点和时值一起改，尾巴钉住。</summary>
        ResizeHead,

        /// <summary>拖右边缘：只改时值。</summary>
        ResizeTail,

        /// <summary>在空白处按下：框一段时间出来。</summary>
        Marquee
    }

    private TokenSource? _tokens;
    private PianoRollPresenter.LaneScene? _scene;
    private int _hoveredNote = -1;

    private DragKind _drag = DragKind.None;

    /// <summary>按下时命中的那个音（本轨音符数组里的下标）。框选时它是 -1。</summary>
    private int _anchor = -1;

    /// <summary>
    /// 按下那一刻指针底下的 tick 与音高。
    ///
    /// **一切位移都是相对它算的**（不是相对「鼠标挪了几像素」）：吸附要的是
    /// 「锚音原来的位置 + 原始位移」再吸一次，理由见 <see cref="UpdateDrag"/>。
    /// 存 tick 而不是像素，是因为视口可能在拖动中途滚动（播放跟着播放头走），
    /// 像素差会跟着失效，tick 差不会。
    /// </summary>
    private double _pressTick;
    private int _pressPitch;

    /// <summary>
    /// 按下那一刻指针在控件里的位置（像素）。
    ///
    /// **只用来量「手挪开了没有」**，不用来算位移 —— 位移一律走 <see cref="_pressTick"/>，
    /// 理由是视口会在拖动中途滚动（播放跟着播放头走），像素差那时就失效了。
    /// 而「手抖没抖」问的正是像素：音符在底下滚不滚，跟手有没有动是两码事。
    ///
    /// 判据在 <see cref="PianoRollGeometry.ExceedsDragThreshold"/>，用在 <see cref="UpdateDrag"/> 开头。
    /// </summary>
    private Point _pressPoint;

    /// <summary>锚音在按下那一刻的位置（模型里的 tick）。整组套的位移由它吸出来。</summary>
    private long _anchorStart;
    private long _anchorLength;

    /// <summary>此刻要画的预览位移（已吸附、已夹）。</summary>
    private long _startDelta;
    private long _lengthDelta;
    private int _pitchDelta;

    /// <summary>框选的起止 tick（未吸附 —— 框说的是「我框到哪儿了」，不是「吸到哪条线上」）。</summary>
    private double _marqueeStart;
    private double _marqueeEnd;

    /// <summary>这一次要动的整组音（按下那一刻的快照，见 <see cref="NoteMoveRequest"/>）。</summary>
    private readonly List<NoteRef> _group = new();

    /// <summary>其中落在**本轨**上的下标 —— 幽灵只画得动这一条轨上的。</summary>
    private readonly List<int> _ghostIndexes = new();

    public PianoRollLane()
    {
        // 音符块允许画到视口外一点点（尾巴），画到控件外面就该被切掉
        ClipToBounds = true;
        Cursor = ArrowCursor;
    }

    /// <summary>做命中判定用的控制器。**只用于悬停与命中** —— 画法一个字都不从这儿取。</summary>
    public PianoRollController? Controller { get; set; }

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex { get; set; }

    /// <summary>悬停到的音符变了。参数是音符下标，-1 = 移开了或没命中。</summary>
    public event EventHandler<int>? HoverChanged;

    /// <summary>这一组音被拖到了别处，命令由窗口去调（一次拖动只发一次）。</summary>
    public event EventHandler<NoteMoveRequest>? NotesMoved;

    /// <summary>这个音的时值（和/或起点）被拖成了新的值，命令由窗口去调。</summary>
    public event EventHandler<NoteResizeRequest>? NoteResized;

    /// <summary>
    /// 空白处横拖框住了一段，这一段里的音要删掉。参数就是**要删的那些音**（已按区间算好），
    /// 命令由窗口去调。
    ///
    /// 框里一个音都没有时不发这条（点一下空白的意思不是「删掉零个音」）。
    /// </summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? NotesDeleted;

    /// <summary>选中集变了（点中一个音、或空白处按下清空）。窗口靠它刷新读数条。</summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? SelectionChanged;

    /// <summary>
    /// 拖动预览变了（幽灵挪了、带子宽了），这一屏要重算一遍才画得出来。
    ///
    /// 场景是 <c>TrackLaneView</c> 那边算的，所以这里只能喊一声；它再拿**同一份**播放头状态
    /// 重跑一次 Refresh。拖动中不重算的话，画面会一直停在按下那一刻的预览上。
    /// </summary>
    public event EventHandler? PreviewChanged;

    /// <summary>
    /// 被拖的那几个音**松手之后会落在哪**。没在拖、或者还没有位移就是 null。
    ///
    /// 没有位移时给 null 而不是给一个全 0 的预览：幽灵和原音符完全重合，
    /// 画出来就是一层虚线糊在实心块上，看着像「坏了」。
    /// </summary>
    public PianoRollPresenter.DragPreview? DragPreview
    {
        get
        {
            if (_drag is not (DragKind.Move or DragKind.ResizeHead or DragKind.ResizeTail)) return null;
            if (_startDelta == 0 && _lengthDelta == 0 && _pitchDelta == 0) return null;
            if (_ghostIndexes.Count == 0) return null;

            // 把活列表交出去是安全的：它只在这次 BuildLane 里被读一遍，场景不留引用
            return new PianoRollPresenter.DragPreview(_ghostIndexes, _startDelta, _lengthDelta, _pitchDelta);
        }
    }

    /// <summary>此刻框住的那一段。没在框选、或者还没拖出宽度（按下没动）就是 null。</summary>
    public PianoRollPresenter.MarqueeRange? Marquee
        => _drag == DragKind.Marquee && _marqueeStart != _marqueeEnd
            ? new PianoRollPresenter.MarqueeRange(
                (long)Math.Round(_marqueeStart), (long)Math.Round(_marqueeEnd))
            : null;

    public TokenSource? Tokens
    {
        get => _tokens;
        set
        {
            if (_tokens is not null) _tokens.Changed -= OnTokensChanged;
            _tokens = value;
            if (_tokens is not null) _tokens.Changed += OnTokensChanged;
            InvalidateVisual();
        }
    }

    /// <summary>换一屏要画的东西。视口一变（滚动、窗口改宽）就要重给一次。</summary>
    public void SetScene(PianoRollPresenter.LaneScene? scene)
    {
        _scene = scene;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Tokens is not { } tokens || _scene is not { } scene) return;

        var palette = tokens.Current;
        var viewport = scene.Viewport;

        // 轨道底色铺满整条，顺带给鼠标一个能命中的面 —— 悬停要落在空白处也算「移开了」。
        // 深浅按轨号奇偶交替，照 wireframe 的 .lane.a / .lane.b：堆在一起时能看清一条轨在哪儿结束。
        var background = TrackIndex % 2 == 0 ? palette.LaneA : palette.LaneB;
        context.FillRectangle(new ImmutableSolidColorBrush(background), new Rect(Bounds.Size));

        // 音高行的黑键底纹**刻意没画**：wireframe 用的是半透明灰（rgba(128,140,155,.075)），
        // 而这一层不许出现颜色字面值，也不为它新造一个令牌。少一层底纹不影响读谱。
        // 音高行之间的分隔线同理 —— 音符块自己就说明了行在哪。

        DrawGrid(context, palette, scene);

        // 音符块：压在网格上面，播放头再压在音符上面
        foreach (var box in scene.Notes)
        {
            DrawNote(context, palette, box, scene.SelectedNotes.Contains(box.Index));
        }

        // 幽灵压在真音符上面：拖到哪儿去了，看的就是它。下面那一块**不动**，
        // 一实一虚摆在一起，用户一眼就看出「现在在哪、松手去哪儿」。
        foreach (var ghost in scene.GhostNotes)
        {
            DrawGhost(context, palette, ghost);
        }

        DrawMarquee(context, palette, scene.Marquee);

        // 标尺下沿那条横线：刻度区到此为止，照 wireframe 的 `moveTo(0, RULER_H + .5)`
        context.DrawLine(
            new Pen(new ImmutableSolidColorBrush(palette.Line), 1),
            new Point(0, Math.Round(PianoRollGeometry.RulerHeight) + 0.5),
            new Point(viewport.Width, Math.Round(PianoRollGeometry.RulerHeight) + 0.5));

        DrawRulerLabels(context, palette, scene);
        DrawPlayhead(context, palette, scene, viewport);
    }

    // ==================== 指针：按下 ====================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Controller is not { } controller || _scene is not { } scene) return;
        // 只接左键：右键在别处另有意思（将来），别在这儿顺手开一次拖动
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 上一次拖动没收拾干净（捕获丢过、控件刚被换过）就地收掉，别让两次拖动叠在一起
        CancelDrag();

        var viewport = scene.Viewport;
        var point = e.GetPosition(this);
        var hit = controller.HitTestRef(TrackIndex, viewport, point.X, point.Y, out var note);

        _pressTick = PianoRollGeometry.TickAtX(viewport, point.X);
        _pressPitch = PianoRollGeometry.PitchAtY(viewport, point.Y);
        _pressPoint = point;

        // 先抓住指针：拖到控件外面松手（或者窗口中途重画把这条轨换掉）也要收得到消息。
        // 放在改选中之前 —— 改选中会让窗口重画一屏，那一趟里这条控件要是被换掉，
        // 捕获就丢了，而这个顺序至少保证「抓到了才算数」
        e.Pointer.Capture(this);
        e.Handled = true;

        bool selectionChanged = false;

        switch (hit)
        {
            case PianoRollGeometry.RollHit.Head:
            case PianoRollGeometry.RollHit.Tail:
                // 拖边缘是**直接操作**，不是选中的动作：没选中它也照样能拉时值。
                // 拉成什么样由幽灵说，不需要先给它点亮一圈边 —— 那反而会让人以为
                // 「要先选中才能改」（框选出来的那一组里，是谁被拉了也看不出来）。
                _drag = hit == PianoRollGeometry.RollHit.Head ? DragKind.ResizeHead : DragKind.ResizeTail;
                _anchor = note.Index;
                CaptureAnchor(controller);
                _ghostIndexes.Add(note.Index);
                break;

            case PianoRollGeometry.RollHit.Body:
                _drag = DragKind.Move;
                _anchor = note.Index;
                CaptureAnchor(controller);

                // Shift 点一下 = 把它也带上。这是**全片唯一能把选中集堆到两个以上的入口**：
                // 空白处横拖那一下虽然也框住一段，可它的结局是直接删掉（见 default 一支），
                // 不留选中 —— 于是「框选一组音符一起移动」这条只能走 Shift 一路。
                //
                // ExtendSelection 对已经在里面的音什么都不做，正合这里的语义：
                // 用户点一个已选中的音，意思是「留着它」，而不是把整组收成它一个。
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    int before = controller.SelectedNotes.Count;
                    controller.ExtendSelection(note);
                    // 本来就在里面 → 选中集没变，那就别喊：喊一声窗口就白重画一屏
                    selectionChanged = controller.SelectedNotes.Count != before;
                }
                // 不在选中集里就先只选它一个；已经在里面就整组留着 —— 用户拖的是那一组
                else if (!controller.IsSelected(note))
                {
                    controller.SelectOnly(note);
                    selectionChanged = true;
                }

                // 要动的那组在按下这一刻定下来：拖动当中谁也不会再改选中集，快照一份，
                // 松手时交给命令的就是它（松手时现取也行，但那时窗口可能已经动过选中集了）
                _group.Clear();
                _group.AddRange(controller.SelectedNotes);
                foreach (var item in _group)
                    if (item.Track == TrackIndex) _ghostIndexes.Add(item.Index);
                break;

            default:
                // 空白处按下 = 框选。选中集**当场清掉**：这一次手势要么框出一段删掉、
                // 要么什么也没框到，两种结果都不该留着上一次的选中
                //（框选删完那几个音就没了，留着它们的下标只会指向别人）。
                controller.ClearSelection();
                selectionChanged = true;
                _drag = DragKind.Marquee;
                _marqueeStart = _marqueeEnd = _pressTick;
                break;
        }

        UpdateCursor(hit);
        InvalidateVisual();

        if (selectionChanged) RaiseSelectionChanged();
    }

    // ==================== 指针：拖动 ====================

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_drag != DragKind.None)
        {
            // 拖动中**不报悬停**：读数条上那几个数说的是模型里的那个音，而拖动期间模型没变。
            // 顺着指针报的话，指针一跑出那个音的方块（方块自己没动），读数条就空了 ——
            // 看着像「拖丢了」。按下时那个音还亮着，正是用户手上正抓着的那个。
            UpdateDrag(e.GetPosition(this));
            return;
        }

        if (_scene is not { } scene || Controller is not { } controller) return;

        var point = e.GetPosition(this);
        var hit = controller.HitTestRef(TrackIndex, scene.Viewport, point.X, point.Y, out var note);
        UpdateCursor(hit);
        SetHover(hit == PianoRollGeometry.RollHit.None ? -1 : note.Index);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // 拖动中指针跑出控件是常事（往上拖过了标尺、往左右拖出了窗口），
        // 这时候把悬停清掉，读数条会在用户还在拖的时候突然空掉
        if (_drag != DragKind.None) return;

        SetHover(-1);
    }

    // ==================== 指针：抬起 ====================

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None) return;

        // 先把状态落定，再放开捕获：Capture(null) 会回调到 CaptureLost，
        // 而那一路是「这次拖动作废」—— 状态还在的话，刚算出来要提交的位移会被它清掉
        var kind = _drag;
        _drag = DragKind.None;

        e.Pointer.Capture(null);
        Settle(kind);
    }

    /// <summary>
    /// 捕获丢了（窗口中途重建、系统弹窗抢走了指针、控件被移出可视树…）。
    ///
    /// **这一次拖动不作数。** 拖到一半被打断，谁也不知道用户本来要拖到哪儿，
    /// 按最后那一帧的位置交一条命令，等于替他做了个他没做完的决定。
    /// 谱面一个字节都没动，幽灵一收，就是按下之前的样子。
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    // ==================== 拖动 ====================

    /// <summary>把锚音在模型里的位置记下来 —— 吸附是「锚的起点 + 原始位移」算的，得有个起点。</summary>
    private void CaptureAnchor(PianoRollController controller)
    {
        var note = controller.Song.Tracks[TrackIndex].Notes[_anchor];
        _anchorStart = note.StartTick;
        _anchorLength = note.LengthTicks;
    }

    /// <summary>
    /// 指针动了，把预览的位移重算一遍。
    ///
    /// <b>这里一个谱面都不改</b>，只更新那几个增量（以及框选的另一头），
    /// 然后喊一声让场景重画。真正的编辑在 <see cref="Settle"/> 里一次交出去。
    /// </summary>
    private void UpdateDrag(Point point)
    {
        if (_scene is not { } scene || Controller is not { } controller) return;

        // 视口**每次现取**，不留按下那一刻的那份：播放中视图会跟着播放头滚，
        // 拿旧视口算出来的落点会整体偏掉一个滚动量
        var viewport = scene.Viewport;

        // 手还没挪够一个门槛 —— 这一下是「点」，不是「拖」。
        //
        // 三个位移和框选的那一头一起退回按下那一刻的样子，于是松手时 Settle 里
        // 那三个守卫全都拦得住：不会因为 1px 手抖把一个抢拍的音吸回格线，
        // 也不会让框选区间从零长度变成几个 tick（那会删掉别行上盖住这几个 tick 的音）。
        //
        // 退出前照样喊一声重画：拖出去又拖回门槛以内的话，幽灵和带子得跟着收掉。
        if (!PianoRollGeometry.ExceedsDragThreshold(
                point.X - _pressPoint.X, point.Y - _pressPoint.Y))
        {
            _startDelta = 0;
            _lengthDelta = 0;
            _pitchDelta = 0;
            _marqueeEnd = _marqueeStart;
            RaisePreviewChanged();
            return;
        }

        double rawTick = PianoRollGeometry.TickAtX(viewport, point.X);
        double moved = rawTick - _pressTick;
        long grid = controller.GridTicks;

        switch (_drag)
        {
            case DragKind.Move:
            {
                // 吸附**相对锚音原来的位置**算，不是相对「鼠标挪了几格」：
                // 一个本来就离格的音（抢拍），第一次拖动会被吸回最近的格线上 ——
                // 那正是吸附的意思。按鼠标位移算的话，它永远只能整格地挪，
                // 抢的那一点永远修不掉。
                long start = PianoRollGeometry.SnapToGrid(_anchorStart + moved, grid);
                long delta = start - _anchorStart;
                int pitch = PianoRollGeometry.PitchAtY(viewport, point.Y) - _pressPitch;

                ClampMove(controller, ref delta, ref pitch);

                _startDelta = delta;
                _lengthDelta = 0;
                _pitchDelta = pitch;
                break;
            }

            case DragKind.ResizeTail:
            {
                // 只改时值，起点钉住。吸的是**时值**，不是尾巴的绝对位置：
                // 一个抢拍的音，尾巴本来就落在半格上，吸绝对位置会把它拽回格线 ——
                // 用户只是想把这一段拉长一点，没打算顺手把抢的那一点抹平。
                long length = PianoRollGeometry.SnapToGrid(_anchorLength + moved, grid);
                if (length < grid) length = grid;   // 时值最小一格

                _startDelta = 0;
                _lengthDelta = length - _anchorLength;
                break;
            }

            case DragKind.ResizeHead:
            {
                // 起点和时值一起改，**尾巴钉住**：新起点 + 新时值 == 老尾巴。
                // 不钉住的话，拉左边会把这个音整体挪走，而用户要的是「它从哪儿开始响」。
                long tail = _anchorStart + _anchorLength;
                long start = PianoRollGeometry.SnapToGrid(_anchorStart + moved, grid);

                // 最多拉到尾巴前一格（给「时值至少一格」留位置），最少到谱面开头 ——
                // 负 tick 在谱面上不存在，这么夹出来的结果和命令的夹法是一致的
                if (start > tail - grid) start = tail - grid;
                if (start < 0) start = 0;

                _startDelta = start - _anchorStart;
                _lengthDelta = -_startDelta;
                break;
            }

            case DragKind.Marquee:
                // 框选**不吸附**：框说的是「我框到哪儿了」，而看不看得见格线是另一回事
                //（十六分的格线压根没画，见 PianoRollGeometry.GridTicks 的说明）。
                // 吸附一下反而会让「框住的那个音」和画出来的框对不上，那才是真的没法用。
                _marqueeEnd = rawTick;
                break;

            default:
                return;
        }

        RaisePreviewChanged();
    }

    /// <summary>
    /// 把整组的位移夹到合法范围内。
    ///
    /// 为什么界面这一层要算一遍：预览要是画到命令去不了的地方，松手那一下整块会跳回来一次。
    /// 算法本身不在这儿 —— 它在 <see cref="PianoRollController.ClampMoveDelta"/>，
    /// 和命令那边的「整组一起夹」是同一份（方向键微调也要用，抄三份迟早走样）。
    /// 「拖动中不碰命令」仍然是这一片的地基（见类注释）：这里调的是控制器的纯换算，不是编辑命令。
    /// </summary>
    private void ClampMove(PianoRollController controller, ref long deltaTicks, ref int deltaPitch)
    {
        var clamped = controller.ClampMoveDelta(_group, deltaTicks, deltaPitch);
        deltaTicks = clamped.DeltaTicks;
        deltaPitch = clamped.DeltaPitch;
    }

    /// <summary>
    /// 把这一次拖动结算成一条明确的编辑意图。
    ///
    /// **一次拖动只发一次事件**（不是每帧一次）：命令那边一次命令 = 撤销栈上的一格，
    /// 拖一下记几百格的话，用户按 Ctrl+Z 得按到手酸。
    ///
    /// 位移是 0 的什么都不发 —— 那一次改的只是选中，而选中在按下的那一刻就已经改完了。
    ///
    /// **下面三个守卫看的是位移，不是手挪了多远**，所以「点击不会变成编辑」这件事
    /// 靠的是 <see cref="UpdateDrag"/> 开头那道像素门槛：手没挪够，位移就一直是 0。
    /// 别把守卫当成「抖动保护」—— 一个抢拍的音，手不动也能有非零位移（见 DragThresholdPixels）。
    /// </summary>
    private void Settle(DragKind kind)
    {
        long startDelta = _startDelta, lengthDelta = _lengthDelta;
        int pitchDelta = _pitchDelta;
        double marqueeStart = _marqueeStart, marqueeEnd = _marqueeEnd;
        int anchor = _anchor;
        long anchorStart = _anchorStart, anchorLength = _anchorLength;
        // 要挪的那组先拷出来，**必须在 ResetDrag 之前**：它清的就是 _group，
        // 清完再取就是把一条「挪 0 个音」的命令发出去
        var group = _group.ToArray();

        // 幽灵和带子先收掉：谱面要么没变（点击），要么马上要变，两种都不该继续画「将要发生」。
        // 顺手把状态清零 —— 下面这几条命令之间窗口会重画，重画时读到的必须已经是「没在拖」
        ResetDrag();
        RaisePreviewChanged();

        var controller = Controller;
        if (controller is null) return;

        switch (kind)
        {
            case DragKind.Move when startDelta != 0 || pitchDelta != 0:
                // 快照一份交给窗口：它处理完命令要重新算选中集，而算的时候会改控制器里那一串
                NotesMoved?.Invoke(this, new NoteMoveRequest(group, startDelta, pitchDelta));
                break;

            case DragKind.ResizeHead or DragKind.ResizeTail when startDelta != 0 || lengthDelta != 0:
                NoteResized?.Invoke(this, new NoteResizeRequest(
                    new NoteRef(TrackIndex, anchor), anchorStart + startDelta, anchorLength + lengthDelta));
                break;

            case DragKind.Marquee:
            {
                long from = (long)Math.Round(Math.Min(marqueeStart, marqueeEnd));
                long to = (long)Math.Round(Math.Max(marqueeStart, marqueeEnd));

                // 区间左闭右开，且 end <= start 就是空区间 —— 见 NotesInRange 的说明。
                // 于是「点了下空白」这一次零长度的手势一个音都框不到，不会误删光标底下那个音。
                // 零长度这件事由 UpdateDrag 的像素门槛保着：手一抖，起止就散开了，
                // 而这里删的是「区间里的所有音」，**不分音高** —— 那一下删掉的是别行上的音
                var hit = controller.NotesInRange(TrackIndex, from, to);
                if (hit.Count > 0) NotesDeleted?.Invoke(this, hit);
                break;
            }
        }
    }

    /// <summary>
    /// 这次拖动作废：什么都不提交，把预览和状态一起收掉。
    ///
    /// **外面也要用**：窗口每次编辑都会换一份曲子，而这条轨是就地重挂的（不是重建控件），
    /// 拖动中那一份快照（<c>_group</c>、幽灵下标、框选区间）指的全是旧曲子上的下标，
    /// 不在这儿清掉的话，下一次拖动结算出来的会是一条指着别人的命令。
    /// </summary>
    public void CancelDrag()
    {
        if (_drag == DragKind.None) return;
        ResetDrag();
        RaisePreviewChanged();
    }

    /// <summary>拖动状态清零（预览、寄存器、快照）。<c>_drag</c> 也一起回到 None。</summary>
    private void ResetDrag()
    {
        _drag = DragKind.None;
        _anchor = -1;
        _startDelta = 0;
        _lengthDelta = 0;
        _pitchDelta = 0;
        _marqueeStart = 0;
        _marqueeEnd = 0;
        _group.Clear();
        _ghostIndexes.Clear();
    }

    /// <summary>拖动预览变了，让场景重算一遍。没有订阅者时是空操作。</summary>
    private void RaisePreviewChanged() => PreviewChanged?.Invoke(this, EventArgs.Empty);

    // ==================== 光标与悬停 ====================

    /// <summary>
    /// 光标说实话：头尾给横向拉伸的箭头（那两处只改时值），身体给能抓的手型
    /// （拖得动，时间和音高两个方向），空白给默认箭头（那儿只能框选，
    /// 框选是个「划一下」的动作，用不着一个专门的手型来许诺什么）。
    ///
    /// Avalonia 没有真正的「抓起」光标（CSS 的 <c>grab</c>），手型是最接近的一个。
    /// </summary>
    private void UpdateCursor(PianoRollGeometry.RollHit hit)
    {
        Cursor = hit switch
        {
            PianoRollGeometry.RollHit.Head or PianoRollGeometry.RollHit.Tail => ResizeCursor,
            PianoRollGeometry.RollHit.Body => GrabCursor,
            _ => ArrowCursor,
        };
    }

    private void SetHover(int note)
    {
        if (note == _hoveredNote) return;
        _hoveredNote = note;
        HoverChanged?.Invoke(this, note);
    }

    private void RaiseSelectionChanged()
        => SelectionChanged?.Invoke(this, Controller?.SelectedNotes.ToArray() ?? Array.Empty<NoteRef>());

    // ==================== 落笔 ====================

    private static void DrawGrid(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene)
    {
        var barPen = new Pen(new ImmutableSolidColorBrush(palette.GridBar), 1);
        var beatPen = new Pen(new ImmutableSolidColorBrush(palette.GridBeat), 1);
        double bottom = scene.Viewport.Height;

        // +0.5 让 1px 的线落在像素中心，不糊成两像素 —— 照 RollPreviewStrip
        foreach (double x in scene.BarLines)
        {
            double cx = Math.Round(x) + 0.5;
            context.DrawLine(barPen, new Point(cx, 0), new Point(cx, bottom));
        }

        // 拍线从标尺下沿起，标尺那一格留给小节号
        foreach (double x in scene.BeatLines)
        {
            double cx = Math.Round(x) + 0.5;
            context.DrawLine(beatPen, new Point(cx, PianoRollGeometry.RulerHeight), new Point(cx, bottom));
        }
    }

    private static void DrawNote(
        DrawingContext context, TokenPalette palette, PianoRollGeometry.NoteBox box, bool selected)
    {
        // 超出可演奏范围的音标灰（判据来自 NoteMapper，见 PianoRollController）：
        // 填 ink-faint —— 它和 note 一样是「实心块」的材质，但比四周的网格线还淡，
        // 一眼就是「这块点不动」。顶边挑 line 而不是 note-edge：后者是正常音符的
        // 亮边颜色，压在灰块上会让灰音重新长出高光，两种音就看不出区别了。
        var fill = box.InRange ? palette.Note : palette.InkFaint;
        var edge = box.InRange ? palette.NoteEdge : palette.Line;

        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        context.FillRectangle(new ImmutableSolidColorBrush(fill), rect);
        context.FillRectangle(new ImmutableSolidColorBrush(edge), new Rect(box.X, box.Y, box.Width, 1));

        // 选中的那些：一圈 accent 细边，**组里每一个都描**。
        // 没有它，框选出来的那一组只有主选中（读数条上那个）看着像选中了，
        // 而拖起来整组一起动 —— 用户没法预料到底会动几个。
        if (selected)
        {
            context.DrawRectangle(
                null, new Pen(new ImmutableSolidColorBrush(palette.Accent), 1), rect);
        }
    }

    /// <summary>
    /// 拖动中的幽灵：**只描边、不填色**，而且描的是虚线。
    ///
    /// 不填色是因为它常常压在别的音（甚至它自己原来那一块）上面 ——
    /// 填一层实色就等于把底下那块盖掉，而「从哪儿挪到哪儿」正是要看这两块的关系。
    /// 虚线则是「还不作数」的记号：谱面一个字节都没变，松手之后它才真的落到那儿。
    /// </summary>
    private static void DrawGhost(
        DrawingContext context, TokenPalette palette, PianoRollGeometry.NoteBox box)
    {
        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.Accent), 1, GhostDash),
            new Rect(box.X, box.Y, box.Width, box.Height));
    }

    /// <summary>
    /// 框选那根带子。照 wireframe 的 <c>.rangebar</c>：**整条轨那么高**（纵向从标尺下沿到底），
    /// 底色 accent-soft、左右两条边是 accent。
    ///
    /// 纵向铺满是必须的：它会删掉这一段里的所有音，与音高无关 —— 画矮了就是在撒谎
    /// （只盖住两行的框，凭什么删掉第三行的音）。
    ///
    /// 为什么带子是**实底**、幽灵是虚线：两者说的不是一件事。
    /// 虚线（幽灵）说的是「还不作数，松手才变」；带子说的是「我框住了这一段」——
    /// 框在手上是真的，用户正盯着里面那几个音决定要不要松手。
    /// 填色用的是 accent-soft 补一层透明度（wireframe 的 <c>opacity:.7</c>）：
    /// 令牌是实色，透明度在画的时候配 —— 和 <c>RollNavStrip.WithAlpha</c> 一个做法，
    /// **不是颜色字面值**（基色仍然只从令牌来）。不补这层透明、实心填满，就把框里的音盖掉了。
    /// </summary>
    private static void DrawMarquee(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.MarqueeRect? marquee)
    {
        if (marquee is not { } band) return;

        context.FillRectangle(
            new ImmutableSolidColorBrush(WithAlpha(palette.AccentSoft, MarqueeOpacity)),
            new Rect(band.X, band.Y, band.Width, band.Height));

        // 左右两条边（wireframe 的 border-inline）。偏 0.5px 落在带的里侧：
        // 画在正边界上的话，这 1px 会把带子往两边各撑出去一点，和框住的那段就对不上了
        var pen = new Pen(new ImmutableSolidColorBrush(palette.Accent), 1);
        double top = band.Y, bottom = band.Y + band.Height;
        context.DrawLine(pen, new Point(band.X + 0.5, top), new Point(band.X + 0.5, bottom));
        double right = band.X + band.Width - 0.5;
        context.DrawLine(pen, new Point(right, top), new Point(right, bottom));
    }

    /// <summary>给令牌色配一个透明度。**不是颜色字面值** —— 基色仍然只从令牌来（同 RollNavStrip）。</summary>
    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    private static void DrawRulerLabels(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene)
    {
        var brush = new ImmutableSolidColorBrush(palette.InkFaint);
        // 刻度数字不追字体令牌：自绘这层的取色桥只送颜色，字体归 XAML 那一层。
        // 一两位数字的等宽与否，看不出差别。
        var typeface = Typeface.Default;

        foreach (var label in scene.BarLabels)
        {
            var text = new FormattedText(
                label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, RulerFontSize, brush);
            context.DrawText(text, new Point(label.X, (PianoRollGeometry.RulerHeight - RulerFontSize) / 2));
        }
    }

    private static void DrawPlayhead(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene,
        in PianoRollGeometry.Viewport viewport)
    {
        double x = scene.PlayheadX;
        if (double.IsNaN(x) || x < 0 || x > viewport.Width) return;

        var pen = new Pen(new ImmutableSolidColorBrush(palette.Stop), PlayheadWidth);
        context.DrawLine(pen, new Point(x, 0), new Point(x, viewport.Height));
    }

    private void OnTokensChanged(object? sender, EventArgs e) => InvalidateVisual();
}

/// <summary>
/// 用户把选中的一组音拖到了别处。<b>参数是整组 + 一个共同的位移</b>（增量，不是目标位置）——
/// 一组音保住彼此的相对关系，只有「都挪这么远」说得清（和 <c>ISongEditor.MoveNotes</c> 一个形状）。
///
/// <see cref="Notes"/> 是一份**快照**：窗口处理完这条命令之后要重新算选中集
/// （下标只在算出来的那一份曲子上有效），而算的时候会改控制器里的选中集 ——
/// 不拷一份的话，窗口手上那串会在读到一半时被换掉。
///
/// 放在命名空间这一层、而不是嵌在 <see cref="PianoRollLane"/> 里：卷帘和轨头**两边都要**喊这条
/// （<see cref="TrackLaneView.NotesMoved"/> 转发的是同一条），嵌在其中一个里面，
/// 另一个的事件签名就得写成 <c>PianoRollLane.NoteMoveRequest</c> —— 同一条契约在两个类之间来回指。
/// </summary>
public sealed record NoteMoveRequest(IReadOnlyList<NoteRef> Notes, long DeltaTicks, int DeltaPitch);

/// <summary>
/// 用户把一个音的边缘拖到了别处。收的是**绝对位置**（目标起点 + 目标时值），
/// 不是增量：拖边缘时尾巴（或起点）是钉住的，界面已经算出目标了，
/// 让命令再推一遍反而是两处各算一次。
///
/// 和 <see cref="NoteMoveRequest"/> 一样放在命名空间这一层，理由见那边。
/// </summary>
public sealed record NoteResizeRequest(NoteRef Note, long StartTick, long LengthTicks);
