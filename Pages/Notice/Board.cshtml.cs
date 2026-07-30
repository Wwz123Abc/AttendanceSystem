using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Notice;

/// <summary>公告栏：所有登录用户都能看，显示"我在受众名单里"的所有有效公告。</summary>
[Authorize]
public class BoardModel(IAnnouncementService announcementService) : AppPageModel
{
    public List<AnnouncementBoardItemDto> Items { get; set; } = [];

    public async Task OnGetAsync()
    {
        Items = await announcementService.GetBoardForUserAsync(CurrentUserId);
    }

    /// <summary>点开某条公告详情时，前端 AJAX（GET，跟页面里其它几处 ?handler= 的写法一致）调这个接口顺手标记已读。</summary>
    public async Task<IActionResult> OnGetMarkReadAsync(int id)
    {
        await announcementService.MarkReadAsync(CurrentUserId, id);
        return new OkResult();
    }
}
