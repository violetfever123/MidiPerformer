using MidiPerformer.Adapters.Gateways;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 急停判定：演奏开始之后，按键盘上任意一个非修饰键就停（F6 不再特殊）。
///
/// 这一整块是从钩子里提纯出来的（<see cref="GlobalHotkeys.ShouldStop"/>），所以它脱得开 Win32：
/// 不用装钩子、不用真按键、不用桌面会话。钩子那边只剩「取键 + 调用」——
/// 也就是说这些断言绿了，急停的口径就是对的，剩下唯一没被这里盖住的是
/// 「钩子有没有把那四个入参取对」，那条归实机（见 48 的验收记录）。
/// </summary>
public class PanicKeyTests
{
    // 常用虚拟键码。写名字不是为了短，是为了让表好读：一堆 0x41 看不出哪个是哪个。
    private const uint Tab = 0x09, Enter = 0x0D;
    private const uint Shift = 0x10, Ctrl = 0x11, Alt = 0x12;
    private const uint Space = 0x20;
    private const uint Digit0 = 0x30, Digit9 = 0x39;
    private const uint A = 0x41, M = 0x4D, Z = 0x5A;
    private const uint LWin = 0x5B, RWin = 0x5C;
    private const uint F6 = 0x75;
    private const uint LShift = 0xA0, RShift = 0xA1;
    private const uint LCtrl = 0xA2, RCtrl = 0xA3;
    private const uint LAlt = 0xA4, RAlt = 0xA5;

    /// <summary>最常见的那一种：演奏中、用户自己按的、手上没按着修饰键。</summary>
    private static bool 演奏中按了(uint vkCode)
        => GlobalHotkeys.ShouldStop(PerformanceStage.Playing, vkCode, injected: false, modifierHeld: false);

    // ==================== 停 ====================

    /// <summary>
    /// 演奏中按下这些键都该停。F6 列在表里不是特例 —— 它和 A 走的是同一条判定，
    /// 谁也没有属于自己的分支（这是「不再为 F6 留特殊处理」那句话的判据）。
    /// </summary>
    [Test]
    public void 字母数字空格回车和F6都停()
    {
        var 表 = new (uint Vk, string 名字)[]
        {
            (A, "字母 A"), (M, "字母 M"), (Z, "字母 Z"),
            (Digit0, "数字 0"), (Digit9, "数字 9"),
            (Space, "空格"), (Enter, "回车"),
            (F6, "F6"),
        };

        Assert.Multiple(() =>
        {
            foreach (var (vk, 名字) in 表)
                Assert.That(演奏中按了(vk), Is.True, $"演奏中按{名字}（0x{vk:X2}）没停");
        });
    }

    // ==================== 不停（一）：单按修饰键 ====================

    /// <summary>
    /// 单按这些键不算急停。前五个是取舍 (c) 点名的那五个；
    /// 左右两侧各自的键码也一并钉住 —— 低层钩子报的是 L/R 那个具体码，
    /// 只挡通用码的话，真按下左 Shift 是挡不住的。
    /// </summary>
    [Test]
    public void 单按修饰键不停()
    {
        var 表 = new (uint Vk, string 名字)[]
        {
            (Alt, "Alt"), (Tab, "Tab"), (Ctrl, "Ctrl"), (Shift, "Shift"),
            (LWin, "左 Win"), (RWin, "右 Win"),
            (LShift, "左 Shift"), (RShift, "右 Shift"),
            (LCtrl, "左 Ctrl"), (RCtrl, "右 Ctrl"),
            (LAlt, "左 Alt"), (RAlt, "右 Alt"),
        };

        Assert.Multiple(() =>
        {
            foreach (var (vk, 名字) in 表)
                Assert.That(演奏中按了(vk), Is.False, $"单按{名字}（0x{vk:X2}）就停了 —— 修饰键本身就是键");
        });
    }

    // ==================== 不停（二）：Alt+Tab 那一类组合 ====================

    /// <summary>
    /// <b>Alt+Tab 停不住。</b>这一条不并进上面那张表里，单独写、两半都显式钉住：
    /// 用户按下开始之后干的第一件事就是切到游戏窗口，而 Alt 和 Tab 本身就是键 ——
    /// 组合键要是算「落下一个非修饰键」，他切一次窗口，整场演奏就没了。
    /// </summary>
    [Test]
    public void Alt加Tab切窗口那一下不停()
    {
        Assert.Multiple(() =>
        {
            Assert.That(演奏中按了(Alt), Is.False, "Alt 落下的那一下就不该停");

            Assert.That(
                GlobalHotkeys.ShouldStop(PerformanceStage.Playing, Tab, injected: false, modifierHeld: true),
                Is.False,
                "按住 Alt 再按 Tab = Alt+Tab：停在这儿，等于用户切一次窗口就把演奏掐了");
        });
    }

    /// <summary>
    /// 组合键的口径不止 Alt+Tab：修饰键按着的时候落下的任何键都不算。
    /// Win+D 里的 D 根本不在排除表里，能拦住它的只有「修饰键还按着」这一条 ——
    /// 所以这条得单独测，不然实现只挡 Alt+Tab 那两颗键也能全绿。
    /// </summary>
    [Test]
    public void 修饰键按着的时候落下的别的键也不算()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalHotkeys.ShouldStop(PerformanceStage.Playing, A, injected: false, modifierHeld: true),
                Is.False, "按着 Shift / Ctrl / Win 再按字母，落下的还是组合键，不是「双手离开键盘之后随手按的那一个」");

            Assert.That(
                GlobalHotkeys.ShouldStop(PerformanceStage.Playing, Digit9, injected: false, modifierHeld: true),
                Is.False, "Win+D 这类里落下的键不在排除表里，靠的就是这条");
        });
    }

    // ==================== 不停（三）：自己发出去的键 ====================

    /// <summary>
    /// 本程序自己注入的键（<c>LLKHF_INJECTED</c>）不算急停 —— 现有行为，别丢。
    /// 演奏键走的就是这条路：丢掉这个过滤，第一个音发出去就把自己停了。
    /// </summary>
    [Test]
    public void 自己发出去的键不停()
    {
        Assert.Multiple(() =>
        {
            foreach (var vk in new[] { A, Space, F6 })
                Assert.That(
                    GlobalHotkeys.ShouldStop(PerformanceStage.Playing, vk, injected: true, modifierHeld: false),
                    Is.False,
                    $"注进去的 0x{vk:X2} 把演奏停了 —— 发演奏键和按急停键是同一套 SendInput");
        });
    }

    // ==================== 阶段门：先倒计时，后演奏 ====================

    /// <summary>
    /// 一场演奏的生命周期（就判定用得着的那几段）。判定是纯函数，所以这个状态机不用真起一场演奏，
    /// 它只是把阶段按顺序喂进去 —— 变的只有「走到哪一段了」。
    /// </summary>
    private sealed class 一场演奏
    {
        public PerformanceStage Stage { get; private set; } = PerformanceStage.Idle;

        public void 按下开始() => Stage = PerformanceStage.Countdown;

        public void 倒计时结束() => Stage = PerformanceStage.Playing;

        public void 停完收尾() => Stage = PerformanceStage.Idle;

        /// <summary>此刻按下这一颗键，停不停。</summary>
        public bool 按键(uint vkCode, bool injected = false, bool modifierHeld = false)
            => GlobalHotkeys.ShouldStop(Stage, vkCode, injected, modifierHeld);
    }

    /// <summary>
    /// <b>同一个键</b>在倒计时期间不停、进入演奏后停。
    ///
    /// 写成一段走过来的过程而不是三个孤立的断言：这条要证明的是「阶段翻面，判定跟着翻面」，
    /// 单点验不出这件事 —— 三个单点全绿，也可能只是因为判定压根没读阶段。
    /// 同一颗键先否后肯，读没读阶段就没有第二种解释了。
    /// </summary>
    [Test]
    public void 倒计时期间同一个键不停进入演奏后停()
    {
        var 演奏 = new 一场演奏();

        bool 空闲时 = 演奏.按键(A);
        演奏.按下开始();
        bool 倒计时时 = 演奏.按键(A);
        演奏.倒计时结束();
        bool 演奏中 = 演奏.按键(A);

        Assert.Multiple(() =>
        {
            Assert.That(空闲时, Is.False, "还没开始就认键 —— 这个窗口一打开就在听全键盘");
            Assert.That(倒计时时, Is.False, "倒计时那几秒正是用户切窗口的时间，认了就是自己把自己掐掉");
            Assert.That(演奏中, Is.True, "阶段翻到演奏了，同一颗键必须停 —— 这一条不翻，整张票就是空的");
        });
    }

    /// <summary>
    /// 用户在倒计时里按过的那几下（切窗口按的 Alt+Tab、手搭在键盘上碰到的键）
    /// 不能把进入演奏之后的判定带偏 —— 阶段门是「此刻在哪一段」，不是一个会记住的闩。
    /// </summary>
    [Test]
    public void 倒计时里按过键不影响进入演奏后那一下()
    {
        var 演奏 = new 一场演奏();
        演奏.按下开始();

        foreach (var vk in new[] { A, Space, Enter, F6 })
            演奏.按键(vk);

        演奏.按键(Alt);
        演奏.按键(Tab, modifierHeld: true);

        演奏.倒计时结束();

        Assert.That(演奏.按键(A), Is.True, "倒计时里按过的那几下把演奏中的判定带偏了");
    }

    /// <summary>
    /// 停完回到空闲，再按不该再触发一次（窗口上「急停」那颗按钮已经变灰、状态行也回就绪了）。
    /// </summary>
    [Test]
    public void 停完回到空闲就不再认键()
    {
        var 演奏 = new 一场演奏();
        演奏.按下开始();
        演奏.倒计时结束();

        bool 停的那一下 = 演奏.按键(A);
        演奏.停完收尾();
        bool 停完再按 = 演奏.按键(A);

        Assert.Multiple(() =>
        {
            Assert.That(停的那一下, Is.True, "演奏中按 A 该停");
            Assert.That(停完再按, Is.False, "已经停了，再按一下不该再报一次急停");
        });
    }
}
