# ============================================================
#  一键本地部署脚本  deploy.ps1
#  作用：发布 → 打包 → 上传到服务器（①②③），并提示/执行服务器升级（④）
#
#  用法（在本项目目录的 PowerShell 里）：
#    .\deploy.ps1                 普通部署（没改数据库时用这个）
#    .\deploy.ps1 -WithMigration  改过数据库结构时用：会一并生成并上传 migrate.sql
#    .\deploy.ps1 -RunUpgrade     上传后自动 SSH 进服务器执行 upgrade.sh（需再输一次密码）
#  两个开关可同时用：.\deploy.ps1 -WithMigration -RunUpgrade
# ============================================================
param(
    [string]$Server  = "root@49.232.210.129",   # 服务器登录
    [switch]$WithMigration,                       # 改过数据库 → 生成并上传 migrate.sql
    [switch]$RunUpgrade                            # 上传后自动远程执行 upgrade.sh
)

$ErrorActionPreference = "Stop"
$Proj = "C:\Users\wuwenzhe\Desktop\AttendanceSystem"
Set-Location $Proj

function Fail($msg) { Write-Host "❌ $msg" -ForegroundColor Red; exit 1 }

# —— ① 清理缓存 + 发布 ————————————————————————————————
Write-Host "==> [1/4] 清理缓存并发布 (Release)..." -ForegroundColor Cyan
Get-ChildItem -Path obj -Recurse -Filter "*.dswa.cache.json" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue            # 修掉那个反复出现的缓存损坏报错
Remove-Item -Recurse -Force .\publish        -ErrorAction SilentlyContinue
Remove-Item -Force          .\publish.tar.gz -ErrorAction SilentlyContinue

dotnet publish -c Release -o .\publish --nologo
if ($LASTEXITCODE -ne 0)                       { Fail "dotnet publish 失败" }
if (-not (Test-Path .\publish\AttendanceSystem.dll)) { Fail "发布产物缺失 AttendanceSystem.dll" }

# —— ② 打包 ————————————————————————————————————————————
Write-Host "==> [2/4] 打包 publish.tar.gz..." -ForegroundColor Cyan
tar -czf publish.tar.gz -C .\publish .
if ($LASTEXITCODE -ne 0) { Fail "打包失败" }
Write-Host ("    大小: {0:N1} MB" -f ((Get-Item .\publish.tar.gz).Length / 1MB))

# —— 可选：生成数据库迁移脚本 ——————————————————————————————
if ($WithMigration) {
    Write-Host "==> 生成幂等迁移脚本 migrate.sql..." -ForegroundColor Cyan
    # 本机安全软件(DLP)会加密构建产物(runtimeconfig.json / staticwebassets 缓存)，
    # 导致 dotnet ef 无法运行。策略：先尝试 ef 自动生成(生成到临时文件，成功才覆盖)；
    # 失败则回退使用仓库中已手工维护的 migrate.sql，避免整条部署中断。
    Get-ChildItem -Path obj, bin -Recurse -Filter "*.dswa.cache.json" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $efOk = $false
    try {
        dotnet build .\AttendanceSystem.csproj -c Debug -p:StaticWebAssetsEnabled=false --nologo -v quiet | Out-Null
        dotnet ef migrations script --idempotent --no-build -o migrate.gen.sql 2>$null
        if ($LASTEXITCODE -eq 0 -and (Test-Path .\migrate.gen.sql) -and ((Get-Item .\migrate.gen.sql).Length -gt 0)) {
            Move-Item -Force .\migrate.gen.sql .\migrate.sql
            $efOk = $true
        }
    } catch { }
    Remove-Item .\migrate.gen.sql -ErrorAction SilentlyContinue

    if ($efOk) {
        Write-Host "    已用 dotnet ef 自动生成 migrate.sql ✓" -ForegroundColor Green
    } elseif ((Test-Path .\migrate.sql) -and ((Get-Item .\migrate.sql).Length -gt 0)) {
        Write-Host "    ⚠ dotnet ef 生成失败（本机 DLP 加密了构建产物），改用仓库中已维护的 migrate.sql 继续。" -ForegroundColor Yellow
    } else {
        Fail "无法生成 migrate.sql，且仓库中也没有可用的 migrate.sql。请在未装 DLP 的机器/CI 上执行：dotnet ef migrations script --idempotent -o migrate.sql"
    }
}

# —— ③ 上传到服务器 ——————————————————————————————————————
Write-Host "==> [3/4] 上传到服务器（请输入服务器密码）..." -ForegroundColor Cyan
scp publish.tar.gz "${Server}:/root/"
if ($LASTEXITCODE -ne 0) { Fail "上传 publish.tar.gz 失败（检查网络/安全组 22 端口/密码）" }
if ($WithMigration) {
    scp migrate.sql "${Server}:/root/"
    if ($LASTEXITCODE -ne 0) { Fail "上传 migrate.sql 失败" }
}
Write-Host "    上传完成 ✓" -ForegroundColor Green

# —— ④ 服务器升级 ——————————————————————————————————————
Write-Host "==> [4/4] 服务器升级" -ForegroundColor Cyan
if ($RunUpgrade) {
    Write-Host "    SSH 执行 upgrade.sh..." -ForegroundColor Cyan
    ssh -t $Server "bash /root/upgrade.sh"
    if ($LASTEXITCODE -ne 0) { Fail "远程 upgrade.sh 执行失败，请登录服务器查看" }
} else {
    Write-Host "    现在去【服务器 SSH 窗口】执行：" -ForegroundColor Yellow
    Write-Host "        bash /root/upgrade.sh"        -ForegroundColor Yellow
    if ($WithMigration) {
        Write-Host "    （脚本会提示『发现 migrate.sql，是否应用?[y/N]』→ 输 y 回车）" -ForegroundColor Yellow
    }
}

Write-Host "✅ 完成。访问 https://wwz.cool 验证。" -ForegroundColor Green
