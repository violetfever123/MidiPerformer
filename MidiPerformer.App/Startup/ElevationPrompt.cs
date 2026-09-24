using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.SelfTest;
using MidiPerformer.App.Views;

namespace MidiPerformer.App.Startup;

/// <summary>
/// 启动时问一次提权。这一层只管三件有副作用的事（弹框、起进程、退出），
/// <b>走哪条路由 <see cref="ElevationStartup.Decide"/> 说了算</b> —— 按顺序问它两遍：
/// 问过用户之后走哪条、真去 <c>runas</c> 之后走哪条。
/// </summary>
public static class ElevationPrompt
{
    /// <summary>
    /// 窗口开出来之后调一次。<paramref name="args"/> 是这次启动的命令行，重启时原样带过去。
    /// 用户拒绝（在框上取消、或在 UAC 上按否）一律只是「这次不提权」，照常往里走：
    /// 编辑一分钱不花，真去演奏时再吃 <c>Format</c> 里那句现成的提示。
    /// </summary>
    public static async Task AskAsync(Window owner, string[]? args)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // 两个环境事实在这儿问好，判定那边就是纯的。
        bool elevated = InputSender.CheckElevation();
        bool selfTest = PerformerSelfTest.Requested;

        // ① 该不该弹？已提权不弹；自检不弹 —— 自检是自动化在跑，弹一颗 UAC 会把脚本挂死。
        if (ElevationStartup.Decide(elevated, selfTest, ElevationChoice.Unasked, ElevationUac.NotAttempted).Route
            != ElevationRoute.Ask)
        {
            return;
        }

        // ② 弹一颗。取消 = 先不用 = 以普通权限继续，不是退出，也不再问第二次。
        bool restart = await Dialogs.ConfirmAsync(
            owner,
            ElevationStartup.PromptTitle,
            ElevationStartup.PromptBody,
            ElevationStartup.PromptRestartText);

        var choice = restart ? ElevationChoice.Restart : ElevationChoice.KeepCurrent;
        if (ElevationStartup.Decide(elevated, selfTest, choice, ElevationUac.NotAttempted).Route
            != ElevationRoute.TryRestart)
        {
            return;
        }

        // ③ 真去 runas，把 UAC 的结果（含用户在安全桌面上按的那个「否」）交回同一张表。
        var uac = ElevationRestart.Launch(
            ElevationRestart.BuildStartInfo(ElevationRestart.CurrentExecutablePath, args));

        if (ElevationStartup.Decide(elevated, selfTest, ElevationChoice.Restart, uac).Route
            != ElevationRoute.ExitForRestart)
        {
            return;
        }

        // ④ 新进程已经起来了，原进程收工。（这一票不做「退出时保存状态」，所以没有状态要交接。）
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
