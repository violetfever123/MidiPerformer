using System.Runtime.InteropServices;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 输入法状态自检：现在这个输入法会不会把按键吃掉。<b>由界面直接调，不经过端口</b> ——
/// 和 <see cref="InputSender.CheckElevation"/> 一样，它是「按开始之前问一句」的事，
/// 用例层只需要一个 bool（见 <c>PerformancePreflight</c>）。
///
/// <b>为什么要问这一句</b>：中文输入法在中文态下会把字母键拿去做候选字，按键到不了游戏里。
/// 演奏时的表现是整段整段地漏音，而程序这边一切正常 —— 没有任何报错可查。
///
/// <b>判据是两级，缺一不可：</b>
/// <list type="number">
/// <item><b>键盘布局是不是中文</b>（<c>GetKeyboardLayout</c> 的语言 ID）。不是中文布局就到此为止 ——
///   日文、韩文输入法同样会吃键，但那不是本程序承诺要挡的东西（spec 只提中文），
///   多加一条只会让用户在自己没预期的地方被拦下来。</item>
/// <item><b>中文布局下再看 IME 的转换模式</b>（<c>IMC_GETCONVERSIONMODE</c> 的
///   <c>IME_CMODE_NATIVE</c> 位）。<b>这一级是必须的</b>：中文输入法自己有「中文态 / 英文态」两档，
///   按一下 Shift 切到英文态之后按键是直接过去的。只看布局的话，这种完全正常的设置会被判成中文态，
///   用户按了半天「开始」没反应、还以为是程序坏了。<c>IMC_GETOPENSTATUS</c>（开/关）也挡不住这一点 ——
///   Win10 起微软拼音切英文改的是**转换模式**，IME 根本没关闭。</item>
/// </list>
///
/// <b>代价，说清楚：</b>
/// <list type="bullet">
/// <item>窗口句柄取的是<b>本线程的活动窗口</b>（就是我们自己的窗口），从不问系统「现在谁在前台」——
///   不查前台窗口是 spec 的安全边界。代价是：用户切到游戏之后再按 Shift 切态，要等切回来才发现。
///   这是有意的取舍，不是疏漏。</item>
/// <item><c>SendMessage</c> 是往 IME 窗口发跨进程消息，理论上存在与 IME 互相等待的风险，
///   所以只允许在按下「开始」那一刻调一次，**绝不许放进任何循环或定时器里**。</item>
/// <item>问不出结果时按「是中文」处理（严的一侧）。误报的代价是用户多按一次、看到一句中文说明；
///   漏报的代价是演奏中静默漏音，用户对着一局不完整的曲子找不出原因。两个代价不对称，所以往严的一侧倒。</item>
/// </list>
///
/// 这是个**网关**：用例层不知道它存在，它也没有对应的端口。网关是 spec 里明确不测的那一类，
/// 所以这里不造测试，把行为和代价写清楚就行。
/// </summary>
public static class InputMethod
{
    /// <summary><c>WM_IME_CONTROL</c>：往 IME 窗口发的控制消息。</summary>
    private const int WM_IME_CONTROL = 0x0283;

    /// <summary><c>IMC_GETCONVERSIONMODE</c>：取当前的转换模式，返回值就是模式位。</summary>
    /// <remarks>
    /// **是 0x0001**。挨着的 <c>0x0005</c> 是 <c>IMC_GETOPENSTATUS</c>（开/关），
    /// 写成它这整条判据就废了：开/关只回 0 或 1，而 <c>1 &amp; IME_CMODE_NATIVE</c> 恒为真，
    /// 于是中文布局下**恒判中文**，连「按 Shift 切到英文态」这个类注释点名要放行的情形也一起误杀。
    /// 两个常量编号相邻、名字又像，是这一块最容易写错的一处。
    /// </remarks>
    private const int IMC_GETCONVERSIONMODE = 0x0001;

    /// <summary><c>IME_CMODE_NATIVE</c>：处于「母语」态 —— 中文输入法下就是中文态，字母键会被拿去做候选字。</summary>
    private const int IME_CMODE_NATIVE = 0x0001;

    /// <summary>语言 ID 里的主语言字段（低 10 位）。</summary>
    private const int PrimaryLanguageMask = 0x03FF;

    /// <summary><c>LANG_CHINESE</c>。用它而不是逐个列举 0x0804 / 0x0404 / 0x0C04 那几个子语言。</summary>
    private const int LangChinese = 0x04;

    /// <summary>
    /// 输入法现在会不会吃键（中文布局 + 中文态）。见类注释里的判据与代价。
    /// </summary>
    public static bool IsChineseActive()
    {
        // 参数 0 = 本线程。用户是在我们自己的窗口上点「开始」，本线程的布局就是此刻生效的那个。
        int languageId = (int)(GetKeyboardLayout(0).ToInt64() & 0xFFFF);
        if ((languageId & PrimaryLanguageMask) != LangChinese) return false;

        // 从零起：拿不到窗口句柄时 ToIntPtr 会抛，而不是静默给一个 0 句柄去问系统。
        IntPtr imeWindow = ImmGetDefaultIMEWnd(GetActiveWindow());
        if (imeWindow == IntPtr.Zero) return true;

        int mode = unchecked((int)SendMessage(imeWindow, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, IntPtr.Zero).ToInt64());

        // 模式是 0 = 这个 IME 答不上来（老式 IME、或查询被拒）。答不上来算「是中文」—— 见类注释。
        if (mode == 0) return true;

        return (mode & IME_CMODE_NATIVE) != 0;
    }

    /// <summary><c>GetActiveWindow</c> 取的是**本线程**的活动窗口，不是系统的前台窗口 —— 见类注释。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
