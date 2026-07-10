# ============================================================
#  一键本地部署脚本  deploy.ps1
#  作用：发布 → 打包 → 上传到服务器（①②③）→ 自动 SSH 远程执行 upgrade.sh 升级（④）
#
#  现在已配置好到服务器的 SSH 免密登录，默认就是"全自动"：
#    .\deploy.ps1                 一条命令走完 ①②③④，全程不用输密码
#  唯一还会等你确认的地方：如果 upgrade.sh 检测到 migrate.sql，会问一句
#  『发现 migrate.sql，是否应用?[y/N]』——这一步故意留着手动确认，
#  避免数据库结构变更在没人看的情况下被静默执行。
#
#  不想自动做某一步时，用下面的开关跳过：
#    .\deploy.ps1 -SkipMigration   不生成/上传 migrate.sql（确定这次没改数据库结构时用）
#    .\deploy.ps1 -SkipUpgrade     只发布上传，不自动 SSH 升级服务器（自己手动去服务器执行 upgrade.sh）
# ============================================================
param(
    [string]$Server  = "root@49.232.210.129",   # 服务器登录（已配置 SSH 密钥，免密）
    [switch]$SkipMigration,                       # 跳过生成/上传 migrate.sql
    [switch]$SkipUpgrade                           # 跳过自动远程执行 upgrade.sh
)
$WithMigration = -not $SkipMigration
$RunUpgrade    = -not $SkipUpgrade

$ErrorActionPreference = "Stop"
$Proj = "C:\Users\w\Desktop\AttendanceSystem(1)\AttendanceSystem"
Set-Location $Proj

function Fail($msg) { Write-Host "❌ $msg" -ForegroundColor Red; exit 1 }

# —— ① 清理缓存 + 发布 ————————————————————————————————
Write-Host "==> [1/4] 清理缓存并发布 (Release)..." -ForegroundColor Cyan
Get-ChildItem -Path obj -Recurse -Filter "*.dswa.cache.json" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue            # 修掉那个反复出现的缓存损坏报错
Remove-Item -Recurse -Force .\publish        -ErrorAction SilentlyContinue
Remove-Item -Force          .\publish.tar.gz -ErrorAction SilentlyContinue

# 本机 DLP 安全软件会把编译过程中自动生成的一批中间文件（AssemblyInfo.cs、
# staticwebassets 缓存等）悄悄加密成乱码，导致编译报 CS2015"是二进制文件"或
# MSB4018 缓存损坏。下面这几个 -p: 参数让编译干脆不生成这些文件，绕开加密：
# 代价只是发布出来的程序集少了版本号等描述信息，不影响功能。
# 注意：不能加 -p:GenerateRazorAssemblyInfo=false ——曾经加过，编译能通过、
# 也测不出问题，但发布出来的版本在服务器上会导致所有 Razor 页面（/Login 等）404，
# 只有 Controller 接口还能用。2026-07-08 那次事故就是这个参数导致网站直接打不开，
# 排查后确认必须去掉，即使这个参数本身看起来和"加密构建文件"这个问题关系不大。
$DlpSafeFlags = @(
    "-p:StaticWebAssetsCacheDefineStaticWebAssetsEnabled=false"
    "-p:GenerateAssemblyInfo=false"
    "-p:GenerateTargetFrameworkAttribute=false"
)

dotnet publish -c Release -o .\publish --nologo @DlpSafeFlags
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
        dotnet build .\AttendanceSystem.csproj -c Debug --nologo -v quiet @DlpSafeFlags | Out-Null
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
Write-Host "==> [3/4] 上传到服务器..." -ForegroundColor Cyan
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
