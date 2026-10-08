using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IAgentActionService"/>
/// <remarks>
/// 实现要点：
/// - 认领/改状态全部用 ExecuteUpdateAsync 在数据库层原子完成（WHERE 带 Status=Pending/未过期），
///   双击或并发只会成功一次；本服务不对已认领的实体再走 SaveChanges 整体写回，避免脏覆盖；
/// - 确认后每个动作都重新做范围/状态校验，校验不过就记失败原因，不硬执行；
/// - 失败不自动回退 Pending（避免重复执行），管理员看到原因后可重新发起。
/// <para>代码按主题拆在同目录的 AgentActionService.*.cs 里（Query / Review / Undo / Execute / HighRisk / Employee / Others），同一个类。</para>
/// </remarks>
public partial class AgentActionService(
    AttendanceDbContext db,
    IDeptScopeService deptScope,
    IUserService userService,
    IAttendanceService attendanceService,
    IEmployeeRegistrationService registrationService,
    IAttendanceGroupService groupService,
    IApprovalService approvalService,
    IAnnouncementService announcementService,
    ILogger<AgentActionService> logger,
    TimeProvider? timeProvider = null) : IAgentActionService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
}
