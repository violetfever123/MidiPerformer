using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MidiPerformer.Tests.Tools;

/// <summary>
/// 变异范围的守卫：**范围一放大，报告就被淹，然后没人看。**
///
/// 这一条盯的是 <c>stryker-config.json</c>，而且只看它 —— <c>dotnet stryker</c> 本身是第三方工具，
/// 不测（spec-测试工具链「明确不测的」）。测的是那两条**纸面上的范围**：
///
///   ① **只有 <c>MidiPerformer.Core</c>。** 去变异 <c>App</c> / <c>Adapters</c> 会被自己的规格
///      打回来（那两层「明确不测」），生出来的 mutant 绝大多数杀不掉也不该杀。
///   ② **<c>Repertoire\</c> 下那 6 个逐字移植的文件排除在外。** 它们的正确性由「对拍」证明，
///      在「跟原版一字不差」这个语境里，一个「等价但写法不同」的 mutant 是好事，不是漏网。
///
/// <b>判据一律是可机械确定的</b>：范围取自配置里的 <c>project</c>；那 6 个文件取自
/// <c>Core</c> 目录下真实的 <c>.cs</c> 清单，<b>不靠人抄一份名单</b> —— 抄的名单会过期，
/// 而过期的名单只会让这条守卫变成一条恒绿的假门。
///
/// 还有一条**对照**，专门防这条测试自己变成假绿：把 <c>mutate</c> 里的排除去掉再算一遍，
/// 结果必须正好等于「Core 下全部的 <c>.cs</c>」。glob 匹配器要是坏了（比如恒不匹配），
/// 这一条当场就红 —— 而不是让「变异范围正好是那 29 个」这种断言在空集上悄悄通过。
/// </summary>
[TestFixture]
public class StrykerScopeTests
{
    private const string 配置文件名 = "stryker-config.json";
    private const string 工具清单相对路径 = ".config/dotnet-tools.json";
    private const string 被测工程目录 = "MidiPerformer.Core";
    private const string 对拍目录相对 = "UseCases/Perform/Repertoire";

    /// <summary>票面 B 组点名的那 6 个。用来钉住「排除的那个目录里到底是哪几个」。</summary>
    private static readonly string[] 对拍的六个 =
    [
        "EventBuilder.cs", "InputTiming.cs", "Music.cs",
        "NoteMapper.cs", "PlayKeys.cs", "RepertoireToSeconds.cs",
    ];

    // ==================== ① 范围只有 Core ====================

    [Test]
    public void 范围只有Core_没有App没有Adapters也没有solution把它撑开()
    {
        var (配置根, 配置, _) = 读配置();
        var 根 = 仓库根();

        Assert.Multiple(() =>
        {
            Assert.That(
                配置根.EnumerateObject().Select(p => p.Name),
                Is.EqualTo(new[] { "stryker-config" }),
                "**根上**只能有 stryker-config 这一个对象 —— 多一个键 Stryker 会拒收");

            Assert.That(
                配置.TryGetProperty("solution", out _),
                Is.False,
                "不设 solution：一设，范围就从「一个工程」变成「整个解决方案」，"
                + "App 与 Adapters 立刻跟着进来 —— 这正是要防的那次「顺手放大」");

            Assert.That(
                配置.GetProperty("project").GetString(),
                Is.EqualTo("MidiPerformer.Core.csproj"),
                "被测工程只能是纯层 Core");

            var 工程名单 = new[] { "MidiPerformer.App.csproj", "MidiPerformer.Adapters.csproj", "MidiPerformer.Tests.csproj" };
            foreach (var 别人 in 工程名单)
            {
                Assert.That(
                    配置.GetProperty("project").GetString(),
                    Is.Not.EqualTo(别人),
                    $"{别人} 不在变异范围里 —— 那两层「明确不测」，变异它们只会把报告淹掉");
            }

            // 「名字对」不等于「指对了」：那个工程得真的躺在 Core 目录下。
            Assert.That(
                File.Exists(Path.Combine(根, 被测工程目录, "MidiPerformer.Core.csproj")),
                Is.True,
                $"project 指的是 {被测工程目录}\\MidiPerformer.Core.csproj，它得真的在");

            var 测试工程 = 配置.GetProperty("test-projects").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(
                测试工程,
                Is.EqualTo(new[] { "MidiPerformer.Tests/MidiPerformer.Tests.csproj" }),
                "测试工程点成一个 —— Tests 引用了 App / Adapters / Core / 对拍原版四个工程，"
                + "不点名 Stryker 会因为「引用了不止一个工程」而拒绝开工");

            // 模式不许爬出被测工程目录。project 把范围钉在 Core 上，而一个带 `..` 的
            // 模式会把这个钉住的范围捅穿 —— 两条都守住，范围才真的只有一个工程。
            var (include, exclude) = 读mutate的include与exclude(配置);
            foreach (var 模式 in include.Concat(exclude))
            {
                Assert.That(
                    模式,
                    Does.Not.Contain(".."),
                    $"mutate 里的模式不许带 `..`（{模式}）—— 那是往被测工程之外爬");
                Assert.That(
                    Path.IsPathRooted(模式) || 模式.StartsWith('/'),
                    Is.False,
                    $"mutate 里的模式必须是相对的（{模式}）");
            }
        });
    }

    // ==================== ② 对拍那 6 个被排除，其余一个都不少 ====================

    [Test]
    public void 对拍那六个排除在变异范围外_而Core里其余的都在里面()
    {
        var (_, 配置, _) = 读配置();
        var 根 = 仓库根();
        var 核心目录 = Path.Combine(根, 被测工程目录);
        Assert.That(Directory.Exists(核心目录), Is.True, $"找不到被测工程目录：{核心目录}");

        var (include, exclude) = 读mutate的include与exclude(配置);

        // 真实清单从盘上取：Core 下所有 .cs，去掉 bin/obj（构建产物，不是源文件），
        // 再去掉 .xaml.cs —— Stryker 自己也跳过它。
        var 全部 = Directory
            .EnumerateFiles(核心目录, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(核心目录, f).Replace('\\', '/'))
            .Where(p => !p.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var 对拍 = 全部.Where(p => p.StartsWith(对拍目录相对 + "/", StringComparison.Ordinal)).ToList();

        Assert.That(全部, Is.Not.Empty, "Core 下一个 .cs 都没找到 —— 被测工程目录找错了");
        Assert.That(
            对拍.Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal),
            Is.EqualTo(对拍的六个.OrderBy(n => n, StringComparer.Ordinal)),
            $"对拍目录（{对拍目录相对}）里应当正好是票面点名的那 6 个 —— "
            + "对不上说明文件改名/挪窝了，配置里那条排除得跟着改");

        // 对照：**只留 include**。结果必须正好是「Core 下的全部」。
        // 这一条是给下面那条断言的体检 —— 匹配器恒不匹配的话，这里就红了，
        // 而不是让「变异范围正好是全部减 6」在空集上悄悄通过（那是标准的假绿）。
        var 只include = 全部.Where(p => include.Any(m => 匹配(m, p))).ToList();
        Assert.That(
            只include,
            Is.EqualTo(全部),
            "mutate 的 include 一个都不该漏 —— 漏了说明范围和纸面上写的不一样");

        var 变异 = 只include.Where(p => !exclude.Any(m => 匹配(m, p))).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                变异,
                Is.EqualTo(全部.Except(对拍).ToList()),
                "变异范围必须正好是「Core 下全部 .cs 减去对拍那 6 个」——"
                + "多一个就是范围放大了，少一个就是顺手把别人也排除了");

            Assert.That(
                变异.Intersect(对拍),
                Is.Empty,
                "对拍那 6 个一个都不许留在变异范围里");

            Assert.That(变异, Is.Not.Empty, "一个都不变异的话这个工具就没意义了");
        });
    }

    // ==================== ③ 豁免必须留字 ====================

    [Test]
    public void 豁免白名单每一条前面都得有一行注释_而且这个位置本身要在()
    {
        var (_, 配置, 行) = 读配置();

        Assert.Multiple(() =>
        {
            foreach (var 键 in new[] { "ignore-mutations", "ignore-methods" })
            {
                Assert.That(
                    配置.TryGetProperty(键, out var 值),
                    Is.True,
                    $"配置里得有 {键} 这一项 —— 豁免的口子要在，哪怕现在一条都不用");
                Assert.That(值.ValueKind, Is.EqualTo(JsonValueKind.Array), $"{键} 得是个数组");

                var 键行号 = Array.FindIndex(行, l => l.Contains($"\"{键}\"", StringComparison.Ordinal));
                Assert.That(键行号, Is.GreaterThanOrEqualTo(0), $"原文里找得到 {键} 那一行");
                Assert.That(
                    上一行非空行(行, 键行号),
                    Does.StartWith("//"),
                    $"{键} 正上方得是一行注释：**这就是「有注释这个位置」** ——"
                    + "规矩得写在口子旁边，不然下一个用这个口子的人不会知道要留字",
                    string.Join(" | ", 行.Skip(Math.Max(0, 键行号 - 3)).Take(4)));
            }

            // 每一条豁免都要有自己那行注释。现在数组是空的，所以这一条是空转的 ——
            // 但它跟着上面的结构一起长出来，等第一条豁免写进来它就开始干活。
            foreach (var 键 in new[] { "ignore-mutations", "ignore-methods" })
            {
                if (!配置.TryGetProperty(键, out var 列表) || 列表.ValueKind != JsonValueKind.Array) { continue; }
                var 键行号 = Array.FindIndex(行, l => l.Contains($"\"{键}\"", StringComparison.Ordinal));
                foreach (var 条目 in 列表.EnumerateArray().Select(e => e.GetString()!))
                {
                    var 条目行号 = Array.FindIndex(行, 键行号, l => l.Contains($"\"{条目}\"", StringComparison.Ordinal));
                    Assert.That(条目行号, Is.GreaterThanOrEqualTo(0), $"{键} 里的 {条目} 在原文里找得到");
                    Assert.That(
                        上一行非空行(行, 条目行号),
                        Does.StartWith("//"),
                        $"{键} 里的 {条目} 上面得有一行注释说明它为什么杀不掉也不该杀 ——"
                        + "**禁止的是沉默的漏网，不是豁免本身**");
                }
            }
        });
    }

    // ==================== ④ 工具版本钉死 ====================

    [Test]
    public void 工具清单把Stryker钉在一个具体版本上_不是范围也不是latest()
    {
        var 路径 = Path.Combine(仓库根(), 工具清单相对路径.Replace('/', Path.DirectorySeparatorChar));

        Assert.That(File.Exists(路径), Is.True, $"找不到 {工具清单相对路径}：{路径}");

        using var 文档 = JsonDocument.Parse(File.ReadAllText(路径, Encoding.UTF8));
        var 根 = 文档.RootElement;

        Assert.That(根.GetProperty("isRoot").GetBoolean(), Is.True, "根清单：在任何子目录里 dotnet tool restore 都找得到它");

        var 工具 = 根.GetProperty("tools");
        Assert.That(工具.TryGetProperty("dotnet-stryker", out var stryker), Is.True, "清单里得有 dotnet-stryker");

        var 版本 = stryker.GetProperty("version").GetString()!;
        Assert.That(
            版本,
            Does.Match(@"^\d+\.\d+\.\d+$"),
            $"版本要钉到具体版本号，不许是范围、不许是 latest（现在写的是 {版本}）——"
            + "仓库里没有 nuget.config、没有 global.json，这份清单就是唯一的工具版本真相");
    }

    // ==================== 读配置 ====================

    /// <summary>
    /// 按 <b>Stryker 自己的口径</b>读配置：<c>System.Text.Json</c> 带
    /// <c>ReadCommentHandling.Skip</c>（<c>FileConfigReader.DeserializeJson</c> 就是这么读的）。
    ///
    /// 这么做是有意的：注释在这个文件里是**载重**的（豁免得靠它留字），所以
    /// 「这份配置连同它的注释一起能被解析」这件事本身就该被验到。手工按行剥注释
    /// 会把这条验不到 —— 而那正好是注释写坏时唯一的症状。
    /// </summary>
    private static (JsonElement 配置根, JsonElement 配置, string[] 行) 读配置()
    {
        var 路径 = Path.Combine(仓库根(), 配置文件名);
        Assert.That(File.Exists(路径), Is.True, $"找不到 {配置文件名}：{路径} —— 没有配置就是没有范围");

        var 行 = File.ReadAllLines(路径, Encoding.UTF8);
        var 原文 = string.Join(Environment.NewLine, 行);
        using var 文档 = JsonDocument.Parse(原文, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        // 根这一层也要交出来：Stryker 要求「根上只有一个 stryker-config 对象」，
        // 多一个键它会连「允许的键」清单一起报出来拒收。断在里层对象上就验不到这条。
        var 配置根 = 文档.RootElement.Clone();
        Assert.That(配置根.TryGetProperty("stryker-config", out var 配置), Is.True, "根对象叫 stryker-config");
        return (配置根, 配置, 行);
    }

    private static (List<string> include, List<string> exclude) 读mutate的include与exclude(JsonElement 配置)
    {
        Assert.That(配置.TryGetProperty("mutate", out var mutate), Is.True, "配置里得有 mutate —— 范围就写在它上面");
        Assert.That(mutate.ValueKind, Is.EqualTo(JsonValueKind.Array), "mutate 得是个数组");

        var 全部 = mutate.EnumerateArray().Select(e => e.GetString()!).ToList();
        var include = 全部.Where(m => !m.StartsWith('!')).ToList();
        var exclude = 全部.Where(m => m.StartsWith('!')).Select(m => m[1..]).ToList();

        Assert.That(include, Is.Not.Empty, "mutate 里至少要有一条 include");
        return (include, exclude);
    }

    /// <summary>从 <paramref name="行号"/> 往上找第一行非空的。</summary>
    private static string 上一行非空行(string[] 行, int 行号)
    {
        for (var i = 行号 - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(行[i])) { return 行[i].Trim(); }
        }
        return string.Empty;
    }

    // ==================== mutate 的 glob 语义 ====================

    /// <summary>
    /// Stryker 的 mutate 通配语义：<c>**</c> 跨目录段，<c>*</c> 只在当前段内。
    ///
    /// 一个够用的实现就够 —— 它只活在这条测试里，而用到它的那两处断言**互为对照**：
    /// 只留 include 时必须匹配到「Core 下的全部」，加上 exclude 时必须正好少掉对拍那 6 个。
    /// 匹配器要是坏了（比如恒不匹配），第一条就红，不会让第二条在空集上悄悄通过。
    /// </summary>
    private static bool 匹配(string 模式, string 相对路径)
    {
        var 模式段 = 模式.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var 路径段 = 相对路径.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return 匹配段(模式段, 0, 路径段, 0);
    }

    private static bool 匹配段(string[] 模式, int i, string[] 路径, int j)
    {
        for (; i < 模式.Length; i++)
        {
            if (模式[i] == "**")
            {
                // `**` 吃掉 0 段到任意多段。
                for (var k = j; k <= 路径.Length; k++)
                {
                    if (匹配段(模式, i + 1, 路径, k)) { return true; }
                }
                return false;
            }

            if (j >= 路径.Length) { return false; }
            if (!段匹配(模式[i], 路径[j])) { return false; }
            j++;
        }
        return j == 路径.Length;
    }

    private static bool 段匹配(string 模式段, string 路径段)
        => Regex.IsMatch(
            路径段,
            "^" + Regex.Escape(模式段).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$",
            RegexOptions.CultureInvariant);

    // ==================== 仓库根 ====================

    /// <summary>
    /// 跟 <see cref="RunAllTests"/> 同一套：从 <see cref="AppContext.BaseDirectory"/> 往上
    /// 找第一个放 <c>MidiPerformer.slnx</c> 的目录。找不到就是**没跑**（<c>Assert.Ignore</c>），
    /// 不是通过 —— 这条测试的全部价值都押在「真读到了仓库里那份配置」上。
    /// </summary>
    private static string 仓库根()
    {
        for (var 目录 = new DirectoryInfo(AppContext.BaseDirectory); 目录 is not null; 目录 = 目录.Parent)
        {
            if (File.Exists(Path.Combine(目录.FullName, "MidiPerformer.slnx"))) { return 目录.FullName; }
        }
        Assert.Ignore($"没跑（不是通过）：从 {AppContext.BaseDirectory} 往上找不到 MidiPerformer.slnx，不知道仓库在哪。");
        throw new InvalidOperationException("unreachable");
    }
}
