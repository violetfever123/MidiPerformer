using System.Runtime.InteropServices;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 真把按键发进系统的那一层，<see cref="IEventSink"/> 的实现，全是 Win32，不含业务判断。
/// 键盘事件用扫描码而不是虚拟键码（<c>KEYEVENTF_SCANCODE</c> + <c>wVk = 0</c>）：游戏读的是键位，虚拟键码还会跟着键盘布局走。
/// 三个鼠标键只按不点（见 <see cref="NoteMapper"/>），全程 <c>dx = dy = 0</c>，光标不动。
/// </summary>
public sealed class InputSender : IEventSink
{
    // ---- INPUT 的 type ----
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    // ---- 键盘事件标志 ----
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // ---- 权限自检（OpenProcessToken + GetTokenInformation）----
    private const uint TOKEN_QUERY = 0x0008;

    /// <summary><c>TOKEN_INFORMATION_CLASS.TokenElevation</c> 的值，不在 BCL 里，只能写死。</summary>
    private const int TokenElevationClass = 20;

    // ---- 鼠标事件标志 ----
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    /// <summary>
    /// 联合体 <c>INPUT</c> 的字节数，64 位下必须是 40：<c>type</c> 占 4 字节加 4 字节对齐填充，再跟上净占 32 字节的 <c>MOUSEINPUT</c>。
    /// 尺寸不对时 <see cref="SendInput"/> 会按 <c>cbSize</c> 校验后静默地一个事件都不发。公开给测试与 exe 自检当判据。
    /// </summary>
    public static int InputStructSize => Marshal.SizeOf<INPUT>();

    /// <summary>
    /// 静态构造里就把布局验一遍，早失败：这个错误在测试与发布产物里是同一个，而发布产物跑不了测试；只在 x64 下尺寸不为 40 时抛。
    /// </summary>
    static InputSender()
    {
        int size = Marshal.SizeOf<INPUT>();
        if (IntPtr.Size == 8 && size != 40)
        {
            throw new InvalidOperationException(
                $"INPUT 联合体的布局不对：64 位下应当是 40 字节，实际是 {size} 字节。"
                + "SendInput 会因为这个尺寸被拒而静默地什么都不发。");
        }
    }

    /// <summary>
    /// 本进程是不是以管理员身份在跑，由界面直接调用（<c>IEventSink</c> 只声明 <see cref="Send"/> 和 <see cref="ReleaseAll"/>）。
    /// 判据是进程令牌的提权位 <c>TokenElevation</c>：UIPI 会把低权限进程发往高权限窗口的输入整批丢掉，而游戏多半是提权跑的；查不到就当作没提权。
    /// </summary>
    public static bool CheckElevation()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;

        try
        {
            int size = Marshal.SizeOf<TOKEN_ELEVATION>();
            if (!GetTokenInformation(token, TokenElevationClass, out TOKEN_ELEVATION elevation, size, out _))
                return false;

            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            // 令牌句柄是内核对象，泄漏它不像托管内存那样会被 GC 收掉。
            CloseHandle(token);
        }
    }

    /// <summary>发出一个物理事件：音键按/抬，或鼠标左中右某个键的按/抬。</summary>
    public void Send(EventBuilder.PhysicalEvent e)
    {
        var batch = new INPUT[1];
        batch[0] = e.Kind == EventBuilder.K_Key ? Keyboard(e.Code, e.Down) : Mouse(e.Kind, e.Down);
        SendAll(batch);
    }

    /// <summary>
    /// 无条件把所有可能按着的键松开：七个音键、逗号，以及鼠标左中右 —— 不记「自己按过哪些」，因为急停、看门狗甚至崩溃
    /// 留下的残留我们这边不知道，游戏那边却是实打实按着的。11 个抬键一次 <see cref="SendInput"/> 发完，被别的输入插队的窗口小得多。
    /// </summary>
    public void ReleaseAll()
    {
        var keys = PlayKeys.Keys;
        var batch = new INPUT[keys.Length + 1 + 3];

        int n = 0;
        foreach (char k in keys) batch[n++] = Keyboard(k, down: false);
        batch[n++] = Keyboard(PlayKeys.TopKey, down: false);

        // 鼠标三键对应三个八度档位（低/高）与升半音，见 NoteMapper。
        batch[n++] = Mouse(EventBuilder.K_MouseLeft, down: false);
        batch[n++] = Mouse(EventBuilder.K_MouseRight, down: false);
        batch[n++] = Mouse(EventBuilder.K_MouseMiddle, down: false);

        SendAll(batch);
    }

    private static void SendAll(INPUT[] batch) =>
        // 返回值是真正插进去的事件数，被 UIPI 拦下（前台窗口权限比我们高）时是 0；这里不检查它。
        SendInput((uint)batch.Length, batch, InputStructSize);

    /// <summary>一个音键的按下或抬起。</summary>
    private static INPUT Keyboard(char key, bool down)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                // wVk 留 0，让系统按扫描码去认键。
                wVk = 0,
                wScan = ScancodeOf(key),
                dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP),
                // time 留 0，让系统填它自己的时间戳。
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
    }

    /// <summary>鼠标某个键的按下或抬起；位移恒为 0，光标不动。</summary>
    private static INPUT Mouse(int kind, bool down)
    {
        uint flag = kind switch
        {
            EventBuilder.K_MouseLeft => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            EventBuilder.K_MouseRight => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            EventBuilder.K_MouseMiddle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是鼠标事件")
        };

        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = 0,
                dwFlags = flag,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
    }

    /// <summary>
    /// 键位 → 扫描码（Set 1 / make code）。表与 <see cref="PlayKeys"/> 的八个键一一对应（<c>Z X C V B N M</c> 同一排相连，逗号紧挨 M）。
    /// 认不出的键直接抛，因为事件表里只可能出现这八个。
    /// </summary>
    private static ushort ScancodeOf(char key) => key switch
    {
        'Z' => 0x2C,
        'X' => 0x2D,
        'C' => 0x2E,
        'V' => 0x2F,
        'B' => 0x30,
        'N' => 0x31,
        'M' => 0x32,
        ',' => 0x33,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "不是口琴的键位")
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    /// <summary><c>TokenElevation</c> 只回一个 DWORD：提权了就是 1，否则 0。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, out TOKEN_ELEVATION information, int length, out int returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Win32 的 <c>INPUT</c>：一个 DWORD 的 type 后面跟一个联合体。用 <see cref="LayoutKind.Explicit"/> + <see cref="FieldOffsetAttribute"/>
    /// 要的就是「同一块内存，两种读法」这个布局本身（<c>SendInput</c> 照着字节读）；联合体偏移 8 是 win-x64 的布局。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
}
