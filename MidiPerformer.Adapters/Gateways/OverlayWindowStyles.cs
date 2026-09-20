using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 悬浮层的三条窗口拓展样式：<c>WS_EX_TRANSPARENT</c> 点击穿透、<c>WS_EX_NOACTIVATE</c> 点了也不激活、
/// <c>WS_EX_TOOLWINDOW</c> 不进任务栏与 Alt+Tab。
/// Avalonia 表达不了这三条，只能到 Win32 这一层来要，界面在 <c>PerformerOverlayWindow.OnOpened</c> 里调一次。
/// </summary>
public static class OverlayWindowStyles
{
    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_TRANSPARENT = 0x0000_0020L;
    private const long WS_EX_TOOLWINDOW = 0x0000_0080L;
    private const long WS_EX_NOACTIVATE = 0x0800_0000L;

    /// <summary>
    /// 三个样式位的并集，公开出来是给测试核对常量值的（网关设没设上验不了，要的是哪几个位验得了）。
    /// </summary>
    public const long Flags = WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

    /// <summary>
    /// 在窗口已有的拓展样式上叠出我们要的那一份：只加位，不清位，免得抹掉分层、RTL 等别的位。
    /// </summary>
    public static long WithOverlayStyles(long currentExtendedStyle) => currentExtendedStyle | Flags;

    /// <summary>
    /// 给一个窗口加上「点击穿透 + 不抢焦点 + 不进任务栏」，返回是否成功；失败不抛。
    /// 必须在窗口显示之后调，句柄那时才真存在。
    /// </summary>
    public static bool Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long wanted = WithOverlayStyles(style);
        if (wanted == style) return true;              // 已经设过了，不重复一次系统调用

        // SetWindowLongPtr 的返回值是旧样式、失败时为 0，所以不看返回值，设完回读一次确认。
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(wanted));
        return (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & wanted) == wanted;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
