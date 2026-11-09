# 提交前自检：编译 + 全量单元测试
# CI（GitHub Actions）每次推送都会跑同一套测试，先在本地跑一遍，避免"推上去才发现变红"。
# 用法：powershell -ExecutionPolicy Bypass -File tools/verify.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '== 1/2 编译（含测试工程）==' -ForegroundColor Cyan
    dotnet build "AttendanceSystem.Tests/AttendanceSystem.Tests.csproj" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw '编译失败：不要推送' }

    Write-Host '== 2/2 全量单元测试 ==' -ForegroundColor Cyan
    dotnet test "AttendanceSystem.Tests/AttendanceSystem.Tests.csproj" -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw '测试失败：不要推送' }

    Write-Host '== 自检通过，可以推送 ==' -ForegroundColor Green
}
finally {
    Pop-Location
}
