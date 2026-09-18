using System.Runtime.InteropServices;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 真把按键发进系统的那一层。<see cref="IEventSink"/> 的实现，全是 Win32，一行逻辑都没有。
///
/// <b>用扫描码，不用虚拟键码。</b> <c>KEYEVENTF_SCANCODE</c> + <c>wVk = 0</c>：
/// 虚拟键码是「哪个键」，扫描码是「键盘上哪个位置」，游戏读的通常是后者。
/// 而且虚拟键码会跟着键盘布局走 —— 在中文输入法或非美式布局下，同一个虚拟键码
/// 落到游戏里的位置可能根本不是我们想要的那个。扫描码没这个问题。
///
/// <b>三个鼠标键只按不点。</b> 它们是口琴的八度/半音修饰键（见 <see cref="NoteMapper"/>），
/// 全程 <c>dx = dy = 0</c>，光标不动、不会点到界面上的任何东西。
///
/// 网关是 spec 里**明确不测**的那一类（缝开在它上面就是为了让它尽可能薄），
/// 所以这个类里不要长业务判断 —— 那部分归 <c>Dispatcher</c>。
/// </summary>
public sealed class InputSender : IEventSink
{
    // ---- INPUT 的 type ----
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    // ---- 键盘事件标志 ----
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // ---- 鼠标事件标志 ----
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    /// <summary>
    /// 联合体 <c>INPUT</c> 的字节数。**64 位下必须是 40**。
    ///
    /// 联合体在 64 位下要从偏移 8 起（<c>type</c> 是个 DWORD，后面跟 4 字节对齐填充），
    /// 而 <c>MOUSEINPUT</c> 自己因为末尾的 <c>dwExtraInfo</c> 是 8 字节指针、要按 8 对齐，
    /// 净占 32 字节 —— 8 + 32 = 40。这里少算一个字段，<see cref="SendInput"/> 会静默地
    /// 一个事件都不发（它按 <c>cbSize</c> 校验），演奏时表现为「点了开始，什么也没发生」。
    ///
    /// 故意做成公开的：布局这种东西只有比出来才作数，测试和 11 的 exe 自检都拿它当判据。
    /// </summary>
    public static int InputStructSize => Marshal.SizeOf<INPUT>();

    /// <summary>
    /// 静态构造里就把布局验一遍（<b>一次性、早失败</b>）。
    ///
    /// 放在运行时第一行而不是只写在测试里，是因为这个错误在测试里和发布产物里是同一个错误，
    /// 而发布产物跑不了测试（裁剪会改变行为）。x64 之外的平台直接抛，比发不出按键强。
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

    /// <summary>发出一个物理事件：音键按/抬，或鼠标左中右某个键的按/抬。</summary>
    public void Send(EventBuilder.PhysicalEvent e)
    {
        var batch = new INPUT[1];
        batch[0] = e.Kind == EventBuilder.K_Key ? Keyboard(e.Code, e.Down) : Mouse(e.Kind, e.Down);
        SendAll(batch);
    }

    /// <summary>
    /// **无条件**把所有可能按着的键松开：<c>Z X C V B N M</c>、逗号，以及鼠标左中右。
    ///
    /// 刻意不「记住自己按过哪些」：上一轮中断（急停、看门狗、甚至程序崩溃）留下的残留，
    /// 我们这边是不知道的，而游戏那边是实打实按着的。要的是把游戏侧清干净，不是跟自己记账。
    /// 多发几个抬键的代价是零（本来就没按着的键，抬起它什么也不会发生）。
    ///
    /// 一次 <see cref="SendInput"/> 全发出去，而不是循环发十一次：中间被别的输入插队的窗口小得多。
    /// </summary>
    public void ReleaseAll()
    {
        var keys = PlayKeys.Keys;
        // 七个音键 + 逗号 + 鼠标左中右三个 = 11 个抬键
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
        // 返回值是「真正插进去的事件数」，被 UIPI 拦下（前台窗口权限比我们高）时会是 0。
        // 这里不看它：权限自检是 06 的事，而且此刻已经晚了 —— 该做的是让调用方事先别开始。
        SendInput((uint)batch.Length, batch, InputStructSize);

    /// <summary>一个音键的按下或抬起。</summary>
    private static INPUT Keyboard(char key, bool down)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                // wVk 留 0，让系统按扫描码去认键 —— 见类注释。
                wVk = 0,
                wScan = ScancodeOf(key),
                dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP),
                // time 留 0：让系统填它自己的时间戳，我们这套时序不依赖它。
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
    }

    /// <summary>鼠标某个键的按下或抬起。位移恒为 0 —— 见类注释。</summary>
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
    /// 键位 → 扫描码（Set 1 / make code）。
    ///
    /// 这张表就是 <see cref="PlayKeys"/> 那八个键的扫描码，顺序一一对应：
    /// <c>Z X C V B N M</c> 是同一排上连着的七个物理键，逗号紧挨着 M。
    /// 认不出的键直接抛：事件表里只可能出现这八个，出现别的说明上游错了，
    /// 而「悄悄漏掉一个音」是演奏时最难查的那种毛病。
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
    /// Win32 的 <c>INPUT</c>：一个 DWORD 的 type，后面跟一个联合体。
    ///
    /// 用 <see cref="LayoutKind.Explicit"/> + <see cref="FieldOffsetAttribute"/> 而不是继承/装箱，
    /// 是因为要的就是「同一块内存，两种读法」这个布局本身 —— <c>SendInput</c> 是照着字节读的。
    /// 联合体的偏移写死 8：这是 win-x64 的布局（<c>type</c> 4 字节 + 4 字节对齐填充），
    /// 本程序只发 win-x64（见 spec 的构建一节），静态构造里那道尺寸检查会拦住别的平台。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
}
