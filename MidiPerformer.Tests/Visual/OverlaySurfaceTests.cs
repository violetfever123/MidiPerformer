using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 悬浮层与演奏入口的**界面事实**。
///
/// 判据和 <see cref="TokenParityTests"/> 一样：**读文件文本 / 读 XAML**，不起 Avalonia ——
/// 起一个真窗口在 NUnit 里要一台有桌面会话的机器，而这些结论本来就写在文件里。
///
/// <b>清清楚楚地说一遍这些断言证明了什么、没证明什么：</b>
/// 它们证明的是「代码要了这几条」。至于 Windows 有没有真给它、焦点有没有真没被抢走、
/// 卡片浮在游戏画面上是什么样 —— 一概没验，也验不了：那要一块真屏幕、一个真前台窗口、
/// 一次真点击。那几条归人工（见 06 的验收记录），浮在游戏画面上那一条归 13。
///
/// 那还守它做什么：因为这四条里任何一条**坏掉都不报错**。置顶没了，卡片只是悄悄跑到
/// 游戏画面后面；ShowActivated 没了，之后发的键全发到它身上而游戏一个都收不到。
/// 用户看到的都是「怎么没反应」，查不出为什么 —— 所以至少要拦住「哪天被人删掉」。
/// </summary>
public class OverlaySurfaceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>三态的名字。少一个，就有一段演奏用户在屏幕上什么也看不见。</summary>
    private static readonly string[] StateNames = { "CountdownState", "PlayingState", "StoppedState" };

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static XElement 悬浮层() => XDocument.Load(Path.Combine(AppDir, "Views", "PerformerOverlayWindow.axaml")).Root!;

    private static XElement 主窗口() => XDocument.Load(Path.Combine(AppDir, "Views", "MainWindow.axaml")).Root!;

    private static string? 属性(XElement e, string name) => e.Attribute(name)?.Value;

    private static string? 名字(XElement e) => e.Attribute(Xaml + "Name")?.Value;

    private static string 读源文件(string name) => File.ReadAllText(Path.Combine(AppDir, "Views", name));

    // ==================== 窗口属性 ====================

    /// <summary>
    /// 悬浮层那几条**不能动的**窗口属性。每一条坏掉都是「看着还在，功能没了」：
    /// 卡片还在屏幕上，但它压不住游戏、或者把焦点偷走了。
    ///
    /// <c>ShowActivated</c> 单独说一句：它是「不抢焦点」的**第一重**，管的是「显示这一下」；
    /// 第二重是 <c>WS_EX_NOACTIVATE</c>，管「之后任何一下」（见 <c>OverlayStyleTests</c>）。
    /// 两个都要，少一个都还有一条抢焦点的路。
    /// </summary>
    [Test]
    public void 悬浮层的窗口属性一条都不能少()
    {
        var window = 悬浮层();

        Assert.Multiple(() =>
        {
            Assert.That(属性(window, "Topmost"), Is.EqualTo("True"),
                "不置顶就被游戏画面盖住 —— 这块提示等于没有");
            Assert.That(属性(window, "ShowActivated"), Is.EqualTo("False"),
                "显示这一下会抢焦点：抢了之后发的按键就发到它身上了（不抢焦点的第一重）");
            Assert.That(属性(window, "Background"), Is.EqualTo("Transparent"),
                "底色不透明就挡住游戏画面");
            Assert.That(属性(window, "TransparencyLevelHint"), Is.EqualTo("Transparent"),
                "没有透明层的话，Background=Transparent 只是一句空话 —— 两个都要");
            Assert.That(属性(window, "SystemDecorations"), Is.EqualTo("None"),
                "系统标题栏会明晃晃地压在游戏画面上");
            Assert.That(属性(window, "ShowInTaskbar"), Is.EqualTo("False"),
                "进了任务栏，切窗口时点它一下就把焦点带走了");
            Assert.That(属性(window, "WindowStartupLocation"), Is.EqualTo("Manual"),
                "位置是代码摆的（顶端居中，见 PlaceAtTopCenter）—— 交给系统摆就摆到屏幕正中间，那是准星的位置");
        });
    }

    /// <summary>
    /// 三态齐全，而且**一开始只露倒计时那一态**。
    ///
    /// 首帧是倒计时不是随便挑的：窗口是 <c>SizeToContent</c> 的，一开始没内容的话
    /// 第一次布局量到的是零尺寸，而定位跟着尺寸走 —— 卡片第一下会摆歪。
    /// </summary>
    [Test]
    public void 三态齐全而且一开始只露倒计时()
    {
        var states = 悬浮层().Descendants().Where(e => StateNames.Contains(名字(e))).ToList();
        var shown = states.Where(s => 属性(s, "IsVisible") != "False").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(states.Select(名字), Is.EquivalentTo(StateNames),
                "三态缺一个：倒计时 / 演奏中 / 已停止，缺哪一段用户就在哪一段看不见东西");
            Assert.That(shown, Has.Count.EqualTo(1), "一开始只该露一态");
            Assert.That(名字(shown[0]), Is.EqualTo("CountdownState"), "一开始露的不是倒计时");
        });
    }

    /// <summary>
    /// 演奏中那一态里必须有「现在在发哪个音」那格 —— 工单里的「状态行显示当前在发的音」，
    /// 游戏里那一半靠它（窗口二那一半见 <c>PerformerWindow.OnNoteSent</c>）。
    /// 用户就是靠它对一下有没有错音的：听不出来的时候，这是唯一的线索。
    /// </summary>
    [Test]
    public void 演奏中那一态里有当前在发的音的读数()
    {
        var playing = 悬浮层().Descendants().First(e => 名字(e) == "PlayingState");

        Assert.That(playing.Descendants().Any(e => 名字(e) == "NoteNow"), Is.True,
            "演奏中这一态没有「当前在发哪个音」那格");
    }

    // ==================== 接上的线 ====================

    /// <summary>
    /// 拓展样式得**真去要**，而且要在窗口显示之后要。
    ///
    /// 这是**文本级守卫**，不是行为验证 —— 它拦的是「哪天有人把这一句删了」，
    /// 删了之后悬浮层会开始吃点击、抢焦点，全程没有任何报错。
    /// 系统有没有真给它这三条，要真窗口加真前台窗口才验得了（归人工）。
    /// </summary>
    [Test]
    public void 拓展样式是去要了而且有个说得出口的地方()
    {
        string overlay = 读源文件("PerformerOverlayWindow.axaml.cs");

        Assert.Multiple(() =>
        {
            Assert.That(overlay, Does.Contain("OverlayWindowStyles.Apply"),
                "没去要那三条拓展样式（点击穿透 / 不激活 / 不进任务栏）");
            Assert.That(overlay, Does.Contain("StylesApplied"),
                "设不上的时候得有样东西说得出「没设上」—— 静默失败在这儿等于让人对着一个会吃点击的窗口查半天");
        });
    }

    /// <summary>
    /// 演奏器窗口要把「样式没设上」说出来（写进状态行），不能只是自己知道。
    ///
    /// 这条同样有两个**不同**的来路要说：悬浮层的样式（见上）和 F6 热键 —— 后者装不上时
    /// 用户唯一能用的急停就只剩窗口上那颗按钮了，而他多半已经切到游戏里去了。
    /// </summary>
    [Test]
    public void 设不上的东西会在状态行里说出来()
    {
        string performer = 读源文件("PerformerWindow.axaml.cs");

        Assert.Multiple(() =>
        {
            Assert.That(performer, Does.Contain("StylesApplied"),
                "悬浮层的拓展样式没设上，演奏器窗口却不说 —— 键会发到悬浮层身上，没有报错");
            Assert.That(performer, Does.Contain("Installed"),
                "F6 装不上也得说一句：那是演奏时唯一的急停手段");
        });
    }

    /// <summary>
    /// 编辑器工具栏上那颗「演奏器…」是**进演奏流程的唯一入口**（工单的原话是「启动演奏」，
    /// wireframe 里写的是「演奏器…」，界面上照 wireframe 走）。
    ///
    /// 只断言「有这颗按钮、而且挂上了命令」：挂着不接命令的按钮按下去什么都不发生，
    /// 而且不报错 —— 那正是这条工单要防的静默失败。
    /// </summary>
    [Test]
    public void 工具栏上的演奏入口是接上的()
    {
        var button = 主窗口().Descendants().FirstOrDefault(e => 名字(e) == "PerformerButton");

        Assert.That(button, Is.Not.Null, "工具栏上没有演奏入口，演奏流程只剩 04 那个独立窗口了");
        Assert.That(属性(button!, "Click"), Is.Not.Null.And.Not.Empty,
            "按钮挂在那儿但没接命令：按下去什么都不发生，也不报错");
    }
}
