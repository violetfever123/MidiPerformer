using System.Runtime.CompilerServices;
using MidiPerformer.Adapters.Gateways;
using MidiPerformer.App.Views;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Project;
using NUnit.Framework;
using ModelNote = MidiPerformer.Core.Model.Note;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 「工程文件当缓存」这件事的可执行形式（<see cref="SongCache"/>）：
/// <list type="bullet">
/// <item><b>保存一定写两份</b> —— 四种输入写出完全一样的结果（A）；</item>
/// <item><b>缓存一条都不丢</b> —— 和 <c>SongProjectWriteTests</c> 那四条「走一趟 mid 就丢」逐条对着来（B）；</item>
/// <item><b>缓存没了 / 坏了就静默读 <c>.mid</c></b> —— 不抛、不拦路，代价（移调没了）也断言出来（C）。</item>
/// </list>
///
/// 后一半（D：v1 的 <c>.mproj</c> 判成读不出来）在 <c>SongProjectFileTests</c> 里 ——
/// 那是 <see cref="SongProjectFile.TryReadProjectHeader"/> 自己的规矩；这儿有一条顺着它走完的
/// 「v1 缓存 → 降级读 <c>.mid</c>」，因为那正是这条规矩在真路上的样子。
///
/// <b>为什么这些断言值得写</b>：缓存这条路坏掉**一样不报错** —— 移调悄悄没了、列表那一格说错话，
/// 屏幕上没有任何东西看得出区别。而 <c>MainWindow</c> 里那两处是私有的
/// （起一个真窗口在 NUnit 里要一台有桌面会话的机器），所以证据得来自这个能拿真文件喂的缝；
/// 主窗口那一半由本节末尾的源文件守卫钉住（和 <c>PerformerSongTests</c> 是同一套办法）。
/// </summary>
public class SongCacheTests
{
    private const long Quarter = 480;

    private string _root = null!;
    private SongLibrary _library = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "mp-songcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _library = new SongLibrary(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不该让测试红
        }
    }

    // ==================== A. 保存一定写两份 ====================

    /// <summary>票上点名的四种输入。四种结果必须完全一样 —— 缓存不判断「需不需要」。</summary>
    private static IEnumerable<TestCaseData> 四种输入()
    {
        yield return 一种("移调非 0", 移调的(-12), edited: false);
        yield return 一种("有空轨", 带空轨的(), edited: false);
        yield return 一种("_edited == true", 一条音的(), edited: true);
        yield return 一种("一个都没占", 一条音的(), edited: false);
    }

    private static TestCaseData 一种(string 说明, Song song, bool edited) =>
        new TestCaseData(说明, song, edited).SetName($"保存一定写两份（{说明}）");

    /// <summary>
    /// **这一组的意义就一句：两条路都得走，一条都不许省。**
    /// 少一条就是 bug —— 少了缓存，移调、删光的轨、轨的身份再也回不来；
    /// 少了主文件，曲库里那首歌整个没了。
    /// </summary>
    [TestCaseSource(nameof(四种输入))]
    public void 保存一定写两份(string 说明, Song song, bool edited)
    {
        SongCache.Save(_library, "这一首", song, SongCache.HeaderFor("这一首", song, edited, @"C:\我的谱子\别处.mid"));

        var header = SongProjectFile.TryReadProjectHeader(_library.WorkPathOf("这一首"));

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(_library.PathOf("这一首")), Is.True,
                $"{说明}：songs\\这一首.mid 得在");
            Assert.That(File.Exists(_library.WorkPathOf("这一首")), Is.True,
                $"{说明}：songs\\.work\\这一首.mproj 得在");
            Assert.That(_library.Names(), Is.EqualTo(new[] { "这一首" }),
                $"{说明}：缓存不是曲库成员，列表里不该多出一首");

            Assert.That(header, Is.Not.Null, $"{说明}：刚写下去的缓存得是当前版本、读得出来");
            Assert.That(header!.Edited, Is.EqualTo(edited),
                $"{说明}：保存这一刻「动过没有」原样写进 header —— 曲库列表上「编辑过」就靠它");
            Assert.That(header.ImportedFrom, Is.EqualTo(@"C:\我的谱子\别处.mid"),
                $"{说明}：来路没有任何特殊待遇，就是个字段");
            Assert.That(header.PlayableTrackCount, Is.EqualTo(PlayableTracks.Of(song).Count),
                $"{说明}：可弹轨数也是这一刻算出来存下的");
        });
    }

    /// <summary>
    /// 写下去的两份**各自**都读得回来，而这不是多写一份文件：
    /// <c>.mid</c> 那一份是给别人的（别的软件也认），<c>.mproj</c> 那一份是本程序自己的（装得下移调）。
    /// </summary>
    [Test]
    public void 两份各自都读得回来()
    {
        var song = 移调的(-12);

        SongCache.Save(_library, "这一首", song, SongCache.HeaderFor("这一首", song, false, null));

        var 从mid读的 = MidiReader.ReadBytes(_library.ReadBytes("这一首"));
        var (_, 从缓存读的) = SongProjectFile.LoadProject(_library.WorkPathOf("这一首"));

        Assert.Multiple(() =>
        {
            Assert.That(从mid读的.Tracks.Single().Notes.Single().Pitch, Is.EqualTo(48),
                "标准 MIDI 那一份：移调已经烧进音高了（60 - 12）");
            Assert.That(从mid读的.Tracks.Single().Transpose, Is.EqualTo(0),
                "标准 MIDI 里没有「移调」这东西");

            Assert.That(从缓存读的.Tracks.Single().Transpose, Is.EqualTo(-12),
                "缓存那一份：移调原样在");
            Assert.That(从缓存读的.Tracks.Single().Notes.Single().Pitch, Is.EqualTo(60),
                "缓存那一份：音符本身一个字节都没动过");
        });
    }

    // ==================== B. 缓存一条都不丢 ====================

    /// <summary>
    /// 52 票那四条「走一趟 mid 就丢」的形状（见 <c>SongProjectWriteTests</c> 末尾那一组）。
    /// </summary>
    private static IEnumerable<TestCaseData> 拿得回来的形状()
    {
        yield return 一个形状("移调非 0", 移调的(12));
        yield return 一个形状("音符被删光的轨", 带空轨的());
        yield return 一个形状("力度 0 的音", 力度0的());
        yield return 一个形状("兜底轨名", 兜底名的());
        yield return 一个形状("同一声道第二段的名字", 同一声道两段的());
    }

    private static TestCaseData 一个形状(string 说明, Song song) =>
        new TestCaseData(说明, song).SetName($"缓存一条都不丢（{说明}）");

    /// <summary>
    /// 存下去再读回来**逐字段相等** —— 走的是缓存那一趟。
    /// 每一条都对着 <c>SongProjectWriteTests</c> 里一条红的：那边丢什么，这边就得留住什么，
    /// 否则缓存就是多余的，「打开时有缓存就用缓存」也就没意义了。
    /// </summary>
    [TestCaseSource(nameof(拿得回来的形状))]
    public void 缓存一条都不丢(string 形状, Song song)
    {
        SongCache.Save(_library, "这一首", song, SongCache.HeaderFor("这一首", song, false, null));

        var loaded = SongCache.Load(_library, "这一首");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Not.Null, $"{形状}：这份缓存好好的，不该降级");
            SongAssert.Same(song, loaded.Song, $"{形状}：缓存那一趟是无损的");
        });
    }

    /// <summary>文件头那几格从缓存里原样回来（列表要显示的三个东西全在里面）。</summary>
    [Test]
    public void 文件头那几格从缓存里原样回来()
    {
        var song = 只有一条能弹的();

        SongCache.Save(_library, "那一首", song,
            SongCache.HeaderFor("那一首", song, edited: true, @"C:\我的谱子\那一首.mid"));

        var loaded = SongCache.Load(_library, "那一首");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Not.Null);
            Assert.That(loaded.Header!.Version, Is.EqualTo(SongProjectFile.ProjectVersion));
            Assert.That(loaded.Header.Name, Is.EqualTo("那一首"));
            Assert.That(loaded.Header.Edited, Is.True, "「编辑过」那一格");
            Assert.That(loaded.Header.ImportedFrom, Is.EqualTo(@"C:\我的谱子\那一首.mid"));
            Assert.That(loaded.Header.PlayableTrackCount, Is.EqualTo(1), "「能不能弹」那一格：三条轨里只有一条能弹");
            Assert.That(loaded.Header.PlayableTrackCount, Is.EqualTo(PlayableTracks.Of(song).Count),
                "存下的数和列表要用的数是同一个算法算出来的");
        });
    }

    // ==================== C. 打开时的降级 ====================

    /// <summary>缓存不存在（从别处拷进来的曲子、或者用户把 <c>.work\</c> 删了）→ 读 <c>.mid</c> 继续。</summary>
    [Test]
    public void 没有缓存就静默读mid()
    {
        _library.WriteBytes("散装的", MidiWriter.WriteBytes(一条音的()));

        var loaded = SongCache.Load(_library, "散装的");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Null, "没有缓存 → 「改过没有」「从哪来」没有就是没有");
            SongAssert.Same(一条音的(), loaded.Song, "曲子照样读得出来");
        });
    }

    /// <summary>缓存坏了（截断 / JSON 坏 / 版本不对）→ **同样**读 <c>.mid</c> 成功，不弹窗、不拦路。</summary>
    [TestCase("", "空文件")]
    [TestCase("{ \"Version\": 2, \"Name\": \"写了一半\"", "截断的 JSON")]
    [TestCase("这不是 JSON", "压根不是 JSON")]
    [TestCase("[1, 2, 3]", "顶层不是对象")]
    [TestCase("{\"Version\": 9999, \"Name\": \"来自未来\"}", "版本比当前新")]
    [TestCase("{\"Version\": 1, \"Name\": \"老版本\"}", "版本比当前老（v1：没有 PlayableTrackCount）")]
    public void 缓存坏了也静默读mid(string content, string 坏法)
    {
        var song = 一条音的();
        _library.WriteBytes("半坏的", MidiWriter.WriteBytes(song));
        _library.WriteWork("半坏的", content);

        var loaded = SongCache.Load(_library, "半坏的");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Null, $"{坏法}：当这份缓存不在");
            SongAssert.Same(song, loaded.Song, $"{坏法}：读 .mid 继续，不抛、不拦路");
        });
    }

    /// <summary>
    /// 头读得出来、**身子**读不出来（JSON 合法但里面那棵树坏了，例如写到一半断电）。
    /// <see cref="SongProjectFile.TryReadProjectHeader"/> 按契约照样读得出头，
    /// 所以这一条走的是 <see cref="SongCache.Load"/> 里那个「头过了、身子抛了」的分支 ——
    /// 它要是漏了，用户点开这一首会看到一句报错，而那份 <c>.mid</c> 明明好好的。
    /// </summary>
    [Test]
    public void 缓存的头读得出来身子坏了也静默读mid()
    {
        var song = 一条音的();
        SongCache.Save(_library, "半坏的", song, SongCache.HeaderFor("半坏的", song, true, null));

        // 头那几个字段完好，谱面那棵树坏掉
        _library.WriteWork("半坏的", File.ReadAllText(_library.WorkPathOf("半坏的"))
            .Replace("\"Song\": {", "\"Song\": \"这不是谱面\", \"扔掉\": {"));

        Assert.That(SongProjectFile.TryReadProjectHeader(_library.WorkPathOf("半坏的")), Is.Not.Null,
            "前提：这一份的头真的读得出来（坏的是身子）");

        var loaded = SongCache.Load(_library, "半坏的");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Null, "头读得出来也没用 —— 身子读不出来就当这份缓存不在");
            SongAssert.Same(song, loaded.Song, "读 .mid 继续");
        });
    }

    /// <summary>
    /// ⚠️ **降级那一趟的代价值多少，这条就断言多少**：移调没了。
    /// 跟上面两条放一起，读的人才知道「读 <c>.mid</c> 继续」意味着什么。
    /// </summary>
    [Test]
    public void 降级那一趟移调确实没了()
    {
        var song = 移调的(12);
        SongCache.Save(_library, "移调", song, SongCache.HeaderFor("移调", song, false, null));

        // 缓存没了（用户在资源管理器里把 .work 整个删了、或者文件被磁盘故障吃掉）
        File.Delete(_library.WorkPathOf("移调"));

        var loaded = SongCache.Load(_library, "移调");

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Header, Is.Null, "没有缓存了");
            Assert.That(loaded.Song.Tracks.Single().Transpose, Is.EqualTo(0), "**代价**：移调没了");
            Assert.That(loaded.Song.Tracks.Single().Notes.Single().Pitch, Is.EqualTo(72),
                "移调被烧进音高写进 .mid 了（60 + 12）—— 声音还在，但「它原本是 60」回不来了");
        });
    }

    /// <summary>连 <c>.mid</c> 都没有 / 都读不出来：这一趟没得降级，交给调用方去报那一句中文。</summary>
    [Test]
    public void 连mid都没有时报的还是那一句中文()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SongCache.Load(_library, "查无此曲"));

        Assert.That(ex!.Message, Does.Contain("查无此曲"), "错误消息得说清是哪一首");
    }

    // ==================== 缝的另一半：主窗口那两处真的接了线 ====================

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static string 主窗口代码() => File.ReadAllText(Path.Combine(AppDir, "Views", "MainWindow.axaml.cs"));

    /// <summary>
    /// 保存那一处**只有一条路**：写两份这件事整个交给 <see cref="SongCache.Save"/>，
    /// 自己不碰曲库的写方法 —— 主窗口里再出现一次 <c>WriteBytes</c> / <c>WriteWork</c>，
    /// 就又有了一条能单独坏掉的路（票上那张三条件表就是这么长出来的）。
    /// </summary>
    [Test]
    public void 保存那一处只走缓存这一条路()
    {
        var 保存 = 花括号段(主窗口代码(), "private void SaveTo", "找不到 SaveTo");

        Assert.Multiple(() =>
        {
            Assert.That(保存, Does.Contain("SongCache.Save("), "写两份是 SongCache 的事");
            Assert.That(保存, Does.Not.Contain("library.Write"), "别自己动手写曲库");
            Assert.That(保存, Does.Not.Contain("WriteBytes"), "别自己动拼 MIDI 字节");
            Assert.That(保存, Does.Not.Contain("WriteProject"), "别自己动手写工程文件");
        });
    }

    /// <summary>
    /// **无条件**：保存那一处除了「手上没有曲子就返回」这一句，不再有第二个判据 ——
    /// 「缓存需不需要写」在这份代码里不存在这个问题。
    /// </summary>
    [Test]
    public void 保存那一处没有第二个判据()
    {
        var 保存 = 花括号段(主窗口代码(), "private void SaveTo", "找不到 SaveTo");

        Assert.Multiple(() =>
        {
            Assert.That(数一数(保存, "if ("), Is.EqualTo(1), "只该有「手上没有曲子就返回」那一句判据");
            Assert.That(保存, Does.Contain("if (_song is not { } song) return;"));
            Assert.That(保存, Does.Contain("catch (InvalidDataException"), "写不进去照样只有那一种异常");
        });
    }

    /// <summary>
    /// 填 header 用的是**此刻**的 <c>_edited</c>，而保存这一处**不写** <c>_edited</c> ——
    /// 它是粘性的（只有 <c>LoadSong</c> 清它）。保存后清成 <c>false</c> 的话，
    /// 第二次保存写下的就是「没改过」，曲库列表上那三个字从此再也不出现。
    /// </summary>
    [Test]
    public void 填了这一刻的改过没有而保存不清它()
    {
        var 保存 = 花括号段(主窗口代码(), "private void SaveTo", "找不到 SaveTo");

        Assert.Multiple(() =>
        {
            Assert.That(保存, Does.Contain("SongCache.HeaderFor(name, song, _edited, _importedFrom)"),
                "写进 header 的就是这一刻的 _edited 与 _importedFrom");
            Assert.That(数一数(保存, "_edited"), Is.EqualTo(1), "只读一次，不赋值");
            Assert.That(保存, Does.Not.Contain("_edited ="), "保存不清 _edited —— 清它的只有 LoadSong");
        });
    }

    /// <summary>打开曲库那一处走的是 <see cref="SongCache.Load"/>，读出来的三个值各归各位。</summary>
    [Test]
    public void 打开曲库那一处走缓存的路()
    {
        var 打开 = 花括号段(主窗口代码(), "private string? TryOpenLibrarySong", "找不到 TryOpenLibrarySong");

        Assert.Multiple(() =>
        {
            Assert.That(打开, Does.Contain("SongCache.Load(library, name)"), "有缓存用缓存、没有就读 .mid：都在那一处");
            Assert.That(打开, Does.Not.Contain("MidiReader."), "降级那一路在 SongCache.Load 里面，别在这儿另写一条");
            Assert.That(打开, Does.Not.Contain("SongProjectFile."), "同上");
        });
    }

    /// <summary>
    /// **顺序是要害**：<c>LoadSong</c> 会把 <c>_edited</c> / <c>_importedFrom</c> 清掉（它不知道新来的是哪一份），
    /// 所以「从文件头补上」必须排在它**之后**。排前面等于没读缓存：
    /// 曲库列表上那首曲子的「编辑过」永远是空的。
    /// </summary>
    [Test]
    public void 打开曲库那一处补文件头排在装曲子之后()
    {
        var 打开 = 花括号段(主窗口代码(), "private string? TryOpenLibrarySong", "找不到 TryOpenLibrarySong");

        // ⚠️ 锚点只到参数表的一半，**刻意不带右括号**。
        //    原来这里是 `"LoadSong(song, name)"` —— 那个右括号等于顺手把「这次调用长什么形状」
        //    也钉进了这条测试。62 号做 ②（开曲耗时进日志）时一度要给 `LoadSong` 加个可选尾参，
        //    那会写成 `LoadSong(song, name, plan)`，`IndexOf` 就回 -1，于是这条测试红在一个
        //    **跟它要测的顺序毫无关系**的地方（装曲子这件事一点没坏）。
        //    ⇒ 这条测试要钉的是「补 `_edited`/`_importedFrom` 排在装曲子**之后**」，
        //      与这次调用带几个参数无关，锚点收到参数表一半为止。
        //    （62 号最后**没有**改这个调用的形状 —— 秒表走的是 `MainWindow` 的私有字段
        //      `_loadingPlan`，调用点至今仍是 `LoadSong(song, name)`，所以原来那个紧锚点今天也过。
        //      放松的锚点留着：它是紧锚点的超集，而且更贴这条测试真正要钉的东西。（62 号改，见 62 号票。））
        int 装 = 打开.IndexOf("LoadSong(song, name", StringComparison.Ordinal);
        int 改过 = 打开.IndexOf("_edited = loaded.Header?.Edited ?? false;", StringComparison.Ordinal);
        int 来路 = 打开.IndexOf("_importedFrom = loaded.Header?.ImportedFrom;", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(装, Is.GreaterThanOrEqualTo(0), "这一处得先把曲子装上去");
            Assert.That(改过, Is.GreaterThan(装), "补 _edited 排在 LoadSong 之后");
            Assert.That(来路, Is.GreaterThan(装), "补 _importedFrom 也排在它之后");
        });
    }

    /// <summary>整个主窗口里碰缓存只有那两处（加一次 <c>HeaderFor</c>），没有第三条路。</summary>
    [Test]
    public void 碰缓存的只有那两处()
    {
        var 代码 = 只读代码(主窗口代码());

        Assert.Multiple(() =>
        {
            Assert.That(数一数(代码, "SongCache.Save("), Is.EqualTo(1), "写缓存只有一处");
            Assert.That(数一数(代码, "SongCache.Load("), Is.EqualTo(1), "读缓存只有一处");
            Assert.That(数一数(代码, "library.PathOf(") + 数一数(代码, "library.WorkPathOf("), Is.EqualTo(0),
                "主窗口不该自己去拼曲库里的路径 —— 那是曲库自己的事");
        });
    }

    /// <summary>
    /// 守卫自己也要有人看着：<see cref="只读代码"/> 真的去掉了注释（上面那些断言靠它），
    /// 而**字符串里的字不能动** —— 注释被改写不该让断言变绿，字符串被误删却会让真断言变红。
    /// </summary>
    [Test]
    public void 守卫用的去注释是真的去了()
    {
        const string 假源码 = """
            // 这一行注释里的 _edited = false; 不算数
            /* 这一块注释里的 _edited = false; 也不算数 */
            var 句子 = "_edited = false;";
            _edited = false;
            """;

        var 留下 = 只读代码(假源码);

        Assert.Multiple(() =>
        {
            Assert.That(留下, Does.Not.Contain("注释"), "注释得整段去掉");
            Assert.That(数一数(留下, "_edited"), Is.EqualTo(2),
                "字符串里那一次 + 真代码那一次 —— 注释里那两次已经没了");
            Assert.That(留下, Does.Contain("\"_edited = false;\""), "字符串里的字一个都不能动");
        });
    }

    // ==================== 守卫用的帮手（与 PerformerSongTests 同一套） ====================

    /// <summary>
    /// 去掉注释，留下代码（和字符串里的字）。不是完整词法分析：够用就行 ——
    /// 它要挡的是「注释里写一句一样的话把断言喂饱」。
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

    /// <summary>数一段代码里某串字出现了几次。**先去过注释**再用。</summary>
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

    /// <summary>取一个方法的花括号体；文本**先去注释**再找（理由同 <see cref="只读代码"/>）。</summary>
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

    // ==================== 手搭的曲子 ====================

    private static ModelTempoMap 速度表() => new(ModelTimeDivision.PulsesPerQuarter(480));

    /// <summary>一轨一个音的最小曲子。</summary>
    private static Song 一条音的() => new(
        new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, Quarter, 100) }) },
        速度表());

    /// <summary>移调非 0 —— 标准 MIDI 装不下它（写出去会被烧进音高）。</summary>
    private static Song 移调的(int transpose) => new(
        new[] { new Track(0, 0, "移调", 0, new[] { new ModelNote(60, 0, Quarter, 100) }, transpose) },
        速度表());

    /// <summary>音符被删光的那条轨：标准 MIDI 里空轨块不成轨，缓存里得留着。</summary>
    private static Song 带空轨的() => new(
        new[]
        {
            new Track(0, 0, "还有音", 0, new[] { new ModelNote(60, 0, Quarter, 100) }),
            new Track(1, 1, "被删光的那条", 24, Array.Empty<ModelNote>())
        },
        速度表());

    /// <summary>力度 0 的音：MIDI 里那是抬键，写出端得把它夹成 1。</summary>
    private static Song 力度0的() => new(
        new[] { new Track(0, 0, "主旋律", 0, new[] { new ModelNote(60, 0, Quarter, 0) }) },
        速度表());

    /// <summary>轨名正好等于兜底名：写出端不写轨名事件，「改过名」与「从来没名字」在 .mid 里分不出来。</summary>
    private static Song 兜底名的() => new(
        new[] { new Track(0, 0, "声道 1", 0, new[] { new ModelNote(60, 0, Quarter, 100) }) },
        速度表());

    /// <summary>同一轨块、同一声道的第二段：一个轨块只写一条轨名，第二段的名字在 .mid 里没地方待。</summary>
    private static Song 同一声道两段的() => new(
        new[]
        {
            new Track(0, 0, "第一段", 24, new[] { new ModelNote(60, 0, Quarter, 100) }),
            new Track(0, 0, "第二段", 42, new[] { new ModelNote(64, Quarter, Quarter, 100) })
        },
        速度表());

    /// <summary>三条轨里只有一条能弹：一条单声部、一条打击乐、一条两个音叠着。</summary>
    private static Song 只有一条能弹的() => new(
        new[]
        {
            new Track(0, 0, "能弹的", 0, new[] { new ModelNote(60, 0, Quarter, 100) }),
            new Track(1, PlayableTracks.PercussionChannel, "打击乐", 0,
                new[] { new ModelNote(38, 0, Quarter, 100) }),
            new Track(2, 2, "两个音叠着", 0,
                new[] { new ModelNote(60, 0, Quarter, 100), new ModelNote(64, 0, Quarter, 100) })
        },
        速度表());
}
