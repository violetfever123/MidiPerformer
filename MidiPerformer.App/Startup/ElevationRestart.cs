using System.ComponentModel;
using System.Diagnostics;

namespace MidiPerformer.App.Startup;

/// <summary>
/// 以管理员身份重启自己：拿当前 exe 的路径、拼 <see cref="ProcessStartInfo"/>、真去 <c>runas</c>，
/// 再把 Win32 那边的失败翻成三态。碰进程与 Win32，所以住在 App 层
/// （<c>Core</c> 的 TFM 是 <c>net8.0</c>，Win32 在编译期就找不到）。
/// 「走哪条路」不在这儿 —— 那是 <see cref="ElevationStartup.Decide"/> 的活。
/// </summary>
public static class ElevationRestart
{
    /// <summary>
    /// 当前这个可执行文件的路径。<b>必须是 <see cref="Environment.ProcessPath"/>，不能是 <c>Assembly.Location</c></b>：
    /// 发布态是自包含单文件裁剪（<c>MidiPerformer.App.csproj</c> 的 <c>MidiPerformerPublish</c>），
    /// 那种产物里 <c>Assembly.Location</c> 是<b>空串</b>，喂给 <see cref="ProcessStartInfo.FileName"/>
    /// 不报错也不启动 —— 一个在开发机上永远看不见的静默失败。
    /// </summary>
    public static string CurrentExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// 重启用的启动信息：同一个 exe、<c>runas</c>、<b>命令行参数原样带过去</b>。
    /// <c>UseShellExecute</c> 必须开：<c>runas</c> 这个词是 ShellExecute 的词，
    /// 不开它只能直连 CreateProcess，提权请求根本递不出去。
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string executablePath, IEnumerable<string>? args)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
        };

        // ArgumentList 而不是手工拼 Arguments：带空格的参数该不该加引号由它管。
        if (args is not null)
        {
            foreach (string arg in args) info.ArgumentList.Add(arg);
        }

        return info;
    }

    /// <summary>
    /// 真去起来，把结果翻成 <see cref="ElevationUac"/>。<b>这里不抛</b>：走这条路只是替用户省掉
    /// 「关掉、右键、找菜单」那几步，起不来最坏也只是回到今天的样子（普通权限 + 演奏时那句提示）。
    /// </summary>
    public static ElevationUac Launch(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
            return ElevationUac.Approved;
        }
        catch (Win32Exception e)
        {
            // 用户在 UAC 那个框上按「否」时，ShellExecuteEx 回的就是 ERROR_CANCELLED。
            return ElevationStartup.ClassifyRestartFailure(e.NativeErrorCode);
        }
        catch (Exception)
        {
            // 别的起不来的原因（路径不对、被策略拦了……）一律当「没起来」，
            // 绝不能让「重启没成功」变成「程序打不开」。
            return ElevationUac.Failed;
        }
    }
}
