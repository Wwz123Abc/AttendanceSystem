using AttendanceSystem.Middlewares;
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

    /// <summary>查"待确认"的登记，按提交时间由新到旧排列，给管理员看。
    /// <paramref name="visibleDeptIds"/> 为空表示不受限（总部超级管理员，看全部，含没带部门信息的旧版通用链接提交）；
    /// 不为空时只返回意向部门落在这个集合内的登记——分公司管理员看不到别的分公司的人扫码登记的姓名/身份证号等隐私信息，
    /// 也看不到没带部门信息的登记（那种交给总部处理）。</summary>
    Task<List<EmployeeRegistrationDto>> GetPendingAsync(HashSet<int>? visibleDeptIds = null);

    /// <summary>管理员驳回一条登记（不建账号）。<paramref name="currentUser"/> 必须能看到这条登记的意向部门
    /// （受限管理员只能驳回自己范围内的登记），否则拒绝——防止跨分公司驳回别人的登记。</summary>
    Task RejectAsync(int id, string? reason, CurrentUser currentUser);

    /// <summary>管理员确认通过、正式建好账号之后，把这条登记标记为「已确认」并关联上新账号。</summary>
    Task MarkConfirmedAsync(int id, int confirmedUserId);

    /// <summary>原子"认领"一条待确认登记：只有还是"待确认"状态、且 <paramref name="currentUser"/> 能看到这条登记的
    /// 意向部门才能抢到，抢到了才允许继续走"建员工"流程，避免管理员双击/网络重试导致同一条登记被处理两次、
    /// 建出两个重复账号；范围外的登记会跟"已经被别人抢走"一样统一返回 false，调用方本来就要在 false 时中止，
    /// 不用额外区分，顺带也不会暴露"这条 id 到底存不存在/是不是别的分公司的"。</summary>
    Task<bool> ClaimForConfirmAsync(int id, CurrentUser currentUser);

    /// <summary>认领成功后、建号过程中途失败（工号重复、校验不通过等）时的补救：把状态改回「待确认」，
    /// 让这条登记能被重新处理，不然登记人明明没建成号，却再也进不了"待确认"列表，只能让员工重新扫码提交。
    /// 只在还没关联到任何账号（ConfirmedUserId 为空）时才回退，避免误把一条已经真正建号成功的登记退回去。</summary>
    Task RevertClaimAsync(int id);
}
