# 38 号工单的实机验证：**抽掉一段的那一段改成在卷帘上直接拖出来**。
#
# 用法: pwsh -NoProfile -File verify-38.ps1     （脚本自己起 app、自己收尾）
#
# 用户 2026-09-20 的原话（这一票就是照这两句做的）：
#   「1.我希望可以某种交互手段可以更精准的切割」
#   「原来的按小节切放弃 不要再出现填小节数字的窗口了。」
#
# 判据分三层，缺一层都证不出这件事：
#   · **输入框没了** —— UIA 数窗口里的 Edit 控件，按下「抽掉一段…」前后**个数一模一样**。
#     从前按下它会多出两个小节号框，这一条量的就是「那个窗口没了」。
#   · **红带子** —— 按像素找 `palette.Stop` 的**长竖列**（红带子左右两条边，
#     整条轨那么高）。这是「拖出来的那一段在屏幕上真的看得见」唯一的证人：
#     读数条说不清、UIA 也读不到（那是自绘控件）。
#   · **音符没被拖走** —— 剪切模式按下时**不做命中测试**（见 PianoRollLane.OnPointerPressed），
#     所以按在音符身上横拖，画出的是红带子、不是把那个音拖走。
#     这一条挡的是「模式没生效、退回了普通的拖音符」——那种坏法下别的一切都照样绿。
#
# 再往下一层是**语义**：轨道头上那一行预览（UIA 读得到，是 TextBlock）。
#   它说的「删掉 N 个音」等按完「抽掉」之后要**对得上**轨头的音数（「N 音」读得到）——
#   预览是这一步的主心骨（CutPreview.Of 与真跑一遍 CutRange 由对照测试钉住），
#   这里量的是它和真锅真灶上那一下是不是同一件事。
#
# 几处踩过的坑：
#   · **抓图的坐标换算**：PrintWindow 抓下来的图是**窗口局部**坐标，而 UIA 报的、
#     鼠标点的都是屏幕坐标。find-note.ps1 是假定窗口在 (0,0) 糊过去的，这一票不这么干 ——
#     每一趟抓图都现取 GetWindowRect，按那个原点换算（`抓像素` / `红边`）。
#     （实测这台机器上 SetWindowPos 挪不动这个窗，所以「假定 (0,0)」在这儿根本立不住。）
#   · 卷帘一屏恒定 4 小节（`PianoRollGeometry.BarsVisible`），Carulli 一小节 1440 tick、
#     窗口里那一段卷帘约 1900px ⇒ 一个十六分格 ≈ 40px。所以「吸到格线上」是**肉眼可见**的
#     40px 量级，脚本里按 ±45px 判。
#   · 红边那一列是**竖着几百像素**的（标尺以下整条轨）。轨头上那颗 `danger` 的「删除」按钮
#     用的也是 Stop 系颜色，但它给不出几百像素的**同列**连续同色 —— 所以判据是「连续长列」，
#     不是「出现过这个颜色」。
#   · 播放头那条线**也是** `palette.Stop`（而且整屏高）。所以先扫一遍「没装备时」的底，
#     再扫「装备之后」的 —— 两次都在的列是播放头，**新出现的**才是红带子的边。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# `诊断` 会把「卷帘里最像红边的那些列」按最长连续段打出来 —— 扫不到边时靠它看清屏幕上的实情。
# （不用 param()：它得是脚本第一句，而这一句前面还有输出编码那一句。
#  也不用 `-诊断`：加了减号的词会被 pwsh -File 当成脚本参数名。）
$诊断 = $args -contains '诊断'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
# 这段 C# **一个 System.Drawing 的类型都不用**：Bitmap 那一头全留在 PowerShell 里。
# 试过 `-ReferencedAssemblies System.Drawing.Common` 那条路 —— 它会把默认引用整套换掉，
# 于是 `List<>`、`System.Threading.Thread` 全找不到（CS0246/CS0234 一串）。
# 改成把抓下来的像素 LockBits + Marshal.Copy 成一个 byte[] 喂进来，扫的是纯 byte 运算。
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V38 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint f);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint PidOf(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
  }
  public static IntPtr At(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  public static uint PidAt(int x, int y) {
    IntPtr w = At(x, y);
    if (w == IntPtr.Zero) return 0;
    uint pid; PidOf(w, out pid); return pid;
  }
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    return "类名'" + c + "' 标题'" + t + "'";
  }
  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    AttachThreadInput(a, b, false);
    Raise(h);
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  /// 窗口矩形：[左, 上, 宽, 高]。抓图坐标 = 屏幕坐标 - (左, 上)，
  /// 所以这个原点每一趟都要现取（UIA 的 BoundingRectangle 和它不是一回事）。
  public static int[] RectOf(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    return new int[] { r.L, r.T, r.R - r.L, r.B - r.T };
  }
  public static void Down() {
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); System.Threading.Thread.Sleep(150);
  }
  public static void Up() {
    mouse_event(0x0004, 0,0,0, IntPtr.Zero); System.Threading.Thread.Sleep(600);
  }
  /// 横拖一道。分步走：一步跳到终点的话 Avalonia 只收到「按下 → 抬起」，
  /// 中间那一串 PointerMoved 一个都没有，拖动就白拖了（照抄 verify-19）。
  public static void DragTo(int x, int y, int steps) {
    for (int i = 1; i <= steps; i++) {
      SetCursorPos(x, y);
      System.Threading.Thread.Sleep(60);
    }
  }
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; PidOf(h, out pid);
      if (pid != want || h == skip || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
      list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  /// 找出「一列里连着 >= minRun 个红边色」的那些 x（报的是**屏幕** x，已加回原点 ox）。
  /// 扫的这一坨（几百行 x 两千列）**放在 C# 里**：同样一趟在 PowerShell 里按 GetPixel
  /// 逐像素跑要好几秒，而这个脚本要扫八九趟。
  /// `px` 是 32bppArgb 的原始像素，每像素 4 字节、行距 `stride`，内存里是 **BGRA** 次序。
  /// `ox`,`oy` 是这张图左上角在屏幕上的位置。
  public static List<int> RedColumns(byte[] px, int stride, int ox, int oy,
      int x0, int x1, int y0, int y1,
      int minRun, int r1, int g1, int b1, int r2, int g2, int b2, int tol) {
    var outp = new List<int>();
    for (int sx = x0; sx < x1; sx++) {
      int x = sx - ox;
      int run = 0, best = 0;
      for (int sy = y0; sy < y1; sy++) {
        int i = (sy - oy) * stride + x * 4;
        int b = px[i], g = px[i+1], r = px[i+2];
        bool hit = (Math.Abs(r-r1)<=tol && Math.Abs(g-g1)<=tol && Math.Abs(b-b1)<=tol)
                || (Math.Abs(r-r2)<=tol && Math.Abs(g-g2)<=tol && Math.Abs(b-b2)<=tol);
        if (hit) { run++; if (run > best) best = run; } else { run = 0; }
      }
      if (best >= minRun) outp.Add(sx);
    }
    return outp;
  }
  /// 诊断用：把「最像红边的那些列」按最长连续段排出来（不管够不够长）。
  /// 扫不到边的时候靠它看清屏幕上到底是什么 —— 是边太短（轨不够高）、
  /// 还是颜色被抗锯齿摊开了（差一点出容差）。
  public static List<string> TopColumns(byte[] px, int stride, int ox, int oy,
      int x0, int x1, int y0, int y1,
      int r1, int g1, int b1, int r2, int g2, int b2, int tol, int top) {
    var rows = new List<int[]>();
    for (int sx = x0; sx < x1; sx++) {
      int x = sx - ox, run = 0, best = 0, total = 0;
      for (int sy = y0; sy < y1; sy++) {
        int i = (sy - oy) * stride + x * 4;
        int b = px[i], g = px[i+1], r = px[i+2];
        bool hit = (Math.Abs(r-r1)<=tol && Math.Abs(g-g1)<=tol && Math.Abs(b-b1)<=tol)
                || (Math.Abs(r-r2)<=tol && Math.Abs(g-g2)<=tol && Math.Abs(b-b2)<=tol);
        if (hit) { run++; total++; if (run > best) best = run; } else { run = 0; }
      }
      if (best > 0) rows.Add(new int[] { sx, best, total });
    }
    rows.Sort((a,b) => b[1].CompareTo(a[1]));
    var outp = new List<string>();
    for (int k = 0; k < rows.Count && k < top; k++)
      outp.Add("x " + rows[k][0] + "：最长 " + rows[k][1] + "、共 " + rows[k][2]);
    return outp;
  }
  /// 取一个点的颜色（0xRRGGBB，屏幕坐标）。
  public static int Pixel(byte[] px, int stride, int ox, int oy, int sx, int sy) {
    int i = (sy - oy) * stride + (sx - ox) * 4;
    if (i < 0 || i + 2 >= px.Length) return -1;
    return (px[i+2] << 16) | (px[i+1] << 8) | px[i];
  }
  /// 诊断用：把**一行**像素摊成「同色段」打出来（屏幕 y = sy，x 从 sx0 到 sx1）。
  /// 扫不到边的时候靠它看清这一行上到底有什么 —— 卷帘从哪个 x 起、
  /// 红带子的底和边各在哪儿、颜色是不是被抗锯齿摊开了。
  public static List<string> RowRuns(byte[] px, int stride, int ox, int oy,
      int sy, int sx0, int sx1, int minWidth) {
    var outp = new List<string>();
    int r0 = -1, g0 = -1, b0 = -1, start = sx0;
    for (int sx = sx0; sx <= sx1; sx++) {
      int r = -1, g = -1, b = -1;
      if (sx < sx1) {
        int i = (sy - oy) * stride + (sx - ox) * 4;
        b = px[i]; g = px[i+1]; r = px[i+2];
      }
      if (r0 < 0) { r0 = r; g0 = g; b0 = b; start = sx; continue; }
      if (r != r0 || g != g0 || b != b0) {
        if (sx - start >= minWidth)
          outp.Add("x " + start + ".." + (sx-1) + "  #" + r0.ToString("X2") + g0.ToString("X2") + b0.ToString("X2"));
        r0 = r; g0 = g; b0 = b; start = sx;
      }
    }
    return outp;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
# 参数名**不能**叫 `$条件`：那是脚本级那个 UIA PropertyCondition 工厂（见 `找类型`），
# 同名参数会把它在函数体里遮掉 —— 现在函数体里没用到，但埋着一颗雷。
function 断言真([string]$名字, [bool]$成立, [string]$原文) {
  if ($成立) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
# **逐字**比对（PowerShell 的 -eq 对字符串不区分大小写）。
function 断言字([string]$名字, [string]$实际, [string]$期望) {
  if ($实际 -ceq $期望) { "  OK   $名字（逐字相同）" }
  else { "  FAIL $名字 读到「$实际」，期望「$期望」"; $script:fail++ }
}

# ---------- 起一个干净实例 ----------
$exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V38]::ShowWindow($_.MainWindowHandle, 9)
  [void]$_.CloseMainWindow()
  if (-not $_.WaitForExit(8000)) { $_.Kill() }
}
Start-Sleep -Milliseconds 900
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }; $proc.Refresh() }
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 3
$h = $proc.MainWindowHandle
$脚本PID = [uint32]$proc.Id
"起了个干净实例：PID $($proc.Id)"

if (-not [V38]::Take($h)) { throw '拽不到前台' }
# 尽量把窗口挪到宽一点的地方（卷帘越宽、一个十六分格越多像素，「吸附到格线上」越好量）。
# **不要求落在 (0,0)**：挪不动也不影响判据 —— 抓图的坐标一律按**现取的**
# GetWindowRect 原点换算（见 抓像素 / 红边），不像 find-note 那样假定 (0,0)。
$窗 = [V38]::RectOf($h)
[void][V38]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 2400, 1700, 0x0004 -bor 0x0010)
Start-Sleep -Milliseconds 900
[void][V38]::Take($h)
$窗 = [V38]::RectOf($h)
$窗左 = $窗[0]; $窗上 = $窗[1]; $窗宽 = $窗[2]; $窗高 = $窗[3]
"窗口 $($窗宽)x$($窗高) @ $($窗左),$($窗上)"

foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)
$停车点 = @([int]($窗左 + $窗宽 * 0.55), [int]($窗上 + $窗高 * 0.85))

# ---------- 取数 ----------
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 找行([string]$曲名) {
  @(找类型 $CT::ListItem | Where-Object {
    $t = @($_.FindAll($TS::Descendants, (& $条件 $CT::Text)))
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名 }) | Select-Object -First 1
}
function 输入框数 { @(找类型 $CT::Edit).Count }
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}

# 轨头在哪（照抄 find-note：轨号文字是两位数字、x 在 440..490 之间）。
function 轨头Y { @(找类型 $CT::Text | Where-Object { $r = $_.Current.BoundingRectangle
      $_.Current.Name -match '^\d{2}$' -and $r.X -gt 440 -and $r.X -lt 490 } |
    Sort-Object { $_.Current.BoundingRectangle.Y } | ForEach-Object { [int]$_.Current.BoundingRectangle.Y }) }

# 第 1 条轨的音数读数（「N 音」）。取轨头那一行里那格 —— 它和轨号同一行。
function 音数 {
  $ys = 轨头Y
  if ($ys.Count -lt 1) { return -1 }
  $y0 = $ys[0]
  $t = 找类型 $CT::Text | Where-Object {
    $r = $_.Current.BoundingRectangle
    $_.Current.Name -match '^\d+ 音$' -and [Math]::Abs($r.Y - $y0) -lt 60 -and $r.X -gt 900 }
  if (@($t).Count -eq 0) { return -1 }
  $m = [regex]::Match((@($t)[0].Current.Name), '^(\d+) 音$')
  return [int]$m.Groups[1].Value
}

# 「抽掉一段」那一行预览 / 提示。CutBar 里没别的长句，按「第 … 小节」或那句提示认。
# 它和「抽掉」「取消」两颗按钮同高，所以先锚到「取消」那颗按钮，再横着找同一行的长文本。
function 预览 {
  $c = 按钮 '取消'
  if (@($c).Count -eq 0) { return $null }
  $cr = @($c)[0].Current.BoundingRectangle
  $t = 找类型 $CT::Text | Where-Object {
    $r = $_.Current.BoundingRectangle
    [Math]::Abs(($r.Y + $r.Height / 2) - ($cr.Y + $cr.Height / 2)) -lt 14 -and $r.X -gt ($cr.X + $cr.Width) }
  if (@($t).Count -eq 0) { return $null }
  return (@($t) | Sort-Object { $_.Current.BoundingRectangle.X })[0].Current.Name
}

# ---------- 像素：红带子那两条边 ----------
# 抓一张窗口图，把它**摊成 byte[]**（32bppArgb，行距 stride）交给 C# 去扫。
# 用 LockBits + Marshal.Copy 而不是逐个 GetPixel：一趟 2400x1700 的拷贝是一次 memcpy，
# 而 GetPixel 是四百多万次跨行调用。
function 抓像素 {
  $r = [V38]::RectOf($h)
  $w = $r[2]; $hh = $r[3]
  $bmp = New-Object System.Drawing.Bitmap($w, $hh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc()
  [void][V38]::PrintWindow($h, $dc, 2)
  $g.ReleaseHdc($dc); $g.Dispose()

  $矩形 = New-Object System.Drawing.Rectangle(0, 0, $w, $hh)
  $数据 = $bmp.LockBits($矩形, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $字节 = New-Object byte[] ($数据.Stride * $hh)
  [System.Runtime.InteropServices.Marshal]::Copy($数据.Scan0, $字节, 0, $字节.Length)
  $stride = $数据.Stride
  $bmp.UnlockBits($数据)
  $bmp.Dispose()
  return @{ 像素 = $字节; 行距 = $stride; 宽 = $w; 高 = $hh; 左 = $r[0]; 上 = $r[1] }
}

$Stop浅 = @(0xA8, 0x32, 0x32)   # 浅色主题的 TokenStop
$Stop深 = @(0xE0, 0x80, 0x80)   # 深色主题的 TokenStop（这台机器跑的是深色，见 find-note 的音符色）

# 一个点的颜色（0xRRGGBB）。判「填色在不在」用：红带子的底是 StopSoft 补一层透明度，
# 它压过的卷帘底色会从偏蓝（#1E2A3A）变成偏红（#2D2328）—— 这个差别一眼就能断言。
function 取色([int]$横, [int]$纵) {
  $图 = 抓像素
  return [V38]::Pixel($图.像素, $图.行距, $图.左, $图.上, $横, $纵)
}

# 在第 1 条轨的卷帘里找「一列里连着 ≥ $最短列 个 Stop 色」的那些 x（**屏幕**坐标）。
# $最短列 = 200：红带子的边是标尺以下**整条轨**那么高（几百像素），
# 轨头上那颗 danger 按钮 / 文字给不出这么长的一列。
function 红边([int]$y顶, [int]$y底, [int]$最短列 = 200) {
  $图 = 抓像素
  # **从窗口左沿起扫**，不从固定的 460 起：卷帘的 tick 0 在屏幕的哪个 x 上，
  # 取决于曲库条 / 轨道头那些列此刻多宽，假设不得。上一版写死 460，
  # 预填那一段的左沿（tick 0，落在 x≈439）就整个在扫描范围外，
  # 「两条边」只扫得到一条 —— 假红。轨头上那些红色是 danger 按钮和文字，
  # 高不过二十来像素，被 $最短列 = 200 挡在外面。
  $x0 = $图.左 + 8
  $x1 = [Math]::Min($图.左 + $图.宽 - 2, $图.左 + 2600)
  if ($诊断) {
    # 走 Write-Host，不走管道：这个函数的**返回值**是给 `并段` 吃的 int 数组，
    # 顺手往管道里丢几行字就成了数组里的一个元素，那边按 [int[]] 一转就炸。
    Write-Host "  [诊断] 卷帘里最像红边的列（按最长连续段排）："
    [V38]::TopColumns($图.像素, $图.行距, $图.左, $图.上, $x0, $x1, $y顶,
      [Math]::Min($y底, $图.上 + $图.高),
      $Stop浅[0], $Stop浅[1], $Stop浅[2], $Stop深[0], $Stop深[1], $Stop深[2], 40, 14) |
      ForEach-Object { Write-Host "    $_" }
    # 再摊一行看看：红带子的填色、两条边、卷帘左沿到底各在哪个 x 上。
    # 「左沿扫不到」这种时候，只有把行摊开才知道它是不见了还是换了颜色。
    $ym = [int](($y顶 + $y底) / 2)
    Write-Host "  [诊断] 卷帘正中那一行（屏幕 y=$ym，x 从 $($图.左 + 300) 起）的同色段："
    [V38]::RowRuns($图.像素, $图.行距, $图.左, $图.上, $ym, $图.左 + 300, $图.左 + 1100, 1) |
      ForEach-Object { Write-Host "    $_" }
  }
  return [V38]::RedColumns($图.像素, $图.行距, $图.左, $图.上,
    $x0, $x1, $y顶, [Math]::Min($y底, $图.上 + $图.高), $最短列,
    $Stop浅[0], $Stop浅[1], $Stop浅[2], $Stop深[0], $Stop深[1], $Stop深[2], 18)
}
# 把连着的 x 并成一段一段，报每段 [起, 止]。
# **两个函数都直接返回数组**（不套 `,`）：套了的话空结果会变成「装着一个空数组的数组」，
# Count 就从 0 变成 1，下面每一条「扫到几段」都跟着错。调用方一律用 `@(...)` 兜住。
function 并段([int[]]$列) {
  $段 = @()
  $起 = -1; $上 = -1
  foreach ($x in $列) {
    if ($起 -lt 0) { $起 = $x; $上 = $x; continue }
    if ($x - $上 -le 2) { $上 = $x; continue }
    $段 += , @($起, $上); $起 = $x; $上 = $x
  }
  if ($起 -ge 0) { $段 += , @($起, $上) }
  return $段
}

# ---------- 点 / 拖（照抄 verify-39 的清场） ----------
function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][V38]::Take($h)
    if ([V38]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($窗左 + $窗宽 / 2); $中纵 = [int]($窗上 + $窗高 / 2)
      if ([V38]::GetForegroundWindow() -eq $h -and [V38]::PidAt($中横, $中纵) -eq $脚本PID) { return }
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住"
}
function 清场([int]$横, [int]$纵, [string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    [V38]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 400
    if ([V38]::PidAt($横, $纵) -eq $脚本PID) { return }
    Start-Sleep -Milliseconds 300
  }
  throw "「$谁」之前清不干净：$横,$纵 上压着 $([V38]::Describe([V38]::At($横, $纵)))"
}
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [V38]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 400
  [V38]::Move($横, $纵); Start-Sleep -Milliseconds 80
  [V38]::Down(); [V38]::Up()
}
# 横拖：按下 → 分步挪 → 抬起。**步骤是现算的**（起点终点都给我），照抄 verify-19 的 Drag。
function 拖([int]$x1, [int]$y1, [int]$x2, [int]$y2, [string]$谁) {
  清场 $x1 $y1 $谁
  [V38]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 400
  [V38]::Move($x1, $y1); Start-Sleep -Milliseconds 250
  [V38]::Down()
  $步数 = 10
  for ($i = 1; $i -le $步数; $i++) {
    [V38]::Move([int]($x1 + ($x2 - $x1) * $i / $步数), [int]($y1 + ($y2 - $y1) * $i / $步数))
    Start-Sleep -Milliseconds 60
  }
  [V38]::Up()
  Start-Sleep -Milliseconds 400
}
function 键([string]$k, [int]$歇 = 700) {
  [void][V38]::Take($h)
  [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds $歇
}

$跑完了 = $false
try {

# =====================================================================
"`n=== 0. 载入 Carulli（4 条轨、3/4 拍、PPQ 480 ⇒ 一小节 1440 tick）==="
# =====================================================================
$曲名 = 'Carulli_Duetto_No2_Op4'
$行 = 找行 $曲名
if (-not $行) { throw "曲库里没有「$曲名」" }
if (-not [V38]::Take($h)) { throw '拽不到前台' }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5

$轨 = 轨头Y
"  轨头 Y = $($轨 -join ', ')"
断言 '载入之后是 4 条轨' $轨.Count 4
$音数0 = 音数
"  第 1 轨音数读数 = 「$($音数0) 音」"
断言真 '第 1 轨的音数读数读得出来' ($音数0 -gt 0) "$音数0 音"

# 找到轨 1 里最上面那个音符（屏幕坐标 + 它的横杠 x 区间）
$扫描 = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'find-note.ps1') -Track 1 2>&1)
"  find-note: $(($扫描 | Where-Object { $_ -notmatch '^\d+,\d+$' }) -join ' / ')"
$音符行 = $扫描 | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
if (-not $音符行) { throw '轨 1 里按像素找不到音符' }
$音符xy = $音符行 -split ','
$音X = [int]$音符xy[0]; $音Y = [int]$音符xy[1]
$横 = $扫描 | Where-Object { $_ -match '横跨 x (\d+)\.\.(\d+)' } | Select-Object -First 1
$音L = -1; $音R = -1
if ($横 -match '横跨 x (\d+)\.\.(\d+)') { $音L = [int]$Matches[1]; $音R = [int]$Matches[2] }
"  第一个音符 @ ($音X,$音Y)，横跨 x $音L..$音R"

# 卷帘的纵向范围：轨头往下 60px 起、到下一个轨头前 20px 止（和 find-note 同一把尺子）
$y顶 = $轨[0] + 60
$y底 = [Math]::Min($轨[1] - 20, [int]($窗高) - 10)
if ($y底 -le $y顶) { throw "轨 1 的卷帘量不出高度（y $y顶..$y底）" }
"  轨 1 的卷帘扫描带 y $y顶..$y底"

# 一个十六分格 ≈ 多少像素：**不在这儿估**，也不拿红带子当尺子（tick 0 那条左沿在屏幕上
# 是被卷帘边框盖住的，见第 2 段）。要按「哪儿到哪儿」挑段的地方，一律从 find-note 量出来的
# 那个音的位置起算 —— 它给的是屏幕坐标，本来就不需要知道 tick。

# ---------- 1. 没装备时：底图（红边只该有播放头那条） ----------
"`n=== 1. 还没按「抽掉一段…」：卷帘上没有红带子 ==="
$底列 = @(并段 @(红边 $y顶 $y底))
"  底图里整列 Stop 色的位置：" + (($底列 | ForEach-Object { "x $($_[0])..$($_[1])" }) -join '、')
$底数 = $底列.Count
断言真 '没装备时最多只有播放头那一列' ($底数 -le 1) "扫到 $底数 段（播放头也在这条颜色上，所以不要求 0）"

$输入框0 = 输入框数
"  此刻窗口里的输入框（UIA Edit）个数 = $输入框0"

# ---------- 2. 按下「抽掉一段…」：这一问换出来，但**一个输入框都没多** ----------
"`n=== 2. 按下「抽掉一段…」：轨道头上换出那一问，且**一个输入框都没多出来** ==="
$b = 按钮 '抽掉一段…'
if (@($b).Count -eq 0) { throw '找不到「抽掉一段…」那颗按钮' }
$br = @($b)[0].Current.BoundingRectangle
点 ([int]($br.X + $br.Width / 2)) ([int]($br.Y + $br.Height / 2)) '「抽掉一段…」'
Start-Sleep -Milliseconds 700

# 「抽掉」和「取消」都出来了 = 那一问真摆上了
断言真 '「抽掉」这颗按钮出来了' (@(按钮 '抽掉').Count -ge 1) "找到 $(@(按钮 '抽掉').Count) 颗"
断言真 '「取消」这颗按钮出来了' (@(按钮 '取消').Count -ge 1) "找到 $(@(按钮 '取消').Count) 颗"
# 用户原话「不要再出现填小节数字的窗口了」——这一条量的就是那个窗口没了
$输入框1 = 输入框数
"  按下之后窗口里的输入框个数 = $输入框1（按下之前 $输入框0）"
断言 '按下「抽掉一段…」一个输入框都没多' $输入框1 $输入框0

# 预填的那一段：装备上时卷帘上就该有一根红带子（本轨选中的音，没有就退回播放头那一小节）
$预 = 预览
"  那一行预览 = 「$预」"
断言真 '预览不是空的（预填了一段）' ($null -ne $预 -and $预.Length -gt 0) "「$预」"
$预列 = @(并段 @(红边 $y顶 $y底))
$新列 = @($预列 | Where-Object { $x = $_[0]; -not ($底列 | Where-Object { [Math]::Abs($_[0] - $x) -le 3 }) })
"  装备之后整列 Stop 色的位置：" + (($预列 | ForEach-Object { "x $($_[0])..$($_[1])" }) -join '、')
断言 '装备上之后多出来的是红带子的右沿（就多这一条）' $新列.Count 1

# **为什么只多一条边。** 预填的是第 1 小节（tick 0..1440），而 tick 0 恰好落在卷帘的最左沿上：
# 左沿那条 6px 宽的焦点轨色条（#74ABDD）把红带子的左沿盖住了 ——
# 实测那一行的同色段是 `x 437..442 #74ABDD` 接着 `x 443..598 #2D2328`（红带子压过的卷帘底色），
# 红的左沿根本不在屏幕上（见 .scratch/shots/v38-diag.txt）。这不是错：
# 带子从谱面最左边起，左沿藏在边框底下，看着就是「从头开始」。
# 所以这一段能验的是**右沿 + 一整片填色** —— 填色下面单独量。
$右沿 = -1
if ($新列.Count -ge 1) { $右沿 = $新列[0][1] } elseif ($预列.Count -ge 1) { $右沿 = $预列[-1][1] }
$ym2 = [int](($y顶 + $y底) / 2)
$里 = 取色 ([Math]::Max($右沿 - 30, $窗左 + 450)) $ym2
$外 = 取色 ([Math]::Min($右沿 + 30, $窗左 + 2380)) $ym2
$里R = ($里 -shr 16) -band 255; $里B = $里 -band 255
$外R = ($外 -shr 16) -band 255; $外B = $外 -band 255
断言真 '红带子的填色确实铺在那一段上（带子里偏红、外面是卷帘底色）' `
  ($里R -gt $里B -and $外B -gt $外R) `
  "带子里 #$($里.ToString('X6'))、带子外 #$($外.ToString('X6'))（窗口正中那一行 y=$ym2）"

# ---------- 3. 正题：按在**音符身上**横拖 —— 红带子跟着手走，音符原地不动 ----------
"`n=== 3. 在音符身上横拖一段：画出红带子，那个音**没有被拖走** ==="
# 剪切模式按下时不做命中测试（PianoRollLane.OnPointerPressed），所以按在音符身上
# 照样是划段 —— 要是模式没生效，这一下会变成「拖动那个音」，右移 300px 后
# 它的横杠左沿会跟着挪 300px，下面那几条断言就是冲着这个来的。
#
# 「音符身上」得按**装备之后**的版式找：轨道头长出第二行（CutBar），卷帘整条被推下去，
# 拿装备之前那个 y 去按，按到的是另一个音高行（甚至按到标尺上）。
# 推下去多少不猜 —— 量：装备之后轨头的 Y 减去装备之前的。
$移 = (轨头Y)[0] - $轨[0]
"  装备之后轨道头往下挪了 $移 px"
$n3 = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'find-note.ps1') -Track 1 -Drop $移 2>&1)
"  find-note（装备之后）: $(($n3 | Where-Object { $_ -notmatch '^\d+,\d+$' }) -join ' / ')"
$行3 = $n3 | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
if (-not $行3) { throw '装备之后按像素找不到音符（-Drop 那把尺子没对准）' }
$xy3 = $行3 -split ','
$音X3 = [int]$xy3[0]; $音Y3 = [int]$xy3[1]
"  装备之后轨 1 里第一个音符 @ ($音X3,$音Y3)"

$拖到 = [Math]::Min($音X3 + 300, [int]($窗宽) - 40)
拖 $音X3 $音Y3 $拖到 $音Y3 '在音符身上划一段'

$预2 = 预览
"  划完之后那一行预览 = 「$预2」"
$列2 = @(并段 @(红边 $y顶 $y底))
$边 = @($列2 | Where-Object { $x = $_[0]; -not ($底列 | Where-Object { [Math]::Abs($_[0] - $x) -le 3 }) })
"  划完之后整列 Stop 色的位置：" + (($列2 | ForEach-Object { "x $($_[0])..$($_[1])" }) -join '、')
断言真 '划出来之后红带子是两条边（不是一条、也不是三条）' ($边.Count -eq 2) "新出现 $($边.Count) 段"
if ($边.Count -eq 2) {
  $左 = $边[0][0]; $右 = $边[1][1]
  "  红带子横跨 x $左..$右（拖的是 $音X3..$拖到）"
  # 容差 60px：一个十六分格在这个窗宽下 ≈ 40px，吸附最多挪半格；给到 60 是连
  # 「窗口没挪成 2400 宽」那种情况也一起容忍 —— 这条要证明的是「沿跟着手走」，
  # 不是「吸附算法精确到像素」（那个归 MidiPerformer.Tests 里的纯函数测）。
  断言真 '红带子的左沿落在按下那一点上（跟着手走，不是别处的边）' ([Math]::Abs($左 - $音X3) -le 60) `
    "按下 $音X3，红带子左沿 $左"
  断言真 '红带子的右沿落在松手那一点上（跟着手走，不是别处的边）' ([Math]::Abs($右 - $拖到) -le 60) `
    "松手 $拖到，红带子右沿 $右"
  断言真 '红带子这一段既没宽成整条轨、也没窄成一条线' (($右 - $左) -ge 0.5 * ($拖到 - $音X3) -and ($右 - $左) -le 1.5 * ($拖到 - $音X3)) `
    "宽 $($右 - $左)px，划了 $($拖到 - $音X3)px"
}
断言真 '红带子这个宽度不是整屏（说明它跟着手走，不是把整条轨都染红）' `
  ($边.Count -eq 2 -and ($边[1][1] - $边[0][0]) -lt ([int]($窗宽) * 0.6)) `
  $(if ($边.Count -eq 2) { "宽 $($边[1][1] - $边[0][0])px，窗口宽 $([int]$窗宽)" } else { '边数不对，量不了宽' })
断言真 '「抽掉」这颗按钮亮了（这一段里有音）' (@(按钮 '抽掉')[0].Current.IsEnabled) `
  "预览说的是「$预2」"

# 那个音有没有被拖走：**先 Esc 收掉那一问，再用装备之前那把尺子量**（版式回到原样，
# 尺子又准了）。而「按下并拖过」这个事实并没有被 Esc 抹掉 ——
# 剪切模式下 Settle 一个字节都不提交（PianoRollLane），真把音拖走了的话，Esc 还不了原。
键 '{ESC}' 900
$拖后 = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'find-note.ps1') -Track 1 2>&1)
$音符行2 = $拖后 | Where-Object { $_ -match '^\d+,\d+$' } | Select-Object -Last 1
if (-not $音符行2) {
  断言真 '划完那个音还在' $false '轨 1 里一个音符都找不到了'
} else {
  $横2 = $拖后 | Where-Object { $_ -match '横跨 x (\d+)\.\.(\d+)' } | Select-Object -First 1
  $音L2 = -1; $音R2 = -1
  if ($横2 -match '横跨 x (\d+)\.\.(\d+)') { $音L2 = [int]$Matches[1]; $音R2 = [int]$Matches[2] }
  "  Esc 之后第一个音符横跨 x $音L2..$音R2（划之前 x $音L..$音R）"
  断言真 '剪切模式下按在音符身上不会把它拖走' ([Math]::Abs($音L2 - $音L) -le 6) `
    "左沿 $音L → $音L2（拖动那个音的话会挪 $($拖到 - $音X3)px 左右）"
}
断言 '那一趟拖动一个音都没动（音数没变）' (音数) $音数0
断言真 'Esc 之后那一问收掉了' (@(按钮 '取消').Count -eq 0) "还找到 $(@(按钮 '取消').Count) 颗"

# ---------- 4. 回车 = 抽掉：音数**正好**少掉预览承诺的那几个 ----------
"`n=== 4. 从那个音的左沿起划一大段（一小节多），回车 = 抽掉 ==="
# 从哪里起划**不是随便挑的**：Carulli 第 1 轨（guitare）开头很稀
# （tick 0 / 480 / 1440 各一个音，之后从 2880 起才密起来），
# 划短了很可能划到一段**一个音都没整个落进去**的区间 —— 那样预览里只有
# 「剪短 / 后面提前」而没有「删掉」，量不出「音数正好少了几个」。
# 所以从**已知那个音的左沿**（= tick 480，find-note 量出来的那个）起，往右划 1500px ≈ 4 拍半，
# 这一段里整个落进去的音十几个，足够量。
$b4 = 按钮 '抽掉一段…'
if (@($b4).Count -eq 0) { throw '找不到「抽掉一段…」那颗按钮（为了真抽一刀）' }
$br4 = @($b4)[0].Current.BoundingRectangle
点 ([int]($br4.X + $br4.Width / 2)) ([int]($br4.Y + $br4.Height / 2)) '「抽掉一段…」（为了真抽一刀）'
Start-Sleep -Milliseconds 700
$x4a = $音L
$x4b = [Math]::Min($音L + 1500, [int]($窗宽) - 40)
$y4 = [int](($y顶 + $y底) / 2)
"  拖 x $x4a..$x4b，y $y4（那个音的左沿起，往右 1500px）"
拖 $x4a $y4 $x4b $y4 '挑一段来抽'
$预4 = 预览
"  预览 = 「$预4」"

$删 = -1
if ($预4 -match '删掉 (\d+) 个音') { $删 = [int]$Matches[1] }
断言真 '预览说清了要删几个音' ($删 -ge 1) "「$预4」"
$短前 = -1; $短后 = -1
if ($预4 -match '(\d+) → (\d+) 小节') { $短前 = [int]$Matches[1]; $短后 = [int]$Matches[2] }
断言真 '预览说这条轨抽完会短下去（不是空着那两小节接着播）' ($短后 -lt $短前 -and $短后 -gt 0) `
  "「$预4」里是 $短前 → $短后 小节"
断言真 '「抽掉」这颗按钮亮了' (@(按钮 '抽掉')[0].Current.IsEnabled) "预览说的是「$预4」"

$音数前 = 音数
"  抽之前音数 = $音数前 音"
键 '{ENTER}' 1200
$音数后 = 音数
"  抽之后音数 = $音数后 音"
断言 '音数正好少了预览说的那么多' ($音数前 - $音数后) $删

断言真 '抽完之后那一问收掉了（「取消」没了）' (@(按钮 '取消').Count -eq 0) "还找到 $(@(按钮 '取消').Count) 颗"
断言真 '「抽掉一段…」回到了轨道头上' (@(按钮 '抽掉一段…').Count -ge 1) "找到 $(@(按钮 '抽掉一段…').Count) 颗"
$列3 = @(并段 @(红边 $y顶 $y底))
"  抽完之后整列 Stop 色的位置：" + (($列3 | ForEach-Object { "x $($_[0])..$($_[1])" }) -join '、')
断言真 '抽完之后红带子从屏幕上消失了' ($列3.Count -le 1) "扫到 $($列3.Count) 段（只该剩播放头那条）"

# ---------- 5. Ctrl+Z 撤销 ----------
"`n=== 5. Ctrl+Z：音数回到原样，红带子没有回来 ==="
键 '^z'
$音数撤 = 音数
"  撤销之后音数 = $音数撤 音"
断言 '撤销把音数还回来了' $音数撤 $音数前
断言真 '撤销没有把那一问一起还回来（红带子没回来）' ((@(并段 @(红边 $y顶 $y底))).Count -le 1) '卷帘上只剩播放头那条'

# ---------- 6. Esc = 取消：不抽，音数一个不少 ----------
"`n=== 6. 重新按「抽掉一段…」，划一段，按 Esc：不抽 ==="
$b6 = 按钮 '抽掉一段…'
$br6 = @($b6)[0].Current.BoundingRectangle
点 ([int]($br6.X + $br6.Width / 2)) ([int]($br6.Y + $br6.Height / 2)) '「抽掉一段…」（第二次）'
Start-Sleep -Milliseconds 700
断言真 '第二次按下也摆上了那一问' (@(按钮 '取消').Count -ge 1) "找到 $(@(按钮 '取消').Count) 颗"

$音数6前 = 音数
# 还是第 4 段划的那一段（那个音的左沿起、往右 1500px）：它横在卷帘正中，
# 装备之后版式往下挪也还在轨里，比拿 find-note 那个 y 稳（那个 y 是装备之前的版式量出来的）。
拖 $x4a $y4 $x4b $y4 '第二次划一段'
$预6 = 预览
"  划完预览 = 「$预6」"
断言真 '第二次也画出了红带子' ((@(并段 @(红边 $y顶 $y底))).Count -ge 2) '两条边都扫得到'
断言真 '第二次划出来的那一段也不是空的' ($null -ne $预6 -and $预6 -notmatch '拖一段') "「$预6」"

键 '{ESC}' 900
$音数6后 = 音数
断言真 'Esc 把那一问收掉了' (@(按钮 '取消').Count -eq 0) "还找到 $(@(按钮 '取消').Count) 颗"
断言 'Esc 之后音数一个都没少' $音数6后 $音数6前
断言真 'Esc 之后红带子也消失了' ((@(并段 @(红边 $y顶 $y底))).Count -le 1) '卷帘上只剩播放头那条'
断言真 '「抽掉一段…」回到了轨道头上' (@(按钮 '抽掉一段…').Count -ge 1) "找到 $(@(按钮 '抽掉一段…').Count) 颗"

# ---------- 7. 按一下不挪：划不出东西来，预览改说「怎么划」 ----------
"`n=== 7. 装备之后在卷帘上**点一下不挪**：那一段是空的，「抽掉」灰着 ==="
$b7 = 按钮 '抽掉一段…'
$br7 = @($b7)[0].Current.BoundingRectangle
点 ([int]($br7.X + $br7.Width / 2)) ([int]($br7.Y + $br7.Height / 2)) '「抽掉一段…」（第三次）'
Start-Sleep -Milliseconds 700
# 点哪儿都行（剪切模式不看按在哪个音高上），只要是卷帘里面 —— 用第 4 段那个 x4a、卷帘正中那个 y。
点 $x4a $y4 '在卷帘上点一下不挪'
Start-Sleep -Milliseconds 500
$预7 = 预览
"  点完预览 = 「$预7」"
断言字 '点一下不挪：预览改说怎么划（Format.CutNeedRange）' $预7 '在这条轨的卷帘上横向拖一段 —— 拖出来的那一段就是要消失的'
断言真 '「抽掉」灰着（这一段是空的）' (-not @(按钮 '抽掉')[0].Current.IsEnabled) '按钮 IsEnabled = false'

# 灰着的时候按回车：**什么都不该发生**，那一问还摆着。
# 为什么不是「收掉那一问」：回车的语义是「把这一问按下去」，而灰着的「抽掉」等于「没什么可按的」——
# 命令在这时候会原样返回同一份曲子（连撤销都不记），按了等于没按。
# 这一下只保证「回车被这一问吃掉了」，没漏到别处去（见 TrackLaneView.ConfirmPendingSplit：
# 灰着也返回 true，窗口那边把 e.Handled 立起来）。
$音数7前 = 音数
键 '{ENTER}' 900
断言 '灰着时按回车：音数一个没少' (音数) $音数7前
断言真 '灰着时按回车：那一问还摆着（回车归这一问用，没漏到别处）' (@(按钮 '取消').Count -eq 1) `
  "「取消」还找到 $(@(按钮 '取消').Count) 颗；「抽掉」灰着 $(-not @(按钮 '抽掉')[0].Current.IsEnabled)"

# ---------- 8. 收尾：全程没弹过对话框 ----------
"`n=== 8. 全程检查：台面上没有多出别的顶层窗（没弹过对话框）==="
$别 = @([V38]::Others($脚本PID, $h))
断言 'app 名下没有留下别的顶层窗' $别.Count 0
if ($别.Count) { $别 | ForEach-Object { "     $([V38]::Describe($_))" } }

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  try {
    $落 = @([V38]::Others($脚本PID, $h))
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [V38]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
[void][V38]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
