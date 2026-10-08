using System.Text.Json.Serialization;

namespace AttendanceSystem.Models.DTOs;

/// <summary>
/// 接口统一返回格式：<c>{ success, message?, data?, total?, …附加字段 }</c>。
/// 字段名是小驼峰、值为 null 的字段不输出（见 Program.cs 的 AddJsonOptions）；
/// 前端和手机端按 <c>success</c> 判断成败、<c>message</c> 取提示语，格式跟改造前逐字段一致。
/// 用下面的静态方法创建，不要在控制器里再手写 <c>new { Success = …, Message = … }</c>。
/// </summary>
public sealed class ApiResponse
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public object? Data { get; init; }
    public int? Total { get; init; }

    /// <summary>附加字段（如新建后的 userId）：序列化时直接平铺到顶层。</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Extra { get; init; }

    /// <summary>成功（可带提示语和附加字段，字段名写小驼峰，如 <c>("userId", 5)</c>）。</summary>
    public static ApiResponse Succeeded(string? message = null, params (string Name, object? Value)[] extra) => new()
    {
        Success = true,
        Message = message,
        Extra = extra.Length == 0 ? null : extra.ToDictionary(e => e.Name, e => e.Value),
    };

    /// <summary>失败，带用户能看懂的提示语。</summary>
    public static ApiResponse Failed(string message) => new() { Success = false, Message = message };

    /// <summary>按布尔结果选提示语：<paramref name="ok"/> 为真返回成功提示，否则返回失败提示。</summary>
    public static ApiResponse FromResult(bool ok, string okMessage, string failMessage) => new()
    {
        Success = ok,
        Message = ok ? okMessage : failMessage,
    };

    /// <summary>成功并带数据（列表接口还可带总数）。</summary>
    public static ApiResponse WithData(object? data, int? total = null) => new()
    {
        Success = true,
        Data = data,
        Total = total,
    };
}
