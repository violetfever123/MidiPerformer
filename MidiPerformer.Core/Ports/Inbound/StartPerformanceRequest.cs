using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 起一场演奏要说的全部话：曲子、弹哪条轨、基准八度、时序档位、倒计时几秒。
/// 参数多，散着传会变成一串位置参数，谁也说不清第三个 <c>int</c> 是轨下标还是八度，所以打包成具名类型。
/// </summary>
/// <param name="Song">要弹的曲子（不可变，起跑之后由用例持有引用）。</param>
/// <param name="TrackIndex">弹哪条轨，<see cref="Song.Tracks"/> 里的下标。</param>
/// <param name="BaseOctave">口琴的基准八度（MIDI 编号，C4 = 第 4 八度）。<c>null</c> = 自动：
/// 让可演奏区容下最多音符，同分时取平均八度最近的（见 <c>NoteMapper.AutoBaseOctave</c>）。</param>
/// <param name="Timing">时序档位，原样用 <see cref="InputTiming"/>。</param>
/// <param name="CountdownSeconds">盲倒计时秒数：用户用这段时间切到游戏窗口，倒计时结束才发第一个音。</param>
public sealed record StartPerformanceRequest(
    Song Song,
    int TrackIndex,
    int? BaseOctave,
    InputTiming Timing,
    double CountdownSeconds);

/// <summary>
/// 预检的结论：放行，或者三种失败之一。文案是界面的事 —— Core 只回一个原因，
/// 界面把它翻成「以管理员身份重开」「切到英文输入法」这类人话。
/// </summary>
public enum PerformanceStartOutcome
{
    /// <summary>放行，演奏已经在跑（或已经在倒计时）。</summary>
    Started,

    /// <summary>不是管理员权限。按键会被 UIPI 挡在前台窗口外面 —— 发出去，游戏一个都收不到。</summary>
    NotElevated,

    /// <summary>输入法处于中文态。按键会被输入法吃掉，表现为整段整段地漏音，而且不报错。</summary>
    ImeActive,

    /// <summary>选中的那条轨不是单声部（或下标越界）。游戏里的口琴同时只能响一个音，弹不了。</summary>
    NoPlayableTrack
}
