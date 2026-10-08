using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IAgentToolExecutor"/>
/// <remarks>
/// 工具实现原则：
/// 1. 全部只读（本阶段 M2）；写工具在 M3 以"动作提案+管理员确认"形态加入。
/// 2. 每个工具执行时都从 DB 重新读操作者范围（不信任缓存），再做范围过滤；
/// 3. 返回文本按"给模型看的最小信息"组装：姓名/工号/部门/日期/状态可以给，
///    身份证号/完整手机号/住址/照片 URL 一律不给（手机号打码）。
/// <para>代码按主题拆在同目录的 AgentToolExecutor.*.cs 里（Definitions / Dispatch / ReadTools / WriteTools / ApprovalAndMore），同一个类。</para>
/// </remarks>
public partial class AgentToolExecutor(
    AttendanceDbContext db,
    IDeptScopeService deptScope,
    ILogger<AgentToolExecutor> logger,
    TimeProvider? timeProvider = null) : IAgentToolExecutor
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
}
