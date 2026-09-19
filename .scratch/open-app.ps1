# 打开编辑器窗口，并载入一首曲子，好让窗口里当场有东西可看/可点。
# 用法: pwsh -File open-app.ps1 [-Song <曲名>] [-Size]
#
# 只关「标题是 MIDI 演奏器」的那一个进程，按 PID 点名关，不做 dotnet 全杀。
#
# **载曲子走曲库，不走「导入」。** 导入那条路要过两个对话框：先是一个系统文件选择框
# （`StorageProvider.OpenFilePickerAsync`），选完文件之后 app 还会弹一个自己画的
# 「导入 MIDI」问名字（`AskNameForSaveAsync`）。两个都是模态窗，而且**两个都会留在那儿**：
# 只要有一个没关掉，它就把后面所有点击和按键全吃掉 —— 主窗口看着一切正常
# （UIA 读得出、PrintWindow 抓得到），就是一动不动，看起来活像「这段逻辑没生效」。
# 曲库里点一首只要一下点击 + 一个回车，没有对话框，跑一百遍也一样。
#
# 曲库里得有这首。没有就先跑一次 `-Import`（那条路留着，但用完一定回头查窗口）。
param(
  [string]$Song = 'Carulli_Duetto_No2_Op4',
  [switch]$Import
)

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
    /// 这个进程还开着的、够大的可见顶层窗（除了主窗）。模态对话框就躲在这里面。
    public static List<IntPtr> Others(uint want, IntPtr skip) {
      var list = new List<IntPtr>();
      EnumWindows((h,l) => {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != want || h == skip || !IsWindowVisible(h)) return true;
        RECT r; GetWindowRect(h, out r);
        if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
        list.Add(h);
        return true;
      }, IntPtr.Zero);
      return list;
    }
    public static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static void Click(int x, int y) {
      SetCursorPos(x, y); System.Threading.Thread.Sleep(250);
      mouse_event(0x0002,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(100);
      mouse_event(0x0004,0,0,0,IntPtr.Zero);
    }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Find-App {
  Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
}
$btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
function 收尾($procId, $h) {
  $left = [W]::Others([uint32]$procId, $h)
  if ($left.Count -gt 0) {
    $l = $AE::FromHandle($left[0])
    throw "还有窗口开着：「$($l.Current.Name)」。它是模态的，会把后面所有点击按键都吃掉 —— 先手工关掉再验。"
  }
}

# ---------- 关掉旧的 ----------
$old = Find-App
if ($old) {
  "关掉旧的那个 PID $($old.Id)"
  [void][W]::ShowWindow($old.MainWindowHandle, 9)
  [void]$old.CloseMainWindow()
  if (-not $old.WaitForExit(8000)) { $old.Kill(); "等它自己关没等到，按 PID 结束了" }
  Start-Sleep -Milliseconds 800
} else { "没有旧的在跑" }

# ---------- 起新的 ----------
$exe = Join-Path $PSScriptRoot '..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
$proc = Start-Process -FilePath $exe -PassThru
"起来了 PID $($proc.Id)：$exe"

$deadline = (Get-Date).AddSeconds(30)
do {
  Start-Sleep -Milliseconds 500
  if ($proc.HasExited) { throw "窗口没起来，退出码 $($proc.ExitCode)" }
  $proc.Refresh()
} while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 2
$h = $proc.MainWindowHandle
[void][W]::ShowWindow($h, 9)
[void][W]::SetWindowPos($h, [W]::NOTOPMOST, 0, 0, 3040, 1740, 0x0040)   # 物理像素，屏幕 3072x1920
Start-Sleep -Milliseconds 600
[void][W]::SetWindowPos($h, [W]::TOPMOST, 0, 0, 0, 0, 0x0003)
[void][W]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800

if ($Import) {
  # 导入那条路（要过两个对话框，能不用就不用）
  $root = $AE::FromHandle($h)
  $import = $root.FindAll($TS::Descendants, $btnCond) |
    Where-Object { $_.Current.Name -like '导入*' } | Select-Object -First 1
  if (-not $import) { throw '找不到「导入」按钮' }
  $import.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 3
  "文件选择框该出来了 —— 这一段还没做成自动的，先手工选完再跑吧"
  exit 1
}

# ---------- 从曲库里点开一首 ----------
$root = $AE::FromHandle($h)
$行 = @($root.FindAll($TS::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem))))
if ($行.Count -eq 0) { throw '曲库是空的 —— 先用 -Import 导一首进去' }

# 找名字对得上的那一行；名字是行里的 Text，不是 ListItem 自己的 Name（那个是控件类型名）
$目标 = $null
foreach ($it in $行) {
  $t = $it.FindAll($TS::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))
  foreach ($x in $t) { if ($x.Current.Name -eq $Song) { $目标 = $it } }
}
if (-not $目标) { throw "曲库里没有「$Song」（有的是：$((@($行 | ForEach-Object { $_.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) | Select-Object -First 1 | ForEach-Object { $_.Current.Name } }) ) -join ', ')）" }

function 数轨 {
  $rr = $AE::FromHandle($h)
  @($rr.FindAll($TS::Descendants, $btnCond) | Where-Object { $_.Current.Name -eq '折叠' }).Count
}

# 路一：UIA 选中 + 回车（曲库的键盘就是回车打开，见 OnListKeyDown）
# 路二：真鼠标双击（OnRowDoubleTapped）
# 两条都留着 —— 载入成功与否只看**轨数**，不看到底是哪条路走通的。
$载进来了 = $false
foreach ($路 in '选中+回车', '双击') {
  [void][W]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 400
  if ($路 -eq '选中+回车') {
    try { $目标.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { "选中失败：$_" }
    Start-Sleep -Milliseconds 400
    [void]$目标.SetFocus()
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  } else {
    $r = $目标.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    [W]::Click($cx, $cy); Start-Sleep -Milliseconds 120; [W]::Click($cx, $cy)
  }
  Start-Sleep -Seconds 4
  $n = 数轨
  "走「$路」之后轨数 = $n"
  if ($n -gt 0) { $载进来了 = $true; break }
}
if (-not $载进来了) { throw "点开了「$Song」但一条轨都没有 —— 没载进来" }

# ---------- 收尾 ----------
收尾 $proc.Id $h
$root = $AE::FromHandle($h)
$轨 = @($root.FindAll($TS::Descendants, $btnCond) | Where-Object { $_.Current.Name -eq '折叠' })
$音色 = @($root.FindAll($TS::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ComboBox))))
"载入完：$($轨.Count) 条轨，音色下拉 $($音色.Count) 个"
"标题 = $((Get-Process -Id $proc.Id).MainWindowTitle)"
"没有别的窗口压着了"
