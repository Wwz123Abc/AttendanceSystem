using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

// 「服务(Service)」= 业务逻辑的集中地。控制器/页面负责接收请求，真正干活的逻辑写在服务里。

/// <summary>
/// 用户服务：登录校验、员工增删改查、密码管理。
/// 密码用 PBKDF2(SHA256) 加盐哈希存储，数据库里永远不存明文密码。
/// </summary>
public class UserService(
    AttendanceDbContext db,
    IZKDeviceSyncService zkDeviceSyncService,
    IOptions<AppSettingsOptions> appOptions,
    IDeptScopeService deptScopeService,
    ILogger<UserService> logger) : IUserService
{
    /// <summary>校验工号+密码。成功返回用户；工号/密码错、账号已停用、或账号被临时锁定，统一返回 null
    /// （登录页看到的提示不区分这几种情况——区分开会让人拿不同提示反推出哪些工号是真实存在的账号，
    /// 等于账号可被枚举）。</summary>
    public async Task<User?> ValidateLoginAsync(string employeeNo, string password)
    {
        // 手机浏览器/输入法很容易在工号或密码前后带出一个看不见的空格（比如中文输入法候选栏
        // 确认数字候选词后自动补一个空格），密码框本身还是打码的，用户自己根本发现不了。
        // 生产上已经实测抓到过一批账号反复"密码错误"锁定——查了他们的密码哈希，用真正的初始密码
        // "123456"（不带任何空格）重新算一遍，是能对上的，问题就出在这多出来的空格上。这里统一先
        // trim 掉再校验，跟建号/重置密码那边"最终存的密码"本来就不带空格的口径对齐。
        // 另一个同类的手机输入法坑：中文输入法有时会切到全角模式，打出来的"123456"其实是"１２３４５６"
        // 这种全角数字，打码的密码框里肉眼完全看不出来，但对计算机是完全不同的字符——这里一并转成半角。
        employeeNo = NormalizeFullWidthDigits(employeeNo.Trim());
        password   = NormalizeFullWidthDigits(password.Trim());

        // 故意不在查询里过滤“在职”，好让下面能对"停用账号"单独记一条日志（对外仍然统一按失败处理）
        var user = await db.Users
            .Include(u => u.Department)
            .Include(u => u.AttendanceGroup)
            .Where(u => u.EmployeeNo == employeeNo)
            .OrderByDescending(u => u.IsActive)   // 万一同工号有多条，优先取在职的
            .FirstOrDefaultAsync();

        // 工号不存在时也要跑一遍同样耗时的密码哈希计算（对着一个固定的假哈希值比对，结果反正会被
        // "user is null"直接覆盖掉，不影响判断）——PBKDF2 故意做得很慢，如果工号不存在直接跳过这一步、
        // 工号存在但密码错才要等哈希算完，两种情况的响应时间会有明显差异，能被人拿来批量探测哪些工号
        // 是真实存在的账号（时序侧信道）。账号已经被锁定时同样要跑这一遍，不能提前 return，否则"被锁定"
        // 又会变成一种新的、能靠响应时间区分出来的信号。
        var passwordOk = VerifyPassword(password, user?.PasswordHash ?? DummyPasswordHashForTimingSafety);

        // 连续输错密码次数太多，账号被临时锁定期间——不管这次密码对不对，一律按失败处理
        if (user is not null && user.LockedUntil > DateTime.Now)
            return null;

        // 找不到人，或密码不对 → 登录失败；工号存在的话顺便记一次失败次数，攒够次数就临时锁定
        if (user is null || !passwordOk)
        {
            if (user is not null)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= appOptions.Value.MaxFailedLoginAttempts)
                {
                    user.LockedUntil = DateTime.Now.AddMinutes(appOptions.Value.LoginLockoutMinutes);
                    logger.LogWarning("账号 {EmployeeNo} 连续登录失败 {Count} 次，临时锁定 {Minutes} 分钟",
                        employeeNo, user.FailedLoginCount, appOptions.Value.LoginLockoutMinutes);
                }
                await db.SaveChangesAsync();
            }
            return null;
        }

        // 人对密码也对，但账号被停用了 → 对调用方统一按登录失败处理，只在服务端日志里留痕方便排查
        if (!user.IsActive)
        {
            logger.LogWarning("停用账号 {EmployeeNo} 尝试登录", employeeNo);
            return null;
        }

        user.FailedLoginCount = 0;      // 登录成功，失败计数清零
        user.LockedUntil      = null;
        user.LastLoginAt      = DateTime.Now;   // 记录这次登录时间
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>工号只能包含字母、数字、下划线、短横线——工号会被直接拼进身份证照片存储目录名、
    /// 考勤机命令文本（PIN=工号），放行任意字符的话，"员工管理"页面表单虽然会拦，但这个方法本身
    /// 是唯一入口（Admin API 走的也是这里），在这里统一校验一次，不用指望每个调用方都记得自己先查一遍。</summary>
    private static void ValidateEmployeeNoFormat(string employeeNo)
    {
        if (string.IsNullOrWhiteSpace(employeeNo))
            throw new InvalidOperationException("请填写工号");
        if (employeeNo.Trim().Length > 50)
            throw new InvalidOperationException("工号不能超过 50 个字");
        if (!Regex.IsMatch(employeeNo.Trim(), @"^[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException("工号只能包含字母、数字、下划线和短横线");
    }

    /// <summary>创建员工（工号不能重复），对初始密码做哈希后保存，顺带把工号+姓名排进考勤机下发队列。
    /// 初始密码是随机生成的（不再是全公司共用一个固定默认密码），首次登录后会被强制要求改密码。</summary>
    public async Task<User> CreateUserAsync(User user, string plainPassword)
    {
        ValidateEmployeeNoFormat(user.EmployeeNo);

        if (await IsEmployeeNoExistsAsync(user.EmployeeNo))
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 已存在");

        // 身份证号命中"已拉黑"人员（永不录用）就直接拒绝建档——黑名单是全公司共享的信息，
        // 换个工号/换个分公司重新建档也要能被拦下来；判定以身份证号为准，手机号不作为黑名单命中依据
        // （避免共用手机号/家庭成员误伤）。这条校验放在服务层唯一的建档入口，管理员手动建档和
        // "确认扫码登记"两个入口都会调用这个方法，一起生效，不用各自重复实现。
        if (!string.IsNullOrWhiteSpace(user.IdNumber)
            && await db.Users.AnyAsync(u => u.IdNumber == user.IdNumber && u.IsBlacklisted))
            throw new InvalidOperationException("该身份证号已被拉黑（永不录用），请联系总部处理");

        user.PasswordHash       = HashPassword(plainPassword);   // 明文密码 → 哈希
        user.MustChangePassword = true;
        user.CreatedAt          = DateTime.Now;
        user.UpdatedAt          = DateTime.Now;

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 上面那个"存不存在"的检查和这里真正插入之间有个时间差：两个管理员几乎同时新建员工、
            // 自动生成到了同一个工号的话，先查的时候都还没冲突，插的时候后到的这个会撞数据库的
            // 唯一索引报错。这里捕获成友好提示，不让admin看到一句看不懂的原始数据库错误。
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 刚被别人抢先用掉了，请重新生成工号或换一个再试");
        }

        // 注意：这里不下发考勤机推送——新建的这一刻员工还没被分配任何设备（UserZKDevice 关联记录
        // 要等调用方接着调 SetUserDevicesAsync 才会建立），此时推送必然是"查到 0 台设备"的空跑。
        // 真正的下发发生在 SetUserDevicesAsync 里（所有创建员工的入口都会紧接着调用它）。
        return user;
    }

    /// <summary>把员工工号+姓名排进考勤机下发队列（设备下次心跳时会取走）。这是本地队列表操作，
    /// 失败了也不该拦住员工创建/编辑本身，出错只记日志。</summary>
    private async Task TryPushToZKDeviceAsync(User user)
    {
        try
        {
            await zkDeviceSyncService.EnqueuePushUserInfoAsync(user);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "员工 {UserId} 排队下发考勤机信息失败", user.Id);
        }
    }

    /// <summary>把"删除该工号"排进考勤机下发队列（设备下次心跳时会取走）。同上，失败只记日志不拦主流程。</summary>
    private async Task TryDeleteFromZKDeviceAsync(string employeeNo, int userId)
    {
        try
        {
            await zkDeviceSyncService.EnqueueDeleteUserInfoAsync(employeeNo, userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "员工 {UserId} 排队从考勤机删除失败", userId);
        }
    }

    /// <summary>员工自己改密码（要先验证原密码）。</summary>
    public async Task<bool> ChangePasswordAsync(int userId, string oldPassword, string newPassword)
    {
        // 跟登录同样的道理：先 trim 掉输入法/浏览器可能带出来的首尾空格、把全角数字转成半角，
        // 不然一旦真存进一个带空格/全角字符的新密码，员工自己根本没法发现（密码框打码），
        // 下次登录不管怎么输都对不上，只能再找管理员重置
        oldPassword = NormalizeFullWidthDigits(oldPassword.Trim());
        newPassword = NormalizeFullWidthDigits(newPassword.Trim());

        var user = await db.Users.FindAsync(userId);
        if (user is null || !VerifyPassword(oldPassword, user.PasswordHash))   // 原密码不对就拒绝
            return false;

        user.PasswordHash       = HashPassword(newPassword);
        user.MustChangePassword = false;   // 自己主动改过密码了，不用再强制跳改密码页
        user.UpdatedAt          = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 管理员重置密码：指定了新密码就用管理员输入的（至少 6 位），
    /// 留空则生成一个随机新密码；返回明文以便告知员工。
    /// </summary>
    public async Task<string> ResetPasswordAsync(int userId, string? newPassword = null)
    {
        var user = await db.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException($"用户 {userId} 不存在");

        string password;
        if (string.IsNullOrWhiteSpace(newPassword))
        {
            password = GenerateRandomPassword(8);
        }
        else
        {
            password = newPassword.Trim();
            if (password.Length < 6)
                throw new InvalidOperationException("新密码不能少于 6 位");
        }

        user.PasswordHash       = HashPassword(password);
        user.MustChangePassword = true;   // 管理员重置的密码，员工下次登录也要强制改成自己的
        user.FailedLoginCount   = 0;      // 重置密码顺带解除之前可能存在的登录锁定，不用等锁定自动过期
        user.LockedUntil        = null;
        user.UpdatedAt          = DateTime.Now;
        await db.SaveChangesAsync();
        return password;
    }

    /// <summary>更新员工基本信息（不含密码）。会检查工号是否被别人占用，顺带把最新工号+姓名排进考勤机下发队列。</summary>
    public async Task<bool> UpdateUserAsync(User user)
    {
        var existing = await db.Users.FindAsync(user.Id);
        if (existing is null) return false;

        ValidateEmployeeNoFormat(user.EmployeeNo);

        if (await IsEmployeeNoExistsAsync(user.EmployeeNo, user.Id))
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 已被其他员工占用");

        var oldEmployeeNo = existing.EmployeeNo;   // 改工号的话，考勤机上旧工号那条记录要跟着清掉，不然会留一条没人对应的僵尸记录

        // 逐项把新值覆盖到数据库里的那条记录上
        existing.RealName           = user.RealName;
        existing.EmployeeNo         = user.EmployeeNo;
        existing.DepartmentId       = user.DepartmentId;
        existing.Position           = user.Position;
        existing.Role               = user.Role;
        existing.AttendanceGroupId  = user.AttendanceGroupId;
        existing.SupervisorUserId   = user.SupervisorUserId;
        existing.Phone              = user.Phone;
        existing.IdNumber           = user.IdNumber;
        existing.ContractCompany    = user.ContractCompany;
        existing.HireDate           = user.HireDate;
        existing.HomeAddress            = user.HomeAddress;
        existing.EmergencyContactName   = user.EmergencyContactName;
        existing.EmergencyContactPhone  = user.EmergencyContactPhone;
        existing.IdCardPhotoUrl         = user.IdCardPhotoUrl;
        existing.AllowRemotePunch       = user.AllowRemotePunch;
        // 注意：不在这里覆盖 Email。员工表单不含这个字段，若在这里赋值，每次编辑都会把数据库里已有的值冲成空。
        existing.UpdatedAt          = DateTime.Now;
        await db.SaveChangesAsync();
        await TryPushToZKDeviceAsync(existing);
        if (oldEmployeeNo != existing.EmployeeNo)
            await TryDeleteFromZKDeviceAsync(oldEmployeeNo, existing.Id);

        return true;
    }

    /// <summary>停用员工（离职）：本地不删除，只是禁止登录，考勤/审批等记录仍保留、可查询；
    /// 顺带把这个工号从考勤机上删掉，离职后不该还能在设备上刷脸打卡。</summary>
    public async Task<bool> DeactivateUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        user.IsActive  = false;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await TryDeleteFromZKDeviceAsync(user.EmployeeNo, user.Id);
        return true;
    }

    /// <summary>重新启用员工。黑名单员工不能直接启用，需先移出黑名单。停用时考勤机上的记录被删过，
    /// 重新启用要顺带补发一次下发，不然设备上刷不了脸。</summary>
    public async Task<bool> ActivateUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;
        if (user.IsBlacklisted)
            throw new InvalidOperationException("该员工在黑名单中，请先「移出黑名单」再启用");

        user.IsActive  = true;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await TryPushToZKDeviceAsync(user);
        return true;
    }

    /// <summary>拉黑员工：标记黑名单（永不录用）并同时禁止登录，顺带把这个工号从考勤机上删掉
    /// （被拉黑的人不该还能在设备上刷脸打卡）。</summary>
    public async Task<bool> BlacklistUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        user.IsBlacklisted = true;
        user.IsActive      = false;   // 黑名单必然禁止登录
        user.UpdatedAt     = DateTime.Now;
        await db.SaveChangesAsync();
        await TryDeleteFromZKDeviceAsync(user.EmployeeNo, user.Id);
        return true;
    }

    /// <summary>移出黑名单：只去掉黑名单标记，账号仍是「已停用」状态，需再手动启用。</summary>
    public async Task<bool> RemoveFromBlacklistAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        user.IsBlacklisted = false;
        user.UpdatedAt     = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>彻底删除员工（连同其考勤记录/打卡/审批/通知按外键级联一并删除），顺带把这个工号从
    /// 考勤机上删掉。慎用。</summary>
    public async Task<bool> DeleteUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        // 先检查这个人是不是还挂在某个考勤组的"审批人"名单里——数据库不允许删除还被这样引用着的人
        var approverOfGroups = await db.AttendanceGroupApprovers
            .Where(a => a.UserId == userId)
            .Join(db.AttendanceGroups, a => a.AttendanceGroupId, g => g.Id, (a, g) => g.GroupName)
            .ToListAsync();
        if (approverOfGroups.Count > 0)
            throw new InvalidOperationException(
                $"该员工是「{string.Join("、", approverOfGroups)}」考勤组的审批人，无法直接删除，请先到「考勤组管理」把他从审批人名单里移除后再删除");

        // 审批节点(ApprovalStep.ApproverUserId)、公告发布人(Announcement.PublisherUserId)对 User 都是
        // Restrict 外键（故意不让删，保留审批/发布历史）——数据库层面会直接拒绝，但那样抛出来的是原始的
        // 外键约束错误，管理员看不懂也不知道该怎么处理，这里换成看得懂的提示，提前说清楚原因
        if (await db.ApprovalSteps.AnyAsync(s => s.ApproverUserId == userId))
            throw new InvalidOperationException("该员工有审批记录（曾经是某个申请单的审批人），无法删除，只能停用");
        if (await db.Announcements.AnyAsync(a => a.PublisherUserId == userId))
            throw new InvalidOperationException("该员工发布过公告，无法删除，只能停用");

        // SupervisorUserId 是 SetNull 外键：直接删的话，还认这个人当"直属上级"的下属会被静默清空上级字段，
        // 二级审批流程按 SupervisorUserId 找审批人会突然找不到人、悄悄断掉——不报错但结果是错的，
        // 比抛异常更麻烦，所以这里主动拦下来，让管理员先手动把这些下属改派给别人
        var subordinates = await db.Users.Where(u => u.SupervisorUserId == userId).Select(u => u.RealName).ToListAsync();
        if (subordinates.Count > 0)
            throw new InvalidOperationException(
                $"「{string.Join("、", subordinates)}」的直属上级是该员工，无法删除，请先到「员工管理」把他们的直属上级改派给别人后再删除");

        var employeeNo = user.EmployeeNo;

        // 必须在真正删除 User 行之前，先把"这个人绑定了哪些考勤机"的下发命令排好队——UserZKDevice
        // 关联记录是级联删除的，User 一删，关联记录跟着没了，届时再查就查不到该往哪几台设备发删除命令了
        await TryDeleteFromZKDeviceAsync(employeeNo, userId);

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>批量启用/停用。启用时会跳过黑名单员工（黑名单需先移出）。返回实际处理条数。
    /// 批量停用的员工，和单个停用一样会顺带从考勤机上删掉。</summary>
    public async Task<int> SetActiveBatchAsync(IEnumerable<int> userIds, bool active)
    {
        var ids   = userIds.Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();

        var changed = 0;
        var deactivatedEmployeeNos = new List<(string EmployeeNo, int UserId)>();
        foreach (var u in users)
        {
            if (active && u.IsBlacklisted) continue;   // 黑名单不参与批量启用
            if (u.IsActive == active) continue;

            u.IsActive  = active;
            u.UpdatedAt = DateTime.Now;
            if (!active) deactivatedEmployeeNos.Add((u.EmployeeNo, u.Id));
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync();
        foreach (var (employeeNo, userId) in deactivatedEmployeeNos)
            await TryDeleteFromZKDeviceAsync(employeeNo, userId);
        return changed;
    }

    /// <summary>按部门/考勤组/角色/关键字分页查询员工（含停用账号，在职排前面）。</summary>
    public async Task<(List<User> Users, int Total)> GetUsersAsync(
        int? deptId = null, int? groupId = null, UserRole? role = null,
        string? keyword = null, int pageIndex = 1, int pageSize = 20,
        EmployeeStatus? status = null, bool unassignedOnly = false)
    {
        // 先搭好基础查询；下面按传入的条件逐个追加过滤
        var query = db.Users
            .Include(u => u.Department)
            .Include(u => u.AttendanceGroup)
            .AsQueryable();

        if (unassignedOnly)                          // 只看“未分配部门”的员工
            query = query.Where(u => u.DepartmentId == null);
        else if (deptId.HasValue)                    // 看该部门 + 所有下级部门的员工（含下级）
        {
            var subtreeIds = await deptScopeService.GetSubtreeIdsAsync(deptId.Value);
            query = query.Where(u => u.DepartmentId != null && subtreeIds.Contains(u.DepartmentId.Value));
        }
        if (groupId.HasValue)
            query = query.Where(u => u.AttendanceGroupId == groupId.Value);
        if (role.HasValue)
            query = query.Where(u => u.Role == role.Value);
        // 状态筛选：由 IsActive/IsBlacklisted 两个字段组合而成
        if (status.HasValue)
            query = status.Value switch
            {
                EmployeeStatus.Active      => query.Where(u => u.IsActive  && !u.IsBlacklisted),
                EmployeeStatus.Disabled    => query.Where(u => !u.IsActive && !u.IsBlacklisted),
                EmployeeStatus.Blacklisted => query.Where(u => u.IsBlacklisted),
                _                          => query
            };
        if (!string.IsNullOrWhiteSpace(keyword))   // 关键字：姓名或工号包含即可
            query = query.Where(u => u.RealName.Contains(keyword) || u.EmployeeNo.Contains(keyword));

        var total = await query.CountAsync();   // 先数出总条数（分页用）
        var users = await query
            .OrderByDescending(u => u.IsActive)  // 在职的排前面
            .ThenBy(u => u.DepartmentId)
            .ThenBy(u => u.EmployeeNo)
            .Skip((pageIndex - 1) * pageSize)    // 跳过前面几页
            .Take(pageSize)                      // 取本页这一批
            .ToListAsync();

        return (users, total);
    }

    /// <summary>按编号取员工（不带关联信息）。</summary>
    public Task<User?> GetUserByIdAsync(int userId)
        => db.Users.FindAsync(userId).AsTask();

    /// <summary>按编号取员工（带部门/考勤组/上级信息）。</summary>
    public Task<User?> GetUserWithDetailsAsync(int userId)
        => db.Users
             .Include(u => u.Department)
             .Include(u => u.AttendanceGroup)
             .Include(u => u.Supervisor)
             .FirstOrDefaultAsync(u => u.Id == userId);

    /// <summary>判断某工号是否已被占用（更新时可排除自己）。</summary>
    public async Task<bool> IsEmployeeNoExistsAsync(string employeeNo, int? excludeUserId = null)
    {
        // 工号全局唯一：连“已停用/黑名单”的员工也算占用（黑名单员工的工号被保留，实现“永不录用”）
        var query = db.Users.Where(u => u.EmployeeNo == employeeNo);
        if (excludeUserId.HasValue)
            query = query.Where(u => u.Id != excludeUserId.Value);   // 排除自己这条
        return await query.AnyAsync();
    }

    /// <summary>各"公司"部门名 -> 工号前缀。只认这几个部门名字，其余部门（总公司、平湖、新能源装备等）不自动生成。</summary>
    private static readonly Dictionary<string, string> CompanyPrefixByDeptName = new()
    {
        ["深圳GA"]   = "GA",
        ["科瑞科技"] = "KJ",
        ["成都鹰诺"] = "IN",
        ["鼎力"]     = "DL",
        ["新能源"]   = "XNY",
        ["XNY"]      = "XNY",   // "XNY"是另一个独立部门（和"新能源"是并列的两个部门节点），命名规则顺延"新能源"，共用同一个前缀和流水号
        // 组织架构调整（2026-09，见 docs/分公司隔离_组织架构适配.md）：原来的"成都鹰诺"/"新能源"整体
        // 拆成了按地区独立的分公司节点，互相隔离、各自独立建部门树，每个新节点名都要能在这里精确匹配到，
        // 同一公司族的不同地区共用同一个前缀和流水号（跟上面"新能源/XNY 共用序列"是同一个道理）
        ["成都鹰诺-深圳地区"]   = "IN",
        ["成都鹰诺-成都地区"]   = "IN",
        ["科瑞新能源-成都地区"] = "XNY",
        ["科瑞新能源-深圳地区"] = "XNY",
        // 2026-09-04：科瑞新能源-成都地区/深圳地区 两个节点合并成一个"科瑞新能源"节点，上面两条旧名字保留不删（无同名部门则永不命中），这条是合并后的新名字
        ["科瑞新能源"]         = "XNY",
        ["苏州科瑞"]           = "SL",
        // 下面几个是测试阶段新建的部门（同一家公司的测试用副本），沿用同一套前缀，
        // 和上面对应的正式部门各自独立累计流水号（因为是不同的部门名，见下方查重逻辑按前缀而不是按部门算）
        ["深圳GA事业部"] = "GA",
        ["科瑞科技测试"] = "KJ",
        ["成都鹰诺测试"] = "IN",
        ["新能源测试"]   = "XNY",
    };

    /// <summary>按部门自动生成下一个工号：从该部门往上找最近的"公司"节点，取前缀 + 该前缀已用到的最大流水号+1（5位，不足补零）。</summary>
    public async Task<string?> GenerateNextEmployeeNoAsync(int? departmentId)
    {
        if (!departmentId.HasValue) return null;

        var deptsById = (await db.Departments
                .Select(d => new { d.Id, d.DeptName, d.ParentId })
                .ToListAsync())
            .ToDictionary(d => d.Id);

        // 从本部门往上走，直到找到一个匹配已知公司名单的节点（最多走 50 层，防止脏数据成环死循环）
        string? prefix = null;
        int? curId = departmentId;
        for (var i = 0; i < 50 && curId.HasValue; i++)
        {
            if (!deptsById.TryGetValue(curId.Value, out var dept)) break;
            if (CompanyPrefixByDeptName.TryGetValue(dept.DeptName, out var p)) { prefix = p; break; }
            curId = dept.ParentId;
        }
        if (prefix is null) return null;   // 找不到匹配的公司，不自动生成，改回手动填写

        // 只认「前缀 + 至少5位数字」这种严格格式的已有工号，避免误认成别的工号（比如手工建的临时工号）
        var pattern = new Regex($"^{Regex.Escape(prefix)}(\\d{{5,}})$");
        var maxNum = (await db.Users
                .Where(u => u.EmployeeNo.StartsWith(prefix))
                .Select(u => u.EmployeeNo)
                .ToListAsync())
            .Select(no => pattern.Match(no))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{maxNum + 1:D5}";
    }

    /// <summary>设置某员工被指定推送到的考勤机集合：全量覆盖式——先算出这次没勾选、但之前关联着的
    /// （要解除），再算出这次新勾选、之前没关联过的（要新增），两步做完，不是简单的"先删光再全插"，
    /// 避免没有实际变化的记录也被重新生成一条（CreatedAt 会被抹掉）。</summary>
    public async Task SetUserDevicesAsync(int userId, IEnumerable<int> deviceIds)
    {
        var idSet = deviceIds.Distinct().ToHashSet();
        var user  = await db.Users.FindAsync(userId);
        if (user is null) return;

        var existing = await db.UserZKDevices.Where(m => m.UserId == userId).ToListAsync();
        var toRemove = existing.Where(m => !idSet.Contains(m.ZKDeviceId)).ToList();
        var toAddIds = idSet.Except(existing.Select(m => m.ZKDeviceId)).ToList();
        var removedDeviceIds = toRemove.Select(m => m.ZKDeviceId).ToList();

        db.UserZKDevices.RemoveRange(toRemove);
        db.UserZKDevices.AddRange(toAddIds.Select(deviceId => new UserZKDevice { UserId = userId, ZKDeviceId = deviceId }));

        await db.SaveChangesAsync();

        // 设备集合变化后顺带同步考勤机下发命令：新勾选的设备要收到这个人的信息（没法在设备上录人脸），
        // 被取消勾选的设备要把这个人删掉（离职/调岗后人脸权限残留，独立分公司场景下还是跨公司越权风险）。
        // 只在真的新增了设备时才需要下发 UPDATE 命令——原来保留不变的设备已经在 UpdateUserAsync 那次
        // （编辑流程）或不需要（新增流程没有"保留不变"这一说）拿到过最新信息，这里再重推一遍纯属浪费。
        if (removedDeviceIds.Count > 0)
            await TryDeleteFromZKDeviceForDevicesAsync(user.EmployeeNo, removedDeviceIds);
        if (toAddIds.Count > 0)
            await TryPushToZKDeviceAsync(user);
    }

    /// <summary>把"删除该工号"排进考勤机下发队列，只发给指定的这几台设备（不是这个人当前绑定的全部设备）——
    /// 用在"编辑员工时取消勾选了某台设备"的场景：这台设备此时已经从 UserZKDevice 关联里移除了，
    /// 没法再用"查这个人绑定了哪些设备"的方式反查出它，必须显式传入要清掉的设备 id。</summary>
    private async Task TryDeleteFromZKDeviceForDevicesAsync(string employeeNo, List<int> deviceIds)
    {
        try
        {
            await zkDeviceSyncService.EnqueueDeleteUserInfoForDevicesAsync(employeeNo, deviceIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "从指定考勤机（{Ids}）删除员工 {EmployeeNo} 失败", string.Join(",", deviceIds), employeeNo);
        }
    }

    /// <summary>取某员工当前被指定推送到的考勤机 Id 列表。</summary>
    public Task<List<int>> GetUserDeviceIdsAsync(int userId)
        => db.UserZKDevices.Where(m => m.UserId == userId).Select(m => m.ZKDeviceId).ToListAsync();

    public async Task SetScopedDepartmentAsync(int userId, int? scopedDepartmentId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return;
        user.ScopedDepartmentId = scopedDepartmentId;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    // ── 密码工具 ──────────────────────────────────────────────────────────────
    // 哈希 = 一种“不可逆加密”：能把密码算成一串乱码存起来，但没法从乱码反推回原密码。
    // 盐(salt) = 一段随机料，混进密码再哈希，让相同密码也产生不同结果，防止被批量破解。

    /// <summary>把字符串里的全角数字（Ｕ+ＦＦ１０～Ｕ+ＦＦ１９，即"０"～"９"）转成对应的半角数字（"0"～"9"），
    /// 其它字符原样保留。中文输入法偶尔会切到全角模式，打出来的数字肉眼很难跟半角区分开（尤其在打码的
    /// 密码框里），但对系统来说是完全不同的字符，登录/改密码校验前统一转一遍，避免因为这个对不上。</summary>
    private static string NormalizeFullWidthDigits(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= '０' and <= '９')   // "０"(FF10) ~ "９"(FF19)，比对应半角数字大 0xFEE0
                chars[i] = (char)(chars[i] - 0xFEE0);
        return new string(chars);
    }

    /// <summary>
    /// 把明文密码变成可安全存储的哈希字符串。
    /// 做法：随机 16 字节盐 + PBKDF2(SHA256，迭代 1 万次)。存储格式「Base64(盐):Base64(哈希)」。
    /// </summary>
    // 老哈希（两段式 "盐:哈希"，不带迭代次数）固定按这个次数校验——保证已经存在的账号密码不受影响，
    // 不用强制全员重置密码就能完成升级。新哈希都用下面 CurrentIterations，带上迭代次数存成三段式，
    // 以后想再调高强度，加个新版本号继续这么升级就行，老哈希还是能按它自己当初的次数正常校验。
    private const int LegacyIterations = 10_000;
    // OWASP 现在给 PBKDF2-HMAC-SHA256 的建议迭代次数（原来的 1 万次太低，暴力破解的成本太便宜了）
    private const int CurrentIterations = 600_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);   // 生成随机盐
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, CurrentIterations, HashAlgorithmName.SHA256, 32);
        return $"{CurrentIterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验密码：从存储值里取出当初的盐（和迭代次数，如果有的话），用同样方法把输入的密码再算一遍，
    /// 比对是否一致。比对用“恒定时间比较”，防止通过比对耗时来猜密码（时序攻击）。
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        int iterations;
        byte[] salt, expectedHash;
        if (parts.Length == 3)          // 新格式："迭代次数:盐:哈希"
        {
            if (!int.TryParse(parts[0], out iterations)) return false;
            salt         = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        else if (parts.Length == 2)     // 老格式："盐:哈希"，没有迭代次数字段，按老次数算
        {
            iterations   = LegacyIterations;
            salt         = Convert.FromBase64String(parts[0]);
            expectedHash = Convert.FromBase64String(parts[1]);
        }
        else
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(       // 用同样的盐、同样的迭代次数重算
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);  // 安全比对
    }

    /// <summary>登录时序侧信道防护用的假哈希：工号根本不存在时，也拿它跑一遍完整的哈希校验计算，
    /// 让"工号不存在"和"工号存在但密码错"这两种失败在响应耗时上没有可观测的差别。</summary>
    private static readonly string DummyPasswordHashForTimingSafety = HashPassword(Guid.NewGuid().ToString("N"));

    /// <summary>生成随机密码（已剔除易混淆字符 0/O/1/I/l），用于重置密码、新建员工的初始密码。</summary>
    public static string GenerateRandomPassword(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789@#!";
        return new string(Enumerable.Range(0, length)
            .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)])   // 每一位随机取一个字符
            .ToArray());
    }
}
