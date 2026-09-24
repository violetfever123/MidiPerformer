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
    /// 编辑器工具栏上那颗「演奏」是**进演奏流程的唯一入口**（工单的原话是「启动演奏」，
    /// wireframe 里写的是「演奏器…」；40 号工单把界面上的字收成了「演奏」，
    /// 按用户那句「将「演奏器」与「文件操作」的表述形式统一」）。
    ///
    /// 只断言「有这颗按钮、而且挂上了命令」：挂着不接命令的按钮按下去什么都不发生，
    /// 而且不报错 —— 那正是这条工单要防的静默失败。
    ///
    /// 名字是 `PerformerButton`，和那颗按钮上的字**各走各的**：40 号改的是字，不是这个名字。
    /// 这条测试按名字找它，所以改文案时改坏这一处，红的是它。
    /// </summary>
    [Test]
    public void 工具栏上的演奏入口是接上的()
    {
        var button = 主窗口().Descendants().FirstOrDefault(e => 名字(e) == "PerformerButton");

        Assert.That(button, Is.Not.Null, "工具栏上没有演奏入口，演奏流程只剩 04 那个独立窗口了");
        Assert.That(属性(button!, "Click"), Is.Not.Null.And.Not.Empty,
            "按钮挂在那儿但没接命令：按下去什么都不发生，也不报错");
    }

    // ==================== 演奏器窗口的行 ====================

    private static XElement 演奏器() =>
        XDocument.Load(Path.Combine(AppDir, "Views", "PerformerWindow.axaml")).Root!;

    /// <summary>窗口里所有写明了名字的控件（<c>x:Name</c>）。</summary>
    private static List<string> 控件名(XElement 窗) =>
        窗.Descendants().Select(名字).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();

    /// <summary>窗口上画出来的字：TextBlock / Run 的 Text、Button 的 Content、ComboBox 的占位。</summary>
    private static List<string> 控件字(XElement 窗) =>
        窗.Descendants()
            .Select(e => e.Attribute("Text")?.Value
                      ?? e.Attribute("Content")?.Value
                      ?? e.Attribute("PlaceholderText")?.Value)
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();

    /// <summary>窗口上按钮的字（<c>Content</c>）。占位文字不算 —— 「先打开一首 MIDI」是下拉框的占位，不是一颗按钮。</summary>
    private static List<string> 按钮字(XElement 窗) =>
        窗.Descendants().Where(e => e.Name.LocalName == "Button")
            .Select(e => e.Attribute("Content")?.Value)
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();

    /// <summary>
    /// 47 号：演奏器窗口上不再有「曲目」行、不再有「基准八度」行 —— 连它们留下的控件一起。
    /// 曲子由曲库窗口决定（<c>docs/spec-界面改版.md</c> 的「演奏器 —— 行级决定」），
    /// 基准八度永远自动。
    ///
    /// 文本级守卫：证明的是「文件里没有这些东西」。窗口真打开时什么样归实机（47 的验收记录里那条）。
    /// 拦的是「哪天有人把这一行抄回来」—— 多一行不报错，只是用户又得在两个地方挑曲子、挑八度。
    /// </summary>
    [Test]
    public void 演奏器上没有曲目行也没有基准八度行()
    {
        var 名 = 控件名(演奏器());
        var 字 = 控件字(演奏器());
        var 钮 = 按钮字(演奏器());

        Assert.Multiple(() =>
        {
            Assert.That(名, Has.None.EqualTo("SongValue"), "「曲目」行那块读数还在");
            Assert.That(名, Has.None.EqualTo("OpenButton"), "「打开 MIDI…」那颗按钮还在");
            Assert.That(名, Has.None.EqualTo("BaseOctaveCombo"), "「基准八度」那个下拉还在");
            Assert.That(字, Has.None.EqualTo("曲目"), "「曲目」这个标签还在");
            Assert.That(字, Has.None.EqualTo("基准八度"), "「基准八度」这个标签还在");
            Assert.That(钮.Any(t => t.Contains("打开 MIDI")), Is.False,
                "还留着一颗写着「打开 MIDI…」的按钮 —— 那是「曲目」行的入口");
        });
    }

    /// <summary>
    /// 47 号：三个下拉都还在，而且打开窗口时后两个停在「标准」和「5 秒」。
    /// 选中项写在 XAML 的 <c>SelectedIndex</c> 上（构造器里又设了一遍，两处对得上）。
    ///
    /// 证明的是「文件里写的是 1」；窗口真打开时显示的是哪一档归实机。
    /// 演奏轨的选中项**不写死**：默认第一条能弹的轨是曲子接进来之后由代码挑的。
    /// </summary>
    [Test]
    public void 演奏器打开时停在标准档和五秒()
    {
        var 下拉 = 演奏器().Descendants().Where(e => e.Name.LocalName == "ComboBox").ToList();
        var 时序 = 下拉.SingleOrDefault(e => 名字(e) == "TimingCombo");
        var 倒计 = 下拉.SingleOrDefault(e => 名字(e) == "CountdownCombo");

        // 找不到时给一句看得懂的话，别让它变成 NullReference
        static string 选中项(XElement? e) => e is null ? "★没有这个下拉" : 属性(e, "SelectedIndex") ?? "★没写 SelectedIndex";

        Assert.Multiple(() =>
        {
            Assert.That(下拉.Select(名字), Does.Contain("TrackCombo"), "「演奏轨」那个下拉没了");
            Assert.That(下拉.Select(名字), Does.Contain("TimingCombo"), "「时序」那个下拉没了");
            Assert.That(下拉.Select(名字), Does.Contain("CountdownCombo"), "「倒计时」那个下拉没了");
            Assert.That(选中项(时序), Is.EqualTo("1"), "时序默认该停在第 1 项 = 标准档");
            Assert.That(选中项(倒计), Is.EqualTo("1"), "倒计时默认该停在第 1 项 = 5 秒");
        });

        Assert.That(读源文件("PerformerWindow.axaml.cs"), Does.Contain("\"3 秒\", \"5 秒\", \"10 秒\""),
            "倒计时的三个选项被改过了 —— 47 只拨默认项，不动选项本身");
    }

    /// <summary>
    /// 47 号定的行序（也是 48–51 往哪儿插东西）：演奏轨在最上面，时序与倒计时成对压在按钮上面。
    /// 只钉这几者**前后**的关系，中间插进来什么（按键速度 / 微调 / 38 根细条）都不影响它。
    /// </summary>
    [Test]
    public void 演奏器的行序是演奏轨在上时序倒计时压着按钮()
    {
        var 顺序 = 演奏器().Descendants().ToList();
        int 位(string 名) => 顺序.FindIndex(e => 名字(e) == 名);

        int 轨 = 位("TrackCombo"), 时 = 位("TimingCombo"), 倒 = 位("CountdownCombo");
        int 始 = 位("StartButton"), 停 = 位("StopButton");

        Assert.Multiple(() =>
        {
            Assert.That(new[] { 轨, 时, 倒, 始, 停 }, Has.None.EqualTo(-1),
                "有几个控件根本找不到，下面的顺序就无从谈起");
            Assert.That(轨, Is.LessThan(时), "演奏轨该在最上面（下面那几块读数都跟着它变）");
            Assert.That(时, Is.LessThan(倒), "倒计时该跟在时序下面");
            Assert.That(倒, Is.LessThan(始), "时序与倒计时成对，一起压在按钮上面");
            Assert.That(始, Is.LessThan(停), "开始演奏在急停上面");
        });
    }

    // ==================== 急停那三处提示 ====================

    /// <summary>
    /// 48 号：急停提示一共三处 —— 「开始演奏」那颗按钮上的小字、悬浮层的倒计时与演奏中两句 ——
    /// 写的都是<b>任意键</b>，一处 F6 的说法都不留。
    /// F6 当然还是能停（它本来就落在「非修饰键」里），但提示不再只说它：
    /// 用户要的是「手离开键盘之后随手按哪个都行」，不是「去找那个键」。
    ///
    /// 悬浮层**倒计时**那一句单独钉一条，因为它和别的两句不是一回事：倒计时那几秒判定压根不认键
    /// （见 <c>GlobalHotkeys.ShouldStop</c> 的阶段门），那几秒能取消的只有窗口上那颗「急停」。
    /// 所以它<b>不能</b>写成「按任意键取消」—— 那时候按下去什么都不会发生，
    /// 而那句话会让人以为它该有反应（正是这张票要修的那类毛病）。
    ///
    /// 文本级守卫：证明的是「文件里写的是这句」；窗口上真显示成什么样归实机（48 的验收记录）。
    /// </summary>
    [Test]
    public void 急停提示三处写的都是任意键()
    {
        var 演奏器上的字 = 控件字(演奏器());
        var 悬浮层上的字 = 控件字(悬浮层());

        string 倒计时那句 = 悬浮层上的字.Single(t => t.Contains("急停") && t.Contains("取消"));

        Assert.Multiple(() =>
        {
            Assert.That(演奏器上的字, Does.Contain("任意键急停"),
                "「开始演奏」那颗按钮上的小字还是老写法");

            // 先 ToList 再数：Where 出来的是惰性迭代器，没有 Count 属性，
            // Has.Count 是反射找属性，会抛 ArgumentException（不是断言失败）—— 红得很难看懂。
            Assert.That(悬浮层上的字.Where(t => t.Contains("任意键")).ToList(), Has.Count.EqualTo(2),
                "悬浮层那两句（倒计时 / 演奏中）要各写一处「任意键」");

            Assert.That(倒计时那句, Does.Contain("急停"),
                "倒计时那几秒只有窗口上那颗「急停」能取消，这句得把它指出来");
            Assert.That(倒计时那句, Does.Not.Contain("按任意键取消"),
                "倒计时期间判定不认键，写成「按任意键取消」就是一句按下去没反应的话");

            foreach (var (地方, 字) in new[] { ("演奏器窗口", 演奏器上的字), ("悬浮层", 悬浮层上的字) })
                Assert.That(字.Where(t => t.Contains("F6")), Is.Empty,
                    $"{地方}上还留着 F6 的说法 —— 三处提示都不该再点名某个键");
        });
    }
}
