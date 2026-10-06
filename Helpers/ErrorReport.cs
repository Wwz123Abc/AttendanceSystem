using System.Security.Claims;
using Serilog;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 给用户看的"出错提示"统一出口：不只说一句"请稍后重试"，而是带上 错误编号 + 时间 + 异常类型，
/// 同时把完整异常（堆栈、请求路径、登录用户）连同同一个编号记进服务器日志。
/// 员工遇到问题时，把页面上那一整段文字（或截图）发给开发人员，开发人员按编号在服务器日志里一搜就能
/// 看到当时到底发生了什么——比"保存失败，请稍后重试"能直接定位得多（2026-10-06 用户要求报错尽量详细）。
/// 不把异常的原始文本（数据库报错常带表名/列名/约束名/SQL）直接展示给员工：展示"类型名 + 编号"，
/// 细节在日志里，两边信息量对开发人员是等价的，又不会把内部细节摆给所有登录用户。
/// </summary>
public static class ErrorReport
{
    /// <summary>短编号，形如 E1006-153012-7F3A（月日-时分秒-4 位随机），便于口头/截图转述。</summary>
    public static string NewId() => $"E{DateTime.Now:MMdd-HHmmss}-{Random.Shared.Next(0x1000, 0x10000):X4}";

    /// <summary>记一条带编号的 Error 日志，并返回"提示语 + 编号/时间/类型 + 反馈指引"的完整文字，直接赋给页面的 ErrorMessage。</summary>
    public static string Describe(Exception ex, string userMessage, HttpContext? http = null)
    {
        var id = NewId();
        var request = http is null
            ? "(无请求上下文)"
            : $"{http.Request.Method} {http.Request.Path}{http.Request.QueryString}，用户={http.User.FindFirstValue(ClaimTypes.Name) ?? "未登录"}（Id={http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "-"}）";
        Log.Error(ex, "[{ErrorId}] 展示给用户的提示：{UserMessage}｜异常类型：{ExceptionType}｜请求：{Request}",
            id, userMessage, ex.GetType().FullName, request);
        return Format(userMessage, id, ex);
    }

    public static string Format(string userMessage, string id, Exception ex) =>
        $"{userMessage}【错误编号 {id}｜{DateTime.Now:yyyy-MM-dd HH:mm:ss}｜{ex.GetType().Name}】——反馈问题时请把这段完整文字（或截图）发给开发人员";
}
