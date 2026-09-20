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
/// 一条轨那一整块：轨道头 + 卷帘。视口向 <see cref="PianoRollController"/> 要、一屏要画的东西向
/// <see cref="PianoRollPresenter"/> 要，剩下的交给 <see cref="PianoRollLane"/>。
/// 它不改谱面，编辑只喊一声由窗口去调命令；唯一就地办完的是问用户（改名 / 删轨 / 抽掉一段）。
/// </summary>
public partial class TrackLaneView : UserControl
{
    /// <summary>一个音高行多高（像素），照 wireframe 的 <c>ROW_H = 7</c>。</summary>
    private const double RowPixels = 7;

    /// <summary>
    /// 这条轨挂在哪份曲子上。不是 readonly：编辑换的是一份新的 <see cref="Song"/>，
    /// 而控件是就地重挂的（见 <see cref="Rebind"/>），不是拆了重建。
    /// </summary>
    private PianoRollController _controller;
    private int _trackIndex;

    /// <summary>
    /// 这条轨收起来了（只留轨道头 + 一行「已折叠 · 不发声」，试听里也不响）。
    /// 状态跟着控件走，就地重挂抹不掉它；轨数真的变了控件会重建，窗口按 <see cref="Identity"/> 把它带过去。
    /// </summary>
    private bool _collapsed;

    /// <summary>
    /// 名字框里正开着一次改名（焦点进来了、还没收摊）：<see cref="CommitRename"/> / <see cref="CancelRename"/> 靠它判断，
    /// 提交 / 取消之后那次 <c>LostFocus</c> 也靠它不再回头提交一遍。
    /// </summary>
    private bool _renaming;

    /// <summary>「删掉这条轨？」那一问正摆着。</summary>
    private bool _confirmingDelete;

    /// <summary>「抽掉一段」那一问正摆着，摆着时这条轨的卷帘只划段（见 <see cref="PianoRollLane.ArmCut"/>）。</summary>
    private bool _splitting;

    /// <summary>最近一次 <see cref="Refresh"/> 时窗口给的播放头状态。拖动中要自己重画一屏，得用和上一帧同一份。</summary>
    private long _lastPlayheadTick;
    private bool _lastPlayheadVisible;

    /// <summary>
    /// 本轨上此刻选中的音符身份（<see cref="NoteId"/>）。复用同一个列表 —— 每帧都要填一次，
    /// 交给 Presenter 是安全的：它在 <c>BuildLane</c> 里拷一份。
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

        // 128 个 GM 音色一次装好，文字走 Format —— 自己在视图里拼一遍「· GM n」，改一处漏一处是迟早的事
        TimbreBox.ItemsSource = Enumerable.Range(0, Format.ProgramNames.Count)
            .Select(Format.ProgramLabel)
            .ToArray();

        // 卷帘自己的尺寸一变就重算一屏：控件刚建出来那一帧宽度还是 0，Refresh 会直接返回，
        // 而窗口那边在轨数不变时不会来 —— 挂在这儿才能让布局一落定就自己补一帧
        Roll.SizeChanged += (_, _) => Refresh(_lastPlayheadTick, _lastPlayheadVisible);

        Roll.Tokens = tokens;
        _controller = controller;
        Bind(trackIndex);

        // 悬停：这条轨的音符先报上去，由窗口去查读数条要的那几个数
        Roll.HoverChanged += (_, note) => HoverChanged?.Invoke(this, note);

        // 编辑意图：原样转发
        Roll.NotesMoved += (_, request) => NotesMoved?.Invoke(this, request);
        Roll.NoteResized += (_, request) => NoteResized?.Invoke(this, request);

        // 这两条不一样：除了往上报还得自己重画一屏 —— 选中集和拖动预览都是这儿算出来的场景的一部分
        Roll.PreviewChanged += (_, _) => Refresh(_lastPlayheadTick, _lastPlayheadVisible);

        // 划出来的那一段变了：红带子和轨道头上那句预览一起重算，划段的手势每帧只喊这一声
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

        // 焦点这一声同理：Refresh 里的 SetFocused 只改这一条，上一条轨得靠窗口推一遍才会灭掉
        Roll.FocusChanged += (_, _) =>
        {
            Refresh(_lastPlayheadTick, _lastPlayheadVisible);
            FocusChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>把这条轨的显示挂到 <see cref="_controller"/> 上、取第 <paramref name="trackIndex"/> 条。构造和 <see cref="Rebind"/> 共用这一份。</summary>
    private void Bind(int trackIndex)
    {
        _trackIndex = trackIndex;

        var controller = _controller;
        var track = controller.Song.Tracks[trackIndex];

        NumberText.Text = Format.TrackNumber(trackIndex + 1);
        NameText.Text = DisplayName(track.Name, trackIndex);
        // 名字框常驻，它显示的就是这个名字：不一齐刷的话，这一格会停在改名之前的那行字上
        NameBox.Text = NameText.Text;

        // 音色那一格两样二选一（见 axaml）：旋律轨给下拉，鼓轨给一句说明 ——
        // 9 号声道在 MIDI 里整条都是鼓组，音色号在它上面没有意义
        bool percussion = track.Channel == PreviewMixer.PercussionChannel;
        TimbrePicker.IsVisible = !percussion;
        TimbreText.IsVisible = percussion;
        if (percussion) TimbreText.Text = $"音色 {Format.Timbre(track.Program, track.Channel)}";
        // 下拉里选中的那一行就是曲子里那一号：第 i 行就是 GM 第 i 号
        else TimbreBox.SelectedIndex = Math.Clamp(track.Program, 0, 127);

        TransposeText.Text = Format.Transpose(track.Transpose);
        CountText.Text = Format.NoteCount(track.Notes.Count);

        // 轨高跟着这条轨的音域走，重挂时必须重设 —— 移调会改音域，不设的话卷帘会拿上一份的高度画
        var (low, high) = controller.PitchRangeOf(trackIndex);
        Roll.Height = PianoRollGeometry.RulerHeight + (high - low + 1) * RowPixels;
        Roll.TrackIndex = trackIndex;
        Roll.Controller = controller;

        // 聚焦那一笔也在这儿兜一次底：控件刚建出来时窗口还没把控制器的聚焦设过来
        SetFocused(controller.FocusedTrack == trackIndex);
    }

    /// <summary>
    /// 把这条轨重挂到新的一份曲子上（编辑之后走这条，不是拆了重建）：控件树、滚动位置、焦点都不动。
    /// 轨数变了那一路仍然得重建，由窗口决定。
    /// </summary>
    /// <param name="controller">照新曲子建出来的控制器。</param>
    /// <param name="trackIndex">这条轨在新曲子里的下标（本次没有轨被删时和原来一样）。</param>
    public void Rebind(PianoRollController controller, int trackIndex)
    {
        // 手上开着的那几件小事先收掉：它们说的都是上一份曲子里的东西 ——
        // 不收的话，改名框会带着一个已经作废的下标提交出去
        CancelRename();
        SetConfirmingDelete(false);
        // 抽掉那一问更要紧：旧谱子上算出来的 tick 在新谱子上多半还读得通，于是会静悄悄地抽错一段
        SetSplitting(false);

        // 拖动中的预览一并作废：位移的基准没有跟着新曲子重算，接着拖下去会结算出一条尺寸对不上的命令
        Roll.CancelDrag();

        _controller = controller;
        Bind(trackIndex);
        Refresh(_lastPlayheadTick, _lastPlayheadVisible);
    }

    /// <summary>悬停到的音符变了（<see cref="NoteId.None"/> = 没命中）。</summary>
    public event EventHandler<NoteId>? HoverChanged;

    /// <summary>移调步进器被按了一下，参数是新的绝对半音数（不是增量）—— 界面算值、窗口调命令。</summary>
    public event EventHandler<int>? TransposeRequested;

    /// <summary>用户在音色下拉里挑了这条轨的音色（和原来那一号不一样）。参数是新的 GM 音色号。</summary>
    public event EventHandler<int>? ProgramRequested;

    /// <summary>
    /// 用户改了这条轨的名字（已经去过两端空白、和原来不一样）。参数是新名字；
    /// 空名字不会走到这儿（<c>SongEditor.RenameTrack</c> 收到空名字会抛），那一支在控件里当取消。
    /// </summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>
    /// 用户已经确认要删掉这条轨（按了那一问里的「删除」）。命令由窗口去调，
    /// 谁的轨看 <c>sender</c> —— 它就是这条轨的 <see cref="TrackLaneView"/>。
    /// </summary>
    public event EventHandler? DeleteRequested;

    /// <summary>
    /// 用户已经确认要抽掉这条轨上的一段。参数是 tick 不是小节号 —— 命令层连「小节」这个概念都没有。
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
    /// 卷帘上按了一下，焦点轨于是挪到了这一条上（卷帘的原话，见 <see cref="PianoRollLane.FocusChanged"/>）。
    /// 窗口靠它把别的轨的高亮灭掉，谁的轨看 <c>sender</c>。
    /// </summary>
    public event EventHandler? FocusChanged;

    /// <summary>
    /// 这条轨收起来 / 展开了。窗口靠它把试听那张表重排一遍 —— 收起来的轨不出声（见 <see cref="PreviewMixer.Mix"/>）。
    /// 这一声不落到任何命令上，谱面一个字节都不动。
    /// </summary>
    public event EventHandler? CollapseChanged;

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex => _trackIndex;

    /// <summary>这条轨收起来了没有。</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>
    /// 这条轨的身份：<c>(轨块号, 声道)</c>，取自模型自己那对唯一键（见 <see cref="Track.TrackIndex"/>）。
    /// 窗口在轨数变化后要把折叠状态带到新的控件上，而下标会随删除整体前移，这一对不重编号。
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
    /// 亮起 / 灭掉「聚焦轨」那三笔：左边那根竖条（<c>FocusBar</c>）、轨道头底色、卷帘底色（推给 <see cref="PianoRollLane.Focused"/>）。
    /// 不跟着折叠走 —— 收起来的轨也有活在干，聚焦停在它身上时竖条照样得在。
    /// 三笔全走令牌、不出现颜色字面值：主题一换，塞进来的 C# 画笔会停在旧色上。
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
    /// 收起 / 展开这条轨：卷帘、那一行「已折叠」、以及试听里响不响（见 <see cref="CollapseChanged"/>）。
    /// 谱面、选中集、导出一个字节都不碰。
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

        // 展开时立刻补一帧：收着的这段时间里 Refresh 一直跳过，场景还停在收起来之前那一份
        if (!collapsed) Refresh(_lastPlayheadTick, _lastPlayheadVisible);

        // 先改完看得见的再喊：窗口收到这一声时回头来问 IsCollapsed 才问得到对的值
        CollapseChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>展开它并滚进视野。Ctrl + ←/→ 定位到一条收起来的轨上时走这条 —— 不展开的话屏幕上一点变化都没有。</summary>
    public void Reveal()
    {
        SetCollapsed(false);
        ScrollIntoView();
    }

    /// <summary>
    /// 滚进视野，不展开（Ctrl+↑/↓ 换聚焦轨走这条）。名字不能叫 BringIntoView —— 那是 <c>Control</c> 上的扩展方法，
    /// 实例方法盖得住它，真叫了那个名字里面再调 <c>this.BringIntoView()</c> 就会一路递归到栈溢出。
    /// </summary>
    public void ScrollIntoView()
    {
        // `this.` 不能省：扩展方法只在「表达式.名字」这个形状上找，光写 BringIntoView() 就是 CS0103
        this.BringIntoView();
    }

    /// <summary>步进器上四个按钮共用的入口：<c>Tag</c> 里是这一下要挪几个半音。当前值从控制器手里的曲子现取。</summary>
    private void OnTransposeStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int delta)) return;

        int current = _controller.Song.Tracks[_trackIndex].Transpose;
        TransposeRequested?.Invoke(this, current + delta);
    }

    // ==================== 音色 ====================

    /// <summary>音色下拉换了。选中项的下标就是 GM 音色号（第 i 行就是 i 号）。</summary>
    private void OnTimbreChanged(object? sender, SelectionChangedEventArgs e)
    {
        int program = TimbreBox.SelectedIndex;
        // 一项都没选中（清空过、或者还没装数据）时什么都不做
        if (program < 0) return;

        // 和曲子里那一号一样就当没发生：Bind 设 SelectedIndex 那一下也会发这一声，
        // 照喊的话每编辑一次就多记一笔「换了音色」的空账
        if (program == _controller.Song.Tracks[_trackIndex].Program) return;

        ProgramRequested?.Invoke(this, program);
    }

    /// <summary>当前视口尺寸下重算一屏要画的东西。窗口改宽、滚动、播放每帧都调它。</summary>
    /// <param name="playheadTick">播放头在哪。</param>
    /// <param name="playheadVisible">要不要画它。拖导航条时是 false（wireframe 标注 3）。</param>
    public void Refresh(long playheadTick, bool playheadVisible)
    {
        // 先记住「此刻」，再决定画不画：宽度还没量出来时场景不重算，
        // 但拖动预览那一头的重画得拿这一份状态，不能因为这一帧没画就丢掉
        _lastPlayheadTick = playheadTick;
        _lastPlayheadVisible = playheadVisible;

        // 聚焦那三笔画在早退之前：收起来的轨、宽度还没量出来的轨，竖条和底色照样得对
        SetFocused(_controller.FocusedTrack == _trackIndex);

        // 收起来的轨没有卷帘可画（Roll 已经藏了），算了也没人看 ——
        // 但上面那两行不能省：展开的那一下要拿「此刻」重算一屏
        if (_collapsed) return;

        double width = Roll.Bounds.Width;
        // 还没量出来（首帧布局之前）就算了，照 wireframe 的 `if (w < 40) return;`
        if (width < 40) return;

        var track = _controller.Song.Tracks[_trackIndex];
        var viewport = _controller.ViewportOf(_trackIndex, width, Roll.Height);

        // 只有落在这条轨上的选中音才画边框 ——「选中」跨轨，别的轨的选中在这条轨上没有落笔的地方
        _selectedHere.Clear();
        foreach (var note in _controller.SelectedNotes)
            if (note.Track == _trackIndex) _selectedHere.Add(note.Id);

        // 拖动预览 / 框选每次现取（它们住在卷帘的字段里、按当前指针位置一路更新），
        // 于是「拖到一半窗口重画一屏」不会把预览抹掉 —— 重算出来的还是此刻那份预览
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

    /// <summary>轨号兜底：没名字的轨显示成「轨 01」。构造里那一次显示和改名时那一次比对必须是同一个算法。</summary>
    private static string DisplayName(string? name, int trackIndex)
        => string.IsNullOrWhiteSpace(name) ? $"轨 {Format.TrackNumber(trackIndex + 1)}" : name;

    /// <summary>焦点进了名字框：这一次改名算开始。全选是给鼠标用户的 —— 改名十有八九是整条换掉。</summary>
    private void OnNameBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (_renaming) return;

        // 同时开着两件事的话，「取消」该取消哪一个说不清
        SetConfirmingDelete(false);
        SetSplitting(false);

        _renaming = true;
        // 起点是已经落地的那个名字，不是框里剩下的一行字
        NameBox.Text = NameText.Text;

        // 划段那一问不抢焦点，这一句只是把焦点落回名字框本身（本来就开着的话是个空操作）
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>输入框里的键盘：回车 = 改，Esc = 不算。别的键（包括方向键）一律放行。</summary>
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

    /// <summary>焦点走了就是这一改算数（点别处、点卷帘、Tab 走开…），和大多数软件的输入框一样。</summary>
    private void OnNameBoxLostFocus(object? sender, RoutedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        if (!_renaming) return;

        string name = NameBox.Text?.Trim() ?? string.Empty;
        // 比的是屏幕上刚显示的那个名字：轨本来没名字、屏幕上写着兜底的「轨 01」时按回车，
        // 不该把「轨 01」写成真名字
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
    /// 收摊：把框里的字退回已经落地的那个名字，并清掉焦点。先落旗再退回 —— 退回会引发 <c>LostFocus</c>，那一路也是「提交」。
    /// 焦点要清掉：窗口的隧道阶段有一句「焦点在 TextBox 里就让开」，不清的话方向键和 Ctrl+Z 会整个失灵。
    /// </summary>
    private void EndRename()
    {
        // 先落旗再退回名字，倒过来的话这一次改名会被自己的 LostFocus 回调提交两遍
        _renaming = false;

        NameBox.Text = NameText.Text;

        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    // ==================== 删轨 ====================

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => SetConfirmingDelete(true);

    private void OnDeleteCancelClick(object? sender, RoutedEventArgs e) => SetConfirmingDelete(false);

    /// <summary>「真的删」。先把这一问收掉再喊命令 —— 命令回来之后这条轨就没了，不收的话那一瞬间还留着「删掉这条轨？」。</summary>
    private void OnDeleteConfirmClick(object? sender, RoutedEventArgs e)
    {
        SetConfirmingDelete(false);
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 摆上 / 收掉那一问。这是两下的动作，不是对话框：不可逆的那一下要慢一点，删错了 <c>Ctrl+Z</c> 就回来。
    /// 状态存在控件字段里、不放在 <see cref="Refresh"/> 里重置 —— 窗口每帧都会调一次 Refresh。
    /// </summary>
    private void SetConfirmingDelete(bool confirming)
    {
        if (_confirmingDelete == confirming) return;

        // 摆上删轨这一问时把抽掉那一问收掉；只在「摆上」这一头收，两个方法互相收也收不出环来
        if (confirming) SetSplitting(false);

        _confirmingDelete = confirming;
        DeleteButton.IsVisible = !confirming;
        DeleteConfirm.IsVisible = confirming;
    }

    /// <summary>收掉「删掉这条轨？」那一问（窗口的 Esc 用它）。返回刚才是不是真摆着这一问：没摆着时 Esc 得留给别处。</summary>
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
    /// 摆上 / 收掉「抽掉一段」那一问：摆上之后这条轨的卷帘只划段（见 <see cref="PianoRollLane.ArmCut"/>），
    /// 那一段划在哪儿、多宽，就在卷帘上直接拖。状态存在字段里、不放在 <see cref="Refresh"/> 里重置 —— 窗口每帧都调一次 Refresh。
    /// </summary>
    private void SetSplitting(bool splitting)
    {
        if (_splitting == splitting) return;

        // 同时开着两件事的话，「取消」该取消哪一个说不清
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
    /// 装备上「抽掉一段」时先替用户划好的那一段：本轨上选中的音整体盖住的那一段，
    /// 一个音都没选中就退回播放头所在的那一小节（播放头那个数只在装备的这一下读一次）。
    /// </summary>
    private (long Start, long End) CutPrefill()
    {
        if (_controller.SelectionSpan(_trackIndex) is { } span) return span;

        // 播放头多半停在「听出不对」的那一刻，那一整小节就是他想剪掉的那一段的起点。
        // 夹到曲子的范围内：折叠让曲子变短之后，播放头可能停在曲子外面
        int bar = Math.Clamp(_controller.BarOfTick(_lastPlayheadTick), 1, _controller.BarCount);
        long start = _controller.TickOfBarClamped(bar - 1);
        return (start, start + _controller.TicksPerBar);
    }

    /// <summary>
    /// 收掉「抽掉一段」那一问（窗口的 Esc 用它），和 <see cref="CancelPendingDelete"/> 成对 ——
    /// 返回值同样是「刚才是不是真摆着这一问」。
    /// </summary>
    public bool CancelPendingSplit()
    {
        if (!_splitting) return false;

        SetSplitting(false);
        return true;
    }

    /// <summary>
    /// 把正摆着的那一问按下去（窗口的 Enter 用它，和 <see cref="CancelPendingSplit"/> 成对）。
    /// 返回「这一下是不是归我用掉了」：没摆着那一问时返回 false，让窗口把回车让给别处；
    /// 「抽掉」灰着时照样算用掉 —— 屏幕上正摆着一问一答。
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
        // 灰着的时候点不到；真走到了就当没发生 —— 命令会原样还回来同一份曲子，白记一笔撤销
        if (!CutYesButton.IsEnabled) return;

        // 那一段得先读出来：SetSplitting(false) 会把它一起收掉（DisarmCut）
        if (Roll.CutRange is not { } range) return;

        SetSplitting(false);
        CutRangeRequested?.Invoke(this, new CutRangeRequest(range.StartTick, range.EndTick));
    }

    /// <summary>
    /// 重算右边那一行预览：按下「抽掉」之前先说清楚会发生什么 —— 这一刀下去后半截轨会往前挪，光看谱面猜不到。
    /// 数字由 <see cref="CutPreview"/> 算（那份预测和真跑一遍的结果由对照测试钉在一起）；
    /// 一个音都不会动的时候把「抽掉」灰掉，还没划出范围时那一行改说「怎么划」。
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
/// 用户确认要从这条轨上抽掉这一段：<c>[StartTick, EndTick)</c>，装的是 tick 不是小节号 ——
/// 命令层连「小节」这个概念都没有，小节号是显示层拿速度表算出来的。
/// </summary>
public sealed record CutRangeRequest(long StartTick, long EndTick);

