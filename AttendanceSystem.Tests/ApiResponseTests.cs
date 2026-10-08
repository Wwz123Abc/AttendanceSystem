using System.Text.Json;
using System.Text.Json.Serialization;
using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Tests;

/// <summary>
/// 接口统一返回格式：序列化结果必须和改造前控制器里手写的 <c>new { Success, Message, … }</c> 完全一样
/// （前端、手机端按 success / message / data / total 等字段取值，字段名不能变）。
/// </summary>
public class ApiResponseTests
{
    // 跟 Program.cs 的 AddJsonOptions 一致：小驼峰、忽略 null
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static Dictionary<string, JsonElement> Parse(object o)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o, Options))!;

    private static void AssertSameFields(object expected, object actual)
    {
        var e = Parse(expected);
        var a = Parse(actual);
        Assert.Equal(e.Keys.OrderBy(k => k), a.Keys.OrderBy(k => k));
        foreach (var k in e.Keys)
            Assert.Equal(e[k].GetRawText(), a[k].GetRawText());
    }

    [Fact]
    public void 失败_只有success和message()
        => AssertSameFields(new { Success = false, Message = "工号和密码不能为空" }, ApiResponse.Failed("工号和密码不能为空"));

    [Fact]
    public void 成功_不带任何字段_只有success()
        => AssertSameFields(new { Success = true }, ApiResponse.Succeeded());

    [Fact]
    public void 成功_带提示语和附加字段_附加字段平铺在顶层()
    {
        AssertSameFields(new { Success = true, Message = "员工创建成功", UserId = 5, InitialPassword = "123456" },
            ApiResponse.Succeeded("员工创建成功", ("userId", 5), ("initialPassword", "123456")));
        AssertSameFields(new { Success = true, DeptId = 7 }, ApiResponse.Succeeded(null, ("deptId", 7)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 按结果选提示语(bool ok)
        => AssertSameFields(new { Success = ok, Message = ok ? "已停用" : "用户不存在" },
            ApiResponse.FromResult(ok, "已停用", "用户不存在"));

    [Fact]
    public void 带数据_列表接口带总数()
    {
        var list = new[] { new { Id = 1, Name = "a" }, new { Id = 2, Name = "b" } };
        AssertSameFields(new { Success = true, Data = list, Total = list.Length }, ApiResponse.WithData(list, list.Length));
        AssertSameFields(new { Success = true, Data = list }, ApiResponse.WithData(list));
    }
}
