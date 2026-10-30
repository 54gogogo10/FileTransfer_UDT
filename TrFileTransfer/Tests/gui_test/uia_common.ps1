# Shared UIA helpers for the TrFileTransfer GUI test (run with -File)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Get-AppWindow([int]$ProcId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
    $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
    if ($win -eq $null) { throw "window for pid $ProcId not found" }
    return $win
}

function Find-First($root, $name, $type) {
    $conds = @()
    if ($name -ne $null) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    }
    if ($type -ne $null) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    }
    if ($conds.Count -eq 0) { throw "need a condition" }
    $and = $conds[0]
    for ($i = 1; $i -lt $conds.Count; $i++) {
        $and = New-Object System.Windows.Automation.AndCondition($and, $conds[$i])
    }
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}

function Find-All($root, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-EditValue($e) {
    try {
        $p = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $p.Current.Value
    } catch { return $null }
}

function Invoke-Button($root, $name) {
    $btn = Find-First $root $name ([System.Windows.Automation.ControlType]::Button)
    if ($btn -eq $null) { throw "button '$name' not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Set-EditTextByValue($root, [string]$currentValue, [string]$newValue) {
    $edits = Find-All $root ([System.Windows.Automation.ControlType]::Edit)
    foreach ($e in $edits) {
        if ("$(Get-EditValue $e)" -eq $currentValue) {
            ($e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($newValue)
            return $true
        }
    }
    throw "edit with value '$currentValue' not found"
}

function Get-EditTextByValue($root, [string]$currentValue) {
    $edits = Find-All $root ([System.Windows.Automation.ControlType]::Edit)
    foreach ($e in $edits) {
        if ("$(Get-EditValue $e)" -eq $currentValue) { return $e }
    }
    return $null
}

function Select-Tab($root, [string]$name) {
    $tab = Find-First $root $name ([System.Windows.Automation.ControlType]::TabItem)
    if ($tab -eq $null) { throw "tab '$name' not found" }
    ($tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
}

function Select-Radio($root, [string]$name) {
    $rb = Find-First $root $name ([System.Windows.Automation.ControlType]::RadioButton)
    if ($rb -eq $null) { throw "radio '$name' not found" }
    ($rb.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
}

function Get-LogText($root) {
    $items = Find-All $root ([System.Windows.Automation.ControlType]::ListItem)
    $lines = @()
    foreach ($i in $items) { $lines += $i.Current.Name }
    return ($lines -join "`n")
}
