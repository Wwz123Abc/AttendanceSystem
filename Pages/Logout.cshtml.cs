using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AttendanceSystem.Pages;

/// <summary>登出页：清掉登录 Cookie，跳回登录页。</summary>
public class LogoutModel : PageModel
{
    // 退出登录会改变服务端会话状态，用 POST（Razor Pages 自动校验防伪令牌）而不是 GET——
    // GET 请求可以被跨站的一张 <img>/<a> 甚至预加载悄悄触发，用 POST 才能挡住这种跨站强制登出。
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);   // 退出登录
        return RedirectToPage("/Login");
    }
}
