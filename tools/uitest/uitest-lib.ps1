#Requires -Version 5.1
<#
.SYNOPSIS
    tools/uitest/ 下那批点击脚本共用的那一段：P/Invoke + UIA 取数 + 起窗口 / 收窗口 / 存图，
    外加**开头就抛**的本机前提检查。

.DESCRIPTION
    用法（dot-source 的那一刻前提检查就跑，不满足就抛）：

        . (Join-Path $PSScriptRoot 'uitest-lib.ps1')

    四条本机前提，一条不满足就 throw —— 没有「尽量」这一档：
      1. 有桌面会话（这套脚本要把窗口摆到真实桌面上量）
      2. 桌面上没有别的 MidiPerformer 实例在跑（**排在分辨率之前**：这条要在任何宿主里都够得着）
      3. 主屏分辨率 ≥ 2360x1520（这是它们把窗口摆成 2360x1520 的前提）
      4. Debug 构建在位（MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe）

    ⚠️ 为什么必须是「抛」而不是在 README 里写一句：不满足前提时这些脚本**不会报错** ——
    摆不动窗口就量到别的东西，桌面会话不在就取到空，然后**所有断言静默地变成假绿**。
    一份「跑起来全绿但其实什么都没验」的报告，比没有报告更坏，因为它会被当成证据。

    ⚠️ 第 2 条是硬规矩：有实例在跑就抛，**不替调用方关掉它**。`起窗口` 里的清场只针对
    它自己起的那个实例。

    ⚠️ 在检查第 3 条（分辨率）**之前**，本库先把进程设成 PerMonitorV2 感知（见下面那段注释）——
    不然 200% 缩放下读到的是虚拟化后的一半，第 3 条会误判，而 UIA 报的又是物理像素，
    两套坐标对不上。调用方**不需要**再自己设一遍（重复设是幂等的，返回 false 而已）。

    ⚠️ 本库是被 dot-source 的，所以下面那行 `Set-StrictMode` 会连**调用方**一起收紧
    （PowerShell 的严格模式按作用域生效，dot-source 就跑在调用方的作用域里）。
    调用方要是用了宽松写法（`$可能为空的量.Count` 之类），在严格模式下会抛。
    这是「共用库统一到 run-selftest 那个头」的已知代价，写在这里免得下次当谜案查。

.EXAMPLE
    . (Join-Path $PSScriptRoot 'uitest-lib.ps1')
    $h = 起窗口
    ... 量 ...
    收窗口

.NOTES
    本目录脚本的退出码约定（本库自己只 throw，不 exit）：
      0  = 断言全过
      1  = 有一条以上断言 FAIL
      2  = 环境没到位（exe 不在、窗口起不来、拽不到前台）
      3  = 前提不满足，或脚本跑到一半断了（**不是**「全过」）
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

# =====================================================================
# DPI 感知：**必须排在下面任何读屏幕尺寸的代码之前**。
# =====================================================================
# pwsh 默认是「不感知 DPI」的，于是 Windows 把窗口坐标**虚拟化**：显示缩放 200% 时，
# 这台 3072x1920 的机器读出来是 1536x960（见下面那条分辨率前提）。
# 后果是两头对不上：SetWindowPos 摆的是虚拟坐标，而 UIA 的 BoundingRectangle 报的是物理像素
# （实测 RootElement = 3072x1920），于是「窗口摆成 2360x1520」这类几何断言当场就红 ——
# 换台机器、换次缩放就复现不了，而且**不报错**，只是量到另一个空间里的数。
# PerMonitorV2（-4）= 物理像素空间，也就是 100% 缩放时那套数字的语义。
#
# best-effort：manifest 已经声明过感知（或别处先设过）时这一句返回 false，那是正常的，
# 不能因此把库带崩 —— 所以包 try/catch，失败就照旧往下走。
try {
  Add-Type -Namespace UitestDpi -Name Ctx -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(System.IntPtr c);
'@
  [void][UitestDpi.Ctx]::SetProcessDpiAwarenessContext([IntPtr](-4))
} catch { }

# =====================================================================
# 前提检查：四条本机前提，不满足就抛。在**任何动作之前**跑完。
# =====================================================================
if (-not [System.Windows.Forms.SystemInformation]::UserInteractive) {
  throw '没有桌面会话 —— 这套脚本要把窗口摆到真实桌面上量，量不到就是静默假绿'
}
# **不替用户关他自己开着的实例。** 从前这里是「无条件把所有 MidiPerformer 都关掉」——
# 桌面上有用户自己开的实例时，那一句就把人家的窗口收走了。
# 现在改成：有在跑的只报出来、停手，请你自己关。
#
# ⚠️ 这一条**排在分辨率前提之前**（67 号票收的尾）：分辨率那条读的是 `[Screen]::Bounds`，
# 读数对不对取决于**宿主进程**感不感知 DPI；实例守卫要在**任何**宿主里都够得着 ——
# 从前它排在分辨率后面，非感知宿主里先撞分辨率那一抛，于是「已经有实例在跑」这条
# **永远走不到**，看到的是「分辨率不够」这条跟眼前问题无关的话。
$前提在跑的 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue)
if ($前提在跑的.Count) {
  throw "已经有 MidiPerformer 在跑（PID $(($前提在跑的 | ForEach-Object { $_.Id }) -join ', ')）—— 先关掉再跑（这个脚本不替你关）"
}
$前提主屏 = [System.Windows.Forms.Screen]::PrimaryScreen
if (-not $前提主屏) { throw '取不到主屏 —— 没有桌面会话' }
if ($前提主屏.Bounds.Width -lt 2360 -or $前提主屏.Bounds.Height -lt 1520) {
  throw "主屏只有 $($前提主屏.Bounds.Width)x$($前提主屏.Bounds.Height) —— 这套脚本要 2360x1520 才摆得下（摆不下就量到别的东西）"
}
$前提exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $前提exe)) { throw "没找到 $前提exe —— 先编译（这套脚本量的是 Debug 构建）" }
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class P40 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint PidOf(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);

  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    AttachThreadInput(a, b, false);
    // 这一抬是无条件的。原先它挂在「前台不是 h」的分支里，于是「已经是前台、
    // 但被别的窗口压着」这种状态**永远不会被抬** —— 而那正是实测到的状态。
    // 67 号票并库时，这里从**内联的两句 SetWindowPos** 改成调 `Raise(h)`：两句一字不差
    // （TOPMOST → NOTOPMOST、SWP_NOACTIVATE），抽出来之后这份 `Take` 就跟 `verify-27`
    // 里那份逐字一样了。行为没动，位置没动（还是 AttachThreadInput 收尾之后、SetFocus 之前）。
    Raise(h);
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; PidOf(h, out pid);
      if (pid != want || h == skip || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
      list.Add(h); return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
  public static bool Enabled(IntPtr h) { return IsWindowEnabled(h); }
  public static bool Visible(IntPtr h) { return IsWindowVisible(h); }
  // 这个句柄**还是一个真窗口**吗。窗口被销毁之后，句柄号可能被系统回收给别的窗口，
  // 那时候 Visible/Rect/Title 读出来的都是别的窗口的样子（全 0、标题空）——
  // 看着像「窗口还在只是没显示」，其实早就没了。40 号工单就栽在这个歧义上。
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  public static bool Alive(IntPtr h) { return IsWindow(h); }
  public static IntPtr Foreground() { return GetForegroundWindow(); }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Rect(IntPtr h) { RECT r; GetWindowRect(h, out r);
    return (r.R-r.L) + "x" + (r.B-r.T) + " @ " + r.L + "," + r.T; }

  // 抓一窗像素，压成 byte[]（B,G,R,A 顺序）。**Add-Type 里一个 System.Drawing 的类型都不能用** ——
  // 见 38 号那条坑：引它会把默认引用整套换掉，List<>、StringBuilder 全找不到。
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr h);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr h, int w, int ht);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bm, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER {
    public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
  }
  [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER h; public uint c1, c2, c3; }

  public static int LastW, LastH;
  public static byte[] Shot(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    IntPtr screen = GetDC(IntPtr.Zero);
    IntPtr dc = CreateCompatibleDC(screen);
    IntPtr bm = CreateCompatibleBitmap(screen, w, ht);
    IntPtr old = SelectObject(dc, bm);
    PrintWindow(h, dc, 2);
    var bi = new BITMAPINFO();
    bi.h.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
    bi.h.biWidth = w; bi.h.biHeight = -ht; bi.h.biPlanes = 1; bi.h.biBitCount = 32; bi.h.biCompression = 0;
    var buf = new byte[w * ht * 4];
    GetDIBits(dc, bm, 0, (uint)ht, buf, ref bi, 0);
    SelectObject(dc, old); DeleteObject(bm); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);
    LastW = w; LastH = ht;
    return buf;
  }
  // 第 y 行、第 x 个像素的 "RRGGBB"
  public static string At(byte[] b, int w, int x, int y) {
    int i = (y * w + x) * 4;
    return b[i+2].ToString("X2") + b[i+1].ToString("X2") + b[i].ToString("X2");
  }
  // 同一个像素，但返回 0xRRGGBB 的 int —— 要比几千个点的时候比字符串快得多
  public static int Rgb(byte[] b, int w, int x, int y) {
    int i = (y * w + x) * 4;
    return (b[i+2] << 16) | (b[i+1] << 8) | b[i];
  }
  // 一格里**最靠右的那一列墨水**在哪：从 x2 往左一列一列找，哪一列上有像素跟底色差得超过
  // tol 就停。返回那一列的 x；整格都是底色就返回 -1。
  // 「名字有没有被省略号截掉」就是靠它判的：截了的话墨水会一直顶到格子右边沿。
  public static int InkRight(byte[] b, int w, int x1, int x2, int y1, int y2, int bg, int tol) {
    int br = (bg >> 16) & 255, bgc = (bg >> 8) & 255, bb = bg & 255;
    for (int x = x2; x >= x1; x--)
      for (int y = y1; y <= y2; y++) {
        int i = (y * w + x) * 4;
        int dr = Math.Abs(b[i+2] - br), dg = Math.Abs(b[i+1] - bgc), db = Math.Abs(b[i] - bb);
        if (dr > tol || dg > tol || db > tol) return x;
      }
    return -1;
  }

  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static IntPtr WindowFromPointPub(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }

  // 一次**规规矩矩**的点击。比 SetCursorPos + mouse_event(相对) 稳，原因有二：
  //   1) 走 MOUSEEVENTF_ABSOLUTE 归一化坐标 —— 这一条消息本身就是鼠标输入，
  //      不依赖「SetCursorPos 会不会顺手产生一条 WM_MOUSEMOVE」；
  //   2) 落点之前先在旁边抖一下（先到 x-24 再到位），保证目标窗口一定收到
  //      「指针进来了」那一条 —— 少了它，Avalonia 那边可能还认为指针在别处。
  // 40 号工单里 SetCursorPos 那套在「开曲库窗」上时灵时不灵，就是踩在这儿。
  public static void ClickAt(int x, int y) {
    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
    Jiggle(x - 24, y, sw, sh);
    Jiggle(x, y, sw, sh);
    System.Threading.Thread.Sleep(90);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(40);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }
  static void Jiggle(int x, int y, int sw, int sh) {
    uint nx = (uint)(Math.Max(0, Math.Min(sw - 1, x)) * 65535L / (sw - 1));
    uint ny = (uint)(Math.Max(0, Math.Min(sh - 1, y)) * 65535L / (sh - 1));
    mouse_event(0x0001 | 0x8000, nx, ny, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(60);
  }
  public static void DoubleClickAt(int x, int y) {
    ClickAt(x, y);
    System.Threading.Thread.Sleep(70);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(40);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }
  // 屏幕上那一点压着的窗口属于哪个进程。点之前拿它对一遍，免得点到别的窗口上去。
  public static uint PidAt(int x, int y) {
    POINT p; p.X = x; p.Y = y;
    IntPtr h = WindowFromPoint(p);
    uint pid; PidOf(h, out pid); return pid;
  }

  // =====================================================================
  // 驱动输入那一半。上面那一半管**截图 + 检视**，这一半管**驱动输入** ——
  // 两份原先各自长在 `verify-25` / `verify-27` / `verify-41` 里的内联类 `V25`（+ `D25`）
  // 里，67 号票并库时取的是**并集**（连 41 要用的那几件一起并，好让 68 只动一个文件）。
  // =====================================================================
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  /// 屏幕上那个点上**实际**是哪个窗口 —— 「点下去了但没有反应」时唯一说得清话的证人：
  /// 前台是它、坐标也对，可那个点上是别的窗口的话，点击就根本没进 app。
  public static string HitTest(int x, int y, IntPtr want) {
    POINT p; p.X = x; p.Y = y;
    IntPtr got = WindowFromPoint(p);
    return (got == want) ? "这个点是本窗口" : "这个点上不是它（句柄 " + got + (got == IntPtr.Zero ? "，点了个空" : "") + "）";
  }
  public static string Cursor() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }
  /// 「这个点上是谁」只说得出句柄，得再补一句它**是谁** —— 抢前台那类问题全靠这个才认得出来。
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; PidOf(h, out pid);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid;
  }
  /// 把窗口抬到**非 topmost 那一层的最上面**。
  ///
  /// 为什么光有 SetForegroundWindow 不够：实测过一次，app **已经是前台**了，
  /// 可 VS Code 还压在它上面 —— 两个都不是 WS_EX_TOPMOST，纯粹是 z 序排在那儿
  /// （app 的 GW_HWNDPREV 往上数第 4 个就是 VS Code 的主窗）。前台和「谁在最上面」
  /// 是两码事。这种状态下的要命之处是 `mouse_event` 把点击交给**光标底下那个窗口**，
  /// 也就是 VS Code：菜单弹一下就被关掉，前台还跟着换成 VS Code —— 一串假红就是这么来的。
  ///
  /// TOPMOST 再 NOTOPMOST 是「挤到非 topmost 层最上面」的老办法：
  /// 如果直接留着 TOPMOST，app 自己的弹出菜单（另一个顶层窗）有被压在主窗底下的风险，
  /// 所以落回 NOTOPMOST。SWP_NOACTIVATE 是别跟下面的抢前台，前台归 Take 管。
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
  }
  /// 这个点上到底是哪个窗口。前台说得再对也不算数，**点击只认这个**。
  ///
  /// 名字：它从 `V25` 搬过来时叫 `At`，而 `At` 在本库里**已经是**「一张截图里某一格的颜色」
  /// （`At(byte[], int, int, int)`，返回 `"RRGGBB"`）—— 同名不同义，搬进来会撞车。
  /// 库里那个 `At` 被 `verify-40.ps1` 用着，所以改的是搬进来的这一个：`WinAt` =「这个点是哪个窗」。
  public static IntPtr WinAt(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  /// 挪光标 / 按一下，拆成两步是为了能在中间「先停到别处去」—— 见 PowerShell 那边的 `点`。
  ///
  /// 为什么要先停到别处：**Avalonia 的悬浮提示是 app 自己的另一个顶层窗**（量到过：
  /// 类名 Avalonia-…、标题空、框 547,843 296x60，就贴在被悬停的那一格下面）。
  /// 它**不吃 hit test** —— `WindowFromPoint` 照报主窗、PID 闸门也照过，可只要它开着，
  /// **第一下点击就什么都不做，第二下才生效**（4 处独立复现，全在「光标刚在同一格上停过」之后）。
  /// 所以每次点之前先把光标挪到窗口里一块没有提示的空地上停够时间，把那张提示关掉，
  /// 再挪回目标立刻按下去（60ms，短到开不出新的提示）。
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Press() {
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  // 按一下再松开。**不用 SendKeys**：它把键投给「当前有焦点的控件」，
  // 而这几条快捷键恰恰是「不管焦点在谁身上、窗口层先吃掉」（TextBox 除外）——
  // 要走就得走正常输入队列（keybd_event 进的就是前台窗口那条）。
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(200);
  }
  // 带 Ctrl 的一下。VK_CONTROL = 0x11。
  public static void Ctrl(byte vk) {
    keybd_event(0x11, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 0, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(0x11, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(300);
  }
  // 「app 自己还开着哪些顶层窗、各在哪」—— 排查悬浮提示那类「进程号对得上、但点在它身上」的窗
  public static int[] RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  // 「两下挨得够近的按」这一段。停车（把悬浮提示关掉）由 PowerShell 那边统一做，
  // 这里只负责按下、松开、再按下、再松开 —— 两下之间的间隔必须小于系统双击间隔，
  // 而 `Press` 中间睡 330ms，两次拼起来就超了，那不是双击是两下单击。
  public static void DoublePress() {
    for (int i = 0; i < 2; i++) {
      mouse_event(0x0002,0,0,0,IntPtr.Zero); mouse_event(0x0004,0,0,0,IntPtr.Zero);
      System.Threading.Thread.Sleep(60);
    }
    System.Threading.Thread.Sleep(400);
  }

  // ---- 下面这一小撮是 41 号票要用的：驱动那个原生文件框（子窗口树是 probe-41-dlg 倒出来的）----
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string cls, string win);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string s);
  // EntryPoint 必须写死：带 CharSet.Unicode 又不给 EntryPoint 时，.NET 会拿**方法名**去拼 W
  // （找 `SendMessageStrW`），而不是拿真函数名 —— 当场 EntryPointNotFoundException。
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr SendMessageStr(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public const uint WM_SETTEXT = 0x000C, WM_CHAR = 0x0102, BM_CLICK = 0x00F5;
  /// 把一串字**当成用户敲的**送进那个文件名格：先 WM_SETTEXT 清空，再一个字符一个 WM_CHAR。
  ///
  /// 为什么不直接 SetWindowText 了事：那个框靠 EN_CHANGE 决定「打开」亮不亮，
  /// 而 WM_CHAR 走的是编辑控件的正常处理路径，通知一定会发出去。实测两条路都灵，
  /// 但这个更贴「用户真的敲了一遍」，不容易在上位机换成别的 shell 时失灵。
  public static void TypeInto(IntPtr edit, string s) {
    SendMessageStr(edit, WM_SETTEXT, IntPtr.Zero, "");
    foreach (char c in s) SendMessage(edit, WM_CHAR, (IntPtr)c, (IntPtr)1);
  }
  /// 把原生框里所有子窗口按类名捞一遍（EnumChildWindows 是平铺的，父子关系得自己按类名接）。
  public static List<IntPtr> KidsOfClass(IntPtr p, string cls) {
    var l = new List<IntPtr>();
    EnumChildWindows(p, (h, x) => { if (ClsOf(h) == cls) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  public static string ClsOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string TxtOf(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

function 存图([byte[]]$b, [int]$w, [int]$ht, [string]$路径) {
  $img = New-Object System.Drawing.Bitmap($w, $ht)
  $data = $img.LockBits((New-Object System.Drawing.Rectangle(0,0,$w,$ht)),
    [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  [System.Runtime.InteropServices.Marshal]::Copy($b, 0, $data.Scan0, $b.Length)
  $img.UnlockBits($data)
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $路径) | Out-Null
  $img.Save($路径, [System.Drawing.Imaging.ImageFormat]::Png)
  $img.Dispose()
}

# 起一个干净实例，摆满工作区，返回主窗口句柄。设 $script:pid脚本。
function 起窗口 {
  $exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
  if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
  Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
    [void][P40]::ShowWindow($_.MainWindowHandle, 9)
    [void]$_.CloseMainWindow()
    if (-not $_.WaitForExit(8000)) { $_.Kill() }
  }
  Start-Sleep -Milliseconds 900
  # ★ $script:proc 必须在 Start-Process **之前**就绑好。下面那两处 throw（"窗口没起来" /
  # "等不到窗口句柄"）发生在赋值之前，一旦从那里抛，$script:proc 从未绑定；调用方的收尾
  # （`try { 收窗口 } catch { }`）去读它，在严格模式下「读未绑定变量」**再抛一条**，而那个
  # catch 把它吞了 —— 于是脚本安安静静地退出、**app 留在桌面上**，锁死下一个人的构建。
  # 实测栽过：一个孤儿实例（PID 24940）卡了 6 个 agent 十几分钟。
  $script:proc = $null
  $proc = Start-Process -FilePath $exe -PassThru
  $期限 = (Get-Date).AddSeconds(30)
  do { Start-Sleep -Milliseconds 500
       if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }
       $proc.Refresh() }
  while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
  if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
  Start-Sleep -Seconds 3
  $script:proc = $proc
  $script:pid脚本 = [uint32]$proc.Id
  $script:h = $proc.MainWindowHandle
  if (-not [P40]::Take($script:h)) { throw '拽不到前台' }
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  [void][P40]::SetWindowPos($script:h, [IntPtr]::Zero, $wa.X, $wa.Y, $wa.Width, $wa.Height, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 800
  # 用 Write-Host 不用裸字符串：裸字符串会跟着 return 的值一起进管道，
  # 调用方 `$h = 起窗口` 就拿到一个数组（`FromHandle` 会报「无法转换 Object[]」）。
  Write-Host "起了一个干净实例：PID $($proc.Id)，窗口 $([P40]::Rect($script:h))"
  return $script:h
}

function 收窗口 {
  # 判 $null 而不是判真假：`$script:proc` 可能压根没绑过（起窗口在 Start-Process 之前就抛了），
  # 严格模式下裸着读它自己会再抛一条，把调用方的 catch 变成**静默漏实例**。
  if ($null -ne $script:proc -and -not $script:proc.HasExited) {
    [void][P40]::ShowWindow($script:h, 9)
    [void]$script:proc.CloseMainWindow()
    if (-not $script:proc.WaitForExit(8000)) { $script:proc.Kill() }
  }
}
