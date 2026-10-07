# 考勤管理系统 AttendanceSystem

一个面向中小企业的考勤管理系统，覆盖打卡、请假/出差/补卡/加班审批、排班、考勤统计报表、熵基（ZKTeco）考勤机对接等常见场景。

## 技术栈

- ASP.NET Core 8（Razor Pages 为主，配合一组 `api/[controller]` 风格的 Web API 供测试/对接使用）
- Entity Framework Core 9 + Pomelo MySQL 驱动，数据库为 MariaDB
- Cookie 认证
- 熵基（ZKTeco）考勤机 PUSH 协议对接（设备主动推送打卡数据）
- NPOI（Excel 导出）、Serilog（日志）
- 阿里云视觉智能开放平台（人脸活体检测 + 1:1 比对，远程打卡用）、高德地图（打卡地点选点）
- DeepSeek 大模型（管理员/文员的"智能助手"，默认关闭，密钥走环境变量 `AGENT_API_KEY`）

## 功能概览

- **打卡**：由熵基考勤机刷脸/指纹打卡后自动推送打卡数据，自动记录上下班时间
- **远程打卡**：出差/外勤等到不了考勤机的员工，可用手机定位+人脸识别打卡，默认关闭需管理员单独开通，有防刷限流
- **我的日历 / 我的记录**：按月查看考勤记录，自动标注休息日，带当月统计
- **审批流程**：请假、补卡、加班、出差申请，审批人按考勤组配置（一级/二级，可配审批人名单）；管理员/文员可在"总审批记录"按管理范围查看最近 2 个月的全部审批并导出
- **排班与考勤组**：批量排班、考勤组/部门管理
- **月度报表**：按部门/考勤组导出 Excel 考勤汇总
- **考勤机管理**：后台维护考勤机序列号白名单，新建/编辑员工自动把工号姓名下发给考勤机
- **扫码登记**：新员工扫码自助填写基础信息，管理员确认后正式建号
- **公告栏**：管理员/文员/主管/班组长按范围发布公告，支持已读未读名单查看
- **分公司/组织隔离**：可为管理员设置管理范围，限定其只能看到本分公司数据
- **智能助手**：管理员/文员用自然语言查询考勤、提出修改，需要二次确认后才执行，全程留痕
- **后台管理**：员工、部门、考勤组（含审批人名单）、班次管理（忘记密码由管理员在员工管理页手动重置）

## 目录结构

```
Controllers/    Web API 控制器（供测试脚本/未来系统对接使用，网页本身走 Razor Pages）；Requests/ 放接口请求体
Pages/          Razor Pages 页面（登录、打卡、审批、后台管理等）
Services/       业务逻辑层（接口 + 实现），含考勤机同步服务
Models/         实体、DTO、枚举、配置项
Data/           EF Core DbContext
Migrations/     数据库迁移记录
Middlewares/    自定义中间件
Helpers/        工具类（Excel 导出、身份声明构建等）
wwwroot/        静态资源（公开）；员工身份证照/人脸照/审批附件放在 PrivateUploads/，不对外直接访问
docs/           项目文档，按用途分目录：运维 / 口径 / 变更记录 / 使用说明 / 设计方案 / 审查与验收（索引见 docs/README.md）
tools/          更新日志转 Word、测试用例导出等脚本
.github/        CI 流水线（编译 + 测试 + 依赖漏洞检查）和依赖更新配置
AttendanceSystem.Tests/  单元测试（xUnit，用 SQLite 内存库；dotnet test AttendanceSystem.Tests）
mobile-app/     Capacitor 安卓壳（已不再维护）
```

## 本地运行

### 1. 准备环境

- .NET 8 SDK
- MariaDB / MySQL

### 2. 配置

复制一份配置文件并填入你自己的数据库连接：

```bash
cp appsettings.Example.json appsettings.json
```

编辑 `appsettings.json`：

- `ConnectionStrings:Default`：填入你的数据库连接串
- `ZKDevice:HeartbeatIntervalSeconds`：考勤机心跳间隔（秒），设备白名单改在"考勤机管理"后台页面维护，不用改配置文件

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

数据库里一个用户都没有时，首次启动会自动创建管理员账号 `admin`（初始密码见 `Program.cs` 的 `SeedAdminAsync`，**上线后必须立即修改**）；之后新建的员工初始密码统一为 `123456`（不再强制首次登录改密码，建议员工自行修改）。

## 工程规范

- 代码风格：`.editorconfig`（编码、缩进、命名约定；规则目前都是"建议"级别，不会让构建失败）
- 统一构建设置：`Directory.Build.props`（Nullable、可复现构建、NuGet 漏洞审计）；`global.json` 约定 .NET SDK 不低于 8.0
- 换行符：`.gitattributes`（仓库统一存 LF；`.sh` 必须 LF）
- 一个类型一个文件，文件名同类型名；通知类型等固定字符串集中在常量类（如 `NotificationTypes`）
- 提交到 `main` 或提 PR 会触发 `.github/workflows/ci.yml`：编译、跑测试、列出有已知漏洞的依赖
- 每次功能改动都要在 `docs/变更记录/更新日志.md` 追加记录（约定见 `CLAUDE.md`）

## 部署

生产环境部署、回滚、数据库变更、运维命令和踩过的坑，都在 [docs/运维/部署与交接手册.md](docs/运维/部署与交接手册.md)。

## 文档

- [部署与交接手册](docs/运维/部署与交接手册.md) —— 部署流程、环境现状、必须知道的坑、核心文件地图
- [口径登记表](docs/口径/口径登记表.md) —— 工时、请假、迟到早退等考勤计算口径
- [员工操作说明（普通员工版）](docs/使用说明/员工操作说明_普通员工版.md)
- [更新日志](docs/变更记录/更新日志.md) —— 按日期记录每次功能改动（Word 版：`docs/变更记录/考勤系统更新日志_完整版.docx`）
