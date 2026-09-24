using System.Runtime.CompilerServices;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 点「演奏」时递给演奏器窗口的是**哪一份**曲子（71 号工单），
/// 以及递进去之后**这一场演奏还在不在**（73 号工单）。
///
/// 47 号票按界面改版删掉了演奏器窗口里唯一的载曲入口（「曲目」行 + 那颗文件选择器），
/// 接替它的那条路本来是「主窗口把手上这首递进去」—— 而两个文件都指着「外面」，
/// 外面没有人：<c>PerformerWindow.LoadSong</c> 零调用，窗口里 <c>TrackCombo</c> 一直是灰的。
/// 71 号票把那条路接上，于是「演奏进行中有人递新曲子」**第一次成了可达状态** ——
/// 而 <c>LoadSong</c> 从没为这个状态准备过：它自己另写了一份开关判据、漏了「没在演奏」，
/// 还会把状态行改成「就绪」。73 号票修的就是它。
///
/// 判据分两层，都**不起 Avalonia**：
/// - 「该递哪一份」是一段纯函数（<see cref="MainWindow.SongForPerformer"/>）—— 直接喂真值；
/// - 「主窗口真按这条规矩接了线」「演奏器真按那条规矩收曲子」读源文件文本
///   （和 <c>UnsavedPromptTests</c> / <c>ToolbarLayoutTests</c> 是同一套办法，读进来的文本先去注释）。
///
/// <b>这些断言证明了什么、没证明什么：</b>证明「表是这么写的、两个窗口按这张表接线」。
/// 「真机上窗口里那行下拉真的换成了新那首」「演奏中那颗开始按钮真的没被点亮」是上机的事。
/// 但这两条规矩**坏掉都不报错**：直接把草稿递过去，窗口照样开、照样能弹；
/// 演奏中把画面换成新那首，也照样不报错 —— 只是屏幕上说的曲子和耳朵里听的不是同一首。
/// 所以至少拦住「哪天被人改回去」。
/// </summary>
public class PerformerSongTests
{
    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "MainWindow.axaml.cs"));

    private static string 组装点代码() => File.ReadAllText(Path.Combine(AppDir, "App.axaml.cs"));

    private static string 演奏器代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "PerformerWindow.axaml.cs"));

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

    // ==================== 演奏中递进来的曲子（73 号票） ====================

    /// <summary>
    /// **73 号的病根不是「谁忘了加 `!running`」，是「同一件事在两个地方各写了一份判据」** ——
    /// <c>SetRunning</c> 里那份有「没在演奏」，<c>LoadSong</c> 里那份没有。
    ///
    /// 所以这条同时钉两件事：整份文件里这两颗控件的 `IsEnabled` **各只写一次**，
    /// 而且那一次就在 <c>SetRunning</c> 里、判据也只写一次（两颗共用同一个局部变量）。
    /// 少了任何一半，「下一个人往 <c>SetRunning</c> 里加控件时照样会漏」这件事就还成立。
    /// </summary>
    [Test]
    public void 演奏器窗口的开关判据只有一处()
    {
        var 原文 = 演奏器代码();

        Assert.Multiple(() =>
        {
            Assert.That(数一数(只读代码(原文), "StartButton.IsEnabled ="), Is.EqualTo(1),
                "「开始」开不开只许在 SetRunning 里判一次 —— 73 号就是这儿另写了一份、漏了「没在演奏」");
            Assert.That(数一数(只读代码(原文), "TrackCombo.IsEnabled ="), Is.EqualTo(1),
                "轨下拉同理");

            var 闸 = 花括号段(原文, "private void SetRunning", "找不到 SetRunning");
            Assert.That(闸, Does.Contain("StartButton.IsEnabled ="), "那一处就在 SetRunning 里");
            Assert.That(闸, Does.Contain("TrackCombo.IsEnabled ="));
            Assert.That(数一数(闸, "!running && _playable.Count > 0"), Is.EqualTo(1),
                "「没在演奏、而且是真有得弹」这条判据只写一次，两颗控件共用它");
        });
    }

    /// <summary>
    /// 装完曲子**走那道闸**，不再自己判一遍 —— <c>SetRunning(_running)</c> 传的是**当前阶段**：
    /// 空闲时它给出的正是从前那句「有得弹就开」，演奏中（万一走到这儿）它就是「锁死」。
    /// </summary>
    [Test]
    public void 装完曲子按当前阶段重画一遍开关()
    {
        var 装 = 花括号段(演奏器代码(), "private void ApplySong", "找不到 ApplySong");

        Assert.Multiple(() =>
        {
            Assert.That(装, Does.Contain("SetRunning(_running);"), "画完按当前阶段重画一遍开关");
            Assert.That(装, Does.Not.Contain("StartButton.IsEnabled"), "别再自己写一份判据");
            Assert.That(装, Does.Not.Contain("TrackCombo.IsEnabled"), "同上");
            Assert.That(装, Does.Contain("_pending = null;"),
                "这一份上了，之前记着的那份就作废（后递的那一次说了算）");
        });
    }

    /// <summary>
    /// **73 号票的正题**：演奏进行中递进来的曲子**先记着，画面一个字都不动**。
    ///
    /// 正在响的是上一首（<c>StartPerformance</c> 手上那份是它自己拿着的，跟这儿换不换无关），
    /// 这时候把下拉、状态行、空状态换成新那首，屏幕上说的曲子就跟耳朵里听的不是同一首了 ——
    /// 而且那 38 根条子会按**新歌**的轨重画，正在响的那个音也跟着跑到别人的音域里。
    /// </summary>
    [Test]
    public void 演奏中递进来的曲子先记着不换画面()
    {
        var 载 = 花括号段(演奏器代码(), "public async Task LoadSong", "找不到 LoadSong");

        Assert.Multiple(() =>
        {
            Assert.That(载, Does.Contain("if (_running)"), "先判阶段");
            Assert.That(载, Does.Contain("_pending = (song, playable);"),
                "记着这一份 —— 挑好的轨一起存，收尾时不用再挑一遍");
            Assert.That(载, Does.Contain("ApplySong(song, playable);"), "空闲时照旧立刻上");

            int 挑 = 载.IndexOf("Task.Run", StringComparison.Ordinal);
            int 判 = 载.IndexOf("if (_running)", StringComparison.Ordinal);
            Assert.That(挑, Is.GreaterThanOrEqualTo(0), "挑轨那一步还在");
            Assert.That(判, Is.GreaterThan(挑), "先挑完（那一步是耗时的）再判阶段，挑出来的东西一起记着");

            // 演奏中不许由 LoadSong 直接动这七样 —— 它们全归 ApplySong（只在空闲那条路上走）
            foreach (string 样 in new[]
                     {
                         "_song = song;", "TrackCombo.ItemsSource", "EmptyBox.IsVisible", "TrackHint.Text",
                         "ShowReady()", "SetStatus(", "RefreshRange()"
                     })
            {
                Assert.That(载, Does.Not.Contain(样), $"演奏中不换画面：{样} 归 ApplySong 管");
            }
        });
    }

    /// <summary>
    /// 这一场收尾时，把记着的那一首**上上去**（不是丢掉）。
    ///
    /// 顺序是要害：必须**先**把这一场收干净（<c>_running = false</c>）、**再**上 ——
    /// 反过来的话，<c>ApplySong</c> 里那句 <c>SetRunning(_running)</c> 拿到的还是「在演奏」，
    /// 新曲子画出来就是锁着的样子（按钮按不动、下拉点不开），而屏幕上没有任何东西说得出为什么。
    /// </summary>
    [Test]
    public void 这一场收尾才把记着的那一首上上去()
    {
        var 收尾 = 花括号段(演奏器代码(), "private void OnPerformanceFinished", "找不到 OnPerformanceFinished");

        int 停 = 收尾.IndexOf("_running = false;", StringComparison.Ordinal);
        int 上 = 收尾.IndexOf("ApplySong(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(收尾, Does.Contain("_pending is { }"), "收尾时看有没有等着上的那一首");
            Assert.That(上, Is.GreaterThanOrEqualTo(0), "有就上 —— 演奏中递进来的曲子不丢，只是晚一步");
            Assert.That(停, Is.GreaterThanOrEqualTo(0));
            Assert.That(上, Is.GreaterThan(停), "先把这一场收干净，再上新曲子");
        });
    }

    /// <summary>
    /// **这一票的主要保护网**：空闲时递曲子，行为**跟从前一模一样**。
    /// 四样一样都不能少、顺序也不能乱（先摆列表再设选中项 —— <c>SelectionChanged</c> 会读 <c>_playable</c>；
    /// 「就绪 · 轨名 · 共 N 个音」要等选中项落定之后才写得对）。
    /// </summary>
    [Test]
    public void 空闲时递曲子还是老样子()
    {
        var 装 = 花括号段(演奏器代码(), "private void ApplySong", "找不到 ApplySong");

        int 提示 = 装.IndexOf("TrackHint.Text = $\"只列出单声部轨 · {song.Tracks.Count} 条轨里 {_playable.Count} 条可演奏\";", StringComparison.Ordinal);
        int 列 = 装.IndexOf("TrackCombo.ItemsSource = any ? _playable.Select(Describe).ToList() : null;", StringComparison.Ordinal);
        int 选 = 装.IndexOf("TrackCombo.SelectedIndex = 0;", StringComparison.Ordinal);
        int 就绪 = 装.IndexOf("ShowReady();", StringComparison.Ordinal);
        int 重画 = 装.IndexOf("RefreshRange();", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(提示, Is.GreaterThanOrEqualTo(0), "提示行还是那句话（轨数 / 可弹数两个数）");
            Assert.That(列, Is.GreaterThan(提示), "先摆列表");
            Assert.That(选, Is.GreaterThan(列), "再选第一条（SelectionChanged 会读 _playable）");
            Assert.That(就绪, Is.GreaterThan(选), "选中之后才写「就绪 · 轨名 · 共 N 个音」");
            Assert.That(重画, Is.GreaterThan(就绪), "两条路最后都要重画那 38 根条子");

            Assert.That(装, Does.Contain("TrackCombo.PlaceholderText = \"选一条轨\";"));
            Assert.That(装, Does.Contain("TrackCombo.PlaceholderText = \"— 无可演奏的轨 —\";"));
            Assert.That(装, Does.Contain("TrackCombo.SelectedIndex = -1;"), "一条可弹的都没有：不选，走空状态那一路");
            Assert.That(装, Does.Contain("SetStatus($\"就绪 · {song.Tracks.Count} 条轨里一条都弹不了，去编辑器里处理一下\""),
                "空状态那句状态行照旧");
            Assert.That(装, Does.Contain("EmptyBox.IsVisible = !any;"));
            Assert.That(装, Does.Contain("TrackHint.IsVisible = any;"));
        });
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
    /// 数一段代码里某串字出现了几次。**先去过注释**再用（注释里写一句 `StartButton.IsEnabled = …`
    /// 也能把「只剩一处」这条断言喂饱，而它要看的自始至终是代码本身）。
    /// </summary>
    private static int 数一数(string source, string 找)
    {
        int 数 = 0;
        for (int i = source.IndexOf(找, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(找, i + 找.Length, StringComparison.Ordinal))
        {
            数++;
        }
        return 数;
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
