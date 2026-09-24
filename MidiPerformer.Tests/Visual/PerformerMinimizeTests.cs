using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 51 号票：按下「开始演奏」**那一刻**窗口自己最小化，把桌面让给游戏；**三种收场
/// （正常结束 / 急停 / 出错）一视同仁，都不碰窗口状态** —— 留在最小化，用户自己切回来。
///
/// <b>这一票的判据分两半，各自说清楚证到了什么：</b>
///
/// - **「三条收场路径跑完后 <c>WindowState</c> 没有被改过」**（票面点名这是最重要的产出）：
///   这一半是**文本级守卫**，读 <c>PerformerWindow.axaml.cs</c> 的源码文本断言 ——
///   起 Avalonia 在 NUnit 里要一台有桌面会话的机器，而这套工程里连
///   <c>Avalonia.Headless</c> 都没有（离线包缓存里也没有），所以真开一个窗口这条路走不通。
///   守卫拦的是**以后有人顺手加一句还原**：加了不会有任何报错，只会让急停那一下一个置顶窗
///   弹回来盖住用户的游戏画面（而且急停认任意键，他手还在键盘上，下一个按键会打到窗口上）。
/// - **「最小化落在按下那一刻」**：从 <c>OnStart</c> 里的调用顺序上断言 ——
///   它排在预检放行之后、悬浮层画倒计时之前、100ms 那条节奏之前，
///   所以「倒计时走完再收」和「第一个音发出去再收」这两条路都进不来。
///
/// <b>证不到的（如实留空，归上机）：</b>「按开始 → 窗口真的下去了 → 切到游戏 → 演奏正常」、
/// 「急停之后窗口没有跳回来」、「正常走完没有跳回来」、「急停之后手继续敲键盘不会打到窗口上」——
/// 这四条要一块真屏幕、一个真前台窗口、一次真按键。本机还多一层：
/// <c>EnableLUA=1</c> + <c>PromptOnSecureDesktop=1</c> ⇒ 预检恒 <c>NotElevated</c>
/// ⇒ **真起不了一场演奏**，连倒计时都进不去（见 74 号票撞过的同一堵墙）。
/// </summary>
public class PerformerMinimizeTests
{
    /// <summary>「收下去」那一句。全文件只许出现一次，而且只许在按下开始那一步。</summary>
    private const string 收下去 = "WindowState = WindowState.Minimized;";

    // ==================== 取材 ====================

    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string 仓库根 => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string 读(params string[] 段) => File.ReadAllText(Path.Combine(段.Prepend(仓库根).ToArray()));

    private static string 演奏器代码 => 读("MidiPerformer.App", "Views", "PerformerWindow.axaml.cs");

    private static string 用例代码 => 读("MidiPerformer.Core", "UseCases", "Perform", "StartPerformance.cs");

    /// <summary>取两处标记之间的原文（注释先摘掉：注释里提一句 WindowState 也能把断言喂饱）。</summary>
    private static string 一段(string source, string 起, string 止)
    {
        source = 只读代码(source);

        int a = source.IndexOf(起, StringComparison.Ordinal);
        Assert.That(a, Is.GreaterThanOrEqualTo(0), $"源码里找不到「{起}」");

        int b = source.IndexOf(止, a + 起.Length, StringComparison.Ordinal);
        Assert.That(b, Is.GreaterThan(a), $"「{起}」之后找不到「{止}」");

        return source[a..b];
    }

    /// <summary>按花括号配对取一个方法的整段（比「两个标记之间」结实，尾巴不看签名写成什么样）。</summary>
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

    /// <summary>一段代码里某串字的位置；找不到当场红，别让下面的比大小变成「-1 &lt; 5」的假绿。</summary>
    private static int 位置(string 段, string 找)
    {
        int i = 段.IndexOf(找, StringComparison.Ordinal);
        Assert.That(i, Is.GreaterThanOrEqualTo(0), $"这一段里找不到「{找}」，下面的先后顺序就无从谈起");
        return i;
    }

    /// <summary>
    /// 摘掉 <c>//</c>、<c>///</c>、<c>/* */</c> 注释，只留代码：这一票的注释里写着「别加还原」，
    /// 而断言要看的自始至终是**代码** —— 注释里那几个字不该把守卫喂饱（也不该把它弄红）。
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
                行注释 = false;
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

    /// <summary>全文件里提到 <c>WindowState</c> 的那些行（去注释之后，带行号）。</summary>
    private static List<(int 行号, string 文本)> 提到窗口状态的行(string source)
        => 只读代码(source)
            .Split('\n')
            .Select((文本, 序) => (行号: 序 + 1, 文本: 文本.Trim()))
            .Where(l => l.文本.Contains("WindowState"))
            .ToList();

    /// <summary>按下开始那一步（急停那颗按钮之前都是它）。</summary>
    private static string 按下开始 => 花括号段(演奏器代码, "private void OnStart(", "找不到 OnStart");

    // ==================== 一、那一刻 ====================

    /// <summary>
    /// <b>按下那一刻就收下去</b>：位置从调用顺序上钉死 —— 排在哪几件事之前就说明它是此时发生的。
    ///
    /// 三条错的路各被哪一句挡住：
    /// - 「倒计时走完再收」：收下去那一句必须排在<b>任何一处读倒计时</b>之前
    ///   （<c>CountdownSecondsLeft</c> 是倒计时唯一的读数），也排在悬浮层与 100ms 那条节奏之前；
    /// - 「第一个音发出去再收」：进度线（<c>OnProgressTick</c>）和派发那条线（<c>OnNoteSent</c>）里
    ///   一个字都不许有 —— 那两条是演奏开始之后才会走到的；
    /// - 「干脆关掉 / 藏起来」：<c>Close()</c> 会走 <c>OnClosed</c>，那里第一句就是
    ///   <c>StopPerformance()</c> —— 等于用户一按开始就把这场演奏掐了，而且不报错。
    ///
    /// 为什么必须在倒计时开始之前（票面 §①）：倒计时那几秒存在的**唯一目的**就是给用户切窗口，
    /// 倒计时走完再收，他还得自己再切一次，那几秒就白留了。
    /// </summary>
    [Test]
    public void 最小化落在按下那一刻而不是倒计时之后()
    {
        var 开始 = 按下开始;
        var 进度线 = 花括号段(演奏器代码, "private void OnProgressTick(",
            "找不到 OnProgressTick —— 倒计时归零那一下就在它里面");
        var 派发线 = 花括号段(演奏器代码, "private void OnNoteSent(",
            "找不到 OnNoteSent —— 第一个音发出去那一下走的就是它");

        Assert.Multiple(() =>
        {
            Assert.That(开始, Does.Contain(收下去),
                "按下开始那一步没有把窗口收下去 —— 51 号要的就是这一句");

            // 预检放行之后：被拒的那一次一个音都没发出去、一条线程都没起，
            // 窗口不该跟着下去 —— 用户正要看那句原因
            Assert.That(位置(开始, 收下去), Is.GreaterThan(位置(开始, "PerformanceStartOutcome.Started")),
                "收下去排在了预检结论之前：预检一拒，窗口也下去了，那句原因没人看得见");

            // 一切跟倒计时有关的动作之前
            Assert.That(位置(开始, 收下去), Is.LessThan(位置(开始, "CountdownSecondsLeft")),
                "收下去排在了读倒计时之后 —— 那正是「倒计时走完再收」，那几秒白留");
            Assert.That(位置(开始, 收下去), Is.LessThan(位置(开始, "ShowOverlay()")),
                "收下去排在了拉起悬浮层之后");
            Assert.That(位置(开始, 收下去), Is.LessThan(位置(开始, "_progressTimer.Start()")),
                "收下去排在了 100ms 那条节奏之后");

            // 是「收下去」，不是「关掉 / 藏起来」
            Assert.That(开始, Does.Not.Contain(".Close("),
                "改成关窗口了：OnClosed 第一句就是 StopPerformance()，用户一按开始这场演奏当场没了");
            Assert.That(开始, Does.Not.Contain(".Hide("),
                "改成藏起来了：窗口不在任务栏上，用户按票面那句话「去任务栏点一下」也找不回来");

            // 演奏开始之后才会走到的那两条线里一个字都不许有
            Assert.That(进度线, Does.Not.Contain("WindowState"),
                "进度线上动了窗口状态 —— 那是「倒计时走完 / 演奏中每 100ms」才走到的地方");
            Assert.That(派发线, Does.Not.Contain("WindowState"),
                "发第一个音那条线上动了窗口状态 —— 最小化不是「第一个音之后」");
        });
    }

    // ==================== 二、三条收场（这一票最重要的产出） ====================

    /// <summary>
    /// 收场那几条路：一条演奏从「开始」走到「没了」，全部出口都在这张表里。
    ///
    /// 「急停」那颗按钮的调用点单独一笔：<c>OnStop</c> 是**表达式体**（<c>=&gt; StopPerformance();</c>），
    /// 花括号配对的取法会一路找下去、切到下一个方法身上 —— 那就变成了在验别人的身子，所以按两处标记切。
    /// </summary>
    private static (string 名字, string 段)[] 收场路径(string 原文) => new[]
    {
        ("正常放完 / 看门狗超时 / 中途出错（三种都走 Finished 这一个回调）",
            花括号段(原文, "private void OnPerformanceFinished", "找不到 OnPerformanceFinished")),
        ("急停（窗口上那颗按钮）：松键并收尾", 花括号段(原文, "private void StopPerformance", "找不到 StopPerformance")),
        ("急停（任意键）", 花括号段(原文, "private void OnPanicHotkey", "找不到 OnPanicHotkey")),
        ("「急停」那颗按钮的调用点（表达式体，按两处标记切）",
            一段(原文, "private void OnStop(", "private void OnGoEditor(")),
        ("倒计时归零那一下与演奏中每 100ms",
            花括号段(原文, "private void OnProgressTick(", "找不到 OnProgressTick")),
        ("关窗收尾", 花括号段(原文, "protected override void OnClosed", "找不到 OnClosed")),
    };

    /// <summary>
    /// <b>这一票最重要的产出</b>：三条收场路径（正常结束 / 急停 / 出错）跑完之后，
    /// 演奏器自己的 <c>WindowState</c> **一次都没有被改过** —— 窗口留在最小化，用户自己切回来。
    ///
    /// 票面 §② 专门否掉了「结束自动还原」，理由是急停那一下：用户刚按下急停，一个**置顶**窗
    /// 突然弹回来盖在游戏上，而那多半正是他想接着玩的时候；而且急停认任意键，他手还在键盘上，
    /// 窗口一弹回来抢了焦点，下一个按键就打到了窗口上。
    ///
    /// 逐条断言的理由：这一条**坏掉不报错** —— 加一句还原，程序照样跑、演奏照样正常，
    /// 只有用户会在急停那一下被挡一次屏幕。所以至少要拦住「哪天有人顺手加回来」。
    /// 别处还有一张网（<see cref="写窗口状态的地方只有那两处"/>）：藏在**新方法**里的还原也能兜住。
    /// </summary>
    [Test]
    public void 三条收场路径跑完后窗口状态一个字节都没动()
    {
        var 各条 = 收场路径(演奏器代码);

        Assert.Multiple(() =>
        {
            foreach (var (名字, 段) in 各条)
                Assert.That(段, Does.Not.Contain("WindowState"),
                    $"「{名字}」里动了 WindowState —— 51 号票 §②：三种收场一视同仁，都不许跳回来");
        });
    }

    /// <summary>
    /// 上面那张网只盖住**已经写出来的**那几个方法；这一条盖住**别处**：
    /// 全文件提到 <c>WindowState</c> 的地方只许有两处 ——
    /// 按下开始那一下的「收下去」，以及 <c>OnGoEditor</c> 里对 **owner（主窗口）** 的还原。
    /// 后者说的不是这一场演奏：那条路是「这个窗口一条能弹的轨都没有，去编辑器」，它收的是主窗口。
    ///
    /// 于是一个「顺手加一句还原」的改动，哪怕写成一个新方法（<c>RestoreWindow()</c> 之类）
    /// 挂在收尾回调上，也会让这一条红：那一行必然带 <c>WindowState</c> 却没有 <c>owner.</c> 前缀。
    /// </summary>
    [Test]
    public void 写窗口状态的地方只有那两处()
    {
        var 提到 = 提到窗口状态的行(演奏器代码);
        var 编辑那条路 = 花括号段(演奏器代码, "private void OnGoEditor(", "找不到 OnGoEditor");

        Assert.Multiple(() =>
        {
            Assert.That(提到, Has.Count.EqualTo(2),
                "全文件提到 WindowState 的地方该只有两处（按下那一下的收下去、OnGoEditor 里对 owner 的还原），"
                + "现在这些是：" + string.Join(" | ", 提到.Select(l => $"第{l.行号}行 {l.文本}")));

            Assert.That(提到.Count(l => l.文本 == 收下去), Is.EqualTo(1),
                "「收下去」那一句应该在，而且全文件只该有一次");

            Assert.That(提到.Where(l => l.文本 != 收下去).Select(l => l.文本), Is.All.Contain("owner."),
                "除了按下那一下，别处再碰 WindowState 只许是 owner（主窗口）的事 —— "
                + "演奏器自己的窗口状态，收场之后一律不许再动");

            Assert.That(编辑那条路, Does.Contain("owner.WindowState"),
                "OnGoEditor 里那句还原是对 owner 的（它收的是主窗口），别把它搬成对演奏器自己的");
        });
    }

    // ==================== 三、倒计时没被吃掉 ====================

    /// <summary>
    /// <b>倒计时照样从头走满</b>：收下去这一下不影响它。
    ///
    /// 判据在**归属**上：倒计时是 Core 的事（<c>StartPerformance</c>：终点在按下那一刻就算定，
    /// 剩几秒由「在不在跑 + 墙上钟」现算），Core 里一个 <c>WindowState</c> 都没有 ——
    /// 窗口在不在屏幕上动不了它。窗口这一侧只是照着那个数画字。
    ///
    /// 顺带钉住倒计时那块悬浮层**没有被窗口带着一起下去**：它是另一个顶层窗口、<c>Show()</c> 不带
    /// owner（带 owner 的话，Win32 下 owner 一最小化，owned 窗口跟着一起隐藏 ——
    /// 那用户切到游戏里就看不到倒计时了，而那几秒正是他需要这个数的时候）。
    /// </summary>
    [Test]
    public void 倒计时照样从头走满没被最小化吃掉()
    {
        var 开始 = 按下开始;
        var 悬浮层 = 花括号段(演奏器代码, "private PerformerOverlayWindow ShowOverlay(",
            "找不到 ShowOverlay");

        Assert.Multiple(() =>
        {
            // 倒计时归 Core 管，Core 不看窗口状态
            Assert.That(用例代码, Does.Contain("_countdownEndsAt = _clock.NowSeconds() + Math.Max(0, request.CountdownSeconds)"),
                "倒计时终点不再是「按下那一刻 + 档位秒数」了 —— 起跑那几毫秒会被算进用户的倒计时里");
            Assert.That(用例代码, Does.Not.Contain("WindowState"),
                "Core 里出现了 WindowState —— 倒计时的长度不该取决于窗口在不在屏幕上");

            // 按下那一刻起跑的那几件事，一件都没被最小化顶掉
            Assert.That(开始, Does.Contain("_performance.Start(request"),
                "按下开始没有真起跑");
            Assert.That(开始, Does.Contain("ShowCountdown("),
                "倒计时不再画到悬浮层上了：用户切到游戏里就看不到还剩几秒");
            Assert.That(开始, Does.Contain("_progressTimer.Start();"),
                "100ms 那条节奏没起来：倒计时与演奏中的读数都不会再动");

            // 悬浮层是自由窗口，不跟着演奏器窗口一起最小化
            Assert.That(悬浮层, Does.Contain(".Show()"), "悬浮层没有显示出来");
            Assert.That(悬浮层, Does.Not.Contain("Owner"),
                "悬浮层挂上了 owner：owner 一最小化它跟着隐藏，游戏里那块倒计时就没了");
        });
    }
}
