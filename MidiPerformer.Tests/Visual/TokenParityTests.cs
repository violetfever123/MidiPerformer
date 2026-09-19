using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MidiPerformer.App.Theme;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 视觉令牌层的守卫。这一层没有行为可测，但有**四条会悄悄坏掉的约束**，
/// 全靠这里强制 —— 它们坏了都不会报错，只会让界面某处悄悄变成另一个颜色：
///
/// 1. 令牌和 <c>docs/wireframe.html</c> 的 <c>:root</c> 逐条对应（改了 wireframe 没改令牌）
/// 2. 取色桥按属性名取到**对的那个**令牌（<c>Resolve</c> 的参数写串了）
/// 3. <c>{DynamicResource}</c> 引用的键真的存在（键名打错 —— 这条最阴，运行时静默失效）
/// 4. 除 <c>Tokens.axaml</c> 外没有字面颜色值（令牌是唯一来源）
///
/// 判据全是**文件文本**，不需要起 Avalonia —— 跑得跟在 NUnit 里读两个文件一样快。
/// </summary>
public class TokenParityTests
{
    /// <summary>wireframe 里属于线框说明层、不进软件的三个令牌。</summary>
    private static readonly string[] WireframeOnly = { "--annot", "--annot-soft", "--annot-line" };

    /// <summary>wireframe 的 :root 一共 29 条，减掉上面三个，进软件的是 26 条。</summary>
    private const int ExpectedTokenCount = 26;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    /// <summary>MidiPerformer 仓库根。</summary>
    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string AppDir => Path.Combine(RepoRoot, "MidiPerformer.App");

    private static string TokensAxaml => Path.Combine(AppDir, "Styles", "Tokens.axaml");

    private static string ControlsAxaml => Path.Combine(AppDir, "Styles", "Controls.axaml");

    private static string Wireframe => Path.Combine(RepoRoot, "docs", "wireframe.html");

    // ==================== 1. 和 wireframe 对账 ====================

    [Test]
    public void 令牌数正好是26条()
    {
        Assert.That(LightTokens().Count - WireframeOnly.Length, Is.EqualTo(ExpectedTokenCount),
            "wireframe 的 :root 减掉标注专用的三条，剩下的就是进软件的令牌数");
        Assert.That(DeclaredTokens("Light").Count, Is.EqualTo(ExpectedTokenCount),
            "Tokens.axaml 的 ThemeDictionaries 里也只该有这么多 —— 多一条少一条都要先改这里");
    }

    [Test]
    public void 明暗两套的键名完全一致()
    {
        var light = DeclaredTokens("Light");
        var dark = DeclaredTokens("Dark");

        Assert.That(dark.Keys, Is.EquivalentTo(light.Keys),
            "明暗两套的键名必须一模一样，差一个就是某个主题下少一块颜色");
    }

    [Test]
    public void 每个令牌的明暗两套值都和wireframe一致()
    {
        var light = LightTokens();
        var dark = DarkTokens();
        var declaredLight = DeclaredTokens("Light");
        var declaredDark = DeclaredTokens("Dark");

        foreach (var (cssVar, _) in light)
        {
            if (WireframeOnly.Contains(cssVar)) continue;

            var token = TokenName(cssVar);
            Assert.That(declaredLight, Contains.Key(token), $"wireframe 有 {cssVar}，Tokens.axaml 里没有 {token}");
            Assert.That(declaredDark, Contains.Key(token), $"{token} 缺暗色那套");

            AssertValue(token, light[cssVar], declaredLight[token].Text, "明亮");
            AssertValue(token, dark[cssVar], declaredDark[token].Text, "暗色");
        }
    }

    [Test]
    public void 标注专用的三个令牌没有混进软件()
    {
        foreach (var cssVar in WireframeOnly)
        {
            Assert.That(DeclaredTokens("Light"), Does.Not.Contain(TokenName(cssVar)),
                $"{cssVar} 是线框标注点专用的，不该进软件");
        }
    }

    // ==================== 2. 取色桥 ====================

    [Test]
    public void 取色桥的属性名和令牌键一一对应()
    {
        var properties = typeof(TokenPalette).GetProperties().Select(p => TokenPalette.KeyPrefix + p.Name);

        Assert.That(properties, Is.EquivalentTo(DeclaredTokens("Light").Keys),
            "TokenPalette 的属性就是令牌键（属性名 + Token 前缀），加令牌忘改这边会在这里红");
    }

    [Test]
    public void 取色桥按属性名取到对的那个令牌()
    {
        // 每条令牌的值都按自己的键去查，所以「取串了」在这里会被抓住 —— 光比名字是抓不住的
        AssertPaletteMatches(TokenPalette.Resolve(LookupFor("Light"), ThemeVariant.Light), "Light");
        AssertPaletteMatches(TokenPalette.Resolve(LookupFor("Dark"), ThemeVariant.Dark), "Dark");
    }

    [Test]
    public void 令牌缺失时取色桥直接炸而不是给个默认值()
    {
        Assert.That(
            () => TokenPalette.Resolve((_, _) => null, ThemeVariant.Light),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("TokenGround"));
    }

    // ==================== 3. 引用完整性 ====================

    [Test]
    public void axaml里引用的每个键都有定义()
    {
        var defined = AllDeclaredKeys();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(".axaml"))
        {
            // 注释要抹掉再找：说明里提一句「Fluent 那边写的是 {DynamicResource SliderThumbBackground}」
            // 是解释，不是引用，不该被当成打错的键名
            var text = BlankComments(File.ReadAllText(file));
            foreach (Match m in ResourceReference.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!defined.Contains(key))
                    offenders.Add($"{Relative(file)}: {{DynamicResource}} 引用了没定义的 {key}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "键名打错不会报错，只会在运行时静默失效 —— 所以在这里拦：\n" + string.Join("\n", offenders));
    }

    // ==================== 4. 没有字面颜色 ====================

    [Test]
    public void 除Tokens_axaml外没有字面颜色值()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles(".axaml").Concat(SourceFiles(".cs")))
        {
            if (Path.GetFileName(file) == "Tokens.axaml") continue;

            // 注释要抹掉再找 —— 注释里写「这里为什么不用 #fff」是说明，不是写死颜色。
            // 抹成等长的空格而不是删掉，行号才不会跑偏。
            var lines = BlankComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (LiteralColor.IsMatch(lines[i]))
                    offenders.Add($"{Relative(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.That(offenders, Is.Empty,
            "令牌是唯一来源，这些地方写死了颜色：\n" + string.Join("\n", offenders));
    }

    // ==================== 5. 覆盖 Fluent 的资源键 ====================

    /// <summary>
    /// Fluent 声明成 <c>Color</c>、而只能用画刷覆盖的键 —— 每条都得登记，并写明消费处。
    ///
    /// 为什么不能照着 Fluent 声明成 <c>Color</c>：令牌驱动的 Color 资源在 Avalonia 里写不出来。
    /// <c>&lt;Color x:Key="x"&gt;{DynamicResource TokenY}&lt;/Color&gt;</c> 直接报
    /// AVLN2005「Unable to parse ... as a color」—— 它把花括号那截当颜色字面值解析，
    /// 不会走标记扩展。唯一能表达的形态是 SolidColorBrush。
    ///
    /// 而画刷塞进 <c>Color=</c> 属性会崩（这个形状本轮真崩过一次），
    /// 所以每一条都必须确认过消费方吃的是画刷。登记表就是那条确认记录。
    /// </summary>
    private static readonly Dictionary<string, string> BrushOverridesColorKey = new()
    {
        ["ScrollBarThumbBackgroundColor"] = "ScrollBar.xaml:288 拿它当 Background，吃画刷",
    };

    [Test]
    public void 覆盖的Fluent键都真的存在()
    {
        var theme = new FluentTheme();
        var offenders = OverriddenKeys()
            .Where(pair => !theme.TryGetResource(pair.Key, ThemeVariant.Light, out _))
            .Select(pair => $"{pair.Key}: Fluent 里没有这个键，覆盖是死的（写多少遍都不生效）")
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Controls.axaml 的 Styles.Resources 是用来覆盖 Fluent 的，键名不存在就白写：\n"
            + string.Join("\n", offenders));
    }

    [Test]
    public void 覆盖Fluent的Color键必须登记过消费处()
    {
        var theme = new FluentTheme();
        var colorTyped = OverriddenKeys()
            .Where(pair => theme.TryGetResource(pair.Key, ThemeVariant.Light, out var value) && value is Color)
            .Select(pair => pair.Key)
            .ToList();

        // 解析器要是没匹配上，下面那条相等断言会退化成「空表等于空表」，白测
        Assert.That(colorTyped, Is.Not.Empty, "一个 Color 键都没解析出来，先看看是不是解析没匹配上");

        // 相等而不是包含：登记的键哪天不再是 Color 了，也要回来把这一条删掉，别留烂账
        Assert.That(BrushOverridesColorKey.Keys, Is.EquivalentTo(colorTyped),
            "这些键 Fluent 声明的是 Color，我们只能用画刷覆盖（Color 拼不出令牌），"
            + "但画刷被喂给 Color= 属性会崩。新增的先去 Fluent 源码确认消费方吃画刷再登记；"
            + "名单里多出来的说明已经不用登记了，删掉。");
    }

    /// <summary>Controls.axaml 的 <c>&lt;Styles.Resources&gt;</c> 里覆盖了哪些 Fluent 键。</summary>
    private static List<(string Key, string Element)> OverriddenKeys()
        => XDocument.Load(ControlsAxaml)
            .Descendants()
            .Single(e => e.Name.LocalName == "Styles.Resources")
            .Elements()
            .Select(e => ((string)e.Attribute(Xaml + "Key")!, e.Name.LocalName))
            .ToList();

    // ==================== 读文件 ====================

    /// <summary>wireframe 的 :root，明亮那套。</summary>
    private static Dictionary<string, string> LightTokens() => CssBlock(File.ReadAllText(Wireframe), ":root{");

    /// <summary>wireframe 的 :root[data-theme="dark"]，暗色那套。</summary>
    private static Dictionary<string, string> DarkTokens()
        => CssBlock(File.ReadAllText(Wireframe), ":root[data-theme=\"dark\"]{");

    /// <summary>Tokens.axaml 里某一套主题声明的令牌：键 → (元素名, 文本)。</summary>
    private static Dictionary<string, (string Element, string Text)> DeclaredTokens(string theme)
    {
        // ThemeDictionaries 是属性元素，元素名是 ResourceDictionary.ThemeDictionaries；
        // 按 LocalName 找，省得去纠结它落在哪个命名空间
        var dictionary = XDocument.Load(TokensAxaml)
            .Descendants()
            .Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements()
            .Single(e => (string?)e.Attribute(Xaml + "Key") == theme);

        return dictionary.Elements().ToDictionary(
            e => (string)e.Attribute(Xaml + "Key")!,
            e => (e.Name.LocalName, e.Value.Trim()));
    }

    /// <summary>Tokens.axaml 和 Controls.axaml 里定义过的全部键（不只是那 26 个）。</summary>
    private static HashSet<string> AllDeclaredKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(".axaml"))
            // 注释要抹掉：把一条定义 <!-- --> 掉，键就**不存在**了。
            // 不抹的话注释里的 x:Key 照样算「已定义」，引用它的地方看着通过、运行时静默失效 ——
            // 正是这条测试要拦的那件事，反被它自己放过去。
            foreach (Match m in XKey.Matches(BlankComments(File.ReadAllText(file))))
                keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>
    /// <c>--ink-muted</c> → <c>TokenInkMuted</c>。<c>--surface-2</c> → <c>TokenSurface2</c>。
    /// 机械映射，没有第二张对照表 —— 有第二张表就迟早对不上。
    /// </summary>
    private static string TokenName(string cssVar)
    {
        var parts = cssVar.TrimStart('-').Split('-');
        return TokenPalette.KeyPrefix + string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static Dictionary<string, string> CssBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"wireframe 里找不到 {selector}");

        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        var body = css[(open + 1)..close];

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0) continue;
            result[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
        }

        return result;
    }

    /// <summary>App 底下的源码文件，跳过 bin/obj。</summary>
    private static IEnumerable<string> SourceFiles(string extension)
        => Directory.EnumerateFiles(AppDir, "*" + extension, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"));

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot, file);

    /// <summary>
    /// XML 注释、C# 块注释、C# 行注释统统抹成空格（换行留着，行号不变）。
    /// 只会漏报不会误报：注释里的颜色本来就不算数。
    /// </summary>
    private static string BlankComments(string text)
        => Comment.Replace(text, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

    // ==================== 比 ====================

    /// <summary>颜色比字面值；阴影比**整段** —— 偏移、模糊、spread、颜色，一样不对就算对不上。</summary>
    private static void AssertValue(string token, string wireframeValue, string declared, string theme)
    {
        if (wireframeValue.StartsWith('#'))
        {
            Assert.That(declared, Is.EqualTo(wireframeValue).IgnoreCase,
                $"{token} 的{theme}值和 wireframe 对不上");
            return;
        }

        Assert.That(CssShadow(wireframeValue), Is.EqualTo(DeclaredShadow(declared)),
            $"{token} 的{theme}阴影和 wireframe 对不上");
    }

    /// <summary>一段阴影：偏移、模糊半径、spread、颜色。</summary>
    private static List<(double X, double Y, double Blur, double Spread, Color Color)> CssShadow(string css)
    {
        // 不按逗号切 —— rgba 里面就有逗号，切了会把颜色拆成好几段。
        // 改成分头捞出「三个挨着的数」和「一个 rgb()/rgba()」，再按出现顺序配对：
        // 每段阴影正好一个颜色，数量对不上就说明这段 CSS 不是这里假设的形状。
        var triples = ShadowTriple.Matches(css)
            .Select(m => (
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
            .ToList();

        var colors = CssColor.Matches(css).Select(m => ParseCssColor(m.Value)).ToList();

        Assert.That(colors, Has.Count.EqualTo(triples.Count),
            $"{css} 里阴影段数和颜色数对不上，解析假设不成立了");

        // CSS 的 box-shadow 没有 spread 这个概念，一律 0
        return triples.Zip(colors, (t, c) => (t.Item1, t.Item2, t.Item3, 0d, c)).ToList();
    }

    private static List<(double X, double Y, double Blur, double Spread, Color Color)> DeclaredShadow(string declared)
    {
        var shadows = BoxShadows.Parse(declared);
        return Enumerable.Range(0, shadows.Count)
            .Select(i => (
                (double)shadows[i].OffsetX, (double)shadows[i].OffsetY,
                (double)shadows[i].Blur, (double)shadows[i].Spread, shadows[i].Color))
            .ToList();
    }

    /// <summary>
    /// <c>rgba(23,28,35,.06)</c> → Color。
    /// alpha 用 <see cref="MidpointRounding.AwayFromZero"/>：<c>.3*255=76.5</c>，
    /// 默认的银行家舍入会把它变成 76（偶数），而 wireframe 那边写的是 <c>#4D</c>=77。
    /// </summary>
    private static Color ParseCssColor(string css)
    {
        var parts = css[(css.IndexOf('(') + 1)..css.IndexOf(')')].Split(',');

        byte Channel(int i) => byte.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        var alpha = parts.Length > 3
            ? (byte)Math.Round(double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) * 255,
                MidpointRounding.AwayFromZero)
            : (byte)255;

        return Color.FromArgb(alpha, Channel(0), Channel(1), Channel(2));
    }

    private static void AssertPaletteMatches(TokenPalette palette, string theme)
    {
        var declared = DeclaredTokens(theme);

        foreach (var property in typeof(TokenPalette).GetProperties())
        {
            var key = TokenPalette.KeyPrefix + property.Name;
            var (element, text) = declared[key];
            var actual = property.GetValue(palette);

            if (element == "BoxShadows")
            {
                Assert.That(DeclaredShadow(text), Is.EqualTo(DeclaredShadow(((BoxShadows)actual!).ToString())),
                    $"{key} 取错了");
            }
            else
            {
                Assert.That(((Color)actual!).ToString(), Is.EqualTo(Color.Parse(text).ToString()),
                    $"{key} 取错了 —— 多半是 Resolve 里的参数顺序写串了");
            }
        }
    }

    /// <summary>
    /// 拿 Tokens.axaml 当资源表，按里面声明的元素类型把文本变成真的 Avalonia 对象。
    /// 于是取色桥是在**真令牌表**上测的，不是在一份手写的替身上。
    /// </summary>
    private static Func<string, ThemeVariant, object?> LookupFor(string theme)
    {
        var values = DeclaredTokens(theme).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Element == "BoxShadows"
                ? (object)BoxShadows.Parse(pair.Value.Text)
                : new SolidColorBrush(Color.Parse(pair.Value.Text)));

        return (key, _) => values.GetValueOrDefault(key);
    }

    /// <summary><c>{DynamicResource TokenXxx}</c> / <c>{StaticResource TokenXxx}</c> 里的键名。</summary>
    private static readonly Regex ResourceReference =
        new(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);

    /// <summary><c>x:Key="TokenXxx"</c>。</summary>
    private static readonly Regex XKey = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 一段阴影里的三个数：偏移 x、偏移 y、模糊半径（单位 px 可省）。
    /// 要求三个数**挨着**，所以 <c>rgba(23,28,35,.06)</c> 那截匹配不上 —— 它中间是逗号不是空白。
    /// </summary>
    private static readonly Regex ShadowTriple =
        new(@"(-?[\d.]+)(?:px)?\s+(-?[\d.]+)(?:px)?\s+(-?[\d.]+)(?:px)?", RegexOptions.Compiled);

    /// <summary><c>rgb(...)</c> / <c>rgba(...)</c> 整段。</summary>
    private static readonly Regex CssColor = new(@"rgba?\([^)]*\)", RegexOptions.Compiled);

    /// <summary>
    /// XML 注释 / C# 块注释 / C# 行注释。
    /// 行注释要求 <c>//</c> 前面不是冒号 —— App.axaml 里的 <c>avares://…</c> 也是两个斜杠，
    /// 照着注释抹掉的话，那一行后面的颜色就再也查不出来了（静默漏报）。
    /// 已知边界：字符串里真的写了 <c>//</c> 且后面还跟颜色，仍然会漏 —— 这条是减少漏报，不是杜绝。
    /// </summary>
    private static readonly Regex Comment =
        new(@"<!--.*?-->|/\*.*?\*/|(?<!:)//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 字面颜色值。只看十六进制和 rgb()/rgba() 两种 —— 具名颜色（Transparent 之类）
    /// 有正当用途，混进来当颜色用的可能性小，不值得为它把误报拉高。
    /// 已知边界：<c>Colors.White</c> 这种也拦得住，但 <c>Brushes.White</c> 拦不住。
    /// </summary>
    private static readonly Regex LiteralColor =
        new(@"#[0-9a-fA-F]{3,8}\b|\brgba?\s*\(|\bColors\.[A-Z]", RegexOptions.Compiled);
}
