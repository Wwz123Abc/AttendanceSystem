using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>员工扫码自助登记服务：校验格式、挡重复提交、给管理员出待确认列表。</summary>
public class EmployeeRegistrationService(AttendanceDbContext db, IDeptScopeService deptScopeService) : IEmployeeRegistrationService
{
    /// <summary>提交登记：先校验姓名/手机号/身份证号格式，再挡掉"已经是员工"或"已经提交过还没处理"这两种重复情况。</summary>
    public async Task SubmitAsync(SubmitRegistrationDto dto)
    {
        // 表单里留空提交时，模型绑定会把空串变成 null，直接 Trim 会空引用、员工只看到"提交失败，请稍后重试"，
        // 而不是"请填写姓名"，所以先兜成空串再校验
        var realName = (dto.RealName ?? "").Trim();
        var phone    = (dto.Phone ?? "").Trim();
        var idNumber = (dto.IdNumber ?? "").Trim().ToUpperInvariant();   // 身份证号末位可能是 x，统一转大写方便比对

        if (string.IsNullOrEmpty(realName))
            throw new InvalidOperationException("请填写姓名");
        if (realName.Length > 50)
            throw new InvalidOperationException("姓名不能超过 50 个字");
        if (!Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            throw new InvalidOperationException("请输入正确格式的手机号（11 位中国大陆手机号）");
        if (!Regex.IsMatch(idNumber, @"^\d{17}[\dX]$"))
            throw new InvalidOperationException("请输入正确格式的身份证号（18 位）");

        // 岗位/劳务公司/住址/紧急联系人/身份证照片：以前是选填，现在改成全部必填，
        // 员工扫码登记时必须一次性把资料填完整，管理员确认录入时才不用再补问一遍。
        if (string.IsNullOrWhiteSpace(dto.Position) || !IEmployeeRegistrationService.AllowedPositions.Contains(dto.Position))
            throw new InvalidOperationException("请选择正确的岗位选项");
        if (string.IsNullOrWhiteSpace(dto.ContractCompany))
            throw new InvalidOperationException("请填写劳务公司");
        if (dto.ContractCompany.Trim().Length > 100)
            throw new InvalidOperationException("劳务公司不能超过 100 个字");
        if (string.IsNullOrWhiteSpace(dto.HomeAddress))
            throw new InvalidOperationException("请填写家庭住址");
        if (dto.HomeAddress.Trim().Length > 200)
            throw new InvalidOperationException("家庭住址不能超过 200 个字");
        if (string.IsNullOrWhiteSpace(dto.EmergencyContactName))
            throw new InvalidOperationException("请填写紧急联系人姓名");
        if (dto.EmergencyContactName.Trim().Length > 50)
            throw new InvalidOperationException("紧急联系人姓名不能超过 50 个字");
        if (string.IsNullOrWhiteSpace(dto.EmergencyContactPhone))
            throw new InvalidOperationException("请填写紧急联系人电话");
        if (!Regex.IsMatch(dto.EmergencyContactPhone.Trim(), @"^1[3-9]\d{9}$"))
            throw new InvalidOperationException("请输入正确格式的紧急联系人电话（11 位中国大陆手机号）");
        if (string.IsNullOrWhiteSpace(dto.IdCardPhotoUrl))
            throw new InvalidOperationException("请上传身份证照片");

        // deptId 是从二维码链接的查询参数带过来的，页面未登录也能访问，理论上谁都能改链接里的数字瞎填——
        // 这里不能直接信，查不到就当没带（落进"未指定"那一档，交给总部处理），不能让一个乱填的 id
        // 直接绑进数据库外键触发约束错误、把提交流程搞崩
        var deptId = dto.DepartmentId.HasValue && await db.Departments.AnyAsync(d => d.Id == dto.DepartmentId.Value)
            ? dto.DepartmentId : null;

        // 身份证号命中"已拉黑"人员（永不录用）直接拒绝——跟 UserService.CreateUserAsync 建档时
        // 的那道口径一致，这里提前挡一次，不用等员工填完一整张表、管理员确认时才发现被拒
        if (await db.Users.AnyAsync(u => u.IdNumber == idNumber && u.IsBlacklisted))
            throw new InvalidOperationException("该身份证号已被拉黑（永不录用），请联系总部处理");

        // 只拦"在职"的重复——已经是在职员工了，不用再登记一遍，让员工直接联系管理员处理；
        // 已停用（不在黑名单）的老员工要能重新扫码登记走离职再入职流程，不能因为老账号还在库里
        // （历史考勤记录要保留，不会真删掉）就被一直拦在外面
        var alreadyActiveEmployee = await db.Users.AnyAsync(u => u.IsActive && (u.Phone == phone || u.IdNumber == idNumber));
        if (alreadyActiveEmployee)
            throw new InvalidOperationException("该手机号或身份证号已在系统中，如有疑问请联系管理员");

        // 之前提交过、管理员还没处理，不用再交一次，避免同一个人反复刷出好几条待确认
        var alreadyPending = await db.EmployeeRegistrations.AnyAsync(r =>
            r.Status == RegistrationStatus.Pending && (r.Phone == phone || r.IdNumber == idNumber));
        if (alreadyPending)
            throw new InvalidOperationException("您已提交过登记，请耐心等待管理员确认，无需重复提交");

        db.EmployeeRegistrations.Add(new EmployeeRegistration
        {
            RealName              = realName,
            Phone                 = phone,
            IdNumber              = idNumber,
            Position              = string.IsNullOrWhiteSpace(dto.Position)              ? null : dto.Position.Trim(),
            ContractCompany       = string.IsNullOrWhiteSpace(dto.ContractCompany)       ? null : dto.ContractCompany.Trim(),
            HomeAddress           = string.IsNullOrWhiteSpace(dto.HomeAddress)           ? null : dto.HomeAddress.Trim(),
            EmergencyContactName  = string.IsNullOrWhiteSpace(dto.EmergencyContactName)  ? null : dto.EmergencyContactName.Trim(),
            EmergencyContactPhone = string.IsNullOrWhiteSpace(dto.EmergencyContactPhone) ? null : dto.EmergencyContactPhone.Trim(),
            IdCardPhotoUrl        = string.IsNullOrWhiteSpace(dto.IdCardPhotoUrl)        ? null : dto.IdCardPhotoUrl,
            DepartmentId = deptId,
            Status      = RegistrationStatus.Pending,
            SubmittedAt = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>查"待确认"的登记，按提交时间从新到旧排。</summary>
    public async Task<List<EmployeeRegistrationDto>> GetPendingAsync(HashSet<int>? visibleDeptIds = null)
    {
        var query = db.EmployeeRegistrations.Where(r => r.Status == RegistrationStatus.Pending);
        // 受限管理员：只看意向部门落在自己管理范围内的登记——没带部门信息的（旧版通用链接提交的）
        // 一律看不到，交给总部处理，不能因为查不到部门就默认放行
        if (visibleDeptIds is not null)
            query = query.Where(r => r.DepartmentId != null && visibleDeptIds.Contains(r.DepartmentId.Value));

        return await query
            .OrderByDescending(r => r.SubmittedAt)
            .Select(r => new EmployeeRegistrationDto
            {
                Id                    = r.Id,
                RealName              = r.RealName,
                Phone                 = r.Phone,
                IdNumber              = r.IdNumber,
                Position              = r.Position,
                ContractCompany       = r.ContractCompany,
                HomeAddress           = r.HomeAddress,
                EmergencyContactName  = r.EmergencyContactName,
                EmergencyContactPhone = r.EmergencyContactPhone,
                IdCardPhotoUrl        = r.IdCardPhotoUrl,
                DepartmentId          = r.DepartmentId,
                DeptName              = r.Department != null ? r.Department.DeptName : null,
                Status      = r.Status,
                StatusText  = r.Status.ToDisplayName(),
                SubmittedAt = r.SubmittedAt
            })
            .ToListAsync();
    }

    /// <summary>驳回：只能驳回还在"待确认"状态、且调用者能看到其意向部门的登记——受限管理员不能驳回别的
    /// 分公司（或无部门归属，只总部可见）的登记，避免跨公司数据破坏。</summary>
    public async Task RejectAsync(int id, string? reason, CurrentUser currentUser)
    {
        var reg = await db.EmployeeRegistrations.FindAsync(id);
        if (reg is null || reg.Status != RegistrationStatus.Pending)
            throw new InvalidOperationException("该登记不存在，或已经被处理过了");
        if (!await deptScopeService.CanAccessDeptAsync(currentUser, reg.DepartmentId))
            throw new InvalidOperationException("该登记不存在，或已经被处理过了");   // 跟"不存在"用同一句话，不额外暴露越权信息

        reg.Status       = RegistrationStatus.Rejected;
        reg.RejectReason  = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        reg.ReviewedAt    = DateTime.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 原子"认领"一条待确认登记：用 ExecuteUpdateAsync 直接在数据库层面做条件更新（WHERE Status = Pending），
    /// 不经过内存里的 change tracker，天然没有"先查后改"之间的竞态窗口。管理员双击"确认录入"、或者网络重试
    /// 导致同一条登记被提交两次处理时，只有第一次能抢到（返回 true），第二次会抢不到（返回 false），
    /// 调用方看到 false 就应该直接中止、不要再往下走"建员工"这一步，避免建出两个重复账号。
    /// 先查一次这条登记的意向部门，确认调用者能看到才走认领——登记的部门在提交后不会再变，
    /// 这一步和下面的原子更新之间没有真正的竞态风险；范围外的登记直接当"抢不到"处理，返回 false。
    /// </summary>
    public async Task<bool> ClaimForConfirmAsync(int id, CurrentUser currentUser)
    {
        var deptId = await db.EmployeeRegistrations.Where(r => r.Id == id)
            .Select(r => (int?)r.DepartmentId).FirstOrDefaultAsync();
        if (!await deptScopeService.CanAccessDeptAsync(currentUser, deptId))
            return false;

        var affected = await db.EmployeeRegistrations
            .Where(r => r.Id == id && r.Status == RegistrationStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RegistrationStatus.Confirmed)
                .SetProperty(r => r.ReviewedAt, DateTime.Now));
        return affected > 0;
    }

    /// <summary>管理员补全信息、正式建好账号后调用：把登记关联上新账号（状态在 ClaimForConfirmAsync 里已经改过了）。</summary>
    public async Task MarkConfirmedAsync(int id, int confirmedUserId)
    {
        var reg = await db.EmployeeRegistrations.FindAsync(id);
        if (reg is null) throw new InvalidOperationException("该登记不存在");

        reg.ConfirmedUserId  = confirmedUserId;
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task RevertClaimAsync(int id)
    {
        await db.EmployeeRegistrations
            .Where(r => r.Id == id && r.Status == RegistrationStatus.Confirmed && r.ConfirmedUserId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RegistrationStatus.Pending)
                .SetProperty(r => r.ReviewedAt, (DateTime?)null));
    }
}
