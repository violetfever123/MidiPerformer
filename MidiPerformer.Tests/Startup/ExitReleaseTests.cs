using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MidiPerformer.Tests.Startup;

/// <summary>
/// 退出收尾那条路上**不许再补一次「松开所有键」**（98 号票）。
///
/// 缘由是 <c>InputSender.ReleaseAll()</c> 是无条件的：不管自己按没按过，它都会发左/右/中三个鼠标的
/// **抬键**。键盘上多发一个抬键没人管，鼠标上多发一个**右键抬起**就是一次真右键 —— 实机实测
/// （<c>.scratch/98-探-孤右键抬起.ps1</c>）：孤立的一发 <c>MOUSEEVENTF_RIGHTUP</c>（没有配对的按下）
/// 会让资源管理器把桌面右键菜单弹出来，菜单窗 <c>#32768</c> 从 0 变 1。
///
/// 而「以管理员身份重启」恰好就落在这一刻：<c>ElevationPrompt</c> 起完新进程就 <c>desktop.Shutdown()</c>，
/// 旧窗口那时已经没了、光标底下是桌面 —— 用户看到的是「新实例一开就自己点了右键」。
///
/// 这一条只管**组装点的 <c>desktop.Exit</c>**。演奏那几条路（起跑清场 / 放完 / 急停 / 看门狗）
/// 照旧无条件松：那时候键是真可能按着的，卡在按下状态比多发一个抬键坏得多。
/// </summary>
public class ExitReleaseTests
{
    /// <summary>
    /// 收尾该收的还在（两个 Dispose），但里面不许再出现 <c>ReleaseAll</c>。
    /// 前半条是防「为了删一行，把整个收尾一起删掉」；后半条才是这一票要钉的东西。
    /// </summary>
    [Test]
    public void 组装点的退出收尾不再补一次松键()
    {
        string app = 去注释(File.ReadAllText(Path.Combine(RepoRoot, "MidiPerformer.App", "App.axaml.cs")));
        string 退出 = 花括号段(app, "desktop.Exit += (_, _) =>");

        Assert.Multiple(() =>
        {
            Assert.That(退出, Is.Not.Empty,
                "组装点里没找到 desktop.Exit —— 判据的落点没了，别让它悄悄变成空转");
            Assert.That(退出, Does.Contain("sink.Dispose"), "退出时该收的那个出口不见了");
            Assert.That(退出, Does.Contain("logFactory.Dispose"), "日志最后那一次收尾不见了");
            Assert.That(退出, Does.Not.Contain("ReleaseAll"),
                "退出时又补了一次松键 —— 那一发孤立的右键抬起在桌面上就是一次右键菜单");
        });
    }

    /// <summary>
    /// 上面那条断言靠「先把注释切掉」才成立：<c>App.axaml.cs</c> 那段注释正要点名
    /// <c>sender.ReleaseAll()</c> 才说得清为什么删它。切法要是坏了（原样返回），
    /// 上面那条守卫就会红在自己的说明文字上；这里先钉住切法本身。
    /// </summary>
    [Test]
    public void 守卫用的去注释是真的去了()
    {
        Assert.That(去注释("sender.ReleaseAll(); // 这里不许再调 sender.ReleaseAll()"),
            Is.EqualTo("sender.ReleaseAll(); "));
    }

    // ==================== 源码位置与读法 ====================

    /// <summary>
    /// 把 <c>//</c> 之后切掉 —— 守卫看的是**代码**，不是注释里的散文。
    /// </summary>
    private static string 去注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");

    /// <summary>
    /// 从一句签名开始，一直抠到它那对花括号合上（按括号配对）。签名本身也算在里面。
    /// </summary>
    private static string 花括号段(string 源码, string 签名)
    {
        int 起 = 源码.IndexOf(签名, StringComparison.Ordinal);
        if (起 < 0) return "";
        int 开 = 源码.IndexOf('{', 起);
        if (开 < 0) return "";

        int 层 = 0;
        for (int i = 开; i < 源码.Length; i++)
        {
            if (源码[i] == '{') 层++;
            else if (源码[i] == '}')
            {
                层--;
                if (层 == 0) return 源码[起..(i + 1)];
            }
        }

        return "";
    }

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));
}
