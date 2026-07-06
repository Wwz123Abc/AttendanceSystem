using System.Security.Cryptography;
using System.Text;
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
public class UserService(AttendanceDbContext db) : IUserService
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

    /// <summary>创建员工（工号不能重复），对初始密码做哈希后保存。</summary>
    public async Task<User> CreateUserAsync(User user, string plainPassword)
    {
        if (await IsEmployeeNoExistsAsync(user.EmployeeNo))
            throw new InvalidOperationException($"工号 {user.EmployeeNo} 已存在");

        user.PasswordHash = HashPassword(plainPassword);   // 明文密码 → 哈希
        user.CreatedAt    = DateTime.Now;
        user.UpdatedAt    = DateTime.Now;

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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

    /// <summary>更新员工基本信息（不含密码）。会检查工号是否被别人占用。</summary>
    public async Task<bool> UpdateUserAsync(User user)
    {
        var existing = await db.Users.FindAsync(user.Id);
        if (existing is null) return false;

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
        existing.HireDate           = user.HireDate;
        // 注意：不在这里覆盖 Email 和 DingTalkUserId。员工表单已不含这两个字段，
        // 若在这里赋值，每次编辑都会把数据库里已有的值冲成空。
        existing.UpdatedAt          = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>停用员工（不删除，只是禁止登录，记录仍保留）。</summary>
    public async Task<bool> DeactivateUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        user.IsActive  = false;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
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

    /// <summary>彻底删除员工（连同其考勤记录/打卡/审批/通知按外键级联一并删除）。慎用。</summary>
    public async Task<bool> DeleteUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>批量启用/停用。启用时会跳过黑名单员工（黑名单需先移出）。返回实际处理条数。</summary>
    public async Task<int> SetActiveBatchAsync(IEnumerable<int> userIds, bool active)
    {
        var ids   = userIds.Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();

        var changed = 0;
        foreach (var u in users)
        {
            if (active && u.IsBlacklisted) continue;   // 黑名单不参与批量启用
            if (u.IsActive == active) continue;
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
