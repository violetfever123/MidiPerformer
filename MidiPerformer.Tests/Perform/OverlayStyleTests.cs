using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 悬浮层要的那三个窗口拓展样式。<b>这不是网关的行为测试</b> ——
/// 设没设上、系统认不认、焦点到底有没有被抢走，只有真窗口加一个真前台窗口才看得见
/// （那一条归人工，见 06 的验收记录；浮在游戏画面上的效果归 13）。
///
/// 这里验的是**纯常量事实**：要的是哪几个位。值得单独守一条，是因为写错一个位的后果
/// 和「忘了设」一模一样，而且是**静默的**：没有编译错、运行时也不报错，键还照发，
/// 只是全发到悬浮层自己或者桌面上 —— 表现就是「游戏一个音都收不到」，查不出原因。
///
/// 三个常量里最容易写错的是不激活那一个：<c>0x0800_0000</c> 和 <c>0x0080_0000</c>
/// 只差一个 0 的位置，而 <c>0x0080_0000</c> 是 <c>WS_EX_LAYERED</c> 之外没人认识的位 ——
/// 设上去毫无效果，窗口照抢焦点。这和 <c>InputMethod</c> 那两个相邻的 IMC_ 常量是同一类陷阱
/// （见那边的说明），也是 <c>InputSenderLayoutTests</c> 守 40 字节的同一个理由：
/// 网关的**事实**能验，行为验不了。
/// </summary>
public class OverlayStyleTests
{
    /// <summary>
    /// WinUser.h 里那三个值，照抄一遍。**故意的重复**：从被测代码里取常量再比，
    /// 就等于自己和自己比 —— 常量表改了这边也跟着改，一条都拦不住。
    /// </summary>
    private const long WS_EX_TRANSPARENT = 0x0000_0020L;   // 点击穿透
    private const long WS_EX_TOOLWINDOW = 0x0000_0080L;    // 不进任务栏 / Alt+Tab
    private const long WS_EX_NOACTIVATE = 0x0800_0000L;    // 点了也不激活

    [Test]
    public void 要的是点击穿透不激活不进任务栏这三个位()
    {
        Assert.That(OverlayWindowStyles.Flags,
            Is.EqualTo(WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            "三个位对不上：多一个少一个、或者哪个抄错了一位数，都是静默失效");
    }

    /// <summary>
    /// 不激活那一位单独再钉一遍 —— 它是要紧的那一个（抢了焦点，之后发的键就发到错的地方），
    /// 而它也是三个里唯一「抄错一位仍然是个合法常量」的。
    /// </summary>
    [Test]
    public void 不激活那一位是最要紧的那一个()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OverlayWindowStyles.Flags & WS_EX_NOACTIVATE, Is.Not.Zero,
                "没要不激活：悬浮层一旦拿到焦点，后面发的按键就全发到它身上，游戏一个都收不到");
            Assert.That(OverlayWindowStyles.Flags & 0x0080_0000L, Is.Zero,
                "0x0080_0000 不是不激活（少了一位就是一个没用的位），设上去等于没设");
        });
    }

    /// <summary>
    /// 叠加而不是覆盖：窗口本来就有别的拓展样式（分层、RTL、拖边框…），
    /// 整个写回去会顺手把它们抹掉。这里只加位、一个都不清。
    /// </summary>
    [Test]
    public void 只加位不清位()
    {
        const long 别的位 = 0x0004_0000L;    // 随手取一个不相干的位，模拟窗口已有的样式

        long after = OverlayWindowStyles.WithOverlayStyles(别的位);

        Assert.Multiple(() =>
        {
            Assert.That(after & 别的位, Is.Not.Zero, "把窗口原有的拓展样式抹掉了 —— 会连带改掉别的行为");
            Assert.That(after, Is.EqualTo(别的位 | OverlayWindowStyles.Flags));
        });
    }

    /// <summary>
    /// 重复叠是幂等的。这一条不是洁癖：样式要等窗口显示之后才设得上，
    /// 而窗口可以被显示多次（一场演奏一遍），<c>Apply</c> 也就可能被调多次。
    /// </summary>
    [Test]
    public void 叠两次和叠一次一样()
    {
        long once = OverlayWindowStyles.WithOverlayStyles(0);

        Assert.That(OverlayWindowStyles.WithOverlayStyles(once), Is.EqualTo(once));
    }
}
