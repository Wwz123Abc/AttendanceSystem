namespace AttendanceSystem.Models.Options;

/// <summary>
/// 应用通用配置（绑定 appsettings.json 的 "AppSettings" 节）。
/// </summary>
public class AppSettingsOptions
{
    public const string SectionName = "AppSettings";

    /// <summary>新员工（含钉钉导入）默认初始密码。</summary>
    public string DefaultPassword { get; set; } = "Abc@12345";

    /// <summary>登录有效期（小时）。</summary>
    public int TokenExpireHours { get; set; } = 8;

    /// <summary>上传文件根目录（相对 wwwroot）。</summary>
    public string UploadPath { get; set; } = "uploads";

    /// <summary>
    /// 出差全勤的默认标准工时（小时）：出差期间不用打卡，按“全勤”记工时（工资按工时结算）。
    /// 如果那天有排班，优先用班次自己的标准工时；没有排班就用这个默认值。
    /// </summary>
    public decimal DefaultDailyWorkHours { get; set; } = 8;
}
