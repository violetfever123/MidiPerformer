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
/// 抽掉一段问在轨道头第二行的那一颗「抽掉」上（要抽的那一段在卷帘上直接拖，见
/// <see cref="PianoRollLane.ArmCut"/>），
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

    /// <summary>
    /// 名字框里正开着一次改名（焦点进来了、还没收摊）。
    ///
    /// 25 号之后名字框**常驻**，没有「开 / 关」这回事了 —— 这面旗说的是
    /// 「这一格现在归用户在打字」，两件事靠它：
    ///   · <see cref="CommitRename"/> / <see cref="CancelRename"/> 判「这一下算不算在改这条轨」
    ///     （<see cref="Rebind"/> 和抽掉一段那两处会无条件来收一收半截改名，没有它的话，
    ///     每一次编辑都会顺手把焦点从名字框上抢走）；
    ///   · 提交 / 取消之后那次 <c>LostFocus</c> 不再回头再提交一遍。
    /// </summary>
    private bool _renaming;

    /// <summary>「删掉这条轨？」那一问正摆着。</summary>
    private bool _confirmingDelete;

    /// <summary>
    /// 「抽掉一段」那一问正摆着。摆着的时候这条轨的卷帘**只划段**（见
    /// <see cref="SetSplitting"/> / <see cref="PianoRollLane.ArmCut"/>）。
    /// </summary>
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
    /// 本轨上此刻选中的音符**身份**（<see cref="NoteId"/>）。
    ///
    /// **复用同一个列表**：每帧都要填一次，每帧新建一个就是白扔的分配。
    /// 交给 Presenter 是安全的 —— 它在 <c>BuildLane</c> 里拷一份（见那边的 <c>CopyOf</c>），
    /// 场景不会留着这个缓冲区的引用。
    /// </summary>
    private readonly List<NoteId> _selectedHere = new();

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

        // 划出来的那一段变了：红带子要重画（场景是这儿算的），
        // 轨道头上那句预览也要重算 —— 两件事一起办，它们说的本来就是同一段。
        //
        // 拖动中走的是**这一条**，不是上面那条：划段的手势每帧只喊这一声
        //（见 PianoRollLane.UpdateDrag 的 Cut 一支），于是那行字跟着手走，
        // 而整屏场景一帧重算一次就够。
        Roll.CutRangeChanged += (_, _) =>
        {
            Refresh(_lastPlayheadTick, _lastPlayheadVisible);
            UpdateCutSummary();
        };

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
        // 名字框常驻，它显示的**就是**这个名字：重挂之后（换了曲子、撤销过、名字在别处改过）
        // 不一齐刷的话，这一格会停在上一份曲子 / 改名之前的那行字上
        NameBox.Text = NameText.Text;

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

        // 拖动中的预览一并作废：幽灵、框选说的都是按**上一份谱面**算出来的那一帧，
        // 而重挂之后位移的基准（按下时的 tick 与锚音长度）没有跟着重算，
        // 接着拖下去会结算出一条尺寸对不上的命令。理由完整地写在 Roll.CancelDrag 上
        Roll.CancelDrag();

        _controller = controller;
        Bind(trackIndex);
        Refresh(_lastPlayheadTick, _lastPlayheadVisible);
    }

    /// <summary>悬停到的音符变了（<see cref="NoteId.None"/> = 没命中）。</summary>
    public event EventHandler<NoteId>? HoverChanged;

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
            if (note.Track == _trackIndex) _selectedHere.Add(note.Id);

        // 拖动预览 / 框选那根虚线框**每次现取**（不是按下时留一份）：
        // 它们住在卷帘的字段里，按当前指针位置一路更新的。于是「拖到一半窗口重画一屏」
        // （改窗口大小、滚动、播放的每一帧）不会把预览抹掉 —— 重算出来的还是此刻那份预览
        var overlay = new PianoRollPresenter.RollOverlay(
            playheadTick, playheadVisible, _selectedHere, Roll.DragPreview, Roll.Marquee, Roll.CutRange);

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

    /// <summary>
    /// 焦点进了名字框：这一次改名算开始。
    ///
    /// 「进编辑」这个信号从**点了「改名」按钮**换成了**焦点进来了** —— 25 号工单把改名做成
    /// 常驻可编辑（名字框一直在那儿，和工具栏那颗 SongNameBox 同一个样子），按钮没了，
    /// 剩下的「开始」只有焦点这一件事。
    ///
    /// 全选是给鼠标用户的：改名十有八九是整条换掉、不是改中间一个字
    /// （想改中间就再点一下，那之后就是普通的编辑了）。
    /// </summary>
    private void OnNameBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (_renaming) return;

        // 删除那一问先收掉：轨道头上同时开着两件事的话，「取消」该取消哪一个说不清
        SetConfirmingDelete(false);
        SetSplitting(false);

        _renaming = true;
        // 起点是**已经落地的那个名字**，不是框里剩下的一行字：上一次没收摊的（不该有的）半截
        // 名字不该留到这一次，而 Bind 一直把这两个控件刷成同一个，这里写一遍就是把话说死
        NameBox.Text = NameText.Text;

        // 38 号之前「抽掉一段」那一问的首站是输入框，收掉它会把焦点一块儿带走；
        // 现在那儿只剩一个划段的手势（见 SetSplitting），焦点没人抢，
        // 这一句就只是把焦点落回名字框本身（本来就开着的话是个空操作）
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

    /// <summary>
    /// 焦点走了就是这一改算数（点别处、点卷帘、Tab 走开…），和大多数软件的输入框一样。
    ///
    /// 名字框常驻之后这一条比从前更要紧：没有「改名」按钮把这一次编辑圈起来了，
    /// 用户在名字上点一下、改两个字、再去点卷帘，那一下就**是**这次改名的收尾。
    /// </summary>
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
    /// 收摊：这一次改名结束了（提交了 / 取消了 / 别处把它收掉了）。
    ///
    /// 常驻的框没有「藏回去」这一步，收摊做的是**把框里的字退回已经落地的那个名字**。
    /// 这一步同时兜住两件事：
    ///   · Esc 取消 —— 半截名字不能留在框里当真的；
    ///   · 提交之后 <c>ClearFocus</c> 引出的那一次 <c>LostFocus</c>（「焦点走了算数」那一路
    ///     也是提交）—— 挡它的是上一句「先落旗」（<see cref="CommitRename"/> 见旗倒了就
    ///     直接返回）；而退回名字这一下保证框里**始终是已经落地的那个名字**。
    ///     两道合起来：提交 / 取消之后框里不会留着半截字，同一次改名也不会被提交第二遍。
    ///
    /// <b>顺手把焦点清掉。</b>窗口的方向键 / 撤销那一段在**隧道阶段**接管按键，
    /// 它开头有一句「焦点在 TextBox 里就让开」—— 名字框要是还攥着焦点，
    /// 那一句会一直让下去：方向键、Ctrl+Z 在整个窗口里静悄悄地失灵。
    /// （曲名那一格不主动放手，它一直都在，让人打完字接着改数是正常的；
    /// 这一格收摊之后就没人在看它了。）
    /// </summary>
    private void EndRename()
    {
        // 先落旗再退回名字：退回会引发 LostFocus，那一路也是「提交」——
        // 倒过来的话，这一次改名会被自己回调进来提交两遍
        _renaming = false;

        NameBox.Text = NameText.Text;

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
    /// 都会把这一问收掉 —— 那正好：这一问说的是「这一条轨的这一个下标」，
    /// 而编辑之后轨的条数可能已经变了（见 <c>ISongEditor.DeleteTrack</c> 的说明）。
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

    private void OnCutClick(object? sender, RoutedEventArgs e) => SetSplitting(true);

    private void OnCutCancelClick(object? sender, RoutedEventArgs e) => SetSplitting(false);

    /// <summary>真的抽。摆着的那一问先收掉，再喊命令 —— 和删轨那一下同一个次序。</summary>
    private void OnCutConfirmClick(object? sender, RoutedEventArgs e) => ConfirmCut();

    /// <summary>
    /// 摆上 / 收掉「抽掉一段」那一问。
    ///
    /// 和删轨那一问一样是**两下**的动作，理由也一样（不可逆的那一下要慢一点，
    /// 而弹窗的代价比多按一下大）。38 号之后它换出来的不再是一排输入框，而是**一个模式**：
    /// 摆上之后这条轨的卷帘只划段（见 <see cref="PianoRollLane.ArmCut"/>），
    /// 那一段划在哪儿、多宽，就在卷帘上直接拖 —— 用户 2026-09-20 的原话是
    /// 「原来的按小节切放弃 不要再出现填小节数字的窗口了」，以及「我希望可以某种交互手段
    /// 可以更精准的切割」。
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
            var (start, end) = CutPrefill();
            Roll.ArmCut(start, end);
        }
        else
        {
            Roll.DisarmCut();
        }

        UpdateCutSummary();
    }

    /// <summary>
    /// 装备上「抽掉一段」时先替用户划好的那一段：本轨上**选中的音**整体盖住的那一段，
    /// 一个音都没选中就退回**播放头所在的那一小节**。
    ///
    /// 两段的理由都写在各自的出处上（<c>PianoRollController.SelectionSpan</c>、
    /// 以及下面那一支）。**播放头那个数只在装备的这一下读一次**：之后播放头怎么走都不重划 ——
    /// 用户已经看见这一段了，跟着播放头跑的话它会在手底下自己挪走。
    /// </summary>
    private (long Start, long End) CutPrefill()
    {
        if (_controller.SelectionSpan(_trackIndex) is { } span) return span;

        // 播放头多半停在「听出不对」的那一刻，而那一整小节就是他想剪掉的那一段的起点。
        // 夹到曲子的范围内：播放头可能停在曲子外面（折叠让曲子变短之后，见 39 号）
        int bar = Math.Clamp(_controller.BarOfTick(_lastPlayheadTick), 1, _controller.BarCount);
        long start = _controller.TickOfBarClamped(bar - 1);
        return (start, start + _controller.TicksPerBar);
    }

    /// <summary>
    /// 收掉「抽掉一段」那一问。窗口的 Esc 用它，和 <see cref="CancelPendingDelete"/> 成对 ——
    /// 返回值同样是「刚才是不是真摆着这一问」，好让窗口决定这次 Esc 算不算用掉了。
    /// </summary>
    public bool CancelPendingSplit()
    {
        if (!_splitting) return false;

        SetSplitting(false);
        return true;
    }

    /// <summary>
    /// 把正摆着的那一问按下去（窗口的 Enter 用它，和 <see cref="CancelPendingSplit"/> 成对）。
    ///
    /// 「抽掉」那颗按钮本来就是给鼠标的，而 38 号之后**焦点多半不在任何输入框里**
    ///（划段那个手势不用打字），于是回车这一下很自然地会落到窗口那一层 ——
    /// 不接的话，键盘用户划完一段还得回来找鼠标点那颗按钮。
    ///
    /// 返回「这一下是不是归我用掉了」：没摆着那一问时返回 false，让窗口把回车让给别处。
    /// 「抽掉」灰着（这一段里没有音）时**照样算用掉** —— 屏幕上正摆着一问一答，
    /// 回车按进这一问里了，不该顺手去触发别的什么。
    /// </summary>
    public bool ConfirmPendingSplit()
    {
        if (!_splitting) return false;

        ConfirmCut();
        return true;
    }

    /// <summary>回车和「抽掉」那颗按钮共用的入口。</summary>
    private void ConfirmCut()
    {
        // 灰着的时候点不到；真走到了（比如读数的中间态）就当没发生 ——
        // 这一段里一个音都不用动，命令会原样还回来一份同样的曲子，白记一笔撤销
        if (!CutYesButton.IsEnabled) return;

        // 那一段得**先读出来**：SetSplitting(false) 会把它一起收掉（DisarmCut）
        if (Roll.CutRange is not { } range) return;

        SetSplitting(false);
        CutRangeRequested?.Invoke(this, new CutRangeRequest(range.StartTick, range.EndTick));
    }

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
    /// 还没划出范围（刚装备上、或者手一抖没挪够）时也是灰的，那一行改说「怎么划」。
    /// </summary>
    private void UpdateCutSummary()
    {
        if (Roll.CutRange is not { } range)
        {
            CutSummary.Text = Format.CutNeedRange;
            CutYesButton.IsEnabled = false;
            return;
        }

        var track = _controller.Song.Tracks[_trackIndex];
        var preview = CutPreview.Of(track.Notes, range.StartTick, range.EndTick, _controller.TicksPerBar);

        CutSummary.Text = Format.CutSummary(
            range.StartTick, range.EndTick, _trackIndex + 1, preview,
            _controller.TicksPerBar, _controller.Song.TempoMap.Division.TicksPerQuarterNote);
        CutYesButton.IsEnabled = preview.Changes;
    }
}

/// <summary>
/// 用户确认要从这条轨上抽掉这一段：<c>[StartTick, EndTick)</c>。
///
/// <b>装的是 tick，不是小节号。</b>「从第 5 小节到第 8 小节」那种对齐在**卷帘上划的那一下**里
/// 就换算完了（见 <c>PianoRollLane.ArmCut</c> 与 <c>PianoRollGeometry.SpanOf</c>，
/// 吸附到十六分格）—— 和 <c>ISongEditor.CutRange</c> 同一条规矩：
/// 命令层连「小节」这个概念都没有，一小节多少 tick 是显示层拿速度表算的。
///
/// 放在命名空间这一层、不嵌在 <see cref="TrackLaneView"/> 里，是跟着
/// <see cref="NoteMoveRequest"/> 那几条走：窗口要按名字接住这个类型，
/// 嵌进去就得写成 <c>TrackLaneView.CutRangeRequest</c>，同一条契约在两个类之间来回指。
/// </summary>
public sealed record CutRangeRequest(long StartTick, long EndTick);

