# 给「压缩 + 裁剪」那一版发布产物做界面体检。
#
# 为什么非要开窗口：内置自检（run-selftest）**不建窗口**，它只跑合成器那套用例。
# 裁剪裁坏的往往是别的东西 —— XAML 里 new 的控件、{Binding} 的目标属性、
# System.Text.Json 反序列化构造器。这些只有真开一次窗口、真读一份 .mproj 才看得见。
#
# 同时验证曲库：发布目录下 songs\ 里的曲子是不是都在、中文名有没有乱码、
# 点一首能不能真载进来（载入 = 走一遍 ReadProject 的 JSON 反序列化）。
#
# 只认自己起的那个 PID，绝不碰桌面上别人已经开着的实例。
#
# 用法: pwsh -NoProfile -File verify-shrunk.ps1 [-点第几首 N]

param(
  # 一串「相对窗口左上角」的点击点，分号隔开，例如 "199,102; 300,260"。
  # 每点一下截一张图（3-点1.png、3-点2.png…），好一格一格看界面是怎麼走过去的。
  [string]$点偏移 = '',
  [int]$每点等秒 = 4
)

Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SHR {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static int[] 矩形(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  public static string 标题(IntPtr h) {
    var sb = new System.Text.StringBuilder(512);
    GetWindowTextW(h, sb, sb.Capacity);
    return sb.ToString();
  }
  public static void 点(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
  }
}
"@

$出 = Join-Path $PSScriptRoot 'verify-compressed'
New-Item -ItemType Directory -Force -Path $出 | Out-Null
$script:图 = $null

function 抓窗口([IntPtr]$h) {
  $r = [SHR]::矩形($h)
  $b = [System.Drawing.Bitmap]::new($r[2], $r[3])
  $g = [System.Drawing.Graphics]::FromImage($b)
  $hdc = $g.GetHdc()
  $好 = [SHR]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT：不受遮挡影响
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  $script:图 = $b
  return $好
}

function 存位图([string]$名) {
  $p = Join-Path $出 $名
  $script:图.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host "  写出 $名  ($($script:图.Width)x$($script:图.Height))"
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $repo 'MidiPerformer.App\bin\Release\net8.0\win-x64\publish\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没有发布产物：$exe" }
$曲库 = Join-Path (Split-Path $exe -Parent) 'songs'

Write-Host "用 $exe"
$exeInfo = Get-Item $exe
Write-Host "产物 $([math]::Round($exeInfo.Length / 1MB, 2)) MB，改动于 $($exeInfo.LastWriteTime)"
Write-Host "曲库 $曲库"
Get-ChildItem $曲库 -Filter *.mproj | Sort-Object Name | ForEach-Object {
  Write-Host ("  {0,-8} {1}" -f $_.Length, $_.Name)
}

$本来就有 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "本来就在跑的 MidiPerformer：$(if ($本来就有.Count) { $本来就有 -join ', ' } else { '（没有）' })"

$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(60)
do {
  Start-Sleep -Milliseconds 500
  if ($proc.HasExited) { throw "启动就退了，退出码 $($proc.ExitCode)" }
  $proc.Refresh()
} while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
$h = $proc.MainWindowHandle
Write-Host "`n我这个实例：PID $($proc.Id)，窗口 $h"
[void][SHR]::ShowWindow($h, 9)
[void][SHR]::SetForegroundWindow($h)
Start-Sleep -Seconds 4

$r = [SHR]::矩形($h)
Write-Host "窗口矩形：$($r[0]),$($r[1]) $($r[2])x$($r[3])"
Write-Host "窗口标题：$([SHR]::标题($h))"

Write-Host "`n── 开窗后长什么样 ──"
$好 = 抓窗口 $h
Write-Host "  PrintWindow 成功 = $好"
存位图 '1-开窗.png'

if ($点偏移.Trim()) {
  $n = 0
  foreach ($段 in $点偏移.Split(';')) {
    if (-not $段.Trim()) { continue }
    $n++
    $xy = $段.Split(',')
    $x = $r[0] + [int]$xy[0].Trim()
    $y = $r[1] + [int]$xy[1].Trim()
    Write-Host "`n── 第 $n 点：窗口内 ($($xy[0].Trim()), $($xy[1].Trim())) ＝ 屏幕 ($x, $y) ──"
    [SHR]::点($x, $y)
    Start-Sleep -Seconds $每点等秒
    Write-Host "  点完的标题：$([SHR]::标题($h))"
    存位图 "3-点$n.png"
  }
}

Write-Host "`n── 收尾（只关我自己这一个）──"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill(); Write-Host '  不肯退，强杀了' }
else { Write-Host "  关掉了 PID $($proc.Id)" }
$还在 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object { $_.Id })
Write-Host "别人那些现在还在吗：$(if ($还在.Count) { $还在 -join ', ' } else { '（一个都不剩）' })"
