using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MidiPerformer.App.Views;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 49 号：音域读数（38 根细条）的那一半**不靠眼睛看**的判据。
///
/// 判据分两层：
/// - 窗口怎么切、这首歌压在窗口哪几格、读数行写什么字 —— 都是 <see cref="PitchRangeReadout"/>
///   里的纯函数（一份 Avalonia 都不碰），直接喂真数据断言。示例曲子是曲库里
///   「（三角洲适配）勾指起誓」轨 1 逐音统计出来的那份真数据（见 <see cref="勾指起誓"/>），
///   **一个数都没改** —— 对不上就是有一边错了。
/// - 「窗口里摆的是这 38 根、四段是 12:12:12:2、段容器不许被撑高、升号只降不透明度」——
///   读 XAML 文本断言（和 47 号的 <c>OverlaySurfaceTests</c> 同一套办法）。
///
/// <b>证明不了什么</b>：屏幕上真画成什么样（四段真的一样宽一样高、像素上真没有哪一段更高）
/// 是上机量出来的，归 <c>.scratch/midi-performer/issues/49</c> 的验收记录；
/// 「拿真曲子打开窗口看到 20 根亮着」要等 71 号把曲子递进演奏器窗口。
/// </summary>
public class PitchRangeReadoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ==================== 示例曲子（真数据，别改） ====================

    /// <summary>
    /// 「（三角洲适配）勾指起誓」轨 1「无标题」的逐音直方图：454 个音符、20 个不同的音高、
    /// 音域 C3(48) – A#5(82)。和 <c>docs/prototype-演奏器读数.html</c> 里那份逐字一致。
    /// </summary>
    private static readonly Dictionary<int, int> 勾指起誓 = new()
    {
        [48] = 2, [50] = 4, [53] = 7, [55] = 9, [57] = 5, [58] = 1,
        [60] = 28, [62] = 5, [64] = 5, [65] = 176, [67] = 97, [69] = 37, [70] = 5,
        [72] = 40, [74] = 4, [76] = 2, [77] = 13, [79] = 3, [81] = 10, [82] = 1,
    };

    /// <summary>解开成逐音的一列（每个音出现几次就写几次）—— 越界的统计按音符个数算。</summary>
    private static List<int> 这首歌() =>
        勾指起誓.OrderBy(kv => kv.Key)
                .SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))
                .ToList();

    /// <summary>读数行的十个槽拼成一句话（界面上那行字就是它们连起来）。</summary>
    private static string 拼(string[] 槽) => string.Concat(槽);

    // ==================== 窗口：38 个半音、中间没洞 ====================

    /// <summary>
    /// 窗口是 <c>[12B, 12B+37]</c>，**38 个半音逐个查**，不是只查两端之后假设中间是满的。
    /// 两头各多查一个音：窗口外紧挨着的那两个都必须落在外面。
    /// </summary>
    [Test]
    public void 窗口是那三十八个半音中间一个洞都没有()
    {
        for (int b = 0; b <= 8; b++)
        {
            var 窗 = PitchRangeReadout.Window(b);

            Assert.That(窗, Has.Length.EqualTo(PitchRangeReadout.Bars), $"基准八度 {b} 的根数");
            Assert.That(窗[0], Is.EqualTo(12 * b), $"基准八度 {b} 的最低音");
            Assert.That(窗[^1], Is.EqualTo(12 * b + 37), $"基准八度 {b} 的最高音");

            for (int i = 0; i < 窗.Length; i++)
            {
                Assert.That(窗[i], Is.EqualTo(12 * b + i), $"基准八度 {b} 的第 {i + 1} 根（逐个查）");
                Assert.That(PitchRangeReadout.InWindow(窗[i], b), Is.True, $"第 {i + 1} 根不在窗口里");
                Assert.That(PitchRangeReadout.BarOf(窗[i], b), Is.EqualTo(i), $"第 {i + 1} 根套错格子");
            }

            Assert.That(PitchRangeReadout.InWindow(12 * b - 1, b), Is.False, "窗口下面那个音不该在里面");
            Assert.That(PitchRangeReadout.InWindow(12 * b + 38, b), Is.False, "窗口上面那个音不该在里面");
            Assert.That(PitchRangeReadout.BarOf(12 * b - 1, b), Is.Null);
            Assert.That(PitchRangeReadout.BarOf(12 * b + 38, b), Is.Null);
        }
    }

    /// <summary>
    /// 这 38 格和 <c>NoteMapper</c> 真放行的那 38 个音**一个不差**。
    ///
    /// 这是「无洞」最硬的那一条：格子是我在界面上切的，放行是执行时真按的 ——
    /// 两边对不上时，界面上会有一格亮着却弹不出来（或者反过来）。所以拿一串
    /// **比窗口两头各多两个音**的音高过一遍 <c>Map</c>，看它放行的正好是那 38 个。
    /// </summary>
    [Test]
    public void 这三十八个半音就是NoteMapper放行的三十八个()
    {
        for (int b = 1; b <= 7; b++)
        {
            var 谱面 = new List<RawNote>();
            for (int p = 12 * b - 2; p <= 12 * b + 39; p++)
                谱面.Add(new RawNote { Pitch = p, Start = 0, End = 1 });

            var 映射 = NoteMapper.Map(谱面, transpose: 0, manualBaseOctave: b);

            Assert.That(映射.BaseOctave, Is.EqualTo(b), "手给的基准八度没被采纳");

            var 放行 = 映射.Notes.Where(n => n.InRange).Select(n => n.Pitch).ToList();
            Assert.That(放行, Is.EqualTo(PitchRangeReadout.Window(b)),
                $"基准八度 {b}：NoteMapper 放行的和条子上那 38 格对不上 —— 有一边错了");
            Assert.That(映射.SkipCount, Is.EqualTo(谱面.Count - PitchRangeReadout.Bars),
                "放行的数目不是 38");
        }
    }

    /// <summary>四段各几根：12 / 12 / 12 / 2，加起来正好 38。段头那个音名就是各段的最低音。</summary>
    [Test]
    public void 四段是十二比十二比十二比二()
    {
        Assert.That(PitchRangeReadout.SegmentLengths, Is.EqualTo(new[] { 12, 12, 12, 2 }));
        Assert.That(PitchRangeReadout.SegmentLengths.Sum(), Is.EqualTo(PitchRangeReadout.Bars));
        Assert.That(PitchRangeReadout.SegmentTops(4), Is.EqualTo(new[] { 48, 60, 72, 84 }));
        Assert.That(PitchRangeReadout.SegmentTops(4).Select(Music.NoteName),
            Is.EqualTo(new[] { "C3", "C4", "C5", "C6" }));
    }

    // ==================== 示例数据的形状 ====================

    /// <summary>
    /// 示例曲子本身没被人改过（改了就说明有人拿它去迁就实现 —— 那是反的）。
    /// </summary>
    [Test]
    public void 示例曲子的形状没被人改过()
    {
        var 音 = 这首歌();

        Assert.Multiple(() =>
        {
            Assert.That(音, Has.Count.EqualTo(454), "音符数");
            Assert.That(音.Distinct().Count(), Is.EqualTo(20), "不同的音高数");
            Assert.That(音.Min(), Is.EqualTo(48));
            Assert.That(音.Max(), Is.EqualTo(82));
            Assert.That(Music.NoteName(48), Is.EqualTo("C3"));
            Assert.That(Music.NoteName(82), Is.EqualTo("A#5"));
            Assert.That(NoteMapper.AutoBaseOctave(音), Is.EqualTo(4), "自动选出来的基准八度");
        });
    }

    // ==================== 越界：三态 ====================

    /// <summary>
    /// 三态越界统计，**拿真数据测**：微调 0 → 0 个；+1 → 0 个；−1 → 2 个音符、占 0.4%。
    ///
    /// 这条是回归基线（规格里点名的那一首）：−1 那 2 个音符是 C3 降半音掉到 B2 ——
    /// 音乐符里出现两次，所以「音符数」是 2、列出来的音名只有一个。
    /// </summary>
    [Test]
    public void 越界统计三态是零加一零减一二()
    {
        var 音 = 这首歌();

        var 零 = PitchRangeReadout.Measure(音, 0);
        var 升 = PitchRangeReadout.Measure(音, +1);
        var 降 = PitchRangeReadout.Measure(音, -1);

        Assert.Multiple(() =>
        {
            Assert.That(零.OutsideNotes, Is.EqualTo(0), "微调 0：不该有音掉出去");
            Assert.That(升.OutsideNotes, Is.EqualTo(0), "微调 +1：最高的 A#5 升到 B5，还在条子里");
            Assert.That(降.OutsideNotes, Is.EqualTo(2), "微调 −1：恰好 2 个音符掉出去（基线）");

            Assert.That(降.Outside.Select(Music.NoteName), Is.EqualTo(new[] { "B2" }),
                "掉出去的是 C3 降半音那个音（两个音符，同一个音高，去重后只列一个）");
            Assert.That(降.OutsidePercent, Is.EqualTo("0.4"), "占比是 2 / 454");
            Assert.That(降.TotalNotes, Is.EqualTo(454), "分母是整曲的音符数");

            Assert.That(降.OutOfRange, Is.True, "有音掉出去 = 要变红换成红字那一组");
            Assert.That(零.OutOfRange, Is.False);
            Assert.That(升.OutOfRange, Is.False);

            Assert.That(new[] { 零.BaseOctave, 升.BaseOctave, 降.BaseOctave },
                Is.All.EqualTo(4), "三种微调下自动基准八度都是 C4");
            Assert.That(降.WindowLow, Is.EqualTo(48), "窗口还是 C3 起（动的是歌，不是窗口）");
        });
    }

    /// <summary>
    /// 亮的根数 = 这首歌用到的**不同**音高数（20），**不是音符数（454）**。
    /// 两者差 20 多倍，写错了条子会整片糊成一条没有信息的红线。
    /// </summary>
    [Test]
    public void 亮的根数是用到的不同音高数不是音符数()
    {
        var 音 = 这首歌();
        var 零 = PitchRangeReadout.Measure(音, 0);
        var 条 = PitchRangeReadout.BarsOf(零, null);

        Assert.Multiple(() =>
        {
            Assert.That(音, Has.Count.EqualTo(454), "这首歌一共 454 个音符");
            Assert.That(零.Used.Count, Is.EqualTo(20), "用到的不同音高是 20 个");
            Assert.That(零.Used, Is.EqualTo(音.Distinct().OrderBy(p => p)),
                "用到的音高（去重升序）—— 读数行那行字的两端就取自它");

            Assert.That(条.Count(b => b.Lit), Is.EqualTo(20), "亮着的根数 = 不同的音高数");
            Assert.That(条.Count(b => !b.Lit), Is.EqualTo(18), "剩下 18 根是暗的");
            Assert.That(条.Select(b => b.Pitch), Is.EqualTo(PitchRangeReadout.Window(4)));
        });
    }

    // ==================== 正在响的：恰好一根，而且必须亮着 ====================

    /// <summary>
    /// 给一个音高，**恰好一根**套细边，**而且那根必须是亮着的**。
    ///
    /// 逐个音钉的：这首歌 454 个音，三种微调各走一遍 —— 每个音符发出来的那一刻，
    /// 条子上有且只有一根套着边，就是它自己那一根，而且那根是亮的（它当然在这首歌用到的音里）。
    /// 掉到窗口外的音（微调 −1 时的 B2）一根都不套：那根条子根本不在条子上。
    /// </summary>
    [Test]
    public void 正在响的恰好一根而且那根一定亮着()
    {
        var 音 = 这首歌();

        foreach (int 微调 in new[] { 0, +1, -1 })
        {
            var 状态 = PitchRangeReadout.Measure(音, 微调);

            foreach (var 原谱 in 音)
            {
                int 响的 = 原谱 + 微调;
                var 套边的 = PitchRangeReadout.BarsOf(状态, 响的).Where(b => b.Now).ToList();
                int 期望 = PitchRangeReadout.InWindow(响的, 状态.BaseOctave) ? 1 : 0;

                Assert.That(套边的, Has.Count.EqualTo(期望),
                    $"微调 {微调}：音高 {Music.NoteName(响的)} 套的细边根数不对");
                if (期望 == 1)
                {
                    Assert.That(套边的[0].Pitch, Is.EqualTo(响的), "细边套到别的音上了");
                    Assert.That(套边的[0].Lit, Is.True,
                        $"微调 {微调}：{Music.NoteName(响的)} 套了细边却没亮");
                }
            }

            // 窗口里那 38 个音逐个给一遍：每个音只套一根，而且套在它自己那一格上
            for (int i = 0; i < PitchRangeReadout.Bars; i++)
            {
                var 条 = PitchRangeReadout.BarsOf(状态, PitchRangeReadout.WindowLow(状态.BaseOctave) + i);
                var 套边的 = 条.Where(b => b.Now).ToList();
                Assert.That(套边的, Has.Count.EqualTo(1), $"第 {i + 1} 根没套上");
                Assert.That(套边的[0], Is.EqualTo(条[i]), $"第 {i + 1} 根套到了别处");
            }

            // 没在响的时候一根都不套（就绪态那一条）
            Assert.That(PitchRangeReadout.BarsOf(状态, null).Count(b => b.Now), Is.EqualTo(0), "没有正在响的音时不该有细边");
        }
    }

    // ==================== 升号 ====================

    /// <summary>升号音（pc ∈ {1,3,6,8,10}）就是那几根 —— 48 起逐个比一遍，一个不错位。</summary>
    [Test]
    public void 升号音判定是那五个半音()
    {
        var 状态 = PitchRangeReadout.Measure(这首歌(), 0);

        foreach (var 条 in PitchRangeReadout.BarsOf(状态, null))
        {
            int pc = Music.Mod(条.Pitch, 12);
            bool 期望 = pc is 1 or 3 or 6 or 8 or 10;
            Assert.That(条.Sharp, Is.EqualTo(期望), $"{Music.NoteName(条.Pitch)}（pc={pc}）");
            Assert.That(PitchRangeReadout.IsSharp(条.Pitch), Is.EqualTo(期望));
        }
    }

    // ==================== 读数行 ====================

    /// <summary>
    /// 常态读数行的十个槽拼出来就是那一句话（含「整首已平移 +N 个半音」与
    /// 「套着细边的那根是正在响的 F4」两种条件句），越界时换成红字那一组。
    /// </summary>
    [Test]
    public void 读数行说的是这一首歌()
    {
        var 音 = 这首歌();
        var 零 = PitchRangeReadout.Measure(音, 0);

        Assert.That(PitchRangeReadout.Measure(Array.Empty<int>(), 0).BaseOctave, Is.EqualTo(4),
            "还没曲子时基准八度也按 AutoBaseOctave 的空表规矩给 4（段头才写得出 C3/C4/C5/C6）");

        var 裸 = PitchRangeReadout.SummarySlots(零, null);
        Assert.That(拼(裸), Is.EqualTo("亮着的是这首歌用到的 20 个音（C3 – A#5），全在条子里面。"));
        Assert.That(拼(new[] { 裸[3], 裸[4], 裸[5] }), Is.EqualTo(""), "没平移就不出现「整首已平移」那句");
        Assert.That(拼(new[] { 裸[7], 裸[8], 裸[9] }), Is.EqualTo(""), "没在响就不出现「套着细边的那根」那句");

        var 响 = PitchRangeReadout.SummarySlots(零, 65);
        Assert.That(拼(响), Is.EqualTo("亮着的是这首歌用到的 20 个音（C3 – A#5），全在条子里面。 套着细边的那根是正在响的 F4。"));

        var 平移 = PitchRangeReadout.Measure(音, +1);
        Assert.That(拼(PitchRangeReadout.SummarySlots(平移, null)),
            Is.EqualTo("亮着的是这首歌用到的 20 个音（C#3 – B5），整首已平移 +1 个半音，全在条子里面。"));

        var 降 = PitchRangeReadout.Measure(音, -1);
        Assert.That(PitchRangeReadout.AlertTitle, Is.EqualTo("当前已超出可演奏音域"));
        Assert.That(PitchRangeReadout.AlertSub(降),
            Is.EqualTo("B2 落到这 38 个音外面了 —— 共 2 个音符，占全曲 0.4%"));
    }

    /// <summary>
    /// 加粗的是哪几个槽（1 / 4 / 8）：窗口里那几个 <c>Run</c> 的强调必须和这儿对得上 ——
    /// 对不上不是报错，只是强调跑到别的字上去了（「亮着的是这首歌用到的<b>20</b>个音」变成
    /// 「亮着的是这首歌用到的 20 个音」那种读起来别扭的样子）。
    /// </summary>
    [Test]
    public void 读数行里被强调的槽是一四八()
    {
        var 音 = 这首歌();
        var 槽 = PitchRangeReadout.SummarySlots(PitchRangeReadout.Measure(音, +1), 65);

        Assert.That(槽, Has.Length.EqualTo(PitchRangeReadout.SummarySlotCount));
        Assert.That(槽[1], Is.EqualTo("20"), "强调槽 1 = 用到的音数");
        Assert.That(槽[4], Is.EqualTo("+1"), "强调槽 4 = 平移了几个半音");
        Assert.That(槽[8], Is.EqualTo("正在响的 F4"), "强调槽 8 = 正在响的那个音");
        Assert.That(槽.All(s => s.Length > 0), Is.True,
            "这一帧三种条件句都在，十个槽都该有内容（空串 = 那一段不出现）");
    }

    // ==================== 音名换音高（事件表那串字） ====================

    /// <summary>
    /// 事件表上递到界面的那串字（<c>NoteMapper.Describe</c> 产的）能换回音高。
    /// 这首歌用到的 20 个音逐个来回换一遍 —— 换错了就是「细边套在另一个音上」，
    /// 而这个读数的全部意义就是「现在响的是哪一格」。
    ///
    /// 换不回来的（空串 / 不是音名的串）必须给 null：**不许猜一个音**。
    /// </summary>
    [Test]
    public void 事件表上那串字能换回音高()
    {
        foreach (var 音高 in 这首歌().Distinct().OrderBy(p => p))
        {
            var 映射 = NoteMapper.Map(
                new[] { new RawNote { Pitch = 音高, Start = 0, End = 1 } },
                transpose: 0, manualBaseOctave: 4);
            var 音符 = 映射.Notes.Single();

            Assert.That(音符.InRange, Is.True, $"{Music.NoteName(音高)} 该是可演奏的");

            var 字 = NoteMapper.Describe(音符, withTime: false);
            Assert.That(字, Does.StartWith(Music.NoteName(音高)));
            Assert.That(PitchRangeReadout.PitchOfLabel(字), Is.EqualTo(音高), 字);
        }

        Assert.Multiple(() =>
        {
            Assert.That(PitchRangeReadout.PitchOfLabel(null), Is.Null);
            Assert.That(PitchRangeReadout.PitchOfLabel(""), Is.Null);
            Assert.That(PitchRangeReadout.PitchOfLabel("就绪 · 先打开一首 MIDI"), Is.Null,
                "不是音名的串不许猜出一个音来");
            Assert.That(PitchRangeReadout.PitchOfLabel("H4(4) → 按[v] 基准八度"), Is.Null, "H 不是音名");
        });
    }

    // ==================== XAML：窗口里摆的就是这 38 根 ====================

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string AppDir => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", "..", "MidiPerformer.App"));

    private static XElement 窗() => XDocument.Load(Path.Combine(AppDir, "Views", "PerformerWindow.axaml")).Root!;

    private static string? 属性(XElement e, string 名) => e.Attribute(名)?.Value;

    private static string? 名字(XElement e) => e.Attribute(Xaml + "Name")?.Value;

    private static XElement 控件(XElement 根, string 名) =>
        根.Descendants().FirstOrDefault(e => 名字(e) == 名)
        ?? throw new AssertionException($"演奏器窗口里找不到 x:Name=\"{名}\" 的控件");

    /// <summary>细条那一行（<c>RangeBar</c>）里那 38 根，按先后。</summary>
    private static List<XElement> 细条(XElement 根) =>
        控件(根, "RangeBar").Descendants()
            .Where(e => e.Name.LocalName == "Border" && (属性(e, "Classes") ?? "").Contains("s38"))
            .ToList();

    /// <summary>窗口的样式表里那一条规则（选择器 + 它的 setter）。</summary>
    private static Dictionary<string, string> 样式(XElement 根, string 选择器)
    {
        var 规则 = 根.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Style" && 属性(e, "Selector") == 选择器)
            ?? throw new AssertionException($"样式表里没有 \"{选择器}\" 这条规则");

        return 规则.Elements()
            .Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(e => 属性(e, "Property")!, e => 属性(e, "Value") ?? "");
    }

    /// <summary>样式表里所有规则的先后（后来者赢，所以次序本身是判据）。</summary>
    private static List<string> 规则次序(XElement 根) =>
        根.Descendants().Where(e => e.Name.LocalName == "Style")
            .Select(e => 属性(e, "Selector") ?? "").ToList();

    /// <summary>
    /// 文件正文，**去掉 XML 注释**。注释里提一句「BoxShadow 接不了令牌」不算用了它 ——
    /// 判据要盯的是标记，不是解释标记的那句话。
    /// </summary>
    private static string 全文(string 名) =>
        Regex.Replace(
            File.ReadAllText(Path.Combine(AppDir, "Views", 名)),
            "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>
    /// 窗口里摆的是**这 38 根**：四段 12/12/12/2，一根不多一根不少，
    /// 而且细条自己不写高度（高度只有样式那一条 24px 给 —— 见下一条）。
    /// </summary>
    [Test]
    public void 窗口里摆的就是这三十八根()
    {
        var 根 = 窗();
        var 段 = 控件(根, "RangeBar").Elements().Where(e => e.Name.LocalName is "Grid" or "Panel").ToList();

        Assert.That(段, Has.Count.EqualTo(4), "细条那一行该切成四段");
        Assert.That(段.Select(s => s.Descendants().Count(e => e.Name.LocalName == "Border")),
            Is.EqualTo(new[] { 12, 12, 12, 2 }), "四段各几根");
        Assert.That(细条(根), Has.Count.EqualTo(PitchRangeReadout.Bars), "一共该有 38 根细条");

        foreach (var 条 in 细条(根))
            Assert.That(属性(条, "Height"), Is.Null,
                "细条自己写死高度 = 以后谁都能给某一根换个高度，那一段就会被撑高");
    }

    /// <summary>
    /// 四段的宽度比是**算法**给的，不是眼睛配的：<c>12*,8,12*,8,12*,8,2*</c>。
    /// 中间那三个 8px 是独立空列（等于 CSS 里那三个 <c>.sep</c>）；
    /// 段容器上**不许加 Spacing** —— 那等于 CSS 的 gap，12 根的那段缝多，每根会偏窄一点点。
    /// </summary>
    [Test]
    public void 四段的宽度比是格子权重给的()
    {
        var 根 = 窗();

        const string 格子 = "12*,8,12*,8,12*,8,2*";
        Assert.That(属性(控件(根, "RangeBar"), "ColumnDefinitions"), Is.EqualTo(格子));
        Assert.That(属性(控件(根, "RangeKeys"), "ColumnDefinitions"), Is.EqualTo(格子),
            "段头那行和条子那行必须用同一套格子，音名才落在各段左端");

        foreach (var 段 in 控件(根, "RangeBar").Elements())
        {
            var 星 = 属性(段, "ColumnDefinitions")!.Split(',');
            Assert.That(星.All(s => s == "*"), Is.True, "段里那几根是等权重的（flex:1 1 0）");
            Assert.That(星.Length, Is.EqualTo(12).Or.EqualTo(2));
        }

        // 「不许 gap」：条子那两行里任何一个控件都不许写 Spacing / ColumnSpacing / RowSpacing
        // （越界那一块的 StackPanel 上那个 Spacing=3 是两行字之间的行距，不是条子上的缝，不在这一条里）
        foreach (string 行 in new[] { "RangeBar", "RangeKeys" })
        {
            foreach (var e in 控件(根, 行).DescendantsAndSelf())
            {
                Assert.That(属性(e, "Spacing"), Is.Null, $"{行} 的 {e.Name.LocalName} 上写了 Spacing（等于 CSS 的 gap）");
                Assert.That(属性(e, "ColumnSpacing"), Is.Null, $"{行} 的 {e.Name.LocalName} 上写了 ColumnSpacing");
                Assert.That(属性(e, "RowSpacing"), Is.Null, $"{行} 的 {e.Name.LocalName} 上写了 RowSpacing");
            }
        }
    }

    /// <summary>
    /// **全部同高，谁都不许被撑高**：段容器显式 <c>Height=24</c> + <c>VerticalAlignment=Bottom</c>
    /// （等于 CSS 的 <c>align-items:flex-end</c>），细条的高度只有样式那一条给、状态里没有高度这一档。
    ///
    /// 病根是嵌套布局默认的 stretch：里面只要有一根比别人高，它所在的**那一段整体**会被撑起来。
    /// 屏幕上是不是真的一样高归上机（这一条只证明「结构上不可能被撑高」）。
    /// </summary>
    [Test]
    public void 四段和细条都不许被撑高()
    {
        var 根 = 窗();

        foreach (var 段 in 控件(根, "RangeBar").Elements())
        {
            Assert.That(属性(段, "Height"), Is.EqualTo("24"), "段容器要显式写高");
            Assert.That(属性(段, "VerticalAlignment"), Is.EqualTo("Bottom"),
                "段容器要显式贴底（等于 CSS 的 align-items:flex-end）");
        }

        var 条 = 样式(根, "Border.s38");
        Assert.That(条["Height"], Is.EqualTo("24"), "细条的高度只有这一条给");
        Assert.That(条["VerticalAlignment"], Is.EqualTo("Bottom"));
        Assert.That(条["Background"], Is.EqualTo("{DynamicResource TokenSurface3}"), "没用到的那几根是暗底");

        // 状态那几条规则里**一个高度都不能有** —— 状态不靠高度分档
        foreach (var 选择器 in new[] { "Border.s38.sharp", "Border.s38.lit", "Border.s38.now" })
            Assert.That(样式(根, 选择器).Keys, Has.None.EqualTo("Height"), $"{选择器} 里写了高度");
    }

    /// <summary>
    /// 升号那几根**只降不透明度**：那一条规则里除了 Opacity 什么都不许有
    /// （一根细条只剩 8px 的肉，换底色或描边就糊成一团）。
    /// 而且次序是「暗 → 升号 → 亮 → 越界」，后来者赢 —— 升号又亮着的时候，亮的那一档说了算。
    /// </summary>
    [Test]
    public void 升号只降不透明度不换底色不描边()
    {
        var 根 = 窗();

        var 升号 = 样式(根, "Border.s38.sharp");
        Assert.That(升号, Has.Count.EqualTo(1), "升号这条规则只该有 Opacity 一个 setter");
        Assert.That(升号["Opacity"], Is.EqualTo("0.55"));

        var 亮 = 样式(根, "Border.s38.lit");
        Assert.That(亮["Background"], Is.EqualTo("{DynamicResource TokenAccent}"));
        Assert.That(亮["Opacity"], Is.EqualTo("1"), "亮的那一档要把升号的 0.55 顶回去");

        var 越界 = 样式(根, "Grid.bad Border.s38.lit");
        Assert.That(越界["Background"], Is.EqualTo("{DynamicResource TokenStop}"),
            "越界时亮的**一律**换红（灰的那些保持灰）");

        var 次序 = 规则次序(根);
        Assert.That(次序.IndexOf("Border.s38"), Is.LessThan(次序.IndexOf("Border.s38.sharp")));
        Assert.That(次序.IndexOf("Border.s38.sharp"), Is.LessThan(次序.IndexOf("Border.s38.lit")));
        Assert.That(次序.IndexOf("Border.s38.lit"), Is.LessThan(次序.IndexOf("Grid.bad Border.s38.lit")),
            "越界那条排在亮的后面才盖得住它");

        // 细边：内描边画的同一圈 1.5px，不带高度、不用阴影
        var 细边 = 样式(根, "Border.s38.now");
        Assert.That(细边["BorderThickness"], Is.EqualTo("1.5"));
        Assert.That(细边["BorderBrush"], Is.EqualTo("{DynamicResource TokenInk}"));
        Assert.That(细边.Keys, Has.None.EqualTo("Height"));
        Assert.That(细边.Keys, Has.None.EqualTo("BoxShadow"));

        Assert.That(全文("PerformerWindow.axaml"), Does.Not.Contain("BoxShadow"),
            "BoxShadow 只吃字面色、接不了令牌；这一圈细边是用内描边画的");
    }

    /// <summary>
    /// 读数行的十个槽：窗口里正好十个 <c>Run</c>，**挨着写、中间不留空白**
    /// （多一个只有空格的 Run，槽就对不上，写出来的字会多几个缝），
    /// 强调（显式写 Foreground）只在第 2 / 5 / 9 个上 —— 和 <c>SummarySlots</c> 里那三处一一对应。
    /// 段头默认写的音名也钉一下（C3 / C4 / C5 / C6，最后一段的字往右靠）。
    /// </summary>
    [Test]
    public void 读数行的十个槽和段头的音名()
    {
        var 根 = 窗();

        var 读数行 = 控件(根, "RangeSummary");
        var 槽 = 读数行.Elements().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(槽, Has.Count.EqualTo(PitchRangeReadout.SummarySlotCount), "槽的个数");
            Assert.That(槽.All(e => e.Name.LocalName == "Run"), Is.True, "槽必须是 Run（界面按顺序往里写字）");
            Assert.That(槽.All(e => 属性(e, "Text") == ""), Is.True, "一开始十个槽都是空的");
            Assert.That(读数行.Nodes().OfType<XText>().Any(t => t.Value.Length > 0), Is.False,
                "Run 之间留了空白 —— 那会多出几个只有空格的槽");
            Assert.That(读数行.DescendantsAndSelf().Count(), Is.EqualTo(PitchRangeReadout.SummarySlotCount + 1),
                "读数行里除了那十个槽不许有别的东西");
            Assert.That(属性(读数行, "IsVisible"), Is.EqualTo("False"), "还没曲子时读数行不出现");

            var 强调 = 槽.Select((e, i) => (e, i))
                         .Where(t => 属性(t.e, "Foreground") is not null)
                         .Select(t => t.i).ToList();
            Assert.That(强调, Is.EqualTo(new[] { 1, 4, 8 }),
                "强调的槽和 PitchRangeReadout.SummarySlots 里那三个值对不上");

            Assert.That(new[] { "RangeKey0", "RangeKey1", "RangeKey2", "RangeKey3" }
                    .Select(n => 属性(控件(根, n), "Text")),
                Is.EqualTo(new[] { "C3", "C4", "C5", "C6" }), "段头那四个音名");
            Assert.That(属性(控件(根, "RangeKey3"), "HorizontalAlignment"), Is.EqualTo("Right"),
                "尾巴那段只有两根细条宽，字要从右端往左长");
        });
    }

    /// <summary>
    /// 块的位置：在「演奏轨」下面、「时序」上面。上面压着「按键速度」，下面紧挨着「微调」——
    /// 微调那一行落地之后（74 号票），**微调那一行紧挨着它**（条子直接贴在微调下面，中间不插任何东西）。
    /// </summary>
    [Test]
    public void 音域块的位置在时序上面()
    {
        var 顺序 = 窗().Descendants().ToList();
        int 位(string 名) => 顺序.FindIndex(e => 名字(e) == 名);

        Assert.Multiple(() =>
        {
            Assert.That(位("PitchRange"), Is.GreaterThan(位("TrackCombo")), "音域块该在演奏轨下面");
            Assert.That(位("PitchRange"), Is.GreaterThan(位("FinePlus")), "微调那一行在它上面");
            Assert.That(位("PitchRange"), Is.LessThan(位("TimingCombo")), "音域块该压在时序上面");
            Assert.That(位("RangeBar"), Is.LessThan(位("RangeKeys")), "段头在条子下面");
            Assert.That(位("RangeKeys"), Is.LessThan(位("RangeSummary")), "读数行在段头下面");
            Assert.That(位("RangeSummary"), Is.LessThan(位("RangeAlert")),
                "越界那一声和读数行是互斥的两块，排在它后面");
        });
    }

    // ==================== 74：微调那一行（±1 半音） ====================

    /// <summary>仓库根（<c>MidiPerformer.App</c> 的上一层）。</summary>
    private static string 仓库根 => Path.GetFullPath(Path.Combine(AppDir, ".."));

    /// <summary>按相对路径读一个文件（判据要跨 Core / App 两边看）。</summary>
    private static string 读(params string[] 段) => File.ReadAllText(Path.Combine(段.Prepend(仓库根).ToArray()));

    /// <summary>演奏器窗口的代码（接线那一半）。</summary>
    private static string 演奏器代码 => 读("MidiPerformer.App", "Views", "PerformerWindow.axaml.cs");

    /// <summary>取两处标记之间的原文（两头的标记都得在，第二个在第一个之后）。</summary>
    private static string 一段(string source, string 起, string 止)
    {
        int a = source.IndexOf(起, StringComparison.Ordinal);
        Assert.That(a, Is.GreaterThanOrEqualTo(0), $"源码里找不到「{起}」");

        int b = source.IndexOf(止, a + 起.Length, StringComparison.Ordinal);
        Assert.That(b, Is.GreaterThan(a), $"「{起}」之后找不到「{止}」");

        return source[a..b];
    }

    /// <summary>数一段代码里某串字出现了几次。</summary>
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

    /// <summary>亮着的那几根的下标（0 起，从窗口最低那个音数）。</summary>
    private static List<int> 亮格(PitchRangeState 状态) =>
        PitchRangeReadout.BarsOf(状态, null)
            .Select((b, i) => (b, i))
            .Where(t => t.b.Lit)
            .Select(t => t.i)
            .ToList();

    /// <summary>
    /// 位置是**钉死的**：在「按键速度」下面、38 根细条正上面，**中间不插任何东西**。
    ///
    /// 这一条盯的是「谁挨着谁」：微调那一行在窗口最外层那排孩子里，**下一个**就得是音域块
    /// （原型 W1 ③：条子直接贴在微调下面，中间不插任何东西 —— 这两块是一个东西的两半，
    /// 隔开一行的话，微调一动，眼睛还得往下跳一段才看得见图变了）。
    /// </summary>
    [Test]
    public void 微调那一行夹在按键速度与三十八根细条中间()
    {
        var 根 = 窗();
        var 顺序 = 根.Descendants().ToList();
        int 位(string 名) => 顺序.FindIndex(e => 名字(e) == 名);

        // 微调那一行 = 三颗按钮的爷爷：Button → StackPanel → Border.stepper → Grid
        var 行 = 控件(根, "FineZero").Parent!.Parent!.Parent!;

        Assert.Multiple(() =>
        {
            Assert.That(行.Name.LocalName, Is.EqualTo("Grid"), "三颗按钮该装在一个 Grid 里（别的行同一套格子）");
            Assert.That(属性(行, "ColumnDefinitions"), Is.EqualTo("74,*"), "和别的行同一套格子");
            Assert.That(行.Elements().First().Attribute("Text")?.Value, Is.EqualTo("微调"), "标签写「微调」");

            Assert.That(位("FineMinus"), Is.GreaterThan(位("KeyRate")), "微调该在按键速度下面");
            Assert.That(位("FinePlus"), Is.LessThan(位("RangeBar")), "微调该在 38 根细条上面");
            Assert.That(位("FineZero"), Is.LessThan(位("TimingCombo")), "整行都在时序上面");

            // 「中间不插任何东西」：最外层那排孩子里，微调那一行的下一个就是音域块
            var 孩子 = 行.Parent!.Elements().ToList();
            int 我 = 孩子.IndexOf(行);
            Assert.That(我, Is.GreaterThanOrEqualTo(0), "微调那一行该是窗口最外层那排孩子之一");
            Assert.That(我 + 1, Is.LessThan(孩子.Count), "微调那一行后面还有个音域块");
            Assert.That(孩子[我 + 1].DescendantsAndSelf().Any(e => 名字(e) == "RangeBar"), Is.True,
                "微调下面紧挨着的就得是那 38 根细条 —— 中间不插任何东西");
        });
    }

    /// <summary>
    /// 三颗按钮 = 三个档位（−1 / 0 / +1），形状照抄轨编辑器那个步进器：
    /// 外框 <c>Border.stepper</c>、三颗都是 <c>Button.step</c>、中间那颗多一个 <c>mid</c>（值格的样子）。
    ///
    /// **默认选谁不在 XAML 里写**：三颗都不带 <c>.on</c>，选中态由代码按 <c>_fineTune</c> 刷
    /// （XAML 里写死一颗 = 和 _fineTune 两个真相源）。
    /// </summary>
    [Test]
    public void 微调是三颗按钮中间那颗是值格的样子()
    {
        var 根 = 窗();
        var 三颗 = new[] { "FineMinus", "FineZero", "FinePlus" }.Select(n => 控件(根, n)).ToList();
        var 框 = 控件(根, "FineZero").Parent!.Parent!;

        Assert.Multiple(() =>
        {
            Assert.That(三颗.Select(e => e.Name.LocalName), Is.All.EqualTo("Button"),
                "三个档位都是按钮，不是只读的值格（「0」也是一个要能选回来的档位）");
            Assert.That(三颗.Select(e => 属性(e, "Content")), Is.EqualTo(new[] { "−1", "0", "+1" }),
                "按钮上写的是排版用的减号（U+2212）");
            Assert.That(三颗.Select(e => 属性(e, "Tag")), Is.EqualTo(new[] { "-1", "0", "1" }),
                "Tag 里是 ASCII 的减号（要被 int.Parse 读）—— 和编辑器那几颗一样，两个字符不一样是有意的");
            Assert.That(三颗.Select(e => 属性(e, "Click")), Is.All.EqualTo("OnFineTune"));

            Assert.That(框.Name.LocalName, Is.EqualTo("Border"));
            Assert.That(属性(框, "Classes"), Is.EqualTo("stepper"), "外框就是编辑器的那个圆角框");
            Assert.That(属性(框, "HorizontalAlignment"), Is.EqualTo("Left"), "整框靠左，不铺满那一栏");

            Assert.That(属性(控件(根, "FineMinus"), "Classes"), Is.EqualTo("step"));
            Assert.That(属性(控件(根, "FineZero"), "Classes"), Is.EqualTo("step mid"));
            Assert.That(属性(控件(根, "FinePlus"), "Classes"), Is.EqualTo("step"));

            Assert.That(三颗.SelectMany(e => (属性(e, "Classes") ?? "").Split(' ')).Contains("on"), Is.False,
                "XAML 里写死选中态 = 和 _fineTune 两个真相源");

            // 样式：照抄编辑器那一套，选中那一档取别的控件「选中」用的那套（accent-soft 底 + 重一档字重）
            Assert.That(样式(根, "Border.stepper")["Background"], Is.EqualTo("{DynamicResource TokenSurface}"));
            Assert.That(样式(根, "Border.stepper")["CornerRadius"],
                Is.EqualTo("{DynamicResource TokenRadiusControl}"));
            Assert.That(样式(根, "Button.step")["Background"], Is.EqualTo("Transparent"),
                "步进键贴着外框，自己不要底色");
            Assert.That(样式(根, "Button.step.mid")["MinWidth"], Is.EqualTo("58"), "中间那格宽一档（照抄值格）");

            // ⚠️ 这条守卫是**实机量出来的**：74 第一次上机时中间那颗加了 TokenFontMono，
            //    等宽字的行高和默认字体不一样 ⇒ 它 36 px 高、顶边比另两颗低 2 px（40 / 36 / 40），
            //    「三颗一样高、顶边一格不差」当场不过。所以除了宽度和那两条竖线，什么都不许往这格上加。
            var 中间 = 样式(根, "Button.step.mid");
            Assert.That(中间.Keys, Has.None.EqualTo("FontFamily"),
                "中间那格不许换字体 —— 行高一变，三颗的盒子就不一样高（实机量到过 40/36/40 px）");
            Assert.That(中间.Keys, Has.None.EqualTo("Height"), "高度不许单独设：一设就和另两颗不一样");
            Assert.That(中间.Keys, Has.None.EqualTo("MinHeight"), "最小高度不许单独设：同上");
            Assert.That(中间.Keys, Has.None.EqualTo("Padding"), "内外边距不许单独设：盒子尺寸会跟着变");

            var 选中 = 样式(根, "Button.step.on");
            Assert.That(选中["Background"], Is.EqualTo("{DynamicResource TokenAccentSoft}"));
            Assert.That(选中["FontWeight"], Is.EqualTo("SemiBold"));
            Assert.That(选中.Keys, Has.None.EqualTo("BorderThickness"),
                "选中态不许改边框粗细 —— 三颗的盒子尺寸会跟着变");

            var 次序 = 规则次序(根);
            Assert.That(次序.IndexOf("Button.step"), Is.LessThan(次序.IndexOf("Button.step.on")),
                "选中那条排在步进键那条后面才盖得住它");
        });
    }

    /// <summary>
    /// **这一票的验收**：微调一挪，亮的那一片**整片平移一根**，而窗口一动不动。
    ///
    /// 拿真数据测（曲库里那首基线）：0 → 20 根；+1 → 还是 20 根，每根的下标各 +1；
    /// −1 → 19 根，每根各 −1，最左那根（C3）掉到窗外去了（2 个音符）。
    /// 窗口在三种状态下都是 48 起（C3）—— 挪的是整首歌，不是窗口。
    /// </summary>
    [Test]
    public void 微调一挪亮的那片整片平移一根()
    {
        var 音 = 这首歌();

        // 窗口 = 载入这首歌时算定的那一个（它不含微调，见下一条）
        int 窗口 = PitchRangeReadout.Measure(音, 0).BaseOctave;

        var 零 = PitchRangeReadout.Measure(音, 0, 窗口);
        var 升 = PitchRangeReadout.Measure(音, +1, 窗口);
        var 降 = PitchRangeReadout.Measure(音, -1, 窗口);

        var 零格 = 亮格(零);
        var 升格 = 亮格(升);
        var 降格 = 亮格(降);

        Assert.Multiple(() =>
        {
            Assert.That(窗口, Is.EqualTo(4), "这首歌的窗口就是 C4 那个（曲库里的基线，自动算出来也是它）");
            Assert.That(new[] { 零.WindowLow, 升.WindowLow, 降.WindowLow }, Is.All.EqualTo(48),
                "微调一动都不动窗口（挪的是整首歌）");
            Assert.That(new[] { 零.BaseOctave, 升.BaseOctave, 降.BaseOctave }, Is.All.EqualTo(4));

            Assert.That(零格, Has.Count.EqualTo(20), "微调 0：亮着的是那 20 个音");
            Assert.That(升格, Is.EqualTo(零格.Select(i => i + 1).ToList()),
                "微调 +1：亮的 20 根整片往右挪一格");
            Assert.That(降格, Is.EqualTo(零格.Select(i => i - 1).Where(i => i >= 0).ToList()),
                "微调 −1：整片往左挪一格，最左那根掉了出去");

            Assert.That(升格, Has.Count.EqualTo(20), "微调 +1 一根都没掉出去（A#5 升到 B5 还在条子里）");
            Assert.That(降格, Has.Count.EqualTo(19), "微调 −1：19 根还亮着");
            Assert.That(降.OutsideNotes, Is.EqualTo(2), "掉出去 2 个音符（C3 → B2，同一个音高出现两次）");
            Assert.That(降.Outside.Select(Music.NoteName), Is.EqualTo(new[] { "B2" }));

            Assert.That(new[] { 零.OutOfRange, 升.OutOfRange }, Is.All.False, "微调 0 / +1 都不越界");
            Assert.That(降.OutOfRange, Is.True, "越界了就该整片换红 + 底下换成红字那一组");
        });
    }

    /// <summary>
    /// **窗口为什么必须钉住**：不钉的话，微调一挪，自动八度会跟着重挑 —— 撞上边界时整片不是平移一格，
    /// 而是**跳到另一个八度去**（用户看到的就不是「整片平移一根」了）。
    ///
    /// 这条用一对人造音高把那个跳变逼出来（真数据那首歌三种状态下自动八度恰好都是 4，看不出这个差别）：
    /// <c>{B2, C3}</c> 的自动八度在 2 / 3 上是平手（都容得下 2 个音），平手取平均八度最近的 → 2；
    /// 整首 +1 之后 <c>{C3, C#3}</c> 只落在 3 那一档 → 自动重挑会跳到 3（窗口整整挪一个八度）。
    /// 钉住（载入时那个 2）之后，才是「两根条子各往右挪一格」。
    /// </summary>
    [Test]
    public void 窗口是载入时钉住的不跟着微调重挑()
    {
        var 音 = new[] { 47, 48 };

        Assert.That(NoteMapper.AutoBaseOctave(音), Is.EqualTo(2), "这条基线的自动八度是 2（窗口 24 起）");
        Assert.That(PitchRangeReadout.Measure(音, +1).BaseOctave, Is.EqualTo(3),
            "不钉住的话，+1 之后自动八度会跳到 3 —— 窗口整整挪一个八度");

        var 零 = PitchRangeReadout.Measure(音, 0, 2);
        var 升 = PitchRangeReadout.Measure(音, +1, 2);

        Assert.Multiple(() =>
        {
            Assert.That(升.BaseOctave, Is.EqualTo(2), "微调不许把窗口也挪了");
            Assert.That(new[] { 零.WindowLow, 升.WindowLow }, Is.All.EqualTo(24), "窗口一直是 B1 那个");
            Assert.That(亮格(零), Is.EqualTo(new[] { 23, 24 }), "B2 和 C3 落在第 24 / 25 根上");
            Assert.That(亮格(升), Is.EqualTo(new[] { 24, 25 }), "钉住之后才是整片平移一根");
            Assert.That(升.OutsideNotes, Is.EqualTo(0), "钉住之后不会有音掉出去");
        });
    }

    /// <summary>
    /// **微调只跟着这一次走，不写回曲子**（票面上那处岔路的判定）：演奏器是「放」的，不是「改」的，
    /// 所以它不写回 <c>track.Transpose</c>，而是化作 <c>StartPerformanceRequest.TransposeOffset</c>。
    ///
    /// 它必须一路走到**事件表**里（<c>EventTable.Build</c>）：条子上亮的那几格和真按下去的那几格
    /// 是同一件事的两半 —— 只挪读数不挪表的话，微调 −1 时那 2 个越界音符照样会被发出去。
    /// 按键速度读数走的是同一张表，所以它也得带上这个偏移。
    /// </summary>
    [Test]
    public void 微调进的是这一次演奏的事件表不写回曲子()
    {
        string 代码 = 演奏器代码;
        string 链 = 读("MidiPerformer.Core", "UseCases", "Perform", "EventTable.cs");
        string 用例 = 读("MidiPerformer.Core", "UseCases", "Perform", "StartPerformance.cs");

        Assert.Multiple(() =>
        {
            Assert.That(代码, Does.Contain("TransposeOffset: _fineTune"), "微调要跟着这一次请求走");
            Assert.That(一段(代码, "BaseOctave: WindowBaseOctave(", "InputTiming.FromIndex("),
                Does.Not.Contain("_fineTune"),
                "基准八度**不含**微调 —— 窗口不动，挪的是整首歌");

            Assert.That(链, Does.Contain("track.Transpose + transposeOffset"),
                "微调叠在曲子自己的移调上面（两者都是「整首歌挪几格」）");
            Assert.That(用例, Does.Contain("request.TransposeOffset"), "演奏那一路要把微调递进链子");

            Assert.That(一段(代码, "EventTable.Build(", "return KeyRateReadout.Measure("),
                Does.Contain("request.TransposeOffset"),
                "按键速度读数和演奏共用一张表：这张表也得带上微调");

            Assert.That(代码, Does.Not.Contain(".Transpose ="), "不许把微调写回 track.Transpose");
            Assert.That(链, Does.Not.Contain(".Transpose ="), "链子那边也不许写回");

            // 读数那一行：字全由 PitchRangeReadout 写，窗口这层不另算一遍
            Assert.That(一段(代码, "private void OnFineTune(", "private void MarkFineTune()"),
                Does.Not.Contain("NoteName").And.Not.Contain("SummarySlots").And.Not.Contain("AlertSub"),
                "读数行的字归 PitchRangeReadout，界面这一层只递参数");
            Assert.That(一段(代码, "_range.Show(", ");"), Does.Contain("transpose + _fineTune"),
                "条子收到的移调就是「曲子自己的移调 + 微调」—— 和事件表加的是同一个数");
        });
    }

    /// <summary>
    /// 窗口不跟着微调走，靠的是 <c>PitchRangeReadout.Measure</c> 那个手给基准八度的那一支
    /// （形状与 <c>NoteMapper.Map</c> 的 <c>manualBaseOctave</c> 一模一样）：不给就还是自动那条老路。
    /// 窗口那一层要把钉住的那个八度一路递到 <c>Measure</c>，不能半路丢了又去自动挑一个。
    /// </summary>
    [Test]
    public void 窗口不跟着微调走是拿手给的基准八度做的()
    {
        string 读数 = 读("MidiPerformer.App", "Views", "PitchRangeReadout.cs");

        Assert.Multiple(() =>
        {
            Assert.That(读数, Does.Contain("int? manualBaseOctave = null"),
                "手给八度那一支要有个默认值 —— 老调用方（49 的那些测试）一个字都不用改");
            Assert.That(读数, Does.Contain("manualBaseOctave ?? NoteMapper.AutoBaseOctave(valid)"),
                "不给的时候还是自动那一条老路");
            Assert.That(读数,
                Does.Contain("PitchRangeReadout.Measure(pitches ?? Array.Empty<int>(), transpose, baseOctave)"),
                "窗口那一层要把钉住的八度递下去，不能半路丢了");
        });
    }

    /// <summary>
    /// 默认选 0，而且「选中的是哪一档」**只有一处**说了算：三颗按钮的 <c>.on</c> 全在
    /// <c>MarkFineTune</c> 里按 <c>_fineTune</c> 刷（XAML 里一颗都不带），构造期先刷一遍。
    /// </summary>
    [Test]
    public void 选中的那一档由代码刷默认是零()
    {
        string 代码 = 演奏器代码;

        Assert.Multiple(() =>
        {
            Assert.That(代码, Does.Contain("private int _fineTune;"), "默认 0（不写初值就是 0）");
            Assert.That(一段(代码, "private void MarkFineTune()", "private PlayableTrack? CurrentTrack()"),
                Does.Contain("FineZero.Classes.Set(\"on\", _fineTune == 0)"), "0 那一档");
            Assert.That(数一数(代码, "Classes.Set(\"on\""), Is.EqualTo(3),
                "选中态只该在 MarkFineTune 里刷那三行");
            Assert.That(一段(代码, "public PerformerWindow(IClock clock, InputSender sender)",
                    "protected override void OnOpened"),
                Does.Contain("MarkFineTune();"), "构造期就得把默认那一档刷上");
        });
    }

    /// <summary>
    /// 演奏期间三颗微调都锁死：这一场的事件表在按下开始那一刻就建好了，中途再挪半音只挪得动读数 ——
    /// 屏幕上说挪了、耳朵里那张旧表照发，正是要防的那种「对不上」。
    /// 判据仍然只有 <c>SetRunning</c> 一处（73 号票）。
    /// </summary>
    [Test]
    public void 微调那三颗在演奏期间锁死()
    {
        string 一处 = 一段(演奏器代码, "private void SetRunning(bool running)", "private Status _status");

        Assert.Multiple(() =>
        {
            Assert.That(一处, Does.Contain("FineMinus.IsEnabled = !running"));
            Assert.That(一处, Does.Contain("FineZero.IsEnabled = !running"));
            Assert.That(一处, Does.Contain("FinePlus.IsEnabled = !running"));

            // 三颗按钮的「能不能按」不许在别处再写一份
            Assert.That(数一数(演奏器代码, "FineMinus.IsEnabled"), Is.EqualTo(1));
            Assert.That(数一数(演奏器代码, "FineZero.IsEnabled"), Is.EqualTo(1));
            Assert.That(数一数(演奏器代码, "FinePlus.IsEnabled"), Is.EqualTo(1));
        });
    }
}
