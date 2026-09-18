using Avalonia.Controls;
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
/// （<see cref="TransposeRequested"/>），命令由窗口去调 —— 编辑脊柱只有一条，
/// 撤销的记账在装饰器里，谁调命令都自动有撤销，但调命令的地方只该有一处。
/// </summary>
public partial class TrackLaneView : UserControl
{
    /// <summary>一个音高行多高（像素），照 wireframe 的 <c>ROW_H = 7</c>。</summary>
    private const double RowPixels = 7;

    private readonly PianoRollController _controller;
    private readonly int _trackIndex;

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
        NameText.Text = string.IsNullOrWhiteSpace(track.Name)
            ? $"轨 {Format.TrackNumber(trackIndex + 1)}"
            : track.Name;
        TimbreText.Text = $"音色 {Format.Timbre(track.Program, track.Channel)}";
        TransposeText.Text = Format.Transpose(track.Transpose);
        CountText.Text = Format.NoteCount(track.Notes.Count);
        // 移调已经做完了，这句话只留**真还没做、也还没有归属切片**的那三样。
        // 删除轨 / 改名归 09，音色试听、只看这条、折叠还没有切片认领 —— 别替它们许诺
        LaterText.Text = "音色（试听） / 只看这条 / 折叠 还没做";

        // 行高恒定（标注 5）：轨高跟着这条轨的音域走，宽音域的轨就高一些
        var (low, high) = controller.PitchRangeOf(trackIndex);
        Roll.Height = PianoRollGeometry.RulerHeight + (high - low + 1) * RowPixels;
        Roll.TrackIndex = trackIndex;
        Roll.Controller = controller;
        Roll.Tokens = tokens;

        // 悬停：这条轨的音符先报上去，由窗口去查读数条要的那几个数
        Roll.HoverChanged += (_, note) => HoverChanged?.Invoke(this, note);
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
        double width = Roll.Bounds.Width;
        // 还没量出来（首帧布局之前）就算了，照 wireframe 的 `if (w < 40) return;`
        if (width < 40) return;

        var track = _controller.Song.Tracks[_trackIndex];
        var viewport = _controller.ViewportOf(_trackIndex, width, Roll.Height);

        // 选中的音只有落在这一条轨上才画这圈边框 —— 「选中」跨轨，一次只有一个
        int selected = _controller.Selection is { } s && s.Track == _trackIndex ? s.Note : -1;
        var overlay = new PianoRollPresenter.RollOverlay(playheadTick, playheadVisible, selected);

        Roll.SetScene(PianoRollPresenter.BuildLane(
            track,
            viewport,
            _controller.BarCount,
            _controller.Song.TempoMap.Division.TicksPerQuarterNote,
            _controller.InRangeFlagsOf(_trackIndex),
            overlay));
    }
}
