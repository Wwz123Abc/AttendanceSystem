$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csvPath = Join-Path $root 'tools\test-cases.csv'
$outPath = Join-Path $root ([char]0x6D4B + [char]0x8BD5 + [char]0x7528 + [char]0x4F8B + '_' + [char]0x8003 + [char]0x52E4 + [char]0x7CFB + [char]0x7EDF + '.xlsx')

if (-not (Test-Path $csvPath)) { throw "CSV not found: $csvPath" }

$priorityColors = @{ P0 = 65535; P1 = 5296274; P2 = 15921906 }

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $wb = $excel.Workbooks.Open($csvPath)
    $ws = $wb.Worksheets.Item(1)
    $ws.Name = [string]([char]0x6D4B + [char]0x8BD5 + [char]0x7528 + [char]0x4F8B)

    $lastRow = $ws.UsedRange.Rows.Count
    $lastCol = 7

    for ($c = 1; $c -le $lastCol; $c++) {
        $cell = $ws.Cells.Item(1, $c)
        $cell.Font.Bold = $true
        $cell.Font.Color = 16777215
        $cell.Interior.Color = 9851952
        $cell.HorizontalAlignment = -4108
        $cell.WrapText = $true
    }
    $ws.Rows.Item(1).RowHeight = 22

    for ($r = 2; $r -le $lastRow; $r++) {
        $pri = [string]$ws.Cells.Item($r, 4).Text
        for ($c = 1; $c -le $lastCol; $c++) {
            $cell = $ws.Cells.Item($r, $c)
            $cell.WrapText = $true
            $cell.VerticalAlignment = -4160
            if ($c -eq 4 -and $priorityColors.ContainsKey($pri)) {
                $cell.Interior.Color = $priorityColors[$pri]
                $cell.HorizontalAlignment = -4108
            }
        }
    }

    $widths = @(12, 14, 24, 10, 36, 44, 44)
    for ($i = 0; $i -lt $widths.Count; $i++) { $ws.Columns.Item($i + 1).ColumnWidth = $widths[$i] }
    if ($lastRow -ge 2) { $ws.Rows.Item("2:$lastRow").RowHeight = 60 }
    $ws.Application.ActiveWindow.SplitRow = 1
    $ws.Application.ActiveWindow.FreezePanes = $true
    $ws.Range('A1').CurrentRegion.AutoFilter() | Out-Null

    $summary = $wb.Worksheets.Add([Type]::Missing, $ws)
    $summary.Name = [string]([char]0x7EDF + [char]0x8BA1)
    $summary.Cells.Item(1, 1).Value2 = [string]([char]0x8003 + [char]0x52E4 + [char]0x7CFB + [char]0x7EDF + [char]0x6D4B + [char]0x8BD5 + [char]0x7528 + [char]0x4F8B + [char]0x7EDF + [char]0x8BA1)
    $summary.Cells.Item(1, 1).Font.Bold = $true
    $summary.Cells.Item(1, 1).Font.Size = 14
    $summary.Cells.Item(3, 1).Value2 = [string]([char]0x6A21 + [char]0x5757)
    $summary.Cells.Item(3, 2).Value2 = [string]([char]0x7528 + [char]0x4F8B + [char]0x6570)

    $modules = @{}
    for ($r = 2; $r -le $lastRow; $r++) {
        $m = [string]$ws.Cells.Item($r, 2).Text
        if (-not $modules.ContainsKey($m)) { $modules[$m] = 0 }
        $modules[$m]++
    }
    $row = 4
    foreach ($k in ($modules.Keys | Sort-Object)) {
        $summary.Cells.Item($row, 1).Value2 = $k
        $summary.Cells.Item($row, 2).Value2 = $modules[$k]
        $row++
    }
    $row++
    $summary.Cells.Item($row, 1).Value2 = [string]([char]0x5408 + [char]0x8BA1)
    $summary.Cells.Item($row, 2).Value2 = ($lastRow - 1)
    $row += 2
    $summary.Cells.Item($row, 1).Value2 = [string]([char]0x4F18 + [char]0x5148 + [char]0x7EA7)
    $summary.Cells.Item($row, 2).Value2 = [string]([char]0x6570 + [char]0x91CF)
    $row++
    foreach ($pri in @('P0', 'P1', 'P2')) {
        $cnt = 0
        for ($r = 2; $r -le $lastRow; $r++) {
            if ([string]$ws.Cells.Item($r, 4).Text -eq $pri) { $cnt++ }
        }
        $summary.Cells.Item($row, 1).Value2 = $pri
        $summary.Cells.Item($row, 2).Value2 = $cnt
        $row++
    }
    $summary.Columns.Item(1).ColumnWidth = 20
    $summary.Columns.Item(2).ColumnWidth = 12

    if (Test-Path $outPath) { Remove-Item $outPath -Force }
    $wb.SaveAs($outPath, 51)
    $wb.Close($false)
    Write-Host ('Generated: ' + $outPath)
    Write-Host ('Total cases: ' + ($lastRow - 1))
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
