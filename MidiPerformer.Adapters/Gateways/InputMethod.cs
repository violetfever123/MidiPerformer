using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 输入法状态自检：判断当前输入法会不会吃掉按键（中文布局且处于中文态），由界面在按下「开始」前直接调用，
/// 用例层只拿到一个 bool（见 <c>PerformancePreflight</c>）。
/// 判据两级：<c>GetKeyboardLayout</c> 的语言 ID 是中文，且 <c>IMC_GETCONVERSIONMODE</c> 的模式带 <c>IME_CMODE_NATIVE</c> 位。
/// 句柄取本线程的活动窗口，<c>SendMessage</c> 只调一次，问不出结果时按「是中文」处理（严的一侧）。
/// </summary>
public static class InputMethod
{
    /// <summary><c>WM_IME_CONTROL</c>：发往 IME 窗口的控制消息。</summary>
    private const int WM_IME_CONTROL = 0x0283;

    /// <summary><c>IMC_GETCONVERSIONMODE</c>：取当前转换模式，返回值即模式位；不能误用相邻的 <c>IMC_GETOPENSTATUS</c>（0x0005）。</summary>
    private const int IMC_GETCONVERSIONMODE = 0x0001;

    /// <summary><c>IME_CMODE_NATIVE</c>：母语态，中文输入法下字母键会被拿去做候选字。</summary>
    private const int IME_CMODE_NATIVE = 0x0001;

    /// <summary>语言 ID 的主语言字段（低 10 位）。</summary>
    private const int PrimaryLanguageMask = 0x03FF;

    /// <summary><c>LANG_CHINESE</c> 主语言值。</summary>
    private const int LangChinese = 0x04;

    /// <summary>
    /// 当前是不是「中文布局 + 中文态」，即输入法会不会吃键。
    /// </summary>
    public static bool IsChineseActive()
    {
        // 参数 0 = 本线程，用户是在我们自己的窗口上点「开始」，本线程的布局就是此刻生效的那个。
        int languageId = (int)(GetKeyboardLayout(0).ToInt64() & 0xFFFF);
        if ((languageId & PrimaryLanguageMask) != LangChinese) return false;

        // 拿不到 IME 窗口句柄时按「是中文」处理（严的一侧）。
        IntPtr imeWindow = ImmGetDefaultIMEWnd(GetActiveWindow());
        if (imeWindow == IntPtr.Zero) return true;

        int mode = unchecked((int)SendMessage(imeWindow, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, IntPtr.Zero).ToInt64());

        // 模式为 0 表示这个 IME 答不上来（老式 IME 或查询被拒），按「是中文」处理。
        if (mode == 0) return true;

        return (mode & IME_CMODE_NATIVE) != 0;
    }

    /// <summary><c>GetActiveWindow</c> 取的是本线程的活动窗口，不是系统的前台窗口。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
