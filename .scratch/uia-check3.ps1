# 鼓轨那一支：带 9 号声道的 MIDI 里，那条轨的音色格应该是「标准鼓组 · 通道 10」
# 这句不能点的说明，而不是一个改了也不知道会怎样的下拉。

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
function ComboVals($root) {
  return @(Find-All $root $CT::ComboBox | ForEach-Object {
    $v = ''
    try { $v = $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    $v })
}
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

$import = Find-All (Get-Root) $CT::Button | Where-Object { $_.Current.Name -like '导入*' } | Select-Object -First 1
Invoke $import
Start-Sleep -Seconds 3

Set-Clipboard -Value 'C:\Users\cao17\Desktop\midiplayer\drywetmidi\Resources\MIDI files\Valid\MultiSequence\Middle\cargo.mid'
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('^v')
Start-Sleep -Milliseconds 800
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 5

$root = Get-Root
$texts = Find-All $root $CT::Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
"轨道头那几行灰字里出现过的:"
$texts | Where-Object { $_ -like '标准鼓组*' -or $_ -like '音色*' } | ForEach-Object { "    '$_'" }
""
"'标准鼓组 · 通道 10' 出现 $((@($texts | Where-Object { $_ -eq '标准鼓组 · 通道 10' })).Count) 处"
"下拉 $((ComboVals $root).Count) 个:"
ComboVals $root | ForEach-Object { "    '$_'" }
""
"轨名: $((@(Find-All $root $CT::Button | Where-Object { $_.Current.Name -eq '折叠' })).Count) 条轨有折叠按钮"
