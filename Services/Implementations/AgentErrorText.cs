using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 智能助手（AGENT）对外展示异常文案的统一出口：只有"本程序自己的代码"抛出的 InvalidOperationException /
/// KeyNotFoundException 才是给用户看的业务提示（服务层专门用它们抛"工号已存在""无权操作该账号"这类中文提示）；
/// EF Core/数据库驱动/框架内部也会抛同样的类型或别的类型，原文可能带出表名、列名、约束名这类内部细节，
/// 一律换成通用文案（完整异常由调用方记进日志）。口径跟 Program.cs 的全局异常兜底、管理页
/// （UserManage/ShiftManage/ZKDeviceManage）"只给中文提示 + 记日志"一致；AGENT 是漏网之处（2026-10-06 复核）。
/// </summary>
public static class AgentErrorText
{
    public const string Generic = "操作失败，请稍后重试或联系管理员";

    /// <summary>这个异常的原文是不是"本程序自己抛出的、给用户看的业务提示"。</summary>
    public static bool IsBusinessMessage(Exception ex) =>
        ex is BusinessException
        || (ex is InvalidOperationException or KeyNotFoundException
            && ex.TargetSite?.DeclaringType?.Assembly == typeof(AgentErrorText).Assembly);

    /// <summary>业务提示原样返回；其它异常返回"通用文案 + 错误编号/时间/类型"，完整异常按同一个编号记进日志
    /// （用户把编号发给开发人员就能查到原因，见 <see cref="AttendanceSystem.Helpers.ErrorReport"/>）。</summary>
    public static string ForUser(Exception ex) =>
        IsBusinessMessage(ex) ? ex.Message : AttendanceSystem.Helpers.ErrorReport.Describe(ex, Generic);
}
