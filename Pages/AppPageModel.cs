using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AttendanceSystem.Pages;

// 「页面后端(PageModel)」= 负责一个网页的“后台逻辑”：加载要显示的数据、处理用户提交的表单。
// 约定：OnGet 在打开页面时执行；OnPost 在页面提交表单时执行。

/// <summary>
/// 所有页面后端的基类：统一提供“当前登录用户 Id”，省得每个页面各写一遍。
/// （和接口那边的 ApiControllerBase 作用一样。）
/// </summary>
public abstract class AppPageModel : PageModel
{
    /// <summary>当前登录用户的编号；没登录/读不到返回 0。</summary>
    protected int CurrentUserId
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /// <summary>当前登录用户所属考勤组编号；未分配考勤组则返回 null。</summary>
    protected int? CurrentGroupId
        => int.TryParse(User.FindFirstValue("AttendanceGroupId"), out var id) ? id : null;
}
