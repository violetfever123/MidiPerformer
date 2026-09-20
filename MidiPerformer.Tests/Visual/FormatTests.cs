using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Ports.Inbound;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 读数条与走带条上的**文案**。
///
/// 单独测一遍的理由：这些字符串是用户唯一能读到数的地方 ——
/// 卷帘上画错了还能靠眼睛发现，读数条上写错一个音名或拍位，用户会当成谱子错了。
/// 顺便把「空值不留白、不抛异常」这一类边界钉住。
/// </summary>
public class FormatTests
{
    [Test]
    public void 音高写成音名加简谱()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Pitch(60), Is.EqualTo("C4（1）"));
            Assert.That(Format.Pitch(61), Is.EqualTo("C#4（#1）"), "升号音两边都带 #");
            Assert.That(Format.Pitch(0), Is.EqualTo("C-1（1）"));
        });
    }

    [Test]
    public void 拍位和时值都写两位小数()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Beat(1), Is.EqualTo("1.00 拍"));
            Assert.That(Format.Beat(2.5), Is.EqualTo("2.50 拍"));
            Assert.That(Format.Length(0.25), Is.EqualTo("0.25 拍"));
        });
    }

    [Test]
    public void 小数点不跟着系统语言走()
    {
        // F2 的格式符会跟着当前区域性变 —— 德语环境下会写成 "1,00"，
        // 而这一条是给人对着谱子核对的，读数必须是稳定的
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.That(Format.Beat(1), Is.EqualTo("1.00 拍"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public void 小节号从一起数()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.BarNumber(1), Is.EqualTo("1"));
            Assert.That(Format.Position(3, 12), Is.EqualTo("3 / 12 小节"));
        });
    }

    [Test]
    public void 轨序号补零对齐()
    {
        Assert.That(Format.TrackNumber(1), Is.EqualTo("01"), "wireframe 里是两位的 01");
    }

    [Test]
    public void 打击乐轨直接说鼓组()
    {
        Assert.Multiple(() =>
        {
            // 9 号声道整条都是鼓组，音色号在那一轨没有意义
            Assert.That(Format.Timbre(0, 9), Is.EqualTo("标准鼓组 · 通道 10"));
            Assert.That(Format.Timbre(0, 0), Is.EqualTo("大钢琴 · GM 1"), "GM 编号从 1 起，程序里从 0 起");
            Assert.That(Format.Timbre(22, 3), Is.EqualTo("口琴 · GM 23"));
        });
    }

    [Test]
    public void 音色号越界就退回编号()
    {
        // 编不出来就别编：报一个假名字比报编号更误导人
        Assert.Multiple(() =>
        {
            Assert.That(Format.ProgramName(200), Is.EqualTo("音色 201"));
            Assert.That(Format.ProgramName(-1), Is.EqualTo("音色 0"));
        });
    }

    /// <summary>
    /// 移调那格的读数：正负号只在真有方向时才出现。
    ///
    /// 零写成 <c>0</c> 而不是 <c>+0</c> 是有意的 —— 零同时也是「没移调」这个默认状态的样子，
    /// 「+0 半音」看着像动过一手。这条单独钉住它，因为轨头上那一格是**每时每刻**都挂着的，
    /// 一个「+0」会在每一首没移调的曲子上出现。
    /// </summary>
    [Test]
    public void 移调读数只在有方向时才带符号()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Transpose(0), Is.EqualTo("0 半音"), "零就是零，不带符号");
            Assert.That(Format.Transpose(12), Is.EqualTo("+12 半音"));
            Assert.That(Format.Transpose(-12), Is.EqualTo("-12 半音"));
            Assert.That(Format.Transpose(1), Is.EqualTo("+1 半音"));
            Assert.That(Format.Transpose(-1), Is.EqualTo("-1 半音"));
            // 移调步进器一直能按，按到 int 的边上也得写得出字来，不能抛
            Assert.That(Format.Transpose(int.MinValue), Is.EqualTo("-2147483648 半音"));
            Assert.That(Format.Transpose(int.MaxValue), Is.EqualTo("+2147483647 半音"));
        });
    }

    [Test]
    public void 时长写成m比ss()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Clock(0), Is.EqualTo("0:00"));
            Assert.That(Format.Clock(65), Is.EqualTo("1:05"));
            Assert.That(Format.Clock(3599), Is.EqualTo("59:59"));
        });
    }

    [Test]
    public void 没时长的时候不写负数也不崩()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.Clock(-5), Is.EqualTo("0:00"));
            Assert.That(Format.Clock(double.NaN), Is.EqualTo("0:00"));
        });
    }

    [Test]
    public void 刻度为零时不除零()
    {
        Assert.That(Format.Beats(960, 0), Is.EqualTo(0));
    }

    // ==================== 读数条右边那行快捷键提示 ====================

    /// <summary>
    /// 那两行提示**逐字钉住**（26 号工单，36 号分成两层）。
    ///
    /// 为什么值得逐字：它坏了不会报错，只会让人以为功能坏了 —— 而且这事已经发生过两次：
    /// 20 之前它写着「空格 播放」（那时空格真的只管开始），18 之前写着「Ctrl + ← → 前后跳」
    /// （那时真的跨轨跳）。两次都是代码往前走了、这行字留在原地，屏幕上没有任何东西会红。
    /// 所以改键位的人**必须**回来改这儿，而下一条测试（<c>ShortcutHintTests</c>）量的是反方向：
    /// 有人改了这句话、按键那一段里却没这个键。
    /// </summary>
    [Test]
    public void 快捷键提示逐字就是屏幕上那两行()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.ReadoutHintPerforming, Is.EqualTo(
                "空格 播放/暂停 · Shift + 空格 回跳一小节并播放 · Ctrl + ↑ ↓ 换轨"),
                "一个音都没选中时屏幕上那一行 —— 改它之前先去看 MainWindow.OnWindowKeyDown，那是它的真身");
            Assert.That(Format.ReadoutHintEditing, Is.EqualTo(
                "← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · "
                + "Ctrl + ← → 选同轨前/后一个音 · Delete 删除 · Esc 取消选中"),
                "选中了音时屏幕上那一行 —— 同上");
        });
    }

    /// <summary>
    /// 提示里点名的每一样，都在**它该在的那一行**里（不能多一个「空格 播放」管两件事的旧说法）。
    ///
    /// 和逐字那条分开写：逐字那条挡的是「有人改了这句」，这条挡的是「这句话里少了一条，
    /// 而剩下的一整行照样过着逐字比对」—— 少一条比多一条难发现，因为少的那条不会打错字。
    ///
    /// 36 号加了两组断言，都是「分层」这件事本身的判据：
    /// **两类各归各的**（走带那几条不在编辑行里、编辑那几条不在走带行里 ——
    /// 两条会重叠的话，「分层」就只是把一整行切成两半而已），
    /// 以及**撤销 / 重做确实撤出去了**（用户原话：「不需要单独写，将它们作为快捷键，
    /// 直接放到『操作』里面作为提示就可以了」）。
    /// </summary>
    [Test]
    public void 提示里一条都不缺()
    {
        Assert.Multiple(() =>
        {
            // 20：空格是**切换**，只写「播放」就是退回被推翻的旧决定
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("空格 播放/暂停"));
            // 35：Shift+空格 是**另一个动作**（回跳一小节并播放），不是空格的修饰版
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("Shift + 空格 回跳一小节并播放"));
            // 36：换轨归**演奏**那一行（用户原话：「将『音轨的选择』放到『播放』和
            //『滚回上一小节播放』里面会比较好」）—— 它换的是焦点轨，而焦点轨决定你弹哪条轨
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("Ctrl + ↑ ↓ 换轨"));

            // 09 的方案 A 四条，一条都不能漏；18 / 19 那两条也都在编辑这一行里
            Assert.That(Format.ReadoutHintEditing, Does.Contain("← → 移时间"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("↑ ↓ 移音高"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Shift + ← → 改时值"));
            // 18：「同轨」两个字是那张工单的全部内容；36 把说法改成「选…一个音」
            //（用户要的是「如何选下一个音」看得见，而原文里没有「选」字）
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Ctrl + ← → 选同轨前/后一个音"));
            // 19：这一条是那次新加进提示里的（从前 Delete 根本没提）
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Delete 删除"));
            // 37：Esc 放开选中的音。动作是 36 号做的，写进这一行是 37 号 ——
            // 用户原话：「取消选中放在『选中一些音符之后』的那个提示行」
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Esc 取消选中"));

            // 两类各归各的
            Assert.That(Format.ReadoutHintPerforming, Does.Not.Contain("移时间"),
                "编辑那一类不该出现在走带那一行里");
            Assert.That(Format.ReadoutHintPerforming, Does.Not.Contain("Delete"));
            Assert.That(Format.ReadoutHintPerforming, Does.Not.Contain("取消选中"),
                "取消选中只在「已经选中了音」的时候才有意义 —— 它归编辑那一层（37）");
            Assert.That(Format.ReadoutHintEditing, Does.Not.Contain("播放/暂停"),
                "走带那一类不该出现在编辑那一行里");
            Assert.That(Format.ReadoutHintEditing, Does.Not.Contain("换轨"));

            // 全文 = 两行都在（分层之后，ToolTip 是唯一能看全的地方）
            Assert.That(Format.ReadoutHintTooltip, Does.Contain(Format.ReadoutHintPerforming));
            Assert.That(Format.ReadoutHintTooltip, Does.Contain(Format.ReadoutHintEditing));

            // 36：撤销 / 重做从提示行**和它的 ToolTip** 里都撤出去了
            //（「操作」菜单项右侧那一处还在，由 ShortcutHintTests 守着）
            foreach (var gone in new[] { "撤销", "重做", "Ctrl+Z", "Ctrl+Y" })
                Assert.That(Format.ReadoutHintTooltip, Does.Not.Contain(gone),
                    $"「{gone}」在「操作」菜单里说一次就够了 —— 提示行和它的 ToolTip 里不该再有");
        });
    }

    // ==================== 抽掉一段的预览 ====================

    /// <summary>用例里那一首的刻度：3/4 拍、每四分音符 480 tick ⇒ 一小节 1440。</summary>
    private const long 一小节 = 3 * 480;
    private const int 四分音符 = 480;

    /// <summary>第 n 小节（1 起）的起始 tick。</summary>
    private static long 第几小节(int n) => (n - 1) * 一小节;

    /// <summary>
    /// 那一行预览是「按下抽掉之前，屏幕上唯一说得清会发生什么的地方」，所以逐字钉住。
    /// 三样都得在：哪几小节、动几个音、这条轨短几小节。
    ///
    /// 38 号之后那一段是**拖出来的 tick**（可以停在任何一条十六分线上），
    /// 但整小节对齐的那一刀仍然说「第 5–8 小节（共 4 小节）」—— 读起来最短，
    /// 而它是最常见的那一刀。
    /// </summary>
    [Test]
    public void 抽掉一段的预览把三样都说出来()
    {
        var preview = new CutPreview.Result(Deleted: 12, Trimmed: 2, Shifted: 30, BarsBefore: 96, BarsAfter: 92);

        Assert.That(
            Format.CutSummary(第几小节(5), 第几小节(9), 3, preview, 一小节, 四分音符),
            Is.EqualTo("第 5–8 小节（共 4 小节）：删掉 12 个音、在切口上剪短 2 个、后面 30 个提前 4 小节"
                       + " · 第 03 轨 96 → 92 小节"));
    }

    /// <summary>
    /// 只有删、没有前移是常事（剪的是尾巴上那一段）—— 那时不该出现「后面 0 个提前 4 小节」。
    /// </summary>
    [Test]
    public void 某一档是零就不提它()
    {
        var preview = new CutPreview.Result(Deleted: 4, Trimmed: 0, Shifted: 0, BarsBefore: 8, BarsAfter: 7);

        Assert.That(
            Format.CutSummary(第几小节(3), 第几小节(4), 1, preview, 一小节, 四分音符),
            Is.EqualTo("第 3–3 小节（共 1 小节）：删掉 4 个音 · 第 01 轨 8 → 7 小节"));
    }

    /// <summary>一个音都不会动的时候直说 —— 那时「抽掉」是灰的，这一行得说清为什么。</summary>
    [Test]
    public void 什么都不改的时候直说()
    {
        var preview = new CutPreview.Result(0, 0, 0, BarsBefore: 8, BarsAfter: 8);

        Assert.That(
            Format.CutSummary(第几小节(5), 第几小节(9), 3, preview, 一小节, 四分音符),
            Is.EqualTo("第 5–8 小节（共 4 小节）：这一段里没有音，抽了和没抽一样"));
    }

    /// <summary>
    /// **38 号的正题**：那一段没对齐小节线的时候，说的不再是「第几小节」，
    /// 而是两端各在**小节内的哪个位置**。
    ///
    /// 这一条是「更精准的切割」在屏幕上的那一半：用户拖到第 5 小节的第 2.75 拍上，
    /// 那一行就得说 2.75 拍 —— 只说「第 5 小节」等于把他刚拖出来的落点又抹掉了，
    /// 而那正是他放弃填小节号的理由。
    /// </summary>
    [Test]
    public void 没对齐小节线时说小节内的位置()
    {
        var preview = new CutPreview.Result(Deleted: 3, Trimmed: 0, Shifted: 7, BarsBefore: 96, BarsAfter: 96);

        // 第 5 小节的第 2 拍（+480）到**第 6 小节的头上**（+1440）
        long start = 第几小节(5) + 480;
        long end = 第几小节(6);

        Assert.That(
            Format.CutSummary(start, end, 3, preview, 一小节, 四分音符),
            Is.EqualTo("第 5 小节第 2 拍 到 第 6 小节：删掉 3 个音、后面 7 个提前 2 拍"
                       + " · 第 03 轨 96 → 96 小节"));
    }

    /// <summary>
    /// 前移多少**不能再写死「几小节」**：切点落在拍上时，提前的量是 2 小节 3 拍。
    /// 写死小节数会算出「提前 0 小节」—— 明明提前了三拍，那是一句错话。
    /// </summary>
    [Test]
    public void 提前多少写得出小节加拍()
    {
        var preview = new CutPreview.Result(Deleted: 1, Trimmed: 0, Shifted: 5, BarsBefore: 10, BarsAfter: 10);

        // 一整小节又三拍：一小节 3 拍，所以正好切了两小节 —— 用来对照下面那一条
        Assert.That(
            Format.CutSummary(0, 2 * 一小节, 1, preview, 一小节, 四分音符),
            Does.Contain("提前 2 小节"));

        // 两小节 + 一拍
        Assert.That(
            Format.CutSummary(0, 2 * 一小节 + 480, 1, preview, 一小节, 四分音符),
            Does.Contain("提前 2 小节 1 拍"));
    }

    /// <summary>位置与长度的两种写法本身：整小节 / 整拍 / 小数拍，各自都在。</summary>
    [Test]
    public void 位置和长度的写法()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.BarPosition(第几小节(5), 一小节, 四分音符), Is.EqualTo("第 5 小节"));
            Assert.That(Format.BarPosition(第几小节(5) + 480, 一小节, 四分音符),
                Is.EqualTo("第 5 小节第 2 拍"), "正好落在拍线上写整拍，拍从 1 起");
            Assert.That(Format.BarPosition(第几小节(5) + 360, 一小节, 四分音符),
                Is.EqualTo("第 5 小节第 1.75 拍"), "十六分的偏移写成小数拍");

            Assert.That(Format.SpanLength(4 * 一小节, 一小节, 四分音符), Is.EqualTo("4 小节"));
            Assert.That(Format.SpanLength(480, 一小节, 四分音符), Is.EqualTo("1 拍"), "不够一小节就只说拍");
            Assert.That(Format.SpanLength(一小节 + 2 * 480, 一小节, 四分音符), Is.EqualTo("1 小节 2 拍"));
            Assert.That(Format.SpanLength(0, 一小节, 四分音符), Is.EqualTo("0 拍"), "零长度不写空串");
        });
    }

    // ==================== 预检不放行的提示 ====================

    /// <summary>
    /// 预检不放行时状态行上那句话。**逐字钉住**，理由和上面那条预览一样：
    /// 这是用户唯一能知道「为什么按了开始没动静」的地方。
    ///
    /// 三句话各点出**下一步该做什么**（以管理员身份重开 / 切成英文 / 换一条轨），
    /// 而不是三句「不能开始」—— 说不清下一步就等于把人支到错方向上去。
    /// 这三条文案是这条工单的硬要求（「两种都要给出明确的中文提示，不是静默失败」），
    /// 所以它不能再写死在窗口里：那样没人给它加得了断言。
    /// </summary>
    [Test]
    public void 三种不放行各有一句说清下一步的中文()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.NotElevated),
                Is.EqualTo("没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。"));
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.ImeActive),
                Is.EqualTo("没开始：输入法现在是中文。中文态下按键会被输入法截走，弹出来就是整段整段地漏音。切成英文再按一次。"));
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.NoPlayableTrack),
                Is.EqualTo("没开始：这条轨弹不了。口琴一次只响一个音，所以只能弹单声部、不带打击乐的轨。换一条试试。"));
        });
    }

    /// <summary>
    /// 每个不放行的原因都得有自己的一句话，而且不能是空的、不能和别人撞。
    ///
    /// 这一条防的是**将来**：往 <see cref="PerformanceStartOutcome"/> 里添一个失败原因、
    /// 忘了在这儿配文案，用户看到的就是「按了开始什么都没发生」—— 正是要防的那种静默失败。
    /// 断言写成「遍历所有枚举值」而不是「逐个列出来」，就是为了让新增的那个自动落进网里：
    /// 新值会走到那句兜底文案上，于是「和别人撞了」这条会红。
    /// </summary>
    [Test]
    public void 每个不放行的原因都有一句不重复的话()
    {
        var refusals = Enum.GetValues<PerformanceStartOutcome>()
            .Where(o => o != PerformanceStartOutcome.Started)
            .ToList();

        var messages = refusals.Select(Format.PreflightRefusal).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.All.Not.Empty, "有一条不放行没给文案 —— 那就是静默失败");
            Assert.That(messages.Distinct().Count(), Is.EqualTo(refusals.Count),
                "两条不放行给了同一句话：用户分不出该去改权限还是去切输入法");
        });
    }

    /// <summary>
    /// 放行时也**绝不**返回空串。
    ///
    /// 调用方只在 <c>!= Started</c> 时才用它，但空串在这儿是个陷阱：哪天真有人漏了那个判断，
    /// 状态行会变成一片空白 —— 又回到「静默失败」。所以宁可回一句「预检没放行」。
    /// </summary>
    [Test]
    public void 放行时也绝不返回空串()
    {
        Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.Started), Is.Not.Empty);
    }
}
