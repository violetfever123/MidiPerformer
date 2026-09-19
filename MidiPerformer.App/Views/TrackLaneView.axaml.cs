using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Preview;

namespace MidiPerformer.App.Views;

/// <summary>
/// 一条轨那一整块：轨道头 + 卷帘。
///
/// 它自己不换算任何东西 —— 卷帘的宽高定下来之后，向 <see cref="PianoRollController"/>
/// 要一份视口，再向 <see cref="PianoRollPresenter"/> 要一屏要画的东西，剩下的交给
/// <see cref="PianoRollLane"/>。这就是「界面层薄」的意思：这里只有布置，没有数学。
///
/// <b>它也不改谱面。</b>轨道头上那个移调步进器只把「当前值 ± n」算出来喊一声
/// （<see cref="TransposeRequested"/>），改名、删除、卷帘上拖出来的那几种编辑、以及
/// 「抽掉一段」同样只喊一声
/// （<see cref="RenameRequested"/> / <see cref="DeleteRequested"/> / <see cref="CutRangeRequested"/> /
/// <see cref="NotesMoved"/> / <see cref="NoteResized"/>），
/// 命令由窗口去调 —— 编辑脊柱只有一条，撤销的记账在装饰器里，谁调命令都自动有撤销，
/// 但调命令的地方只该有一处。
///
/// 唯一「就地办完」的是**问用户**这件事：改名问在输入框里、删轨问在那一行小字上、
/// 抽掉一段问在轨道头第二行那两个小节号框上，
/// 三句问话都不出这个控件（也不弹对话框）—— 问完的答案才喊出去。
/// </summary>
public partial class TrackLaneView : UserControl
{
    /// <summary>一个音高行多高（像素），照 wireframe 的 <c>ROW_H = 7</c>。</summary>
    private const double RowPixels = 7;

    /// <summary>
    /// 这条轨挂在哪份曲子上。<b>不是 readonly</b>：编辑换的是一份新的 <see cref="Song"/>，
    /// 而控件是**就地重挂**的（见 <see cref="Rebind"/>），不是拆了重建。
    /// </summary>
    private PianoRollController _controller;
    private int _trackIndex;

    /// <summary>
    /// 这条轨收起来了（只留轨道头 + 一行「已折叠 · 不发声」，试听里也不响）。
    ///
    /// **状态住在这儿，不在窗口里。** 它跟着控件走，于是编辑（就地重挂）不会把它抹掉 ——
    /// 这正是 14 把「每次编辑重建控件」改掉之后白捡的一样：从前的做法下，
    /// 收起来再挪一个音，这条轨会自己弹开。
    /// 代价是**轨数真的变了**时控件要重建，这一格跟着丢 —— 窗口按轨的身份把它带过去
    /// （见 <see cref="Identity"/>），所以那一路也保得住。
    /// 换曲子（另一首）是**不带**的：那一格说的是这条轨，不是这个位置。
    /// </summary>
    private bool _collapsed;

    /// <summary>改名输入框正开着。</summary>
    private bool _renaming;

    /// <summary>「删掉这条轨？」那一问正摆着。</summary>
    private bool _confirmingDelete;

    /// <summary>「抽掉第 __ 到 第 __ 小节」那一问正摆着。</summary>
    private bool _splitting;

    /// <summary>
    /// 最近一次 <see cref="Refresh"/> 时窗口给的播放头状态。
    ///
    /// 存下来是因为**拖动中要自己重画**：卷帘只喊一声「预览变了」，它手上只有 tick，
    /// 算不出像素（那是 Presenter 的活）。重画得用和上一帧同一份播放头状态，
    /// 换个值重算的话，拖一下播放头会闪一下（拖导航条时那条红线本来就该是藏着的）。
    /// </summary>
    private long _lastPlayheadTick;
    private bool _lastPlayheadVisible;

    /// <summary>
    /// 本轨上此刻选中的音符下标。
    ///
    /// **复用同一个列表**：每帧都要填一次，每帧新建一个就是白扔的分配。
    /// 交给 Presenter 是安全的 —— 它在 <c>BuildLane</c> 里拷一份（见那边的 <c>CopyOf</c>），
    /// 场景不会留着这个缓冲区的引用。
    /// </summary>
    private readonly List<int> _selectedHere = new();

    /// <summary>给可视化设计器用的空构造。真跑起来走下面那个。</summary>
    public TrackLaneView()
    {
        InitializeComponent();
        _controller = null!;
    }

    /// <param name="controller">卷帘的脑子。</param>
    /// <param name="trackIndex">这条轨在 <c>Song.Tracks</c> 里的下标。</param>
    /// <param name="tokens">自绘取色桥。</param>
    public TrackLaneView(PianoRollController controller, int trackIndex, TokenSource tokens)
    {
        InitializeComponent();

        // 音色（16）、折叠（15）都做完了，这句话里只剩**真还没做、也还没有归属切片**的那一样：
        // 只看这条（独奏）—— 别替它许诺
        LaterText.Text = "只看这条 还没做";

        // 128 个 GM 音色一次装好。**文字走 Format**（和鼓轨那句话、和 FormatTests 盯的是同一处）：
        // 在视图里自己拼一遍「· GM n」，改一处漏一处是迟早的事
        TimbreBox.ItemsSource = Enumerable.Range(0, Format.ProgramNames.Count)
            .Select(Format.ProgramLabel)
            .ToArray();

        // 卷帘自己的尺寸一变就重算一屏。
        //
        // **这是「编辑一下音轨就消失」的一半解药。** 控件刚建出来那一帧宽度还是 0，
        // Refresh 直接返回（照 wireframe 的 `if (w < 40) return;`）；而窗口那一趟
        // LanesHost.SizeChanged 在轨数不变时**根本不会来**（高度没变），于是场景一直算不出来，
        // 卷帘上连底色都没有 —— 要等某次改窗宽、某帧播放、某次点导航才补上。
        // 挂在这儿之后，布局一落定就自己补一帧，不指望外面谁记得来喊。
        Roll.SizeChanged += (_, _) => Refresh(_lastPlayheadTick, _lastPlayheadVisible);

        Roll.Tokens = tokens;
        _controller = controller;
        Bind(trackIndex);

        // 悬停：这条轨的音符先报上去，由窗口去查读数条要的那几个数
        Roll.HoverChanged += (_, note) => HoverChanged?.Invoke(this, note);

        // 编辑意图：原样转发（形状都不动，见各个事件的说明）
        Roll.NotesMoved += (_, request) => NotesMoved?.Invoke(this, request);
        Roll.NoteResized += (_, request) => NoteResized?.Invoke(this, request);

        // 这两条不一样：它们除了往上报，还得**自己重画一屏**。
        // 选中集和拖动预览都是场景的一部分（选中圈、虚线幽灵都画在里面），
        // 而场景是这儿算出来的 —— 不重跑一次 Refresh，卷帘那边只 InvalidateVisual 会
        // 拿着旧场景再画一遍，屏幕上一点变化都没有。
        Roll.PreviewChanged += (_, _) => Refresh(_lastPlayheadTick, _lastPlayheadVisible);
        Roll.SelectionChanged += (_, notes) =>
        {
            Refresh(_lastPlayheadTick, _lastPlayheadVisible);
            SelectionChanged?.Invoke(this, notes);
        };

        // 焦点这一声同理，而且更要紧：`Refresh` 里那句 `SetFocused` 只改**这一条**的
        // 竖条和底色，上一条轨得靠窗口推一遍才会灭掉
        Roll.FocusChanged += (_, _) =>
        {
            Refresh(_lastPlayheadTick, _lastPlayheadVisible);
            FocusChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// 把这条轨的显示挂到 <see cref="_controller"/> 上、取第 <paramref name="trackIndex"/> 条。
    /// 构造和 <see cref="Rebind"/> 共用这一份 —— 两处各写一遍的话，
    /// 重挂之后迟早有一格显示停在上一份曲子上的旧数。
    /// </summary>
    private void Bind(int trackIndex)
    {
        _trackIndex = trackIndex;

        var controller = _controller;
        var track = controller.Song.Tracks[trackIndex];

        NumberText.Text = Format.TrackNumber(trackIndex + 1);
        NameText.Text = DisplayName(track.Name, trackIndex);

        // 音色那一格两样二选一（见 axaml）：旋律轨给下拉，鼓轨给一句说明。
        // 鼓轨不给下拉不是省事：9 号声道在 MIDI 里整条都是鼓组，音色号在它上面没有意义，
        // 换成一个听不出区别的号只是让人以为自己改坏了什么
        bool percussion = track.Channel == PreviewMixer.PercussionChannel;
        TimbrePicker.IsVisible = !percussion;
        TimbreText.IsVisible = percussion;
        if (percussion) TimbreText.Text = $"音色 {Format.Timbre(track.Program, track.Channel)}";
        // 下拉里选中的那一行就是曲子里那一号。**下标**就是音色号 ——
        // 这一格装的是 0..127 那 128 行，第 i 行就是 GM 第 i 号
        else TimbreBox.SelectedIndex = Math.Clamp(track.Program, 0, 127);

        TransposeText.Text = Format.Transpose(track.Transpose);
        CountText.Text = Format.NoteCount(track.Notes.Count);

        // 行高恒定（标注 5）：轨高跟着这条轨的音域走，宽音域的轨就高一些。
        // 重挂时必须重设：移调会改音域，轨高跟着变，不设的话卷帘会拿上一份的高度画
        var (low, high) = controller.PitchRangeOf(trackIndex);
        Roll.Height = PianoRollGeometry.RulerHeight + (high - low + 1) * RowPixels;
        Roll.TrackIndex = trackIndex;
        Roll.Controller = controller;

        // 聚焦那一笔也在这儿兜一次：控件刚建出来时窗口还没把控制器的聚焦设过来
        // （SyncLanes 是先造控件、再造控制器里那个值），真正对上的值由随后的
        // RefreshView 推 —— 推的还是同一个判断，重复一次不花什么，漏一次就是一条轨一直亮着
        SetFocused(controller.FocusedTrack == trackIndex);
    }

    /// <summary>
    /// 把这条轨重挂到**新的一份曲子**上（编辑之后走这条，不是拆了重建）。
    ///
    /// 窗口每次编辑都会换一份 <see cref="Song"/>，而控件和数据是一对一的：
    /// 轨数没变就地重挂，控件树、卷帘的滚动位置、焦点都不动，用户看不出中间换过一次。
    /// 从前的做法是把整摞控件拆掉重建，代价是看得见的 —— 内容高度掉到 0 时
    /// <c>ScrollViewer</c> 把滚动位置夹回顶部，新控件当帧量不出宽度于是整片空白。
    ///
    /// 轨数变了那一路仍然得重建（多一条少一条没有「就地」可言），由窗口决定。
    /// </summary>
    /// <param name="controller">照新曲子建出来的控制器。</param>
    /// <param name="trackIndex">这条轨在新曲子里的下标（本次没有轨被删时和原来一样）。</param>
    public void Rebind(PianoRollController controller, int trackIndex)
    {
        // 手上开着的那两件小事先收掉：改名框里那半截名字、删轨那一问，
        // 说的都是**上一份**曲子里的东西。从前每次编辑都换新控件，这两件顺手就没了；
        // 现在轨是复用的，得自己收 —— 不然改名框会带着一个已经作废的下标提交出去
        CancelRename();
        SetConfirmingDelete(false);
        // 抽掉那一问也一样，而且更要紧：两个框里的小节号在旧谱子上算出来的 tick
        // 在新谱子上多半还「读得通」（越界会被夹），于是会静悄悄地抽错一段
        SetSplitting(false);

        // 拖动中的预览一并作废：幽灵和框选说的都是旧下标，在新曲子上一个都对不上
        Roll.CancelDrag();

        _controller = controller;
        Bind(trackIndex);
        Refresh(_lastPlayheadTick, _lastPlayheadVisible);
    }

    /// <summary>悬停到的音符变了（-1 = 没命中）。</summary>
    public event EventHandler<int>? HoverChanged;

    /// <summary>
    /// 移调步进器被按了一下，参数是**新的绝对半音数**（不是增量）。
    ///
    /// 界面算值、窗口调命令：这样「移调是绝对赋值」这条语义只有界面这一处解释，
    /// 命令那边永远只收到一个明确的目标值。
    /// </summary>
    public event EventHandler<int>? TransposeRequested;

    /// <summary>
    /// 用户在音色下拉里挑了这条轨的音色（**和曲子里原来那一号不一样**）。参数是新的 GM 音色号。
    ///
    /// 和 <see cref="TransposeRequested"/> 一个路子：界面只喊一声，命令由窗口去调 ——
    /// 编辑脊柱只有一条。参数给的是**绝对的一号音色**，不是「换到下一号」，
    /// 换音色本来就是从一张表里挑一个。
    /// </summary>
    public event EventHandler<int>? ProgramRequested;

    /// <summary>
    /// 用户改了这条轨的名字（**已经去过两端空白，而且和原来不一样**）。参数是新名字。
    ///
    /// 空名字不会走到这儿：<c>SongEditor.RenameTrack</c> 收到空名字会**抛**
    /// （去掉两端空白总得剩下点什么），而清空输入框在用户心里也不是「我要一条没名字的轨」，
    /// 多半是「算了」——所以那一支在控件里就当取消，喊都不喊。
    /// </summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>
    /// 用户**已经确认**要删掉这条轨（按了那一问里的「删除」）。命令由窗口去调。
    ///
    /// 谁的轨看 <c>sender</c>：它就是这条轨的 <see cref="TrackLaneView"/>，
    /// <see cref="TrackIndex"/> 就在它身上 —— 窗口处理移调时用的就是这个路子
    /// （<c>sender is not TrackLaneView lane</c>），两边保持一致。
    /// </summary>
    public event EventHandler? DeleteRequested;

    /// <summary>
    /// 用户**已经确认**要抽掉这条轨上的一段（两个小节号填好了，预览那一行也看过了）。
    ///
    /// 参数是**tick**，不是小节号：命令层没有「小节」这个概念（小节线是显示层画的东西），
    /// 从两个框里读出来的小节号在 <see cref="TryReadCutSpan"/> 里当场换算成 tick ——
    /// 换算要用的两个数（一小节多少 tick、整曲共几小节）只有控制器有。
    /// 命令由窗口去调，和 <see cref="DeleteRequested"/> 一样：谁的轨看 <c>sender</c>。
    /// </summary>
    public event EventHandler<CutRangeRequest>? CutRangeRequested;

    /// <summary>卷帘上拖出来的一组音要挪（增量）。转发卷帘的原话，形状一模一样。</summary>
    public event EventHandler<NoteMoveRequest>? NotesMoved;

    /// <summary>卷帘上有一个音的起点 / 时值被拖成了新的值（绝对位置）。</summary>
    public event EventHandler<NoteResizeRequest>? NoteResized;

    /// <summary>卷帘上的选中集变了。窗口靠它刷新读数条和「选中」那一格。</summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? SelectionChanged;

    /// <summary>
    /// 卷帘上按了一下，焦点轨于是挪到了**这一条**上（卷帘的原话，见
    /// <see cref="PianoRollLane.FocusChanged"/>）。窗口靠它把别的轨的高亮灭掉。
    ///
    /// 谁的轨看 <c>sender</c>，和 <see cref="DeleteRequested"/> 那条路子一致。
    /// </summary>
    public event EventHandler? FocusChanged;

    /// <summary>
    /// 这条轨收起来 / 展开了。**窗口靠它把试听那张表重排一遍** ——
    /// 收起来的轨不出声（见 <see cref="PreviewMixer.Mix"/> 的 mutedTracks）。
    ///
    /// 和别的「改谱面」事件不一样：这一声不落到任何命令上，谱面一个字节都不动，
    /// 改的只是「这条轨响不响」。所以它不叫 Requested，也没有参数 —— 收没收到控件自己身上问
    /// （<see cref="IsCollapsed"/>）。
    /// </summary>
    public event EventHandler? CollapseChanged;

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex => _trackIndex;

    /// <summary>这条轨收起来了没有。</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>
    /// 这条轨的**身份**：<c>(轨块号, 声道)</c>，取值来自模型自己那对唯一键
    /// （见 <see cref="Track.TrackIndex"/>）。
    ///
    /// 窗口在轨数变化后要把折叠状态带到新的控件上，而<b>下标在这儿不能用</b>：
    /// 删掉第 0 条之后 <c>Song.Tracks</c> 的下标整体前移，按下标带会把折叠挪到别人身上；
    /// 这一对不重编号，删谁都还是它自己。
    /// </summary>
    public (int Chunk, int Channel) Identity
    {
        get
        {
            var track = _controller.Song.Tracks[_trackIndex];
            return (track.TrackIndex, track.Channel);
        }
    }

    // ==================== 聚焦 ====================

    /// <summary>这条轨是不是当前聚焦的那一条。只给画画用，窗口不读它（要读读控制器的）。</summary>
    private bool _focused;

    /// <summary>
    /// 亮起 / 灭掉「聚焦轨」那三笔：左边那根竖条（<c>FocusBar</c>）、轨道头底色（<c>.head.focus</c>）、
    /// 卷帘底色（推给 <see cref="PianoRollLane.Focused"/>，它自己取令牌画）。
    ///
    /// <b>不跟着折叠走。</b>收起来的轨也有活在干（展开、改名、删除、将来的分割都长在轨道头上），
    /// 聚焦停在它身上时竖条照样得在 —— 不然 Ctrl+↑/↓ 走到一条收起来的轨上，
    /// 屏幕上一个落点都没有，和按键失灵没有区别。
    /// 所以 <see cref="Refresh"/> 把这一步摆在那些「收起来了就不画」的早退<b>之前</b>，
    /// 另外由 <see cref="Bind"/> 兜一次底（控件刚建出来、还没人 Refresh 过的那一帧）。
    ///
    /// <b>一个颜色字面值都不出现。</b>三笔全走令牌：XAML 那半边的 <c>{DynamicResource}</c>
    /// 换主题时自己会跟，卷帘那半边每次重画现取。这里要是塞一个 C# 画笔进去，
    /// 主题一换它就停在旧色上不动了 —— 而这一类「只在某种主题下才看得出来」的毛病最难查。
    /// </summary>
    public void SetFocused(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;

        FocusBar.IsVisible = focused;
        Head.Classes.Set("focus", focused);
        Roll.Focused = focused;
    }

    // ==================== 折叠 ====================

    private void OnFoldClick(object? sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    /// <summary>
    /// 收起 / 展开这条轨。动的是三样：看得见的那两样（卷帘与那一行「已折叠」），
    /// 以及**试听里响不响**（收起来的轨不出声，见 <see cref="CollapseChanged"/>）。
    /// 谱面、选中集、导出一个字节都不碰 —— 折叠不是「不要它」。
    /// </summary>
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        FoldButton.Content = collapsed ? "展开" : "折叠";
        Roll.IsVisible = !collapsed;
        Strip.IsVisible = collapsed;
        // 收起来的那一条底色跟着轨号奇偶走，和卷帘的 .lane.a/.lane.b 是同一套
        Strip.Classes.Set("b", _trackIndex % 2 != 0);

        // 展开时立刻补一帧：收着的这段时间里 Refresh 一直跳过，场景还停在收起来之前那一份，
        // 而曲子可能已经在背后改过好几轮了（编辑、撤销都换过 Song）。
        // 收起那一头不用补 —— 卷帘藏了，画什么都没人看
        if (!collapsed) Refresh(_lastPlayheadTick, _lastPlayheadVisible);

        // 喊一声让窗口重排试听那张表。**先改完看得见的再喊**：窗口收到这一声时
        // 这个控件的状态已经是新的了，它回头来问 IsCollapsed 问得到对的值
        CollapseChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 展开它并滚进视野。Ctrl + ←/→ 定位到一条收起来的轨上时走这条 ——
    /// 不展开的话「跳过去了」在屏幕上一个像素的变化都没有，用户只会以为按键失灵了。
    /// </summary>
    public void Reveal()
    {
        SetCollapsed(false);
        ScrollIntoView();
    }

    /// <summary>
    /// 滚进视野，**不展开**。Ctrl+↑/↓ 换聚焦轨走这条 ——
    /// 那条路本来就跳过收起来的轨（见 <c>MainWindow.MoveFocus</c>），
    /// 所以不像 <see cref="Reveal"/> 那样需要先把一条看不见的轨掰开。
    ///
    /// 名字**不能**叫 BringIntoView：那是 <c>Control</c> 上的**扩展方法**，
    /// 而实例方法盖得住扩展方法 —— 真叫了那个名字，里面再写 <c>this.BringIntoView()</c>
    /// 调到的就是自己，一路递归到栈溢出。
    /// </summary>
    public void ScrollIntoView()
    {
        // `this.` 不能省：BringIntoView 是 ControlExtensions 上的**扩展方法**，
        // 而扩展方法只在「表达式.名字」这个形状上找 —— 光写 BringIntoView() 编译器
        // 只去类自己和基类里找，找不到就是 CS0103
        this.BringIntoView();
    }

    /// <summary>
    /// 步进器上四个按钮共用的入口：<c>Tag</c> 里是这一下要挪几个半音。
    ///
    /// 当前值从控制器手里的曲子现取 —— 每次编辑之后窗口都会把这条轨重挂一遍
    /// （<see cref="Rebind"/>，控制器换成新的），所以这个控件手上的值永远是最新的，
    /// 不会累加到一次编辑之前的旧值上。
    /// </summary>
    private void OnTransposeStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int delta)) return;

        int current = _controller.Song.Tracks[_trackIndex].Transpose;
        TransposeRequested?.Invoke(this, current + delta);
    }

    // ==================== 音色 ====================

    /// <summary>
    /// 音色下拉换了。选中项的**下标就是 GM 音色号**（第 i 行就是 i 号）。
    /// </summary>
    private void OnTimbreChanged(object? sender, SelectionChangedEventArgs e)
    {
        int program = TimbreBox.SelectedIndex;
        // 一项都没选中（清空过、或者还没装数据）时什么都不做
        if (program < 0) return;

        // 和曲子里那一号一样就当没发生 —— 这一条不是省事，是**必须**：
        // Bind 会把 SelectedIndex 设成曲子里那一号（重挂时每一次编辑都会走一趟），
        // 那一下同样会发 SelectionChanged，照喊的话每编辑一次就多记一笔「换了音色」的空账，
        // 用户按撤销会看到一串按了什么都不动的格子
        if (program == _controller.Song.Tracks[_trackIndex].Program) return;

        ProgramRequested?.Invoke(this, program);
    }

    /// <summary>当前视口尺寸下重算一屏要画的东西。窗口改宽、滚动、播放每帧都调它。</summary>
    /// <param name="playheadTick">播放头在哪。</param>
    /// <param name="playheadVisible">要不要画它。拖导航条时是 false（wireframe 标注 3）。</param>
    public void Refresh(long playheadTick, bool playheadVisible)
    {
        // 先记住「此刻」，再决定画不画：宽度还没量出来（首帧布局之前）时场景不重算，
        // 但拖动预览那一头的重画得拿这一份状态，不能因为这一帧没画就丢掉
        _lastPlayheadTick = playheadTick;
        _lastPlayheadVisible = playheadVisible;

        // 聚焦那三笔画在早退**之前**：收起来的轨、宽度还没量出来的轨，竖条和底色照样得对
        //（见 SetFocused 的说明）。挪到下面去的话，收起来的轨会一直亮着或者一直不亮
        SetFocused(_controller.FocusedTrack == _trackIndex);

        // 收起来的轨没有卷帘可画（Roll 已经藏了），算了也没人看 ——
        // 这一趟不能省掉上面那两行：展开的那一下要拿「此刻」重算一屏
        if (_collapsed) return;

        double width = Roll.Bounds.Width;
        // 还没量出来（首帧布局之前）就算了，照 wireframe 的 `if (w < 40) return;`
        if (width < 40) return;

        var track = _controller.Song.Tracks[_trackIndex];
        var viewport = _controller.ViewportOf(_trackIndex, width, Roll.Height);

        // 选中的音只有落在这一条轨上才画这圈边框 —— 「选中」跨轨，别条轨的选中在这条轨上没有落笔的地方
        _selectedHere.Clear();
        foreach (var note in _controller.SelectedNotes)
            if (note.Track == _trackIndex) _selectedHere.Add(note.Index);

        // 拖动预览 / 框选那根虚线框**每次现取**（不是按下时留一份）：
        // 它们住在卷帘的字段里，按当前指针位置一路更新的。于是「拖到一半窗口重画一屏」
        // （改窗口大小、滚动、播放的每一帧）不会把预览抹掉 —— 重算出来的还是此刻那份预览
        var overlay = new PianoRollPresenter.RollOverlay(
            playheadTick, playheadVisible, _selectedHere, Roll.DragPreview, Roll.Marquee);

        Roll.SetScene(PianoRollPresenter.BuildLane(
            track,
            viewport,
            _controller.BarCount,
            _controller.Song.TempoMap.Division.TicksPerQuarterNote,
            _controller.InRangeFlagsOf(_trackIndex),
            overlay));
    }

    // ==================== 改名 ====================

    /// <summary>
    /// 轨号兜底：没名字的轨显示成「轨 01」。
    ///
    /// 构造里那一次显示和改名时那一次比对**必须是同一个算法** ——
    /// 对不上的话，用户在「轨 01」上按回车会被当成改过一次名（其实他一个字都没改）。
    /// </summary>
    private static string DisplayName(string? name, int trackIndex)
        => string.IsNullOrWhiteSpace(name) ? $"轨 {Format.TrackNumber(trackIndex + 1)}" : name;

    private void OnRenameClick(object? sender, RoutedEventArgs e) => BeginRename();

    /// <summary>就地改成输入框：同一个位置换上去，整行不跳。</summary>
    private void BeginRename()
    {
        if (_renaming) return;

        // 删除那一问先收掉：轨道头上同时开着两件事的话，「取消」该取消哪一个说不清
        SetConfirmingDelete(false);
        SetSplitting(false);

        _renaming = true;
        NameBox.Text = NameText.Text;
        NameText.IsVisible = false;
        NameBox.IsVisible = true;
        // 改名按钮自己先藏起来：再点一下没有第二种意思，留着它只会让人以为「再点一次能取消」
        RenameButton.IsVisible = false;

        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>
    /// 输入框里的键盘：回车 = 改，Esc = 不算。
    ///
    /// 别的键（包括方向键）一律放行 —— 这里只是轨道头上的一个名字框，
    /// 不接管编曲那套键位；<c>e.Handled</c> 只在这两个键上打。
    /// </summary>
    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                CommitRename();
                break;

            case Key.Escape:
                e.Handled = true;
                CancelRename();
                break;
        }
    }

    /// <summary>焦点走了就是这一改算数（点别处、点了别的轨的改名…），和大多数软件的输入框一样。</summary>
    private void OnNameBoxLostFocus(object? sender, RoutedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        if (!_renaming) return;

        string name = NameBox.Text?.Trim() ?? string.Empty;
        // 比的是**屏幕上刚显示的那个名字**，不是模型里那个：用户看不出区别的改动不该提交
        // （轨本来没名字、屏幕上写着兜底的「轨 01」时按回车，就不该把「轨 01」写成真名字）
        string shown = NameText.Text ?? string.Empty;
        EndRename();

        if (name.Length == 0) return;
        if (string.Equals(name, shown, StringComparison.Ordinal)) return;

        RenameRequested?.Invoke(this, name);
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        EndRename();
    }

    /// <summary>
    /// 收摊：输入框藏回去，那一行名字换回来。
    ///
    /// <b>顺手把焦点清掉。</b>窗口的方向键 / 撤销那一段在**隧道阶段**接管按键，
    /// 它开头有一句「焦点在 TextBox 里就让开」—— 藏起来的输入框要是还攥着焦点，
    /// 那一句会一直让下去：方向键、Ctrl+Z 在整个窗口里静悄悄地失灵，
    /// 而且看不出是谁在挡（那个框已经不显示了）。曲名那一格没这个毛病：它从不藏。
    /// </summary>
    private void EndRename()
    {
        // 先落旗再藏：藏会引发 LostFocus，那一路也是「提交」——
        // 倒过来的话，这一次改名会被自己回调进来提交两遍
        _renaming = false;

        NameBox.IsVisible = false;
        NameText.IsVisible = true;
        RenameButton.IsVisible = true;

        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    // ==================== 删轨 ====================

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => SetConfirmingDelete(true);

    private void OnDeleteCancelClick(object? sender, RoutedEventArgs e) => SetConfirmingDelete(false);

    /// <summary>
    /// 「真的删」。
    ///
    /// 先把这一问收掉再喊命令：命令回来之后这条轨就没了，窗口会把所有轨道头重建一遍，
    /// 这个控件当场作废 —— 不收的话，那一瞬间屏幕上还留着「删掉这条轨？[删除][取消]」，
    /// 看着像没删掉。
    /// </summary>
    private void OnDeleteConfirmClick(object? sender, RoutedEventArgs e)
    {
        SetConfirmingDelete(false);
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 摆上 / 收掉那一问。
    ///
    /// 这是个**两下**的动作，不是对话框：不可逆的那一下要慢一点，但这个软件里
    /// 弹窗的代价（要有个爹窗口居中、要自己管生命周期）比多按一下大得多；
    /// 而且这里删错了 <c>Ctrl+Z</c> 就回来，多问一句只是防手滑。
    ///
    /// 状态存在控件字段里、不放在 Refresh 里重置：窗口每帧都会调一次 Refresh，
    /// 放那儿的话这一问会立刻消失。代价是**任何一次编辑**（窗口重建所有轨道头）
    /// 都会把这一问收掉 —— 那正好，选中集、视图位置在编辑之后本来就作废了。
    /// </summary>
    private void SetConfirmingDelete(bool confirming)
    {
        if (_confirmingDelete == confirming) return;

        // 反过来的那一半：摆上删轨这一问时把抽掉那一问收掉（见 BeginRename 里同一句）。
        // 只在「摆上」那一头收，收摊那一头不用管 —— 两个方法互相收也收不出环来：
        // SetSplitting(true) 里调的是 SetConfirmingDelete(false)，那一支不再回头
        if (confirming) SetSplitting(false);

        _confirmingDelete = confirming;
        DeleteButton.IsVisible = !confirming;
        DeleteConfirm.IsVisible = confirming;
    }

    /// <summary>
    /// 收掉「删掉这条轨？」那一问。窗口的 Esc 用它。
    ///
    /// 返回**刚才是不是真摆着这一问**：窗口拿它决定这一次 Esc 算不算用掉了 ——
    /// 没摆着的时候 Esc 得留给别处，不该被这儿白吞一下（现在别处没绑 Esc，但绑上的那天不会踩雷）。
    /// </summary>
    public bool CancelPendingDelete()
    {
        if (!_confirmingDelete) return false;

        SetConfirmingDelete(false);
        return true;
    }

    // ==================== 抽掉一段 ====================

    /// <summary>
    /// 两个框里读出来的那一段：
    /// <see cref="FromBar"/> / <see cref="ToBar"/> 是**夹过界、换过序之后**的小节号（给预览那一行看），
    /// <see cref="StartTick"/> / <see cref="EndTick"/> 是同一段的 tick 形状（给命令用）。
    ///
    /// 两样必须一起带出来：预览里写的「第 5–8 小节」和真发出去的 tick
    /// 得是**同一次**换算的结果，各算一遍迟早会出现「预览说 5–8、实际剪了 5–9」。
    /// </summary>
    private readonly record struct CutSpan(int FromBar, int ToBar, long StartTick, long EndTick);

    private void OnCutClick(object? sender, RoutedEventArgs e) => SetSplitting(true);

    private void OnCutCancelClick(object? sender, RoutedEventArgs e) => SetSplitting(false);

    /// <summary>真的抽。摆着的那一问先收掉，再喊命令 —— 和删轨那一下同一个次序。</summary>
    private void OnCutConfirmClick(object? sender, RoutedEventArgs e) => ConfirmCut();

    /// <summary>
    /// 两个框里的键盘：回车 = 抽，Esc = 不算。
    ///
    /// 别的键一律放行（包括左右键，在框里归光标用）—— 和改名框同一条规矩。
    /// 窗口那一层的方向键 / 撤销那一段本来就会在焦点是 <see cref="TextBox"/> 时整个让开，
    /// 所以在这儿打字不会被谁抢走。
    /// </summary>
    private void OnCutBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmCut();
                break;

            case Key.Escape:
                e.Handled = true;
                SetSplitting(false);
                break;
        }
    }

    /// <summary>改了框里的数就重算右边那一行预览。</summary>
    private void OnCutBarChanged(object? sender, TextChangedEventArgs e) => UpdateCutSummary();

    /// <summary>
    /// 焦点离开哪个框，就把那个框里写的**换成效的那个数**（夹过界、填反了换过来的那一段）。
    ///
    /// 预览那一行说的本来就是夹过之后的结果，框里却还留着 <c>0</c> 或者 <c>999</c>，
    /// 看着像「它没理我」—— 用户会以为这一刀剪的是第 999 小节。
    /// 只在失焦这一下改写：边打字边改的话，「10」打到一半会被夹成「96」，手都下不去。
    /// </summary>
    private void OnCutBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!TryReadCutSpan(out var span)) return;

        string from = Format.BarNumber(span.FromBar);
        string to = Format.BarNumber(span.ToBar);
        if (CutFromBox.Text != from) CutFromBox.Text = from;
        if (CutToBox.Text != to) CutToBox.Text = to;
        UpdateCutSummary();
    }

    /// <summary>
    /// 摆上 / 收掉「抽掉一段」那一问。
    ///
    /// 和删轨那一问一样是**两下**的动作，理由也一样（不可逆的那一下要慢一点，
    /// 而弹窗的代价比多按一下大）。不同的地方是这一问**有内容要填**，
    /// 所以它换出来的是一整行，不是一排按钮。
    ///
    /// 状态存在字段里、不放在 <see cref="Refresh"/> 里重置：窗口每帧都调一次 Refresh，
    /// 放那儿的话这一问会立刻消失。
    /// </summary>
    private void SetSplitting(bool splitting)
    {
        if (_splitting == splitting) return;

        // 轨道头上同时开着两件事的话，「取消」该取消哪一个说不清（和 BeginRename 里同一句）
        if (splitting)
        {
            CancelRename();
            SetConfirmingDelete(false);
        }

        _splitting = splitting;
        CutButton.IsVisible = !splitting;
        CutBar.IsVisible = splitting;

        if (splitting)
        {
            // 默认从**播放头所在的那一小节**起，两头都是它：用户多半是先听着不对、停在那儿，
            // 再回来剪的。填一个小节号比填两个窄，而填反了也会自动换过来
            int bar = Math.Clamp(_controller.BarOfTick(_lastPlayheadTick), 1, _controller.BarCount);
            CutFromBox.Text = Format.BarNumber(bar);
            CutToBox.Text = Format.BarNumber(bar);
            UpdateCutSummary();

            CutFromBox.Focus();
            CutFromBox.SelectAll();
        }
        else
        {
            // 藏起来的输入框要是还攥着焦点，窗口那一套快捷键会一直让着它（见 EndRename）
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }
    }

    /// <summary>
    /// 收掉「抽掉一段」那一问。窗口的 Esc 用它，和 <see cref="CancelPendingDelete"/> 成对 ——
    /// 返回值同样是「刚才是不是真摆着这一问」，好让窗口决定这次 Esc 算不算用掉了。
    ///
    /// 焦点在那个小节号框里时窗口那一层接不到 Esc（它先让给输入框），走的是框自己的
    /// <see cref="OnCutBoxKeyDown"/>；两条路都通到这儿。
    /// </summary>
    public bool CancelPendingSplit()
    {
        if (!_splitting) return false;

        SetSplitting(false);
        return true;
    }

    /// <summary>回车和「抽掉」那颗按钮共用的入口。</summary>
    private void ConfirmCut()
    {
        // 灰着的时候点不到、回车也走不到这儿；真走到了（比如读数的中间态）就当没发生 ——
        // 这一段里一个音都不用动，命令会原样还回来一份同样的曲子，白记一笔撤销
        if (!CutYesButton.IsEnabled) return;
        if (!TryReadCutSpan(out var span)) return;

        SetSplitting(false);
        CutRangeRequested?.Invoke(this, new CutRangeRequest(span.StartTick, span.EndTick));
    }

    /// <summary>
    /// 两个框 → 一段 tick。**预览那一行说的就是这里读出来的结果**。
    ///
    /// 三件事都在这儿办（用户填进来的数是「输入」，不是「意图」，得先照顾成意图）：
    /// <list type="bullet">
    /// <item>空着、或者不是数字 —— 读不出来，返回 false，预览改成一句「两个框都填上小节号」。</item>
    /// <item>越界（<c>0</c>、比总小节还大）—— 夹到 <c>[1, BarCount]</c>。用户说「到第 999 小节」
    /// 意思就是「到末尾」，为这个报错太较真。</item>
    /// <item>起点大于终点（填反了）—— 换过来。命令收到反的会**抛**（见 <c>CutRange</c> 的参数说明），
    /// 而填反是常事，不是错误：界面上就该换好再传，抛出去只是把皮球踢给了用户。</item>
    /// </list>
    ///
    /// 夹过、换过之后的两个小节号**一起**带出去，预览那一行和发出去的 tick 于是永远对得上。
    /// </summary>
    private bool TryReadCutSpan(out CutSpan span)
    {
        span = default;
        if (!TryReadBar(CutFromBox, out int from) || !TryReadBar(CutToBox, out int to)) return false;

        int barCount = _controller.BarCount;
        from = Math.Clamp(from, 1, barCount);
        to = Math.Clamp(to, 1, barCount);
        if (from > to) (from, to) = (to, from);

        // 含头含尾：从第 5 小节到第 8 小节 = [第 5 小节的起点, 第 9 小节的起点)
        long startTick = _controller.TickOfBarClamped(from - 1);
        long endTick = _controller.TickOfBarClamped(to - 1) + _controller.TicksPerBar;

        span = new CutSpan(from, to, startTick, endTick);
        return true;
    }

    /// <summary>
    /// 一个框读成一个小节号。读不出来（空、只有空白、不是整数）返回 false。
    ///
    /// 用 <c>InvariantCulture</c>：这一格装的是个纯数字，在各种区域设置下都该按同一套读 ——
    /// 换个语言就把「1,5」读成 15 或者读不出来，是最难复现的那类毛病。
    /// </summary>
    private static bool TryReadBar(TextBox box, out int bar) =>
        int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out bar);

    /// <summary>
    /// 重算右边那一行预览：**按下「抽掉」之前，先说清楚会发生什么**。
    ///
    /// 这是这一步的主心骨。抽掉一段是这个软件里唯一会改**时间轴**的编辑，
    /// 别的编辑都在原地（音高、时值、名字），结果一眼看得出来；这一刀下去，
    /// 后半截整条轨会往前挪，光看谱面根本猜不到会成什么样。
    ///
    /// 数字由 <see cref="CutPreview"/> 算 —— 那份预测和真跑一遍 <c>CutRange</c> 的结果
    /// 由 <c>CutPreviewTests</c> 里的对照测试钉在一起，所以这一行不是「大概齐」。
    ///
    /// 一个音都不会动的时候**把「抽掉」灰掉**：那时命令原样返回同一份曲子，
    /// 连撤销都不记一笔，按下去什么都不发生、还看不出为什么，比灰着更坏。
    /// </summary>
    private void UpdateCutSummary()
    {
        if (!TryReadCutSpan(out var span))
        {
            CutSummary.Text = Format.CutNeedNumbers;
            CutYesButton.IsEnabled = false;
            return;
        }

        var track = _controller.Song.Tracks[_trackIndex];
        var preview = CutPreview.Of(
            track.Notes, span.StartTick, span.EndTick, _controller.TicksPerBar);

        CutSummary.Text = Format.CutSummary(span.FromBar, span.ToBar, _trackIndex + 1, preview);
        CutYesButton.IsEnabled = preview.Changes;
    }
}

/// <summary>
/// 用户确认要从这条轨上抽掉这一段：<c>[StartTick, EndTick)</c>。
///
/// <b>装的是 tick，不是小节号。</b>「从第 5 小节到第 8 小节」那种对齐在控件里就换算完了
/// （见 <c>TrackLaneView.TryReadCutSpan</c>）—— 和 <c>ISongEditor.CutRange</c> 同一条规矩：
/// 命令层连「小节」这个概念都没有，一小节多少 tick 是显示层拿速度表算的。
///
/// 放在命名空间这一层、不嵌在 <see cref="TrackLaneView"/> 里，是跟着
/// <see cref="NoteMoveRequest"/> 那几条走：窗口要按名字接住这个类型，
/// 嵌进去就得写成 <c>TrackLaneView.CutRangeRequest</c>，同一条契约在两个类之间来回指。
/// </summary>
public sealed record CutRangeRequest(long StartTick, long EndTick);

