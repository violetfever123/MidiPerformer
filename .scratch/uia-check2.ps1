# 折叠状态能不能活过一次编辑 —— 这三件事的交叉点：
# 问题一是「一编辑轨就消失」，问题二是折叠。折叠状态存在控件上，
# 编辑走的是 ApplySong，所以这里量的是：编辑之后那条轨还是收着的吗。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Root {
  $p = Get-Process -Name dotnet -EA SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'MIDI 演奏器' } | Select-Object -First 1
  if (-not $p) { throw 'MidiPerformer 没在跑' }
  return $AE::FromHandle($p.MainWindowHandle)
}
function Find-All($root, $type) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  return $root.FindAll($TS::Descendants, $cond)
}
function FoldNames($root) {
  return ((Find-All $root $CT::Button | Where-Object { $_.Current.Name -in @('折叠','展开') } |
           ForEach-Object { $_.Current.Name }) -join ', ')
}
function ComboVals($root) {
  return ((Find-All $root $CT::ComboBox | ForEach-Object {
    $v = ''
    try { $v = $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    $v }) -join ' | ')
}
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

"轨数 / 按钮        : $(FoldNames (Get-Root))"
"音色               : $(ComboVals (Get-Root))"

# 1) 把第一条轨收起来
$f = Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -eq '折叠' } | Select-Object -First 1
Invoke $f
Start-Sleep -Milliseconds 800
"收起来之后         : $(FoldNames (Get-Root))"

# 2) 这时候改第二条轨的音色（一次真正的编辑 -> ApplySong）
$combo = (Find-All (Get-Root) $CT::ComboBox)[0]
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 700
$item = Find-All (Get-Root) $CT::ListItem | Where-Object { $_.Current.Name -like '长笛*' } | Select-Object -First 1
if (-not $item) { $item = Find-All (Get-Root) $CT::ListItem | Where-Object { $_.Current.Name -like '小提琴*' } | Select-Object -Last 1 }
"选音色 -> '$($item.Current.Name)'"
$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1000

"改完音色之后       : $(FoldNames (Get-Root))"
"音色               : $(ComboVals (Get-Root))"
$strips = (Find-All (Get-Root) $CT::Text | Where-Object { $_.Current.Name -eq '已折叠' }).Count
"'已折叠' 还在几处    : $strips"

# 3) 撤销，看折叠状态是不是也还在
$undo = Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -eq '撤销' } | Select-Object -First 1
if ($undo -and $undo.Current.IsEnabled) {
  Invoke $undo
  Start-Sleep -Milliseconds 900
  "撤销之后           : $(FoldNames (Get-Root))"
  "音色               : $(ComboVals (Get-Root))"
  "'已折叠' 还在几处    : $((Find-All (Get-Root) $CT::Text | Where-Object { $_.Current.Name -eq '已折叠' }).Count)"
} else { "撤销没亮，跳过" }
