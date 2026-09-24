using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 曲库：硬盘上一个平铺的目录，文件名就是曲名。每份测试用它自己的临时目录，跑完就删。
/// 盯的是目录操作这一层：有哪些名字、名字对应哪个文件、改名 / 删除真的动了盘。
/// 曲库成员是**标准 MIDI（<c>.mid</c>）**；文件里的字节 / JSON 对不对分别是
/// <c>MidiReader</c> / <see cref="SongProjectFile"/> 的事，这一层不解析内容。
/// </summary>
public class SongLibraryTests
{
    private string _root = null!;
    private SongLibrary _library = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "mp-songlib-" + Guid.NewGuid().ToString("N"));
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

    // ==================== 有哪些曲子 ====================

    [Test]
    public void 写进去的曲子在列表里()
    {
        _library.Write("起风了", "{}");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Names(), Is.EqualTo(new[] { "起风了" }));
            Assert.That(_library.Contains("起风了"), Is.True);
        });
    }

    [Test]
    public void 列表按名字排序()
    {
        foreach (var name in new[] { "C大调", "A小调", "B大调" })
            _library.Write(name, "{}");

        Assert.That(_library.Names(), Is.EqualTo(new[] { "A小调", "B大调", "C大调" }),
            "列表得是排好序的：界面直接照着画，不该每画一行排一次");
    }

    /// <summary>目录还不存在（装完还没导入过任何曲子）时列表是空的，不是错误。</summary>
    [Test]
    public void 目录不存在时列表是空的()
    {
        var missing = new SongLibrary(Path.Combine(_root, "还没建出来的子目录"));

        Assert.Multiple(() =>
        {
            Assert.That(missing.Names(), Is.Empty);
            Assert.That(missing.Contains("随便什么"), Is.False);
        });
    }

    /// <summary>
    /// 曲库成员就是这种文件：后缀是 <c>.mid</c>（标准 MIDI）。
    /// 这个常量是 <c>const</c>、会被内联进调用方的程序集，改它必须整体重建，所以在这儿钉一条明文。
    /// </summary>
    [Test]
    public void 曲库成员的后缀是mid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SongLibrary.Extension, Is.EqualTo(".mid"));
            Assert.That(_library.PathOf("起风了"), Is.EqualTo(Path.Combine(_root, "起风了.mid")));
        });
    }

    /// <summary>只认 .mid：说明文件、老的工程文件、备份文件、没后缀的都不算曲子。</summary>
    [Test]
    public void 不是mid的文件不算曲子()
    {
        _library.Write("正经曲子", "{}");
        File.WriteAllText(Path.Combine(_root, "说明.txt"), "这个文件夹是放曲子的");
        File.WriteAllText(Path.Combine(_root, "老的工程.mproj"), "{}");
        File.WriteAllText(Path.Combine(_root, "backup.mid.bak"), "{}");
        File.WriteAllText(Path.Combine(_root, "没有后缀"), "{}");

        Assert.That(_library.Names(), Is.EqualTo(new[] { "正经曲子" }));
    }

    /// <summary>后缀大小写不敏感：自己写的是小写，但 ".MID" 不该被漏掉。</summary>
    [Test]
    public void 后缀大小写不影响认出曲子()
    {
        File.WriteAllText(Path.Combine(_root, "大写后缀.MID"), "{}");
        File.WriteAllText(Path.Combine(_root, "混合大小写.Mid"), "{}");

        Assert.That(_library.Names(), Is.EquivalentTo(new[] { "大写后缀", "混合大小写" }));
    }

    /// <summary>
    /// 曲库目录里的**子目录**内容不上列表（下一票的缓存住在 <c>songs\.work\</c> 里）。
    /// 这条靠的是 <see cref="SongLibrary.Names"/> 用的是无参的 <c>EnumerateFiles()</c>，
    /// 默认只扫本层（TopDirectoryOnly）—— **不是**别的排除逻辑。
    ///
    /// 子目录里那个文件特意用 <c>.mid</c>：只放 .mproj 的话后缀过滤本身就会把它挡掉，
    /// 「不递归」这个机制根本没被测到（换成递归枚举那条断言照样绿）。
    /// </summary>
    [Test]
    public void 子目录里的东西不上列表()
    {
        _library.Write("台面上的", "{}");

        string work = Path.Combine(_root, ".work");
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "藏在子目录里的.mid"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(work, "缓存.mproj"), "{}");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(work, "藏在子目录里的.mid")), Is.True,
                "前提：那个 .mid 真的在盘上，否则上面那条断言是空转");
            Assert.That(_library.Names(), Is.EqualTo(new[] { "台面上的" }));
        });
    }

    /// <summary>
    /// Q29 的过渡期守卫：老曲库（<c>.mproj</c>）还在 <c>songs\</c> 里躺着，程序照常跑 ——
    /// 只是列表按后缀认，看不见它们。**只是看不见，不是被清理掉**。
    /// </summary>
    [Test]
    public void 残留的老工程文件自动无视()
    {
        File.WriteAllText(Path.Combine(_root, "夜的钢琴曲.mproj"), "{\"Version\": 1}");
        _library.Write("新存的", "{}");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Names(), Is.EqualTo(new[] { "新存的" }));
            Assert.That(_library.Contains("夜的钢琴曲"), Is.False, "老文件不是曲库成员");
            Assert.That(File.Exists(Path.Combine(_root, "夜的钢琴曲.mproj")), Is.True,
                "它还在盘上 —— 过渡期不该顺手把人家的老文件删掉");
        });
    }

    // ==================== 读 / 写 ====================

    [Test]
    public void 写进去再读回来一字不差()
    {
        const string text = "{\n  \"Name\": \"起风了\",\n  \"歌\": \"这一行有中文和换行\"\n}";

        _library.Write("起风了", text);

        Assert.That(_library.Read("起风了"), Is.EqualTo(text));
    }

    /// <summary>
    /// 曲库不认识 JSON：文件里装的是什么，读出来就是什么 ——
    /// 「读不出来的工程怎么办」是 <see cref="SongProjectFile"/> 的判断。
    /// </summary>
    [Test]
    public void 读回来的是原样的文本_哪怕它不是合法的工程()
    {
        _library.Write("坏文件", "这不是 JSON");

        Assert.That(_library.Read("坏文件"), Is.EqualTo("这不是 JSON"));
    }

    [Test]
    public void 读不存在的曲子会报错()
    {
        var ex = Assert.Throws<InvalidDataException>(() => _library.Read("没这首"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("没这首"), "得说清是哪一首");
            Assert.That(ex.Message, Does.Contain("曲库里没有"));
        });
    }

    /// <summary>第一次导入的时候曲库目录还不存在 —— 写就得把它建出来。</summary>
    [Test]
    public void 写的时候目录不存在会建出来()
    {
        string nested = Path.Combine(_root, "songs");
        var library = new SongLibrary(nested);

        library.Write("新建的曲子", "{}");

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(nested), Is.True);
            Assert.That(library.Read("新建的曲子"), Is.EqualTo("{}"));
        });
    }

    /// <summary>同名再写一次就是覆盖 —— 那是「保存」，不是撞名。</summary>
    [Test]
    public void 同名再写一次是覆盖()
    {
        _library.Write("存两次", "第一次");
        _library.Write("存两次", "第二次");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Read("存两次"), Is.EqualTo("第二次"));
            Assert.That(_library.Names(), Has.Count.EqualTo(1), "覆盖不该多出一个文件");
        });
    }

    [Test]
    public void 曲库目录是空的时构造会拒绝()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() => new SongLibrary(""));
            Assert.Throws<InvalidDataException>(() => new SongLibrary("   "));
        });
    }

    [Test]
    public void 曲名对应哪个文件是目录加名字加后缀()
    {
        Assert.That(_library.PathOf("起风了"),
            Is.EqualTo(Path.Combine(_library.Directory, "起风了" + SongLibrary.Extension)));
    }

    // ==================== 删除 ====================

    [Test]
    public void 删掉的曲子文件也没了()
    {
        _library.Write("删我", "{}");

        _library.Delete("删我");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Names(), Is.Empty);
            Assert.That(_library.Contains("删我"), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "删我" + SongLibrary.Extension)), Is.False,
                "不能只是列表里不显示，盘上得真的没了");
        });
    }

    /// <summary>删一个不存在的名字报错，不是静默成功。</summary>
    [Test]
    public void 删不存在的曲子会报错()
    {
        var ex = Assert.Throws<InvalidDataException>(() => _library.Delete("本来就没有"));

        Assert.That(ex!.Message, Does.Contain("本来就没有"));
    }

    [Test]
    public void 删掉一首不影响别的()
    {
        _library.Write("留着", "A");
        _library.Write("删掉", "B");

        _library.Delete("删掉");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Names(), Is.EqualTo(new[] { "留着" }));
            Assert.That(_library.Read("留着"), Is.EqualTo("A"));
        });
    }

    // ==================== 改名 ====================

    /// <summary>改名就是改文件名：文件换了名字，内容一个字节都不动。</summary>
    [Test]
    public void 改名真的换了文件名而内容一字不差()
    {
        const string text = "{\n  \"Name\": \"旧名字\",\n  \"一个音符\": 60\n}";
        _library.Write("旧名字", text);
        string oldPath = _library.PathOf("旧名字");

        _library.Rename("旧名字", "新名字");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oldPath), Is.False, "旧文件得没");
            Assert.That(_library.Read("新名字"), Is.EqualTo(text), "内容一个字节都不该动（连里面的曲名也不动）");
            Assert.That(_library.Names(), Is.EqualTo(new[] { "新名字" }));
        });
    }

    /// <summary>改成同一个名字是空操作，不报错也不动盘（用户点了确定但什么都没改）。</summary>
    [Test]
    public void 改成同一个名字是空操作()
    {
        _library.Write("没变", "内容");

        _library.Rename("没变", "没变");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Names(), Is.EqualTo(new[] { "没变" }));
            Assert.That(_library.Read("没变"), Is.EqualTo("内容"));
        });
    }

    /// <summary>
    /// 改名撞名报错，不覆盖 —— 和「写」正好相反（写是同名即覆盖，改名顺手覆盖等于把对方那首删了）。
    /// </summary>
    [Test]
    public void 改名撞名会报错而且两首都在()
    {
        _library.Write("甲", "甲的内容");
        _library.Write("乙", "乙的内容");

        var ex = Assert.Throws<InvalidDataException>(() => _library.Rename("甲", "乙"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("乙"), "得说清是哪个名字被人用了");
            Assert.That(_library.Read("甲"), Is.EqualTo("甲的内容"), "被改的那首还在原处");
            Assert.That(_library.Read("乙"), Is.EqualTo("乙的内容"), "撞上的那首一个字都没被覆盖");
            Assert.That(_library.Names(), Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void 改名时旧名字不存在会报错()
    {
        var ex = Assert.Throws<InvalidDataException>(() => _library.Rename("没有这首", "新名字"));

        Assert.That(ex!.Message, Does.Contain("没有这首"));
    }

    /// <summary>只改大小写不算撞名：Windows 上那本来就是同一个文件。</summary>
    [Test]
    public void 只改大小写不算撞名()
    {
        _library.Write("Song", "内容");

        _library.Rename("Song", "SONG");

        Assert.Multiple(() =>
        {
            // 重点是盘上那个名字的大小写真的变了：Windows 不分大小写，
            // File.Exists 与 Read("SONG") 在改名没生效时照样成立，光看那两条会被骗过去
            Assert.That(_library.Names(), Is.EqualTo(new[] { "SONG" }), "盘上的名字得真的变成新写法");
            Assert.That(_library.Read("SONG"), Is.EqualTo("内容"), "改的是名字，内容一个字节都不动");
            Assert.That(_library.Contains("song"), Is.True, "Windows 上这个名字就是那个文件");
        });
    }

    // ==================== 曲名消毒 ====================

    /// <summary>Windows 文件名里不能出现的字符会被去掉（去掉了名字还能用）。</summary>
    [TestCase("a/b", "ab")]
    [TestCase("a\\b", "ab")]
    [TestCase("a:b", "ab")]
    [TestCase("a*b?c", "abc")]
    [TestCase("a<b>c|d\"e", "abcde")]
    [TestCase("  两头的空格  ", "两头的空格")]
    [TestCase("结尾的点...", "结尾的点")]
    [TestCase("结尾的点... ", "结尾的点")]
    [TestCase("\t制表符换行\n", "制表符换行")]
    public void 曲名里的非法字符会被去掉(string written, string expected)
    {
        Assert.That(SongLibrary.Sanitize(written), Is.EqualTo(expected));
    }

    /// <summary>去掉非法字符之后只剩空白的名字当不了文件名。</summary>
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("///")]
    [TestCase("...")]
    [TestCase("...   ...")]
    public void 空名字会被拒绝(string written)
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() => SongLibrary.Sanitize(written));
            Assert.That(SongLibrary.IsUsableName(written), Is.False);
            Assert.Throws<InvalidDataException>(() => _library.Write(written, "{}"));
        });
    }

    /// <summary>
    /// Windows 保留的设备名当不了文件名：带后缀也一样会被当成设备，"CON.mid" 建不出来。
    /// </summary>
    [TestCase("CON")]
    [TestCase("con")]
    [TestCase("NUL")]
    [TestCase("COM1")]
    [TestCase("lpt9")]
    [TestCase("CON.mid")]
    public void 保留设备名会被拒绝(string written)
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() => SongLibrary.Sanitize(written));
            Assert.That(SongLibrary.IsUsableName(written), Is.False);
        });
    }

    /// <summary>
    /// 消毒是写和读都做的：带非法字符的名字写得进也读得出（两边消毒结果一样，落在同一个文件上）。
    /// </summary>
    [Test]
    public void 带非法字符的名字写得进也读得出()
    {
        _library.Write("起风了/第一章", "内容");

        Assert.Multiple(() =>
        {
            Assert.That(_library.Read("起风了/第一章"), Is.EqualTo("内容"));
            Assert.That(_library.Names(), Is.EqualTo(new[] { "起风了第一章" }));
            Assert.That(_library.Contains("起风了第一章"), Is.True);
        });
    }

    /// <summary>能用的名字判断得和消毒一致 —— 界面拿它灰确定按钮。</summary>
    [TestCase("起风了", true)]
    [TestCase("a b c", true)]
    [TestCase("CON", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void 能用的名字判断(string? name, bool usable)
    {
        Assert.That(SongLibrary.IsUsableName(name), Is.EqualTo(usable));
    }
}
