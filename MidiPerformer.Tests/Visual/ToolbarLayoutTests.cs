using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 工具栏的**排法与状态**（42 号工单：排法 B + 空状态）。后来接上了 54 号（「导出」被吸收、
/// 「另存为…」跟「保存」分岔）和 55 号（「导入 MIDI…」搬去曲库窗口）——
/// **那两票的判据也在这儿**，因为「这颗按钮现在在哪儿、还接不接着命令」是一件事：
/// 撤和建分在两个文件里，分开钉的话总有一头坏掉不报错。
///
/// 判据和 <see cref="OverlaySurfaceTests"/> 一样：**读 XAML / 读源文件文本，不起 Avalonia**。
/// 起一个真窗口在 NUnit 里要一台有桌面会话的机器，而这些结论本来就写在文件里。
///
/// <b>这些断言证明了什么、没证明什么：</b>它们证明的是「文件里这么写着」——
/// 组容器用的是哪个令牌、空状态关的是哪几样、穿主色的那一格只有一颗、那颗按钮长在哪条
/// Border 的最后一格。**「屏幕上真的素着」「那颗按钮真的在右上角」「文件框真的开在曲库窗口
/// 头上」是像素 / 焦点上的事**，归上机截图（<c>tools/uitest/</c> 那套），那儿才是逐格比的地方。
///
/// 那还守它做什么：这几条**坏掉都不报错**。多一格穿上主色、空状态漏关一样、
/// 组容器换成 TokenLine、那颗按钮搬走之后处理函数还留着 —— 界面上都只是「看着不太对」，
/// 没有一条会红。所以至少拦住「哪天被人改回去」。
/// </summary>
public class ToolbarLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static XDocument 主窗口() => XDocument.Load(Path.Combine(AppDir, "Views", "MainWindow.axaml"));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "MainWindow.axaml.cs"));

    private static XDocument 曲库面板() => XDocument.Load(Path.Combine(AppDir, "Views", "SongLibraryPanel.axaml"));

    private static string 曲库面板代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "SongLibraryPanel.axaml.cs"));

    private static string 曲库窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "SongLibraryWindow.axaml.cs"));

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

    /// <summary>
    /// 曲库面板里那条标题行：含「歌曲库」三个字的那个 <c>Border</c>（有且只有一个）。
    /// 「导入 MIDI…」就住在这条里 —— 「右上角」说的是这条的最后一格。
    /// </summary>
    private static XElement 曲库标题行()
    {
        var found = 曲库面板().Descendants()
            .Where(e => e.Name.LocalName == "Border" && e.Descendants().Any(d => 属性(d, "Text") == "歌曲库"))
            .ToList();

        Assert.That(found, Has.Count.EqualTo(1),
            "SongLibraryPanel.axaml 里该有且只有一条标题行（含「歌曲库」那个 Border）");
        return found[0];
    }

    /// <summary>工具栏左边那一排（从「歌曲库」那一头数起的那串）。</summary>
    private static XElement 左边一排() => 全部元素()
        .First(e => e.Name.LocalName == "StackPanel" && e.Elements().Any(c => 名字(c) == "LibraryButton"));

    // ==================== 排法 ====================

    /// <summary>
    /// 排法 B（左起）：歌曲库 · [保存 │ 另存为…] · 操作 ▾ · 演奏。
    ///
    /// 用「谁挨着谁」来钉，而不是数个数：顺序错了也伤不到功能，所以没有别的东西会红 ——
    /// 而排法就是这一票要交的东西。
    ///
    /// <b>⚠️ 这张表一行里短过两次，两次都不是重排：</b>
    /// <list type="bullet">
    /// <item>54 号：「导出」被「另存为…」吸收掉（42 号故意留着它，等的就是那一刻，
    /// 见 <see cref="导出那颗按钮和处理函数都删干净了"/>）。</item>
    /// <item>55 号：「导入 MIDI…」搬去曲库窗口右上角（见 <see cref="导入那颗从工具栏撤干净了"/>）。
    /// 它在这张表里的**相对次序照旧**（原来那个「文件」下拉里它排第一，就在存盘组后面）——
    /// 排法本身归 42 号，后两票只让该走的那一格消失，不重排。</item>
    /// </list>
    /// </summary>
    [Test]
    public void 排法B的次序()
    {
        var 左起 = 左边一排().Elements().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(名字(左起[0]), Is.EqualTo("LibraryButton"), "第一位是「歌曲库」");
            Assert.That(属性(左起[1], "Classes"), Is.EqualTo("savegroup"), "第二位是存盘组（一个带描边的容器）");
            Assert.That(属性(左起[2], "Classes"), Is.EqualTo("toolbar"), "存盘组后面直接是「操作 ▾」那个菜单");
            Assert.That(名字(左起[3]), Is.EqualTo("PerformerButton"), "最后是「演奏」");
            Assert.That(左起, Has.Count.EqualTo(4),
                "「导出」被吸收掉、「导入 MIDI…」搬走之后，左边这一排是四样（存盘组算一样）");
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
    /// 几条判据都必须在 <c>RefreshEditState</c> 一处算：散在载入 / 编辑 / 存盘各写一遍的话，
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
            Assert.That(刷新, Does.Not.Contain("ExportButton"),
                "「导出」那颗已经没了（54 号票）—— 判据还留在这儿就是没删干净");

            // 曲库这一条里**不许出现 _song**：它亮不亮跟手上有没曲子无关，只跟配没配曲库有关 ——
            // 空状态里它是唯一亮着的那颗
            Assert.That(刷新, Does.Contain("LibraryButton.IsEnabled = _library is not null;"),
                "「歌曲库」的判据只有曲库那一半 —— 空状态里只剩它亮着");
            Assert.That(刷新, Does.Not.Contain("LibraryButton.IsEnabled = _song"), "「歌曲库」不该跟着曲子灰");

            // 「导入 MIDI…」**不在这一排上了**（55 号搬去曲库窗口右上角）：
            // 主窗口里既没有那颗按钮，也没有它的判据 —— 判据留在这儿就是没删干净
            Assert.That(全部元素().Any(e => 名字(e) == "ImportButton"), Is.False,
                "「导入 MIDI…」搬去曲库窗口了，工具栏上不该还有它");
            Assert.That(刷新, Does.Not.Contain("ImportButton"), "它的判据也别留在这儿");
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
    /// <c>_edited</c> 是粘的：写进工程文件头，给曲库列表那格「改过 / 没动过」用 ——
    /// 存盘不清、撤销回原样也不清。工具栏那一格答的是另一个问题（现在这份和盘上那份对不对得上），
    /// 所以载入和存盘都要清零。合成一个的话，一首存过又重开的曲子一上来就穿着主色，
    /// 而那正是用户说的「载入之后是普通态」。
    ///
    /// ⚠️ 52 号票之后那个文件头住在**缓存**里（`songs\.work\<名字>.mproj`，见 53 号票），
    /// 不再住曲库成员（`.mid`）里 —— 所以下面那条「头里写的是谁」的断言换了个落点，理由写在原地。
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

            // ⚠️ 52 号票（曲库换成 `.mid`）：保存**不再写工程头**了 —— `.mid` 里装不下
            // Edited / ImportedFrom，这两样连同那个粘性标记归 53 号票的缓存
            //（`songs\.work\<名字>.mproj`）。所以「头里写的是 `_edited`」这条**在这一票里没有落点**
            //（SaveTo 里已经一个工程头都不写了），先钉住那一头还成立、而且更该守着的事：
            // **保存落盘写出去的是标准 MIDI**（别顺手把工程头塞回 `.mid` —— 那会让曲库成员不再是干净 MIDI，
            // 拷给别人就带着本程序的私货）。53 号票把缓存写回来之后，这里应补回一条
            // 「缓存那一次写用的是 `_edited, _importedFrom`」——**那条才是原判据的接替者**。
            //
            // ⚠️ 53 号票已经落地：落盘那两笔整个搬进了 `SongCache.Save`（保存那一处不再自己写文件），
            // 所以「SaveTo 里有 MidiWriter」这条**换了个落点**，换成了下面这一条（就是上面点名要的接替者）。
            // 「`.mid` 是干净的标准 MIDI、工程头不许塞回去」由两处接着守：
            // `SongProjectWriteTests.存进曲库的是标准MIDI文件不是JSON`（成员是什么格式）
            // 与 `SongCacheTests.两份各自都读得回来`（写下去的那份 .mid 真能当标准 MIDI 读回来）。
            Assert.That(花括号段(代码, "private void SaveTo", "找不到 SaveTo"),
                Does.Contain("SongCache.HeaderFor(name, song, _edited, _importedFrom)"),
                "缓存那一次写用的是这一刻的 _edited 与 _importedFrom —— 原判据的接替者");

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

    // ==================== 搬走的那一颗（55 号票）====================

    /// <summary>
    /// 「导入 MIDI…」从工具栏上**撤干净了**：按钮和处理函数一起消失，而且处理函数是删掉、
    /// 不是留着不用（留着的话下一个人会把它接回去，那时工具栏和曲库窗口各有一条导入的路）。
    ///
    /// ⚠️ 为什么这条得和 <see cref="曲库窗口右上角那颗按钮和它的那条路"/> **一起看**：
    /// 撤和建必须落在同一个提交里。中间那段「两边都没有」的时间里，**没有任何路径能把一首
    /// 曲子导进来** —— 比「存不到别处」更狠，是整条入库的路断了。
    /// 只钉一头的话，另一头坏掉不报错，而那一头坏掉正是这件事。
    /// </summary>
    [Test]
    public void 导入那颗从工具栏撤干净了()
    {
        string 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(全部元素().Any(e => 名字(e) == "ImportButton"), Is.False,
                "工具栏上「导入 MIDI…」该不见了（它搬去曲库窗口右上角）");
            Assert.That(代码, Does.Not.Contain("void OnImportClick"),
                "主窗口这边的处理函数要删掉 —— 留着不用也会被下一个人接回去");
        });
    }

    /// <summary>
    /// 那颗按钮**建在曲库窗口的右上角**，而且**接的是同一条导入的路**：
    /// 面板喊一声 → 窗口换掉 sender 转出去 → 主窗口接住 → 走原来那个选文件 + <c>ImportFile</c>。
    ///
    /// 「右上角」在这份判据里的意思是**它是标题行里最右边那样**（和「歌曲库」三个字同一条
    /// Border，排在「N 首」后面）。真在屏幕上的位置归上机截图，这儿钉的是「文件里这么写着」。
    ///
    /// ⚠️ 那颗按钮**默认藏着**（<c>IsVisible="False"</c>）：面板自己不会导入，那件事在主窗口手上，
    /// 而主窗口是另一个窗口。没人接得住这一声的时候，一颗按下去什么都不发生、也不报错的按钮
    /// 比不摆更坏 —— 所以「有人接」和「按钮在」是同一个动作（窗口那个自定义访问器）。
    /// </summary>
    [Test]
    public void 曲库窗口右上角那颗按钮和它的那条路()
    {
        var 按钮 = 曲库标题行().Descendants().SingleOrDefault(e => 名字(e) == "ImportButton");
        Assert.That(按钮, Is.Not.Null, "曲库面板的标题行里该有那颗「导入 MIDI…」");
        if (按钮 is null) return;

        string 面板代码 = 曲库面板代码();
        string 窗口代码 = 曲库窗口代码();
        string 主窗代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(属性(按钮, "Content"), Is.EqualTo("导入 MIDI…"), "文案就是用户说的那一个");
            Assert.That(属性(按钮, "Click"), Is.EqualTo("OnImportClick"),
                "挂着不接命令的按钮按下去什么都不发生、也不报错");
            Assert.That(按钮.Parent!.Elements().Last(), Is.SameAs(按钮),
                "它是标题行里最右边的那样 —— 「右上角」");
            Assert.That(属性(按钮, "IsVisible"), Is.EqualTo("False"),
                "默认藏着，由窗口接上命令时打开（没人接就不露面）");

            // 面板这一层：接上点击、把这一声喊出去
            Assert.That(面板代码, Does.Contain("public event EventHandler? ImportRequested"));
            Assert.That(面板代码, Does.Contain("ImportRequested?.Invoke(this, EventArgs.Empty)"));
            Assert.That(面板代码, Does.Contain("ImportAvailable"),
                "「有没有人接得住这一声」是那颗按钮露不露面的判据");

            // 窗口这一层：换掉 sender 往上报，并在接上命令时让按钮露面
            Assert.That(窗口代码, Does.Contain("_panel.ImportRequested += (_, _) => _importRequested?.Invoke(this, EventArgs.Empty)"),
                "跨窗口那一跳在曲库窗口上（和打开 / 删除同一个做法）");
            Assert.That(窗口代码, Does.Contain("panel.ImportAvailable = true;"),
                "接上命令才让那颗按钮露面");

            // 主窗口这一层：接住，并走原来那条路 —— 别在这儿另写一套导入
            Assert.That(主窗代码, Does.Contain("dialog.ImportRequested += OnLibraryImportRequested"));
            Assert.That(主窗代码, Does.Contain("await ImportViaPickerAsync(dialog)"),
                "真正干活的是主窗口那条现成的路");
        });
    }

    /// <summary>
    /// 🔴 **文件框（和这条路上那几句问话）挂在发起它的那扇窗口身上，不是主窗口自己。**
    ///
    /// 曲库窗口是**模态**、压在主窗口头上：文件框要是挂在主窗口上，它就会开在那个模态框
    /// **后面** —— 用户看到的是「按了没反应」。
    ///
    /// 这条是这一票最容易做错的地方，而且**做错了不报错、单测也照过**（弹的是原生窗口，
    /// NUnit 里根本起不来）。所以只能在源码这一层把它钉住：那个文件框那一行得写着 <c>owner</c>。
    /// </summary>
    [Test]
    public void 导入的文件框挂在发起它的那扇窗口上()
    {
        string 代码 = 主窗口代码();
        var 挑文件 = 花括号段(代码, "private async Task ImportViaPickerAsync", "找不到 ImportViaPickerAsync");
        var 导入 = 花括号段(代码, "private async Task ImportFile", "找不到 ImportFile");

        Assert.Multiple(() =>
        {
            Assert.That(挑文件, Does.Contain("owner.StorageProvider.OpenFilePickerAsync"),
                "文件框要挂在发起这次导入的那扇窗口上");
            Assert.That(挑文件, Does.Not.Contain("await StorageProvider."),
                "别在文件框那一行用主窗口自己的 StorageProvider —— 那就是开在模态框后面那一版");
            Assert.That(挑文件, Does.Contain("FileTypeFilter = new[] { MidiFileType }"),
                "过滤器沿用现成的 MidiFileType，别新写一个");

            Assert.That(导入, Does.Contain("ConfirmUnsavedAsync(UnsavedScene.SwitchSong, owner)"),
                "「手上这份还没存」那句问话也挂在发起它的窗口上（挂主窗口会开在模态框后面）");
            Assert.That(导入, Does.Contain("Path.GetFileNameWithoutExtension(path), owner)"),
                "起名那一句同理");
            Assert.That(导入, Does.Contain("SaveTo(library, name)"),
                "落盘那一步一个字都没变");
        });
    }

    /// <summary>
    /// 从曲库窗口导进来一首之后，**那个列表得当场重列** —— 加完看不见它等于没加成，
    /// 而那颗按钮的用处就是往这个列表里加东西。
    /// </summary>
    [Test]
    public void 从曲库导入完列表当场重列()
    {
        var 接导入 = 花括号段(主窗口代码(), "private async void OnLibraryImportRequested", "找不到 OnLibraryImportRequested");

        Assert.That(接导入, Does.Contain("dialog.RefreshLibrary(_currentName)"),
            "导完当场重列一遍曲库窗口那个列表");
    }

    /// <summary>
    /// 「导出」这一票**撤了**（54 号票）—— 它的活被「另存为…」接管，于是它失去了存在理由。
    ///
    /// <b>按钮和处理函数要一起消失，而且处理函数是删掉、不是留着不用。</b>
    /// 留着的话下一个人会把它接回去（「台面上有个现成的」），那时「另存为…」和「导出」
    /// 又变成两条各写一遍的路 —— 而两条路各自坏掉都不报错，正是这一票要收掉的东西。
    /// </summary>
    [Test]
    public void 导出那颗按钮和处理函数都删干净了()
    {
        string 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(全部元素().Any(e => 名字(e) == "ExportButton"), Is.False,
                "工具栏上「导出」那颗按钮该不见了");
            Assert.That(代码, Does.Not.Contain("OnExportClick"),
                "处理函数要删掉 —— 留着不用也会被下一个人接回去");
        });
    }

    // ==================== 另存为 / 保存 分岔（54 号票）====================

    /// <summary>
    /// 🔴 **这一票最容易做错的地方：两个入口必须分岔。**
    ///
    /// 54 号之前 <c>SaveAsAsync</c> 被**两个**入口共用，而它干的事是「问一个名字存进曲库」：
    /// 「保存」在一首还没名字的曲子上走它，「另存为…」也走它。
    /// 顺手把整个方法换成文件选择器 —— 看着最省事 —— **「保存」在一首没名字的曲子上就会弹文件框**，
    /// 而「保存」永远是「写进曲库」，跟文件框没有关系。
    ///
    /// 所以分岔成这样，而且**两边各只做一件事**：
    /// <list type="bullet">
    /// <item>「保存」（<c>SaveAsync</c>）：有曲名就写回那一首；没曲名就问一个名字，问到了走同一条写库的路
    /// （<c>AskNameForSaveAsync</c> → <c>SaveTo</c>）。**一个文件框都不许出现。**</item>
    /// <item>「另存为…」（<c>OnSaveAsClick</c>）：弹原生文件选择器，把你挑的那个路径写成标准 MIDI
    /// （<c>MidiWriter.Write</c>，和导入同一个读写器）。**一个字节都不进曲库** ——
    /// 不写成员、不写缓存，也不再问曲名。</item>
    /// </list>
    ///
    /// <b>为什么「不入库」要单独钉：</b>「顺手也存一份进库」不报错、也没人看得出来 ——
    /// 直到用户另存了一份到桌面，曲库列表里凭空多出一首。而那时它已经是个「功能」了。
    /// </summary>
    [Test]
    public void 保存那条路写库另存为那条路写外面()
    {
        string 代码 = 主窗口代码();
        var 保存 = 花括号段(代码, "private async Task SaveAsync", "找不到 SaveAsync");
        var 另存为 = 花括号段(代码, "private async void OnSaveAsClick", "找不到 OnSaveAsClick");

        Assert.Multiple(() =>
        {
            // ── 「保存」：只写库，一个文件框都不许有 ──
            Assert.That(保存, Does.Contain("AskNameForSaveAsync"),
                "「保存」在没名字的曲子上问的是曲名");
            Assert.That(保存, Does.Contain("SaveTo(library,"), "问到了就写进曲库");
            Assert.That(保存, Does.Not.Contain("SaveFilePickerAsync"),
                "「保存」永远不弹文件框 —— 整个方法换成文件框正是这一票最容易犯的错");
            Assert.That(保存, Does.Not.Contain("MidiWriter"), "落盘是 SaveTo → SongCache 的事");

            // ── 「另存为…」：只写外面，一个字节都不进库 ──
            Assert.That(另存为, Does.Contain("SaveFilePickerAsync"), "「另存为…」挑的是一个路径");
            Assert.That(另存为, Does.Contain("MidiWriter.Write("),
                "写出去的是标准 MIDI（和导入同一个读写器，别另拼字节）");
            Assert.That(另存为, Does.Not.Contain("SaveTo"), "「另存为…」不入库");
            Assert.That(另存为, Does.Not.Contain("SongCache.Save"), "也不写曲库那份缓存");
            Assert.That(另存为, Does.Not.Contain("AskNameForSaveAsync"),
                "「另存为…」不该再问曲名 —— 名字由文件框那一格决定");
        });
    }

    /// <summary>
    /// 🔴 **「另存为…」必须和「导出」用同一个过滤器**（<c>MidiFileType</c>，<c>*.mid;*.midi</c>），
    /// 而不是在文件框那儿另写一份 —— 两份过滤器迟早会不一样，那时「导入认得、另存为写出来的却挑不着」
    /// 之类的怪事就来了，而且不报错。
    ///
    /// 默认扩展名也得是 <c>mid</c>：用户敲一个不带后缀的名字，落下来得是个能让别的软件认出来的文件。
    /// </summary>
    [Test]
    public void 另存为沿用同一个过滤器()
    {
        var 另存为 = 花括号段(主窗口代码(), "private async void OnSaveAsClick", "找不到 OnSaveAsClick");

        Assert.Multiple(() =>
        {
            Assert.That(另存为, Does.Contain("FileTypeChoices = new[] { MidiFileType }"),
                "过滤器沿用现成的 MidiFileType，别新写一个");
            Assert.That(另存为, Does.Contain("DefaultExtension = \"mid\""), "默认扩展名是 mid");
        });
    }

    /// <summary>
    /// ⚠️ **改名撞名那处的话在 54 号之后会变成假话。** 那句原来指的路是
    /// 「要覆盖它请用『另存为…』，那里会问一句」—— 而「另存为…」从此**不往曲库里写了**，
    /// 于是它指向一个**做不到的动作**。
    ///
    /// **注释和提示各一处，两处都得改**（只改字符串的话，下一个读代码的人照着注释走），
    /// 所以两句原文各钉一条。真正能覆盖那一首的路只有一条：
    /// **把那一首打开，再按「保存」**（保存是覆盖语义）。
    /// </summary>
    [Test]
    public void 改名撞名那处不再指向另存为()
    {
        var 曲名框 = 花括号段(主窗口代码(), "private void OnSongNameKeyDown", "找不到 OnSongNameKeyDown");

        Assert.Multiple(() =>
        {
            Assert.That(曲名框, Does.Not.Contain("覆盖它请用「另存为…」"),
                "提示里那句是假话：「另存为…」不往曲库里写，覆盖不了曲库里那一首");
            Assert.That(曲名框, Does.Not.Contain("真要覆盖走「另存为…」"), "注释里那半句同样是假的");
            Assert.That(曲名框, Does.Not.Contain("那里会问一句"), "「会问一句」说的也不再是存进曲库了");
            Assert.That(曲名框, Does.Contain("「保存」"), "得指一条真走得通的路（保存是覆盖语义）");
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
