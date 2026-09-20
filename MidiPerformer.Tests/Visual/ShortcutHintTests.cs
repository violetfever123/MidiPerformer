using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 屏幕上写着快捷键的每一处，和真按键对得上。
/// 判据是文件文本，和 <see cref="TokenParityTests"/> 一样：不起 Avalonia，
/// 量的是「文件里写着什么」，不是「控件摆成了什么样」。
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

    // ==================== 提示行 ↔ 真按键 ====================

    /// <summary>
    /// 提示里的一句话 ↔ 按键那一段里的落点，一行一条，对应提示行的两层：
    /// 演奏（没选中音时显示）和编辑（选中了音时显示）。
    ///
    /// 右边是判别式，挑得够独特 —— 如 <c>←/→</c> 认的是 <c>NudgeNotes(-grid, 0)</c>
    /// 而不是光写 <c>NudgeNotes</c>（后者在 <c>↑/↓</c> 那一支里也有），
    /// 空格认的是 <c>TogglePlayback()</c>（退回去只调 <c>StartPlayback</c> 就该红）。
    ///
    /// 每一行还必须出现在它该在的那一行常量里，把一条从演奏挪进编辑（或反过来）两边都会红。
    /// 撤销 / 重做不在表里：它们由「操作」菜单那条测试守着。
    /// </summary>
    private static readonly (string Hint, string[] Binding)[] 演奏那一行 =
    {
        ("空格 播放/暂停", new[] { "Key.Space", "TogglePlayback()" }),
        // Shift+空格 是另一个动作，判别式认整句 `Key.Space && shift` + 方法名 `BackOneBarAndPlay()`
        ("Shift + 空格 回跳一小节并播放", new[] { "Key.Space && shift", "BackOneBarAndPlay()" }),
        // Ctrl + ↑↓ 换轨：换的是焦点轨，而焦点轨决定你弹哪条轨，所以归演奏这一层
        ("Ctrl + ↑ ↓ 换轨", new[] { "ctrl && e.Key is Key.Up or Key.Down", "MoveFocus(" }),
    };

    private static readonly (string Hint, string[] Binding)[] 编辑那一行 =
    {
        // ←/→ 移时间一格（十六分），和拖动吸的是同一个格
        ("← → 移时间（一格 = 十六分）", new[] { "NudgeNotes(-grid, 0)", "NudgeNotes(grid, 0)" }),
        // ↑/↓ 移音高一个半音（卷帘上高音在上，方向不能反）
        ("↑ ↓ 移音高", new[] { "NudgeNotes(0, 1)", "NudgeNotes(0, -1)" }),
        // Shift + ←/→：改时值，只动主选中那一个
        ("Shift + ← → 改时值", new[] { "Key.Left when shift", "Key.Right when shift", "NudgeLength(" }),
        ("Ctrl + ← → 选同轨前/后一个音", new[] { "ctrl && e.Key is Key.Left or Key.Right", "JumpSelection(" }),
        // Delete（Backspace 是同一个动作的第二个落点）
        ("Delete 删除", new[] { "Key.Delete or Key.Back", "DeleteSelection()" }),
        // Esc 放开选中的音：判别式是 `Key.Escape` + `ClearSelection()`（光认前者，
        // 把这一支改成「Esc 什么都不做」也是绿的）
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
    /// 「一格 = 十六分」的文字和换算对得上：格 = 四分音符的四分之一，
    /// 出自 <see cref="PianoRollGeometry.GridTicks"/>，和拖动吸的是同一个格。
    /// </summary>
    [Test]
    public void 一格真的是十六分()
    {
        Assert.That(Format.ReadoutHintEditing, Does.Contain("一格 = 十六分"),
            "这句话在**编辑**那一层里（走带那一层不涉及格）");

        // 480 PPQ 的曲子：一格 120 tick
        var tempo = new TempoMap(TimeDivision.PulsesPerQuarter(480), null,
            new[] { new TimeSignatureChange(0, 4, 4) });

        Assert.That(PianoRollGeometry.GridTicks(tempo), Is.EqualTo(120),
            "一格不再是十六分了 —— 提示里那句「一格 = 十六分」得跟着改");
        Assert.That(PianoRollGeometry.GridTicks(tempo) * 4, Is.EqualTo(480),
            "一格 = 四分音符 / 4 = 十六分音符");
    }

    // ==================== 分层：显示哪一层、全文在哪 ====================

    /// <summary>
    /// 提示行分两层：屏幕上是此刻该看的那一类，ToolTip 是两行合起来的全文。
    /// 换层的判据是「选中集非空」（不是「恰好一个」），由 <c>RefreshReadout</c> 喊，
    /// 且 ToolTip 不在 XAML 里另设一份。
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
    /// Esc 放开选中的音，但不动焦点轨：<c>Esc</c> 那一支的分支体里
    /// 只调 <c>ClearSelection()</c>，不碰焦点轨，并把读数 / 卷帘都重画。
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

    // ==================== 提示行的长相与全文 ====================

    /// <summary>
    /// 提示行单行 + 省略号，窄窗截掉的那半行从 ToolTip 看全。
    /// <c>TextTrimming</c> 只在横向溢出时才出省略号，所以 <c>TextWrapping="Wrap"</c>
    /// 不能一起写（换行了就不溢出，省略号永远不出现）；ToolTip 也不在 XAML 里另设一份。
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
    /// 提示行的字只有 <see cref="Format"/> 里那两个常量一个来源，视图这一头不许抄第二份
    /// （XAML 里写死一份 Text 会和代码里设的那份打架，谁生效看加载顺序）。
    /// 逐字比对归 <c>FormatTests</c>，这里管的是有没有第二个地方也在写这句话。
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

            // 探针必须带上键名（「Shift + 空格 …」而不只是「回跳一小节并播放」）——
            // 后半句在 MainWindow 的注释里也有，光认后半句会误报
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

            // 赋值那两处（构造函数起手 + RefreshHint 换层）都得指着常量
            Assert.That(code, Does.Contain("HintText.Text = Format.ReadoutHintPerforming;"),
                "起手那一下说的是「还没载曲子、什么都没选中」那个状态");
            Assert.That(code, Does.Contain("ToolTip.SetTip(HintText, Format.ReadoutHintTooltip);"),
                "ToolTip 喂的是**两行合起来**那个常量（全文），不是当前这一层");
        });
    }

    // ==================== 三处 ToolTip 各有各的着落 ====================

    /// <summary>
    /// 播放按钮的提示说的是切换（▶ 播放 / ⏸ 暂停 / ▶ 继续），而不是「开始试听」；
    /// 「（空格键同效）」那句是真绑定，得留着。
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
    /// 撤销 / 重做的快捷键只在菜单项右侧那一处（InputGesture）说一次：
    /// 两处 ToolTip 一个都不许有，提示行那两行和提示行的 ToolTip 里也不许再有。
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

            // 提示行两处（屏幕那一行 + ToolTip 那份全文）里都不许再有它们
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
    /// 走带条上没有 <c>■ 停止</c> 了，顶上那颗是 <c>↻ 重头播放</c>，提示语说的是它真做的事。
    /// （「急停是 F6」那句提示在演奏器窗口，不归这里管。）
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

    // ==================== 轨头没有「改名」按钮 ====================

    /// <summary>轨头上没有「改名」按钮（名字就地可编辑）。</summary>
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

    /// <summary>取属性，取不到就红（键名拼错、属性被删都算取不到）。</summary>
    private static string Attr(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        Assert.That(value, Is.Not.Null, $"{element.Name.LocalName} 上没有 {name}");
        return value!;
    }

    /// <summary>
    /// <c>OnWindowKeyDown</c> 的方法体（从签名到配对的那个右花括号）。
    /// 必须是方法体不能是整个文件：整文件里 <c>NudgeLength</c> / <c>MoveFocus</c> 这些方法
    /// 本身就在，绑定那一支被删了照样找得到。
    /// </summary>
    private static string KeyHandlerBody() => 花括号段(
        File.ReadAllText(MainWindowCode), "private void OnWindowKeyDown",
        "MainWindow 里找不到 OnWindowKeyDown —— 提示行就没有真身可对了");

    /// <summary>
    /// 从 <paramref name="signature"/> 那一处起，数到配对的那个右花括号为止的那一段。
    ///
    /// 靠数不靠找下一个成员：段里有 <c>is InputElement { Focusable: true }</c> 这种代码花括号。
    /// 只认 <c>IndexOf</c> 的第一次出现，所以 <paramref name="signature"/> 得挑够长的一段
    /// （<c>"if (e.Key == Key.Escape)"</c> 这种），不能只写 <c>"if"</c>。
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
