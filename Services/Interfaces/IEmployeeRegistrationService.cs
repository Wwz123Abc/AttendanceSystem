using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>员工扫码自助登记服务契约：员工提交基础信息，管理员确认后再正式建号。</summary>
public interface IEmployeeRegistrationService
{
    /// <summary>岗位固定选项列表（登记页下拉框、服务端校验共用同一份，避免两处维护不一致）。</summary>
    static readonly string[] AllowedPositions =
        ["电气熟手", "机械熟手", "电气中工", "机械中工", "普工", "调试熟手", "调试中工", "CNC操作工", "磨工"];

    /// <summary>员工扫码提交登记（姓名/手机号/身份证号等）。会校验格式，并挡掉重复提交。</summary>
    Task SubmitAsync(SubmitRegistrationDto dto);

    /// <summary>查所有"待确认"的登记，按提交时间由新到旧排列，给管理员看。</summary>
    Task<List<EmployeeRegistrationDto>> GetPendingAsync();

    /// <summary>管理员驳回一条登记（不建账号）。</summary>
    Task RejectAsync(int id, string? reason);

    /// <summary>管理员确认通过、正式建好账号之后，把这条登记标记为「已确认」并关联上新账号。</summary>
    Task MarkConfirmedAsync(int id, int confirmedUserId);
}
