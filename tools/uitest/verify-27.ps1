# 27 号工单的实机验证：轨头那行灰字删干净了、演奏器窗口的常驻风险横幅删干净了、
# 三种「没开始」诊断还在。
#
# 用法: pwsh -NoProfile -File verify-27.ps1     （脚本自己起 app、自己收尾）
#
# 复用 23 号那份驱动、25 号补过的浮层闸门（点之前清场那套的来龙去脉见 verify-25.ps1 抬头）。
# 这一票特有的四个量法：
#   · **「删干净了」不能只看字**：元素整个没了和元素还在只是隐着，UIA 里是两种样子
#     （IsVisible=False 的元素**不进** UIA 树）。所以除了搜「只看这条」，还要按 AutomationId
#     直接找 `LaterText` 这个元素本身 —— 它在树里，就说明只是藏起来了。
#   · **反证**：载一首曲子，先断言 4 个轨头（`Head`）和 4 个音数都在场。
#     不然「灰字 0 条」在空状态下也成立，那是**空过**不是通过。
#   · **右边空出来那一条有多宽**：工单里点名的那条风险（`Grid.Column="9"` 那一格空着，
#     会不会看着像少了点什么）。量法是 Head 的右边缘减去音数的右边缘 = 那片空白的宽度，
#     再扫一遍「这片空白里有没有任何元素」——「真的空」和「少了点东西」是两句话。
#   · **三种诊断**：权限那一句能真的触到（本脚本在非提权下跑，app 继承令牌）；
#     输入法那一句**触不到**（预检第一关就是权限，非提权进程永远走不到第二关）；
#     轨不可弹那一句**按构造成触不到**（下拉框只列可弹的轨，空的时候开始按钮是灰的）。
#     后者两条在工单里如实写着「没验」，不假装。

#     所以判据是「ErrorText 有没有那句话」，不是「有没有多出一个窗口」。
#   · **改名真的动盘**：曲库就是 bin/Debug/net8.0/songs/ 下的一堆 .mproj，改名 = File.Move。
#     所以改完去数文件名，然后**改名改回去**并断言内容哈希一字未动。
#
# ✅ **80 号票已修**（依据：71 号 `4fd9f40`「点『演奏』把主窗那首递给演奏器」+ 78 号把素材种进 Debug 曲库）。
# 第 5 节的 `开一首` 原先按 id 去**演奏器窗口**里取那颗文件选择器 —— 47 号票已按规格把它连同
# 「曲目」行一起删了（`PerformerWindow.axaml.cs`），取到 0 个、`[0]` 上去当场「索引超出范围」。
# 现在改成 **71 号之后的真实路径**：先把那一首装进**主窗**（走「歌曲库」那个模态窗，第 1 节
# 已经把这条路趟平了），再按一次「演奏」，由主窗把它递进去（窗口是复用的，每按一次都重递）。
# ⚠️ **顺序不能反**：主窗里没歌的时候「演奏」是灰的（`PerformerButton.IsEnabled = _song is not null`，42 号票），
# 对灰按钮 Invoke 抛的是个光秃秃的 System.Exception（verify-41 那一趟实测到了这个死法）。
# 除此之外 `SongValue`（「曲目」那一行）也是同一次改版删掉的，所以第 5 节读曲名改读**主窗的曲名框**。
# ⇒ 第 5 节往后（含「没开始」三种诊断）现在**能跑到了**。

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

# ---- 共用库（67 号票）---------------------------------------------------
# 这里原先内联着两份抄件（一份 P/Invoke + 驱动输入 + 清场，一份只管双击和 RectOf）——
# 两份都并进了 tools/uitest/uitest-lib.ps1（库的类名仍是 P40），这边改成点源。
# 点源的那一刻库自己的四条前提检查就跑：没桌面会话 / 有实例在跑 / 分辨率不够 / 没编译 —— 一律抛。
. (Join-Path $PSScriptRoot 'uitest-lib.ps1')
# 搬过来只有一处「路径」要改（不是逻辑）：exe 从 `$PSScriptRoot 上一级变成上两级（.scratch/ → tools/uitest/）。
# 没改的话第一句自查就抛「没找到 …—— 先编译」，一次实机跑就是这么废掉的。
#
# ⚠️ 并库的第二处**非改名**改动（也不是逻辑）：库的第 48 行是 `Set-StrictMode -Version Latest`，
# 而库是被点源的 —— 严格模式按作用域生效、dot-source 就跑在调用方的作用域里，于是**这一份脚本
# 也一并被收紧了**（库抬头 29-32 行写着这桩代价：「调用方要是用了宽松写法，在严格模式下会抛」）。
# 收紧之后本脚本里那批「辅助函数返回集合、调用方读 `.Count`」的写法会当场炸：函数输出会把
# **1 元数组拆成标量、0 元拆成 $null**，而严格模式下标量读 `.Count` 是「找不到属性 Count」。
# 实测：老位置那趟（无严格模式）第 1 节 4 条全绿；搬到新位置点源之后，第 1 节**第一句就死** ——
# 这不是 47 号票那桩「演奏器里的文件选择器没了」，是并库引进来的，所以在这一票里修掉。
# 修法：**凡是从这些函数取值、又读 `.Count` 的地方，自己包一层 `@(...)`**（老文件里大部分
# 本来就有，漏的那十几处是这一票补的，逐处见提交信息）：`.Count` / `[0]` / `foreach` 在两种
# 模式下结果一样，只是不再依赖宽松模式兜底。**辅助函数本身一个字没动。**
# 反面教材（15:37 实测）：给辅助函数加前置逗号 `, @(...)` 看着更「治本」，其实把**管道**废了 ——
# `找类型 $CT::Button | Where-Object {...}` 收到的是「一整个数组」这一个对象，`数轨` 当场
# 数成 1，第 1 节直接 FAIL。所以别往那个方向改。

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$条件 = { param($t) New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }

$fail = 0
function 断言真([string]$名字, [bool]$条件, [string]$原文) {
  if ($条件) { "  OK   $名字（$原文）" } else { "  FAIL $名字（$原文）"; $script:fail++ }
}



# 抛异常也要把自己起的实例收掉。没有这个 trap 的话，一条 throw 就把 app 留在桌面上，
# 下一次跑又撞上下面那条「已经有 MidiPerformer 在跑」的自检 —— 自己给自己下绊子。
$proc = $null
trap {
  Write-Host "`n[异常] $_"
  if ($null -ne $proc) {
    try { $proc.Refresh(); if (-not $proc.HasExited) { $proc.Kill(); Write-Host "收了实例 PID $($proc.Id)" } } catch { }
  }
  break
}

# ---------- 起一个干净实例 ----------
$exe = Join-Path $PSScriptRoot '..\..\MidiPerformer.App\bin\Debug\net8.0\MidiPerformer.exe'
if (-not (Test-Path $exe)) { throw "没找到 $exe —— 先编译" }
# **不替用户关他自己开着的实例。** 从前这里是「无条件把所有 MidiPerformer 都关掉」——
# 桌面上有用户自己开的实例时，那一句就把人家的窗口收走了（verify-40-lib 那条坑说的就是它）。
# 现在改成：有在跑的只报出来、停手，请你自己关。
$在跑的 = @(Get-Process -Name MidiPerformer -EA SilentlyContinue)
if ($在跑的.Count) {
  throw "已经有 MidiPerformer 在跑（PID $(($在跑的 | ForEach-Object { $_.Id }) -join ', ')）—— 先关掉再跑（这个脚本不替你关）"
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

if (-not [P40]::Take($h)) { throw '拽不到前台' }
$摆 = { param($摆X, $摆Y, $摆宽, $摆高)
  [void][P40]::SetWindowPos($h, [IntPtr]::Zero, $摆X, $摆Y, $摆宽, $摆高, 0x0004 -bor 0x0010)
  Start-Sleep -Milliseconds 700
  ($AE::FromHandle($h)).Current.BoundingRectangle
}
$r1 = & $摆 405 450 2360 1520
$win = & $摆 (810 - [int]$r1.X) (900 - [int]$r1.Y) (4720 - [int]$r1.Width) (3040 - [int]$r1.Height)
"窗口 $([int]$win.Width)x$([int]$win.Height) @ $([int]$win.X),$([int]$win.Y)"
if ([int]$win.Width -ne 2360 -or [int]$win.Height -ne 1520) { throw "窗口没摆成 2360x1520（摆完 $([int]$win.Width)x$([int]$win.Height)）" }
foreach ($等 in 1..20) {
  $root = $AE::FromHandle($h)
  if (@($root.FindAll($TS::Descendants, (& $条件 $CT::Edit)) | Where-Object { $_.Current.AutomationId -eq 'SongNameBox' }).Count) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
$root = $AE::FromHandle($h)

# 「停车点」= 窗口里那块没有悬浮提示的空地（卷帘区中部）：每次点之前先停这儿，
# 把上一下遗留下来的悬浮提示关掉。挑卷帘区是因为整条工具栏、轨头、曲库行上的元素
# 大多挂着 ToolTip，而卷帘区只有悬停读数、没有 ToolTip。
$停车点 = @([int]($win.X + $win.Width * 0.6), [int]($win.Y + $win.Height * 0.45))

# ---------- 闸门：前台 + 点上（这两个坑的来龙去脉见 verify-23.ps1 抬头）----------
# 点上干不干净：**只看 PID 是不够的** —— 这一条是这一票里最贵的一个坑，量了五支探针才钉死。
# 菜单的弹出层、存盘/打开的那个原生框（类名 #32770）都是 **app 自己的顶层窗**，
# PID 和主窗一模一样，光比 PID 的闸门全放行。实测到的现场：焦点被放开之后敲了一个
# 没人接的回车，「文件」菜单的弹出层就开在 429,527 296x244 那一块，正好盖住曲库行；
# 第 10 节要点的 708,737 上，UIA 读出来是 `MenuItem「导出」` —— 那一击点的是导出，
# app 弹出「导出 MIDI」的存盘框把整行盖住，后面单击双击全打在框里，那四条红全是这么来的。
# 所以要问的不是「这是不是 app 的窗」，而是「这是不是**主窗**」。
function 净了([int]$横, [int]$纵) {
  if ($横 -lt 0) { return $true }
  if ([P40]::PidAt($横, $纵) -ne $脚本PID) { return $false }
  return ([P40]::WinAt($横, $纵) -eq $h)
}

# app 自己开着的、**该收掉**的浮层：菜单弹出层（里面有 MenuItem）和原生对话框（类名 #32770）。
# 悬浮提示不算 —— 它不是浮层，是跟着光标走的一张纸，把光标挪开就散了；
# 而且**绝对不能拿 Esc 去收它**：改名框开着的时候按 Esc 会把改名取消掉（§7/§8/§9 全废）。
function 浮层([object[]]$别窗) {
  $要收 = @()
  foreach ($w in $别窗) {
    # GetClassName 是 DllImport 那个三参数的（要 StringBuilder 的老签名），直接丢句柄过去
    # 只会得到「找不到参数计数为 1 的重载」—— 而且那是**非终止**错误：脚本照跑，
    # `$类` 是 $null，于是浮层永远收不出东西，还一路绿灯。
    $缓冲 = New-Object System.Text.StringBuilder 256
    [void][P40]::GetClassName($w, $缓冲, 256)
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

# 点/按键之前把场子清干净：抢前台 → 收浮层（Esc）→ 点上只能压着主窗。
# 清不干净就抛 —— 宁可当场红，也不要静悄悄地打在别人的控件上（那正是上面那个坑的形状）。
function 清场([int]$横, [int]$纵, [string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    要前台 $谁
    if (净了 $横 $纵) {
      $要收 = 浮层 @([P40]::Others($脚本PID, $h))
      if ($要收.Count -eq 0) { return }
      $说 = 'app 还开着 ' + $要收.Count + ' 个浮层（' + (($要收 | ForEach-Object { [P40]::Describe($_) }) -join ' / ') + '）'
      Write-Host "    ↺「$谁」之前先收一下：$说（第 $i 次，Esc）"
      [P40]::Key(0x1B)
    } else {
      $在 = [P40]::WinAt($横, $纵)
      $at = $AE::FromPoint([System.Windows.Point]::new([double]$横, [double]$纵))
      $读到 = ''
      if ($at) {
        $读到 = '，UIA 读到 ' + ($at.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') + '「' + $at.Current.Name + '」'
      }
      Write-Host "    ↺「$谁」之前先收一下：$横,$纵 上压着 $([P40]::Describe($在))$读到（第 $i 次）"
      if ($at -and $at.Current.ControlType -eq $CT::MenuItem) { [P40]::Key(0x1B) }
      [P40]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 700
    }
    Start-Sleep -Milliseconds 400
  }
  throw "「$谁」之前清不干净：$横,$纵 上还压着 $([P40]::Describe([P40]::WinAt($横, $纵)))，或者 app 还开着浮层"
}


function 要前台([string]$谁) {
  for ($i = 1; $i -le 6; $i++) {
    [void][P40]::Take($h)
    if ([P40]::GetForegroundWindow() -eq $h) {
      Start-Sleep -Milliseconds 250
      $中横 = [int]($win.X + $win.Width / 2); $中纵 = [int]($win.Y + $win.Height / 2)
      if ([P40]::GetForegroundWindow() -eq $h -and [P40]::PidAt($中横, $中纵) -eq $脚本PID) { return }
      Write-Host "    [「$谁」前台是它了，但窗口正中压着 $([P40]::Describe([P40]::WinAt($中横, $中纵))) —— 再抬一次]"
    }
    Start-Sleep -Milliseconds 500
  }
  throw "「$谁」之前没能让 app 既在前台、又没被压住（前台是 $([P40]::Describe([P40]::GetForegroundWindow()))）"
}
function 点([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  # 先停到窗口里一块没有悬浮提示的空地上，把可能正开着的那张提示关掉，
  # 再挪到目标立刻按下 —— 不这么做的话「第一下点击什么都不做」会一路假红（见 P40::Move 的注释）。
  [P40]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [P40]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [P40]::Press()
}
function 双击([int]$横, [int]$纵, [string]$谁) {
  清场 $横 $纵 $谁
  [P40]::Move($停车点[0], $停车点[1]); Start-Sleep -Milliseconds 500
  [P40]::Move($横, $纵); Start-Sleep -Milliseconds 60
  [P40]::DoublePress()
}
function 按([byte]$键, [string]$谁) { 清场 -1 -1 $谁; [P40]::Key($键) }

# ---------- 取数 ----------
# ⚠️ 这一批返回的是**集合**，但 PowerShell 的函数输出会把 1 元数组拆成标量、0 元拆成 $null；
# 库是点源的、带的 `Set-StrictMode -Version Latest` 又把本脚本一并收紧了（见抬头），
# 于是**读 `.Count` 的地方必须自己包一层 `@(...)`** —— 老文件里绝大多数地方本来就这么写，
# 漏的那十几处是这一票补的（见提交信息）。
# 反面教材（实测踩过，别改回去）：给这些函数加**前置逗号** `, @(...)` 能让赋值拿到数组，
# 但**管道**就废了 —— `找类型 $CT::Button | Where-Object {...}` 从此收到的是「一整个数组」
# 这一个对象，`数轨` 当场数成 1（15:37 那趟整支脚本就是这么废掉的）。
function 找类型([object]$类型) { @($root.FindAll($TS::Descendants, (& $条件 $类型))) }
function 文本([string]$含) { @(找类型 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" }) }
function 按钮([string]$名) { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq $名 }) }
function 按编号([string]$id) {
  @(@(找类型 $CT::Text) + @(找类型 $CT::Button) + @(找类型 $CT::Edit) + @(找类型 $CT::ComboBox)) |
    Where-Object { $_.Current.AutomationId -eq $id }
}
# 轨数用「折叠」按钮数：每条轨的头上都有一颗（和 verify-23 同一把尺子）
function 数轨 { @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' }).Count }
function 行里的文字($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Text))) }
function 行里的框($行) { @($行.FindAll($TS::Descendants, (& $条件 $CT::Edit))) }
# 原先这儿还有 `曲库行` / `找行` / `行名` 三个：它们在**主窗口**里找曲库的行，而 40 号工单
# 把曲库搬进独立窗口之后主窗口一个 ListItem 都没有了（实测 0 个）—— 留着就是三个
# 「调用不报错、永远返回空」的陷阱。找行现在归下面 `开曲库并打开` 管。
function 取值($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function 设值($e, [string]$v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function 矩形($e) { $e.Current.BoundingRectangle }
function 中心($e) { $r = 矩形 $e; @([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) }
function 曲名框 { (按编号 'SongNameBox')[0] }
# 「这一行选中了没有」不能读 `$e.Current.IsSelected` —— 那个属性**不在**
# AutomationElementInformation 上（它在 SelectionItemPattern 里），读出来是 $null，
# 而不带值的 $null 在断言里恒假：那会变成一条**永远红**的假断言。
# 也只能拿它当尺子，因为「点了行内边距之后这一行有没有选中」正是这一票要验的东西。
function 选中了($行) {
  try { $行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
  catch { $false }
}
# 行里「不是名字文字」的那一格：右边那格小字（'没动过' / '改过' / '读不出来'）。
# 它是 TextBlock（不是 TextBox 也不是 Button）—— 单击只是选中、双击会打开、
# 而且**没有 ToolTip**（ToolTip 只挂在名字和删除上），所以是行内最干净的一个落点。
function 小字($行) { (行里的文字 $行)[1] }
function 曲名格($行) { (行里的文字 $行)[0] }


$曲名 = 'Carulli_Duetto_No2_Op4'
$MIDI = 'C:\Users\cao17\Desktop\midiplayer\drywetmidi\Resources\MIDI files\Valid\MultiTrack\Middle\Carulli_Duetto_No2_Op4.mid'

# 这一票不碰曲库（不改名、不删），所以没有 25 号那套备份/还原 —— 只读它、点开一首曲子。
function 按编号全([string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  @($root.FindAll($TS::Descendants, $c))
}
function 某根里([object]$根, [object]$类型) { @($根.FindAll($TS::Descendants, (& $条件 $类型))) }
function 某根编号([object]$根, [string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  @($根.FindAll($TS::Descendants, $c))
}
function 某根文字([object]$根, [string]$含) {
  @(某根里 $根 $CT::Text | Where-Object { $_.Current.Name -like "*$含*" })
}
function 别的顶层窗 { @([P40]::Others($脚本PID, $h)) }
# 80 号票删掉了 `类名` 和 `找原生框` 两个辅助函数 —— 它们只服务过旧版 `开一首`
# （在演奏器窗口里点那颗文件选择器开出来的 `#32770` 原生框，47 号票把按钮连同那条路一起删了）。
# 载歌改成走主窗的曲库模态窗之后，本脚本再没有一处按窗口类名找东西；
# 仍然按 `#32770` 找原生框的只剩抬头那两句注释（说的是**旧的**那条路），留着当历史说明。
function 查别的窗([string]$标) {
  $o = @(别的顶层窗)
  $说 = if ($o.Count) { ($o | ForEach-Object { $r = [P40]::RectOf($_); "$([P40]::Describe($_)) 框=$($r[0]),$($r[1]) $($r[2])x$($r[3])" }) -join ' ;; ' } else { '没有' }
  Write-Host "  [$标] app 的别的顶层窗 $($o.Count) 个：$说"
}

# 「跑到一半断了」和「全过」要分得开（25 号那口坑，见文件尾）。
$跑完了 = $false

try {

# =====================================================================
"`n=== 0. 安全闸：本脚本必须在非提权下跑 ==="
# =====================================================================
# app 是本进程的子进程，令牌一样。**一旦提权**，第 5 节按下「开始演奏」就会真的过预检、
# 真的开始往系统里发按键 —— 发到那时前台的那个窗口上。那不是这一票想干的事，
# 所以宁可不跑：这一条挡不住风险的时候，别的都白说。
$我是管理员 = (New-Object Security.Principal.WindowsPrincipal(
  [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($我是管理员) { throw '本脚本必须在**非提权**下跑：提权之后按下「开始演奏」会真的往系统里发按键' }
Write-Host '  当前非提权 —— app 继承同样的令牌，第 5 节的「开始」必然停在权限那一关'

# =====================================================================
"`n=== 1. 载一首曲子（轨头得先在场，否则下面那些「0 条」都是空过） ==="
# =====================================================================
# ---------- 载曲：40 号工单之后，曲库搬进了独立的模态窗 ----------
# 从前这里是「在主窗口的 ListItem 里选中那一行、SetFocus、回车」。40 号工单把曲库搬进了
# SongLibraryWindow（工具栏「歌曲库」那颗按钮开出来的模态框，`MainWindow.axaml:210`），
# 主窗口里**一个 ListItem 都不剩**（`.scratch/probe-lib-open.ps1` 实测数到 0 个）——
# 老写法永远找不到行，第一步就死。上面 `曲库行` / `找行` / `行名` 三个辅助函数
# 就是那条死路上的东西，一并删了：留着的话，下一个读的人会以为主窗口还有列表可找。
#
# 新路（探针全程量过）：点「歌曲库」→ 模态窗的 SongList 里找到那一行 → 把库窗拽到前台
# → 选中 + 回车 → 窗口自己关掉、曲子装上（装不上窗口不关，见 `MainWindow.axaml.cs:397`）。
#
# 三处只有踩过才知道的：
#   · **行的 Name 不是曲名**，是容器的类名（实测 'Avalonia.Controls.Grid'），
#     曲名在行里面的 Text 上。别拿 `$_.Current.Name` 比。
#   · 也不能拿「行里所有文字拼起来的串」比 —— 那是 '曲名 / 没动过'，多一格状态字。
#     要挖子树找「正好等于曲名」的那个 Text。
#   · 回车之前拽的是**库窗**、不是主窗。模态期间主窗是死的（`要前台` 死抠 `$h`，
#     会一直等到超时），焦点给主窗这一下就白敲了。
function 库元素([object]$根, [object]$条件) { @($根.FindAll($TS::Descendants, $条件)) }
function 库类型([object]$根, [object]$类型) {
  库元素 $根 (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $类型))
}
function 库编号([object]$根, [string]$id) {
  库元素 $根 (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function 开曲库并打开([string]$曲名) {
  $库钮 = @(库编号 ($AE::FromHandle($h)) 'LibraryButton')
  if ($库钮.Count -ne 1) { throw "工具栏上找不到「歌曲库」那颗按钮（按 AutomationId=LibraryButton 数到 $($库钮.Count) 颗）" }
  [void]$库钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  # 等模态窗：它是 app 的另一个可见顶层窗，里面有 SongList
  $期限 = (Get-Date).AddSeconds(20)
  $库窗 = [IntPtr]::Zero
  while ((Get-Date) -lt $期限 -and $库窗 -eq [IntPtr]::Zero) {
    foreach ($w in [P40]::Others($脚本PID, $h)) {
      try { if (@(库编号 ($AE::FromHandle($w)) 'SongList').Count -gt 0) { $库窗 = $w; break } } catch { }
    }
    if ($库窗 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
  }
  if ($库窗 -eq [IntPtr]::Zero) { throw '点了「歌曲库」之后没等到带 SongList 的那个窗口' }
  Start-Sleep -Milliseconds 800
  $库根 = $AE::FromHandle($库窗)

  # 列表是异步填的：轮询等那一行出现
  $期限 = (Get-Date).AddSeconds(20)
  $行 = $null
  while ((Get-Date) -lt $期限 -and $null -eq $行) {
    $行 = @(库类型 $库根 $CT::ListItem | Where-Object {
      @(库类型 $_ $CT::Text | Where-Object { $_.Current.Name -eq $曲名 }).Count -gt 0
    }) | Select-Object -First 1
    if ($null -eq $行) { Start-Sleep -Milliseconds 400 }
  }
  if ($null -eq $行) {
    $在 = (@(库类型 $库根 $CT::ListItem | ForEach-Object {
      @(库类型 $_ $CT::Text | ForEach-Object { $_.Current.Name }) }) -join ' / ')
    throw "曲库里没有「$曲名」这一行（现在有：$在）"
  }

  # 拽的是库窗：SendKeys 投给「当前有焦点的窗口」，别的程序盖在上面时这一下会往
  # 人家的编辑器里敲一个回车（32 号实测栽过）。
  [void][P40]::Take($库窗)
  [void]$行.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 400
  [void]$行.SetFocus()
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

  # 装上了才关窗：读不出来会把话写进页脚、窗口留着 —— 窗口不走就是没装上
  $期限 = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $期限 -and (@([P40]::Others($脚本PID, $h)) -contains $库窗)) { Start-Sleep -Milliseconds 300 }
  if (@([P40]::Others($脚本PID, $h)) -contains $库窗) {
    $页脚 = (库编号 $库根 'StatusText' | ForEach-Object { $_.Current.Name }) -join ' / '
    throw "曲库窗口没关掉 —— 多半是没装上（页脚：「$页脚」）"
  }
}
开曲库并打开 $曲名
Start-Sleep -Seconds 5
$轨数 = 数轨
断言真 '曲子真的载进来了（4 条轨的轨头都在场）' ($轨数 -eq 4) "$轨数 条轨"

# =====================================================================
"`n=== 2. 轨头那行灰字：删掉了，还是只是藏起来了 ==="
# =====================================================================
# 两种判据都要：搜文字（用户看得见的那一句）+ 按 AutomationId 找元素本身。
# 只搜文字的话，「元素还在、只是 IsVisible=False」也会是 0 条（隐藏的元素不进 UIA 树）——
# 那是两种不同的交付，工单要的是**删掉**。
$灰 = @(文本 '只看这条')
断言真 '「只看这条」这几个字一条都不剩' ($灰.Count -eq 0) "数到 $($灰.Count) 条"
$还没 = @(文本 '还没做')
断言真 '「还没做」也一条都不剩（别的控件别处也没有这句）' ($还没.Count -eq 0) "数到 $($还没.Count) 条"
$元素 = @(按编号全 'LaterText')
断言真 'LaterText 这个元素本身不在 UIA 树里了（不是仅隐藏）' ($元素.Count -eq 0) "数到 $($元素.Count) 个"

# 反证：上面三条在「一条轨都没有」的空窗口上照样成立。轨头真在场，它们才说明问题。
#
# 轨头这一格**不能按 AutomationId 找**，两条路都堵死（都是实测）：
#   · `Head` 是个 Border —— 它根本不进 UIA 树（按 id 数到 0 个）；
#   · `CountText` **撞车** —— 曲库面板底部那个「2 首」也叫 CountText，全窗口数到 5 个。
# 所以改成**从每条轨都有的「折叠」按钮往上爬**：轨头那一行横跨整条轨，
# 往上第一个「宽到几百像素、里面又只读得到一个 CountText」的祖先就是它。
$走 = [System.Windows.Automation.TreeWalker]::RawViewWalker
$头们 = @()
$数们 = @()
$折们 = @(找类型 $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })
foreach ($折 in $折们) {
  $p = $折
  $层 = @()
  for ($i = 0; $i -lt 8; $i++) {
    $p = $走.GetParent($p)
    if (-not $p) { break }
    $层 += $p
  }
  $好 = $null; $好数 = $null
  foreach ($q in $层) {
    $n = @(某根编号 $q 'CountText')
    if ($n.Count -eq 1 -and (矩形 $q).Width -gt 600) { $好 = $q; $好数 = $n[0]; break }
  }
  if ($好) { $头们 += $好; $数们 += $好数 } else { Write-Host "    ★ 有一颗「折叠」往上找不到轨头行（爬了 $($层.Count) 层）" }
}
断言真 '反证：4 条轨的轨头行都找得到（每颗「折叠」往上都有一行横跨整轨的容器）' ($折们.Count -eq 4 -and $头们.Count -eq 4) "折叠按钮 $($折们.Count) 个，轨头行 $($头们.Count) 个"
断言真 '反证：每个轨头里都读得到一个音数（CountText）' ($数们.Count -eq 4) "数到 $($数们.Count) 个"
if ($数们.Count) { Write-Host "    音数那一格现在写的是「$($数们[0].Current.Name)」" }

# =====================================================================
"`n=== 3. 右边空出来那一条有多宽（工单点名要看的那一眼） ==="
# =====================================================================
# 工单里的担心：`Grid.Column="9"` 那一格空着（列定义没删，是为了不动九处 Grid.Column），
# 会不会在轨头右边留出一条「看着像少了点什么」的空白。
# 量法：Head 的右边缘 − 音数的右边缘 = 从最后一个真元素到行尾的距离；
# 再扫一遍这片矩形里有没有任何元素 —— 「真的空」和「少了点东西」是两句话。
$量过 = @()
foreach ($头 in $头们) {
  $r头 = 矩形 $头
  $格 = @(某根编号 $头 'CountText')
  if ($格.Count -ne 1) { Write-Host "    ★ 这个轨头里有 $($格.Count) 个音数格，跳过"; continue }
  $r音 = 矩形 $格[0]
  $空白左 = [int]($r音.X + $r音.Width)
  $空白右 = [int]($r头.X + $r头.Width)
  $空白宽 = $空白右 - $空白左
  # 这片空白里还有没有东西：
  $里头 = @(某根里 $头 $CT::Text) + @(某根里 $头 $CT::Button) + @(某根里 $头 $CT::Edit) + @(某根里 $头 $CT::ComboBox)
  $闯进 = @($里头 | Where-Object {
    $r = 矩形 $_
    $右 = [int]($r.X + $r.Width)
    $_.Current.AutomationId -ne 'CountText' -and $右 -gt $空白左 + 1 -and [int]$r.X -lt $空白右 - 1
  })
  $量过 += [pscustomobject]@{
    头 = "$([int]$r头.X),$([int]$r头.Y)"
    头宽 = [int]$r头.Width
    音数右 = $空白左
    空白宽 = $空白宽
    空白里的元素 = $闯进.Count
  }
  Write-Host "    轨头 @$([int]$r头.X),$([int]$r头.Y) 宽 $([int]$r头.Width)：音数右边缘 $空白左，行右边缘 $空白右"
  Write-Host "      → 右边空出 $空白宽 px；这片空白里的元素 $($闯进.Count) 个$(if($闯进.Count){'：' + (($闯进 | ForEach-Object { "id='$($_.Current.AutomationId)' 名='$($_.Current.Name)'" }) -join ' / ')})"
}
断言真 '每个轨头右边那片空白里都是真的空（一个元素都没有）' ($量过.Count -eq 4 -and @($量过 | Where-Object { $_.空白里的元素 -ne 0 }).Count -eq 0) "量到 $($量过.Count) 个轨头：$(($量过 | ForEach-Object { "$($_.空白宽)px" }) -join ' / ')"
Write-Host '    （「看着像不像少了点东西」这一条，量到的是「那片像素上什么都没有」；好不好看归人工）'

# =====================================================================
"`n=== 4. 演奏器窗口：常驻风险横幅没了、状态行还在 ==="
# =====================================================================
# 按 AutomationId 找，**不按文字找**：40 号工单把这颗按钮的 Content 从「演奏器…」改成了「演奏」
# （`MainWindow.axaml:229`，那里的注释也写着「x:Name 叫 PerformerButton —— 有测试按这个名字找它」）。
# 按文字找的话，按钮好端端在场，这条断言却是红的 —— 量到 0 颗，看着像入口被人删了。
$开钮 = @(某根编号 $root 'PerformerButton')
断言真 '工具栏上那颗「演奏」还在（入口没被顺手删掉）' ($开钮.Count -eq 1) "数到 $($开钮.Count) 颗"
[void]$开钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$期限 = (Get-Date).AddSeconds(20)
$演奏器 = [IntPtr]::Zero
while ((Get-Date) -lt $期限 -and $演奏器 -eq [IntPtr]::Zero) {
  foreach ($w in 别的顶层窗) {
    try { if (@(某根编号 ($AE::FromHandle($w)) 'StartButton').Count -gt 0) { $演奏器 = $w; break } } catch { }
  }
  if ($演奏器 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($演奏器 -eq [IntPtr]::Zero) { throw '等不到演奏器窗口（按了「演奏」之后没多出 StartButton 那个顶层窗）' }
Start-Sleep -Seconds 2
$根二 = $AE::FromHandle($演奏器)
$r二 = [P40]::RectOf($演奏器)
断言真 '演奏器窗口真的开出来了' ($r二[2] -gt 300) "$([P40]::Describe($演奏器)) 框=$($r二[0]),$($r二[1]) $($r二[2])x$($r二[3])"

# 横幅那一整块的两句话，一个字都不该剩。窗口子树里搜 + 主窗口子树里也搜一遍
# （万一有人把它挪到主窗口去，只搜演奏器窗口就会漏掉）。
$横幅一 = @(某根文字 $根二 '虚拟输入违反游戏规则') + @(某根文字 $root '虚拟输入违反游戏规则')
$横幅二 = @(某根文字 $根二 '本程序只做一件事') + @(某根文字 $root '本程序只做一件事')
$横幅三 = @(某根文字 $根二 '风险自负') + @(某根文字 $root '风险自负')
断言真 '「虚拟输入违反游戏规则，可能导致封号」那一句没了' ($横幅一.Count -eq 0) "数到 $($横幅一.Count) 条"
断言真 '「本程序只做一件事：往系统里发按键…」那一句也没了' ($横幅二.Count -eq 0) "数到 $($横幅二.Count) 条"
断言真 '整个程序里再也搜不到「风险自负」' ($横幅三.Count -eq 0) "数到 $($横幅三.Count) 条"

# 删横幅不该把「为什么没开始」的通道一起带走。
$状 = @(某根编号 $根二 'StatusText')
断言真 '状态行还在（横幅删了没把它一起带走）' ($状.Count -eq 1) "数到 $($状.Count) 个"
if ($状.Count -eq 1) {
  $r状 = 矩形 $状[0]
  $窗口底 = $r二[1] + $r二[3]
  $状态底 = [int]($r状.Y + $r状.Height)
  Write-Host "    窗口 430x$($r二[3])，底边 y=$窗口底；状态行 y=$([int]$r状.Y) 高 $([int]$r状.Height)，底边 y=$状态底"
  Write-Host "      → 状态行下面还剩 $($窗口底 - $状态底) px（StackPanel 的 Margin=14 就是它）"
  断言真 '状态行可见（不是藏在窗口外面）' (-not $状[0].Current.IsOffscreen) "IsOffscreen=$($状[0].Current.IsOffscreen)"
  断言真 '状态行整块落在窗口矩形里' ($状态底 -le $窗口底 -and [int]$r状.Y -ge $r二[1]) "状态行底 $状态底 ≤ 窗口底 $窗口底"
  # 「窗口底下面就是状态行」= 没有元素被挤到可视区外面去。窗口是 SizeToContent="Height"，
  # 高度就是内容撑出来的，所以这一条量的是「撑出来的高度装得下所有东西」。
  $越界 = @()
  foreach ($t in @(某根里 $根二 $CT::Text) + @(某根里 $根二 $CT::Button) + @(某根里 $根二 $CT::ComboBox) + @(某根里 $根二 $CT::Edit)) {
    $r = 矩形 $t
    if (-not $t.Current.IsOffscreen -and [int]($r.Y + $r.Height) -gt $窗口底 + 1) { $越界 += $t }
  }
  断言真 '没有控件被顶到窗口底边外面去（SizeToContent 撑得下）' ($越界.Count -eq 0) "越界的 $($越界.Count) 个"
}

# =====================================================================
"`n=== 5. 「没开始」三种诊断：能触到的那一种，逐字量 ==="
# =====================================================================
# 预检的顺序是**权限 → 输入法 → 有没有能弹的轨**（见 PerformancePreflight 的类注释）。
# 本脚本非提权，所以按下去必然停在第一关 —— 这也顺带把「顺序」这件事实测了一遍：
# 后面两关的文案这次一个字都读不到。
$预期 = '没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。'

# ---------- 把「载一首 + 递给演奏器」封成一个函数 ----------
# **47 号之后演奏器里没有文件选择器了**（「曲目」行连同那颗按钮一起删的），曲子由**主窗递进来**（71 号）。
# 所以「载一首」= ① 在主窗的曲库模态窗里装上那一首（第 1 节的 `开曲库并打开`，那条路已经趟平了），
# ② 按一次「演奏」把它重递进去 —— 窗口是**复用**的，每按一次都要重递一次；换了曲子再按，
# 窗口里必须换成新那首（`App.axaml.cs` 的 `PerformerFactory` + `MainWindow.axaml.cs` 的 `OnPerformerClick`）。
#
# ⚠️ **顺序不能反**：主窗里没曲子的时候「演奏」是灰的（`PerformerButton.IsEnabled = _song is not null`），
# 对灰按钮 Invoke 抛的是个**光秃秃的 System.Exception** —— 屏幕上哪一颗灰、为什么灰，那句话里一个字都没有。
# （verify-41 那一趟就是死在这个死法上，所以先在函数里自己判一次、报清楚。）
function 开一首([string]$曲名) {
  开曲库并打开 $曲名
  Start-Sleep -Seconds 5
  $钮 = @(某根编号 ($AE::FromHandle($h)) 'PerformerButton')
  if ($钮.Count -ne 1) { throw "工具栏上找不到「演奏」那颗按钮（按 AutomationId=PerformerButton 数到 $($钮.Count) 颗）" }
  if (-not $钮[0].Current.IsEnabled) { throw '「演奏」是灰的 —— 主窗里没装上曲子（IsEnabled = _song is not null）' }
  [void]$钮[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 3
}

# 哪一首能弹**不能假设**：实测 Carulli_Duetto_No2_Op4 载进去之后「开始演奏」是灰的、
# 提示行整条不显示 —— 那是空状态块顶上来了（「这首歌没有可演奏的单声部轨」）。
# 所以按候选表一首一首试，试到有一首「开始」能按为止。这一步只是为了拿到一个能按的开始键，
# 与这一票要验的东西无关；试不动的那几首把窗口里的话原样打出来，省得回头猜。
#
# ⚠️ 候选表**只能是曲库里有的**（`songs\` 下那几份 .mid）：71 号之后曲子只从曲库来，
# 原先那串语料文件名（65tmmntw / Hymn-Nr-05 / pilgrim）在这条路上够不着了。
# 表里第一首是已知能弹的（sm_mol：78 号量的 8 条可弹），后面几首留着兜底。
$候补 = @('sm_mol', 'Carulli_Duetto_No2_Op4', 'cargo', '（三角洲适配）勾指起誓')
$载入的 = $null
$成功文件 = $null
$提示文 = '（还没载入）'
foreach ($f in $候补) {
  开一首 $f
  $根二 = $AE::FromHandle($演奏器)
  # 「载进来的是哪一首」读**主窗的曲名框**：曲目那一行（`SongValue`）跟演奏器里那颗文件选择器
  # 是同一次改版（47 号）删掉的，演奏器窗口里已经没有一个能读的曲名了。
  # 曲名框是 TextBox，字在 ValuePattern 上，不在 `Current.Name` 上。
  $名字格 = @(某根编号 ($AE::FromHandle($h)) 'SongNameBox')
  $曲名读到 = if ($名字格.Count) { 取值 $名字格[0] } else { '（没有）' }
  $轨提示 = @(某根编号 $根二 'TrackHint')
  $提示文 = if ($轨提示.Count) { $轨提示[0].Current.Name } else { '（没有提示行）' }
  $开2 = @(某根编号 $根二 'StartButton')
  $能按 = if ($开2.Count) { $开2[0].Current.IsEnabled } else { $false }
  Write-Host "  载入「$曲名读到」：提示行「$提示文」，开始可按=$能按"
  if ($能按) { $载入的 = $曲名读到; $成功文件 = $f; break }
  $看见 = (某根里 $根二 $CT::Text | ForEach-Object { $_.Current.Name }) -join ' | '
  Write-Host "    → 这一首按不动，窗口里现在写着：$看见"
}
断言真 '至少载进来一首能弹的（不然下面那条「按开始」根本没得按）' ($null -ne $载入的) "最后载入的是「$载入的」"
if ($成功文件) {
  # 曲库里的名字就是文件名去掉 .mid，所以这里不用再削尾巴（从前那版要削）
  断言真 '载进来的是**点的那一首**（不是列表里别的那一行）' ($载入的 -eq $成功文件) "点了「$成功文件」，主窗曲名框写的是「$载入的」"
}
断言真 '载入之后提示行报出了轨数与可弹数' ($提示文 -like '只列出单声部轨 · *') "「$提示文」"

$根二 = $AE::FromHandle($演奏器)
$开始 = @(某根编号 $根二 'StartButton')
if ($开始.Count -ne 1 -or -not $开始[0].Current.IsEnabled) {
  throw '「开始演奏」还是灰的 —— 上面那条断言已经红了，别让下面那句「没按动」看起来像「按了没反应」'
}
$前 = @(别的顶层窗).Count
[void]$开始[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$根二 = $AE::FromHandle($演奏器)
$状 = @(某根编号 $根二 'StatusText')
$读到 = $状[0].Current.Name
Write-Host "  状态行现在写的是：`n    「$读到」"
断言真 '按下去停在权限那一关，状态行逐字就是那句中文' ($读到 -eq $预期) "读到「$读到」"
$后 = @(别的顶层窗).Count
断言真 '预检不放行时没有多拉起悬浮层（不该多出置顶窗口）' ($后 -eq $前) "按之前 $前 个别的顶层窗，按之后 $后 个"
if ($后 -ne $前) { 查别的窗 '按之后' }

  $跑完了 = $true
}
catch {
  Write-Host "`n★ 脚本跑到一半抛了：$_"
  查别的窗 '抛的时候'
}
finally {
  # 这个脚本不写盘（曲库只读、MIDI 只读），所以没有 25 号那套还原。
  # 要收的只有 app 自己：连它一起开着的那个原生框、那个置顶的演奏器窗口。
  try {
    $落 = @(别的顶层窗)
    if ($落.Count) { Write-Host "`n收尾：还开着的别的顶层窗 $(($落 | ForEach-Object { [P40]::Describe($_) }) -join ' ;; ')" }
  } catch { }
}

"`n========== 结果 =========="
# 「跑到一半断了」和「全过」必须分得开：断言是 Write-Host 打的、收尾这几句是输出流，
# 脚本真要是在半路抛了，上面的 OK 只覆盖到断点为止 —— 25 号实测栽过一回。
if (-not $跑完了) {
  '★ 跑到一半断了：断点之后的每一条都没验过，这不是全过'
  $fail = $fail + 1
} elseif ($fail -eq 0) { '全过' } else { "$fail 条红" }
# 81 号票：裁决行 —— run-all.ps1 拿它跟退出码复核，对不上就把这一条降级成红（别删）。
"==== uitest 裁决 不过=$fail"
[void][P40]::ShowWindow($h, 9)
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
exit $fail
