using MidiPerformer.Adapters.Presenters;
using MidiPerformer.Core.Ports.Inbound;
using NUnit.Framework;

namespace MidiPerformer.Tests.Visual;

/// <summary>
/// 读数条与走带条上的文案：这些字符串是用户唯一能读到数的地方，
/// 顺便钉住「空值不留白、不抛异常」这一类边界。
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
        // F2 的格式符跟着区域走 —— 德语环境下会写成 "1,00"
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
        Assert.Multiple(() =>
        {
            Assert.That(Format.ProgramName(200), Is.EqualTo("音色 201"));
            Assert.That(Format.ProgramName(-1), Is.EqualTo("音色 0"));
        });
    }

    /// <summary>
    /// 移调那格的读数：正负号只在真有方向时才出现，零写成 <c>0</c> 而不是 <c>+0</c>。
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
            // 按到 int 的边上也得写得出字，不能抛
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
    /// 那两行提示逐字钉住：改键位的人必须回来改这儿，
    /// 反方向（改了字而按键那一段里没这个键）由 <c>ShortcutHintTests</c> 守。
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
    /// 提示里点名的每一样都在它该在的那一行里，两类各归各的，撤销 / 重做也确实不在里面。
    /// 和逐字那条分开：逐字那条挡「有人改了这句」，这条挡「少了一条而剩下的照样过着逐字比对」。
    /// </summary>
    [Test]
    public void 提示里一条都不缺()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("空格 播放/暂停"));
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("Shift + 空格 回跳一小节并播放"));
            // 换轨归演奏那一行：它换的是焦点轨，而焦点轨决定你弹哪条轨
            Assert.That(Format.ReadoutHintPerforming, Does.Contain("Ctrl + ↑ ↓ 换轨"));

            Assert.That(Format.ReadoutHintEditing, Does.Contain("← → 移时间"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("↑ ↓ 移音高"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Shift + ← → 改时值"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Ctrl + ← → 选同轨前/后一个音"));
            Assert.That(Format.ReadoutHintEditing, Does.Contain("Delete 删除"));
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

            // 全文 = 两行都在
            Assert.That(Format.ReadoutHintTooltip, Does.Contain(Format.ReadoutHintPerforming));
            Assert.That(Format.ReadoutHintTooltip, Does.Contain(Format.ReadoutHintEditing));

            // 撤销 / 重做不在提示行和它的 ToolTip 里（菜单项右侧那一处由 ShortcutHintTests 守着）
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
    /// 那一行预览逐字钉住，三样都得在：哪几小节、动几个音、这条轨短几小节。
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

    /// <summary>只有删、没有前移时，不该出现「后面 0 个提前 4 小节」。</summary>
    [Test]
    public void 某一档是零就不提它()
    {
        var preview = new CutPreview.Result(Deleted: 4, Trimmed: 0, Shifted: 0, BarsBefore: 8, BarsAfter: 7);

        Assert.That(
            Format.CutSummary(第几小节(3), 第几小节(4), 1, preview, 一小节, 四分音符),
            Is.EqualTo("第 3–3 小节（共 1 小节）：删掉 4 个音 · 第 01 轨 8 → 7 小节"));
    }

    /// <summary>一个音都不会动的时候直说（那时「抽掉」是灰的）。</summary>
    [Test]
    public void 什么都不改的时候直说()
    {
        var preview = new CutPreview.Result(0, 0, 0, BarsBefore: 8, BarsAfter: 8);

        Assert.That(
            Format.CutSummary(第几小节(5), 第几小节(9), 3, preview, 一小节, 四分音符),
            Is.EqualTo("第 5–8 小节（共 4 小节）：这一段里没有音，抽了和没抽一样"));
    }

    /// <summary>
    /// 那一段没对齐小节线时，说的不再是「第几小节」，而是两端各在小节内的哪个位置。
    /// </summary>
    [Test]
    public void 没对齐小节线时说小节内的位置()
    {
        var preview = new CutPreview.Result(Deleted: 3, Trimmed: 0, Shifted: 7, BarsBefore: 96, BarsAfter: 96);

        // 第 5 小节的第 2 拍（+480）到第 6 小节的头上（+1440，都是相对第 5 小节开头）
        long start = 第几小节(5) + 480;
        long end = 第几小节(6);

        Assert.That(
            Format.CutSummary(start, end, 3, preview, 一小节, 四分音符),
            Is.EqualTo("第 5 小节第 2 拍 到 第 6 小节：删掉 3 个音、后面 7 个提前 2 拍"
                       + " · 第 03 轨 96 → 96 小节"));
    }

    /// <summary>
    /// 前移多少不能写死「几小节」：切点落在拍上时得写得出「几小节几拍」。
    /// </summary>
    [Test]
    public void 提前多少写得出小节加拍()
    {
        var preview = new CutPreview.Result(Deleted: 1, Trimmed: 0, Shifted: 5, BarsBefore: 10, BarsAfter: 10);

        // 一小节 3 拍，1 小节 3 拍正好凑成 2 小节 —— 对照下面那一条
        Assert.That(
            Format.CutSummary(0, 2 * 一小节, 1, preview, 一小节, 四分音符),
            Does.Contain("提前 2 小节"));

        // 两小节 + 一拍
        Assert.That(
            Format.CutSummary(0, 2 * 一小节 + 480, 1, preview, 一小节, 四分音符),
            Does.Contain("提前 2 小节 1 拍"));
    }

    /// <summary>位置与长度的两种写法：整小节 / 整拍 / 小数拍 / 零长度。</summary>
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
    /// 预检不放行时状态行上那句话，逐字钉住：三句各点出下一步该做什么
    /// （以管理员身份重开 / 切成英文 / 换一条轨），而不是三句「不能开始」。
    /// </summary>
    [Test]
    public void 三种不放行各有一句说清下一步的中文()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.NotElevated),
                Is.EqualTo("没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。"));
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.ImeActive),
                Is.EqualTo("当前输入法为中文，请切成英文输入法。"));
            Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.NoPlayableTrack),
                Is.EqualTo("没开始：这条轨弹不了。口琴一次只响一个音，所以只能弹单声部、不带打击乐的轨。换一条试试。"));
        });
    }

    /// <summary>
    /// 每个不放行的原因都得有自己的一句话，不能是空的、不能和别人撞。
    /// 断言遍历所有枚举值而不是逐个列出来，新添的失败原因才会自动落进网里。
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
    /// 放行时也绝不返回空串：调用方只在 <c>!= Started</c> 时才用它，
    /// 但漏了那个判断的话状态行会变成一片空白。
    /// </summary>
    [Test]
    public void 放行时也绝不返回空串()
    {
        Assert.That(Format.PreflightRefusal(PerformanceStartOutcome.Started), Is.Not.Empty);
    }
}
