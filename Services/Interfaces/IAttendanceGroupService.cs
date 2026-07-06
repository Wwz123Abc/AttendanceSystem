namespace AttendanceSystem.Services.Interfaces;

/// <summary>考勤组与部门联动服务：按部门自动建组、让员工考勤组跟随部门。</summary>
public interface IAttendanceGroupService
{
    /// <summary>确保某部门有对应的考勤组（没有就自动创建），返回该考勤组 Id；部门不存在返回 0。</summary>
    Task<int> EnsureDeptGroupAsync(int deptId);

    /// <summary>
    /// 按部门全量同步：给每个启用部门补建考勤组，并把每位“有部门”的员工归入其部门对应的考勤组。
    /// 返回（新建组数, 调整归属的员工数）。
    /// </summary>
    Task<(int GroupsCreated, int UsersMoved)> SyncAllAsync();
}
