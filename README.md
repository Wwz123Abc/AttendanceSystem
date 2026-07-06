# 考勤管理系统 AttendanceSystem

一个面向中小企业的考勤管理系统，覆盖打卡、请假/出差/补卡/加班审批、排班、考勤统计报表、钉钉对接、忘记密码自助找回等常见场景。

## 技术栈

- ASP.NET Core 8（Razor Pages 为主，配合一组 `api/[controller]` 风格的 Web API 供测试/对接使用）
- Entity Framework Core 9 + Pomelo MySQL 驱动，数据库为 MariaDB
- Cookie 认证
- 钉钉开放平台（工作通知、组织架构同步）
- NPOI（Excel 导出）、Serilog（日志）

## 功能概览

- **打卡**：支持定位打卡，自动记录上下班时间
- **我的日历 / 我的记录**：按月查看考勤记录，自动标注周末、节假日，带当月统计
- **审批流程**：请假、补卡、加班、出差申请与多级审批，审批流程可自定义
- **排班与考勤组**：批量排班、考勤组/部门管理
- **月度报表**：按部门/考勤组导出 Excel 考勤汇总
- **钉钉对接**：组织架构/考勤数据同步，工作通知发送验证码
- **忘记密码自助找回**：通过钉钉工作通知验证码重置密码，无需管理员介入
- **后台管理**：员工、部门、考勤组、假期、班次、审批流程管理

## 目录结构

```
Controllers/    Web API 控制器（供测试脚本/未来系统对接使用，网页本身走 Razor Pages）
Pages/          Razor Pages 页面（登录、打卡、审批、后台管理等）
Services/       业务逻辑层（接口 + 实现），含钉钉客户端
Models/         实体、DTO、枚举、配置项
Data/           EF Core DbContext
Migrations/     数据库迁移记录
Middlewares/    自定义中间件
Helpers/        工具类（Excel 导出、身份声明构建等）
wwwroot/        静态资源
docs/           项目文档（部署手册、操作说明、数据库表结构、更新日志等）
tools/          测试用例脚本
```

## 本地运行

### 1. 准备环境

- .NET 8 SDK
- MariaDB / MySQL

### 2. 配置

复制一份配置文件并填入你自己的数据库连接和钉钉密钥：

```bash
cp appsettings.Example.json appsettings.json
```

编辑 `appsettings.json`：

- `ConnectionStrings:Default`：填入你的数据库连接串
- `DingTalk:AppKey` / `AppSecret` / `AgentId`：填入你的钉钉自建应用凭据（用于组织架构同步和工作通知验证码，非必须功能可留空）

> `appsettings.json` 已在 `.gitignore` 中排除，不会被提交，请放心填入真实密钥。

### 3. 初始化数据库

```bash
dotnet ef database update
```

（或直接执行仓库中的 `migrate.sql`）

### 4. 运行

```bash
dotnet run
```

默认账号密码见 `AppSettings:DefaultPassword`（初始新建员工的默认密码）。

## 部署

生产环境部署步骤见 [docs/部署手册.docx](docs/部署手册.docx)，本机一键部署脚本见 [deploy.ps1](deploy.ps1)。

## 文档

- [考勤系统操作说明](docs/考勤系统操作说明(2).docx) —— 面向使用者的操作手册
- [后端实现说明文档](docs/后端实现说明文档.docx) —— 技术实现说明
- [数据库表结构文档](docs/数据库表结构文档_2.docx)
- [环境迁移配置清单](docs/环境迁移配置清单.docx)
- [更新日志](docs/更新日志.md) —— 按日期记录每次功能改动
