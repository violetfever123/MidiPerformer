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
/// 1. 提示行（<see cref="Format.ReadoutHint"/>）里点名的每个手势，<c>OnWindowKeyDown</c> 里都真的绑着
/// 2. 提示行单行 + 省略号，全文从 ToolTip 看，而 ToolTip 喂的是**同一个常量**（不是抄的第二份）
/// 3. 三处 ToolTip 各有各的着落：播放按钮改口、撤销 / 重做删掉、■ 停止 换成 ↻ 重头播放（33 号）
/// 4. 轨头的「改名」按钮确实没了（25 号工单留下的空档，这条只在 26 的验收单上收口）
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
    /// （空格、方向键、Shift、Ctrl+←→、Ctrl+↑↓、Delete、Ctrl+Z/Y），一行一条。
    ///
    /// 右边的片段是**判别式**，不是随便挑的：比如 <c>←/→</c> 那一行写的是
    /// <c>NudgeNotes(-grid, 0)</c> / <c>NudgeNotes(grid, 0)</c> 而不是光写 <c>NudgeNotes</c> ——
    /// 后者在 <c>↑/↓</c> 那一支里也有，绑错了方向照样绿。同理空格认的是 <c>TogglePlayback()</c>：
    /// 20 号工单之后它才是**切换**，退回去只调 <c>StartPlayback</c> 就该红。
    /// </summary>
    private static readonly (string Hint, string[] Binding)[] HintToBinding =
    {
        // 20：空格从「开始」改成播放 / 暂停
        ("空格 播放/暂停", new[] { "Key.Space", "TogglePlayback()" }),
        // 09 方案 A：←/→ 移时间一格（十六分），和拖动吸的是同一个格
        ("← → 移时间（一格 = 十六分）", new[] { "NudgeNotes(-grid, 0)", "NudgeNotes(grid, 0)" }),
        // 09 方案 A：↑/↓ 移音高一个半音（方向不能反 —— 卷帘上高音在上）
        ("↑ ↓ 移音高", new[] { "NudgeNotes(0, 1)", "NudgeNotes(0, -1)" }),
        // Shift + ←/→：改时值，只动主选中那一个
        ("Shift + ← → 改时值", new[] { "Key.Left when shift", "Key.Right when shift", "NudgeLength(" }),
        // 18：「同轨」是那张工单的全部内容，绑到别处去了这句话就是假的
        ("Ctrl + ← → 同轨前后跳", new[] { "ctrl && e.Key is Key.Left or Key.Right", "JumpSelection(" }),
        // Ctrl + ←/→ 的对称那一半：在轨之间走
        ("Ctrl + ↑ ↓ 换轨", new[] { "ctrl && e.Key is Key.Up or Key.Down", "MoveFocus(" }),
        // 19：Delete（Backspace 是同一个动作的第二个落点）
        ("Delete 删除", new[] { "Key.Delete or Key.Back", "DeleteSelection()" }),
        // 撤销 / 重做：Ctrl+Shift+Z 也走重做那条，这里只认 Ctrl+Z / Ctrl+Y 两个写法
        ("Ctrl+Z 撤销", new[] { "Key.Z && !shift", "Undo()" }),
        ("Ctrl+Y 重做", new[] { "Key.Y", "Redo()" }),
    };

    [Test]
    public void 提示里的每个手势在按键那一段里都真的绑着()
    {
        var handler = KeyHandlerBody();

        Assert.Multiple(() =>
        {
            foreach (var (hint, bindings) in HintToBinding)
            {
                Assert.That(Format.ReadoutHint, Does.Contain(hint),
                    $"提示行里没有「{hint}」—— 这一条要么被删了，要么改口了，两边得一起动");

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
        Assert.That(Format.ReadoutHint, Does.Contain("一格 = 十六分"));

        // 480 PPQ 的曲子：一格 120 tick，正好是四分音符（一拍）的四分之一
        var tempo = new TempoMap(TimeDivision.PulsesPerQuarter(480), null,
            new[] { new TimeSignatureChange(0, 4, 4) });

        Assert.That(PianoRollGeometry.GridTicks(tempo), Is.EqualTo(120),
            "一格不再是十六分了 —— 提示里那句「一格 = 十六分」得跟着改");
        Assert.That(PianoRollGeometry.GridTicks(tempo) * 4, Is.EqualTo(480),
            "一格 = 四分音符 / 4 = 十六分音符");
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
                "ToolTip 由代码按 Format.ReadoutHint 设（一句喂两处）—— XAML 里再写一份就是第二个真相源");
        });
    }

    /// <summary>
    /// 显示的那一行和悬停看全的那份**是同一个常量**，不是抄出来的两份。
    ///
    /// 抄一份的代价很具体：改提示的人只会改 Format 里那一句，于是屏幕上挂着新的半行
    /// 和 ToolTip 里的旧全文 —— 那时 ToolTip 比不写更坏，它「证实」了一句已经被推翻的话。
    /// </summary>
    [Test]
    public void 显示的那行和悬停看的那份是同一句()
    {
        var code = File.ReadAllText(MainWindowCode);

        Assert.Multiple(() =>
        {
            Assert.That(code, Does.Contain("HintText.Text = Format.ReadoutHint;"),
                "提示行的字得从 Format 来");
            Assert.That(code, Does.Contain("ToolTip.SetTip(HintText, Format.ReadoutHint);"),
                "ToolTip 得喂**同一个常量** —— 抄一份就有新半行配旧全文的那一天");
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
    /// 所以这条是两半：ToolTip 一个都不许有（不能同一件事说两遍），
    /// InputGesture 一个都不能丢（删了 ToolTip 又丢了它，快捷键就在屏幕上消失了）。
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
    /// 花括号靠数，不靠找下一个成员：这个方法里有 `is InputElement { Focusable: true }` 这种
    /// 代码里的花括号，找「下一个空行 + private」那种写法会被它带偏。
    /// </summary>
    private static string KeyHandlerBody()
    {
        const string signature = "private void OnWindowKeyDown";
        var source = File.ReadAllText(MainWindowCode);

        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "MainWindow 里找不到 OnWindowKeyDown —— 提示行就没有真身可对了");

        int open = source.IndexOf('{', start);
        Assert.That(open, Is.GreaterThanOrEqualTo(0), "OnWindowKeyDown 后面没有方法体");

        for (int i = open, depth = 0; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail("OnWindowKeyDown 的花括号不配对 —— 上面那个数括号的假设不成立了");
        return string.Empty;
    }
}
