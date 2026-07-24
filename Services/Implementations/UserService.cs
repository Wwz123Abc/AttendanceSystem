using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

// 「服务(Service)」= 业务逻辑的集中地。控制器/页面负责接收请求，真正干活的逻辑写在服务里。

/// <summary>
/// 用户服务：登录校验、员工增删改查、密码管理。
/// 密码用 PBKDF2(SHA256) 加盐哈希存储，数据库里永远不存明文密码。
/// </summary>
public class UserService(
    AttendanceDbContext db,
    IDingTalkContactClient dingTalkContactClient,
    ILogger<UserService> logger) : IUserService
{
    /// <summary>校验工号+密码。成功返回用户；工号/密码错返回 null；账号停用则抛异常。</summary>
    public async Task<User?> ValidateLoginAsync(string employeeNo, string password)
    {
        // 故意不在查询里过滤“在职”，这样才能区分“工号/密码错”和“账号被停用”两种情况
        var user = await db.Users
            .Include(u => u.Department)
            .Include(u => u.AttendanceGroup)
            .Where(u => u.EmployeeNo == employeeNo)
            .OrderByDescending(u => u.IsActive)   // 万一同工号有多条，优先取在职的
            .FirstOrDefaultAsync();

        // 找不到人，或密码不对 → 登录失败
        if (user is null || !VerifyPassword(password, user.PasswordHash))
            return null;

        // 人对密码也对，但账号被停用了 → 明确提示
        if (!user.IsActive)
            throw new InvalidOperationException("账号已停用，无法登录，请联系管理员");

        user.LastLoginAt = DateTime.Now;   // 记录这次登录时间
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// 创建员工（工号不能重复），对初始密码做哈希后保存。
    /// 保存完本地记录后，顺带尝试把这个人同步创建到钉钉通讯录（本系统 → 钉钉）：
    /// 需要同时满足"填了手机号"和"所在部门能换算出钉钉部门编号"，两个条件缺一个就直接跳过（不算错误，
    /// 大多数手工建的临时工本就没打算同步钉钉）；条件都满足但钉钉那边创建失败了，才会带一句提示回来。
    /// </summary>
    public async Task<(User User, string? DingTalkWarning)> CreateUserAsync(User user, string plainPassword)
    {
        if (await IsEmployeeNoExistsAsync(user.EmployeeNo))
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 已存在");

        user.PasswordHash = HashPassword(plainPassword);   // 明文密码 → 哈希
        user.CreatedAt    = DateTime.Now;
        user.UpdatedAt    = DateTime.Now;

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var warning = await TryCreateOnDingTalkAsync(user);
        return (user, warning);
    }

    /// <summary>
    /// 尝试把刚建好的本地员工同步创建到钉钉通讯录。返回值：null=已同步成功或本来就不打算同步；
    /// 非空=已经具备同步条件、但调用钉钉接口失败了，这句话是给管理员看的提示。
    /// </summary>
    private async Task<string?> TryCreateOnDingTalkAsync(User user)
    {
        if (string.IsNullOrWhiteSpace(user.Phone) || !user.DepartmentId.HasValue)
        {
            logger.LogInformation("创建员工 {UserId}：没填手机号或没分配部门，跳过钉钉同步创建", user.Id);
            return null;   // 没手机号或没分配部门：钉钉创建这两项必填，凑不齐就直接跳过
        }

        var dept = await db.Departments.FindAsync(user.DepartmentId.Value);
        // 部门要是已经和钉钉那边对应上了（DingTalkDeptId 有值），才知道这个人在钉钉里该挂到哪个部门下；
        // 纯本地新建、还没来得及同步到钉钉的部门没有这个编号，换算不出来，只能跳过（等部门同步好了再手动补）
        if (dept?.DingTalkDeptId is not { } dingDeptId)
        {
            logger.LogInformation("创建员工 {UserId}：所在部门 {DeptId} 还没有对应的钉钉部门编号，跳过钉钉同步创建",
                user.Id, user.DepartmentId);
            return null;
        }

        try
        {
            user.DingTalkUserId = await dingTalkContactClient.CreateEmployeeAsync(
                user.RealName, user.Phone, user.EmployeeNo, user.Position, [dingDeptId]);
            await db.SaveChangesAsync();
            logger.LogInformation("创建员工 {UserId} 时同步创建钉钉账号成功，钉钉 userid={DingTalkUserId}", user.Id, user.DingTalkUserId);
            return null;
        }
        catch (DingTalkApiException ex) when (ex.ErrCode == 40103)
        {
            // errcode=40103：不是失败，是钉钉的正常流程——这个手机号还不是企业钉钉里的成员，
            // 钉钉给对方发了一条"加入企业"的邀请，要等对方本人同意之后才会正式成为企业成员。
            // 这种情况下钉钉不会立刻给 userid，所以 DingTalkUserId 暂时留空；对方同意邀请后
            // 需要管理员自己去钉钉通讯录确认、或用"自动映射 userid"功能补上关联。
            logger.LogInformation("创建员工 {UserId}：钉钉已发出加入企业邀请，等待对方同意", user.Id);
            return "钉钉已同步创建，已发出邀请，对方同意后即可加入组织。";
        }
        catch (Exception ex)
        {
            // 钉钉那边建不了（比如手机号在企业内已被别人占用、令牌过期），不能因此拦住本地创建，
            // 只记日志 + 告诉管理员一句，让他知道钉钉通讯录可能需要手动核对/添加
            logger.LogWarning(ex, "创建员工 {UserId} 时同步创建钉钉账号失败", user.Id);
            return $"钉钉同步创建失败：{ex.Message}（本地账号已正常创建，如需要请到钉钉通讯录手动添加）";
        }
    }

    /// <summary>员工自己改密码（要先验证原密码）。</summary>
    public async Task<bool> ChangePasswordAsync(int userId, string oldPassword, string newPassword)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null || !VerifyPassword(oldPassword, user.PasswordHash))   // 原密码不对就拒绝
            return false;

        user.PasswordHash = HashPassword(newPassword);
        user.UpdatedAt    = DateTime.Now;
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

        user.PasswordHash = HashPassword(password);
        user.UpdatedAt    = DateTime.Now;
        await db.SaveChangesAsync();
        return password;
    }

    /// <summary>
    /// 更新员工基本信息（不含密码）。会检查工号是否被别人占用。
    /// 如果这个人绑定了钉钉，顺带把姓名/手机号/工号/职位同步更新到钉钉通讯录（本系统 → 钉钉），
    /// 保持两边资料一致；钉钉同步失败不影响本地保存，只返回一句提示。
    /// </summary>
    public async Task<(bool Success, string? DingTalkWarning)> UpdateUserAsync(User user)
    {
        var existing = await db.Users.FindAsync(user.Id);
        if (existing is null) return (false, null);

        if (await IsEmployeeNoExistsAsync(user.EmployeeNo, user.Id))
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 已被其他员工占用");

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
        // 注意：不在这里覆盖 Email 和 DingTalkUserId。员工表单已不含这两个字段，
        // 若在这里赋值，每次编辑都会把数据库里已有的值冲成空。
        existing.UpdatedAt          = DateTime.Now;
        await db.SaveChangesAsync();

        string? warning = null;
        if (string.IsNullOrEmpty(existing.DingTalkUserId))
        {
            logger.LogInformation("更新员工 {UserId}：本地未绑定钉钉账号（DingTalkUserId 为空），跳过钉钉同步", user.Id);
        }
        else
        {
            // 换算这个人现在所在部门对应的钉钉部门编号：换不出来（新部门还没同步到钉钉）就传 null，
            // 表示这次更新不动钉钉那边的部门归属，只同步姓名/手机号/工号/职位这几项
            long? dingDeptId = null;
            if (existing.DepartmentId.HasValue)
            {
                var dept = await db.Departments.FindAsync(existing.DepartmentId.Value);
                dingDeptId = dept?.DingTalkDeptId;
            }

            try
            {
                // 返回非空说明只是部分字段没能同步（钉钉的已知限制，比如手机号不一致），不算失败，
                // 直接把这句说明当成提示带回去，不进 catch（catch 是给"整体失败"用的）
                warning = await dingTalkContactClient.UpdateEmployeeAsync(
                    existing.DingTalkUserId, existing.RealName, existing.Phone, existing.EmployeeNo, existing.Position,
                    dingDeptId.HasValue ? [dingDeptId.Value] : null);
                logger.LogInformation("更新员工 {UserId} 同步钉钉资料完成，钉钉 userid={DingTalkUserId}，部分未同步说明：{Note}",
                    user.Id, existing.DingTalkUserId, warning ?? "（无，全部同步成功）");
            }
            catch (Exception ex)
            {
                // 钉钉那边更新不了（比如令牌过期、网络问题、手机号在钉钉企业内已被别人占用），不能因此拦住本地保存，
                // 只记日志 + 告诉管理员一句，让他知道钉钉通讯录可能需要手动核对
                logger.LogWarning(ex, "更新员工 {UserId} 时同步更新钉钉资料失败，钉钉 userid={DingTalkUserId}", user.Id, existing.DingTalkUserId);
                warning = $"钉钉同步更新失败：{ex.Message}（请到钉钉通讯录手动确认）";
            }
        }

        return (true, warning);
    }

    /// <summary>
    /// 停用员工（离职）：本地不删除，只是禁止登录，考勤/审批等记录仍保留、可查询。
    /// 如果这个人绑定了钉钉，顺带把他从钉钉企业通讯录里删掉——钉钉那边只有"删除"没有"临时禁用"，
    /// 员工离职后没道理继续挂在企业通讯录里；本地 DingTalkUserId 也一并清空，以后万一重新入职，
    /// 会当成一个新员工重新走一遍"创建/邀请加入"的流程。钉钉这边删除失败不拦住本地停用，只带一句提示回去。
    /// </summary>
    public async Task<(bool Success, string? DingTalkWarning)> DeactivateUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return (false, null);

        string? warning = null;
        if (string.IsNullOrEmpty(user.DingTalkUserId))
        {
            logger.LogInformation("停用员工 {UserId}：本地未绑定钉钉账号（DingTalkUserId 为空），跳过钉钉同步删除", userId);
        }
        else
        {
            try
            {
                await dingTalkContactClient.DeleteEmployeeAsync(user.DingTalkUserId);
                logger.LogInformation("停用员工 {UserId} 时同步删除钉钉账号成功，钉钉 userid={DingTalkUserId}", userId, user.DingTalkUserId);
            }
            catch (Exception ex)
            {
                // 钉钉那边删不掉（比如令牌过期、网络问题、对方已经手动删过），不能因此拦住本地停用，
                // 只记日志 + 告诉管理员一句，让他知道钉钉通讯录可能需要手动处理
                logger.LogWarning(ex, "停用员工 {UserId} 时同步删除钉钉账号失败，钉钉 userid={DingTalkUserId}", userId, user.DingTalkUserId);
                warning = $"钉钉同步删除失败：{ex.Message}（请到钉钉通讯录手动确认/删除）";
            }
            user.DingTalkUserId = null;   // 不论钉钉那边删没删成，本地都不再当他是"已绑定钉钉"，避免以后同步到一个其实已经不存在的 userid
        }

        user.IsActive  = false;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return (true, warning);
    }

    /// <summary>重新启用员工。黑名单员工不能直接启用，需先移出黑名单。</summary>
    public async Task<bool> ActivateUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;
        if (user.IsBlacklisted)
            throw new InvalidOperationException("该员工在黑名单中，请先「移出黑名单」再启用");

        user.IsActive  = true;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>拉黑员工：标记黑名单（永不录用）并同时禁止登录。</summary>
    public async Task<bool> BlacklistUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        user.IsBlacklisted = true;
        user.IsActive      = false;   // 黑名单必然禁止登录
        user.UpdatedAt     = DateTime.Now;
        await db.SaveChangesAsync();
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

    /// <summary>
    /// 彻底删除员工（连同其考勤记录/打卡/审批/通知按外键级联一并删除）。慎用。
    /// 如果这个人绑定了钉钉，顺带把他从钉钉企业通讯录里也删掉——
    /// 钉钉开放平台只提供"删除"，没有"临时禁用"，所以只在这个不可恢复的操作上联动，不在"停用/拉黑"上做。
    /// </summary>
    public async Task<(bool Success, string? DingTalkWarning)> DeleteUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return (false, null);

        // 先检查这个人是不是还挂在某个考勤组的"审批人"名单里——数据库不允许删除还被这样引用着的人，
        // 之前没做这个检查时，删除会先把钉钉那边的账号删掉、本地才因为这个外键约束保存失败，
        // 变成"钉钉已删、本地没删"的不一致状态；现在提前查一遍，直接给出清楚的提示，不去动钉钉
        var approverOfGroups = await db.AttendanceGroupApprovers
            .Where(a => a.UserId == userId)
            .Join(db.AttendanceGroups, a => a.AttendanceGroupId, g => g.Id, (a, g) => g.GroupName)
            .ToListAsync();
        if (approverOfGroups.Count > 0)
            throw new InvalidOperationException(
                $"该员工是「{string.Join("、", approverOfGroups)}」考勤组的审批人，无法直接删除，请先到「考勤组管理」把他从审批人名单里移除后再删除");

        string? warning = null;
        if (string.IsNullOrEmpty(user.DingTalkUserId))
        {
            logger.LogInformation("删除员工 {UserId}：本地未绑定钉钉账号（DingTalkUserId 为空），跳过钉钉同步删除", userId);
        }
        else
        {
            try
            {
                await dingTalkContactClient.DeleteEmployeeAsync(user.DingTalkUserId);
                logger.LogInformation("删除员工 {UserId} 时同步删除钉钉账号成功，钉钉 userid={DingTalkUserId}", userId, user.DingTalkUserId);
            }
            catch (Exception ex)
            {
                // 钉钉那边删不掉（比如令牌过期、网络问题、对方已经手动删过），不能因此拦住本地删除，
                // 只记日志 + 告诉管理员一句，让他知道钉钉通讯录可能需要手动处理
                logger.LogWarning(ex, "删除员工 {UserId} 时同步删除钉钉账号失败，钉钉 userid={DingTalkUserId}", userId, user.DingTalkUserId);
                warning = $"钉钉同步删除失败：{ex.Message}（请到钉钉通讯录手动确认/删除）";
            }
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return (true, warning);
    }

    /// <summary>
    /// 批量启用/停用。启用时会跳过黑名单员工（黑名单需先移出）。返回实际处理条数。
    /// 批量停用（离职）时，和单个停用一样，绑定了钉钉的员工会顺带从钉钉通讯录里删掉（本地记录仍保留）；
    /// 某个人钉钉那边删除失败不影响其他人，也不拦住本地停用，只记日志，不在这里逐条往上抛提示。
    /// </summary>
    public async Task<int> SetActiveBatchAsync(IEnumerable<int> userIds, bool active)
    {
        var ids   = userIds.Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();

        var changed = 0;
        foreach (var u in users)
        {
            if (active && u.IsBlacklisted) continue;   // 黑名单不参与批量启用
            if (u.IsActive == active) continue;

            if (!active && !string.IsNullOrEmpty(u.DingTalkUserId))
            {
                try
                {
                    await dingTalkContactClient.DeleteEmployeeAsync(u.DingTalkUserId);
                    logger.LogInformation("批量停用员工 {UserId} 时同步删除钉钉账号成功，钉钉 userid={DingTalkUserId}", u.Id, u.DingTalkUserId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "批量停用员工 {UserId} 时同步删除钉钉账号失败，钉钉 userid={DingTalkUserId}", u.Id, u.DingTalkUserId);
                }
                u.DingTalkUserId = null;
            }

            u.IsActive  = active;
            u.UpdatedAt = DateTime.Now;
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync();
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
            var subtreeIds = await GetDeptSubtreeIdsAsync(deptId.Value);
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

    /// <summary>取某部门自己 + 所有下级部门的编号集合（用于“点某部门要看到含下级的全部人”）。</summary>
    private async Task<HashSet<int>> GetDeptSubtreeIdsAsync(int deptId)
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

    // ── 密码工具 ──────────────────────────────────────────────────────────────
    // 哈希 = 一种“不可逆加密”：能把密码算成一串乱码存起来，但没法从乱码反推回原密码。
    // 盐(salt) = 一段随机料，混进密码再哈希，让相同密码也产生不同结果，防止被批量破解。

    /// <summary>
    /// 把明文密码变成可安全存储的哈希字符串。
    /// 做法：随机 16 字节盐 + PBKDF2(SHA256，迭代 1 万次)。存储格式「Base64(盐):Base64(哈希)」。
    /// </summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);   // 生成随机盐
        var hash = Rfc2898DeriveBytes.Pbkdf2(            // 用盐反复加密 1 万次
            Encoding.UTF8.GetBytes(password), salt, 10_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";  // 盐和哈希一起存
    }

    /// <summary>
    /// 校验密码：从存储值里取出当初的盐，用同样方法把输入的密码再算一遍，比对是否一致。
    /// 比对用“恒定时间比较”，防止通过比对耗时来猜密码（时序攻击）。
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');                  // 拆出 盐 和 哈希 两部分
        if (parts.Length != 2) return false;
        var salt         = Convert.FromBase64String(parts[0]);
        var expectedHash = Convert.FromBase64String(parts[1]);
        var actualHash   = Rfc2898DeriveBytes.Pbkdf2(       // 用同样的盐重算
            Encoding.UTF8.GetBytes(password), salt, 10_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);  // 安全比对
    }

    /// <summary>生成随机密码（已剔除易混淆字符 0/O/1/I/l），用于重置密码。</summary>
    private static string GenerateRandomPassword(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789@#!";
        return new string(Enumerable.Range(0, length)
            .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)])   // 每一位随机取一个字符
            .ToArray());
    }
}
