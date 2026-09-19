# 折叠**不跨曲子漏过去**。
#
# 折叠以前只是「看不见」，漏过去最多是屏幕上一条轨莫名其妙收着；现在还兼着「不出声」，
# 漏过去就成了「打开一首新曲子，某条轨是哑的」。所以 SyncLanes 那条重建分支改成：
# 同一首曲子的新一份（编辑 / 撤销）照旧按身份把折叠带过去，**换一首曲子一律不带**。
#
# 这一条怎么量得动：从曲库里**再打开当前这首**（走的就是 LoadSong → rebuildAll: true，
# 和换一首曲子是同一条路），收着的那条轨该自己弹开。老行为下它会保持收着 ——
# 同一份谱面里 (轨块, 声道) 那一对不变，身份还认得出它。
#
# 先跑 open-app.ps1 把窗口和一首曲子准备好，再跑这个。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W5 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$FOLDED = '已折叠 · 不发声'

function Get-App {
  Get-Process -Name dotnet, MidiPerformer -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
}

function Get-Root {
  $p = Get-App
  if (-not $p) { throw 'MidiPerformer 没在跑' }
  return $AE::FromHandle($p.MainWindowHandle)
}

function Find-All($root, $type) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $cond)
}

function Fold-Names($root) {
  return @(Find-All $root $CT::Button |
    Where-Object { $_.Current.Name -in @('折叠', '展开') } |
    ForEach-Object { $_.Current.Name })
}

function Texts-Of($e) {
  return @(Find-All $e $CT::Text | ForEach-Object { $_.Current.Name })
}

# 曲名框里写的那个名字 —— 就是当前开着的这一首在曲库里的名字
function Current-Song($root) {
  return (Find-All $root $CT::Edit | Select-Object -First 1).GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

# ---------- 先收起来一条 ----------
"===== 收起第二条轨 ====="
$root = Get-Root

# 上一趟留下的折叠状态先摊平（跑第二遍时脚本不该依赖上一遍收到哪儿了）
foreach ($b in @(Find-All $root $CT::Button | Where-Object { $_.Current.Name -eq '展开' })) {
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 400
}
$root = Get-Root
$names0 = Fold-Names $root
"收起前: $($names0 -join ', ')"
if ($names0.Count -lt 2) { throw '这个窗口里没有两条以上的轨' }

$second = @(Find-All $root $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })[1]
$second.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1000
"收起后: $((Fold-Names (Get-Root)) -join ', ')"
if ((Fold-Names (Get-Root))[1] -ne '展开') { throw '第二条轨没收起来' }
""

# ---------- 从曲库里再打开当前这首 ----------
$root = Get-Root
$song = Current-Song $root
"曲名框 = '$song'"

$row = Find-All $root $CT::ListItem |
  Where-Object { (Texts-Of $_) -contains $song } | Select-Object -First 1
if (-not $row) { throw "曲库里找不到 '$song' 那一行" }
"点开曲库里那一行: '$((Texts-Of $row) -join ' | ')'"
""
"===== 重新装一遍（= 换一首曲子那条重建路）====="

# 单击只选中、双击/回车才打开（见 SongLibraryPanel.OnRowDoubleTapped）
$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$row.SetFocus()
Start-Sleep -Milliseconds 400
[void][W5]::SetForegroundWindow((Get-App).MainWindowHandle)
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 3

$root = Get-Root
$names2 = Fold-Names $root
"重新装完: $($names2 -join ', ')"
"'$FOLDED' 剩 $((@(Find-All $root $CT::Text | Where-Object { $_.Current.Name -eq $FOLDED })).Count) 处"
$leak = @($names2 | Where-Object { $_ -eq '展开' }).Count
""
if ($names2.Count -eq $names0.Count) {
  if ($leak -eq 0) { "没漏过去：$($names2.Count) 条轨全是「折叠」" }
  else { "漏过去了：还有 $leak 条轨收着" }
} else {
  "轨数变了（$($names0.Count) -> $($names2.Count)）—— 装的好像不是同一首，这一趟不作数"
}
