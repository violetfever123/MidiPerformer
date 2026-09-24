# 41 号工单的辅助探针：把「打开 MIDI 文件」那个原生框的**子窗口树**倒出来。
#
# 为什么要它：那个框记得的是上次用过的目录（现在是用户自己的曲库），而取证的曲子住在
# `...\drywetmidi\Resources\MIDI files\Valid\MultiTrack\Middle\`。verify-27 那套「从列表里
# 选中一行再点打开」只在「框正好开在目标目录」时管用。要跨目录就得知道框里有哪些子窗口
# ——文件名那格、地址栏、以及它们能不能 SetWindowText。
#
# 用法: pwsh -NoProfile -File .scratch/probe-41-dlg.ps1

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class W41 {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll", EntryPoint="GetWindowThreadProcessId")]
  public static extern uint PidOf(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Txt(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  public static List<IntPtr> Kids(IntPtr p) {
    var l = new List<IntPtr>();
    EnumChildWindows(p, (h, x) => { l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static List<IntPtr> Tops() {
    var l = new List<IntPtr>();
    EnumWindows((h, x) => { l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
function 某根里([object]$根, [object]$类型) { @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 某根编号([object]$根, [string]$id) {
  @($根.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))))
}

# 抛异常也要把自己起的实例收掉，否则下一条 throw 就把窗口留在桌面上。
$proc = $null
trap {
  Write-Host "`n[异常] $_"
  if ($null -ne $proc) {
    try { $proc.Refresh(); if (-not $proc.HasExited) { $proc.Kill(); Write-Host "收了实例 PID $($proc.Id)" } } catch { }
  }
  break
}

$在跑的 = @(Get-Process -Name MidiPerformer -ErrorAction SilentlyContinue)
if ($在跑的.Count) { throw "已经有 MidiPerformer 在跑（PID $(($在跑的 | ForEach-Object { $_.Id }) -join ', ')）—— 先关掉再跑" }

$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $proc.Refresh() } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
Start-Sleep -Seconds 4
$h = $proc.MainWindowHandle
$脚本PID = [uint32]$proc.Id
"起了实例 PID $($proc.Id)"

$root = $AE::FromHandle($h)
$期限 = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $期限 -and -not (某根编号 $root 'SongNameBox').Count) {
  Start-Sleep -Milliseconds 500; $root = $AE::FromHandle($h)
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

$开钮 = 某根编号 $root 'PerformerButton'
[void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(20)
$演奏器 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
  foreach ($w in [W41]::Tops()) {
    if ($w -eq $h) { continue }
    # 变量别叫 $pid —— 那是 PowerShell 的只读自动变量，[ref] 上去会当场抛
    $进程号 = [uint32]0
    [void][W41]::PidOf($w, [ref]$进程号)
    if ($进程号 -ne $脚本PID -or -not [W41]::IsWindowVisible($w)) { continue }
    try { if ((某根编号 ($AE::FromHandle($w)) 'StartButton').Count) { $演奏器 = $w; break } } catch { }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($演奏器 -eq [IntPtr]::Zero) { throw '等不到演奏器窗口' }
Start-Sleep -Seconds 2
"演奏器窗口 $([W41]::Cls($演奏器)) 标题'$([W41]::Txt($演奏器))'"

$开2 = 某根编号 ($AE::FromHandle($演奏器)) 'OpenButton'
[void]$开2[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(25)
$框 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $框 -eq [IntPtr]::Zero) {
  foreach ($w in [W41]::Tops()) {
    if ([W41]::Cls($w) -like '#32770*') { $框 = $w; break }
  }
  if ($框 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($框 -eq [IntPtr]::Zero) { throw '等不到原生框' }
Start-Sleep -Seconds 1
$r = [W41]::Rect($框)
"`n=== 原生框 $([W41]::Cls($框)) '$([W41]::Txt($框))' 框=$($r[0]),$($r[1]) $($r[2])x$($r[3]) ==="

"`n--- 子窗口树（EnumChildWindows，全部后代，按类名+文字）---"
foreach ($k in [W41]::Kids($框)) {
  $kr = [W41]::Rect($k)
  $v = if ([W41]::IsWindowVisible($k)) { '显' } else { '隐' }
  "  {0,-28} {1} '{2}'  框={3},{4} {5}x{6}" -f `
    [W41]::Cls($k), $v, [W41]::Txt($k), $kr[0], $kr[1], $kr[2], $kr[3]
}

"`n--- 能编辑的子窗口（类名像 Edit / ComboBox / ComboBoxEx32 / ToolbarWindow32 / DirectUI）---"
foreach ($k in [W41]::Kids($框)) {
  $c = [W41]::Cls($k)
  if ($c -match 'Edit|ComboBox|ToolbarWindow32|DirectUI|Breadcrumb|SysTreeView|SHELLDLL') {
    "  $c  '$([W41]::Txt($k))'  框=$(([W41]::Rect($k)) -join ',')"
  }
}

"`n--- UIA 子树（前 40 个，类型/id/名/类名）---"
$框根 = $AE::FromHandle($框)
$i = 0
foreach ($e in @($框根.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))) {
  if ($i++ -ge 40) { break }
  "  {0,-16} id='{1}' 名='{2}' 类='{3}'" -f `
    ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''), $e.Current.AutomationId, $e.Current.Name, $e.Current.ClassName
}

# 收尾：直接收实例（框是模态的，进程一走它就没了）
$proc.Refresh()
if (-not $proc.HasExited) { $proc.Kill() }
"`n收了实例 PID $($proc.Id)"
