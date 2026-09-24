using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 覆盖登录/改密码/重置密码统一用的输入规范化：中文输入法全角模式下打出来的字母、数字、符号
/// （比如随机重置密码里的 "!"→"！"）以前会一直提示"工号或密码错误"（2026-09-24 现场反馈）。
/// 同时保证已经存在的老账号（旧版本存下来的写法）不会因为这次统一而突然登录不上。
/// </summary>
public class PasswordNormalizationTests
{
    [Theory]
    [InlineData("Ab3@#!xy", "Ab3@#!xy")]          // 纯半角原样不变
    [InlineData("Ab3@#！xy", "Ab3@#!xy")]         // 全角感叹号（中文输入法最常见的坑）
    [InlineData("Ｅｘ３＠＃！", "Ex3@#!")]          // 全角字母+数字+符号混着来
    [InlineData("１２３４５６", "123456")]          // 全角数字
    [InlineData("  123456  ", "123456")]           // 首尾空白
    [InlineData("　123456　", "123456")]   // 首尾全角空格
    public void 全角字符统一转成半角(string input, string expected)
        => Assert.Equal(expected, UserService.NormalizeInput(input));

    [Fact]
    public void 纯半角输入只有一个候选_没有额外开销()
        => Assert.Single(UserService.PasswordCandidates("Ab3@#!xy"));

    [Fact]
    public void 随机密码里的感叹号被输入法打成全角_也能登录()
    {
        var hash = UserService.HashPassword("Ab3@#!xy");   // 重置时系统生成并存下的密码
        var typed = "Ab3@#！xy";                           // 员工用中文输入法打出来的
        Assert.True(UserService.MatchCandidate(UserService.PasswordCandidates(typed), hash) >= 0);
    }

    [Fact]
    public void 管理员全角模式下手动指定的密码_现在存成半角_员工输入半角能登录()
    {
        // 新逻辑：重置时先规范化再存，存下来的是半角的 123456
        var stored = UserService.HashPassword(UserService.NormalizeInput("１２３４５６"));
        Assert.True(UserService.MatchCandidate(UserService.PasswordCandidates("123456"), stored) >= 0);
        Assert.True(UserService.MatchCandidate(UserService.PasswordCandidates("１２３４５６"), stored) >= 0);
    }

    [Fact]
    public void 兼容老账号_旧版重置密码把全角数字原样存了下来_仍然能登录()
    {
        var legacyStored = UserService.HashPassword("１２３４５６");   // 旧版：管理员手输全角，原样存库
        var candidates = UserService.PasswordCandidates("１２３４５６"); // 员工照着抄同样的全角
        var matched = UserService.MatchCandidate(candidates, legacyStored);
        Assert.True(matched > 0);   // 靠老写法候选对上的，登录成功后会被顺手改存成半角
    }

    [Fact]
    public void 兼容老账号_旧版改密码只转了全角数字_仍然能登录()
    {
        var legacyStored = UserService.HashPassword("Ab３＠");         // 旧版：数字转成了半角，全角符号原样存下
        var matched = UserService.MatchCandidate(UserService.PasswordCandidates("Ab３＠"), legacyStored);
        Assert.True(matched >= 0);
    }

    [Fact]
    public void 随机重置密码_半角原样输入和全角输入法输入都能登录()
    {
        for (var i = 0; i < 300; i++)
        {
            var pwd = UserService.GenerateRandomPassword(8);
            var hash = UserService.HashPassword(UserService.NormalizeInput(pwd));
            // 半角原样
            Assert.True(UserService.MatchCandidate(UserService.PasswordCandidates(pwd), hash) >= 0, pwd);
            // 整串被输入法打成全角（字母/数字/符号全部变全角）
            var full = new string(pwd.Select(c => (char)(c + 0xFEE0)).ToArray());
            Assert.True(UserService.MatchCandidate(UserService.PasswordCandidates(full), hash) >= 0, full);
        }
    }

    [Fact]
    public void 真正错误的密码仍然对不上()
    {
        var hash = UserService.HashPassword("Ab3@#!xy");
        Assert.Equal(-1, UserService.MatchCandidate(UserService.PasswordCandidates("Ab3@#!xz"), hash));
    }
}
