# 图标验证：这东西到底有没有出现在**标题栏左上角**和**任务栏的应用列表**里。
#
# 为什么不用 verify-40-lib 里的 `起窗口`：那个函数开头会把**所有** MidiPerformer 进程
# 关掉（这是它一贯的做法，验证脚本要一个干净实例）。但这次桌面上有一个**用户自己开的**
# 实例，关掉它就是把人家手上的东西弄没了。所以这里自己起、自己收，只认自己那个 PID。
#
# 量的是三处：
#   1. 标题栏左上角那一小块 —— 截屏放大看（唯一的地面真相：Windows 到底画了什么）
#   2. 任务栏那一条 —— 一起截下来
#   3. 窗口类上的 GCLP_HICONSM / GCLP_HICON —— 任务栏和 Alt-Tab 是从这里取图的；
#      两个都是 0 的话，说明窗口类自己没设图标，由 exe 的图标兜底（Win32 的默认行为）
#
# ⚠️ 位图一律走 `$script:图` 这个变量传，不走函数返回值 ——
# PowerShell 的函数会把体内每一句的输出都算进返回值，`$bmp` 一不小心就成了数组，
# 接着 `$bmp.Width` 被"成员枚举"成数组、`* 6` 报 op_Multiply 找不到（第一版就栽在这）。
#
# 用法: pwsh -NoProfile -File verify-icon.ps1

Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ICO {
  [DllImport("user32.dll")] public static extern IntPtr GetClassLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  // PW_RENDERFULLCONTENT = 2：把窗口自己画一遍，不管它是不是被别的窗口压着。
  // 这一步是必需的 —— SetForegroundWindow 经常被 Windows 挡下来（不允许后台进程抢前台），
  // 抢不到的话 CopyFromScreen 抓到的就是压在它上面的那个窗口（第一版抓到的是我自己的终端）。
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static IntPtr 小图标(IntPtr h) { return GetClassLongPtr(h, -34); }  // GCLP_HICONSM
  public static IntPtr 大图标(IntPtr h) { return GetClassLongPtr(h, -14); }  // GCLP_HICON
  public static int[] 矩形(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    return new int[] { r.L, r.T, r.R - r.L, r.B - r.T };
  }
}
"@

$出 = Join-Path $PSScriptRoot 'appicon-preview'
New-Item -ItemType Directory -Force -Path $出 | Out-Null
$script:图 = $null

function 截区域([int]$x, [int]$y, [int]$w, [int]$ht) {
  $b = [System.Drawing.Bitmap]::new($w, $ht)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen($x, $y, 0, 0, [System.Drawing.Size]::new($w, $ht))
  $g.Dispose()
  $script:图 = $b
}

function 抓窗口([IntPtr]$h) {
  $r = [ICO]::矩形($h)
  $b = [System.Drawing.Bitmap]::new($r[2], $r[3])
  $g = [System.Drawing.Graphics]::FromImage($b)
  $hdc = $g.GetHdc()
  $好 = [ICO]::PrintWindow($h, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  $script:图 = $b
  return $好
}

function 放大([int]$倍) {
  $源 = $script:图
  $b = [System.Drawing.Bitmap]::new($源.Width * $倍, $源.Height * $倍)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g.DrawImage($源, 0, 0, $b.Width, $b.Height)
  $g.Dispose()
  $script:图 = $b
}

function 裁([int]$x, [int]$y, [int]$w, [int]$ht) {
  $源 = $script:图
  $b = [System.Drawing.Bitmap]::new($w, $ht)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.DrawImage($源,
    [System.Drawing.Rectangle]::new(0, 0, $w, $ht),
    [System.Drawing.Rectangle]::new($x, $y, $w, $ht),
    [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()
  $script:图 = $b
}

function 存位图([string]$名) {
  $p = Join-Path $出 $名
  $script:图.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host "  写出 $名  ($($script:图.Width)x$($script:图.Height))"
}

$exe = (Resolve-Path (Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe')).Path
Write-Host "用 $exe"

# ⚠️ 只记下「本来就有哪些 MidiPerformer」，一个都不碰。
$本来就有 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "本来就在跑的 MidiPerformer：$(if ($本来就有.Count) { $本来就有 -join ', ' } else { '（没有）' })"

$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do {
  Start-Sleep -Milliseconds 500
  if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }
  $proc.Refresh()
} while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }

$h = $proc.MainWindowHandle
Write-Host "我这个实例：PID $($proc.Id)，窗口 $h"
[void][ICO]::ShowWindow($h, 9)
[void][ICO]::SetForegroundWindow($h)
Start-Sleep -Seconds 3

$r = [ICO]::矩形($h)
Write-Host "窗口矩形：$($r[0]),$($r[1]) $($r[2])x$($r[3])"

# ── 1. 窗口自己画一遍（不受遮挡影响），再从里面裁标题栏左上角 ──
Write-Host "`n── 1. 标题栏左上角（图标就画在这一块里）──"
$好 = 抓窗口 $h
Write-Host "  PrintWindow 成功 = $好"
if (-not $好) { Write-Host '  ⚠️ PrintWindow 没成功，下面这张图不作数' }
存位图 'window-full.png'
裁 0 0 150 52
放大 6
存位图 'titlebar-corner-6x.png'

# ── 3. 任务栏那一条 ──
Write-Host "`n── 2. 任务栏（屏幕最下面那一条）──"
$屏 = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
截区域 $屏.X ($屏.Y + $屏.Height - 120) $屏.Width 120
存位图 'taskbar.png'

# ── 4. 窗口类上的两个图标句柄 ──
Write-Host "`n── 3. 窗口类上的图标句柄 ──"
$小 = [ICO]::小图标($h)
$大 = [ICO]::大图标($h)
Write-Host "  GCLP_HICONSM（小图标，标题栏/任务栏用）= $小"
Write-Host "  GCLP_HICON  （大图标，Alt-Tab 用）      = $大"
if ($小 -eq [IntPtr]::Zero -and $大 -eq [IntPtr]::Zero) {
  Write-Host '  两个都是 0 —— 窗口类自己没设图标，由 exe 的图标兜底（Win32 默认行为）'
} else {
  foreach ($对 in @(@('类小图标', $小), @('类大图标', $大))) {
    if ($对[1] -ne [IntPtr]::Zero) {
      $script:图 = [System.Drawing.Icon]::FromHandle($对[1]).ToBitmap()
      存位图 "$($对[0])-从窗口类取回.png"
    }
  }
}

# ── 收尾：只关我自己这一个 ──
Write-Host "`n── 收尾 ──"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill(); Write-Host '  我这个实例不肯退，强杀了' }
else { Write-Host "  关掉了我这个实例（PID $($proc.Id)）" }
$还在 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "本来就在跑的那些现在还在吗：$(if ($还在.Count) { $还在 -join ', ' } else { '（一个都不剩 —— 有问题！）' })"
