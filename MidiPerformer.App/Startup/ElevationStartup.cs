namespace MidiPerformer.App.Startup;

/// <summary>启动时在提权这件事上走哪条路。</summary>
public enum ElevationRoute
{
    /// <summary>不弹框、不重启，照常开窗（已提权 / 自检 / 用户选了先不用 / UAC 没成）。</summary>
    Proceed,

    /// <summary>该把那颗「以管理员身份重启」弹出来了。</summary>
    Ask,

    /// <summary>用户点了那颗按钮 —— 去 <c>runas</c> 重启自己。</summary>
    TryRestart,

    /// <summary>新进程已经起来了，原进程收工。</summary>
    ExitForRestart,
}

/// <summary>用户在「以管理员身份重启」那颗框上做了什么。</summary>
public enum ElevationChoice
{
    /// <summary>还没问过（启动时刚进来）。</summary>
    Unasked,

    /// <summary>点了「以管理员身份重启」。</summary>
    Restart,

    /// <summary>选了取消 / 叉掉了框 —— 就是「先不用」。</summary>
    KeepCurrent,
}

/// <summary>真去 <c>runas</c> 之后 UAC 那边给了什么结果。</summary>
public enum ElevationUac
{
    /// <summary>还没真去试。</summary>
    NotAttempted,

    /// <summary>新进程起来了。</summary>
    Approved,

    /// <summary>被拒：<c>ERROR_CANCELLED</c>，用户在 UAC 那个框上按了「否」。</summary>
    Cancelled,

    /// <summary>因为别的原因没起来（策略拦了、路径不对……）。</summary>
    Failed,
}

/// <summary>为什么走这条路。只给日志与测试看，界面不读它。</summary>
public enum ElevationReason
{
    AlreadyElevated,
    SelfTest,
    AwaitingUser,
    UserDeclined,
    UacPending,
    UacApproved,
    UacCancelled,
    UacFailed,
}

/// <summary>一次判定：走哪条路，以及为什么。</summary>
public readonly record struct ElevationDecision(ElevationRoute Route, ElevationReason Reason);

/// <summary>
/// 「启动时要不要提权、提权没成怎么办」的那张表。<b>纯函数</b>：进程权限、用户点没点、UAC 给了什么
/// 都由调用方问好了传进来，这里只判断 —— 所以整块能脱开进程与 UAC 测。
/// </summary>
public static class ElevationStartup
{
    /// <summary><c>ERROR_CANCELLED</c>：用户在 UAC 那个框上按了「否」。这就是「被拒」的全部含义。</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>那颗框的标题。也是实机验证时从一堆顶层窗里认它的凭据。</summary>
    public const string PromptTitle = "要以管理员身份重启吗？";

    /// <summary>
    /// 正文说清两件事：为什么值得重启（往游戏里发的按键会被 Windows 挡掉，且不报错）；
    /// 以及不重启也不是走投无路（编辑照常，只有演奏那一下会提醒）。
    /// </summary>
    public const string PromptBody =
        "这个程序要往游戏里发按键，普通权限下 Windows 会把它挡在游戏窗口外面 —— 一个音都收不到，还不报错。"
        + "以管理员身份重启一下就好了。\n\n"
        + "不重启也行：照常进去编辑谱面，只有点演奏那一下才会提醒你权限不够。";

    /// <summary>会真动手的那颗按钮。写动词，不写「确定」。</summary>
    public const string PromptRestartText = "以管理员身份重启";

    /// <summary>重启失败的原生错误码 → 三态。<b>只有 1223 是「用户按了否」</b>，别的都是「没起来」。</summary>
    public static ElevationUac ClassifyRestartFailure(int nativeErrorCode) =>
        nativeErrorCode == ErrorCancelled ? ElevationUac.Cancelled : ElevationUac.Failed;

    /// <summary>
    /// 走哪条路。四条输入穷尽了启动时提权这条路的所有情形，每一组都有明确结局：
    /// 已提权 → 不弹；自检 → 不弹（自检是脚本在跑，弹 UAC 会把脚本挂死）；
    /// 用户拒绝、UAC 被拒、UAC 因为别的原因没起来 → <b>三条都继续以普通权限进</b>，不是退出、不是再弹；
    /// 只有「点了重启 + UAC 通过」才收工换进程。
    /// </summary>
    public static ElevationDecision Decide(
        bool elevated, bool selfTest, ElevationChoice choice, ElevationUac uac)
    {
        if (elevated) return new(ElevationRoute.Proceed, ElevationReason.AlreadyElevated);
        if (selfTest) return new(ElevationRoute.Proceed, ElevationReason.SelfTest);

        return choice switch
        {
            ElevationChoice.Unasked =>
                new(ElevationRoute.Ask, ElevationReason.AwaitingUser),

            ElevationChoice.KeepCurrent =>
                new(ElevationRoute.Proceed, ElevationReason.UserDeclined),

            _ => uac switch
            {
                ElevationUac.NotAttempted => new(ElevationRoute.TryRestart, ElevationReason.UacPending),
                ElevationUac.Approved => new(ElevationRoute.ExitForRestart, ElevationReason.UacApproved),

                // 被拒和别的失败在这一层长得一样（都是「照常进」），分成两个原因只是为了日志好认。
                ElevationUac.Cancelled => new(ElevationRoute.Proceed, ElevationReason.UacCancelled),
                _ => new(ElevationRoute.Proceed, ElevationReason.UacFailed),
            }
        };
    }
}
