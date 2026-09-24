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
    /// 一场演奏的生命周期。手上的事实只有两件 —— 在不在跑、倒计时还剩几秒 ——
    /// 阶段由 <see cref="GlobalHotkeys.StageOf"/> 现推，和演奏器窗口交给钩子的是同一个函数
    /// （<c>PerformerWindow.CurrentStage</c>）。判定是纯函数，所以这个状态机不用真起一场演奏。
    /// </summary>
    private sealed class 一场演奏
    {
        private bool _在跑;
        private int _倒计时;

        public PerformanceStage Stage => GlobalHotkeys.StageOf(_在跑, _倒计时);

        /// <summary>按下开始：进倒计时，还剩 <paramref name="秒"/> 秒。</summary>
        public void 按下开始(int 秒)
        {
            _在跑 = true;
            _倒计时 = 秒;
        }

        /// <summary>时钟走一秒。</summary>
        public void 过一秒() => _倒计时 = Math.Max(0, _倒计时 - 1);

        /// <summary>倒计时归零（等于把剩下的秒数一次走完）。</summary>
        public void 倒计时走完() => _倒计时 = 0;

        public void 停完收尾()
        {
            _在跑 = false;
            _倒计时 = 0;
        }

        /// <summary>此刻按下这一颗键，停不停。</summary>
        public bool 按键(uint vkCode, bool injected = false, bool modifierHeld = false)
            => GlobalHotkeys.ShouldStop(Stage, vkCode, injected, modifierHeld);
    }

    /// <summary>
    /// 阶段就是那两件事推出来的，边界在「还剩 1 秒」和「归零」之间。
    /// 这一段单拎出来测：它是判定的**输入**，喂错了的话纯函数再对也白搭。
    /// </summary>
    [Test]
    public void 阶段由在不在跑和倒计时剩几秒推出来()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalHotkeys.StageOf(running: false, countdownSecondsLeft: 0),
                Is.EqualTo(PerformanceStage.Idle), "没在跑 = 空闲");
            Assert.That(GlobalHotkeys.StageOf(running: false, countdownSecondsLeft: 5),
                Is.EqualTo(PerformanceStage.Idle), "已经停了之后那个剩余秒数就没有意义了，别拿它当「在倒计时」");
            Assert.That(GlobalHotkeys.StageOf(running: true, countdownSecondsLeft: 5),
                Is.EqualTo(PerformanceStage.Countdown), "在跑 + 还有秒数 = 倒计时那一段");
            Assert.That(GlobalHotkeys.StageOf(running: true, countdownSecondsLeft: 1),
                Is.EqualTo(PerformanceStage.Countdown), "还剩 1 秒也还是倒计时");
            Assert.That(GlobalHotkeys.StageOf(running: true, countdownSecondsLeft: 0),
                Is.EqualTo(PerformanceStage.Playing), "在跑 + 归零 = 演奏中");
        });
    }

    /// <summary>
    /// 阶段门的状态机测试：**先倒计时后演奏**，同一颗键走完整场。
    ///
    /// 倒计时 5 秒一秒一秒走完，重点在边界那一秒 —— 判定必须正好在「还剩 1 秒 → 归零」
    /// 之间翻面：早一秒是「用户在切窗口时自己把演奏掐了」，晚一秒是「按了没反应」，
    /// 两个方向都是这张票要修的毛病。
    /// </summary>
    [Test]
    public void 倒计时走完那一下判定正好翻面()
    {
        var 演奏 = new 一场演奏();
        演奏.按下开始(5);

        var 走过的阶段 = new List<PerformanceStage>();
        var 停不停 = new List<bool>();

        for (int i = 0; i < 6; i++)     // 还剩 5 / 4 / 3 / 2 / 1 秒，走完第 5 次就归零
        {
            走过的阶段.Add(演奏.Stage);
            停不停.Add(演奏.按键(A));
            if (i < 5) 演奏.过一秒();
        }

        Assert.Multiple(() =>
        {
            Assert.That(走过的阶段.Take(5), Is.All.EqualTo(PerformanceStage.Countdown),
                "倒计时那 5 秒里阶段不该动");
            Assert.That(走过的阶段[5], Is.EqualTo(PerformanceStage.Playing),
                "剩下的一秒走完就该进演奏那一段");
            Assert.That(停不停.Take(5), Is.All.False,
                "倒计时里按的那 5 下一下都不许停 —— 那几秒正是用户在切窗口的时间");
            Assert.That(停不停[5], Is.True, "归零之后同一颗键必须停");
        });
    }

    /// <summary>
    /// <b>同一个键</b>在倒计时期间不停、进入演奏后停 —— 空闲那一段也一起验了。
    /// </summary>
    [Test]
    public void 倒计时期间同一个键不停进入演奏后停()
    {
        var 演奏 = new 一场演奏();

        bool 空闲时 = 演奏.按键(A);
        演奏.按下开始(5);
        bool 倒计时时 = 演奏.按键(A);
        演奏.倒计时走完();
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
        演奏.按下开始(5);

        foreach (var vk in new[] { A, Space, Enter, F6 })
            演奏.按键(vk);

        演奏.按键(Alt);
        演奏.按键(Tab, modifierHeld: true);

        演奏.倒计时走完();

        Assert.That(演奏.按键(A), Is.True, "倒计时里按过的那几下把演奏中的判定带偏了");
    }

    /// <summary>
    /// 停完回到空闲，再按不该再触发一次（窗口上「急停」那颗按钮已经变灰、状态行也回就绪了）。
    /// </summary>
    [Test]
    public void 停完回到空闲就不再认键()
    {
        var 演奏 = new 一场演奏();
        演奏.按下开始(5);
        演奏.倒计时走完();

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
