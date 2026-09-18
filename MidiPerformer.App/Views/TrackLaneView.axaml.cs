using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;

namespace MidiPerformer.App.Views;

/// <summary>
/// 一条轨那一整块：轨道头 + 卷帘。
///
/// 它自己不换算任何东西 —— 卷帘的宽高定下来之后，向 <see cref="PianoRollController"/>
/// 要一份视口，再向 <see cref="PianoRollPresenter"/> 要一屏要画的东西，剩下的交给
/// <see cref="PianoRollLane"/>。这就是「界面层薄」的意思：这里只有布置，没有数学。
///
/// <b>它也不改谱面。</b>轨道头上那个移调步进器只把「当前值 ± n」算出来喊一声
/// （<see cref="TransposeRequested"/>），改名、删除、卷帘上拖出来的那几种编辑同样只喊一声
/// （<see cref="RenameRequested"/> / <see cref="DeleteRequested"/> /
/// <see cref="NotesMoved"/> / <see cref="NoteResized"/> / <see cref="NotesDeleted"/>），
/// 命令由窗口去调 —— 编辑脊柱只有一条，撤销的记账在装饰器里，谁调命令都自动有撤销，
/// 但调命令的地方只该有一处。
///
/// 唯一「就地办完」的是**问用户**这件事：改名问在输入框里、删轨问在那一行小字上，
/// 两句问话都不出这个控件（也不弹对话框）—— 问完的答案才喊出去。
/// </summary>
public partial class TrackLaneView : UserControl
{
    /// <summary>一个音高行多高（像素），照 wireframe 的 <c>ROW_H = 7</c>。</summary>
    private const double RowPixels = 7;

    private readonly PianoRollController _controller;
    private readonly int _trackIndex;

    /// <summary>改名输入框正开着。</summary>
    private bool _renaming;

    /// <summary>「删掉这条轨？」那一问正摆着。</summary>
    private bool _confirmingDelete;

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

        _controller = controller;
        _trackIndex = trackIndex;

        var track = controller.Song.Tracks[trackIndex];

        NumberText.Text = Format.TrackNumber(trackIndex + 1);
        NameText.Text = DisplayName(track.Name, trackIndex);
        TimbreText.Text = $"音色 {Format.Timbre(track.Program, track.Channel)}";
        TransposeText.Text = Format.Transpose(track.Transpose);
        CountText.Text = Format.NoteCount(track.Notes.Count);
        // 移调、改名、删轨都做完了（09），这句话只留**真还没做、也还没有归属切片**的那三样：
        // 音色试听、只看这条、折叠 —— 别替它们许诺
        LaterText.Text = "音色（试听） / 只看这条 / 折叠 还没做";

        // 行高恒定（标注 5）：轨高跟着这条轨的音域走，宽音域的轨就高一些
        var (low, high) = controller.PitchRangeOf(trackIndex);
        Roll.Height = PianoRollGeometry.RulerHeight + (high - low + 1) * RowPixels;
        Roll.TrackIndex = trackIndex;
        Roll.Controller = controller;
        Roll.Tokens = tokens;

        // 悬停：这条轨的音符先报上去，由窗口去查读数条要的那几个数
        Roll.HoverChanged += (_, note) => HoverChanged?.Invoke(this, note);

        // 编辑意图：原样转发（形状都不动，见各个事件的说明）
        Roll.NotesMoved += (_, request) => NotesMoved?.Invoke(this, request);
        Roll.NoteResized += (_, request) => NoteResized?.Invoke(this, request);
        Roll.NotesDeleted += (_, notes) => NotesDeleted?.Invoke(this, notes);

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

    /// <summary>卷帘上拖出来的一组音要挪（增量）。转发卷帘的原话，形状一模一样。</summary>
    public event EventHandler<NoteMoveRequest>? NotesMoved;

    /// <summary>卷帘上有一个音的起点 / 时值被拖成了新的值（绝对位置）。</summary>
    public event EventHandler<NoteResizeRequest>? NoteResized;

    /// <summary>卷帘上空白处横拖框住的那几个音要删掉（参数就是要删的那些音）。</summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? NotesDeleted;

    /// <summary>卷帘上的选中集变了。窗口靠它刷新读数条和「选中」那一格。</summary>
    public event EventHandler<IReadOnlyList<NoteRef>>? SelectionChanged;

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex => _trackIndex;

    /// <summary>
    /// 步进器上四个按钮共用的入口：<c>Tag</c> 里是这一下要挪几个半音。
    ///
    /// 当前值从控制器手里的曲子现取 —— 每次编辑之后窗口都会把所有轨重建一遍，
    /// 所以这个控件手上的值永远是最新的，不会累加到一次编辑之前的旧值上。
    /// </summary>
    private void OnTransposeStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int delta)) return;

        int current = _controller.Song.Tracks[_trackIndex].Transpose;
        TransposeRequested?.Invoke(this, current + delta);
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
}
