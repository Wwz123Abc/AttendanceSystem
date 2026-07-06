$c = @(Import-Csv 'C:\Users\wuwenzhe\Desktop\AttendanceSystem\tools\test-cases.csv' -Encoding UTF8)
Write-Host "Count: $($c.Count)"
Write-Host "Headers: $($c[0].PSObject.Properties.Name -join ' | ')"
$g = $c | Group-Object { $_.PSObject.Properties[1].Value }
Write-Host "Groups: $($g.Count)"
$g | Select-Object -First 5 Name, Count | Format-Table
