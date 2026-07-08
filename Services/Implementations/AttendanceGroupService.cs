using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>考勤组与部门联动：查部门跟随的考勤组、给考勤组配置跟随部门并批量归组。</summary>
public class AttendanceGroupService(AttendanceDbContext db) : IAttendanceGroupService
{
    public Task<int?> GetGroupIdForDepartmentAsync(int deptId)
        => db.Departments.Where(d => d.Id == deptId).Select(d => d.AttendanceGroupId).FirstOrDefaultAsync();

    public async Task<int> SetGroupDepartmentsAsync(int groupId, List<int> departmentIds)
    {
        var deptIdSet = departmentIds.Distinct().ToHashSet();

        // 之前跟着本组、但这次没勾选的部门 → 解除跟随关系
        var previouslyLinked = await db.Departments
            .Where(d => d.AttendanceGroupId == groupId && !deptIdSet.Contains(d.Id))
            .ToListAsync();
        foreach (var d in previouslyLinked)
            d.AttendanceGroupId = null;

        // 这次勾选的部门 → 改为跟随本组（哪怕之前跟着别的组，直接改过来）
        var toLink = await db.Departments.Where(d => deptIdSet.Contains(d.Id)).ToListAsync();
        foreach (var d in toLink)
            d.AttendanceGroupId = groupId;

        // 所属公司：取勾选部门里出现过的公司名，去重拼起来，作为本组的展示文字
        var group = await db.AttendanceGroups.FindAsync(groupId);
        if (group is not null)
        {
            var companyNames = toLink
                .Select(d => d.CompanyName)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();
            group.CompanyName = companyNames.Count > 0 ? string.Join("、", companyNames) : null;
            group.UpdatedAt   = DateTime.Now;
        }

        // 长期跟随：这些部门现有的员工立即批量归入本组（以后新入职/调入这些部门的员工，
        // 会在「员工管理」保存时通过 GetGroupIdForDepartmentAsync 查到本组并自动归入）
        var users = await db.Users
            .Where(u => u.DepartmentId != null && deptIdSet.Contains(u.DepartmentId.Value))
            .ToListAsync();
        var moved = 0;
        foreach (var u in users)
        {
            if (u.AttendanceGroupId == groupId) continue;
            u.AttendanceGroupId = groupId;
            u.UpdatedAt         = DateTime.Now;
            moved++;
        }

        await db.SaveChangesAsync();
        return moved;
    }
}
