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

    /// <summary>点开某条公告详情时，前端 AJAX 调这个接口顺手标记已读。会改数据库，用 POST（Razor Pages
    /// 自动校验防伪令牌），不用页面里其它几处只读查询用的 GET ?handler= 写法。</summary>
    public async Task<IActionResult> OnPostMarkReadAsync(int id)
    {
        await announcementService.MarkReadAsync(CurrentUserId, id);
        return new OkResult();
    }
}
