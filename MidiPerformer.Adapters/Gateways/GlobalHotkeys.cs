using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 全局急停热键 F6：用 <c>WH_KEYBOARD_LL</c> 低层钩子，因为演奏时前台窗口是游戏，本程序收不到按键消息。
/// 回调只投递事件、不干活，并滤掉带 <c>LLKHF_INJECTED</c> 的注入键；装在有消息循环的界面线程上。
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>F6 虚拟键码。</summary>
    private const uint VK_F6 = 0x75;

    /// <summary>事件由 SendInput / keybd_event 注入。</summary>
    private const uint LLKHF_INJECTED = 0x0000_0010;

    /// <summary>钩子回调委托必须由字段持有，否则 GC 回收后钩子指向野内存。</summary>
    private readonly HookProc _proc;

    /// <summary>装钩子那个线程的同步上下文，用来把事件投递回界面线程。</summary>
    private readonly SynchronizationContext? _context;

    private IntPtr _hook = IntPtr.Zero;

    /// <summary>F6 被按下（且非程序自己注入）时触发；回调不在界面线程上。</summary>
    public event Action? Panic;

    public GlobalHotkeys()
    {
        _proc = Callback;
        _context = SynchronizationContext.Current;
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
        // nCode < 0 表示系统要求直接传递该事件；WM_SYSKEYDOWN（如按住 Alt 再按 F6）同样算急停。
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (data.vkCode == VK_F6 && (data.flags & LLKHF_INJECTED) == 0)
                Deliver();
        }

        // 一律往下传，不吞这个键，否则会改掉游戏里 F6 的行为。
        return CallNextHookEx(_hook, nCode, wParam, lParam);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
