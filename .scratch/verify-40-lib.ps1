# 40 号工单探针 / 验证脚本共用的那一段：P/Invoke + UIA 取数 + 起一个干净实例。
# 用法: . (Join-Path $PSScriptRoot 'verify-40-lib.ps1')

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
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
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
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
  $exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
  if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
  Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
    [void][P40]::ShowWindow($_.MainWindowHandle, 9)
    [void]$_.CloseMainWindow()
    if (-not $_.WaitForExit(8000)) { $_.Kill() }
  }
  Start-Sleep -Milliseconds 900
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
  if ($script:proc -and -not $script:proc.HasExited) {
    [void][P40]::ShowWindow($script:h, 9)
    [void]$script:proc.CloseMainWindow()
    if (-not $script:proc.WaitForExit(8000)) { $script:proc.Kill() }
  }
}
