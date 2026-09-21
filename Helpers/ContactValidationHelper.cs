namespace AttendanceSystem.Helpers;

/// <summary>手机号/身份证号格式校验：网页端（UserManage.cshtml.cs）和接口端（AdminController.cs）
/// 以前各写了一份完全相同的正则，抽到这里共用一份，避免以后改一处漏了另一处（2026-09-21）。</summary>
public static class ContactValidationHelper
{
    /// <summary>手机号格式是否正确（11 位中国大陆手机号）。空/空白值不算不正确——是否必填由调用方自己判断。</summary>
    public static bool IsValidPhone(string phone) =>
        System.Text.RegularExpressions.Regex.IsMatch(phone.Trim(), @"^1[3-9]\d{9}$");

    /// <summary>身份证号格式是否正确（18 位，末位可以是 X/x）。空/空白值不算不正确——是否必填由调用方自己判断。</summary>
    public static bool IsValidIdNumber(string idNumber) =>
        System.Text.RegularExpressions.Regex.IsMatch(idNumber.Trim(), @"^\d{17}[\dXx]$");

    /// <summary>手机号/身份证号一起校验，不合格返回中文提示，都合格（或都没填）返回 null。</summary>
    public static string? ValidateContactFormat(string? phone, string? idNumber)
    {
        if (!string.IsNullOrWhiteSpace(phone) && !IsValidPhone(phone))
            return "请输入正确格式的手机号（11 位中国大陆手机号）";
        if (!string.IsNullOrWhiteSpace(idNumber) && !IsValidIdNumber(idNumber))
            return "请输入正确格式的身份证号（18 位）";
        return null;
    }
}
