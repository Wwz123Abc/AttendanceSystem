using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>员工扫码自助登记服务：校验格式、挡重复提交、给管理员出待确认列表。</summary>
public class EmployeeRegistrationService(AttendanceDbContext db) : IEmployeeRegistrationService
{
    /// <summary>提交登记：先校验姓名/手机号/身份证号格式，再挡掉"已经是员工"或"已经提交过还没处理"这两种重复情况。</summary>
    public async Task SubmitAsync(SubmitRegistrationDto dto)
    {
        var realName = dto.RealName.Trim();
        var phone    = dto.Phone.Trim();
        var idNumber = dto.IdNumber.Trim().ToUpperInvariant();   // 身份证号末位可能是 x，统一转大写方便比对

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

        // 已经是在职员工了，不用再登记一遍，让员工直接联系管理员处理
        var alreadyEmployee = await db.Users.AnyAsync(u => u.Phone == phone || u.IdNumber == idNumber);
        if (alreadyEmployee)
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
            Status      = RegistrationStatus.Pending,
            SubmittedAt = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>查所有待确认的登记，按提交时间从新到旧排。</summary>
    public async Task<List<EmployeeRegistrationDto>> GetPendingAsync()
    {
        return await db.EmployeeRegistrations
            .Where(r => r.Status == RegistrationStatus.Pending)
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
                Status      = r.Status,
                StatusText  = r.Status.ToDisplayName(),
                SubmittedAt = r.SubmittedAt
            })
            .ToListAsync();
    }

    /// <summary>驳回：只能驳回还在"待确认"状态的登记，避免重复处理已经处理过的记录。</summary>
    public async Task RejectAsync(int id, string? reason)
    {
        var reg = await db.EmployeeRegistrations.FindAsync(id);
        if (reg is null || reg.Status != RegistrationStatus.Pending)
            throw new InvalidOperationException("该登记不存在，或已经被处理过了");

        reg.Status       = RegistrationStatus.Rejected;
        reg.RejectReason  = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        reg.ReviewedAt    = DateTime.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>管理员补全信息、正式建好账号后调用：把登记标记为已确认，关联上新账号。</summary>
    public async Task MarkConfirmedAsync(int id, int confirmedUserId)
    {
        var reg = await db.EmployeeRegistrations.FindAsync(id);
        if (reg is null || reg.Status != RegistrationStatus.Pending)
            throw new InvalidOperationException("该登记不存在，或已经被处理过了");

        reg.Status          = RegistrationStatus.Confirmed;
        reg.ConfirmedUserId  = confirmedUserId;
        reg.ReviewedAt       = DateTime.Now;
        await db.SaveChangesAsync();
    }
}
