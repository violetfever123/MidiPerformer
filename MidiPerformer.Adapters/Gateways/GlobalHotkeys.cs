using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 全局急停热键：**F6**。用 <c>WH_KEYBOARD_LL</c> 低层钩子，因为演奏时前台窗口是游戏，
/// 我们的窗口一个消息都收不到 —— 只有装到系统这一层的钩子才看得见那次按键。
///
/// <b>必须滤掉自己发的键</b>：程序往系统里发的按键自己也会过一遍钩子，
/// 不滤的话「发一个 Z → 钩子看见 → 当成急停」这种事迟早会发生（尤其哪天把急停键
/// 改成字母键的时候）。判据是 <c>KBDLLHOOKSTRUCT.flags</c> 上的 <c>LLKHF_INJECTED</c> ——
/// <c>SendInput</c> 注入的事件一定带这一位，真人敲的键盘不带。
///
/// <b>回调里只投递，不干活</b>：低层钩子回调有很紧的超时预算，超了系统直接摘掉钩子
/// （表现为「按了一次之后 F6 就再也不灵了」，而且不会有任何报错）。所以回调里只做
/// 一次读结构体 + 一次投递，真正的停止逻辑在别处跑。
///
/// <b>装在有消息循环的线程上</b>：低层钩子的回调是靠那个线程的消息泵送进来的。
/// 界面线程正好有消息泵，所以由窗口在 <c>OnOpened</c> 里 <see cref="Install"/>，
/// 在 <c>OnClosed</c> 里 <see cref="Dispose"/> —— 装卸都在同一个线程上。
///
/// 这是个**网关**：用例层根本不知道它存在，它也没有对应的端口（见 spec「端口与网关是两样东西」）。
/// 它是 spec 里明确不测的那一类，所以这里不造测试，把行为写清楚就行。
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>F6。</summary>
    private const uint VK_F6 = 0x75;

    /// <summary>事件是别的程序（或我们自己）用 SendInput / keybd_event 注入的。</summary>
    private const uint LLKHF_INJECTED = 0x0000_0010;

    /// <summary>
    /// 钩子回调的委托**必须有一个字段存着**。这是 P/Invoke 回调最经典的坑：
    /// 只传一个临时 lambda 进去，GC 一收，钩子就指向一块野内存，之后按 F6 直接崩进程。
    /// </summary>
    private readonly HookProc _proc;

    /// <summary>装钩子那个线程的同步上下文。投递用它，好让订阅方回到界面线程上。</summary>
    private readonly SynchronizationContext? _context;

    private IntPtr _hook = IntPtr.Zero;

    /// <summary>F6 被按下（且不是程序自己注入的）时触发。回调不在界面线程上，见类注释。</summary>
    public event Action? Panic;

    public GlobalHotkeys()
    {
        _proc = Callback;
        _context = SynchronizationContext.Current;
    }

    /// <summary>钩子是否已经装上。</summary>
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>
    /// 装钩子。<b>必须在有消息循环的线程上调用</b>（界面线程），重复调用只有第一次生效。
    /// </summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;

        // 低层钩子的回调在本进程里执行，所以模块句柄取本模块即可（传 IntPtr.Zero 也行，
        // 但显式给一个更保险：hMod 为空的语义在各版本 Windows 上并不完全一致）。
        IntPtr module = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

        if (_hook == IntPtr.Zero)
        {
            // 装不上就装不上：演奏照样能开始，只是少了 F6 这条急停。界面上的急停按钮还在，
            // 看门狗也还在。这里不抛，是因为把整个窗口打不开的代价比少一个热键大得多。
            // 具体原因（多半是被安全软件拦了）由调用方在界面上提示，见 PerformerWindow。
        }
    }

    /// <summary>摘钩子。没装上时无操作。理想情况下在装它的那个线程上调（窗口关闭时就是）。</summary>
    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 是系统说「这个事件别你管，直接传下去」。
        // WM_SYSKEYDOWN 也算：按住 Alt 之类再按 F6 时来的是它 —— 用户按急停的意图是一样的。
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (data.vkCode == VK_F6 && (data.flags & LLKHF_INJECTED) == 0)
                Deliver();
        }

        // 一律往下传，**不吞这个键**。吞了会顺手改掉游戏里 F6 的行为，而急停只需要知道它被按了。
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 把「急停」这件事投出去，立刻返回。
    ///
    /// 优先投到装钩子时那个同步上下文（界面线程）—— 订阅方是窗口，回到界面线程才对它安全。
    /// 拿不到上下文（装了钩子却没有同步上下文的场合）就丢给线程池：
    /// 无论如何都不能在这个回调里直接调订阅方，那等于把可能很重的活放进钩子回调里。
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
