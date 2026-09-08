using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IDeptScopeService"/>
public class DeptScopeService(AttendanceDbContext db) : IDeptScopeService
{
    public async Task<HashSet<int>> GetSubtreeIdsAsync(int deptId)
    {
        var all      = await db.Departments.Select(d => new { d.Id, d.ParentId }).ToListAsync();
        var byParent = all.GroupBy(d => d.ParentId ?? 0).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var result = new HashSet<int> { deptId };
        void Walk(int id)
        {
            if (!byParent.TryGetValue(id, out var kids)) return;
            foreach (var k in kids)
                if (result.Add(k)) Walk(k);   // Add 返回 false 说明已经访问过，防止部门数据成环时死循环
        }
        Walk(deptId);
        return result;
    }

    public async Task<HashSet<int>?> GetVisibleDeptIdsAsync(CurrentUser currentUser)
        => currentUser.ScopedDepartmentId.HasValue
            ? await GetSubtreeIdsAsync(currentUser.ScopedDepartmentId.Value)
            : null;

    public async Task<int?> ResolveEffectiveDeptIdAsync(CurrentUser currentUser, int? requestedDeptId)
    {
        if (!currentUser.ScopedDepartmentId.HasValue)
            return requestedDeptId;   // 不受限，调用方传什么就用什么

        var visibleIds = await GetSubtreeIdsAsync(currentUser.ScopedDepartmentId.Value);
        // 没传、或者传的部门不在自己范围内 → 强制收窄成自己的范围根；传的在范围内 → 保留（允许受限管理员
        // 在自己范围内进一步缩小到某个子部门查看）
        return requestedDeptId.HasValue && visibleIds.Contains(requestedDeptId.Value)
            ? requestedDeptId
            : currentUser.ScopedDepartmentId;
    }

    public async Task<List<int>?> ResolveEffectiveDeptIdsAsync(CurrentUser currentUser, List<int>? requestedDeptIds)
    {
        if (!currentUser.ScopedDepartmentId.HasValue)
            return requestedDeptIds;

        var visibleIds = await GetSubtreeIdsAsync(currentUser.ScopedDepartmentId.Value);
        if (requestedDeptIds is null || requestedDeptIds.Count == 0)
            return visibleIds.ToList();   // 没传 → 默认给自己整个范围

        var intersect = requestedDeptIds.Where(visibleIds.Contains).ToList();
        return intersect.Count > 0 ? intersect : visibleIds.ToList();   // 传的全部不在范围内 → 兜底成自己整个范围，不是空列表
    }

    public async Task<bool> CanAccessDeptAsync(CurrentUser currentUser, int? deptId)
    {
        if (!currentUser.ScopedDepartmentId.HasValue)
            return true;   // 不受限，什么都能访问

        if (!deptId.HasValue)
            return false;  // 受限用户不能访问"没有部门"的数据

        var visibleIds = await GetSubtreeIdsAsync(currentUser.ScopedDepartmentId.Value);
        return visibleIds.Contains(deptId.Value);
    }
}
