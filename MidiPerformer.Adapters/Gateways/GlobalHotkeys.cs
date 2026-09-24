using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 演奏走到哪一段了 —— 急停判定的「阶段门」看的就是它。
/// </summary>
public enum PerformanceStage
{
    /// <summary>还没开始，或者已经停了（包括刚停完那一下之后）。这一段一个键都不认。</summary>
    Idle,

    /// <summary>倒计时中：按下开始之后、第一个音发出去之前。<b>这一段不认键</b>。</summary>
    Countdown,

    /// <summary>演奏中：已经在往游戏里发按键了。只有这一段认「任意键急停」。</summary>
    Playing
}

/// <summary>
/// 全局急停键：<b>演奏开始之后，按键盘上任意一个非修饰键就停</b>，不用去找 F6
/// （F6 本来就落在「非修饰键」里，没有特例）。用 <c>WH_KEYBOARD_LL</c> 低层钩子，
/// 因为演奏时前台窗口是游戏，本程序收不到按键消息。
///
/// 判定本身是纯函数 <see cref="ShouldStop"/>：钩子回调只做「取键 + 调用」，
/// 所以停 / 不停这一整块脱得开 Win32 测（见 <c>MidiPerformer.Tests/Perform/PanicKeyTests.cs</c>）。
/// 回调只投递事件、不干活，并滤掉带 <c>LLKHF_INJECTED</c> 的注入键；装在有消息循环的界面线程上。
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>事件由 SendInput / keybd_event 注入 —— 那就是本程序自己发出去的那个键。</summary>
    private const uint LLKHF_INJECTED = 0x0000_0010;

    /// <summary>
    /// 排除表（判定用）：<b>单按</b>表里的键不算急停，<b>按住</b>表里的键时再按别的键也不算。
    /// 前五条是修饰键；最后一条 Tab 不是修饰键，但 Alt+Tab 切窗口那一下必须有它，所以一并排掉。
    /// 左右两侧各自的键码都要列：低层钩子报的是 L/R 那个具体码，不是通用码。
    /// </summary>
    private static readonly uint[] ExcludedKeys =
    {
        0x09,               // Tab（不是修饰键，但 Alt+Tab 里它是另一半）
        0x10, 0x11, 0x12,   // Shift / Ctrl / Alt（通用码）
        0x5B, 0x5C,         // 左 / 右 Win
        0xA0, 0xA1,         // 左 / 右 Shift
        0xA2, 0xA3,         // 左 / 右 Ctrl
        0xA4, 0xA5          // 左 / 右 Alt
    };

    /// <summary>
    /// 「此刻有没有修饰键按着」要问的那几个键。只问通用码：<c>GetAsyncKeyState</c> 对
    /// VK_SHIFT / VK_CONTROL / VK_MENU 会把左右两边一起算上，不必再问 L/R 那一对。
    /// </summary>
    private static readonly uint[] HeldKeys = { 0x09, 0x10, 0x11, 0x12, 0x5B, 0x5C };

    /// <summary>钩子回调委托必须由字段持有，否则 GC 回收后钩子指向野内存。</summary>
    private readonly HookProc _proc;

    /// <summary>装钩子那个线程的同步上下文，用来把事件投递回界面线程。</summary>
    private readonly SynchronizationContext? _context;

    private IntPtr _hook = IntPtr.Zero;

    /// <summary>
    /// 现在走到哪一段了。<b>按下那一刻现问</b>，不是装钩子时问一次 ——
    /// 倒计时一结束就得翻面，靠界面那边定时去刷会差一次 tick。
    /// 不设就是 <see cref="PerformanceStage.Idle"/>（一个键都不认）。
    /// </summary>
    public Func<PerformanceStage>? StageSource { get; set; }

    /// <summary>急停键被按下（非注入、非修饰键、且已经到了演奏那一段）时触发；回调不在界面线程上。</summary>
    public event Action? Panic;

    public GlobalHotkeys()
    {
        _proc = Callback;
        _context = SynchronizationContext.Current;
    }

    /// <summary>
    /// 这一下按键该不该急停。**纯函数**：不读字段、不碰 Win32，同样的入参永远同样的答案。
    /// </summary>
    /// <param name="stage">按下那一刻走到哪一段了。空闲与倒计时一律不停。</param>
    /// <param name="vkCode">按下的虚拟键码。</param>
    /// <param name="injected">这一下是不是注入的（本程序发的演奏键就是）。</param>
    /// <param name="modifierHeld">按下这一刻，排除表里的键是不是已经有一个按住了。</param>
    /// <remarks>
    /// 四条口径的先后就是优先级：注入的键最先出局（它连「用户按的」都不是），
    /// 然后才是阶段门，最后才是「这一下算不算修饰键 / 组合键」。
    /// </remarks>
    public static bool ShouldStop(PerformanceStage stage, uint vkCode, bool injected, bool modifierHeld)
    {
        // 自己发出去的键不算：演奏键就是这么发出去的，滤不掉的话第一个音就把自己停了。
        if (injected) return false;

        // 阶段门：倒计时那几秒正是用户切窗口的时间，认了就等于自己把自己掐掉。
        // 倒计时阶段的取消仍然只有窗口上那颗「急停」按钮。
        if (stage != PerformanceStage.Playing) return false;

        // 单按修饰键（含 Tab）不算：Alt、Tab 本身就是键，认了就等于按一下就停。
        if (Array.IndexOf(ExcludedKeys, vkCode) >= 0) return false;

        // 组合键也不算：Alt+Tab、Win+D 这些。落下来的那个键可能不在排除表里（D 就不在），
        // 但它是在修饰键按着的时候落的，不是「双手离开键盘之后随手按下的任意键」。
        if (modifierHeld) return false;

        return true;
    }

    /// <summary>钩子是否已经装上。</summary>
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>装钩子；必须在有消息循环的线程上调用，重复调用只有第一次生效。</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;

        // 低层钩子的回调在本进程内执行，模块句柄取本模块即可。
        IntPtr module = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

        if (_hook == IntPtr.Zero)
        {
            // 装钩子失败不抛：_hook 保持零，下次调用会再试一次
        }
    }

    /// <summary>摘钩子，没装上时无操作。</summary>
    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 表示系统要求直接传递该事件；WM_SYSKEYDOWN（如按住 Alt 再按 Tab）一样要判。
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // 这里只做两件事：取键（阶段 / 键码 / 注入位 / 修饰键），调判定。别的都不做。
            if (ShouldStop(
                    StageSource?.Invoke() ?? PerformanceStage.Idle,
                    data.vkCode,
                    (data.flags & LLKHF_INJECTED) != 0,
                    ModifierHeld()))
                Deliver();
        }

        // 一律往下传，不吞这个键，否则会改掉游戏里这个键的行为。
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 排除表里的键此刻有没有按住的。按下的那个键自己也算在内，不影响结论 ——
    /// 它要是在排除表里，前一条判定已经把它拦下了。
    /// </summary>
    private static bool ModifierHeld()
    {
        foreach (uint vk in HeldKeys)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;

        return false;
    }

    /// <summary>
    /// 把急停事件投出去后立刻返回：优先投到装钩子时的同步上下文（界面线程），拿不到就交给线程池。
    /// </summary>
    private void Deliver()
    {
        if (_context is { } ctx)
            ctx.Post(static state => ((GlobalHotkeys)state!).Panic?.Invoke(), this);
        else
            ThreadPool.QueueUserWorkItem(static state => ((GlobalHotkeys)state!).Panic?.Invoke(), this);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>那个键此刻按着没有：返回值最高位是 1 就是按着。</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
