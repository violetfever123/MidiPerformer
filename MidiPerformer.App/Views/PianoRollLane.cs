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
/// 一条轨的卷帘：画的全是 <see cref="PianoRollPresenter.LaneScene"/> 里算好的东西，自己一个乘法都不做。
/// 指针交互只产出 <see cref="NotesMoved"/> / <see cref="NoteResized"/> 两种意图，命令由窗口去调；
/// 命中判定与选中集都归 <see cref="PianoRollController"/>，拖动期间不改谱面，只画预览。
/// </summary>
public sealed class PianoRollLane : Control
{
    /// <summary>刻度数字的字号。标尺高度得跟着它走，见 <see cref="PianoRollGeometry.RulerHeight"/>。</summary>
    private const double RulerFontSize = 12;

    /// <summary>播放头红线多宽，照 wireframe 的 <c>fillRect(px-1, 0, 2, h)</c>。</summary>
    private const double PlayheadWidth = 2;

    /// <summary>
    /// 选中那一圈的线宽，照 <c>prototype-编辑痕迹.html</c> 的 A。
    /// 描边骑在音符块的边上画（<see cref="DrawingContext.DrawRectangle"/> 以路径为中心、里外各一半），
    /// 音符块上下只留 1.6px 的缝（<c>NotePad</c> 0.8 × 2），往外长会吃掉 4px 把相邻两行连成一片。
    /// </summary>
    private const double SelectionStrokeWidth = 2;

    /// <summary>
    /// 幽灵的虚线节奏（画 3px、空 2px）：实心块是谱面上真有的音，虚线是松手之后会变成的样子。
    /// 框选那根带子不用它（见 <see cref="DrawMarquee"/>）。
    /// </summary>
    private static readonly ImmutableDashStyle GhostDash = new(new double[] { 3, 2 }, 0);

    /// <summary>
    /// 带子底色补的透明度，照 wireframe 的 <c>.rangebar { opacity: .7 }</c>；框选那根和「抽掉一段」那根共用。
    /// 令牌里只有实色，透明是画的时候配上去的（同 <c>RollNavStrip.WithAlpha</c>）。
    /// </summary>
    private const double MarqueeOpacity = 0.7;

    /// <summary>
    /// 三种光标，建一次用到底 —— <see cref="Cursor"/> 是 IDisposable 还带一个句柄，
    /// 每次指针移动都 new 一个的话，一次拖动就是几百个。
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
        Marquee,

        /// <summary>「抽掉一段」装备着的时候按下：在卷帘上横拖出会消失的那一段（要剪的是时间，与音高无关）。</summary>
        Cut
    }

    private TokenSource? _tokens;
    private PianoRollPresenter.LaneScene? _scene;
    private NoteId _hoveredNote = NoteId.None;

    private DragKind _drag = DragKind.None;

    /// <summary>按下时命中的那个音的身份（<see cref="Note.Id"/>）。框选时它是 <see cref="NoteId.None"/>。</summary>
    private NoteId _anchor = NoteId.None;

    /// <summary>
    /// 按下那一刻指针底下的 tick 与音高；一切位移都是相对它算的
    ///（吸附要的是「锚音原来的位置 + 原始位移」再吸一次）。
    /// 存 tick 而不是像素，是因为视口可能在拖动中途滚动（播放跟着播放头走），像素差那时就失效了。
    /// </summary>
    private double _pressTick;
    private int _pressPitch;

    /// <summary>
    /// 按下那一刻指针在控件里的位置（像素），只用来量「手挪开了没有」；位移一律走 <see cref="_pressTick"/>。
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

    /// <summary>框选的起止 tick（不吸附 —— 框说的是「我框到哪儿了」，不是「吸到哪条线上」）。</summary>
    private double _marqueeStart;
    private double _marqueeEnd;

    /// <summary>
    /// 「抽掉一段」装备着（轨道头上正摆着那一问）。装备期间这一次手势只划段：按下不选中、不挪音。
    /// </summary>
    private bool _cutArmed;

    /// <summary>
    /// 抽掉那一段的起止 tick（已吸附到格线，起止有序）。
    /// 和框选那两个不一样，它们是持久的：松手之后要留着，由 <see cref="DisarmCut"/> 清掉，<see cref="ResetDrag"/> 不碰。
    /// </summary>
    private long _cutStart;
    private long _cutEnd;

    /// <summary>这一次要动的整组音（按下那一刻的快照，见 <see cref="NoteMoveRequest"/>）。</summary>
    private readonly List<NoteRef> _group = new();

    /// <summary>其中落在本轨上的那几个的身份 —— 幽灵只画得动这一条轨上的。</summary>
    private readonly List<NoteId> _ghostIds = new();

    public PianoRollLane()
    {
        // 音符块允许画到视口外一点点（尾巴），画到控件外面就该被切掉
        ClipToBounds = true;
        Cursor = ArrowCursor;
    }

    /// <summary>做命中判定用的控制器。只用于悬停与命中，画法一个字都不从这儿取。</summary>
    public PianoRollController? Controller { get; set; }

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex { get; set; }

    private bool _focused;

    /// <summary>
    /// 这是不是当前聚焦的那条轨（Ctrl+↑/↓ 走的那个光标，见 <see cref="PianoRollController.FocusedTrack"/>）。只影响这一条的底色。
    /// 令牌都是实色，所以聚焦时整条换成 <c>LaneFocus</c>，不在奇偶底色上再压一层。
    /// </summary>
    public bool Focused
    {
        get => _focused;
        set
        {
            if (_focused == value) return;
            _focused = value;
            InvalidateVisual();
        }
    }

    /// <summary>悬停到的音符变了。参数是那个音的身份，<see cref="NoteId.None"/> = 移开了或没命中。</summary>
    public event EventHandler<NoteId>? HoverChanged;

    /// <summary>这一组音被拖到了别处，命令由窗口去调（一次拖动只发一次）。</summary>
    public event EventHandler<NoteMoveRequest>? NotesMoved;

    /// <summary>这个音的时值（和/或起点）被拖成了新的值，命令由窗口去调。</summary>
    public event EventHandler<NoteResizeRequest>? NoteResized;

    /// <summary>选中集变了（点中一个音、框住一段、或空白处按下清空）。窗口靠它刷新读数条。</summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? SelectionChanged;

    /// <summary>
    /// 聚焦轨挪到这条轨上来了（按下时手落在这一条上，见 <see cref="OnPointerPressed"/>），
    /// 只在这条轨本来不是聚焦轨时发。
    /// 窗口收到它把整窗的底色推一遍；悬浮不发这条，焦点得有明确的动作（按下、或 Ctrl+↑/↓）才算数。
    /// </summary>
    public event EventHandler? FocusChanged;

    /// <summary>
    /// 拖动预览变了（幽灵挪了、带子宽了），这一屏要重算一遍才画得出来 —— 场景是 <c>TrackLaneView</c> 那边算的。
    /// </summary>
    public event EventHandler? PreviewChanged;

    /// <summary>
    /// 被拖的那几个音松手之后会落在哪。没在拖、或者还没有位移就是 null
    ///（给全 0 的预览的话，幽灵和原音符完全重合，画出来像坏了）。
    /// </summary>
    public PianoRollPresenter.DragPreview? DragPreview
    {
        get
        {
            if (_drag is not (DragKind.Move or DragKind.ResizeHead or DragKind.ResizeTail)) return null;
            if (_startDelta == 0 && _lengthDelta == 0 && _pitchDelta == 0) return null;
            if (_ghostIds.Count == 0) return null;

            // 把活列表交出去是安全的：它只在这次 BuildLane 里被读一遍，场景不留引用
            return new PianoRollPresenter.DragPreview(_ghostIds, _startDelta, _lengthDelta, _pitchDelta);
        }
    }

    /// <summary>此刻框住的那一段。没在框选、或者还没拖出宽度（按下没动）就是 null。</summary>
    public PianoRollPresenter.MarqueeRange? Marquee
        => _drag == DragKind.Marquee && _marqueeStart != _marqueeEnd
            ? new PianoRollPresenter.MarqueeRange(
                (long)Math.Round(_marqueeStart), (long)Math.Round(_marqueeEnd))
            : null;

    /// <summary>
    /// 装备着「抽掉一段」时，此刻划出来的那一段。没装备、或者还没拖出宽度就是 null。
    /// 松手之后照样有值，这一点和 <see cref="Marquee"/> 不一样。
    /// </summary>
    public PianoRollPresenter.MarqueeRange? CutRange
        => _cutArmed && _cutStart != _cutEnd
            ? new PianoRollPresenter.MarqueeRange(_cutStart, _cutEnd)
            : null;

    /// <summary>
    /// 装备上「抽掉一段」：整条卷帘从此只划段，并且先替用户划好一段（范围由调用方算好给进来）。
    /// </summary>
    public void ArmCut(long startTick, long endTick)
    {
        _cutArmed = true;
        if (startTick > endTick) (startTick, endTick) = (endTick, startTick);
        _cutStart = startTick;
        _cutEnd = endTick;

        InvalidateVisual();
        RaiseCutRangeChanged();
    }

    /// <summary>
    /// 收掉「抽掉一段」：划好的那一段一起丢掉，卷帘回到平时那个意思。没装备时什么都不做，也不喊。
    /// </summary>
    public void DisarmCut()
    {
        if (!_cutArmed) return;

        _cutArmed = false;
        _cutStart = 0;
        _cutEnd = 0;
        // 手上正划着的那一次也作废：它划的是「已经收掉的那一问」里的一段
        if (_drag == DragKind.Cut) _drag = DragKind.None;

        InvalidateVisual();
        RaiseCutRangeChanged();
    }

    /// <summary>
    /// 划出来的那一段变了（装备上、拖动中、收掉），轨道头上那句预览要重算。
    /// 和 <see cref="PreviewChanged"/> 分开：这一声只让一行字重算，那一声让整条轨重算一屏场景。
    /// </summary>
    public event EventHandler? CutRangeChanged;

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

        // 轨道底色铺满整条，顺带给鼠标一个能命中的面 —— 悬停落在空白处也算「移开了」。
        // 深浅按轨号奇偶交替（wireframe 的 .lane.a / .lane.b），聚焦的那一条整条换成 LaneFocus。
        var background = Focused
            ? palette.LaneFocus
            : TrackIndex % 2 == 0 ? palette.LaneA : palette.LaneB;
        context.FillRectangle(new ImmutableSolidColorBrush(background), new Rect(Bounds.Size));

        // 音高行的黑键底纹和行分隔线都不画

        DrawGrid(context, palette, scene);

        // 音符块：压在网格上面，播放头再压在音符上面
        foreach (var box in scene.Notes)
        {
            DrawNote(context, palette, box, scene.SelectedNotes.Contains(box.Id));
        }

        // 幽灵压在真音符上面：底下那一块不动，一实一虚摆在一起就是「现在在哪、松手去哪儿」
        foreach (var ghost in scene.GhostNotes)
        {
            DrawGhost(context, palette, ghost);
        }

        DrawMarquee(context, palette, scene.Marquee);
        DrawCutBand(context, palette, scene.CutBand);

        // 标尺下沿那条横线：刻度区到此为止，照 wireframe 的 `moveTo(0, RULER_H + .5)`。
        // 颜色用 Line 的下一档 InkFaint，线宽仍是 1px —— 要的是边界明确，不是一条抢注意力的粗杠。
        context.DrawLine(
            new Pen(new ImmutableSolidColorBrush(palette.InkFaint), 1),
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 上一次拖动没收拾干净（捕获丢过、控件刚被换过）就地收掉，别让两次拖动叠在一起
        CancelDrag();

        var viewport = scene.Viewport;
        var point = e.GetPosition(this);

        _pressTick = PianoRollGeometry.TickAtX(viewport, point.X);
        _pressPitch = PianoRollGeometry.PitchAtY(viewport, point.Y);
        _pressPoint = point;

        // 先抓住指针：拖到控件外面松手（或者窗口中途重画把这条轨换掉）也要收得到消息。
        // 放在改选中之前 —— 改选中会让窗口重画一屏，那一趟里控件被换掉捕获就丢了
        e.Pointer.Capture(this);
        e.Handled = true;

        // 手落在哪条轨上，焦点就跟到哪条轨上（见 FocusChanged）。摆在这里的理由同上：挪焦点也会让窗口重画一屏
        bool focusChanged = controller.SetFocusedTrack(TrackIndex);

        // 「抽掉一段」装备着的时候，整条卷帘只有这一个意思：按下就是重新划一段，命中判定与选中集一概不参与
        if (_cutArmed)
        {
            _drag = DragKind.Cut;
            _cutStart = _cutEnd = PianoRollGeometry.SnapToGrid(_pressTick, controller.GridTicks);

            UpdateCursor(PianoRollGeometry.RollHit.None);
            InvalidateVisual();
            // 叫一声把那一行预览说回「先拖一段」：上一次划的那一段已经被这一下清掉了
            RaiseCutRangeChanged();
            if (focusChanged) FocusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var hit = controller.HitTestRef(TrackIndex, viewport, point.X, point.Y, out var note);

        bool selectionChanged = false;

        switch (hit)
        {
            case PianoRollGeometry.RollHit.Head:
            case PianoRollGeometry.RollHit.Tail:
                // 拖边缘是直接操作，不需要先选中 —— 拉成什么样由幽灵说
                _drag = hit == PianoRollGeometry.RollHit.Head ? DragKind.ResizeHead : DragKind.ResizeTail;
                _anchor = note.Id;
                CaptureAnchor(controller);
                _ghostIds.Add(note.Id);
                break;

            case PianoRollGeometry.RollHit.Body:
                _drag = DragKind.Move;
                _anchor = note.Id;
                CaptureAnchor(controller);

                // Shift 点一下 = 把它也带上（多选的另一条路是空白处横拖框一段）。
                // ExtendSelection 对已经在里面的音什么都不做，正合这里的语义：点一个已选中的音意思是「留着它」
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    int before = controller.SelectedNotes.Count;
                    controller.ExtendSelection(note);
                    // 选中集没变就别喊：喊一声窗口就白重画一屏
                    selectionChanged = controller.SelectedNotes.Count != before;
                }
                // 不在选中集里就先只选它一个；已经在里面就整组留着 —— 用户拖的是那一组
                else if (!controller.IsSelected(note))
                {
                    controller.SelectOnly(note);
                    selectionChanged = true;
                }

                // 要动的那组在按下这一刻快照一份 —— 松手时窗口可能已经动过选中集了
                _group.Clear();
                _group.AddRange(controller.SelectedNotes);
                foreach (var item in _group)
                    if (item.Track == TrackIndex) _ghostIds.Add(item.Id);
                break;

            default:
                // 空白处按下 = 框选，选中集当场清掉：这一次手势说的就是「从现在开始算」
                controller.ClearSelection();
                selectionChanged = true;
                _drag = DragKind.Marquee;
                _marqueeStart = _marqueeEnd = _pressTick;
                break;
        }

        UpdateCursor(hit);
        InvalidateVisual();

        // 选中的那一声先喊，焦点那一声摆后面 —— 它引起的那次重画画的已经是最终的选中集
        if (selectionChanged) RaiseSelectionChanged();
        if (focusChanged) FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ==================== 指针：拖动 ====================

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_drag != DragKind.None)
        {
            // 拖动中不报悬停：读数条说的是模型里的那个音，顺着指针报的话，
            // 指针一跑出那个音的方块（方块自己没动）读数条就空了，看着像「拖丢了」
            UpdateDrag(e.GetPosition(this));
            return;
        }

        if (_scene is not { } scene || Controller is not { } controller) return;

        var point = e.GetPosition(this);
        var hit = controller.HitTestRef(TrackIndex, scene.Viewport, point.X, point.Y, out var note);
        UpdateCursor(hit);
        SetHover(hit == PianoRollGeometry.RollHit.None ? NoteId.None : note.Id);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // 拖动中指针跑出控件是常事，这时清悬停会让读数条在用户还在拖的时候突然空掉
        if (_drag != DragKind.None) return;

        SetHover(NoteId.None);
    }

    // ==================== 指针：抬起 ====================

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None) return;

        // 先把状态落定再放开捕获：Capture(null) 会回调到 CaptureLost，状态还在的话刚算出的位移会被它清掉
        var kind = _drag;
        _drag = DragKind.None;

        e.Pointer.Capture(null);
        Settle(kind);
    }

    /// <summary>
    /// 捕获丢了（窗口中途重建、系统弹窗抢走指针、控件被移出可视树…）。这一次拖动不作数 ——
    /// 拖到一半被打断，谁也不知道用户本来要拖到哪儿，谱面一个字节都没动。
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    // ==================== 拖动 ====================

    /// <summary>
    /// 把锚音在模型里的位置记下来 —— 吸附是按「锚的起点 + 原始位移」算的，得有个起点。
    /// 按身份扫一遍找它、不存下标：下一次编辑之后那个下标可能已经指着别人了。
    /// </summary>
    private void CaptureAnchor(PianoRollController controller)
    {
        foreach (var note in controller.Song.Tracks[TrackIndex].Notes)
        {
            if (note.Id != _anchor) continue;
            _anchorStart = note.StartTick;
            _anchorLength = note.LengthTicks;
            return;
        }
    }

    /// <summary>
    /// 指针动了，把预览的位移重算一遍。这里不改谱面，真正的编辑在 <see cref="Settle"/> 里一次交出去。
    /// </summary>
    private void UpdateDrag(Point point)
    {
        if (_scene is not { } scene || Controller is not { } controller) return;

        // 视口每次现取：播放中视图会跟着播放头滚，拿旧视口算出来的落点会整体偏掉一个滚动量
        var viewport = scene.Viewport;

        // 手还没挪够一个门槛 —— 这一下是「点」，不是「拖」：几个位移一起退回按下那一刻的样子，
        // 松手时 Settle 里的守卫于是全都拦得住（不会因为 1px 手抖把一个抢拍的音吸回格线）。
        // 退出前照样喊一声重画：拖出去又拖回门槛以内的话，幽灵和带子得跟着收掉
        if (!PianoRollGeometry.ExceedsDragThreshold(
                point.X - _pressPoint.X, point.Y - _pressPoint.Y))
        {
            _startDelta = 0;
            _lengthDelta = 0;
            _pitchDelta = 0;
            _marqueeEnd = _marqueeStart;

            // 划段这一支也要退回去，留着的话「抽掉」会照着一个手已经收回来的宽度剪下去
            if (_drag == DragKind.Cut)
            {
                _cutEnd = _cutStart;
                RaiseCutRangeChanged();
                return;
            }

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
                // 吸附相对锚音原来的位置算：一个抢拍的音第一次拖动会被吸回最近的格线
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
                // 只改时值、起点钉住；吸的是时值不是尾巴的绝对位置，否则一个抢拍的音会被拽回格线
                long length = PianoRollGeometry.SnapToGrid(_anchorLength + moved, grid);
                if (length < grid) length = grid;   // 时值最小一格

                _startDelta = 0;
                _lengthDelta = length - _anchorLength;
                break;
            }

            case DragKind.ResizeHead:
            {
                // 起点和时值一起改、尾巴钉住：新起点 + 新时值 == 老尾巴
                long tail = _anchorStart + _anchorLength;
                long start = PianoRollGeometry.SnapToGrid(_anchorStart + moved, grid);

                // 最多拉到尾巴前一格（给「时值至少一格」留位置），最少到谱面开头 —— 负 tick 不存在
                if (start > tail - grid) start = tail - grid;
                if (start < 0) start = 0;

                _startDelta = start - _anchorStart;
                _lengthDelta = -_startDelta;
                break;
            }

            case DragKind.Marquee:
                // 框选不吸附：框说的是「我框到哪儿了」，吸附会让框住的音和画出来的框对不上
                _marqueeEnd = rawTick;
                break;

            case DragKind.Cut:
            {
                // 划段要吸附（和框选正相反）：这一段要交给命令，两端吸到十六分格上，剪完之后的音还在拍上
                var span = PianoRollGeometry.SpanOf(_pressTick, rawTick, grid);
                if (span is { } cut) (_cutStart, _cutEnd) = (cut.Start, cut.End);
                else _cutEnd = _cutStart;   // 还没挪过半格：这一段是空的

                RaiseCutRangeChanged();
                return;
            }

            default:
                return;
        }

        RaisePreviewChanged();
    }

    /// <summary>
    /// 把整组的位移夹到合法范围内 —— 预览画到命令去不了的地方，松手那一下整块会跳回来。
    /// 算法在 <see cref="PianoRollController.ClampMoveDelta"/>，和命令那边是同一份。
    /// </summary>
    private void ClampMove(PianoRollController controller, ref long deltaTicks, ref int deltaPitch)
    {
        var clamped = controller.ClampMoveDelta(_group, deltaTicks, deltaPitch);
        deltaTicks = clamped.DeltaTicks;
        deltaPitch = clamped.DeltaPitch;
    }

    /// <summary>
    /// 把这一次拖动结算成一条明确的编辑意图。一次拖动只发一次事件（命令那边一次命令 = 撤销栈上一格）；
    /// 位移是 0 的什么都不发 —— 那一次改的只是选中，而选中在按下的那一刻就已经改完了。
    /// 下面几个守卫看的是位移不是手挪了多远，「点击不会变成编辑」靠的是 <see cref="UpdateDrag"/> 开头那道像素门槛。
    /// </summary>
    private void Settle(DragKind kind)
    {
        long startDelta = _startDelta, lengthDelta = _lengthDelta;
        int pitchDelta = _pitchDelta;
        double marqueeStart = _marqueeStart, marqueeEnd = _marqueeEnd;
        var anchor = _anchor;
        long anchorStart = _anchorStart, anchorLength = _anchorLength;
        // 要挪的那组必须在 ResetDrag 之前拷出来 —— 它清的就是 _group
        var group = _group.ToArray();

        // 幽灵和带子先收掉，状态一并清零 —— 下面几条命令之间窗口会重画，那时读到的必须是「没在拖」
        ResetDrag();
        RaisePreviewChanged();

        var controller = Controller;
        if (controller is null) return;

        switch (kind)
        {
            case DragKind.Move when startDelta != 0 || pitchDelta != 0:
                // 快照一份交给窗口：窗口收到它之后会重建控制器，而 _group 是这条控件的活字段，那一趟里可能被清掉
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

                // 区间左闭右开，end <= start 就是空区间（见 NotesInRange 的说明）：零长度的手势一个音都框不到，
                // 而选中集在按下那一刻已经清空了，于是这一次手势什么都不做。
                // 框住只选中、不删（删在窗口那侧绑 Delete / Backspace），而且不分音高 —— 区间只按时间命中
                controller.SetSelection(controller.NotesInRange(TrackIndex, from, to));
                RaiseSelectionChanged();
                break;
            }

            // 划段结算成什么也不发：那一段已经落在 _cutStart/_cutEnd 上了，动谱面的是「抽掉」那颗按钮
            case DragKind.Cut:
                break;
        }
    }

    /// <summary>
    /// 这次拖动作废：什么都不提交，把预览和状态一起收掉。窗口每次编辑（就地重挂）也会来收一遍 ——
    /// 手上的位移基准没有跟着新曲子重算，接着拖会结算出一条尺寸对不上的命令。
    /// 划段那一次按「作废」处理，那一段收成零宽。
    /// </summary>
    public void CancelDrag()
    {
        if (_drag == DragKind.None) return;

        bool wasCut = _drag == DragKind.Cut;
        ResetDrag();

        if (wasCut)
        {
            _cutEnd = _cutStart;
            RaiseCutRangeChanged();
            return;
        }

        RaisePreviewChanged();
    }

    /// <summary>
    /// 拖动状态清零（预览、寄存器、快照），<c>_drag</c> 也一起回到 None。
    /// 不碰 <c>_cutArmed</c> / <c>_cutStart</c> / <c>_cutEnd</c> —— 那是「抽掉一段」那一问的状态，由 <see cref="DisarmCut"/> 收。
    /// </summary>
    private void ResetDrag()
    {
        _drag = DragKind.None;
        _anchor = NoteId.None;
        _startDelta = 0;
        _lengthDelta = 0;
        _pitchDelta = 0;
        _marqueeStart = 0;
        _marqueeEnd = 0;
        _group.Clear();
        _ghostIds.Clear();
    }

    /// <summary>拖动预览变了，让场景重算一遍。没有订阅者时是空操作。</summary>
    private void RaisePreviewChanged() => PreviewChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>划出来的那一段变了，轨道头上那句预览要重算一遍。没有订阅者时是空操作。</summary>
    private void RaiseCutRangeChanged() => CutRangeChanged?.Invoke(this, EventArgs.Empty);

    // ==================== 光标与悬停 ====================

    /// <summary>
    /// 光标按命中位置换：头尾给横向拉伸的箭头（那两处只改时值），身体给能抓的手型，空白给默认箭头。
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

    private void SetHover(NoteId note)
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
        // 超出可演奏范围的音标灰（判据来自 NoteMapper）：填 ink-faint，顶边挑 line 而不是 note-edge，
        // 否则灰音会重新长出正常音符的亮边，两种音就看不出区别了
        var fill = box.InRange ? palette.Note : palette.InkFaint;
        var edge = box.InRange ? palette.NoteEdge : palette.Line;

        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        context.FillRectangle(new ImmutableSolidColorBrush(fill), rect);
        context.FillRectangle(new ImmutableSolidColorBrush(edge), new Rect(box.X, box.Y, box.Width, 1));

        // 选中的那些：一圈 accent 细边，组里每一个都描 —— 拖起来是整组一起动，只描一个看不出会动几个
        if (selected)
        {
            context.DrawRectangle(
                null, new Pen(new ImmutableSolidColorBrush(palette.Accent), SelectionStrokeWidth),
                rect);
        }
    }

    /// <summary>
    /// 拖动中的幽灵：只描边、不填色，而且描的是虚线。
    /// 不填色是因为它常常压在别的音（甚至它自己原来那一块）上面，填实色就把底下那块盖掉了，
    /// 而「从哪儿挪到哪儿」正要看这两块的关系。
    /// </summary>
    private static void DrawGhost(
        DrawingContext context, TokenPalette palette, PianoRollGeometry.NoteBox box)
    {
        context.DrawRectangle(
            null, new Pen(new ImmutableSolidColorBrush(palette.Accent), 1, GhostDash),
            new Rect(box.X, box.Y, box.Width, box.Height));
    }

    /// <summary>
    /// 框选那根带子，照 wireframe 的 <c>.rangebar</c>：整条轨那么高（纵向从标尺下沿到底），底色 accent-soft。
    /// 纵向铺满是因为它框住的是这一段里的所有音，与音高无关；实底区别于幽灵的虚线，说的是「我框住了这一段」。
    /// </summary>
    private static void DrawMarquee(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.MarqueeRect? marquee)
    {
        if (marquee is not { } band) return;

        context.FillRectangle(
            new ImmutableSolidColorBrush(WithAlpha(palette.AccentSoft, MarqueeOpacity)),
            new Rect(band.X, band.Y, band.Width, band.Height));

        // 左右两条边；偏 0.5px 落在带的里侧，画在正边界上的话带子会比框住的那段宽出 1px
        var pen = new Pen(new ImmutableSolidColorBrush(palette.Accent), 1);
        double top = band.Y, bottom = band.Y + band.Height;
        context.DrawLine(pen, new Point(band.X + 0.5, top), new Point(band.X + 0.5, bottom));
        double right = band.X + band.Width - 0.5;
        context.DrawLine(pen, new Point(right, top), new Point(right, bottom));
    }

    /// <summary>给令牌色配一个透明度。基色仍然只从令牌来（同 RollNavStrip）。</summary>
    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    /// <summary>红带子那两条竖边的宽度（像素），比框选那根粗一倍 —— 见 <see cref="DrawCutBand"/>。</summary>
    private const double CutEdgeWidth = 2;

    /// <summary>
    /// 「抽掉一段」正划着的那一段：红色的带子，形状和框选那根一模一样（同一个 <c>MarqueeOf</c> 算出来的像素）。
    /// 红的是令牌里的 <c>StopSoft</c> / <c>Stop</c>，说的是「这一段会消失」，和蓝色的「选中了这一段」正好相反。
    /// 边比框选那根粗一倍（2px 对 1px）：红带子多半窄得多，1px 的边在那么窄的一条上会吃掉大半宽度。
    /// </summary>
    private static void DrawCutBand(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.MarqueeRect? band)
    {
        if (band is not { } cut) return;

        context.FillRectangle(
            new ImmutableSolidColorBrush(WithAlpha(palette.StopSoft, MarqueeOpacity)),
            new Rect(cut.X, cut.Y, cut.Width, cut.Height));

        var pen = new Pen(new ImmutableSolidColorBrush(palette.Stop), CutEdgeWidth);
        double top = cut.Y, bottom = cut.Y + cut.Height;
        // 和框选那根同一个偏法：边画在带的里侧，带子不会比划出来的那一段宽
        context.DrawLine(pen, new Point(cut.X + 1, top), new Point(cut.X + 1, bottom));
        double right = cut.X + cut.Width - 1;
        context.DrawLine(pen, new Point(right, top), new Point(right, bottom));
    }

    private static void DrawRulerLabels(
        DrawingContext context, TokenPalette palette, PianoRollPresenter.LaneScene scene)
    {
        // 颜色用 Ink（正文那一档）：刻度数字是读数，不必跟网格线去抢那几档浅色
        var brush = new ImmutableSolidColorBrush(palette.Ink);
        // 字重加粗、不换字族：小字号下细体会糊掉，而换字族会让数字宽度和对齐都变（落点是按字符宽估的）
        var typeface = new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold);

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
/// 用户把选中的一组音拖到了别处。参数是整组 + 一个共同的位移（增量，不是目标位置）。
/// <see cref="Notes"/> 是一份快照：卷帘交出来的是它自己的活字段，而窗口处理这条命令时会重建控制器。
/// </summary>
public sealed record NoteMoveRequest(IReadOnlyList<NoteRef> Notes, long DeltaTicks, int DeltaPitch);

/// <summary>
/// 用户把一个音的边缘拖到了别处。收的是绝对位置（目标起点 + 目标时值），不是增量 ——
/// 拖边缘时尾巴（或起点）是钉住的，界面已经算出目标了。
/// </summary>
public sealed record NoteResizeRequest(NoteRef Note, long StartTick, long LengthTicks);
