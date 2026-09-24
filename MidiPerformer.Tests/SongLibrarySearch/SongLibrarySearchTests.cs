using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MidiPerformer.App.Views;
using NUnit.Framework;

namespace MidiPerformer.Tests.SongLibrarySearch;

/// <summary>
/// 曲库搜索框的**筛选**：测的是两个纯函数 —— <see cref="SongLibraryPanel.Filter"/>（筛曲名）和
/// <see cref="SongLibraryPanel.EmptyHintFor"/>（该说哪句空态）。跟 PianoRollGeometry 是同一类做法：
/// 给一张表、拿一张表，不起控件、不碰盘，所以脱开 Avalonia 也测得了。
///
/// **「搜索框有没有真的接到列表上」不在这儿** —— 那要一块真屏幕、一次真敲键，归上机（工单 B 那一节）。
/// 这里能钉住的是规矩和那句话：空查询 = 全部、大小写不敏感、子串命中、没命中返回空，
/// 以及**两种空态必须是两句不同的话**（写成同一句不会红任何别的测试，但用户会以为曲子丢了）。
///
/// ⚠️ **这个目录为什么不叫 `SongLibrary\`**：`MidiPerformer.Tests\` 下的每个目录名都是一个命名空间，
/// 而命名空间会被**同族**的其它测试文件优先解析到类型之前 —— `Project\SongLibraryTests.cs` 里
/// 那些没写限定的 `SongLibrary` 当场变成 CS0118「是命名空间，但此处被当做类型来使用」，
/// **整个测试工程编译不过**。新目录别取成仓库里某个类型的名字。
/// </summary>
public class SongLibrarySearchTests
{
    /// <summary>
    /// 一份假曲名，**顺序就是 <c>SongLibrary.Names()</c> 会给出的那个拼音序**
    /// （勾 gou 起 qi 夜 ye 月 yue）。排序是曲库那一层的活，这儿只是照抄它的结果，
    /// 好让「过滤不改序」有得比。
    /// </summary>
    private static readonly string[] 拼音序 = { "勾指起誓", "起风了", "夜的钢琴曲", "月亮代表我的心" };

    // ==================== A. 纯函数层 ====================

    [Test]
    public void 空查询返回全部()
    {
        var 筛过了 = SongLibraryPanel.Filter(拼音序, "");

        Assert.That(筛过了, Is.EqualTo(拼音序));
    }

    [Test]
    public void 没敲过字也返回全部()
    {
        // 搜索框还没建好 / 文本是 null 时也得是「不筛」，不能变成「全不匹配」
        Assert.That(SongLibraryPanel.Filter(拼音序, null), Is.EqualTo(拼音序));
    }

    [Test]
    public void 光是空白也算没查询()
    {
        // 手一抖多打一个空格就整份列表空掉的话，用户只会觉得搜索框坏了
        Assert.That(SongLibraryPanel.Filter(拼音序, "   "), Is.EqualTo(拼音序));
    }

    [Test]
    public void 大小写不敏感()
    {
        string[] 表 = { "ABC", "abc", "xyz" };

        Assert.That(SongLibraryPanel.Filter(表, "abc"), Is.EqualTo(new[] { "ABC", "abc" }));
        Assert.That(SongLibraryPanel.Filter(表, "ABC"), Is.EqualTo(new[] { "ABC", "abc" }));
    }

    [Test]
    public void 按子串命中()
    {
        // 「钢」在「夜的钢琴曲」中间，不是开头 —— 要的是子串，不是前缀
        Assert.That(SongLibraryPanel.Filter(拼音序, "钢"), Is.EqualTo(new[] { "夜的钢琴曲" }));
    }

    [Test]
    public void 没命中返回空()
    {
        Assert.That(SongLibraryPanel.Filter(拼音序, "压根没有这么一首"), Is.Empty);
    }

    [Test]
    public void 过滤不改序()
    {
        // 搜索是**过滤**，不是重排：拿拼音序进来，剩下那几首必须还是那个相对次序。
        // 重排过的话，用户会觉得「搜一下顺序就乱了」。
        var 筛过了 = SongLibraryPanel.Filter(拼音序, "的");

        Assert.That(筛过了, Is.EqualTo(new[] { "夜的钢琴曲", "月亮代表我的心" }));
    }

    [Test]
    public void 曲名表不能是_null()
    {
        Assert.Throws<ArgumentNullException>(() => SongLibraryPanel.Filter(null!, ""));
    }

    // ==================== 两种空态 ====================

    /// <summary>
    /// 这一票里唯一一个「看起来正常但说错话」的地方：搜了个不存在的词，屏幕上写「还没有曲子」，
    /// 用户会以为**曲子丢了**。所以这条断言到**两句不同的文案**，不是只断言列表是空的。
    /// </summary>
    [Test]
    public void 搜不中时的空态文案跟一首都没有时是两句不同的话()
    {
        var 筛过了 = SongLibraryPanel.Filter(拼音序, "压根没有这么一首");
        Assert.That(筛过了, Is.Empty);

        string? 搜不中 = SongLibraryPanel.EmptyHintFor(拼音序.Length, 筛过了.Count);
        string? 一首都没有 = SongLibraryPanel.EmptyHintFor(0, 0);

        Assert.That(搜不中, Is.EqualTo(SongLibraryPanel.NoMatchHint));
        Assert.That(一首都没有, Is.EqualTo(SongLibraryPanel.EmptyLibraryHint));
        Assert.That(搜不中, Is.Not.EqualTo(一首都没有));
    }

    [Test]
    public void 曲库空的时候说的话()
    {
        Assert.That(SongLibraryPanel.EmptyLibraryHint, Is.EqualTo("还没有曲子"));

        // 曲库本来就空，手上还挂着一个查询串，也算「还没有曲子」——
        // 那不是用户搜没的，是本来就没有
        Assert.That(SongLibraryPanel.EmptyHintFor(0, 5), Is.EqualTo(SongLibraryPanel.EmptyLibraryHint));
    }

    [Test]
    public void 搜不中的时候说的话()
    {
        Assert.That(SongLibraryPanel.NoMatchHint, Is.EqualTo("没有匹配的曲子"));
        Assert.That(SongLibraryPanel.EmptyHintFor(拼音序.Length, 0), Is.EqualTo(SongLibraryPanel.NoMatchHint));
    }

    [Test]
    public void 列表里有东西就不显示空态()
    {
        Assert.That(SongLibraryPanel.EmptyHintFor(拼音序.Length, 拼音序.Length), Is.Null);
        Assert.That(SongLibraryPanel.EmptyHintFor(拼音序.Length, 1), Is.Null);
    }

    // ==================== culture ====================

    /// <summary>
    /// 筛选用的比较方式必须是 **culture 那一档**，跟 <c>SongLibrary.Names()</c> 排序用的
    /// <c>StringComparer.CurrentCulture</c> 是同一个 culture（见
    /// <c>MidiPerformer.Adapters/Gateways/SongLibrary.cs</c> 那条注释：中文曲名按拼音走）。
    ///
    /// **为什么这条只能读源码**：过滤**不改序** —— 顺序是排序给的，所以「换了个 culture」
    /// 在行为上一丝都看不出来，换成 Ordinal 也不会红任何一条别的测试。能钉住它的只有这一行字。
    ///
    /// 断言「剥完注释只剩 1 处」**同时就是这把刀自己的体检**：那行字在文档注释里也写着，
    /// 要是去注释坏掉（把整段源码原样返回），这里会数到 2 而红 —— 这条断言不会悄悄变成空转。
    /// </summary>
    [Test]
    public void 筛选用的是排序那个_culture()
    {
        // 刀的体检：注释里的这行字剥完必须看不见
        Assert.That(去注释("var x = 1; // StringComparison.CurrentCultureIgnoreCase"),
                    Does.Not.Contain("CurrentCultureIgnoreCase"));

        string 源码 = File.ReadAllText(Path.Combine(AppDir, "Views", "SongLibraryPanel.axaml.cs"));

        var 命中 = 去注释(源码).Split('\n')
            .Where(行 => 行.Contains("StringComparison.CurrentCultureIgnoreCase"))
            .ToArray();

        Assert.That(命中.Length, Is.EqualTo(1),
            "筛选的比较方式得是 StringComparison.CurrentCultureIgnoreCase（跟排序同一个 culture），而且只此一处。");
    }

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string ThisFileDir([CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;

    /// <summary>去掉整行与行尾的 <c>//</c> 注释（含 <c>///</c>）：找「代码里有没有这行字」时不能把散文算进去。</summary>
    private static string 去注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");
}
