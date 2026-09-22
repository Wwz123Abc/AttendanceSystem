using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>系统公告服务：发布（含算受众名单、写已读记录、写站内通知）、撤下、查询。</summary>
public class AnnouncementService(AttendanceDbContext db) : IAnnouncementService
{
    public async Task<Announcement> PublishAsync(int publisherUserId, UserRole publisherRole, PublishAnnouncementDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) throw new InvalidOperationException("请填写公告标题");
        if (dto.Title.Trim().Length > 200) throw new InvalidOperationException("标题不能超过 200 个字");
        if (string.IsNullOrWhiteSpace(dto.Content)) throw new InvalidOperationException("请填写公告内容");
        if (dto.Content.Trim().Length > 2000) throw new InvalidOperationException("内容不能超过 2000 个字");

        // 班组长/主管只能发给自己的直属下属：范围在服务端强制锁死，不采信页面传来的 ScopeType/ScopeId，
        // 避免有人改改前端请求就能越权发给别的部门/考勤组
        var isManager = publisherRole is UserRole.Admin or UserRole.Clerk;
        var scopeType = isManager ? dto.ScopeType : AnnouncementScopeType.DirectReports;
        int? scopeId  = isManager ? dto.ScopeId : null;

        if (isManager && scopeType is AnnouncementScopeType.Department or AnnouncementScopeType.AttendanceGroup && scopeId is null)
            throw new InvalidOperationException("请选择具体的部门/考勤组");

        var audienceIds = await ResolveAudienceAsync(publisherUserId, scopeType, scopeId);
        if (audienceIds.Count == 0)
            throw new InvalidOperationException("这个范围里没有任何在职员工，无法发布");

        var now = DateTime.Now;
        var announcement = new Announcement
        {
            Title           = dto.Title.Trim(),
            Content         = dto.Content.Trim(),
            PublisherUserId = publisherUserId,
            ScopeType       = scopeType,
            ScopeId         = scopeId,
            IsActive        = true,
            CreatedAt       = now,
            UpdatedAt       = now
        };
        db.Announcements.Add(announcement);
        await db.SaveChangesAsync();   // 先存一次拿到 Id，后面已读记录/通知要用

        // 受众名单：一人一条"未读"记录（后台靠这张表看谁读了谁没读），顺带给每个人也发一条站内通知（铃铛能立刻看到）
        var briefContent = announcement.Content.Length > 100 ? announcement.Content[..100] + "..." : announcement.Content;
        foreach (var uid in audienceIds)
        {
            db.AnnouncementReads.Add(new AnnouncementRead { AnnouncementId = announcement.Id, UserId = uid, ReadAt = null });
            db.Notifications.Add(new Notification
            {
                UserId           = uid,
                Title            = "新公告：" + announcement.Title,
                Content          = briefContent,
                NotificationType = "Announcement",
                RelatedId        = announcement.Id,
                CreatedAt        = now
            });
        }
        await db.SaveChangesAsync();
        return announcement;
    }

    public async Task<bool> WithdrawAsync(int operatorUserId, bool isManager, int announcementId, HashSet<int>? managerVisibleDeptIds = null)
    {
        var a = await db.Announcements.FindAsync(announcementId);
        if (a is null) return false;
        // 自己发的公告，本人一定能撤；否则要是管理员/文员，且这条公告的发布范围落在自己管理范围内——
        // 原来只判断了 isManager，任何一个分公司管理员都能撤别的分公司（甚至总部"全公司"）的公告，
        // 撤下后从所有分公司公告栏消失，且没有恢复入口
        var allowed = a.PublisherUserId == operatorUserId
            || (isManager && await IsAnnouncementInScopeAsync(a, managerVisibleDeptIds));
        if (!allowed) return false;

        a.IsActive  = false;
        a.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        // 撤下的同时把发布时给每个受众发的站内通知一并标已读，不然铃铛里还留着这条、
        // 点进去却因为 IsActive=false 在公告栏里看不到了（NotificationController 的未读列表
        // 只看 UserId/IsRead，不管公告本身是不是已经撤下——2026-09-21 代码审查发现）
        await db.Notifications
            .Where(n => n.NotificationType == "Announcement" && n.RelatedId == announcementId && !n.IsRead)
            .ExecuteUpdateAsync(n => n
                .SetProperty(x => x.IsRead, true)
                .SetProperty(x => x.ReadAt, DateTime.Now));
        return true;
    }

    /// <summary>这条公告的发布范围是否落在调用者的管理范围内——用于撤下/查已读名单这类"按公告 id 操作"
    /// 的二次校验。<paramref name="visibleDeptIds"/> 为 null 表示总部超级管理员，不受限。</summary>
    private async Task<bool> IsAnnouncementInScopeAsync(Announcement a, HashSet<int>? visibleDeptIds)
    {
        if (visibleDeptIds is null) return true;
        switch (a.ScopeType)
        {
            case AnnouncementScopeType.All:
                return false;   // 全公司范围的公告只有总部能撤/查已读名单
            case AnnouncementScopeType.Department:
                return a.ScopeId.HasValue && visibleDeptIds.Contains(a.ScopeId.Value);
            case AnnouncementScopeType.AttendanceGroup:
                if (!a.ScopeId.HasValue) return false;
                var groupDeptIds = await db.Departments.Where(d => d.AttendanceGroupId == a.ScopeId.Value)
                    .Select(d => d.Id).ToListAsync();
                // 关联部门必须都在自己范围内（All，不是 Any）——跟考勤组本身的写权限口径一致，
                // 不然跨司共用组发的公告，两边分公司管理员都能撤
                return groupDeptIds.Count > 0 && groupDeptIds.All(visibleDeptIds.Contains);
            case AnnouncementScopeType.DirectReports:
                // 不能用"发布人现在所在的部门"判断——真正的受众是发布时 ResolveAudienceAsync 按
                // SupervisorUserId 算出来的那批人，发布人后续调岗不会改变已经发出去的这条公告的受众。
                // 用 AnnouncementReads 里实际留底的受众名单反查他们的部门，才是这条公告真实覆盖的范围
                // （2026-09-21 代码审查发现：原实现会因为发布人调岗，让"能不能撤/查已读"的判定跟着
                // 发布人的新部门变，而不是跟着这条公告实际发给了谁）
                var audienceDeptIds = await db.AnnouncementReads.Where(r => r.AnnouncementId == a.Id)
                    .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => u.DepartmentId)
                    .Where(d => d != null).Select(d => d!.Value).Distinct().ToListAsync();
                return audienceDeptIds.Count > 0 && audienceDeptIds.All(visibleDeptIds.Contains);
            default:
                return false;
        }
    }

    /// <summary>公告栏最多显示这么多条——以前不限条数，一个部门/全公司发久了公告栏会无限变长，
    /// 每次打开都要整表拉回来（2026-09-21 代码审查发现）。按时间只留最近这些，不做真分页
    /// （公告本来就不是需要往前翻很久的内容，旧的仍然在"我的已发布"里能查到）。</summary>
    private const int BoardMaxItems = 200;

    public async Task<List<AnnouncementBoardItemDto>> GetBoardForUserAsync(int userId)
    {
        return await db.AnnouncementReads
            .Include(r => r.Announcement).ThenInclude(a => a.Publisher)
            .Where(r => r.UserId == userId && r.Announcement.IsActive)
            .OrderByDescending(r => r.Announcement.CreatedAt)
            .Take(BoardMaxItems)
            .Select(r => new AnnouncementBoardItemDto
            {
                Id            = r.Announcement.Id,
                Title         = r.Announcement.Title,
                Content       = r.Announcement.Content,
                PublisherName = r.Announcement.Publisher.RealName,
                CreatedAt     = r.Announcement.CreatedAt,
                IsRead        = r.ReadAt.HasValue
            })
            .ToListAsync();
    }

    public async Task MarkReadAsync(int userId, int announcementId)
    {
        var r = await db.AnnouncementReads.FirstOrDefaultAsync(x => x.UserId == userId && x.AnnouncementId == announcementId);
        if (r is null || r.ReadAt.HasValue) return;   // 不在受众名单里、或者已经读过了，都不用处理
        r.ReadAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    public async Task<List<AnnouncementPublishedItemDto>> GetMyPublishedAsync(int publisherUserId)
    {
        var items = await db.Announcements
            .Include(a => a.Reads)
            .Where(a => a.PublisherUserId == publisherUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(BoardMaxItems)
            .ToListAsync();

        // 范围文字要查部门/考勤组名字，一次性批量查出来，避免在循环里逐条查库
        var deptIds  = items.Where(a => a.ScopeType == AnnouncementScopeType.Department && a.ScopeId.HasValue)
            .Select(a => a.ScopeId!.Value).Distinct().ToList();
        var groupIds = items.Where(a => a.ScopeType == AnnouncementScopeType.AttendanceGroup && a.ScopeId.HasValue)
            .Select(a => a.ScopeId!.Value).Distinct().ToList();
        var deptNames  = await db.Departments.Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DeptName);
        var groupNames = await db.AttendanceGroups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.GroupName);

        return items.Select(a => new AnnouncementPublishedItemDto
        {
            Id        = a.Id,
            Title     = a.Title,
            Content   = a.Content,
            ScopeType = a.ScopeType,
            ScopeText = a.ScopeType switch
            {
                AnnouncementScopeType.All             => "全公司",
                AnnouncementScopeType.Department      => deptNames.GetValueOrDefault(a.ScopeId ?? 0, "（部门已删除）"),
                AnnouncementScopeType.AttendanceGroup => groupNames.GetValueOrDefault(a.ScopeId ?? 0, "（考勤组已删除）"),
                AnnouncementScopeType.DirectReports   => "我的直属下属",
                _                                     => "—"
            },
            IsActive   = a.IsActive,
            CreatedAt  = a.CreatedAt,
            TotalCount = a.Reads.Count,
            ReadCount  = a.Reads.Count(r => r.ReadAt.HasValue)
        }).ToList();
    }

    public async Task<List<AnnouncementReadDetailDto>?> GetReadDetailAsync(int operatorUserId, bool isManager, int announcementId, HashSet<int>? managerVisibleDeptIds = null)
    {
        var a = await db.Announcements.FindAsync(announcementId);
        if (a is null) return null;
        var allowed = a.PublisherUserId == operatorUserId
            || (isManager && await IsAnnouncementInScopeAsync(a, managerVisibleDeptIds));
        if (!allowed) return null;   // 不是自己发的、又管不到这条公告的范围，不能查已读名单

        return await db.AnnouncementReads
            .Include(r => r.User)
            .Where(r => r.AnnouncementId == announcementId)
            .OrderBy(r => r.ReadAt.HasValue).ThenBy(r => r.User.RealName)   // 没读的排前面，方便一眼看出还差谁没读
            .Select(r => new AnnouncementReadDetailDto
            {
                UserId     = r.UserId,
                RealName   = r.User.RealName,
                EmployeeNo = r.User.EmployeeNo,
                ReadAt     = r.ReadAt
            })
            .ToListAsync();
    }

    public Task<int> CountDirectReportsAsync(int userId)
        => db.Users.CountAsync(u => u.IsActive && u.SupervisorUserId == userId);

    public async Task<List<AnnouncementScopeOptionDto>> GetDepartmentOptionsAsync(HashSet<int>? visibleDeptIds = null)
    {
        var depts = await db.Departments.Where(d => d.IsActive)
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        var byId     = depts.ToDictionary(d => d.Id);
        var byParent = depts.Where(d => d.ParentId.HasValue)
            .GroupBy(d => d.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<AnnouncementScopeOptionDto>();
        void Add(Department d, int depth)
        {
            result.Add(new AnnouncementScopeOptionDto { Id = d.Id, Name = new string('　', depth) + d.DeptName });
            if (byParent.TryGetValue(d.Id, out var kids))
                foreach (var k in kids) Add(k, depth + 1);
        }
        foreach (var root in depts.Where(d => !d.ParentId.HasValue || !byId.ContainsKey(d.ParentId.Value)))
            Add(root, 0);

        return visibleDeptIds is null ? result : result.Where(d => visibleDeptIds.Contains(d.Id)).ToList();
    }

    public async Task<List<AnnouncementScopeOptionDto>> GetAttendanceGroupOptionsAsync(HashSet<int>? visibleDeptIds = null)
    {
        var groups = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName)
            .Select(g => new AnnouncementScopeOptionDto { Id = g.Id, Name = g.GroupName })
            .ToListAsync();
        if (visibleDeptIds is null) return groups;

        // 受限管理员：只保留"关联部门都在自己范围内（All，不是 Any）或完全没关联部门（全公司通用组）"的组，
        // 跟 IsAnnouncementInScopeAsync/发布时的服务端校验同一套口径（2026-09-17 代码审查发现的口径统一）
        var groupDeptMap = (await db.Departments.Where(d => d.AttendanceGroupId != null)
                .Select(d => new { d.Id, d.AttendanceGroupId }).ToListAsync())
            .GroupBy(x => x.AttendanceGroupId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        return groups.Where(g => !groupDeptMap.TryGetValue(g.Id, out var deptIds)
            || deptIds.All(visibleDeptIds.Contains)).ToList();
    }

    /// <summary>按发布范围算出这次公告实际要发给哪些（在职）员工的 Id 列表。</summary>
    private async Task<List<int>> ResolveAudienceAsync(int publisherUserId, AnnouncementScopeType scopeType, int? scopeId)
    {
        switch (scopeType)
        {
            case AnnouncementScopeType.All:
                return await db.Users.Where(u => u.IsActive).Select(u => u.Id).ToListAsync();

            case AnnouncementScopeType.Department:
            {
                if (scopeId is not { } deptId) return [];
                var deptIds = await GetDeptAndDescendantIdsAsync(deptId);
                return await db.Users
                    .Where(u => u.IsActive && u.DepartmentId.HasValue && deptIds.Contains(u.DepartmentId.Value))
                    .Select(u => u.Id).ToListAsync();
            }

            case AnnouncementScopeType.AttendanceGroup:
            {
                if (scopeId is not { } groupId) return [];
                return await db.Users.Where(u => u.IsActive && u.AttendanceGroupId == groupId)
                    .Select(u => u.Id).ToListAsync();
            }

            case AnnouncementScopeType.DirectReports:
                return await db.Users.Where(u => u.IsActive && u.SupervisorUserId == publisherUserId)
                    .Select(u => u.Id).ToListAsync();

            default:
                return [];
        }
    }

    /// <summary>某个部门自己 + 它下面所有层级子部门的 Id 集合（"按部门发公告"要连子部门一起发，不然子部门的人收不到）。</summary>
    private async Task<HashSet<int>> GetDeptAndDescendantIdsAsync(int rootDeptId)
    {
        var all = await db.Departments.Where(d => d.IsActive)
            .Select(d => new { d.Id, d.ParentId }).ToListAsync();
        var byParent = all.Where(d => d.ParentId.HasValue)
            .GroupBy(d => d.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var result = new HashSet<int> { rootDeptId };
        var queue  = new Queue<int>();
        queue.Enqueue(rootDeptId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!byParent.TryGetValue(cur, out var kids)) continue;
            foreach (var k in kids)
                if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }
}
