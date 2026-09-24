# 把「点了按钮之后到底弹出了什么」拍下来。
#
# 上一版只 PrintWindow 主窗口，什么都没看见 —— 不是没弹，是**曲库住在另一个顶层窗口里**
# （40 号工单之后它是模态窗口 SongLibraryWindow），主窗口画一遍当然画不到它。
# 所以这里改成：枚举本进程的**所有顶层窗口**，逐个 PrintWindow。
# 这样无论是菜单、模态窗还是提示框，只要它是个窗口，就躲不掉。
#
# 只认自己起的那个 PID；桌面上别人开着的实例一个都不碰。
#
# 用法: pwsh -NoProfile -File verify-lib-ui.ps1 -点偏移 "199,102"

param(
  [string]$点偏移 = '',
  [int]$每点等秒 = 4,
  # 打开曲库之后，再点列表里的第几首（1 开始）。坐标按曲库窗口自己的大小算，
  # 所以窗口被 Windows 摆到哪儿都不影响。行距 66px、首行中心 168px，是从截图上量出来的。
  [int]$曲库第几行 = 0,
  # 直接在曲库窗口里点某个像素（相对曲库窗口左上角，就是截图上的坐标），双击由 -曲库双击 决定。
  # 例：-曲库内点 "1049,886"（关闭按钮）/ "344,498"（Carulli 那一行）
  [string]$曲库内点 = '',
  [switch]$曲库双击
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class UIW {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // ── 曲库窗口只认「投递」，不认「注入」──
  // 试过的、都没用的：SetCursorPos+mouse_event、先抢前台（AttachThreadInput 版也抢到了）、
  // 把它设成 TOPMOST、SetActiveWindow+BringWindowToTop。WindowFromPoint 明明说那一点上
  // 挨着的就是曲库窗口，可按钮连 hover 都不亮。
  // 管用的：直接把 WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP **PostMessage 给那个窗口**，
  // 坐标用它的客户区坐标（就是截图上的坐标）—— 绕开命中测试和前台，一步到位。
  public static IntPtr LP(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
  public static void 投递点击(IntPtr h, int x, int y) {
    IntPtr lp = LP(x, y);
    PostMessage(h, 0x0200, IntPtr.Zero, lp);   // WM_MOUSEMOVE
    System.Threading.Thread.Sleep(120);
    PostMessage(h, 0x0201, (IntPtr)1, lp);     // WM_LBUTTONDOWN
    System.Threading.Thread.Sleep(80);
    PostMessage(h, 0x0202, IntPtr.Zero, lp);   // WM_LBUTTONUP
  }

  public static IntPtr TOPMOST = new IntPtr(-1);
  public static IntPtr NOTOPMOST = new IntPtr(-2);
  public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

  public static string 标题(IntPtr h) { var s = new StringBuilder(512); GetWindowTextW(h, s, s.Capacity); return s.ToString(); }
  public static string 类名(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, s.Capacity); return s.ToString(); }
  public static int[] 矩形(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }

  // 本进程的所有可见顶层窗口
  public static List<IntPtr> 本进程窗口(uint 目标) {
    var 结果 = new List<IntPtr>();
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == 目标 && IsWindowVisible(h)) 结果.Add(h);
      return true;
    }, IntPtr.Zero);
    return 结果;
  }

  public static void 点(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }
}
"@

$出 = Join-Path $PSScriptRoot 'verify-lib-ui'
New-Item -ItemType Directory -Force -Path $出 | Out-Null

# ── 81 号票：这一条修前**一条判据都没有**（拍完图就走人），却在总表里占着一个「绿」——
#    「绿」必须等于「有一批判据真的通过了」，它一条都没有。这是本票要杀的病。
#    补的判据全是它自己一直在量、抬头也早就承诺过的东西（没有新造需求）：
#      · 窗口标题（起的是不是我们那个 app）
#      · 「点了按钮之后弹出了什么」拍得到（枚举到窗口 + PrintWindow 成功 + 图真写出去了）
#      · 调用方**点名**要在曲库窗口里点时，曲库窗口必须在场 —— 修前是「找不到就跳过、
#        什么都不做」，那正是这套 harness 最怕的「不响的门」（看着像验过了，其实一步没走）
#  🔴 一条像素值判据都没有（硬规矩：像素读回只能进 probe，不能进 verify）。
$fail = 0
function 断言真([string]$名, [bool]$条件, [string]$原文) {
  if ($条件) { Write-Host "  OK   $名（$原文）" } else { Write-Host "  FAIL $名（$原文）" -ForegroundColor Red; $script:fail++ }
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $repo 'MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没有发布产物：$exe" }

$本来就有 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "本来就在跑的：$(if ($本来就有.Count) { $本来就有 -join ', ' } else { '（没有）' })"

$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(60)
do {
  Start-Sleep -Milliseconds 500
  if ($proc.HasExited) { throw "启动就退了，退出码 $($proc.ExitCode)" }
  $proc.Refresh()
} while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }

$主 = $proc.MainWindowHandle
$pid_ = [uint32]$proc.Id
Write-Host "我这个实例：PID $pid_，主窗口 $主"
$主标 = [UIW]::标题($主)
断言真 '主窗口标题是「MIDI 演奏器」' ($主标 -eq 'MIDI 演奏器') "「$主标」"
[void][UIW]::SetWindowPos($主, [UIW]::TOPMOST, 0, 0, 0, 0, [UIW]::SWP_NOMOVE -bor [UIW]::SWP_NOSIZE -bor [UIW]::SWP_NOACTIVATE)
[void][UIW]::SetForegroundWindow($主)
Start-Sleep -Seconds 4

# 拍一遍：本进程的每个顶层窗口各存一张图，顺便列出它们的标题/类名/位置
function 拍一轮([string]$标签) {
  $r = [UIW]::矩形($主)
  Write-Host "`n===== $标签（窗口内坐标 ＝ 屏幕坐标 - ($($r[0]),$($r[1]))）====="
  $n = 0
  $没截到 = 0
  $没写出 = 0
  foreach ($h in [UIW]::本进程窗口($pid_)) {
    $n++
    $wr = [UIW]::矩形($h)
    Write-Host ("  [{0}] hwnd={1} {2}x{3} @({4},{5})  类={6}  标题=「{7}」" -f `
      $n, $h, $wr[2], $wr[3], $wr[0], $wr[1], [UIW]::类名($h), [UIW]::标题($h))
    if ($wr[2] -le 0 -or $wr[3] -le 0) { Write-Host '       （尺寸为零，跳过）'; continue }
    $b = [System.Drawing.Bitmap]::new($wr[2], $wr[3])
    $g = [System.Drawing.Graphics]::FromImage($b)
    $hdc = $g.GetHdc()
    $好 = [UIW]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $p = Join-Path $出 ("{0}-窗{1}.png" -f $标签, $n)
    $b.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    Write-Host "       写出 $(Split-Path $p -Leaf)（PrintWindow=$好）"
    if (-not $好) { $没截到++ }
    if (-not (Test-Path -LiteralPath $p) -or (Get-Item -LiteralPath $p).Length -le 0) { $没写出++ }
  }
  if ($n -eq 0) { Write-Host '  （一个可见顶层窗口都没有？）' }
  断言真 "$标签：枚举到可见顶层窗口" ($n -gt 0) "$n 个"
  断言真 "$标签：每个窗口都截下来了（PrintWindow 全成功）" ($没截到 -eq 0) "没截到 $没截到 个"
  断言真 "$标签：图都写出去了（非空）" ($没写出 -eq 0) "没写出 $没写出 个"
  return $r
}

$r = 拍一轮 '1-开窗'

if ($点偏移.Trim()) {
  $k = 0
  foreach ($段 in $点偏移.Split(';')) {
    if (-not $段.Trim()) { continue }
    $k++
    $xy = $段.Split(',')
    $x = $r[0] + [int]$xy[0].Trim()
    $y = $r[1] + [int]$xy[1].Trim()
    Write-Host "`n>>>> 第 $k 点：窗口内 ($($xy[0].Trim()), $($xy[1].Trim())) ＝ 屏幕 ($x, $y)"
    [UIW]::点($x, $y)
    Start-Sleep -Seconds $每点等秒
    [void](拍一轮 "2-点$k")
  }
}

if ($曲库第几行 -gt 0 -or $曲库内点.Trim()) {
  # 曲库是个模态窗口，自己的顶层窗口，标题就叫「歌曲库」——从枚举里把它找出来
  $库 = [IntPtr]::Zero
  foreach ($h in [UIW]::本进程窗口($pid_)) {
    if ([UIW]::标题($h) -eq '歌曲库') { $库 = $h; break }
  }
  if ($库 -eq [IntPtr]::Zero) {
    Write-Host "`n⚠️ 没找到「歌曲库」窗口"
    断言真 '点名要在曲库窗口里点，曲库窗口就得在场' $false `
      '枚举本进程的可见顶层窗口，没有一个标题是「歌曲库」（修前这里是「找不到就什么都不做」——一次点击都没发生，却看着像验过了）'
  }
  else {
    # **曲库窗口也得提到最前**：TOP 只加在主窗口上时，这个被它拥有的模态窗口并不跟着置顶，
    # 于是点在它身上的鼠标事件会被压在它上面的窗口（我自己的终端）截走 ——
    # 现象就是「点了半天，曲库纹丝不动」，而主窗口上的按钮却点得动。
    [void][UIW]::SetWindowPos($库, [UIW]::TOPMOST, 0, 0, 0, 0, [UIW]::SWP_NOMOVE -bor [UIW]::SWP_NOSIZE -bor [UIW]::SWP_NOACTIVATE)
    Start-Sleep -Milliseconds 500
    $wr = [UIW]::矩形($库)
    if ($曲库内点.Trim()) {
      $内 = $曲库内点.Split(',')
      $离x = [int]$内[0].Trim(); $离y = [int]$内[1].Trim()
      $说明 = "曲库内 ($离x,$离y)"
    } else {
      $离x = [int]($wr[2] * 0.30)
      $离y = [int](($wr[3] * (168 + ($曲库第几行 - 1) * 66)) / 991.0)
      $说明 = "曲库第 $曲库第几行 行 ($离x,$离y)"
    }
    $x = $wr[0] + $离x
    $y = $wr[1] + $离y
    Write-Host "`n>>>> 点 $说明：曲库窗口 $($wr[2])x$($wr[3]) @($($wr[0]),$($wr[1]))（投递到窗口的客户区坐标）$(if ($曲库双击) { '，双击' })"
    [UIW]::投递点击($库, $离x, $离y)
    if ($曲库双击) { Start-Sleep -Milliseconds 150; [UIW]::投递点击($库, $离x, $离y) }
    Start-Sleep -Seconds 6
    # 曲库窗口还在不在 —— 点「关闭」的话这一步就看得出来点有没有落进去
    $库还在 = $false
    foreach ($h in [UIW]::本进程窗口($pid_)) { if ([UIW]::标题($h) -eq '歌曲库') { $库还在 = $true } }
    Write-Host "  点完：曲库窗口还在 = $库还在"
    [void](拍一轮 "3-点$($说明 -replace '[^\w]','_')")
  }
}

Write-Host "`n── 收尾（只关我自己这一个）──"
foreach ($h in [UIW]::本进程窗口($pid_)) { [void][UIW]::SetWindowPos($h, [UIW]::NOTOPMOST, 0, 0, 0, 0, [UIW]::SWP_NOMOVE -bor [UIW]::SWP_NOSIZE -bor [UIW]::SWP_NOACTIVATE) }
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill(); Write-Host '  不肯退，强杀了' }
else { Write-Host "  关掉了 PID $pid_" }
$还在 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "别人那些现在还在吗：$(if ($还在.Count) { $还在 -join ', ' } else { '（一个都不剩）' })"
# ⚠️ 「本来就在跑的还在不在」**不判红**：run-all.ps1 的抬头专门写过为什么
#    （这一轮桌上同时有别人的实例来来去去，拿它判红等于让结论看别人脸色）。
#    它是必须看见的信息，不是判据。

"`n$(if ($fail -eq 0) { '全过' } else { "$fail 条红" })"
# 81 号票：裁决行 + 退出码（run-all.ps1 拿这行复核退出码，对不上就降级成红）。
"==== uitest 裁决 不过=$fail"
exit $fail
