# 一句话报出 app 现在是什么状态：载的是哪首、几条轨、有没有窗口压着、收不收得到键。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class ST {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want || h == skip || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
      list.Add(h); return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$app = Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $app) { throw 'MidiPerformer 没在跑' }
$h = $app.MainWindowHandle
$root = $AE::FromHandle($h)
$tc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
$all = @($root.FindAll($TS::Descendants, $tc))

"PID $($app.Id)  窗口 0x$($h.ToString('X'))"
$others = [ST]::Others([uint32]$app.Id, $h)
if ($others.Count -eq 0) { "没有别的窗口压着" } else { $others | ForEach-Object { "压着：「$([ST]::Title($_))」" } }
$r = $root.Current.BoundingRectangle
"窗口矩形 ($([int]$r.X),$([int]$r.Y)) $([int]$r.Width)x$([int]$r.Height)"
"轨头：" + ((@($all | Where-Object { $r2 = $_.Current.BoundingRectangle
    $_.Current.Name -match '^\d{2}$' -and $r2.X -gt 440 -and $r2.X -lt 490 }) |
  Sort-Object { $_.Current.BoundingRectangle.Y } |
  ForEach-Object { "$($_.Current.Name)@$([int]$_.Current.BoundingRectangle.Y)" }) -join ' ')
"音色：" + ((@($all | Where-Object { $_.Current.Name -like '*GM *' }) | ForEach-Object { $_.Current.Name }) -join ' / ')
"曲名那一行：" + ((@($all | Where-Object { $_.Current.BoundingRectangle.Y -lt 130 }) | ForEach-Object { $_.Current.Name }) -join ' | ')

# 收不收得到键：空格一下，看「位置」动不动
function 位置 {
  $lbl = $all | Where-Object { $_.Current.Name -eq '位置' } | Select-Object -First 1
  if (-not $lbl) { return '?' }
  $lr = $lbl.Current.BoundingRectangle
  $v = @($root.FindAll($TS::Descendants, $tc)) | Where-Object { $r3 = $_.Current.BoundingRectangle
      [Math]::Abs($r3.Y - $lr.Y) -lt 14 -and $r3.X -gt $lr.X -and $r3.X -lt ($lr.X + 260) } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if ($v) { $v.Current.Name } else { '?' }
}
[void][ST]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 600
"按空格之前 位置 = $(位置)"
[System.Windows.Forms.SendKeys]::SendWait(' ')
Start-Sleep -Milliseconds 4000
"按空格之后 位置 = $(位置)   （变了就是收得到键）"
[System.Windows.Forms.SendKeys]::SendWait(' ')
Start-Sleep -Milliseconds 600
