using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 工具栏的**排法与状态**（42 号工单：排法 B + 空状态）。
///
/// 判据和 <see cref="OverlaySurfaceTests"/> 一样：**读 XAML / 读源文件文本，不起 Avalonia**。
/// 起一个真窗口在 NUnit 里要一台有桌面会话的机器，而这些结论本来就写在文件里。
///
/// <b>这些断言证明了什么、没证明什么：</b>它们证明的是「文件里这么写着」——
/// 组容器用的是哪个令牌、空状态关的是哪几样、穿主色的那一格只有一颗。
/// 「屏幕上真的素着」「填色真的只有那一格」「描边真的比邻居淡」是**像素**上的事，
/// 归上机截图（<c>.scratch/verify-42.ps1</c>），那儿才是逐格比的地方。
///
/// 那还守它做什么：这几条**坏掉都不报错**。多一格穿上主色、空状态漏关一样、
/// 组容器换成 TokenLine —— 界面上都只是「看着不太对」，没有一条会红。所以至少拦住「哪天被人改回去」。
/// </summary>
public class ToolbarLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static XDocument 主窗口() => XDocument.Load(Path.Combine(AppDir, "Views", "MainWindow.axaml"));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "MainWindow.axaml.cs"));

    private static IEnumerable<XElement> 全部元素() => 主窗口().Descendants();

    private static string? 名字(XElement e) => e.Attribute(Xaml + "Name")?.Value;

    private static string? 属性(XElement e, string name) => e.Attribute(name)?.Value;

    /// <summary>`x:Name` 指到的那个元素，有且只有一个。</summary>
    private static XElement 元素(string name)
    {
        var found = 全部元素().Where(e => 名字(e) == name).ToList();
        Assert.That(found, Has.Count.EqualTo(1), $"MainWindow.axaml 里该有且只有一个 x:Name=\"{name}\"");
        return found[0];
    }

    /// <summary>工具栏左边那一排（从「歌曲库」那一头数起的那串）。</summary>
    private static XElement 左边一排() => 全部元素()
        .First(e => e.Name.LocalName == "StackPanel" && e.Elements().Any(c => 名字(c) == "LibraryButton"));

    // ==================== 排法 ====================

    /// <summary>
    /// 排法 B（左起）：歌曲库 · [保存 │ 另存为…] · 导入 MIDI… · 导出 · 操作 ▾ · 演奏。
    ///
    /// 用「谁挨着谁」来钉，而不是数个数：顺序错了也伤不到功能，所以没有别的东西会红 ——
    /// 而排法就是这一票要交的东西。
    ///
    /// <b>「导入 MIDI…」和「导出」在这张表里不是常住户</b>：后者要被「另存为…」吸收掉，
    /// 前者要搬去曲库窗口的右上角。两颗在台面上的**相对次序照旧**（原来那个「文件」下拉里
    /// 就是「导入」排第一）—— 这一票只把它们提上来，不重排。在这一票里**一颗都不许撤**：
    /// 撤了「导出」，在它被吸收之前就没有任何路径能把曲子存到任意位置；
    /// 撤了「导入 MIDI…」，整条入库的路当场断掉。两颗各有各的工单去搬。
    /// </summary>
    [Test]
    public void 排法B的次序()
    {
        var 左起 = 左边一排().Elements().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(名字(左起[0]), Is.EqualTo("LibraryButton"), "第一位是「歌曲库」");
            Assert.That(属性(左起[1], "Classes"), Is.EqualTo("savegroup"), "第二位是存盘组（一个带描边的容器）");
            Assert.That(名字(左起[2]), Is.EqualTo("ImportButton"), "存盘组后面跟着还没搬走的「导入 MIDI…」（下拉里它排第一）");
            Assert.That(名字(左起[3]), Is.EqualTo("ExportButton"), "再跟着还没搬走的「导出」");
            Assert.That(属性(左起[4], "Classes"), Is.EqualTo("toolbar"), "第五位是「操作 ▾」那个菜单");
            Assert.That(名字(左起[5]), Is.EqualTo("PerformerButton"), "最后是「演奏」");
            Assert.That(左起, Has.Count.EqualTo(6), "左边这一排是六样（存盘组算一样）");
        });
    }

    /// <summary>
    /// 存盘组里是「保存 / 另存为…」两颗，中间一道竖分隔。
    /// 两颗都用 <c>Button.menubar</c>：干净态下它们跟旁边四样长得一模一样（这一票的「六样全素」）。
    /// </summary>
    [Test]
    public void 存盘组里是保存和另存为两颗()
    {
        var 组 = 元素("SaveGroup");
        var 内容 = 组.Descendants().Where(e => e.Name.LocalName is "Button" or "Border").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(属性(组, "Classes"), Is.EqualTo("savegroup"));
            Assert.That(名字(元素("SaveButton")), Is.EqualTo("SaveButton"));
            Assert.That(属性(元素("SaveButton"), "Classes"), Is.EqualTo("menubar"), "干净态下「保存」跟别的入口一个长相");
            Assert.That(属性(元素("SaveAsButton"), "Classes"), Is.EqualTo("menubar"));

            // 三个孩子：保存、竖分隔、另存为 —— 中间的竖分隔就是「这是两颗」那句话
            Assert.That(内容.Select(e => 名字(e) ?? 属性(e, "Classes")).ToList(),
                Is.EqualTo(new[] { "SaveButton", "savegroup-sep", "SaveAsButton" }),
                "组里该是「保存 │ 另存为…」");

            // 组容器自己的框是描边，不是底色块；两颗按钮自己不带底色（底色归组容器与 .dirty 那条）
            Assert.That(属性(元素("SaveButton"), "Background"), Is.Null,
                "「保存」的底色只能来自 .dirty 那条样式，写死在 XAML 里就没法随状态变了");
            Assert.That(属性(元素("SaveAsButton"), "Background"), Is.Null);
        });
    }

    /// <summary>
    /// 组容器的描边是 <c>TokenLineSoft</c>，**不是** <c>TokenLine</c>。
    ///
    /// 这条是这一票最容易做错、又最看不出来的一处：换成 TokenLine 也照样有个框，
    /// 只是跟旁边那几颗独立入口的边一样重，「这是个组」就说不出来了。
    /// 组里那道竖分隔跟着用同一个令牌 —— 一个组只有一种边框浓度。
    /// </summary>
    [Test]
    public void 组容器的描边是软的()
    {
        var 组样式 = 样式("Border.savegroup");
        var 分隔样式 = 样式("Border.savegroup-sep");

        Assert.Multiple(() =>
        {
            Assert.That(设(组样式, "BorderBrush"), Is.EqualTo("{DynamicResource TokenLineSoft}"),
                "组容器的描边得用 TokenLineSoft");
            Assert.That(设(组样式, "BorderBrush"), Is.Not.EqualTo("{DynamicResource TokenLine}"),
                "TokenLine 是旁边那几颗独立入口的边的浓度 —— 用了它「这是个组」就看不出来");
            Assert.That(设(组样式, "BorderThickness"), Is.EqualTo("1"), "描边一像素");
            Assert.That(设(分隔样式, "Background"), Is.EqualTo("{DynamicResource TokenLineSoft}"));
        });
    }

    /// <summary>
    /// 「有没存的东西」只让「保存」那一格穿主色：XAML 里带 <c>.dirty</c> 的样式填的是
    /// <c>TokenAccent</c>，而不带 <c>.dirty</c> 的工具栏样式一个都碰不到主色。
    ///
    /// 后半条是这条测试真正值钱的地方：`Button.menubar` 那几条样式要是哪条顺手写了主色，
    /// 干净态就不是「六样全素」了 —— 而那同样不报错。
    /// </summary>
    [Test]
    public void 主色只画在保存那一格的dirty样式上()
    {
        var menubar样式 = 主窗口().Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Where(e => 属性(e, "Selector")?.Contains("menubar") == true)
            .ToList();

        var 碰主色的 = menubar样式
            .Where(s => s.Descendants().Any(d => 属性(d, "Value")?.Contains("TokenAccent") == true))
            .Select(s => 属性(s, "Selector"))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(碰主色的, Is.Not.Empty, "「保存」那格穿主色的样式不见了 —— 那这个状态就没有出口了");
            Assert.That(碰主色的, Is.All.Contains(".dirty"),
                "工具栏上除了「有没存的东西」那一格，别的样式一个都不许碰主色（干净态六样全素）");
            Assert.That(碰主色的, Is.All.Contains("Button.menubar"),
                "碰主色的必须是那几颗入口按钮的样式，不是别的什么");
        });
    }

    // ==================== 空状态 ====================

    /// <summary>
    /// 空状态（还没装曲子）里：`保存` / `另存为…` / `操作` / `演奏` 四样灰掉，
    /// **「歌曲库」亮着**（它是唯一亮着的那颗 —— 第一首得从那儿拿进来）。
    ///
    /// 四条判据都必须在 <c>RefreshEditState</c> 一处算：散在载入 / 编辑 / 存盘各写一遍的话，
    /// 迟早有一条路忘了写，而「该亮的没亮」不会报错。
    /// </summary>
    [Test]
    public void 空状态里四样灰掉而歌曲库亮着()
    {
        var 刷新 = 花括号段(主窗口代码(), "private void RefreshEditState", "找不到 RefreshEditState");

        Assert.Multiple(() =>
        {
            Assert.That(刷新, Does.Contain("OperationMenu.IsEnabled = _song is not null;"), "没曲子可动时「操作」要灰");
            Assert.That(刷新, Does.Contain("PerformerButton.IsEnabled = _song is not null;"), "没曲子可弹时「演奏」要灰");
            Assert.That(刷新, Does.Contain("SaveButton.IsEnabled = _song is not null && _library is not null;"), "没曲子时「保存」要灰");
            Assert.That(刷新, Does.Contain("SaveAsButton.IsEnabled = SaveButton.IsEnabled;"), "「另存为…」跟「保存」同一条判据");
            Assert.That(刷新, Does.Contain("ExportButton.IsEnabled = _song is not null;"), "没曲子可导出时「导出」要灰");

            // 曲库这一条里**不许出现 _song**：它亮不亮跟手上有没曲子无关，只跟配没配曲库有关 ——
            // 空状态里它是唯一亮着的那颗
            Assert.That(刷新, Does.Contain("LibraryButton.IsEnabled = _library is not null;"),
                "「歌曲库」的判据只有曲库那一半 —— 空状态里只剩它亮着");
            Assert.That(刷新, Does.Not.Contain("LibraryButton.IsEnabled = _song"), "「歌曲库」不该跟着曲子灰");

            // 「导入 MIDI…」也不灰：空状态里它正是该用的那一颗（整条入库的路），
            // 而且它现在还没搬走，搬走之前必须一直能用
            Assert.That(刷新, Does.Not.Contain("ImportButton.IsEnabled"), "「导入 MIDI…」不跟着曲子灰");
            Assert.That(属性(元素("ImportButton"), "IsEnabled"), Is.Null, "「导入 MIDI…」XAML 里也不置灰");
        });
    }

    /// <summary>
    /// 「歌曲名」那一格空状态里是**整个藏掉**，不是置灰：没装曲子时它不是「按不动」，
    /// 是「这儿现在没有东西」。判据是 <c>IsVisible</c>，不是 <c>IsEnabled</c> ——
    /// 两者只差一个词，屏幕上差一整格。
    ///
    /// 藏的是**整格**（标签 + 名字框），所以两者必须装在同一个容器里：
    /// 只藏名字框的话，屏幕上会剩一个光秃秃的「歌曲名」挂在那儿。
    /// </summary>
    [Test]
    public void 空状态里歌曲名那一格是整个藏掉()
    {
        var 刷新 = 花括号段(主窗口代码(), "private void RefreshEditState", "找不到 RefreshEditState");
        var 格 = 元素("SongNameCell");

        Assert.Multiple(() =>
        {
            Assert.That(刷新, Does.Contain("SongNameCell.IsVisible = _song is not null;"),
                "空状态藏的是「那一格」—— 判据得是 IsVisible");
            Assert.That(刷新, Does.Not.Contain("SongNameCell.IsEnabled"),
                "置灰不是这一票要的（用户原话：「歌曲名」那一格整个隐藏）");

            // 标签和名字框住在同一格里，藏一个等于藏两个
            Assert.That(格.Descendants().Any(e => 属性(e, "Text") == "歌曲名"), Is.True, "「歌曲名」那三个字得在格子里");
            Assert.That(格.Descendants().Any(e => 名字(e) == "SongNameBox"), Is.True, "名字框也得在格子里");
        });
    }

    /// <summary>
    /// 载入一首曲子之后四样恢复、名字格回来，**不需要重启**：判据全在
    /// <c>RefreshEditState</c> 一处，而 <c>LoadSong</c> 会喊它。换曲子那一趟是同一个实例，
    /// 所以「回来」这件事只要「载入这条路会重算」就够了。
    /// </summary>
    [Test]
    public void 载入之后当场恢复不用重启()
    {
        var 载入 = 花括号段(主窗口代码(), "private void LoadSong", "找不到 LoadSong");

        Assert.That(载入, Does.Contain("RefreshEditState();"),
            "载入一首曲子之后得重算工具栏那几样 —— 不然要重启才回得来");
    }

    // ==================== 「有改动」那个状态 ====================

    /// <summary>
    /// 穿主色那一格**只有一颗**：<c>RefreshEditState</c> 里只许出现一次 <c>Classes.Set</c>，
    /// 而且它点的是 <c>SaveButton</c>。
    ///
    /// 「为什么不整组一起变」——丢的是「你改了没存」这件事，「另存为」没这个意思。
    /// 整组一起变也照样能跑，所以这条不红的话没人会发现。
    /// </summary>
    [Test]
    public void 穿主色的只有保存那一格()
    {
        var 刷新 = 花括号段(主窗口代码(), "private void RefreshEditState", "找不到 RefreshEditState");

        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(刷新, Regex.Escape("Classes.Set")).Count, Is.EqualTo(1),
                "RefreshEditState 里只该给一颗按钮换类 —— 多一颗就是多一格穿主色");
            Assert.That(刷新, Does.Contain("SaveButton.Classes.Set(\"dirty\", _dirty);"),
                "「有没存的东西」是「保存」那一格的状态，不是组容器的、也不是「另存为…」的");
            Assert.That(刷新, Does.Not.Contain("SaveAsButton.Classes"), "「另存为…」不跟着变");
        });
    }

    /// <summary>
    /// 「有没存的东西」和「这份工程动过没有」是**两个**标记，不能合并。
    ///
    /// <c>_edited</c> 是粘的：写进工程头，给曲库列表那格「改过 / 没动过」用 ——
    /// 存盘不清、撤销回原样也不清。工具栏那一格答的是另一个问题（现在这份和盘上那份对不对得上），
    /// 所以载入和存盘都要清零。合成一个的话，一首存过又重开的曲子一上来就穿着主色，
    /// 而那正是用户说的「载入之后是普通态」。
    /// </summary>
    [Test]
    public void 没存的东西和动过没有是两个标记()
    {
        string 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(花括号段(代码, "private void ApplySong", "找不到 ApplySong"), Does.Contain("_dirty = true;"),
                "每一次编辑之后手上这份就和盘上那份对不上了");
            Assert.That(花括号段(代码, "private void LoadSong", "找不到 LoadSong"), Does.Contain("_dirty = false;"),
                "刚装上来的这一份和盘上那份是一致的");
            Assert.That(花括号段(代码, "private void SaveTo", "找不到 SaveTo"), Does.Contain("_dirty = false;"),
                "存下去之后又对上了，主色该褪下来");

            // 工程头里那个还是粘性的 _edited：曲库列表那格「改过 / 没动过」靠它
            Assert.That(花括号段(代码, "private void SaveTo", "找不到 SaveTo"), Does.Contain("_edited, _importedFrom"),
                "写进工程头的还得是 _edited（粘性标记），别顺手换成 _dirty");
            Assert.That(花括号段(代码, "private void ApplySong", "找不到 ApplySong"), Does.Contain("_edited = true;"),
                "_edited 归曲库列表那格小字，谁也不许把它删了");
        });
    }

    /// <summary>存下去之后主色得当场褪下来 —— 所以存盘那条路也要重算一次工具栏状态。</summary>
    [Test]
    public void 存盘之后工具栏当场重算()
    {
        var 存 = 花括号段(主窗口代码(), "private void SaveTo", "找不到 SaveTo");

        Assert.Multiple(() =>
        {
            Assert.That(存, Does.Contain("RefreshEditState();"), "存完不重算的话，主色一直挂着直到你换个曲子");
            Assert.That(存.IndexOf("_dirty = false;", StringComparison.Ordinal),
                Is.LessThan(存.IndexOf("RefreshEditState();", StringComparison.Ordinal)),
                "先清标记再重算，反过来的话算的还是一秒前那个状态");
        });
    }

    // ==================== 还没搬走的两颗 ====================

    /// <summary>
    /// 「导入 MIDI…」和「导出」这一票**都不许撤**，而且两颗都得接上命令 ——
    /// 挂着不接命令的按钮按下去什么都不发生、也不报错。
    ///
    /// 撤「导入 MIDI…」最狠：撤掉它、而曲库窗口那颗还没建起来的那段时间里，
    /// **没有任何路径能把一首曲子导进来**（整条入库的路断了）。
    /// 撤「导出」是「存不到任意位置」。两颗各有各的工单去搬，这一票只排位置。
    /// </summary>
    [Test]
    public void 还没搬走的两颗都在而且都接上了命令()
    {
        string 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(属性(元素("ImportButton"), "Click"), Is.EqualTo("OnImportClick"),
                "「导入 MIDI…」撤了整条入库的路就断在这一票里了");
            Assert.That(属性(元素("ExportButton"), "Click"), Is.EqualTo("OnExportClick"),
                "「导出」撤了就没有路径能存到任意位置（等「另存为…」接管它的活再删）");
            Assert.That(代码, Does.Contain("private async void OnImportClick"), "接的命令得真在");
            Assert.That(代码, Does.Contain("private async void OnExportClick"), "同上");
        });
    }

    /// <summary>
    /// 「文件」下拉拆掉了：XAML 里不许再有那个 <c>Header="文件"</c> 的菜单项。
    /// 它里面四样东西各有去处（存盘组两颗 + 还没搬走的两颗），一个都不会因为拆下拉而消失。
    /// </summary>
    [Test]
    public void 文件那个下拉拆掉了()
    {
        var 剩下 = 全部元素()
            .Where(e => e.Name.LocalName == "MenuItem" && 属性(e, "Header") == "文件")
            .ToList();

        Assert.That(剩下, Is.Empty, "「文件」下拉归这一票拆掉，四样东西都搬到台面上了");
    }

    /// <summary>
    /// 主窗口里那句「还没有曲子」的提示**不许再指向「文件」菜单** ——
    /// 那个下拉没了，照旧写着就是指了一条走不通的路。
    ///
    /// 「导入 MIDI…」搬去曲库窗口之后这句话照样成立（那儿也只有这一颗按钮叫这个名字）。
    /// </summary>
    [Test]
    public void 空状态提示不再指向文件菜单()
    {
        string 提示 = 属性(元素("EmptyHint"), "Text") ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(提示, Does.Contain("导入 MIDI…"), "得指一条现在真有的路");
            Assert.That(提示, Does.Not.Contain("「文件」"), "「文件」菜单已经拆掉了");
        });
    }

    // ==================== 读文件 ====================

    /// <summary>Window.Styles 里 `Selector` 指到的那条样式。</summary>
    private static XElement 样式(string selector)
    {
        var found = 主窗口().Descendants()
            .Where(e => e.Name.LocalName == "Style" && 属性(e, "Selector") == selector)
            .ToList();

        Assert.That(found, Has.Count.EqualTo(1), $"MainWindow.axaml 里该有且只有一条 `{selector}` 样式");
        return found[0];
    }

    /// <summary>样式里某个属性的值。取不到就红（属性被删、拼错都算取不到）。</summary>
    private static string? 设(XElement style, string property)
    {
        var setter = style.Elements().FirstOrDefault(e => 属性(e, "Property") == property);
        Assert.That(setter, Is.Not.Null, $"`{属性(style, "Selector")}` 上没有 {property} 这一条");
        return 属性(setter!, "Value");
    }

    /// <summary>
    /// 从 <paramref name="signature"/> 那一处起，数到配对的那个右花括号为止的那一段。
    /// 同 <c>ShortcutHintTests</c> 里那个：靠数不靠找下一个成员。
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
