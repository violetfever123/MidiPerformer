using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 文案收口（26 号工单）：屏幕上写着快捷键的每一处，和**真按键**对得上。
///
/// 这一层没有行为可测 —— 提示写错了程序照样跑，只是人会以为功能坏了，然后去修一个没坏的东西。
/// 所以判据和 <see cref="TokenParityTests"/> 一样，是**文件文本**：
///
/// 1. 提示行**两层各自**点名的每个手势，<c>OnWindowKeyDown</c> 里都真的绑着（36 号分层的）
/// 2. 提示行单行 + 省略号，全文从 ToolTip 看，而 ToolTip 喂的是 Format 里那两个常量
/// 3. 分层的**判据**（选中集非空 → 编辑那一行）和 Esc 放开选中这两处在代码里真的那么写着
/// 4. 三处 ToolTip 各有各的着落：播放按钮改口、撤销 / 重做删掉、■ 停止 换成 ↻ 重头播放（33 号）
/// 5. 轨头的「改名」按钮确实没了（25 号工单留下的空档，这条只在 26 的验收单上收口）
///
/// **不起 Avalonia。** 这里量的是「文件里写着什么」，不是「控件摆成了什么样」——
/// 摆得对不对只能靠人眼看（见工单的验收单）。
/// </summary>
public class ShortcutHintTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    /// <summary>MidiPerformer 仓库根。</summary>
    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string AppDir => Path.Combine(RepoRoot, "MidiPerformer.App");

    private static string MainWindowAxaml => Path.Combine(AppDir, "Views", "MainWindow.axaml");

    private static string MainWindowCode => Path.Combine(AppDir, "Views", "MainWindow.axaml.cs");

    private static string TrackLaneAxaml => Path.Combine(AppDir, "Views", "TrackLaneView.axaml");

    // ==================== 1. 提示行 ↔ 真按键 ====================

    /// <summary>
    /// 提示里的一句话 ↔ 按键那一段里的落点。**这张表就是工单 26 的验收单本身**
    /// （空格、方向键、Shift、Ctrl+←→、Ctrl+↑↓、Delete），一行一条。
    ///
    /// 右边的片段是**判别式**，不是随便挑的：比如 <c>←/→</c> 那一行写的是
    /// <c>NudgeNotes(-grid, 0)</c> / <c>NudgeNotes(grid, 0)</c> 而不是光写 <c>NudgeNotes</c> ——
    /// 后者在 <c>↑/↓</c> 那一支里也有，绑错了方向照样绿。同理空格认的是 <c>TogglePlayback()</c>：
    /// 20 号工单之后它才是**切换**，退回去只调 <c>StartPlayback</c> 就该红。
    ///
    /// **36 号起这张表分成两张**，对应提示行的两层：<c>演奏</c>（没选中音时显示）和
    /// <c>编辑</c>（选中了音时显示）。每一行还必须出现在**它该在的那一行常量**里 ——
    /// 这一半是分层之后新加的：把一条从走带挪进编辑、或者反过来，两边都会红。
    ///
    /// 撤销 / 重做那两行**从表里撤了**：它们的键位还在（<c>Undo</c> / <c>Redo</c> 那两支），
    /// 但提示行里不再写它们（36 号，用户定的），所以对账表的这一头没有了对象 ——
    /// 它们现在由「操作」菜单那条测试守着（见下面第 4 组）。
    /// </summary>
    private static readonly (string Hint, string[] Binding)[] 演奏那一行 =
    {
        // 20：空格从「开始」改成播放 / 暂停
        ("空格 播放/暂停", new[] { "Key.Space", "TogglePlayback()" }),
        // 35：Shift+空格 是**另一个动作**，绑在**另一个方法**上。
        // 判别式挑的是 `Key.Space && shift` 这一整句 + `BackOneBarAndPlay()`：
        // 左边光写 `Key.Space` 的话，把这一支删了、空格那一支照样在，这条会假绿；
        // 右边光写方法名的话，绑到裸空格上（= 空格变成回跳，播放/暂停没了）也是绿的。
        ("Shift + 空格 回跳一小节并播放", new[] { "Key.Space && shift", "BackOneBarAndPlay()" }),
        // Ctrl + ←/→ 的对称那一半：在轨之间走（36 号归到演奏这一层：
        // 它换的是焦点轨，而焦点轨决定你弹哪条轨）
        ("Ctrl + ↑ ↓ 换轨", new[] { "ctrl && e.Key is Key.Up or Key.Down", "MoveFocus(" }),
    };

    private static readonly (string Hint, string[] Binding)[] 编辑那一行 =
    {
        // 09 方案 A：←/→ 移时间一格（十六分），和拖动吸的是同一个格
        ("← → 移时间（一格 = 十六分）", new[] { "NudgeNotes(-grid, 0)", "NudgeNotes(grid, 0)" }),
        // 09 方案 A：↑/↓ 移音高一个半音（方向不能反 —— 卷帘上高音在上）
        ("↑ ↓ 移音高", new[] { "NudgeNotes(0, 1)", "NudgeNotes(0, -1)" }),
        // Shift + ←/→：改时值，只动主选中那一个
        ("Shift + ← → 改时值", new[] { "Key.Left when shift", "Key.Right when shift", "NudgeLength(" }),
        // 18：「同轨」是那张工单的全部内容，绑到别处去了这句话就是假的。
        // 36 把说法改成「选同轨前/后一个音」—— 键和落点一个字节没动，改的只是叫法
        ("Ctrl + ← → 选同轨前/后一个音", new[] { "ctrl && e.Key is Key.Left or Key.Right", "JumpSelection(" }),
        // 19：Delete（Backspace 是同一个动作的第二个落点）
        ("Delete 删除", new[] { "Key.Delete or Key.Back", "DeleteSelection()" }),
        // 37：Esc 放开选中的音。**这一条是补写法**：36 号把动作做了（`Key.Escape` 那一支
        // 调 `ClearSelection()`），可屏幕上没有一处说得清它 —— 用户 2026-09-20 定了放哪儿：
        // 「取消选中放在『选中一些音符之后』的那个提示行」，也就是这一层。
        // 判别式挑的是 `Key.Escape` + `ClearSelection()`：左边光写前者的话，
        // 把这一支改成「Esc 什么都不做」也是绿的。
        ("Esc 取消选中", new[] { "Key.Escape", "ClearSelection()" }),
    };

    [Test]
    public void 提示里的每个手势在按键那一段里都真的绑着()
    {
        var handler = KeyHandlerBody();

        Assert.Multiple(() =>
        {
            foreach (var (hint, bindings, line, 哪一行) in
                     演奏那一行.Select(h => (h.Hint, h.Binding, Format.ReadoutHintPerforming, "演奏那一行"))
                     .Concat(编辑那一行.Select(h => (h.Hint, h.Binding, Format.ReadoutHintEditing, "编辑那一行"))))
            {
                Assert.That(line, Does.Contain(hint),
                    $"{哪一行}里没有「{hint}」—— 这一条要么被删了，要么改口了，要么挪到另一层去了，"
                    + "两边得一起动");

                foreach (var binding in bindings)
                    Assert.That(handler, Does.Contain(binding),
                        $"提示行写着「{hint}」，而 OnWindowKeyDown 里找不到 `{binding}` —— "
                        + "要么把这句话删掉（没有这条键就说不得），要么把键接回来");
            }
        });
    }

    /// <summary>
    /// 「一格 = 十六分」这句话是量出来的，不是形容词：格 = 四分音符的四分之一。
    ///
    /// 它值得单独一条，因为这一句是**唯一一句说得出「按一下走多远」的话** ——
    /// 提示里别的手势错了是「按了没反应」，这一句错了是「按了反应不对」，更难发现。
    /// 换算的真身是 <see cref="PianoRollGeometry.GridTicks"/>，和拖动吸的是同一个格。
    /// </summary>
    [Test]
    public void 一格真的是十六分()
    {
        Assert.That(Format.ReadoutHintEditing, Does.Contain("一格 = 十六分"),
            "这句话在**编辑**那一层里（走带那一层不涉及格）");

        // 480 PPQ 的曲子：一格 120 tick，正好是四分音符（一拍）的四分之一
        var tempo = new TempoMap(TimeDivision.PulsesPerQuarter(480), null,
            new[] { new TimeSignatureChange(0, 4, 4) });

        Assert.That(PianoRollGeometry.GridTicks(tempo), Is.EqualTo(120),
            "一格不再是十六分了 —— 提示里那句「一格 = 十六分」得跟着改");
        Assert.That(PianoRollGeometry.GridTicks(tempo) * 4, Is.EqualTo(480),
            "一格 = 四分音符 / 4 = 十六分音符");
    }

    // ==================== 2. 分层：显示哪一层、全文在哪 ====================

    /// <summary>
    /// 提示行**分两层**（36 号工单）：屏幕上是「此刻该看的那一类」，ToolTip 是两行合起来的全文。
    ///
    /// 四条断言各自挡一件事：
    ///
    /// - **两层不一样**：抄成同一句话的话，「分层」就只剩一个名字。
    /// - **判据是「选中集的个数 &gt; 0」**：这是用户 2026-09-20 拍的（另一个选项是「恰好一个才切」）。
    ///   写成 <c>== 1</c> 就该红 —— 那会把「选中一批」这种真实状态（Shift 点、框选）判到走带那一层去，
    ///   而 <c>← →</c> / <c>Delete</c> 动的正是那一批。
    /// - **由 <c>RefreshReadout</c> 喊**：改选中的每条路最后都汇到那儿（点、框选、Ctrl+←→、Escape、编辑、撤销），
    ///   另起一处喊就早晚有一条路忘了喊。
    /// - **ToolTip 喂的是两行合起来那个常量**，且**不在 XAML 里另设一份**（第二个真相源）。
    /// </summary>
    [Test]
    public void 提示行分两层_判据是选中集非空()
    {
        var code = File.ReadAllText(MainWindowCode);
        var hint = Element("HintText");

        Assert.Multiple(() =>
        {
            Assert.That(Format.ReadoutHintPerforming, Is.Not.EqualTo(Format.ReadoutHintEditing),
                "两层是两句话 —— 一样的话就不用分了");

            Assert.That(code, Does.Contain(
                "HintText.Text = hasNote ? Format.ReadoutHintEditing : Format.ReadoutHintPerforming;"),
                "换层那一句得按 hasNote 选常量（散在别处写字符串，就是两个会走散的真相源）");
            Assert.That(code, Does.Contain("controller.SelectedNotes.Count > 0"),
                "判据是**选中集非空**（用户拍的），不是「恰好一个」");
            Assert.That(code, Does.Not.Contain("SelectedNotes.Count == 1"),
                "「恰好一个才切」是这一票**没选**的那个方案");

            var readout = 花括号段(code, "private void RefreshReadout",
                "MainWindow 里找不到 RefreshReadout —— 提示行就没有换层的时机了");
            Assert.That(readout, Does.Contain("RefreshHint();"),
                "换层得由 RefreshReadout 喊 —— 它是改选中的唯一汇合点");

            Assert.That(code, Does.Contain("ToolTip.SetTip(HintText, Format.ReadoutHintTooltip);"),
                "ToolTip 喂的是**两行合起来**那个常量（全文），不是当前这一层");
            Assert.That(hint.Attribute("ToolTip.Tip"), Is.Null,
                "XAML 里再写一份 ToolTip 就是第二个真相源");
        });
    }

    /// <summary>
    /// `Esc` 放开选中的音，**但不动焦点轨**（36 号，用户 2026-09-20 的两条要求：
    /// 「当你单独选中一个音的时候，按 Escape 键可以取消选择这一个音」/
    /// 「但是焦点轨不能取消选择，必须选一个」）。
    ///
    /// 这一条只能量文件文本（<c>Esc</c> 那一支的**分支体**）：判据有三样 ——
    /// 它确实调了 <c>ClearSelection()</c>、它**没有**碰焦点轨、它把读数 / 卷帘都重画了
    ///（不重画的话选中没了而屏幕上那圈高亮还在）。
    /// </summary>
    [Test]
    public void Escape放开选中但不动焦点轨()
    {
        var code = File.ReadAllText(MainWindowCode);
        var 分支 = 花括号段(code, "if (e.Key == Key.Escape)",
            "MainWindow 里找不到 Esc 那一支 —— 放开选中就没地方接了");

        Assert.Multiple(() =>
        {
            Assert.That(分支, Does.Contain("controller.ClearSelection();"),
                "Esc 要放开选中的音（用户要求 1）");
            Assert.That(分支, Does.Not.Contain("FocusedTrack"),
                "**焦点轨不动**（用户要求 2）—— 它一直有一条，Esc 不碰它");
            Assert.That(分支, Does.Not.Contain("MoveFocus"),
                "同上：Esc 不是换轨");
            Assert.That(分支, Does.Contain("RefreshReadout();"),
                "放开之后读数和提示行都得回位（提示行要回走带那一层）");
            Assert.That(分支, Does.Contain("RefreshView();"),
                "卷帘上那圈选中高亮也得跟着灭");
        });
    }

    // ==================== 2. 提示行的长相与全文 ====================

    /// <summary>
    /// 提示行**单行 + 省略号**，窄窗截掉的那半行从 ToolTip 看全。
    ///
    /// 两条断言是一对，缺一条就自相矛盾：<c>TextTrimming</c> 只在**横向溢出**时才出省略号，
    /// 而 <c>TextWrapping="Wrap"</c> 恰恰让横向不溢出 —— 两个一起写，省略号永远不出现，
    /// 窄窗只是把读数栏撑成两行高（那就是工单说的「变形」）。
    ///
    /// 第三条断言（XAML 里不许写 ToolTip）的理由是**两个地方都能设 = 有一个是死的**：
    /// ToolTip 在代码里按同一个常量设（见下一条测试），XAML 再写一份的话谁生效要看加载顺序。
    /// 文案上不该有「哪份生效」这种问题。
    /// </summary>
    [Test]
    public void 提示行单行出省略号_全文在ToolTip里()
    {
        var hint = Element("HintText");

        Assert.Multiple(() =>
        {
            Assert.That(Attr(hint, "TextTrimming"), Is.EqualTo("CharacterEllipsis"),
                "窄窗要出省略号 —— 这是「屏幕够宽就全显示、窄了看 ToolTip」那条规矩的下半截");
            Assert.That(Attr(hint, "TextWrapping"), Is.Not.EqualTo("Wrap"),
                "Wrap 和 CharacterEllipsis 是互斥的：换行了就不横向溢出，省略号永远不出现，"
                + "窄窗只会把读数栏撑成两行高");
            Assert.That(hint.Attribute("ToolTip.Tip"), Is.Null,
                "ToolTip 由代码按 Format.ReadoutHintTooltip 设（一句喂两处）—— XAML 里再写一份就是第二个真相源");
        });
    }

    /// <summary>
    /// 提示行的字**只有一个来源**：<see cref="Format"/> 里那两个常量。视图这一头（XAML 也好、
    /// 代码也好）**不许抄第二份**。
    ///
    /// 这条是 26 号那张工单的原话推出来的：「一条快捷键要是屏幕上没有一处说得出它，就等于没有」——
    /// 反过来也成立：同一句话在屏幕上说两遍，改的时候就只改得动一遍。
    /// 抄一份的下场很具体：XAML 里那 `<c>Text</c>` 会和代码里设的那份**打架**，
    /// 谁赢取决于加载顺序，而这种 bug 只在改文案的那天现形。
    ///
    /// 逐字比对的活儿归 <c>FormatTests</c>，这里管的是「有没有第二个地方也在写这句话」。
    /// </summary>
    [Test]
    public void 提示行的字只有Format一处来源()
    {
        var code = File.ReadAllText(MainWindowCode);
        var hint = Element("HintText");

        Assert.Multiple(() =>
        {
            Assert.That(hint.Attribute("Text"), Is.Null,
                "XAML 里写死一份 Text，就和构造函数里设的那份打架了");

            // 两行里各自挑一段**只可能来自常量**的话当作探针：抄一份整行的人不会只抄半个词。
            // 探针必须带上键名（「Shift + 空格 …」而不只是「回跳一小节并播放」）——
            // 后半句在 MainWindow 的注释里是有的（讲 35 号那一支），光认后半句会误报。
            foreach (var 探针 in new[]
                     {
                         "Shift + 空格 回跳一小节并播放",
                         "移时间（一格 = 十六分）",
                         "Ctrl + ← → 选同轨前/后一个音",
                         "Esc 取消选中",
                     })
                Assert.That(code, Does.Not.Contain(探针),
                    $"MainWindow.axaml.cs 里出现了「{探针}」这个字面量 —— 提示行的字得从 Format 来"
                    + "（抄一份就有「改了 Format、屏幕上没变」的那一天）");

            // 赋值那两处（构造函数起手一下 + RefreshHint 换层）都得指着常量
            Assert.That(code, Does.Contain("HintText.Text = Format.ReadoutHintPerforming;"),
                "起手那一下说的是「还没载曲子、什么都没选中」那个状态");
            Assert.That(code, Does.Contain("ToolTip.SetTip(HintText, Format.ReadoutHintTooltip);"),
                "ToolTip 喂的是**两行合起来**那个常量（全文），不是当前这一层");
        });
    }

    // ==================== 3. 三处 ToolTip 各有各的着落 ====================

    /// <summary>
    /// 播放按钮的提示要和 20 号工单的**实际行为**一致：它是一颗切换键
    /// （▶ 播放 / ⏸ 暂停 / ▶ 继续），不是「开始试听」。
    ///
    /// 所以这里量两样：头两个字必须是「播放 / 暂停」（和按钮上会变的字同款），
    /// 以及那句「（空格键同效）」得留着 —— 它是一条真的绑定，不是套话。
    /// </summary>
    [Test]
    public void 播放按钮的提示说的是切换而不是开始()
    {
        var tip = Attr(Element("PlayButton"), "ToolTip.Tip");

        Assert.Multiple(() =>
        {
            Assert.That(tip, Does.StartWith("播放 / 暂停"),
                "按钮上的字在 ▶ 播放 / ⏸ 暂停 / ▶ 继续 之间换，提示得说得出它是一颗切换键");
            Assert.That(tip, Does.Contain("（空格键同效）"),
                "空格和它是同一条路（见 OnWindowKeyDown 的空格那一支），这句话不是套话");
        });
    }

    /// <summary>
    /// 撤销 / 重做的快捷键**只出现在菜单项右侧那一处**（23 号工单印上去的 InputGesture）。
    ///
    /// 所以这条是三半：ToolTip 一个都不许有（不能同一件事说两遍）、
    /// InputGesture 一个都不能丢（删了 ToolTip 又丢了它，快捷键就在屏幕上消失了）、
    /// 以及 36 号之后**提示行那两行和它们的 ToolTip 里也不许再有**。
    ///
    /// 第三半是用户 2026-09-20 亲口定的：「CTRL+Y、CTRL+Z 及『撤销』与『重做』这两个点
    /// 不需要单独写，将它们作为快捷键，直接放到『操作』里面作为提示就可以了」。
    /// 这是一条**减法**，最容易的翻车方式是「顺手在 ToolTip 里补一份全文」——
    /// 而那条 ToolTip 正是「看全」的出口，它一多，屏幕上就又多了一处。
    /// </summary>
    [Test]
    public void 撤销和重做的快捷键只在菜单项右侧说一次()
    {
        var undo = Element("UndoMenuItem");
        var redo = Element("RedoMenuItem");

        Assert.Multiple(() =>
        {
            Assert.That(undo.Attribute("ToolTip.Tip"), Is.Null, "撤销的 ToolTip 归 26 号删掉");
            Assert.That(redo.Attribute("ToolTip.Tip"), Is.Null, "重做的 ToolTip 归 26 号删掉");

            Assert.That(Attr(undo, "InputGesture"), Is.EqualTo("Ctrl+Z"),
                "ToolTip 删了之后，Ctrl+Z 在屏幕上就只剩这一处了");
            Assert.That(Attr(redo, "InputGesture"), Is.EqualTo("Ctrl+Y"),
                "同上 —— 菜单项右侧那一处不能说没就没");

            // 36 号：提示行两处（屏幕那一行 + ToolTip 那份全文）里都不许再有它们
            foreach (var 地方 in new[]
                     {
                         (Name: "演奏那一行", Text: Format.ReadoutHintPerforming),
                         (Name: "编辑那一行", Text: Format.ReadoutHintEditing),
                         (Name: "提示行的 ToolTip", Text: Format.ReadoutHintTooltip),
                     })
            foreach (var 字 in new[] { "撤销", "重做", "Ctrl+Z", "Ctrl+Y" })
                Assert.That(地方.Text, Does.Not.Contain(字),
                    $"{地方.Name}里出现了「{字}」—— 用户要的是「不需要单独写」，"
                    + "它们归「操作」菜单项右侧那一处");
        });
    }

    /// <summary>
    /// `■ 停止` **整个没了**（33 号工单把它换成了 `↻ 重头播放`），它那句 ToolTip
    /// 「停下来（急停是 F6）」跟着一起走。
    ///
    /// 26 号钉的是「这句话一个字都没动」；33 号改了那颗按钮的语义，那句话就作废了 ——
    /// 但这条测试**不删**，改钉新事实：
    ///
    /// - 走带条上没有 `■ 停止` 了（谁把它加回来，先在这里红）
    /// - 顶上那颗是 `↻ 重头播放`，提示语说的是它真做的事
    ///
    /// 「急停是 F6」这句提示没丢：它本来就在**演奏器窗口**
    /// （`PerformerWindow.axaml` 的「F6 急停」、悬浮层里两处），而全局钩子只在那个窗口
    /// 开着的时候才装着 —— 编辑窗口里这句是第二处，撤掉它反而更准。
    /// </summary>
    [Test]
    public void 停止那颗按钮换成了重头播放()
    {
        var buttons = XDocument.Load(MainWindowAxaml).Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(buttons.Any(b => (string?)b.Attribute(Xaml + "Name") == "StopButton"), Is.False,
                "`■ 停止` 归 33 号删掉了，别加回来");

            var restart = buttons.Single(b => (string?)b.Attribute(Xaml + "Name") == "RestartButton");
            Assert.That((string?)restart.Attribute("Content"), Is.EqualTo("↻ 重头播放"),
                "图标是 ↻（U+21BB）：和旁边的 ▶ ⏸ 一样是字符，不引图标依赖");
            Assert.That((string?)restart.Attribute("ToolTip.Tip"),
                Is.EqualTo("重头播放：播放头回开头、视野回第一小节，立刻开始放"),
                "提示语得说清按下去会发生什么（26 号的口径）——「回开头」和「立刻放」两件都要在");
        });
    }

    // ==================== 4. 轨头没有「改名」按钮 ====================

    /// <summary>
    /// 25 号工单把轨头的 `改名` 按钮删了（名字就地可编辑），按钮没了 ToolTip 自然跟着走。
    ///
    /// 这条挂在 26 的验收单上，是因为**前一张工单删了按钮、这一张才来收口文案**：
    /// 谁把按钮加回来，会先在这里红，而不是等到有人发现轨头上多了一颗没人解释的按钮。
    /// </summary>
    [Test]
    public void 轨头不再有改名按钮()
    {
        var offenders = XDocument.Load(TrackLaneAxaml)
            .Descendants()
            .Where(e => ((string?)e.Attribute("Content"))?.Contains("改名") == true)
            .Select(e => e.Name.LocalName + " Content=\"改名\"")
            .ToList();

        Assert.That(offenders, Is.Empty, "轨头改名按钮随 25 号工单删掉了，别加回来：\n" + string.Join("\n", offenders));
    }

    // ==================== 读文件 ====================

    /// <summary>MainWindow.axaml 里 `x:Name` 指到的那个元素。</summary>
    private static XElement Element(string name)
    {
        var found = XDocument.Load(MainWindowAxaml)
            .Descendants()
            .Where(e => (string?)e.Attribute(Xaml + "Name") == name)
            .ToList();

        Assert.That(found, Has.Count.EqualTo(1), $"MainWindow.axaml 里该有且只有一个 x:Name=\"{name}\"");
        return found[0];
    }

    /// <summary>取属性，取不到就红（键名拼错、属性被删都属于「取不到」）。</summary>
    private static string Attr(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        Assert.That(value, Is.Not.Null, $"{element.Name.LocalName} 上没有 {name}");
        return value!;
    }

    /// <summary>
    /// <c>OnWindowKeyDown</c> 的**方法体**（从签名到配对的那个右花括号）。
    ///
    /// 判据必须是方法体，不能是整个文件：整文件里 <c>NudgeLength</c> / <c>MoveFocus</c> 这些方法
    /// 本身就在，绑定那一支被删掉了照样找得到 —— 那样上面那张对账表就成了摆设。
    /// </summary>
    private static string KeyHandlerBody() => 花括号段(
        File.ReadAllText(MainWindowCode), "private void OnWindowKeyDown",
        "MainWindow 里找不到 OnWindowKeyDown —— 提示行就没有真身可对了");

    /// <summary>
    /// 从 <paramref name="signature"/> 那一处起，数到**配对的那个右花括号**为止的那一段。
    ///
    /// 为什么靠数不靠找下一个成员：这几段里都有 `is InputElement { Focusable: true }`
    /// 这种**代码里的花括号**，找「下一个空行 + private」那种写法会被它带偏。
    /// 用法上只认 <c>IndexOf</c> 的**第一次**出现 —— 所以 <paramref name="signature"/>
    /// 得挑够长的一段（<c>"if (e.Key == Key.Escape)"</c> 这种），不能只写 <c>"if"</c>。
    ///
    /// 36 号抽出来是因为要量第三段（Esc 那一支）了：与其再抄一遍数括号的循环，
    /// 不如让三处走同一条 —— 数括号这个假设哪天不成立，只该红一次。
    /// </summary>
    private static string 花括号段(string source, string signature, string 找不到就说)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), 找不到就说);

        int open = source.IndexOf('{', start);
        Assert.That(open, Is.GreaterThanOrEqualTo(0), $"{signature} 后面没有花括号段");

        for (int i = open, depth = 0; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"{signature} 的花括号不配对 —— 上面那个数括号的假设不成立了");
        return string.Empty;
    }
}
