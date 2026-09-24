using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>
/// 身份证照/人脸照/审批附件/考勤机抓拍照片这类敏感文件的授权访问入口。
/// 这些文件存在 wwwroot 之外（见 PrivateFileStorage），不会被 UseStaticFiles 匿名下发，
/// 必须登录后走这里，再按文件类型判一次权限。
/// 路由刻意保持跟以前放 wwwroot 时一样的 "/uploads/..." 前缀——这样数据库里已经存的旧 URL
/// （IdCardPhotoUrl/FaceReferencePhotoUrl/AttachmentUrls 等）完全不用改，页面上的
/// &lt;img src&gt;/&lt;a href&gt; 也不用改，只是背后从"匿名读 wwwroot"变成"登录后走这里判权限"。
/// 权限判断按分类走"显式白名单"：只认识 idcards/zkdevice/faces/approvals 这四类目录结构，
/// 除了角色，还按文件实际归属的部门/人二次校验；识别不了的分类一律拒绝（不是"仅登录即可读"），
/// 避免以后新增一种上传目录时，不小心漏了权限校验也能被匿名/任意登录用户读到。
/// </summary>
[Authorize]
[Route("uploads")]
public class PrivateFilesController(IWebHostEnvironment env, AttendanceDbContext db, IDeptScopeService deptScopeService) : Controller
{
    [HttpGet("{**relativePath}")]
    public async Task<IActionResult> Get(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return NotFound();

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var category = segments.Length > 0 ? segments[0] : "";
        var cu        = HttpContext.GetCurrentUser()!;
        var isManager = cu.Role is UserRole.Admin or UserRole.Clerk;

        var allowed = category switch
        {
            "idcards"   => isManager && await CanAccessIdCardsAsync(segments),
            "zkdevice"  => isManager && await CanAccessZkDeviceAsync(segments),
            "faces"     => await CanAccessFacesAsync(segments, isManager),
            "approvals" => await CanAccessApprovalAsync(segments, isManager),
            _           => false   // 不认识的分类，一律拒绝，不再是"仅登录即可读"
        };
        if (!allowed) return Forbid();

        // 文件实际存放在 {PrivateUploads}/uploads/{分类}/...（各处上传代码都是这样落盘的，数据库里存的 URL 也带
        // "/uploads/" 前缀）；而路由前缀 "uploads" 已经被路由吃掉了，relativePath 只剩 "{分类}/..."，
        // 所以这里必须把 "uploads" 这一层补回来，否则所有身份证照/人脸照/附件都会 404。
        var root     = Path.GetFullPath(Path.Combine(PrivateFileStorage.GetRoot(env), "uploads"));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Forbid();   // 防路径穿越
        if (!System.IO.File.Exists(fullPath)) return NotFound();

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".webp" => "image/webp",
            _       => "image/jpeg"
        };
        return PhysicalFile(fullPath, contentType);
    }

    /// <summary>idcards（身份证照片）：目录结构 idcards/{工号}/xxx 或 idcards/registrations/{手机号}/xxx（未确认扫码登记）。
    /// 按对应员工/登记的部门做范围校验——只是"管理员/文员"身份不够，还要管得到那个部门。</summary>
    private async Task<bool> CanAccessIdCardsAsync(string[] segments)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (segments.Length < 2) return false;

        if (segments[1] == "registrations")
        {
            if (segments.Length < 3) return false;
            var phone = segments[2];
            // 同一个手机号理论上可能对应好几条登记（比如驳回过一次又重新提交），任一条落在自己范围内就放行
            var deptIds = await db.EmployeeRegistrations.Where(r => r.Phone == phone)
                .Select(r => r.DepartmentId).Distinct().ToListAsync();
            foreach (var d in deptIds)
                if (await deptScopeService.CanAccessDeptAsync(cu, d)) return true;
            return false;
        }

        // 按"这张照片的地址是谁的证件照"反查所属员工，再看他的部门。以前是按路径里的工号反查：
        // ① 员工改了工号，旧路径里的工号查不到人 → 部门为空 → 分公司管理员被拒（总部不受影响，问题很隐蔽）；
        // ② 工号被删除后可以复用，新员工占了这个工号，旧照片的可读性就由"新员工的部门"决定，
        //    别的分公司的管理员能读到上一任员工的身份证照。地址是每张照片唯一的，不受工号变化影响。
        // 找不到任何员工引用这张照片（孤儿文件）就不给读。
        var url = "/uploads/" + string.Join('/', segments);
        var owner = await db.Users.Where(u => u.IdCardPhotoUrl == url)
            .Select(u => new { u.DepartmentId }).FirstOrDefaultAsync();
        if (owner is null) return false;
        return await deptScopeService.CanAccessDeptAsync(cu, owner.DepartmentId);
    }

    /// <summary>zkdevice（考勤机抓拍照片）：目录结构 zkdevice/{yyyyMMdd}/{SN}_{时间}_{guid}.jpg，
    /// 文件名开头的 SN 就是拍下这张照片的那台设备——按设备归属部门做范围校验。</summary>
    private async Task<bool> CanAccessZkDeviceAsync(string[] segments)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (segments.Length < 3) return false;

        var fileName = segments[2];
        // 文件名固定格式是 {SN}_{HHmmss}_{32位十六进制guid}.ext，从右边数第二个下划线之前就是 SN——
        // SN 本身允许包含下划线也不影响，因为后面这两段是我们自己生成时固定加上去的，位置是确定的
        var lastUnderscore       = fileName.LastIndexOf('_');
        var secondLastUnderscore = lastUnderscore < 0 ? -1 : fileName.LastIndexOf('_', lastUnderscore - 1);
        if (secondLastUnderscore <= 0) return false;
        var sn = fileName[..secondLastUnderscore];

        var deptId = await db.ZKDevices.Where(d => d.SN == sn).Select(d => d.DepartmentId).FirstOrDefaultAsync();
        return await deptScopeService.CanAccessDeptAsync(cu, deptId);
    }

    /// <summary>faces（人脸参考照/远程打卡现场抓拍）：目录结构 faces/{userId}/xxx 或
    /// faces/attempts/{yyyyMMdd}/{userId}_{时间}_{guid}.jpg。本人一定能看自己的；
    /// 其他人要看，必须是管理员/文员，且管得到这个人所在的部门。</summary>
    private async Task<bool> CanAccessFacesAsync(string[] segments, bool isManager)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (segments.Length < 2) return false;

        int ownerId;
        if (segments[1] == "attempts")
        {
            if (segments.Length < 4) return false;
            var underscoreIdx = segments[3].IndexOf('_');
            if (underscoreIdx <= 0 || !int.TryParse(segments[3][..underscoreIdx], out ownerId)) return false;
        }
        else if (!int.TryParse(segments[1], out ownerId))
        {
            return false;
        }

        if (ownerId == cu.UserId) return true;
        if (!isManager) return false;
        var deptId = await db.Users.Where(u => u.Id == ownerId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
        return await deptScopeService.CanAccessDeptAsync(cu, deptId);
    }

    /// <summary>approvals（请假/出差等申请附件）：目录结构 approvals/{申请人UserId}/xxx。
    /// 申请人本人、管得到申请人部门的管理员/文员、或这张申请单实际的审批人（含班组长/主管），才能看。</summary>
    private async Task<bool> CanAccessApprovalAsync(string[] segments, bool isManager)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (segments.Length < 2 || !int.TryParse(segments[1], out var applicantId)) return false;
        if (applicantId == cu.UserId) return true;

        if (isManager)
        {
            var deptId = await db.Users.Where(u => u.Id == applicantId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
            if (await deptScopeService.CanAccessDeptAsync(cu, deptId)) return true;
        }

        return await db.ApprovalSteps
            .AnyAsync(s => s.ApproverUserId == cu.UserId && s.ApprovalRequest.ApplicantUserId == applicantId);
    }
}
