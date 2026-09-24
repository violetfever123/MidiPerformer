using System.Runtime.CompilerServices;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 点「演奏」时递给演奏器窗口的是**哪一份**曲子（71 号工单）。
///
/// 47 号票按界面改版删掉了演奏器窗口里唯一的载曲入口（「曲目」行 + 那颗文件选择器），
/// 接替它的那条路本来是「主窗口把手上这首递进去」—— 而两个文件都指着「外面」，
/// 外面没有人：<c>PerformerWindow.LoadSong</c> 零调用，窗口里 <c>TrackCombo</c> 一直是灰的。
/// 这张票把那条路接上。
///
/// 判据分两层，都**不起 Avalonia**：
/// - 「该递哪一份」是一段纯函数（<see cref="MainWindow.SongForPerformer"/>）—— 直接喂真值；
/// - 「主窗口真按这条规矩接了线」读源文件文本（和 <c>UnsavedPromptTests</c> / <c>ToolbarLayoutTests</c>
///   是同一套办法，读进来的文本先去注释）。
///
/// <b>这些断言证明了什么、没证明什么：</b>证明「表是这么写的、主窗口按这张表接线」。
/// 「真机上窗口里那行下拉真的换成了新那首」「<c>TrackCombo</c> 真的不再是能点=False」是上机的事。
/// 但这条规矩**坏掉不报错**：直接递 <c>_song</c> 过去，窗口照样开、照样能弹，
/// 只是弹的是草稿，而弹窗上刚写着「用已存的」—— 没有一条会红。所以至少拦住「哪天被人改回去」。
/// </summary>
public class PerformerSongTests
{
    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "MainWindow.axaml.cs"));

    private static string 组装点代码() => File.ReadAllText(Path.Combine(AppDir, "App.axaml.cs"));

    // ---- 手搭的两份曲子。只是两个不相同的 Song 引用，内容是什么不打紧 ----

    private const long Quarter = 480;

    /// <summary>曲库里存着的那一份。</summary>
    private static Song 已存的() => new(
        new[] { new Track(0, 0, "主旋律", 24, new[] { new Note(60, 0, Quarter, 100) }) },
        new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>编辑器里那份改到一半的草稿 —— 引用不同、内容也不同（多了一个音）。</summary>
    private static Song 草稿() => new(
        new[]
        {
            new Track(0, 0, "主旋律", 24,
                new[] { new Note(60, 0, Quarter, 100), new Note(64, Quarter, Quarter, 100) })
        },
        new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    // ==================== 那张表 ====================

    /// <summary>
    /// **这张票最容易做错的一格**：改过还没存时，递进去的必须是曲库里存的那份，不是手上那份。
    ///
    /// 顺手写 <c>_song</c> 过去不会报错 —— 窗口开得出来、曲子也弹得响，
    /// 只是弹的是草稿，而用户刚在弹窗上按的是「用已存的」，那行字当场变成了假话。
    /// </summary>
    [Test]
    public void 改过还没存时递的是曲库里那份()
    {
        var 已存 = 已存的();
        var 草稿版 = 草稿();

        var 递 = MainWindow.SongForPerformer(草稿版, 已存, dirty: true);

        Assert.Multiple(() =>
        {
            Assert.That(递, Is.SameAs(已存), "递的是曲库里存的那份");
            Assert.That(递, Is.Not.SameAs(草稿版), "不是编辑器里那份草稿");
        });
    }

    /// <summary>
    /// 没改动时递手上这份 —— 它**就是**盘上那份（<c>_dirty</c> 为假的意思正是如此）。
    /// 这时还回头去找「已存的」，会在「刚导入、还没进曲库」那条路上找个空。
    /// </summary>
    [Test]
    public void 没改动时递手上这份()
    {
        var 已存 = 已存的();
        var 手上 = 草稿();

        Assert.That(MainWindow.SongForPerformer(手上, 已存, dirty: false), Is.SameAs(手上));
    }

    /// <summary>
    /// 改过还没存、而这一首**还没进过曲库**（导入时取消了命名，或者刚从曲库删掉）：
    /// 没有「已存的」那一份可递，手上这份是唯一的一份 —— 递它，而不是什么都不做。
    /// </summary>
    [Test]
    public void 还没进过曲库时递手上那份()
    {
        var 手上 = 草稿();

        Assert.That(MainWindow.SongForPerformer(手上, stored: null, dirty: true), Is.SameAs(手上));
    }

    /// <summary>
    /// 手上没有曲子就**什么都不该发生** —— 不开那个空窗（「开出一个没东西可弹的窗口比灰着更让人困惑」，
    /// 工具栏那颗按钮的判据是同一个）。这一条是兜底：按钮本来就是灰的。
    /// </summary>
    [Test]
    public void 没有曲子就不开窗()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MainWindow.SongForPerformer(null, null, dirty: false), Is.Null);
            Assert.That(MainWindow.SongForPerformer(null, 已存的(), dirty: false), Is.Null);
            Assert.That(MainWindow.SongForPerformer(null, 已存的(), dirty: true), Is.Null);
        });
    }

    // ==================== 接在主窗口上 ====================

    /// <summary>
    /// 那条取舍真的接在点击处理上，而且拿的三个值就是那三样：
    /// 手上这份 <c>_song</c>、曲库里那份 <c>_storedSong</c>、有没有改动 <c>_dirty</c>。
    ///
    /// 第二个实参写错就是这一票的全部风险 —— 所以它逐个字对。
    /// </summary>
    [Test]
    public void 点演奏那处按这张表取曲子()
    {
        var 点演奏 = 花括号段(主窗口代码(), "private async void OnPerformerClick",
            "找不到 OnPerformerClick");

        Assert.Multiple(() =>
        {
            Assert.That(点演奏, Does.Contain("SongForPerformer(_song, _storedSong, _dirty)"),
                "手上这份 / 曲库里那份 / 有没有改动 —— 三个值都得来自各自的字段");
        });
    }

    /// <summary>
    /// 取舍必须在**问完**「改过还没存」那句之后做：
    /// 那一问里选「是」（先存再继续）会把改动存下去，「曲库里存的那份」当场变成手上这份 ——
    /// 先取再问的话，存完递出去的还是那张已经作废的旧副本。
    /// </summary>
    [Test]
    public void 取舍在问完之后才做()
    {
        var 点演奏 = 花括号段(主窗口代码(), "private async void OnPerformerClick",
            "找不到 OnPerformerClick");

        int 问 = 点演奏.IndexOf("ConfirmUnsavedAsync", StringComparison.Ordinal);
        int 取 = 点演奏.IndexOf("SongForPerformer(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(问, Is.GreaterThanOrEqualTo(0), "这一处要先问未保存那句");
            Assert.That(取, Is.GreaterThan(问), "取曲子排在问之后");
        });
    }

    /// <summary>
    /// 递进去的那一份真的交给了窗口：走 <c>PerformerWindow.LoadSong</c>，
    /// 而不是在别处另起一套「装一首曲子」。
    /// </summary>
    [Test]
    public void 递过去的曲子交给了窗口()
    {
        var 点演奏 = 花括号段(主窗口代码(), "private async void OnPerformerClick",
            "找不到 OnPerformerClick");

        Assert.Multiple(() =>
        {
            Assert.That(点演奏, Does.Contain("await window.LoadSong(song);"),
                "曲子经 LoadSong 递进去 —— 挑轨、列下拉框、默认选第一条都在那一头");
            Assert.That(点演奏, Does.Contain("if (!window.IsVisible) window.Show(this);"),
                "复用那个窗口的显 / 唤还是老样子");
        });
    }

    /// <summary>
    /// 工厂的返回类型必须看得见 <see cref="PerformerWindow"/> —— 要递曲子就得知道
    /// <c>LoadSong</c> 长在哪个类型上。收成 <c>Func&lt;Window&gt;</c> 就调度不动了。
    /// </summary>
    [Test]
    public void 工厂给的是演奏器窗口()
    {
        Assert.Multiple(() =>
        {
            Assert.That(主窗口代码(), Does.Contain("private readonly Func<PerformerWindow>? _performerFactory;"));
            Assert.That(主窗口代码(), Does.Contain("Func<PerformerWindow>? performerFactory,"));

            var 组装点 = 组装点代码();
            Assert.That(组装点, Does.Contain("private static Func<PerformerWindow> PerformerFactory("));
            // 复用与单例照旧：同一个窗口还是同一个窗口（同一时刻只允许一个，F6 急停靠低层键盘钩子）
            Assert.That(组装点, Does.Contain("if (live is not null) return live;"));
            Assert.That(组装点, Does.Contain("window.Closed += (_, _) => live = null;"));
        });
    }

    // ==================== 「曲库里那份」这个字段本身 ====================

    /// <summary>
    /// <c>_storedSong</c> 只在和曲库打交道的那两处换：读出来的地方（<c>TryOpenLibrarySong</c>）
    /// 和写下去的地方（<c>SaveTo</c>）。
    /// </summary>
    [Test]
    public void 曲库里那份只在读写曲库那两处换()
    {
        var 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(花括号段(代码, "private string? TryOpenLibrarySong", "找不到 TryOpenLibrarySong"),
                Does.Contain("_storedSong = song;"), "从曲库读出来装上：这一份就是「已存的」");
            Assert.That(花括号段(代码, "private void SaveTo", "找不到 SaveTo"),
                Does.Contain("_storedSong = song;"), "写进曲库：写下去的这一份就是「已存的」");
        });
    }

    /// <summary>
    /// **这张票的另一半要害**：编辑那条路（<see cref="MainWindow"/> 的 <c>ApplySong</c>）
    /// 一个字节都不许碰 <c>_storedSong</c>。
    ///
    /// 「顺手让它跟编辑一起走」看着更整齐，而它正是这一票要防的那件事 ——
    /// 跟着走之后，「已存的」就等于草稿，上面那张表整张失效，
    /// 而弹窗上那句「点「用已存的」就拿曲库里存的那份去弹」照旧写着。
    /// </summary>
    [Test]
    public void 编辑那条路不碰曲库里那份()
    {
        var 代码 = 主窗口代码();

        Assert.Multiple(() =>
        {
            Assert.That(花括号段(代码, "private void ApplySong", "找不到 ApplySong"),
                Does.Not.Contain("_storedSong"), "编辑换的是手上那份，盘上那份没动");
            Assert.That(花括号段(代码, "private async Task<bool> SaveFirstAsync", "找不到 SaveFirstAsync"),
                Does.Not.Contain("_storedSong"), "「先存再继续」是走 SaveAsync 存的，不自己动手");
        });
    }

    /// <summary>
    /// 换曲子那一处（<c>LoadSong</c>）必须把 <c>_storedSong</c> 清掉：
    /// 导入另一首之后，「已存的」还留着上一首的话 —— 点了演奏弹出来的是别人。
    /// </summary>
    [Test]
    public void 换曲子时忘掉上一首的曲库副本()
    {
        Assert.That(花括号段(主窗口代码(), "private void LoadSong", "找不到 LoadSong"),
            Does.Contain("_storedSong = null;"));
    }

    /// <summary>
    /// 曲库里那一份被删掉之后，手上这份成了唯一的一份 —— 再点演奏没有「已存的」可递。
    /// </summary>
    [Test]
    public void 曲库那份删掉之后就不是已存的了()
    {
        var 删 = 花括号段(主窗口代码(), "private void OnLibraryDeleteRequested", "找不到 OnLibraryDeleteRequested");

        Assert.That(删, Does.Contain("_storedSong = null;"));
    }

    // ==================== 两个取材函数 ====================

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

    /// <summary>
    /// 取一个方法的花括号体（和 <c>UnsavedPromptTests</c> 同一套办法）。
    /// 文本**先去注释**再找：注释里写着 `_storedSong = song;` 也一样能把断言弄绿，
    /// 而断言要看的自始至终是代码本身。
    /// </summary>
    private static string 花括号段(string source, string signature, string 找不到就说)
    {
        source = 只读代码(source);

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
