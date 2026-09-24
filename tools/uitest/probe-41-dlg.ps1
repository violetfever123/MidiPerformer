# 41 号工单的辅助探针 —— **80 号票改过它的题目**。
#
# 老题目（已作废）：把「打开 MIDI 文件」那个原生框的**子窗口树**倒出来。
# 那个框是从**演奏器窗口**里那颗文件选择器开出来的，而 47 号票按规格把「曲目」行连同那颗
# 选择器一起删了（`PerformerWindow.axaml.cs`）——**这条路上再也不会冒出 `#32770`**。
# 于是老探针的落点没了：那句按 id 取按钮的 `某根编号 (...)` 取到 0 个，`[0]` 上去就是一个
# 「索引超出范围」。它留在 `tools/uitest/` 里是当**凭据**用的（68 号那次搬家的说法：
# 「它是『那两样是实打实的窗口』的唯一凭据」），所以不能删，只能换题目。
#
# 新题目：**把 71 号那条「主窗递歌」的路走一遍，再倒出这条路两端的子树** ——
#   · 主窗工具栏「歌曲库」开出来的那个**模态窗**（`SongList` 在里面；40 号之后主窗一个 ListItem 都没有）
#     —— `verify-41` 新写的 `开一首` 就靠它选中一行 + 回车；
#   · 之后按「演奏」开出来的**演奏器窗口**（曲子是主窗递进去的）。
# 两棵子树都按「类型 / AutomationId / 名字 / 隐不隐」逐行打出来 —— 下一个写脚本的人要按 id 锚元素，
# 先看这儿有什么，别去猜、也别信行号。
#
# 顺带两条**零命中**的活体判据（47 号那桩改版的现场版）：
#   · 演奏器窗口里没有名叫「打开 MIDI…」的按钮（那颗文件选择器没了）；
#   · 曲目那一行（读数的 id 是 `SongValue`）不在演奏器窗口里 —— 名字不写在这儿是**故意的**，
#     80 号票的验收里那一串「按 AutomationId 判据」的 grep 要对这一份**零命中**，删干净与否由
#     `.scratch/probe-47-*.ps1` 那两份（它们要的就是零命中清单）负责。
#
# 用法: pwsh -NoProfile -File tools/uitest/probe-41-dlg.ps1

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
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
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowThreadProcessId")]
  public static extern uint PidOf(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Txt(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
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
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
$proc = Start-Process -FilePath $exe -PassThru
$期限 = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $proc.Refresh() } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $期限)
if ($proc.MainWindowHandle -eq 0) { throw '等不到窗口句柄' }
Start-Sleep -Seconds 4
$h = $proc.MainWindowHandle
$脚本PID = [uint32]$proc.Id
"起了实例 PID $($proc.Id)（主窗 $([W41]::Cls($h)) '$([W41]::Txt($h))'）"

$root = $AE::FromHandle($h)
$期限 = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $期限 -and -not (某根编号 $root 'SongNameBox').Count) {
  Start-Sleep -Milliseconds 500; $root = $AE::FromHandle($h)
}
Start-Sleep -Seconds 2

# app 自己所有可见顶层窗 —— 后面「等哪一个窗口」全靠它（`[P40]::Others` 那份库没在这儿点源，
# 这一份是自包含的探针，只用上面的 W41）。
# ⚠️ 这个函数**故意不加前置逗号**（不用 `return ,$出`）：加了之后 `@(别的顶层窗)` 会**恒等 1 个**
# （那个逗号把结果包成「一个元素」，外面再套 @() 就把它当那一个元素 —— 73 号实测栽过，
# 断言一直报「还剩 1 个」，差点被当成产品缺陷开票）。取件一律 `$x = 别的顶层窗` 之后再读 `.Count`，
# 或者在调用处包 @()；这一份两处调用都包了 @()。
function 别的顶层窗 {
  $出 = @()
  foreach ($w in [W41]::Tops()) {
    if ($w -eq $h) { continue }
    # 变量别叫 $pid —— 那是 PowerShell 的只读自动变量，[ref] 上去会当场抛
    $进程号 = [uint32]0
    [void][W41]::PidOf($w, [ref]$进程号)
    if ($进程号 -eq $脚本PID) { $出 += $w }
  }
  $出
}

# ---------- 1. 主窗：走「歌曲库」把 sm_mol 装上（80 号之后载歌只发生在这里）----------
# 这一步是**必须**的：主窗里没曲子的时候「演奏」是灰的（`PerformerButton.IsEnabled = _song is not null`），
# 对灰按钮 Invoke 抛的是个光秃秃的 System.Exception。
$曲名 = 'sm_mol'
$库钮 = @(某根编号 $root 'LibraryButton')
if ($库钮.Count -ne 1) { throw "工具栏上找不到「歌曲库」那颗按钮（数到 $($库钮.Count) 颗）" }
[void]$库钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(20)
$库窗 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $库窗 -eq [IntPtr]::Zero) {
  foreach ($w in @(别的顶层窗)) {
    try { if ((某根编号 ($AE::FromHandle($w)) 'SongList').Count) { $库窗 = $w; break } } catch { }
  }
  if ($库窗 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($库窗 -eq [IntPtr]::Zero) { throw '点了「歌曲库」之后没等到带 SongList 的那个窗口' }
Start-Sleep -Milliseconds 900
$库根 = $AE::FromHandle($库窗)

"`n=== 曲库模态窗 $([W41]::Cls($库窗)) '$([W41]::Txt($库窗))' 框=$(([W41]::Rect($库窗)) -join ',') ==="
"--- UIA 子树（前 40 个：类型 / id / 名字 / 隐）---"
$i = 0
foreach ($e in @($库根.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))) {
  if ($i++ -ge 40) { break }
  "  {0,-14} id='{1}' 名='{2}' 隐={3}" -f `
    ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''), $e.Current.AutomationId, $e.Current.Name, $e.Current.IsOffscreen
}
"--- 列表行（SongList 里每一行的名字）---"
$行们 = @(某根里 $库根 $CT::ListItem)
"  行数 $($行们.Count)"
foreach ($行 in $行们) {
  $字 = @(某根里 $行 $CT::Text | ForEach-Object { $_.Current.Name }) -join ' / '
  "    id='$($行.Current.AutomationId)' 名='$($行.Current.Name)' → 文字「$字」"
}

# 行的 Name **不是曲名**（是容器的类名）；曲名在行里的 Text 上。挖子树找**正好等于曲名**的那个 Text。
$行 = @($行们 | Where-Object {
  @(某根里 $_ $CT::Text | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0
}) | Select-Object -First 1
if ($null -eq $行) { throw "曲库列表里没有「$曲名」这一行" }
# 回车送给**库窗**：SendKeys 投给「当前有焦点的窗口」（32 号实测：别的程序盖在上面时这一下会敲进别人家），
# 所以先把库窗拽到前台（这一份没点源共用库，Take 就是这一句 SetForegroundWindow）。
# ⚠️ 模态期间拽的必须是**库窗**：主窗这时候是死的。
[void][W41]::SetForegroundWindow($库窗)
Start-Sleep -Milliseconds 500
[void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
[void]$行.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
$期限 = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $期限 -and (@(别的顶层窗) -contains $库窗)) { Start-Sleep -Milliseconds 300 }
if (@(别的顶层窗) -contains $库窗) {
  $页脚 = (@(某根编号 $库根 'StatusText') | ForEach-Object { $_.Current.Name }) -join ' / '
  throw "曲库窗口没关掉 —— 多半是没装上（页脚：「$页脚」）"
}
Start-Sleep -Seconds 4
$格 = @(某根编号 ($AE::FromHandle($h)) 'SongNameBox')
$读到 = if ($格.Count) { $格[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '（没有）' }
"  装上的是「$读到」（期望「$曲名」）"

# ---------- 2. 按「演奏」：主窗把手上的这首递给演奏器 ----------
$开钮 = @(某根编号 ($AE::FromHandle($h)) 'PerformerButton')
if ($开钮.Count -ne 1) { throw "工具栏上找不到「演奏」那颗按钮（数到 $($开钮.Count) 颗）" }
"  「演奏」IsEnabled=$($开钮[0].Current.IsEnabled)（灰的话下面那句 Invoke 只会丢一个光秃秃的 System.Exception）"
[void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(20)
$演奏器 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
  foreach ($w in @(别的顶层窗)) {
    try { if ((某根编号 ($AE::FromHandle($w)) 'StartButton').Count) { $演奏器 = $w; break } } catch { }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($演奏器 -eq [IntPtr]::Zero) { throw '等不到演奏器窗口' }
Start-Sleep -Seconds 2

"`n--- app 的别的顶层窗（此刻）---"
foreach ($w in @(别的顶层窗)) { "  $([W41]::Cls($w)) '$([W41]::Txt($w))' 框=$(([W41]::Rect($w)) -join ',')" }

$根二 = $AE::FromHandle($演奏器)
"`n=== 演奏器窗口 $([W41]::Cls($演奏器)) '$([W41]::Txt($演奏器))' 框=$(([W41]::Rect($演奏器)) -join ',') ==="
"--- UIA 子树（全量：类型 / id / 名字 / 隐）---"
foreach ($e in @($根二.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))) {
  "  {0,-14} id='{1}' 名='{2}' 隐={3}" -f `
    ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''), $e.Current.AutomationId, $e.Current.Name, $e.Current.IsOffscreen
}

# ---------- 3. 两条活体判据：47 号删掉的那两样，现场零命中 ----------
$打的 = @(某根里 $根二 $CT::Button | Where-Object { $_.Current.Name -like '打开*' })
$曲目行 = @(某根编号 $根二 'SongValue')
$提示 = @(某根编号 $根二 'TrackHint')
"`n=== 判据 ==="
"  演奏器窗口里名叫「打开…」的按钮：$($打的.Count) 个（47 号删了那颗文件选择器，该是 0）"
foreach ($b in $打的) { "    ★ 还有：id='$($b.Current.AutomationId)' 名='$($b.Current.Name)'" }
"  曲目那一行（id 是 SongValue 的那个读数）的元素：$($曲目行.Count) 个（该是 0 —— 47 号连同「曲目」行一起删的）"
$提示文 = if ($提示.Count) { $提示[0].Current.Name } else { '（没有）' }
"  提示行（TrackHint）：「$提示文」（该是「只列出单声部轨 · 13 条轨里 8 条可演奏」）"

# 收尾：直接收实例（模态的事都做完了，进程一走窗口全没）
$proc.Refresh()
if (-not $proc.HasExited) { $proc.Kill() }
"`n收了实例 PID $($proc.Id)"
