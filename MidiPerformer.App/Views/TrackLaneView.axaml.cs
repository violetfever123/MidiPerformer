using Avalonia.Controls;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.App.Theme;
using MidiPerformer.Core.Model;

namespace MidiPerformer.App.Views;

/// <summary>
/// 一条轨那一整块：轨道头（只读）+ 卷帘。
///
/// 它自己不换算任何东西 —— 卷帘的宽高定下来之后，向 <see cref="PianoRollController"/>
/// 要一份视口，再向 <see cref="PianoRollPresenter"/> 要一屏要画的东西，剩下的交给
/// <see cref="PianoRollLane"/>。这就是「界面层薄」的意思：这里只有布置，没有数学。
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
        // 只报数、不给控件：改它是 08 的事，而「现在移了几个半音」是这一屏显示得对不对的一半
        TransposeText.Text = $"移调 {track.Transpose:+0;-0;0} 半音";
        CountText.Text = Format.NoteCount(track.Notes.Count);
        LaterText.Text = "音色 / 移调 / 只看这条 / 折叠 / 删除轨 归 08";

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

    /// <summary>这条轨在 <c>Song.Tracks</c> 里的下标。</summary>
    public int TrackIndex => _trackIndex;

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
