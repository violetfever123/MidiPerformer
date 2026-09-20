using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 悬浮层要的那三个窗口拓展样式 —— 验的是纯常量事实：要的是哪几个位
/// （设没设上、焦点有没有被抢走，只有真窗口看得见，那一条归人工）。
/// 写错一个位的后果和忘了设一样，而且是静默的：没有编译错、运行时不报错，键照发但发错了地方。
/// </summary>
public class OverlayStyleTests
{
    /// <summary>WinUser.h 里那三个值，照抄一遍 —— 从被测代码里取常量再比就是自己和自己比。</summary>
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
    /// 不激活那一位单独再钉一遍：抢了焦点，之后发的键就全发到错的地方，
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
    /// 叠加而不是覆盖：整个写回去会把窗口原有的拓展样式（分层、RTL、拖边框…）抹掉，
    /// 这里只加位、一个都不清。
    /// </summary>
    [Test]
    public void 只加位不清位()
    {
        const long 别的位 = 0x0004_0000L;    // 一个不相干的位，模拟窗口已有的样式

        long after = OverlayWindowStyles.WithOverlayStyles(别的位);

        Assert.Multiple(() =>
        {
            Assert.That(after & 别的位, Is.Not.Zero, "把窗口原有的拓展样式抹掉了 —— 会连带改掉别的行为");
            Assert.That(after, Is.EqualTo(别的位 | OverlayWindowStyles.Flags));
        });
    }

    /// <summary>
    /// 重复叠是幂等的：样式要等窗口显示之后才设得上，而窗口一场演奏显示一遍，
    /// <c>Apply</c> 会被调多次。
    /// </summary>
    [Test]
    public void 叠两次和叠一次一样()
    {
        long once = OverlayWindowStyles.WithOverlayStyles(0);

        Assert.That(OverlayWindowStyles.WithOverlayStyles(once), Is.EqualTo(once));
    }
}
