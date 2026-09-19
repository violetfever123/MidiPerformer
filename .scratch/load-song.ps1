# 把曲库里那一首 Carulli 双击打开。之后 verify-21 才有东西可测。
# 用法: pwsh -NoProfile -File load-song.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class L21 {
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
  public static bool Take(IntPtr h) {
    IntPtr fg = GetForegroundWindow();
    uint a = GetWindowThreadProcessId(fg, IntPtr.Zero), b = GetWindowThreadProcessId(h, IntPtr.Zero);
    AttachThreadInput(a, b, true);
    keybd_event(0x12,0,0,IntPtr.Zero); keybd_event(0x12,0,2,IntPtr.Zero);
    SetForegroundWindow(h);
    if (GetForegroundWindow() != h) ShowWindow(h, 9);
    if (GetForegroundWindow() != h) {
      SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0040);
      SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0040);
    }
    AttachThreadInput(a, b, false);
    SetFocus(h); System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
  }
  public static void DoubleClick(int x, int y) { Click(x,y); System.Threading.Thread.Sleep(90); Click(x,y); }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$C = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$p = Get-Process -Name MidiPerformer -EA SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
if (-not $p) { throw 'MidiPerformer 没在跑' }
$h = $p.MainWindowHandle
$root = $AE::FromHandle($h)

# 曲库那一行：找写着 Carulli 的那个 ListBoxItem，点它中间
$row = $root.FindAll($TS::Descendants, (& $C $CT::ListItem)) |
  Where-Object { $_.Current.Name -eq 'Avalonia.Controls.Grid' } |
  Where-Object {
    $r = $_.Current.BoundingRectangle
    $t = $_.FindAll($TS::Descendants, (& $C $CT::Text)) | Where-Object { $_.Current.Name -like 'Carulli*' }
    $t.Count -gt 0
  } | Select-Object -First 1
if (-not $row) { throw '曲库里没有 Carulli 那一行' }

$r = $row.Current.BoundingRectangle
"曲库行 @ $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
if (-not [L21]::Take($h)) { throw '拽不到前台' }
[L21]::DoubleClick([int]($r.X + 60), [int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 3

$播放 = $root.FindAll($TS::Descendants, (& $C $CT::Button)) | Where-Object { $_.Current.AutomationId -eq 'PlayButton' } | Select-Object -First 1
"播放键可用 = $($播放.Current.IsEnabled)"
$空 = $root.FindAll($TS::Descendants, (& $C $CT::Text)) | Where-Object { $_.Current.Name -like '还没有曲子*' }
"空状态提示还在吗 = $($空.Count -gt 0)"
