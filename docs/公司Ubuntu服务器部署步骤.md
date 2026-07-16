# 考勤系统 部署到公司 Ubuntu 服务器（内网版）

版本：v1.0　日期：2026-07-14

## 适用场景

这份文档专门针对**这一次**的部署场景写的，不是通用文档：
- 服务器：公司内部一台**全新的空 Ubuntu 机器**，什么都没装过。
- 访问范围：**只在公司内网用**，员工不需要在公司网络之外访问，所以不需要域名、不需要 HTTPS 证书、不需要 ICP 备案，比对外开放的部署简单很多。

如果以后这台服务器的定位变了（比如要开放给公司外部访问），需要另外补充域名/HTTPS/Nginx 反向代理那部分，可以参考项目里更完整的 `docs/新服务器首次部署指南.md`（同时覆盖了内网/公网两种情况）。

这份文档从"一台空机器"开始，一路走到"能在公司电脑/手机浏览器里打开系统"为止，交给公司 IT 按顺序执行即可。

## 第一步：确认基本情况

在开始之前，需要先知道 / 准备好这几样东西：

| 需要什么 | 从哪来 |
|---|---|
| 服务器的内网 IP 地址 | 问 IT，或在服务器上执行 `ip addr` 查看 |
| 服务器的 SSH 登录方式（账号/密码或密钥） | IT 自己配置/知道 |
| 一个用于数据库的强密码（自己现在定一个，记下来） | 现在就想好，后面会用到 |
| 公司真实的钉钉自建应用 AppKey / AppSecret / AgentId（如果要用钉钉同步功能） | 钉钉开放平台的自建应用详情页；不着急的话可以先跳过，以后再补配置 |

## 第二步：服务器上安装运行环境

以下命令都在服务器的 SSH 终端里执行（先用准备好的账号 SSH 登录进服务器）。

### 2.1 安装 .NET 8 运行时
```bash
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y aspnetcore-runtime-8.0
dotnet --list-runtimes   # 确认能看到 Microsoft.AspNetCore.App 8.0.x，装成功了
```
（如果服务器不是 Ubuntu 22.04，把命令里的 `22.04` 换成实际版本号，比如 `20.04`、`24.04`。）

### 2.2 安装数据库（MariaDB）
```bash
sudo apt install -y mariadb-server
sudo mysql_secure_installation   # 按提示设置 root 密码等安全选项，一路选默认/Y 基本没问题
```
装好后进数据库命令行建库建账号（把下面的 `换成第一步定好的强密码` 替换成真实密码）：
```bash
sudo mysql -uroot -p
```
```sql
CREATE DATABASE AttendanceDB CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
CREATE USER 'att_user'@'localhost' IDENTIFIED BY '换成第一步定好的强密码';
GRANT ALL PRIVILEGES ON AttendanceDB.* TO 'att_user'@'localhost';
FLUSH PRIVILEGES;
EXIT;
```
⚠️ 注意是 `'att_user'@'localhost'`（只允许本机连接），不要写成 `'att_user'@'%'`——数据库不需要对外网开放，只有装在同一台服务器上的考勤系统程序会连它。

## 第三步：本地（开发者电脑）打包程序

在**开发者自己的电脑**（不是服务器）上执行，把程序打包好、准备传到服务器：

```powershell
cd "C:\Users\w\Desktop\AttendanceSystem(1)\AttendanceSystem"
dotnet publish -c Release -o .\publish
tar -czf publish.tar.gz -C .\publish .

# 首次部署，服务器数据库是空的，需要生成完整建表脚本
dotnet ef migrations script -o migrate.sql
```

把打包好的 `publish.tar.gz` 和 `migrate.sql` 传到服务器（把 `服务器内网IP` 换成第一步查到的地址）：
```powershell
scp publish.tar.gz migrate.sql 用户名@服务器内网IP:/root/
```

## 第四步：服务器上部署程序

回到服务器的 SSH 终端：

### 4.1 解压到程序目录
```bash
sudo mkdir -p /var/www/attendance-net
sudo tar -xzf /root/publish.tar.gz -C /var/www/attendance-net
sudo chown -R www-data:www-data /var/www/attendance-net
```

### 4.2 建表
```bash
mysql -uatt_user -p AttendanceDB < /root/migrate.sql
```
（输入的密码是第二步设置的数据库密码）

### 4.3 配置数据库连接和钉钉密钥
这一步涉及真实密码/密钥，**不要**把这些信息写进代码仓库，只写在服务器本地这个文件里：
```bash
sudo nano /var/www/attendance-net/appsettings.Production.json
```
内容（把里面的占位符换成真实值，钉钉部分如果暂时没有可以先留空，以后再回来补）：
```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Port=3306;Database=AttendanceDB;User=att_user;Password=换成第二步设置的数据库密码;CharSet=utf8mb4;AllowPublicKeyRetrieval=true;SslMode=None;"
  },
  "DingTalk": {
    "AppKey": "换成公司真实钉钉应用的 AppKey",
    "AppSecret": "换成公司真实钉钉应用的 AppSecret",
    "AgentId": "换成公司真实钉钉应用的 AgentId"
  }
}
```
保存退出（nano 编辑器：`Ctrl+O` 保存，`Ctrl+X` 退出）。

如果要用钉钉同步打卡数据，还需要去钉钉开放平台，把这台服务器的**公网出口 IP** 加进对应自建应用的 IP 白名单（这一步不做的话，服务器内网本身还是能正常打开系统打卡，只是钉钉同步功能会报权限错误）。

### 4.4 配置成开机自启的系统服务
```bash
sudo nano /etc/systemd/system/attendance-net.service
```
内容：
```ini
[Unit]
Description=考勤系统 (AttendanceSystem)
After=network.target mysql.service

[Service]
WorkingDirectory=/var/www/attendance-net
ExecStart=/usr/bin/dotnet /var/www/attendance-net/AttendanceSystem.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=attendance-net
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080

[Install]
WantedBy=multi-user.target
```
⚠️ 注意这里用的是 `0.0.0.0:5080`（监听所有网卡），不是 `127.0.0.1`——因为是纯内网访问，需要让公司内网里的其它电脑能直接连上这台服务器的 5080 端口，不经过 Nginx 反向代理。

启动服务：
```bash
sudo systemctl daemon-reload
sudo systemctl enable attendance-net
sudo systemctl start attendance-net
sudo systemctl status attendance-net --no-pager   # 期望看到 active (running)
```

### 4.5 检查防火墙（如果服务器开了防火墙的话）
```bash
sudo ufw status
```
如果显示 `Status: active`，需要放行 5080 端口（只放行公司内网网段访问，不要对外网开）：
```bash
sudo ufw allow from 公司内网网段 to any port 5080
# 例如内网是 192.168.1.0/24 段，就写：sudo ufw allow from 192.168.1.0/24 to any port 5080
```
如果 `ufw status` 显示 `Status: inactive`（没开防火墙），这一步可以跳过。

## 第五步：验证部署成功

先在服务器本机验证程序确实跑起来了：
```bash
curl -I http://127.0.0.1:5080/Login   # 期望看到 HTTP/1.1 200 或 302
systemctl status attendance-net --no-pager
```

再在公司内网的任意一台电脑上，浏览器打开：
```
http://服务器内网IP:5080
```
应该能看到登录页。

首次打开、数据库刚建好表时，系统会自动创建一个默认管理员账号：
- 工号：`admin`
- 初始密码：`Admin@123`

**登录后请立即修改这个默认密码。** 然后可以试一下"员工管理"页的"从钉钉导入员工"按钮，能成功导入或者至少给出明确的错误提示（不是白屏/系统报错），说明钉钉那部分配置也接通了；如果第 4.3 步暂时没配钉钉密钥，这个按钮会提示"未配置"，属于正常现象，以后补上密钥再试。

## 第六步：以后怎么更新

这台服务器首次部署完成后，以后代码有改动需要更新时，不用再重复上面这一整套步骤，只需要：
1. 开发者本地重新 `dotnet publish` 打包、`scp` 传到服务器（同第三步）。
2. 服务器上解压覆盖 `/var/www/attendance-net`（同 4.1，不用重新建库）。
3. 如果这次改动加了新的数据库字段，额外生成一份增量迁移脚本执行一下（`dotnet ef migrations script --idempotent -o migrate.sql`，只补新增的部分，已经跑过的部分会自动跳过）。
4. `sudo systemctl restart attendance-net` 重启服务生效。

具体命令细节可以参考 `docs/部署手册.docx`。

## 常见问题

| 现象 | 处理 |
|---|---|
| 内网电脑打不开 `http://服务器内网IP:5080` | 先在服务器本机用 `curl -I http://127.0.0.1:5080/Login` 确认程序本身有没有跑起来；如果本机能访问、内网访问不了，多半是防火墙没放行 5080 端口（见第 4.5 步） |
| `systemctl status` 显示服务没起来/反复重启 | 看日志定位原因：`journalctl -u attendance-net -n 50 --no-pager` |
| 登录页打开了，但登录报"数据库连接失败" | 检查 `appsettings.Production.json` 里的密码是不是和建库时设置的密码一致 |
| "从钉钉导入员工"报权限错误 | 钉钉应用的 IP 白名单里没加这台服务器的公网出口 IP，找管理钉钉后台的人补上 |

---
*本文档针对这一次"全新空 Ubuntu 服务器 + 纯内网访问"的场景定制。更完整、覆盖公网访问场景的版本见 `docs/新服务器首次部署指南.md`；日常升级操作见 `docs/部署手册.docx`。*
