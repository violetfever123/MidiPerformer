using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MidiPerformer.App.Views;
using MidiPerformer.Core.UseCases.Project;
using NUnit.Framework;

namespace MidiPerformer.Tests.SongLibraryRowMark;

/// <summary>
/// 曲库列表**第二格**那个标记：测的是纯函数 <see cref="SongLibraryPanel.MarkFor"/> ——
/// 给「缓存在不在、读不读得出、<c>Edited</c>、可弹轨数」，拿「显示什么、什么颜色」。
/// 跟 <c>Filter</c> / <c>EmptyHintFor</c> 是同一类做法：不碰控件、不碰盘，脱开 Avalonia 也测得了。
///
/// 判据**分两层**，这一票的两处重点各在一层里：
/// <list type="number">
/// <item><b>闸：有没有缓存</b> —— 「根本没有缓存」和「缓存坏了」是两件事，而
/// <c>TryReadProjectHeader</c> 对这两种都返回 <c>null</c>。分开它们的是**缓存那条路径存不存在**。</item>
/// <item><b>真值表四格</b> —— 缓存里那两个事实互相独立，`(没改过, 弹得动)` 这一格钉的正是
/// 「**缓存在 ≠ 改过**」：手拷进来的 <c>.mid</c> 打开、什么都没动、顺手按一下 Ctrl+S，
/// 缓存就有了而 <c>Edited</c> 还是 <c>false</c>。</item>
/// </list>
///
/// ⚠️ **这个目录为什么不叫 `SongLibraryRowMark.cs` 那种名字**：`MidiPerformer.Tests\` 下的
/// 每个目录名都是一个命名空间，而命名空间会被**同族**的其它测试文件优先解析到类型之前。
/// 新目录别取成仓库里某个类型的名字（见 `SongLibrarySearch/` 顶上那段更长的说明）。
///
/// **不测的**：`.mid` 的字节长相（DryWetMidi 的事）、文件选择器、`%` 之类的数字。
/// **也不测控件** —— 「曲库窗口里那一行真的长这样」归上机（工单 C 那一节）。
/// </summary>
public class SongLibraryRowMarkTests
{
    // ==================== 闸：有没有缓存 ====================

    /// <summary>
    /// 「缓存不存在 → 什么都不显示」是这一票的重点：它实现的是「**从别处手拷进来的 `.mid` 不带标记**」
    /// 那条规则。显示「没动过」是**错的** —— 那等于宣称程序认识这首它从没存过的曲子。
    /// </summary>
    [Test]
    public void 缓存不存在时第二格什么都不显示()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: false, header: null);

        Assert.That(mark.Text, Is.Empty);
        Assert.That(mark.Text, Is.Not.EqualTo("没动过"),
            "「本程序没给它存过盘」不是「没动过」：那三个字等于宣称程序认识它。");
        Assert.That(mark.Bad, Is.False);
    }

    [Test]
    public void 缓存读不出来时说的是读不出来而且是坏的()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: true, header: null);

        Assert.That(mark.Text, Is.EqualTo(SongLibraryPanel.UnreadableMark));
        Assert.That(mark.Text, Is.EqualTo("读不出来"));
        Assert.That(mark.Bad, Is.True);
    }

    /// <summary>
    /// 🔴 **这一票最容易做塌的一处。** 两种情况喂进 <see cref="SongLibraryPanel.MarkFor"/> 的
    /// header 都是 <c>null</c>（<c>TryReadProjectHeader</c> 对「文件不在」和「文件坏了」一样返回 null），
    /// 分得开它们的只有缓存路径存不存在那一个入参。**只看 header 是不是 null，就有缓存那一格全废。**
    /// </summary>
    [Test]
    public void 没有缓存和缓存坏了是两件不同的事()
    {
        var 没有缓存 = SongLibraryPanel.MarkFor(hasCache: false, header: null);
        var 缓存坏了 = SongLibraryPanel.MarkFor(hasCache: true, header: null);

        Assert.That(没有缓存.Text, Is.Not.EqualTo(缓存坏了.Text));
        Assert.That(没有缓存.Bad, Is.Not.EqualTo(缓存坏了.Bad));
    }

    // ==================== 真值表四格 ====================

    [Test]
    public void 改过而且弹得动()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: true, header: Header(edited: true, playable: 3));

        Assert.That(mark.Text, Is.EqualTo("编辑过 · 可播放"));
        Assert.That(mark.Bad, Is.False);
    }

    [Test]
    public void 改过但是弹不动()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: true, header: Header(edited: true, playable: 0));

        Assert.That(mark.Text, Is.EqualTo("编辑过 · 不可播放"));
        Assert.That(mark.Bad, Is.False);
    }

    /// <summary>
    /// ⚠️ **这一票最关键的一格** —— 它钉死的是「**缓存在 ≠ 改过**」：
    /// 打开一首手拷进来的 <c>.mid</c>、什么都没动、顺手按了下 Ctrl+S ⇒ 缓存有了，
    /// <c>Edited</c> 还是 <c>false</c>，这一格仍然是「没改过」。
    /// **今天 <c>Meta(header)</c> 的三态里没有这一格** —— 它的入参里根本没有「缓存在不在」这件事，
    /// 所以任何输入都给不出「可播放」这四个字。
    /// </summary>
    [Test]
    public void 没改过但弹得动()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: true, header: Header(edited: false, playable: 1));

        Assert.That(mark.Text, Is.EqualTo("可播放"));
        Assert.That(mark.Text, Is.Not.EqualTo("没动过"),
            "有缓存 + 没改过，要说的是「可播放」，不是旧三态那句「没动过」。");
        Assert.That(mark.Bad, Is.False);
    }

    [Test]
    public void 没改过也弹不动()
    {
        var mark = SongLibraryPanel.MarkFor(hasCache: true, header: Header(edited: false, playable: 0));

        Assert.That(mark.Text, Is.EqualTo("不可播放"));
        Assert.That(mark.Bad, Is.False);
    }

    /// <summary>四格是四句互不相同的话 —— 有一格写重了，用户在列表上就分不出那两种曲子。</summary>
    [Test]
    public void 四格是四句不同的话()
    {
        var 四格 = new[]
        {
            SongLibraryPanel.MarkFor(true, Header(edited: true, playable: 3)).Text,
            SongLibraryPanel.MarkFor(true, Header(edited: true, playable: 0)).Text,
            SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 3)).Text,
            SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 0)).Text,
        };

        Assert.That(四格, Is.All.Not.Empty);
        Assert.That(四格.Distinct().Count(), Is.EqualTo(4));
    }

    /// <summary>那个数是**条数**，不是「能不能弹」：1 条能弹和 30 条能弹都算「可播放」。</summary>
    [Test]
    public void 能弹的轨是一条还是三十条都算可播放()
    {
        Assert.That(SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 1)).Text,
                    Is.EqualTo(SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 30)).Text));
    }

    // ==================== 两条口径 ====================

    /// <summary>
    /// **「不可播放」不是警告色**：弹不了不是错误，是事实（比如音域超出那 38 格）。
    /// **红色只留给真正出错的那一格**（「读不出来」）。
    /// </summary>
    [Test]
    public void 不可播放不是警告色()
    {
        Assert.That(SongLibraryPanel.MarkFor(true, Header(edited: true, playable: 0)).Bad, Is.False);
        Assert.That(SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 0)).Bad, Is.False);
    }

    /// <summary>把六种输入一起过一遍：**只有「有缓存 + 读不出来」那一格**是坏的。</summary>
    [Test]
    public void 只有读不出来那一格才是坏色()
    {
        var 全部 = new (bool 有缓存, ProjectHeader? 头)[]
        {
            (false, null),                  // 没存过盘的散装 .mid —— 不是错误，是没标记
            (true, null),                   // 有缓存但读不出来 —— 这一格才是错误
            (true, Header(true, 5)),
            (true, Header(true, 0)),
            (true, Header(false, 5)),
            (true, Header(false, 0)),
        };

        foreach (var (有缓存, 头) in 全部)
        {
            var mark = SongLibraryPanel.MarkFor(有缓存, 头);
            string 这一格 = $"有缓存={有缓存}，缓存头={(头 is null ? "读不出来" : "读得出来")}";

            if (有缓存 && 头 is null)
            {
                Assert.That(mark.Bad, Is.True, 这一格);
                Assert.That(mark.Text, Is.EqualTo(SongLibraryPanel.UnreadableMark), 这一格);
            }
            else
            {
                Assert.That(mark.Bad, Is.False, 这一格);
                Assert.That(mark.Text, Is.Not.EqualTo(SongLibraryPanel.UnreadableMark), 这一格);
            }
        }
    }

    /// <summary>
    /// `ImportedFrom` **不参与任何判据**。拿它当判据，每首导入过的歌都会长出标记，
    /// 而「手拷进来的 `.mid` 没有标记」那条规则就废了 —— 程序导入和手动拷贝会长得一模一样。
    /// </summary>
    [Test]
    public void 从哪来的不参与判据()
    {
        var 导入过的 = SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 2, importedFrom: @"D:\下载\某首.mid"));
        var 没来路的 = SongLibraryPanel.MarkFor(true, Header(edited: false, playable: 2));

        Assert.That(导入过的, Is.EqualTo(没来路的));
    }

    // ==================== 那一行拿哪个文件去读 ====================

    /// <summary>
    /// 第二格读的必须是**缓存**（`songs\.work\&lt;名字&gt;.mproj`），**不是 `.mid`**。
    ///
    /// 53 之后缓存住在 `.work\` 下，而 `.mid` 是**二进制** —— 拿它去解析 JSON **永远**失败
    /// （<c>TryReadProjectHeader</c> 对 `.mid` 恒返回 <c>null</c>），于是每一行都掉进「没有缓存」
    /// 那一格，标记**恒为空**、这一票整个白做。而且它**不报错**。
    ///
    /// 判据抽成纯函数之后，上面那些测试全都喂真值、测不到「这一行到底拿哪个路径去读」——
    /// 这一条只能读源码。跟 <c>SongLibrarySearchTests</c> 里那条 culture 断言是同一把刀。
    /// </summary>
    [Test]
    public void 行标记读的是缓存不是_mid()
    {
        // 刀的体检：模式写错的话，下面「一处都没有」那条会永远成立 —— 先证明这把刀找得到东西
        Assert.That(命中行数("var x = a.PathOf(b);", ".PathOf("), Is.EqualTo(1));

        string 代码 = 去注释(File.ReadAllText(Path.Combine(AppDir, "Views", "SongLibraryPanel.axaml.cs")));

        Assert.That(命中行数(代码, "WorkPathOf"), Is.GreaterThan(0),
            "第二格得从缓存（songs\\.work\\<名字>.mproj）里读文件头。");
        Assert.That(命中行数(代码, "HasWork"), Is.GreaterThan(0),
            "「根本没有缓存」和「缓存坏了」得先问一声缓存路径在不在，不能只看 header 是不是 null。");
        Assert.That(命中行数(代码, ".PathOf("), Is.Zero,
            "别读 .mid：它是二进制，读它永远解析失败，第二格会恒为空。");
    }

    // ==================== 帮手 ====================

    /// <summary>一份缓存的文件头。`Version` 用当前版本（别的版本 <c>TryReadProjectHeader</c> 判成读不出来）、名字随便。</summary>
    private static ProjectHeader Header(bool edited, int playable, string? importedFrom = null) =>
        new(SongProjectFile.ProjectVersion, "随便一首", edited, importedFrom, playable);

    private static int 命中行数(string 代码, string 片段) => 代码.Split('\n').Count(行 => 行.Contains(片段));

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string ThisFileDir([CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;

    /// <summary>去掉整行与行尾的 <c>//</c> 注释（含 <c>///</c>）：找「代码里有没有这行字」时不能把散文算进去。</summary>
    private static string 去注释(string 源码) => Regex.Replace(源码, "//[^\n]*", "");
}
