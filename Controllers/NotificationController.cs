using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;

namespace AttendanceSystem.Controllers;

/// <summary>站内通知接口：拉未读、单条已读、全部已读（页面右上角小铃铛用）。</summary>
[Authorize]
[ApiController]
[Route("api/notifications")]
public class NotificationController(AttendanceDbContext db) : ApiControllerBase
{
    /// <summary>拉当前用户最近 15 条未读通知，并返回未读总数。</summary>
    [HttpGet]
    public async Task<IActionResult> GetUnread()
    {
        var uid   = CurrentUserId;
        var items = await db.Notifications
            .Where(n => n.UserId == uid && !n.IsRead)        // 我的、未读的
            .OrderByDescending(n => n.CreatedAt)             // 新的在前
            .Take(15)
            .Select(n => new
            {
                n.Id, n.Title, n.Content,
                CreatedAt = n.CreatedAt.ToString("MM-dd HH:mm"),
                n.NotificationType, n.RelatedId
            })
            .ToListAsync();

        var total = await db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead);   // 未读总数
        return Ok(new { count = total, items });
    }

    /// <summary>把某条通知标记为已读（只能标自己的）。</summary>
    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == CurrentUserId);
        if (n is null) return NotFound();
        n.IsRead = true;
        n.ReadAt = DateTime.Now;
        await db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>把当前用户所有未读通知一次性标为已读（ExecuteUpdate 直接批量更新，不用逐条取出）。</summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        await db.Notifications
            .Where(n => n.UserId == CurrentUserId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.Now));
        return Ok();
    }
}
