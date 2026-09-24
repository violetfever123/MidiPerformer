using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MidiPerformer.App.Views;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 未保存弹窗（43 号工单）：三个场景各说什么、第二颗按钮穿不穿警示色，以及三处入口接上了没有。
///
/// 判据分两层，都**不起 Avalonia**：
/// - 「哪一处该说什么」是一张纯数据的表（<see cref="UnsavedPrompt.Of"/>），直接断言三格值；
/// - 「控件上真接上了」读源文件文本 —— 和 <c>ToolbarLayoutTests</c> 是同一套办法。
///   读进来的文本**先去注释**（<see cref="只读代码"/> / <see cref="只读标记"/>）：
///   源文件里的解释性文字既能把规则「说」成满足的，也能把规则「说」成违反的，
///   而断言要看的自始至终是代码和标记本身。
///
/// <b>这些断言证明了什么、没证明什么：</b>证明「表里这么写着、三处入口都调了它」。
/// 「弹窗真弹出来了、回车真的落在「是」上、✕ 真的什么都不做」是上机的事
/// （<c>.scratch/verify-43.ps1</c>）。但这两样**坏掉都不报错**：少接一处入口、
/// 第二颗顺手写成「否」、给「点演奏」那颗也穿上警示色 —— 界面上只是「跟说好的不一样」，没有一条会红。
///
/// 「点演奏」那一处是这张票最容易做错的地方（它按下去不丢东西），所以在这儿单列一条。
/// </summary>
public class UnsavedPromptTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string 读源文件(string name) => File.ReadAllText(Path.Combine(AppDir, "Views", name));

    // 断言看的是**代码**，不是散文。源文件里的注释会同时做两件坏事：
    // 注释里写着「取 Warn 而不是 Stop」能把 `.Not.Contain("TokenStop")` 弄红，
    // 注释里写着「{DynamicResource TokenWarnSoft}」又能把 `.Contain(...)` 弄绿。
    // 所以读进来先去注释 —— 断言的**一条都没松**，看的还是同一件事，只是不看解释。
    private static string 弹窗代码() => 只读代码(读源文件("UnsavedChangesDialog.axaml.cs"));

    private static string 主窗口代码() => 只读代码(读源文件("MainWindow.axaml.cs"));

    /// <summary>
    /// 摘掉 <c>//</c>、<c>///</c>、<c>/* */</c> 注释，只留代码。
    /// 字符串和字符字面量里的 <c>//</c>（比如一个网址）**不动** —— 那是代码，不是解释。
    /// </summary>
    private static string 只读代码(string source)
    {
        var kept = new System.Text.StringBuilder(source.Length);
        bool 在串里 = false, 在字符里 = false, 行注释 = false, 块注释 = false;

        for (int i = 0; i < source.Length; i++)
        {
            char 本 = source[i];
            char 下 = i + 1 < source.Length ? source[i + 1] : '\0';

            if (行注释)
            {
                if (本 != '\n') continue;
                行注释 = false;                       // 换行留着，行数才对得上
            }
            else if (块注释)
            {
                if (本 == '*' && 下 == '/') { 块注释 = false; i++; continue; }
                if (本 != '\n') continue;
            }
            else if (在串里 || 在字符里)
            {
                if (本 == '\\' && 下 != '\0') { kept.Append(本).Append(下); i++; continue; }
                if (本 == (在串里 ? '"' : '\'')) { 在串里 = false; 在字符里 = false; }
            }
            else if (本 == '"') 在串里 = true;
            else if (本 == '\'') 在字符里 = true;
            else if (本 == '/' && 下 == '/') { 行注释 = true; i++; continue; }
            else if (本 == '/' && 下 == '*') { 块注释 = true; i++; continue; }

            kept.Append(本);
        }

        return kept.ToString();
    }

    /// <summary>摘掉 <c>&lt;!-- --&gt;</c> 注释，只留标记。XAML 的注释是这一种，不是 <c>//</c>。</summary>
    private static string 只读标记(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>
    /// 按 <c>x:Name</c> 取一个真元素（走 XDocument，读的是**真属性**，注释冒充不了它）。
    /// </summary>
    private static XElement 弹窗(string name)
    {
        var found = XDocument.Load(Path.Combine(AppDir, "Views", "UnsavedChangesDialog.axaml"))
            .Descendants()
            .Where(e => e.Attribute(Xaml + "Name")?.Value == name)
            .ToList();

        Assert.That(found, Has.Count.EqualTo(1), $"弹窗 XAML 里该有且只有一个 x:Name=\"{name}\"");
        return found[0];
    }

    // ==================== 先证明「只看代码」这件事本身 ====================

    /// <summary>
    /// 上面那些断言全都建在「读进来的文本里没有散文」上，所以这条先把那把刀本身试一遍 ——
    /// 它要是哪天变成空转（注释没摘掉），上面那些断言就悄悄退化成「注释说到了也算数」。
    ///
    /// 顺带钉住另一头：**字符串里的 <c>//</c> 不是注释**，不能连代码一起吞掉。
    /// </summary>
    [Test]
    public void 去散文只摘注释不摘代码()
    {
        Assert.Multiple(() =>
        {
            Assert.That(只读代码("var a = 1; // TokenStop\nvar b = 2;"), Is.EqualTo("var a = 1; \nvar b = 2;"));
            Assert.That(只读代码("/* TokenStop */ var a = 1;"), Is.EqualTo(" var a = 1;"));
            Assert.That(只读代码("/// <summary>TokenStop</summary>\nvar a = 1;"), Is.EqualTo("\nvar a = 1;"));

            // 网址里的 `//` 是代码，得原样留着 —— 不然摘注释顺手把那行后半截吃了
            Assert.That(只读代码("var u = \"https://x//y\"; // TokenStop"), Is.EqualTo("var u = \"https://x//y\"; "));
            Assert.That(只读代码("char c = '\\''; // TokenStop"), Is.EqualTo("char c = '\\''; "));

            Assert.That(只读标记("<!-- TokenStop -->\n<Button />"), Is.EqualTo("\n<Button />"));
        });
    }

    // ==================== 那张三元的表 ====================

    /// <summary>三处场景就是三处 —— 多一处少一处都得先回来改这张表。</summary>
    [Test]
    public void 场景就是三处()
        => Assert.That(Enum.GetValues<UnsavedScene>(), Has.Length.EqualTo(3), "切曲子 / 点演奏 / 关窗口");

    /// <summary>
    /// 正文第二行、第二颗按钮文案、第二颗是否着色 —— 逐格对一遍。
    /// 三处**只差这三样**，别的都一样（第一行同一个它、两颗按钮的出口同一套意思）。
    /// </summary>
    [Test]
    public void 三处场景第二行和第二颗各说各的()
    {
        var 切曲子 = UnsavedPrompt.Of(UnsavedScene.SwitchSong);
        var 点演奏 = UnsavedPrompt.Of(UnsavedScene.Perform);
        var 关窗口 = UnsavedPrompt.Of(UnsavedScene.CloseWindow);

        Assert.Multiple(() =>
        {
            Assert.That(切曲子.SecondLine, Is.EqualTo("点「丢掉」就去打开另一首，这次的改动没了。"));
            Assert.That(切曲子.SecondButton, Is.EqualTo("丢掉"));
            Assert.That(切曲子.Warn, Is.True, "切曲子是真丢东西");

            Assert.That(点演奏.SecondLine, Is.EqualTo("点「用已存的」就拿曲库里存的那份去弹，改动留着。"));
            Assert.That(点演奏.SecondButton, Is.EqualTo("用已存的"));
            Assert.That(点演奏.Warn, Is.False, "它不丢东西");

            Assert.That(关窗口.SecondLine, Is.EqualTo("点「丢掉」就关掉，这次的改动没了。"));
            Assert.That(关窗口.SecondButton, Is.EqualTo("丢掉"));
            Assert.That(关窗口.Warn, Is.True, "关窗口也是真丢东西");
        });
    }

    /// <summary>
    /// **这张票最容易做错的一处**：点演奏那颗第二钮既不叫「丢掉」、也不穿警示色。
    ///
    /// 按下去**不丢东西** —— 弹的是曲库里存的那份，草稿还留在编辑器里，弹完还能接着改、接着存。
    /// 所以名字要写它真会干什么（用已存的），颜色也不给（警示色是留给「这条路会丢东西」的）。
    /// 顺手复制切曲子那一行的话，两条都不会报错，只是按钮在骗人。
    /// </summary>
    [Test]
    public void 点演奏那处不丢东西()
    {
        var 点演奏 = UnsavedPrompt.Of(UnsavedScene.Perform);

        Assert.Multiple(() =>
        {
            Assert.That(点演奏.SecondButton, Is.EqualTo("用已存的"), "它按下去不丢东西，名字就得说它真会干什么");
            Assert.That(点演奏.SecondButton, Is.Not.EqualTo("丢掉"));
            Assert.That(点演奏.Warn, Is.False, "不丢东西就不穿警示色");
            Assert.That(点演奏.SecondLine, Does.Contain("改动留着"), "第二行也得把「草稿还在」说出来");
        });
    }

    /// <summary>
    /// 会丢东西的那两处才穿警示色，而且穿的是 <c>TokenWarn</c>（弹窗 XAML 里那四条样式）——
    /// **不是** <c>TokenStop</c>：用户没做错什么，只是这条路会丢东西。
    /// </summary>
    [Test]
    public void 只有会丢东西的两处穿警示色()
    {
        Assert.Multiple(() =>
        {
            Assert.That(UnsavedPrompt.Of(UnsavedScene.SwitchSong).Warn, Is.True);
            Assert.That(UnsavedPrompt.Of(UnsavedScene.CloseWindow).Warn, Is.True);
            Assert.That(UnsavedPrompt.Of(UnsavedScene.Perform).Warn, Is.False);

            var 弹窗Xaml = 只读标记(读源文件("UnsavedChangesDialog.axaml"));
            Assert.That(弹窗Xaml, Does.Contain("{DynamicResource TokenWarnSoft}"));
            Assert.That(弹窗Xaml, Does.Contain("{DynamicResource TokenWarnLine}"));
            Assert.That(弹窗Xaml, Does.Contain("{DynamicResource TokenWarn}"));
            Assert.That(弹窗Xaml, Does.Not.Contain("TokenStop"),
                "警示色不是报错的红 —— 用户没做错什么，只是这条路会丢东西");
        });
    }

    /// <summary>
    /// 第二颗**不写「否」**：是 / 否回答的是标题那一问（「改动还没保存」），而那一问本身不是个问题。
    /// 写成它真会干什么之后，两颗各自把后果说出来，不用回头读标题。
    /// </summary>
    [Test]
    public void 第二颗不写否()
    {
        var 三颗 = Enum.GetValues<UnsavedScene>().Select(s => UnsavedPrompt.Of(s).SecondButton).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(三颗, Has.None.EqualTo("否"));
            Assert.That(三颗, Has.None.EqualTo("取消"), "取消是右上角那颗 ✕ 的事，不占一颗按钮");
        });
    }

    // ==================== 第一行 ====================

    /// <summary>
    /// 三处的正文**第一行逐字相同**，只有曲名不同 —— 因为「继续」在三处指的不是同一件事，
    /// 而这一行说的是三处共有的那件事（改过了、还没存）。
    ///
    /// 第一行压根不吃场景（<see cref="UnsavedPrompt.FirstLine"/> 只收一个曲名），
    /// 而且弹窗里只有一处给它赋值 —— 想让它按场景分叉都做不到。
    /// </summary>
    [Test]
    public void 第一行三处逐字相同()
    {
        Assert.Multiple(() =>
        {
            Assert.That(UnsavedPrompt.FirstLine("侏儒之歌"), Is.EqualTo("「侏儒之歌」改过了，还没存。"));
            Assert.That(UnsavedPrompt.FirstLine(""), Is.EqualTo("「这一份」改过了，还没存。"), "名字空着也得读得通");
            Assert.That(Regex.Matches(弹窗代码(), "FirstLineText.Text").Count, Is.EqualTo(1),
                "第一行只有一处赋值 —— 三处共用同一个它，分不出叉来");

            // 三处场景传的是同一个曲名来源（主窗口手上这份的曲名），所以三处读出来是同一句
            Assert.That(主窗口代码(), Does.Contain("UnsavedChangesDialog.AskAsync(owner, scene, _title)"));
        });
    }

    // ==================== 三颗出口 ====================

    /// <summary>
    /// 默认焦点在「是」：开窗后直接敲回车落在它身上。
    /// 两处判据缺一不可 —— `IsDefault` 让回车触发它，`Focus()` 让焦点圈看得见在它身上。
    /// </summary>
    [Test]
    public void 默认焦点在是()
    {
        var 是 = 弹窗("YesButton");

        Assert.Multiple(() =>
        {
            Assert.That(是.Attribute("Content")?.Value, Is.EqualTo("是"));
            Assert.That(是.Attribute("Classes")?.Value, Does.Contain("primary"), "「是」是主色那一颗");
            Assert.That(是.Attribute("IsDefault")?.Value, Is.EqualTo("True"), "回车得落在它身上");

            // 焦点圈：开窗那一下显式给
            Assert.That(弹窗代码(), Does.Contain("YesButton.Focus();"));
        });
    }

    /// <summary>
    /// ✕（和 Esc）= **什么都不做**：改动还在、歌还在、窗口还在。
    ///
    /// 这颗 ✕ 是系统标题栏画的，我们管不着它长什么样（平时素色、移上去才变红底白叉）。
    /// 能做的是让「关掉窗口」这条路**不改选择**：<c>Choice</c> 默认就停在
    /// <see cref="UnsavedChoice.Cancel"/>，而全文件只有两颗按钮的点击处理会改它 ——
    /// 所以叉掉 / 按 Esc 拿到的永远是「什么都不做」。
    /// </summary>
    [Test]
    public void 叉掉等于什么都不做()
    {
        var 代码 = 弹窗代码();

        Assert.Multiple(() =>
        {
            Assert.That(代码, Does.Contain(
                "public UnsavedChoice Choice { get; private set; } = UnsavedChoice.Cancel;"),
                "默认值就是「什么都不做」—— 调用方只判这一个值，不必再分怎么关的");

            // 全文件改了它两处：两颗按钮各一次，多一处就是「叉掉也干了点什么」
            Assert.That(Regex.Matches(代码, "Choice = ").Count, Is.EqualTo(2));
            Assert.That(代码, Does.Contain("Choice = UnsavedChoice.Save;"));
            Assert.That(代码, Does.Contain("Choice = UnsavedChoice.Continue;"));
        });
    }

    // ==================== 接在主窗口上 ====================

    /// <summary>
    /// 三处入口都接上了：切曲子（曲库打开另一首 / 导入）、点演奏、关窗口。
    /// 漏接一处不报错，只是那条路照样悄悄把改动丢掉 —— 而那正是这张票要修的东西。
    /// </summary>
    [Test]
    public void 三处入口都接上了()
    {
        var 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(代码, Does.Contain("ConfirmUnsavedAsync(UnsavedScene.SwitchSong"), "切曲子那处");
            Assert.That(代码, Does.Contain("ConfirmUnsavedAsync(UnsavedScene.Perform"), "点演奏那处");
            Assert.That(代码, Does.Contain("ConfirmUnsavedAsync(UnsavedScene.CloseWindow"), "关窗口那处");

            // 切曲子有两条路：曲库里打开另一首、导入一个 .mid。两条都得拦 ——
            // 导入那条拦在 ImportFile 里，挑文件和拖放两条路才一并盖得住
            Assert.That(Regex.Matches(代码, "ConfirmUnsavedAsync\\(UnsavedScene\\.SwitchSong").Count,
                Is.EqualTo(2), "曲库打开和导入各一处");
        });
    }

    /// <summary>
    /// 「要不要问」的判据是 <c>_dirty</c>（工具栏「保存」那一格穿不穿主色用的是同一个），
    /// **不是** <c>_edited</c> —— 后者是粘的：存过盘、撤销回原样都还写着 true，
    /// 拿它当判据会对着没改动的一份弹窗。
    /// </summary>
    [Test]
    public void 要不要问的判据还是那个dirty()
    {
        var 拦 = 花括号段(主窗口代码(), "private async Task<bool> ConfirmUnsavedAsync",
            "找不到 ConfirmUnsavedAsync");

        Assert.Multiple(() =>
        {
            Assert.That(拦, Does.Contain("if (!_dirty) return true;"), "没改动就直接放行，不弹窗");
            Assert.That(拦, Does.Not.Contain("_edited"), "「动过没有」那个粘性标记不是这一处的判据");
        });
    }

    /// <summary>
    /// 「是」= **先存再继续**：存下去了才放行。存不下去（问名字时取消了、或者没配曲库）
    /// 就停在原地 —— 继续下去正是要把改动丢掉，而用户刚才选的是「存」。
    /// </summary>
    [Test]
    public void 是就是先存再继续()
    {
        var 存 = 花括号段(主窗口代码(), "private async Task<bool> SaveFirstAsync", "找不到 SaveFirstAsync");

        Assert.Multiple(() =>
        {
            Assert.That(存, Does.Contain("await SaveAsync();"));
            Assert.That(存, Does.Contain("return !_dirty;"), "存下去才算数 —— 判据还是那个 _dirty");
        });
    }

    /// <summary>取一个方法/一段代码的花括号体（和 ToolbarLayoutTests 同一套办法）。</summary>
    private static string 花括号段(string source, string signature, string 找不到就说)
    {
        int open = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(open, Is.GreaterThanOrEqualTo(0), 找不到就说);

        open = source.IndexOf('{', open);
        Assert.That(open, Is.GreaterThanOrEqualTo(0), $"{signature} 后面没有花括号段");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"{signature} 的花括号没闭合");
        return "";
    }
}
