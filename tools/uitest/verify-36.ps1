# 36 号工单的实机验证：**提示行分两层** + **Esc 放开选中的音（焦点轨不动）**。
#
# 用法: pwsh -NoProfile -File verify-36.ps1     （脚本自己起 app、自己收尾）
#
# 驱动是 26/27 号那一份（UIA 真找元素、清场、摆窗、光标先停别处再点 —— 那套的来龙去脉
# 见 verify-25/26 的抬头），按键那一半借 35 号（窗口级 keybd_event，不走 SendKeys 的焦点路径）。
# 音符的屏幕位置借 21/26 号那份像素扫描（见下面「找音符」）。
#
# 这一票是**两件独立的事**，两边都得量：
#
#   A. **提示行分两层**：屏幕上那一行只显「此刻该看的那一类」——
#      一个音都没选中 = 走带那一层（空格 / Shift+空格 / Ctrl+↑↓ 换轨），
#      选中了音 = 编辑那一层（移时间 / 移音高 / 改时值 / 选前后一个音 / Delete / **Esc 取消选中**）。
#      ToolTip 始终是两行合起来的全文（它是「看全」的唯一出口）。
#      判据是用户 2026-09-20 拍的：**选中集的个数 ≥ 1 就切**（另一选项是「恰好一个才切」）。
#      末尾那条 `Esc 取消选中` 是 **37 号**补写的（动作是这一票做的，可屏幕上当时没有一处
#      说得清它）—— 用户 2026-09-20 定了它归哪儿：「取消选中放在『选中一些音符之后』的那个提示行」。
#
#   B. **Esc 放开选中的音，但不碰焦点轨**（用户同一天的两条要求：
#      「当你单独选中一个音的时候，按 Escape 键可以取消选择这一个音」/
#      「但是焦点轨不能取消选择，必须选一个」）。
#
# 量 B 的关键：**焦点轨必须落在轨 02 上**。焦点轨停在第 1 条轨上的话，
# 「Esc 没碰它」和「Esc 把它清成了第 1 条」在屏幕上长得一模一样，两句话就分不开了。
# 所以这一票的音符扫描**按第 2 条轨扫**（找音符 2）—— §2 点的是 02 轨上的音，
# 读数条那一格「轨」就是 02；§5 放开之后再 Ctrl+→，那一格**还得是 02**。
#
# 减法的两处（都是用户点名要的，不是顺手）：
#   · 撤销 / 重做**从提示行和它的 ToolTip 里都撤走了**，只留「操作」菜单项右侧那一处。
#     26 号当年的正对照（提示行的 ToolTip 里就有 Ctrl+Z）因此**反过来**了，见 §0。
#   · Esc 那一条**一个音都没选中时什么都不做**（不标记 Handled，让键照常往下走），
#     焦点在输入框里时更是整块让开（`OnWindowKeyDown` 开头那句）—— 两条都量了（§6/§7）。
#
# 前提：**非提权**。本脚本不点「开始演奏」，但同样不许提权跑：提权的话 app 继承了高权限令牌，
# 以后谁在这个脚本上加一句「按一下开始试试」就会真的往当前前台窗口发合成按键。宁可从一开始就拦住。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class V36 {
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
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  /// 「这个点上是谁」只说得出句柄，得再补一句它**是谁** —— 抢前台那类问题全靠这个才认得出来。
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "（空）";
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    uint pid; GetWindowThreadProcessIdOut(h, out pid);
    return "句柄 " + h + " 类名'" + c + "' 标题'" + t + "' PID " + pid;
  }
  // 同一个 API 要两种用法（Take 里丢弃 pid、Others 里要 pid），而 `out` 是签名的一部分，
  // 一个声明顶不了两种 —— 于是按 EntryPoint 再来一个。
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
  public static extern uint GetWindowThreadProcessIdOut(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  /// 把窗口抬到**非 topmost 那一层的最上面**（前台和「谁在最上面」是两码事，
  /// 实测过一次：app 已是前台，VS Code 还压在它上面 —— 那样 mouse_event 把点击交给 VS Code）。
  public static void Raise(IntPtr h) {
    SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010);
    SetWindowPos(h, new IntPtr(-2), 0,0,0,0, 0x0001|0x0002|0x0010);
  }
  /// 这个点上到底是哪个窗口。前台说得再对也不算数，**点击只认这个**。
  public static IntPtr At(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  /// 这个点上那个窗口属于哪个进程。判「是不是 app」不判「是不是 $h」：
  /// 菜单一弹出来，点上的就是 app 自己的弹出层（另一个顶层窗、另一个句柄）。
  public static uint PidAt(int x, int y) {
    IntPtr w = At(x, y);
    if (w == IntPtr.Zero) return 0;
    uint pid; GetWindowThreadProcessIdOut(w, out pid); return pid;
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
  /// 挪光标 / 按一下，拆成两步是为了能在中间「先停到别处去」—— 见 PowerShell 那边的 `点`。
  ///
  /// 为什么要先停到别处：**Avalonia 的悬浮提示是 app 自己的另一个顶层窗**，
  /// 它不吃 hit test（`WindowFromPoint` 照报主窗、PID 闸门也照过），可只要它开着，
  /// **第一下点击就什么都不做，第二下才生效**（26 号在 4 处独立复现过）。
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Press() {
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(330);
  }
  /// **按住 Shift 点一下** —— §3 的题面（「Shift 点一个音 = 把它也带上」那条路）。
  /// 修饰键必须和点击**同时**在场：先按下 Shift、点、再松开，
  /// 而且按下和点击之间要隔一下（Avalonia 读的是按下时那一刻的 KeyModifiers）。
  public static void ShiftPress() {
    keybd_event(0x10, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(80);
    mouse_event(0x0002, 0,0,0, IntPtr.Zero); mouse_event(0x0004, 0,0,0, IntPtr.Zero);
    System.Threading.Thread.Sleep(140);
    keybd_event(0x10, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(330);
  }
  /// 按一下再松开。**不用 SendKeys**：它把键投给「当前有焦点的控件」，
  /// 而这几条快捷键恰恰是「不管焦点在谁身上、窗口层先吃掉」（TextBox 除外）——
  /// 要走就得走正常输入队列（keybd_event 进的就是前台窗口那条）。
  public static void Key(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);
    System.Threading.Thread.Sleep(200);
  }
  // 带 Ctrl 的一下（VK_CONTROL = 0x11）。
  public static void Ctrl(byte vk) {
    keybd_event(0x11, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 0, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, 2, IntPtr.Zero);   System.Threading.Thread.Sleep(50);
    keybd_event(0x11, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(300);
  }
  /// app 进程还开着的、够大的可见顶层窗（除了主窗）。模态对话框、演奏器窗都躲在这里面。
  /// **带尺寸下限**：Avalonia 自己会开一些细碎的可见顶层窗（提示、拖拽层），不滤掉天天误报。
  public static List<IntPtr> Others(uint want, IntPtr skip) {
    var list = new List<IntPtr>();
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessIdOut(h, out pid);
      if (pid != want || h == skip || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if ((r.R - r.L) < 80 || (r.B - r.T) < 60) return true;
      list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}
function 断言([string]$名字, $实际, $期望) {
  if ("$实际" -eq "$期望") { "  OK   $名字 = $实际" }
  else { "  FAIL $名字 = $实际（期望 $期望）"; $script:fail++ }
}
# **逐字**比对。`-eq` 在 PowerShell 里对字符串是**不区分大小写**的，比文案得用 `-ceq`：
# 提示行里那几句英文字母（Delete / Shift / Ctrl）大小写写错了，`-eq` 会一路绿灯。
function 断言字([string]$名字, [string]$实际, [string]$期望) {
  if ($实际 -ceq $期望) { "  OK   $名字（$($实际.Length) 字，逐字相同）" }
  else { "  FAIL $名字 读到「$实际」（$($实际.Length) 字），期望「$期望」（$($期望.Length) 字）"; $script:fail++ }
}

# ---------- 起一个干净实例 ----------
$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
Get-Process -Name MidiPerformer -EA SilentlyContinue | ForEach-Object {
  [void][V36]::ShowWindow($_.MainWindowHandle, 9)
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

if (-not [V36]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][V36]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
# 摆进**工作区里**（26 号量出来的坑：这台机器任务栏从 y=1824 起，压在工具栏上就没法悬停/截图）。
# 高一点是为了给 4 条轨的卷帘留地方 —— §2 点的是**第 2 条轨**上的音。
$r1 = & $摆 405 300 2360 1480
$win = & $摆 (810 - [int]$r1.X) (600 - [int]$r1.Y) (4720 - [int]$r1.Width) (2960 - [int]$r1.Height)
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)（底边 $([int]($win.Y + $win.Height))，任务栏从 1824 起）"

# 等主窗真的长出来（曲库那一列在场）再往下走
foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

# 「停车点」= 窗口里那块没有悬浮提示的空地（卷帘区中部）：每次点之前先停这儿，
# 把上一下遗留下来的悬浮提示关掉（见 V36::Move 的注释）。
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 取数 ----------
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 文本([string]$含) { @(找类型 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" }) }
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::Pane) +
    @(找类型 $CT::Custom) + @(找类型 $CT::ListItem)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
function 找文本([string]$名字) {
  @(找类型 $CT::Text | Where-Object { $_.Current.Name -eq $名字 }) | Select-Object -First 1
}
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
function 找行([string]$曲名) {
  @(找类型 $CT::ListItem | Where-Object {
    $t = 行里的文字 $_
    $t.Count -gt 0 -and $t[0].Current.Name -eq $曲名 }) | Select-Object -First 1
}
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 矩形($e) { $e.Current.BoundingRectangle }
# 轨数用「折叠」按钮数：每条轨的头上都有一颗（和 verify-23/26 同一把尺子）
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 焦点是谁 {
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f) { return '（没有焦点元素）' }
  "$($f.Current.ControlType.ProgrammaticName) id='$($f.Current.AutomationId)' 名='$($f.Current.Name)'"
}

# 第 $序 条轨的轨头 —— 拿「折叠」按钮当锚（每条轨一颗，按 Y 排就是轨序）。
# **不用轨号文字当锚**：读数条那一格「轨」也是 '02' 这种两位数字，选中一个音之后
# 它就在 UIA 树里了，`找文本 '02'` 会撞上（第一版就是这么踩的）。
function 轨头([int]$序) {
  $折叠 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' } |
            Sort-Object { $_.Current.BoundingRectangle.Y })
  if ($序 -lt 1 -or $序 -gt $折叠.Count) { throw "第 $序 条轨不在场（共 $($折叠.Count) 条轨）" }
  $折叠[$序 - 1]
}
# 第 $序 条轨的**轨号文字**（'02'）—— 扫描时拿它的 X 当左界，所以得按 Y 挑出这一条轨的那一个。
function 轨号文字([int]$序) {
  $y = (轨头 $序).Current.BoundingRectangle.Y
  $c = @(找类型 $CT::Text | Where-Object {
      $_.Current.Name -ceq ('{0:D2}' -f $序) -and
      [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 40 })
  if ($c.Count -ne 1) { throw "第 $序 条轨的轨号文字不唯一（$($c.Count) 个）" }
  $c[0]
}
# 第 $序 条轨轨头上的「N 音」—— §3 拿它当**「Shift 点真的变成了两个音」**的证人。
function 音数([int]$序) {
  $y = (轨头 $序).Current.BoundingRectangle.Y
  $c = @(找类型 $CT::Text | Where-Object {
      $_.Current.Name -match '^\s*\d+\s*音\s*$' -and
      [Math]::Abs($_.Current.BoundingRectangle.Y - $y) -lt 40 })
  if ($c.Count -ne 1) { throw "第 $序 条轨的音数读数不唯一（$($c.Count) 个）" }
  [int]([regex]::Match($c[0].Current.Name, '\d+').Value)
}

# ---------- 提示行那两行 ----------
# 逐字抄自 Format.cs 的两个常量（§0 会拿 Format.cs 原文独立比对一遍：
# 断言红了要能立刻分清「屏幕错了」还是「脚本抄错了」）。
$预期走带 = '空格 播放/暂停 · Shift + 空格 回跳一小节并播放 · Ctrl + ↑ ↓ 换轨'
$预期编辑 = '← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · Ctrl + ← → 选同轨前/后一个音 · Delete 删除 · Esc 取消选中'
$预期全文 = "$预期走带 · $预期编辑"

function 屏幕那一行 { (按编号 'HintText')[0].Current.Name }
function 提示ToolTip { (按编号 'HintText')[0].Current.HelpText }
# 读数栏在不在场：`ReadoutDetail` 藏起来时**整块不进 UIA 树**（26 号量的），
# 所以「一个音都没选中」这条状态在屏幕上读得出来 —— 这就是「放开」的判据。
function 读数栏在 { @(按编号 'ReadoutPitchText').Count -eq 1 }
function 轨读数 { if (读数栏在) { (按编号 'ReadoutTrackText')[0].Current.Name } else { '(读数栏藏着)' } }
function 音高读数 { if (读数栏在) { (按编号 'ReadoutPitchText')[0].Current.Name } else { '(读数栏藏着)' } }

# ---------- 找音符的像素位置（借 21/26 号那份扫法）----------
# 上界从**这一条轨的轨号文字**往下 110px（跳过轨头那一排和标尺：标尺上小节号的连续段
# 也够长，会被当成音符，悬上去什么都读不到），下界到**下一条轨的轨号**往上 14px。
function 找音符([int]$序 = 1) {
  $左锚 = 轨号文字 $序
  $下一条 = $null
  try { $下一条 = 轨号文字 ($序 + 1) } catch { }
  if (-not $左锚) { throw "找不到第 $序 条轨的轨号 —— 卷帘没起来？" }

  $bmp = New-Object System.Drawing.Bitmap([int]$win.Width, [int]$win.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$win.X, [int]$win.Y, 0, 0, $bmp.Size)
  $g.Dispose()

  $top = [int]($左锚.Current.BoundingRectangle.Y - $win.Y) + 110
  $bot = if ($下一条) { [int]($下一条.Current.BoundingRectangle.Y - $win.Y) - 14 }
         else { [int]($win.Height) - 240 }      # 最后一条轨：退到读数栏上方
  $left = [int]($左锚.Current.BoundingRectangle.X - $win.X) + 4
  $right = [int]($win.Width) - 20
  if ($bot - $top -lt 12) { throw "第 $序 条轨的卷帘只有 $($bot - $top)px 高 —— 扫不出音符" }

  $行 = @()
  for ($y = $top; $y -lt $bot; $y += 2) {
    $最好 = 0; $最好X = -1; $连 = 0; $起 = -1
    for ($x = $left; $x -lt $right; $x += 2) {
      $c = $bmp.GetPixel($x, $y)
      if ((0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) -gt 60) {
        if ($连 -eq 0) { $起 = $x }
        $连 += 2
      } else {
        if ($连 -gt $最好) { $最好 = $连; $最好X = $起 }
        $连 = 0
      }
    }
    if ($连 -gt $最好) { $最好 = $连; $最好X = $起 }
    if ($最好 -ge 24) { $行 += ,@{ Y = $y; X = $最好X; 宽 = $最好 } }
  }
  $bmp.Dispose()

  $并 = @()
  foreach ($b in $行) {
    if ($并.Count -gt 0 -and ($b.Y - $并[-1].末) -le 4) {
      $m = $并[-1]; $m.末 = $b.Y; $m.行数++
      if ($b.宽 -gt $m.宽) { $m.宽 = $b.宽; $m.X = $b.X; $m.Y = $b.Y }
    } else {
      $并 += @{ Y = $b.Y; 末 = $b.Y; X = $b.X; 宽 = $b.宽; 行数 = 1 }
    }
  }
  @($并 | Where-Object { $_.行数 -ge 3 } | ForEach-Object {
      @{ X = [int]($win.X + $_.X + $_.宽 / 2); Y = [int]($win.Y + $_.Y) }
    })
}

# ---------- 清场 / 前台 / 点 ----------
# 悬浮提示不算浮层（它是跟着光标走的一张纸，把光标挪开就散了）；
# 而且**绝对不能拿 Esc 去收它** —— 这一票的 Esc 正是题面，用它清场等于自己把题做了。
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
    $缓冲 = New-Object System.Text.StringBuilder 256
    [void][V36]::GetClassName($w, $缓冲, 256)
    $类 = $缓冲.ToString()
    $是菜单 = $false
    try {
      $el = $AE::FromHandle($w)
      $是菜单 = @($el.FindAll($TS::Descendants, (& $条件 $CT::MenuItem))).Count -gt 0
    } catch { }
    if ($是菜单 -or $类 -like '#32770*') { $要收 += $w }
  }
  , $要收
}
function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][V36]::Take($h)
    if ([V36]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($win.X + $win.Width / 2); $中纵 = [int]($win.Y + $win.Height / 2)
      if ([V36]::GetForegroundWindow() -eq $h -and [V36]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host "    [「$谁」前台是它了，但窗口正中压着 $([V36]::Describe([V36]::At($中横, $中纵))) —— 再抬一次]"
    }
    Start-Sleep -Milliseconds 500
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住（前台是 $([V36]::Describe([V36]::GetForegroundWindow()))）"
}
function 清场([int]$横, [int]$纵, [string]$谁) {
  # **这一票的 `清场` 不发 Esc。** 26 号那份拿 Esc 收浮层，可 Esc 正是这一票的题面：
  # 万一真有浮层开着，那一下会把选中的音放开，后面的断言全成了假绿
  #（§3 的 Delete 会从「删两个」变成「删一个」，而那一节正是拿它当证人的）。
  # 这一票不开菜单（没有菜单要收），所以把光标挪到空地停一会儿就够；
  # 真要有对话框卡着，六轮之后直接抛 —— 不静悄悄往下走。
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    [V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
    if ([V36]::PidAt($横, $纵) -eq $脚本PID) {
      $要收 = 浮层 @([V36]::Others($脚本PID, $h))
      if ($要收.Count -eq 0) { return }
      Write-Host "    ↺「$谁」之前 app 还开着 $($要收.Count) 个浮层（$(($要收 | ForEach-Object { [V36]::Describe($_) }) -join ' / ')）—— 不发 Esc（那是这一票的题面），再挪一次光标"
    } else {
      Write-Host "    ↺「$谁」之前 $横,$纵 上压着 $([V36]::Describe([V36]::At($横, $纵)))（第 $i 次）"
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前清不干净：$横,$纵 上还压着 $([V36]::Describe([V36]::At($横, $纵)))，或者 app 还开着浮层"
}
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V36]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [V36]::Press()
}
function Shift点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [V36]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [V36]::ShiftPress()
}

# **前台只在真掉了时才拽**：`Take` 里的 `SetFocus(h)` 会把焦点打回最后一个输入框。
function 确保前台([string]$谁) {
  if ([V36]::GetForegroundWindow() -eq $h) { return $true }
  "  ！！ 「$谁」之前前台掉了（台面上是 $([V36]::Describe([V36]::GetForegroundWindow()))）—— 拽回来；"
  "     这一下 `Take` 里的 `SetFocus(h)` 会把焦点打回输入框，本节的焦点读数作废。"
  return [V36]::Take($h)
}

# **焦点必须从输入框里挪开再发窗口级按键。**
# `OnWindowKeyDown` 开头那一句「焦点在 TextBox 里就整块让开」是真的（35 号 §6 量过：
# 焦点在小节号框里时空格进了框）。而 `Take`/`点` 这两条路都会把焦点打回输入框 ——
# 焦点留在框里发 Esc，量的是**输入框**怎么处理 Esc，不是 app，那是一条**假绿**。
# 挪到卷帘的滚动区（Pane id='LanesScroll'）：跳小节按回车之后 app 自己就把焦点放那儿
#（33/34 号量的），所以它一定可聚焦。
function 焦点挪出输入框([string]$谁) {
  [void](确保前台 $谁)
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if ($f -and $f.Current.ControlType -eq $CT::Edit) {
    "  ！！ 发键之前焦点在输入框里（id='$($f.Current.AutomationId)'）—— 先挪到卷帘上"
    $卷 = @(按编号 'LanesScroll')
    if ($卷.Count -ne 1) { throw "找不到唯一的 LanesScroll（$($卷.Count) 个）—— 焦点没处挪" }
    [void]$卷[0].SetFocus(); Start-Sleep -Milliseconds 400
  }
}
function 按Esc([string]$在哪) {
  焦点挪出输入框 "在「$在哪」按 Esc"
  "  （按之前：焦点 = $(焦点是谁)；屏幕上那一行 = 「$(屏幕那一行)」；读数栏在 = $(读数栏在)）"
  [V36]::Key(0x1B); Start-Sleep -Milliseconds 500
}
function 按Ctrl右([string]$在哪) {
  焦点挪出输入框 "在「$在哪」按 Ctrl+→"
  [V36]::Ctrl(0x27); Start-Sleep -Milliseconds 600
}
function 按Ctrl下([string]$在哪) {
  焦点挪出输入框 "在「$在哪」按 Ctrl+↓"
  [V36]::Ctrl(0x28); Start-Sleep -Milliseconds 600
}
function 按删除([string]$在哪) {
  焦点挪出输入框 "在「$在哪」按 Delete"
  [V36]::Key(0x2E); Start-Sleep -Milliseconds 700
}
function 按撤销([string]$在哪) {
  焦点挪出输入框 "在「$在哪」按 Ctrl+Z"
  [V36]::Ctrl(0x5A); Start-Sleep -Milliseconds 700
}
function 按钮字 { (按编号 'PlayButton')[0].Current.Name }

$跑完了 = $false
try {

# =====================================================================
"`n=== 0. 干净实例、还没载曲子、一个音都没选中 → 屏幕上应该是**走带那一层** ==="
# =====================================================================
断言 '屏幕上有且只有一个 HintText' (@(按编号 'HintText').Count) 1
断言字 '屏幕上那一行逐字就是走带那一层' (屏幕那一行) $预期走带
# ToolTip 是**两行合起来的全文**（换层不动它）—— UIA 里就是 HelpText，不用悬停就能读。
断言字 '提示行的 ToolTip 逐字就是两行合起来的全文' (提示ToolTip) $预期全文

# 静态对照：屏幕上那两句和 Format.cs 里那两份是不是同一个字符串。
# 红了要能立刻分清「屏幕错了」还是「脚本抄错了」—— 两份独立地比一下。
$码 = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot '..\..\MidiPerformer.Adapters\Presenters\Format.cs')
function 码里的([string]$名) {
  $m = [regex]::Match($码, "$名\s*=\s*([\s\S]*?);")
  if (-not $m.Success) { throw "Format.cs 里没抓到 $名" }
  -join ([regex]::Matches($m.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value })
}
断言字 '代码里走带那一层和脚本里这一份逐字相同（红了先怀疑脚本抄错）' (码里的 'ReadoutHintPerforming') $预期走带
断言字 '代码里编辑那一层和脚本里这一份逐字相同' (码里的 'ReadoutHintEditing') $预期编辑
$m全文 = [regex]::Match($码, 'ReadoutHintTooltip\s*=\s*([\s\S]*?);')
断言真 'ToolTip 那一份是两层拼起来的（不是手抄的第三份全文）' `
  ($m全文.Success -and (($m全文.Groups[1].Value -replace '\s', '') -eq 'ReadoutHintPerforming+"·"+ReadoutHintEditing')) `
  "Format.cs 里 ToolTip 那一行 = $(if ($m全文.Success) { $m全文.Groups[1].Value } else { '抓不到' })"

# 屏幕上写着这两行的元素**只有提示行自己**（第二处就是第二个真相源）。
$两行 = @(找类型 $CT::Text | Where-Object { $_.Current.Name -ceq $预期走带 -or $_.Current.Name -ceq $预期编辑 })
断言 '屏幕上写着这两行的 Text 只有 1 个（提示行自己）' $两行.Count 1

# 撤销 / 重做**撤出去了**（用户：「不需要单独写，直接放到『操作』里面作为提示就可以了」）。
# 26 号当年的正对照是反过来的（那时它证明「提示行的 ToolTip 里就有这对键位」）——
# 36 号把这两条撤走之后，这一条钉的是**没有**。
foreach ($字 in @('撤销', '重做', 'Ctrl+Z', 'Ctrl+Y')) {
  断言真 "提示行与它的 ToolTip 里都没有「$字」" `
    ((屏幕那一行) -notlike "*$字*" -and (提示ToolTip) -notlike "*$字*") '只留「操作」菜单项右侧那一处'
}

# 37 号补写的那条「Esc 取消选中」**只在编辑那一层里**（它只在「已经选中了音」时有意义）——
# 一个音都没选中的时候不该露头。这是它归哪一层的判据。
断言真 '没选中音时屏幕上没有「取消选中」（它归编辑那一层）' ((屏幕那一行) -notlike '*取消选中*') ''

# 一个音都没选中时按 Esc：什么都不该发生（这一下连 Handled 都不该标）。
$按钮0 = 按钮字
按Esc '还没载曲子'
断言 '没载曲子时按 Esc：按钮还是' (按钮字) $按钮0
断言真 '没载曲子时按 Esc：读数栏仍藏着' (-not (读数栏在)) "读数栏在 = $(读数栏在)"
断言字 '没载曲子时按 Esc：屏幕上那一行没变' (屏幕那一行) $预期走带

# ---------- 载入 Carulli（4 条轨：§2 要点的音在第 2 条上）----------
$曲名 = 'Carulli_Duetto_No2_Op4'
$行 = 找行 $曲名
if (-not $行) { throw "曲库里没有「$曲名」" }
if (-not (确保前台 '载歌')) { throw '拽不到前台' }
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
"载入「$曲名」：$(数轨) 条轨"
if ((数轨) -lt 2) { throw "只有 $(数轨) 条轨 —— 这一票要在**第 2 条轨**上量（见抬头）" }

# =====================================================================
"`n=== 1. 载进来了、还是一个音都没选中 → 仍是**走带那一层**（层和读数无关）==="
# =====================================================================
断言字 '载入之后屏幕上那一行还是走带那一层' (屏幕那一行) $预期走带
断言真 '载入之后读数栏仍藏着（一个音都没选中）' (-not (读数栏在)) "读数栏在 = $(读数栏在)"
断言字 '载入之后 ToolTip 还是全文' (提示ToolTip) $预期全文

# =====================================================================
"`n=== 2. 点中第 2 条轨上的一个音 → 换成**编辑那一层** ==="
# =====================================================================
$音2 = @(找音符 2)
"  第 2 条轨扫到 $($音2.Count) 条音符带"
if ($音2.Count -lt 2) { throw "第 2 条轨只扫到 $($音2.Count) 条音符带 —— §3 要点第二个音，至少得两条" }
$甲 = $音2[0]
点 $甲.X $甲.Y "点中 02 轨上的第一个音 $($甲.X),$($甲.Y)"
Start-Sleep -Milliseconds 600
[V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700

断言真 '选中一个音之后读数栏出来了（反证：确实选中了）' (读数栏在) "读数栏在 = $(读数栏在)"
断言真 '读数是这个音的值，不是初始的破折号' ((音高读数) -ne '—') "音高 = $(音高读数)"
断言 '读数条的「轨」是 02（这一票要在第 2 条轨上量焦点轨）' (轨读数) '02'
断言字 '选中之后屏幕上那一行逐字就是编辑那一层' (屏幕那一行) $预期编辑
断言字 '换层**不动 ToolTip**：还是两行合起来的全文' (提示ToolTip) $预期全文
foreach ($片段 in @('← → 移时间', '↑ ↓ 移音高', 'Shift + ← → 改时值', 'Ctrl + ← → 选同轨前/后一个音', 'Delete 删除', 'Esc 取消选中')) {
  断言真 "编辑那一层里有「$片段」" ((屏幕那一行) -like "*$片段*") ''
}
foreach ($片段 in @('播放/暂停', '回跳一小节并播放', '换轨')) {
  断言真 "选中之后屏幕上没有「$片段」（走带那一层让位了）" ((屏幕那一行) -notlike "*$片段*") ''
}

# =====================================================================
"`n=== 3. Shift 点第二个音 → **还是**编辑那一层（判据是 ≥1，不是 ==1）==="
# =====================================================================
# 这一节是「≥1 就切」和「恰好一个才切」的**分水岭**：判据写成 == 1 的话，
# Shift 点完第二下，选中集变成 2，屏幕上那一行会**退回走带那一层**。
#
# 但「选中了 2 个」这件事屏幕上没有别的地方说得出来（读数条只说**主选中**那一个，
# 而主选中是选中集的尾巴 —— ExtendSelection 把新加的那个接在后面，
# 所以音高读数会变成第二个音的值：光看读数，多选和「只选了第二个」长得一样）。
# 所以要**真删一次**才验得出来：Delete 删的是**整批选中**（见 DeleteSelection），
# 删掉 2 个还是 1 个，轨头上的「N 音」立刻说得出来。删完 Ctrl+Z 撤销回来。
$音数前 = 音数 2
$乙 = $音2 | Where-Object { [Math]::Abs($_.Y - $甲.Y) -ge 8 } | Select-Object -First 1
if (-not $乙) { throw '第 2 条轨上找不到**另一个音高**上的音符带 —— 那下面那步的判据就分不开了' }
"  02 轨音数 $音数前；甲 = $($甲.X),$($甲.Y)；乙 = $($乙.X),$($乙.Y)（不同音高）"
Shift点 $乙.X $乙.Y "Shift 点中 02 轨上的第二个音 $($乙.X),$($乙.Y)"
Start-Sleep -Milliseconds 600
[V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700

断言字 'Shift 点第二个音之后，那一层**还是**编辑那一层（≥1 就切）' (屏幕那一行) $预期编辑
断言真 'Shift 点之后读数栏还在（有东西是选中的）' (读数栏在) "读数栏在 = $(读数栏在)"

按删除 'Shift 点出来的那一批'
$音数删 = 音数 2
"  Delete 之后 02 轨音数 $音数删（删之前 $音数前）"
断言 'Delete 删掉了**两个**音（Shift 点真的加进选中集了，不是只换了主选中）' $音数删 ($音数前 - 2)
按撤销 '把刚才那两个音撤回来'
$音数回 = 音数 2
断言 'Ctrl+Z 之后音数回到原样（收尾：不给下一个人留一份改过的曲子）' $音数回 $音数前

# =====================================================================
"`n=== 4. Esc → 放开选中的音，屏幕上那一行回到**走带那一层** ==="
# =====================================================================
# **先明确造出一个「有音选中、焦点轨 = 02」的状态**，不靠上一节留下的：
# §3 末尾那次 Ctrl+Z 会不会把选中集也一起还原，那是撤销那一支的细节，
# 不该拿它当前提（真没还原的话，这一节的 Esc 就是在「本来就没有选中」上量的 —— 假绿）。
# 点一下就完事：点在一个已经选中的音上什么都不改（见 PianoRollLane 的 Body 一支），
# 点在别处它就只选这一个，两种情况都满足「≥1 个选中、焦点轨 = 02」。
点 $甲.X $甲.Y "重新点中 02 轨上那个音 $($甲.X),$($甲.Y)"
Start-Sleep -Milliseconds 600
[V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700
断言真 'Esc 之前确实有音选中（读数栏在场）' (读数栏在) "读数栏在 = $(读数栏在)；轨 = $(轨读数)"
断言字 'Esc 之前那一行是编辑那一层' (屏幕那一行) $预期编辑

按Esc '有音选中的时候'
断言真 'Esc 之后读数栏藏回去了（选中的音放开了）' (-not (读数栏在)) "读数栏在 = $(读数栏在)"
断言字 'Esc 之后屏幕上那一行回到走带那一层' (屏幕那一行) $预期走带
断言字 'Esc 之后 ToolTip 仍是全文（放开选中不动它）' (提示ToolTip) $预期全文

# =====================================================================
"`n=== 5. Esc **没动焦点轨**：放开之后再 Ctrl+→，跳的还是 02 轨上的音 ==="
# =====================================================================
# 这是用户要求 2（「焦点轨不能取消选择，必须选一个」）的判据。
# 量它的关键在于**焦点轨在第 2 条轨上**（§4 那一点就是为它铺的）：
# 要是 Esc 把焦点轨清成了第 1 条，下面这一下 Ctrl+→ 会在 01 轨上跳，
# 读数条的「轨」立刻变成 01 —— 那两句话才分得开。
按Ctrl右 'Esc 放开之后'
断言真 'Ctrl+→ 又选上一个音了（读数栏回来了）' (读数栏在) "读数栏在 = $(读数栏在)"
断言 '焦点轨还是 02（Esc 没把它清掉/换掉）' (轨读数) '02'
断言字 '又选上一个音之后，那一行回到编辑那一层' (屏幕那一行) $预期编辑
# 顺手量一下「换轨换不掉这一层」：换轨动的是焦点轨、不动选中集，所以层不该跟着变。
# 36 号正是把「换轨」归到**走带**那一层的，这一条挡住「有人把它接到焦点轨变化上」。
$层前 = 屏幕那一行
按Ctrl下 '选中着音的时候'
断言字 '选中着音时按 Ctrl+↓ 换轨：那一层没被换掉（它看的是选中集，不是焦点轨）' (屏幕那一行) $层前

# =====================================================================
"`n=== 6. 反证：一个音都没选中时，Esc 什么都不做 ==="
# =====================================================================
# Esc 那一条**只在真有选中时才标记 Handled**（一个音都没有时不吃掉这一下）。
# 症状反过来是这样的：实现成「无条件吞掉 + 无条件重画」，屏幕上什么也看不出来，
# 但别的手势会莫名其妙地少收到一颗键。
按Esc '先把上面选中的那个音放开'
断言真 '第一下 Esc 放开了（读数栏藏回去）' (-not (读数栏在)) "读数栏在 = $(读数栏在)"
断言字 '第一下 Esc 之后那一行是走带那一层' (屏幕那一行) $预期走带
$按钮6 = 按钮字
按Esc '已经没有东西可放开了'
断言真 '第二下 Esc：读数栏还藏着' (-not (读数栏在)) "读数栏在 = $(读数栏在)"
断言字 '第二下 Esc：那一行没变' (屏幕那一行) $预期走带
断言 '第二下 Esc：按钮字没变（这一下没被谁顺手吃掉又做成别的事）' (按钮字) $按钮6

# =====================================================================
"`n=== 7. 反证：焦点在小节号框里时，Esc **不**放开选中的音 ==="
# =====================================================================
# `OnWindowKeyDown` 开头那一句让开输入框（35 号 §6 量过同一件事的另一面）：
# 焦点在框里时，Esc 归输入框自己用。这一条**故意不走 `按Esc`**（那个包装会把焦点挪出输入框），
# 就是为了让焦点留在框里。
$甲7 = @(找音符 2)[0]
点 $甲7.X $甲7.Y "先选中一个音（好有东西可放开）$($甲7.X),$($甲7.Y)"
Start-Sleep -Milliseconds 600
[V36]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700
断言字 '先确认选中了（那一行是编辑那一层）' (屏幕那一行) $预期编辑

$小节框 = (按编号 'JumpBox')[0]
if (-not $小节框) { throw '找不到 JumpBox' }
[void](确保前台 '焦点放进框里')
[void]$小节框.SetFocus(); Start-Sleep -Milliseconds 500
"  （主动把焦点放进小节号框）焦点 = $(焦点是谁)"
[V36]::Key(0x1B); Start-Sleep -Milliseconds 600
断言真 '焦点在框里时按 Esc：选中的音**没**被放开（读数栏还在）' (读数栏在) "读数栏在 = $(读数栏在)；焦点 = $(焦点是谁)"
断言字 '焦点在框里时按 Esc：那一行仍是编辑那一层' (屏幕那一行) $预期编辑

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
}
finally {
  try {
    $落 = @([V36]::Others($脚本PID, $h))
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [V36]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
# 「跑到一半断了」和「全过」必须分得开：断言是 Write-Host 打的，
# 脚本真要在半路抛了，上面的 OK 只覆盖到断点为止。
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
[void][V36]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
