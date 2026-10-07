using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 审批人的解析和改派：只依赖数据库，ApprovalService（建审批节点）和 UserService（停用账号时改派待办）共用，
/// 避免两边各写一份"兜底审批人"的规则。
/// </summary>
public static class ApproverResolver
{
    /// <summary>有审批权限的角色（和 Program.cs 里 ApprovePolicy 一致）：管理员、文员、主管、班组长。
    /// 角色被降成普通员工的人进不了"待我审批"页面、也调不了审批接口，不能再当审批人。</summary>
    public static readonly UserRole[] ApproverRoles = [UserRole.Admin, UserRole.Clerk, UserRole.Supervisor, UserRole.TeamLeader];

    /// <summary>取某部门自己 + 一路向上所有祖先部门的 id 集合（deptId 为空时返回空集合）。</summary>
    public static async Task<HashSet<int>> GetAncestorDeptIdsAsync(AttendanceDbContext db, int? deptId)
    {
        var ids = new HashSet<int>();
        var cur = deptId;
        while (cur.HasValue && ids.Add(cur.Value))
            cur = await db.Departments.Where(d => d.Id == cur.Value).Select(d => d.ParentId).FirstOrDefaultAsync();
        return ids;
    }

    /// <summary>
    /// 兜底审批人：指派一名在职管理员/文员，排除申请人自己；候选人必须"管得到"申请人所在部门
    /// （总部超级管理员恒可以；分公司的只有申请人部门落在自己范围内才算）。优先同考勤组的。
    /// </summary>
    public static async Task<int?> ResolveFallbackApproverAsync(AttendanceDbContext db, User applicant)
    {
        var ancestorIds = await GetAncestorDeptIdsAsync(db, applicant.DepartmentId);
        var managers = await db.Users
            .Where(u => u.IsActive
                     && (u.Role == UserRole.Admin || u.Role == UserRole.Clerk)
                     && u.Id != applicant.Id
                     && (u.ScopedDepartmentId == null
                         || (applicant.DepartmentId != null && ancestorIds.Contains(u.ScopedDepartmentId.Value))))
            .Select(u => new { u.Id, u.AttendanceGroupId })
            .ToListAsync();
        if (managers.Count == 0) return null;
        var sameGroup = managers.FirstOrDefault(m => m.AttendanceGroupId == applicant.AttendanceGroupId);
        return (sameGroup ?? managers[0]).Id;   // 优先同组，否则取第一个
    }

    /// <summary>
    /// "直属上级，没有就兜底"：直属上级必须是在职的、有审批权限的角色、而且不能是申请人本人——否则会出现
    /// "自己批自己的单"（上级被误设成本人）、"单子派给已经停用的人"或"派给已降成普通员工、打不开待审批页面的人"
    /// （2026-10-07 第 18 轮审查）。
    /// </summary>
    public static async Task<int?> ResolveSupervisorOrFallbackAsync(AttendanceDbContext db, User applicant)
    {
        if (applicant.SupervisorUserId is { } sid && sid != applicant.Id
            && await db.Users.AnyAsync(u => u.Id == sid && u.IsActive && ApproverRoles.Contains(u.Role)))
            return sid;
        return await ResolveFallbackApproverAsync(db, applicant);
    }

    /// <summary>
    /// 账号停用/拉黑后，把他名下还没处理的审批节点改派给别人（申请人的直属上级，没有就兜底管理员），
    /// 并通知新的审批人。审批只认节点上写死的那个人，不改派的话这些单子谁也处理不了，申请人也撤不回
    /// （加班单还必须当天提交，卡住就永远批不下来）。返回改派的节点数；找不到可改派的人时保持原样并返回不含它的计数。
    /// </summary>
    public static async Task<int> ReassignPendingStepsAsync(AttendanceDbContext db, IReadOnlyCollection<int> deactivatedUserIds)
    {
        if (deactivatedUserIds.Count == 0) return 0;
        var steps = await db.ApprovalSteps
            .Include(s => s.ApprovalRequest).ThenInclude(r => r.Applicant)
            .Include(s => s.ApprovalRequest).ThenInclude(r => r.ApprovalSteps)
            .Where(s => s.ApprovalStatus == ApprovalStatus.Pending
                        && deactivatedUserIds.Contains(s.ApproverUserId)
                        && (s.ApprovalRequest.ApprovalStatus == ApprovalStatus.Pending
                            || s.ApprovalRequest.ApprovalStatus == ApprovalStatus.InProgress))
            .ToListAsync();
        var moved = 0;
        foreach (var step in steps)
        {
            var applicant = step.ApprovalRequest.Applicant;
            if (applicant is null) continue;
            // 同一张单上别的节点已经是这个人的话（比如二级审批里直属上级本来就是第二级），换一个人，免得同一个人连点两次"通过"
            var otherApprovers = step.ApprovalRequest.ApprovalSteps.Where(x => x.Id != step.Id).Select(x => x.ApproverUserId).ToHashSet();
            var supervisorOk = applicant.SupervisorUserId is { } sid && sid != applicant.Id && !deactivatedUserIds.Contains(sid)
                               && !otherApprovers.Contains(sid)
                               && await db.Users.AnyAsync(u => u.Id == sid && u.IsActive && ApproverRoles.Contains(u.Role));
            var newApprover = supervisorOk ? applicant.SupervisorUserId : await ResolveFallbackApproverAsync(db, applicant);
            // 兜底人选如果也撞了，宁可让他多点一次，也不能让单子没人批，所以这里不再换人
            if (newApprover is null || newApprover == step.ApproverUserId || deactivatedUserIds.Contains(newApprover.Value)) continue;

            step.ApproverUserId = newApprover.Value;
            db.Notifications.Add(new Notification
            {
                UserId           = newApprover.Value,
                Title            = "审批流转通知",
                Content          = $"{applicant.RealName} 的{step.ApprovalRequest.ApprovalType.ToDisplayName()}申请（{step.ApprovalRequest.RequestNo}）原审批人已离职/停用，已转交给您，请处理",
                NotificationType = "ApprovalPending",
                RelatedId        = step.ApprovalRequestId,
                CreatedAt        = DateTime.Now,
            });
            moved++;
        }
        if (moved > 0) await db.SaveChangesAsync();
        return moved;
    }
}
