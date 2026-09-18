using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Core.Ports.Inbound;

/// <summary>
/// 起一场演奏要说的全部话：曲子、弹哪条轨、基准八度、时序档位、倒计时几秒。
///
/// <b>它是全程序唯一值得做成具名类型的 Request</b>。编辑命令都是收一两个散参返回一个
/// <see cref="Song"/>，「改没改」比引用就够了（见 spec 的 Request / Result 一节）；
/// 这里五六个参数散着传，调用点会变成一串位置参数，谁也说不清第三个 <c>int</c> 是轨下标还是八度。
///
/// 它是**入站端口**那一类：由外向内递进用例，没有第二个实现，也不需要倒置 ——
/// 它存在的理由是「打包」，不是「抽象」。
/// </summary>
/// <param name="Song">要弹的曲子（不可变，起跑之后由用例持有引用）。</param>
/// <param name="TrackIndex">弹哪条轨，<see cref="Song.Tracks"/> 里的下标。</param>
/// <param name="BaseOctave">口琴的基准八度（MIDI 编号，C4 = 第 4 八度）。<c>null</c> = 自动：
/// 让可演奏区容下最多音符，同分时取平均八度最近的（见 <c>NoteMapper.AutoBaseOctave</c>）。</param>
/// <param name="Timing">时序档位。三档原样用 <see cref="InputTiming"/>，这里不另立一套编号。</param>
/// <param name="CountdownSeconds">盲倒计时秒数：用户用这段时间切到游戏窗口，倒计时结束才发第一个音。</param>
public sealed record StartPerformanceRequest(
    Song Song,
    int TrackIndex,
    int? BaseOctave,
    InputTiming Timing,
    double CountdownSeconds);

/// <summary>
/// 预检的结论。<b>是枚举，不是 Result 对象。</b>
///
/// 它必须存在：三种失败要给三种不同的中文提示。但**文案是界面的事** ——
/// Core 只回一个原因，界面把它翻成「以管理员身份重开」「切到英文输入法」这类人话。
/// 用例层一旦开始拼中文句子，文案就再也没法集中改，也没法翻译。
///
/// 这也不是 DTO 层：它不映射任何外部格式，跟文件格式那件事没有关系。
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
