using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 悬浮层的窗口拓展样式。**Avalonia 表达不了这三个，只能到 Win32 这一层来要**，
/// 所以它落在网关里，界面那边只调一次（<c>PerformerOverlayWindow.OnOpened</c>）。
///
/// 三个样式各挡一件事：
/// <list type="bullet">
/// <item><c>WS_EX_TRANSPARENT</c> —— <b>点击穿透</b>。鼠标事件直接落到下面那个窗口（游戏）上，
///   悬浮层在鼠标眼里等于不存在。不给它的话，倒计时那几秒里用户点到悬浮层上，
///   点中的是一块什么都接不住的透明窗口 —— 游戏那边收不到这一下。</item>
/// <item><c>WS_EX_NOACTIVATE</c> —— <b>点了也不激活</b>。这一条是整块里最要紧的：
///   一旦悬浮层拿到焦点，后面发的按键就全发到它身上（或者发到桌面），游戏一个都收不到。
///   倒计时期间用户往游戏那边点一下、或者悬浮层自己弹出来，都可能触发激活。</item>
/// <item><c>WS_EX_TOOLWINDOW</c> —— <b>不进任务栏、不进 Alt+Tab</b>。演奏时用户要在游戏里待着，
///   切窗口的列表里冒出来一个「演奏提示」只会碍事，而且点它就会把焦点带走。</item>
/// </list>
///
/// <c>ShowActivated = false</c> 那一条在 Avalonia 侧设（见悬浮层窗口），和这里的
/// <c>WS_EX_NOACTIVATE</c> 是两件事：前者管「显示这一下不抢焦点」，后者管「之后任何一下都不抢」。
/// 两个都要。
///
/// 这是个**网关**：不测，行为写清楚就行。只发 win-x64（见 spec 的构建一节），
/// 所以直接用 <c>GetWindowLongPtr</c> 那一对 64 位入口点。
/// </summary>
public static class OverlayWindowStyles
{
    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_TRANSPARENT = 0x0000_0020L;
    private const long WS_EX_TOOLWINDOW = 0x0000_0080L;
    private const long WS_EX_NOACTIVATE = 0x0800_0000L;

    /// <summary>
    /// 给一个窗口加上「点击穿透 + 不抢焦点 + 不进任务栏」。
    ///
    /// 返回是否成功。<b>失败不抛</b>：拿不到句柄的场合（窗口还没建出来、或者调用发生得太早）
    /// 是可能发生的，而这里抛出去就是一次崩溃 —— 悬浮层少一条样式顶多是它不好用，
    /// 不该把整个演奏流程带走。调用方（窗口）自己决定要不要在状态行里说一句。
    ///
    /// <b>必须在窗口显示之后调</b>：句柄是显示时才真有的，<c>TryGetPlatformHandle()</c> 拿不到就没得设。
    /// </summary>
    public static bool Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long wanted = style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (wanted == style) return true;              // 已经设过了，不重复一次系统调用

        // 返回值是**旧的**样式，设失败时返回 0。这里不看返回值，回头读一次确认 ——
        // 「设没设上」比「调用有没有返回值」更贴近我们要问的问题（SetLastError 那套
        // 在有托管代码参与的调用栈上并不可靠）。
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(wanted));
        return (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & wanted) == wanted;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
