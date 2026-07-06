using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>考勤组与部门联动：按部门自动建组、员工考勤组跟随部门。</summary>
public class AttendanceGroupService(AttendanceDbContext db) : IAttendanceGroupService
{
    /// <summary>确保某部门有对应考勤组（没有就建），返回考勤组 Id。</summary>
    public async Task<int> EnsureDeptGroupAsync(int deptId)
    {
        var dept = await db.Departments.FindAsync(deptId);
        if (dept is null) return 0;

        var group = await db.AttendanceGroups.FirstOrDefaultAsync(g => g.DepartmentId == deptId);
        if (group is null)
        {
            group = new AttendanceGroup
            {
                DepartmentId = deptId,
                GroupName    = dept.DeptName,
                CompanyName  = dept.CompanyName,
                IsActive     = true,
                CreatedAt    = DateTime.Now,
                UpdatedAt    = DateTime.Now
            };
            db.AttendanceGroups.Add(group);
            await db.SaveChangesAsync();
        }
        return group.Id;
    }

    /// <summary>按部门全量同步：补建考勤组 + 员工归入其部门的考勤组。</summary>
    public async Task<(int GroupsCreated, int UsersMoved)> SyncAllAsync()
    {
        var depts  = await db.Departments.Where(d => d.IsActive).ToListAsync();
        var groups = await db.AttendanceGroups.Where(g => g.DepartmentId != null).ToListAsync();
        var byDept = groups.ToDictionary(g => g.DepartmentId!.Value, g => g);

        // 1) 给缺组的部门补建考勤组
        var created = 0;
        foreach (var d in depts)
        {
            if (byDept.ContainsKey(d.Id)) continue;
            var g = new AttendanceGroup
            {
                DepartmentId = d.Id,
                GroupName    = d.DeptName,
                CompanyName  = d.CompanyName,
                IsActive     = true,
                CreatedAt    = DateTime.Now,
                UpdatedAt    = DateTime.Now
            };
            db.AttendanceGroups.Add(g);
            byDept[d.Id] = g;
            created++;
        }
        if (created > 0) await db.SaveChangesAsync();   // 先存，拿到新组的 Id

        // 2) 员工归组：有部门的人 → 其部门对应的考勤组
        var moved = 0;
        var users = await db.Users.Where(u => u.DepartmentId != null).ToListAsync();
        foreach (var u in users)
        {
            if (byDept.TryGetValue(u.DepartmentId!.Value, out var g) && u.AttendanceGroupId != g.Id)
            {
                u.AttendanceGroupId = g.Id;
                u.UpdatedAt         = DateTime.Now;
                moved++;
            }
        }
        if (moved > 0) await db.SaveChangesAsync();

        return (created, moved);
    }
}
