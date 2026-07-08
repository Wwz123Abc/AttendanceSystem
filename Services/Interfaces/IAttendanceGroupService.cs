namespace AttendanceSystem.Services.Interfaces;

/// <summary>考勤组与部门联动服务：查某部门长期跟随的考勤组、给考勤组配置所跟随的部门。</summary>
public interface IAttendanceGroupService
{
    /// <summary>
    /// 查某部门长期跟随的考勤组编号。部门没有配置跟随的考勤组时返回 null
    /// （这种情况下，新建/编辑该部门的员工要沿用表单里手动选的考勤组，不会被强制改）。
    /// </summary>
    Task<int?> GetGroupIdForDepartmentAsync(int deptId);

    /// <summary>
    /// 给考勤组配置所跟随的部门（在"考勤组管理"页勾选部门树后调用）：
    /// 没被勾选的部门解除跟随；被勾选的部门改为跟随本组（哪怕之前跟着别的组，也会直接改过来）；
    /// 同时把这些部门里现有的员工立即批量归入本组，并按勾选部门的所属公司重新拼一遍本组的"所属公司"展示文字。
    /// 返回本次实际调整了考勤组的员工人数。
    /// </summary>
    Task<int> SetGroupDepartmentsAsync(int groupId, List<int> departmentIds);
}
